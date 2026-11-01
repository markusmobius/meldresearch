using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MeldResearch.LocalBackend.Services.Research;

/// <summary>
/// Turns an email body into the text that is analyzed: HTML becomes plain text, and quoted earlier messages, forwarded
/// copies and signatures are cut, so each email contributes only what its sender wrote. Runs on this computer.
/// </summary>
internal static partial class EmailText
{
    public const int MaxCharacters = 8000;

    [GeneratedRegex(@"<(script|style|head)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Invisible();
    [GeneratedRegex(@"<blockquote\b[^>]*>.*?</blockquote\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Blockquote();
    [GeneratedRegex(@"<\s*(br|/p|/div|/li|/tr|/h[1-6])\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreaks();
    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();
    // "On Mon, 3 Nov 2026 at 10:00, A. Smith <a@b.org> wrote:" (possibly wrapped onto two lines) and common equivalents.
    [GeneratedRegex(@"^\s*(On\s.{0,200}?wrote:|Am\s.{0,200}?schrieb.{0,80}:|Le\s.{0,200}?a écrit\s?:)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ReplyHeader();
    [GeneratedRegex(@"^\s*(-{2,}\s*(Original Message|Forwarded message)\s*-{2,}|_{10,}|From:\s.+\r?\n(Sent|Date):\s.+)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ForwardOrOutlook();
    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex Spaces();
    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();

    public static string FromHtml(string html)
    {
        string text = Invisible().Replace(html, "");
        text = Blockquote().Replace(text, "\n");
        text = LineBreaks().Replace(text, "\n");
        text = Tags().Replace(text, "");
        return WebUtility.HtmlDecode(text);
    }

    /// <summary>The sender's own words: no quoted replies, forwarded copies or signature; normalized and capped.</summary>
    public static string Clean(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\u00a0', ' ');
        // A reply header that Gmail wrapped over two lines is joined first, so it is recognised.
        text = Regex.Replace(text, @"^(On\s[^\n]{0,200})\n([^\n]{0,120}wrote:)\s*$", "$1 $2", RegexOptions.Multiline);
        int cut = text.Length;
        foreach (var pattern in new[] { ReplyHeader(), ForwardOrOutlook() })
            if (pattern.Match(text) is { Success: true } match && match.Index < cut) cut = match.Index;
        text = text[..cut];
        var lines = new List<string>();
        foreach (string raw in text.Split('\n'))
        {
            string line = Spaces().Replace(raw, " ").TrimEnd();
            if (line == "--" || line == "-- ") break;
            if (line.TrimStart().StartsWith('>')) continue;
            lines.Add(line);
        }
        text = BlankLines().Replace(string.Join('\n', lines), "\n\n").Trim();
        if (text.Length > MaxCharacters)
        {
            const int keep = MaxCharacters - 4;
            int end = text.LastIndexOf('\n', keep);
            text = text[..(end > keep / 2 ? end : keep)].TrimEnd() + "\n[…]";
        }
        return text;
    }

    /// <summary>Gmail's base64url body data as UTF-8 text (invalid sequences replaced).</summary>
    public static string Decode(string data)
    {
        string base64 = data.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(base64)); }
        catch (FormatException) { return ""; }
    }

    /// <summary>The address in a From header ("A. Smith &lt;a@b.org&gt;" → a@b.org), lower case.</summary>
    public static string Address(string from)
    {
        var match = Regex.Match(from, @"<([^<>\s]+@[^<>\s]+)>");
        return (match.Success ? match.Groups[1].Value : from.Trim()).ToLowerInvariant();
    }
}
