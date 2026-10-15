using System.Text.Json.Nodes;

namespace Wcs.ContractCheck;

/// <summary>
/// <code>
/// dotnet run --project tools/Wcs.ContractCheck/src -- check  [--contracts &lt;folder&gt;] [--baseline &lt;folder&gt;]
/// dotnet run --project tools/Wcs.ContractCheck/src -- update [--contracts &lt;folder&gt;]
/// </code>
/// <c>check</c> regenerates both documents from the code and fails (exit 1) if they differ from the
/// committed ones, listing every difference as breaking or compatible. With <c>--baseline</c> (the
/// documents of the branch a pull request goes into, or of the last release) it also fails if the
/// committed documents break the baseline without raising the major part of their version.
/// <c>update</c> writes the regenerated documents, and refuses (exit 2) a breaking change whose major
/// version was not raised. The contracts folder defaults to <c>guide/contracts</c> (or <c>docs/contracts</c>)
/// next to <c>Wcs.slnx</c>, searched from the current folder.
/// </summary>
internal static class ContractCheckProgram
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("check" or "update"))
        {
            Console.Error.WriteLine("Usage: Wcs.ContractCheck check|update [--contracts <folder>] [--baseline <folder>]");
            return 64;
        }

        var contracts = Option(args, "--contracts") ?? ContractFiles.FindFolder(Directory.GetCurrentDirectory());
        if (contracts == null)
        {
            Console.Error.WriteLine("Wcs.slnx not found above the current folder: pass --contracts.");
            return 64;
        }

        var generated = await ContractFiles.GenerateAsync();
        return args[0] == "check"
            ? Check(contracts, generated, Option(args, "--baseline"), Console.Out)
            : Update(contracts, generated, Console.Out);
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    internal static int Check(string contracts, IReadOnlyDictionary<string, JsonObject> generated, string? baseline, TextWriter output)
    {
        var failed = false;
        foreach (var (name, document) in generated)
        {
            var committed = ContractFiles.Read(Path.Combine(contracts, name));
            if (committed == null)
            {
                output.WriteLine($"{name}: missing from {contracts}. Run: dotnet run --project tools/Wcs.ContractCheck/src -- update");
                failed = true;
                continue;
            }

            if (!JsonNode.DeepEquals(committed, document))
            {
                failed = true;
                output.WriteLine($"{name}: the code no longer matches the committed contract ({ContractComparer.Version(committed)}).");
                Report(ContractComparer.Compare(committed, document), output);
                output.WriteLine("  If the change is deliberate, run: dotnet run --project tools/Wcs.ContractCheck/src -- update");
            }
            else output.WriteLine($"{name}: up to date ({ContractComparer.Version(committed)}).");

            if (baseline == null) continue;
            var previous = ContractFiles.Read(Path.Combine(baseline, name));
            if (previous == null)
            {
                output.WriteLine($"{name}: not in the baseline, nothing to compare.");
                continue;
            }
            if (!VersionAllows(previous, committed, $"{name} against the baseline", output)) failed = true;
        }

        // The examples are checked against the documents the code produces, so an example the change
        // invalidates fails here too.
        var examples = ContractExamples.Validate(contracts, generated);
        foreach (var error in examples) output.WriteLine("example " + error);
        if (examples.Count > 0) failed = true;
        else output.WriteLine($"{ContractExamples.All(contracts).Count} examples valid.");
        return failed ? 1 : 0;
    }

    internal static int Update(string contracts, IReadOnlyDictionary<string, JsonObject> generated, TextWriter output)
    {
        var refused = false;
        foreach (var (name, document) in generated)
        {
            var committed = ContractFiles.Read(Path.Combine(contracts, name));
            if (committed != null && !VersionAllows(committed, document, name, output))
            {
                refused = true;
                continue;
            }
            Directory.CreateDirectory(contracts);
            File.WriteAllText(Path.Combine(contracts, name), ContractFiles.ToText(document));
            output.WriteLine($"{name}: written ({ContractComparer.Version(document)}).");
        }
        return refused ? 2 : 0;
    }

    /// <summary>A breaking change passes only with a higher major version.</summary>
    private static bool VersionAllows(JsonObject before, JsonObject after, string what, TextWriter output)
    {
        var changes = ContractComparer.Compare(before, after);
        if (changes.Count == 0) return true;
        output.WriteLine($"{what}: {ContractComparer.Version(before)} -> {ContractComparer.Version(after)}");
        Report(changes, output);
        if (changes.Any(c => c.Kind == ChangeKind.Breaking) && ContractComparer.Major(after) <= ContractComparer.Major(before))
        {
            var constant = after["x-wcs-provider"]?.GetValue<string>() == "wms" ? "WarehouseContract.Version" : "HostApiContract.Version";
            output.WriteLine($"  Breaking changes need a new major version: raise it in {constant}, or make the change compatible.");
            return false;
        }
        return true;
    }

    private static void Report(IReadOnlyList<ContractChange> changes, TextWriter output)
    {
        foreach (var change in changes.OrderByDescending(c => c.Kind).ThenBy(c => c.Where, StringComparer.Ordinal))
            output.WriteLine("  " + change);
    }
}
