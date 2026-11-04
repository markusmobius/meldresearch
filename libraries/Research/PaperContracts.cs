using System.Text;
using System.Text.RegularExpressions;

namespace MeldResearch.Research;

/// <summary>
/// What a project's main LaTeX document says it is: its \title and its abstract, as plain text. Path and Sha256 name the
/// main document they were read from. Either may be null (a project without a title or an abstract).
/// </summary>
public sealed record PaperInfo(string Path, string Sha256, string? Title, string? Abstract);

/// <summary>Describes one research card from its main paper: the paper's title and abstract and its project's summary.</summary>
public sealed record CardSummaryRequest(string Title, string Abstract, ProjectProfile Summary);

/// <summary>
/// Reads the title and abstract of a project's paper directly from the LaTeX (no model): \title{...} (also
/// \title[short]{long}) and \begin{abstract}...\end{abstract} or \abstract{...}, also from a file the document includes
/// with \input or \include. Among several complete documents (a paper next to memos or notes), one with an abstract is the
/// paper; otherwise the usual main document. LaTeX markup is reduced to plain text.
/// </summary>
public static partial class Papers
{
    public const int MaxTitle = 300, MaxAbstract = 4000;
    /// <summary>How papers are read; a newer reading reads every project's paper again (no model call).</summary>
    public const int Version = 2;

    /// <summary>The marker of the processed revision a project's paper was read from.</summary>
    public static string ReadFor(string revision) => $"{Version}:{revision}";

    [GeneratedRegex(@"(?<!\\)%[^\n]*")]
    private static partial Regex Comment();
    [GeneratedRegex(@"\\begin\{abstract\}(.*?)\\end\{abstract\}", RegexOptions.Singleline)]
    private static partial Regex AbstractEnvironment();
    [GeneratedRegex(@"\\(?:input|include)\s*\{([^{}]+)\}")]
    private static partial Regex Include();

    public static PaperInfo? Read(IEnumerable<(string Path, string Text)> texFiles)
    {
        var files = texFiles.ToDictionary(file => file.Path, file => file.Text, StringComparer.Ordinal);
        var documents = new List<PaperInfo>();
        var remaining = files.Select(file => (file.Key, file.Value)).ToList();
        while (ProjectSummaries.MainDocument(remaining) is { } candidate)
        {
            documents.Add(ReadDocument(files, candidate));
            remaining.RemoveAll(file => file.Key == candidate);
        }
        return documents.FirstOrDefault(document => document.Abstract is not null) ?? documents.FirstOrDefault();
    }

    private static PaperInfo ReadDocument(IReadOnlyDictionary<string, string> files, string main)
    {
        string text = Comment().Replace(files[main], "");
        string? title = Argument(text, "\\title");
        string? summary = AbstractEnvironment().Match(text) is { Success: true } environment ? environment.Groups[1].Value : Argument(text, "\\abstract");
        if (summary is null)
        {
            // An abstract kept in its own file: \input{abstract} or \include{sections/abstract}.
            foreach (Match include in Include().Matches(text))
            {
                string name = include.Groups[1].Value.Trim();
                string? path = new[] { name, name + ".tex" }.Select(candidate => Resolve(main, candidate)).FirstOrDefault(files.ContainsKey);
                if (path is null) continue;
                string included = Comment().Replace(files[path], "");
                summary = AbstractEnvironment().Match(included) is { Success: true } found ? found.Groups[1].Value
                    : Path.GetFileNameWithoutExtension(path).Contains("abstract", StringComparison.OrdinalIgnoreCase) ? included : null;
                if (summary is not null) break;
            }
        }
        return new(main, ResearchEvidence.Hash(files[main]), Plain(title, MaxTitle), Plain(summary, MaxAbstract));
    }

    /// <summary>The braced argument of a command (after an optional [..] argument), with nested braces.</summary>
    private static string? Argument(string text, string command) =>
        Find(text, command) is { } found ? text[(found.Open + 1)..found.Close] : null;

