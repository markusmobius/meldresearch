namespace MeldResearch.LocalBackend.Contracts;

/// <summary>Reports the local browser connection without disclosing credentials.</summary>
public sealed record BridgeStatusResponse(bool Connected, bool Busy, int ProtocolVersion,
    string? ConnectionVersion = null, bool Dummy = false);

/// <summary>Identifies the required browser connector and the optional development installation directory.</summary>
public sealed record ExtensionSetupResponse(string ExtensionId, string ExtensionVersion,
    int ProtocolVersion, string? DevelopmentDirectory, string StoreUrl, bool StorePublished);

/// <summary>Which installed browser should open its extensions page: "chrome" or "edge".</summary>
public sealed record OpenExtensionsPageRequest(string Browser);

/// <summary>Describes an accessible, non-archived Overleaf project.</summary>
public sealed record OverleafProjectResponse(string Id, string Name, DateTimeOffset? UpdatedAt);

/// <summary>Contains the projects returned by an explicit browser refresh.</summary>
public sealed record ProjectListResponse(IReadOnlyList<OverleafProjectResponse> Projects);

/// <summary>Describes a validated file inside a downloaded project archive.</summary>
public sealed record ArchiveEntryResponse(string Path, long Bytes);

/// <summary>Describes a project snapshot stored in per-user application data.</summary>
public sealed record DownloadResponse(Guid Id, string ProjectId, string ProjectName,
    DateTimeOffset DownloadedAt, long Bytes, IReadOnlyList<ArchiveEntryResponse> Files);

/// <summary>Contains locally persisted project snapshots.</summary>
public sealed record DownloadListResponse(IReadOnlyList<DownloadResponse> Downloads);
