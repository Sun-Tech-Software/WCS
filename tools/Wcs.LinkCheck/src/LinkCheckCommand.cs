namespace Wcs.LinkCheck;

/// <summary>The command line of the link checker (see Program.cs).</summary>
public static class LinkCheckCommand
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        string? root = null;
        var excluded = new List<string>();
        var forbidden = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--exclude" when i + 1 < args.Length:
                    excluded.Add(args[++i]);
                    break;
                case "--forbid" when i + 1 < args.Length:
                    forbidden.Add(args[++i]);
                    break;
                case "-h" or "--help":
                    output.WriteLine("Usage: Wcs.LinkCheck [<root>] [--exclude <path>]... [--forbid <path>]...");
                    return 0;
                default:
                    if (args[i].StartsWith('-') || root is not null)
                    {
                        error.WriteLine($"Unexpected argument: {args[i]}");
                        return 2;
                    }
                    root = args[i];
                    break;
            }
        }
        root ??= Directory.GetCurrentDirectory();
        if (!Directory.Exists(root))
        {
            error.WriteLine($"Root not found: {root}");
            return 2;
        }

        var checker = new LinkChecker(root, excluded, forbidden);
        var documents = checker.Documents();
        var findings = checker.Check();
        if (findings.Count == 0)
        {
            output.WriteLine($"Links OK: {documents.Count} Markdown documents checked under {Path.GetFullPath(root)}.");
            return 0;
        }
        error.WriteLine($"{findings.Count} broken link(s) in {findings.Select(f => f.File).Distinct().Count()} of {documents.Count} Markdown documents:");
        foreach (var f in findings) error.WriteLine($"  {f}");
        return 1;
    }
}
