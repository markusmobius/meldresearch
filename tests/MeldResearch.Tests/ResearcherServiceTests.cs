using Microsoft.Extensions.Logging.Abstractions;
using MeldResearch.LocalBackend.Services.Research;
using MeldResearch.Research;

namespace MeldResearch.Tests;

public sealed class ResearcherServiceTests
{
    private static readonly MeldAccountResponse Account = new(Guid.NewGuid(), "Synthetic Researcher", "synthetic@example.invalid");
    private static readonly LinkedCredential Credential = new(Account, new string('a', 64), DateTimeOffset.UtcNow.AddDays(1));
    private static readonly PublicationWork Work = new("W1", "A synthetic paper", ["Synthetic Researcher"], 2025, "2025-01-01",
        "Synthetic Journal", null, "An abstract.", false);
    private static ResearcherCandidate Candidate(string id) =>
        new(id, "Synthetic Researcher", null, ["Synthetic University"], [], 12, [Work]);

    private static (ResearchCoordinatorTests.MemoryStore Store, ResearchCoordinatorTests.Remote Remote, ResearcherService Service) Create()
    {
        var store = new ResearchCoordinatorTests.MemoryStore(new(1, Credential, null, null, [], []));
        var remote = new ResearchCoordinatorTests.Remote();
        return (store, remote, new ResearcherService(store, remote, NullLogger<ResearcherService>.Instance, TimeProvider.System));
    }

    [Fact]
    public async Task SearchAnswersAtOnceAndOnlyTheUsersConfirmationQueuesTheImport()
    {
        var (store, remote, service) = Create();
        string? searched = null;
        remote.Search = request => { searched = request.Name; return new([Candidate("A1"), Candidate("A2")]); };
        remote.Works = request => new(request.AuthorId == "A2" ? [Work] : throw new InvalidOperationException());

        await service.RunOnceAsync(CancellationToken.None);
        Assert.Null(searched); // Nothing is searched until the user asks.

        await service.DecideAsync(new("search", null, "  Synthetic Researcher ", null), CancellationToken.None);
        Assert.Equal("Synthetic Researcher", searched);
        var offered = Assert.Single(store.State.Researchers!);
        Assert.Equal("choice", offered.Status);
        Assert.Equal(2, offered.Candidates.Count);
        Assert.Null(offered.Researcher);

        var invented = await Assert.ThrowsAsync<ResearchFailure>(() =>
            service.DecideAsync(new("confirm", "A9", null, offered.Version), CancellationToken.None));
        Assert.Equal("invalid_researcher_choice", invented.Code);

        await service.DecideAsync(new("confirm", "A2", null, offered.Version), CancellationToken.None);
        Assert.Equal("importing", store.State.Researchers![0].Status);
        Assert.Empty((await service.PublicationsAsync(CancellationToken.None)).Works); // Queued, not fetched in the request.
        await service.RunOnceAsync(CancellationToken.None);
        var ready = store.State.Researchers![0];
        Assert.Equal(("ready", "A2"), (ready.Status, ready.Researcher!.Id));
        Assert.Equal([Work], ready.Works);
        Assert.True(ready.DueAt > DateTimeOffset.UtcNow.AddDays(6));
        Assert.Equal([Work], (await service.PublicationsAsync(CancellationToken.None)).Works);
    }

    [Fact]
    public async Task AQueuedImportResumesWithANewWorker()
    {
        var (store, remote, _) = Create();
        remote.Search = _ => new([Candidate("A1")]);
        remote.Works = _ => new([Work]);
        var first = new ResearcherService(store, remote, NullLogger<ResearcherService>.Instance, TimeProvider.System);
        await first.DecideAsync(new("search", null, "Synthetic Researcher", null), CancellationToken.None);
        await first.DecideAsync(new("confirm", "A1", null, store.State.Researchers![0].Version), CancellationToken.None);

        // A restart: the persisted state alone drives the next worker.
        var restarted = new ResearcherService(store, remote, NullLogger<ResearcherService>.Instance, TimeProvider.System);
        await restarted.RunOnceAsync(CancellationToken.None);
        Assert.Equal(("ready", 1), (store.State.Researchers![0].Status, store.State.Researchers[0].Works.Count));
    }

