using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MeldResearch.RemoteBackend.Services;
using MeldResearch.Research;

namespace MeldResearch.Tests;

public sealed class OpenAlexTests
{
    private const string Authors = """
        {"results":[
          {"id":"https://openalex.org/A123","display_name":"Synthetic Researcher","orcid":null,"works_count":42,
           "display_name_alternatives":["S. Researcher"],
           "last_known_institutions":[{"display_name":"Synthetic University"}],
           "affiliations":[{"institution":{"display_name":"Synthetic University"}},{"institution":{"display_name":"Other Lab"}}]},
          {"id":"https://openalex.org/not-an-author","display_name":"Ignored"}
        ]}
        """;
    private const string Works = """
        {"meta":{"next_cursor":null},"results":[
          {"id":"https://openalex.org/W9","title":"A synthetic paper","publication_year":2025,"publication_date":"2025-03-01",
           "authorships":[{"author":{"display_name":"Synthetic Researcher"}}],
           "primary_location":{"source":{"display_name":"Synthetic Journal"}},"doi":"https://doi.org/10.0/x",
           "abstract_inverted_index":{"Synthetic":[0],"results":[1],"hold.":[2]},"is_retracted":false}
        ]}
        """;

    private static OpenAlexClient Client(Func<HttpRequestMessage, string> respond, string? key = "synthetic-key", List<HttpRequestMessage>? seen = null) =>
        new(new Clients(new Handler(request =>
        {
            seen?.Add(request);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(request), Encoding.UTF8, "application/json") };
        })), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["OpenAlex:ApiKey"] = key }).Build(),
            NullLogger<OpenAlexClient>.Instance);

    [Fact]
    public async Task ParsesAuthorsAndWorksAndRebuildsAbstracts()
    {
        var seen = new List<HttpRequestMessage>();
        var client = Client(request => request.RequestUri!.AbsolutePath == "/authors" ? Authors : Works, seen: seen);
        var author = Assert.Single(await client.SearchAuthorsAsync(["Synthetic Researcher", "synthetic researcher"], CancellationToken.None));
        Assert.Equal(("A123", 42), (author.Id, author.WorksCount));
        Assert.Equal(["Synthetic University", "Other Lab"], author.Affiliations);
        Assert.Single(seen); // Case-insensitive duplicate names are searched once.
        Assert.Equal("Bearer synthetic-key", seen[0].Headers.Authorization!.ToString());

        var work = Assert.Single(await client.ReadPublicationsAsync("A123", CancellationToken.None));
        Assert.Equal(("W9", "Synthetic results hold.", "Synthetic Journal"), (work.Id, work.Abstract, work.Venue));
        Assert.Contains("filter=author.id:A123", seen[1].RequestUri!.Query);
        await Assert.ThrowsAsync<ResearchFailure>(() => client.ReadPublicationsAsync("../authors", CancellationToken.None));
    }

    [Fact]
    public async Task RateLimitsAreReportedAsRetryableFailures()
    {
        var client = new OpenAlexClient(new Clients(new Handler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests))),
            new ConfigurationBuilder().Build(), NullLogger<OpenAlexClient>.Instance);
        var failure = await Assert.ThrowsAsync<ResearchFailure>(() => client.SearchAuthorsAsync(["Synthetic"], CancellationToken.None));
        Assert.Equal((429, "openalex_rate_limited"), (failure.Status, failure.Code));
    }

    [Fact]
    public async Task TheModelOnlyRanksRealCandidatesAndNeverInventsAnAuthor()
    {
        var openAlex = Client(request => request.RequestUri!.AbsolutePath == "/authors" ? Authors : Works);
        var invented = new Resolver(new FakeFoundry("""{"authorIds":["A999"]}"""), openAlex);
        Assert.Equal("invalid_researcher_ranking",
            (await Assert.ThrowsAsync<ResearchFailure>(() => invented.ResolveAsync("Synthetic Researcher", CancellationToken.None))).Code);

        var ranked = await new Resolver(new FakeFoundry("""{"authorIds":["A123"]}"""), openAlex).ResolveAsync("Synthetic Researcher", CancellationToken.None);
        Assert.Equal("A123", Assert.Single(ranked).Id);
        Assert.Equal("A synthetic paper", Assert.Single(ranked[0].SamplePublications).Title);
    }

    private sealed class Resolver(IFoundryService foundry, IOpenAlexClient openAlex)
    {
        private readonly ResearcherResolver _inner = new(openAlex, foundry, NullLogger<ResearcherResolver>.Instance);
        public Task<IReadOnlyList<ResearcherCandidate>> ResolveAsync(string name, CancellationToken ct) => _inner.ResolveAsync(name, ct);
    }

    private sealed class FakeFoundry(string ranking) : IFoundryService
    {
        public bool Configured => true;
        public long ChatRequests => 0;
        public long EmbeddingRequests => 0;
        public InferenceCapacity Capacity => new("chat", "embedding", 0, 2);
        public Task<ProfileStepResponse> ProfileStepAsync(ProfileStepRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<ResearchEmbeddingResponse> EmbedAsync(ResearchEmbeddingRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<JsonElement> CompleteJsonAsync(string system, string schemaName, string schema, object input, CancellationToken ct) =>
            Task.FromResult(JsonDocument.Parse(schemaName == "researcher_name_variants" ? """{"names":[]}""" : ranking).RootElement.Clone());
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
