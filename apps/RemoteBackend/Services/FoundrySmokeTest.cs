using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Azure.Core;
using MeldResearch.Research;

namespace MeldResearch.RemoteBackend.Services;

internal sealed record FoundrySmokeTestOptions(string? ExpectEndpoint, bool ShowResponse);

/// <summary>
/// Live Foundry check through the production analysis path: synthetic evidence, the real prompt and strict
/// schema, server-side citation validation, and embeddings. Consumes model quota; prints no content or
/// credentials unless --show-response is given. It never opens the data directory or starts the web server.
/// </summary>
internal static class FoundrySmokeTest
{
    public const string Switch = "--foundry-smoke-test";

    internal const string SyntheticSource = """
        \section{Pilot study}
        We ran a synthetic pilot study with 40 participants over two weeks.
        Structured weekly feedback improved task completion from 52\% to 61\%.
        \section{Next steps}
        We plan a preregistered replication with 200 participants.
        """;

    /// <summary>Removes the smoke-test switches so the remaining arguments configure the host normally.</summary>
    public static bool TryParse(string[] args, out FoundrySmokeTestOptions? options, out string[] remaining)
    {
        options = null;
        remaining = args;
        if (!args.Contains(Switch, StringComparer.Ordinal)) return false;
        string? expect = null;
        bool show = false;
        var rest = new List<string>();
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case Switch: break;
                case "--show-response": show = true; break;
                case "--expect-endpoint" when index + 1 < args.Length: expect = args[++index]; break;
                case "--expect-endpoint": throw new ArgumentException("--expect-endpoint requires a URL.");
                default: rest.Add(args[index]); break;
            }
        }
        options = new(expect, show);
        remaining = [.. rest];
        return true;
    }

    public static async Task<int> RunAsync(IConfiguration configuration, IHostEnvironment environment, FoundrySmokeTestOptions options)
    {
        using var stopping = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        Console.CancelKeyPress += (_, cancel) => { cancel.Cancel = true; stopping.Cancel(); };
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        return await RunAsync(configuration, environment, options, http, null, Console.Out, Console.Error, stopping.Token);
    }

    internal static async Task<int> RunAsync(IConfiguration configuration, IHostEnvironment environment, FoundrySmokeTestOptions options,
        HttpClient http, TokenCredential? credential, TextWriter output, TextWriter error, CancellationToken ct)
    {
        string endpoint = configuration["Foundry:Endpoint"] ?? "";
        if (options.ExpectEndpoint is { } expected && Normalize(expected) != Normalize(endpoint))
        {
            http.Dispose();
            error.WriteLine("FAIL: the configured Foundry endpoint differs from --expect-endpoint. No model request was sent.");
            return 2;
        }
        FoundryService foundry;
        try { foundry = new FoundryService(configuration, environment, http, credential); }
        catch (InvalidOperationException exception)
        {
            http.Dispose();
            error.WriteLine($"FAIL: invalid Foundry configuration. {exception.Message} No model request was sent.");
            return 2;
        }
        using (foundry)
        {
            if (!foundry.Configured)
            {
                error.WriteLine("FAIL: Foundry:Endpoint, Foundry:ChatDeployment and Foundry:EmbeddingDeployment are required. No model request was sent.");
                return 2;
            }
            output.WriteLine($"Foundry {Normalize(endpoint)}: chat '{configuration["Foundry:ChatDeployment"]}', " +
                $"embeddings '{configuration["Foundry:EmbeddingDeployment"]}', authentication: {Authentication(configuration)}. Credentials hidden.");
            try
            {
                var stopwatch = Stopwatch.StartNew();
                string source = SyntheticSource.ReplaceLineEndings("\n");
                var initial = await foundry.ProfileStepAsync(new(SyntheticProject, "Synthetic smoke test", true, null,
                    new("main.tex", null, source)), ct);
                var update = await foundry.ProfileStepAsync(new(SyntheticProject, "Synthetic smoke test", false, initial.Profile,
                    new("main.tex", source, source + "\n" + SyntheticChange.ReplaceLineEndings("\n"))), ct);
                if (update.Updates.Count == 0)
                    throw new ResearchFailure(502, "empty_update", "The synthetic change produced no grounded Update.");
                output.WriteLine($"PASS: summary steps, strict schema and grounded citations validated, " +
                    $"{update.Updates.Count} update(s), {stopwatch.Elapsed.TotalSeconds:F1}s.");
                if (options.ShowResponse) output.WriteLine(JsonSerializer.Serialize(update, ResearchJson.Options));

                stopwatch.Restart();
                var vectors = await foundry.EmbedAsync(new(["Synthetic cache example.", "Synthetic testing example."]), ct);
                if (vectors.Embeddings is not { Count: 2 } || vectors.Embeddings[0].Count != vectors.Embeddings[1].Count)
                    throw new ResearchFailure(502, "invalid_embeddings", "Embedding validation failed.");
                output.WriteLine($"PASS: embeddings, 2 vectors of {vectors.Embeddings[0].Count} dimensions, {stopwatch.Elapsed.TotalSeconds:F1}s.");
                output.WriteLine($"Foundry smoke test passed: {foundry.ChatRequests} chat and {foundry.EmbeddingRequests} embedding request(s). No other provider was used.");
                return 0;
            }
            catch (ResearchFailure failure)
            {
                error.WriteLine($"FAIL: {failure.Code} (status {failure.Status}). {failure.Message} Model content suppressed; no other provider was used.");
                return 1;
            }
            catch (OperationCanceledException)
            {
                error.WriteLine("FAIL: canceled or timed out. No other provider was used.");
                return 1;
            }
        }
    }

    private const string SyntheticProject = "5e1f0a5e1f0a5e1f0a5e1f0a";
    internal const string SyntheticChange = """
        \section{Replication}
        The preregistered replication with 200 participants is complete; task completion improved from 50\% to 63\%.
        """;

    private static string Authentication(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration["Foundry:ApiKey"]) ? "API key"
        : !string.IsNullOrWhiteSpace(configuration["Foundry:DevelopmentTokenSource"]) ? "development credential source"
        : !string.IsNullOrWhiteSpace(configuration["Foundry:ManagedIdentityClientId"]) ? "user-assigned managed identity"
        : "system-assigned managed identity";

    private static string Normalize(string endpoint) => endpoint.Trim().TrimEnd('/') + "/";
}