    /// <summary>Where a command starts, and the positions of its argument's opening and closing braces.</summary>
    private static (int Start, int Open, int Close)? Find(string text, string command)
    {
        int index = 0;
        while ((index = text.IndexOf(command, index, StringComparison.Ordinal)) >= 0)
        {
            int start = index, position = index + command.Length;
            index = position;
            if (position < text.Length && char.IsLetter(text[position])) continue;
            if (position < text.Length && text[position] == '*') position++;
            while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
            if (position < text.Length && text[position] == '[')
            {
                int close = text.IndexOf(']', position);
                if (close < 0) return null;
                position = close + 1;
                while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
            }
            if (position >= text.Length || text[position] != '{') continue;
            int depth = 0;
            for (int end = position; end < text.Length; end++)
            {
                if (text[end] == '\\') { end++; continue; }
                if (text[end] == '{') depth++;
                else if (text[end] == '}' && --depth == 0) return (start, position, end);
            }
            return null;
        }
        return null;
    }

    /// <summary>The position of the brace closing the one at <paramref name="open"/>, or -1.</summary>
    private static int Close(string text, int open)
    {
        int depth = 0;
        for (int index = open; index < text.Length; index++)
        {
            if (text[index] == '\\') { index++; continue; }
            if (text[index] == '{') depth++;
            else if (text[index] == '}' && --depth == 0) return index;
        }
        return -1;
    }

    private static string Resolve(string main, string name)
    {
        string folder = main.Contains('/') ? main[..main.LastIndexOf('/')] : "";
        string path = name.TrimStart('.', '/');
        return name.StartsWith("./", StringComparison.Ordinal) || folder.Length == 0 ? path : $"{folder}/{path}";
    }

    /// <summary>LaTeX to plain text: notes, citations and labels dropped, formatting commands unwrapped, spacing normalized.</summary>
    internal static string? Plain(string? latex, int max)
    {
        if (latex is null) return null;
        string text = latex;
        foreach (string dropped in new[] { "\\thanks", "\\footnote", "\\cite", "\\citep", "\\citet", "\\label", "\\ref", "\\eqref", "\\vspace", "\\hspace" })
            while (Find(text, dropped) is { } found) text = text[..found.Start] + text[(found.Close + 1)..];
        // \texorpdfstring{typeset}{plain} and \href{url}{text} keep only their text.
        foreach (string command in new[] { "\\texorpdfstring", "\\href" })
            while (Find(text, command) is { } found)
            {
                string rest = text[(found.Close + 1)..].TrimStart();
                int close = rest.StartsWith('{') ? Close(rest, 0) : -1;
                string kept = close < 0 ? text[(found.Open + 1)..found.Close] : rest[1..close];
                text = text[..found.Start] + kept + (close < 0 ? rest : rest[(close + 1)..]);
            }
        // A line break, also with spacing (\\[0.3em]) or a star, is a space.
        text = Regex.Replace(text, @"\\\\\*?(?:\s*\[[^\]]*\])?", " ");
        text = text.Replace("~", " ").Replace("\\&", "&").Replace("\\%", "%").Replace("\\$", "$").Replace("\\_", "_")
            .Replace("``", "\"").Replace("''", "\"").Replace("---", "—").Replace("--", "–");
        text = Regex.Replace(text, @"\\(?:textbf|textit|emph|textsc|texttt|textrm|underline|mbox|text|mathrm|mathbf|url)\s*\{", "{");
        text = Regex.Replace(text, @"\\(?:noindent|centering|newline|par|medskip|bigskip|smallskip|maketitle)\b", " ");
        text = Regex.Replace(text, @"\\[A-Za-z]+\*?", "");
        var plain = new StringBuilder(text.Length);
        foreach (char character in text) if (character is not ('{' or '}')) plain.Append(character);
        string result = Regex.Replace(plain.ToString(), @"\s+", " ").Trim();
        if (result.Length == 0) return null;
        return result.Length <= max ? result : result[..max].TrimEnd() + "…";
    }

    public static void Validate(CardSummaryRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > MaxTitle + 1 ||
            string.IsNullOrWhiteSpace(request.Abstract) || request.Abstract.Length > MaxAbstract + 1 ||
            request.Summary is null || !ProfileEvidence.ValidProfile(request.Summary))
            throw new ResearchFailure(400, "invalid_card_request", "The research card summary request is invalid.");
    }
}
