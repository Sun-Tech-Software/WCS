namespace Wcs.SampleWms;

/// <summary>
/// The sample's own admin routes, under <c>/sample-admin</c>. <b>Not part of the contract</b>: the WCS
/// never calls them. They stand in for the screens and the ERP of a real WMS - the ones that create
/// orders - so that the quick start can show an extraction, and they let a person see what the WMS
/// holds. They require the same <c>X-Api-Key</c> as the contract.
/// </summary>
public static class AdminEndpoints
{
    public const string Prefix = "/sample-admin";
    public const string State = "state";
    public const string ExtractionOrders = "extraction-orders";

    /// <summary>The body of <c>POST /sample-admin/extraction-orders</c>.</summary>
    /// <param name="LoadUnitIds">The load units to bring out.</param>
    /// <param name="BayCellId">The bay cell they go to; the outfeed bay (900) when omitted.</param>
    public sealed record CreateExtractionOrder(IReadOnlyList<decimal> LoadUnitIds, decimal? BayCellId = null);

    public static RouteGroupBuilder MapSampleAdmin(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup(Prefix).WithTags("Sample admin (not part of the contract)");

        // Everything the sample holds: cells with their positions, load units and where they are,
        // reservations, extraction orders with the state of each row, and the journal of what the WCS
        // reported.
        admin.MapGet(State, (SampleWarehouse wms) => Results.Ok(wms.Snapshot()));

        // Create an extraction order: the WCS picks it up on its next turn (GET extractions/pending-work),
        // brings the load units to the bay and reports the outcome of each row.
        admin.MapPost(ExtractionOrders, (CreateExtractionOrder request, SampleWarehouse wms) =>
        {
            var (order, error) = wms.CreateExtractionOrder(request.LoadUnitIds, request.BayCellId ?? SampleData.OutfeedBayCellId);
            return order is null
                ? Results.Problem(error, statusCode: StatusCodes.Status400BadRequest)
                : Results.Created($"{Prefix}/{State}", new { orderId = order.Id, pickingCellId = order.PickingCellId, rows = order.Rows.Select(r => new { rowId = r.Id, loadUnitId = r.LoadUnitId }) });
        });

        return admin;
    }
}
