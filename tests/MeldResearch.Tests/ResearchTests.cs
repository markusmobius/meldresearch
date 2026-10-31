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

    [Fact]
    public void MetadataAndZipPackagingDoNotChangeTheContentRevision()
    {
        var snapshot = Snapshot("The pilot has 40 participants.");
        var renamed = snapshot with { ProjectName = "Renamed", ObservedAt = Now.AddDays(1), SourceUpdatedAt = Now.AddDays(1) };
        Assert.Equal(snapshot.Revision, ResearchEvidence.Revision(renamed.Files));
        Assert.Empty(ResearchEvidence.ChangedPaths(snapshot, renamed));
        Assert.False(ResearchEvidence.HasTextChanges(snapshot, renamed));
    }

    private static readonly ProjectProfile Profile = new("Synthetic pilot", "A synthetic pilot study.", ["40 participants."], ["pilot"]);
    private static ProfileStepRequest Step(bool initial, string? before, string? after) =>
        new("0123456789abcdef01234567", "Synthetic research", initial, initial ? null : Profile, new("main.tex", before, after));
    private static ProfileUpdate Update(string quote, EvidenceVersion version = EvidenceVersion.Current) =>
        new("Pilot doubled", "The pilot grew.", [new("main.tex", quote, version)]);

    [Fact]
    public void SummaryStepsAreGroundedInTheSuppliedFileAndInitialStepsReportNothing()
    {
        var update = Step(false, "The pilot has 40 participants.", "The pilot has 80 participants.");
        ProfileEvidence.ValidateRequest(update);
        ProfileEvidence.ValidateResponse(update, new(Profile, [Update("80 participants")], "Revised.", "chat"));
        Assert.Throws<ResearchFailure>(() => ProfileEvidence.ValidateResponse(update, new(Profile, [Update("120 participants")], "Invented.", "chat")));
        // A revision must rest on the current text, not only on what was removed.
        Assert.Throws<ResearchFailure>(() => ProfileEvidence.ValidateResponse(update,
            new(Profile, [Update("40 participants", EvidenceVersion.Previous)], "Old only.", "chat")));
        var deleted = Step(false, "The pilot has 40 participants.", null);
        ProfileEvidence.ValidateResponse(deleted, new(Profile, [Update("40 participants", EvidenceVersion.Previous)], "Removed.", "chat"));

        var initial = Step(true, null, "The pilot has 40 participants.");
        ProfileEvidence.ValidateRequest(initial);
        Assert.Throws<ResearchFailure>(() => ProfileEvidence.ValidateResponse(initial, new(Profile, [Update("40 participants")], "Baseline.", "chat")));
        Assert.Throws<ResearchFailure>(() => ProfileEvidence.ValidateRequest(Step(true, "before", "after")));
        Assert.Throws<ResearchFailure>(() => ProfileEvidence.ValidateRequest(Step(false, "before", "after") with { Profile = null }));
        Assert.Throws<ResearchFailure>(() => ProfileEvidence.ValidateRequest(Step(true, null, "x") with { File = new("refs.bib", null, "x") }));
    }

    [Fact]
    public void SummariesAreLimitedToFiveThousandCharactersAsJson()
    {
        Assert.True(ProfileEvidence.ValidProfile(Profile));
        var large = Profile with { Description = new string('d', 1500), Contributions = Enumerable.Repeat(new string('c', 400), 12).ToArray() };
        Assert.True(ProfileEvidence.Length(large) > ProfileEvidence.MaxProfileCharacters);
        Assert.False(ProfileEvidence.ValidProfile(large));
    }

    [Fact]
    public void FilesAreOrderedByEarliestEditWithUnknownTimesFirstAndDeletionsLast()
    {
        var manifest = new ProjectManifest("revision", Now, null,
        [
            new("main.tex", new string('a', 64), 1, true), new("sections/b.tex", new string('b', 64), 1, true),
            new("sections/a.tex", new string('c', 64), 1, true), new("old/copy.tex", new string('d', 64), 1, true),
            new("refs.bib", new string('e', 64), 1, false)
        ], new Dictionary<string, DateTimeOffset> { ["sections/b.tex"] = Now.AddDays(-2), ["sections/a.tex"] = Now.AddDays(-1) });
        var processed = new Dictionary<string, string> { ["sections/a.tex"] = new string('c', 64), ["gone.tex"] = new string('f', 64) };
        var work = ProcessingPlan.Work(processed, manifest);
        Assert.Equal(["main.tex", "old/copy.tex", "sections/b.tex", "gone.tex"], ProcessingPlan.Order(work, manifest));
        Assert.DoesNotContain("refs.bib", work);
    }

    [Fact]
    public async Task StoresSourceTextsEncryptedByHashAndPrunesUnreferencedOnes()
    {
        using var vault = new LocalVault(_temporary.Path, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        string text = "PRIVATE-SOURCE-SENTINEL", hash = ResearchEvidence.Hash(text), other = ResearchEvidence.Hash("other");
        await vault.WriteTextAsync(hash, text, CancellationToken.None);
        await vault.WriteTextAsync(other, "other", CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentException>(() => vault.WriteTextAsync(hash, "tampered", CancellationToken.None));
        string file = Path.Combine(_temporary.Path, "texts", hash + ".protected");
        Assert.DoesNotContain(text, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)));
        Assert.Equal(text, await vault.ReadTextAsync(hash, CancellationToken.None));
        // A blob moved to another hash's name fails authentication instead of returning the wrong text.
        File.Copy(file, Path.Combine(_temporary.Path, "texts", other + ".protected"), true);
        Assert.Equal("research_store_damaged", (await Assert.ThrowsAsync<ResearchFailure>(() => vault.ReadTextAsync(other, CancellationToken.None))).Code);
        vault.PruneTexts(new HashSet<string> { hash });
        Assert.Null(await vault.ReadTextAsync(other, CancellationToken.None));
        Assert.Equal(text, await vault.ReadTextAsync(hash, CancellationToken.None));
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
