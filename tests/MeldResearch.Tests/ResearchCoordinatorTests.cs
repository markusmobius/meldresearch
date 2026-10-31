using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MeldResearch.LocalBackend.Contracts;
using MeldResearch.LocalBackend.Middleware;
using MeldResearch.LocalBackend.Services.Research;
using MeldResearch.Research;

namespace MeldResearch.Tests;

public sealed class ResearchCoordinatorTests
{
    private static readonly MeldAccountResponse Account = new(Guid.NewGuid(), "Synthetic", "synthetic@example.invalid");
    private static readonly LinkedCredential Credential = new(Account, new string('a', 64), DateTimeOffset.UtcNow.AddDays(1));
    private const string ProjectId = "0123456789abcdef01234567";

    private static ResearchSnapshot Files(params (string Path, string Text)[] files)
    {
        var items = files.Select(file => new ResearchFile(file.Path, ResearchEvidence.Hash(file.Text), Encoding.UTF8.GetByteCount(file.Text),
            ResearchEvidence.TextExtensions.Contains(Path.GetExtension(file.Path)) ? file.Text : null)).ToArray();
        return new(ProjectId, "Synthetic research", ResearchEvidence.Revision(items), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(-1), items);
    }
    private static MonitoredProject Included(string id = ProjectId) =>
        new(Account.Id, id, "Synthetic", true, ResearchEvidence.ConsentVersion, null, null, [], [], null, null, null, null, null, 0, 0);
    private static ResearchCoordinator Coordinator(IResearchStore store, IResearchRemote remote, IOverleafEvidenceSource source) =>
        new(store, remote, source, new(new Uri("https://meld.example.invalid"), false, 300),
            NullLogger<ResearchCoordinator>.Instance, TimeProvider.System);
    private static async Task Drain(ResearchCoordinator coordinator, bool force = false)
    {
        for (int cycle = 0; cycle < 50 && await coordinator.RunCycleAsync(force && cycle == 0, CancellationToken.None); cycle++) { }
    }

    [Fact]
    public async Task InitialImportFoldsFilesOneAtATimeOldestFirstWithoutUpdates()
    {
        var source = new Source(Files(("main.tex", "Main text."), ("a.tex", "Section A."), ("b.tex", "Section B."), ("refs.bib", "@book{x}")))
        {
            EditTimes = new Dictionary<string, DateTimeOffset> { ["a.tex"] = DateTimeOffset.UtcNow.AddDays(-1), ["b.tex"] = DateTimeOffset.UtcNow.AddDays(-3) }
        };
        var store = new MemoryStore(new(1, Credential, null, null, [Included()], []));
        var remote = new Remote();
        using var coordinator = Coordinator(store, remote, source);
        await Drain(coordinator);
        // No history for main.tex means it is older than the history: it comes first, then by edit time.
        Assert.Equal(["main.tex", "b.tex", "a.tex"], remote.Requests.Select(request => request.File.Path));
        Assert.All(remote.Requests, request => Assert.True(request.Initial && request.File.PreviousText is null));
        Assert.Null(remote.Requests[0].Profile);
        Assert.NotNull(remote.Requests[1].Profile);
        var project = Assert.Single(store.State.Projects);
        Assert.True(project.ImportComplete);
        Assert.Empty(project.Updates);
        Assert.Null(project.Pending);
        Assert.Equal(source.Snapshot.Revision, project.Baseline!.Revision);
        Assert.Equal(1, project.UnprocessedFiles);
        Assert.Equal("ready", (await coordinator.StatusAsync(CancellationToken.None)).Projects[0].Phase);
    }

    [Fact]
    public async Task TheQueueResumesAfterARestartWithoutRedoingFinishedFiles()
    {
        var source = new Source(Files(("a.tex", "A."), ("b.tex", "B."), ("c.tex", "C.")));
        var store = new MemoryStore(new(1, Credential, null, null, [Included()], []));
        var remote = new Remote();
        using (var first = Coordinator(store, remote, source))
        {
            Assert.True(await first.RunCycleAsync(false, CancellationToken.None, maxSteps: 1));
            var status = await first.StatusAsync(CancellationToken.None);
            Assert.Equal((1, 3), (status.Projects[0].FilesDone, status.Projects[0].FilesTotal));
            Assert.Equal(("import", 1, 3), (status.Processing!.Kind, status.Processing.FilesDone, status.Processing.FilesTotal));
        }
        using var restarted = Coordinator(store, remote, source);
        await Drain(restarted);
        Assert.Equal(["a.tex", "b.tex", "c.tex"], remote.Requests.Select(request => request.File.Path));
        Assert.Equal(1, source.Reads);
        Assert.True(store.State.Projects[0].ImportComplete);
    }

