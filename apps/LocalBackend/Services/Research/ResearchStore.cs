using System.Text.Json.Serialization;
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
/// the user last included or resumed the project: the most recent choice is read and processed first. Source is null for an
/// Overleaf project and "gmail" for a Gmail workstream, whose Parent is the Gmail address it comes from; a workstream has a
/// profile and Updates like a project but no files, and joins research cards the same way. Paper is the title and abstract
/// read from the main LaTeX document of the Baseline revision PaperFor (an Overleaf project's anchor for its card).
/// </summary>
internal sealed record MonitoredProject(Guid AccountId, string ProjectId, string Name, bool Enabled, string ConsentVersion,
    ResearchSnapshot? Snapshot, string? Summary, IReadOnlyList<ResearchFact> Facts, IReadOnlyList<ResearchUpdate> Updates,
    DateTimeOffset? LastAttemptAt, DateTimeOffset? LastSuccessAt, string? ErrorCode, string? Error,
    string? Explanation, int UnprocessedFiles, int FailureCount,
    ProjectProfile? Profile = null, IReadOnlyDictionary<string, string>? Processed = null, ProjectManifest? Baseline = null,
    ProjectManifest? Pending = null, IReadOnlyList<string>? PendingOrder = null, DateTimeOffset? PendingSince = null,
    bool ImportComplete = false, DateTimeOffset? LastSweepAt = null, DateTimeOffset? NextAttemptAt = null,
    DateTimeOffset? PrioritizedAt = null, string? SyncETag = null, string? SyncedFor = null, DateTimeOffset? LastSyncedAt = null,
    string? Source = null, string? Parent = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PaperInfo? Paper = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PaperFor = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PendingTree>? PendingTrees = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string>? Partial = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? ExcludedDocs = null)
{
    /// <summary>An Overleaf project (Source null or "overleaf"): read from Overleaf file by file. Other sources are not swept.</summary>
    public bool IsOverleaf => Source is null or ResearchSources.Overleaf;
}

/// <summary>
/// This computer's key to the account's encrypted sync (kept only inside the encrypted research state). ProfileETag and
/// ProfileSyncedFor track the synced profile blob like SyncETag and SyncedFor track each project's bundle.
/// </summary>
internal sealed record SyncState(Guid AccountId, string DataKey, DateTimeOffset UnlockedAt, string? ProfileETag = null,
    string? ProfileSyncedFor = null, DateTimeOffset? LastPushAt = null, DateTimeOffset? LastPullAt = null,
    string? KeyETag = null, DateTimeOffset? KeyCreatedAt = null);

/// <summary>
/// A root tree to fold in the current ingest cycle (OVERLEAF.md §6-§7): its root, date, the whole tree in reading order
/// (Order, for the outline) and the documents to send (Send). Partial on the project maps a long document being folded in
/// parts to "hash:first line of the last part folded".
/// </summary>
internal sealed record PendingTree(string Root, DateTimeOffset Date, IReadOnlyList<string> Order, IReadOnlyList<string> Send);

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

/// <summary>
/// A research card in the private profile: one piece of research (a paper with its drafts, slides and material, and the email
/// about it). Profile describes the card's anchor, its main paper (ThemePlan.AnchorOf; Anchor when the user chose it);
/// MergedFrom maps the anchor to the fingerprint it was described from, so a card is described again only when its anchor
/// or the anchor's paper or summary changed. Other members are the card's materials. Pinned members were placed by the user
/// and are never moved by automatic regrouping. Reason says why the last member was grouped here. Skipped: the user marked
/// the card as not their own research (for example helping a family member): it leaves the profile and the feed, its
/// Overleaf projects pause, and new email of the same kind is skipped too; SkipGrouped once it was compared (loosely) with
/// the other skipped cards.
/// </summary>
internal sealed record ResearchTheme(Guid AccountId, string Id, IReadOnlyList<string> Members, ProjectProfile? Profile,
    IReadOnlyDictionary<string, string>? MergedFrom, DateTimeOffset CreatedAt, IReadOnlyList<string>? Pinned = null,
    string? Blurb = null, string? BlurbFor = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Anchor = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Skipped = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? SkipGrouped = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<DetailBullet>? Details = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SummaryFor = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? MatchPending = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null);

/// <summary>
/// One entry of a research project's changelog (OVERLEAF.md §8): a source that updated it (an Overleaf root tree, an email
/// thread's new messages), a removal, or the user's change. Changes is what it did to the details; Key merges the parts of
/// one root tree of one cycle into one entry.
/// </summary>
internal sealed record ChangelogEntry(Guid Id, Guid AccountId, string CardId, DateTimeOffset At, string Kind, string Title,
    string? ProjectId, IReadOnlyList<string> Documents, IReadOnlyList<DetailChange> Changes, string? Key, DateTimeOffset RecordedAt)
{
    public const string Overleaf = "overleaf", Gmail = "gmail", Removed = "removed", User = "user", Merge = "merge";
}

