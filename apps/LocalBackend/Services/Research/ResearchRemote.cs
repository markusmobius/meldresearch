using System.Net.Http.Headers;
using System.Text.Json;
using MeldResearch.Research;

namespace MeldResearch.LocalBackend.Services.Research;

internal sealed record ResearchOptions(Uri RemoteOrigin, bool Dummy, int PollSeconds, TimeSpan? SweepEvery = null, int ParallelProjects = 3)
{
    /// <summary>How often included projects are read again for changes; a manual check reads them at once.</summary>
    public TimeSpan SweepInterval => SweepEvery ?? TimeSpan.FromHours(24);

    public static ResearchOptions Load(IConfiguration configuration, IHostEnvironment environment)
    {
        bool dummy = configuration.GetValue<bool>("Dummy");
        string url = configuration["RemoteUrl"] ?? (dummy ? "http://127.0.0.1:6050" : "https://www.meldresearch.org");
        if (!Uri.TryCreate(url.TrimEnd('/') + "/", UriKind.Absolute, out var origin) ||
            origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.AbsolutePath != "/" ||
            (origin.Scheme != "https" && !(environment.IsDevelopment() && origin.IsLoopback && origin.Scheme == "http")) ||
            (dummy && (!environment.IsDevelopment() || !origin.IsLoopback)))
            throw new InvalidOperationException("Use an HTTPS remote origin. Dummy and loopback HTTP are development-only.");
        int poll = configuration.GetValue("ResearchPollSeconds", 300);
        if (poll is < 30 or > 3600) throw new InvalidOperationException("ResearchPollSeconds must be between 30 and 3600.");
        double hours = configuration.GetValue("ResearchSweepHours", 24.0);
        if (hours is < 0.1 or > 168) throw new InvalidOperationException("ResearchSweepHours must be between 0.1 and 168.");
        // Different projects are independent, so several are folded in at once; each project's files stay in order.
        int parallel = configuration.GetValue("ResearchParallelProjects", 3);
        if (parallel is < 1 or > 8) throw new InvalidOperationException("ResearchParallelProjects must be between 1 and 8.");
        return new(origin, dummy, poll, TimeSpan.FromHours(hours), parallel);
    }
}

internal sealed record AccountPicture(string ContentType, byte[] Image);
internal sealed record SyncBlob(byte[] Content, string ETag);

internal interface IResearchRemote
{
    Task<RemoteStatusResponse> StatusAsync(CancellationToken ct);
    Task<RedeemSignInResponse> RedeemSignInAsync(RedeemSignInRequest request, CancellationToken ct);
    Task<MeldAccountResponse> AccountAsync(string token, CancellationToken ct);
    Task RevokeAsync(string token, CancellationToken ct);
    /// <summary>The desktop OAuth client for read-only Gmail; gmail_not_configured when the server does not offer Gmail.</summary>
    Task<GmailClientResponse> GmailClientAsync(string token, CancellationToken ct);
    /// <summary>Deletes the account, its devices and its encrypted synced data on Meld's server.</summary>
    Task DeleteAccountAsync(string token, CancellationToken ct);
    Task<ProfileStepResponse> ProfileStepAsync(string token, ProfileStepRequest request, CancellationToken ct);
    /// <summary>Places one Gmail thread and records its new messages as bullets.</summary>
    Task<EmailStepResponse> EmailStepAsync(string token, EmailStepRequest request, CancellationToken ct);
    /// <summary>Summarizes an email stream for its research card.</summary>
    Task<EmailStreamResponse> EmailStreamAsync(string token, EmailStreamRequest request, CancellationToken ct);
    Task<ProjectSummaryResponse> SummarizeProjectAsync(string token, ProjectSummaryRequest request, CancellationToken ct);
    Task<ThemeMatchResponse> MatchThemeAsync(string token, ThemeMatchRequest request, CancellationToken ct);
    Task<ThemeMergeResponse> MergeThemeAsync(string token, ThemeMergeRequest request, CancellationToken ct);
    Task<ThemeRegroupResponse> RegroupThemesAsync(string token, ThemeRegroupRequest request, CancellationToken ct);
    Task<CardBlurbResponse> DescribeCardAsync(string token, CardBlurbRequest request, CancellationToken ct);
    Task<SyncListResponse> SyncListAsync(string token, CancellationToken ct);
    Task<SyncLinksResponse> SyncLinksAsync(string token, SyncLinksRequest request, CancellationToken ct);
    Task SyncDeleteAllAsync(string token, CancellationToken ct);
    /// <summary>Downloads a blob through its link; null when it does not exist.</summary>
    Task<SyncBlob?> BlobGetAsync(string url, CancellationToken ct);
    /// <summary>Uploads a blob through its link, only if it still has ETag ifMatch (or does not exist when ifMatch is null).</summary>
    Task<string> BlobPutAsync(string url, byte[] content, string? ifMatch, CancellationToken ct);
    Task BlobDeleteAsync(string url, CancellationToken ct);
    /// <summary>The account's profile picture stored by Meld's server; null when there is none.</summary>
    Task<AccountPicture?> AccountPictureAsync(string token, CancellationToken ct);
    Task<ResearcherSearchResponse> SearchResearcherAsync(string token, ResearcherSearchRequest request, CancellationToken ct);
    Task<ResearcherWorksResponse> ResearcherWorksAsync(string token, ResearcherWorksRequest request, CancellationToken ct);
}

