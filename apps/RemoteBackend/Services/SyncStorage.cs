using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using MeldResearch.Research;

namespace MeldResearch.RemoteBackend.Services;

/// <summary>
/// Each account's encrypted sync folder ("{accountId}/" in the sync container). The service never sees plaintext: the local
/// app encrypts before uploading. It lists the folder, hands out short-lived links for named blobs in it, and deletes it
/// when the user starts over.
/// </summary>
internal interface ISyncStorage
{
    bool Configured { get; }
    Task<SyncListResponse> ListAsync(Guid account, CancellationToken ct);
    Task<SyncLinksResponse> LinksAsync(Guid account, SyncLinksRequest request, CancellationToken ct);
    Task DeleteAllAsync(Guid account, CancellationToken ct);
}

internal static class SyncFolders
{
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromMinutes(10);
    public static string Prefix(Guid account) => account.ToString("N") + "/";

    public static long Quota(IConfiguration configuration) =>
        Math.Clamp(configuration.GetValue("Storage:QuotaMegabytes", 500L), 10, 100_000) * 1024 * 1024;

    public static void Validate(SyncLinksRequest request)
    {
        if (request?.Names is not { Count: > 0 and <= 200 } names || names.Any(name => !SyncCrypto.IsBlobName(name)) ||
            names.Distinct(StringComparer.Ordinal).Count() != names.Count)
            throw new ResearchFailure(400, "invalid_sync_request", "Ask for 1-200 known sync blobs.");
    }

    public static ResearchFailure Full() => new(507, "sync_quota_exceeded", "Your encrypted sync storage is full. Forget unused projects or start over.");
}

/// <summary>
/// Azure Blob Storage through the VM's managed identity (as newsprinceton-llmserver does): user-delegation SAS links,
/// valid for ten minutes, for single blobs in the caller's folder, so the local app transfers ciphertext directly to Azure.
/// The identity needs Storage Blob Data Contributor on the container and the right to create user delegation keys.
/// </summary>
internal sealed class AzureSyncStorage : ISyncStorage
{
    private readonly BlobServiceClient _service;
    private readonly BlobContainerClient _container;
    private readonly long _quota;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _keyGate = new(1, 1);
    private UserDelegationKey? _key;

    public AzureSyncStorage(IConfiguration configuration, TimeProvider clock) : this(configuration, clock, null) { }

