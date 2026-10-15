using System.Text.Json;
using Wcs.Wms.Contracts;
using Xunit;

namespace Wcs.Wms.Contracts.Test;

public class WarehouseTransitionContractTest
{
    [Fact]
    public void AllocateOperationIdRoundTripsThroughJson()
    {
        var operationId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var request = new AllocateLoadUnitRequest(
            42, 7, "P1", true, false, 150, 148, "wcs", operationId);

        var copy = JsonSerializer.Deserialize<AllocateLoadUnitRequest>(
            JsonSerializer.Serialize(request));

        Assert.Equal(operationId, copy!.OperationId);
    }

    [Fact]
    public void ReleaseOperationIdRoundTripsThroughJson()
    {
        var operationId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var request = new ReleaseLoadUnitRequest(42, false, "wcs", operationId);

        var copy = JsonSerializer.Deserialize<ReleaseLoadUnitRequest>(
            JsonSerializer.Serialize(request));

        Assert.Equal(operationId, copy!.OperationId);
    }

    [Fact]
    public void LegacyAllocateConstructorDefaultsOperationIdToEmpty()
    {
        var request = new AllocateLoadUnitRequest(
            42, 7, "P1", true, false, 150, 148, "wcs");

        Assert.Equal(Guid.Empty, request.OperationId);
    }

    [Fact]
    public void LegacyReleaseJsonDefaultsOperationIdToEmpty()
    {
        var request = JsonSerializer.Deserialize<ReleaseLoadUnitRequest>(
            """{"loadUnitId":42,"manual":false,"requestedByUserId":"wcs"}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(Guid.Empty, request!.OperationId);
    }
}
