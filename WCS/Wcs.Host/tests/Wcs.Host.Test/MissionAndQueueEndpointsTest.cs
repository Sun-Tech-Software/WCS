using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.Engine;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Host.Test;

[Collection("host")]
public sealed class MissionAndQueueEndpointsTest(HostFixture host) : IAsyncLifetime
{
    private readonly List<Guid> _missionIds = [];
    private readonly Guid _queueSource = Guid.NewGuid();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var context = host.Store.CreateContext();
        await context.Missions.Where(m => _missionIds.Contains(m.MissionId)).ExecuteDeleteAsync();
        await context.ExtractionQueue.Where(q => q.SourceItemId == _queueSource).ExecuteDeleteAsync();
    }

    [Fact]
    public async Task AnActiveMissionIsListedAndExistsForItsLoadUnit()
    {
        var client = host.CreateAuthenticatedClient();
        var mission = new PathMission(Guid.NewGuid()) { OperationType = OperationTypes.Outbound, LuId = 77, LuCode = "UDC77" };
        _missionIds.Add(mission.MissionId);
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        try
        {
            var url = ApiRoutes.Prefix + ApiRoutes.Missions;
            Assert.Contains((await client.GetFromJsonAsync<List<MissionDto>>(url))!, m => m.MissionId == mission.MissionId);
            Assert.Contains((await client.GetFromJsonAsync<List<MissionDto>>($"{url}?udc=77"))!, m => m.MissionId == mission.MissionId);
            Assert.DoesNotContain((await client.GetFromJsonAsync<List<MissionDto>>($"{url}?udc=78"))!, m => m.MissionId == mission.MissionId);
            Assert.DoesNotContain((await client.GetFromJsonAsync<List<MissionDto>>($"{url}?type={(int)MissionTypes.Rejection}"))!, m => m.MissionId == mission.MissionId);
            Assert.Contains((await client.GetFromJsonAsync<List<MissionDto>>($"{url}?itemId={mission.PositionItemId}"))!, m => m.MissionId == mission.MissionId);
            Assert.DoesNotContain((await client.GetFromJsonAsync<List<MissionDto>>($"{url}?itemId={Guid.NewGuid()}"))!, m => m.MissionId == mission.MissionId);
            Assert.Equal("UDC77", (await client.GetFromJsonAsync<MissionDto>($"{url}/{mission.MissionId}"))!.LoadUnitCode);
            Assert.True(await client.GetFromJsonAsync<bool>($"{url}/exists?udc=77"));
            Assert.False(await client.GetFromJsonAsync<bool>($"{url}/exists?udc=78"));
            Assert.True(await client.GetFromJsonAsync<bool>($"{url}/exists?udc=77&status=0&status=1"));
            Assert.False(await client.GetFromJsonAsync<bool>($"{url}/exists?udc=77&status=99"));
            mission.Status = MissionStatusTypes.Error;
            Assert.True(await client.GetFromJsonAsync<bool>($"{url}/exists?udc=77"));
            Assert.False(await client.GetFromJsonAsync<bool>($"{url}/exists?udc=77&status=0&status=1"));
            Assert.Empty((await client.GetFromJsonAsync<List<MissionStepDto>>($"{url}/{mission.MissionId}/steps"))!);
            // Gli step di tutte le missioni attive in una richiesta sola: c'e' una missione, nessuno step.
            var all = await client.GetAsync($"{url}/steps");
            Assert.Equal(HttpStatusCode.OK, all.StatusCode);
            Assert.Empty((await all.Content.ReadFromJsonAsync<List<MissionStepDto>>())!);
        }
        finally { ActiveMissions.Instance.RemoveAll(m => m.MissionId == mission.MissionId); }

        // Missione non piu' attiva: non e' «zero step», non c'e'.
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"{ApiRoutes.Prefix}{ApiRoutes.Missions}/{mission.MissionId}/steps")).StatusCode);
    }

    /// <summary>
    /// Voce 44 della coda (decisione del proprietario del 10/10): il DTO della missione porta il segno
    /// della destinazione scelta dall'operatore (<c>PathMission.OperatorDestination</c>), nell'elenco e
    /// nella missione sola. La pagina ne ricava che cosa chiede lo stesso magazzino scelto di nuovo:
    /// un'altra cella con il segno, di nuovo una cella senza.
    /// </summary>
    [Fact]
    public async Task TheMissionCarriesTheMarkOfTheOperatorsChoice()
    {
        var client = host.CreateAuthenticatedClient();
        var storage = TestDevices.WithRow(Guid.NewGuid(), d => d.DeviceType = DeviceTypes.Storage);
        PathDevices.Instance.Add(storage);
        var chosen = new PathMission(Guid.NewGuid()) { LuId = 83, LuCode = "UDC83" };
        Assert.True(chosen.SetOperatorDestination(storage.ItemGuid));
        var configured = new PathMission(Guid.NewGuid()) { LuId = 84, LuCode = "UDC84", DestinationItemId = storage.ItemGuid };
        _missionIds.AddRange([chosen.MissionId, configured.MissionId]);
        ActiveMissions.Instance.Add(chosen, buildRoute: false);
        ActiveMissions.Instance.Add(configured, buildRoute: false);
        try
        {
            var url = ApiRoutes.Prefix + ApiRoutes.Missions;
            var listed = (await client.GetFromJsonAsync<List<MissionDto>>(url))!;
            Assert.True(listed.Single(m => m.MissionId == chosen.MissionId).OperatorDestination);
            Assert.False(listed.Single(m => m.MissionId == configured.MissionId).OperatorDestination);
            Assert.True((await client.GetFromJsonAsync<MissionDto>($"{url}/{chosen.MissionId}"))!.OperatorDestination);
            Assert.False((await client.GetFromJsonAsync<MissionDto>($"{url}/{configured.MissionId}"))!.OperatorDestination);
        }
        finally
        {
            ActiveMissions.Instance.RemoveAll(m => m.MissionId == chosen.MissionId || m.MissionId == configured.MissionId);
            PathDevices.Instance.Remove(storage.ItemGuid);
        }
    }

    /// <summary>
    /// Item 54, decision 4 of the owner of 13/10: a step of the mission is in flight, and the move waits for
    /// its end instead of being refused - 202, the mission still where it is, and its row carries the move
    /// that waits. Until item 54 it was a 409.
    /// </summary>
    [Fact]
    public async Task AMoveDuringAStepIsAcceptedAndTheMissionShowsItWhileItWaits()
    {
        var client = host.CreateAuthenticatedClient();
        var from = TestDevices.WithRow(Guid.NewGuid(), d => d.DeviceType = DeviceTypes.Automation);
        var to = TestDevices.WithRow(Guid.NewGuid(), d => d.DeviceType = DeviceTypes.Automation);
        PathDevices.Instance.Add(from);
        PathDevices.Instance.Add(to);
        var mission = new PathMission(Guid.NewGuid()) { LuId = 78, LuCode = "UDC78", PositionItemId = from.ItemGuid };
        var placed = mission.PositionWork;
        _missionIds.Add(mission.MissionId);
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        var step = new MissionStep { StepId = Guid.NewGuid(), MissionId = mission.MissionId, FromNodeId = from.ItemGuid, ToNodeId = to.ItemGuid };
        MissionSteps.Instance.Add(step);
        try
        {
            await placed;
            from.RunningStepId = step.StepId;
            from.Status = DeviceStatusTypes.Moving;

            var answer = await client.PutAsJsonAsync($"{ApiRoutes.Prefix}{ApiRoutes.Missions}/{mission.MissionId}/position",
                new MissionPositionUpdate(to.ItemGuid));

            Assert.Equal(HttpStatusCode.Accepted, answer.StatusCode);
            var row = (await client.GetFromJsonAsync<MissionDto>($"{ApiRoutes.Prefix}{ApiRoutes.Missions}/{mission.MissionId}"))!;
            Assert.Equal(MissionPendingChangeKind.Position, row.PendingChange);
            Assert.Equal(to.ItemGuid, row.PendingChangeItemId);
            Assert.Equal(from.ItemGuid, row.PositionItemId);
        }
        finally
        {
            await Wcs.Engine.Api.PendingMissionChanges.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Wcs.Engine.Api.PendingMissionChanges.Reset();
            await mission.PositionWork;
            ActiveMissions.Instance.RemoveAll(m => m.MissionId == mission.MissionId);
            MissionSteps.Instance.RemoveAll(s => s.MissionId == mission.MissionId);
            PathDevices.Instance.Remove(from.ItemGuid);
            PathDevices.Instance.Remove(to.ItemGuid);
        }
    }

    [Fact]
    public async Task AMissionIsMovedToAnotherDeviceThroughThePutRoute()
    {
        var client = host.CreateAuthenticatedClient();
        var from = TestDevices.WithRow(Guid.NewGuid(), d => d.DeviceType = DeviceTypes.Automation);
        var to = TestDevices.WithRow(Guid.NewGuid(), d => d.DeviceType = DeviceTypes.Automation);
        PathDevices.Instance.Add(from);
        PathDevices.Instance.Add(to);
        var mission = new PathMission(Guid.NewGuid()) { LuId = 79, LuCode = "UDC79", PositionItemId = from.ItemGuid };
        var placed = mission.PositionWork;
        var occupant = new PathMission(Guid.NewGuid()) { LuId = 80, LuCode = "UDC80" };
        _missionIds.AddRange([mission.MissionId, occupant.MissionId]);
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        ActiveMissions.Instance.Add(occupant, buildRoute: false);
        try
        {
            var url = $"{ApiRoutes.Prefix}{ApiRoutes.Missions}/{mission.MissionId}/position";

            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(url, new MissionPositionUpdate(to.ItemGuid))).StatusCode);
            Assert.Equal(to.ItemGuid, (await client.GetFromJsonAsync<MissionDto>($"{ApiRoutes.Prefix}{ApiRoutes.Missions}/{mission.MissionId}"))!.PositionItemId);

            // Un dispositivo che non esiste: 400. Un dispositivo che porta gia' un'altra missione: 409.
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(url, new MissionPositionUpdate(Guid.NewGuid()))).StatusCode);
            occupant.PositionItemId = from.ItemGuid;
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(url, new MissionPositionUpdate(from.ItemGuid))).StatusCode);
            Assert.Equal(to.ItemGuid, mission.PositionItemId);

            Assert.Equal(HttpStatusCode.NotFound,
                (await client.PutAsJsonAsync($"{ApiRoutes.Prefix}{ApiRoutes.Missions}/{Guid.NewGuid()}/position", new MissionPositionUpdate(to.ItemGuid))).StatusCode);
        }
        finally
        {
            // Ogni posizione scritta avvia il lavoro di sfondo del setter (PathMission.PositionWork),
            // che alla fine riscrive la riga della missione (StartUpdate). Non aspettato, poteva
            // arrivare dopo la cancellazione delle righe in DisposeAsync e lasciare UDC79 e UDC80 nel
            // database condiviso dalla collezione: GraphEndpointsTest, piu' avanti, trovava missioni
            // e rispondeva 409 a ogni PUT /plant/graph (undici rosse, una passata ogni tanto).
            await Task.WhenAll(placed, mission.PositionWork, occupant.PositionWork);
            ActiveMissions.Instance.RemoveAll(m => m.MissionId == mission.MissionId || m.MissionId == occupant.MissionId);
            PathDevices.Instance.Remove(from.ItemGuid);
            PathDevices.Instance.Remove(to.ItemGuid);
        }
    }

    /// <summary>
    /// Risposta 4 del proprietario dell'8/10: con il motore in Recover lo spostamento e' rifiutato con
    /// 503 e il titolo che la pagina riconosce (<see cref="MissionMoveProblems.EngineRecoveringTitle"/>),
    /// e la missione resta dov'era. Lo stato del motore si ripristina alla fine: la collezione lo
    /// condivide.
    /// </summary>
    [Fact]
    public async Task InRecoverTheMoveIsRefusedWithItsOwnTitle()
    {
        var client = host.CreateAuthenticatedClient();
        var from = TestDevices.WithRow(Guid.NewGuid(), d => d.DeviceType = DeviceTypes.Automation);
        var to = TestDevices.WithRow(Guid.NewGuid(), d => d.DeviceType = DeviceTypes.Automation);
        PathDevices.Instance.Add(from);
        PathDevices.Instance.Add(to);
        var mission = new PathMission(Guid.NewGuid()) { LuId = 81, LuCode = "UDC81", PositionItemId = from.ItemGuid };
        var placed = mission.PositionWork;
        _missionIds.Add(mission.MissionId);
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        var previous = Navigator.NavStatus;
        try
        {
            await placed;
            Navigator.NavStatus = NavigatorStatusTypes.Recover;

            var answer = await client.PutAsJsonAsync($"{ApiRoutes.Prefix}{ApiRoutes.Missions}/{mission.MissionId}/position",
                new MissionPositionUpdate(to.ItemGuid));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, answer.StatusCode);
            var problem = await answer.Content.ReadFromJsonAsync<ProblemDetails>();
            Assert.Equal("Il motore sta ancora caricando le missioni", problem!.Title);
            Assert.Equal(from.ItemGuid, mission.PositionItemId);
            Assert.Same(mission, from.LoadedLu);
            Assert.Null(to.LoadedLu);
        }
        finally
        {
            Navigator.NavStatus = previous;
            await mission.PositionWork;
            ActiveMissions.Instance.RemoveAll(m => m.MissionId == mission.MissionId);
            PathDevices.Instance.Remove(from.ItemGuid);
            PathDevices.Instance.Remove(to.ItemGuid);
        }
    }

    [Fact]
    public async Task TheQueueIsListedQueriedReprioritisedAndADeleteBecomesACommand()
    {
        var client = host.CreateAuthenticatedClient();
        long id;
        await using (var context = host.Store.CreateContext())
        {
            var row = new ExtractionQueueItem { LoadUnitId = 500, CellId = 31, SourceItemId = _queueSource };
            await ExtractionQueueStore.UpsertAsync(context, row);
            await ExtractionQueueStore.UpsertAsync(context, new ExtractionQueueItem { LoadUnitId = 500, CellId = 32, SourceItemId = _queueSource, ExtractionRowId = 15 });
            await context.SaveChangesAsync();
            id = row.Id;
        }
        var url = ApiRoutes.Prefix + ApiRoutes.ExtractionQueue;
        Assert.Contains((await client.GetFromJsonAsync<List<ExtractionQueueItemDto>>(url))!, q => q.Id == id);
        Assert.Contains((await client.GetFromJsonAsync<List<ExtractionQueueItemDto>>($"{url}?udc=500"))!, q => q.Id == id);
        Assert.DoesNotContain((await client.GetFromJsonAsync<List<ExtractionQueueItemDto>>($"{url}?udc=501"))!, q => q.Id == id);
        Assert.True(await client.GetFromJsonAsync<bool>($"{url}/contains?udc=500"));
        Assert.False(await client.GetFromJsonAsync<bool>($"{url}/contains?udc=500&excludeRow=15")); // la riga senza ExtractionRowId non conta
        Assert.True(await client.GetFromJsonAsync<bool>($"{url}/contains?udc=500&excludeRow=16"));
        Assert.True(await client.GetFromJsonAsync<bool>($"{url}/contains?cell=31"));
        Assert.Contains(31m, (await client.GetFromJsonAsync<List<decimal>>($"{url}/cells"))!);
        Assert.Contains(500m, (await client.GetFromJsonAsync<List<decimal>>($"{url}/load-units"))!);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PatchAsJsonAsync($"{url}/{id}/priority", new PriorityUpdate(3))).StatusCode);
        Assert.Equal(3, (await client.GetFromJsonAsync<ExtractionQueueItemDto>($"{url}/{id}"))!.Priority);

        var deleted = await client.DeleteAsync($"{url}/{id}");
        Assert.Equal(HttpStatusCode.Accepted, deleted.StatusCode);
        var command = await deleted.Content.ReadFromJsonAsync<CommandDto>();
        Assert.Equal((4001, 500m), (command!.CommandType, command.LoadUnitId!.Value));
    }
}
