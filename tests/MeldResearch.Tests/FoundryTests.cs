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
            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            limited.Headers.Add("retry-after-ms", "1");
            return Task.FromResult(limited);
        }));
        using var foundry = Create(http);
        var error = await Assert.ThrowsAsync<ResearchFailure>(() => foundry.ProfileStepAsync(Initial("Synthetic research."), CancellationToken.None));
        Assert.Equal(429, error.Status);
        // Retried as a rate limit, then reported; never another provider or an empty success.
        Assert.Equal(FoundryService.MaxAttempts, calls);
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
    public async Task ProjectSummariesSendOnlyTheMainTexStatelesslyAndRejectMalformedParagraphs()
    {
        string? sent = null;
        string reply = "A synthetic study of pilot participants and their completion rates.";
        using var http = new HttpClient(new Handler(async request =>
        {
            sent = await request.Content!.ReadAsStringAsync();
            return Response(new { choices = new[] { new { finish_reason = "stop", message = new { content = JsonSerializer.Serialize(new { summary = reply }) } } } });
        }));
        using var foundry = Create(http);
        var request = new ProjectSummaryRequest("0123456789abcdef01234567", "Synthetic research", "main.tex", "\\documentclass{article}\\begin{document}Pilot.\\end{document}");
        var result = await foundry.SummarizeProjectAsync(request, CancellationToken.None);
        Assert.Equal((reply, "synthetic-chat"), (result.Summary, result.ChatModel));
        using (var body = JsonDocument.Parse(sent!))
        {
            Assert.False(body.RootElement.GetProperty("store").GetBoolean());
            Assert.Contains("Pilot.", body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
        }
        reply = "Two\nlines.";
        Assert.Equal("invalid_project_summary", (await Assert.ThrowsAsync<ResearchFailure>(() => foundry.SummarizeProjectAsync(request, CancellationToken.None))).Code);
        Assert.Equal("invalid_project_summary_request", (await Assert.ThrowsAsync<ResearchFailure>(() =>
            foundry.SummarizeProjectAsync(request with { Path = "notes.md" }, CancellationToken.None))).Code);
    }

    [Fact]
    public async Task RateLimitsAreRetriedAfterRetryAfterAndBusyRequestsWaitTheirTurn()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
                limited.Headers.Add("retry-after-ms", "5");
                return Task.FromResult(limited);
            }
            return Task.FromResult(Step(new(Profile, "Read.", [])));
        }));
        using var foundry = Create(http);
        var result = await foundry.ProfileStepAsync(Initial("Synthetic."), CancellationToken.None);
        Assert.Equal(2, calls);
        Assert.Equal(Json(Profile), Json(result.Profile));

        // With one slot, a second request waits for the first instead of being refused.
        var release = new TaskCompletionSource();
        int started = 0;
        using var slow = new HttpClient(new Handler(async _ =>
        {
            if (Interlocked.Increment(ref started) == 1) await release.Task;
            return Step(new(Profile, "Read.", []));
        }));
        using var single = Create(slow, ("Foundry:MaxConcurrency", "1"));
        var first = single.ProfileStepAsync(Initial("First."), CancellationToken.None);
        var second = single.ProfileStepAsync(Initial("Second."), CancellationToken.None);
        await Task.Delay(100);
        Assert.Equal(1, started);
        Assert.Equal(new InferenceCapacity("synthetic-chat", "synthetic-embedding", 1, 1), single.Capacity);
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(2, started);
    }

    [Fact]
    public async Task AnOversizedSummaryGetsOneSummaryOfTheSummaryAndIsOtherwiseAcceptedAsIs()
    {
        var bodies = new List<string>();
        var tooLong = Profile with { Description = string.Join(" ", Enumerable.Repeat("A long synthetic sentence about the pilot.", 60)),
            Topics = [.. Enumerable.Range(1, 20).Select(index => "topic " + index)] };
        var replies = new Queue<HttpResponseMessage>();
        using var http = new HttpClient(new Handler(async request =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync());
            return replies.Dequeue();
        }));
        using var foundry = Create(http);

        // The shortening request carries only the summary and what is too long, not the file.
        replies.Enqueue(Step(new(tooLong, "Read.", [])));
        replies.Enqueue(Response(new { choices = new[] { new { finish_reason = "stop", message = new { content = JsonSerializer.Serialize(new { profile = Profile }, ResearchJson.Options) } } } }));
        var shortened = await foundry.ProfileStepAsync(Initial("UNIQUE-FILE-TEXT."), CancellationToken.None);
        Assert.Equal(2, bodies.Count);
        Assert.Contains("UNIQUE-FILE-TEXT", bodies[0]);
        Assert.DoesNotContain("UNIQUE-FILE-TEXT", bodies[1]);
        Assert.Contains("at most 12 topics (it has 20)", bodies[1]);
        Assert.Equal(Json(Profile), Json(shortened.Profile));

        // Still too long after shortening: accepted as is, within the hard safety limits.
        replies.Enqueue(Step(new(tooLong, "Read.", [])));
        replies.Enqueue(Response(new { choices = new[] { new { finish_reason = "stop", message = new { content = JsonSerializer.Serialize(new { profile = tooLong }, ResearchJson.Options) } } } }));
        var accepted = await foundry.ProfileStepAsync(Initial("Synthetic."), CancellationToken.None);
        Assert.Equal(Json(tooLong), Json(accepted.Profile));

        // Beyond the hard limits (and shortening failed): trimmed to the targets instead of failing.
        var huge = tooLong with { Description = new string('x', 3000) + ". " + new string('y', 2000) };
        replies.Enqueue(Step(new(huge, "Read.", [])));
        replies.Enqueue(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        var fitted = await foundry.ProfileStepAsync(Initial("Synthetic."), CancellationToken.None);
        Assert.True(fitted.Profile.Description.Length <= ProfileEvidence.MaxDescription);
        Assert.Equal(ProfileEvidence.MaxTopics, fitted.Profile.Topics.Count);

        // An empty title is a failed answer, not a long one: it is still rejected, without a shortening call.
        replies.Enqueue(Step(new(Profile with { Title = "" }, "Read.", [])));
        int before = bodies.Count;
        Assert.Equal("ungrounded_research_analysis", (await Assert.ThrowsAsync<ResearchFailure>(() =>
            foundry.ProfileStepAsync(Initial("Synthetic."), CancellationToken.None))).Code);
        Assert.Equal(before + 1, bodies.Count);
    }

    [Fact]
    public async Task GroupingAcceptsOnlyOfferedCardsAndValidCombinedSummaries()
    {
        string reply = "";
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Response(new { choices = new[] { new { finish_reason = "stop", message = new { content = reply } } } }))));
        using var foundry = Create(http);
        string card = new('a', 32);
        var match = new ThemeMatchRequest("news-slant-slides", Profile, [new(card, "News slant", "Why outlets differ.", ["media"])]);
        reply = JsonSerializer.Serialize(new { cardId = card, explanation = "Slides of the same paper." });
        Assert.Equal(card, (await foundry.MatchThemeAsync(match, CancellationToken.None)).ThemeId);
        reply = JsonSerializer.Serialize(new { cardId = (string?)null, explanation = "Different question." });
        Assert.Null((await foundry.MatchThemeAsync(match, CancellationToken.None)).ThemeId);
        reply = JsonSerializer.Serialize(new { cardId = new string('b', 32), explanation = "Invented." });
        Assert.Equal("invalid_theme_match", (await Assert.ThrowsAsync<ResearchFailure>(() => foundry.MatchThemeAsync(match, CancellationToken.None))).Code);

        string third = new('c', 32);
        var regroup = new ThemeRegroupRequest([new(card, "Scooping in academia", "Disclosure.", []), new(third, "Scooping in drug discovery", "Priority.", [])]);
        reply = JsonSerializer.Serialize(new { groups = new[] { new { cardIds = new[] { card, third }, explanation = "Same programme." } } });
        Assert.Equal([card, third], Assert.Single((await foundry.RegroupThemesAsync(regroup, CancellationToken.None)).Groups).CardIds);
        foreach (var invalid in new object[] { new[] { new { cardIds = new[] { card }, explanation = "Alone." } },
                     new[] { new { cardIds = new[] { card, new string('d', 32) }, explanation = "Invented." } } })
        {
            reply = JsonSerializer.Serialize(new { groups = invalid });
            Assert.Equal("invalid_theme_groups", (await Assert.ThrowsAsync<ResearchFailure>(() => foundry.RegroupThemesAsync(regroup, CancellationToken.None))).Code);
        }

        var merge = new ThemeMergeRequest([new("news-slant", Profile), new("news-slant-slides", Profile)]);
        reply = JsonSerializer.Serialize(new { profile = Profile }, ResearchJson.Options);
        Assert.Equal(Json(Profile), Json((await foundry.MergeThemeAsync(merge, CancellationToken.None)).Profile));
        reply = JsonSerializer.Serialize(new { profile = Profile with { Title = "" } }, ResearchJson.Options);
        Assert.Equal("invalid_theme_summary", (await Assert.ThrowsAsync<ResearchFailure>(() => foundry.MergeThemeAsync(merge, CancellationToken.None))).Code);
        Assert.Equal("invalid_theme_request", (await Assert.ThrowsAsync<ResearchFailure>(() =>
            foundry.MergeThemeAsync(new([new("solo", Profile)]), CancellationToken.None))).Code);
    }

    [Fact]
    public async Task EmailStepsCopyCitedLinesOfNewMessagesAndDropAnythingElse()
    {
        string reply = "";
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Response(new { choices = new[] { new { finish_reason = "stop", message = new { content = reply } } } }))));
        using var foundry = Create(http);
        var now = DateTimeOffset.UtcNow;
        string card = new('a', 32);
        var request = new EmailStepRequest("t1", "Robustness results", "me@example.invalid",
            [new("m2", "Me <me@example.invalid>", now, true, "Lukas,\nthe 200-agent simulations are done.\nTable 4 is in the draft.")],
            [new("m1", "Lukas <lukas@example.invalid>", now.AddDays(-1), false, "Can you run the larger simulations?")], false, [], null,
            [new(card, "Social learning", "A paper.", ["learning"])]);
        object Citation(string id, int start, int end) => new { messageId = id, startLine = start, endLine = end };
        object Bullet(string text, params object[] evidence) => new { text, evidence };
        reply = JsonSerializer.Serialize(new
        {
            decision = "project", projectId = card, suggestedProject = "ignored", newsworthy = true, headline = "Simulations done",
            explanation = "A research result.",
            bullets = new[] { Bullet("The user finished the larger simulations.", Citation("m2", 2, 3), Citation("m1", 1, 1)),
                Bullet("Invented.", Citation("m2", 7, 9), Citation("m9", 1, 1)) }
        }, ResearchJson.Options);
        var response = await foundry.EmailStepAsync(request, CancellationToken.None);
        var citation = Assert.Single(Assert.Single(response.Bullets).Evidence);
        Assert.Equal(("Robustness results", "the 200-agent simulations are done.\nTable 4 is in the draft.", "m2"),
            (citation.Path, citation.Quote, citation.Email!.MessageId));
        Assert.Equal((EmailSteps.Project, card, (string?)null, true, "Simulations done"),
            (response.Decision, response.ProjectId, response.SuggestedProject, response.Newsworthy, response.Headline));

        // Newsworthy without any valid bullet is not newsworthy; a skip carries nothing.
        reply = JsonSerializer.Serialize(new { decision = "unassigned", projectId = (string?)null, suggestedProject = "Robustness work",
            bullets = new[] { Bullet("Invented.", Citation("m9", 1, 1)) }, newsworthy = true, headline = "Invented", explanation = "No fit." }, ResearchJson.Options);
        response = await foundry.EmailStepAsync(request, CancellationToken.None);
        Assert.Equal(("Robustness work", 0, false, (string?)null), (response.SuggestedProject, response.Bullets.Count, response.Newsworthy, response.Headline));
        reply = JsonSerializer.Serialize(new { decision = "skip", projectId = (string?)null, suggestedProject = (string?)null,
            bullets = Array.Empty<object>(), newsworthy = false, headline = (string?)null, explanation = "Scheduling." }, ResearchJson.Options);
        Assert.Equal(EmailSteps.Skip, (await foundry.EmailStepAsync(request, CancellationToken.None)).Decision);

        reply = JsonSerializer.Serialize(new { decision = "project", projectId = new string('e', 32), suggestedProject = (string?)null,
            bullets = Array.Empty<object>(), newsworthy = false, headline = (string?)null, explanation = "Invented." }, ResearchJson.Options);
        Assert.Equal("invalid_email_decision", (await Assert.ThrowsAsync<ResearchFailure>(() => foundry.EmailStepAsync(request, CancellationToken.None))).Code);

        // An email stream summary keeps the stream's name as its title.
        reply = JsonSerializer.Serialize(new { profile = Profile with { Title = "Something else" } }, ResearchJson.Options);
        var stream = await foundry.EmailStreamAsync(new("Email · Social learning", null, [new("Robustness results", "Simulations done", ["The user finished the simulations."])]),
            CancellationToken.None);
        Assert.Equal("Email · Social learning", stream.Profile.Title);
        await Assert.ThrowsAsync<ResearchFailure>(() => foundry.EmailStreamAsync(new("Email", null, []), CancellationToken.None));
    }

    [Fact]
    public async Task DetailStepsCiteSentLinesVerbatimAndIgnoreUnknownDetails()
    {
        string reply = "", sent = "";
        using var http = new HttpClient(new Handler(async message =>
        {
            sent = await message.Content!.ReadAsStringAsync();
            return Response(new { choices = new[] { new { finish_reason = "stop", message = new { content = reply } } } });
        }));
        using var foundry = Create(http);
        var request = new DetailStepRequest(DetailSource.Overleaf, "PCM", "Paying for Information, Not Clicks", "We build a marketplace.",
            [new("d1", "The paper proposes a publisher marketplace.", [new("r1", "main.tex", false, new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero))])],
            ["main.tex [sent]", "sections/model.tex [sent]"],
            [new("main.tex", "\\title{PCM}\nWe build a marketplace.", 1), new("sections/model.tex", "Credit is split by Shapley values.\nPrices are fixed.", 41)]);
        object Op(string op, string? id, string? text, object[] cite, string[] remove) => new { op, id, text, cite, remove };
        object Cite(string item, int start, int end) => new { item, startLine = start, endLine = end };
        reply = JsonSerializer.Serialize(new
        {
            explanation = "Added the attribution rule.",
            ops = new[]
            {
                Op("add", null, "Credit is split among publishers by Shapley values.", [Cite("sections/model.tex", 41, 41), Cite("sections/model.tex", 1, 1)], []),
                Op("add", null, "Invented.", [Cite("other.tex", 1, 1)], []),
                Op("revise", "d1", null, [Cite("main.tex", 2, 2)], ["r1", "r9"]),
                Op("remove", "d9", null, [], [])
            }
        }, ResearchJson.Options);
        var response = await foundry.DetailStepAsync(request, CancellationToken.None);
        // The model sees each citation's date and today's date, so it can tell how old a plan or status is.
        Assert.Contains("2026-09-01", sent);
        Assert.Contains(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), sent);
        // Edits come back in the order they are applied: removals, revisions, additions.
        Assert.Equal([DetailRules.Revise, DetailRules.Add], response.Ops.Select(op => op.Op));
        Assert.Equal(("We build a marketplace.", "r1"), (response.Ops[0].Add.Single().Quote, response.Ops[0].Remove.Single()));
        // Line 41 is the first line of a segment that starts at line 41; line 1 is not part of that segment.
        Assert.Equal(new DetailQuote("sections/model.tex", 41, 41, "Credit is split by Shapley values."), Assert.Single(response.Ops[1].Add));

        // A full list: an addition listed before the removal that makes room for it is still made.
        var full = request with { Details = [.. Enumerable.Range(0, DetailRules.MaxDetails).Select(index => new DetailInput("d" + index, "Detail " + index + ".", [new("c0", "main.tex", false)]))] };
        reply = JsonSerializer.Serialize(new
        {
            explanation = "A new result replaces an old plan.",
            ops = new[] { Op("add", null, "Prices are fixed.", [Cite("sections/model.tex", 42, 42)], []), Op("remove", "d3", null, [], []) }
        }, ResearchJson.Options);
        Assert.Equal([DetailRules.Remove, DetailRules.Add], (await foundry.DetailStepAsync(full, CancellationToken.None)).Ops.Select(op => op.Op));

        reply = JsonSerializer.Serialize(new { title = "ignored", summary = "The paper proposes a marketplace.\nIt is under revision." }, ResearchJson.Options);
        var summary = await foundry.SummarizeDetailsAsync(new("Paying for Information, Not Clicks", null, ["The paper proposes a marketplace."]), CancellationToken.None);
        Assert.Equal(("Paying for Information, Not Clicks", "The paper proposes a marketplace. It is under revision."), (summary.Title, summary.Summary));

        reply = JsonSerializer.Serialize(new { details = new[] { new { text = "Combined.", from = new[] { "a", "zz" } }, new { text = "None.", from = new[] { "zz" } } } },
            ResearchJson.Options);
        var merged = await foundry.MergeDetailsAsync(new([new("A", [new("a", "Detail A.", [])]), new("B", [new("b", "Detail B.", [])])]), CancellationToken.None);
        Assert.Equal(new DetailMerged("Combined.", ["a"]), Assert.Single(merged.Details), new MergedComparer());

        // A review merges and retires details, never inventing ids and never keeping fewer than half of them.
        var review = new DetailReviewRequest("PCM", null, null, [new("a", "Pilot planned.", [new("c0", "email: Plan", false, new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero))]),
            new("b", "Pilot has 80 publishers.", []), new("c", "The pilot has 80 publishers.", []), new("d", "Prices are fixed.", [])]);
        reply = JsonSerializer.Serialize(new { explanation = "Merged the pilot.", details = new[]
            { new { text = "The pilot has 80 publishers.", from = new[] { "b", "c", "zz" } }, new { text = "Prices are fixed.", from = new[] { "d", "b" } } } }, ResearchJson.Options);
        var reviewed = await foundry.ReviewDetailsAsync(review, CancellationToken.None);
        Assert.Equal([new DetailMerged("The pilot has 80 publishers.", ["b", "c"]), new DetailMerged("Prices are fixed.", ["d"])], reviewed.Details, new MergedComparer());
        Assert.Contains("2026-03-01", sent);
        reply = JsonSerializer.Serialize(new { explanation = "Too much.", details = new[] { new { text = "Prices are fixed.", from = new[] { "d" } } } }, ResearchJson.Options);
        Assert.Equal("invalid_detail_review", (await Assert.ThrowsAsync<ResearchFailure>(() => foundry.ReviewDetailsAsync(review, CancellationToken.None))).Code);

        // A Housekeeping group: one headline line without quotes, and a project only if it is one of those offered.
        var group = new WaitingGroupRequest("NBER paper drafting", [new("Literature review slides shared", ["The user shared the slides."]),
            new("Memory paper drafting", [])], [new(new string('a', 32), "Memory paper", "A paper on memory.", [])]);
        reply = JsonSerializer.Serialize(new { headline = "\"Memory paper:\nliterature review and first draft\"", projectId = new string('a', 32), explanation = "Same paper." }, ResearchJson.Options);
        var described = await foundry.DescribeWaitingGroupAsync(group, CancellationToken.None);
        Assert.Equal(("Memory paper: literature review and first draft", new string('a', 32)), (described.Headline, described.ProjectId));
        Assert.Contains("Literature review slides shared", sent);
        reply = JsonSerializer.Serialize(new { headline = "Memory paper", projectId = new string('f', 32), explanation = "Invented." }, ResearchJson.Options);
        Assert.Null((await foundry.DescribeWaitingGroupAsync(group, CancellationToken.None)).ProjectId);
        reply = JsonSerializer.Serialize(new { headline = " ", projectId = (string?)null, explanation = "" }, ResearchJson.Options);
        Assert.Equal("invalid_waiting_group", (await Assert.ThrowsAsync<ResearchFailure>(() => foundry.DescribeWaitingGroupAsync(group, CancellationToken.None))).Code);

        // Thread titles: one plain line each, in order; a wrong count is rejected.
        var titlesRequest = new ThreadTitlesRequest([new("updated pdf", ["The user sent the revised paper."]), new("(no subject)", [])]);
        reply = JsonSerializer.Serialize(new { titles = new[] { "Revised paper sent.", "\"Gemini link shared\"" } }, ResearchJson.Options);
        Assert.Equal(["Revised paper sent", "Gemini link shared"], (await foundry.TitleThreadsAsync(titlesRequest, CancellationToken.None)).Titles);
        reply = JsonSerializer.Serialize(new { titles = new[] { "Only one" } }, ResearchJson.Options);
        Assert.Equal("invalid_thread_titles", (await Assert.ThrowsAsync<ResearchFailure>(() => foundry.TitleThreadsAsync(titlesRequest, CancellationToken.None))).Code);

        // Collaborators: groups of mention ids with a name (or null) and the author flag; the people's text is sent, a malformed answer rejected.
        var people = new CollaboratorsRequest("PCM", ["me@example.invalid"], "L. Bolte ; Sid Satya",
            [new("m1", "Lukas Bolte", "lukas@example.invalid", [Collaborators.From], 2), new("m2", null, "office@example.invalid", [Collaborators.Cc], 1)]);
        reply = JsonSerializer.Serialize(new { people = new object[] { new { name = "Lukas Bolte", mentions = new[] { "m1" }, author = true, involved = true },
            new { name = (string?)null, mentions = Array.Empty<string>(), author = false, involved = false } }, dropped = new[] { "m2" } }, ResearchJson.Options);
        var merged2 = await foundry.CollaboratorsAsync(people with { Mentions = [people.Mentions[0] with { Points = ["I ran the robustness checks."] }, people.Mentions[1]] },
            CancellationToken.None);
        Assert.Equal(("Lukas Bolte", true, true, null), (merged2.People[0].Name, merged2.People[0].Author, merged2.People[0].Involved, merged2.People[1].Name));
        Assert.Contains("I ran the robustness checks.", sent);
        Assert.Equal(["m2"], merged2.Dropped);
        Assert.Contains("L. Bolte ; Sid Satya", sent);
        reply = JsonSerializer.Serialize(new { people = "none" }, ResearchJson.Options);
        Assert.Equal("invalid_collaborators", (await Assert.ThrowsAsync<ResearchFailure>(() => foundry.CollaboratorsAsync(people, CancellationToken.None))).Code);

        // A short name is returned only when asked (older clients do not know the field).
        reply = JsonSerializer.Serialize(new { title = "x", shortName = "Publisher Content Marketplace (PCM)", summary = "One. Two." }, ResearchJson.Options);
        Assert.Null((await foundry.SummarizeDetailsAsync(new("Paying for Information", null, ["A."]), CancellationToken.None)).ShortName);
        Assert.Equal("Publisher Content Marketplace (PCM)",
            (await foundry.SummarizeDetailsAsync(new("Paying for Information", null, ["A."], ShortName: true), CancellationToken.None)).ShortName);
        Assert.DoesNotContain("shortName", JsonSerializer.Serialize(new DetailSummaryResponse("t", "s", "chat"), ResearchJson.Options));
    }

    private sealed class MergedComparer : IEqualityComparer<DetailMerged>
    {
        public bool Equals(DetailMerged? x, DetailMerged? y) => x!.Text == y!.Text && x.From.SequenceEqual(y.From);
        public int GetHashCode(DetailMerged obj) => obj.Text.GetHashCode();
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

    private static FoundryService Create(HttpClient http, params (string Key, string Value)[] extra)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Foundry:Endpoint"] = "https://synthetic.openai.azure.com/openai/v1/",
            ["Foundry:ChatDeployment"] = "synthetic-chat", ["Foundry:EmbeddingDeployment"] = "synthetic-embedding",
            ["Foundry:ApiKey"] = "synthetic-test-only"
        };
        foreach (var (key, value) in extra) settings[key] = value;
        return new(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), new Environment("Development"), http, null);
    }
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
