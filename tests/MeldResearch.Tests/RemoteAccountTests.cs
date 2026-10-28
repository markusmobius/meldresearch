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
    public async Task DeviceLinkRequiresExistingAccountAndProofAndCanBeRevoked()
    {
        var service = await ServiceAsync(new EphemeralDataProtectionProvider());
        string verifier = new('a', 64);
        var link = await service.BeginLinkAsync(new("Synthetic device", ResearchEvidence.Hash(verifier)), CancellationToken.None);
        var notRegistered = await Assert.ThrowsAsync<ResearchFailure>(() => service.ApproveAsync(link.UserCode, _principal, true, CancellationToken.None));
        Assert.Equal("meld_account_required", notRegistered.Code);
        var account = await service.RegisterAsync(_principal, CancellationToken.None);
        Assert.Equal(account, await service.RegisterAsync(_principal, CancellationToken.None));
        await service.ApproveAsync(link.UserCode, _principal, true, CancellationToken.None);
        var invalid = await Assert.ThrowsAsync<ResearchFailure>(() => service.PollAsync(new(link.DeviceCode, new string('b', 64)), CancellationToken.None));
        Assert.Equal("invalid_device_proof", invalid.Code);
        var result = await service.PollAsync(new(link.DeviceCode, verifier), CancellationToken.None);
        Assert.Equal("approved", result.Status);
        Assert.Equal(account, result.Account);
        Assert.Equal(account, await service.AuthenticateAsync("Bearer " + result.AccessToken, CancellationToken.None));
        var tooFast = await Assert.ThrowsAsync<ResearchFailure>(() => service.PollAsync(new(link.DeviceCode, verifier), CancellationToken.None));
        Assert.Equal("device_poll_too_fast", tooFast.Code);
        _clock.UtcNow = Now.AddSeconds(6);
        var retry = await service.PollAsync(new(link.DeviceCode, verifier), CancellationToken.None);
        Assert.Equal(result.AccessToken, retry.AccessToken);
        await service.RevokeAsync("Bearer " + result.AccessToken, CancellationToken.None);
        await Assert.ThrowsAsync<ResearchFailure>(() => service.AuthenticateAsync("Bearer " + result.AccessToken, CancellationToken.None));
        var revoked = await Assert.ThrowsAsync<ResearchFailure>(() => service.PollAsync(new(link.DeviceCode, verifier), CancellationToken.None));
        Assert.Equal("device_link_expired", revoked.Code);
    }

    [Fact]
    public async Task PendingLinksAccountsAndKeysSurviveARestart()
    {
        string verifier = new('c', 64);
        DeviceLinkResponse link;
        await using (var first = Host())
        {
            var service = await ServiceAsync(first.GetRequiredService<IDataProtectionProvider>());
            await service.RegisterAsync(_principal, CancellationToken.None);
            link = await service.BeginLinkAsync(new("Synthetic laptop", ResearchEvidence.Hash(verifier)), CancellationToken.None);
        }
        await using (var second = Host())
        {
            var service = await ServiceAsync(second.GetRequiredService<IDataProtectionProvider>());
            Assert.Equal("Synthetic laptop", await service.DeviceNameAsync(link.UserCode, CancellationToken.None));
            await service.ApproveAsync(link.UserCode, _principal, true, CancellationToken.None);
            var result = await service.PollAsync(new(link.DeviceCode, verifier), CancellationToken.None);
            Assert.Equal("synthetic@example.invalid", (await service.AuthenticateAsync("Bearer " + result.AccessToken, CancellationToken.None)).Email);
        }
        await using var check = await Databases().CreateDbContextAsync();
        Assert.Single(await check.DataProtectionKeys.ToListAsync());
    }

    [Fact]
    public async Task ADeviceLinkCanBeDecidedOnlyOnce()
    {
        var service = await ServiceAsync(new EphemeralDataProtectionProvider());
        await service.RegisterAsync(_principal, CancellationToken.None);
        var link = await service.BeginLinkAsync(new("Synthetic device", ResearchEvidence.Hash(new string('d', 64))), CancellationToken.None);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Outcome(
            service.ApproveAsync(link.UserCode, _principal, index % 2 == 0, CancellationToken.None))));
        Assert.Single(outcomes, outcome => outcome is null);
        Assert.All(outcomes.Where(outcome => outcome is not null), outcome => Assert.Equal("device_link_decided", outcome));
        await using var database = await Databases().CreateDbContextAsync();
        var grant = await database.DeviceGrants.SingleAsync();
        Assert.Equal(grant.Status == "approved" ? 1 : 0, await database.Devices.CountAsync());
    }

    [Fact]
    public async Task PersonalDataAndCredentialsAreNotStoredInPlaintext()
    {
        var service = await ServiceAsync(new EphemeralDataProtectionProvider());
        await service.RegisterAsync(_principal, CancellationToken.None);
        string verifier = new('e', 64);
        var link = await service.BeginLinkAsync(new("Private Device Name", ResearchEvidence.Hash(verifier)), CancellationToken.None);
        await service.ApproveAsync(link.UserCode, _principal, true, CancellationToken.None);
        var result = await service.PollAsync(new(link.DeviceCode, verifier), CancellationToken.None);
        SqliteConnection.ClearAllPools();
        string stored = string.Concat(Directory.GetFiles(_temporary.Path, RemoteDatabase.FileName + "*")
            .Select(path => Encoding.UTF8.GetString(File.ReadAllBytes(path))));
        Assert.Contains("SQLite format 3", stored);
        foreach (string secret in new[] { "synthetic@example.invalid", "Synthetic Person", "Private Device Name", result.AccessToken!, link.DeviceCode })
            Assert.DoesNotContain(secret, stored);
    }

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
