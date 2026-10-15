using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// La definizione di «step in volo»: mandato al campo, senza risposta ancora.
/// </summary>
/// <remarks>
/// E' la definizione da cui dipendono due rifiuti — quello della rimozione di una missione e
/// quello della ricostruzione della rotta — quindi va tenuta ferma da sola. Il punto delicato e'
/// <c>Warning</c>: un dispositivo che ha fallito uno step conserva <c>RunningStepId</c> di proposito
/// (il ritentativo di gruppo lo usa), ma non ha niente in volo, e l'operatore deve poterlo
/// svuotare.
/// </remarks>
public class StepInVoloTest : IDisposable
{
    private readonly NavigatorStatusTypes _statoPrecedente = Navigator.NavStatus;

    public StepInVoloTest()
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

    /// <summary>
    /// Un dispositivo registrato nel grafo, con una missione attiva a bordo e uno step di quella
    /// missione che parte da lui.
    /// </summary>
    private static (AutomationDevice sorgente, PathMission missione, MissionStep step) Scenario()
    {
        var sorgente = new AutomationDevice(Guid.NewGuid());
        var destinazione = new AutomationDevice(Guid.NewGuid());

        // Gli step risolvono SourceItem attraverso PathDevices: senza registrare il dispositivo
        // lo step non avrebbe sorgente e non risulterebbe mai in volo.
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

    [Fact]
    public void SenzaStepInCorsoNienteEInVolo()
    {
        var (_, missione, step) = Scenario();

        Assert.Null(missione.StepInFlight());

        // Lo step risale alla propria missione per chiave, non piu' con una scansione: stesso
        // oggetto, in tempo costante.
        Assert.Same(missione, step.PathMission);
    }

    [Fact]
    public void LoStepCheIlDispositivoStaEseguendoEInVolo()
    {
        var (sorgente, missione, step) = Scenario();

        sorgente.RunningStepId = step.StepId;
        sorgente.Status = DeviceStatusTypes.Moving;

        Assert.True(sorgente.IsStepInFlight(step.StepId));
        Assert.Same(step, missione.StepInFlight());
    }

    [Theory]
    [InlineData(DeviceStatusTypes.Warning)]
    [InlineData(DeviceStatusTypes.Error)]
    public void UnDispositivoInErroreNonHaNienteInVolo(DeviceStatusTypes stato)
    {
        var (sorgente, missione, step) = Scenario();

        // Com'e' dopo uno StepError: lo step e' ancora addosso al dispositivo, ma e' concluso male,
        // e il campo non sta facendo niente. E' il caso in cui l'operatore deve poter togliere il
        // bancale.
        sorgente.RunningStepId = step.StepId;
        sorgente.Status = stato;

        Assert.False(sorgente.IsStepInFlight(step.StepId));
        Assert.Null(missione.StepInFlight());
        Assert.False(sorgente.IsBusyWithCommand);
    }

    [Fact]
    public void UnAltroStepNonRisultaInVolo()
    {
        var (sorgente, _, step) = Scenario();

        sorgente.RunningStepId = step.StepId;
        sorgente.Status = DeviceStatusTypes.Moving;

        Assert.False(sorgente.IsStepInFlight(Guid.NewGuid()));
        Assert.False(sorgente.IsStepInFlight(Guid.Empty));
    }
}
