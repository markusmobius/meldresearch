using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MeldResearch.RemoteBackend.Data;
using MeldResearch.RemoteBackend.Services;
using MeldResearch.Research;

namespace MeldResearch.Tests;

public sealed class InferenceUsageTests : IDisposable
{
    private const string SourceSentinel = "UNIQUE-SOURCE-SENTINEL participants";
    private readonly TemporaryDirectory _temporary = new();

    [Fact]
    public async Task RecordsTokensByPurposeAndOperationWithoutContent()
    {
        var usage = await StoreAsync();
        var responses = new Queue<HttpResponseMessage>([
            Json(new { choices = Choices(), usage = new { prompt_tokens = 1200, completion_tokens = 300, total_tokens = 1500 } }),
            Json(new { choices = Choices(), usage = new { prompt_tokens = 500, completion_tokens = 100 } }),
            Json(new { data = new[] { new { index = 0, embedding = new[] { 1f, 0f } } }, usage = new { prompt_tokens = 8, total_tokens = 8 } }),
            new(HttpStatusCode.TooManyRequests) { Content = new StringContent("""{"error":{"code":"RateLimitReached"}}""", Encoding.UTF8, "application/json") },
            Json(new { choices = Choices() }),
        ]);
        using var http = new HttpClient(new Handler(_ => responses.Dequeue()));
        using var foundry = Foundry(http, usage);

        await foundry.ProfileStepAsync(new("0123456789abcdef01234567", "Synthetic research", true, null,
            new("main.tex", null, SourceSentinel)), CancellationToken.None);
        var profile = new ProjectProfile("Synthetic pilot", "A synthetic pilot study.", ["40 participants."], ["pilot"]);
        await foundry.ProfileStepAsync(new("0123456789abcdef01234567", "Synthetic research", false, profile,
            new("main.tex", "Before.", SourceSentinel)), CancellationToken.None);
        await foundry.EmbedAsync(new([SourceSentinel]), CancellationToken.None);
        await Assert.ThrowsAsync<ResearchFailure>(() => foundry.ProfileStepAsync(new("0123456789abcdef01234567", "Synthetic research", false,
            profile, new("main.tex", "Before.", SourceSentinel)), CancellationToken.None));
        // Researcher matching: a response without a usage block counts as unknown usage.
        await foundry.CompleteJsonAsync("system", "synthetic", """{"type":"object"}""", new { name = "Synthetic" }, CancellationToken.None);

        var now = DateTimeOffset.UtcNow;
        var result = await usage.QueryAsync(now.AddHours(-1), now.AddMinutes(1), null, null, foundry.Capacity, CancellationToken.None);
        Assert.Equal(1, result.BucketMinutes);
        Assert.Equal(["synthetic-chat", "synthetic-embedding"], result.Deployments);
        Assert.Equal(new InferenceCapacity("synthetic-chat", "synthetic-embedding", 0, 2), result.Capacity);
        long Tokens(InferencePurpose purpose, InferenceOperation operation, Func<InferenceUsageBucket, long> value) =>
            result.Buckets.Where(item => item.Purpose == purpose && item.Operation == operation).Sum(value);
        Assert.Equal(1200, Tokens(InferencePurpose.ProfileImport, InferenceOperation.Chat, item => item.PromptTokens));
        Assert.Equal(300, Tokens(InferencePurpose.ProfileImport, InferenceOperation.Chat, item => item.CompletionTokens));
        Assert.Equal(500, Tokens(InferencePurpose.ProfileUpdate, InferenceOperation.Chat, item => item.PromptTokens));
        Assert.Equal(2, Tokens(InferencePurpose.ProfileUpdate, InferenceOperation.Chat, item => item.Requests));
        Assert.Equal(1, Tokens(InferencePurpose.ProfileUpdate, InferenceOperation.Chat, item => item.RateLimitedRequests));
        Assert.Equal(1, Tokens(InferencePurpose.ProfileUpdate, InferenceOperation.Chat, item => item.FailedRequests));
        Assert.Equal(8, Tokens(InferencePurpose.Other, InferenceOperation.Embedding, item => item.PromptTokens));
        Assert.Equal(1, Tokens(InferencePurpose.ResearcherMatching, InferenceOperation.Chat, item => item.UnknownUsageRequests));
        Assert.Equal(5, result.Buckets.Sum(item => item.Requests));

        var filtered = await usage.QueryAsync(now.AddHours(-1), now.AddMinutes(1), "synthetic-chat", InferencePurpose.ProfileImport,
            foundry.Capacity, CancellationToken.None);
        Assert.Equal(1, filtered.Buckets.Sum(item => item.Requests));

        SqliteConnection.ClearAllPools();
        string stored = string.Concat(Directory.GetFiles(_temporary.Path, RemoteDatabase.FileName + "*")
            .Select(path => Encoding.UTF8.GetString(File.ReadAllBytes(path))));
        Assert.DoesNotContain("UNIQUE-SOURCE-SENTINEL", stored);
    }

