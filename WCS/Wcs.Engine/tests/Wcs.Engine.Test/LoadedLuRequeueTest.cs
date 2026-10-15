using Wcs.Connection.Interface.ConnectionData;
using Wcs.Connection.Interface.Events;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Items.Managers;
using Wcs.Engine.Persistence;
using Wcs.Engine.Test.Routing;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Chi rimette in coda al giro delle rotte la UDC caricata la legge una volta sola (voce 41, compito
/// 7, decisione del proprietario dell'11/10): <c>Standard.AddLoadedLuToRouteToBuildQueue</c>,
/// <c>AutomationDeviceLuManager.AddLoadedLuToRouteToBuildQueue</c> e
/// <c>AutomationDeviceConnectionManager.ProcessDeviceTypeSpecificLogic</c> leggevano
/// <c>device.LoadedLu</c> per il controllo e di nuovo per l'accodamento, e se in mezzo la missione
/// lasciava il dispositivo accodavano un nullo: il giro lo pescava e tornava senza fare niente, e la
/// missione in coda dietro aspettava un giro in piu'. E la accoda una volta sola anche se un altro la
/// accoda intanto (revisione finale della voce 41): lo sguardo alla coda e l'accodamento stanno
/// insieme (<c>AutomationDevice.RequeueLoadedLu</c>, <c>RoutesToBuild.EnqueueIfAbsent</c>).
/// </summary>
/// <remarks>
/// La missione sta su Q verso X, senza rotta; la cucitura <c>RouteQueueingPauseForTesting</c> ferma chi
/// accoda dopo aver letto la missione e prima dell'accodamento, e li' la prova sposta la missione su R,
/// o la accoda da un altro accodatore. Nessuna
/// attesa a tempo: i tetti di 30 s fanno fallire invece di appendere. Il motore sta in <c>Stop</c>, e in
/// <c>Run</c> solo dove la via provata lo chiede, con <c>AutoExecuteStep</c> spento su tutti i
/// dispositivi.
/// </remarks>
public sealed class LoadedLuRequeueTest : IDisposable
{
    private const decimal LoadUnitId = 43;
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly QueuedDatabaseWriter<WcsWrite>? _previousWriter = Navigator.StateWriter;
    private readonly FakeWarehouse _warehouse = new();
    private readonly RoutingTestPlant _plant = new(NavigatorStatusTypes.Stop);

    public LoadedLuRequeueTest()
    {
        Navigator.Store = _database;
        Navigator.StateWriter = null;
        Navigator.Warehouse = _warehouse;
        RoutesToBuild.Instance.Items = new();
    }

    public void Dispose()
    {
        RoutesToBuild.Instance.Items = new();
        _plant.Dispose();
        Navigator.Warehouse = _previousWarehouse;
        Navigator.StateWriter = _previousWriter;
        Navigator.Store = null;
        _database.Dispose();
    }

    /// <summary>
    /// Q esegue e vede la sua UDC senza uno step da fare: la strategia <c>Standard</c> la rimette in
    /// coda. Fra il controllo e l'accodamento la missione passa su R: in coda va la missione vista, una
    /// volta, e nessun nullo. Prima in coda andava il nullo, letto da Q ormai vuoto.
    /// </summary>
    [Fact]
    public async Task TheStandardStrategyQueuesTheMissionItSawAlsoWhenItLeavesMeanwhile()
    {
        var mission = await AMissionOnQTowardsXThatLeavesForRWhenQueued();

        Navigator.NavStatus = NavigatorStatusTypes.Run;
        try
        {
            await _plant["Q"].ExecuteStep().WaitAsync(Ceiling);
        }
        finally
        {
            Navigator.NavStatus = NavigatorStatusTypes.Stop;
        }

        await TheQueueHoldsTheMissionOnceAndNoNull(mission);
    }

    /// <summary>
    /// La UDC arriva su Q (<c>LoadLu</c>), che non e' la sua destinazione e non ha uno step da fare: il
    /// gestore delle UDC la rimette in coda. Fra il controllo e l'accodamento la missione passa su R: in
    /// coda va la missione vista, una volta, e nessun nullo. Prima in coda andava il nullo.
    /// </summary>
    [Fact]
    public async Task TheLuManagerQueuesTheMissionItSawAlsoWhenItLeavesMeanwhile()
    {
        var mission = await AMissionOnQTowardsXThatLeavesForRWhenQueued(startOn: "P");

        await _plant["Q"].LuManager!.LoadLu(mission.MissionId, activateExecuteStep: false).WaitAsync(Ceiling);

        await TheQueueHoldsTheMissionOnceAndNoNull(mission);
    }

    /// <summary>
    /// Il PLC conferma i dati della UDC su Q, che ha una destinazione fissa: il gestore delle
    /// connessioni (<c>ProcessDeviceTypeSpecificLogic</c>) la rimette in coda. Fra il controllo e
    /// l'accodamento la missione passa su R: in coda va la missione vista, una volta, e nessun nullo.
    /// Prima in coda andava il nullo. Il codice della UDC c'e' gia', e il gestionale non si interroga.
    /// </summary>
    [Fact]
    public async Task TheConnectionManagerQueuesTheMissionItSawAlsoWhenItLeavesMeanwhile()
    {
        var mission = await AMissionOnQTowardsXThatLeavesForRWhenQueued();
        mission.LuCode = "U43";
        _plant["Q"].DestinationItemId = _plant["X"].ItemGuid;
        var confirmed = new AsiSendLuDataEventArgs(_plant["Q"].ItemGuid, ResultCodeTypes.Success,
            new AsiSendLuData { DataConfirm = 1 });

        await new AutomationDeviceConnectionManager(_plant["Q"]).AsiSendLuData(confirmed).WaitAsync(Ceiling);

        await TheQueueHoldsTheMissionOnceAndNoNull(mission);
        Assert.Empty(_warehouse.Calls);
    }

    /// <summary>
    /// Q esegue e vede la sua UDC senza uno step da fare: la strategia <c>Standard</c> la rimette in
    /// coda. Fra la lettura e l'accodamento un altro accodatore, che aveva gia' guardato e non l'aveva
    /// trovata, la accoda: in coda va una volta sola. Prima lo sguardo e l'accodamento erano separati in
    /// ognuno dei tre accodatori della UDC caricata, e la missione finiva in coda due volte; il primo giro
    /// ne toglieva l'indice, e la copia rimasta non la vedeva piu' nessuno (rilievi M31 e M136).
    /// </summary>
    [Fact]
    public async Task TheStandardStrategyQueuesTheMissionOnceWhenAnotherQueuesItMeanwhile()
    {
        var mission = await AMissionOnQTowardsXThatLeavesForRWhenQueued(queuedMeanwhile: true);

        Navigator.NavStatus = NavigatorStatusTypes.Run;
        try
        {
            await _plant["Q"].ExecuteStep().WaitAsync(Ceiling);
        }
        finally
        {
            Navigator.NavStatus = NavigatorStatusTypes.Stop;
        }

        TheQueueHoldsTheMissionOnce(mission);
    }

    /// <summary>Lo stesso con il gestore delle UDC, all'arrivo della UDC su Q.</summary>
    [Fact]
    public async Task TheLuManagerQueuesTheMissionOnceWhenAnotherQueuesItMeanwhile()
    {
        var mission = await AMissionOnQTowardsXThatLeavesForRWhenQueued(startOn: "P", queuedMeanwhile: true);

        await _plant["Q"].LuManager!.LoadLu(mission.MissionId, activateExecuteStep: false).WaitAsync(Ceiling);

        TheQueueHoldsTheMissionOnce(mission);
    }

    /// <summary>Lo stesso con il gestore delle connessioni, alla conferma dei dati della UDC.</summary>
    [Fact]
    public async Task TheConnectionManagerQueuesTheMissionOnceWhenAnotherQueuesItMeanwhile()
    {
        var mission = await AMissionOnQTowardsXThatLeavesForRWhenQueued(queuedMeanwhile: true);
        mission.LuCode = "U43";
        _plant["Q"].DestinationItemId = _plant["X"].ItemGuid;
        var confirmed = new AsiSendLuDataEventArgs(_plant["Q"].ItemGuid, ResultCodeTypes.Success,
            new AsiSendLuData { DataConfirm = 1 });

        await new AutomationDeviceConnectionManager(_plant["Q"]).AsiSendLuData(confirmed).WaitAsync(Ceiling);

        TheQueueHoldsTheMissionOnce(mission);
    }

    /// <summary>
    /// Una missione su <paramref name="startOn"/> verso X, fra le attive e senza rotta, e su Q la
    /// cucitura che, la prima volta, la sposta su R, o con <paramref name="queuedMeanwhile"/> la accoda
    /// come un altro accodatore che aveva gia' guardato. Il motore e' fermo, la coda vuota.
    /// </summary>
    private async Task<PathMission> AMissionOnQTowardsXThatLeavesForRWhenQueued(string startOn = "Q",
        bool queuedMeanwhile = false)
    {
        foreach (var name in new[] { "P", "Q", "X", "R" }) _plant.Device(name);
        _plant.Link("P", "Q");
        _plant.Link("Q", "X");
        _plant.Link("Q", "R");

        var mission = new PathMission
        {
            PositionItemId = _plant[startOn].ItemGuid,
            DestinationItemId = _plant["X"].ItemGuid,
            LuId = LoadUnitId,
            MissionType = MissionTypes.MovingToItem
        };
        await mission.PositionWork;

        foreach (var name in _plant.DeviceNames) _plant[name].AutoExecuteStep = false;
        Navigator.NavStatus = NavigatorStatusTypes.Run;
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        Assert.Same(mission, ActiveMissions.Instance[mission.MissionId]);
        await mission.PositionWork;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;

        Assert.Same(mission, _plant[startOn].LoadedLu);
        Assert.Null(mission.StepToDo);
        RoutesToBuild.Instance.Items = new();

        _plant["Q"].RouteQueueingPauseForTesting = () =>
        {
            _plant["Q"].RouteQueueingPauseForTesting = null;

            if (queuedMeanwhile)
            {
                RoutesToBuild.Instance.Enqueue(mission);
                return;
            }

            mission.PositionItemId = _plant["R"].ItemGuid;
            Assert.Null(_plant["Q"].LoadedLu);
        };

        return mission;
    }

    /// <summary>
    /// La cucitura e' scattata (la missione e' su R), e in coda c'e' la missione, una volta sola, e
    /// nessun nullo. Il resto della coda non si guarda: e' un singleton, e il passaggio in Run sveglia
    /// i dispositivi rimasti dalle altre prove, che ci rimettono le loro missioni; un nullo, dopo il
    /// rimedio, non lo accoda piu' nessuno.
    /// </summary>
    private async Task TheQueueHoldsTheMissionOnceAndNoNull(PathMission mission)
    {
        Assert.Null(_plant["Q"].RouteQueueingPauseForTesting);
        Assert.Equal(_plant["R"].ItemGuid, mission.PositionItemId);
        await mission.PositionWork.WaitAsync(Ceiling);

        var queued = RoutesToBuild.Instance.Items.ToArray();
        Assert.DoesNotContain(null, queued);
        Assert.Single(queued, entry => ReferenceEquals(entry, mission));
    }

    /// <summary>
    /// La cucitura e' scattata, e in coda c'e' la missione una volta sola, con il suo indice. Il resto
    /// della coda non si guarda (vedi <see cref="TheQueueHoldsTheMissionOnceAndNoNull"/>).
    /// </summary>
    private void TheQueueHoldsTheMissionOnce(PathMission mission)
    {
        Assert.Null(_plant["Q"].RouteQueueingPauseForTesting);
        Assert.Single(RoutesToBuild.Instance.Items.ToArray(), entry => ReferenceEquals(entry, mission));
        Assert.Same(mission, RoutesToBuild.Instance[mission.MissionId]);
    }
}
