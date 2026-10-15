using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Wcs.LinkCheck;

/// <summary>
/// The anchors GitHub gives to the headings of a rendered Markdown file: the heading's text as it is
/// rendered (no markup), lower-cased; letters, digits, marks, '-' and '_' kept, spaces turned into
/// '-', everything else dropped; a repeated slug gets "-1", "-2", ... in order of appearance.
/// </summary>
public static partial class GitHubSlug
{
    /// <summary>The slug of one heading, before the de-duplication suffix.</summary>
    public static string Of(string headingMarkdown)
    {
        var text = RenderedText(headingMarkdown);
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
        {
            if (c == ' ') { sb.Append('-'); continue; }
            if (c == '-' || c == '_') { sb.Append(c); continue; }
            switch (CharUnicodeInfo.GetUnicodeCategory(c))
            {
                case UnicodeCategory.UppercaseLetter:
                case UnicodeCategory.LowercaseLetter:
                case UnicodeCategory.TitlecaseLetter:
                case UnicodeCategory.ModifierLetter:
                case UnicodeCategory.OtherLetter:
                case UnicodeCategory.NonSpacingMark:
                case UnicodeCategory.SpacingCombiningMark:
                case UnicodeCategory.EnclosingMark:
                case UnicodeCategory.DecimalDigitNumber:
                case UnicodeCategory.LetterNumber:
                case UnicodeCategory.OtherNumber:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>The slugs of a sequence of headings, with GitHub's suffixes for repeated ones.</summary>
    public static IReadOnlyList<string> OfAll(IEnumerable<string> headingsMarkdown)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var h in headingsMarkdown)
        {
            var slug = Of(h);
            var candidate = slug;
            if (seen.TryGetValue(slug, out var n))
            {
                do { n++; candidate = $"{slug}-{n}"; } while (seen.ContainsKey(candidate));
                seen[slug] = n;
            }
            seen.TryAdd(candidate, 0);
            result.Add(candidate);
        }
        return result;
    }

    /// <summary>The text GitHub renders for a heading's inline Markdown.</summary>
    internal static string RenderedText(string markdown)
    {
        // Code spans keep their content literally; the rest loses its markup.
        var sb = new StringBuilder();
        var last = 0;
        foreach (Match m in CodeSpan().Matches(markdown))
        {
            sb.Append(StripMarkup(markdown[last..m.Index]));
            sb.Append(m.Groups[2].Value.Trim());
            last = m.Index + m.Length;
        }
        sb.Append(StripMarkup(markdown[last..]));
        return sb.ToString().Trim();
    }

    private static string StripMarkup(string s)
    {
        s = Image().Replace(s, "$1");
        s = InlineLink().Replace(s, "$1");
        s = HtmlTag().Replace(s, "");
        s = s.Replace("\\", "");
        // Emphasis with underscores only at word edges: snake_case keeps its underscores.
        s = EdgeUnderscores().Replace(s, "");
        return s;
    }

    [GeneratedRegex(@"(`+)(.+?)\1")]
    internal static partial Regex CodeSpan();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex InlineLink();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])_+|_+(?![\p{L}\p{N}])")]
    private static partial Regex EdgeUnderscores();
}