    [Fact]
    public async Task ChoosesBucketsLikeProjectMeldAndRejectsInvalidRanges()
    {
        var start = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(1, InferenceUsageStore.BucketMinutes(start, start.AddDays(2)));
        Assert.Equal(60, InferenceUsageStore.BucketMinutes(start, start.AddDays(30)));
        Assert.Equal(1440, InferenceUsageStore.BucketMinutes(start, start.AddDays(365)));
        var usage = await StoreAsync();
        var capacity = new InferenceCapacity("chat", "embedding", 0, 2);
        await Assert.ThrowsAsync<ResearchFailure>(() => usage.QueryAsync(start, start, null, null, capacity, CancellationToken.None));
        await Assert.ThrowsAsync<ResearchFailure>(() => usage.QueryAsync(start, start.AddDays(367), null, null, capacity, CancellationToken.None));

        await usage.RecordAsync(new(start.AddHours(1).AddMinutes(10), "chat", InferenceOperation.Chat, InferencePurpose.ProfileUpdate,
            200, 10, 5, TimeSpan.FromSeconds(1)), CancellationToken.None);
        await usage.RecordAsync(new(start.AddHours(1).AddMinutes(50), "chat", InferenceOperation.Chat, InferencePurpose.ProfileUpdate,
            200, 20, 5, TimeSpan.FromSeconds(1)), CancellationToken.None);
        var hourly = await usage.QueryAsync(start, start.AddDays(7), null, null, capacity, CancellationToken.None);
        var bucket = Assert.Single(hourly.Buckets);
        Assert.Equal((start.AddHours(1), 30L, 10L, 2L), (bucket.Start, bucket.PromptTokens, bucket.CompletionTokens, bucket.Requests));
    }

    [Theory]
    [InlineData(false, "google", "true", "MarkusMobius@Gmail.com", true)]
    [InlineData(false, "google", null, "markusmobius@gmail.com", false)]
    [InlineData(false, "google", "true", "someone@example.invalid", false)]
    [InlineData(false, "dummy", "true", "markusmobius@gmail.com", false)]
    [InlineData(true, "dummy", "true", "researcher@example.invalid", true)]
    [InlineData(true, "google", "true", "markusmobius@gmail.com", false)]
    public void OnlyAVerifiedGoogleSuperuserOrTheDummyAccountSeesUsage(bool dummy, string provider, string? verified, string email, bool expected)
    {
        var options = RemoteOptions.Load(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Dummy"] = dummy.ToString(), ["PublicOrigin"] = "http://127.0.0.1:6050", ["DataDirectory"] = _temporary.Path
        }).Build(), new Environment("Development"));
        List<Claim> claims = [new(ClaimTypes.Email, email), new("meld_provider", provider)];
        if (verified is not null) claims.Add(new("meld_email_verified", verified));
        Assert.Equal(expected, options.IsSuperuser(new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies"))));
        Assert.False(options.IsSuperuser(new ClaimsPrincipal(new ClaimsIdentity(claims))));
    }

    [Fact]
    public void TheSuperuserListIsConfigurable()
    {
        var options = RemoteOptions.Load(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Superusers"] = "operator@example.invalid; second@example.invalid", ["DataDirectory"] = _temporary.Path
        }).Build(), new Environment("Production"));
        ClaimsPrincipal User(string email) => new(new ClaimsIdentity([new(ClaimTypes.Email, email), new("meld_provider", "google"),
            new("meld_email_verified", "true")], "Cookies"));
        Assert.True(options.IsSuperuser(User("second@example.invalid")));
        Assert.False(options.IsSuperuser(User(RemoteOptions.DefaultSuperuser)));
    }

    private async Task<InferenceUsageStore> StoreAsync()
    {
        var databases = new TestDatabases(RemoteDatabase.ConnectionString(_temporary.Path));
        await RemoteDatabase.InitializeAsync(databases, _temporary.Path, CancellationToken.None);
        return new InferenceUsageStore(databases, TimeProvider.System, NullLogger<InferenceUsageStore>.Instance);
    }

    private static FoundryService Foundry(HttpClient http, InferenceUsageStore usage) => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Foundry:Endpoint"] = "https://synthetic.openai.azure.com/openai/v1/",
            ["Foundry:ChatDeployment"] = "synthetic-chat", ["Foundry:EmbeddingDeployment"] = "synthetic-embedding",
            ["Foundry:ApiKey"] = "synthetic-test-only"
        }).Build(), new Environment("Development"), http, null, null, usage);

    private static object[] Choices() => [new { finish_reason = "stop", message = new { content = JsonSerializer.Serialize(
        new ModelStep(new("Synthetic pilot", "A synthetic pilot study.", ["40 participants."], ["pilot"]), "Read.", []), ResearchJson.Options) } }];

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temporary.Dispose();
    }

    private sealed class TestDatabases(string connection) : IDbContextFactory<RemoteDbContext>
    {
        private readonly DbContextOptions<RemoteDbContext> _options = new DbContextOptionsBuilder<RemoteDbContext>().UseSqlite(connection).Options;
        public RemoteDbContext CreateDbContext() => new(_options);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "MeldResearch.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
