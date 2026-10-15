using Log.Interface;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Items.Managers;
using Wcs.Engine.Test.Routing;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Il giro di gruppo che fallisce: il gettone si rimette a posto — quello andava gia' bene — e
/// adesso il guasto lascia anche una riga.
/// </summary>
/// <remarks>
/// <para>
/// Il <c>catch (Exception)</c> di <c>GroupExecuteStep</c> rimetteva a posto
/// <c>GroupExecuteStepRunning</c> e non scriveva niente. Cinque strade entrano in quel <c>try</c> e
/// quattro possono sollevare un guasto del confine; la quinta ha gia' la propria riga, aggiunta in
/// <c>AutomationDeviceStepExecutor</c> con il commento «o il guasto resta muto come quello del
/// 2026-09-09». Il ramo di gruppo e' gia' la variante meno collaudata (voce D123): un gestionale che
/// non risponde fermava il gruppo senza lasciare nessun appiglio.
/// </para>
/// <para>
/// <b>Che cosa e' cambiato con la revisione dell'ondata 1 (K3).</b> La prima versione di questo
/// collaudo asseriva una riga di livello <c>Error</c> che contenesse l'<c>ItemGuid</c> del
/// dispositivo: la riparazione non ne scrive nessuna cosi'. <c>AutomationDevice.LogError</c> scrive
/// a livello <b>Avviso</b> e nomina <c>ASI.SUBITEM.PARTITION</c>, non il guid; il verde arrivava
/// dalla riga di un'altra riparazione (<c>PathMission.PositionItemId</c>), in corsa e letta senza
/// aspettarla. Adesso si asserisce cio' che la riparazione scrive davvero — livello Avviso, il nome
/// del metodo che <c>[CallerMemberName]</c> ci mette dentro, e l'ASI del dispositivo, che qui e'
/// valorizzato apposta — e la si aspetta con <c>log.Wait</c>.
/// </para>
/// <para>
/// <b>E il collaudo e' deterministico.</b> Il setter di <c>GroupExecuteStepRunning</c>, quando torna
/// falso su un dispositivo con il caricamento di gruppo attivo, avvia
/// <c>RestartAfterDelayAsync</c>. Le prove che devono osservare quella ripartenza possono attenderne
/// direttamente il completamento; le altre mettono davanti al ciclo il gettone di annullamento che
/// la partenza controlla, cosi' l'unico giro in scena e' quello del collaudo.
/// </para>
/// <para>
/// <b>E dopo il rilascio.</b> L'attesa si annulla con il gettone di rilascio del dispositivo e
/// <c>StartProcessGroupExecuteStep</c> rifiuta comunque un dispositivo gia' rilasciato. La prova
/// <see cref="ADeviceReleasedDuringTheDelayedRestartDoesNotGetItsLoopBack"/> attende l'operazione
/// stessa, non una finestra di tempo piu' lunga del ritardo produttivo.
/// </para>
/// </remarks>
public sealed class GroupExecuteStepFailureTest : IDisposable
{
    private const decimal CellId = 610;
    private const string Asi = "77";
    private const string SubItem = "3";
    private const string Partition = "9";

    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly FakeWarehouse _warehouse = new();
    private readonly RoutingTestPlant _plant = new(NavigatorStatusTypes.Run);

    public GroupExecuteStepFailureTest()
    {
        Navigator.Warehouse = _warehouse;
    }

