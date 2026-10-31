using System.Security.Cryptography;
using System.Text;

namespace MeldResearch.Research;

public static class ResearchEvidence
{
    public const int MaxFiles = 2000;
    public const int MaxFileTextBytes = 128 * 1024;
    /// <summary>Bounds local storage only: each model request carries a single file of at most <see cref="MaxFileTextBytes"/>.</summary>
    public const int MaxProjectTextBytes = 16 * 1024 * 1024;
    public const int MaxFacts = 200;
    public const int MaxChanges = 12;
    public const int MaxRequestBytes = 3 * 1024 * 1024;
    public const string ConsentVersion = "overleaf-foundry-2026-10";
    /// <summary>Sources whose text is analyzed. Everything else, including .bib bibliographies, is tracked by hash only.</summary>
    public static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".tex", ".md", ".txt" };

    /// <summary>
    /// Drops text this version no longer analyzes (stored snapshots kept .bib text), so comparisons and requests see only
    /// analyzed sources. Hashes, and therefore the revision, are unchanged.
    /// </summary>
    public static ResearchSnapshot WithAnalyzedTextOnly(ResearchSnapshot snapshot) =>
        snapshot.Files.Any(file => file.Text is not null && !TextExtensions.Contains(Path.GetExtension(file.Path)))
            ? snapshot with { Files = snapshot.Files.Select(file => file.Text is not null && !TextExtensions.Contains(Path.GetExtension(file.Path))
                ? file with { Text = null } : file).ToArray() }
            : snapshot;

    public static bool IsProjectId(string? id) => id is { Length: 24 } &&
        id.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));

    public static string Revision(IEnumerable<ResearchFile> files) =>
        Hash(string.Join('\n', files.OrderBy(file => file.Path, StringComparer.Ordinal)
            .Select(file => $"{file.Path.Length}:{file.Path}:{file.Sha256}")));

    public static IReadOnlyList<string> ChangedPaths(ResearchSnapshot? previous, ResearchSnapshot current)
    {
        var before = previous?.Files.ToDictionary(file => file.Path, StringComparer.Ordinal)
            ?? new Dictionary<string, ResearchFile>(StringComparer.Ordinal);
        var after = current.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        return before.Keys.Union(after.Keys, StringComparer.Ordinal)
            .Where(path => !before.TryGetValue(path, out var left) || !after.TryGetValue(path, out var right) ||
                left.Sha256 != right.Sha256).Order(StringComparer.Ordinal).ToArray();
    }

    public static bool HasTextChanges(ResearchSnapshot? previous, ResearchSnapshot current)
    {
        var paths = ChangedPaths(previous, current).ToHashSet(StringComparer.Ordinal);
        return current.Files.Concat(previous?.Files ?? [])
            .Any(file => file.Text is not null && paths.Contains(file.Path));
    }

    public static void ValidateSnapshot(ResearchSnapshot snapshot)
    {
        if (!IsProjectId(snapshot.ProjectId) || !ShortText(snapshot.ProjectName, 500) ||
            snapshot.Files is null || snapshot.Files.Count > MaxFiles ||
            snapshot.ObservedAt == default)
            throw InvalidEvidence();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long textBytes = 0;
        foreach (var file in snapshot.Files)
        {
            if (file is null || !IsSafePath(file.Path) || !paths.Add(file.Path) || file.Bytes < 0 ||
                file.Sha256 is not { Length: 64 } || !file.Sha256.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
                throw InvalidEvidence();
            if (file.Text is not { } text) continue;
            int bytes = Encoding.UTF8.GetByteCount(text);
            if (!TextExtensions.Contains(Path.GetExtension(file.Path)) || bytes > MaxFileTextBytes ||
                bytes != file.Bytes || Hash(text) != file.Sha256 || text.Contains('\0'))
                throw InvalidEvidence();
            textBytes += bytes;
        }
        if (textBytes > MaxProjectTextBytes || snapshot.Revision != Revision(snapshot.Files))
            throw InvalidEvidence();
    }

    public static void ValidateEmbeddings(IReadOnlyList<IReadOnlyList<float>> vectors, int expected)
    {
        if (vectors.Count != expected || vectors.Any(vector => vector.Count is 0 or > 8192 ||
                vector.Any(value => !float.IsFinite(value))) ||
            vectors.Select(vector => vector.Count).Distinct().Count() > 1)
            throw new ResearchFailure(502, "invalid_embeddings", "Foundry returned an invalid embedding response. Nothing was committed.");
    }

    public static double Cosine(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        if (left.Count != right.Count) throw new ResearchFailure(409, "embedding_model_changed",
            "The embedding dimensions changed. Rebuild this project's index before processing another delta.");
        double dot = 0, a = 0, b = 0;
        for (int index = 0; index < left.Count; index++)
        {
            dot += (double)left[index] * right[index];
            a += (double)left[index] * left[index];
            b += (double)right[index] * right[index];
        }
        return a == 0 || b == 0 ? 0 : dot / Math.Sqrt(a * b);
    }

    public static bool IsSafePath(string? path) => path is { Length: > 0 and <= 1024 } &&
        !path.StartsWith('/') && !path.Contains('\\') &&
        !path.Any(character => character < ' ' || "<>:\"|?*".Contains(character)) &&
        !path.Split('/').Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' '));

    private static bool ShortText(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;
    private static bool ValidTags(IReadOnlyList<string>? tags) =>
        tags is { Count: <= 12 } && tags.All(tag => ShortText(tag, 80));
    private static ResearchFailure InvalidEvidence() => new(400, "invalid_research_evidence",
        "The research evidence is invalid or exceeds the supported source limits.");
}
