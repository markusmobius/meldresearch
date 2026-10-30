namespace MeldResearch.LocalBackend.Services.Research;

internal sealed record RemotePingResponse(bool Online, DateTimeOffset CheckedAt);

/// <summary>
/// Whether the remote service is reachable, for the toolbar's connection indicator. Results are cached briefly so
/// several open tabs do not multiply requests; the check sends no credentials or research content.
/// </summary>
internal sealed class RemoteConnectivity(ResearchOptions options, IHttpClientFactory clients, TimeProvider clock)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RemotePingResponse? _last;

    public async Task<RemotePingResponse> CheckAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_last is { } cached && clock.GetUtcNow() - cached.CheckedAt < CacheFor) return cached;
            bool online;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var http = clients.CreateClient("research-remote");
                using var response = await http.GetAsync(new Uri(options.RemoteOrigin, "healthz"), timeout.Token);
                online = response.IsSuccessStatusCode;
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                online = false;
            }
            return _last = new(online, clock.GetUtcNow());
        }
        finally { _gate.Release(); }
    }
}
