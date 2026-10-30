using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MeldResearch.Research;

namespace MeldResearch.RemoteBackend.Services;

internal interface IOpenAlexClient
{
    Task<IReadOnlyList<ResearcherCandidate>> SearchAuthorsAsync(IEnumerable<string> names, CancellationToken ct);
    Task<IReadOnlyList<PublicationWork>> SamplePublicationsAsync(string authorId, CancellationToken ct);
    Task<IReadOnlyList<PublicationWork>> ReadPublicationsAsync(string authorId, CancellationToken ct);
}

/// <summary>Reads public author and work metadata from OpenAlex (ported from Project Meld). Nothing is stored here.</summary>
internal sealed class OpenAlexClient(IHttpClientFactory clients, IConfiguration configuration, ILogger<OpenAlexClient> logger) : IOpenAlexClient
{
    internal const int MaximumWorks = 1000;
    private const int MaximumAbstract = 6000;

    public async Task<IReadOnlyList<ResearcherCandidate>> SearchAuthorsAsync(IEnumerable<string> names, CancellationToken ct)
    {
        var authors = new Dictionary<string, ResearcherCandidate>(StringComparer.Ordinal);
        foreach (var name in names.Select(name => name.Trim()).Where(name => name.Length is > 1 and <= 160)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(4))
        {
            using var response = await GetAsync("authors?per_page=10&search=" + Uri.EscapeDataString(name), ct);
            if (Property(response.RootElement, "results").ValueKind != JsonValueKind.Array) throw Unsupported();
            foreach (var author in Items(response.RootElement, "results"))
            {
                var id = NormalizeId(Text(author, "id"), 'A');
                var displayName = Text(author, "display_name");
                if (id is null || string.IsNullOrWhiteSpace(displayName)) continue;
                var affiliations = Items(author, "last_known_institutions").Select(institution => Text(institution, "display_name"))
                    .Concat(Items(author, "affiliations").Select(affiliation => Text(Property(affiliation, "institution"), "display_name")))
                    .Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
                authors.TryAdd(id, new(id, Clip(displayName, 300), Text(author, "orcid"), affiliations,
                    Items(author, "display_name_alternatives").Where(value => value.ValueKind == JsonValueKind.String)
                        .Select(value => Clip(value.GetString()!, 300)).Take(10).ToArray(), Number(author, "works_count"), []));
            }
        }
        return authors.Values.Take(30).ToArray();
    }

    public async Task<IReadOnlyList<PublicationWork>> SamplePublicationsAsync(string authorId, CancellationToken ct)
    {
        using var response = await GetAsync(WorksUrl(authorId) + "&per_page=3&sort=cited_by_count:desc", ct);
        return ReadWorks(response.RootElement, includeAbstract: false).Take(3).ToArray();
    }

    public async Task<IReadOnlyList<PublicationWork>> ReadPublicationsAsync(string authorId, CancellationToken ct)
    {
        var works = new Dictionary<string, PublicationWork>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = "*";
        while (cursor is not null && works.Count < MaximumWorks)
        {
            if (!cursors.Add(cursor)) throw Unsupported();
            using var response = await GetAsync(WorksUrl(authorId) + "&per_page=100&sort=publication_date:desc&cursor=" + Uri.EscapeDataString(cursor), ct);
            foreach (var work in ReadWorks(response.RootElement, includeAbstract: true)) works.TryAdd(work.Id, work);
            cursor = Text(Property(response.RootElement, "meta"), "next_cursor");
            if (cursor is { Length: 0 }) cursor = null;
        }
        return works.Values.Take(MaximumWorks).OrderByDescending(work => work.PublicationDate, StringComparer.Ordinal)
            .ThenBy(work => work.Id, StringComparer.Ordinal).ToArray();
    }

