using System.Diagnostics;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// L'attesa fra uno step singolo e il successivo non tiene fermo il thread che la chiede.
/// </summary>
/// <remarks>
/// Il thread e' del pool, lo stesso che porta avanti i cicli del motore e quelli di fondo di ogni
/// magazzino: un dispositivo che dorme con <c>Thread.Sleep</c> ferma il lavoro di dispositivi che
/// non c'entrano nulla, e con molti movimenti insieme il pool resta senza thread liberi. La prova
/// e' che <c>ExecuteSingleMOVE_LU_TO</c> restituisce il controllo <b>prima</b> che l'attesa sia
/// passata: il <c>Task</c> non e' concluso al ritorno, e il chiamante non e' rimasto fermo. Voce
/// D72 del registro.
/// </remarks>
public class AttesaNonBloccanteTest : IDisposable
{
    private readonly NavigatorStatusTypes _statoPrecedente = Navigator.NavStatus;

    public AttesaNonBloccanteTest()
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
    /// Un gruppo con uno step singolo pronto a partire: la sorgente ha la UDC a bordo ed e'
    /// libera, la destinazione e' vuota e libera.
    /// </summary>
    private static (AutomationDevice gruppo, AutomationDevice sorgente, AutomationDevice destinazione, MissionStep step)
        Scenario()
    {
        var gruppo = new AutomationDevice(Guid.NewGuid());
        var sorgente = new AutomationDevice(Guid.NewGuid()) { Status = DeviceStatusTypes.Ready };
        var destinazione = new AutomationDevice(Guid.NewGuid()) { Status = DeviceStatusTypes.Ready };

        PathDevices.Instance.Add(gruppo);
        PathDevices.Instance.Add(sorgente);
        PathDevices.Instance.Add(destinazione);

        // La missione e' a bordo della sorgente: e' cosi' che la sorgente risulta pronta a
        // trasferire, e che StartPutToMOVE_LU trova lo step da fare.
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

        return (gruppo, sorgente, destinazione, step);
    }

    [Fact]
    public async Task IlChiamanteRiprendeIlControlloPrimaCheLAttesaSiaPassata()
    {
        var (gruppo, sorgente, destinazione, step) = Scenario();

        var orologio = Stopwatch.StartNew();
        var esecuzione = gruppo.StepExecutor!.ExecuteSingleMOVE_LU_TO([step]);
        var trascorso = orologio.ElapsedMilliseconds;

        // Il comando e' partito (lo stato e' gia' quello di un movimento in corso) ma l'attesa che
        // segue non e' ancora finita: il thread e' tornato al chiamante.
        Assert.False(esecuzione.IsCompleted);
        Assert.True(trascorso < 100, $"il chiamante e' rimasto fermo per {trascorso} ms");

        await esecuzione;

        Assert.Equal(DeviceStatusTypes.Moving, sorgente.Status);
        Assert.Equal(DeviceStatusTypes.Busy, destinazione.Status);
        Assert.Equal(step.StepId, sorgente.RunningStepId);
        Assert.Equal(gruppo.RunningStepId, step.GroupedMovementId);
    }

    [Fact]
    public async Task SenzaStepDaFareChiudeSubitoELasciaIlGruppoLibero()
    {
        var gruppo = new AutomationDevice(Guid.NewGuid()) { GroupExecuteStepRunning = true };

        var esecuzione = gruppo.StepExecutor!.ExecuteSingleMOVE_LU_TO([]);

        Assert.True(esecuzione.IsCompleted);
        await esecuzione;
        Assert.False(gruppo.GroupExecuteStepRunning);
    }
}
