using Wcs.Connection.Interface;
using Wcs.Connection.Interface.CommandData;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Items.Managers;
using Wcs.Engine.Test.Routing;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Il trasferimento <c>GetFrom</c> che non parte: chi tiene il gettone e la prenotazione li deve
/// riavere indietro.
/// </summary>
/// <remarks>
/// <para>
/// In un <c>GetFrom</c> il dispositivo che esegue e' il <b>ricevente</b>, ma il gettone
/// (<c>RunningStepId</c>) e lo stato di lavorazione (<c>Moving</c>) stanno sulla <b>sorgente</b>:
/// li scrivono <c>ConfigureGetFromStep</c> e <c>ExecuteGetFromMovement</c>. Il gestore dell'errore
/// normalizzava invece il ricevente — un'operazione nulla, perche' su di lui il gettone non e' mai
/// stato scritto — e la sorgente restava <c>Moving</c> con l'identificativo di passo addosso.
/// </para>
/// <para>
/// Da quella terna non si esce da soli: <c>TryReleaseHungRunningStep</c> pretende <c>Ready</c> e
/// che nessun comando sia partito da quel dispositivo per quello step, e qui falliscono tutt'e due
/// le condizioni; <c>Moving</c> e' fra gli stati che <c>IsBusyWithCommand</c> considera impegnati,
/// quindi ogni esecuzione seguente viene rifiutata; e il <c>ResetError</c> dell'operatore vuole
/// <c>Status == Error</c>. Nessun timeout del PLC interviene, perche' il comando non e' partito.
/// </para>
/// </remarks>
public sealed class GetFromFailureTest : IDisposable
{
    private readonly RoutingTestPlant _plant = new(NavigatorStatusTypes.Run);

    public void Dispose()
    {
        _plant.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Il comando al campo solleva: la sorgente torna disponibile — gettone restituito e stato
    /// <c>Ready</c> — e la destinazione riavere il proprio posto.
    /// </summary>
    [Fact]
    public void AGetFromThatDoesNotStartGivesTheSourceItsTokenBack()
    {
        var (source, receiver, step) = APlantAboutToGetFrom(new FailingConnection());

        new AutomationDeviceStepExecutor(receiver).StartReceivingLu(source);

        Assert.Equal(Guid.Empty, source.RunningStepId);
        Assert.Equal(DeviceStatusTypes.Ready, source.Status);
        Assert.Equal(Guid.Empty, receiver.WaitingStepId);
        Assert.Equal(DeviceStatusTypes.Ready, receiver.Status);
        Assert.NotEqual(Guid.Empty, step.StepId);
    }

    /// <summary>
    /// La meta' che deve riuscire: senza questa, un <c>GetFrom</c> che non partisse mai passerebbe
    /// il collaudo qui sopra. Il comando arriva al campo, e da quel momento gettone e prenotazione
    /// <b>devono</b> restare presi: c'e' un movimento in volo che li giustifica.
    /// </summary>
    [Fact]
    public void AGetFromThatStartsKeepsTheTokenAndTheReservation()
    {
        var connection = new RecordingConnection();
        var (source, receiver, step) = APlantAboutToGetFrom(connection);

        new AutomationDeviceStepExecutor(receiver).StartReceivingLu(source);

        Assert.Single(connection.Movements);
        Assert.Equal(step.StepId, source.RunningStepId);
        Assert.Equal(step.StepId, receiver.WaitingStepId);
        Assert.Equal(source.ItemGuid, step.RunningNode);
    }

    /// <summary>
    /// Un impianto con una sorgente che porta una UDC diretta al ricevente, e lo step gia'
    /// pianificato fra i due.
    /// </summary>
    private (AutomationDevice Source, AutomationDevice Receiver, MissionStep Step) APlantAboutToGetFrom(
        IConnection connection)
    {
        var source = _plant.Device("S");
        var receiver = _plant.Device("R");
        _plant.Link("S", "R");

        receiver.MotionType = MotionTypes.GetFrom;
        receiver.DeviceConnection = connection;

        var mission = new PathMission
        {
            PositionItemId = source.ItemGuid,
            DestinationItemId = receiver.ItemGuid,
            LuId = 11
        };

        ActiveMissions.Instance.Add(mission, buildRoute: false);

        // L'aggiunta alla collezione attiva esce subito se il navigatore non e' in marcia: senza
        // questa verifica il resto proverebbe il contrario di cio' che dice il nome.
        Assert.Same(mission, source.LoadedLu);

        var step = _plant.PlannedStep(mission, "S", "R");

        Assert.Same(step, mission.StepToDo);
        source.Status = DeviceStatusTypes.Ready;
        receiver.Status = DeviceStatusTypes.Ready;

        return (source, receiver, step);
    }

    /// <summary>Il campo che rifiuta il comando: e' il guasto che il collaudo mette in scena.</summary>
    private sealed class FailingConnection : ConnectionDouble
    {
        public override void MoveLuFromAsiToAsi(RunAsi sourceItem, DataAsi actionItemPerformer, DataAsi fromAsi,
            DataAsi toAsi, Guid stepGuid, decimal luId, int luHandlingMode = 0) =>
            throw new InvalidOperationException("il PLC non ha accettato il comando");
    }

    /// <summary>Il campo che accetta: tiene i comandi ricevuti.</summary>
    private sealed class RecordingConnection : ConnectionDouble
    {
        public List<Guid> Movements { get; } = [];

        public override void MoveLuFromAsiToAsi(RunAsi sourceItem, DataAsi actionItemPerformer, DataAsi fromAsi,
            DataAsi toAsi, Guid stepGuid, decimal luId, int luHandlingMode = 0) => Movements.Add(stepGuid);
    }

    /// <summary>La parte dell'interfaccia che questi collaudi non usano.</summary>
    private abstract class ConnectionDouble : IConnection
    {
        public decimal Id => 1;
        public bool Managed { get; set; }

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

        public abstract void MoveLuFromAsiToAsi(RunAsi sourceItem, DataAsi actionItemPerformer, DataAsi fromAsi,
            DataAsi toAsi, Guid stepGuid, decimal luId, int luHandlingMode = 0);

        public void MoveLuFromAsiToAsi(RunAsi sourceItem, DataAsi actionItemPerformer, Guid groupedMovementId,
            List<MovementLuDataCommand> loadUnitsToMove, int luHandlingMode = 0) { }

        public void OpenConnection() { }
        public void RequestLuDataFromAsi(RunAsi item) { }
        public void SendCommandToAsi(RunAsi destinationItem, int commandCode, List<string?> parameters) { }
        public void SendLuDataToAsi(RunAsi item, LuInfoDataCommand value) { }
        public void ShowMessageToAsi(RunAsi destinationItem, string message) { }
    }
}
