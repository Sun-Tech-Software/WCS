using Wcs.Connection.Interface;
using Wcs.Connection.Interface.CommandData;
using Wcs.Connection.Interface.ConnectionData;
using Wcs.Connection.Interface.Events;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Test.Routing;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Il gestore dell'evento con cui il PLC chiede i dati di un'unita' di carico
/// (<c>Navigator.AsiRequestsLuData</c>): da quando la descrizione arriva dal gestionale attraverso
/// il confine HTTP, quella riga puo' sollevare. Il metodo e' un <c>async void</c> — e' un gestore di
/// evento vero, non ha un chiamante a cui restituire un <see cref="Task"/> — quindi un'eccezione che
/// esce di li' non ha nessun <see cref="Task"/> che la raccolga: viene consegnata al
/// <see cref="SynchronizationContext"/> catturato all'ingresso e, in produzione dove quel contesto
/// non c'e', rilanciata su un thread del pool. Nell'host non e' registrato nessun
/// <c>AppDomain.UnhandledException</c>: il processo termina.
/// </summary>
/// <remarks>
/// Il collaudo intercetta proprio quella consegna: installa un <see cref="SynchronizationContext"/>
/// che esegue in linea cio' che gli viene affidato e annota le eccezioni. E' l'unico osservatorio
/// possibile per un <c>async void</c>, e non discrimina con un'attesa: quando
/// <see cref="Navigator.AsiRequestsLuData"/> ritorna, la consegna e' gia' avvenuta perche' il
/// magazzino finto risponde senza cedere il thread.
/// </remarks>
public sealed class AsiRequestsLuDataTest : IDisposable
{
    private const decimal LoadUnitId = 4242;

    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly RoutingTestPlant _plant = new(NavigatorStatusTypes.Run);
    private readonly FakeWarehouse _warehouse = new();

    public AsiRequestsLuDataTest()
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
    /// Il gestionale che non risponde non deve uscire dal gestore: e' il modo di rompersi che il
    /// confine doveva togliere, non aggiungere. La risposta al PLC non parte — resta il suo timeout,
    /// che e' il comportamento voluto — e la richiesta lascia una riga nel registro.
    /// </summary>
    [Fact]
    public void AWarehouseThatDoesNotAnswerDoesNotEscapeFromTheAsiRequest()
    {
        var (device, connection) = ADeviceAskingForItsLoadUnit();
        _warehouse.Unavailable = true;

        var escaped = RunOnACapturingContext(() =>
            Navigator.AsiRequestsLuData(this, AskFor(device)));

        Assert.Empty(escaped);
        Assert.Empty(connection.SentLuData);
    }

    /// <summary>
    /// La meta' che deve riuscire: senza questa, un gestore che non facesse piu' niente passerebbe
    /// il collaudo qui sopra. Il gestionale risponde e i dati arrivano al PLC.
    /// </summary>
    [Fact]
    public void AWarehouseThatAnswersSendsTheLoadUnitDataToThePlc()
    {
        var (device, connection) = ADeviceAskingForItsLoadUnit();
        _warehouse.LoadUnits[LoadUnitId] = new LoadUnitDescription(LoadUnitId, "UDC-4242", LoadUnitTypeId: 1,
            HandlingMode: 0, Tare: 0, Height: 100, Width: 80, Depth: 120, Weight: 250);

        var escaped = RunOnACapturingContext(() =>
            Navigator.AsiRequestsLuData(this, AskFor(device)));

        Assert.Empty(escaped);
        var sent = Assert.Single(connection.SentLuData);
        Assert.Equal((int)LoadUnitId, sent.LuId);
        Assert.Equal(100, sent.LuHeight);
        Assert.Equal(80, sent.LuLength);
        Assert.Equal(120, sent.LuWidth);
    }

