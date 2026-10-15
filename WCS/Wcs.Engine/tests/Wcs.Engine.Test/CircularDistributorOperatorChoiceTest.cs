using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Test.Routing;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Il distributore circolare rispetta la destinazione scelta dall'operatore (voce 40, decisione del
/// proprietario del 10/10): un'UDC con la scelta esce da un'uscita che porta alla destinazione
/// scelta, e destinazione e segno restano; le UDC senza scelta escono dalla prima uscita libera in
/// ordine di <c>PriorityInRoutePath</c>, come prima; se nessuna uscita ci porta, la missione
/// aspetta con il freno del navigatore, tiene la scelta, e una riga di avviso lo dice al primo
/// fallimento e alle soglie del freno.
/// </summary>
/// <remarks>
/// <para>
/// Il distributore D ha tre uscite, E1, E2 ed E3, con priorita' 1, 2 e 3; da E1 ed E2 si va a F, da
/// E3 a X. Il navigatore sta in <c>Stop</c>: la missione si aggiunge in <c>Recover</c>, perche'
/// <c>ActiveMissions.Add</c> esce subito in <c>Stop</c>, poi si passa a <c>Stop</c>. La prova
/// chiama la decisione del distributore (<c>ProcessCircularDistributionLu</c>) una volta per giro,
/// al posto del ciclo da un secondo, che non gira.
/// </para>
/// <para>
/// Il freno ha un orologio finto, come in <see cref="RouteLoopBrakeTest"/>. Le righe si contano
/// sulla console, che <c>Navigator.WriteLog</c> scrive in modo sincrono; con la diagnostica spenta
/// ci arrivano solo avvisi ed errori, quindi le righe contate sono gli avvisi.
/// </para>
/// </remarks>
public sealed class CircularDistributorOperatorChoiceTest : IDisposable
{
    private readonly RetryBackoff _previousRetries = Navigator.RoutePathRetries;
    private readonly bool _previousDebugLog = Navigator.ActivateDebugLog;
    private readonly RoutingTestPlant _plant = new();
    private readonly ManualClock _clock = new();

    public CircularDistributorOperatorChoiceTest()
    {
        Navigator.RoutePathRetries = new RetryBackoff(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5), _clock);
        Navigator.ActivateDebugLog = false;

        _plant.Device("D", DeviceTypes.CircularDistributor);
        _plant.Device("E1").PriorityInRoutePath = 1;
        _plant.Device("E2").PriorityInRoutePath = 2;
        _plant.Device("E3").PriorityInRoutePath = 3;
        _plant.Device("F");
        _plant.Device("X");

