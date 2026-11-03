using System.Text.RegularExpressions;

namespace MeldResearch.Research;

/// <summary>One document's place in a project: what it includes (in order) and what includes it.</summary>
public sealed record DocumentNode(string Path, IReadOnlyList<string> Children, IReadOnlyList<string> Parents, bool Standalone);

/// <summary>A root document and its tree in reading order; Date is the latest edit of any document in the tree.</summary>
public sealed record RootTree(string Root, IReadOnlyList<string> ReadingOrder, DateTimeOffset Date);

/// <summary>A document, or one section of a long document, to send in a step. FirstLine numbers its first line in the whole document.</summary>
public sealed record PartDocument(string Path, string Text, int FirstLine);

/// <summary>
/// The include structure of an Overleaf project (OVERLEAF.md §3-§7): include edges from \input, \include, \subfile,
/// \import and \subimport (comments and \iffalse blocks ignored); roots are documents nothing includes (an orphan without
/// \documentclass needs <see cref="MinOrphanCharacters"/>); a root's tree is read depth-first in include order, and its
/// date is the latest edit of any document in it. Long documents are packed into parts of at most
/// <see cref="PartCharacters"/>, split at sections and then paragraphs.
/// </summary>
public static partial class LatexTree
{
    public const int MinOrphanCharacters = 1000, PartCharacters = 60000;

    [GeneratedRegex(@"(?<!\\)%[^\n]*")]
    private static partial Regex Comment();
    [GeneratedRegex(@"\\iffalse\b.*?\\fi\b", RegexOptions.Singleline)]
    private static partial Regex IfFalse();
    [GeneratedRegex(@"\\(input|include|subfile)\s*\{([^{}]+)\}|\\input\s+([^\s{}\\]+)|\\(import|subimport|inputfrom|subinputfrom|includefrom|subincludefrom)\s*\{([^{}]*)\}\s*\{([^{}]+)\}")]
    private static partial Regex Includes();
    [GeneratedRegex(@"\\documentclass\b")]
    private static partial Regex DocumentClass();
    [GeneratedRegex(@"^\s*\\(part|chapter|section)\*?\s*[\[{]", RegexOptions.Multiline)]
    private static partial Regex Heading();

    /// <summary>Text without comments and \iffalse blocks (for reading structure, not for what is sent).</summary>
    public static string Uncommented(string text) => IfFalse().Replace(Comment().Replace(text, ""), "");

    /// <summary>The include graph of the readable documents (path → text). Edges that would close a cycle are dropped.</summary>
    public static IReadOnlyDictionary<string, DocumentNode> Graph(IReadOnlyDictionary<string, string> documents)
    {
        var raw = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (path, text) in documents)
        {
            var children = new List<string>();
            foreach (Match match in Includes().Matches(Uncommented(text)))
            {
                string? folder = match.Groups[5].Success ? match.Groups[5].Value : null;
                string name = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Success ? match.Groups[3].Value : match.Groups[6].Value;
                if (Resolve(path, folder, name.Trim(), documents) is { } target && target != path && !children.Contains(target))
                    children.Add(target);
            }
            raw[path] = children;
        }
        // Depth-first from every document in path order; an edge back into the current path closes a cycle and is dropped.
        var kept = raw.Keys.ToDictionary(path => path, _ => new List<string>(), StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        void Visit(string path)
        {
            state[path] = 1;
            foreach (var child in raw[path])
            {
                if (state.GetValueOrDefault(child) == 1) continue;
                kept[path].Add(child);
                if (state.GetValueOrDefault(child) == 0) Visit(child);
            }
            state[path] = 2;
        }
        // Documents with \documentclass first, so a cycle through a paper keeps the paper as its root.
        foreach (var path in raw.Keys.OrderBy(path => DocumentClass().IsMatch(Uncommented(documents[path])) ? 0 : 1).ThenBy(path => path, StringComparer.Ordinal))
            if (state.GetValueOrDefault(path) == 0) Visit(path);
        var parents = raw.Keys.ToDictionary(path => path, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var (path, children) in kept)
            foreach (var child in children) parents[child].Add(path);
        return kept.ToDictionary(item => item.Key, item => new DocumentNode(item.Key, item.Value, parents[item.Key],
            DocumentClass().IsMatch(Uncommented(documents[item.Key]))), StringComparer.Ordinal);
    }

