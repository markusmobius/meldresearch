namespace MeldResearch.Research;

/// <summary>Asks for short-lived links to blobs in the caller's own sync folder; Write also allows creating and replacing them.</summary>
public sealed record SyncLinksRequest(IReadOnlyList<string> Names, bool Write);
public sealed record SyncLink(string Name, string Url);
public sealed record SyncLinksResponse(IReadOnlyList<SyncLink> Links, DateTimeOffset ExpiresAt);

/// <summary>One blob of the caller's sync folder; ETag changes whenever it is replaced.</summary>
public sealed record SyncBlobInfo(string Name, string ETag, long Size, DateTimeOffset LastModified);
public sealed record SyncListResponse(IReadOnlyList<SyncBlobInfo> Blobs, long UsedBytes, long QuotaBytes);
