using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using MeldResearch.RemoteBackend.Services;
using MeldResearch.Research;

namespace MeldResearch.Tests;

public sealed class SyncStorageTests : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();

    [Fact]
    public async Task TheDevelopmentStoreBehavesLikeAzureForSignedLinksETagsAndConditionalUploads()
    {
        var options = new RemoteOptions { DataDirectory = _temporary.Path, PublicOrigin = new Uri("http://127.0.0.1:6050/"), Dummy = true };
        var store = new DevelopmentSyncStorage(options, new ConfigurationBuilder().Build(), TimeProvider.System);
        var account = Guid.NewGuid();
        var write = await store.LinksAsync(account, new([SyncCrypto.KeyBlob], Write: true), CancellationToken.None);
        var read = await store.LinksAsync(account, new([SyncCrypto.KeyBlob], Write: false), CancellationToken.None);
        Assert.Equal(404, await StatusAsync(store, read.Links[0].Url, "GET"));

        var (created, etag) = await PutAsync(store, write.Links[0].Url, "one"u8.ToArray(), ifNoneMatch: "*");
        Assert.Equal(201, created);
        Assert.Equal(412, (await PutAsync(store, write.Links[0].Url, "two"u8.ToArray(), ifNoneMatch: "*")).Status);
        Assert.Equal(412, (await PutAsync(store, write.Links[0].Url, "two"u8.ToArray(), ifMatch: "\"stale\"")).Status);
        Assert.Equal(201, (await PutAsync(store, write.Links[0].Url, "two"u8.ToArray(), ifMatch: etag)).Status);
        Assert.Equal(403, (await PutAsync(store, read.Links[0].Url, "three"u8.ToArray())).Status);
        Assert.Equal(403, await StatusAsync(store, read.Links[0].Url.Replace("sig=", "sig=0"), "GET"));
        Assert.Equal(200, await StatusAsync(store, read.Links[0].Url, "GET"));
        Assert.Equal(403, await StatusAsync(store, read.Links[0].Url, "DELETE"));

        var listed = Assert.Single((await store.ListAsync(account, CancellationToken.None)).Blobs);
        Assert.Equal((SyncCrypto.KeyBlob, 3L), (listed.Name, listed.Size));
        Assert.Empty((await store.ListAsync(Guid.NewGuid(), CancellationToken.None)).Blobs);
        await Assert.ThrowsAsync<ResearchFailure>(() => store.LinksAsync(account, new(["../escape.bin"], true), CancellationToken.None));
        Assert.Equal(202, await StatusAsync(store, write.Links[0].Url, "DELETE"));
        Assert.Equal(404, await StatusAsync(store, read.Links[0].Url, "GET"));
        Assert.Equal(201, (await PutAsync(store, write.Links[0].Url, "again"u8.ToArray(), ifNoneMatch: "*")).Status);
        await store.DeleteAllAsync(account, CancellationToken.None);
        Assert.Empty((await store.ListAsync(account, CancellationToken.None)).Blobs);
    }

    private static async Task<int> StatusAsync(DevelopmentSyncStorage store, string url, string method)
    {
        var context = Context(url, method);
        await (await Handle(store, context, url)).ExecuteAsync(context);
        return context.Response.StatusCode;
    }

    private static async Task<(int Status, string ETag)> PutAsync(DevelopmentSyncStorage store, string url, byte[] body,
        string? ifMatch = null, string? ifNoneMatch = null)
    {
        var context = Context(url, "PUT");
        context.Request.Headers["x-ms-blob-type"] = "BlockBlob";
        if (ifMatch is not null) context.Request.Headers.IfMatch = ifMatch;
        if (ifNoneMatch is not null) context.Request.Headers.IfNoneMatch = ifNoneMatch;
        context.Request.Body = new MemoryStream(body);
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>(new BodySize());
        await (await Handle(store, context, url)).ExecuteAsync(context);
        return (context.Response.StatusCode, context.Response.Headers.ETag.ToString());
    }

    private static Task<IResult> Handle(DevelopmentSyncStorage store, HttpContext context, string url)
    {
        var parts = new Uri(url).AbsolutePath.Split('/', 4);
        return store.HandleAsync(context, parts[2], parts[3]);
    }

    private static DefaultHttpContext Context(string url, string method)
    {
        var uri = new Uri(url);
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        context.Request.Method = method;
        context.Request.Path = uri.AbsolutePath;
        context.Request.QueryString = new QueryString(uri.Query);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class BodySize : Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; }
    }

    public void Dispose() => _temporary.Dispose();
}
