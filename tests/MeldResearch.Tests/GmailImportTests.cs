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
    public void EmailAnswersMustQuoteNewMessagesVerbatimAndChooseAValidWorkstream()
    {
        var now = DateTimeOffset.UtcNow;
        string candidate = "gmail-" + new string('a', 32);
        var request = new EmailStepRequest("t1", "Plan", Address,
            [new("m2", "Lukas <lukas@example.invalid>", now, false, "Line one.\nLine two.\nLine three.")],
            [new("m1", "Me <" + Address + ">", now.AddDays(-1), true, "Earlier text.")], null,
            [new(candidate, new("Paper", "A paper.", [], []))]);
        EmailSteps.ValidateRequest(request);

        var resolved = FoundryService.ResolveEmailCitation(request, new("m2", 2, 3));
        Assert.Equal(("Plan", "Line two.\nLine three.", "m2"), (resolved.Path, resolved.Quote, resolved.Email!.MessageId));
        Assert.Throws<ResearchFailure>(() => FoundryService.ResolveEmailCitation(request, new("m1", 1, 1)));
        Assert.Throws<ResearchFailure>(() => FoundryService.ResolveEmailCitation(request, new("m2", 2, 9)));

        var profile = new ProjectProfile("Paper", "A paper.", [], []);
        EmailStepResponse Answer(string decision, string? id, ResearchCitation citation) =>
            new(decision, id, profile, [new ProfileUpdate("Progress", "Summary.", [citation])], "Why.", "chat");
        EmailSteps.ValidateResponse(request, Answer(EmailSteps.Existing, candidate, resolved));
        EmailSteps.ValidateResponse(request, Answer(EmailSteps.New, null, resolved));
        EmailSteps.ValidateResponse(request, new(EmailSteps.Skip, null, null, [], "Social.", "chat"));
        Assert.Equal("citation_not_verbatim", Assert.Throws<ResearchFailure>(() =>
            EmailSteps.ValidateResponse(request, Answer(EmailSteps.New, null, resolved with { Quote = "Line two, paraphrased." }))).Code);
        // Earlier messages were reported before; Updates rest on the new ones.
        Assert.Equal("citation_not_verbatim", Assert.Throws<ResearchFailure>(() => EmailSteps.ValidateResponse(request,
            Answer(EmailSteps.New, null, resolved with { Quote = "Earlier text.", Email = new("m1", "Me <" + Address + ">", now.AddDays(-1)) }))).Code);
        Assert.Equal("invalid_email_decision", Assert.Throws<ResearchFailure>(() =>
            EmailSteps.ValidateResponse(request, Answer(EmailSteps.Existing, "gmail-" + new string('b', 32), resolved))).Code);
        Assert.Equal("invalid_email_decision", Assert.Throws<ResearchFailure>(() =>
            EmailSteps.ValidateResponse(request, Answer(EmailSteps.Skip, null, resolved))).Code);
        Assert.Throws<ResearchFailure>(() => EmailSteps.ValidateRequest(request with { PreviousWorkstreamId = "gmail-" + new string('c', 32) }));
    }

    [Fact]
    public void SyncedGmailAccountsMergeAnalyzedThreadsImportTimesAndTheLease()
    {
        var at = DateTimeOffset.UtcNow;
        GmailThreadState Thread(string id, int minutes) => new(id, "1", "m", at, null, at.AddMinutes(minutes));
        var local = Connected(at) with { Processed = [Thread("a", 0), Thread("b", 0)], LastImportAt = at, LeaseBy = "here", LeaseUntil = at.AddMinutes(5) };
        var remote = Connected(at) with { Processed = [Thread("b", 5), Thread("c", 0)], LastImportAt = at.AddMinutes(-30), LeaseBy = "there", LeaseUntil = at.AddMinutes(9) };
        var merged = SyncBundles.MergeGmail([local], [remote], Account.Id)!.Single();
        Assert.Equal(["a", "b", "c"], merged.Processed!.Select(thread => thread.Id).Order());
        Assert.Equal(at.AddMinutes(5), merged.Processed!.Single(thread => thread.Id == "b").ProcessedAt);
        Assert.Equal((at, "there"), (merged.LastImportAt, merged.LeaseBy));
        // A newer removal elsewhere wins over everything analyzed here.
        var removed = Connected(at.AddMinutes(1)) with { RefreshToken = null, Removed = true, Status = "removed" };
        Assert.True(SyncBundles.MergeGmail([local], [removed], Account.Id)!.Single().Removed);
        Assert.Null(SyncBundles.MergeGmail([local], [removed], Account.Id)!.Single().Processed);
    }

    [Fact]
    public async Task TheFirstImportAnalyzesOnlyNewResearchMailAndAnUnchangedMailboxMakesNoModelCall()
    {
        var mail = Setup();
        await Run(mail.Importer);
        Assert.Equal(["Revision of the social learning paper", "Lunch on Friday?", "Results for the social learning paper"],
            mail.Remote.EmailRequests.Select(request => request.Subject));
        var first = mail.Remote.EmailRequests[0];
        // The newsletter is not the user's work; the thread older than the import window is not read for analysis.
        Assert.Equal(["m11", "m12"], first.Messages.Select(message => message.Id));
        Assert.Equal((true, false), (first.Messages[0].FromUser, first.Messages[1].FromUser));
        Assert.Empty(first.Context);
        Assert.Empty(first.Candidates);
        var workstream = Assert.Single(mail.Store.State.Projects);
        Assert.Equal((ResearchSources.Gmail, Address, "Social learning paper", true), (workstream.Source, workstream.Parent, workstream.Name, workstream.ImportComplete));
        Assert.True(ResearchSources.IsGmailWorkstreamId(workstream.ProjectId));
        Assert.Equal(workstream.ProjectId, Assert.Single(mail.Remote.EmailRequests[2].Candidates).Id);
        // Updates happened when the mail was written; quotes are the email text.
        Assert.Equal([mail.Clock.UtcNow.AddDays(-19), mail.Clock.UtcNow.AddDays(-2)], workstream.Updates.Select(update => update.ObservedAt));
        Assert.All(workstream.Updates, update => Assert.Equal(ResearchUpdateKind.InitialImport, update.Kind));
        Assert.Equal("The simulations confirm the main result.", workstream.Updates[1].Evidence.Single().Quote);
        var account = mail.Store.State.GmailAccounts!.Single();
        Assert.Equal(4, account.Processed!.Count);
        Assert.Null(account.Processed.Single(thread => thread.Id == "t2").WorkstreamId);
        Assert.Equal(("m12", workstream.ProjectId), (account.Processed.Single(thread => thread.Id == "t1").LastMessageId,
            account.Processed.Single(thread => thread.Id == "t1").WorkstreamId));
        Assert.Equal((mail.Clock.UtcNow, (string?)null), (account.LastImportAt, account.LeaseBy));
        Assert.Null(mail.Importer.Progress(Address));

        // Nothing changed: Import now lists the threads but reads none and makes no model call.
        int reads = mail.Google.ThreadReads;
        mail.Importer.Request(Address);
        await Run(mail.Importer);
        Assert.Equal((3, reads), (mail.Remote.EmailRequests.Count, mail.Google.ThreadReads));
        Assert.False(mail.Importer.HasWork(mail.Store.State, Account.Id, mail.Clock.UtcNow.AddMinutes(49)));
        Assert.True(mail.Importer.HasWork(mail.Store.State, Account.Id, mail.Clock.UtcNow.AddMinutes(71)));

        // A reply: only the new message is analyzed, earlier ones are background, and it continues the same workstream.
        mail.Clock.UtcNow += TimeSpan.FromMinutes(75);
        var revision = mail.Google.Mailbox[2];
        mail.Google.Mailbox[2] = revision with { HistoryId = "2", Messages = [.. revision.Messages, Mine(mail.Clock.UtcNow, "m14", 1, "The new introduction is drafted.")] };
        await Run(mail.Importer);
        var reply = mail.Remote.EmailRequests[^1];
        Assert.Equal(4, mail.Remote.EmailRequests.Count);
        Assert.Equal(["m14"], reply.Messages.Select(message => message.Id));
        Assert.Equal(["m11", "m12"], reply.Context.Select(message => message.Id));
        Assert.Equal(workstream.ProjectId, reply.PreviousWorkstreamId);
        workstream = Assert.Single(mail.Store.State.Projects);
        Assert.Equal(3, workstream.Updates.Count);
        Assert.Equal(ResearchUpdateKind.Change, workstream.Updates[^1].Kind);
        Assert.Equal(("2", "m14"), (mail.Store.State.GmailAccounts!.Single().Processed!.Single(thread => thread.Id == "t1").HistoryId,
            mail.Store.State.GmailAccounts!.Single().Processed!.Single(thread => thread.Id == "t1").LastMessageId));
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
        var account = mail.Store.State.GmailAccounts!.Single();
        Assert.Null(account.Processed!.Single(thread => thread.Id == "t1").WorkstreamId);
        Assert.NotNull(account.LastImportAt);
        Assert.Single(mail.Store.State.Projects);

        // Meld's server is down: nothing is committed and the import continues once it answers.
        mail = Setup();
        mail.Remote.EmailStep = _ => throw new ResearchFailure(503, "remote_unavailable", "Meld's server is not answering.");
        await Run(mail.Importer);
        Assert.Empty(mail.Store.State.Projects);
        // Only the thread outside the import window, which needs no model call, is done.
        Assert.Equal(["t0"], mail.Store.State.GmailAccounts!.Single().Processed!.Select(thread => thread.Id));
        Assert.Contains("not answering", mail.Importer.LastError(Address));
        mail.Remote.EmailStep = Answer;
        mail.Clock.UtcNow += GmailImporter.RetryAfter;
        await Run(mail.Importer);
        Assert.Equal(4, mail.Store.State.GmailAccounts!.Single().Processed!.Count);
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
        var a = Computer(new(1, Credential, null, null, [], [], GmailAccounts: [Connected(now.AddHours(-1))]));
        using var first = a.Coordinator;
        await first.WithSyncAsync((sync, credential) => sync.CreateAsync(credential, "correct horse battery", None), None);
        await Drain(first);
        Assert.Equal(3, a.Remote.EmailRequests.Count);
        Assert.Null(a.Store.State.GmailAccounts!.Single().LeaseBy);
        Assert.All(box.Contents.Values, content => Assert.DoesNotContain("simulations", Encoding.UTF8.GetString(content)));

        // A second computer pulls the analyzed threads and the workstream: the import is not repeated.
        var b = Computer(new(1, Credential, null, null, [], []));
        using var second = b.Coordinator;
        await second.WithSyncAsync(async (sync, credential) => { await sync.UnlockAsync(credential, new("correct horse battery", null), None); return true; }, None);
        await Drain(second);
        Assert.Equal((0, 0), (b.Remote.EmailRequests.Count, b.Google.ThreadReads));
        Assert.Equal(4, b.Store.State.GmailAccounts!.Single().Processed!.Count);
        var workstream = Assert.Single(b.Store.State.Projects, project => project.Source == ResearchSources.Gmail);
        Assert.Equal(2, workstream.Updates.Count);

        // While the second computer holds the lease (Gmail stopped answering mid-import), the first one waits.
        b.Google.Unavailable = true;
        b.Importer.Request(Address);
        await Drain(second);
        Assert.NotNull(b.Store.State.GmailAccounts!.Single().LeaseBy);
        a.Importer.Request(Address);
        await Drain(first, force: true);
        Assert.Equal(b.Store.State.GmailAccounts!.Single().LeaseBy, a.Store.State.GmailAccounts!.Single().LeaseBy);
        Assert.True(a.Importer.Progress(Address)!.Waiting);
        Assert.Equal(3, a.Remote.EmailRequests.Count);
    }

    private sealed record Mail(GmailImporter Importer, MemoryStore Store, Remote Remote, GmailTests.FakeGoogle Google, TestClock Clock);

    private static Mail Setup()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var store = new MemoryStore(new(1, Credential, null, null, [], [], GmailAccounts: [Connected(clock.UtcNow.AddHours(-1))]));
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

    /// <summary>Threads the user wrote in, newest first: research, a lunch, research, and one older than the import window.</summary>
    private static List<GmailThread> Mailbox(DateTimeOffset now) => [
        new("t3", "1", "Results for the social learning paper", [Mine(now, "m31", 2, "The simulations confirm the main result.\nTable 4 is in the draft.")]),
        new("t2", "1", "Lunch on Friday?", [Mine(now, "m21", 6, "Lunch at 12:30?"), Theirs(now, "m22", 6, "Sure!")]),
        new("t1", "1", "Revision of the social learning paper", [Mine(now, "m11", 20, "We will rerun the simulations with 200 agents."),
            Theirs(now, "m12", 19, "I will draft the response letter."), Theirs(now, "m13", 18, "Weekly digest: 12 new papers", bulk: true)]),
        new("t0", "1", "Old conference", [Mine(now, "m01", 40, "See you at the conference.")])];

    /// <summary>A stand-in for Foundry: lunch is skipped; research continues the previous or best candidate, else starts one.</summary>
    private static EmailStepResponse Answer(EmailStepRequest request)
    {
        if (request.Subject.StartsWith("Lunch", StringComparison.Ordinal)) return new(EmailSteps.Skip, null, null, [], "Not research.", "chat");
        var message = request.Messages[^1];
        var citation = new ResearchCitation(request.Subject, message.Text.Split('\n')[0], EvidenceVersion.Current,
            new EmailEvidence(message.Id, message.From, message.SentAt));
        var profile = new ProjectProfile("Social learning paper", "Revising the social learning paper.", ["Simulations"], ["social learning"]);
        string? target = request.PreviousWorkstreamId ?? request.Candidates.FirstOrDefault()?.Id;
        return new(target is null ? EmailSteps.New : EmailSteps.Existing, target, profile,
            [new ProfileUpdate("Progress: " + request.Subject, "Summary.", [citation])], "Explained.", "chat");
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
