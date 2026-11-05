using Microsoft.Extensions.Logging.Abstractions;
using MeldResearch.LocalBackend.Contracts;
using MeldResearch.LocalBackend.Services.Research;
using MeldResearch.Research;
using static MeldResearch.Tests.ResearchCoordinatorTests;

namespace MeldResearch.Tests;

/// <summary>Two "computers" (separate local stores) sharing one account's encrypted blob folder.</summary>
public sealed class ResearchSyncTests
{
    private const string Passphrase = "correct horse battery", NewPassphrase = "staple battery horse";
    private static readonly CancellationToken None = CancellationToken.None;

    private sealed record Computer(ResearchCoordinator Coordinator, MemoryStore Store, Remote Remote, Source Source) : IDisposable
    {
        public Task<SyncStatusResponse> SyncAsync() => Coordinator.StatusAsync(None).ContinueWith(task => task.Result.Sync!);
        public Task<SyncKeyResponse> CreateAsync(string passphrase = Passphrase) =>
            Coordinator.WithSyncAsync((sync, credential) => sync.CreateAsync(credential, passphrase, None), None);
        public Task UnlockAsync(string? passphrase, string? recovery = null) =>
            Coordinator.WithSyncAsync(async (sync, credential) => { await sync.UnlockAsync(credential, new(passphrase, recovery), None); return true; }, None);
        public void Dispose() => Coordinator.Dispose();
    }

    private static Computer New(BlobBox box, ResearchSnapshot snapshot, params MonitoredProject[] projects)
    {
        var store = new MemoryStore(new(1, Credential, null, null, projects, [],
            Decisions: [.. projects.Select(project => new ProjectDecision(Account.Id, project.ProjectId, true, DateTimeOffset.UtcNow))]));
        var remote = new Remote { Box = box };
        var source = new Source(snapshot);
        return new(new(store, remote, source, new(new Uri("https://meld.example.invalid"), false, 300),
            NullLogger<ResearchCoordinator>.Instance, TimeProvider.System), store, remote, source);
    }

    private static string Json<T>(T value) => System.Text.Json.JsonSerializer.Serialize(value, ResearchJson.Options);
    private static int ModelCalls(Remote remote) => remote.Calls + remote.Blurbs.Count + remote.Matches.Count + remote.Merges.Count + remote.Regroups.Count + remote.DetailSummaries.Count + remote.DetailMerges.Count + remote.Reviews.Count;

    [Fact]
    public async Task NothingIsProcessedUntilThisComputerHasTheKeyAndOnlyCiphertextIsStored()
    {
        var box = new BlobBox();
        using var first = New(box, Files(("main.tex", MainDoc("Main text about scooping.")), ("a.tex", "Section A.")), Included());
        await Drain(first.Coordinator);
        Assert.Equal(0, first.Remote.Calls);
        Assert.Equal(0, first.Source.Reads);
        Assert.Equal("setup", (await first.SyncAsync()).State);

        await Assert.ThrowsAsync<ResearchFailure>(() => first.CreateAsync("too short"));
        var key = await first.CreateAsync();
        Assert.Matches("^([0-9A-Z]{4}-){7}[0-9A-Z]{4}$", key.RecoveryKey);
        await Drain(first.Coordinator);

        Assert.Equal(1, first.Remote.Calls);
        var status = await first.SyncAsync();
        Assert.Equal(("ready", 1), (status.State, status.SyncedProjects));
        var names = box.Contents.Keys.Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(3, names.Length);
        Assert.Equal([SyncCrypto.KeyBlob, SyncCrypto.ProfileBlob], names.Where(name => !name.StartsWith("p3/")).Order(StringComparer.Ordinal));
        Assert.Matches("^p3/[0-9a-f]{32}\\.bin$", Assert.Single(names, name => name.StartsWith("p3/")));
        foreach (var content in box.Contents.Values)
        {
            string text = System.Text.Encoding.UTF8.GetString(content);
            Assert.DoesNotContain("scooping", text);
            Assert.DoesNotContain(ProjectId, text);
        }
        // Unchanged research is not uploaded again.
        int puts = box.Puts;
        await Drain(first.Coordinator, force: true);
        Assert.Equal(puts, box.Puts);
        // Nor downloaded again: Azure lists an ETag without the quotes its uploads return, and that is the same version.
        int gets = box.Gets;
        await Drain(first.Coordinator, force: true);
        await Drain(first.Coordinator, force: true);
        Assert.Equal(gets, box.Gets);
        Assert.True(ResearchSync.SameTag("\"0x8DD1\"", "0x8DD1"));
        Assert.True(ResearchSync.SameTag("W/\"0x8DD1\"", "\"0x8DD1\""));
        Assert.False(ResearchSync.SameTag("\"0x8DD1\"", "\"0x8DD2\""));
        Assert.False(ResearchSync.SameTag("\"0x8DD1\"", null));
    }

