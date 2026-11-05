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
    /// <summary>
    /// Who wrote each line of a file now (Overleaf's history diff); null when the connector cannot say (an older connector,
    /// a history too long to rebuild, a binary file).
    /// </summary>
    Task<FileAuthorship?> FileAuthorsAsync(string projectId, string path, CancellationToken ct) => Task.FromResult<FileAuthorship?>(null);
    /// <summary>Whether the connected connector reads who wrote each line (FileAuthorsAsync).</summary>
    bool ReadsFileAuthors => false;
}

/// <summary>
/// Who wrote a file's current lines: each non-empty line by a short hash of its trimmed text (CollaboratorPlan.LineHash) with
/// its characters by author (an index into Users, or -1 for text without an author, such as text there when the file was added).
/// </summary>
internal sealed record FileAuthorship(IReadOnlyList<OverleafPerson> Users, IReadOnlyDictionary<string, IReadOnlyList<(int Who, int Chars)>> Lines);

/// <summary>
/// What Overleaf's history says about a project: when each file was last edited, who edited it, who edited each file
/// (FileEditors: each person with their latest edit of that file), to say who an update rests on, and who added each file
/// (FileAdders: credited with text that was there when the file was added).
/// </summary>
internal sealed record OverleafHistory(IReadOnlyDictionary<string, DateTimeOffset>? EditTimes, IReadOnlyList<OverleafPerson>? Editors,
    IReadOnlyDictionary<string, IReadOnlyList<OverleafPerson>>? FileEditors = null, IReadOnlyDictionary<string, OverleafPerson>? FileAdders = null);

internal sealed class OverleafEvidenceSource(IBrowserRelay browser, IOverleafService overleaf) : IOverleafEvidenceSource
{
    // A busy browser is not unavailable: reads queue behind the current transfer.
    public bool Available => browser.Status.Connected;
    public OverleafProjectsSnapshot? Latest => overleaf.Latest;
    public Task<IReadOnlyList<OverleafProjectResponse>> ProjectsAsync(CancellationToken ct) => overleaf.RefreshAsync(ct);
    public async Task<IReadOnlyDictionary<string, DateTimeOffset>?> EditTimesAsync(string projectId, CancellationToken ct) =>
        (await HistoryAsync(projectId, ct)).EditTimes;

    /// <summary>The first connector version that reads who wrote each line (fileAuthors).</summary>
    private static readonly Version FileAuthorsSince = new(0, 6, 14);

    public bool ReadsFileAuthors => browser.Status is { Connected: true } status &&
        (status.Dummy || Version.TryParse(status.ConnectionVersion, out var version) && version >= FileAuthorsSince);

    public async Task<FileAuthorship?> FileAuthorsAsync(string projectId, string path, CancellationToken ct)
    {
        if (!ReadsFileAuthors || !ResearchEvidence.IsSafePath(path)) return null;
        try
        {
            await using var response = await browser.FetchAsync("fileAuthors", projectId, ct, path);
            if (!response.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)) return null;
            await using var stream = File.OpenRead(response.Path);
            return await ParseFileAuthorsAsync(stream, ct);
        }
        // History too long to rebuild, a binary file or an Overleaf change: the details keep the per-file credit.
        catch (Exception error) when (error is MeldResearch.LocalBackend.Middleware.ApiFailure or System.Text.Json.JsonException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The connector's line authorship: { users: [{ name, email }], lines: [[hash, [[who, chars], ...]], ...] }.</summary>
    internal static async Task<FileAuthorship?> ParseFileAuthorsAsync(Stream stream, CancellationToken ct)
    {
        using var document = await System.Text.Json.JsonDocument.ParseAsync(stream, new() { MaxDepth = 8 }, ct);
        var root = document.RootElement;
        if (!root.TryGetProperty("users", out var users) || users.ValueKind != System.Text.Json.JsonValueKind.Array ||
            !root.TryGetProperty("lines", out var lines) || lines.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
        var people = users.EnumerateArray().Take(200).Select(user => ProjectParser.Person(user) ?? new OverleafPerson(null, null)).ToArray();
        var result = new Dictionary<string, IReadOnlyList<(int, int)>>(StringComparer.Ordinal);
        foreach (var line in lines.EnumerateArray().Take(20000))
        {
            if (line.ValueKind != System.Text.Json.JsonValueKind.Array || line.GetArrayLength() != 2 || line[0].GetString() is not { Length: 12 } hash ||
                line[1].ValueKind != System.Text.Json.JsonValueKind.Array || result.ContainsKey(hash)) continue;
            var counts = new List<(int, int)>();
            foreach (var count in line[1].EnumerateArray().Take(50))
                if (count.ValueKind == System.Text.Json.JsonValueKind.Array && count.GetArrayLength() == 2 && count[0].TryGetInt32(out int who) &&
                    who >= -1 && who < people.Length && count[1].TryGetInt32(out int chars) && chars > 0)
                    counts.Add((who, chars));
            result[hash] = counts;
        }
        return new(people, result);
    }

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
        Dictionary<string, IReadOnlyList<OverleafPerson>>? files = null;
        Dictionary<string, OverleafPerson>? adders = null;
        if (document.RootElement.TryGetProperty("editors", out var list) && list.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            // Indexes into the connector's list (files refers to it), before dropping unusable entries.
            var all = list.EnumerateArray().Take(MaxEditors * 2).Select(ProjectParser.Person).ToArray();
            editors = [.. all.OfType<OverleafPerson>().DistinctBy(person => person.Email ?? "name:" + person.Name).Take(MaxEditors)];
            if (document.RootElement.TryGetProperty("files", out var byFile) && byFile.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                files = new(StringComparer.Ordinal);
                foreach (var file in byFile.EnumerateObject().Take(5000))
                {
                    if (!ResearchEvidence.IsSafePath(file.Name) || file.Value.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                    var people = new List<OverleafPerson>();
                    foreach (var entry in file.Value.EnumerateArray().Take(20))
                        if (entry.ValueKind == System.Text.Json.JsonValueKind.Array && entry.GetArrayLength() == 2 &&
                            entry[0].TryGetInt32(out int index) && index >= 0 && index < all.Length && all[index] is { } person &&
                            entry[1].TryGetInt64(out long ms) && ms > 0)
                            people.Add(person with { At = DateTimeOffset.FromUnixTimeMilliseconds(ms) });
                    if (people.Count != 0) files[file.Name] = people;
                }
            }
            if (document.RootElement.TryGetProperty("added", out var byAdd) && byAdd.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                adders = new(StringComparer.Ordinal);
                foreach (var file in byAdd.EnumerateObject().Take(5000))
                    if (ResearchEvidence.IsSafePath(file.Name) && file.Value.TryGetInt32(out int index) && index >= 0 && index < all.Length && all[index] is { } person)
                        adders[file.Name] = person with { At = null };
            }
        }
        if (!document.RootElement.TryGetProperty("paths", out var paths) || paths.ValueKind != System.Text.Json.JsonValueKind.Object)
            return new(null, editors, files, adders);
        var times = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var entry in paths.EnumerateObject())
        {
            if (times.Count >= 5000) break;
            if (ResearchEvidence.IsSafePath(entry.Name) && entry.Value.ValueKind == System.Text.Json.JsonValueKind.Number &&
                entry.Value.TryGetInt64(out long milliseconds) && milliseconds > 0)
                times[entry.Name] = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        return new(times, editors, files, adders);
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
