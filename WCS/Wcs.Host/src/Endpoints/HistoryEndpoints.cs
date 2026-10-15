using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.Engine.Api;
using Wcs.Host.Mapping;
using Wcs.Host.Security;
using static Wcs.Common.Enums;

namespace Wcs.Host.Endpoints;

internal static class HistoryEndpoints
{
    public static RouteGroupBuilder MapHistory(this RouteGroupBuilder api)
    {
        var handled = api.MapGroup(ApiRoutes.HandledLoadUnits);
        handled.MapGet("/", async (DateTime from, DateTime to, short? type, decimal? udc, decimal? process, int take = 1000) =>
            (await HistoryQueries.HandledLoadUnitsAsync(from, to, type, udc, process, take)).Select(DtoMapping.ToDto).ToList()).WithName("ListHandledLoadUnits");
        // groupBy arriva come string, non come SummaryPeriod: il binder degli enum delle minimal API
        // e' sensibile alle maiuscole (Enum.TryParse senza ignoreCase), e la rotta e' documentata con
        // i valori minuscoli (day|week|month|year), quindi l'ignoreCase lo si applica qui a mano.
        // Enum.TryParse da solo non basta: accetta anche le cifre («99») e gli elenchi separati da
        // virgola, che non sono valori della rotta; Enum.IsDefined li rimanda al 400.
        handled.MapGet("/summary", async (DateTime from, DateTime to, string? groupBy) =>
        {
            var period = SummaryPeriod.Day;
            if (groupBy != null && !(Enum.TryParse(groupBy, ignoreCase: true, out period) && Enum.IsDefined(period)))
                return Results.Problem($"groupBy non valido: '{groupBy}'. Valori ammessi: day, week, month, year.",
                    statusCode: StatusCodes.Status400BadRequest);
            return Results.Ok((await HistoryQueries.HandledLoadUnitSummaryAsync(from, to, period)).Select(DtoMapping.ToDto).ToList());
        }).WithName("HandledLoadUnitSummary");
        handled.MapGet("/exists", async (decimal? process, decimal? loadUnitProcess, decimal? noteHeader) =>
            process == null && loadUnitProcess == null && noteHeader == null
                ? Results.Problem("Serve process, loadUnitProcess o noteHeader", statusCode: StatusCodes.Status400BadRequest)
                : Results.Ok(await HistoryQueries.HandledLoadUnitExistsAsync(process, loadUnitProcess, noteHeader))).WithName("HandledLoadUnitExists");

        var log = api.MapGroup(ApiRoutes.Log);
        log.MapGet("/", async (DateTime from, DateTime to, Guid? itemId, Guid? missionId, int take = 1000) =>
            (await HistoryQueries.LogAsync(from, to, itemId, missionId, take)).Select(DtoMapping.ToDto).ToList()).WithName("ListLog");
        log.MapGet("/transit-times", async (DateTime from, DateTime to, string fromAsi, string toAsi) =>
            (await HistoryQueries.TransitTimesAsync(from, to, fromAsi, toAsi)).Select(DtoMapping.ToDto).ToList()).WithName("TransitTimes");
        log.MapPost("/", async (LogEntryRequest request) =>
        {
            await HistoryQueries.AppendLogAsync(DtoMapping.ToEntity(request));
            return Results.Accepted();
        }).DoesNotRequireEditPermission("una riga del registro, non la configurazione dell'impianto")
        .WithName("AppendLog");

        // I messaggi scambiati con i PLC: stessa forma della rotta del registro. Prende il posto
        // della SELECT su PCM_LOG che il FrmPcmLog del Client fa sul database del gestionale.
        var plcMessages = api.MapGroup(ApiRoutes.PlcMessages);
        plcMessages.MapGet("/", async (DateTime from, DateTime to, int take = 1000) =>
            (await HistoryQueries.PlcMessagesAsync(from, to, take)).Select(DtoMapping.ToDto).ToList()).WithName("ListPlcMessages");
        return api;
    }
}
