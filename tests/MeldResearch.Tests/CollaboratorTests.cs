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
