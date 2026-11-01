using System.Security.Cryptography;

namespace MeldResearch.Research;

/// <summary>
/// Where a research card member comes from. Overleaf projects keep their Overleaf ids (24 hex characters); a Gmail
/// workstream (a line of work Meld finds in one Gmail account's mail) has an id "gmail-" + 32 hex characters, and its
/// Gmail account is its parent.
/// </summary>
public static class ResearchSources
{
    public const string Overleaf = "overleaf", Gmail = "gmail";
    private const string GmailPrefix = "gmail-";

    public static bool IsKnown(string? source) => source is null or Overleaf or Gmail;

    public static bool IsGmailWorkstreamId(string? id) => id is { Length: 38 } && id.StartsWith(GmailPrefix, StringComparison.Ordinal) &&
        id.AsSpan(GmailPrefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;

    /// <summary>Any member of a research card: an Overleaf project or a Gmail workstream.</summary>
    public static bool IsMemberId(string? id) => ResearchEvidence.IsProjectId(id) || IsGmailWorkstreamId(id);

    public static string NewGmailWorkstreamId() => GmailPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>The source an id belongs to (from its form).</summary>
    public static string SourceOf(string id) => IsGmailWorkstreamId(id) ? Gmail : Overleaf;
}
