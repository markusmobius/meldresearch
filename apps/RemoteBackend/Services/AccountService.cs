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
    Task<string> IssueSignInCodeAsync(ClaimsPrincipal principal, LocalSignIn request, CancellationToken ct);
    Task<RedeemSignInResponse> RedeemSignInAsync(RedeemSignInRequest request, CancellationToken ct);
    Task<MeldAccountResponse> AuthenticateAsync(string authorization, CancellationToken ct);
    Task RevokeAsync(string authorization, CancellationToken ct);
}

internal sealed class AccountService(
    IDbContextFactory<RemoteDbContext> databases, RemoteOptions options, IDataProtectionProvider protection, TimeProvider clock)
    : IAccountService
{
    private const int MaxPendingCodes = 100;
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

    public async Task<string> IssueSignInCodeAsync(ClaimsPrincipal principal, LocalSignIn request, CancellationToken ct)
    {
        var identity = Identity(principal);
        await using var database = await databases.CreateDbContextAsync(ct);
        var account = await FindAccountAsync(database, identity.Subject, ct) ??
            throw new ResearchFailure(403, "meld_account_required", "Create a Meld Research account on this website before signing in to the app.");
        var now = clock.GetUtcNow();
        await using var transaction = await database.Database.BeginTransactionAsync(ct);
        await database.SignInCodes.Where(code => code.ExpiresAt <= now).ExecuteDeleteAsync(ct);
        if (await database.SignInCodes.CountAsync(ct) >= MaxPendingCodes)
            throw new ResearchFailure(429, "sign_in_capacity", "Too many sign-ins are pending. Try again shortly.");
        string code = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        database.SignInCodes.Add(new()
        {
            CodeHash = ResearchEvidence.Hash(code), AccountId = account.Id, ProtectedName = _protector.Protect(request.Device),
            Challenge = request.Challenge, ExpiresAt = now.AddMinutes(5)
        });
        await database.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return code;
    }

    public async Task<RedeemSignInResponse> RedeemSignInAsync(RedeemSignInRequest request, CancellationToken ct)
    {
        if (request.Code is not { Length: 64 } || request.Verifier is not { Length: 64 } ||
            !request.Code.All(Uri.IsHexDigit) || !request.Verifier.All(Uri.IsHexDigit))
            throw new ResearchFailure(400, "invalid_sign_in_proof", "The sign-in proof is invalid.");
        string codeHash = ResearchEvidence.Hash(request.Code.ToLowerInvariant());
        await using var database = await databases.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow();
        await using var transaction = await database.Database.BeginTransactionAsync(ct);
        var grant = await database.SignInCodes.AsNoTracking().Include(item => item.Account)
            .FirstOrDefaultAsync(item => item.CodeHash == codeHash && item.ExpiresAt > now, ct);
        // Each code is single-use, even when the proof is wrong; concurrent redemptions race on this delete.
        if (grant?.Account is not { } account ||
            await database.SignInCodes.Where(item => item.CodeHash == codeHash).ExecuteDeleteAsync(ct) != 1)
            throw new ResearchFailure(410, "sign_in_expired", "The sign-in expired or was already used. Sign in again.");
        if (!FixedEquals(ResearchEvidence.Hash(request.Verifier.ToLowerInvariant()), grant.Challenge))
        {
            await transaction.CommitAsync(ct);
            throw new ResearchFailure(403, "invalid_sign_in_proof", "The sign-in proof is invalid.");
        }
        string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var expires = now.AddDays(30);
        await database.Devices.Where(device => device.ExpiresAt <= now).ExecuteDeleteAsync(ct);
        database.Devices.Add(new()
        {
            Id = Guid.NewGuid(), AccountId = account.Id, TokenHash = ResearchEvidence.Hash(token), ProtectedName = grant.ProtectedName,
            CreatedAt = now, ExpiresAt = expires
        });
        await database.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(token, expires, Response(account));
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
        await transaction.CommitAsync(ct);
    }

    private static Task<AccountEntity?> FindAccountAsync(RemoteDbContext database, string subject, CancellationToken ct) =>
        database.Accounts.AsNoTracking().FirstOrDefaultAsync(account => account.Subject == subject, ct);

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
    private static ResearchFailure Unauthorized() => new(401, "meld_account_required", "Link this installation to an existing Meld Research account.");
}
