using System.Net;
using System.Net.Http.Json;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.Engine;
using Wcs.Engine.Persistence.Entities;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Host.Test;

[Collection("host")]
public sealed class HistoryAndPlcEndpointsTest(HostFixture host)
{
    /// <summary>Il primo giorno del mese corrente: lo storico via API non risponde su date precedenti.</summary>
    private static DateTime FirstOfThisMonth => new(DateTime.Now.Year, DateTime.Now.Month, 1);

    [Fact]
    public async Task ALogEntryPostedIsReadBackWithinItsInterval()
    {
        var client = host.CreateAuthenticatedClient();
        var url = ApiRoutes.Prefix + ApiRoutes.Log;
        var mission = Guid.NewGuid();
        var posted = await client.PostAsJsonAsync(url, new LogEntryRequest(null, "9999", null, null, 0, "traccia web", null, mission));
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);
        var from = DateTime.Now.AddMinutes(-1);
        var to = DateTime.Now.AddMinutes(1);
        var entries = await client.GetFromJsonAsync<List<LogEntryDto>>($"{url}?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}&missionId={mission}");
        Assert.Contains(entries!, e => e.Description == "traccia web");
    }

    [Fact]
    public async Task TwoLogRowsNinetySecondsApartGiveATransitTimeOfNinetySeconds()
    {
        var client = host.CreateAuthenticatedClient();
        var url = ApiRoutes.Prefix + ApiRoutes.Log;
        var mission = Guid.NewGuid();
        var start = FirstOfThisMonth.AddDays(1).AddHours(10);

        Assert.Equal(HttpStatusCode.Accepted,
            (await client.PostAsJsonAsync(url, new LogEntryRequest(null, "0100", null, null, 1, "partenza", null, mission, start))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted,
            (await client.PostAsJsonAsync(url, new LogEntryRequest(null, "0200", null, null, 1, "arrivo", null, mission, start.AddSeconds(90)))).StatusCode);

        var window = $"from={Uri.EscapeDataString(start.AddMinutes(-1).ToString("O"))}&to={Uri.EscapeDataString(start.AddMinutes(5).ToString("O"))}";
        var transit = await client.GetFromJsonAsync<List<TransitTime>>($"{url}/transit-times?{window}&fromAsi=01%25&toAsi=02%25");
        var crossing = Assert.Single(transit!, t => t.MissionId == mission);
        Assert.Equal(90, crossing.Seconds);
    }

    [Fact]
    public async Task AMonthBeforeThisOneIs400()
    {
        var client = host.CreateAuthenticatedClient();
        var from = Uri.EscapeDataString(FirstOfThisMonth.AddDays(-1).ToString("O"));
        var to = Uri.EscapeDataString(DateTime.Now.ToString("O"));
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"{ApiRoutes.Prefix}{ApiRoutes.Log}?from={from}&to={to}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"{ApiRoutes.Prefix}{ApiRoutes.HandledLoadUnits}?from={from}&to={to}")).StatusCode);
    }

    [Fact]
    public async Task PostingALogEntryDatedThePreviousMonthIs400()
    {
        var client = host.CreateAuthenticatedClient();
        var url = ApiRoutes.Prefix + ApiRoutes.Log;
        var mission = Guid.NewGuid();
        var previousMonth = FirstOfThisMonth.AddDays(-1);
        var posted = await client.PostAsJsonAsync(url, new LogEntryRequest(null, "9999", null, null, 0, "fuori mese", null, mission, previousMonth));
        Assert.Equal(HttpStatusCode.BadRequest, posted.StatusCode);
    }

    [Fact]
    public async Task HandledLoadUnitsAreListedSummarisedAndChecked()
    {
        var client = host.CreateAuthenticatedClient();
        var day = FirstOfThisMonth.AddDays(1);
        await using (var context = host.History.CreateContext())
        {
            context.HandledLoadUnits.Add(new HandledLoadUnit { MissionId = Guid.NewGuid(), Type = 2, Date = day.AddHours(12), NoteHeaderId = 44 });
            await context.SaveChangesAsync();
        }
        var url = ApiRoutes.Prefix + ApiRoutes.HandledLoadUnits;
        var range = $"from={FirstOfThisMonth:yyyy-MM-dd}T00:00:00&to={day.AddDays(1):yyyy-MM-dd}T00:00:00";
        Assert.Contains((await client.GetFromJsonAsync<List<HandledLoadUnitDto>>($"{url}?{range}&type=2"))!, h => h.NoteHeaderId == 44);
        Assert.Contains((await client.GetFromJsonAsync<List<HandledLoadUnitSummary>>($"{url}/summary?{range}&groupBy=month"))!,
            s => s.Type == 2 && s.Period == DateOnly.FromDateTime(FirstOfThisMonth));
        Assert.True(await client.GetFromJsonAsync<bool>($"{url}/exists?noteHeader=44"));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{url}/exists")).StatusCode);
    }

    [Fact]
    public async Task AGroupByOutsideTheAllowedValuesIs400()
    {
        var client = host.CreateAuthenticatedClient();
        var url = ApiRoutes.Prefix + ApiRoutes.HandledLoadUnits;
        var range = $"from={FirstOfThisMonth:yyyy-MM-dd}T00:00:00&to={DateTime.Now.AddDays(1):yyyy-MM-dd}T00:00:00";
        // «99» e' il caso che Enum.TryParse da solo lasciava passare: rispondeva 200 raggruppando per giorno.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{url}/summary?{range}&groupBy=99")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{url}/summary?{range}&groupBy=decade")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{url}/summary?{range}&groupBy=month")).StatusCode);
    }

    /// <summary>
    /// I messaggi scambiati con i PLC si rileggono dalla rotta: e' quello che il
    /// <c>FrmPcmLog</c> del Client oggi cerca in <c>PCM_LOG</c> sul database del gestionale.
    /// </summary>
    /// <remarks>
    /// Le righe si scrivono nel database dei messaggi dei PLC, che dalla voce <c>D183</c> non e'
    /// piu' lo storico. La rotta non cambia — <c>GET /api/v1/history/plc-messages</c> — perche' e'
    /// la stessa domanda: cambia solo dove sta il dato.
    /// </remarks>
    [Fact]
    public async Task PlcMessagesWrittenToThePlcDatabaseAreReadBackFromTheRoute()
    {
        var client = host.CreateAuthenticatedClient();
        var day = FirstOfThisMonth.AddDays(2).AddHours(7);
        await using (var context = host.PlcLog.CreateContext())
        {
            context.PlcMessages.Add(new PlcMessage
            {
                Date = day, PlcId = 3, Type = 0, HeaderLength = 8, Syscode = "SYS9", OpType = 1, OpCode = 2,
                OpProgress = 4294967295, FrameLength = 120, DataLength = 96, DataCrc32 = 4000000000,
                CodingError = false, Asi = "0300", Subitem = "0001", Partition = "0002",
                MessageLength = 128, MessageId = 555, Xml = "<Received />"
            });
            await context.SaveChangesAsync();
        }

        var url = ApiRoutes.Prefix + ApiRoutes.PlcMessages;
        var range = $"from={FirstOfThisMonth:yyyy-MM-dd}T00:00:00&to={day.AddDays(1):yyyy-MM-dd}T00:00:00";
        var rows = await client.GetFromJsonAsync<List<PlcMessageDto>>($"{url}?{range}&take=100");
        var row = Assert.Single(rows!, m => m.MessageId == 555);
        Assert.Equal(3m, row.PlcId);
        Assert.Equal("0300", row.Asi);
        // Il progressivo al massimo di uint: la vecchia colonna int di PCM_LOG lo avrebbe scritto negativo.
        Assert.Equal(4294967295L, row.OpProgress);
        Assert.Equal("<Received />", row.Xml);
    }

    /// <summary>
    /// Con le due guardie della rotta soddisfatte - il permesso di modifica e il motore fermo, che la
    /// fixture lascia in Run (compito 4b della console, si veda <see cref="PlcConnectionsGuardsTest"/>).
    /// </summary>
    [Fact]
    public async Task PlcConnectionsAreReplacedAndTheirStatusRead()
    {
        var client = host.CreateAuthenticatedClient();
        var url = ApiRoutes.Prefix + ApiRoutes.PlcConnections;
        var previous = Navigator.NavStatus;
        await host.DetachEveryDeviceFromItsPlcConnectionAsync();
        using (host.GrantEditPermission(client))
        {
            Navigator.NavStatus = NavigatorStatusTypes.Stop;
            try
            {
                var put = await client.PutAsJsonAsync(url, new[] { new PlcConnectionDto(1, "PLC1", "10.0.0.1", 2011, "10.0.0.2", 2012) });
                Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
            }
            finally { Navigator.NavStatus = previous; }
        }

        Assert.Equal("PLC1", Assert.Single((await client.GetFromJsonAsync<List<PlcConnectionDto>>(url))!).Description);
        Assert.Equal(1, (await client.GetFromJsonAsync<PlcConnectionStatus>($"{url}/1/status"))!.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{url}/2/status")).StatusCode);
    }
}
