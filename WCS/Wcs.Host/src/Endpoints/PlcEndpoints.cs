using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.Engine.Api;
using Wcs.Host.Mapping;
using Wcs.Host.Security;

namespace Wcs.Host.Endpoints;

internal static class PlcEndpoints
{
    public static RouteGroupBuilder MapPlc(this RouteGroupBuilder api)
    {
        var group = api.MapGroup(ApiRoutes.PlcConnections);
        group.MapGet("/", async () => (await PlcSettingsFacade.ConnectionsAsync()).Select(DtoMapping.ToDto).ToList()).WithName("ListPlcConnections");
        // Le due guardie delle scritture dell'impianto (compito 4b della console, decisione del
        // proprietario del 26/9): il permesso di modifica (423 senza) e il motore fermo (409,
        // EngineBusyException da PlcSettingsFacade.ReplaceAsync). Fino ad allora la rotta non aveva
        // ne' l'uno ne' l'altro: chiunque poteva togliere una connessione che il motore stava usando.
        // Dal compito 4c (decisione del 27/9) anche una terza, dentro la stessa scrittura: una
        // connessione che un dispositivo usa ancora non si toglie (409, PlcConnectionInUseException,
        // con connessioni e dispositivi nel campo plcConnectionsInUse).
        group.MapPut("/", async (PlcConnectionDto[] connections) =>
        {
            await PlcSettingsFacade.ReplaceAsync(connections.Select(DtoMapping.ToEntity).ToList());
            return Results.NoContent();
        }).RequiresEditPermission().WithName("ReplacePlcConnections");
        group.MapGet("/{id:long}/status", async (long id) =>
            await PlcSettingsFacade.FindAsync(id) is { } found ? Results.Ok(DtoMapping.ToStatus(found)) : Results.NotFound()).WithName("PlcConnectionStatus");
        return api;
    }
}
