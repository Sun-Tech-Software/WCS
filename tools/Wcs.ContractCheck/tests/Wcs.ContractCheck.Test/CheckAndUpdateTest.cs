using System.Text.Json.Nodes;
using Xunit;

namespace Wcs.ContractCheck.Test;

/// <summary>
/// What the two commands do with a difference: <c>check</c> fails on any, <c>update</c> writes it unless it is
/// breaking without a new major version, and <c>check --baseline</c> applies the same rule to the committed
/// documents against those of the target branch.
/// </summary>
public sealed class CheckAndUpdateTest : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("wcs-contract-check-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static JsonObject Contract(string version, bool withLog = true)
    {
        var document = RepositoryContracts.Read(HostApiDocument.FileName).DeepClone().AsObject();
        document["info"]!["version"] = version;
        if (!withLog) document["paths"]!.AsObject().Remove("/api/v1/log");
        return document;
    }

    private void Commit(string folder, JsonObject document)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, HostApiDocument.FileName), ContractFiles.ToText(document));
    }

    private static Dictionary<string, JsonObject> Generated(JsonObject document) => new() { [HostApiDocument.FileName] = document };

    [Fact]
    public void UpdateRefusesABreakingChangeWithTheSameMajorVersion()
    {
        Commit(_folder, Contract("1.0.0"));
        var output = new StringWriter();

        var exit = ContractCheckProgram.Update(_folder, Generated(Contract("1.1.0", withLog: false)), output);

        Assert.Equal(2, exit);
        Assert.Contains("BREAKING  GET /api/v1/log: operation removed", output.ToString());
        Assert.Contains("HostApiContract.Version", output.ToString());
        Assert.Equal("1.0.0", ContractComparer.Version(ContractFiles.Read(Path.Combine(_folder, HostApiDocument.FileName))!));
    }

    [Fact]
    public void UpdateWritesABreakingChangeWithANewMajorVersion()
    {
        Commit(_folder, Contract("1.0.0"));

        var exit = ContractCheckProgram.Update(_folder, Generated(Contract("2.0.0", withLog: false)), new StringWriter());

        Assert.Equal(0, exit);
        Assert.Equal("2.0.0", ContractComparer.Version(ContractFiles.Read(Path.Combine(_folder, HostApiDocument.FileName))!));
    }

    [Fact]
    public void CheckFailsOnAnyDifferenceEvenACompatibleOne()
    {
        Commit(_folder, Contract("1.0.0", withLog: false));
        var output = new StringWriter();

        var exit = ContractCheckProgram.Check(_folder, Generated(Contract("1.0.0")), baseline: null, output);

        Assert.Equal(1, exit);
        Assert.Contains("compatible  GET /api/v1/log: operation added", output.ToString());
    }

    [Fact]
    public void CheckAgainstABaselineFailsOnABreakingChangeWithoutANewMajorVersion()
    {
        var baseline = Path.Combine(_folder, "baseline");
        Commit(baseline, Contract("1.0.0"));
        Commit(_folder, Contract("1.1.0", withLog: false));
        var output = new StringWriter();

        var exit = ContractCheckProgram.Check(_folder, Generated(Contract("1.1.0", withLog: false)), baseline, output);

        Assert.Equal(1, exit);
        Assert.Contains("against the baseline: 1.0.0 -> 1.1.0", output.ToString());
    }

    [Fact]
    public void CheckAgainstABaselineAcceptsACompatibleChange()
    {
        var baseline = Path.Combine(_folder, "baseline");
        Commit(baseline, Contract("1.0.0", withLog: false));
        Commit(_folder, Contract("1.1.0"));

        var exit = ContractCheckProgram.Check(_folder, Generated(Contract("1.1.0")), baseline, new StringWriter());

        Assert.Equal(0, exit);
    }
}
