using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using MeldResearch.BrowserProtocol;
using MeldResearch.Research;

namespace MeldResearch.RemoteBackend.Services;

internal sealed record AccountRecord(Guid Id, string Subject, string Name, string Email);
internal sealed record DeviceRecord(Guid Id, Guid AccountId, string TokenHash, DateTimeOffset ExpiresAt, string Name);
internal sealed record AccountDatabase(int Version, IReadOnlyList<AccountRecord> Accounts, IReadOnlyList<DeviceRecord> Devices);
internal sealed record DeviceGrant(string Code, string UserCode, string Name, string Challenge,
    DateTimeOffset ExpiresAt, DateTimeOffset NextPollAt, string Status = "pending",
    MeldAccountResponse? Account = null, string? AccessToken = null, DateTimeOffset? CredentialExpiresAt = null);

internal interface IAccountService
{
    Task<MeldAccountResponse> RegisterAsync(ClaimsPrincipal principal, CancellationToken ct);
    Task<MeldAccountResponse?> FindAsync(ClaimsPrincipal principal, CancellationToken ct);
    Task<DeviceLinkResponse> BeginLinkAsync(CreateDeviceLinkRequest request, CancellationToken ct);
    Task<string> DeviceNameAsync(string userCode, CancellationToken ct);
    Task ApproveAsync(string userCode, ClaimsPrincipal principal, bool allow, CancellationToken ct);
    Task<PollDeviceLinkResponse> PollAsync(PollDeviceLinkRequest request, CancellationToken ct);
    Task<MeldAccountResponse> AuthenticateAsync(string authorization, CancellationToken ct);
    Task RevokeAsync(string authorization, CancellationToken ct);
}

