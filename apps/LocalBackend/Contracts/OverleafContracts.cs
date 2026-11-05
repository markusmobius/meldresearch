using System.Text.Json.Serialization;

namespace MeldResearch.LocalBackend.Contracts;

/// <summary>
/// Reports the local browser connection without disclosing credentials. ConnectionVersion changes with every connection or
/// Overleaf sign-in; ConnectorVersion is the connected connector's own version (null until it says, as older ones do not).
/// </summary>
public sealed record BridgeStatusResponse(bool Connected, bool Busy, int ProtocolVersion,
    string? ConnectionVersion = null, bool Dummy = false, string? ConnectorVersion = null);

/// <summary>Identifies the required browser connector and the optional development installation directory.</summary>
public sealed record ExtensionSetupResponse(string ExtensionId, string ExtensionVersion,
    int ProtocolVersion, string? DevelopmentDirectory, string StoreUrl, bool StorePublished);

/// <summary>
/// Describes an accessible, non-archived Overleaf project. Owner: who owns it on Overleaf; Owned: the user owns it (so the
/// owner's name and email are the user's own).
/// </summary>
public sealed record OverleafProjectResponse(string Id, string Name, DateTimeOffset? UpdatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OverleafPerson? Owner = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Owned = false);

/// <summary>A person as Overleaf names them: a name, an email address (lower case), or both; At: their latest edit, if known.</summary>
public sealed record OverleafPerson(string? Name, string? Email,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? At = null);

/// <summary>Contains the projects returned by an explicit browser refresh.</summary>
public sealed record ProjectListResponse(IReadOnlyList<OverleafProjectResponse> Projects);

/// <summary>Describes a validated file inside a downloaded project archive.</summary>
public sealed record ArchiveEntryResponse(string Path, long Bytes);

/// <summary>Describes a project snapshot stored in per-user application data.</summary>
public sealed record DownloadResponse(Guid Id, string ProjectId, string ProjectName,
    DateTimeOffset DownloadedAt, long Bytes, IReadOnlyList<ArchiveEntryResponse> Files);

/// <summary>Contains locally persisted project snapshots.</summary>
public sealed record DownloadListResponse(IReadOnlyList<DownloadResponse> Downloads);