    internal AzureSyncStorage(IConfiguration configuration, TimeProvider clock, TokenCredential? credential)
    {
        string url = configuration["Storage:AccountUrl"]!;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var account) || account.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Storage:AccountUrl must be the HTTPS blob endpoint of the storage account.");
        string? clientId = configuration["Storage:ManagedIdentityClientId"];
        credential ??= string.IsNullOrWhiteSpace(clientId)
            ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId));
        _service = new BlobServiceClient(account, credential);
        _container = _service.GetBlobContainerClient(configuration["Storage:Container"] is { Length: > 0 } name ? name : "meldresearch");
        _quota = SyncFolders.Quota(configuration);
        _clock = clock;
    }

    public bool Configured => true;

    public async Task<SyncListResponse> ListAsync(Guid account, CancellationToken ct)
    {
        string prefix = SyncFolders.Prefix(account);
        var blobs = new List<SyncBlobInfo>();
        await foreach (var item in Call(() => _container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, ct)))
            blobs.Add(new(item.Name[prefix.Length..], item.Properties.ETag?.ToString() ?? "", item.Properties.ContentLength ?? 0,
                item.Properties.LastModified ?? DateTimeOffset.MinValue));
        return new(blobs, blobs.Sum(blob => blob.Size), _quota);
    }

    public async Task<SyncLinksResponse> LinksAsync(Guid account, SyncLinksRequest request, CancellationToken ct)
    {
        SyncFolders.Validate(request);
        if (request.Write && (await ListAsync(account, ct)).UsedBytes > _quota) throw SyncFolders.Full();
        var key = await DelegationKeyAsync(ct);
        var expires = _clock.GetUtcNow() + SyncFolders.LinkLifetime;
        var links = request.Names.Select(name =>
        {
            var blob = _container.GetBlobClient(SyncFolders.Prefix(account) + name);
            var sas = new BlobSasBuilder
            {
                BlobContainerName = _container.Name, BlobName = blob.Name, Resource = "b", Protocol = SasProtocol.Https,
                StartsOn = _clock.GetUtcNow().AddMinutes(-2), ExpiresOn = expires
            };
            sas.SetPermissions(request.Write
                ? BlobSasPermissions.Read | BlobSasPermissions.Write | BlobSasPermissions.Create | BlobSasPermissions.Delete
                : BlobSasPermissions.Read);
            var uri = new BlobUriBuilder(blob.Uri) { Sas = sas.ToSasQueryParameters(key, _service.AccountName) }.ToUri();
            return new SyncLink(name, uri.AbsoluteUri);
        }).ToArray();
        return new(links, expires);
    }

    public async Task DeleteAllAsync(Guid account, CancellationToken ct)
    {
        string prefix = SyncFolders.Prefix(account);
        await foreach (var item in Call(() => _container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, ct)))
            await _container.DeleteBlobIfExistsAsync(item.Name, DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct);
    }

    /// <summary>One user delegation key, valid two days and renewed daily, signs every link.</summary>
    private async Task<UserDelegationKey> DelegationKeyAsync(CancellationToken ct)
    {
        await _keyGate.WaitAsync(ct);
        try
        {
            var now = _clock.GetUtcNow();
            if (_key is null || _key.SignedExpiresOn - now < TimeSpan.FromDays(1))
            {
                try { _key = (await _service.GetUserDelegationKeyAsync(now.AddMinutes(-2), now.AddDays(2), ct)).Value; }
                catch (Exception error) when (error is RequestFailedException or AuthenticationFailedException)
                { throw new ResearchFailure(503, "sync_storage_unavailable", "Encrypted sync storage is not reachable from Meld's server."); }
            }
            return _key;
        }
        finally { _keyGate.Release(); }
    }

    private static async IAsyncEnumerable<BlobItem> Call(Func<AsyncPageable<BlobItem>> list)
    {
        var enumerator = list().GetAsyncEnumerator();
        try
        {
            while (true)
            {
                bool more;
                try { more = await enumerator.MoveNextAsync(); }
                catch (Exception error) when (error is RequestFailedException or AuthenticationFailedException)
                { throw new ResearchFailure(503, "sync_storage_unavailable", "Encrypted sync storage is not reachable from Meld's server."); }
                if (!more) yield break;
                yield return enumerator.Current;
            }
        }
        finally { await enumerator.DisposeAsync(); }
    }
}

/// <summary>Used when no storage account is configured: sync is off and the local app works on this computer only.</summary>
internal sealed class DisabledSyncStorage : ISyncStorage
{
    public bool Configured => false;
    public Task<SyncListResponse> ListAsync(Guid account, CancellationToken ct) => throw Off();
    public Task<SyncLinksResponse> LinksAsync(Guid account, SyncLinksRequest request, CancellationToken ct) => throw Off();
    public Task DeleteAllAsync(Guid account, CancellationToken ct) => throw Off();
    private static ResearchFailure Off() => new(503, "sync_not_configured", "Encrypted sync is not configured on this server.");
}

