using MeldResearch.Research;

namespace MeldResearch.Tests;

public sealed class LatexTreeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IncludesFormTreesWhoseRootsAreDocumentsNothingIncludes()
    {
        var documents = new Dictionary<string, string>
        {
            ["main.tex"] = "\\documentclass{article}\n% \\input{commented}\n\\begin{document}\n\\input{sections/intro}\n\\include{sections/model.tex}\n" +
                "\\iffalse\\input{sections/old}\\fi\n\\import{appendix/}{proofs}\n\\end{document}",
            ["sections/intro.tex"] = "Intro. \\input{figures/table}",
            ["sections/model.tex"] = "Model. \\subfile{details}",
            ["sections/details.tex"] = "Details.",
            ["sections/old.tex"] = "Old section.",
            ["figures/table.tex"] = "A table.",
            ["appendix/proofs.tex"] = "Proofs.",
            ["commented.tex"] = "Never included.",
            ["memo.tex"] = "\\documentclass{article}\\begin{document}A memo.\\end{document}",
            ["scrap.tex"] = "A small scrap.",
            ["notes.md"] = new string('n', LatexTree.MinOrphanCharacters),
        };
        var graph = LatexTree.Graph(documents);
        Assert.Equal(["sections/intro.tex", "sections/model.tex", "appendix/proofs.tex"], graph["main.tex"].Children);
        // \input resolves relative to the project root when not relative to the including file; \subfile relative to it.
        Assert.Equal(["figures/table.tex"], graph["sections/intro.tex"].Children);
        Assert.Equal(["sections/details.tex"], graph["sections/model.tex"].Children);
        Assert.Equal(["main.tex"], graph["sections/intro.tex"].Parents);

        var dates = new Dictionary<string, DateTimeOffset> { ["sections/details.tex"] = Now.AddDays(-2), ["memo.tex"] = Now.AddDays(-10) };
        var roots = LatexTree.Roots(graph, documents, path => dates.GetValueOrDefault(path, Now.AddDays(-300)));
        // Small orphans without \documentclass are not roots; commented-out and \iffalse files are orphans.
        Assert.Equal(["main.tex", "memo.tex", "notes.md"], roots.Select(root => root.Root).Order(StringComparer.Ordinal));
        var paper = roots.Single(root => root.Root == "main.tex");
        Assert.Equal(["main.tex", "sections/intro.tex", "figures/table.tex", "sections/model.tex", "sections/details.tex", "appendix/proofs.tex"],
            paper.ReadingOrder);
        // A tree is as recent as its most recently edited document; roots are ordered oldest first.
        Assert.Equal(Now.AddDays(-2), paper.Date);
        Assert.Equal(["notes.md", "memo.tex", "main.tex"], roots.Select(root => root.Root));

        var selected = LatexTree.Selected(roots, Now, TimeSpan.FromDays(183), fallback: true);
        Assert.Equal(["memo.tex", "main.tex"], selected.Select(root => root.Root));
        // Nothing within the window: the newest root, only when asked to fall back.
        Assert.Equal(["main.tex"], LatexTree.Selected(roots, Now.AddYears(2), TimeSpan.FromDays(183), fallback: true).Select(root => root.Root));
        Assert.Empty(LatexTree.Selected(roots, Now.AddYears(2), TimeSpan.FromDays(183), fallback: false));
    }

    [Fact]
    public void CyclesKeepTheDocumentClassAsRootAndSharedSectionsAppearInEachTree()
    {
        var documents = new Dictionary<string, string>
        {
            ["paper.tex"] = "\\documentclass{article}\\input{a}\\input{shared}",
            ["a.tex"] = "\\input{paper}",
            ["slides.tex"] = "\\documentclass{beamer}\\input{shared}",
            ["shared.tex"] = "Shared.",
        };
        var graph = LatexTree.Graph(documents);
        Assert.Empty(graph["paper.tex"].Parents);
        Assert.Empty(graph["a.tex"].Children);
        var roots = LatexTree.Roots(graph, documents, _ => Now);
        Assert.Equal(["paper.tex", "slides.tex"], roots.Select(root => root.Root));
        Assert.Contains("shared.tex", roots[0].ReadingOrder);
        Assert.Contains("shared.tex", roots[1].ReadingOrder);
    }

    [Fact]
    public void LongDocumentsArePackedAndSplitAtSectionsThenParagraphsWithTheirLineNumbers()
    {
        string section(string name, int lines) => $"\\section{{{name}}}\n" + string.Join('\n', Enumerable.Repeat(new string('x', 99), lines));
        string paper = section("One", 50) + "\n" + section("Two", 50) + "\n" + section("Three", 50);
        var parts = LatexTree.Pack([("a.tex", "Short."), ("paper.tex", paper), ("b.tex", "Also short.")], budget: 6000);
        var segments = parts.SelectMany(part => part).Where(segment => segment.Path == "paper.tex").ToArray();
        Assert.Equal(3, segments.Length);
        Assert.All(segments, segment => Assert.StartsWith("\\section{", segment.Text));
        Assert.Equal([1, 52, 103], segments.Select(segment => segment.FirstLine));
        Assert.All(parts, part => Assert.True(part.Sum(segment => segment.Text.Length) <= 6000));
        Assert.Equal("a.tex", parts[0][0].Path);
        Assert.Equal("b.tex", parts[^1][^1].Path);
        // A section too long on its own is cut at blank lines.
        string longSection = "\\section{Long}\n" + string.Join("\n\n", Enumerable.Repeat(new string('y', 1000), 20));
        var cut = LatexTree.Pack([("long.tex", longSection)], budget: 5000).SelectMany(part => part).ToArray();
        Assert.True(cut.Length >= 4);
        Assert.All(cut, segment => Assert.True(segment.Text.Length <= 5000));
        Assert.Equal(longSection.Replace("\r\n", "\n"), string.Join('\n', cut.Select(segment => segment.Text)));
    }
}