    /// <summary>
    /// The roots (documents with no parent; an orphan without \documentclass needs <see cref="MinOrphanCharacters"/>) with
    /// their trees in reading order and their dates, oldest first.
    /// </summary>
    public static IReadOnlyList<RootTree> Roots(IReadOnlyDictionary<string, DocumentNode> graph, IReadOnlyDictionary<string, string> documents,
        Func<string, DateTimeOffset> edited)
    {
        var roots = new List<RootTree>();
        foreach (var node in graph.Values.Where(node => node.Parents.Count == 0).OrderBy(node => node.Path, StringComparer.Ordinal))
        {
            if (!node.Standalone && documents[node.Path].Length < MinOrphanCharacters) continue;
            var order = ReadingOrder(graph, node.Path);
            roots.Add(new(node.Path, order, order.Max(edited)));
        }
        return [.. roots.OrderBy(root => root.Date).ThenBy(root => root.Root, StringComparer.Ordinal)];
    }

    /// <summary>The tree of a root, depth-first in include order, each document once.</summary>
    public static IReadOnlyList<string> ReadingOrder(IReadOnlyDictionary<string, DocumentNode> graph, string root)
    {
        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Walk(string path)
        {
            if (!seen.Add(path)) return;
            order.Add(path);
            foreach (var child in graph[path].Children) Walk(child);
        }
        Walk(root);
        return order;
    }

    /// <summary>Roots dated within the window (oldest first); when none is and <paramref name="fallback"/>, the newest root.</summary>
    public static IReadOnlyList<RootTree> Selected(IReadOnlyList<RootTree> roots, DateTimeOffset now, TimeSpan window, bool fallback)
    {
        var recent = roots.Where(root => now - root.Date <= window).ToArray();
        return recent.Length != 0 || !fallback || roots.Count == 0 ? recent : [roots[^1]];
    }

    /// <summary>
    /// Packs documents (in reading order) into parts of at most <paramref name="budget"/> characters; a longer document is
    /// split at \part/\chapter/\section lines, then at blank lines, then at the budget.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<PartDocument>> Pack(IReadOnlyList<(string Path, string Text)> documents, int budget = PartCharacters)
    {
        var parts = new List<IReadOnlyList<PartDocument>>();
        var current = new List<PartDocument>();
        int size = 0;
        foreach (var (path, text) in documents)
            foreach (var segment in Split(path, text, budget))
            {
                if (current.Count != 0 && size + segment.Text.Length > budget)
                {
                    parts.Add(current);
                    current = [];
                    size = 0;
                }
                current.Add(segment);
                size += segment.Text.Length;
            }
        if (current.Count != 0) parts.Add(current);
        return parts;
    }

    private static IEnumerable<PartDocument> Split(string path, string text, int budget)
    {
        string normalized = text.Replace("\r\n", "\n");
        if (normalized.Length <= budget) { yield return new(path, normalized, 1); yield break; }
        var lines = normalized.Split('\n');
        // Cut points: heading lines first; blank lines when a section alone is too long.
        var headings = new HashSet<int>(Enumerable.Range(0, lines.Length).Where(index => index > 0 && Heading().IsMatch(lines[index])));
        int start = 0;
        while (start < lines.Length)
        {
            int size = 0, end = start, lastHeading = -1, lastBlank = -1;
            while (end < lines.Length && size + lines[end].Length + 1 <= budget)
            {
                if (end > start && headings.Contains(end)) lastHeading = end;
                if (end > start && lines[end].Trim().Length == 0) lastBlank = end;
                size += lines[end].Length + 1;
                end++;
            }
            if (end < lines.Length)
            {
                if (lastHeading > start) end = lastHeading;
                else if (lastBlank > start) end = lastBlank;
                else if (end == start)
                {
                    // A single line over the budget: cut it.
                    yield return new(path, lines[start][..budget], start + 1);
                    lines[start] = lines[start][budget..];
                    continue;
                }
            }
            yield return new(path, string.Join('\n', lines[start..end]), start + 1);
            start = end;
        }
    }

    private static string? Resolve(string from, string? folder, string name, IReadOnlyDictionary<string, string> documents)
    {
        // Relative to the including file first, then to the project root (LaTeX resolves \input from the compiling document).
        string fromFolder = from.Contains('/') ? from[..from.LastIndexOf('/')] : "";
        string target = folder is null || folder.Length == 0 ? name : folder.TrimEnd('/') + "/" + name;
        foreach (string basePath in new[] { fromFolder, "" })
            foreach (string candidate in new[] { target, target + ".tex" })
            {
                string path = Normalize(basePath.Length == 0 ? candidate : basePath + "/" + candidate);
                if (documents.ContainsKey(path)) return path;
            }
        return null;
    }

    private static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (string part in path.Replace('\\', '/').Split('/'))
        {
            if (part.Length == 0 || part == ".") continue;
            if (part == "..") { if (parts.Count != 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(part);
        }
        return string.Join('/', parts);
    }
}