    [Fact]
    public async Task ANewerDecisionOrAccountChangeDiscardsAnInFlightResult()
    {
        var (store, remote, service) = Create();
        remote.Search = _ =>
        {
            store.State = store.State with { Researchers = [new(Account.Id, 99, "rejected", "Second Name", [], null, [], null, null, null)] };
            return new([Candidate("A1")]);
        };
        var changed = await Assert.ThrowsAsync<ResearchFailure>(() =>
            service.DecideAsync(new("search", null, "First Name", null), CancellationToken.None));
        Assert.Equal("researcher_changed", changed.Code);
        Assert.Equal(("rejected", "Second Name"), (store.State.Researchers![0].Status, store.State.Researchers[0].SearchName));

        remote.Search = _ => new([Candidate("A1")]);
        await service.DecideAsync(new("search", null, "Synthetic Researcher", null), CancellationToken.None);
        await service.DecideAsync(new("confirm", "A1", null, store.State.Researchers![0].Version), CancellationToken.None);
        remote.Works = _ =>
        {
            store.State = store.State with { Credential = null };
            return new([Work]);
        };
        await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal("importing", store.State.Researchers![0].Status);
        Assert.Empty(store.State.Researchers[0].Works);
    }

    [Fact]
    public async Task NoneRejectsTheCandidatesAndAStaleChoiceIsRefused()
    {
        var (store, remote, service) = Create();
        remote.Search = _ => new([Candidate("A1")]);
        await service.DecideAsync(new("search", null, "Synthetic Researcher", null), CancellationToken.None);
        long version = store.State.Researchers![0].Version;
        await Assert.ThrowsAsync<ResearchFailure>(() => service.DecideAsync(new("none", null, null, version - 1), CancellationToken.None));
        await service.DecideAsync(new("none", null, null, version), CancellationToken.None);
        Assert.Equal("rejected", store.State.Researchers![0].Status);
        Assert.Empty(store.State.Researchers[0].Candidates);
    }

    [Fact]
    public async Task AFailedSearchIsReportedAndKeepsTheConfirmedAuthor()
    {
        var (store, remote, service) = Create();
        remote.Search = _ => new([Candidate("A1")]);
        remote.Works = _ => new([Work]);
        await service.DecideAsync(new("search", null, "Synthetic Researcher", null), CancellationToken.None);
        await service.DecideAsync(new("confirm", "A1", null, store.State.Researchers![0].Version), CancellationToken.None);
        await service.RunOnceAsync(CancellationToken.None);
        var ready = store.State.Researchers![0];

        remote.Search = _ => throw new ResearchFailure(429, "openalex_rate_limited", "OpenAlex has reached its request limit.");
        var failure = await Assert.ThrowsAsync<ResearchFailure>(() =>
            service.DecideAsync(new("search", null, "Another Name", ready.Version), CancellationToken.None));
        Assert.Equal("openalex_rate_limited", failure.Code);
        Assert.Same(ready, store.State.Researchers![0]);
    }

    [Fact]
    public async Task ImportFailuresBackOffAndRetryNowRunsAgainImmediately()
    {
        var (store, remote, service) = Create();
        remote.Search = _ => new([Candidate("A1")]);
        await service.DecideAsync(new("search", null, "Synthetic Researcher", null), CancellationToken.None);
        await service.DecideAsync(new("confirm", "A1", null, store.State.Researchers![0].Version), CancellationToken.None);

        remote.Works = _ => throw new ResearchFailure(429, "openalex_rate_limited", "OpenAlex has reached its request limit.");
        await service.RunOnceAsync(CancellationToken.None);
        var limited = store.State.Researchers![0];
        Assert.Equal("importing", limited.Status);
        Assert.Contains("request limit", limited.LastError);
        Assert.True(limited.DueAt > DateTimeOffset.UtcNow.AddMinutes(30));

        remote.Works = _ => throw new ResearchFailure(502, "remote_gateway_error", "Meld's server did not answer its web proxy.");
        await service.DecideAsync(new("retry", null, null, limited.Version), CancellationToken.None);
        Assert.Null(store.State.Researchers![0].LastError);
        await service.RunOnceAsync(CancellationToken.None);
        var failed = store.State.Researchers![0];
        Assert.Equal(1, failed.Failures);
        Assert.True(failed.DueAt < DateTimeOffset.UtcNow.AddMinutes(2));

        remote.Works = _ => new([Work]);
        await service.DecideAsync(new("retry", null, null, failed.Version), CancellationToken.None);
        await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(("ready", 0), (store.State.Researchers![0].Status, store.State.Researchers[0].Failures));
    }
}
