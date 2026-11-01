using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MeldResearch.LocalBackend.Services.Research;
using MeldResearch.LocalBackend.Services.Updates;
using MeldResearch.Research;
using static MeldResearch.Tests.ResearchCoordinatorTests;

namespace MeldResearch.Tests;

public sealed class GmailTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private const string Redirect = "http://127.0.0.1:6010/gmail/callback";

    [Fact]
    public void TheConsentUrlAsksOnlyForReadOnlyGmailWithPkceAndOfflineAccess()
    {
        // RFC 7636 appendix B example.
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", IGmailGoogle.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        var google = new GmailGoogle(new Clients(new Handler(_ => throw new InvalidOperationException())));
        var url = google.AuthorizationUrl(new("client-id", "secret", false), Redirect, "state1", "challenge1");
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        Assert.Equal("accounts.google.com", url.Host);
        Assert.Equal(("client-id", Redirect, "code", "S256", "challenge1", "state1", "offline"),
            (query["client_id"], query["redirect_uri"], query["response_type"], query["code_challenge_method"], query["code_challenge"], query["state"], query["access_type"]));
        Assert.Equal(["openid", "email", IGmailGoogle.ReadOnlyScope], query["scope"]!.Split(' '));
        Assert.DoesNotContain("secret", url.AbsoluteUri);
    }

    [Fact]
    public async Task TheGoogleClientExchangesWithTheVerifierPagesThreadsAndReportsAWithdrawnGrant()
    {
        var requests = new List<(string Url, string Body)>();
        var google = new GmailGoogle(new Clients(new Handler(request =>
        {
            string body = request.Content?.ReadAsStringAsync().Result ?? "";
            requests.Add((request.RequestUri!.AbsoluteUri, body));
            string url = request.RequestUri.AbsoluteUri;
            if (url.StartsWith("https://oauth2.googleapis.com/token") && body.Contains("grant_type=refresh_token"))
                return Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"Token has been expired or revoked."}""");
            if (url.StartsWith("https://oauth2.googleapis.com/token"))
                return Json(HttpStatusCode.OK, """{"access_token":"at","expires_in":3599,"refresh_token":"rt","scope":"openid https://www.googleapis.com/auth/gmail.readonly email","token_type":"Bearer","id_token":"x"}""");
            if (url.Contains("/threads") && !url.Contains("pageToken"))
                return Json(HttpStatusCode.OK, """{"threads":[{"id":"t1","snippet":"s","historyId":"9"},{"id":"t2"}],"nextPageToken":"p2","resultSizeEstimate":3}""");
            if (url.Contains("/threads")) return Json(HttpStatusCode.OK, """{"threads":[{"id":"t3"}],"resultSizeEstimate":3}""");
            if (url.EndsWith("/profile")) return Json(HttpStatusCode.OK, """{"emailAddress":"markus@example.invalid","messagesTotal":5,"threadsTotal":3,"historyId":"77"}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        })));
        var client = new GmailClientResponse("client-id", "secret", false);
        var tokens = await google.ExchangeAsync(client, "code1", Redirect, "verifier1", None);
        Assert.Equal(("at", "rt"), (tokens.AccessToken, tokens.RefreshToken));
        Assert.Contains(IGmailGoogle.ReadOnlyScope, tokens.Scope);
        var exchange = System.Web.HttpUtility.ParseQueryString(requests[0].Body);
        Assert.Equal(("code1", "verifier1", "secret", Redirect, "authorization_code"),
            (exchange["code"], exchange["code_verifier"], exchange["client_secret"], exchange["redirect_uri"], exchange["grant_type"]));

        Assert.Equal([new GmailThreadRef("t1", "9"), new GmailThreadRef("t2", ""), new GmailThreadRef("t3", "")],
            await google.ParticipatedThreadsAsync("at", 30, None));
        Assert.Contains(Uri.EscapeDataString("in:sent newer_than:30d -in:chats"), requests[1].Url);
        Assert.Equal(new GmailProfile("markus@example.invalid", "77"), await google.ProfileAsync("at", None));
        var withdrawn = await Assert.ThrowsAsync<ResearchFailure>(() => google.RefreshAsync(client, "rt", None));
        Assert.Equal("gmail_reconnect", withdrawn.Code);
    }

    private sealed record Setup(GmailConnector Connector, MemoryStore Store, Remote Remote, FakeGoogle Google);

    private static Setup New(GmailClientResponse? client = null, bool configured = true, LocalResearchState? state = null)
    {
        var store = new MemoryStore(state ?? new(1, Credential, null, null, [], []));
        var remote = new Remote { GmailClient = configured ? client ?? new("client-id", "secret", false) : null };
        var google = new FakeGoogle();
        return new(new GmailConnector(store, remote, google, TimeProvider.System, NullLogger<GmailConnector>.Instance), store, remote, google);
    }

    private static string StateOf(string url) => System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["state"]!;

    [Fact]
    public async Task ConnectingNeedsConsentAndAOneTimeStateThenStoresTheGrantAndCountsThreads()
    {
        var setup = New();
        Assert.Equal("gmail_consent_required", (await Assert.ThrowsAsync<ResearchFailure>(() => setup.Connector.BeginAsync(Redirect, null, None))).Code);
        var begin = await setup.Connector.BeginAsync(Redirect, GmailConnector.ConsentVersion, None);
        string state = StateOf(begin.AuthorizationUrl);

        // A callback that this app did not start is refused and does not disturb the real one.
        Assert.NotNull(await setup.Connector.CompleteAsync("code", new string('0', 64), null, None));
        Assert.Empty(setup.Store.State.GmailAccounts ?? []);
        Assert.Null(await setup.Connector.CompleteAsync("code", state, null, None));
        // Single use: the same callback again is refused.
        Assert.NotNull(await setup.Connector.CompleteAsync("code", state, null, None));
        Assert.Single(setup.Google.Exchanges);
        var stored = Assert.Single(setup.Store.State.GmailAccounts!);
        Assert.Equal(("first@example.invalid", "refresh-first@example.invalid", "connected", 4), (stored.Address, stored.RefreshToken, stored.Status, stored.Threads));
        Assert.Equal(Redirect, setup.Google.Exchanges.Single().RedirectUri);
        var status = setup.Connector.Status(setup.Store.State);
        Assert.Equal("first@example.invalid", Assert.Single(status.Accounts).Address);
        Assert.DoesNotContain("refresh-", System.Text.Json.JsonSerializer.Serialize(status, ResearchJson.Options));

        // A second account; a refused consent is reported and stores nothing.
        begin = await setup.Connector.BeginAsync(Redirect, GmailConnector.ConsentVersion, None);
        Assert.Null(await setup.Connector.CompleteAsync("code", StateOf(begin.AuthorizationUrl), null, None));
        Assert.Equal(2, setup.Connector.Status(setup.Store.State).Accounts.Count);
        begin = await setup.Connector.BeginAsync(Redirect, GmailConnector.ConsentVersion, None);
        Assert.Contains("not allowed", await setup.Connector.CompleteAsync(null, StateOf(begin.AuthorizationUrl), "access_denied", None));
        Assert.Contains("not allowed", setup.Connector.Status(setup.Store.State).LastError);
    }

    [Fact]
    public async Task WithoutTheGmailPermissionNothingIsStoredAndTheGrantIsRevoked()
    {
        var setup = New();
        setup.Google.Scope = "openid email";
        var begin = await setup.Connector.BeginAsync(Redirect, GmailConnector.ConsentVersion, None);
        Assert.Contains("permission to read your email", await setup.Connector.CompleteAsync("code", StateOf(begin.AuthorizationUrl), null, None));
        Assert.Empty(setup.Store.State.GmailAccounts ?? []);
        Assert.Equal(["refresh-first@example.invalid"], setup.Google.Revoked);
    }

    [Fact]
    public async Task ChecksRefreshAccessAndAWithdrawnGrantAsksToReconnect()
    {
        var setup = New();
        var begin = await setup.Connector.BeginAsync(Redirect, GmailConnector.ConsentVersion, None);
        await setup.Connector.CompleteAsync("code", StateOf(begin.AuthorizationUrl), null, None);
        setup.Google.ExpireAccess = true;
        Assert.Equal(4, (await setup.Connector.CheckAsync("first@example.invalid", None)).Threads);
        Assert.Equal(1, setup.Google.Refreshes);

        setup.Google.Withdrawn = true;
        Assert.Equal("gmail_reconnect", (await Assert.ThrowsAsync<ResearchFailure>(() => setup.Connector.CheckAsync("FIRST@example.invalid", None))).Code);
        Assert.Equal("reconnect", setup.Store.State.GmailAccounts!.Single().Status);
    }

    [Fact]
    public async Task RemovingRevokesAtGoogleAndRemovesTheAccountAndItsWorkstreams()
    {
        string workstream = "gmail-" + new string('d', 32), other = "gmail-" + new string('e', 32);
        var setup = New();
        var begin = await setup.Connector.BeginAsync(Redirect, GmailConnector.ConsentVersion, None);
        await setup.Connector.CompleteAsync("code", StateOf(begin.AuthorizationUrl), null, None);
        setup.Store.State = setup.Store.State with { Projects = [
            Included(workstream) with { Source = ResearchSources.Gmail, Parent = "first@example.invalid" },
            Included(other) with { Source = ResearchSources.Gmail, Parent = "second@example.invalid" }, Included()] };

        await setup.Connector.RemoveAsync("first@example.invalid", None);
        Assert.Equal(["refresh-first@example.invalid"], setup.Google.Revoked);
        var tombstone = setup.Store.State.GmailAccounts!.Single();
        Assert.Equal((true, (string?)null), (tombstone.Removed, tombstone.RefreshToken));
        Assert.Empty(setup.Connector.Status(setup.Store.State).Accounts);
        Assert.Equal([ProjectId, other], setup.Store.State.Projects.Select(project => project.ProjectId).Order(StringComparer.Ordinal));
        await Assert.ThrowsAsync<ResearchFailure>(() => setup.Connector.CheckAsync("first@example.invalid", None));
    }

    [Fact]
    public async Task WithoutGmailOnTheServerConnectingIsRefused()
    {
        var setup = New(configured: false);
        Assert.Equal("gmail_not_configured", (await Assert.ThrowsAsync<ResearchFailure>(() =>
            setup.Connector.BeginAsync(Redirect, GmailConnector.ConsentVersion, None))).Code);
        Assert.False(await setup.Connector.ConfiguredAsync(None));
    }

    [Fact]
    public async Task TheGrantSyncsEndToEndEncryptedButNeverGoesIntoABackup()
    {
        var box = new BlobBox();
        var snapshot = Files(("main.tex", "Main text."));
        var firstStore = new MemoryStore(new(1, Credential, null, null, [Included()], [],
            Decisions: [new ProjectDecision(Account.Id, ProjectId, true, DateTimeOffset.UtcNow)]));
        var firstRemote = new Remote { Box = box };
        using var first = new ResearchCoordinator(firstStore, firstRemote, new Source(snapshot), new(new Uri("https://meld.example.invalid"), false, 300),
            NullLogger<ResearchCoordinator>.Instance, TimeProvider.System);
        await first.WithSyncAsync((sync, credential) => sync.CreateAsync(credential, "correct horse battery", None), None);
        var connector = new GmailConnector(firstStore, firstRemote, new FakeGoogle(), TimeProvider.System, NullLogger<GmailConnector>.Instance);
        var begin = await connector.BeginAsync(Redirect, GmailConnector.ConsentVersion, None);
        await connector.CompleteAsync("code", StateOf(begin.AuthorizationUrl), null, None);
        await Drain(first);
        Assert.All(box.Contents.Values, content => Assert.DoesNotContain("refresh-first", Encoding.UTF8.GetString(content)));

        var secondStore = new MemoryStore(new(1, Credential, null, null, [], []));
        using var second = new ResearchCoordinator(secondStore, new Remote { Box = box }, new Source(snapshot), new(new Uri("https://meld.example.invalid"), false, 300),
            NullLogger<ResearchCoordinator>.Instance, TimeProvider.System);
        await second.WithSyncAsync(async (sync, credential) => { await sync.UnlockAsync(credential, new("correct horse battery", null), None); return true; }, None);
        await Drain(second);
        Assert.Equal("refresh-first@example.invalid", secondStore.State.GmailAccounts!.Single().RefreshToken);

        // Removing it on the first computer removes it on the second too.
        await connector.RemoveAsync("first@example.invalid", None);
        await Drain(first, force: true);
        await Drain(second, force: true);
        Assert.True(secondStore.State.GmailAccounts!.Single().Removed);
        Assert.Null(secondStore.State.GmailAccounts!.Single().RefreshToken);

        // A backup is not encrypted: no Gmail account or grant goes into it.
        var updates = new UpdateOptions(false, "test", "development", null, null, null, null, new Uri("https://meld.example.invalid"), false,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
        await connector.BeginAsync(Redirect, GmailConnector.ConsentVersion, None).ContinueWith(async task =>
            await connector.CompleteAsync("code", StateOf(task.Result.AuthorizationUrl), null, None)).Unwrap();
        var zip = (await new ResearchArchive(firstStore, first, updates, TimeProvider.System, NullLogger<ResearchArchive>.Instance).ExportAsync(None)).Zip;
        using var archive = new ZipArchive(new MemoryStream(zip));
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            string text = reader.ReadToEnd();
            Assert.DoesNotContain("refresh-", text);
            Assert.DoesNotContain("gmailAccounts", text);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Google stand-in: each exchange connects the next address; access tokens can be made to expire.</summary>
    internal sealed class FakeGoogle : IGmailGoogle
    {
        /// <summary>The threads the user wrote in, newest first.</summary>
        public List<GmailThread> Mailbox { get; set; } = [.. Enumerable.Range(1, 4).Select(index => new GmailThread($"t{index}", "1", $"Thread {index}", []))];
        public int ThreadReads { get; private set; }
        public bool Unavailable { get; set; }
        private static readonly string[] Addresses = ["first@example.invalid", "second@example.invalid"];
        private int _next;
        public string Scope { get; set; } = "openid email " + IGmailGoogle.ReadOnlyScope;
        public bool ExpireAccess { get; set; }
        public bool Withdrawn { get; set; }
        public int Refreshes { get; private set; }
        public List<string> Revoked { get; } = [];
        public List<(string Code, string RedirectUri, string Verifier)> Exchanges { get; } = [];

        public Uri AuthorizationUrl(GmailClientResponse client, string redirectUri, string state, string challenge) =>
            new($"https://accounts.example.invalid/auth?state={state}&challenge={challenge}");

        public Task<GmailTokens> ExchangeAsync(GmailClientResponse client, string code, string redirectUri, string verifier, CancellationToken ct)
        {
            Exchanges.Add((code, redirectUri, verifier));
            string address = Addresses[_next++ % Addresses.Length];
            return Task.FromResult(new GmailTokens("access-" + address, "refresh-" + address, DateTimeOffset.UtcNow.AddHours(1), Scope));
        }

        public Task<GmailTokens> RefreshAsync(GmailClientResponse client, string refreshToken, CancellationToken ct)
        {
            if (Withdrawn) throw new ResearchFailure(401, "gmail_reconnect", "Connect it again.");
            Refreshes++;
            return Task.FromResult(new GmailTokens("access-" + refreshToken["refresh-".Length..], null, DateTimeOffset.UtcNow.AddHours(1), Scope));
        }

        public Task RevokeAsync(string token, CancellationToken ct) { Revoked.Add(token); return Task.CompletedTask; }
        public Task<GmailProfile> ProfileAsync(string accessToken, CancellationToken ct) => Task.FromResult(new GmailProfile(accessToken["access-".Length..], "5"));

        public Task<IReadOnlyList<GmailThreadRef>> ParticipatedThreadsAsync(string accessToken, int days, CancellationToken ct)
        {
            if (ExpireAccess) { ExpireAccess = false; throw new ResearchFailure(401, "gmail_token_expired", "Expired."); }
            if (Withdrawn) throw new ResearchFailure(401, "gmail_token_expired", "Expired.");
            if (Unavailable) throw new ResearchFailure(503, "gmail_unavailable", "Gmail did not answer.");
            return Task.FromResult<IReadOnlyList<GmailThreadRef>>([.. Mailbox.Select(thread => new GmailThreadRef(thread.Id, thread.HistoryId))]);
        }

        public Task<GmailThread> ThreadAsync(string accessToken, string threadId, CancellationToken ct)
        {
            ThreadReads++;
            return Task.FromResult(Mailbox.FirstOrDefault(thread => thread.Id == threadId) ??
                throw new ResearchFailure(404, "gmail_thread_missing", "The thread no longer exists."));
        }
    }
}
