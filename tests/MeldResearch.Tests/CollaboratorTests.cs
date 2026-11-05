using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MeldResearch.LocalBackend.Contracts;
using MeldResearch.LocalBackend.Services;
using MeldResearch.LocalBackend.Services.Research;
using MeldResearch.Research;
using static MeldResearch.Tests.ResearchCoordinatorTests;

namespace MeldResearch.Tests;

/// <summary>A research project's collaborators (OVERLEAF.md §12a): Overleaf editors, owner and authors, and email people, merged.</summary>
public sealed class CollaboratorTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static ResearchCoordinator Create(IResearchStore store, IResearchRemote remote, IOverleafEvidenceSource source) =>
        new(store, remote, source, new(new Uri("https://meld.example.invalid"), false, 300), NullLogger<ResearchCoordinator>.Instance, TimeProvider.System);

    [Fact]
    public async Task PeopleAreReadFromPapersHeadersTheProjectListAndHistoryWithoutAModel()
    {
        Assert.Equal("Ada Lovelace ; L. Bolte ; Sid Satya Emails: sid@example.invalid, ada@example.invalid",
            Papers.Authors(@"\author{Ada Lovelace \and L.~Bolte \and Sid Satya\thanks{sid@example.invalid}}\email{ada@example.invalid}"));
        Assert.Null(Papers.Authors(@"\title{No authors}"));
        Assert.Equal([("Lukas Bolte", "lukas@example.invalid"), ("Satya, Sid", "s.satya@example.invalid"), (null, "office@example.invalid")],
            EmailText.People("Lukas Bolte <Lukas@Example.invalid>, \"Satya, Sid\" <s.satya@example.invalid>, office@example.invalid"));

        var list = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"projects":[{"id":"0123456789abcdef01234567","name":"Paper","archived":false,"trashed":false,"lastUpdated":"2026-10-01T00:00:00Z",
            "owner":{"name":"Tanya Rosenblat","email":"Tanya@example.invalid"},"owned":false}]}
            """));
        var project = Assert.Single(await ProjectParser.ParseAsync(list, None));
        Assert.Equal((new OverleafPerson("Tanya Rosenblat", "tanya@example.invalid"), false), (project.Owner, project.Owned));

        var history = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"paths":{"main.tex":1767225600000},"complete":true,"editors":[{"name":"Lukas Bolte","email":"l.bolte@example.invalid","at":1767225600000},
            {"name":null,"email":"not an address"},{"name":"Sid Satya","email":null}],
            "files":{"main.tex":[[0,1767225600000],[2,1767139200000],[1,1767139200000]],"../escape.tex":[[0,1]]}}
            """));
        var read = await OverleafEvidenceSource.ParseHistoryAsync(history, None);
        Assert.Equal([new OverleafPerson("Lukas Bolte", "l.bolte@example.invalid", DateTimeOffset.FromUnixTimeMilliseconds(1767225600000)), new("Sid Satya", null)],
            read.Editors);
        Assert.Single(read.EditTimes!);
        // Who edited each file: indexes into the connector's list (an unusable editor is skipped), unsafe paths ignored.
        var file = Assert.Single(read.FileEditors!);
        Assert.Equal("main.tex", file.Key);
        Assert.Equal(["Lukas Bolte", "Sid Satya"], file.Value.Select(person => person.Name!));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1767139200000), file.Value[1].At);
    }

    [Fact]
    public void TheModelOnlyGroupsPeopleAndPicksNamesAsWritten()
    {
        var request = new CollaboratorsRequest("PCM", ["me@example.invalid"], "Lukas Bolte ; Ada Lovelace", [
            new("m1", "L. Bolte", "lukas@example.invalid", [Collaborators.From], 3),
            new("m2", null, "l.bolte@uni.example.invalid", [Collaborators.Editor], 1),
            new("m3", "Sid Satya", "sid@example.invalid", [Collaborators.Cc], 1),
            new("m4", null, "office@example.invalid", [Collaborators.Cc], 1)]);
        Collaborators.ValidateRequest(request);
        var (people, dropped) = Collaborators.Clean(request, new([
            new("Lukas Bolte", ["m1", "m2"], true),
            // An invented name falls back to a name as written; a mention claimed twice stays with the first person.
            new("Siddharth Satya", ["m3", "m1"], false),
            // An author only in the author blocks is kept; one the authors do not name is not.
            new("Ada Lovelace", [], true), new("Grace Hopper", [], true)], ["m4", "m9"], "chat"));
        Assert.Equal([("Lukas Bolte", new[] { "m1", "m2" }, true), ("Sid Satya", ["m3"], false), ("Ada Lovelace", [], true)],
            people.Select(person => (person.Name, person.Mentions.ToArray(), person.Author)));
        Assert.Equal(["m4"], dropped);

        // Mentions the model left out stay as people of their own.
        var (kept, _) = Collaborators.Clean(request, new([], [], "chat"));
        Assert.Equal(4, kept.Count);
        Assert.Equal("invalid_collaborators", Assert.Throws<ResearchFailure>(() => Collaborators.ValidateRequest(request with { Mentions = [], Authors = null })).Code);
    }

    [Fact]
    public async Task CollaboratorsAreMergedOnceAndOnlyNewPeopleCostACall()
    {
        var source = new Source(Files(("main.tex", "\\documentclass{article}\n\\title{Paying for Information}\n" +
            "\\author{Synthetic \\and L.~Bolte \\and Sid Satya}\n\\begin{document}\n\\begin{abstract}\nA marketplace.\n\\end{abstract}\nText.\n\\end{document}")))
        {
            Editors = [new("Synthetic", "synthetic@example.invalid"), new("Lukas Bolte", "l.bolte@uni.example.invalid", DateTimeOffset.UtcNow.AddDays(-2)),
                new("Sid Satya", "sid@example.invalid")],
            Owner = new("Tanya Rosenblat", "tanya@example.invalid")
        };
        var store = new MemoryStore(new(1, Credential, null, null, [Included()], []));
        var remote = new Remote();
        using var coordinator = Create(store, remote, source);
        await Drain(coordinator);

        // One call: the user (by their Meld address) is left out, the paper's authors come along.
        var request = Assert.Single(remote.People);
        Assert.Equal(["l.bolte@uni.example.invalid", "sid@example.invalid", "tanya@example.invalid"], request.Mentions.Select(mention => mention.Email).Order());
        Assert.Contains("synthetic@example.invalid", request.Self);
        Assert.Contains("L. Bolte", request.Authors);
        // The owner who never edited the project is no collaborator.
        var card = Assert.Single(store.State.Themes!);
        Assert.Equal([("Lukas Bolte", "author,editor"), ("Sid Satya", "author,editor")],
            card.Collaborators!.People.Select(person => (person.Name, string.Join(",", person.Kinds))).OrderBy(item => item.Name));
        await Drain(coordinator, force: true);
        Assert.Single(remote.People);

        // A thread on the project: a known person under another address joins them without a call; the office is new.
        var at = DateTimeOffset.UtcNow;
        GmailBullet Point(string from, string quote) => new(Guid.NewGuid(), quote, [new ResearchCitation("Pilot", quote, EvidenceVersion.Current,
            new EmailEvidence("m1", from, at))], at, at, false);
        GmailThreadRecord Thread(string id, params MailPerson[] people) => new(Account.Id, "synthetic@example.invalid", id, "Pilot", GmailThreadRecord.Assigned,
            "1", "m1", at, at, [Point("Grace Hopper <grace@example.invalid>", "I reran the pilot regressions with clustered errors."),
                Point("Pat Assistant <pat@example.invalid>", "Thanks, slides received.")], CardId: card.Id, FoldedMessageId: "m1", People: people);
        store.State = store.State with { GmailThreads = [Thread("t1", new MailPerson("Lukas Bolte", "lukas@example.invalid", 2, 1, 0, at))] };
        await Drain(coordinator);
        Assert.Single(remote.People);
        var lukas = store.State.Themes!.Single().Collaborators!.People.Single(person => person.Name == "Lukas Bolte");
        Assert.Equal(["l.bolte@uni.example.invalid", "lukas@example.invalid"], lukas.Emails.Order());
        Assert.Equal(4, lukas.Count);

        // More mail from known people changes counts only (no call), and so does someone only copied (they cannot be a collaborator).
        store.State = store.State with { GmailThreads = [Thread("t1", new MailPerson("Lukas Bolte", "lukas@example.invalid", 5, 1, 0, at),
            new MailPerson(null, "research-office@example.invalid", 0, 0, 1, at))] };
        await Drain(coordinator);
        Assert.Single(remote.People);
        // People who wrote something Meld used are judged in one call: substantive work counts, an assistant's thanks does not.
        store.State = store.State with
        {
            GmailThreads = [Thread("t1", new MailPerson("Lukas Bolte", "lukas@example.invalid", 5, 1, 0, at), new MailPerson(null, "research-office@example.invalid", 0, 0, 1, at),
                new MailPerson("Grace Hopper", "grace@example.invalid", 1, 0, 0, at), new MailPerson("Pat Assistant", "pat@example.invalid", 1, 1, 0, at))]
        };
        await Drain(coordinator);
        Assert.Equal(2, remote.People.Count);
        Assert.Equal(["I reran the pilot regressions with clustered errors."], remote.People[1].Mentions.Single(mention => mention.Email == "grace@example.invalid").Points);
        var status = (await coordinator.StatusAsync(None)).Themes!.Single().Collaborators!;
        Assert.Equal(["Grace Hopper", "Lukas Bolte", "Sid Satya"], status.Select(person => person.Name).Order());
        Assert.DoesNotContain(status, person => person.Emails.Contains("research-office@example.invalid") || person.Emails.Contains("pat@example.invalid"));
        Assert.Equal(7, status.Single(person => person.Name == "Lukas Bolte").Count);
        await Drain(coordinator);
        Assert.Equal(2, remote.People.Count);
    }

    [Fact]
    public async Task EditorsAreReadOnceFromAConnectorThatReportsThemAndAgainUntilThen()
    {
        var source = new Source(Files(("main.tex", "\\documentclass{article}\n\\begin{document}\nText.\n\\end{document}")));
        var store = new MemoryStore(new(1, Credential, null, null, [Included()], []));
        var remote = new Remote();
        using var coordinator = Create(store, remote, source);
        await Drain(coordinator);
        // An older connector reports no editors: that is not "nobody", so the next check asks again.
        Assert.Null(store.State.Projects.Single().EditorsFor);
        int reads = source.HistoryReads;
        await Drain(coordinator, force: true);
        Assert.Equal(reads + 1, source.HistoryReads);
        Assert.Null(store.State.Projects.Single().EditorsFor);

        // The current connector (it also says who edited each file): read once, then not again while the project is unchanged.
        source.Editors = [new("Lukas Bolte", "lukas@example.invalid")];
        source.FileEditors = new Dictionary<string, IReadOnlyList<OverleafPerson>> { ["main.tex"] = [new("Lukas Bolte", "lukas@example.invalid", DateTimeOffset.UtcNow)] };
        reads = source.HistoryReads;
        await Drain(coordinator, force: true);
        await Drain(coordinator, force: true);
        Assert.Equal(reads + 1, source.HistoryReads);
        Assert.Equal((CollaboratorPlan.EditorsVersion, "lukas@example.invalid"),
            (store.State.Projects.Single().EditorsFor, store.State.Projects.Single().Editors!.Single().Email));
        Assert.Equal("Lukas Bolte", store.State.Themes!.Single().Collaborators!.People.Single().Name);
        // Details read before who edited each file was kept rest on everyone who edited the cited file.
        var theme = (await coordinator.StatusAsync(None)).Themes!.Single();
        Assert.Equal((1.0, 0.0), (theme.Collaborators!.Single().Share, theme.YourShare));
    }

    [Fact]
    public async Task EditorsAreReadEvenWhenAnotherComputerChecksOverleaf()
    {
        var source = new Source(Files(("main.tex", "\\documentclass{article}\n\\begin{document}\nText.\n\\end{document}")));
        var store = new MemoryStore(new(1, Credential, null, null, [Included()], []));
        var remote = new Remote();
        using var coordinator = Create(store, remote, source);
        await Drain(coordinator);
        // Another computer just checked and synced its copy (without who edited each file): this computer's check is not due.
        var project = store.State.Projects.Single();
        store.State = store.State with { Projects = [project with { LastSweepAt = DateTimeOffset.UtcNow, EditorsFor = null }] };
        source.FileEditors = new Dictionary<string, IReadOnlyList<OverleafPerson>> { ["main.tex"] = [new("Lukas Bolte", "lukas@example.invalid", DateTimeOffset.UtcNow)] };
        int reads = source.HistoryReads, snapshots = source.Reads;
        await Drain(coordinator);
        await Drain(coordinator);
        Assert.Equal((reads + 1, snapshots), (source.HistoryReads, source.Reads));
        var read = store.State.Projects.Single();
        Assert.Equal((CollaboratorPlan.EditorsVersion, "Lukas Bolte"), (read.EditorsFor, read.Baseline!.FileEditors!["main.tex"].Single().Name));

        // A synced copy of the same version without who edited each file keeps this computer's knowledge of it.
        var bundle = SyncBundles.Project(read with { Baseline = read.Baseline with { FileEditors = null } }, new Dictionary<string, string>(), DateTimeOffset.UtcNow);
        var pulled = SyncBundles.ApplyProject(store.State, Account.Id, bundle, "etag2").Projects.Single();
        Assert.Equal("Lukas Bolte", pulled.Baseline!.FileEditors!["main.tex"].Single().Name);
    }

    [Fact]
    public void AQuoteIsCreditedToWhoWroteMostOfItsLines()
    {
        var lukas = new OverleafPerson("Lukas Bolte", "lukas@example.invalid");
        var nicole = new OverleafPerson("Nicole Immorlica", "nicimm@example.invalid");
        var me = new OverleafPerson("Synthetic", "synthetic@example.invalid");
        var self = CollaboratorPlan.Self(new LocalResearchState(1, Credential, null, null, [], []), Account.Id);
        var lines = Authors([lukas, nicole], ("Forty participants joined the pilot.", [(0, 25), (1, 3)]), ("Data collection is complete.", [(-1, 24)]),
            ("We estimate the effect.", [(1, 19)]));
        // A typo fix (3 of 28 characters) earns no credit; the person who wrote most of it is always credited.
        Assert.Equal(["e:lukas@example.invalid"], CollaboratorPlan.LineCredit("  Forty participants joined the pilot.  ", lines, me, self));
        // Text without an author goes to who added the file (here the user).
        Assert.Equal(["e:lukas@example.invalid", "self"], CollaboratorPlan.LineCredit("Forty participants joined the pilot.\nData collection is complete.", lines, me, self));
        Assert.Equal(["e:nicimm@example.invalid"], CollaboratorPlan.LineCredit("We estimate the effect.", lines, me, self));
        // Lines not found (the file changed since): no line credit.
        Assert.Null(CollaboratorPlan.LineCredit("A line that is not there.", lines, me, self));
        // Text without an author still counts when nobody is known to credit it with: a small edit is not the whole quote.
        var draft = Authors([lukas], ("The draft the owner uploaded, with a small fix.", [(-1, 35), (0, 4)]), ("Untouched draft.", [(-1, 14)]));
        Assert.Equal([], CollaboratorPlan.LineCredit("The draft the owner uploaded, with a small fix.", draft, null, self));
        // Found but written by nobody Meld can name: nobody, never the per-file fallback.
        Assert.Equal([], CollaboratorPlan.LineCredit("Untouched draft.", draft, null, self));
        Assert.Equal(["self"], CollaboratorPlan.LineCredit("The draft the owner uploaded, with a small fix.", draft, me, self));
    }

    private static FileAuthorship Authors(OverleafPerson[] users, params (string Line, (int Who, int Chars)[] Counts)[] lines) =>
        new(users, lines.ToDictionary(line => CollaboratorPlan.LineHash(line.Line), line => (IReadOnlyList<(int, int)>)line.Counts));

    [Fact]
    public async Task DetailsAreCreditedToWhoWroteTheQuotedLinesAndRewordingKeepsTheCredit()
    {
        static string Paper(params string[] body) => "\\documentclass{article}\n\\begin{document}\n" + string.Join("\n", body) + "\n\\end{document}";
        var lukas = new OverleafPerson("Lukas Bolte", "lukas@example.invalid", DateTimeOffset.UtcNow);
        // The user uploaded the paper: its text has no author in Overleaf's history, and the user added the file; Lukas edited it since.
        var source = new Source(Files(("main.tex", Paper("Forty participants joined."))))
        {
            Editors = [lukas], FileEditors = new Dictionary<string, IReadOnlyList<OverleafPerson>> { ["main.tex"] = [lukas] },
            FileAdders = new Dictionary<string, OverleafPerson> { ["main.tex"] = new("Synthetic", "synthetic@example.invalid") }
        };
        source.Authorship["main.tex"] = Authors([lukas], ("Forty participants joined.", [(-1, 23)]));
        var store = new MemoryStore(new(1, Credential, null, null, [Included()], []));
        var remote = new Remote
        {
            // The model keeps the pilot detail (re-quoting it with the same wording) and adds a detail for a results line.
            Detail = request =>
            {
                var document = request.Documents.Single();
                var text = document.Text.Split('\n');
                DetailQuote Quote(string part) { int index = Array.FindIndex(text, line => line.Contains(part)); return new(document.Path, document.FirstLine + index, document.FirstLine + index, text[index].Trim()); }
                var ops = new List<DetailOp>();
                var pilot = request.Details.FirstOrDefault(detail => detail.Text.Contains("pilot"));
                ops.Add(pilot is null ? new(DetailRules.Add, null, "The pilot has 40 participants.", [Quote("Forty")], [])
                    : new(DetailRules.Revise, pilot.Id, pilot.Text, [Quote("Forty")], []));
                if (text.Any(line => line.Contains("Results")) && !request.Details.Any(detail => detail.Text.Contains("gains")))
                    ops.Add(new(DetailRules.Add, null, "Results show 15 percent gains.", [Quote("Results")], []));
                return new(ops, "Folded.", "chat");
            }
        };
        using var coordinator = Create(store, remote, source);
        await Drain(coordinator);
        // The user's uploaded line: credited to the user, although Overleaf lists only Lukas as an editor of the file.
        Assert.Equal(["self"], store.State.Themes!.Single().Details!.Single().Citations.Single().By);
        Assert.Equal(CollaboratorPlan.CreditVersion, store.State.Themes!.Single().CreditedFor);

        // Lukas rewords the line: the detail keeps its wording and is quoted again, and keeps the user's credit.
        source.Snapshot = Files(("main.tex", Paper("Forty people took part."))) with { SourceUpdatedAt = DateTimeOffset.UtcNow };
        source.Authorship["main.tex"] = Authors([lukas], ("Forty people took part.", [(0, 19)]));
        await Drain(coordinator, force: true);
        var pilot = store.State.Themes!.Single().Details!.Single();
        Assert.Equal(("The pilot has 40 participants.", "Forty people took part."), (pilot.Text, pilot.Citations.Single().Quote));
        Assert.Equal(["self"], pilot.Citations.Single().By);

        // Lukas adds a result: that detail is his.
        source.Snapshot = Files(("main.tex", Paper("Forty people took part.", "Results show 15 percent gains."))) with { SourceUpdatedAt = DateTimeOffset.UtcNow };
        source.Authorship["main.tex"] = Authors([lukas], ("Forty people took part.", [(0, 19)]), ("Results show 15 percent gains.", [(0, 26)]));
        await Drain(coordinator, force: true);
        var theme = (await coordinator.StatusAsync(None)).Themes!.Single();
        Assert.Equal([("The pilot has 40 participants.", "You"), ("Results show 15 percent gains.", "Lukas Bolte")],
            theme.Details!.Select(detail => (detail.Text, string.Join(", ", detail.By!))));
        Assert.Equal((0.5, 0.5), (theme.YourShare, theme.Collaborators!.Single().Share));
    }

    [Fact]
    public async Task ExistingDetailsAreCreditedByLineOnceTheConnectorCanSay()
    {
        var lukas = new OverleafPerson("Lukas Bolte", "lukas@example.invalid", DateTimeOffset.UtcNow);
        // The history does not say who added the file (a copied project): its owner, the user, is known from the project list only.
        var source = new Source(Files(("main.tex", "\\documentclass{article}\n\\begin{document}\nForty participants joined.\n\\end{document}")))
        {
            Editors = [lukas], FileEditors = new Dictionary<string, IReadOnlyList<OverleafPerson>> { ["main.tex"] = [lukas] },
            ReadsFileAuthors = false, Owner = new("Synthetic", "synthetic@example.invalid"), Owned = true
        };
        var store = new MemoryStore(new(1, Credential, null, null, [Included()], []));
        var remote = new Remote();
        using var coordinator = Create(store, remote, source);
        await Drain(coordinator);
        // An older connector: credited to who edited the file (Lukas), and not yet by line.
        var card = store.State.Themes!.Single();
        Assert.Null(card.CreditedFor);
        Assert.All(card.Details!.SelectMany(detail => detail.Citations), citation => Assert.Equal(["e:lukas@example.invalid"], citation.By));

        // After a restart, as earlier versions stored it: the owner was never kept and no project list is read yet. Crediting
        // waits until the list is read, so the user's draft (text without an author) is theirs, not nobody's.
        store.State = store.State with { Projects = [.. store.State.Projects.Select(project => project with { Owner = null, Owned = null })] };
        source.Latest = null;
        // The current connector: each quote's lines are read once; the uploaded text is the user's (who added the file).
        // Crediting changes no detail, so it makes no summary, review or other model call.
        int summaries = remote.DetailSummaries.Count, reviews = remote.Reviews.Count, embeds = remote.Embeds.Count;
        source.ReadsFileAuthors = true;
        source.Authorship["main.tex"] = Authors([lukas], ("\\documentclass{article}", [(-1, 22)]), ("\\begin{document}", [(-1, 15)]),
            ("Forty participants joined.", [(-1, 20), (0, 3)]), ("\\end{document}", [(-1, 13)]));
        await Drain(coordinator);
        await Drain(coordinator);
        card = store.State.Themes!.Single();
        Assert.Equal(CollaboratorPlan.CreditVersion, card.CreditedFor);
        Assert.All(card.Details!.SelectMany(detail => detail.Citations), citation => Assert.Equal(["self"], citation.By));
        Assert.Equal(1, source.AuthorReads);
        Assert.Equal(1.0, (await coordinator.StatusAsync(None)).Themes!.Single().YourShare);
        Assert.Equal((summaries, reviews, embeds), (remote.DetailSummaries.Count, remote.Reviews.Count, remote.Embeds.Count));
        // A card summarized by the version that also hashed the credit is not summarized again either.
        var details = card.Details!;
        string legacy = ResearchEvidence.Hash(System.Text.Json.JsonSerializer.Serialize(details, ResearchJson.Options));
        Assert.True(DetailPlan.Matches(legacy, details));
        Assert.True(DetailPlan.Matches(DetailPlan.Fingerprint(details), details));
        Assert.False(DetailPlan.Matches(DetailPlan.Fingerprint([.. details.Skip(1)]), details));
        Assert.Equal(0, remote.Calls - remote.DetailRequests.Count);
    }

    [Fact]
    public async Task EachOverleafChangeRestsOnWhoEditedTheCitedFilesSinceMeldLastReadThem()
    {
        var first = DateTimeOffset.UtcNow.AddDays(-3);
        var source = new Source(Files(("main.tex", "\\documentclass{article}\n\\begin{document}\nForty participants.\n\\end{document}")))
        {
            EditTimes = new Dictionary<string, DateTimeOffset> { ["main.tex"] = first },
            Editors = [new("Synthetic", "synthetic@example.invalid"), new("Lukas Bolte", "lukas@example.invalid")],
            FileEditors = new Dictionary<string, IReadOnlyList<OverleafPerson>>
            {
                ["main.tex"] = [new("Synthetic", "synthetic@example.invalid", first), new("Lukas Bolte", "lukas@example.invalid", first.AddHours(-1))]
            }
        };
        var store = new MemoryStore(new(1, Credential, null, null, [Included()], []));
        var remote = new Remote();
        using var coordinator = Create(store, remote, source);
        await Drain(coordinator);
        // The first reading rests on everyone who wrote the file: the user and Lukas.
        var card = Assert.Single(store.State.Themes!);
        var change = Assert.Single(Assert.Single(store.State.Changelog!).Changes);
        Assert.Equal(["self", "e:lukas@example.invalid"], change.By);
        var page = await coordinator.ChangelogAsync(card.Id, null, null, 30, None);
        Assert.Equal(["You", "Lukas Bolte"], page.Entries.Single().Changes.Single().By);
        // The details now: every one rests on both, so both have 100% (shares need not add up to 100%).
        Assert.Equal(["self", "e:lukas@example.invalid"], store.State.Themes!.Single().Details!.Single().Citations.Single().By);
        var theme = (await coordinator.StatusAsync(None)).Themes!.Single();
        Assert.Equal((1, 1.0, 1.0), (theme.KnownDetails, theme.YourShare, theme.Collaborators!.Single(person => person.Name == "Lukas Bolte").Share));
        // Each detail says who it rests on.
        Assert.Equal(["You", "Lukas Bolte"], theme.Details!.Single().By);
        // Which details: the ones a click on a share lists.
        string detailId = store.State.Themes!.Single().Details!.Single().Id;
        Assert.Equal([detailId], theme.YourDetails);
        Assert.Equal([detailId], theme.Collaborators!.Single(person => person.Name == "Lukas Bolte").Details);

        // A later edit only by Lukas: the update rests on him alone.
        var second = DateTimeOffset.UtcNow.AddHours(-1);
        source.Snapshot = Files(("main.tex", "\\documentclass{article}\n\\begin{document}\nEighty participants.\n\\end{document}")) with { SourceUpdatedAt = second };
        source.EditTimes = new Dictionary<string, DateTimeOffset> { ["main.tex"] = second };
        source.FileEditors = new Dictionary<string, IReadOnlyList<OverleafPerson>>
        {
            ["main.tex"] = [new("Lukas Bolte", "lukas@example.invalid", second), new("Synthetic", "synthetic@example.invalid", first)]
        };
        await Drain(coordinator, force: true);
        var latest = store.State.Changelog!.OrderByDescending(entry => entry.At).First();
        Assert.All(latest.Changes, item => Assert.Equal(["e:lukas@example.invalid"], item.By));
        page = await coordinator.ChangelogAsync(card.Id, null, null, 30, None);
        Assert.All(page.Entries.First().Changes, item => Assert.Equal(["Lukas Bolte"], item.By));
        // The current details rest only on Lukas now: the user's share is computed over what is kept, not over history.
        theme = (await coordinator.StatusAsync(None)).Themes!.Single();
        Assert.Equal((0.0, 1.0), (theme.YourShare, theme.Collaborators!.Single(person => person.Name == "Lukas Bolte").Share));
    }

    [Fact]
    public void EmailChangesRestOnTheSendersOfTheCitedMessages()
    {
        var details = new DetailBullet[] { new("d1", "The pilot has 40 participants.", []) };
        var response = new DetailStepResponse([
            new(DetailRules.Add, null, "The pilot has 80 participants.", [new("m2", 1, 1, "We now have 80.")], []),
            new(DetailRules.Remove, "d1", null, [], [])], "Updated.", "chat");
        var senders = new Dictionary<string, string> { ["m1"] = "self", ["m2"] = "e:lukas@example.invalid" };
        var (_, changes) = DetailPlan.Apply(details, response, quote => new DetailSource(DetailSource.Gmail, "me@example.invalid", "t1", quote.Item, "Pilot", null),
            quote => senders.TryGetValue(quote.Item, out var key) ? [key] : [], ["self", "e:lukas@example.invalid"]);
        // A removal rests on everyone behind the step; an addition on whoever wrote what it cites.
        Assert.Equal([("remove", "self,e:lukas@example.invalid"), ("add", "e:lukas@example.invalid")],
            changes.Select(change => (change.Op, string.Join(",", change.By!))));

        var card = new ResearchTheme(Account.Id, new string('a', 32), [], null, null, DateTimeOffset.UtcNow,
            Collaborators: new CollaboratorSet("p", "a", [new("Lukas Bolte", ["lukas@example.invalid"], [Collaborators.From], 2, null, ["e:lukas@example.invalid"])], []));
        Assert.Equal(["You", "Lukas Bolte", "Others"], CollaboratorPlan.Names(["e:pat@example.invalid", "e:lukas@example.invalid", "self"], card));
        Assert.Null(CollaboratorPlan.Names(null, card));
    }
}
