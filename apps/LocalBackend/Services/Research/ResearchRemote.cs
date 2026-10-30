using System.Net.Http.Headers;
using System.Text.Json;
using MeldResearch.Research;

namespace MeldResearch.LocalBackend.Services.Research;

internal sealed record ResearchOptions(Uri RemoteOrigin, bool Dummy, int PollSeconds)
{
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
        return new(origin, dummy, poll);
    }
}

internal interface IResearchRemote
{
    Task<RemoteStatusResponse> StatusAsync(CancellationToken ct);
    Task<RedeemSignInResponse> RedeemSignInAsync(RedeemSignInRequest request, CancellationToken ct);
    Task<MeldAccountResponse> AccountAsync(string token, CancellationToken ct);
    Task RevokeAsync(string token, CancellationToken ct);
    Task<AnalyzeResearchResponse> AnalyzeAsync(string token, AnalyzeResearchRequest request, CancellationToken ct);
    Task<ResearchEmbeddingResponse> EmbedAsync(string token, ResearchEmbeddingRequest request, CancellationToken ct);
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
    public Task<AnalyzeResearchResponse> AnalyzeAsync(string token, AnalyzeResearchRequest request, CancellationToken ct) =>
        CallAsync<AnalyzeResearchResponse>("api/research/analyze", HttpMethod.Post, request, token, ct);
    public Task<ResearchEmbeddingResponse> EmbedAsync(string token, ResearchEmbeddingRequest request, CancellationToken ct) =>
        CallAsync<ResearchEmbeddingResponse>("api/research/embeddings", HttpMethod.Post, request, token, ct);
    public Task<ResearcherSearchResponse> SearchResearcherAsync(string token, ResearcherSearchRequest request, CancellationToken ct) =>
        CallAsync<ResearcherSearchResponse>("api/researcher/candidates", HttpMethod.Post, request, token, ct);
    public Task<ResearcherWorksResponse> ResearcherWorksAsync(string token, ResearcherWorksRequest request, CancellationToken ct) =>
        CallAsync<ResearcherWorksResponse>("api/researcher/works", HttpMethod.Post, request, token, ct);

    private async Task<T> CallAsync<T>(string route, HttpMethod method, object? body, string? token, CancellationToken ct)
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