internal sealed class AccountService(RemoteOptions options, IDataProtectionProvider protection, TimeProvider clock)
    : IAccountService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, DeviceGrant> _grants = new(StringComparer.Ordinal);
    private readonly IDataProtector _protector = protection.CreateProtector("MeldResearch.Remote.Accounts.v1");
    private string StorePath => Path.Combine(options.DataDirectory, "accounts.protected");

    public async Task<MeldAccountResponse> RegisterAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var identity = Identity(principal);
        await _gate.WaitAsync(ct);
        try
        {
            var database = await ReadAsync(ct);
            var existing = database.Accounts.FirstOrDefault(account => account.Subject == identity.Subject);
            if (existing is not null) return Response(existing);
            var account = new AccountRecord(Guid.NewGuid(), identity.Subject, identity.Name, identity.Email);
            await SaveAsync(database with { Accounts = [.. database.Accounts, account] }, ct);
            return Response(account);
        }
        finally { _gate.Release(); }
    }

    public async Task<MeldAccountResponse?> FindAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var identity = Identity(principal);
        await _gate.WaitAsync(ct);
        try
        {
            var account = (await ReadAsync(ct)).Accounts.FirstOrDefault(account => account.Subject == identity.Subject);
            return account is null ? null : Response(account);
        }
        finally { _gate.Release(); }
    }

    public async Task<DeviceLinkResponse> BeginLinkAsync(CreateDeviceLinkRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceName) || request.DeviceName.Length > 100 ||
            request.Challenge is not { Length: 64 } || !request.Challenge.All(Uri.IsHexDigit))
            throw new ResearchFailure(400, "invalid_device_link", "A device name and SHA-256 proof challenge are required.");
        await _gate.WaitAsync(ct);
        try
        {
            Prune();
            if (_grants.Count >= 100)
                throw new ResearchFailure(429, "device_link_capacity", "Too many device links are pending. Try again shortly.");
            string code = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            string userCode;
            do { userCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)); }
            while (_grants.Values.Any(grant => grant.UserCode == userCode));
            var expires = clock.GetUtcNow().AddMinutes(10);
            _grants.Add(code, new(code, userCode, request.DeviceName, request.Challenge.ToLowerInvariant(), expires, clock.GetUtcNow()));
            return new(code, userCode, new Uri(options.PublicOrigin, "connect?code=" + userCode).AbsoluteUri, expires, 5);
        }
        finally { _gate.Release(); }
    }

    public async Task<string> DeviceNameAsync(string userCode, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return FindGrant(userCode).Name; }
        finally { _gate.Release(); }
    }

    public async Task ApproveAsync(string userCode, ClaimsPrincipal principal, bool allow, CancellationToken ct)
    {
        var identity = Identity(principal);
        await _gate.WaitAsync(ct);
        try
        {
            var grant = FindGrant(userCode);
            if (grant.Status != "pending") throw new ResearchFailure(409, "device_link_decided", "This device link has already been decided.");
            var database = await ReadAsync(ct);
            var account = database.Accounts.FirstOrDefault(account => account.Subject == identity.Subject) ??
                throw new ResearchFailure(403, "meld_account_required", "Create a Meld Research account on this website before linking a device.");
            if (!allow)
            {
                _grants[grant.Code] = grant with { Status = "denied" };
                return;
            }
            string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var expires = clock.GetUtcNow().AddDays(30);
            var device = new DeviceRecord(Guid.NewGuid(), account.Id, ResearchEvidence.Hash(token), expires, grant.Name);
            await SaveAsync(database with { Devices = [.. database.Devices.Where(item => item.ExpiresAt > clock.GetUtcNow()), device] }, ct);
            _grants[grant.Code] = grant with
            {
                Status = "approved", Account = Response(account), AccessToken = token, CredentialExpiresAt = expires
            };
        }
        finally { _gate.Release(); }
    }

    public async Task<PollDeviceLinkResponse> PollAsync(PollDeviceLinkRequest request, CancellationToken ct)
    {
        if (request.DeviceCode is not { Length: 64 } || request.Verifier is not { Length: 64 } ||
            !request.DeviceCode.All(Uri.IsHexDigit) || !request.Verifier.All(Uri.IsHexDigit))
            throw new ResearchFailure(400, "invalid_device_proof", "The device-link proof is invalid.");
        await _gate.WaitAsync(ct);
        try
        {
            Prune();
            if (!_grants.TryGetValue(request.DeviceCode, out var grant))
                throw new ResearchFailure(410, "device_link_expired", "The device link expired. Start a new link.");
            if (!FixedEquals(ResearchEvidence.Hash(request.Verifier), grant.Challenge))
                throw new ResearchFailure(403, "invalid_device_proof", "The device-link proof is invalid.");
            var now = clock.GetUtcNow();
            if (now < grant.NextPollAt)
                throw new ResearchFailure(429, "device_poll_too_fast", "Wait before checking device approval again.");
            _grants[grant.Code] = grant with { NextPollAt = now.AddSeconds(5) };
            return new(grant.Status, grant.AccessToken, grant.CredentialExpiresAt, grant.Account);
        }
        finally { _gate.Release(); }
    }

    public async Task<MeldAccountResponse> AuthenticateAsync(string authorization, CancellationToken ct)
    {
        string hash = TokenHash(authorization);
        await _gate.WaitAsync(ct);
        try
        {
            var database = await ReadAsync(ct);
            var device = database.Devices.FirstOrDefault(device => FixedEquals(device.TokenHash, hash) && device.ExpiresAt > clock.GetUtcNow());
            var account = device is null ? null : database.Accounts.FirstOrDefault(account => account.Id == device.AccountId);
            return account is null ? throw Unauthorized() : Response(account);
        }
        finally { _gate.Release(); }
    }

    public async Task RevokeAsync(string authorization, CancellationToken ct)
    {
        string hash = TokenHash(authorization);
        await _gate.WaitAsync(ct);
        try
        {
            var database = await ReadAsync(ct);
            await SaveAsync(database with { Devices = database.Devices.Where(device => !FixedEquals(device.TokenHash, hash)).ToArray() }, ct);
            foreach (string code in _grants.Where(item => item.Value.AccessToken is { } token && FixedEquals(ResearchEvidence.Hash(token), hash))
                .Select(item => item.Key).ToArray()) _grants.Remove(code);
        }
        finally { _gate.Release(); }
    }

    private DeviceGrant FindGrant(string userCode)
    {
        Prune();
        return _grants.Values.FirstOrDefault(grant => grant.UserCode == userCode) ??
            throw new ResearchFailure(410, "device_link_expired", "The device link expired or does not exist.");
    }

    private void Prune()
    {
        foreach (string code in _grants.Where(item => item.Value.ExpiresAt <= clock.GetUtcNow()).Select(item => item.Key).ToArray())
            _grants.Remove(code);
    }

    private (string Subject, string Name, string Email) Identity(ClaimsPrincipal principal)
    {
        string? provider = principal.FindFirstValue("meld_provider");
        string? subject = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        string? email = principal.FindFirstValue(ClaimTypes.Email);
        string? name = principal.FindFirstValue(ClaimTypes.Name);
        if (principal.Identity?.IsAuthenticated != true || (provider != "google" && !(options.Dummy && provider == "dummy")) ||
            principal.FindFirstValue("meld_email_verified") != "true" || string.IsNullOrWhiteSpace(subject) || subject.Length > 255 ||
            string.IsNullOrWhiteSpace(email) || email.Length > 320)
            throw Unauthorized();
        return (provider + ":" + subject, string.IsNullOrWhiteSpace(name) ? email : name[..Math.Min(name.Length, 200)], email);
    }

    private async Task<AccountDatabase> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(StorePath)) return new(1, [], []);
        if (new FileInfo(StorePath).Length > 16 * 1024 * 1024) throw new InvalidDataException("Remote account store exceeds its size limit.");
        byte[] encrypted = await File.ReadAllBytesAsync(StorePath, ct);
        if (encrypted.Length > 16 * 1024 * 1024) throw new InvalidDataException("Remote account store exceeds its size limit.");
        byte[] bytes = _protector.Unprotect(encrypted);
        try
        {
            var database = JsonSerializer.Deserialize<AccountDatabase>(bytes, ResearchJson.Options);
            return database is { Version: 1, Accounts: not null, Devices: not null } ? database :
                throw new InvalidDataException("The remote account store is invalid.");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task SaveAsync(AccountDatabase database, CancellationToken ct)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(database, ResearchJson.Options);
        string temporary = StorePath + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            if (bytes.Length > 15 * 1024 * 1024)
                throw new ResearchFailure(503, "account_store_capacity", "The account store has reached its capacity. Existing records were retained.");
            byte[] encrypted = _protector.Protect(bytes);
            await using (var stream = RuntimeFiles.CreatePrivateFile(temporary))
                await stream.WriteAsync(encrypted, ct);
            File.Move(temporary, StorePath, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string TokenHash(string authorization) =>
        authorization.StartsWith("Bearer ", StringComparison.Ordinal) && authorization[7..] is { Length: 64 } token &&
        token.All(Uri.IsHexDigit) ? ResearchEvidence.Hash(token) : throw Unauthorized();
    private static bool FixedEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
    private static MeldAccountResponse Response(AccountRecord account) => new(account.Id, account.Name, account.Email);
    private static ResearchFailure Unauthorized() => new(401, "meld_account_required", "Link this installation to an existing Meld Research account.");
    public void Dispose() => _gate.Dispose();
}