internal sealed class ResearchRemote(ResearchOptions options, IHttpClientFactory clients) : IResearchRemote
{
    public Task<RemoteStatusResponse> StatusAsync(CancellationToken ct) => CallAsync<RemoteStatusResponse>("api/status", HttpMethod.Get, null, null, ct);
    public Task<RedeemSignInResponse> RedeemSignInAsync(RedeemSignInRequest request, CancellationToken ct) =>
        CallAsync<RedeemSignInResponse>("api/sign-in/redeem", HttpMethod.Post, request, null, ct);
    public Task<MeldAccountResponse> AccountAsync(string token, CancellationToken ct) =>
        CallAsync<MeldAccountResponse>("api/account", HttpMethod.Get, null, token, ct);
    public async Task RevokeAsync(string token, CancellationToken ct) =>
        _ = await CallAsync<JsonElement>("api/account/device", HttpMethod.Delete, null, token, ct);
    public Task<GmailClientResponse> GmailClientAsync(string token, CancellationToken ct) =>
        CallAsync<GmailClientResponse>("api/gmail/client", HttpMethod.Get, null, token, ct);
    public async Task DeleteAccountAsync(string token, CancellationToken ct) =>
        _ = await CallAsync<JsonElement>("api/account", HttpMethod.Delete, null, token, ct);
    public Task<ProfileStepResponse> ProfileStepAsync(string token, ProfileStepRequest request, CancellationToken ct) =>
        CallAsync<ProfileStepResponse>("api/research/profile-step", HttpMethod.Post, request, token, ct);
    public Task<EmailStepResponse> EmailStepAsync(string token, EmailStepRequest request, CancellationToken ct) =>
        CallAsync<EmailStepResponse>("api/research/email-step", HttpMethod.Post, request, token, ct);
    public Task<EmailStreamResponse> EmailStreamAsync(string token, EmailStreamRequest request, CancellationToken ct) =>
        CallAsync<EmailStreamResponse>("api/research/email-stream", HttpMethod.Post, request, token, ct);
    public Task<ProjectSummaryResponse> SummarizeProjectAsync(string token, ProjectSummaryRequest request, CancellationToken ct) =>
        CallAsync<ProjectSummaryResponse>("api/research/project-summary", HttpMethod.Post, request, token, ct);
    public Task<ThemeMatchResponse> MatchThemeAsync(string token, ThemeMatchRequest request, CancellationToken ct) =>
        CallAsync<ThemeMatchResponse>("api/research/theme-match", HttpMethod.Post, request, token, ct);
    public Task<ThemeMergeResponse> MergeThemeAsync(string token, ThemeMergeRequest request, CancellationToken ct) =>
        CallAsync<ThemeMergeResponse>("api/research/theme-merge", HttpMethod.Post, request, token, ct);
    public Task<ThemeRegroupResponse> RegroupThemesAsync(string token, ThemeRegroupRequest request, CancellationToken ct) =>
        CallAsync<ThemeRegroupResponse>("api/research/theme-regroup", HttpMethod.Post, request, token, ct);
    public Task<SyncListResponse> SyncListAsync(string token, CancellationToken ct) =>
        CallAsync<SyncListResponse>("api/sync/blobs", HttpMethod.Get, null, token, ct);
    public Task<SyncLinksResponse> SyncLinksAsync(string token, SyncLinksRequest request, CancellationToken ct) =>
        CallAsync<SyncLinksResponse>("api/sync/links", HttpMethod.Post, request, token, ct);
    public async Task SyncDeleteAllAsync(string token, CancellationToken ct) =>
        _ = await CallAsync<JsonElement>("api/sync/blobs", HttpMethod.Delete, null, token, ct);

