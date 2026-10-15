using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.Engine;
using Wcs.Engine.Api;
using Wcs.Engine.Items;
using Wcs.Host.Mapping;
using Wcs.Host.Security;
using static Wcs.Common.Enums;

namespace Wcs.Host.Endpoints;

internal static class CommandEndpoints
{
    public static RouteGroupBuilder MapCommands(this RouteGroupBuilder api)
    {
        var group = api.MapGroup(ApiRoutes.Commands);

        group.MapPost("/", async (CommandRequest request) =>
        {
            if (!Enum.IsDefined(typeof(Command.CommandTypes), request.CommandType))
                return Results.Problem($"Tipo di comando sconosciuto: {request.CommandType}", statusCode: StatusCodes.Status400BadRequest);
            if (!CommandGateway.IsWritableStatus(request.Status))
                return Results.Problem(
                    $"Stato {request.Status} non scrivibile da fuori: si accetta 0 (da eseguire) o 2 (già concluso); Running, Warning ed Error li scrive il motore.",
                    statusCode: StatusCodes.Status400BadRequest);
            if (CommandGateway.RequiresRunningEngine(request.CommandType) && Navigator.NavStatus != NavigatorStatusTypes.Run)
                return Results.Problem($"Il motore è in {Navigator.NavStatus}: il comando {request.CommandType} richiede Run",
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            var record = DtoMapping.ToEntity(request);
            var id = await CommandGateway.EnqueueAsync(record);
            return Results.Created($"{ApiRoutes.Prefix}{ApiRoutes.Commands}/{id}", DtoMapping.ToDto(record));
        }).DoesNotRequireEditPermission("un comando accodato al motore, non la configurazione dell'impianto")
        .WithName("EnqueueCommand");

        group.MapGet("/", async (short? status, string? workstationId, string? userId, DateTime? from, DateTime? to,
                Guid? itemId, short? showStatus, int? minType, int? maxType, string? excludeUserId, int take = 200) =>
            (await CommandGateway.ListAsync(new CommandFilter(status, workstationId, userId, from, to, itemId, showStatus, minType, maxType, excludeUserId, take)))
                .Select(DtoMapping.ToDto).ToList())
            .WithName("ListCommands");

        group.MapGet("/{id:long}", async (long id) =>
            await CommandGateway.FindAsync(id) is { } found ? Results.Ok(DtoMapping.ToDto(found)) : Results.NotFound())
            .WithName("GetCommand");

        group.MapPut("/{id:long}", async (long id, CommandUpdate update) =>
            await CommandGateway.UpdateAsync(id, update.Status, update.ErrorDescription, update.Info, update.ShowStatus) switch
            {
                CommandGateway.UpdateResults.Updated => Results.NoContent(),
                CommandGateway.UpdateResults.NotFound => Results.NotFound(),
                CommandGateway.UpdateResults.UnknownStatus => Results.Problem(
                    $"Stato {update.Status} sconosciuto: gli stati di un comando sono 0 (da eseguire), 1 (in esecuzione), 2 (concluso), 3 (avviso), 99 (errore).",
                    statusCode: StatusCodes.Status400BadRequest),
                _ => Results.Problem(
                    $"Il comando {id} e' gia' partito: non si riporta lo stato a {update.Status}, il motore lo rieseguirebbe.",
                    statusCode: StatusCodes.Status400BadRequest)
            })
            .DoesNotRequireEditPermission("lo stato di un comando della coda, non la configurazione dell'impianto")
            .WithName("UpdateCommand");

        return api;
    }
}
