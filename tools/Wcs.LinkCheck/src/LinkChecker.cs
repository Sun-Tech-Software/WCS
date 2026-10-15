using System.Text.RegularExpressions;

namespace Wcs.LinkCheck;

/// <summary>A broken link: the document (relative to the root, with '/'), its line, the target, why.</summary>
public sealed record Finding(string File, int Line, string Target, string Problem)
{
    public override string ToString() => $"{File}:{Line}: ({Target}) {Problem}";
}

/// <summary>
/// Checks the links of every Markdown document under a root as GitHub resolves them: relative
/// targets exist with the exact case of the file system (GitHub is case-sensitive, Windows is not),
/// anchors are headings of the target, a line link is <c>file#L12</c> or <c>file#L12-L20</c> on a line
/// that exists, no target falls in a forbidden part, and no document writes an absolute local path.
/// External links (http, mailto, ...) are not followed.
/// </summary>
public sealed partial class LinkChecker
{
    /// <summary>Folders never read, at any depth.</summary>
    public static readonly IReadOnlyList<string> SkippedFolderNames = [".git", "bin", "obj", "node_modules", "TestResults"];

    /// <summary>
    /// Paths never read, relative to the root: the historical documents (kept as they were written)
    /// and this tool's own fixtures (broken on purpose).
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultExclusions =
        ["docs/history", "tools/Wcs.LinkCheck/tests/Wcs.LinkCheck.Test/Fixtures"];

    private readonly string _root;
    private readonly IReadOnlyList<string> _excluded;
    private readonly IReadOnlyList<string> _forbidden;
    private readonly Dictionary<string, MarkdownDocument> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _entries = new(StringComparer.Ordinal);

    public LinkChecker(string root, IEnumerable<string>? excluded = null, IEnumerable<string>? forbidden = null)
    {
        _root = Path.GetFullPath(root);
        _excluded = [.. DefaultExclusions.Concat(excluded ?? []).Select(Normalize)];
        _forbidden = [.. (forbidden ?? []).Select(Normalize)];
    }

    /// <summary>The documents read, relative to the root, in ordinal order.</summary>
    public IReadOnlyList<string> Documents() =>
        [.. Walk(_root).Select(Relative).Where(r => !IsUnder(r, _excluded)).Order(StringComparer.Ordinal)];

    public IReadOnlyList<Finding> Check()
    {
        var findings = new List<Finding>();
        foreach (var rel in Documents())
        {
            var doc = Load(rel);
            foreach (var link in doc.Links)
            {
                var problem = CheckLink(rel, link.Target);
                if (problem is not null) findings.Add(new Finding(rel, link.Line, link.Target, problem));
            }
            foreach (var p in doc.Prose)
            {
                foreach (Match m in LocalPathInProse().Matches(p.Text))
                    findings.Add(new Finding(rel, p.Line, m.Value, "absolute local path in the text"));
            }
        }
        return [.. findings.OrderBy(f => f.File, StringComparer.Ordinal).ThenBy(f => f.Line)];
    }

    private string? CheckLink(string fromRel, string rawTarget)
    {
        var target = rawTarget.Trim();
        if (LocalAbsolute().IsMatch(target)) return "absolute local path: use a path relative to the document";
        if (Scheme().IsMatch(target)) return null;   // external: http, https, mailto, ...

        var hash = target.IndexOf('#');
        var pathPart = hash >= 0 ? target[..hash] : target;
        var fragment = hash >= 0 ? Uri.UnescapeDataString(target[(hash + 1)..]) : null;
        var query = pathPart.IndexOf('?');
        if (query >= 0) pathPart = pathPart[..query];
        pathPart = Uri.UnescapeDataString(pathPart);

        string resolved;
        if (pathPart.Length == 0)
        {
            resolved = fromRel;
        }
        else
        {
            var baseDir = pathPart.StartsWith('/') ? "" : ParentOf(fromRel);
            var combined = Combine(baseDir, pathPart.TrimStart('/'));
            if (combined is null) return "goes above the root of the repository";
            resolved = combined;
        }

        if (IsUnder(resolved, _forbidden)) return "points to a part that is not in the public repository";

        var (exists, isDirectory, caseProblem) = Locate(resolved);
        if (!exists)
        {
            if (caseProblem is not null) return caseProblem;
            var line = LineSuffix().Match(resolved);
            if (line.Success && Locate(line.Groups[1].Value).Exists)
                return $"a line written as ':{line.Groups[2].Value}' does not work on GitHub: write '{Path.GetFileName(line.Groups[1].Value)}#L{line.Groups[2].Value}'";
            return "file not found";
        }

        if (string.IsNullOrEmpty(fragment)) return null;
        if (isDirectory) return "an anchor on a folder";
        if (IsMarkdown(resolved))
        {
            return Load(resolved).Anchors.Contains(fragment) ? null : $"no heading '#{fragment}' in {resolved}";
        }
        var lines = LineFragment().Match(fragment);
        if (!lines.Success) return "an anchor on a file that is not Markdown: only #L<n> or #L<n>-L<m> work there";
        var count = LineCount(resolved);
        var first = int.Parse(lines.Groups[1].Value);
        var last = lines.Groups[2].Success ? int.Parse(lines.Groups[2].Value) : first;
        if (first < 1 || last < first) return "the line range is empty";
        if (last > count) return $"line {last} does not exist: the file has {count} lines";
        return null;
    }