    private const long MaxBlobBytes = 256L * 1024 * 1024;

    public async Task<SyncBlob?> BlobGetAsync(string url, CancellationToken ct)
    {
        using var response = await BlobSendAsync(new HttpRequestMessage(HttpMethod.Get, url), ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBlobBytes) throw BlobFailure(response);
        byte[] content = await response.Content.ReadAsByteArrayAsync(ct);
        return new(content, response.Headers.ETag?.ToString() ?? "");
    }

    public async Task<string> BlobPutAsync(string url, byte[] content, string? ifMatch, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(content) };
        request.Content.Headers.ContentType = new("application/octet-stream");
        request.Headers.Add("x-ms-blob-type", "BlockBlob");
        if (ifMatch is null) request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        else request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        using var response = await BlobSendAsync(request, ct);
        if (response.StatusCode is System.Net.HttpStatusCode.PreconditionFailed or System.Net.HttpStatusCode.Conflict)
            throw new ResearchFailure(412, "sync_conflict", "Another computer changed this synced data first; Meld merges it on the next check.");
        if (!response.IsSuccessStatusCode) throw BlobFailure(response);
        return response.Headers.ETag?.ToString() ?? throw BlobFailure(response);
    }

    public async Task BlobDeleteAsync(string url, CancellationToken ct)
    {
        using var response = await BlobSendAsync(new HttpRequestMessage(HttpMethod.Delete, url), ct);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound) throw BlobFailure(response);
    }

    /// <summary>Blob links carry their own short-lived signature; no Meld credential is sent to the storage service.</summary>
    private async Task<HttpResponseMessage> BlobSendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMinutes(5));
            try { return await clients.CreateClient("sync-blobs").SendAsync(request, HttpCompletionOption.ResponseContentRead, deadline.Token); }
            catch (HttpRequestException) { throw new ResearchFailure(503, "remote_unavailable", "Encrypted sync storage is unreachable. Meld retries shortly."); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new ResearchFailure(504, "remote_timeout", "Encrypted sync storage did not answer in time. Meld retries shortly."); }
        }
    }

    private static ResearchFailure BlobFailure(HttpResponseMessage response) =>
        new(502, "sync_storage_failed", $"Encrypted sync storage rejected the request (HTTP {(int)response.StatusCode}).");

    public async Task<AccountPicture?> AccountPictureAsync(string token, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(options.RemoteOrigin, "api/account/picture"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var http = clients.CreateClient("research-remote");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 512 * 1024) return null;
            string? type = response.Content.Headers.ContentType?.MediaType;
            if (type is not ("image/jpeg" or "image/png" or "image/gif" or "image/webp")) return null;
            byte[] image = await response.Content.ReadAsByteArrayAsync(deadline.Token);
            return image.Length is > 0 and <= 512 * 1024 ? new(type, image) : null;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { return null; }
    }

    public Task<CardBlurbResponse> DescribeCardAsync(string token, CardBlurbRequest request, CancellationToken ct) =>
        CallAsync<CardBlurbResponse>("api/research/card-blurb", HttpMethod.Post, request, token, ct);
    public Task<ResearcherSearchResponse> SearchResearcherAsync(string token, ResearcherSearchRequest request, CancellationToken ct) =>
        CallAsync<ResearcherSearchResponse>("api/researcher/candidates", HttpMethod.Post, request, token, ct);
    public Task<ResearcherWorksResponse> ResearcherWorksAsync(string token, ResearcherWorksRequest request, CancellationToken ct) =>
        CallAsync<ResearcherWorksResponse>("api/researcher/works", HttpMethod.Post, request, token, ct);

    /// <summary>
    /// A dropped connection (no response) is retried once after a moment: every remote call is stateless or idempotent on the
    /// server, so repeating it cannot duplicate anything.
    /// </summary>
    private async Task<T> CallAsync<T>(string route, HttpMethod method, object? body, string? token, CancellationToken ct)
    {
        try { return await CallOnceAsync<T>(route, method, body, token, ct); }
        catch (ResearchFailure failure) when (failure.Code == "remote_unavailable" && !ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            return await CallOnceAsync<T>(route, method, body, token, ct);
        }
    }

    private async Task<T> CallOnceAsync<T>(string route, HttpMethod method, object? body, string? token, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(route.StartsWith("api/research", StringComparison.Ordinal) ? TimeSpan.FromMinutes(4) : TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(method, new Uri(options.RemoteOrigin, route));
        if (body is not null)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body, ResearchJson.Options);
            if (bytes.Length > ResearchEvidence.MaxRequestBytes)
                throw new ResearchFailure(413, "research_request_too_large", "This project exceeds the supported inference request size.");
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new("application/json");
        }
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var http = clients.CreateClient("research-remote");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                if (method == HttpMethod.Delete && typeof(T) == typeof(JsonElement)) return default!;
                throw InvalidResponse();
            }
            int limit = !response.IsSuccessStatusCode ? 65536 : route == "api/researcher/works" ? 16 * 1024 * 1024 : 5 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > limit) throw InvalidResponse();
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var memory = new MemoryStream();
            byte[] buffer = new byte[16384];
            int count;
            while ((count = await stream.ReadAsync(buffer, deadline.Token)) != 0)
            {
                if (memory.Length + count > limit) throw InvalidResponse();
                memory.Write(buffer, 0, count);
            }
            if (!response.IsSuccessStatusCode)
            {
                string code = "remote_request_failed";
                string detail = $"The remote service rejected this request (HTTP {(int)response.StatusCode}).";
                // A web page instead of a JSON problem means the proxy in front of the service answered, not the service.
                if (response.Content.Headers.ContentType?.MediaType is "text/html" or "text/plain")
                {
                    code = "remote_gateway_error";
                    detail = $"Meld's server did not answer its web proxy ({(response.Headers.Server.ToString() is { Length: > 0 } server ? server : "proxy")}, HTTP {(int)response.StatusCode}). The service may be restarting or failing on this request.";
                }
                try
                {
                    using var problem = JsonDocument.Parse(memory.ToArray());
                    if (problem.RootElement.TryGetProperty("code", out var value) && value.GetString() is { Length: > 0 and <= 80 } name &&
                        name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')) code = name;
                    if (problem.RootElement.TryGetProperty("detail", out value) && value.GetString() is { Length: > 0 and <= 1000 } message) detail = message;
                }
                catch (JsonException) { throw new ResearchFailure((int)response.StatusCode, code, detail); }
                throw new ResearchFailure((int)response.StatusCode, code, detail);
            }
            return JsonSerializer.Deserialize<T>(memory.ToArray(), ResearchJson.Options) ?? throw InvalidResponse();
        }
        catch (JsonException) { throw InvalidResponse(); }
        catch (HttpRequestException) { throw new ResearchFailure(503, "remote_unavailable", "The Meld Research remote service is unavailable. Existing local data is unchanged."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new ResearchFailure(504, "remote_timeout", "The remote operation timed out. Existing local data is unchanged."); }
    }

    private static ResearchFailure InvalidResponse() =>
        new(502, "invalid_remote_response", "The remote service returned an unsupported response. Existing local data is unchanged.");
}
