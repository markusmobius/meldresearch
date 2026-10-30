using MeldResearch.Research;

namespace MeldResearch.LocalBackend.Services.Research;

internal sealed record LinkedCredential(MeldAccountResponse Account, string Token, DateTimeOffset ExpiresAt);
/// <summary>Obsolete device-code link state. Kept only so research state written by earlier versions still loads.</summary>
internal sealed record PendingDeviceLink(string DeviceCode, string Verifier, string VerificationUri, string UserCode,
    DateTimeOffset ExpiresAt, DateTimeOffset NextPollAt);
internal sealed record MonitoredProject(Guid AccountId, string ProjectId, string Name, bool Enabled, string ConsentVersion,
    ResearchSnapshot? Snapshot, string? Summary, IReadOnlyList<ResearchFact> Facts, IReadOnlyList<ResearchUpdate> Updates,
    DateTimeOffset? LastAttemptAt, DateTimeOffset? LastSuccessAt, string? ErrorCode, string? Error,
    string? Explanation, int UnprocessedFiles, int FailureCount);
internal sealed record LocalResearchState(int Version, LinkedCredential? Credential, PendingDeviceLink? PendingLink,
    string? AccountError, IReadOnlyList<MonitoredProject> Projects, IReadOnlyList<LinkedCredential> PendingRevocations,
    IReadOnlyList<ProjectDecision>? Decisions = null, IReadOnlyList<ResearcherState>? Researchers = null);

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
    public void Dispose() => _gate.Dispose();
}
