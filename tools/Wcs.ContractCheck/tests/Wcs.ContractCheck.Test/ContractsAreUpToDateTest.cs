using Xunit;

namespace Wcs.ContractCheck.Test;

/// <summary>
/// The same check CI runs (<c>Wcs.ContractCheck check</c>): the documents generated from the code are the
/// committed ones, and every example is valid against them. A change of the contract that was not made on
/// purpose, with <c>Wcs.ContractCheck update</c>, fails here.
/// </summary>
[Collection("host")]
public sealed class ContractsAreUpToDateTest
{
    [Fact]
    public async Task TheCommittedContractsAreWhatTheCodeGenerates()
    {
        var output = new StringWriter();

        var exit = ContractCheckProgram.Check(RepositoryContracts.Folder, await ContractFiles.GenerateAsync(), baseline: null, output);

        Assert.True(exit == 0, output.ToString());
    }
}
