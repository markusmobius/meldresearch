using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MeldResearch.LocalBackend.Contracts;
using MeldResearch.LocalBackend.Services.Research;
using MeldResearch.RemoteBackend.Services;
using MeldResearch.Research;
using static MeldResearch.Tests.ResearchCoordinatorTests;

namespace MeldResearch.Tests;

public sealed class GmailImportTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private const string Address = "first@example.invalid";

    [Fact]
    public void EmailTextKeepsOnlyWhatTheSenderWrote()
    {
        Assert.Equal("Thanks, that works.", EmailText.Clean("Thanks, that works.\r\n\r\nOn Tue, Nov 4, 2026 at 9:00 AM Lukas Bolte <\r\nlukas@example.invalid> wrote:\r\n> The old text\r\n> more"));
        Assert.Equal("Done.", EmailText.Clean("Done.\n\nFrom: Lukas Bolte\nSent: Monday, November 3, 2026\nTo: Demo\nSubject: Plan\n\nOld text"));
        Assert.Equal("See below.", EmailText.Clean("See below.\n\n---------- Forwarded message ---------\nFrom: someone"));
        Assert.Equal("Best,\nDemo", EmailText.Clean("Best,\nDemo\n-- \nDemo Researcher\nProfessor of Economics"));
        Assert.Equal("Agreed.\n\nAlso this.", EmailText.Clean("Agreed.\n> inline quote\n\n\n\nAlso   this."));
        string capped = EmailText.Clean(string.Join('\n', Enumerable.Repeat(new string('x', 99), 120)));
        Assert.True(capped.Length <= EmailText.MaxCharacters && capped.EndsWith("[…]"), capped.Length.ToString());
        Assert.Equal("Sounds good & thanks\nNext line", EmailText.Clean(EmailText.FromHtml(
            "<html><head><style>p{}</style></head><div>Sounds <b>good</b> &amp; thanks</div>Next line<blockquote>quoted</blockquote></html>")));
        Assert.Equal("lukas@example.invalid", EmailText.Address("Lukas Bolte <Lukas@Example.invalid>"));
    }

    [Fact]
    public async Task TheGoogleClientReadsAThreadAsCleanedTextAndMarksSentAndBulkMail()
    {
        static string Data(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string json = """
            {"id":"t1","historyId":"42","messages":[
             {"id":"m1","labelIds":["SENT"],"internalDate":"1767268800000","payload":{"mimeType":"multipart/alternative","headers":[
               {"name":"From","value":"Demo <me@example.invalid>"},{"name":"Subject","value":"Revision plan"}],"parts":[
               {"mimeType":"text/plain","filename":"","body":{"data":"@1","size":10}},
               {"mimeType":"text/html","filename":"","body":{"data":"@2","size":5}}]}},
             {"id":"m2","labelIds":["INBOX"],"internalDate":"1767272400000","payload":{"mimeType":"text/html","headers":[
               {"name":"From","value":"Lukas <lukas@example.invalid>"}],"body":{"data":"@3","size":5}}},
             {"id":"m3","labelIds":["INBOX","CATEGORY_PROMOTIONS"],"internalDate":"1767276000000","payload":{"mimeType":"text/plain","headers":[
               {"name":"From","value":"News <news@example.invalid>"},{"name":"List-Unsubscribe","value":"<mailto:x@example.invalid>"}],"body":{"data":"@4","size":5}}}]}
            """.Replace("@1", Data("Hi Lukas,\nwe rerun the simulations.\n\nOn Mon, 3 Nov 2026 at 10:00, Lukas <l@example.invalid> wrote:\n> old text"))
            .Replace("@2", Data("<p>ignored</p>")).Replace("@3", Data("<div>Sounds <b>good</b> &amp; thanks</div><blockquote>quoted</blockquote>"))
            .Replace("@4", Data("Sale!"));
        var urls = new List<string>();
        var google = new GmailGoogle(new Clients(request =>
        {
            urls.Add(request.RequestUri!.AbsoluteUri);
            return request.RequestUri.AbsoluteUri.Contains("/threads/t1?")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var thread = await google.ThreadAsync("at", "t1", None);
        Assert.Equal(("t1", "42", "Revision plan"), (thread.Id, thread.HistoryId, thread.Subject));
        Assert.Equal(["m1", "m2", "m3"], thread.Messages.Select(message => message.Id));
        Assert.Equal(("Hi Lukas,\nwe rerun the simulations.", true, false), (thread.Messages[0].Text, thread.Messages[0].FromUser, thread.Messages[0].Bulk));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1767268800000), thread.Messages[0].SentAt);
        Assert.Equal(("Sounds good & thanks", false, false), (thread.Messages[1].Text, thread.Messages[1].FromUser, thread.Messages[1].Bulk));
        Assert.True(thread.Messages[2].Bulk);
        Assert.Contains("format=full", urls[0]);
        Assert.Equal("gmail_thread_missing", (await Assert.ThrowsAsync<ResearchFailure>(() => google.ThreadAsync("at", "gone", None))).Code);
        await Assert.ThrowsAsync<ResearchFailure>(() => google.ThreadAsync("at", "../profile", None));
    }

    [Fact]
    public void ThreadAnswersMustQuoteNewMessagesVerbatimAndPlaceOnlyOnOfferedProjects()
    {
        var now = DateTimeOffset.UtcNow;
        var card = new ThemeCandidate(new string('a', 32), "Social learning", "A paper on social learning.", ["learning"]);
        var request = new EmailStepRequest("t1", "Plan", Address,
            [new("m2", "Lukas <lukas@example.invalid>", now, false, "Line one.\nLine two.\nLine three.")],
            [new("m1", "Me <" + Address + ">", now.AddDays(-1), true, "Earlier text.")], false, [], null, [card]);
        EmailSteps.ValidateRequest(request);
        // A known thread continues with its bullets and project; it is not offered projects to move to.
        var known = request with { Known = true, Bullets = ["The user proposed a plan."], Project = card, Projects = [] };
        EmailSteps.ValidateRequest(known);
        Assert.Throws<ResearchFailure>(() => EmailSteps.ValidateRequest(known with { Projects = [card] }));
        Assert.Throws<ResearchFailure>(() => EmailSteps.ValidateRequest(request with { Bullets = ["Only for known threads."] }));

        var resolved = FoundryService.ResolveEmailCitation(request, new("m2", 2, 3));
        Assert.Equal(("Plan", "Line two.\nLine three.", "m2"), (resolved.Path, resolved.Quote, resolved.Email!.MessageId));
        Assert.Throws<ResearchFailure>(() => FoundryService.ResolveEmailCitation(request, new("m1", 1, 1)));
        Assert.Throws<ResearchFailure>(() => FoundryService.ResolveEmailCitation(request, new("m2", 2, 9)));

        EmailStepResponse Answer(string decision, string? id, ResearchCitation citation, string? suggested = null) =>
            new(decision, id, suggested, [new EmailBullet("Lukas agreed to the plan.", [citation])], true, "Plan agreed", "Why.", "chat");
        EmailSteps.ValidateResponse(request, Answer(EmailSteps.Project, card.Id, resolved));
        EmailSteps.ValidateResponse(request, Answer(EmailSteps.Unassigned, null, resolved, "Social learning revision"));
        EmailSteps.ValidateResponse(known, Answer(EmailSteps.Continue, null, resolved));
        EmailSteps.ValidateResponse(request, new(EmailSteps.Skip, null, null, [], false, null, "Social.", "chat"));
        Assert.Equal("citation_not_verbatim", Assert.Throws<ResearchFailure>(() =>
            EmailSteps.ValidateResponse(request, Answer(EmailSteps.Project, card.Id, resolved with { Quote = "Line two, paraphrased." }))).Code);
        // Earlier messages were recorded before; bullets rest on the new ones.
        Assert.Equal("citation_not_verbatim", Assert.Throws<ResearchFailure>(() => EmailSteps.ValidateResponse(request,
            Answer(EmailSteps.Project, card.Id, resolved with { Quote = "Earlier text.", Email = new("m1", "Me <" + Address + ">", now.AddDays(-1)) }))).Code);
        foreach (var invalid in new[] { Answer(EmailSteps.Project, new string('b', 32), resolved), Answer(EmailSteps.Continue, null, resolved),
                     Answer(EmailSteps.Unassigned, null, resolved), Answer(EmailSteps.Skip, null, resolved) })
            Assert.Equal("invalid_email_decision", Assert.Throws<ResearchFailure>(() => EmailSteps.ValidateResponse(request, invalid)).Code);
        Assert.Equal("invalid_email_decision", Assert.Throws<ResearchFailure>(() =>
            EmailSteps.ValidateResponse(known, Answer(EmailSteps.Project, card.Id, resolved))).Code);
        Assert.Equal("invalid_email_bullets", Assert.Throws<ResearchFailure>(() =>
            EmailSteps.ValidateResponse(known, Answer(EmailSteps.Continue, null, resolved) with { Headline = null })).Code);
    }

    [Fact]
    public void SyncedThreadsAndGmailAccountsMergeByLatestChange()
    {
        var at = DateTimeOffset.UtcNow;
        GmailThreadRecord Thread(string id, int minutes, string status) =>
            new(Account.Id, Address, id, "Subject " + id, status, "1", "m", at, at.AddMinutes(minutes), []);
        var merged = SyncBundles.MergeThreads([Thread("a", 0, GmailThreadRecord.Assigned), Thread("b", 0, GmailThreadRecord.Pending)],
            [Thread("b", 5, GmailThreadRecord.Dismissed), Thread("c", 0, GmailThreadRecord.Skipped)], Account.Id)!;
        Assert.Equal(["a", "b", "c"], merged.Select(thread => thread.Id).Order());
        Assert.Equal(GmailThreadRecord.Dismissed, merged.Single(thread => thread.Id == "b").Status);

        var local = Connected(at) with { LastImportAt = at, ImportVersion = GmailImporter.Version, LeaseBy = "here", LeaseUntil = at.AddMinutes(5) };
        var remote = Connected(at) with { LastImportAt = at.AddMinutes(-30), LeaseBy = "there", LeaseUntil = at.AddMinutes(9) };
        var account = SyncBundles.MergeGmail([local], [remote], Account.Id)!.Single();
        Assert.Equal((at, "there", (int?)GmailImporter.Version), (account.LastImportAt, account.LeaseBy, account.ImportVersion));
        // A newer removal elsewhere wins.
        var removed = Connected(at.AddMinutes(1)) with { RefreshToken = null, Removed = true, Status = "removed" };
        Assert.True(SyncBundles.MergeGmail([local], [removed], Account.Id)!.Single().Removed);
    }

    [Fact]
    public async Task NewThreadsArePlacedOnResearchCardsOrLeftForTheUserAndRepliesAddBulletsToTheSameThread()
    {
        var mail = Setup();
        await Run(mail.Importer);
        Assert.Equal(["Revision of the social learning paper", "Grant renewal budget", "Lunch on Friday?", "Results for the social learning paper"],
            mail.Remote.EmailRequests.Select(request => request.Subject));
        var first = mail.Remote.EmailRequests[0];
        // The newsletter is not the user's work; the thread older than the import window is not read for analysis.
        Assert.Equal(["m11", "m12"], first.Messages.Select(message => message.Id));
        Assert.Equal((false, Card), (first.Known, first.Projects.Single().Id));
        Assert.Equal("Social learning", first.Projects.Single().Title);

        // Both research threads join the card's one email stream; the grant waits for the user; lunch is skipped.
        var threads = mail.Store.State.GmailThreads!.ToDictionary(thread => thread.Id);
        var stream = Assert.Single(mail.Store.State.Projects, project => project.Source == ResearchSources.Gmail);
        Assert.Equal((GmailThreadRecord.Assigned, stream.ProjectId), (threads["t1"].Status, threads["t1"].StreamId));
        Assert.Equal((GmailThreadRecord.Assigned, stream.ProjectId), (threads["t3"].Status, threads["t3"].StreamId));
        Assert.Equal((GmailThreadRecord.Pending, "Grant renewal"), (threads["g1"].Status, threads["g1"].Suggested));
        Assert.Equal(GmailThreadRecord.Skipped, threads["t2"].Status);
        Assert.Equal(GmailThreadRecord.Skipped, threads["t0"].Status);
        Assert.Equal((Address, "Email · Social learning"), (stream.Parent, stream.Name));
        var card = mail.Store.State.Themes!.Single(theme => theme.Id == Card);
        Assert.Contains(stream.ProjectId, card.Members);
        Assert.Contains(stream.ProjectId, card.Pinned!);
        // One bullet per new message the stand-in cites; Updates happened when the mail was written.
        Assert.Equal(["Bullet: We will rerun the simulations with 200 agents.", "Bullet: I will draft the response letter."],
            threads["t1"].Bullets.Select(bullet => bullet.Text));
        Assert.Equal(mail.Clock.UtcNow.AddDays(-19), threads["t1"].NewsAt);
        Assert.Equal("Progress: Revision of the social learning paper", threads["t1"].Headline);

        // After the import the stream is summarized once from both threads, so the card can include it.
        var summary = Assert.Single(mail.Remote.StreamRequests);
        Assert.Equal(["Results for the social learning paper", "Revision of the social learning paper"], summary.Threads.Select(thread => thread.Subject));
        stream = mail.Store.State.Projects.Single(project => project.ProjectId == stream.ProjectId);
        Assert.Equal((true, "Email · Social learning"), (stream.ImportComplete, stream.Profile!.Title));

        // The feed: assigned threads with news on their card, and the thread waiting for a decision.
        var feed = GmailImporter.Threads(mail.Store.State, Account.Id);
        Assert.Equal(["t3", "g1", "t1"], feed.Select(thread => thread.Id));
        Assert.Equal((Card, "Social learning"), (feed[0].CardId, feed[0].CardTitle));
        Assert.Equal(GmailThreadRecord.Pending, feed[1].Status);

        // Nothing changed: Import now lists the threads but reads none and makes no model call.
        int reads = mail.Google.ThreadReads;
        mail.Importer.Request(Address);
        await Run(mail.Importer);
        Assert.Equal((4, 1, reads), (mail.Remote.EmailRequests.Count, mail.Remote.StreamRequests.Count, mail.Google.ThreadReads));

        // A reply: only the new message is analyzed, with the thread's bullets and project; it adds to the same thread.
        mail.Clock.UtcNow += TimeSpan.FromMinutes(75);
        var revision = mail.Google.Mailbox[3];
        mail.Google.Mailbox[3] = revision with { HistoryId = "2", Messages = [.. revision.Messages, Mine(mail.Clock.UtcNow, "m14", 1, "The new introduction is drafted.")] };
        await Run(mail.Importer);
        var reply = mail.Remote.EmailRequests[^1];
        Assert.Equal(5, mail.Remote.EmailRequests.Count);
        Assert.Equal(["m14"], reply.Messages.Select(message => message.Id));
        Assert.Equal(["m11", "m12"], reply.Context.Select(message => message.Id));
        Assert.Equal((true, Card, 0), (reply.Known, reply.Project?.Id, reply.Projects.Count));
        Assert.Equal(2, reply.Bullets.Count);
        var t1 = mail.Store.State.GmailThreads!.Single(thread => thread.Id == "t1");
        Assert.Equal(3, t1.Bullets.Count);
        Assert.Equal(("2", "m14"), (t1.HistoryId, t1.LastMessageId));
        Assert.Equal(2, mail.Remote.StreamRequests.Count);
    }

    [Fact]
    public async Task TheUserDecidesWhereAWaitingThreadGoesAndNoProjectIsCreatedWithoutThem()
    {
        var mail = Setup();
        await Run(mail.Importer);
        int cards = mail.Store.State.Themes!.Count;
        Assert.Equal(1, cards);

        // A new project: a card of its own with an email stream named by the user.
        await mail.Importer.DecideAsync(new(Address, "g1", "new", Name: "Grant renewal 2027"), None);
        var grant = mail.Store.State.GmailThreads!.Single(thread => thread.Id == "g1");
        var stream = mail.Store.State.Projects.Single(project => project.ProjectId == grant.StreamId);
        Assert.Equal((GmailThreadRecord.Assigned, "Grant renewal 2027", false), (grant.Status, stream.Name, stream.ImportComplete));
        var created = mail.Store.State.Themes!.Single(theme => theme.Members.Contains(stream.ProjectId));
        Assert.NotEqual(Card, created.Id);
        await Run(mail.Importer);
        Assert.True(mail.Store.State.Projects.Single(project => project.ProjectId == stream.ProjectId).ImportComplete);

        // Moving a thread to another card; dismissing the last thread of a stream removes the stream from its card.
        await mail.Importer.DecideAsync(new(Address, "t3", "project", CardId: created.Id), None);
        Assert.Equal(stream.ProjectId, mail.Store.State.GmailThreads!.Single(thread => thread.Id == "t3").StreamId);
        await mail.Importer.DecideAsync(new(Address, "t1", "dismiss"), None);
        Assert.DoesNotContain(mail.Store.State.Projects, project => project.Name == "Email · Social learning");
        Assert.DoesNotContain(mail.Store.State.Themes!.SelectMany(theme => theme.Members), member => member.StartsWith("gmail-") && member != stream.ProjectId);
        Assert.Equal("gmail_thread_not_found", (await Assert.ThrowsAsync<ResearchFailure>(() =>
            mail.Importer.DecideAsync(new(Address, "t2", "project", CardId: Card), None))).Code);
        Assert.Equal("research_card_not_found", (await Assert.ThrowsAsync<ResearchFailure>(() =>
            mail.Importer.DecideAsync(new(Address, "t3", "project", CardId: new string('f', 32)), None))).Code);

        // A dismissed thread is not read again, even when new mail arrives.
        int calls = mail.Remote.EmailRequests.Count, reads = mail.Google.ThreadReads;
        mail.Google.Mailbox[3] = mail.Google.Mailbox[3] with { HistoryId = "2", Messages = [.. mail.Google.Mailbox[3].Messages, Mine(mail.Clock.UtcNow, "m14", 0, "More.")] };
        mail.Importer.Request(Address);
        await Run(mail.Importer);
        Assert.Equal((calls, reads), (mail.Remote.EmailRequests.Count, mail.Google.ThreadReads));
        Assert.Equal("2", mail.Store.State.GmailThreads!.Single(thread => thread.Id == "t1").HistoryId);
    }

    [Fact]
    public async Task AnEarlierWorkstreamAnalysisIsReplacedOnceAndTheMailImportedAgain()
    {
        string old = "gmail-" + new string('d', 32);
        var mail = Setup(state => state with
        {
            GmailVersion = 0,
            Projects = [.. state.Projects, Included(old) with { Source = ResearchSources.Gmail, Parent = Address, Profile = new("Old", "Old.", [], []) }],
            Themes = [.. state.Themes!, new ResearchTheme(Account.Id, new string('e', 32), [old], null, null, DateTimeOffset.UtcNow)],
            GmailAccounts = [Connected(DateTimeOffset.UtcNow.AddHours(-2)) with { LastImportAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                Processed = [new("t1", "1", "m12", DateTimeOffset.UtcNow, old, DateTimeOffset.UtcNow)] }]
        });
        await Run(mail.Importer);
        Assert.Equal(GmailImporter.Version, mail.Store.State.GmailVersion);
        Assert.DoesNotContain(mail.Store.State.Projects, project => project.ProjectId == old);
        Assert.Contains(mail.Store.State.Decisions!, decision => decision.ProjectId == old && !decision.Included);
        Assert.Null(mail.Store.State.GmailAccounts!.Single().Processed);
        // Imported again although the last import was minutes ago.
        Assert.Equal(4, mail.Remote.EmailRequests.Count);
        Assert.Equal(GmailImporter.Version, mail.Store.State.GmailAccounts!.Single().ImportVersion);
    }

    [Fact]
    public async Task AThreadThatCannotBeAnalyzedIsRetriedThenPassedOverAndOutagesPauseTheImport()
    {
        var mail = Setup();
        var now = mail.Clock.UtcNow;
        mail.Google.Mailbox = [
            new("t2", "1", "Revision of the paper", [Mine(now, "m21", 1, "Done with the revision.")]),
            new("t1", "1", "Broken thread", [Mine(now, "m11", 2, "Something the model cannot handle.")])];
        mail.Remote.EmailStep = request => request.Subject == "Broken thread"
            ? throw new ResearchFailure(502, "invalid_email_step", "Foundry returned an unsupported email analysis.") : Answer(request);
        for (int attempt = 1; attempt < GmailImporter.FailuresBeforeSkip; attempt++)
        {
            await Run(mail.Importer);
            Assert.Equal(attempt, mail.Remote.EmailRequests.Count);
            Assert.Contains("unsupported", mail.Importer.LastError(Address));
            Assert.Equal(new GmailImportProgress(0, 2), mail.Importer.Progress(Address));
            Assert.False(mail.Importer.HasWork(mail.Store.State, Account.Id, mail.Clock.UtcNow));
            mail.Clock.UtcNow += GmailImporter.RetryAfter;
        }
        await Run(mail.Importer);
        Assert.Equal(GmailImporter.FailuresBeforeSkip + 1, mail.Remote.EmailRequests.Count);
        Assert.Equal(GmailThreadRecord.Skipped, mail.Store.State.GmailThreads!.Single(thread => thread.Id == "t1").Status);
        Assert.NotNull(mail.Store.State.GmailAccounts!.Single().LastImportAt);

        // Meld's server is down: nothing is committed and the import continues once it answers.
        mail = Setup();
        mail.Remote.EmailStep = _ => throw new ResearchFailure(503, "remote_unavailable", "Meld's server is not answering.");
        await Run(mail.Importer);
        // Only the thread outside the import window, which needs no model call, is done.
        Assert.Equal(["t0"], mail.Store.State.GmailThreads!.Select(thread => thread.Id));
        Assert.Contains("not answering", mail.Importer.LastError(Address));
        mail.Remote.EmailStep = Answer;
        mail.Clock.UtcNow += GmailImporter.RetryAfter;
        await Run(mail.Importer);
        Assert.Equal(5, mail.Store.State.GmailThreads!.Count);
        Assert.Null(mail.Importer.LastError(Address));
    }

    [Fact]
    public async Task WithEncryptedSyncAnotherComputerReusesTheImportAndWaitsWhileOneIsImporting()
    {
        var box = new BlobBox();
        var now = DateTimeOffset.UtcNow;
        (ResearchCoordinator Coordinator, MemoryStore Store, Remote Remote, GmailTests.FakeGoogle Google, GmailImporter Importer) Computer(LocalResearchState state)
        {
            var store = new MemoryStore(state);
            var remote = new Remote { Box = box, EmailStep = Answer };
            var google = new GmailTests.FakeGoogle { Mailbox = Mailbox(now) };
            var connector = new GmailConnector(store, remote, google, TimeProvider.System, NullLogger<GmailConnector>.Instance);
            var importer = new GmailImporter(store, remote, connector, google, TimeProvider.System, NullLogger<GmailImporter>.Instance);
            return (new ResearchCoordinator(store, remote, new Source(Files(("main.tex", "Main text."))), new(new Uri("https://meld.example.invalid"), false, 300),
                NullLogger<ResearchCoordinator>.Instance, TimeProvider.System, null, connector, importer), store, remote, google, importer);
        }
        var a = Computer(Initial(now) with { GmailAccounts = [Connected(now.AddHours(-1))] });
        using var first = a.Coordinator;
        await first.WithSyncAsync((sync, credential) => sync.CreateAsync(credential, "correct horse battery", None), None);
        await Drain(first);
        Assert.Equal(4, a.Remote.EmailRequests.Count);
        Assert.Null(a.Store.State.GmailAccounts!.Single().LeaseBy);
        Assert.All(box.Contents.Values, content => Assert.DoesNotContain("simulations", Encoding.UTF8.GetString(content)));

        // A second computer pulls the recorded threads and the email stream: the import is not repeated.
        var b = Computer(new(1, Credential, null, null, [], [], GmailVersion: GmailImporter.Version));
        using var second = b.Coordinator;
        await second.WithSyncAsync(async (sync, credential) => { await sync.UnlockAsync(credential, new("correct horse battery", null), None); return true; }, None);
        await Drain(second);
        Assert.Equal((0, 0), (b.Remote.EmailRequests.Count, b.Google.ThreadReads));
        Assert.Equal(5, b.Store.State.GmailThreads!.Count);
        var stream = Assert.Single(b.Store.State.Projects, project => project.Source == ResearchSources.Gmail);
        Assert.Contains(b.Store.State.Themes!, theme => theme.Members.Contains(stream.ProjectId));
        Assert.Equal(2, b.Store.State.GmailThreads!.Single(thread => thread.Id == "t1").Bullets.Count);

        // While the second computer holds the lease (Gmail stopped answering mid-import), the first one waits.
        b.Google.Unavailable = true;
        b.Importer.Request(Address);
        await Drain(second);
        Assert.NotNull(b.Store.State.GmailAccounts!.Single().LeaseBy);
        a.Importer.Request(Address);
        await Drain(first, force: true);
        Assert.Equal(b.Store.State.GmailAccounts!.Single().LeaseBy, a.Store.State.GmailAccounts!.Single().LeaseBy);
        Assert.True(a.Importer.Progress(Address)!.Waiting);
        Assert.Equal(4, a.Remote.EmailRequests.Count);
    }

    private const string Card = "0123456789abcdef0123456789abcdef";

    private sealed record Mail(GmailImporter Importer, MemoryStore Store, Remote Remote, GmailTests.FakeGoogle Google, TestClock Clock);

    /// <summary>An included Overleaf project on a research card about the social learning paper.</summary>
    private static LocalResearchState Initial(DateTimeOffset now) =>
        new(1, Credential, null, null, [Included() with { Profile = new("Social learning", "A paper on social learning.", [], ["learning"]), ImportComplete = true }], [],
            Themes: [new ResearchTheme(Account.Id, Card, [ProjectId], new("Social learning", "A paper on how people learn from each other's choices.", [], ["learning"]),
                null, now)],
            GmailVersion: GmailImporter.Version);

    private static Mail Setup(Func<LocalResearchState, LocalResearchState>? change = null)
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var state = Initial(clock.UtcNow) with { GmailAccounts = [Connected(clock.UtcNow.AddHours(-1))] };
        var store = new MemoryStore(change is null ? state : change(state));
        var remote = new Remote { EmailStep = Answer };
        var google = new GmailTests.FakeGoogle { Mailbox = Mailbox(clock.UtcNow) };
        var connector = new GmailConnector(store, remote, google, clock, NullLogger<GmailConnector>.Instance);
        return new(new GmailImporter(store, remote, connector, google, clock, NullLogger<GmailImporter>.Instance), store, remote, google, clock);
    }

    private static async Task Run(GmailImporter importer)
    {
        for (int step = 0; step < 50 && await importer.StepAsync(Credential, null, None); step++) { }
    }

    private static GmailAccountState Connected(DateTimeOffset at) =>
        new(Account.Id, Address, "refresh-" + Address, GmailConnector.ConsentVersion, at);

    private static GmailMessage Mine(DateTimeOffset now, string id, int daysAgo, string text) =>
        new(id, $"Demo <{Address}>", now.AddDays(-daysAgo), true, false, text);
    private static GmailMessage Theirs(DateTimeOffset now, string id, int daysAgo, string text, bool bulk = false) =>
        new(id, "Lukas Bolte <lukas@example.invalid>", now.AddDays(-daysAgo), false, bulk, text);

    /// <summary>Threads the user wrote in, newest first: research, a lunch, a grant, research, and one older than the import window.</summary>
    private static List<GmailThread> Mailbox(DateTimeOffset now) => [
        new("t3", "1", "Results for the social learning paper", [Mine(now, "m31", 2, "The simulations confirm the main result.\nTable 4 is in the draft.")]),
        new("t2", "1", "Lunch on Friday?", [Mine(now, "m21", 6, "Lunch at 12:30?"), Theirs(now, "m22", 6, "Sure!")]),
        new("g1", "1", "Grant renewal budget", [Mine(now, "g11", 12, "I finalized the renewal budget.")]),
        new("t1", "1", "Revision of the social learning paper", [Mine(now, "m11", 20, "We will rerun the simulations with 200 agents."),
            Theirs(now, "m12", 19, "I will draft the response letter."), Theirs(now, "m13", 18, "Weekly digest: 12 new papers", bulk: true)]),
        new("t0", "1", "Old conference", [Mine(now, "m01", 40, "See you at the conference.")])];

    /// <summary>
    /// A stand-in for Foundry: lunch is skipped, the grant is left unassigned, other threads go to the first offered project;
    /// each new message becomes one cited, newsworthy bullet.
    /// </summary>
    private static EmailStepResponse Answer(EmailStepRequest request)
    {
        if (request.Subject.StartsWith("Lunch", StringComparison.Ordinal)) return new(EmailSteps.Skip, null, null, [], false, null, "Not work.", "chat");
        var bullets = request.Messages.Select(message => new EmailBullet("Bullet: " + message.Text.Split('\n')[0],
            [new ResearchCitation(request.Subject, message.Text.Split('\n')[0], EvidenceVersion.Current, new EmailEvidence(message.Id, message.From, message.SentAt))])).ToArray();
        string decision = request.Known ? EmailSteps.Continue : request.Subject.StartsWith("Grant", StringComparison.Ordinal) || request.Projects.Count == 0
            ? EmailSteps.Unassigned : EmailSteps.Project;
        return new(decision, decision == EmailSteps.Project ? request.Projects[0].Id : null,
            decision == EmailSteps.Unassigned ? "Grant renewal" : null, bullets, true, "Progress: " + request.Subject, "Explained.", "chat");
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class Clients(Func<HttpRequestMessage, HttpResponseMessage> respond) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(respond), disposeHandler: true);
        private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
        }
    }
}
