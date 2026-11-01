using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MeldResearch.RemoteBackend.Data;
using MeldResearch.RemoteBackend.Services;

namespace MeldResearch.Tests;

public sealed class ProfilePictureTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
    private readonly TemporaryDirectory _temporary = new();

    [Theory]
    [InlineData("https://lh3.googleusercontent.com/a/ACg8ocK=s96-c", true)]
    [InlineData("http://lh3.googleusercontent.com/a/x", false)]
    [InlineData("https://googleusercontent.com.evil.example/a", false)]
    [InlineData("https://lh3.googleusercontent.com:8443/a", false)]
    [InlineData("https://example.com/me.png", false)]
    [InlineData(null, false)]
    public void OnlyGoogleHostedHttpsPicturesAreFetched(string? url, bool expected) =>
        Assert.Equal(expected, ProfilePictures.IsGooglePicture(url));

    [Fact]
    public void PicturesAreRequestedAt192PixelsAndOnlyRealImagesAreKept()
    {
        Assert.Equal("https://lh3.googleusercontent.com/a/ACg8ocK=s192-c", ProfilePictures.Sized("https://lh3.googleusercontent.com/a/ACg8ocK=s96-c"));
        Assert.Equal("https://lh3.googleusercontent.com/a/plain", ProfilePictures.Sized("https://lh3.googleusercontent.com/a/plain"));
        Assert.Equal("image/png", ProfilePictures.Sniff(Png));
        Assert.Equal("image/jpeg", ProfilePictures.Sniff([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Null(ProfilePictures.Sniff("<svg onload=alert(1)>"u8));
        Assert.Null(ProfilePictures.Sniff("<html>"u8));
    }

    [Fact]
    public async Task APictureIsStoredEncryptedFetchedOncePerUrlAndRemovedWhenGoogleHasNone()
    {
        IDbContextFactory<RemoteDbContext> databases = new TestDatabases(RemoteDatabase.ConnectionString(_temporary.Path));
        await RemoteDatabase.InitializeAsync(databases, _temporary.Path, CancellationToken.None);
        var account = Guid.NewGuid();
        await using (var database = await databases.CreateDbContextAsync())
        {
            database.Accounts.Add(new() { Id = account, Subject = "google:1", ProtectedName = "n", ProtectedEmail = "e", CreatedAt = DateTimeOffset.UtcNow });
            await database.SaveChangesAsync();
        }
        var requests = new List<string>();
        var clients = new Clients(new Handler(request =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) };
        }));
        var pictures = new ProfilePictures(databases, clients, new EphemeralDataProtectionProvider(), TimeProvider.System,
            NullLogger<ProfilePictures>.Instance);

        await pictures.UpdateAsync(account, "https://lh3.googleusercontent.com/a/one=s96-c", CancellationToken.None);
        var stored = await pictures.FindAsync(account, CancellationToken.None);
        Assert.Equal("image/png", stored!.ContentType);
        Assert.Equal(Png, stored.Image);
        Assert.Equal(["https://lh3.googleusercontent.com/a/one=s192-c"], requests);
        await using (var database = await databases.CreateDbContextAsync())
            Assert.NotEqual(Png, (await database.AccountPictures.SingleAsync()).ProtectedImage);

        await pictures.UpdateAsync(account, "https://lh3.googleusercontent.com/a/one=s96-c", CancellationToken.None);
        Assert.Single(requests);
        await pictures.UpdateAsync(account, "https://lh3.googleusercontent.com/a/two=s96-c", CancellationToken.None);
        Assert.Equal(2, requests.Count);
        await pictures.UpdateAsync(account, null, CancellationToken.None);
        Assert.Null(await pictures.FindAsync(account, CancellationToken.None));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temporary.Dispose();
    }

    private sealed class TestDatabases(string connection) : IDbContextFactory<RemoteDbContext>
    {
        private readonly DbContextOptions<RemoteDbContext> _options = new DbContextOptionsBuilder<RemoteDbContext>().UseSqlite(connection).Options;
        public RemoteDbContext CreateDbContext() => new(_options);
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }
}
