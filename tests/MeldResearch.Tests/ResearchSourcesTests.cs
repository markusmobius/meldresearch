using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MeldResearch.LocalBackend.Services.Research;
using MeldResearch.LocalBackend.Services.Updates;
using MeldResearch.Research;
using static MeldResearch.Tests.ResearchCoordinatorTests;

namespace MeldResearch.Tests;

/// <summary>Research card members from sources other than Overleaf (Gmail workstreams): the source-generic foundation.</summary>
public sealed class ResearchSourcesTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly string WorkstreamId = "gmail-" + new string('a', 32);
    private const string GmailAddress = "markus@example.invalid";

    private static MonitoredProject Workstream(string id, string title = "Grant renewal")
    {
        var now = DateTimeOffset.UtcNow;
        var update = new ResearchUpdate(Guid.NewGuid(), id, title, ResearchUpdateKind.Change, "Budget agreed", "The budget was agreed.", now, now,
            null, "email-1", Guid.NewGuid(), [new ResearchCitation("Re: grant renewal", "We agreed on the budget.", EvidenceVersion.Current,
                new EmailEvidence("18c2f0a1b2c3d4e5", "lukas@example.invalid", now.AddHours(-2)))], null);
        return new MonitoredProject(Account.Id, id, title, true, ResearchEvidence.ConsentVersion, null, null, [], [update],
            null, now, null, null, null, 0, 0, Profile: new ProjectProfile(title, "A grant renewal discussed by email.", ["Drafted the budget."], ["grants"]),
            ImportComplete: true, Source: ResearchSources.Gmail, Parent: GmailAddress);
    }

    private static ResearchCoordinator Coordinator(MemoryStore store, Remote remote, Source source) =>
        new(store, remote, source, new(new Uri("https://meld.example.invalid"), false, 300), NullLogger<ResearchCoordinator>.Instance, TimeProvider.System);

    [Fact]
    public void MemberIdsTellOverleafProjectsAndGmailWorkstreamsApart()
    {
        Assert.True(ResearchSources.IsMemberId(ProjectId));
        Assert.True(ResearchSources.IsMemberId(WorkstreamId));
        Assert.Equal((ResearchSources.Overleaf, ResearchSources.Gmail), (ResearchSources.SourceOf(ProjectId), ResearchSources.SourceOf(WorkstreamId)));
        string created = ResearchSources.NewGmailWorkstreamId();
        Assert.True(ResearchSources.IsGmailWorkstreamId(created));
        Assert.NotEqual(created, ResearchSources.NewGmailWorkstreamId());
        Assert.False(ResearchSources.IsMemberId("gmail-" + new string('A', 32)));
        Assert.False(ResearchSources.IsMemberId("gmail-../../x"));
        Assert.False(ResearchSources.IsGmailWorkstreamId(ProjectId));
    }

    [Fact]
    public void EmailEvidenceIsOptionalAndOverleafCitationsAndBundlesKeepTheirJson()
    {
        var file = new ResearchCitation("main.tex", "Forty participants.", EvidenceVersion.Current);
        string json = JsonSerializer.Serialize(file, ResearchJson.Options);
        Assert.DoesNotContain("email", json);
        var email = Workstream(WorkstreamId).Updates[0].Evidence[0];
        var copy = JsonSerializer.Deserialize<ResearchCitation>(JsonSerializer.Serialize(email, ResearchJson.Options), ResearchJson.Options)!;
        Assert.Equal(email, copy);

        // Overleaf projects and profiles without workstreams serialize as before, so their sync fingerprints do not change.
        var overleaf = Included() with { Profile = new ProjectProfile("T", "D", [], []) };
        string bundle = JsonSerializer.Serialize(SyncBundles.Project(overleaf, new Dictionary<string, string>(), DateTimeOffset.UnixEpoch), ResearchJson.Options);
        Assert.DoesNotContain("\"source\"", bundle);
        Assert.DoesNotContain("\"parent\"", bundle);
        var state = new LocalResearchState(1, Credential, null, null, [overleaf], []);
        Assert.DoesNotContain("workstreams", JsonSerializer.Serialize(SyncBundles.Profile(state, Account.Id, DateTimeOffset.UnixEpoch), ResearchJson.Options));
    }

    [Fact]
    public async Task ABackupKeepsWorkstreamsAndRejectsAProjectFileWithTheWrongSource()
    {
        var updates = new UpdateOptions(false, "test", "development", null, null, null, null, new Uri("https://meld.example.invalid"), false,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
        var store = new MemoryStore(new(1, Credential, null, null, [Workstream(WorkstreamId)], []));
        using var coordinator = Coordinator(store, new Remote(), new Source(Files(("main.tex", "Main text."))));
        var archive = new ResearchArchive(store, coordinator, updates, TimeProvider.System, NullLogger<ResearchArchive>.Instance);
        var zip = (await archive.ExportAsync(None)).Zip;

        var freshStore = new MemoryStore(new(1, Credential, null, null, [], []));
        using var fresh = Coordinator(freshStore, new Remote(), new Source(Files(("main.tex", "Main text."))));
        var restore = new ResearchArchive(freshStore, fresh, updates, TimeProvider.System, NullLogger<ResearchArchive>.Instance);
        Assert.Equal(1, (await restore.ImportAsync(new MemoryStream(zip), None)).Projects);
        var restored = freshStore.State.Projects.Single();
        Assert.Equal((WorkstreamId, ResearchSources.Gmail, GmailAddress, true), (restored.ProjectId, restored.Source, restored.Parent, restored.Enabled));

        // A Gmail workstream file renamed to an Overleaf id is refused.
        using var copy = new MemoryStream();
        copy.Write(zip);
        using (var edited = new System.IO.Compression.ZipArchive(copy, System.IO.Compression.ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = edited.GetEntry($"projects/{WorkstreamId}.json")!;
            string text;
            using (var reader = new StreamReader(entry.Open())) text = reader.ReadToEnd();
            entry.Delete();
            using var writer = new StreamWriter(edited.CreateEntry($"projects/{ProjectId}.json").Open());
            writer.Write(text.Replace(WorkstreamId, ProjectId));
        }
        var other = new MemoryStore(new(1, Credential, null, null, [], []));
        using var otherCoordinator = Coordinator(other, new Remote(), new Source(Files(("main.tex", "Main text."))));
        var refused = await Assert.ThrowsAsync<ResearchFailure>(() =>
            new ResearchArchive(other, otherCoordinator, updates, TimeProvider.System, NullLogger<ResearchArchive>.Instance)
                .ImportAsync(new MemoryStream(copy.ToArray()), None));
        Assert.Equal("invalid_export", refused.Code);
    }
}