    private MarkdownDocument Load(string rel)
    {
        if (!_documents.TryGetValue(rel, out var doc))
        {
            doc = MarkdownDocument.Parse(File.ReadAllText(Full(rel)));
            _documents[rel] = doc;
        }
        return doc;
    }

    /// <summary>Whether the path exists with this exact case, segment by segment.</summary>
    private (bool Exists, bool IsDirectory, string? CaseProblem) Locate(string rel)
    {
        var dir = _root;
        var segments = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return (true, true, null);
        for (var i = 0; i < segments.Length; i++)
        {
            var names = Entries(dir);
            var exact = names.FirstOrDefault(n => string.Equals(n, segments[i], StringComparison.Ordinal));
            if (exact is null)
            {
                var other = names.FirstOrDefault(n => string.Equals(n, segments[i], StringComparison.OrdinalIgnoreCase));
                if (other is null) return (false, false, null);
                var onDisk = string.Join('/', segments.Take(i).Append(other));
                return (false, false, $"wrong case: on disk it is '{onDisk}' (GitHub is case-sensitive)");
            }
            dir = Path.Combine(dir, exact);
        }
        return (true, Directory.Exists(dir), null);
    }

    private string[] Entries(string dir)
    {
        if (!_entries.TryGetValue(dir, out var names))
        {
            names = Directory.Exists(dir)
                ? [.. Directory.EnumerateFileSystemEntries(dir).Select(e => Path.GetFileName(e))]
                : [];
            _entries[dir] = names;
        }
        return names;
    }

    private int LineCount(string rel)
    {
        var text = File.ReadAllText(Full(rel));
        if (text.Length == 0) return 0;
        var count = text.Count(c => c == '\n');
        return text.EndsWith('\n') ? count : count + 1;
    }

    private static IEnumerable<string> Walk(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            if (IsMarkdown(file)) yield return file;
        }
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            if (SkippedFolderNames.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase)) continue;
            foreach (var f in Walk(sub)) yield return f;
        }
    }

    private static bool IsMarkdown(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string rel, IReadOnlyList<string> prefixes) =>
        prefixes.Any(p => p.Length > 0 && (string.Equals(rel, p, StringComparison.OrdinalIgnoreCase)
            || rel.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase)));

    private string Relative(string full) => Path.GetRelativePath(_root, full).Replace('\\', '/');

    private string Full(string rel) => Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));

    private static string ParentOf(string rel)
    {
        var slash = rel.LastIndexOf('/');
        return slash < 0 ? "" : rel[..slash];
    }

    /// <summary>A relative path joined to a folder and normalised; null when it climbs above the root.</summary>
    private static string? Combine(string baseDir, string path)
    {
        var parts = new List<string>(baseDir.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var seg in path.Replace('\\', '/').Split('/'))
        {
            if (seg is "" or ".") continue;
            if (seg == "..")
            {
                if (parts.Count == 0) return null;
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(seg);
        }
        return string.Join('/', parts);
    }

    private static string Normalize(string rel) => rel.Replace('\\', '/').Trim('/');

    [GeneratedRegex(@"^(?:[A-Za-z]:[\\/]|\\\\|file:)", RegexOptions.IgnoreCase)]
    private static partial Regex LocalAbsolute();

    [GeneratedRegex(@"(?<![\w/\\])[A-Za-z]:\\[^\s)\]`'""»]*")]
    private static partial Regex LocalPathInProse();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+-]*:")]
    private static partial Regex Scheme();

    [GeneratedRegex(@"^(.*\.[A-Za-z0-9]+):(\d+)(?:-\d+)?$")]
    private static partial Regex LineSuffix();

    [GeneratedRegex(@"^L(\d+)(?:-L(\d+))?$")]
    private static partial Regex LineFragment();
}
