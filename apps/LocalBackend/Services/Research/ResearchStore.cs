using System.Text.Json.Serialization;
using MeldResearch.LocalBackend.Contracts;
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
/// Owner is who owns the project on Overleaf (Owned: the user does, so it names the user) and Editors who edited it, from
/// Overleaf's history: with the paper's authors they are the project's collaborators. EditorsFor is the way of reading
/// editors (CollaboratorPlan.EditorsVersion) they were read by; until it is current, each check reads the history again (an
/// older connector or a failed read does not count). PlacedAt: the user moved the project to its card then; the cycle reading
/// it there shows its updates in the feed from then (cleared when that cycle finishes).
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? ExcludedDocs = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OverleafPerson? Owner = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Owned = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<OverleafPerson>? Editors = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EditorsFor = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? PlacedAt = null)
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

/// <summary>
/// An observed project's files by hash; the text of analyzed files is in the encrypted text store. FileEditors: who edited
/// each file and when they last did (from Overleaf's history), to say who the updates from this version rest on; FileAdders:
/// who added each file (credited with text that was there when it was added, such as an upload or a copy).
/// </summary>
internal sealed record ProjectManifest(string Revision, DateTimeOffset ObservedAt, DateTimeOffset? SourceUpdatedAt,
    IReadOnlyList<ManifestFile> Files, IReadOnlyDictionary<string, DateTimeOffset>? EditTimes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, IReadOnlyList<OverleafPerson>>? FileEditors = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, OverleafPerson>? FileAdders = null);
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
/// the other skipped cards. ReviewedFor and ReviewedAt record the last review of the details (OVERLEAF.md §8a): which
/// details it saw, and when, so a card is reviewed at most once per ResearchCoordinator.ReviewInterval and only after it
/// changed. ShortName is the short name (2-5 words) the summary step writes with the summary ("" once asked without an
/// answer); ChosenName is the user's own name for the project. A card is shown as ChosenName, else Name (the name the user
/// gave a project started from email), else ShortName, else its title (the main paper's full title); see ThemePlan.DisplayTitle.
/// Collaborators: the people the research is done with (CollaboratorPlan). CreditedFor: the way of crediting its Overleaf
/// quotes (CollaboratorPlan.CreditVersion) its existing details were credited by.
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReviewedFor = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ReviewedAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ShortName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ChosenName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CollaboratorSet? Collaborators = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? CreditedFor = null);

/// <summary>
/// A research project's collaborators (OVERLEAF.md §12a), merged from its people: For is the fingerprint of the people and
/// author blocks they were merged from (CollaboratorPlan.Fingerprint), AuthorsFor of the author blocks alone. Dropped are the
/// keys of people who are no collaborators (the user, mailing lists, role addresses). Minor are the people on the project
/// without substantive involvement (only on email, not authors or editors): "key|hash of their points", so they are judged
/// again only when what they wrote that Meld used changes.
/// </summary>
internal sealed record CollaboratorSet(string For, string AuthorsFor, IReadOnlyList<Collaborator> People, IReadOnlyList<string> Dropped,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Minor = null);

/// <summary>
/// One collaborator: a name (null when only an address is known), their email addresses (none when only a name is known),
/// how they appear (Collaborators.Editor, Owner, Author, From, To, Cc), how often (edits count once, emails per message),
/// when last, and the keys of the people merged into them (CollaboratorPlan.Key).
/// </summary>
internal sealed record Collaborator(string? Name, IReadOnlyList<string> Emails, IReadOnlyList<string> Kinds, int Count,
    DateTimeOffset? At, IReadOnlyList<string> Keys);

/// <summary>
/// A person on an email thread: their address (lower case), the name they used, how many messages they sent, received or were
/// copied on, and the ids of the messages they sent (Sent; null when read before it was kept), to find what they wrote.
/// </summary>
internal sealed record MailPerson(string? Name, string Email, int From, int To, int Cc, DateTimeOffset At,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Sent = null);