    public void Dispose()
    {
        _plant.Dispose();
        Navigator.Warehouse = _previousWarehouse;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// L'inizio esatto della riga che <c>AutomationDevice.LogError</c> scrive dal <c>catch</c> del
    /// giro di gruppo: <c>[ASI a.b.c] Error in {nome del metodo}: </c>, con il nome che ci mette
    /// <c>[CallerMemberName]</c>.
    /// </summary>
    /// <remarks>
    /// Il confronto e' sul <b>prefisso</b> e non su «da qualche parte c'e' scritto
    /// GroupExecuteStep»: la riga porta anche lo stack trace dell'eccezione, e quello nomina
    /// <c>GroupExecuteStep</c> per qualunque guasto sollevato li' sotto — compresi quelli che
    /// registra qualcun altro. Con il confronto largo il collaudo restava verde anche togliendo la
    /// riparazione.
    /// </remarks>
    private const string GroupTurnFailurePrefix =
        $"[ASI {Asi}.{SubItem}.{Partition}] Error in {nameof(AutomationDeviceGroupManager.GroupExecuteStep)}: ";

    /// <summary>La riga che <c>AutomationDevice.LogError</c> scrive dal <c>catch</c> del giro di gruppo.</summary>
    private static bool IsTheGroupTurnFailureLine(string description, LogEntryType level) =>
        level == LogEntryType.Warning && description.StartsWith(GroupTurnFailurePrefix, StringComparison.Ordinal);

    [Fact]
    public async Task AWarehouseThatDoesNotAnswerOnAGroupTurnLeavesALine()
    {
        using var log = new RecordingLogWriter();
        var device = ADeviceLookingForWhereToStore();
        _warehouse.Unavailable = true;

        await device.GroupManager!.GroupExecuteStep();

        await log.Wait(IsTheGroupTurnFailureLine).WaitAsync(TimeSpan.FromSeconds(30));

        // Il gettone tornava gia' a posto: e' la meta' che il catch faceva bene, e resta vera.
        // Nessun ciclo di sfondo puo' averlo ripreso, per la ragione scritta in testa alla classe.
        Assert.False(device.GroupExecuteStepRunning);
    }

    /// <summary>
    /// La meta' che deve riuscire: senza questa, un giro che sollevasse sempre passerebbe il
    /// collaudo qui sopra. Con il gestionale che risponde, il giro non lascia nessuna riga di
    /// guasto.
    /// </summary>
    /// <remarks>
    /// Un'asserzione negativa sul registro non si puo' aspettare, e l'ordine fra due <c>Task.Run</c>
    /// non e' un discriminante. Qui il discriminante e' la <b>console</b>: <c>Navigator.WriteLog</c>
    /// la scrive nel proprio <c>finally</c>, in modo sincrono, dentro la chiamata a
    /// <c>GroupExecuteStep</c> che il collaudo ha gia' atteso. Se la riga fosse nata, sarebbe li'
    /// gia' adesso.
    /// </remarks>
    [Fact]
    public async Task AGroupTurnThatGoesThroughLeavesNoFailureLine()
    {
        var device = ADeviceLookingForWhereToStore();

        using var console = new RecordingConsole();
        await device.GroupManager!.GroupExecuteStep();

        Assert.DoesNotContain(console.Lines, line => line.StartsWith(GroupTurnFailurePrefix, StringComparison.Ordinal));
        Assert.False(device.GroupExecuteStepRunning);
    }

    /// <summary>
    /// Che la console sia davvero il canale che si sta guardando: lo stesso giro, con il gestionale
    /// che non risponde, ce la lascia. Senza questa, una console che non ricevesse niente farebbe
    /// passare il collaudo qui sopra a prescindere.
    /// </summary>
    [Fact]
    public async Task TheSameTurnThatFailsDoesLeaveItsLineOnTheConsole()
    {
        var device = ADeviceLookingForWhereToStore();
        _warehouse.Unavailable = true;

        using var console = new RecordingConsole();
        await device.GroupManager!.GroupExecuteStep();

        Assert.Contains(console.Lines, line => line.StartsWith(GroupTurnFailurePrefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Il giro che finisce programma la ripartenza del ciclo di gruppo. Se durante l'attesa il
    /// dispositivo viene rilasciato, l'operazione deve terminare per cancellazione senza far
    /// rinascere il ciclo su un dispositivo morto.
    /// </summary>
    /// <remarks>
    /// Il test attende l'operazione produttiva di riavvio, non il trascorrere di una finestra
    /// scelta dal test: <c>Dispose</c> annulla il token che deve chiuderla immediatamente.
    /// </remarks>
    [Fact]
    public async Task ADeviceReleasedDuringTheDelayedRestartDoesNotGetItsLoopBack()
    {
        var device = ADeviceLookingForWhereToStore();
        _warehouse.Unavailable = true;

        var manager = device.GroupManager!;
        var delayedRestart = manager.RestartAfterDelayAsync();
        device.Dispose();
        await delayedRestart.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(manager.CancellationGroupExStepTokenSource);
    }

    /// <summary>
    /// Un dispositivo con a bordo una UDC senza destinazione di deposito: e' la strada
    /// <c>VerifyWhereAllocateLuGrouped</c>, una delle quattro che entrano nel <c>try</c> del giro di
    /// gruppo e chiamano il gestionale.
    /// </summary>
    private AutomationDevice ADeviceLookingForWhereToStore()
    {
        var device = _plant.Device("G");
        device.GroupLoadingActive = true;
        device.LookWhereAllocateLu = LookWhereAllocateLuTypes.BuildRouteToStoreByIdLuType;
        device.Status = DeviceStatusTypes.Ready;

        // La terna che AutomationDevice.LogError mette nella riga: senza, il testo sarebbe
        // «[ASI ..]» e non direbbe di quale dispositivo si parla.
        device.ASI = Asi;
        device.SUBITEM = SubItem;
        device.PARTITION = Partition;

        // Il ciclo di sfondo del gruppo non deve nascere: StartProcessGroupExecuteStep esce subito
        // quando il gettone di annullamento c'e' gia'. Senza questo, il setter di
        // GroupExecuteStepRunning ne avvia uno 100 ms dopo la fine del giro, quello contende il
        // gettone che il collaudo guarda, e resta acceso anche dopo. Lo smaltisce il Dispose del
        // dispositivo, che PathDevices.Clear chiama per ognuno; e la ripartenza ritardata che
        // arriva dopo il Dispose non lo fa rinascere, per la ragione scritta in testa alla classe.
        device.GroupManager!.CancellationGroupExStepTokenSource = new CancellationTokenSource();

        // La cella collegata e' cio' che porta la ricerca del posto fino al gestionale: senza,
        // FindRouteAllocateLu esce prima di chiedere qualcosa.
        device.LinkedCellId = CellId;

        // La cella c'e' comunque, anche nella prova in cui il gestionale non risponde: cosi' il
        // rosso non puo' venire da una cella che manca al doppio.
        _warehouse.Cells[CellId] = new CellDescription(CellId, AreaId: 1, WarehouseId: 0, Depth: 1, Inverted: false,
            CellLockState.None, AllowedLoadUnitTypeId: null, GroupId: null, GroupOrder: 0, "P01A",
            DoorCodeB: string.Empty, DoorCount: 1, MultiDepthStorage: false);

        var mission = new PathMission { PositionItemId = device.ItemGuid, LuId = 11, IdLuType = 3 };
        ActiveMissions.Instance.Add(mission, buildRoute: false);

        // L'aggiunta alla collezione attiva esce subito se il navigatore non e' in marcia: senza
        // questa verifica il resto proverebbe il contrario di cio' che dice il nome.
        Assert.Same(mission, device.LoadedLu);
        Assert.Single(device.LUsLoaded);

        return device;
    }
}