    [Fact]
    public async Task ASecondComputerContinuesFromSyncedWorkAndProcessesOnlyTheDelta()
    {
        var box = new BlobBox();
        var original = Files(("main.tex", MainDoc("Main text.")), ("a.tex", "Section A."));
        using var first = New(box, original, Included());
        await first.CreateAsync();
        await Drain(first.Coordinator);
        var imported = first.Store.State.Projects.Single();

        using var second = New(box, original);
        await Drain(second.Coordinator);
        Assert.Equal("locked", (await second.SyncAsync()).State);
        var wrong = await Assert.ThrowsAsync<ResearchFailure>(() => second.UnlockAsync("not the passphrase"));
        Assert.Equal("wrong_passphrase", wrong.Code);

        await second.UnlockAsync(Passphrase);
        await Drain(second.Coordinator);
        var pulled = Assert.Single(second.Store.State.Projects);
        Assert.True(pulled.ImportComplete);
        Assert.Equal(Json(imported.Profile), Json(pulled.Profile));
        Assert.Equal(imported.Baseline!.Revision, pulled.Baseline!.Revision);
        Assert.Equal(first.Store.State.Themes!.Single().Id, second.Store.State.Themes!.Single().Id);
        Assert.Equal(0, ModelCalls(second.Remote));
        Assert.Equal(0, second.Source.Reads);

        // A forced check reads Overleaf but finds the synced content unchanged: still no model call.
        await Drain(second.Coordinator, force: true);
        Assert.Equal(1, second.Source.Reads);
        Assert.Equal(0, second.Remote.Calls);

        // Overleaf changes: the second computer processes only the changed file and syncs it back.
        var changed = Files(("main.tex", MainDoc("Main text, now with a second experiment.")), ("a.tex", "Section A."));
        second.Source.Snapshot = changed;
        await Drain(second.Coordinator, force: true);
        Assert.Equal(["main.tex"], Assert.Single(second.Remote.DetailRequests).Documents.Select(document => document.Path));

        // The first computer adopts that work: its own check of the same content needs no model call.
        first.Source.Snapshot = changed;
        int before = first.Remote.Calls;
        await Drain(first.Coordinator, force: true);
        Assert.Equal(before, first.Remote.Calls);
        Assert.Equal(changed.Revision, first.Store.State.Projects.Single().Baseline!.Revision);
        Assert.Equal(Json(second.Store.State.Projects.Single().Profile), Json(first.Store.State.Projects.Single().Profile));
    }

    [Fact]
    public async Task TheRecoveryKeyUnlocksAndAPassphraseChangeKeepsOtherComputersSyncing()
    {
        var box = new BlobBox();
        var snapshot = Files(("main.tex", MainDoc("Main text.")));
        using var first = New(box, snapshot, Included());
        var key = await first.CreateAsync();
        await Drain(first.Coordinator);

        using var second = New(box, snapshot);
        var wrong = await Assert.ThrowsAsync<ResearchFailure>(() => second.UnlockAsync(null, "0000-0000-0000-0000-0000-0000-0000-0000"));
        Assert.Equal("wrong_passphrase", wrong.Code);
        await second.UnlockAsync(null, key.RecoveryKey.ToLowerInvariant());
        await Drain(second.Coordinator);
        Assert.Single(second.Store.State.Projects);

        await first.Coordinator.WithSyncAsync(async (sync, credential) => { await sync.ChangePassphraseAsync(credential, NewPassphrase, None); return true; }, None);
        await Drain(second.Coordinator, force: true);
        Assert.Equal("ready", (await second.SyncAsync()).State);

        using var third = New(box, snapshot);
        await Assert.ThrowsAsync<ResearchFailure>(() => third.UnlockAsync(Passphrase));
        await third.UnlockAsync(NewPassphrase);
        await Drain(third.Coordinator);
        Assert.Equal(0, ModelCalls(third.Remote));
        Assert.Single(third.Store.State.Projects);
    }

