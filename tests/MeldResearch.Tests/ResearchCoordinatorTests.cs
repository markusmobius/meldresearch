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

    [Fact]
    public async Task UnchangedSourcesMakeExactlyZeroModelCalls()
    {
        var snapshot = ResearchTests.Snapshot("Unchanged synthetic research.");
        var project = Project(snapshot);
        var store = new MemoryStore(new(1, Credential, null, null, [project], []));
        var remote = new Remote();
        using var coordinator = Coordinator(store, remote, new Source(snapshot));
        await coordinator.RunCycleAsync(true, CancellationToken.None);
        Assert.Equal(0, remote.ModelCalls);
        Assert.Equal(snapshot.Revision, store.State.Projects[0].Snapshot!.Revision);
        Assert.Contains("No model calls", store.State.Projects[0].Explanation);
    }

    [Fact]
    public async Task ModelFailureDoesNotAdvanceTheSnapshotOrDiscardKnowledge()
    {
        var before = ResearchTests.Snapshot("The synthetic pilot has 40 participants.");
        var after = ResearchTests.Snapshot("The synthetic pilot has 80 participants.");
        var store = new MemoryStore(new(1, Credential, null, null, [Project(before)], []));
        var remote = new Remote { Failure = new(502, "synthetic_model_failure", "Synthetic model failure.") };
        using var coordinator = Coordinator(store, remote, new Source(after));
        await coordinator.RunCycleAsync(true, CancellationToken.None);
        Assert.Equal(before.Revision, store.State.Projects[0].Snapshot!.Revision);
        Assert.Equal("synthetic_model_failure", store.State.Projects[0].ErrorCode);
        Assert.Empty(store.State.Projects[0].Updates);
    }

    [Fact]
    public async Task AccountChangeDuringInferenceDiscardsTheResult()
    {
        var snapshot = ResearchTests.Snapshot("Synthetic research evidence.");
        var project = Project(null);
        var store = new MemoryStore(new(1, Credential, null, null, [project], []));
        var remote = new Remote { DuringAnalysis = () => store.State = store.State with { Credential = null } };
        using var coordinator = Coordinator(store, remote, new Source(snapshot));
        await coordinator.RunCycleAsync(true, CancellationToken.None);
        Assert.Null(store.State.Projects[0].Snapshot);
        Assert.Empty(store.State.Projects[0].Updates);
        Assert.Equal("research_state_changed", store.State.Projects[0].ErrorCode);
    }

    [Fact]
    public async Task OnlyExplicitlyConsentedProjectsAreRead()
    {
        var snapshot = ResearchTests.Snapshot("Synthetic evidence.");
        var source = new Source(snapshot);
        var store = new MemoryStore(new(1, Credential, null, null, [Project(null) with { ConsentVersion = "old-policy" }], []));
        using var coordinator = Coordinator(store, new Remote(), source);
        await coordinator.RunCycleAsync(true, CancellationToken.None);
        Assert.Equal(0, source.Reads);
        store.State = store.State with { Credential = null };
        await coordinator.RunCycleAsync(true, CancellationToken.None);
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task DeletingTheLastTextFileStillProcessesKnowledgeRemoval()
    {
        var previous = ResearchTests.Snapshot("The old result is supported here.");
        var empty = previous with { Files = [], Revision = ResearchEvidence.Revision([]) };
        var id = Guid.NewGuid();
        var fact = new ResearchFact(id, "Old result", previous.Files[0].Text!, [],
            [new("main.tex", previous.Files[0].Text!, EvidenceVersion.Current)], previous.Revision, "embedding", [1f]);
        var store = new MemoryStore(new(1, Credential, null, null, [Project(previous) with { Facts = [fact] }], []));
        var remote = new Remote
        {
            Analysis = new(new("The source was removed.", "No current source remains.",
                [new(FactOperation.Remove, id, "Result no longer sourced", "The supporting source was removed.", [],
                    [new("main.tex", previous.Files[0].Text!, EvidenceVersion.Previous)])]), "chat", "embedding", [])
        };
        using var coordinator = Coordinator(store, remote, new Source(empty));
        await coordinator.RunCycleAsync(true, CancellationToken.None);
        Assert.Null(store.State.Projects[0].Error);
        Assert.Empty(store.State.Projects[0].Facts);
        Assert.Equal(ResearchUpdateKind.Removal, Assert.Single(store.State.Projects[0].Updates).Kind);
    }

    [Fact]
    public async Task BrowserOutageDoesNotAccumulateAnEverExtendingModelBackoff()
    {
        var source = new Source(ResearchTests.Snapshot("Synthetic source.")) { Available = false };
        var store = new MemoryStore(new(1, Credential, null, null, [Project(null)], []));
        using var coordinator = Coordinator(store, new Remote(), source);
        for (int index = 0; index < 20; index++) await coordinator.RunCycleAsync(false, CancellationToken.None);
        Assert.Equal(0, store.State.Projects[0].FailureCount);
        Assert.Null(store.State.Projects[0].LastAttemptAt);
        source.Available = true;
        await coordinator.RunCycleAsync(false, CancellationToken.None);
        Assert.Equal(1, source.Reads);
    }

    private static MonitoredProject Project(ResearchSnapshot? snapshot) =>
        new(Account.Id, "0123456789abcdef01234567", "Synthetic", true, ResearchEvidence.ConsentVersion,
            snapshot, null, [], [], null, null, null, null, null, 0, 0);
    private static ResearchCoordinator Coordinator(IResearchStore store, IResearchRemote remote, IOverleafEvidenceSource source) =>
        new(store, remote, source, new(new Uri("https://meld.example.invalid"), false, 300),
            NullLogger<ResearchCoordinator>.Instance, TimeProvider.System);

    private sealed class MemoryStore(LocalResearchState state) : IResearchStore
    {
        public LocalResearchState State { get; set; } = state;
        public Task<LocalResearchState> ReadAsync(CancellationToken ct) => Task.FromResult(State);
        public Task<LocalResearchState> ChangeAsync(Func<LocalResearchState, LocalResearchState> change, CancellationToken ct)
        { State = change(State); return Task.FromResult(State); }
    }
    private sealed class Source(ResearchSnapshot snapshot) : IOverleafEvidenceSource
    {
        public bool Available { get; set; } = true;
        public int Reads { get; private set; }
        public Task<IReadOnlyList<OverleafProjectResponse>> ProjectsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<OverleafProjectResponse>>([new(snapshot.ProjectId, snapshot.ProjectName, null)]);
        public Task<ResearchSnapshot> SnapshotAsync(OverleafProjectResponse project, CancellationToken ct)
        { Reads++; return Task.FromResult(snapshot); }
    }
    private sealed class Remote : IResearchRemote
    {
        public int ModelCalls { get; private set; }
        public ResearchFailure? Failure { get; init; }
        public AnalyzeResearchResponse? Analysis { get; init; }
        public Action? DuringAnalysis { get; init; }
        public Task<RemoteStatusResponse> StatusAsync(CancellationToken ct) =>
            Task.FromResult(new RemoteStatusResponse(false, true, true, "https://meld.example.invalid/privacy.html", "synthetic", "https://chromewebstore.google.com"));
        public Task<MeldAccountResponse> AccountAsync(string token, CancellationToken ct) => Task.FromResult(Account);
        public Task<AnalyzeResearchResponse> AnalyzeAsync(string token, AnalyzeResearchRequest request, CancellationToken ct)
        {
            ModelCalls++;
            if (Failure is not null) throw Failure;
            DuringAnalysis?.Invoke();
            return Task.FromResult(Analysis ?? new(new ResearchAnalysis("Synthetic project summary.", "No meaningful new facts.", []), "chat", "embedding", []));
        }
        public Task<ResearchEmbeddingResponse> EmbedAsync(string token, ResearchEmbeddingRequest request, CancellationToken ct)
        { ModelCalls++; return Task.FromResult(new ResearchEmbeddingResponse("embedding", [[1f]])); }
        public Task RevokeAsync(string token, CancellationToken ct) => Task.CompletedTask;
        public RedeemSignInRequest? Redeemed { get; private set; }
        public Task<RedeemSignInResponse> RedeemSignInAsync(RedeemSignInRequest request, CancellationToken ct)
        {
            Redeemed = request;
            return Task.FromResult(new RedeemSignInResponse(new string('b', 64), DateTimeOffset.UtcNow.AddDays(30), Account));
        }
    }

    [Fact]
    public async Task StartupClearsAccountMessagesLeftByEarlierVersions()
    {
        var legacy = new PendingDeviceLink(new string('d', 64), new string('e', 64), "https://meld.example.invalid/connect?code=ABC",
            "ABCDEF012345", DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(-5));
        var store = new MemoryStore(new(1, Credential, legacy, "The account link expired. Start a new link.", [], []));
        using var coordinator = Coordinator(store, new Remote(), new Source(ResearchTests.Snapshot("Synthetic.")));
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
        using var coordinator = Coordinator(store, remote, new Source(ResearchTests.Snapshot("Synthetic.")));
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
}
