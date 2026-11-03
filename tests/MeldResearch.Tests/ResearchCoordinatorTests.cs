using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MeldResearch.LocalBackend.Contracts;
using MeldResearch.LocalBackend.Middleware;
using MeldResearch.LocalBackend.Services.Research;
using MeldResearch.Research;

namespace MeldResearch.Tests;

public sealed class ResearchCoordinatorTests
{
    internal static readonly MeldAccountResponse Account = new(Guid.NewGuid(), "Synthetic", "synthetic@example.invalid");
    internal static readonly LinkedCredential Credential = new(Account, new string('a', 64), DateTimeOffset.UtcNow.AddDays(1));
    internal const string ProjectId = "0123456789abcdef01234567";

    internal static ResearchSnapshot Files(params (string Path, string Text)[] files)
    {
        var items = files.Select(file => new ResearchFile(file.Path, ResearchEvidence.Hash(file.Text), Encoding.UTF8.GetByteCount(file.Text),
            ResearchEvidence.TextExtensions.Contains(Path.GetExtension(file.Path)) ? file.Text : null)).ToArray();
        return new(ProjectId, "Synthetic research", ResearchEvidence.Revision(items), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(-1), items);
    }
    /// <summary>A compilable main document (a root) with the given text, including a.tex when the project has one.</summary>
    internal static string MainDoc(string text) => "\\documentclass{article}\n\\begin{document}\n" + text + "\n\\input{a}\n\\end{document}";
    internal static MonitoredProject Included(string id = ProjectId) =>
        new(Account.Id, id, "Synthetic", true, ResearchEvidence.ConsentVersion, null, null, [], [], null, null, null, null, null, 0, 0);
    private static ResearchCoordinator Coordinator(IResearchStore store, IResearchRemote remote, IOverleafEvidenceSource source) =>
        new(store, remote, source, new(new Uri("https://meld.example.invalid"), false, 300),
            NullLogger<ResearchCoordinator>.Instance, TimeProvider.System);
    internal static async Task Drain(ResearchCoordinator coordinator, bool force = false)
    {
        for (int cycle = 0; cycle < 50 && await coordinator.RunCycleAsync(force && cycle == 0, CancellationToken.None); cycle++) { }
    }

    [Fact]
    public async Task ACheckWithoutTextChangesKeepsTheFilesEditTimes()
    {
        var edited = new Dictionary<string, DateTimeOffset>
        {
            ["a.tex"] = DateTimeOffset.UtcNow.AddDays(-1), ["b.tex"] = DateTimeOffset.UtcNow.AddDays(-3)
        };
        var source = new Source(Files(("main.tex", "Main text."), ("a.tex", "Section A."), ("b.tex", "Section B."))) { EditTimes = edited };
        var store = new MemoryStore(new(1, Credential, null, null, [Included()], []));
        using var coordinator = Coordinator(store, new Remote(), source);
        await Drain(coordinator);
        Assert.Equal(edited, store.State.Projects[0].Baseline!.EditTimes);

        // A forced check whose only change is a bibliography: no processing, and the times of files still present remain.
        source.Snapshot = Files(("main.tex", "Main text."), ("a.tex", "Section A."), ("b.tex", "Section B."), ("refs.bib", "@book{x}"));
        source.EditTimes = null;
        await Drain(coordinator, force: true);
        Assert.Contains("No model calls", store.State.Projects[0].Explanation);
        Assert.Equal(edited, store.State.Projects[0].Baseline!.EditTimes);

        // A removed file loses its time (deleting it is processed, which reads the history again).
        source.Snapshot = Files(("main.tex", "Main text."), ("a.tex", "Section A."), ("refs.bib", "@book{x}"));
        source.EditTimes = new Dictionary<string, DateTimeOffset> { ["a.tex"] = edited["a.tex"] };
        await Drain(coordinator, force: true);
        Assert.Equal(["a.tex"], store.State.Projects[0].Baseline!.EditTimes!.Keys);
    }

