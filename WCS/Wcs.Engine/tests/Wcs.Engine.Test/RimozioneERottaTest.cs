using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Le due regole del 2026-09-03 sulle liste condivise: una missione con uno step in volo non si
/// rimuove e non cambia rotta; e la rimozione toglie la missione dal motore prima di parlare con
/// il mondo esterno.
/// </summary>
/// <remarks>
/// Il navigatore sta in <c>Recover</c>: <c>ORM.PathMission</c> esce prima di scrivere. Invece
/// <c>Navigator.WmsBusinessArea</c> e' <c>null</c> in questi test, e questo e' voluto: una
/// rimozione che ha qualcosa da cancellare sul WMS va in eccezione, ed e' esattamente il caso in
/// cui l'ordine vecchio lasciava la missione «per aria».
/// </remarks>
public class RimozioneERottaTest : IDisposable
{
    private readonly NavigatorStatusTypes _statoPrecedente = Navigator.NavStatus;

    public RimozioneERottaTest()
    {
        Navigator.NavStatus = NavigatorStatusTypes.Recover;
        ActiveMissions.Instance.RemoveAll(_ => true);
    }

    public void Dispose()
    {
        ActiveMissions.Instance.RemoveAll(_ => true);
        Navigator.NavStatus = _statoPrecedente;
        GC.SuppressFinalize(this);
    }

    private static (AutomationDevice sorgente, PathMission missione, MissionStep step) Scenario()
    {
        var sorgente = new AutomationDevice(Guid.NewGuid());
        var destinazione = new AutomationDevice(Guid.NewGuid());
        PathDevices.Instance.Add(sorgente);
        PathDevices.Instance.Add(destinazione);

        var missione = new PathMission { PositionItemId = sorgente.ItemGuid };
        ActiveMissions.Instance.Add(missione, buildRoute: false);

        var step = new MissionStep
        {
            StepId = Guid.NewGuid(),
            MissionId = missione.MissionId,
            FromNodeId = sorgente.ItemGuid,
            ToNodeId = destinazione.ItemGuid
        };
        MissionSteps.Instance.Add(step);

        return (sorgente, missione, step);
    }

    private static void MettiInVolo(AutomationDevice sorgente, MissionStep step)
    {
        sorgente.RunningStepId = step.StepId;
        sorgente.Status = DeviceStatusTypes.Moving;
    }

    // ------------------------------------------------------------------ rimozione

    [Fact]
    public async Task LaRimozioneERifiutataConUnoStepInVolo()
    {
        var (sorgente, missione, step) = Scenario();
        MettiInVolo(sorgente, step);

        Assert.False(await ActiveMissions.Instance.Remove(missione));

        // Niente e' stato toccato: la missione e' ancora attiva, a bordo, e lo step c'e' ancora.
        Assert.True(ActiveMissions.Instance.Contains(missione));
        Assert.Same(missione, sorgente.LoadedLu);
        Assert.Contains(step, MissionSteps.Instance.List);
        Assert.Equal(step.StepId, sorgente.RunningStepId);
    }

    [Theory]
    [InlineData(DeviceStatusTypes.Warning)]
    [InlineData(DeviceStatusTypes.Error)]
    public async Task DaUnElementoInErroreLaMissioneSiTogliEIlDispositivoEScarico(DeviceStatusTypes stato)
    {
        var (sorgente, missione, step) = Scenario();

        // La macchina ha perso il bancale ed e' andata in errore: lo step e' ancora addosso, ma
        // e' concluso. L'operatore elimina il bancale.
        sorgente.RunningStepId = step.StepId;
        sorgente.Status = stato;

        Assert.True(await ActiveMissions.Instance.Remove(missione));

        Assert.False(ActiveMissions.Instance.Contains(missione));
        Assert.Null(sorgente.LoadedLu);
        Assert.DoesNotContain(step, MissionSteps.Instance.List);
    }

    [Fact]
    public async Task LaMissioneSpariscePrimaDiParlareConIlMondoEsterno()
    {
        var (sorgente, missione, _) = Scenario();

        // Un'assegnazione da cancellare sul WMS: in questi test WmsBusinessArea e' null, quindi la
        // cancellazione esterna fallisce. Con l'ordine vecchio la missione restava attiva e senza
        // posizione -- «per aria» -- per sempre.
        missione.GuidAssignmentLu = Guid.NewGuid();

        Assert.True(await ActiveMissions.Instance.Remove(missione));

        Assert.False(ActiveMissions.Instance.Contains(missione));
        Assert.Null(sorgente.LoadedLu);
        Assert.Equal(Guid.Empty, missione.PositionItemId);
    }

    // ------------------------------------------------------------------ rotta

    [Fact]
    public async Task LaRottaNonSiRicostruisceConUnoStepInVolo()
    {
        var (sorgente, missione, step) = Scenario();
        MettiInVolo(sorgente, step);

        await missione.BuildPathToDestination();

        // Lo step in volo e' ancora li', e il dispositivo lo sta ancora eseguendo.
        Assert.Contains(step, MissionSteps.Instance.List);
        Assert.Equal(step.StepId, sorgente.RunningStepId);
    }

    [Fact]
    public void LaDestinazioneNonCambiaConUnoStepInVolo()
    {
        var (sorgente, missione, step) = Scenario();
        MettiInVolo(sorgente, step);

        var altrove = Guid.NewGuid();
        missione.DestinationItemId = altrove;

        Assert.NotEqual(altrove, missione.DestinationItemId);
        Assert.Contains(step, MissionSteps.Instance.List);
        Assert.Equal(step.StepId, sorgente.RunningStepId);
    }

    [Fact]
    public void LaSondaDelPercorsoNonToccaLaMissione()
    {
        var (sorgente, missione, step) = Scenario();
        MettiInVolo(sorgente, step);

        var destinazionePrima = missione.DestinationItemId;

        // Chiede se esista un percorso verso un posto qualunque. Prima scriveva quel posto sulla
        // missione e poi lo ripristinava, cancellando gli step due volte.
        _ = missione.CheckRouteToDestination(Guid.NewGuid());

        Assert.Equal(destinazionePrima, missione.DestinationItemId);
        Assert.Contains(step, MissionSteps.Instance.List);
        Assert.Equal(step.StepId, sorgente.RunningStepId);
    }
}