        _plant.Link("D", "E1");
        _plant.Link("D", "E2");
        _plant.Link("D", "E3");
        _plant.Link("E1", "F");
        _plant.Link("E2", "F");
        _plant.Link("E3", "X");
    }

    public void Dispose()
    {
        _plant.Dispose();
        Navigator.ActivateDebugLog = _previousDebugLog;
        Navigator.RoutePathRetries = _previousRetries;
    }

    /// <summary>
    /// La scelta (X) si raggiunge solo dalla terza uscita, e le tre sono libere: l'UDC esce da E3, la
    /// destinazione resta X con il segno, e la rotta e' D-E3-X. Prima usciva da E1 e la destinazione
    /// diventava E1, con la scelta caduta.
    /// </summary>
    [Fact]
    public void TheLoadUnitWithTheOperatorsChoiceLeavesThroughTheExitThatLeadsThere()
    {
        var mission = AMissionOnTheDistributorChosenFor("X");

        Assert.True(_plant["D"].StepExecutor!.ProcessCircularDistributionLu());

        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Equal(["D", "E3", "X"], RouteOf(mission));
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// La stessa scena con un'UDC senza scelta: esce dalla prima uscita libera in ordine di
    /// priorita', E1, che diventa la sua destinazione, come prima della voce 40.
    /// </summary>
    [Fact]
    public void TheLoadUnitWithoutAChoiceStillLeavesThroughTheFirstFreeExit()
    {
        var mission = _plant.MissionAt("D", null);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        mission.DestinationItemId = _plant["X"].ItemGuid;

        Assert.True(_plant["D"].StepExecutor!.ProcessCircularDistributionLu());

        Assert.Equal(_plant["E1"].ItemGuid, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
        Assert.Equal(["D", "E1"], RouteOf(mission));
    }

    /// <summary>
    /// L'unica uscita che porta alla scelta e' occupata: l'UDC aspetta, senza prendere E1 o E2 e
    /// senza avviso, come aspetta oggi un distributore con le uscite occupate.
    /// </summary>
    [Fact]
    public void WhenTheExitThatLeadsThereIsBusyTheLoadUnitWaitsWithoutTakingAnother()
    {
        var mission = AMissionOnTheDistributorChosenFor("X");
        _plant["E3"].Status = DeviceStatusTypes.Busy;
        using var console = new RecordingConsole();

        Assert.False(_plant["D"].StepExecutor!.ProcessCircularDistributionLu());

        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Empty(mission.MissionSteps);
        Assert.DoesNotContain(console.Lines, line => line.Contains($"Missione {mission.MissionId}"));
    }

    /// <summary>
    /// Nessuna uscita porta alla scelta (Y non e' collegato a niente): la missione aspetta, la
    /// destinazione e il segno restano, e la riga di avviso esce una volta sola - non a ogni giro del
    /// distributore, che col freno chiuso non ritenta, e non ai fallimenti dal secondo al nono - e poi
    /// di nuovo al decimo, la soglia del freno.
    /// </summary>
    [Fact]
    public void WhenNoExitLeadsThereTheMissionWaitsWithTheBrakeAndKeepsTheChoice()
    {
        _plant.Device("Y");
        var mission = AMissionOnTheDistributorChosenFor("Y");
        var distributor = _plant["D"].StepExecutor!;
        var warning = $"Missione {mission.MissionId}: nessuna uscita del distributore circolare";
        using var console = new RecordingConsole();

        Assert.False(distributor.ProcessCircularDistributionLu());
        Assert.False(distributor.ProcessCircularDistributionLu());
        Assert.False(distributor.ProcessCircularDistributionLu());

        Assert.Equal(_plant["Y"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Empty(mission.MissionSteps);
        Assert.Equal(1, Navigator.RoutePathRetries.Failures(mission.MissionId));
        Assert.Single(console.Lines, line => line.Contains(warning) && line.Contains("(fallimento 1)"));

        for (var failure = 2; failure <= 9; failure++)
        {
            _clock.Advance(TimeSpan.FromSeconds(5));
            Assert.False(distributor.ProcessCircularDistributionLu());
        }

        Assert.Equal(9, Navigator.RoutePathRetries.Failures(mission.MissionId));
        Assert.Single(console.Lines, line => line.Contains(warning));

        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(distributor.ProcessCircularDistributionLu());

        Assert.Equal(2, console.Lines.Count(line => line.Contains(warning)));
        Assert.Single(console.Lines, line => line.Contains(warning) && line.Contains("(fallimento 10)"));
        Assert.Equal(_plant["Y"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
    }

    /// <summary>
    /// L'unica uscita che porta alla scelta (E3 verso X) e' in errore: conta come un'uscita che non
    /// ci porta (decisione del proprietario del 10/10, C2/C12), quindi la missione aspetta con il
    /// freno, tiene la scelta, e la riga di avviso esce una volta sola. Quando E3 torna Ready e il
    /// freno lascia passare, l'UDC esce da E3 da sola e il freno dimentica il fallimento. Prima di
    /// questa decisione l'uscita guasta contava come «ci porta ma occupata»: attesa senza freno e
    /// senza riga, all'infinito.
    /// </summary>
    [Fact]
    public void WhenTheOnlyExitThatLeadsThereIsInErrorTheMissionWaitsWithTheBrakeAndLeavesWhenItIsReady()
    {
        var mission = AMissionOnTheDistributorChosenFor("X");
        var distributor = _plant["D"].StepExecutor!;
        var warning = $"Missione {mission.MissionId}: nessuna uscita del distributore circolare";
        _plant["E3"].Status = DeviceStatusTypes.Error;
        using var console = new RecordingConsole();

        Assert.False(distributor.ProcessCircularDistributionLu());
        Assert.False(distributor.ProcessCircularDistributionLu());

        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Empty(mission.MissionSteps);
        Assert.Equal(1, Navigator.RoutePathRetries.Failures(mission.MissionId));
        Assert.Single(console.Lines, line => line.Contains(warning));
        Assert.Single(console.Lines, line => line.Contains(warning) && line.Contains("(fallimento 1)"));

        _plant["E3"].Status = DeviceStatusTypes.Ready;
        _clock.Advance(TimeSpan.FromSeconds(5));

        Assert.True(distributor.ProcessCircularDistributionLu());

        Assert.Equal(["D", "E3", "X"], RouteOf(mission));
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Equal(0, Navigator.RoutePathRetries.Failures(mission.MissionId));
        Assert.Single(console.Lines, line => line.Contains(warning));
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Un gruppo scelto: si raggiunge da un'uscita che porta a uno dei suoi dispositivi. Il gruppo G
    /// ha il membro M, e M si raggiunge solo da E2: l'UDC esce da E2, e la destinazione resta G.
    /// </summary>
    [Fact]
    public void AChosenGroupIsReachedThroughTheExitThatLeadsToOneOfItsMembers()
    {
        _plant.Device("G", DeviceTypes.GroupOfAutomationDevices).GroupItemId = 7;
        _plant.Device("M").BelongingGroupId = 7;
        _plant.Link("E2", "M");
        var mission = AMissionOnTheDistributorChosenFor("G");

        Assert.True(_plant["D"].StepExecutor!.ProcessCircularDistributionLu());

        Assert.Equal(_plant["G"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Equal(["D", "E2", "M"], RouteOf(mission));
    }

    /// <summary>
    /// Un magazzino scelto: S si raggiunge da E2 attraverso il suo ricollocatore R. L'UDC esce da E2
    /// e la rotta arriva a S.
    /// </summary>
    [Fact]
    public void AChosenStorageIsReachedThroughTheExitThatLeadsToIt()
    {
        _plant.Device("R");
        _plant.Device("S", DeviceTypes.Storage);
        _plant.Link("E2", "R");
        _plant.Link("R", "S");
        var mission = AMissionOnTheDistributorChosenFor("S");

        Assert.True(_plant["D"].StepExecutor!.ProcessCircularDistributionLu());

        Assert.Equal(_plant["S"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Equal(["D", "E2", "R", "S"], RouteOf(mission));
    }

    /// <summary>
    /// Un'uscita che porta alla scelta solo tornando sul distributore non ci porta: E1 torna su D, e
    /// da D si arriva a X per E3. Con E3 occupata l'UDC aspetta invece di uscire da E1.
    /// </summary>
    [Fact]
    public void AnExitThatLeadsThereOnlyBackThroughTheDistributorDoesNotCount()
    {
        _plant.Link("E1", "D");
        var mission = AMissionOnTheDistributorChosenFor("X");
        _plant["E3"].Status = DeviceStatusTypes.Busy;

        Assert.False(_plant["D"].StepExecutor!.ProcessCircularDistributionLu());

        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.Empty(mission.MissionSteps);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Con un cambio di destinazione dell'operatore in corso il distributore non scrive la rotta,
    /// anche se E3, che porta alla scelta, e' libera: il commit del cambio scrive la sua, e una rotta
    /// verso la destinazione vecchia scritta intanto la coprirebbe o ci si mescolerebbe. Si riprova al
    /// giro dopo; nessun fallimento si conta e nessun avvio resta contato.
    /// </summary>
    [Fact]
    public void AnOperatorChangeInProgressHoldsBackTheExit()
    {
        var mission = AMissionOnTheDistributorChosenFor("X");
        Assert.Equal(PathMission.OperatorChangeStart.Begun, mission.TryBeginOperatorChange());

        try
        {
            Assert.False(_plant["D"].StepExecutor!.ProcessCircularDistributionLu());
        }
        finally
        {
            mission.EndOperatorChange();
        }

        Assert.Empty(mission.MissionSteps);
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Equal(0, Navigator.RoutePathRetries.Failures(mission.MissionId));
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Lo stesso per un'UDC ancora senza scelta, che l'operatore sta mandando altrove (voce 41, compito
    /// 5): con il cambio in corso il distributore non le scrive la prima uscita libera come destinazione,
    /// ne' una rotta, e si riprova al giro dopo. Il commit del cambio non prende piu' il semaforo del
    /// distributore; senza questa guardia, scritto in mezzo al giro del distributore, gli faceva cadere
    /// la scelta appena fatta, e l'UDC usciva da E1 con la destinazione E1.
    /// </summary>
    [Fact]
    public void AnOperatorChangeInProgressHoldsBackTheExitOfALoadUnitWithoutAChoice()
    {
        var mission = _plant.MissionAt("D", null);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        mission.DestinationItemId = _plant["X"].ItemGuid;
        Assert.Equal(PathMission.OperatorChangeStart.Begun, mission.TryBeginOperatorChange());

        try
        {
            Assert.False(_plant["D"].StepExecutor!.ProcessCircularDistributionLu());
        }
        finally
        {
            mission.EndOperatorChange();
        }

        Assert.Empty(mission.MissionSteps);
        Assert.Equal(_plant["X"].ItemGuid, mission.DestinationItemId);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(mission);

        // Finito il cambio, il giro dopo fa quello che faceva: la prima uscita libera.
        Assert.True(_plant["D"].StepExecutor!.ProcessCircularDistributionLu());
        Assert.Equal(_plant["E1"].ItemGuid, mission.DestinationItemId);
        Assert.Equal(["D", "E1"], RouteOf(mission));
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Mentre il distributore scrive la prima uscita libera a un'UDC senza scelta, il cambio
    /// dell'operatore non comincia: la scrittura e' un avvio contato sulla missione (voce 41, compito
    /// 5). La prova chiede il cambio nel momento in cui la destinazione E1 si scrive.
    /// </summary>
    [Fact]
    public void WhileTheExitIsWrittenAnOperatorChangeDoesNotBegin()
    {
        var mission = _plant.MissionAt("D", null);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        mission.DestinationItemId = _plant["X"].ItemGuid;
        PathMission.OperatorChangeStart? whileWriting = null;
        mission.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(PathMission.DestinationItemId) || whileWriting != null) return;

            whileWriting = mission.TryBeginOperatorChange();
            if (whileWriting == PathMission.OperatorChangeStart.Begun) mission.EndOperatorChange();
        };

        Assert.True(_plant["D"].StepExecutor!.ProcessCircularDistributionLu());

        Assert.Equal(PathMission.OperatorChangeStart.StepStarting, whileWriting);
        Assert.Equal(_plant["E1"].ItemGuid, mission.DestinationItemId);
        ChangeDestinationStepStartsTest.NoStepStartIsLeftCounted(mission);
    }

    /// <summary>
    /// Due giri senza un'uscita che porti alla scelta (Y non e' collegato) contano due fallimenti;
    /// poi E2 porta a Y, il freno lascia passare, e l'UDC esce da E2: la rotta e' scritta e il freno
    /// ha dimenticato i fallimenti, quindi il prossimo conta da uno.
    /// </summary>
    [Fact]
    public void WhenAnExitLeadsThereAgainTheRouteIsWrittenAndTheFailuresAreForgotten()
    {
        _plant.Device("Y");
        var mission = AMissionOnTheDistributorChosenFor("Y");
        var distributor = _plant["D"].StepExecutor!;

        Assert.False(distributor.ProcessCircularDistributionLu());
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(distributor.ProcessCircularDistributionLu());
        Assert.Equal(2, Navigator.RoutePathRetries.Failures(mission.MissionId));

        _plant.Link("E2", "Y");
        _clock.Advance(TimeSpan.FromSeconds(5));

        Assert.True(distributor.ProcessCircularDistributionLu());

        Assert.Equal(["D", "E2", "Y"], RouteOf(mission));
        Assert.Equal(0, Navigator.RoutePathRetries.Failures(mission.MissionId));
        Assert.False(Navigator.RoutePathRetries.IsWaiting(mission.MissionId));
    }

    /// <summary>
    /// I nomi della rotta della missione, dal distributore in avanti: gli step stanno nella collezione
    /// comune senza un ordine, e qui si concatenano seguendo le partenze. Uno step che non si aggancia
    /// resta fuori, e il confronto con l'atteso lo fa vedere.
    /// </summary>
    private string[] RouteOf(PathMission mission)
    {
        var steps = mission.MissionSteps;
        var names = new List<string>();
        Guid? at = _plant["D"].ItemGuid;
        names.Add(_plant.NameOf(at));
        while (steps.FirstOrDefault(step => step.FromNodeId == at) is { } next)
        {
            steps.Remove(next);
            at = next.ToNodeId;
            names.Add(_plant.NameOf(at));
        }

        Assert.Empty(steps);
        return [.. names];
    }

    private PathMission AMissionOnTheDistributorChosenFor(string destination)
    {
        var mission = _plant.MissionAt("D", null);
        Assert.Same(mission, _plant["D"].LoadedLu);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.True(mission.SetOperatorDestination(_plant[destination].ItemGuid));
        return mission;
    }
}