    [Fact]
    public async Task OnlyExplicitlyConsentedProjectsAreRead()
    {
        var source = new Source(Files(("main.tex", "Synthetic evidence.")));
        var store = new MemoryStore(new(1, Credential, null, null, [Included() with { ConsentVersion = "old-policy" }], []));
        using var coordinator = Coordinator(store, new Remote(), source);
        await coordinator.RunCycleAsync(true, CancellationToken.None);
        Assert.Equal(0, source.Reads);
        store.State = store.State with { Credential = null };
        await coordinator.RunCycleAsync(true, CancellationToken.None);
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task BrowserOutageIsReportedWithoutBackoffAndRetriedWhenTheBrowserReturns()
    {
        var source = new Source(Files(("main.tex", "Synthetic source."))) { Available = false };
        var store = new MemoryStore(new(1, Credential, null, null, [Included()], []));
        using var coordinator = Coordinator(store, new Remote(), source);
        for (int index = 0; index < 5; index++) await coordinator.RunCycleAsync(false, CancellationToken.None);
        Assert.Equal(("browser_unavailable", 0), (store.State.Projects[0].ErrorCode, store.State.Projects[0].FailureCount));
        source.Available = true;
        await Drain(coordinator);
        Assert.Equal(1, source.Reads);
        Assert.True(store.State.Projects[0].ImportComplete);
    }

    [Fact]
    public async Task LargeProjectsImportWhileBibliographiesOversizedAndNonUtf8FilesAreTrackedByHashOnly()
    {
        string zip = Path.Combine(Path.GetTempPath(), "meld-bib-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
            {
                var entries = new List<(string, byte[])>
                {
                    ("main.tex", Encoding.UTF8.GetBytes("Synthetic research.")),
                    ("refs.bib", Encoding.UTF8.GetBytes("@article{a, title={" + new string('x', 200_000) + "}}")),
                    ("generated.tex", Encoding.UTF8.GetBytes(new string('t', ResearchEvidence.MaxFileTextBytes + 1))),
                    ("latin1.tex", [0x63, 0x61, 0x66, 0xE9])
                };
                // Well over the earlier 256 KiB whole-project limit: each request now carries only one file.
                for (int index = 0; index < 8; index++) entries.Add(($"versions/v{index}.tex", Encoding.UTF8.GetBytes(new string((char)('a' + index), 100_000))));
                foreach (var (name, bytes) in entries)
                {
                    using var stream = archive.CreateEntry(name).Open();
                    stream.Write(bytes);
                }
            }
            var snapshot = await OverleafEvidenceSource.ReadSnapshotAsync(zip, new(ProjectId, "Synthetic", null), DateTimeOffset.UtcNow, CancellationToken.None);
            foreach (string path in new[] { "refs.bib", "generated.tex", "latin1.tex" })
                Assert.Null(Assert.Single(snapshot.Files, file => file.Path == path).Text);
            Assert.Equal(9, snapshot.Files.Count(file => file.Text is not null));
        }
        finally { File.Delete(zip); }
    }

    [Fact]
    public async Task EditTimesComeOnlyAsPathsAndTimesFromTheConnector()
    {
        using var json = new MemoryStream(Encoding.UTF8.GetBytes("""{"paths":{"main.tex":1767225600000,"../evil.tex":1,"sections/a.tex":"x"},"complete":true}"""));
        var times = await OverleafEvidenceSource.ParseEditTimesAsync(json, CancellationToken.None);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1767225600000), Assert.Single(times!).Value);
    }

    [Fact]
    public async Task StartupClearsAccountMessagesLeftByEarlierVersions()
    {
        var legacy = new PendingDeviceLink(new string('d', 64), new string('e', 64), "https://meld.example.invalid/connect?code=ABC",
            "ABCDEF012345", DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(-5));
        var store = new MemoryStore(new(1, Credential, legacy, "The account link expired. Start a new link.", [], []));
        using var coordinator = Coordinator(store, new Remote(), new Source(Files(("main.tex", "Synthetic."))));
        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            for (int attempt = 0; attempt < 100 && store.State.AccountError is not null; attempt++) await Task.Delay(50);
            Assert.Null(store.State.AccountError);
            Assert.Null(store.State.PendingLink);
            Assert.Equal(Credential, store.State.Credential);
        }
        finally { await coordinator.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task SignInCompletesOnlyForTheStateThisAppStartedAndProvesTheVerifier()
    {
        var store = new MemoryStore(new(1, null, null, null, [], []));
        var remote = new Remote();
        using var coordinator = Coordinator(store, remote, new Source(Files(("main.tex", "Synthetic."))));
        var link = await coordinator.BeginLinkAsync("http://127.0.0.1:6001/account/callback", CancellationToken.None);
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(link.SignInUrl).Query);
        Assert.Equal("https://meld.example.invalid/connect", new Uri(link.SignInUrl).GetLeftPart(UriPartial.Path));
        Assert.Equal("http://127.0.0.1:6001/account/callback", query["redirect_uri"]);
        Assert.Equal(link.SignInUrl, (await coordinator.StatusAsync(CancellationToken.None)).PendingLink?.SignInUrl);
        string code = new('c', 64);

        // A crafted callback (wrong or missing state) cannot sign this app in, and does not consume the real sign-in.
        await coordinator.CompleteLinkAsync(code, new string('0', 64), CancellationToken.None);
        await coordinator.CompleteLinkAsync(code, null, CancellationToken.None);
        Assert.Null(store.State.Credential);
        Assert.Null(remote.Redeemed);
        Assert.Contains("not started here", store.State.AccountError);

        await coordinator.CompleteLinkAsync(code, query["state"], CancellationToken.None);
        Assert.Equal(Account, store.State.Credential?.Account);
        Assert.Null(store.State.AccountError);
        Assert.Equal(code, remote.Redeemed!.Code);
        Assert.Equal(query["challenge"], ResearchEvidence.Hash(remote.Redeemed.Verifier));
        Assert.Null((await coordinator.StatusAsync(CancellationToken.None)).PendingLink);

        // The state is single-use.
        store.State = store.State with { Credential = null };
        await coordinator.CompleteLinkAsync(code, query["state"], CancellationToken.None);
        Assert.Null(store.State.Credential);
    }

    [Fact]
    public async Task ProjectDecisionsAreActiveChoicesAndIncludingStillRequiresConsent()
    {
        var snapshot = Files(("main.tex", "Synthetic research."));
        var store = new MemoryStore(new(1, Credential, null, null, [], []));
        using var coordinator = Coordinator(store, new Remote(), new Source(snapshot));
        string id = snapshot.ProjectId;

        var refused = await Assert.ThrowsAsync<ResearchFailure>(() => coordinator.DecideProjectAsync(id, new(true, null), CancellationToken.None));
        Assert.Equal("research_consent_required", refused.Code);
        Assert.Empty(store.State.Projects);
        Assert.Null(store.State.Decisions);

        await coordinator.DecideProjectAsync(id, new(true, ResearchEvidence.ConsentVersion), CancellationToken.None);
        Assert.True(Assert.Single(store.State.Projects).Enabled);
        Assert.True(Assert.Single(store.State.Decisions!).Included);

        await coordinator.DecideProjectAsync(id, new(false, null), CancellationToken.None);
        Assert.False(Assert.Single(store.State.Projects).Enabled);
        var decision = Assert.Single((await coordinator.StatusAsync(CancellationToken.None)).Decisions);
        Assert.Equal((id, false), (decision.ProjectId, decision.Included));

        await coordinator.DecideProjectAsync(id, new(true, ResearchEvidence.ConsentVersion), CancellationToken.None);
        await coordinator.ForgetAsync(id, CancellationToken.None);
        Assert.Empty(store.State.Projects);
        Assert.False(Assert.Single(store.State.Decisions!).Included);
    }

    [Fact]
    public async Task ProjectDecisionsAndResearchersBelongToOneAccount()
    {
        var other = new ProjectDecision(Guid.NewGuid(), "0123456789abcdef01234567", true, DateTimeOffset.UtcNow);
        var otherResearcher = new ResearcherState(other.AccountId, 1, "ready", "Someone", [], null, [], null, null, null);
        var store = new MemoryStore(new(1, Credential, null, null, [], [], [other], [otherResearcher]));
        using var coordinator = Coordinator(store, new Remote(), new Source(Files(("main.tex", "Synthetic."))));
        var status = await coordinator.StatusAsync(CancellationToken.None);
        Assert.Empty(status.Decisions);
        Assert.Null(status.Researcher);
    }

    [Fact]
    public async Task StateWrittenBeforeDecisionsAndResearchersStillLoads()
    {
        string directory = Path.Combine(Path.GetTempPath(), "meld-vault-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var vault = new LocalVault(directory, new byte[32]);
            await vault.WriteAsync(new(1, Credential, null, null, [], []), CancellationToken.None);
            string json = System.Text.Json.JsonSerializer.Serialize(new LocalResearchState(1, Credential, null, null, [], []), ResearchJson.Options);
            Assert.Contains("\"decisions\":null", json);
            var legacy = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
            legacy.Remove("decisions");
            legacy.Remove("researchers");
            var restored = System.Text.Json.JsonSerializer.Deserialize<LocalResearchState>(legacy.ToJsonString(), ResearchJson.Options)!;
            Assert.Null(restored.Decisions);
            Assert.Null(restored.Researchers);
            Assert.Equal(Credential, (await vault.ReadAsync(CancellationToken.None)).Credential);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task IncludingUsesTheKnownProjectListWithoutWaitingForTheBrowser()
    {
        var snapshot = Files(("main.tex", "Synthetic research."));
        var source = new Source(snapshot) { Latest = new([new(snapshot.ProjectId, "Known name", DateTimeOffset.UtcNow)], DateTimeOffset.UtcNow) };
        var store = new MemoryStore(new(1, Credential, null, null, [], []));
        using var coordinator = Coordinator(store, new Remote(), source);
        await coordinator.DecideProjectAsync(snapshot.ProjectId, new(true, ResearchEvidence.ConsentVersion), CancellationToken.None);
        Assert.Equal(0, source.ListReads);
        Assert.Equal("Known name", Assert.Single(store.State.Projects).Name);
    }

    [Fact]
    public async Task SummarizeSendsOnlyTheMainTexOfARecentProjectAndReusesItWhileUnchanged()
    {
        var source = new Source(Files(("sections/intro.tex", "Intro text."),
            ("paper.tex", "\\documentclass{article}\n\\begin{document}\nA study of synthetic markets.\n\\end{document}"),
            ("slides/talk.tex", "\\documentclass{beamer}\n\\begin{document}\nSlides.\n\\end{document}")));
        var store = new MemoryStore(new(1, Credential, null, null, [], []));
        var remote = new Remote();
        using var coordinator = Coordinator(store, remote, source);

        await coordinator.RequestSummaryAsync(ProjectId, CancellationToken.None);
        Assert.Equal("queued", (await coordinator.StatusAsync(CancellationToken.None)).Summaries!.Single().Status);
        await Drain(coordinator);
        var request = Assert.Single(remote.Summaries);
        Assert.Equal(("paper.tex", ProjectId), (request.Path, request.ProjectId));
        Assert.Contains("synthetic markets", request.Text);
        Assert.Empty(remote.Requests); // Not included: no profile processing.
        Assert.Empty(store.State.Projects);
        var ready = Assert.Single((await coordinator.StatusAsync(CancellationToken.None)).Summaries!);
        Assert.Equal(("ready", "paper.tex", "A synthetic project about paper.tex."), (ready.Status, ready.MainPath, ready.Summary));
        Assert.DoesNotContain(store.State.Summaries!, item => item.Summary!.Contains("synthetic markets"));

        // Asking again with the same main file reuses the summary without a model call.
        await coordinator.RequestSummaryAsync(ProjectId, CancellationToken.None);
        await Drain(coordinator);
        Assert.Single(remote.Summaries);
        Assert.Equal("ready", store.State.Summaries!.Single().Status);

        await coordinator.ResetIfDueAsync(CancellationToken.None);
        Assert.Empty(store.State.Summaries!);
    }

    [Fact]
    public async Task SummariesAreOnlyForRecentProjectsWithAMainDocument()
    {
        var old = Files(("main.tex", "\\documentclass{article}\\begin{document}Old.\\end{document}")) with { SourceUpdatedAt = DateTimeOffset.UtcNow.AddDays(-200) };
        var store = new MemoryStore(new(1, Credential, null, null, [], []));
        using (var coordinator = Coordinator(store, new Remote(), new Source(old)))
            Assert.Equal("project_not_recent", (await Assert.ThrowsAsync<ResearchFailure>(() =>
                coordinator.RequestSummaryAsync(ProjectId, CancellationToken.None))).Code);

        var remote = new Remote();
        using (var coordinator = Coordinator(store, remote, new Source(Files(("notes.tex", "Only notes, no document.")))))
        {
            await coordinator.RequestSummaryAsync(ProjectId, CancellationToken.None);
            await Drain(coordinator);
        }
        var failed = Assert.Single(store.State.Summaries!);
        Assert.Equal("error", failed.Status);
        Assert.Contains("No main .tex file", failed.Error);
        Assert.Empty(remote.Summaries);
    }

    [Fact]
    public void TheMainDocumentIsTheTopLevelCompilableTexPreferringMain()
    {
        const string doc = "\\documentclass{article}\n\\begin{document}x\\end{document}";
        Assert.Equal("main.tex", ProjectSummaries.MainDocument([("deep/paper.tex", doc), ("main.tex", doc), ("other.tex", doc + "longer")]));
        Assert.Equal("other.tex", ProjectSummaries.MainDocument([("deep/main.tex", doc), ("other.tex", doc), ("refs.tex", "no class")]));
        Assert.Null(ProjectSummaries.MainDocument([("chapter.tex", "\\section{A}"), ("readme.md", doc)]));
    }

    private const string SecondId = "1123456789abcdef01234567";
    private static MultiSource TwoProjects() => new(
        Files(("a1.tex", "A one."), ("a2.tex", "A two.")) with { ProjectName = "news-slant" },
        Files(("b1.tex", "B one.")) with { ProjectId = SecondId, ProjectName = "news-slant-slides" });
    private static MonitoredProject IncludedAs(string id, string name) => Included(id) with { Name = name };

    [Fact]
    public void ThePapersTitleAndAbstractAreReadFromTheMainDocument()
    {
        var paper = Papers.Read([
            ("sections/intro.tex", "\\section{Introduction} Text."),
            ("abstract.tex", "We study how AI answers can pay publishers \\cite{x} for the information~they contribute."),
            ("main.tex", "\\documentclass{article}\n% \\title{Commented out}\n\\title[Short]{Paying for Information, \\\\ Not Clicks\\thanks{We thank {all}.}}\n" +
                "\\begin{document}\\maketitle\n\\input{abstract}\n\\end{document}")])!;
        Assert.Equal(("main.tex", "Paying for Information, Not Clicks"), (paper.Path, paper.Title));
        Assert.Equal("We study how AI answers can pay publishers for the information they contribute.", paper.Abstract);
        var inline = Papers.Read([("paper.tex", "\\documentclass{article}\\begin{document}\\begin{abstract}\n A \\emph{publisher} marketplace.\n\\end{abstract}\\end{document}")])!;
        Assert.Equal(((string?)null, (string?)"A publisher marketplace."), (inline.Title, inline.Abstract));
        Assert.Null(Papers.Read([("notes.tex", "Just notes.")]));

        // A memo next to the paper: the document with an abstract is the paper, even when the memo looks like the main file.
        var memo = Papers.Read([
            ("main.tex", "\\documentclass{article}\\title{Measurement Error in our News Slant Project}\\begin{document}" + new string('x', 5000) + "\\end{document}"),
            ("paper/slant.tex", "\\documentclass{article}\\title{\\texorpdfstring{\\texttt{slant}}{slant}: Article-Level Slant, see \\href{https://x.example}{the site}}" +
                "\\begin{document}\\begin{abstract}We measure slant.\\end{abstract}\\end{document}")])!;
        Assert.Equal(("paper/slant.tex", "slant: Article-Level Slant, see the site", "We measure slant."), (memo.Path, memo.Title, memo.Abstract));
    }

    internal sealed class MultiSource(params ResearchSnapshot[] snapshots) : IOverleafEvidenceSource
    {
        public bool Available => true;
        public OverleafProjectsSnapshot? Latest => null;
        public Task<IReadOnlyList<OverleafProjectResponse>> ProjectsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<OverleafProjectResponse>>(snapshots.Select(item => new OverleafProjectResponse(item.ProjectId, item.ProjectName, item.SourceUpdatedAt)).ToArray());
        public int Reads { get; private set; }
        public Task<ResearchSnapshot> SnapshotAsync(OverleafProjectResponse project, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(snapshots.Single(item => item.ProjectId == project.Id) with { ObservedAt = DateTimeOffset.UtcNow });
        }
        public Task<IReadOnlyDictionary<string, DateTimeOffset>?> EditTimesAsync(string projectId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, DateTimeOffset>?>(null);
    }

    [Fact]
    public async Task ABusyBrowserIsNotReportedAsUnavailable()
    {
        var snapshot = ResearchTests.Snapshot("Synthetic research.");
        var relay = new QueueingRelay();
        var source = new OverleafEvidenceSource(relay, new MeldResearch.LocalBackend.Services.OverleafService(relay, null!, TimeProvider.System));
        relay.Busy = true;
        Assert.True(source.Available);
        relay.Connected = false;
        Assert.False(source.Available);
        await Task.CompletedTask;
    }

    private sealed class QueueingRelay : MeldResearch.LocalBackend.Services.IBrowserRelay
    {
        public bool Connected { get; set; } = true;
        public bool Busy { get; set; }
        public BridgeStatusResponse Status => new(Connected, Busy, 2);
        public Task ConnectAsync(System.Net.WebSockets.WebSocket socket, string localUrl, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MeldResearch.LocalBackend.Services.BrowserResponse> FetchAsync(string operation, string? projectId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    internal sealed class MemoryStore(LocalResearchState state) : IResearchStore
    {
        public LocalResearchState State { get; set; } = state;
        public Dictionary<string, string> Texts { get; } = [];
        public Task<LocalResearchState> ReadAsync(CancellationToken ct) => Task.FromResult(State);
        public Task<LocalResearchState> ChangeAsync(Func<LocalResearchState, LocalResearchState> change, CancellationToken ct)
        { State = change(State); return Task.FromResult(State); }
        public Task WriteTextAsync(string sha256, string text, CancellationToken ct) { Texts[sha256] = text; return Task.CompletedTask; }
        public Task<string?> ReadTextAsync(string sha256, CancellationToken ct) => Task.FromResult(Texts.GetValueOrDefault(sha256));
        public void PruneTexts(IReadOnlySet<string> keep) { foreach (var hash in Texts.Keys.Where(hash => !keep.Contains(hash)).ToArray()) Texts.Remove(hash); }
    }

    internal sealed class Source(ResearchSnapshot snapshot) : IOverleafEvidenceSource
    {
        public ResearchSnapshot Snapshot { get; set; } = snapshot;
        public bool Available { get; set; } = true;
        public OverleafProjectsSnapshot? Latest { get; set; }
        public IReadOnlyDictionary<string, DateTimeOffset>? EditTimes { get; set; }
        public int Reads { get; private set; }
        public int ListReads { get; private set; }
        public Task<IReadOnlyList<OverleafProjectResponse>> ProjectsAsync(CancellationToken ct)
        {
            ListReads++;
            return Task.FromResult<IReadOnlyList<OverleafProjectResponse>>([new(Snapshot.ProjectId, Snapshot.ProjectName, Snapshot.SourceUpdatedAt)]);
        }
        public Task<ResearchSnapshot> SnapshotAsync(OverleafProjectResponse project, CancellationToken ct)
        { Reads++; return Task.FromResult(Snapshot with { ObservedAt = DateTimeOffset.UtcNow }); }
        public Task<IReadOnlyDictionary<string, DateTimeOffset>?> EditTimesAsync(string projectId, CancellationToken ct) => Task.FromResult(EditTimes);
    }

    internal sealed class Remote : IResearchRemote
    {
        public List<ProfileStepRequest> Requests { get; } = [];
        public int Calls { get; private set; }
        public ResearchFailure? Failure { get; set; }
        public Func<ProfileStepRequest, ProfileStepResponse>? Step { get; init; }
        public Action<ProfileStepRequest>? During { get; set; }
        public static ProfileStepResponse Summary(ProfileStepRequest request, IReadOnlyList<ProfileUpdate> updates) =>
            new(new("Synthetic project", "Summary after " + request.File.Path, [request.File.Path], ["synthetic"]), updates, "Folded in.", "chat");
        public Task<ProfileStepResponse> ProfileStepAsync(string token, ProfileStepRequest request, CancellationToken ct)
        {
            lock (Requests) Calls++;
            if (Failure is not null) throw Failure;
            During?.Invoke(request);
            ct.ThrowIfCancellationRequested();
            lock (Requests) Requests.Add(request);
            return Task.FromResult((Step ?? (item => Summary(item, [])))(request));
        }
        public List<EmailStepRequest> EmailRequests { get; } = [];
        public Func<EmailStepRequest, EmailStepResponse>? EmailStep { get; set; }
        public List<EmailStreamRequest> StreamRequests { get; } = [];
        public Task<EmailStreamResponse> EmailStreamAsync(string token, EmailStreamRequest request, CancellationToken ct)
        {
            lock (StreamRequests) StreamRequests.Add(request);
            return Task.FromResult(new EmailStreamResponse(new(request.Name, "Email work: " + string.Join("; ", request.Threads.Select(thread => thread.Subject)),
                [.. request.Threads.SelectMany(thread => thread.Bullets).Take(15)], ["email"]), "chat"));
        }
        public Task<EmailStepResponse> EmailStepAsync(string token, EmailStepRequest request, CancellationToken ct)
        {
            lock (EmailRequests) EmailRequests.Add(request);
            return Task.FromResult((EmailStep ?? (_ => throw new InvalidOperationException("No email step expected.")))(request));
        }
        public List<ThemeMatchRequest> Matches { get; } = [];
        public List<ThemeMergeRequest> Merges { get; } = [];
        public Func<ThemeMatchRequest, string?> Match { get; set; } = _ => null;
        public Task<ThemeMatchResponse> MatchThemeAsync(string token, ThemeMatchRequest request, CancellationToken ct)
        {
            Matches.Add(request);
            return Task.FromResult(new ThemeMatchResponse(Match(request), "Synthetic decision.", "chat"));
        }
        public Task<ThemeMergeResponse> MergeThemeAsync(string token, ThemeMergeRequest request, CancellationToken ct)
        {
            Merges.Add(request);
            return Task.FromResult(new ThemeMergeResponse(new("Combined research line", string.Join(" + ", request.Members.Select(member => member.ProjectName)),
                ["Combined."], ["combined"]), "chat"));
        }
        public Task<AccountPicture?> AccountPictureAsync(string token, CancellationToken ct) => Task.FromResult<AccountPicture?>(null);
        public List<DetailStepRequest> DetailRequests { get; } = [];
        /// <summary>The model's edits for a detail step; by default one detail per sent document, citing its first line.</summary>
        public Func<DetailStepRequest, DetailStepResponse>? Detail { get; set; }
        /// <summary>Runs while a detail step is in flight (for pausing or changing the account meanwhile).</summary>
        public Action<DetailStepRequest>? DuringDetail { get; set; }
        public Task<DetailStepResponse> DetailStepAsync(string token, DetailStepRequest request, CancellationToken ct)
        {
            lock (Requests) Calls++;
            if (Failure is not null) throw Failure;
            DuringDetail?.Invoke(request);
            ct.ThrowIfCancellationRequested();
            lock (DetailRequests) DetailRequests.Add(request);
            return Task.FromResult((Detail ?? DefaultDetail)(request));
        }
        public static DetailStepResponse DefaultDetail(DetailStepRequest request) =>
            new([.. request.Kind == DetailSource.Overleaf
                ? request.Documents.Where(document => document.Text.Split('\n')[0].Trim().Length != 0).Select(document => new DetailOp(DetailRules.Add, null,
                    "Detail from " + document.Path + " line " + document.FirstLine + ".", [new(document.Path, document.FirstLine, document.FirstLine,
                        document.Text.Split('\n')[0].Trim())], []))
                : request.Messages!.Select(message => new DetailOp(DetailRules.Add, null, "Email detail: " + message.Text.Split('\n')[0].Trim(),
                    [new(message.Id, 1, 1, message.Text.Split('\n')[0].Trim())], []))], "Folded.", "chat");
        public List<DetailSummaryRequest> DetailSummaries { get; } = [];
        public Task<DetailSummaryResponse> SummarizeDetailsAsync(string token, DetailSummaryRequest request, CancellationToken ct)
        {
            lock (DetailSummaries) DetailSummaries.Add(request);
            return Task.FromResult(new DetailSummaryResponse(request.PaperTitle ?? "Working title " + request.Details.Count,
                "A project with " + request.Details.Count + " details. It is ongoing.", "chat"));
        }
        public List<DetailMergeRequest> DetailMerges { get; } = [];
        public Task<DetailMergeResponse> MergeDetailsAsync(string token, DetailMergeRequest request, CancellationToken ct)
        {
            lock (DetailMerges) DetailMerges.Add(request);
            return Task.FromResult(new DetailMergeResponse([.. request.Cards.SelectMany(card => card.Details)
                .Select(detail => new DetailMerged(detail.Text, [detail.Id])).Take(DetailRules.MaxDetails)], "chat"));
        }
        public List<DetailReviewRequest> Reviews { get; } = [];
        /// <summary>The model's review; by default every detail is kept unchanged.</summary>
        public Func<DetailReviewRequest, DetailReviewResponse>? Review { get; set; }
        public Task<DetailReviewResponse> ReviewDetailsAsync(string token, DetailReviewRequest request, CancellationToken ct)
        {
            lock (Reviews) Reviews.Add(request);
            return Task.FromResult((Review ?? (item => new([.. item.Details.Select(detail => new DetailMerged(detail.Text, [detail.Id]))], "Unchanged.", "chat")))(request));
        }
        public List<CardSummaryRequest> Cards { get; } = [];
        public Task<ThemeMergeResponse> SummarizeCardAsync(string token, CardSummaryRequest request, CancellationToken ct)
        {
            lock (Cards) Cards.Add(request);
            return Task.FromResult(new ThemeMergeResponse(new(request.Title, "Condensed: " + request.Abstract, ["From the abstract."], ["paper"]), "chat"));
        }
        public List<CardBlurbRequest> Blurbs { get; } = [];
        public Task<CardBlurbResponse> DescribeCardAsync(string token, CardBlurbRequest request, CancellationToken ct)
        {
            lock (Blurbs) Blurbs.Add(request);
            return Task.FromResult(new CardBlurbResponse("Four or five sentences about " + request.Profile.Title + ".", "chat"));
        }
        public List<ThemeRegroupRequest> Regroups { get; } = [];
        public Func<ThemeRegroupRequest, IReadOnlyList<ThemeGroup>> Regroup { get; set; } = _ => [];
        public Task<ThemeRegroupResponse> RegroupThemesAsync(string token, ThemeRegroupRequest request, CancellationToken ct)
        {
            Regroups.Add(request);
            return Task.FromResult(new ThemeRegroupResponse(Regroup(request), "chat"));
        }
        public List<ProjectSummaryRequest> Summaries { get; } = [];
        public Task<ProjectSummaryResponse> SummarizeProjectAsync(string token, ProjectSummaryRequest request, CancellationToken ct)
        {
            Summaries.Add(request);
            return Task.FromResult(new ProjectSummaryResponse("A synthetic project about " + request.Path + ".", "chat"));
        }
        public Task<RemoteStatusResponse> StatusAsync(CancellationToken ct) =>
            Task.FromResult(new RemoteStatusResponse(false, true, true, "https://meld.example.invalid/privacy.html", "synthetic", "https://chromewebstore.google.com",
                SyncConfigured: Box is not null, GmailConfigured: GmailClient is not null));
        /// <summary>The Gmail desktop client Meld's server hands out; null means the server does not offer Gmail.</summary>
        public GmailClientResponse? GmailClient { get; init; } = new("test-client.apps.googleusercontent.com", "test-secret", false);
        public Task<GmailClientResponse> GmailClientAsync(string token, CancellationToken ct) => GmailClient is { } client
            ? Task.FromResult(client) : Task.FromException<GmailClientResponse>(new ResearchFailure(503, "gmail_not_configured", "No Gmail."));
        /// <summary>Encrypted sync storage shared by the "computers" of a test; null means the server offers no sync.</summary>
        public BlobBox? Box { get; init; }
        private BlobBox RequireBox => Box ?? throw new ResearchFailure(503, "sync_not_configured", "No sync.");
        public Task<SyncListResponse> SyncListAsync(string token, CancellationToken ct) => Task.FromResult(RequireBox.List());
        public Task<SyncLinksResponse> SyncLinksAsync(string token, SyncLinksRequest request, CancellationToken ct) =>
            Task.FromResult(new SyncLinksResponse([.. request.Names.Select(name => new SyncLink(name, "mem:" + name))], DateTimeOffset.UtcNow.AddMinutes(10)));
        public Task SyncDeleteAllAsync(string token, CancellationToken ct) { RequireBox.Clear(); return Task.CompletedTask; }
        public Task<SyncBlob?> BlobGetAsync(string url, CancellationToken ct) => Task.FromResult(RequireBox.Get(url[4..]));
        public Task<string> BlobPutAsync(string url, byte[] content, string? ifMatch, CancellationToken ct) => Task.FromResult(RequireBox.Put(url[4..], content, ifMatch));
        public Task BlobDeleteAsync(string url, CancellationToken ct) { RequireBox.Delete(url[4..]); return Task.CompletedTask; }
        /// <summary>The account Meld's server reports for the credential.</summary>
        public MeldAccountResponse Identity { get; init; } = Account;
        public Task<MeldAccountResponse> AccountAsync(string token, CancellationToken ct) => Task.FromResult(Identity);
        public Task RevokeAsync(string token, CancellationToken ct) => Task.CompletedTask;
        public int AccountDeletions { get; private set; }
        public Task DeleteAccountAsync(string token, CancellationToken ct) { AccountDeletions++; Box?.Clear(); return Task.CompletedTask; }
        public Func<ResearcherSearchRequest, ResearcherSearchResponse>? Search { get; set; }
        public Func<ResearcherWorksRequest, ResearcherWorksResponse>? Works { get; set; }
        public Task<ResearcherSearchResponse> SearchResearcherAsync(string token, ResearcherSearchRequest request, CancellationToken ct) =>
            Task.FromResult((Search ?? throw new NotSupportedException())(request));
        public Task<ResearcherWorksResponse> ResearcherWorksAsync(string token, ResearcherWorksRequest request, CancellationToken ct) =>
            Task.FromResult((Works ?? throw new NotSupportedException())(request));
        public RedeemSignInRequest? Redeemed { get; private set; }
        public Task<RedeemSignInResponse> RedeemSignInAsync(RedeemSignInRequest request, CancellationToken ct)
        {
            Redeemed = request;
            return Task.FromResult(new RedeemSignInResponse(new string('b', 64), DateTimeOffset.UtcNow.AddDays(30), Account));
        }
    }
}

/// <summary>An in-memory stand-in for the account's blob folder, with the same conditional-write rules as Azure.</summary>
internal sealed class BlobBox
{
    private readonly Dictionary<string, (byte[] Content, string ETag)> _blobs = new(StringComparer.Ordinal);
    private int _version;
    public int Puts { get; private set; }
    public IReadOnlyDictionary<string, byte[]> Contents { get { lock (_blobs) return _blobs.ToDictionary(item => item.Key, item => item.Value.Content); } }
    public SyncListResponse List()
    {
        lock (_blobs)
            return new([.. _blobs.Select(item => new SyncBlobInfo(item.Key, item.Value.ETag, item.Value.Content.Length, DateTimeOffset.UtcNow))],
                _blobs.Values.Sum(item => (long)item.Content.Length), 500L << 20);
    }
    public SyncBlob? Get(string name) { lock (_blobs) return _blobs.TryGetValue(name, out var blob) ? new(blob.Content, blob.ETag) : null; }
    public string Put(string name, byte[] content, string? ifMatch)
    {
        lock (_blobs)
        {
            bool exists = _blobs.TryGetValue(name, out var current);
            if (ifMatch is null ? exists : !exists || current.ETag != ifMatch)
                throw new ResearchFailure(412, "sync_conflict", "Changed elsewhere.");
            Puts++;
            string etag = $"\"{++_version}\"";
            _blobs[name] = (content, etag);
            return etag;
        }
    }
    public void Delete(string name) { lock (_blobs) _blobs.Remove(name); }
    public void Clear() { lock (_blobs) _blobs.Clear(); }
}
