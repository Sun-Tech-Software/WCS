using System.Text.RegularExpressions;

namespace Wcs.LinkCheck;

/// <summary>A link written in a Markdown document: its 1-based line and its raw target.</summary>
public sealed record MarkdownLink(int Line, string Target);

/// <summary>Text written outside code that is not a link but must not be there (a local path).</summary>
public sealed record MarkdownProse(int Line, string Text);

/// <summary>
/// What the link checker needs of a Markdown file: the links it writes, the anchors it offers and
/// its prose outside code. Fenced code blocks and code spans are not Markdown: nothing in them is a
/// link or a heading.
/// </summary>
public sealed partial class MarkdownDocument
{
    public IReadOnlyList<MarkdownLink> Links { get; }
    public IReadOnlySet<string> Anchors { get; }
    public IReadOnlyList<MarkdownProse> Prose { get; }

    private MarkdownDocument(IReadOnlyList<MarkdownLink> links, IReadOnlySet<string> anchors, IReadOnlyList<MarkdownProse> prose)
    {
        Links = links;
        Anchors = anchors;
        Prose = prose;
    }

    public static MarkdownDocument Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var links = new List<MarkdownLink>();
        var prose = new List<MarkdownProse>();
        var headings = new List<string>();
        var explicitAnchors = new HashSet<string>(StringComparer.Ordinal);

        string? fence = null;           // the opening fence while inside a fenced block
        string? previous = null;        // the previous line of prose, for setext headings
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var fenceMatch = Fence().Match(line);
            if (fence is not null)
            {
                if (fenceMatch.Success && fenceMatch.Groups[1].Value[0] == fence[0] && fenceMatch.Groups[1].Value.Length >= fence.Length
                    && fenceMatch.Groups[2].Value.Trim().Length == 0)
                {
                    fence = null;
                }
                continue;
            }
            if (fenceMatch.Success)
            {
                fence = fenceMatch.Groups[1].Value;
                previous = null;
                continue;
            }

            var visible = BlankCodeSpans(line);
            var atx = AtxHeading().Match(line);
            if (atx.Success)
            {
                headings.Add(ClosingHashes().Replace(atx.Groups[2].Value, "").Trim());
                previous = null;
            }
            else if (previous is not null && SetextUnderline().IsMatch(line))
            {
                headings.Add(previous.Trim());
                previous = null;
                continue;
            }
            else
            {
                previous = string.IsNullOrWhiteSpace(line) ? null : line;
            }

            foreach (Match m in HtmlAnchor().Matches(visible)) explicitAnchors.Add(m.Groups[1].Value);
            foreach (var target in InlineTargets(visible)) links.Add(new MarkdownLink(i + 1, target));
            var def = ReferenceDefinition().Match(visible);
            if (def.Success) links.Add(new MarkdownLink(i + 1, Unwrap(def.Groups[1].Value)));
            foreach (Match m in HtmlLink().Matches(visible)) links.Add(new MarkdownLink(i + 1, m.Groups[1].Value));
            // The prose without the link targets, which are checked as links
            prose.Add(new MarkdownProse(i + 1, LinkTarget().Replace(visible, "]")));
        }

        var anchors = new HashSet<string>(GitHubSlug.OfAll(headings), StringComparer.Ordinal);
        anchors.UnionWith(explicitAnchors);
        return new MarkdownDocument(links, anchors, prose);
    }

    /// <summary>The targets of the inline links and images of a line: <c>[text](target "title")</c>.</summary>
    private static IEnumerable<string> InlineTargets(string line)
    {
        var at = 0;
        while ((at = line.IndexOf("](", at, StringComparison.Ordinal)) >= 0)
        {
            var start = at + 2;
            at = start;
            if (line.LastIndexOf('[', start - 2) < 0) continue;
            var i = start;
            while (i < line.Length && line[i] == ' ') i++;
            string target;
            if (i < line.Length && line[i] == '<')
            {
                var close = line.IndexOf('>', i);
                if (close < 0) continue;
                target = line[(i + 1)..close];
            }
            else
            {
                // Balanced parentheses are part of the target; a space ends it (a title may follow).
                var depth = 0;
                var j = i;
                for (; j < line.Length; j++)
                {
                    var c = line[j];
                    if (c == '\\' && j + 1 < line.Length) { j++; continue; }
                    if (c == '(') depth++;
                    else if (c == ')') { if (depth == 0) break; depth--; }
                    else if (c == ' ') break;
                }
                if (j >= line.Length) continue;
                target = line[i..j];
            }
            if (target.Length > 0) yield return target;
        }
    }

    private static string Unwrap(string target) =>
        target.StartsWith('<') && target.EndsWith('>') ? target[1..^1] : target;

    /// <summary>The line with its code spans replaced by spaces: same columns, no code.</summary>
    private static string BlankCodeSpans(string line) =>
        GitHubSlug.CodeSpan().Replace(line, m => new string(' ', m.Length));

    [GeneratedRegex(@"\]\([^)]*\)")]
    private static partial Regex LinkTarget();

    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})(.*)$")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^ {0,3}(#{1,6})(?:[ \t]+(.*?))?[ \t]*$")]
    private static partial Regex AtxHeading();

    [GeneratedRegex(@"(?:^|[ \t]+)#+[ \t]*$")]
    private static partial Regex ClosingHashes();

    [GeneratedRegex(@"^ {0,3}(?:=+|-+)[ \t]*$")]
    private static partial Regex SetextUnderline();

    [GeneratedRegex(@"^ {0,3}\[[^\]]+\]:[ \t]*(\S+)")]
    private static partial Regex ReferenceDefinition();

    [GeneratedRegex(@"<a\s[^>]*\b(?:name|id)\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlAnchor();

    [GeneratedRegex(@"<(?:a|img)\s[^>]*\b(?:href|src)\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlLink();
}
