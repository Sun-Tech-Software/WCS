using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.Engine.Api;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence.Entities;
using Wcs.Host.Mapping;
using Wcs.Host.Security;

namespace Wcs.Host.Endpoints;

internal static class ExtractionQueueEndpoints
{
    public static RouteGroupBuilder MapExtractionQueue(this RouteGroupBuilder api)
    {
        var group = api.MapGroup(ApiRoutes.ExtractionQueue);
        group.MapGet("/", async (bool? processed, decimal? udc, int take = 500) =>
            (await ExtractionQueueQueries.ListAsync(processed, udc, take)).Select(DtoMapping.ToDto).ToList()).WithName("ListExtractionQueue");
        group.MapGet("/contains", async (decimal? udc, decimal? cell, decimal? excludeRow, decimal? excludeAssignmentId) =>
            udc != null ? Results.Ok(await ExtractionQueueQueries.ContainsLoadUnitAsync(udc.Value, excludeRow, excludeAssignmentId))
            : cell != null ? Results.Ok(await ExtractionQueueQueries.ContainsCellAsync(cell.Value))
            : Results.Problem("Serve udc o cell", statusCode: StatusCodes.Status400BadRequest)).WithName("ExtractionQueueContains");
        group.MapGet("/cells", ExtractionQueueQueries.CellsAsync).WithName("ExtractionQueueCells");
        group.MapGet("/load-units", ExtractionQueueQueries.LoadUnitsAsync).WithName("ExtractionQueueLoadUnits");
        group.MapGet("/{id:long}", async (long id) =>
            await ExtractionQueueQueries.FindAsync(id) is { } row ? Results.Ok(DtoMapping.ToDto(row)) : Results.NotFound()).WithName("GetExtractionQueueItem");
        group.MapPatch("/{id:long}/priority", async (long id, PriorityUpdate update) =>
            await ExtractionQueueQueries.SetPriorityAsync(id, update.Priority) ? Results.NoContent() : Results.NotFound())
            .DoesNotRequireEditPermission("la priorita' di una riga della coda di estrazione: lavoro del motore, non configurazione")
            .WithName("SetExtractionPriority");

        // Togliere una riga è un comando del motore, non una DELETE sulla tabella: la risposta è il comando accodato.
        group.MapDelete("/{id:long}", async (long id, string? workstationId, string? userId) =>
        {
            var row = await ExtractionQueueQueries.FindAsync(id);
            if (row == null) return Results.NotFound();
            var record = new CommandRecord
            {
                CommandType = (int)Command.CommandTypes.DeleteLuFromExtractionQueue,
                LoadUnitId = row.LoadUnitId,
                WorkstationId = workstationId,
                UserId = userId
            };
            await CommandGateway.EnqueueAsync(record);
            return Results.Accepted($"{ApiRoutes.Prefix}{ApiRoutes.Commands}/{record.Id}", DtoMapping.ToDto(record));
        }).DoesNotRequireEditPermission("accoda un comando del motore che toglie la riga: lavoro, non configurazione")
        .WithName("DeleteFromExtractionQueue");
        return api;
    }
}
