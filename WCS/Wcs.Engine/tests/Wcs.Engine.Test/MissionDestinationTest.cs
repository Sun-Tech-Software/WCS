using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Il setter della destinazione di una missione: il codice porta di deposito, e la rete che
/// impedisce di cambiare destinazione con uno step in volo.
/// </summary>
/// <remarks>
/// <para>
/// Verso un magazzino il codice porta e' la cella scelta dal gestionale e deve sopravvivere; verso
/// un altro dispositivo non polmone si azzera come sempre. Il caso vero: la rilocazione della UDC
/// davanti per estrarre quella dietro, collaudo del 2026-09-04.
/// </para>
/// <para>
/// <b>Questa classe fissa <c>NavStatus</c>, e non e' un dettaglio.</b> Il blocco del setter che i
/// due primi collaudi provano esce subito quando <c>NavStatus</c> e' <c>Recover</c>, che e' il
/// valore che le altre classi lasciano in giro: con quel valore il setter non eseguiva niente,
/// <c>StorageDoorCode</c> restava quello iniziale, <c>BufferCellId</c> restava zero, e tutte e tre
/// le asserzioni passavano lo stesso. Il collaudo verde non provava che il codice porta
/// «sopravvivesse»; provava che nessuno l'avesse toccato.
/// </para>
/// </remarks>
public sealed class MissionDestinationTest : IDisposable
{
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;

    public MissionDestinationTest()
    {
        ActiveMissions.Instance.RemoveAll(_ => true);
        MissionSteps.Instance.RemoveAll(_ => true);
        Navigator.NavStatus = NavigatorStatusTypes.Run;
    }

    public void Dispose()
    {
        ActiveMissions.Instance.RemoveAll(_ => true);
        MissionSteps.Instance.RemoveAll(_ => true);
        Navigator.NavStatus = _previousStatus;
        GC.SuppressFinalize(this);
    }

    private static AutomationDevice Device(DeviceTypes deviceType)
    {
        var device = new AutomationDevice(Guid.NewGuid()) { DeviceType = deviceType };
        PathDevices.Instance.Add(device);
        return device;
    }

    [Fact]
    public void TheStorageDoorCodeSurvivesADestinationTowardsAStorage()
    {
        // Il blocco sotto prova esce subito in Recover: senza questa verifica il resto proverebbe
        // che nessuno ha toccato il codice porta, non che sia sopravvissuto.
        Assert.NotEqual(NavigatorStatusTypes.Recover, Navigator.NavStatus);

        var sourceStorage = Device(DeviceTypes.Storage);
        var destinationStorage = Device(DeviceTypes.Storage);

        // Come nasce una rilocazione in AddMissionToPickUp: prima la cella, poi la destinazione
        var mission = new PathMission { SourceItemId = sourceStorage.ItemGuid, StorageDoorCode = "ST01.0105.0002.E" };

        mission.DestinationItemId = sourceStorage.ItemGuid;
        Assert.Equal("ST01.0105.0002.E", mission.StorageDoorCode);

        // Il secondo cambio e' quello che conta: la destinazione cambia davvero, quindi il blocco
        // gira, e il codice porta deve restare.
        mission.DestinationItemId = destinationStorage.ItemGuid;
        Assert.Equal(destinationStorage.ItemGuid, mission.DestinationItemId);
        Assert.Equal("ST01.0105.0002.E", mission.StorageDoorCode);
        Assert.Equal(0m, mission.BufferCellId);
    }

    [Fact]
    public void TheStorageDoorCodeIsClearedByADestinationTowardsAnyOtherDevice()
    {
        Assert.NotEqual(NavigatorStatusTypes.Recover, Navigator.NavStatus);

        var storage = Device(DeviceTypes.Storage);
        var conveyor = Device(DeviceTypes.Automation);

        var mission = new PathMission { StorageDoorCode = "ST01.0105.0002.E" };
        mission.DestinationItemId = storage.ItemGuid;

        mission.DestinationItemId = conveyor.ItemGuid;

        Assert.Equal(conveyor.ItemGuid, mission.DestinationItemId);
        Assert.Null(mission.StorageDoorCode);
        Assert.Equal(0m, mission.BufferCellId);
    }