/// <summary>What one update did to one detail: added, revised (Before and After) or removed it.</summary>
internal sealed record DetailChange(string Op, string DetailId, string? Before, string? After);

/// <summary>
/// An Overleaf file the user removed as a source of a research project (OVERLEAF.md §10). It waits in Private updates until
/// the user adds it to another project (TargetCardId: folded there once) or skips it; its own project ignores it meanwhile.
/// </summary>
internal sealed record PendingFile(Guid AccountId, string ProjectId, string ProjectName, string Path, string Sha256, string FromCardId,
    DateTimeOffset RemovedAt, string? TargetCardId = null);

/// <summary>ResetEpoch records the last one-time research reset this installation applied (see ResearchCoordinator.ResetEpoch).</summary>
internal sealed record LocalResearchState(int Version, LinkedCredential? Credential, PendingDeviceLink? PendingLink,
    string? AccountError, IReadOnlyList<MonitoredProject> Projects, IReadOnlyList<LinkedCredential> PendingRevocations,
    IReadOnlyList<ProjectDecision>? Decisions = null, IReadOnlyList<ResearcherState>? Researchers = null, int ResetEpoch = 0,
    IReadOnlyList<ProjectSummaryState>? Summaries = null, IReadOnlyList<ResearchTheme>? Themes = null,
    string? ThemesRegroupedFor = null, SyncState? Sync = null, IReadOnlyList<GmailAccountState>? GmailAccounts = null,
    string? DeviceId = null, IReadOnlyList<GmailThreadRecord>? GmailThreads = null, int GmailVersion = 0, int ThemesVersion = 0,
    IReadOnlyList<ChangelogEntry>? Changelog = null, IReadOnlyList<PendingFile>? PendingFiles = null,
    IReadOnlyList<string>? SkippedProjects = null);

/// <summary>
/// A Gmail account connected for read-only access. RefreshToken is Google's long-lived grant; it is kept only in this encrypted
/// state and in the end-to-end encrypted sync (never in backups or logs, never sent to Meld's server). ChangedAt orders
/// connections across computers; a removed account stays as a tombstone (Removed, no token) so other computers drop it too.
/// Status: connected, reconnect (Google withdrew the grant, for example after 7 days in testing mode) or error. Threads is
/// how many threads the user wrote in during the import window at the last check. ImportVersion is the thread analysis
/// the last import used (an older one imports again). LeaseBy/LeaseUntil: the computer importing now; others wait, so mail
/// is not analyzed twice. Processed belongs to the earlier per-workstream analysis and is only kept so older state loads.
/// </summary>
internal sealed record GmailAccountState(Guid AccountId, string Address, string? RefreshToken, string ConsentVersion,
    DateTimeOffset ChangedAt, bool Removed = false, string Status = "connected", string? Error = null, int? Threads = null,
    DateTimeOffset? CheckedAt = null, string? HistoryId = null, DateTimeOffset? LastImportAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<GmailThreadState>? Processed = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LeaseBy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? LeaseUntil = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ImportVersion = null);

/// <summary>Obsolete per-workstream analysis record; kept only so state written by an earlier version still loads.</summary>
internal sealed record GmailThreadState(string Id, string HistoryId, string LastMessageId, DateTimeOffset LastSentAt,
    string? WorkstreamId, DateTimeOffset ProcessedAt);

/// <summary>
/// One Gmail thread as Meld recorded it: a list of preview bullets that grows as mail arrives. Status: assigned (a source of
/// research card CardId: its mail up to FoldedMessageId is folded into the card's details), pending (work, but the user
/// decides where it goes: Private updates), skipped (not work; looked at again only when new mail arrives) or dismissed
/// (the user chose to ignore it for good). StreamId belongs to the earlier email streams and is no longer set.
/// HistoryId is Gmail's version of the thread when it was last analyzed ("" while a long thread is analyzed in parts) and
/// LastMessageId/LastSentAt the last message analyzed, so only later messages are new. Headline and NewsAt are set when the
/// thread made newsworthy progress (it then appears in the Updates feed); Suggested names a project for a pending thread.
/// </summary>
internal sealed record GmailThreadRecord(Guid AccountId, string Address, string Id, string Subject, string Status, string HistoryId,
    string LastMessageId, DateTimeOffset LastSentAt, DateTimeOffset UpdatedAt, IReadOnlyList<GmailBullet> Bullets,
    string? StreamId = null, string? Headline = null, DateTimeOffset? NewsAt = null, string? Suggested = null, string? Explanation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CardId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FoldedMessageId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? FoldedSentAt = null)
{
    public const string Assigned = "assigned", Pending = "pending", Skipped = "skipped", Dismissed = "dismissed";
}

/// <summary>One fact from a thread with its quoted evidence; Newsworthy when it was part of a step forward.</summary>
internal sealed record GmailBullet(Guid Id, string Text, IReadOnlyList<ResearchCitation> Evidence, DateTimeOffset SentAt, DateTimeOffset AddedAt,
    bool Newsworthy);

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
