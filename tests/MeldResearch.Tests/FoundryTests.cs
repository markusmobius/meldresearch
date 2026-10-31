using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using MeldResearch.RemoteBackend.Services;
using MeldResearch.Research;

namespace MeldResearch.Tests;

public sealed class FoundryTests
{
    private static readonly ProjectProfile Profile = new("Synthetic pilot", "A synthetic pilot study.", ["40 participants."], ["pilot"]);
    private static ProfileStepRequest Initial(string text) =>
        new("0123456789abcdef01234567", "Synthetic research", true, null, new("main.tex", null, text));
    private static ProfileStepRequest Update(string before, string after) =>
        new("0123456789abcdef01234567", "Synthetic research", false, Profile, new("main.tex", before, after));
    private static string Json(ProjectProfile profile) => JsonSerializer.Serialize(profile, ResearchJson.Options);
    private static HttpResponseMessage Step(ModelStep step) => Response(new { choices = new[] { new { finish_reason = "stop",
        message = new { content = JsonSerializer.Serialize(step, ResearchJson.Options) } } } });

    [Fact]
    public async Task UsesStrictStatelessFoundryChatAndValidatesTheSummaryStep()
    {
        var routes = new List<string>();
        string? sent = null;
        using var http = new HttpClient(new Handler(async request =>
        {
            routes.Add(request.RequestUri!.AbsolutePath);
            Assert.Equal("synthetic-test-only", request.Headers.GetValues("api-key").Single());
            sent = await request.Content!.ReadAsStringAsync();
            using var body = JsonDocument.Parse(sent);
            Assert.Equal("synthetic-chat", body.RootElement.GetProperty("model").GetString());
            Assert.False(body.RootElement.GetProperty("store").GetBoolean(), "Foundry chat must disable stored completions.");
            Assert.True(body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean());
            return Step(new(Profile, "Read the pilot.", []));
        }));
        using var foundry = Create(http);
        var result = await foundry.ProfileStepAsync(Initial("The synthetic pilot has 40 participants."), CancellationToken.None);
        Assert.Equal(["/openai/v1/chat/completions"], routes);
        Assert.Equal(Json(Profile), Json(result.Profile));
        Assert.Empty(result.Updates);
        Assert.Equal("synthetic-chat", result.ChatModel);
        Assert.Contains("1\\tThe synthetic pilot has 40 participants.",
            JsonDocument.Parse(sent!).RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("First line.\r\nA meaningful synthetic result.\r\nLast line.", 2, 2, "A meaningful synthetic result.")]
    [InlineData("First line.\nA meaningful synthetic result.\nLast line.", 2, 2, "A meaningful synthetic result.")]
    [InlineData("First line.\r\nA meaningful synthetic result.\r\nLast line.", 1, 2, "First line.\r\nA meaningful synthetic result.")]
    public async Task BuildsVerbatimCitationsFromSelectedSourceLines(string text, int start, int end, string expected)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Step(new(Profile, "A new result.",
            [new("Result", "A supported synthetic result.", [new(start, end, EvidenceVersion.Current)])])))));
        using var foundry = Create(http);
        var result = await foundry.ProfileStepAsync(Update("First line.", text), CancellationToken.None);
        var citation = Assert.Single(Assert.Single(result.Updates).Evidence);
        Assert.Equal((expected, "main.tex"), (citation.Quote, citation.Path));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 20)]
    public async Task RejectsInvalidModelLineRanges(int start, int end)
    {
        int calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            calls++;
            return Task.FromResult(Step(new(Profile, "Synthetic.", [new("Synthetic", "Synthetic.", [new(start, end, EvidenceVersion.Current)])])));
        }));
        using var foundry = Create(http);
        var error = await Assert.ThrowsAsync<ResearchFailure>(() => foundry.ProfileStepAsync(
            Update("Old.", "A synthetic result."), CancellationToken.None));
        Assert.Equal("invalid_source_citation", error.Code);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task InitialStepsNeverReportUpdatesAndOversizedSummariesAreShortenedOnce()
    {
        var huge = Profile with { Description = new string('d', 1500), Contributions = Enumerable.Repeat(new string('c', 400), 12).ToArray() };
        int calls = 0;
        using var http = new HttpClient(new Handler(_ => Task.FromResult(++calls == 1 ? Step(new(huge, "Too long.", [])) : Step(new(Profile, "Shorter.", [])))));
        using var foundry = Create(http);
        var result = await foundry.ProfileStepAsync(Initial("Synthetic."), CancellationToken.None);
        Assert.Equal((2, Json(Profile)), (calls, Json(result.Profile)));

        using var reporting = new HttpClient(new Handler(_ => Task.FromResult(Step(new(Profile, "Initial.",
            [new("Not news", "An initial import is not an update.", [new(1, 1, EvidenceVersion.Current)])])))));
        using var strict = Create(reporting);
        Assert.Equal("ungrounded_research_analysis",
            (await Assert.ThrowsAsync<ResearchFailure>(() => strict.ProfileStepAsync(Initial("Synthetic."), CancellationToken.None))).Code);
    }

    [Fact]
    public async Task ProviderErrorsDoNotFallbackOrBecomeAnEmptySuccessfulStep()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        }));
        using var foundry = Create(http);
        var error = await Assert.ThrowsAsync<ResearchFailure>(() => foundry.ProfileStepAsync(Initial("Synthetic research."), CancellationToken.None));
        Assert.Equal(429, error.Status);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void RejectsDevelopmentCredentialSourcesInProduction()
    {
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException()));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Foundry:Endpoint"] = "https://synthetic.openai.azure.com/openai/v1/",
            ["Foundry:DevelopmentTokenSource"] = "https://credentials.example.invalid/api/inference/token"
        }).Build();
        Assert.Throws<InvalidOperationException>(() => new FoundryService(configuration, new Environment("Production"), http, null));
    }

    [Theory]
    [InlineData("Trapi", "https://synthetic.openai.azure.com/openai/v1/chat/completions")]
    [InlineData("Foundry", "https://different.example.invalid/openai/v1/chat/completions")]
    public async Task RejectsNonFoundryOrUnpinnedDevelopmentCredentialsBeforeAnyModelCall(string provider, string endpoint)
    {
        int calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++;
            Assert.Equal("/api/inference/token", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Response(new
            {
                provider, token = "synthetic-development-token", expiresOn = DateTimeOffset.UtcNow.AddHours(1),
                endpoint, embeddingEndpoint = "https://synthetic.openai.azure.com/openai/v1/embeddings",
                chatDeployment = "synthetic-chat", embeddingDeployment = "synthetic-embedding"
            }));
        }));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Foundry:Endpoint"] = "https://synthetic.openai.azure.com/openai/v1/",
            ["Foundry:ChatDeployment"] = "synthetic-chat", ["Foundry:EmbeddingDeployment"] = "synthetic-embedding",
            ["Foundry:DevelopmentTokenSource"] = "https://credentials.example.invalid/api/inference/token"
        }).Build();
        using var foundry = new FoundryService(configuration, new Environment("Development"), http, null);
        var error = await Assert.ThrowsAsync<ResearchFailure>(() => foundry.ProfileStepAsync(Initial("Synthetic research."), CancellationToken.None));
        Assert.Equal("foundry_configuration_mismatch", error.Code);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ManagedIdentityUsesFoundryBearerScopeAndNeverFallsBack()
    {
        var credential = new Credential();
        var authorizations = new List<string>();
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.False(request.Headers.Contains("api-key"));
            authorizations.Add(request.Headers.Authorization!.ToString());
            return await SyntheticFoundry(request);
        }));
        using var foundry = new FoundryService(ManagedIdentityConfiguration(), new Environment("Production"), http, credential);
        await foundry.EmbedAsync(new(["Synthetic text."]), CancellationToken.None);
        Assert.Equal("Bearer synthetic-managed-identity-token", Assert.Single(authorizations));
        Assert.Equal(["https://ai.azure.com/.default"], Assert.Single(credential.Scopes));

        int calls = 0;
        using var unreachable = new HttpClient(new Handler(_ => { calls++; throw new InvalidOperationException(); }));
        using var failing = new FoundryService(ManagedIdentityConfiguration(), new Environment("Production"), unreachable,
            new Credential(new Azure.Identity.AuthenticationFailedException("synthetic identity failure")));
        var error = await Assert.ThrowsAsync<ResearchFailure>(() => failing.EmbedAsync(new(["Synthetic text."]), CancellationToken.None));
        Assert.Equal("foundry_credentials_unavailable", error.Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RejectionsNameFoundrysStatusAndErrorCodeButNeverItsMessage()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"error":{"code":"PermissionDenied","message":"SECRET-SENTINEL The principal lacks the role."}}""",
                System.Text.Encoding.UTF8, "application/json")
        })));
        using var foundry = new FoundryService(ManagedIdentityConfiguration(), new Environment("Production"), http, new Credential());
        var error = await Assert.ThrowsAsync<ResearchFailure>(() => foundry.EmbedAsync(new(["Synthetic text."]), CancellationToken.None));
        Assert.Equal("foundry_request_failed", error.Code);
        Assert.Contains("HTTP 403, PermissionDenied", error.Message);
        Assert.DoesNotContain("SECRET-SENTINEL", error.Message);
    }

    [Fact]
    public async Task SmokeTestUsesTheProductionPathAndPrintsNoContentOrCredentials()
    {
        int calls = 0;
        using var output = new StringWriter();
        using var error = new StringWriter();
        var http = new HttpClient(new Handler(request => { calls++; return SyntheticFoundry(request); }));
        int code = await FoundrySmokeTest.RunAsync(ManagedIdentityConfiguration(), new Environment("Production"),
            new("https://synthetic.openai.azure.com/openai/v1", false), http, new Credential(), output, error, CancellationToken.None);
        Assert.True(code == 0, error.ToString());
        Assert.Equal(3, calls);
        string printed = output.ToString();
        Assert.Contains("user-assigned managed identity", printed);
        Assert.Contains("PASS: summary steps", printed);
        Assert.Contains("PASS: embeddings, 2 vectors of 3 dimensions", printed);
        foreach (string hidden in new[] { "40 participants", "Synthetic pilot", "synthetic-managed-identity-token" })
            Assert.DoesNotContain(hidden, printed);
    }

    [Fact]
    public async Task SmokeTestRefusesAnUnexpectedEndpointAndReportsProviderErrorsWithoutContent()
    {
        int calls = 0;
        var http = new HttpClient(new Handler(_ => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)); }));
        using var error = new StringWriter();
        Assert.Equal(2, await FoundrySmokeTest.RunAsync(ManagedIdentityConfiguration(), new Environment("Production"),
            new("https://other.openai.azure.com/openai/v1/", false), http, new Credential(), TextWriter.Null, error, CancellationToken.None));
        Assert.Equal(0, calls);

        http = new HttpClient(new Handler(_ => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)); }));
        error.GetStringBuilder().Clear();
        Assert.Equal(1, await FoundrySmokeTest.RunAsync(ManagedIdentityConfiguration(), new Environment("Production"),
            new(null, false), http, new Credential(), TextWriter.Null, error, CancellationToken.None));
        Assert.Equal(1, calls);
        Assert.Contains("foundry_request_failed", error.ToString());
    }

    [Fact]
    public void SmokeTestSwitchesAreRemovedBeforeHostConfiguration()
    {
        Assert.False(FoundrySmokeTest.TryParse(["--Port", "6050"], out _, out var unchanged));
        Assert.Equal(["--Port", "6050"], unchanged);
        Assert.True(FoundrySmokeTest.TryParse(["--foundry-smoke-test", "--environment", "Development",
            "--expect-endpoint", "https://synthetic.openai.azure.com/openai/v1/", "--show-response"], out var options, out var rest));
        Assert.Equal(new FoundrySmokeTestOptions("https://synthetic.openai.azure.com/openai/v1/", true), options);
        Assert.Equal(["--environment", "Development"], rest);
    }

    private static IConfiguration ManagedIdentityConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Foundry:Endpoint"] = "https://synthetic.openai.azure.com/openai/v1/",
            ["Foundry:ChatDeployment"] = "synthetic-chat", ["Foundry:EmbeddingDeployment"] = "synthetic-embedding",
            ["Foundry:ManagedIdentityClientId"] = "00000000-0000-0000-0000-000000000001"
        }).Build();

    // Answers like Foundry: a grounded change citing line 2 of the smoke-test source, and one vector per input.
    private static async Task<HttpResponseMessage> SyntheticFoundry(HttpRequestMessage request)
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        if (request.RequestUri!.AbsolutePath.EndsWith("/embeddings", StringComparison.Ordinal))
            return Response(new { data = body.RootElement.GetProperty("input").EnumerateArray()
                .Select((_, index) => new { index, embedding = new[] { 1f, 0f, 0f } }).ToArray() });
        // An update cites the replication paragraph the smoke test appends (the last line of the current text).
        string user = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        bool update = user.Contains("\"mode\":\"update\"", StringComparison.Ordinal);
        int last = JsonDocument.Parse(user).RootElement.GetProperty("file").GetProperty("currentLines").GetArrayLength();
        return Step(new(new("Synthetic pilot", "A synthetic pilot study.", ["Weekly feedback improved completion."], ["pilot"]),
            "Synthetic.", update ? [new("Replication", "A replication was completed.", [new(last, last, EvidenceVersion.Current)])] : []));
    }

    private sealed class Credential(Exception? failure = null) : TokenCredential
    {
        public List<string[]> Scopes { get; } = [];
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken ct) =>
            GetTokenAsync(context, ct).AsTask().GetAwaiter().GetResult();
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken ct)
        {
            Scopes.Add(context.Scopes);
            if (failure is not null) throw failure;
            return ValueTask.FromResult(new AccessToken("synthetic-managed-identity-token", DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    private static FoundryService Create(HttpClient http) => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Foundry:Endpoint"] = "https://synthetic.openai.azure.com/openai/v1/",
            ["Foundry:ChatDeployment"] = "synthetic-chat", ["Foundry:EmbeddingDeployment"] = "synthetic-embedding",
            ["Foundry:ApiKey"] = "synthetic-test-only"
        }).Build(), new Environment("Development"), http, null);
    private static HttpResponseMessage Response(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => callback(request);
    }
    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "MeldResearch.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