    private (AutomationDevice Device, RecordingConnection Connection) ADeviceAskingForItsLoadUnit()
    {
        var device = _plant.Device("A");
        device.ASI = "0001";
        device.SUBITEM = "0002";
        device.PARTITION = "0003";

        var connection = new RecordingConnection();
        device.DeviceConnection = connection;

        var mission = new PathMission { PositionItemId = device.ItemGuid, LuId = LoadUnitId, LuCode = "UDC-4242" };
        ActiveMissions.Instance.Add(mission, buildRoute: false);

        // Senza questa verifica il resto proverebbe il contrario di cio' che dice il nome: se la
        // missione non fosse a bordo, il gestore uscirebbe alla guardia «dispositivo senza UDC».
        Assert.Same(mission, device.LoadedLu);

        return (device, connection);
    }

    private static AsiRequestsLuInfoEventArgs AskFor(AutomationDevice device) =>
        new(new AsiRequestsLuInfoData
        {
            Asi = device.ASI,
            Subitem = device.SUBITEM,
            Partition = device.PARTITION,
            RequestId = 7
        });

    /// <summary>
    /// Esegue <paramref name="action"/> con un contesto di sincronizzazione che annota le eccezioni
    /// affidategli invece di rilanciarle: e' li' che finisce cio' che esce da un <c>async void</c>.
    /// </summary>
    private static List<Exception> RunOnACapturingContext(Action action)
    {
        var previous = SynchronizationContext.Current;
        var context = new CapturingSynchronizationContext();

        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            action();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        return context.Escaped;
    }

    private sealed class CapturingSynchronizationContext : SynchronizationContext
    {
        public List<Exception> Escaped { get; } = [];

        public override void Post(SendOrPostCallback d, object? state) => Run(d, state);

        public override void Send(SendOrPostCallback d, object? state) => Run(d, state);

        private void Run(SendOrPostCallback d, object? state)
        {
            try
            {
                d(state);
            }
            catch (Exception ex)
            {
                Escaped.Add(ex);
            }
        }
    }

    /// <summary>Il campo nei test: non parla con nessun PLC, tiene i dati che gli sono stati mandati.</summary>
    private sealed class RecordingConnection : IConnection
    {
        public List<LuInfoDataCommand> SentLuData { get; } = [];

        public decimal Id => 1;
        public bool Managed { get; set; }

        // Il motore non li solleva in questo test: servono a soddisfare l'interfaccia.
#pragma warning disable CS0067
        public event Handlers.AsiEventEventHandler? AsiEvent;
        public event Handlers.AsiMovementIsCompletedEventHandler? AsiMovementIsCompleted;
        public event Handlers.AsiRequestsLuInfoEventHandler? AsiRequestsLuInfo;
        public event Handlers.AsiSendLuDataEventHandler? AsiSendLuData;
        public event Handlers.LuUnloadedFromAsiEventHandler? LuUnloadedFromAsi;
        public event Handlers.NewLuOnAsiEventHandler? NewLuOnAsi;
#pragma warning restore CS0067

        public void CloseConnection() { }
        public void Dispose() { }

        public void MoveLuFromAsiToAsi(RunAsi sourceItem, DataAsi actionItemPerformer, DataAsi fromAsi, DataAsi toAsi,
            Guid stepGuid, decimal luId, int luHandlingMode = 0) { }

        public void MoveLuFromAsiToAsi(RunAsi sourceItem, DataAsi actionItemPerformer, Guid groupedMovementId,
            List<MovementLuDataCommand> loadUnitsToMove, int luHandlingMode = 0) { }

        public void OpenConnection() { }
        public void RequestLuDataFromAsi(RunAsi item) { }
        public void SendCommandToAsi(RunAsi destinationItem, int commandCode, List<string?> parameters) { }
        public void SendLuDataToAsi(RunAsi item, LuInfoDataCommand value) => SentLuData.Add(value);
        public void ShowMessageToAsi(RunAsi destinationItem, string message) { }
    }
}
