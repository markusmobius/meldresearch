using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeldResearch.Research;

namespace MeldResearch.LocalBackend.Services.Research;

internal sealed record GmailTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt, string Scope);
internal sealed record GmailProfile(string Address, string? HistoryId);

/// <summary>Google's OAuth endpoints and the read-only Gmail API (the parts Meld uses).</summary>
internal interface IGmailGoogle
{
    public const string ReadOnlyScope = "https://www.googleapis.com/auth/gmail.readonly";

    /// <summary>The Google consent page; it returns to redirectUri with a one-time code (loopback, PKCE S256).</summary>
    Uri AuthorizationUrl(GmailClientResponse client, string redirectUri, string state, string challenge);
    Task<GmailTokens> ExchangeAsync(GmailClientResponse client, string code, string redirectUri, string verifier, CancellationToken ct);
    /// <summary>A fresh access token; throws gmail_reconnect when Google no longer accepts the grant.</summary>
    Task<GmailTokens> RefreshAsync(GmailClientResponse client, string refreshToken, CancellationToken ct);
    Task RevokeAsync(string token, CancellationToken ct);
    Task<GmailProfile> ProfileAsync(string accessToken, CancellationToken ct);
    /// <summary>Ids of the threads the user wrote in during the last <paramref name="days"/> days (chats excluded).</summary>
    Task<IReadOnlyList<string>> ParticipatedThreadsAsync(string accessToken, int days, CancellationToken ct);

    /// <summary>The PKCE S256 challenge for a verifier.</summary>
    public static string Challenge(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Talks to Google directly from this computer: the code exchange, token refresh and Gmail reads never pass through Meld's
/// server. Tokens and mail content are never logged.
/// </summary>
internal sealed class GmailGoogle(IHttpClientFactory clients) : IGmailGoogle
{
    public const string HttpClientName = "google";
    private const int MaxThreads = 2000;

    public Uri AuthorizationUrl(GmailClientResponse client, string redirectUri, string state, string challenge)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = client.ClientId, ["redirect_uri"] = redirectUri, ["response_type"] = "code",
            ["scope"] = "openid email " + IGmailGoogle.ReadOnlyScope, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256",
            ["state"] = state, ["access_type"] = "offline", ["prompt"] = "consent select_account"
        };
        return new Uri("https://accounts.google.com/o/oauth2/v2/auth?" +
            string.Join('&', query.Select(item => $"{item.Key}={Uri.EscapeDataString(item.Value)}")));
    }

