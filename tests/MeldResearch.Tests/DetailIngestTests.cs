using Microsoft.Extensions.Logging.Abstractions;
using MeldResearch.LocalBackend.Contracts;
using MeldResearch.LocalBackend.Services.Research;
using MeldResearch.Research;
using static MeldResearch.Tests.ResearchCoordinatorTests;

namespace MeldResearch.Tests;

/// <summary>The Overleaf ingest of OVERLEAF.md: root trees into a research project's details, summaries, changelog and cards.</summary>
public sealed class DetailIngestTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private const string SecondId = "1123456789abcdef01234567";

    private static string Paper(string title, string abstractText, params string[] inputs) =>
        $"\\documentclass{{article}}\n\\title{{{title}}}\n\\begin{{document}}\n\\begin{{abstract}}\n{abstractText}\n\\end{{abstract}}\n" +
        string.Join("\n", inputs.Select(input => $"\\input{{{input}}}")) + "\n\\end{document}";

    private static string Memo(string text) => "\\documentclass{article}\n\\begin{document}\n" + text + "\n\\end{document}";

    private static ResearchCoordinator Create(IResearchStore store, IResearchRemote remote, IOverleafEvidenceSource source) =>
        new(store, remote, source, new(new Uri("https://meld.example.invalid"), false, 300), NullLogger<ResearchCoordinator>.Instance, TimeProvider.System);

    private static Source PaperProject(out Dictionary<string, DateTimeOffset> edited)
    {
        var now = DateTimeOffset.UtcNow;
        edited = new()
        {
            ["main.tex"] = now.AddDays(-20), ["sections/a.tex"] = now.AddDays(-20), ["sections/b.tex"] = now.AddDays(-1),
            ["memo.tex"] = now.AddDays(-10), ["old.tex"] = now.AddDays(-300), ["scrap.tex"] = now.AddDays(-1)
        };
        return new Source(Files(("main.tex", Paper("Paying for Information", "We build a publisher marketplace.", "sections/a", "sections/b")),
            ("sections/a.tex", "Model section."), ("sections/b.tex", "Results section."), ("memo.tex", Memo("A memo on pricing.")),
            ("old.tex", Memo("Old notes.")), ("scrap.tex", "tiny"), ("refs.bib", "@book{x}"))) { EditTimes = edited };
    }

    [Fact]
    public async Task TheInitialIngestReadsRootTreesOldestFirstAndWritesTheSummaryOnceAtTheEnd()
    {
        var source = PaperProject(out var edited);
        var store = new MemoryStore(new(1, Credential, null, null, [Included()], []));
        var remote = new Remote();
        using var coordinator = Create(store, remote, source);
        await Drain(coordinator);

        // The memo's tree first, then the paper's (its newest section makes it the newest tree); old roots and scraps are not read.
        Assert.Equal([["memo.tex"], ["main.tex", "sections/a.tex", "sections/b.tex"]], remote.DetailRequests.Select(request => request.Documents.Select(document => document.Path).ToArray()));
        var paper = remote.DetailRequests[1];
        Assert.Equal(["main.tex [sent]", "sections/a.tex [sent]", "sections/b.tex [sent]"], paper.Outline);
        Assert.Equal(("Paying for Information", "We build a publisher marketplace."), (paper.PaperTitle, paper.Abstract));
        Assert.Single(paper.Details);

        var card = Assert.Single(store.State.Themes!);
        Assert.Equal([ProjectId], card.Members);
        Assert.Equal(4, card.Details!.Count);
        var cited = card.Details.SelectMany(detail => detail.Citations).Single(citation => citation.Source.Item == "sections/a.tex").Source;
        Assert.Equal((DetailSource.Overleaf, ProjectId, ResearchEvidence.Hash("Model section."), edited["sections/a.tex"]),
            (cited.Kind, cited.Container, cited.Version, cited.At));
        // One summary, written once the cycle finished; its title is the paper's.
        var summary = Assert.Single(remote.DetailSummaries);
        Assert.Equal("Paying for Information", summary.PaperTitle);
        Assert.Equal(("Paying for Information", "A project with 4 details. It is ongoing."), (card.Profile!.Title, card.Profile.Description));
        Assert.Empty(remote.Matches);

        // The changelog has one entry per root tree, dated by the tree; the feed shows them newest first.
        Assert.Equal([("memo.tex", edited["memo.tex"], 1), ("main.tex", edited["sections/b.tex"], 3)],
            store.State.Changelog!.Select(entry => (entry.Title, entry.At, entry.Changes.Count)));
        var status = await coordinator.StatusAsync(None);
        Assert.Equal(["main.tex", "memo.tex"], status.Changes!.Select(change => change.Title));
        Assert.Equal(4, status.Themes!.Single().Details!.Count);
        var project = store.State.Projects.Single();
        Assert.True(project.ImportComplete);
        Assert.Null(project.Pending);
        Assert.Equal(["main.tex", "memo.tex", "sections/a.tex", "sections/b.tex"], project.Processed!.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("Paying for Information", project.Paper!.Title);

        // Unchanged content: a forced check makes no model call.
        await Drain(coordinator, force: true);
        Assert.Equal((2, 1), (remote.DetailRequests.Count, remote.DetailSummaries.Count));
    }

    [Fact]
    public async Task LaterIngestsSendOnlyChangedDocumentsAndDropOutdatedOrRemovedSources()
    {
        var source = PaperProject(out var edited);
        var store = new MemoryStore(new(1, Credential, null, null, [Included()], []));
        var remote = new Remote();
        using var coordinator = Create(store, remote, source);
        await Drain(coordinator);

        // A changed section: only it is sent, with the tree as outline; its earlier citations are marked outdated and dropped.
        source.Snapshot = Files(("main.tex", Paper("Paying for Information", "We build a publisher marketplace.", "sections/a", "sections/b")),
            ("sections/a.tex", "Model section, revised."), ("sections/b.tex", "Results section."), ("memo.tex", Memo("A memo on pricing.")),
            ("old.tex", Memo("Old notes.")), ("scrap.tex", "tiny"), ("refs.bib", "@book{x}"));
        source.EditTimes = new Dictionary<string, DateTimeOffset>(edited) { ["sections/a.tex"] = DateTimeOffset.UtcNow };
        await Drain(coordinator, force: true);
        var update = remote.DetailRequests[^1];
        Assert.Equal(["sections/a.tex"], update.Documents.Select(document => document.Path));
        Assert.Contains("sections/a.tex [sent]", update.Outline);
        Assert.Contains(update.Details, detail => detail.Citations.Any(citation => citation.Outdated));
        var card = store.State.Themes!.Single();
        Assert.Equal(4, card.Details!.Count);
        Assert.All(card.Details.SelectMany(detail => detail.Citations).Where(citation => citation.Source.Item == "sections/a.tex"),
            citation => Assert.Equal(ResearchEvidence.Hash("Model section, revised."), citation.Source.Version));
        Assert.Equal([DetailRules.Add, DetailRules.Remove], store.State.Changelog![^1].Changes.Select(change => change.Op));
        Assert.Equal(2, remote.DetailSummaries.Count);

        // A section removed from the paper: main.tex is sent, and the section's sources go when the cycle ends.
        source.Snapshot = Files(("main.tex", Paper("Paying for Information", "We build a publisher marketplace.", "sections/a")),
            ("sections/a.tex", "Model section, revised."), ("memo.tex", Memo("A memo on pricing.")), ("old.tex", Memo("Old notes.")));
        source.EditTimes = new Dictionary<string, DateTimeOffset> { ["main.tex"] = DateTimeOffset.UtcNow, ["memo.tex"] = edited["memo.tex"], ["old.tex"] = edited["old.tex"] };
        await Drain(coordinator, force: true);
        Assert.Equal(["main.tex"], remote.DetailRequests[^1].Documents.Select(document => document.Path));
        card = store.State.Themes!.Single();
        Assert.DoesNotContain(card.Details!.SelectMany(detail => detail.Citations), citation => citation.Source.Item == "sections/b.tex");
        var removal = store.State.Changelog!.Single(entry => entry.Kind == ChangelogEntry.Removed);
        Assert.Equal(("sections/b.tex removed", 1), (removal.Title, removal.Changes.Count));
        Assert.DoesNotContain("sections/b.tex", store.State.Projects.Single().Processed!.Keys);
    }

    [Fact]
    public async Task ALongDocumentIsReadInPartsAndAnInterruptedIngestResumesWhereItStopped()
    {
        string section(string name) => $"\\section{{{name}}}\n" + string.Join('\n', Enumerable.Repeat(new string('x', 99), 250));
        string main = "\\documentclass{article}\n\\begin{document}\n" + section("One") + "\n" + section("Two") + "\n" + section("Three") + "\n\\end{document}";
        var source = new Source(Files(("main.tex", main)));
        var store = new MemoryStore(new(1, Credential, null, null, [Included()], []));
        var remote = new Remote();
        using (var first = Create(store, remote, source))
            Assert.True(await first.RunCycleAsync(false, None, maxSteps: 1));
        Assert.Single(remote.DetailRequests);
        Assert.NotNull(store.State.Projects[0].Partial);
        Assert.Equal((0, 1), DetailPlan.Progress(store.State.Projects[0]));

        using var restarted = Create(store, remote, source);
        await Drain(restarted);
        Assert.Equal(2, remote.DetailRequests.Count);
        Assert.True(remote.DetailRequests[1].Documents[0].FirstLine > 1);
        Assert.StartsWith("\\section{Three}", remote.DetailRequests[1].Documents[0].Text);
        Assert.Equal(1, source.Reads);
        var project = store.State.Projects[0];
        Assert.Null(project.Partial);
        Assert.Equal(ResearchEvidence.Hash(main), project.Processed!["main.tex"]);
        // One changelog entry for the tree, although it was read in two parts.
        Assert.Equal(2, Assert.Single(store.State.Changelog!).Changes.Count);
    }

    [Fact]
    public async Task UnchangedMetadataIsNotEvenDownloadedAndModelFailuresBackOffWithoutLosingProgress()
    {
        var source = new Source(Files(("main.tex", Memo("Main text."))));
        var store = new MemoryStore(new(1, Credential, null, null, [Included()], []));
        var remote = new Remote { Failure = new(502, "synthetic_model_failure", "Synthetic model failure.") };
        using var coordinator = Create(store, remote, source);
        await Drain(coordinator);
        var project = store.State.Projects[0];
        Assert.Equal("synthetic_model_failure", project.ErrorCode);
        Assert.True(project.NextAttemptAt > DateTimeOffset.UtcNow);
        Assert.NotNull(project.Pending);
        int calls = remote.Calls;
        await Drain(coordinator);
        Assert.Equal(calls, remote.Calls);

        remote.Failure = null;
        await Drain(coordinator, force: true);
        Assert.True(store.State.Projects[0].ImportComplete);
        Assert.Null(store.State.Projects[0].ErrorCode);
        int reads = source.Reads, details = remote.DetailRequests.Count;

        // The daily check is not due yet; once due, unchanged Overleaf metadata needs no download either.
        await Drain(coordinator);
        store.State = store.State with { Projects = [store.State.Projects[0] with { LastSweepAt = DateTimeOffset.UtcNow.AddDays(-2) }] };
        await Drain(coordinator);
        Assert.Equal((reads, details), (source.Reads, remote.DetailRequests.Count));
    }

    [Fact]
    public async Task PausingAProjectOrChangingTheAccountDuringAStepDiscardsTheResult()
    {
        var store = new MemoryStore(new(1, Credential, null, null, [Included()], []));
        ResearchCoordinator? coordinator = null;
        var remote = new Remote();
        remote.DuringDetail = _ => coordinator!.SetMonitoringAsync(ProjectId, new(false, null), None).GetAwaiter().GetResult();
        using (coordinator = Create(store, remote, new Source(Files(("main.tex", Memo("Synthetic research."))))))
            await Drain(coordinator);
        Assert.False(store.State.Projects.Single().Enabled);
        Assert.Null(store.State.Projects.Single().Error);
        Assert.Empty(store.State.Themes ?? []);

        store = new MemoryStore(new(1, Credential, null, null, [Included()], []));
        remote = new Remote();
        remote.DuringDetail = _ => store.State = store.State with { Credential = null };
        using var other = Create(store, remote, new Source(Files(("main.tex", Memo("Synthetic research.")))));
        await other.RunCycleAsync(false, None);
        Assert.Empty(store.State.Themes ?? []);
        Assert.Empty(store.State.Projects[0].Processed ?? new Dictionary<string, string>());
    }

    [Fact]
    public async Task ANewCardIsMatchedStrictlyAndCombinedWithItsDetailsMergedAndMovingAProjectMovesItsSources()
    {
        var source = new MultiSource(
            Files(("main.tex", Paper("Paying for Information", "A marketplace.", "model"))) with { ProjectName = "pcm-manuscript" },
            Files(("model.tex", "Model."), ("slides.tex", Memo("Slides of the marketplace paper."))) with { ProjectId = SecondId, ProjectName = "pcm-slides" });
        var store = new MemoryStore(new(1, Credential, null, null, [Included(), Included(SecondId) with { Name = "pcm-slides" }], []));
        var remote = new Remote { Match = request => request.Candidates[0].Id };
        using var coordinator = Create(store, remote, source);
        await Drain(coordinator);
        var card = Assert.Single(store.State.Themes!);
        Assert.Equal([ProjectId, SecondId], card.Members.Order(StringComparer.Ordinal));
        // The details of both were merged by one call (keeping their sources); the summary was written for the combined card.
        Assert.Single(remote.DetailMerges);
        Assert.Equal(2, card.Details!.Count);
        Assert.Contains(card.Details, detail => detail.Citations.Any(citation => citation.Source.Container == SecondId));
        Assert.Equal("Synthetic decision.", card.Reason);
        Assert.All(store.State.Changelog!.Where(entry => entry.Kind == ChangelogEntry.Overleaf), entry => Assert.Equal(card.Id, entry.CardId));
        Assert.Equal("Paying for Information", card.Profile!.Title);

        // Moving the slides out: their sources leave this card and their documents are read again into a card of their own.
        int before = remote.DetailRequests.Count;
        await coordinator.MoveToThemeAsync(SecondId, null, None);
        await Drain(coordinator);
        Assert.Equal(2, store.State.Themes!.Count);
        Assert.DoesNotContain(store.State.Themes!.Single(theme => theme.Id == card.Id).Details!.SelectMany(detail => detail.Citations),
            citation => citation.Source.Container == SecondId);
        var own = store.State.Themes!.Single(theme => theme.Id != card.Id);
        Assert.Equal([SecondId], own.Members);
        Assert.Equal([SecondId], own.Pinned!);
        Assert.Equal(before + 1, remote.DetailRequests.Count);
        Assert.Single(own.Details!);
    }

    [Fact]
    public async Task UnrelatedProjectsKeepTheirCardsUntilRegroupingCombinesThem()
    {
        var source = new MultiSource(
            Files(("main.tex", Memo("Scooping in academia."))) with { ProjectName = "scooping-academia" },
            Files(("main.tex", Memo("Scooping in drug discovery."))) with { ProjectId = SecondId, ProjectName = "scooping-drugs" });
        var store = new MemoryStore(new(1, Credential, null, null, [Included(), Included(SecondId)], []));
        var remote = new Remote { Regroup = request => [new([.. request.Cards.Select(card => card.Id)], "Same scooping research.")] };
        using var coordinator = Create(store, remote, source);
        await Drain(coordinator);
        var card = Assert.Single(store.State.Themes!);
        Assert.Equal(2, card.Members.Count);
        Assert.Single(remote.Regroups);
        Assert.Equal("Same scooping research.", card.Reason);
        await Drain(coordinator, force: true);
        Assert.Single(remote.Regroups);
    }

    [Fact]
    public async Task RemovingASourceSendsTheFileToPrivateUpdatesWhereItCanBeAddedToAnotherProject()
    {
        var source = PaperProject(out _);
        var store = new MemoryStore(new(1, Credential, null, null, [Included()], []));
        var remote = new Remote();
        using var coordinator = Create(store, remote, source);
        await Drain(coordinator);
        var card = store.State.Themes!.Single();
        var key = card.Details!.SelectMany(detail => detail.Citations).First(citation => citation.Source.Item == "sections/a.tex").Source.Key;

        await coordinator.RemoveSourceAsync(card.Id, key, None);
        card = store.State.Themes!.Single();
        Assert.DoesNotContain(card.Details!.SelectMany(detail => detail.Citations), citation => citation.Source.Key == key);
        Assert.Equal(["sections/a.tex"], store.State.Projects.Single().ExcludedDocs);
        var waiting = Assert.Single((await coordinator.StatusAsync(None)).PendingFiles!);
        Assert.Equal(("sections/a.tex", card.Id), (waiting.Path, waiting.FromCardId));

        // Added to another project: folded there once; its own project keeps ignoring it, even when it changes.
        var other = ThemePlan.NewCard(Account.Id, [], DateTimeOffset.UtcNow, name: "Pricing notes");
        store.State = store.State with { Themes = [.. store.State.Themes!, other] };
        await coordinator.DecidePendingFileAsync(new(ProjectId, "sections/a.tex", "project", other.Id), None);
        await Drain(coordinator);
        Assert.Equal(["sections/a.tex"], remote.DetailRequests[^1].Documents.Select(document => document.Path));
        Assert.Single(store.State.Themes!.Single(theme => theme.Id == other.Id).Details!);
        Assert.Empty(store.State.PendingFiles!);
        int calls = remote.DetailRequests.Count;
        source.Snapshot = Files(("main.tex", Paper("Paying for Information", "We build a publisher marketplace.", "sections/a", "sections/b")),
            ("sections/a.tex", "Model section, changed again."), ("sections/b.tex", "Results section."), ("memo.tex", Memo("A memo on pricing.")),
            ("old.tex", Memo("Old notes.")), ("scrap.tex", "tiny"), ("refs.bib", "@book{x}"));
        await Drain(coordinator, force: true);
        Assert.Equal(calls, remote.DetailRequests.Count);
    }

    [Fact]
    public async Task ASkippedCardPausesItsProjectsLeavesTheFeedAndComesBackWhenBroughtBack()
    {
        var source = new Source(Files(("main.tex", Memo("Daughter's thesis."))));
        var store = new MemoryStore(new(1, Credential, null, null, [Included()], []));
        using var coordinator = Create(store, new Remote(), source);
        await Drain(coordinator);
        var card = store.State.Themes!.Single();
        await coordinator.SkipThemeAsync(card.Id, true, None);
        var status = await coordinator.StatusAsync(None);
        Assert.True(status.Themes!.Single().Skipped);
        Assert.Empty(status.Changes!);
        int reads = source.Reads;
        await Drain(coordinator, force: true);
        Assert.Equal(reads, source.Reads);

        await coordinator.SkipThemeAsync(card.Id, false, None);
        await Drain(coordinator, force: true);
        Assert.True(source.Reads > reads);
        Assert.NotEmpty((await coordinator.StatusAsync(None)).Changes!);
    }

    [Fact]
    public async Task TheFreshStartKeepsSignInChoicesSkippedWorkAndOpenAlexButRebuildsTheResearch()
    {
        var researcher = new ResearcherState(Account.Id, 3, "ready", "Synthetic", [], null, [], DateTimeOffset.UtcNow, null, null);
        var skippedCard = new ResearchTheme(Account.Id, new string('a', 32), [SecondId], new("Thesis", "D", [], []), null, DateTimeOffset.UtcNow, Skipped: true);
        var dismissed = new GmailThreadRecord(Account.Id, "me@example.invalid", "t1", "Lunch", GmailThreadRecord.Dismissed, "1", "m", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, [], CardId: skippedCard.Id);
        var placed = dismissed with { Id = "t2", Status = GmailThreadRecord.Assigned };
        var store = new MemoryStore(new(1, Credential, null, null, [Included() with { Name = "pcm", Processed = new Dictionary<string, string> { ["main.tex"] = "x" } },
                Included(SecondId) with { Name = "thesis" }], [],
            [new(Account.Id, ProjectId, true, DateTimeOffset.UtcNow), new(Account.Id, SecondId, true, DateTimeOffset.UtcNow),
                new(Account.Id, "2123456789abcdef01234567", false, DateTimeOffset.UtcNow)], [researcher],
            Themes: [skippedCard], GmailThreads: [dismissed, placed],
            GmailAccounts: [new(Account.Id, "me@example.invalid", "refresh", GmailConnector.ConsentVersion, DateTimeOffset.UtcNow, LastImportAt: DateTimeOffset.UtcNow)],
            Changelog: [new(Guid.NewGuid(), Account.Id, skippedCard.Id, DateTimeOffset.UtcNow, ChangelogEntry.Overleaf, "main.tex", null, [], [], null, DateTimeOffset.UtcNow)]));
        store.Texts["abc"] = "Synthetic source text.";
        using var coordinator = Create(store, new Remote(), new Source(Files(("main.tex", Memo("Synthetic.")))));
        Assert.True(await coordinator.ResetIfDueAsync(None));
        var state = store.State;
        Assert.Equal(Credential, state.Credential);
        Assert.Equal([("pcm", (IReadOnlyDictionary<string, string>?)null), ("thesis", null)], state.Projects.Select(project => (project.Name, project.Processed)));
        Assert.Equal(3, state.Decisions!.Count);
        Assert.Single(state.Researchers!);
        Assert.Empty(state.Themes!);
        Assert.Empty(state.Changelog!);
        Assert.Equal([SecondId], state.SkippedProjects);
        Assert.Equal(["t1"], state.GmailThreads!.Select(thread => thread.Id));
        Assert.Null(state.GmailThreads![0].CardId);
        Assert.Null(state.GmailAccounts!.Single().LastImportAt);
        Assert.Empty(store.Texts);
        // Applied once.
        Assert.False(await coordinator.ResetIfDueAsync(None));

        // The skipped project's card is skipped again once it is read.
        var rebuilt = ThemePlan.Place(state, Account.Id, SecondId, null, DateTimeOffset.UtcNow);
        Assert.True(rebuilt.Themes!.Single().Skipped);
    }

    [Fact]
    public async Task TheChangelogPagesNewestFirstWithoutSkippingEntriesThatShareATime()
    {
        string card = new('a', 32);
        var at = DateTimeOffset.UtcNow.AddDays(-1);
        ChangelogEntry Entry(int minutes, string title) =>
            new(Guid.NewGuid(), Account.Id, card, at.AddMinutes(minutes), ChangelogEntry.Gmail, title, null, [], [], null, at);
        // Three entries at the same time straddle the page boundary.
        var entries = new[] { Entry(5, "newest"), Entry(0, "tie 1"), Entry(0, "tie 2"), Entry(0, "tie 3"), Entry(-5, "oldest"),
            Entry(9, "other card") with { CardId = new string('b', 32) } };
        var store = new MemoryStore(new(1, Credential, null, null, [], [], Changelog: entries));
        using var coordinator = Create(store, new Remote(), new Source(Files(("main.tex", Memo("Synthetic.")))));

        var titles = new List<string>();
        ChangelogPageResponse? page = null;
        do
        {
            var last = page?.Entries[^1];
            page = await coordinator.ChangelogAsync(card, last?.At, last?.Id, 2, None);
            titles.AddRange(page.Entries.Select(entry => entry.Title));
        } while (page.More);
        Assert.Equal("newest", titles[0]);
        Assert.Equal(["newest", "oldest", "tie 1", "tie 2", "tie 3"], titles.Order(StringComparer.Ordinal));
        Assert.Equal("oldest", titles[^1]);

        // When the last entry shown no longer exists (merged away), the next page continues from its time.
        var older = await coordinator.ChangelogAsync(card, at, Guid.NewGuid(), 10, None);
        Assert.Equal(["oldest"], older.Entries.Select(entry => entry.Title));
    }

    [Fact]
    public async Task ServerOutagesAreShownAsRetryingUntilTheyPersist()
    {
        var store = new MemoryStore(new(1, Credential, null, null, [Included()], []));
        var remote = new Remote { Failure = new ResearchFailure(503, "remote_unavailable", "Meld's server is unavailable.") };
        using var coordinator = Create(store, remote, new Source(Files(("main.tex", Memo("Synthetic.")))));
        await coordinator.RunCycleAsync(false, None);
        Assert.Equal("retrying", (await coordinator.StatusAsync(None)).Projects[0].Phase);
        store.State = store.State with { Projects = [store.State.Projects[0] with { FailureCount = ResearchCoordinator.TransientFailuresShown }] };
        Assert.Equal("error", (await coordinator.StatusAsync(None)).Projects[0].Phase);
    }
}
