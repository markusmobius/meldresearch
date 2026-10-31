using MeldResearch.RemoteBackend.Data;
using Microsoft.EntityFrameworkCore;

namespace MeldResearch.RemoteBackend.Services;

internal enum InferenceOperation { Chat, Embedding }

/// <summary>Why a model request was made, for the superuser's usage dashboard (as in Project Meld).</summary>
internal enum InferencePurpose { ProfileImport, ProfileUpdate, ResearcherMatching, Other }

internal sealed record InferenceUsageEntry(DateTimeOffset At, string Deployment, InferenceOperation Operation, InferencePurpose Purpose,
    int StatusCode, long? PromptTokens, long? CompletionTokens, TimeSpan Duration);

internal sealed record InferenceUsageBucket(DateTimeOffset Start, string Deployment, InferenceOperation Operation, InferencePurpose Purpose,
    long PromptTokens, long CompletionTokens, long Requests, long FailedRequests, long RateLimitedRequests, long UnknownUsageRequests);

internal sealed record InferenceCapacity(string ChatDeployment, string EmbeddingDeployment, int Active, int MaxConcurrency);

internal sealed record InferenceUsageResponse(DateTimeOffset From, DateTimeOffset To, int BucketMinutes,
    IReadOnlyList<InferenceUsageBucket> Buckets, IReadOnlyList<string> Deployments, InferenceCapacity Capacity);

/// <summary>
/// Records one row per Foundry request (time, deployment, operation, purpose, status, token counts, duration) and
/// aggregates them for the superuser's LLM usage dashboard. No prompt, response or account is stored. Recording never
/// fails the model request it describes.
/// </summary>
internal sealed class InferenceUsageStore(IDbContextFactory<RemoteDbContext> databases, TimeProvider clock, ILogger<InferenceUsageStore> logger)
{
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(400);
    internal static readonly TimeSpan MaximumRange = TimeSpan.FromDays(366);
    private long _prunedAtMs;

    public async Task RecordAsync(InferenceUsageEntry entry, CancellationToken ct)
    {
        try
        {
            await using var database = await databases.CreateDbContextAsync(ct);
            database.InferenceUsage.Add(new()
            {
                AtMs = entry.At.ToUnixTimeMilliseconds(), Deployment = entry.Deployment, Operation = entry.Operation.ToString(),
                Purpose = entry.Purpose.ToString(), StatusCode = entry.StatusCode, PromptTokens = entry.PromptTokens,
                CompletionTokens = entry.CompletionTokens, DurationMs = (int)Math.Clamp(entry.Duration.TotalMilliseconds, 0, int.MaxValue)
            });
            await database.SaveChangesAsync(ct);
            long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            if (now - Interlocked.Read(ref _prunedAtMs) > TimeSpan.FromDays(1).TotalMilliseconds)
            {
                Interlocked.Exchange(ref _prunedAtMs, now);
                long cutoff = now - (long)Retention.TotalMilliseconds;
                await database.InferenceUsage.Where(item => item.AtMs < cutoff).ExecuteDeleteAsync(ct);
            }
        }
        catch (Exception error) when (error is DbUpdateException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or OperationCanceledException)
        {
            logger.LogWarning("Could not record LLM usage ({Type}).", error.GetType().Name);
        }
    }

    /// <summary>Buckets of 1 minute up to 2 days, 1 hour up to 90 days, otherwise 1 day (as in Project Meld).</summary>
    public static int BucketMinutes(DateTimeOffset from, DateTimeOffset to) =>
        (to - from).TotalDays <= 2 ? 1 : (to - from).TotalDays <= 90 ? 60 : 1440;

    public async Task<InferenceUsageResponse> QueryAsync(DateTimeOffset from, DateTimeOffset to, string? deployment,
        InferencePurpose? purpose, InferenceCapacity capacity, CancellationToken ct)
    {
        if (from >= to || to - from > MaximumRange)
            throw new MeldResearch.Research.ResearchFailure(400, "invalid_usage_range", "Choose an increasing date range of at most 366 days.");
        int minutes = BucketMinutes(from, to);
        long step = minutes * 60_000L, start = from.ToUnixTimeMilliseconds(), end = to.ToUnixTimeMilliseconds();
        string? purposeName = purpose?.ToString();
        await using var database = await databases.CreateDbContextAsync(ct);
        var rows = await database.InferenceUsage.AsNoTracking()
            .Where(item => item.AtMs >= start && item.AtMs < end && (deployment == null || item.Deployment == deployment) &&
                (purposeName == null || item.Purpose == purposeName))
            .GroupBy(item => new { Bucket = item.AtMs / step, item.Deployment, item.Operation, item.Purpose })
            .Select(group => new
            {
                group.Key.Bucket, group.Key.Deployment, group.Key.Operation, group.Key.Purpose,
                Prompt = group.Sum(item => item.PromptTokens ?? 0), Completion = group.Sum(item => item.CompletionTokens ?? 0),
                Requests = group.LongCount(),
                Failed = group.LongCount(item => item.StatusCode < 200 || item.StatusCode >= 300),
                Limited = group.LongCount(item => item.StatusCode == 429),
                Unknown = group.LongCount(item => item.PromptTokens == null)
            }).ToListAsync(ct);
        var deployments = await database.InferenceUsage.AsNoTracking().Where(item => item.AtMs >= start && item.AtMs < end)
            .Select(item => item.Deployment).Distinct().ToListAsync(ct);
        var buckets = rows.Select(row => new InferenceUsageBucket(DateTimeOffset.FromUnixTimeMilliseconds(row.Bucket * step), row.Deployment,
                Enum.TryParse<InferenceOperation>(row.Operation, out var operation) ? operation : InferenceOperation.Chat,
                Enum.TryParse<InferencePurpose>(row.Purpose, out var kind) ? kind : InferencePurpose.Other,
                row.Prompt, row.Completion, row.Requests, row.Failed, row.Limited, row.Unknown))
            .OrderBy(item => item.Start).ThenBy(item => item.Deployment, StringComparer.Ordinal)
            .ThenBy(item => item.Operation).ThenBy(item => item.Purpose).ToArray();
        string[] known = [.. deployments.Concat([capacity.ChatDeployment, capacity.EmbeddingDeployment])
            .Where(name => name.Length != 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        return new(from.ToUniversalTime(), to.ToUniversalTime(), minutes, buckets, known, capacity);
    }
}