    [Fact]
    public async Task AnyUnlockedComputerCanShowTheRecoveryKeyAgain()
    {
        var box = new BlobBox();
        var snapshot = Files(("main.tex", MainDoc("Main text.")));
        using var first = New(box, snapshot, Included());
        var created = await first.CreateAsync();
        Task<SyncKeyResponse> Show(Computer computer) =>
            computer.Coordinator.WithSyncAsync((sync, credential) => sync.RecoveryKeyAsync(credential, None), None);
        Assert.Equal((created.RecoveryKey, false), ((await Show(first)).RecoveryKey, (await Show(first)).Replaced));

        using var second = New(box, snapshot);
        await Assert.ThrowsAsync<ResearchFailure>(() => Show(second));
        await second.UnlockAsync(Passphrase);
        Assert.Equal(created.RecoveryKey, (await Show(second)).RecoveryKey);

        // A key file made before it kept a copy: a new recovery key replaces the old one, and other computers keep syncing.
        var file = System.Text.Json.JsonSerializer.Deserialize<SyncKeyFile>(box.Get(SyncCrypto.KeyBlob)!.Content, ResearchJson.Options)!;
        box.Put(SyncCrypto.KeyBlob, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(file with { SealedRecoveryKey = null }, ResearchJson.Options),
            box.Get(SyncCrypto.KeyBlob)!.ETag);
        var replaced = await Show(first);
        Assert.True(replaced.Replaced);
        Assert.NotEqual(created.RecoveryKey, replaced.RecoveryKey);
        Assert.Equal(replaced.RecoveryKey, (await Show(second)).RecoveryKey);
        await Drain(second.Coordinator, force: true);
        Assert.Equal("ready", (await second.SyncAsync()).State);
        using var third = New(box, snapshot);
        await Assert.ThrowsAsync<ResearchFailure>(() => third.UnlockAsync(null, created.RecoveryKey));
        await third.UnlockAsync(null, replaced.RecoveryKey);
    }

    [Fact]
    public async Task StartingOverReplacesTheSyncedCopyAndLocksOtherComputersUntilTheNewPassphrase()
    {
        var box = new BlobBox();
        var snapshot = Files(("main.tex", MainDoc("Main text.")));
        using var first = New(box, snapshot, Included());
        await first.CreateAsync();
        await Drain(first.Coordinator);
        using var second = New(box, snapshot);
        await second.UnlockAsync(Passphrase);
        await Drain(second.Coordinator);
        string oldProjectBlob = box.Contents.Keys.Single(name => name.StartsWith("p3/"));

        // Forgotten passphrase on the first computer: its local research is kept and uploaded under a new key.
        await first.Coordinator.WithSyncAsync((sync, credential) => sync.StartOverAsync(credential, NewPassphrase, None), None);
        await Drain(first.Coordinator);
        Assert.Equal(3, box.Contents.Count);
        Assert.DoesNotContain(oldProjectBlob, box.Contents.Keys);
        Assert.Equal(1, first.Remote.Calls);

        // The second computer notices the new key and stops instead of uploading under the old one.
        int puts = box.Puts;
        second.Store.State = second.Store.State with { Projects = [.. second.Store.State.Projects.Select(item => item with { Name = "Renamed here" })] };
        await Drain(second.Coordinator, force: true);
        Assert.Equal(puts, box.Puts);
        Assert.Equal("locked", (await second.SyncAsync()).State);
        Assert.Null(second.Store.State.Sync);

        await second.UnlockAsync(NewPassphrase);
        await Drain(second.Coordinator);
        Assert.Equal("ready", (await second.SyncAsync()).State);
        Assert.Equal(0, second.Remote.Calls);
    }

