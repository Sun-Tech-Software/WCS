using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace Wcs.ContractCheck.Test;

/// <summary>
/// The engine state each plant write needs (<see cref="HostApiAnswers.EngineState"/>, <c>x-wcs-engine-state</c>)
/// is knowledge written by hand, as is the "Engine" column of <c>reference/host-api.md</c>: the two must agree.
/// </summary>
public sealed partial class HostApiDocumentTest
{
    [Fact]
    public void TheEngineStatesAgreeWithTheHostReference()
    {
        var document = RepositoryContracts.Read(HostApiDocument.FileName);
        var reference = File.ReadAllText(Path.Combine(RepositoryContracts.Folder, "..", "reference", "host-api.md"));

        var documented = new Dictionary<string, string>();
        foreach (Match row in Row().Matches(reference))
        {
            var engine = row.Groups["engine"].Value.Trim();
            if (engine.Length == 0 || engine.StartsWith("see ", StringComparison.Ordinal)) continue;
            var operation = document["paths"]![row.Groups["path"].Value]?[row.Groups["method"].Value.ToLowerInvariant()];
            Assert.True(operation != null, $"{row.Groups["method"].Value} {row.Groups["path"].Value} is in the reference, not in the contract");
            documented[operation!["operationId"]!.GetValue<string>()] = engine.StartsWith("Stop", StringComparison.Ordinal) ? "Stop" : "not Run";
        }

        var declared = HostApiAnswers.EngineState.ToDictionary(e => e.Key, e => e.Value.StartsWith("Stop", StringComparison.Ordinal) ? "Stop" : "not Run");
        Assert.Equal(documented.OrderBy(e => e.Key), declared.OrderBy(e => e.Key));
    }

    [Fact]
    public void EveryWriteOfTheLeaseIsMarkedAndAsksForTheHolder()
    {
        var document = RepositoryContracts.Read(HostApiDocument.FileName);
        foreach (var operation in document["paths"]!.AsObject().SelectMany(p => p.Value!.AsObject().Select(o => o.Value!.AsObject())))
        {
            var lease = operation["x-wcs-edit-lease"]!.GetValue<bool>();
            var header = (operation["parameters"]?.AsArray() ?? []).Any(p => p!["name"]!.GetValue<string>() == "X-Edit-Holder");
            Assert.Equal(lease, header);
            Assert.Equal(lease, operation["responses"]!["423"] != null);
        }
    }

    [GeneratedRegex(@"^\| (?<method>GET|POST|PUT|DELETE|PATCH) \| `(?<path>/api/v1/[^`]*)` \|[^|]*\|(?<engine>[^|]*)\|", RegexOptions.Multiline)]
    private static partial Regex Row();
}