/// <summary>
/// One entry of a research project's changelog (OVERLEAF.md §8): a source that updated it (an Overleaf root tree, an email
/// thread's new messages), a removal, the user's change, a merge of cards, or a periodic review (Review: overlapping details
/// merged and overtaken plans retired; not shown in the feed). Changes is what it did to the details; Key merges the parts
/// of one root tree of one cycle into one entry. PlacedAt: when the user placed the source on this project (an email thread
/// from Housekeeping, a project moved here), so the Updates feed shows the update from then (DetailPlan.Feed).
/// </summary>
internal sealed record ChangelogEntry(Guid Id, Guid AccountId, string CardId, DateTimeOffset At, string Kind, string Title,
    string? ProjectId, IReadOnlyList<string> Documents, IReadOnlyList<DetailChange> Changes, string? Key, DateTimeOffset RecordedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? PlacedAt = null)
{
    public const string Overleaf = "overleaf", Gmail = "gmail", Removed = "removed", User = "user", Merge = "merge", Review = "review";
}

/// <summary>
/// What one update did to one detail: added, revised (Before and After) or removed it. By: who it rests on, as person keys
/// (CollaboratorPlan.Key) or "self" for the user: the senders of the cited email, or who edited the cited files since Meld
/// last read them; null when not known (earlier updates, or the user's own changes).
/// </summary>
internal sealed record DetailChange(string Op, string DetailId, string? Before, string? After,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? By = null);

/// <summary>
/// An Overleaf file the user removed as a source of a research project (OVERLEAF.md §10). It waits in Housekeeping until
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
    IReadOnlyList<string>? SkippedProjects = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<HousekeepingGroup>? HousekeepingGroups = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RankVector>? Vectors = null);

/// <summary>
/// An embedding used only to order the project chooser (Ranking): of a research card, a Housekeeping group, an Overleaf
/// project or a removed file (Key), made from the text whose hash is For by embedding Model, stored normalized as 8-bit
/// values (Data, base64). Kept on this computer only; each computer embeds its own (it is cheap) and nothing else uses them.
/// </summary>
internal sealed record RankVector(Guid AccountId, string Key, string For, string Model, string Data);

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
/// decides where it goes: Housekeeping, in group GroupId; GroupPinned when the user put it there), skipped (not work; looked at again only when new mail arrives) or dismissed
/// (the user chose to ignore it for good). StreamId belongs to the earlier email streams and is no longer set.
/// HistoryId is Gmail's version of the thread when it was last analyzed ("" while a long thread is analyzed in parts) and
/// LastMessageId/LastSentAt the last message analyzed, so only later messages are new. Headline and NewsAt are set when the
/// thread made newsworthy progress (it then appears in the Updates feed); Suggested names a project for a pending thread.
/// Title is a short headline written for a waiting thread (TitledFor: the title style and last message it was written for).
/// People are the thread's senders and recipients other than the user (null until read), for the collaborators of its project.
/// PlacedAt: when the user put the thread on its project (or started a project with it); the updates its earlier mail makes
/// there are shown in the feed from then.
/// </summary>
internal sealed record GmailThreadRecord(Guid AccountId, string Address, string Id, string Subject, string Status, string HistoryId,
    string LastMessageId, DateTimeOffset LastSentAt, DateTimeOffset UpdatedAt, IReadOnlyList<GmailBullet> Bullets,
    string? StreamId = null, string? Headline = null, DateTimeOffset? NewsAt = null, string? Suggested = null, string? Explanation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CardId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FoldedMessageId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? FoldedSentAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? GroupId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? GroupPinned = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TitledFor = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<MailPerson>? People = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? PlacedAt = null)
{
    public const string Assigned = "assigned", Pending = "pending", Skipped = "skipped", Dismissed = "dismissed";
}

/// <summary>
/// A Housekeeping group: waiting email threads (GmailThreadRecord.GroupId) about the same topic, people or kind of work, so
/// the user decides on them at once. Name comes from the first thread's suggested project (Named: the user renamed it, so
/// it is never renamed). Held: the user made it; a held group, a renamed one, or one holding a thread the user moved there
/// is never combined by regrouping. SuggestedCardId: an existing research project the group looks like (strict rule),
/// shown for one-click confirmation, with SuggestionReason; SuggestedFor records which members and projects it was
/// checked for. Checked lists the members the last regrouping pass saw, so only groups that gained threads are compared
/// again. NamedFor records which threads the name was written for: when the threads change, the name falls back at once
/// to the suggested name of the oldest remaining thread, and a headline written from all threads (in the same call as the
/// project check) replaces it. Groups hold no details or summary; keeping one turns it into a research project.
/// </summary>
internal sealed record HousekeepingGroup(Guid AccountId, string Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Named = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Held = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SuggestedCardId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SuggestionReason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SuggestedFor = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Checked = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NamedFor = null);

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
