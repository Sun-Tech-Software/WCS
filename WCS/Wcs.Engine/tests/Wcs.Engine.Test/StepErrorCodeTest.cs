using Wcs.Connection.Interface;
using Wcs.Connection.Interface.CommandData;
using Wcs.Connection.Interface.ConnectionData;
using Wcs.Connection.Interface.Events;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Items.Managers;
using Wcs.Engine.Test.Routing;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Un movimento che il campo chiude con un codice d'errore vero: il motore marca lo step e la
/// missione in errore e libera la destinazione.
/// </summary>
/// <remarks>
/// <c>StepError</c> componeva la descrizione con <c>int.Parse($"D{errorCode}", HexNumber)</c>, la
/// stessa lettura esadecimale riparata nel simulatore: su un codice a nove cifre (75 dei 116 del
/// <c>.resx</c>) andava in <c>OverflowException</c> prima del <c>try</c>, il <c>catch</c> generale
/// di <c>AsiMovementIsCompleted</c> scriveva una riga e basta, e la destinazione restava prenotata
/// per un movimento che non sarebbe mai arrivato. Con la riparazione del simulatore l'evento
/// arriva davvero con quel codice: senza questa, il sintomo si spostava di un livello.
/// </remarks>
public sealed class StepErrorCodeTest : IDisposable
{
    private const int SupercapFault = 208207873;

    private readonly RoutingTestPlant _plant = new(NavigatorStatusTypes.Run);

    public void Dispose()
    {
        _plant.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Il codice a nove cifre: lo step va in errore e la destinazione torna libera.</summary>
    [Fact]
    public async Task ATrueErrorCodeMarksTheStepAndReleasesTheDestination()
    {
        var (source, receiver, step, mission) = APlantWithAMovementInFlight();

        await receiver.ConnectionAsiMovementIsCompleted(new AsiMovementIsCompletedEventArgs(
            receiver.ItemGuid, step.StepId, ResultCodeTypes.Error, SupercapFault, "Supercap fault",
            new AsiMovementIsCompletedData()));

        Assert.Equal(ResultCodeTypes.Error, step.StepStatus);
        Assert.Equal(MissionStatusTypes.Error, mission.Status);
        Assert.Contains($"Code {SupercapFault}", mission.MovingDescription);
        Assert.Equal(Guid.Empty, receiver.WaitingStepId);
        Assert.Equal(DeviceStatusTypes.Warning, receiver.Status);
        Assert.NotEqual(Guid.Empty, source.ItemGuid);
    }

    /// <summary>Il codice piccolo: il numero scritto e' quello vero, non <c>0xD15</c>.</summary>
    [Fact]
    public async Task ASmallErrorCodeIsWrittenAsItself()
    {
        var (_, receiver, step, mission) = APlantWithAMovementInFlight();

        await receiver.ConnectionAsiMovementIsCompleted(new AsiMovementIsCompletedEventArgs(
            receiver.ItemGuid, step.StepId, ResultCodeTypes.Error, 15, "Aisle not enabled",
            new AsiMovementIsCompletedData()));

        Assert.Contains("Code 15 - Aisle not enabled", mission.MovingDescription);
        Assert.DoesNotContain("3349", mission.MovingDescription);
    }

    /// <summary>
    /// Un <c>GetFrom</c> accettato dal campo: il ricevente esegue, la sorgente tiene il gettone, il
    /// ricevente aspetta lo step. E' lo stato in cui arriva un completamento con errore.
    /// </summary>
    private (AutomationDevice Source, AutomationDevice Receiver, MissionStep Step, PathMission Mission) APlantWithAMovementInFlight()
    {
        var source = _plant.Device("S");
        var receiver = _plant.Device("R");
        _plant.Link("S", "R");

        receiver.MotionType = MotionTypes.GetFrom;
        receiver.DeviceConnection = new AcceptingConnection();

        var mission = new PathMission
        {
            PositionItemId = source.ItemGuid,
            DestinationItemId = receiver.ItemGuid,
            LuId = 11
        };
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        Assert.Same(mission, source.LoadedLu);

        var step = _plant.PlannedStep(mission, "S", "R");
        Assert.Same(step, mission.StepToDo);
        source.Status = DeviceStatusTypes.Ready;
        receiver.Status = DeviceStatusTypes.Ready;

        new AutomationDeviceStepExecutor(receiver).StartReceivingLu(source);
        Assert.Equal(step.StepId, receiver.WaitingStepId);

        return (source, receiver, step, mission);
    }

    /// <summary>Il campo che accetta il comando e non risponde da solo: la risposta la da' il collaudo.</summary>
    private sealed class AcceptingConnection : IConnection
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
        public void MoveLuFromAsiToAsi(RunAsi sourceItem, DataAsi actionItemPerformer, DataAsi fromAsi,
            DataAsi toAsi, Guid stepGuid, decimal luId, int luHandlingMode = 0) { }
        public void MoveLuFromAsiToAsi(RunAsi sourceItem, DataAsi actionItemPerformer, Guid groupedMovementId,
            List<MovementLuDataCommand> loadUnitsToMove, int luHandlingMode = 0) { }
        public void OpenConnection() { }
        public void RequestLuDataFromAsi(RunAsi item) { }
        public void SendCommandToAsi(RunAsi destinationItem, int commandCode, List<string?> parameters) { }
        public void SendLuDataToAsi(RunAsi item, LuInfoDataCommand value) { }
        public void ShowMessageToAsi(RunAsi destinationItem, string message) { }
    }
}
