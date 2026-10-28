using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using MeldResearch.RemoteBackend.Data;
using MeldResearch.Research;

namespace MeldResearch.RemoteBackend.Services;

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

internal sealed class AccountService(
    IDbContextFactory<RemoteDbContext> databases, RemoteOptions options, IDataProtectionProvider protection, TimeProvider clock)
    : IAccountService
{
    private const int MaxPendingGrants = 100;
    private const string Pending = "pending", Approved = "approved", Denied = "denied";
    private readonly IDataProtector _protector = protection.CreateProtector("MeldResearch.Remote.Database.v1");

    public async Task<MeldAccountResponse> RegisterAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var identity = Identity(principal);
        await using var database = await databases.CreateDbContextAsync(ct);
        if (await FindAccountAsync(database, identity.Subject, ct) is { } existing) return Response(existing);
        var account = new AccountEntity
        {
            Id = Guid.NewGuid(), Subject = identity.Subject, CreatedAt = clock.GetUtcNow(),
            ProtectedName = _protector.Protect(identity.Name), ProtectedEmail = _protector.Protect(identity.Email)
        };
        database.Accounts.Add(account);
        try { await database.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // A concurrent sign-up for the same identity won the unique Subject index.
            database.ChangeTracker.Clear();
            return Response(await FindAccountAsync(database, identity.Subject, ct) ?? throw new InvalidOperationException("Account registration failed."));
        }
        return Response(account);
    }

    public async Task<MeldAccountResponse?> FindAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var identity = Identity(principal);
        await using var database = await databases.CreateDbContextAsync(ct);
        return await FindAccountAsync(database, identity.Subject, ct) is { } account ? Response(account) : null;
    }

    public async Task<DeviceLinkResponse> BeginLinkAsync(CreateDeviceLinkRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceName) || request.DeviceName.Length > 100 ||
            request.Challenge is not { Length: 64 } || !request.Challenge.All(Uri.IsHexDigit))
            throw new ResearchFailure(400, "invalid_device_link", "A device name and SHA-256 proof challenge are required.");
        await using var database = await databases.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow();
        await using var transaction = await database.Database.BeginTransactionAsync(ct);
        await database.DeviceGrants.Where(grant => grant.ExpiresAt <= now).ExecuteDeleteAsync(ct);
        if (await database.DeviceGrants.CountAsync(ct) >= MaxPendingGrants)
            throw new ResearchFailure(429, "device_link_capacity", "Too many device links are pending. Try again shortly.");
        string code = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        string userCode;
        do { userCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)); }
        while (await database.DeviceGrants.AnyAsync(grant => grant.UserCode == userCode, ct));
        var expires = now.AddMinutes(10);
        database.DeviceGrants.Add(new()
        {
            DeviceCodeHash = ResearchEvidence.Hash(code), UserCode = userCode, ProtectedName = _protector.Protect(request.DeviceName),
            Challenge = request.Challenge.ToLowerInvariant(), Status = Pending, ExpiresAt = expires, NextPollAt = now
        });
        await database.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(code, userCode, new Uri(options.PublicOrigin, "connect?code=" + userCode).AbsoluteUri, expires, 5);
    }

    public async Task<string> DeviceNameAsync(string userCode, CancellationToken ct)
    {
        await using var database = await databases.CreateDbContextAsync(ct);
        return _protector.Unprotect((await FindGrantAsync(database, userCode, ct)).ProtectedName);
    }

    public async Task ApproveAsync(string userCode, ClaimsPrincipal principal, bool allow, CancellationToken ct)
    {
        var identity = Identity(principal);
        await using var database = await databases.CreateDbContextAsync(ct);
        await using var transaction = await database.Database.BeginTransactionAsync(ct);
        var grant = await FindGrantAsync(database, userCode, ct);
        if (grant.Status != Pending) throw Decided();
        var account = await FindAccountAsync(database, identity.Subject, ct) ??
            throw new ResearchFailure(403, "meld_account_required", "Create a Meld Research account on this website before linking a device.");
        var now = clock.GetUtcNow();
        if (!allow)
        {
            if (await database.DeviceGrants.Where(item => item.DeviceCodeHash == grant.DeviceCodeHash && item.Status == Pending)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.Status, Denied), ct) == 0) throw Decided();
            await transaction.CommitAsync(ct);
            return;
        }
        string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        string tokenHash = ResearchEvidence.Hash(token);
        string protectedToken = _protector.Protect(token);
        var expires = now.AddDays(30);
        if (await database.DeviceGrants.Where(item => item.DeviceCodeHash == grant.DeviceCodeHash && item.Status == Pending)
            .ExecuteUpdateAsync(set => set
                .SetProperty(item => item.Status, Approved)
                .SetProperty(item => item.AccountId, account.Id)
                .SetProperty(item => item.ProtectedAccessToken, protectedToken)
                .SetProperty(item => item.AccessTokenHash, tokenHash)
                .SetProperty(item => item.CredentialExpiresAt, expires), ct) == 0) throw Decided();
        await database.Devices.Where(device => device.ExpiresAt <= now).ExecuteDeleteAsync(ct);
        database.Devices.Add(new()
        {
            Id = Guid.NewGuid(), AccountId = account.Id, TokenHash = tokenHash, ProtectedName = grant.ProtectedName,
            CreatedAt = now, ExpiresAt = expires
        });
        await database.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<PollDeviceLinkResponse> PollAsync(PollDeviceLinkRequest request, CancellationToken ct)
    {
        if (request.DeviceCode is not { Length: 64 } || request.Verifier is not { Length: 64 } ||
            !request.DeviceCode.All(Uri.IsHexDigit) || !request.Verifier.All(Uri.IsHexDigit))
            throw new ResearchFailure(400, "invalid_device_proof", "The device-link proof is invalid.");
        string codeHash = ResearchEvidence.Hash(request.DeviceCode);
        await using var database = await databases.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow();
        var grant = await database.DeviceGrants.AsNoTracking().Include(item => item.Account)
            .FirstOrDefaultAsync(item => item.DeviceCodeHash == codeHash && item.ExpiresAt > now, ct) ??
            throw new ResearchFailure(410, "device_link_expired", "The device link expired. Start a new link.");
        if (!FixedEquals(ResearchEvidence.Hash(request.Verifier), grant.Challenge))
            throw new ResearchFailure(403, "invalid_device_proof", "The device-link proof is invalid.");
        var next = now.AddSeconds(5);
        // The conditional update makes concurrent polls race-free: only one caller can claim each interval.
        if (await database.DeviceGrants.Where(item => item.DeviceCodeHash == codeHash && item.NextPollAt <= now)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.NextPollAt, next), ct) == 0)
            throw new ResearchFailure(429, "device_poll_too_fast", "Wait before checking device approval again.");
        return new(grant.Status, grant.ProtectedAccessToken is { } token ? _protector.Unprotect(token) : null,
            grant.CredentialExpiresAt, grant.Account is { } account ? Response(account) : null);
    }

    public async Task<MeldAccountResponse> AuthenticateAsync(string authorization, CancellationToken ct)
    {
        string hash = TokenHash(authorization);
        await using var database = await databases.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow();
        var account = await database.Devices.AsNoTracking()
            .Where(device => device.TokenHash == hash && device.ExpiresAt > now)
            .Select(device => device.Account).FirstOrDefaultAsync(ct);
        return account is null ? throw Unauthorized() : Response(account);
    }

    public async Task RevokeAsync(string authorization, CancellationToken ct)
    {
        string hash = TokenHash(authorization);
        await using var database = await databases.CreateDbContextAsync(ct);
        await using var transaction = await database.Database.BeginTransactionAsync(ct);
        await database.Devices.Where(device => device.TokenHash == hash).ExecuteDeleteAsync(ct);
        await database.DeviceGrants.Where(grant => grant.AccessTokenHash == hash).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static Task<AccountEntity?> FindAccountAsync(RemoteDbContext database, string subject, CancellationToken ct) =>
        database.Accounts.AsNoTracking().FirstOrDefaultAsync(account => account.Subject == subject, ct);

    private async Task<DeviceGrantEntity> FindGrantAsync(RemoteDbContext database, string userCode, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return await database.DeviceGrants.AsNoTracking().FirstOrDefaultAsync(grant => grant.UserCode == userCode && grant.ExpiresAt > now, ct) ??
            throw new ResearchFailure(410, "device_link_expired", "The device link expired or does not exist.");
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

    private MeldAccountResponse Response(AccountEntity account) =>
        new(account.Id, _protector.Unprotect(account.ProtectedName), _protector.Unprotect(account.ProtectedEmail));

    private static string TokenHash(string authorization) =>
        authorization.StartsWith("Bearer ", StringComparison.Ordinal) && authorization[7..] is { Length: 64 } token &&
        token.All(Uri.IsHexDigit) ? ResearchEvidence.Hash(token) : throw Unauthorized();
    private static bool FixedEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
    private static ResearchFailure Decided() => new(409, "device_link_decided", "This device link has already been decided.");
    private static ResearchFailure Unauthorized() => new(401, "meld_account_required", "Link this installation to an existing Meld Research account.");
}