    public Task<GmailTokens> ExchangeAsync(GmailClientResponse client, string code, string redirectUri, string verifier, CancellationToken ct) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = client.ClientId, ["client_secret"] = client.ClientSecret, ["code"] = code, ["code_verifier"] = verifier,
            ["grant_type"] = "authorization_code", ["redirect_uri"] = redirectUri
        }, ct);

    public Task<GmailTokens> RefreshAsync(GmailClientResponse client, string refreshToken, CancellationToken ct) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = client.ClientId, ["client_secret"] = client.ClientSecret, ["refresh_token"] = refreshToken, ["grant_type"] = "refresh_token"
        }, ct);

    public async Task RevokeAsync(string token, CancellationToken ct)
    {
        using var response = await Client().PostAsync("https://oauth2.googleapis.com/revoke",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }), ct);
        // An already invalid token is as good as revoked.
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.BadRequest) throw Unavailable();
    }

    public async Task<GmailProfile> ProfileAsync(string accessToken, CancellationToken ct)
    {
        var profile = await GetAsync<ProfileJson>("https://gmail.googleapis.com/gmail/v1/users/me/profile", accessToken, ct);
        return new(profile.EmailAddress, profile.HistoryId);
    }

    public async Task<IReadOnlyList<string>> ParticipatedThreadsAsync(string accessToken, int days, CancellationToken ct)
    {
        var ids = new List<string>();
        string? page = null;
        string query = Uri.EscapeDataString($"in:sent newer_than:{days}d -in:chats");
        do
        {
            var result = await GetAsync<ThreadsJson>($"https://gmail.googleapis.com/gmail/v1/users/me/threads?q={query}&maxResults=500" +
                (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page)), accessToken, ct);
            ids.AddRange((result.Threads ?? []).Select(thread => thread.Id));
            page = result.NextPageToken;
        }
        while (page is not null && ids.Count < MaxThreads);
        return ids.Distinct(StringComparer.Ordinal).Take(MaxThreads).ToArray();
    }

    private async Task<GmailTokens> TokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var response = await Client().PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form), ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            string? error = null;
            try { error = JsonSerializer.Deserialize<TokenErrorJson>(body, Lenient)?.Error; } catch (JsonException) { }
            if (error is "invalid_grant")
                throw new ResearchFailure(401, "gmail_reconnect", "Google no longer accepts Meld's access to this Gmail account. Connect it again.");
            if ((int)response.StatusCode >= 500) throw Unavailable();
            throw new ResearchFailure(502, "gmail_sign_in_failed", "Google did not accept the Gmail sign-in. Try connecting again.");
        }
        var tokens = JsonSerializer.Deserialize<TokenJson>(body, Lenient) ?? throw Unavailable();
        return new(tokens.AccessToken, tokens.RefreshToken, DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, tokens.ExpiresIn - 60)), tokens.Scope ?? "");
    }

    private async Task<T> GetAsync<T>(string url, string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await Client().SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new ResearchFailure(401, "gmail_token_expired", "The Gmail access token expired.");
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new ResearchFailure(403, "gmail_scope_missing", "Meld may not read this Gmail account. Connect it again and allow reading your email.");
        if (!response.IsSuccessStatusCode) throw Unavailable();
        return await response.Content.ReadFromJsonAsync<T>(Lenient, ct) ?? throw Unavailable();
    }

    private HttpClient Client() => clients.CreateClient(HttpClientName);
    private static ResearchFailure Unavailable() => new(503, "gmail_unavailable", "Gmail did not answer. Meld tries again later.");

    // Google's responses carry more fields than Meld reads.
    private static readonly JsonSerializerOptions Lenient = new(JsonSerializerDefaults.Web);
    private sealed record TokenJson([property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken, [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("scope")] string? Scope);
    private sealed record TokenErrorJson([property: JsonPropertyName("error")] string? Error);
    private sealed record ProfileJson(string EmailAddress, string? HistoryId);
    private sealed record ThreadJson(string Id);
    private sealed record ThreadsJson(IReadOnlyList<ThreadJson>? Threads, string? NextPageToken);
}

/// <summary>
/// Dummy mode: a synthetic mailbox and an immediate "consent" that returns straight to the app, so the Gmail flow can be
/// tested without Google. Each connection is a different synthetic account.
/// </summary>
internal sealed class DummyGmailGoogle : IGmailGoogle
{
    private static readonly string[] Addresses = ["researcher.gmail@example.invalid", "second.gmail@example.invalid", "third.gmail@example.invalid"];
    private int _connections;

    public Uri AuthorizationUrl(GmailClientResponse client, string redirectUri, string state, string challenge) =>
        new($"{redirectUri}?code=dummy-code&state={Uri.EscapeDataString(state)}");

    public Task<GmailTokens> ExchangeAsync(GmailClientResponse client, string code, string redirectUri, string verifier, CancellationToken ct)
    {
        int index = Interlocked.Increment(ref _connections) - 1;
        string address = Addresses[index % Addresses.Length];
        return Task.FromResult(new GmailTokens("dummy-access|" + address, "dummy-refresh|" + address, DateTimeOffset.UtcNow.AddHours(1),
            "openid email " + IGmailGoogle.ReadOnlyScope));
    }

    public Task<GmailTokens> RefreshAsync(GmailClientResponse client, string refreshToken, CancellationToken ct) =>
        Task.FromResult(new GmailTokens("dummy-access|" + refreshToken.Split('|')[^1], null, DateTimeOffset.UtcNow.AddHours(1), IGmailGoogle.ReadOnlyScope));

    public Task RevokeAsync(string token, CancellationToken ct) => Task.CompletedTask;

    public Task<GmailProfile> ProfileAsync(string accessToken, CancellationToken ct) =>
        Task.FromResult(new GmailProfile(accessToken.Split('|')[^1], "1000"));

    public Task<IReadOnlyList<string>> ParticipatedThreadsAsync(string accessToken, int days, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([.. Enumerable.Range(1, accessToken.EndsWith(Addresses[0], StringComparison.Ordinal) ? 7 : 3)
            .Select(index => $"dummy-thread-{index}")]);
}