    /// <summary>
    /// Con uno step in volo la destinazione non cambia, e la rotta non viene cancellata: rendere
    /// libero a meta' movimento il dispositivo che lo sta eseguendo e' il danno che questa rete
    /// esiste per evitare.
    /// </summary>
    /// <remarks>
    /// La guardia stava <b>dopo</b> <c>RemoveMissionSteps()</c>, che toglie tutti gli step della
    /// missione: quando la destinazione cambiava davvero — cioe' nell'unico caso in cui la rete ha
    /// un senso — a quel punto non c'era piu' niente da trovare e la guardia rispondeva sempre
    /// <c>null</c>. E <c>RemoveMissionSteps</c> chiama <c>SourceItem.RemoveStep</c>, che azzera il
    /// <c>RunningStepId</c> della sorgente: il danno era gia' fatto una riga prima del controllo.
    /// </remarks>
    [Fact]
    public void ADestinationDoesNotChangeWithAStepInFlight()
    {
        var source = Device(DeviceTypes.Automation);
        var firstDestination = Device(DeviceTypes.Automation);
        var otherDestination = Device(DeviceTypes.Automation);

        var mission = new PathMission { PositionItemId = source.ItemGuid };
        ActiveMissions.Instance.Add(mission, buildRoute: false);

        // L'aggiunta alla collezione attiva esce subito se il navigatore non e' in marcia: senza
        // questa verifica il resto proverebbe il contrario di cio' che dice il nome.
        Assert.Same(mission, source.LoadedLu);

        mission.DestinationItemId = firstDestination.ItemGuid;

        var step = new MissionStep
        {
            StepId = Guid.NewGuid(),
            MissionId = mission.MissionId,
            Date = DateTime.Now,
            FromNodeId = source.ItemGuid,
            ToNodeId = firstDestination.ItemGuid
        };

        MissionSteps.Instance.Add(step);

        // Lo step e' in volo: il comando e' partito da questa sorgente e la risposta non e' ancora
        // arrivata.
        source.RunningStepId = step.StepId;
        source.Status = DeviceStatusTypes.Moving;
        Assert.NotNull(mission.StepInFlight());

        mission.DestinationItemId = otherDestination.ItemGuid;

        Assert.Equal(firstDestination.ItemGuid, mission.DestinationItemId);
        Assert.NotEmpty(MissionSteps.Instance.Where(x => x.StepId == step.StepId));
        Assert.Equal(step.StepId, source.RunningStepId);
    }

    /// <summary>
    /// La meta' che deve riuscire: senza questa, un setter che non cambiasse mai la destinazione
    /// passerebbe il collaudo qui sopra. Senza niente in volo, la destinazione cambia e la rotta
    /// vecchia se ne va.
    /// </summary>
    [Fact]
    public void ADestinationChangesWhenNothingIsInFlight()
    {
        var source = Device(DeviceTypes.Automation);
        var firstDestination = Device(DeviceTypes.Automation);
        var otherDestination = Device(DeviceTypes.Automation);

        var mission = new PathMission { PositionItemId = source.ItemGuid };
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        Assert.Same(mission, source.LoadedLu);

        mission.DestinationItemId = firstDestination.ItemGuid;

        MissionSteps.Instance.Add(new MissionStep
        {
            StepId = Guid.NewGuid(),
            MissionId = mission.MissionId,
            Date = DateTime.Now,
            FromNodeId = source.ItemGuid,
            ToNodeId = firstDestination.ItemGuid
        });

        Assert.Null(mission.StepInFlight());

        mission.DestinationItemId = otherDestination.ItemGuid;

        Assert.Equal(otherDestination.ItemGuid, mission.DestinationItemId);
        Assert.Empty(MissionSteps.Instance.Where(x => x.MissionId == mission.MissionId));
    }

    /// <summary>
    /// Riscrivere la destinazione che la missione ha gia' non tocca niente: ne' la rotta, ne' la porta,
    /// ne' la versione della rotta (voce 41). E' quello su cui conta la scelta fra due destinazioni
    /// fisse (<c>Navigator.BuildRouteToFixedDestination</c>: «il setter non fa nulla»); prima il
    /// setter toglieva la rotta anche con lo stesso valore, senza passare dai dispositivi.
    /// </summary>
    [Fact]
    public void WritingTheSameDestinationAgainKeepsTheRoute()
    {
        var source = Device(DeviceTypes.Automation);
        var destination = Device(DeviceTypes.Automation);

        var mission = new PathMission { DestinationItemId = destination.ItemGuid, StorageDoorCode = "ST01.0105.0002.E" };
        var step = new MissionStep
        {
            StepId = Guid.NewGuid(),
            MissionId = mission.MissionId,
            Date = DateTime.Now,
            FromNodeId = source.ItemGuid,
            ToNodeId = destination.ItemGuid
        };
        mission.MissionSteps = [step];
        var version = mission.RouteVersion;

        mission.DestinationItemId = destination.ItemGuid;

        Assert.Equal(destination.ItemGuid, mission.DestinationItemId);
        Assert.Same(step, Assert.Single(MissionSteps.Instance.Where(x => x.MissionId == mission.MissionId)));
        Assert.Equal("ST01.0105.0002.E", mission.StorageDoorCode);
        Assert.Equal(version, mission.RouteVersion);
    }
}
