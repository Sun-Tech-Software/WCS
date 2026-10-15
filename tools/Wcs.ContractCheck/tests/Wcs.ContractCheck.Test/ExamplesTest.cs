using System.Text.Json.Nodes;
using Xunit;

namespace Wcs.ContractCheck.Test;

/// <summary>
/// The examples of <c>guide/contracts/examples</c> are valid against the committed schemas, and the schemas are
/// strict enough to refuse the mistakes an integrator makes: the wrong casing, a missing field.
/// </summary>
public sealed class ExamplesTest
{
    /// <summary>The storing and the extraction cycle, on both sides, and the receipts.</summary>
    private static readonly string[] Required =
    [
        "examples/wms/DescribeLoadUnit.response.json",
        "examples/wms/ReserveStorage.request.json",
        "examples/wms/ReserveStorage.response.json",
        "examples/wms/GetStorageReservation.response.json",
        "examples/wms/AllocateLoadUnit.request.json",
        "examples/wms/ReleaseStorageReservation.request.json",
        "examples/wms/LocateLoadUnit.response.json",
        "examples/wms/GetPendingExtractionWork.response.json",
        "examples/wms/FindLoadUnit.request.json",
        "examples/wms/FindLoadUnit.response.json",
        "examples/wms/ReportExtractionOutcomes.request.json",
        "examples/wms/GetPausedExtractionRows.request.json",
        "examples/wms/GetPausedExtractionRows.response.json",
        "examples/wms/ReportMissionServedAssignment.request.json",
        "examples/wms/ReleaseLoadUnit.request.json",
        "examples/wms/ReleaseLoadUnitCell.request.json",
        "examples/wms/ReleaseLoadUnitCell.response.json",
        "examples/host/GetStatus.response.json",
        "examples/host/EnqueueCommand.request.json",
        "examples/host/EnqueueCommand.response.json",
        "examples/host/ListMissions.response.json",
        "examples/host/AcquireOrRenewEditLock.request.json",
        "examples/host/AcquireOrRenewEditLock.response.json",
        "examples/host/ReplacePlantGraph.response.423.json",
        "examples/host/ListWmsCells.response.503.json",
    ];

    [Fact]
    public void EveryExampleIsValidAgainstItsContract()
    {
        var errors = ContractExamples.Validate(RepositoryContracts.Folder);

        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    [Fact]
    public void TheStoringAndExtractionCyclesHaveTheirExamples()
    {
        var present = ContractExamples.All(RepositoryContracts.Folder);

        Assert.Empty(Required.Except(present));
    }

    private static JsonNode Example(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryContracts.Folder, "examples", "wms", name)))!;

    private static JsonObject Recased(JsonNode example, Func<string, string> casing) =>
        new([.. example.AsObject().Select(p => KeyValuePair.Create(casing(p.Key), p.Value?.DeepClone()))]);

    [Fact]
    public void ACamelCaseRequestSentInPascalCaseIsRefused()
    {
        var wrong = Recased(Example("AllocateLoadUnit.request.json"), name => char.ToUpperInvariant(name[0]) + name[1..]);

        var errors = ContractExamples.Validate(RepositoryContracts.Read(WmsContractDocument.FileName), "AllocateLoadUnit.request", wrong);

        Assert.Contains(errors, e => e.Contains("the required property loadUnitId is missing", StringComparison.Ordinal));
    }

    [Fact]
    public void APascalCaseRequestSentInCamelCaseIsRefused()
    {
        var wrong = Recased(Example("ReserveStorage.request.json"), name => char.ToLowerInvariant(name[0]) + name[1..]);

        var errors = ContractExamples.Validate(RepositoryContracts.Read(WmsContractDocument.FileName), "ReserveStorage.request", wrong);

        Assert.Contains(errors, e => e.Contains("the required property LoadUnitId is missing", StringComparison.Ordinal));
    }

    [Fact]
    public void AnAnswerWithoutARequiredOutcomeIsRefused()
    {
        var wrong = Example("ReportPickedExtractionBecameDeposit.response.json").AsObject().DeepClone().AsObject();
        wrong.Remove("assignment");

        var errors = ContractExamples.Validate(RepositoryContracts.Read(WmsContractDocument.FileName), "ReportPickedExtractionBecameDeposit.response", wrong);

        Assert.Contains(errors, e => e.Contains("the required property assignment is missing", StringComparison.Ordinal));
    }

    [Fact]
    public void ADecimalIdentifierIsValidWithOrWithoutItsDecimalPart()
    {
        var document = RepositoryContracts.Read(WmsContractDocument.FileName);
        var example = Example("ReleaseLoadUnit.request.json").AsObject();

        foreach (var id in new[] { "5001", "5001.0" })
        {
            example["loadUnitId"] = JsonNode.Parse(id);
            Assert.Empty(ContractExamples.Validate(document, "ReleaseLoadUnit.request", example));
        }
    }
}
