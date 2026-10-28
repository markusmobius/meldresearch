using System.Text;
using MeldResearch.LocalBackend.Services.Research;
using MeldResearch.Research;

namespace MeldResearch.Tests;

public sealed class ResearchTests : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public void Dispose() => _temporary.Dispose();

    internal static ResearchSnapshot Snapshot(string text, string path = "main.tex")
    {
        var file = new ResearchFile(path, ResearchEvidence.Hash(text), Encoding.UTF8.GetByteCount(text), text);
        return new("0123456789abcdef01234567", "Synthetic research", ResearchEvidence.Revision([file]), Now, null, [file]);
    }
    private static ResearchChange Add(string quote) => new(FactOperation.Add, null, "Synthetic pilot",
        quote, ["research"], [new("main.tex", quote, EvidenceVersion.Current)]);

    [Fact]
    public void MetadataAndZipPackagingDoNotChangeTheContentRevision()
    {
        var snapshot = Snapshot("The pilot has 40 participants.");
        var renamed = snapshot with { ProjectName = "Renamed", ObservedAt = Now.AddDays(1), SourceUpdatedAt = Now.AddDays(1) };
        Assert.Equal(snapshot.Revision, ResearchEvidence.Revision(renamed.Files));
        Assert.Empty(ResearchEvidence.ChangedPaths(snapshot, renamed));
        Assert.False(ResearchEvidence.HasTextChanges(snapshot, renamed));
    }

    [Fact]
    public void InitialImportIsGroundedAndDoesNotInventAResearchEventDate()
    {
        var snapshot = Snapshot("The pilot has 40 participants.");
        var request = new AnalyzeResearchRequest(null, snapshot, null, []);
        var result = new AnalyzeResearchResponse(new("An existing pilot.", "Initial baseline.", [Add(snapshot.Files[0].Text!)]),
            "synthetic-chat", "synthetic-embedding", [new float[] { 1, 0 }]);
        var reduced = ResearchReducer.Apply(request, result, []);
        var update = Assert.Single(reduced.Updates);
        Assert.Equal(ResearchUpdateKind.InitialImport, update.Kind);
        Assert.Equal(snapshot.ObservedAt, update.ObservedAt);
        Assert.Null(update.SourceUpdatedAt);
        Assert.Null(update.PreviousRevision);
        Assert.Equal(snapshot.ProjectId, update.ProjectId);
    }

    [Fact]
    public void DeltaRevisesExistingKnowledgeAndPreservesItsPreviousSummary()
    {
        var previous = Snapshot("The pilot has 40 participants.");
        var current = Snapshot("The pilot has 80 participants.");
        var fact = new ResearchFact(Guid.NewGuid(), "Pilot", previous.Files[0].Text!, [],
            [new("main.tex", previous.Files[0].Text!, EvidenceVersion.Current)], previous.Revision, "embedding", [1f, 0f]);
        var context = new ResearchFactContext(fact.Id, fact.Title, fact.Summary, [], fact.Evidence);
        var request = new AnalyzeResearchRequest(previous, current, "A pilot.", [context]);
        var change = new ResearchChange(FactOperation.Revise, fact.Id, "Larger pilot", current.Files[0].Text!, [],
            [new("main.tex", current.Files[0].Text!, EvidenceVersion.Current)]);
        var result = ResearchReducer.Apply(request, new(new("A larger pilot.", "Participant count revised.", [change]),
            "chat", "embedding", [new float[] { 0, 1 }]), [fact]);
        Assert.Equal(fact.Id, Assert.Single(result.Facts).Id);
        Assert.Equal(fact.Summary, Assert.Single(result.Updates).PreviousSummary);
        Assert.Equal(current.Files[0].Text, result.Facts[0].Summary);
    }

    [Fact]
    public void RemovalCannotLeaveSupersededKnowledgeInTheCurrentFacts()
    {
        var previous = Snapshot("The intervention improves outcomes.");
        var current = Snapshot("The earlier improvement claim was withdrawn.");
        var id = Guid.NewGuid();
        var fact = new ResearchFact(id, "Improvement", previous.Files[0].Text!, [],
            [new("main.tex", previous.Files[0].Text!, EvidenceVersion.Current)], previous.Revision, "embedding", [1f]);
        var request = new AnalyzeResearchRequest(previous, current, null,
            [new(id, fact.Title, fact.Summary, [], fact.Evidence)]);
        var change = new ResearchChange(FactOperation.Remove, id, "Claim withdrawn", current.Files[0].Text!, [],
            [new("main.tex", current.Files[0].Text!, EvidenceVersion.Current)]);
        var result = ResearchReducer.Apply(request, new(new("The claim is withdrawn.", "Correction.", [change]), "chat", "embedding", []), [fact]);
        Assert.Empty(result.Facts);
        Assert.Equal(ResearchUpdateKind.Removal, Assert.Single(result.Updates).Kind);
    }

    [Fact]
    public void RejectsInventedQuotesAndChangesNotSupportedByTheDelta()
    {
        var snapshot = Snapshot("Synthetic source text.");
        var request = new AnalyzeResearchRequest(null, snapshot, null, []);
        Assert.Throws<ResearchFailure>(() => ResearchEvidence.ValidateAnalysis(request,
            new("Summary.", "Explanation.", [Add("An invented result.")])));
        Assert.Throws<ResearchFailure>(() => ResearchEvidence.ValidateAnalysis(request with { Previous = snapshot },
            new("Summary.", "Explanation.", [Add(snapshot.Files[0].Text!)])));
    }

    [Fact]
    public void ValidatesExactUtf8ByteLimitRatherThanCharacterCount()
    {
        var text = new string('x', ResearchEvidence.MaxFileTextBytes);
        ResearchEvidence.ValidateSnapshot(Snapshot(text));
        Assert.Throws<ResearchFailure>(() => ResearchEvidence.ValidateSnapshot(Snapshot(text + "x")));
        Assert.Throws<ResearchFailure>(() => ResearchEvidence.ValidateSnapshot(Snapshot(new string('\u00e9', ResearchEvidence.MaxFileTextBytes))));
    }

    [Fact]
    public void RejectsEmbeddingShapeErrorsAndNonFiniteNumbers()
    {
        Assert.Throws<ResearchFailure>(() => ResearchEvidence.ValidateEmbeddings([[float.NaN]], 1));
        Assert.Throws<ResearchFailure>(() => ResearchEvidence.ValidateEmbeddings([[1f], [1f, 2f]], 2));
        Assert.Throws<ResearchFailure>(() => ResearchEvidence.ValidateEmbeddings([[1f]], 2));
    }

    [Fact]
    public async Task EncryptsCredentialsAndResearchAndRejectsTamperingWithoutResetting()
    {
        byte[] key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        using var vault = new LocalVault(_temporary.Path, key);
        var account = new MeldAccountResponse(Guid.NewGuid(), "Synthetic Researcher", "synthetic@example.invalid");
        var state = new LocalResearchState(1, new(account, "PRIVATE-TEST-SENTINEL", Now.AddDays(1)), null, null, [], []);
        await vault.WriteAsync(state, CancellationToken.None);
        byte[] encrypted = await File.ReadAllBytesAsync(Path.Combine(_temporary.Path, "research.protected"));
        Assert.DoesNotContain("PRIVATE-TEST-SENTINEL", Encoding.UTF8.GetString(encrypted));
        Assert.DoesNotContain(account.Email, Encoding.UTF8.GetString(encrypted));
        Assert.Equal(state.Credential, (await vault.ReadAsync(CancellationToken.None)).Credential);
        encrypted[^1] ^= 1;
        await File.WriteAllBytesAsync(Path.Combine(_temporary.Path, "research.protected"), encrypted);
        var error = await Assert.ThrowsAsync<ResearchFailure>(() => vault.ReadAsync(CancellationToken.None));
        Assert.Equal("research_store_damaged", error.Code);
        Assert.Equal(encrypted, await File.ReadAllBytesAsync(Path.Combine(_temporary.Path, "research.protected")));
    }
}