    [Fact]
    public async Task UnchangedContentMakesZeroModelCallsAndUnchangedMetadataIsNotEvenDownloaded()
    {
        var source = new Source(Files(("main.tex", "Main text.")));
        var store = new MemoryStore(new(1, Credential, null, null, [Included()], []));
        var remote = new Remote();
        using var coordinator = Coordinator(store, remote, source);
        await Drain(coordinator);
        int calls = remote.Requests.Count;

        await Drain(coordinator, force: true);
        Assert.Equal(calls, remote.Requests.Count);
        Assert.Equal(2, source.Reads);
        Assert.Contains("No model calls", store.State.Projects[0].Explanation);

        // The daily check is not due yet; once due, unchanged Overleaf metadata needs no download either.
        await Drain(coordinator);
        Assert.Equal(2, source.Reads);
        store.State = store.State with { Projects = [store.State.Projects[0] with { LastSweepAt = DateTimeOffset.UtcNow.AddDays(-2) }] };
        await Drain(coordinator);
        Assert.Equal(2, source.Reads);
        Assert.True(store.State.Projects[0].LastSweepAt > DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal(calls, remote.Requests.Count);
    }

    [Fact]
    public async Task ChangedAndDeletedFilesBecomeGroundedUpdatesAgainstThePreviousText()
    {
        var source = new Source(Files(("main.tex", "The pilot has 40 participants."), ("old.tex", "An old claim.")));
        var store = new MemoryStore(new(1, Credential, null, null, [Included()], []));
        var remote = new Remote
        {
            Step = request => request.Initial ? Remote.Summary(request, []) : request.File.CurrentText is { } current
                ? Remote.Summary(request, [new("Pilot doubled", "The pilot grew to 80.", [new(request.File.Path, "80 participants", EvidenceVersion.Current)])])
                : Remote.Summary(request, [new("Claim removed", "The old claim was dropped.", [new(request.File.Path, "old claim", EvidenceVersion.Previous)])])
        };
        using var coordinator = Coordinator(store, remote, source);
        await Drain(coordinator);
        source.Snapshot = Files(("main.tex", "The pilot has 80 participants."));
        await Drain(coordinator, force: true);

        var delta = remote.Requests.Where(request => !request.Initial).ToArray();
        Assert.Equal(["main.tex", "old.tex"], delta.Select(request => request.File.Path));
        Assert.Equal(("The pilot has 40 participants.", "The pilot has 80 participants."), (delta[0].File.PreviousText, delta[0].File.CurrentText));
        Assert.Null(delta[1].File.CurrentText);
        var updates = store.State.Projects[0].Updates;
        Assert.Equal([ResearchUpdateKind.Change, ResearchUpdateKind.Removal], updates.Select(update => update.Kind));
        Assert.Equal("80 participants", updates[0].Evidence[0].Quote);
        Assert.Equal(store.State.Projects[0].Baseline!.Revision, updates[0].Revision);
    }

    [Fact]
    public async Task IdenticalCopiesInOtherFoldersAreNotProcessedAgain()
    {
        var source = new Source(Files(("main.tex", "The paper."), ("submission/main.tex", "The paper."), ("camera/main.tex", "The paper.")));
        var store = new MemoryStore(new(1, Credential, null, null, [Included()], []));
        var remote = new Remote();
        using var coordinator = Coordinator(store, remote, source);
        await Drain(coordinator);
        Assert.Single(remote.Requests);
        Assert.Equal(3, store.State.Projects[0].Processed!.Count);
    }

    [Fact]
    public async Task ModelFailureKeepsProgressBacksOffAndAManualCheckRetries()
    {
        var source = new Source(Files(("main.tex", "Main text.")));
        var store = new MemoryStore(new(1, Credential, null, null, [Included()], []));
        var remote = new Remote { Failure = new(502, "synthetic_model_failure", "Synthetic model failure.") };
        using var coordinator = Coordinator(store, remote, source);
        await Drain(coordinator);
        var project = store.State.Projects[0];
        Assert.Equal("synthetic_model_failure", project.ErrorCode);
        Assert.True(project.NextAttemptAt > DateTimeOffset.UtcNow);
        Assert.Empty(project.Processed ?? new Dictionary<string, string>());
        Assert.NotNull(project.Pending);
        int calls = remote.Calls;
        await Drain(coordinator);
        Assert.Equal(calls, remote.Calls);

        remote.Failure = null;
        await Drain(coordinator, force: true);
        Assert.True(store.State.Projects[0].ImportComplete);
        Assert.Null(store.State.Projects[0].ErrorCode);
    }

    [Fact]
    public async Task AccountChangeDuringAStepDiscardsTheResult()
    {
        var store = new MemoryStore(new(1, Credential, null, null, [Included()], []));
        var remote = new Remote();
        remote.During = _ => store.State = store.State with { Credential = null };
        using var coordinator = Coordinator(store, remote, new Source(Files(("main.tex", "Main text."))));
        await coordinator.RunCycleAsync(false, CancellationToken.None);
        Assert.Null(store.State.Projects[0].Profile);
        Assert.Empty(store.State.Projects[0].Processed ?? new Dictionary<string, string>());
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
    public async Task ExcludingAnotherProjectDoesNotInterruptTheProjectBeingProcessed()
    {
        var store = new MemoryStore(new(1, Credential, null, null, [Included()], []));
        ResearchCoordinator? coordinator = null;
        var remote = new Remote();
        remote.During = _ => coordinator!.DecideProjectAsync("1123456789abcdef01234567", new(false, null), CancellationToken.None).GetAwaiter().GetResult();
        using (coordinator = Coordinator(store, remote, new Source(Files(("main.tex", "Synthetic research.")))))
            await Drain(coordinator);
        Assert.True(store.State.Projects[0].ImportComplete);
        Assert.Null(store.State.Projects[0].Error);
    }

    [Fact]
    public async Task PausingTheProjectBeingProcessedStopsOnlyItWithoutRecordingAnError()
    {
        var store = new MemoryStore(new(1, Credential, null, null, [Included()], []));
        ResearchCoordinator? coordinator = null;
        var remote = new Remote();
        remote.During = _ => coordinator!.SetMonitoringAsync(ProjectId, new(false, null), CancellationToken.None).GetAwaiter().GetResult();
        using (coordinator = Coordinator(store, remote, new Source(Files(("main.tex", "Synthetic research.")))))
            await Drain(coordinator);
        var project = Assert.Single(store.State.Projects);
        Assert.False(project.Enabled);
        Assert.Null(project.Profile);
        Assert.Null(project.Error);
    }

    [Fact]
    public async Task ProjectsFromTheEarlierPipelineAreReimportedAndTheirOldSnapshotIsDropped()
    {
        var legacy = Included() with { Snapshot = ResearchTests.Snapshot("Legacy text."), Summary = "Old summary.", Updates = [] };
        var store = new MemoryStore(new(1, Credential, null, null, [legacy], []));
        var remote = new Remote();
        using var coordinator = Coordinator(store, remote, new Source(Files(("main.tex", "Legacy text."))));
        await Drain(coordinator);
        var project = store.State.Projects[0];
        Assert.Null(project.Snapshot);
        Assert.Null(project.Summary);
        Assert.True(project.ImportComplete);
        Assert.True(Assert.Single(remote.Requests).Initial);
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

    private sealed class Source(ResearchSnapshot snapshot) : IOverleafEvidenceSource
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
            Calls++;
            if (Failure is not null) throw Failure;
            During?.Invoke(request);
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult((Step ?? (item => Summary(item, [])))(request));
        }
        public Task<RemoteStatusResponse> StatusAsync(CancellationToken ct) =>
            Task.FromResult(new RemoteStatusResponse(false, true, true, "https://meld.example.invalid/privacy.html", "synthetic", "https://chromewebstore.google.com"));
        public Task<MeldAccountResponse> AccountAsync(string token, CancellationToken ct) => Task.FromResult(Account);
        public Task RevokeAsync(string token, CancellationToken ct) => Task.CompletedTask;
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