    [Fact]
    public async Task StartingOverOnAnEmptyComputerKeepsAnotherComputersResearchCardsWithoutModelCalls()
    {
        var box = new BlobBox();
        var snapshot = Files(("main.tex", MainDoc("Main text.")));
        using var first = New(box, snapshot, Included());
        await first.CreateAsync();
        await Drain(first.Coordinator);
        var card = first.Store.State.Themes!.Single();
        Assert.NotNull(card.Blurb);
        int calls = ModelCalls(first.Remote);

        // A new computer with nothing on it starts over: the synced copy is replaced by an empty profile.
        using var fresh = New(box, snapshot);
        await fresh.Coordinator.WithSyncAsync((sync, credential) => sync.StartOverAsync(credential, NewPassphrase, None), None);
        await Drain(fresh.Coordinator);
        Assert.Empty(fresh.Store.State.Projects);

        // The first computer unlocks with the new passphrase and keeps its project, card and description.
        await Drain(first.Coordinator, force: true);
        Assert.Equal("locked", (await first.SyncAsync()).State);
        await first.UnlockAsync(NewPassphrase);
        await Drain(first.Coordinator);
        Assert.Equal(calls, ModelCalls(first.Remote));
        Assert.Equal(card.Id, first.Store.State.Themes!.Single().Id);
        Assert.Equal(card.Blurb, first.Store.State.Themes!.Single().Blurb);

        // ... and uploads them, so the empty computer now receives them too.
        await Drain(fresh.Coordinator, force: true);
        Assert.Single(fresh.Store.State.Projects);
        Assert.Equal(card.Id, fresh.Store.State.Themes!.Single().Id);
        Assert.Equal(0, ModelCalls(fresh.Remote));
    }

    [Fact]
    public async Task ExcludingAProjectDeletesItsSyncedCopyAndStopsItOnOtherComputers()
    {
        var box = new BlobBox();
        var snapshot = Files(("main.tex", MainDoc("Main text.")));
        using var first = New(box, snapshot, Included());
        await first.CreateAsync();
        await Drain(first.Coordinator);
        using var second = New(box, snapshot);
        await second.UnlockAsync(Passphrase);
        await Drain(second.Coordinator);
        Assert.True(second.Store.State.Projects.Single().Enabled);

        await first.Coordinator.DecideProjectAsync(ProjectId, new(false, null), None);
        await Drain(first.Coordinator);
        Assert.DoesNotContain(box.Contents.Keys, name => name.StartsWith("p3/"));

        await Drain(second.Coordinator, force: true);
        Assert.False(second.Store.State.Decisions!.Single().Included);
        Assert.All(second.Store.State.Projects, project => Assert.False(project.Enabled));
    }

    [Fact]
    public async Task ResearchSyncedByAnEarlierFormatIsIgnoredAndDeleted()
    {
        var box = new BlobBox();
        var snapshot = Files(("main.tex", MainDoc("Main text.")));
        using var first = New(box, snapshot, Included());
        await first.CreateAsync();
        await Drain(first.Coordinator);
        // Blobs left by the earlier detail format (and the one before it).
        string old = "p2/" + new string('a', 32) + ".bin";
        foreach (var name in new[] { "profile2.bin", old, "profile.bin", "p/" + new string('b', 32) + ".bin" })
        {
            Assert.True(SyncCrypto.IsBlobName(name) && SyncCrypto.IsLegacyBlob(name));
            box.Put(name, [1, 2, 3], null);
        }
        Assert.False(SyncCrypto.IsLegacyBlob(SyncCrypto.ProfileBlob));

        first.Store.State = first.Store.State with { Projects = [.. first.Store.State.Projects.Select(item => item with { Name = "Renamed" })] };
        await Drain(first.Coordinator, force: true);
        Assert.DoesNotContain(box.Contents.Keys, SyncCrypto.IsLegacyBlob);
        Assert.Contains(SyncCrypto.ProfileBlob, box.Contents.Keys);
    }

    [Fact]
    public async Task WithoutServerSyncNothingChanges()
    {
        var store = new MemoryStore(new(1, Credential, null, null, [Included()], []));
        var remote = new Remote();
        using var coordinator = new ResearchCoordinator(store, remote, new Source(Files(("main.tex", MainDoc("Main text.")))),
            new(new Uri("https://meld.example.invalid"), false, 300), NullLogger<ResearchCoordinator>.Instance, TimeProvider.System);
        await Drain(coordinator);
        Assert.Equal(1, remote.Calls);
        Assert.Equal("off", (await coordinator.StatusAsync(None)).Sync!.State);
        Assert.Null(store.State.Sync);
    }
}
