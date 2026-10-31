using MeldResearch.Research;

namespace MeldResearch.LocalBackend.Contracts;

/// <summary>Enables or pauses monitoring; enabling requires the current explicit model-processing consent.</summary>
public sealed record SetProjectMonitoringRequest(bool Enabled, string? ConsentVersion);

/// <summary>The website sign-in page for this installation. It returns to the app's loopback callback; it holds no secret.</summary>
public sealed record AccountLinkResponse(string SignInUrl, DateTimeOffset ExpiresAt);

/// <summary>Local monitoring state, without credentials or stored embeddings.</summary>
/// Phase: waiting (first read not done), checking (reading Overleaf), queued, importing, updating, ready or error.
public sealed record MonitoredProjectResponse(string ProjectId, string Name, bool Enabled, bool ConsentRequired,
    string? Revision, string? Summary, int FactCount, DateTimeOffset? LastAttemptAt, DateTimeOffset? LastSuccessAt,
    string? ErrorCode, string? Error, string? Explanation, int UnprocessedFiles,
    ProjectProfile? Profile, string Phase, int FilesDone, int FilesTotal, DateTimeOffset? LastSweepAt, DateTimeOffset? NextSweepAt);

/// <summary>The project being processed now and how many others wait in the queue.</summary>
/// <summary>The project the queue is working on; CurrentFile is the file being folded into its summary next (or now).</summary>
public sealed record ProcessingStatusResponse(string ProjectId, string Name, string Kind, int FilesDone, int FilesTotal, int Queued,
    string? CurrentFile = null);

/// <summary>Updates-only application status. No source documents are sent by this status request.</summary>
public sealed record ResearchStatusResponse(MeldAccountResponse? Account, DateTimeOffset? CredentialExpiresAt,
    AccountLinkResponse? PendingLink, string? AccountError, bool Busy, bool Dummy, string PrivacyUrl,
    string ConsentVersion, int PollSeconds, int PendingRevocations, IReadOnlyList<MonitoredProjectResponse> Projects,
    IReadOnlyList<ResearchUpdate> Updates, int TotalUpdates, IReadOnlyList<ProjectDecisionResponse> Decisions,
    OverleafProjectsSnapshot? OverleafProjects, ResearcherStatusResponse? Researcher, ProcessingStatusResponse? Processing);

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
