using MeldResearch.Research;

namespace MeldResearch.LocalBackend.Contracts;

/// <summary>Enables or pauses monitoring; enabling requires the current explicit model-processing consent.</summary>
public sealed record SetProjectMonitoringRequest(bool Enabled, string? ConsentVersion);

/// <summary>The website sign-in page for this installation. It returns to the app's loopback callback; it holds no secret.</summary>
public sealed record AccountLinkResponse(string SignInUrl, DateTimeOffset ExpiresAt);

/// <summary>Local monitoring state, without credentials or stored embeddings.</summary>
/// Phase: waiting (first read not done), checking (reading Overleaf), queued, importing, updating, ready or error.
/// Source: null for an Overleaf project, "gmail" for a Gmail workstream (Parent: its Gmail address).
public sealed record MonitoredProjectResponse(string ProjectId, string Name, bool Enabled, bool ConsentRequired,
    string? Revision, string? Summary, int FactCount, DateTimeOffset? LastAttemptAt, DateTimeOffset? LastSuccessAt,
    string? ErrorCode, string? Error, string? Explanation, int UnprocessedFiles,
    ProjectProfile? Profile, string Phase, int FilesDone, int FilesTotal, DateTimeOffset? LastSweepAt, DateTimeOffset? NextSweepAt,
    string? Source = null, string? Parent = null);

/// <summary>The project the queue is working on; CurrentFile is the file being folded into its summary next (or now).</summary>
public sealed record ProcessingStatusResponse(string ProjectId, string Name, string Kind, int FilesDone, int FilesTotal, int Queued,
    string? CurrentFile = null, IReadOnlyList<string>? AlsoRunning = null);

/// <summary>Updates-only application status. No source documents are sent by this status request.</summary>
public sealed record ResearchStatusResponse(MeldAccountResponse? Account, DateTimeOffset? CredentialExpiresAt,
    AccountLinkResponse? PendingLink, string? AccountError, bool Busy, bool Dummy, string PrivacyUrl,
    string ConsentVersion, int PollSeconds, int PendingRevocations, IReadOnlyList<MonitoredProjectResponse> Projects,
    IReadOnlyList<ResearchUpdate> Updates, int TotalUpdates, IReadOnlyList<ProjectDecisionResponse> Decisions,
    OverleafProjectsSnapshot? OverleafProjects, ResearcherStatusResponse? Researcher, ProcessingStatusResponse? Processing,
    IReadOnlyList<ProjectSummaryResponseItem>? Summaries = null, IReadOnlyList<ResearchThemeResponse>? Themes = null,
    SyncStatusResponse? Sync = null, GmailStatusResponse? Gmail = null);

/// <summary>
/// Account → Gmail. Configured: Meld's server offers Gmail. Accounts never carry Google's grants. LastError: why the last
/// connection attempt did not complete (shown once on return from Google).
/// </summary>
public sealed record GmailStatusResponse(bool Configured, int WindowDays, string ConsentVersion, IReadOnlyList<GmailAccountResponse> Accounts,
    string? LastError);
/// <summary>
/// Status: connected, reconnect (Google withdrew the grant) or error. Threads: threads written in during the window.
/// Analyzed: threads analyzed so far; Workstreams: lines of work found in this account; Importing: the import running now;
/// ImportError: why the last import stopped (it is retried).
/// </summary>
public sealed record GmailAccountResponse(string Address, string Status, string? Error, int? Threads, DateTimeOffset? CheckedAt,
    DateTimeOffset ConnectedAt, int Analyzed = 0, int Workstreams = 0, DateTimeOffset? LastImportAt = null,
    DateTimeOffset? NextImportAt = null, GmailImportProgress? Importing = null, string? ImportError = null);
/// <summary>An import in progress: threads done of the threads with new mail; Waiting: another computer is importing.</summary>
public sealed record GmailImportProgress(int Done, int Total, bool Waiting = false);
public sealed record GmailConnectRequest(string? ConsentVersion = null);
/// <summary>Google's consent page; it returns to this app.</summary>
public sealed record GmailConnectResponse(string AuthorizationUrl, DateTimeOffset ExpiresAt);