    public static string? NormalizeId(string? value, char kind)
    {
        if (value is null) return null;
        const string prefix = "https://openalex.org/";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) value = value[prefix.Length..];
        return value.Length is > 1 and <= 24 && value[0] == kind && !value.AsSpan(1).ContainsAnyExceptInRange('0', '9') ? value : null;
    }

    private static string WorksUrl(string authorId) =>
        "works?filter=author.id:" + (NormalizeId(authorId, 'A') ?? throw new ResearchFailure(400, "invalid_author_id", "Choose a valid OpenAlex author."));

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openalex.org/" + path);
        if (configuration["OpenAlex:ApiKey"] is { Length: > 0 } key)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var http = clients.CreateClient("openalex");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("OpenAlex request failed: HTTP {Status}.", (int)response.StatusCode);
                throw response.StatusCode == HttpStatusCode.TooManyRequests
                    ? new ResearchFailure(429, "openalex_rate_limited", "OpenAlex has reached its request limit. Try again later.")
                    : new ResearchFailure(503, "openalex_unavailable", "OpenAlex is unavailable. Try again later.");
            }
            if (response.Content.Headers.ContentLength > 8 * 1024 * 1024) throw Unsupported();
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var memory = new MemoryStream();
            byte[] buffer = new byte[16384];
            int count;
            while ((count = await stream.ReadAsync(buffer, deadline.Token)) != 0)
            {
                if (memory.Length + count > 8 * 1024 * 1024) throw Unsupported();
                memory.Write(buffer, 0, count);
            }
            return JsonDocument.Parse(memory.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException) { throw Unsupported(); }
        catch (HttpRequestException error)
        {
            logger.LogWarning("OpenAlex transport failed: {Failure}.", error.HttpRequestError);
            throw new ResearchFailure(503, "openalex_unavailable", "OpenAlex is unavailable. Try again later.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ResearchFailure(504, "openalex_timeout", "OpenAlex did not respond in time. Try again later.");
        }
    }

    private static IEnumerable<PublicationWork> ReadWorks(JsonElement root, bool includeAbstract)
    {
        if (Property(root, "results").ValueKind != JsonValueKind.Array) throw Unsupported();
        foreach (var item in Items(root, "results"))
        {
            var id = NormalizeId(Text(item, "id"), 'W');
            var title = Text(item, "title") ?? Text(item, "display_name");
            if (id is null || string.IsNullOrWhiteSpace(title)) continue;
            var authors = Items(item, "authorships").Select(authorship => Text(Property(authorship, "author"), "display_name"))
                .Where(name => !string.IsNullOrWhiteSpace(name)).Cast<string>().Select(name => Clip(name, 200)).Take(100).ToArray();
            var summary = includeAbstract ? ReconstructAbstract(Property(item, "abstract_inverted_index")) : null;
            yield return new(id, Clip(title, 1000), authors, Number(item, "publication_year"), Text(item, "publication_date"),
                Text(Property(Property(item, "primary_location"), "source"), "display_name") is { } venue ? Clip(venue, 300) : null,
                Text(item, "doi"), summary is null ? null : Clip(summary, MaximumAbstract),
                Property(item, "is_retracted").ValueKind == JsonValueKind.True);
        }
    }

    private static string? ReconstructAbstract(JsonElement invertedIndex)
    {
        if (invertedIndex.ValueKind != JsonValueKind.Object) return null;
        var words = new SortedDictionary<int, string>();
        foreach (var token in invertedIndex.EnumerateObject())
        {
            if (token.Value.ValueKind != JsonValueKind.Array) return null;
            foreach (var position in token.Value.EnumerateArray())
            {
                if (position.ValueKind != JsonValueKind.Number || !position.TryGetInt32(out var index) || index is < 0 or >= 30000 ||
                    !words.TryAdd(index, token.Name)) return null;
            }
        }
        if (words.Count == 0 || words.Keys.Last() != words.Count - 1) return null;
        return string.Join(' ', words.Values);
    }

    private static string Clip(string value, int length) => value.Length <= length ? value : value[..length];
    private static ResearchFailure Unsupported() => new(502, "openalex_response_unsupported", "OpenAlex returned an unsupported response.");
    private static JsonElement Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : default;
    private static string? Text(JsonElement value, string name) =>
        Property(value, name) is { ValueKind: JsonValueKind.String } property ? property.GetString() : null;
    private static int Number(JsonElement value, string name) =>
        Property(value, name) is { ValueKind: JsonValueKind.Number } property && property.TryGetInt32(out var number) ? number : 0;
    private static IEnumerable<JsonElement> Items(JsonElement value, string name) =>
        Property(value, name) is { ValueKind: JsonValueKind.Array } property ? property.EnumerateArray() : [];
}
