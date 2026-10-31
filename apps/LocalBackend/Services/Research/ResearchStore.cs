using MeldResearch.Research;

namespace MeldResearch.LocalBackend.Services.Research;

internal sealed record LinkedCredential(MeldAccountResponse Account, string Token, DateTimeOffset ExpiresAt);
/// <summary>Obsolete device-code link state. Kept only so research state written by earlier versions still loads.</summary>
internal sealed record PendingDeviceLink(string DeviceCode, string Verifier, string VerificationUri, string UserCode,
    DateTimeOffset ExpiresAt, DateTimeOffset NextPollAt);
/// <summary>
/// A monitored project. Snapshot, Summary and Facts belong to the earlier whole-snapshot pipeline and are cleared when the
/// project is first processed file by file. Profile is the running summary; Processed maps each analyzed path to the hash
/// whose text the summary already reflects. Pending is a newer observed manifest still being worked through in
/// PendingOrder; together they form the persistent work queue, so processing resumes after a restart. PrioritizedAt is when
/// the user last included or resumed the project: the most recent choice is read and processed first.
/// </summary>
internal sealed record MonitoredProject(Guid AccountId, string ProjectId, string Name, bool Enabled, string ConsentVersion,
    ResearchSnapshot? Snapshot, string? Summary, IReadOnlyList<ResearchFact> Facts, IReadOnlyList<ResearchUpdate> Updates,
    DateTimeOffset? LastAttemptAt, DateTimeOffset? LastSuccessAt, string? ErrorCode, string? Error,
    string? Explanation, int UnprocessedFiles, int FailureCount,
    ProjectProfile? Profile = null, IReadOnlyDictionary<string, string>? Processed = null, ProjectManifest? Baseline = null,
    ProjectManifest? Pending = null, IReadOnlyList<string>? PendingOrder = null, DateTimeOffset? PendingSince = null,
    bool ImportComplete = false, DateTimeOffset? LastSweepAt = null, DateTimeOffset? NextAttemptAt = null,
    DateTimeOffset? PrioritizedAt = null);

/// <summary>An observed project's files by hash; the text of analyzed files is in the encrypted text store.</summary>
internal sealed record ProjectManifest(string Revision, DateTimeOffset ObservedAt, DateTimeOffset? SourceUpdatedAt,
    IReadOnlyList<ManifestFile> Files, IReadOnlyDictionary<string, DateTimeOffset>? EditTimes = null);
internal sealed record ManifestFile(string Path, string Sha256, long Bytes, bool Analyzed);
/// <summary>
/// A one-paragraph description of an Overleaf project from its main .tex file, made only when the user chose Summarize for
/// it (also for projects they have not included). Status: queued, ready or error. MainSha256 lets an unchanged main file
/// skip the model; the file text itself is not kept.
/// </summary>
internal sealed record ProjectSummaryState(Guid AccountId, string ProjectId, string Name, string Status, DateTimeOffset RequestedAt,
    string? MainPath = null, string? MainSha256 = null, string? Summary = null, DateTimeOffset? UpdatedAt = null,
    DateTimeOffset? SourceUpdatedAt = null, string? Error = null);

/// <summary>ResetEpoch records the last one-time research reset this installation applied (see ResearchCoordinator.ResetEpoch).</summary>
internal sealed record LocalResearchState(int Version, LinkedCredential? Credential, PendingDeviceLink? PendingLink,
    string? AccountError, IReadOnlyList<MonitoredProject> Projects, IReadOnlyList<LinkedCredential> PendingRevocations,
    IReadOnlyList<ProjectDecision>? Decisions = null, IReadOnlyList<ResearcherState>? Researchers = null, int ResetEpoch = 0,
    IReadOnlyList<ProjectSummaryState>? Summaries = null);

/// <summary>The user's active choice whether an Overleaf project is part of their private profile.</summary>
internal sealed record ProjectDecision(Guid AccountId, string ProjectId, bool Included, DateTimeOffset DecidedAt);

/// <summary>
/// The account's OpenAlex identity, kept only on this computer (ported from Project Meld's publication registry).
/// Status: searching, choice, unmatched, rejected, importing or ready. Version guards results against later decisions.
/// </summary>
internal sealed record ResearcherState(Guid AccountId, long Version, string Status, string SearchName,
    IReadOnlyList<ResearcherCandidate> Candidates, ResearcherCandidate? Researcher, IReadOnlyList<PublicationWork> Works,
    DateTimeOffset? UpdatedAt, DateTimeOffset? DueAt, string? LastError, int Failures = 0);

internal interface IResearchStore
{
    Task<LocalResearchState> ReadAsync(CancellationToken ct);
    Task<LocalResearchState> ChangeAsync(Func<LocalResearchState, LocalResearchState> change, CancellationToken ct);
    Task WriteTextAsync(string sha256, string text, CancellationToken ct);
    Task<string?> ReadTextAsync(string sha256, CancellationToken ct);
    void PruneTexts(IReadOnlySet<string> keep);
}

internal sealed class ResearchStore(ILocalVault vault) : IResearchStore, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<LocalResearchState> ReadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await vault.ReadAsync(ct); }
        finally { _gate.Release(); }
    }

    public async Task<LocalResearchState> ChangeAsync(Func<LocalResearchState, LocalResearchState> change, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = change(await vault.ReadAsync(ct));
            await vault.WriteAsync(state, ct);
            return state;
        }
        finally { _gate.Release(); }
    }
    public Task WriteTextAsync(string sha256, string text, CancellationToken ct) => vault.WriteTextAsync(sha256, text, ct);
    public Task<string?> ReadTextAsync(string sha256, CancellationToken ct) => vault.ReadTextAsync(sha256, ct);
    public void PruneTexts(IReadOnlySet<string> keep) => vault.PruneTexts(keep);
    public void Dispose() => _gate.Dispose();
}
