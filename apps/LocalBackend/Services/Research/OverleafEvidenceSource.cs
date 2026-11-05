using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using MeldResearch.LocalBackend.Contracts;
using MeldResearch.Research;

namespace MeldResearch.LocalBackend.Services.Research;

internal interface IOverleafEvidenceSource
{
    bool Available { get; }
    OverleafProjectsSnapshot? Latest { get; }
    Task<IReadOnlyList<OverleafProjectResponse>> ProjectsAsync(CancellationToken ct);
    Task<ResearchSnapshot> SnapshotAsync(OverleafProjectResponse project, CancellationToken ct);
    /// <summary>When each file was last edited, from Overleaf's history; null if the connector or Overleaf cannot say.</summary>
    Task<IReadOnlyDictionary<string, DateTimeOffset>?> EditTimesAsync(string projectId, CancellationToken ct);
    /// <summary>The edit times and the people who edited the project (Editors null when unknown), from one history read.</summary>
    async Task<OverleafHistory> HistoryAsync(string projectId, CancellationToken ct) => new(await EditTimesAsync(projectId, ct), null);
}

/// <summary>What Overleaf's history says about a project: when each file was last edited, and who edited it.</summary>
internal sealed record OverleafHistory(IReadOnlyDictionary<string, DateTimeOffset>? EditTimes, IReadOnlyList<OverleafPerson>? Editors);

internal sealed class OverleafEvidenceSource(IBrowserRelay browser, IOverleafService overleaf) : IOverleafEvidenceSource
{
    // A busy browser is not unavailable: reads queue behind the current transfer.
    public bool Available => browser.Status.Connected;
    public OverleafProjectsSnapshot? Latest => overleaf.Latest;
    public Task<IReadOnlyList<OverleafProjectResponse>> ProjectsAsync(CancellationToken ct) => overleaf.RefreshAsync(ct);
    public async Task<IReadOnlyDictionary<string, DateTimeOffset>?> EditTimesAsync(string projectId, CancellationToken ct) =>
        (await HistoryAsync(projectId, ct)).EditTimes;

    public async Task<OverleafHistory> HistoryAsync(string projectId, CancellationToken ct)
    {
        try
        {
            await using var response = await browser.FetchAsync("projectHistory", projectId, ct);
            if (!response.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)) return new(null, null);
            await using var stream = File.OpenRead(response.Path);
            return await ParseHistoryAsync(stream, ct);
        }
        // An older connector, a plan without history or an Overleaf change: fall back to a fixed order.
        catch (Exception error) when (error is MeldResearch.LocalBackend.Middleware.ApiFailure or System.Text.Json.JsonException or InvalidDataException)
        {
            return new(null, null);
        }
    }

    internal static async Task<IReadOnlyDictionary<string, DateTimeOffset>?> ParseEditTimesAsync(Stream stream, CancellationToken ct) =>
        (await ParseHistoryAsync(stream, ct)).EditTimes;

    /// <summary>The connector's history: { paths: { path: ms }, complete, editors: [{ name, email, at }] } (editors since connector 0.6.11).</summary>
    internal static async Task<OverleafHistory> ParseHistoryAsync(Stream stream, CancellationToken ct)
    {
        using var document = await System.Text.Json.JsonDocument.ParseAsync(stream, new() { MaxDepth = 8 }, ct);
        IReadOnlyList<OverleafPerson>? editors = null;
        if (document.RootElement.TryGetProperty("editors", out var list) && list.ValueKind == System.Text.Json.JsonValueKind.Array)
            editors = [.. list.EnumerateArray().Select(ProjectParser.Person).OfType<OverleafPerson>()
                .DistinctBy(person => person.Email ?? "name:" + person.Name).Take(MaxEditors)];
        if (!document.RootElement.TryGetProperty("paths", out var paths) || paths.ValueKind != System.Text.Json.JsonValueKind.Object)
            return new(null, editors);
        var times = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var entry in paths.EnumerateObject())
        {
            if (times.Count >= 5000) break;
            if (ResearchEvidence.IsSafePath(entry.Name) && entry.Value.ValueKind == System.Text.Json.JsonValueKind.Number &&
                entry.Value.TryGetInt64(out long milliseconds) && milliseconds > 0)
                times[entry.Name] = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        return new(times, editors);
    }

    /// <summary>At most this many editors are kept per project.</summary>
    public const int MaxEditors = 100;

    public async Task<ResearchSnapshot> SnapshotAsync(OverleafProjectResponse project, CancellationToken ct)
    {
        await using var response = await browser.FetchAsync("downloadProject", project.Id, ct);
        await ArchiveStore.ValidateAsync(response.Path, ct);
        return await ReadSnapshotAsync(response.Path, project, DateTimeOffset.UtcNow, ct);
    }

    internal static async Task<ResearchSnapshot> ReadSnapshotAsync(string path, OverleafProjectResponse project,
        DateTimeOffset observedAt, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(path);
        var entries = archive.Entries.Where(entry => !entry.FullName.Replace('\\', '/').EndsWith('/')).ToArray();
        if (entries.Length > ResearchEvidence.MaxFiles)
            throw new ResearchFailure(413, "research_file_limit", "The project exceeds the research-ingestion file limit.");
        var files = new List<ResearchFile>();
        long textBytes = 0;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            string name = entry.FullName.Replace('\\', '/');
            await using var stream = entry.Open();
            // A single oversized source (for example a generated table) is tracked by hash like a figure, not a reason to skip the project.
            if (ResearchEvidence.TextExtensions.Contains(Path.GetExtension(name)) && entry.Length <= ResearchEvidence.MaxFileTextBytes)
            {
                if (textBytes + entry.Length > ResearchEvidence.MaxProjectTextBytes)
                    throw new ResearchFailure(413, "research_text_limit", "The project exceeds the supported text size. Nothing was partially indexed.");
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory, ct);
                byte[] bytes = memory.ToArray();
                string? text;
                try { text = new UTF8Encoding(false, true).GetString(bytes); }
                catch (DecoderFallbackException) { text = null; }
                // Non-UTF-8 or binary content in a text-named file is tracked by hash only, like a figure.
                if (text is not null && text.Contains('\0')) text = null;
                files.Add(new(name, ResearchEvidence.Hash(bytes), bytes.Length, text));
                if (text is not null) textBytes += bytes.Length;
            }
            else
            {
                byte[] hash = await SHA256.HashDataAsync(stream, ct);
                files.Add(new(name, Convert.ToHexStringLower(hash), entry.Length, null));
            }
        }
        var snapshot = new ResearchSnapshot(project.Id, project.Name, ResearchEvidence.Revision(files), observedAt, project.UpdatedAt, files);
        ResearchEvidence.ValidateSnapshot(snapshot);
        return snapshot;
    }
}