/// <summary>
/// Dummy mode only (development, loopback): blobs live in files under the data directory and the links point back to this
/// service, with the same rules as Azure (ETags, If-Match and If-None-Match on upload, HMAC-signed ten-minute links).
/// </summary>
internal sealed class DevelopmentSyncStorage(RemoteOptions options, IConfiguration configuration, TimeProvider clock) : ISyncStorage
{
    private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);
    private readonly long _quota = SyncFolders.Quota(configuration);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private string Root => Path.Combine(options.DataDirectory, "sync-dev");

    public bool Configured => true;

    public Task<SyncListResponse> ListAsync(Guid account, CancellationToken ct)
    {
        string folder = Path.Combine(Root, account.ToString("N"));
        var blobs = !Directory.Exists(folder) ? [] : Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(path =>
        {
            var file = new FileInfo(path);
            string name = Path.GetRelativePath(folder, path).Replace('\\', '/');
            return new SyncBlobInfo(name, ETag(File.ReadAllBytes(path)), file.Length, file.LastWriteTimeUtc);
        }).Where(blob => SyncCrypto.IsBlobName(blob.Name)).ToArray();
        return Task.FromResult(new SyncListResponse(blobs, blobs.Sum(blob => blob.Size), _quota));
    }

    public async Task<SyncLinksResponse> LinksAsync(Guid account, SyncLinksRequest request, CancellationToken ct)
    {
        SyncFolders.Validate(request);
        if (request.Write && (await ListAsync(account, ct)).UsedBytes > _quota) throw SyncFolders.Full();
        var expires = clock.GetUtcNow() + SyncFolders.LinkLifetime;
        long seconds = expires.ToUnixTimeSeconds();
        var links = request.Names.Select(name => new SyncLink(name, new Uri(options.PublicOrigin,
            $"dev-sync/{account:N}/{name}?exp={seconds}&w={(request.Write ? 1 : 0)}&sig={Sign(account, name, request.Write, seconds)}").AbsoluteUri)).ToArray();
        return new(links, expires);
    }

    public Task DeleteAllAsync(Guid account, CancellationToken ct)
    {
        string folder = Path.Combine(Root, account.ToString("N"));
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        return Task.CompletedTask;
    }

    /// <summary>Serves a signed link like Azure: GET, HEAD or PUT (with x-ms-blob-type, If-Match, If-None-Match).</summary>
    public async Task<IResult> HandleAsync(HttpContext context, string account, string name)
    {
        if (!Guid.TryParseExact(account, "N", out var id) || !SyncCrypto.IsBlobName(name) ||
            !long.TryParse(context.Request.Query["exp"], out long seconds) || seconds < clock.GetUtcNow().ToUnixTimeSeconds())
            return Results.StatusCode(403);
        bool write = context.Request.Query["w"] == "1";
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(id, name, write, seconds)),
                Encoding.ASCII.GetBytes(context.Request.Query["sig"].ToString())))
            return Results.StatusCode(403);
        string path = Path.Combine(Root, id.ToString("N"), name.Replace('/', Path.DirectorySeparatorChar));
        var gate = _locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(context.RequestAborted);
        try
        {
            byte[]? current = File.Exists(path) ? await File.ReadAllBytesAsync(path, context.RequestAborted) : null;
            if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
            {
                if (current is null) return Results.NotFound();
                context.Response.Headers.ETag = ETag(current);
                return HttpMethods.IsHead(context.Request.Method) ? Results.Ok() : Results.Bytes(current, "application/octet-stream");
            }
            if (HttpMethods.IsDelete(context.Request.Method) && write)
            {
                if (current is null) return Results.NotFound();
                File.Delete(path);
                return Results.StatusCode(202);
            }
            if (!HttpMethods.IsPut(context.Request.Method) || !write || context.Request.Headers["x-ms-blob-type"] != "BlockBlob")
                return Results.StatusCode(403);
            string ifMatch = context.Request.Headers.IfMatch.ToString(), ifNoneMatch = context.Request.Headers.IfNoneMatch.ToString();
            if ((ifNoneMatch == "*" && current is not null) || (ifMatch.Length != 0 && (current is null || ifMatch != ETag(current))))
                return Results.StatusCode(412);
            context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = 256 * 1024 * 1024;
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body, context.RequestAborted);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, body.ToArray(), context.RequestAborted);
            context.Response.Headers.ETag = ETag(body.ToArray());
            return Results.StatusCode(201);
        }
        finally { gate.Release(); }
    }

    private string Sign(Guid account, string name, bool write, long seconds) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes($"{account:N}|{name}|{write}|{seconds}")));
    private static string ETag(byte[] content) => "\"" + Convert.ToHexStringLower(SHA256.HashData(content))[..32] + "\"";
}
