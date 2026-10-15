using System.Text.RegularExpressions;
using Xunit;

namespace Wcs.Host.Test;

/// <summary>
/// Every project reference names its folders with their exact case. Windows does not care, so a
/// reference to <c>..\..\WCS.Connection.Interface</c> built there for months while the folder is
/// <c>Wcs.Connection.Interface</c>; on a case-sensitive file system (Linux, macOS with such a volume)
/// <c>dotnet build</c> does not find the project (quick start run of 15/10, D8).
/// </summary>
public sealed partial class ProjectReferenceCaseTest
{
    [Fact]
    public void EveryProjectReferenceMatchesTheCaseOfTheFoldersOnDisk()
    {
        var root = RepositoryRoot();
        var wrong = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                     .Where(f => !IsBuildOutput(root, f)))
        {
            foreach (Match reference in ReferencePattern().Matches(File.ReadAllText(file)))
            {
                var relative = reference.Groups[1].Value;
                if (relative.Contains("$(", StringComparison.Ordinal)) continue;

                var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, relative.Replace('\\', Path.DirectorySeparatorChar)));
                // A reference outside this repository (the Common submodule is inside it) is not ours to check.
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) continue;

                if (ExactCase(root, target) is { } onDisk && !string.Equals(onDisk, target[root.Length..], StringComparison.Ordinal))
                    wrong.Add($"{Path.GetRelativePath(root, file)}: {relative} (on disk: {onDisk})");
            }
        }

        Assert.True(wrong.Count == 0, "References whose case differs from the disk:" + Environment.NewLine + string.Join(Environment.NewLine, wrong));
    }

    /// <summary>The path below <paramref name="root"/> with the case of the entries on disk.</summary>
    private static string? ExactCase(string root, string target)
    {
        var current = root;
        var parts = new List<string>();
        foreach (var part in target[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = Directory.EnumerateFileSystemEntries(current)
                .Select(Path.GetFileName)
                .FirstOrDefault(name => string.Equals(name, part, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return null;
            parts.Add(entry);
            current = Path.Combine(current, entry);
        }

        return Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, parts);
    }

    private static bool IsBuildOutput(string root, string file) =>
        Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar)
            .Any(segment => segment is "bin" or "obj" or "node_modules" or ".git");

    private static string RepositoryRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder != null && !File.Exists(Path.Combine(folder.FullName, "Wcs.slnx")))
            folder = folder.Parent;
        return folder?.FullName.TrimEnd(Path.DirectorySeparatorChar)
               ?? throw new InvalidOperationException($"No Wcs.slnx above {AppContext.BaseDirectory}");
    }

    [GeneratedRegex("""<(?:ProjectReference\s+Include|Project\s+Path)="([^"*]+)"[^>]*>""")]
    private static partial Regex ReferencePattern();
}
