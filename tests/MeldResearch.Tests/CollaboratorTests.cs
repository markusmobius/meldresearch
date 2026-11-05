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
            {"name":null,"email":"not an address"},{"name":"Sid Satya","email":null}]}
            """));
        var read = await OverleafEvidenceSource.ParseHistoryAsync(history, None);
        Assert.Equal([new OverleafPerson("Lukas Bolte", "l.bolte@example.invalid", DateTimeOffset.FromUnixTimeMilliseconds(1767225600000)), new("Sid Satya", null)],
            read.Editors);
        Assert.Single(read.EditTimes!);
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
        var card = Assert.Single(store.State.Themes!);
        Assert.Equal([("Lukas Bolte", "author,editor"), ("Sid Satya", "author,editor"), ("Tanya Rosenblat", "owner")],
            card.Collaborators!.People.Select(person => (person.Name, string.Join(",", person.Kinds))).OrderBy(item => item.Name));
        await Drain(coordinator, force: true);
        Assert.Single(remote.People);

        // A thread on the project: a known person under another address joins them without a call; the office is new.
        var at = DateTimeOffset.UtcNow;
        GmailThreadRecord Thread(string id, params MailPerson[] people) => new(Account.Id, "synthetic@example.invalid", id, "Pilot", GmailThreadRecord.Assigned,
            "1", "m1", at, at, [], CardId: card.Id, FoldedMessageId: "m1", People: people);
        store.State = store.State with { GmailThreads = [Thread("t1", new MailPerson("Lukas Bolte", "lukas@example.invalid", 2, 1, 0, at))] };
        await Drain(coordinator);
        Assert.Single(remote.People);
        var lukas = store.State.Themes!.Single().Collaborators!.People.Single(person => person.Name == "Lukas Bolte");
        Assert.Equal(["l.bolte@uni.example.invalid", "lukas@example.invalid"], lukas.Emails.Order());
        Assert.Equal(4, lukas.Count);

        // More mail from known people changes counts only (no call); someone new makes one call, which drops a role address.
        store.State = store.State with { GmailThreads = [Thread("t1", new MailPerson("Lukas Bolte", "lukas@example.invalid", 5, 1, 0, at))] };
        await Drain(coordinator);
        Assert.Single(remote.People);
        store.State = store.State with
        {
            GmailThreads = [Thread("t1", new MailPerson("Lukas Bolte", "lukas@example.invalid", 5, 1, 0, at), new MailPerson(null, "research-office@example.invalid", 0, 0, 1, at),
                new MailPerson("Grace Hopper", "grace@example.invalid", 1, 0, 0, at))]
        };
        await Drain(coordinator);
        Assert.Equal(2, remote.People.Count);
        var status = (await coordinator.StatusAsync(None)).Themes!.Single().Collaborators!;
        Assert.Equal(["Grace Hopper", "Lukas Bolte", "Sid Satya", "Tanya Rosenblat"], status.Select(person => person.Name).Order());
        Assert.DoesNotContain(status, person => person.Emails.Contains("research-office@example.invalid"));
        Assert.Equal(7, status.Single(person => person.Name == "Lukas Bolte").Count);
        await Drain(coordinator);
        Assert.Equal(2, remote.People.Count);
    }

    [Fact]
    public async Task ProjectsReadBeforeCollaboratorsReadTheirEditorsOnce()
    {
        var source = new Source(Files(("main.tex", "\\documentclass{article}\n\\begin{document}\nText.\n\\end{document}")));
        var store = new MemoryStore(new(1, Credential, null, null, [Included()], []));
        var remote = new Remote();
        using var coordinator = Create(store, remote, source);
        await Drain(coordinator);
        // As an earlier version stored it: no editors.
        store.State = store.State with { Projects = [.. store.State.Projects.Select(project => project with { Editors = null })] };
        source.Editors = [new("Lukas Bolte", "lukas@example.invalid")];
        int reads = source.HistoryReads;
        await Drain(coordinator, force: true);
        await Drain(coordinator, force: true);
        Assert.Equal(reads + 1, source.HistoryReads);
        Assert.Equal("lukas@example.invalid", store.State.Projects.Single().Editors!.Single().Email);
        Assert.Equal("Lukas Bolte", store.State.Themes!.Single().Collaborators!.People.Single().Name);
    }
}
