using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MeldResearch.RemoteBackend.Data;
using MeldResearch.RemoteBackend.Services;
using MeldResearch.Research;

namespace MeldResearch.Tests;

public sealed class RemoteAccountTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly TemporaryDirectory _temporary = new();
    private readonly TestClock _clock = new(Now);
    private readonly ClaimsPrincipal _principal = new(new ClaimsIdentity([
        new(ClaimTypes.NameIdentifier, "synthetic-subject"), new(ClaimTypes.Name, "Synthetic Person"),
        new(ClaimTypes.Email, "synthetic@example.invalid"), new("meld_provider", "google"),
        new("meld_email_verified", "true")
    ], "test"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temporary.Dispose();
    }

    [Fact]
    public async Task SignInRequiresAnExistingAccountAndTheVerifierAndIsSingleUse()
    {
        var service = await ServiceAsync(new EphemeralDataProtectionProvider());
        string verifier = new('a', 64);
        var request = Request(verifier, "Synthetic device");
        var notRegistered = await Assert.ThrowsAsync<ResearchFailure>(() => service.IssueSignInCodeAsync(_principal, request, CancellationToken.None));
        Assert.Equal("meld_account_required", notRegistered.Code);
        var account = await service.RegisterAsync(_principal, CancellationToken.None);
        Assert.Equal(account, await service.RegisterAsync(_principal, CancellationToken.None));

        string stolen = await service.IssueSignInCodeAsync(_principal, request, CancellationToken.None);
        var wrongProof = await Assert.ThrowsAsync<ResearchFailure>(() => service.RedeemSignInAsync(new(stolen, new string('b', 64)), CancellationToken.None));
        Assert.Equal("invalid_sign_in_proof", wrongProof.Code);
        var burned = await Assert.ThrowsAsync<ResearchFailure>(() => service.RedeemSignInAsync(new(stolen, verifier), CancellationToken.None));
        Assert.Equal("sign_in_expired", burned.Code);

        string code = await service.IssueSignInCodeAsync(_principal, request, CancellationToken.None);
        var result = await service.RedeemSignInAsync(new(code, verifier), CancellationToken.None);
        Assert.Equal(account, result.Account);
        Assert.Equal(Now.AddDays(30), result.ExpiresAt);
        Assert.Equal(account, await service.AuthenticateAsync("Bearer " + result.AccessToken, CancellationToken.None));
        var reused = await Assert.ThrowsAsync<ResearchFailure>(() => service.RedeemSignInAsync(new(code, verifier), CancellationToken.None));
        Assert.Equal("sign_in_expired", reused.Code);
        await service.RevokeAsync("Bearer " + result.AccessToken, CancellationToken.None);
        await Assert.ThrowsAsync<ResearchFailure>(() => service.AuthenticateAsync("Bearer " + result.AccessToken, CancellationToken.None));

        string late = await service.IssueSignInCodeAsync(_principal, request, CancellationToken.None);
        _clock.UtcNow = Now.AddMinutes(6);
        var expired = await Assert.ThrowsAsync<ResearchFailure>(() => service.RedeemSignInAsync(new(late, verifier), CancellationToken.None));
        Assert.Equal("sign_in_expired", expired.Code);
    }

    [Fact]
    public async Task PendingSignInsAccountsAndKeysSurviveARestart()
    {
        string verifier = new('c', 64), code;
        await using (var first = Host())
        {
            var service = await ServiceAsync(first.GetRequiredService<IDataProtectionProvider>());
            await service.RegisterAsync(_principal, CancellationToken.None);
            code = await service.IssueSignInCodeAsync(_principal, Request(verifier, "Synthetic laptop"), CancellationToken.None);
        }
        await using (var second = Host())
        {
            var service = await ServiceAsync(second.GetRequiredService<IDataProtectionProvider>());
            var result = await service.RedeemSignInAsync(new(code, verifier), CancellationToken.None);
            Assert.Equal("synthetic@example.invalid", (await service.AuthenticateAsync("Bearer " + result.AccessToken, CancellationToken.None)).Email);
        }
        await using var check = await Databases().CreateDbContextAsync();
        Assert.Single(await check.DataProtectionKeys.ToListAsync());
        Assert.Empty(await check.SignInCodes.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentRedemptionsIssueOneCredential()
    {
        var service = await ServiceAsync(new EphemeralDataProtectionProvider());
        await service.RegisterAsync(_principal, CancellationToken.None);
        string verifier = new('d', 64);
        string code = await service.IssueSignInCodeAsync(_principal, Request(verifier, "Synthetic device"), CancellationToken.None);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Outcome(service.RedeemSignInAsync(new(code, verifier), CancellationToken.None))));
        Assert.Single(outcomes, outcome => outcome is null);
        Assert.All(outcomes.Where(outcome => outcome is not null), outcome => Assert.Equal("sign_in_expired", outcome));
        await using var database = await Databases().CreateDbContextAsync();
        Assert.Equal(1, await database.Devices.CountAsync());
    }

    [Fact]
    public async Task PersonalDataAndCredentialsAreNotStoredInPlaintext()
    {
        var service = await ServiceAsync(new EphemeralDataProtectionProvider());
        await service.RegisterAsync(_principal, CancellationToken.None);
        string verifier = new('e', 64);
        string pending = await service.IssueSignInCodeAsync(_principal, Request(verifier, "Private Device Name"), CancellationToken.None);
        string code = await service.IssueSignInCodeAsync(_principal, Request(verifier, "Private Device Name"), CancellationToken.None);
        var result = await service.RedeemSignInAsync(new(code, verifier), CancellationToken.None);
        SqliteConnection.ClearAllPools();
        string stored = string.Concat(Directory.GetFiles(_temporary.Path, RemoteDatabase.FileName + "*")
            .Select(path => Encoding.UTF8.GetString(File.ReadAllBytes(path))));
        Assert.Contains("SQLite format 3", stored);
        foreach (string secret in new[] { "synthetic@example.invalid", "Synthetic Person", "Private Device Name", result.AccessToken, code, pending, verifier })
            Assert.DoesNotContain(secret, stored);
    }

    [Theory]
    [InlineData("http://127.0.0.1:6001/account/callback", true)]
    [InlineData("http://localhost:6010/account/callback", true)]
    [InlineData("https://evil.example/account/callback", false)]
    [InlineData("http://127.0.0.1.evil.example:6001/account/callback", false)]
    [InlineData("http://127.0.0.1:6001/other", false)]
    [InlineData("http://127.0.0.1:6001/account/callback?next=https://evil.example", false)]
    [InlineData("http://user@127.0.0.1:6001/account/callback", false)]
    [InlineData("http://127.0.0.1:80/account/callback", false)]
    [InlineData("https://127.0.0.1:6001/account/callback", false)]
    public void SignInRequestsReturnOnlyToThisComputersLoopbackCallback(string redirect, bool valid)
    {
        var values = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["state"] = new string('a', 64), ["challenge"] = new string('b', 64), ["redirect_uri"] = redirect, ["device"] = "Meld Research on Windows"
        };
        Microsoft.Extensions.Primitives.StringValues Read(string key) => values.GetValueOrDefault(key);
        if (valid)
        {
            var request = LocalSignIn.From(Read)!;
            Assert.Equal(redirect, request.RedirectUri);
            Assert.StartsWith(redirect + "?code=", request.Callback(new string('c', 64)));
            Assert.Equal(request, LocalSignIn.From(key => System.Web.HttpUtility.ParseQueryString(request.Query)[key]));
        }
        else Assert.Equal("invalid_sign_in_request", Assert.Throws<ResearchFailure>(() => LocalSignIn.From(Read)).Code);
        Assert.Null(LocalSignIn.From(_ => default));
    }

    private static LocalSignIn Request(string verifier, string device) =>
        new(new string('f', 64), ResearchEvidence.Hash(verifier), "http://127.0.0.1:6001/account/callback", device);
    private async Task<AccountService> ServiceAsync(IDataProtectionProvider protection)
    {
        var databases = Databases();
        await RemoteDatabase.InitializeAsync(databases, _temporary.Path, CancellationToken.None);
        var options = new RemoteOptions { DataDirectory = _temporary.Path, PublicOrigin = new("https://meld.example.invalid"), Dummy = false, GoogleClientId = "synthetic.apps.googleusercontent.com" };
        return new AccountService(databases, options, protection, _clock);
    }

    private IDbContextFactory<RemoteDbContext> Databases() => new TestDatabases(RemoteDatabase.ConnectionString(_temporary.Path));

    // Mirrors Program.cs: the data-protection key ring is persisted in the same database.
    private ServiceProvider Host()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<RemoteDbContext>(database => database.UseSqlite(RemoteDatabase.ConnectionString(_temporary.Path)));
        services.AddDataProtection().SetApplicationName("MeldResearch.Remote.Tests").PersistKeysToDbContext<RemoteDbContext>();
        return services.BuildServiceProvider();
    }

    private static async Task<string?> Outcome(Task operation)
    {
        try { await operation; return null; }
        catch (ResearchFailure failure) { return failure.Code; }
    }

    private sealed class TestDatabases(string connection) : IDbContextFactory<RemoteDbContext>
    {
        private readonly DbContextOptions<RemoteDbContext> _options = new DbContextOptionsBuilder<RemoteDbContext>().UseSqlite(connection).Options;
        public RemoteDbContext CreateDbContext() => new(_options);
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