/// <summary>
/// Encrypted sync on this computer: off (not offered by the server), signed-out, checking, setup (choose a passphrase),
/// locked (enter the passphrase or recovery key) or ready.
/// </summary>
public sealed record SyncStatusResponse(string State, int SyncedProjects, DateTimeOffset? LastPushAt, DateTimeOffset? LastPullAt,
    string? Error);
/// <summary>A passphrase (create, unlock, start over, change) or, for unlocking only, the recovery key.</summary>
public sealed record SyncPassphraseRequest(string? Passphrase = null, string? RecoveryKey = null);
/// <summary>
/// The recovery key that unlocks the synced data if the passphrase is forgotten. Replaced: the account's key file had no
/// copy of it (made before keys could be shown again), so a new one was made and the previous one no longer works.
/// </summary>
public sealed record SyncKeyResponse(string RecoveryKey, bool Replaced = false);

/// <summary>
/// A download being prepared (State preparing), ready (Url downloads it until ExpiresAt) or failed. One at a time; a ready
/// download stays available for five minutes, kept only in the app's memory.
/// </summary>
public sealed record ExportStatusResponse(string Id, string State, DateTimeOffset StartedAt, DateTimeOffset? ReadyAt,
    DateTimeOffset? ExpiresAt, string? FileName, long? Bytes, int Projects, string? Url, string? Error);
public sealed record ExportStateResponse(ExportStatusResponse? Current);

/// <summary>What an uploaded export contained; OtherAccount when it was downloaded while signed in as someone else.</summary>
public sealed record ImportResponse(int Projects, int Included, int Cards, int Summaries, bool Publications, string? ExportedEmail,
    bool OtherAccount, DateTimeOffset ExportedAt);

/// <summary>Leaving Meld Research requires typing DELETE.</summary>
public sealed record LeaveRequest(string? Confirmation = null);
/// <summary>
/// Uninstalling: the installed app is being removed and stops in a moment. AccountDeleted: the Meld account and its encrypted
/// synced data were deleted on Meld's server (false when no account was signed in).
/// </summary>
public sealed record LeaveResponse(bool Uninstalling, bool AccountDeleted, string? LogPath);

/// <summary>A research card: its combined summary (null until made) and its Overleaf projects. Updating while being summarized again.</summary>
public sealed record ResearchThemeResponse(string Id, ProjectProfile? Profile, IReadOnlyList<string> ProjectIds, bool Updating,
    string? Blurb = null);

/// <summary>Moves a project to another research card, or to a new card of its own when ThemeId is null.</summary>
public sealed record MoveToThemeRequest(string? ThemeId);

/// <summary>Combines a research card into another card.</summary>
public sealed record MergeThemeRequest(string IntoThemeId);

/// <summary>A project's one-paragraph summary from its main .tex file. Status: queued, running, ready or error.</summary>
public sealed record ProjectSummaryResponseItem(string ProjectId, string Status, string? Summary, string? MainPath,
    DateTimeOffset? UpdatedAt, DateTimeOffset? SourceUpdatedAt, string? Error);

/// <summary>Includes a project in the private profile (which starts monitoring, so it needs consent) or excludes it.</summary>
public sealed record ProjectDecisionRequest(bool Included, string? ConsentVersion);
public sealed record ProjectDecisionResponse(string ProjectId, bool Included, DateTimeOffset DecidedAt);

/// <summary>The most recent Overleaf project list read through the browser, by the page or by monitoring.</summary>
public sealed record OverleafProjectsSnapshot(IReadOnlyList<OverleafProjectResponse> Projects, DateTimeOffset FetchedAt);

/// <summary>Action: search (with SearchName), confirm (with AuthorId from the offered candidates) or none.</summary>
public sealed record ResearcherDecisionRequest(string Action, string? AuthorId, string? SearchName, long? BaseVersion);

/// <summary>The account's OpenAlex match, without the full publication list.</summary>
public sealed record ResearcherStatusResponse(long Version, string Status, string SearchName,
    IReadOnlyList<ResearcherCandidate> Candidates, ResearcherCandidate? Researcher, int WorkCount,
    DateTimeOffset? UpdatedAt, DateTimeOffset? NextRefreshAt, string? LastError);

/// <summary>The confirmed author's public works, stored on this computer.</summary>
public sealed record PublicationsResponse(ResearcherCandidate? Researcher, IReadOnlyList<PublicationWork> Works, DateTimeOffset? UpdatedAt);
