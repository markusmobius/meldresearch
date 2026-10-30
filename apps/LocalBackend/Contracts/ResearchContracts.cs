using MeldResearch.Research;

namespace MeldResearch.LocalBackend.Contracts;

/// <summary>Enables or pauses monitoring; enabling requires the current explicit model-processing consent.</summary>
public sealed record SetProjectMonitoringRequest(bool Enabled, string? ConsentVersion);

/// <summary>The website sign-in page for this installation. It returns to the app's loopback callback; it holds no secret.</summary>
public sealed record AccountLinkResponse(string SignInUrl, DateTimeOffset ExpiresAt);

/// <summary>Local monitoring state, without credentials or stored embeddings.</summary>
public sealed record MonitoredProjectResponse(string ProjectId, string Name, bool Enabled, bool ConsentRequired,
    string? Revision, string? Summary, int FactCount, DateTimeOffset? LastAttemptAt, DateTimeOffset? LastSuccessAt,
    string? ErrorCode, string? Error, string? Explanation, int UnprocessedFiles);

/// <summary>Updates-only application status. No source documents are sent by this status request.</summary>
public sealed record ResearchStatusResponse(MeldAccountResponse? Account, DateTimeOffset? CredentialExpiresAt,
    AccountLinkResponse? PendingLink, string? AccountError, bool Busy, bool Dummy, string PrivacyUrl,
    string ConsentVersion, int PollSeconds, int PendingRevocations, IReadOnlyList<MonitoredProjectResponse> Projects,
    IReadOnlyList<ResearchUpdate> Updates, int TotalUpdates);
