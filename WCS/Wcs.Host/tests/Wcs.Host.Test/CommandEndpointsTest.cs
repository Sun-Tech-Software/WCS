using System.Net;
using System.Net.Http.Json;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.Engine;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Host.Test;

[Collection("host")]
public sealed class CommandEndpointsTest(HostFixture host)
{
    private const string Url = ApiRoutes.Prefix + ApiRoutes.Commands;

    [Fact]
    public async Task ACommandIsCreatedReadBackAndUpdated()
    {
        var client = host.CreateAuthenticatedClient();
        var created = await client.PostAsJsonAsync(Url, new CommandRequest(1001, WorkstationId: "PC1", UserId: "operatore"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = await created.Content.ReadFromJsonAsync<CommandDto>();
        Assert.NotNull(dto);
        Assert.True(dto.Id > 0);
        Assert.Equal(created.Headers.Location!.ToString(), $"{Url}/{dto.Id}");

        var list = await client.GetFromJsonAsync<List<CommandDto>>($"{Url}?status=0&workstationId=PC1");
        Assert.Contains(list!, c => c.Id == dto.Id);

        var updated = await client.PutAsJsonAsync($"{Url}/{dto.Id}", new CommandUpdate(2, null, "eseguito", 1));
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        Assert.Equal("eseguito", (await client.GetFromJsonAsync<CommandDto>($"{Url}/{dto.Id}"))!.Info);
    }

    [Fact]
    public async Task AMissingCommandIs404AndABadTypeIs400()
    {
        var client = host.CreateAuthenticatedClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Url}/999999")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Url, new CommandRequest(42))).StatusCode);
    }

    /// <summary>
    /// A body that is not JSON is the caller's mistake: 400 with the reason, not 500. Typical case: a
    /// guide command rewritten for curl.exe in PowerShell 7.3+, where the backslashes of
    /// <c>{ \"commandType\": 1002 }</c> reach the host (quick start run of 15/10, D3). In Development, the
    /// environment of the quick start, minimal APIs throw BadHttpRequestException instead of answering
    /// 400 themselves, and the exception handler turned it into 500.
    /// </summary>
    [Fact]
    public async Task ABodyThatIsNotJsonIs400WithTheReason()
    {
        var client = host.CreateAuthenticatedClient();
        using var body = new StringContent("""{ \"commandType\": 1002 }""", System.Text.Encoding.UTF8, "application/json");

        var response = await client.PostAsync(Url, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.Equal("Richiesta non leggibile", problem!.Title);
        Assert.Equal("Failed to read parameter \"CommandRequest request\" from the request body as JSON.", problem.Detail);
    }

    [Fact]
    public async Task AWindowGivenWithTheOffsetFindsTheCommandJustCreated()
    {
        if (TimeZoneInfo.Local.BaseUtcOffset == TimeSpan.Zero) return; // su una macchina in UTC il test non distingue nulla
        // "O" porta l'offset e il binder consegna Kind.Utc; le colonne sono marcate con DateTime.Now.
        var client = host.CreateAuthenticatedClient();
        var created = await client.PostAsJsonAsync(Url, new CommandRequest(1001, WorkstationId: "PCFUSO"));
        var dto = (await created.Content.ReadFromJsonAsync<CommandDto>())!;
        var from = Uri.EscapeDataString(DateTime.Now.AddMinutes(-1).ToString("O"));
        var to = Uri.EscapeDataString(DateTime.Now.AddMinutes(1).ToString("O"));
        var list = await client.GetFromJsonAsync<List<CommandDto>>($"{Url}?from={from}&to={to}&workstationId=PCFUSO");
        Assert.Contains(list!, c => c.Id == dto.Id);
    }

    [Fact]
    public async Task ATraceAlreadyDoneIsAcceptedAndAnIntermediateStatusIs400()
    {
        var client = host.CreateAuthenticatedClient();
        var done = await client.PostAsJsonAsync(Url, new CommandRequest(1001, Status: 2));
        Assert.Equal(HttpStatusCode.Created, done.StatusCode);
        Assert.Equal((short)2, (await done.Content.ReadFromJsonAsync<CommandDto>())!.Status);

        var running = await client.PostAsJsonAsync(Url, new CommandRequest(1001, Status: 1));
        Assert.Equal(HttpStatusCode.BadRequest, running.StatusCode);
    }

    [Fact]
    public async Task AnOperatorTraceIsAcceptedAlreadyDoneAndListedAmongUserCommands()
    {
        var client = host.CreateAuthenticatedClient();
        var url = ApiRoutes.Prefix + ApiRoutes.Commands;
        var posted = await client.PostAsJsonAsync(url, new CommandRequest((int)CommandTypes.ChangeCellLockStatus, Info: "blocco celle", UserId: "MARIO", WorkstationId: "PC1", Status: 2));
        Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
        var trace = await posted.Content.ReadFromJsonAsync<CommandDto>();
        Assert.Equal((short)2, trace!.Status);
        Assert.Contains((await client.GetFromJsonAsync<List<CommandDto>>($"{url}?excludeUserId=ORCHESTRA_ROUTE"))!, c => c.Id == trace.Id);
    }

    [Fact]
    public async Task WithTheEngineStoppedAnOperatorTraceIsAcceptedButAnItemCommandIs503()
    {
        var previous = Navigator.NavStatus;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        try
        {
            var client = host.CreateAuthenticatedClient();
            var url = ApiRoutes.Prefix + ApiRoutes.Commands;

            // La traccia arriva gia' Done: il motore non la esegue, quindi non serve che sia in Run.
            var trace = await client.PostAsJsonAsync(url, new CommandRequest((int)CommandTypes.AddOrModifyLuPosition, Status: 2));
            Assert.Equal(HttpStatusCode.Created, trace.StatusCode);

            // Un comando vero su una UDC richiede invece il motore avviato.
            var device = await client.PostAsJsonAsync(url, new CommandRequest((int)CommandTypes.ItemUnloadLu));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, device.StatusCode);
        }
        finally { Navigator.NavStatus = previous; }
    }

    [Fact]
    public async Task EveryOperatorTraceTypeIsAcceptedAlreadyDone()
    {
        var client = host.CreateAuthenticatedClient();
        var url = ApiRoutes.Prefix + ApiRoutes.Commands;
        CommandTypes[] traceTypes =
        [
            CommandTypes.ModifyRushMoverStatus, CommandTypes.ChangeCellLockStatus,
            CommandTypes.AddOrModifyLuPosition, CommandTypes.ModifyDatabaseCellContents
        ];
        foreach (var traceType in traceTypes)
        {
            var posted = await client.PostAsJsonAsync(url, new CommandRequest((int)traceType, Status: 2));
            Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
            Assert.Equal((short)2, (await posted.Content.ReadFromJsonAsync<CommandDto>())!.Status);
        }
    }

    [Fact]
    public async Task WithoutAStoreTheAnswerIs503()
    {
        var client = host.CreateAuthenticatedClient();
        var previous = Navigator.Store;
        Navigator.Store = null;
        try
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync(Url)).StatusCode);
        }
        finally { Navigator.Store = previous; }
    }

    [Fact]
    public async Task ADeviceCommandWithTheEngineStoppedIs503()
    {
        var previous = Navigator.NavStatus;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        try
        {
            var response = await host.CreateAuthenticatedClient().PostAsJsonAsync(Url, new CommandRequest(3003, LoadUnitId: 1));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally { Navigator.NavStatus = previous; }
    }

    [Fact]
    public async Task TheCommandListFiltersByItemShowStatusTypeRangeAndExcludedUser()
    {
        Navigator.NavStatus = NavigatorStatusTypes.Run; // altri test della collection lo fermano e lo ripristinano in Dispose
        var client = host.CreateAuthenticatedClient();
        var url = ApiRoutes.Prefix + ApiRoutes.Commands;
        var item = Guid.NewGuid();
        var mine = await (await client.PostAsJsonAsync(url, new CommandRequest(2001, ItemId: item, UserId: "MARIO"))).Content.ReadFromJsonAsync<CommandDto>();
        var engine = await (await client.PostAsJsonAsync(url, new CommandRequest(3007, ItemId: item, UserId: "ORCHESTRA_ROUTE"))).Content.ReadFromJsonAsync<CommandDto>();
        var other = await (await client.PostAsJsonAsync(url, new CommandRequest(1001, UserId: "MARIO"))).Content.ReadFromJsonAsync<CommandDto>();

        var byItem = await client.GetFromJsonAsync<List<CommandDto>>($"{url}?itemId={item}");
        Assert.Contains(byItem!, c => c.Id == mine!.Id);
        Assert.Contains(byItem!, c => c.Id == engine!.Id);
        Assert.DoesNotContain(byItem!, c => c.Id == other!.Id);

        var users = await client.GetFromJsonAsync<List<CommandDto>>($"{url}?maxType=2999&excludeUserId=ORCHESTRA_ROUTE");
        Assert.Contains(users!, c => c.Id == mine!.Id);
        Assert.Contains(users!, c => c.Id == other!.Id);
        Assert.DoesNotContain(users!, c => c.Id == engine!.Id);

        var range = await client.GetFromJsonAsync<List<CommandDto>>($"{url}?minType=3000&maxType=3999");
        Assert.Contains(range!, c => c.Id == engine!.Id);
        Assert.DoesNotContain(range!, c => c.Id == mine!.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"{url}/{mine!.Id}", new CommandUpdate(mine.Status, null, null, 1))).StatusCode);
        var unseen = await client.GetFromJsonAsync<List<CommandDto>>($"{url}?showStatus=0&itemId={item}");
        Assert.DoesNotContain(unseen!, c => c.Id == mine.Id);
        Assert.Contains(unseen!, c => c.Id == engine!.Id);
    }

    [Fact]
    public async Task ATraceCarriesItsOwnDateAndACommandWithoutOneTakesNow()
    {
        var client = host.CreateAuthenticatedClient();
        var data = new DateTime(2026, 3, 4, 5, 6, 7);
        var posted = await client.PostAsJsonAsync(Url, new CommandRequest((int)CommandTypes.ChangeCellLockStatus, Info: "blocco", UserId: "MARIO", Status: 2, Date: data));
        Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
        Assert.Equal(data, (await posted.Content.ReadFromJsonAsync<CommandDto>())!.Date);

        var now = await (await client.PostAsJsonAsync(Url, new CommandRequest(1001))).Content.ReadFromJsonAsync<CommandDto>();
        Assert.InRange(now!.Date, DateTime.Now.AddMinutes(-1), DateTime.Now.AddMinutes(1));
    }

    /// <summary>
    /// «Segna come letto» del Client rispedisce la fotografia che aveva in mano: se nel frattempo il
    /// motore ha concluso il comando, lo stato piu' basso lo rimetterebbe in coda e il motore lo
    /// rieseguirebbe. La PUT deve rifiutare la retrocessione e lasciare passare lo stesso stato.
    /// </summary>
    [Fact]
    public async Task APutThatWouldTakeADoneCommandBackToToDoIsRefusedAndTheSameStatusStillMarksItRead()
    {
        var client = host.CreateAuthenticatedClient();
        var done = (await (await client.PostAsJsonAsync(Url, new CommandRequest(1001, Status: 2))).Content.ReadFromJsonAsync<CommandDto>())!;

        // Senza ShowStatus e' una retrocessione vera: rifiutata.
        var backwards = await client.PutAsJsonAsync($"{Url}/{done.Id}", new CommandUpdate(0, null, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);
        Assert.Equal((short)2, (await client.GetFromJsonAsync<CommandDto>($"{Url}/{done.Id}"))!.Status);

        // Con ShowStatus e' «segna come letto» su una fotografia vecchia: passa, e scrive solo ShowStatus.
        var staleRead = await client.PutAsJsonAsync($"{Url}/{done.Id}", new CommandUpdate(0, null, null, 1));
        Assert.Equal(HttpStatusCode.NoContent, staleRead.StatusCode);
        var afterStale = (await client.GetFromJsonAsync<CommandDto>($"{Url}/{done.Id}"))!;
        Assert.Equal((short)2, afterStale.Status);
        Assert.Equal((short)1, afterStale.ShowStatus);

        var read = await client.PutAsJsonAsync($"{Url}/{done.Id}", new CommandUpdate(2, null, null, 1));
        Assert.Equal(HttpStatusCode.NoContent, read.StatusCode);
        var after = (await client.GetFromJsonAsync<CommandDto>($"{Url}/{done.Id}"))!;
        Assert.Equal((short)2, after.Status);
        Assert.Equal((short)1, after.ShowStatus);
    }

    /// <summary>Uno stato fuori dall'enum passerebbe il filtro dei pendenti e resterebbe in coda per sempre.</summary>
    [Fact]
    public async Task APutWithAStatusOutsideTheEnumIs400()
    {
        var client = host.CreateAuthenticatedClient();
        var command = (await (await client.PostAsJsonAsync(Url, new CommandRequest(1001))).Content.ReadFromJsonAsync<CommandDto>())!;

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{Url}/{command.Id}", new CommandUpdate(-1, null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{Url}/{command.Id}", new CommandUpdate(7, null, null, null))).StatusCode);
        Assert.Equal((short)0, (await client.GetFromJsonAsync<CommandDto>($"{Url}/{command.Id}"))!.Status);
    }
}
