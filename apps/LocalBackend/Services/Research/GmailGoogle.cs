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
/// <summary>A thread in the listing; HistoryId changes whenever the thread changes (for example a new message).</summary>
internal sealed record GmailThreadRef(string Id, string HistoryId);
/// <summary>One email, cleaned on this computer. FromUser: the user sent it. Bulk: a mailing list, newsletter or automated message.</summary>
internal sealed record GmailMessage(string Id, string From, DateTimeOffset SentAt, bool FromUser, bool Bulk, string Text);
internal sealed record GmailThread(string Id, string HistoryId, string Subject, IReadOnlyList<GmailMessage> Messages);

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
    /// <summary>The threads the user wrote in during the last <paramref name="days"/> days (chats excluded), newest first.</summary>
    Task<IReadOnlyList<GmailThreadRef>> ParticipatedThreadsAsync(string accessToken, int days, CancellationToken ct);
    /// <summary>One thread with its messages, oldest first, cleaned (quoted replies and signatures removed).</summary>
    Task<GmailThread> ThreadAsync(string accessToken, string threadId, CancellationToken ct);

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

    public async Task<IReadOnlyList<GmailThreadRef>> ParticipatedThreadsAsync(string accessToken, int days, CancellationToken ct)
    {
        var ids = new List<GmailThreadRef>();
        string? page = null;
        string query = Uri.EscapeDataString($"in:sent newer_than:{days}d -in:chats");
        do
        {
            var result = await GetAsync<ThreadsJson>($"https://gmail.googleapis.com/gmail/v1/users/me/threads?q={query}&maxResults=500" +
                (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page)), accessToken, ct);
            ids.AddRange((result.Threads ?? []).Where(thread => EmailSteps.IsGmailId(thread.Id))
                .Select(thread => new GmailThreadRef(thread.Id, thread.HistoryId ?? "")));
            page = result.NextPageToken;
        }
        while (page is not null && ids.Count < MaxThreads);
        return ids.DistinctBy(thread => thread.Id).Take(MaxThreads).ToArray();
    }

    public async Task<GmailThread> ThreadAsync(string accessToken, string threadId, CancellationToken ct)
    {
        if (!EmailSteps.IsGmailId(threadId)) throw new ResearchFailure(400, "invalid_thread", "Invalid Gmail thread.");
        var thread = await GetAsync<ThreadFullJson>($"https://gmail.googleapis.com/gmail/v1/users/me/threads/{threadId}?format=full", accessToken, ct);
        var messages = new List<GmailMessage>();
        string subject = "";
        foreach (var message in thread.Messages ?? [])
        {
            if (!EmailSteps.IsGmailId(message.Id) || message.Payload is not { } payload) continue;
            string Header(string name) => payload.Headers?.FirstOrDefault(header => string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase))?.Value ?? "";
            if (subject.Length == 0) subject = Header("Subject").Trim();
            var labels = message.LabelIds ?? [];
            bool bulk = Header("List-Unsubscribe").Length != 0 || Header("Precedence") is "bulk" or "list" or "junk" ||
                labels.Any(label => label is "CATEGORY_PROMOTIONS" or "CATEGORY_SOCIAL" or "CATEGORY_FORUMS" or "CATEGORY_UPDATES");
            var sent = long.TryParse(message.InternalDate, out long milliseconds) ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : DateTimeOffset.UnixEpoch;
            messages.Add(new(message.Id, Header("From").Trim(), sent, labels.Contains("SENT"), bulk, EmailText.Clean(Body(payload))));
        }
        return new(thread.Id, thread.HistoryId ?? "", subject.Length == 0 ? "(no subject)" : subject, [.. messages.OrderBy(message => message.SentAt)]);
    }

    /// <summary>The message's plain text part, else its HTML part as text (attachments are ignored).</summary>
    private static string Body(PartJson payload)
    {
        PartJson? Find(PartJson part, string type) =>
            string.Equals(part.MimeType, type, StringComparison.OrdinalIgnoreCase) && part.Body?.Data is { Length: > 0 } && string.IsNullOrEmpty(part.Filename)
                ? part : (part.Parts ?? []).Select(child => Find(child, type)).FirstOrDefault(found => found is not null);
        if (Find(payload, "text/plain") is { } plain) return EmailText.Decode(plain.Body!.Data!);
        if (Find(payload, "text/html") is { } html) return EmailText.FromHtml(EmailText.Decode(html.Body!.Data!));
        return "";
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
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new ResearchFailure(404, "gmail_thread_missing", "The thread no longer exists.");
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
    private sealed record ThreadJson(string Id, string? HistoryId);
    private sealed record HeaderJson(string Name, string Value);
    private sealed record BodyJson(string? Data, int Size);
    private sealed record PartJson(string? MimeType, string? Filename, IReadOnlyList<HeaderJson>? Headers, BodyJson? Body, IReadOnlyList<PartJson>? Parts);
    private sealed record MessageJson(string Id, IReadOnlyList<string>? LabelIds, string? InternalDate, PartJson? Payload);
    private sealed record ThreadFullJson(string Id, string? HistoryId, IReadOnlyList<MessageJson>? Messages);
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

    public Task<IReadOnlyList<GmailThreadRef>> ParticipatedThreadsAsync(string accessToken, int days, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<GmailThreadRef>>([.. Mailbox(accessToken.Split('|')[^1]).Select(thread => new GmailThreadRef(thread.Id, thread.HistoryId))]);

    public Task<GmailThread> ThreadAsync(string accessToken, string threadId, CancellationToken ct) =>
        Task.FromResult(Mailbox(accessToken.Split('|')[^1]).FirstOrDefault(thread => thread.Id == threadId) ??
            throw new ResearchFailure(404, "gmail_thread_missing", "The thread no longer exists."));

    /// <summary>Synthetic threads the user wrote in (newest first): research work, a grant, and a lunch that should be skipped.</summary>
    private static IReadOnlyList<GmailThread> Mailbox(string address)
    {
        var day = DateTimeOffset.UtcNow.Date;
        GmailMessage Mine(string id, int daysAgo, string text) => new(id, $"Demo Researcher <{address}>", day.AddDays(-daysAgo).AddHours(10), true, false, text);
        GmailMessage From(string id, string from, int daysAgo, string text) => new(id, from, day.AddDays(-daysAgo).AddHours(14), false, false, text);
        if (address != Addresses[0])
            return [new GmailThread("dummyc1", "301", "Spring syllabus for Behavioral Economics", [
                Mine("dummyc1a", 5, "Hi all,\nI posted the draft syllabus for Behavioral Economics. New this year: a two-week module on social learning\nexperiments with an in-class replication of an information cascade.\nBest,\nDemo"),
                From("dummyc1b", "Teaching Assistant <ta@example.invalid>", 4, "Thanks! I can prepare the cascade experiment materials by next week.")])];
        return [
            new GmailThread("dummyt4", "204", "Robustness results for the social learning paper", [
                Mine("dummyt4a", 2, "Lukas,\nthe 200-agent simulations are done. They confirm the main result: herding falls by about 30% when signals\nare correlated, and the effect is stable across network sizes from 50 to 200.\nI added Table 4 to the draft.\nDemo")]),
            new GmailThread("dummyt3", "203", "Lunch on Friday?", [
                Mine("dummyt3a", 6, "Lunch at 12:30 on Friday?"),
                From("dummyt3b", "Sid Satya <sid@example.invalid>", 6, "Sure, see you then!")]),
            new GmailThread("dummyt2", "202", "Grant renewal budget", [
                Mine("dummyt2a", 12, "Tanya,\nI finalized the budget for the renewal: two research assistants and summer support, $480,000 over three years.\nI will upload it to the portal tomorrow.\nDemo"),
                From("dummyt2b", "Tanya Rosenblat <tanya@example.invalid>", 11, "Great, thank you. I will add the data management plan and the letters of collaboration.")]),
            new GmailThread("dummyt1", "201", "Revision plan for the social learning paper", [
                Mine("dummyt1a", 20, "Hi Lukas,\nthe referees ask for a robustness check on network size. I will rerun the simulations with 50 and 200 agents\nand extend the model to correlated signals.\nDemo"),
                From("dummyt1b", "Lukas Bolte <lukas@example.invalid>", 19, "Sounds good. I will draft the response letter and the new introduction.")])
        ];
    }
}
