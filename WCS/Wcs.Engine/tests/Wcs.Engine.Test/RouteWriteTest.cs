using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Xunit;
using static Wcs.Common.Enums;
using RouteWriteOutcome = Wcs.Engine.Items.PathMission.RouteWriteOutcome;

namespace Wcs.Engine.Test;

/// <summary>
/// La versione della rotta e la primitiva che la scrive (voce 41, compito 2, disegno C): chi ha
/// calcolato una rotta su una versione vecchia non scrive, e due scrittori insieme non lasciano alla
/// missione una rotta mista.
/// </summary>
/// <remarks>
/// <para>
/// Il motore sta in <c>Stop</c>: il setter della destinazione, in <c>Recover</c>, salta il blocco che
/// toglie gli step passando dai dispositivi. Nessun dispositivo: gli step portano solo gli
/// identificativi dei nodi, e la primitiva non guarda i dispositivi.
/// </para>
/// <para>
/// Le prove di concorrenza fermano uno scrittore dentro la primitiva con la cucitura
/// <see cref="PathMission.RouteWritePauseForTesting"/> e aspettano che il secondo sia fermo sul
/// lucchetto, o che abbia finito: nessuna attesa a tempo. Se il lucchetto mancasse il secondo
/// finirebbe prima che il primo riparta, e la prova lo vede.
/// </para>
/// </remarks>
public sealed class RouteWriteTest : IDisposable
{
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;

    private static readonly Guid P = Guid.NewGuid();
    private static readonly Guid X = Guid.NewGuid();
    private static readonly Guid Y = Guid.NewGuid();

    public RouteWriteTest()
    {
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        MissionSteps.Instance.RemoveAll(_ => true);
    }

    public void Dispose()
    {
        MissionSteps.Instance.RemoveAll(_ => true);
        Navigator.NavStatus = _previousStatus;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Due scrittori hanno calcolato sulla stessa versione: il primo scrive, il secondo perde e non
    /// tocca niente, ne' i campi ne' gli step.
    /// </summary>
    [Fact]
    public void OfTwoWritersOnTheSameVersionTheFirstWinsAndTheSecondLoses()
    {
        var mission = new PathMission();
        var seen = mission.RouteVersion;
        var first = Step(mission, P, X);
        var second = Step(mission, P, Y);
        var secondFieldsWritten = false;

        var firstOutcome = mission.TryWriteRoute(seen, [first], () =>
        {
            mission.DestinationItemId = X;
            return true;
        });
        var secondOutcome = mission.TryWriteRoute(seen, [second], () =>
        {
            secondFieldsWritten = true;
            mission.DestinationItemId = Y;
            return true;
        });

        Assert.Equal(RouteWriteOutcome.Written, firstOutcome);
        Assert.Equal(RouteWriteOutcome.Lost, secondOutcome);
        Assert.False(secondFieldsWritten);
        Assert.Equal(X, mission.DestinationItemId);
        Assert.Equal([first.StepId], RouteOf(mission));
    }

    /// <summary>
    /// Il primo scrittore e' fermo dentro la primitiva, con la rotta vecchia tolta e la nuova non
    /// ancora scritta: il secondo, sulla stessa versione, aspetta che finisca e poi perde. Senza il
    /// lucchetto entrava in quel punto, trovava la versione ancora uguale, e la missione restava con
    /// gli step di tutti e due.
    /// </summary>
    /// <remarks>
    /// Il primo scrittore riscrive la destinazione che la missione ha gia': i suoi campi non fanno
    /// salire la versione, e il secondo la trova uguale. Lo ferma solo il lucchetto.
    /// </remarks>
    [Fact]
    public async Task ASecondWriterCannotGetIntoTheMiddleOfAWrite()
    {
        var mission = new PathMission { DestinationItemId = X };
        var seen = mission.RouteVersion;
        var first = Step(mission, P, X);
        var second = Step(mission, P, Y);
        using var pause = new PauseOnce(mission);

        var firstWriter = new Writer(() => mission.TryWriteRoute(seen, [first], () =>
        {
            mission.DestinationItemId = X;
            return true;
        }));
        await pause.ReachedOrFinished(firstWriter);
        Assert.True(pause.Reached);

        var secondWriter = new Writer(() => mission.TryWriteRoute(seen, [second], () =>
        {
            mission.DestinationItemId = Y;
            return true;
        }));
        secondWriter.WaitUntilBlockedOrFinished();

        pause.Release();

        Assert.Equal(RouteWriteOutcome.Written, await firstWriter.Finished);
        Assert.Equal(RouteWriteOutcome.Lost, await secondWriter.Finished);
        Assert.Equal(X, mission.DestinationItemId);
        Assert.Equal([first.StepId], RouteOf(mission));
    }

    /// <summary>
    /// Il setter della destinazione prende il lucchetto della rotta: un cambio di destinazione che
    /// arriva mentre uno scrittore e' a meta' scrittura aspetta, e poi toglie la rotta che non va piu'
    /// verso la destinazione. Senza, cambiava la destinazione e toglieva gli step nel mezzo, e lo
    /// scrittore aggiungeva dopo la sua rotta verso la destinazione di prima: destinazione e rotta di
    /// due scrittori diversi.
    /// </summary>
    /// <remarks>
    /// La missione parte senza destinazione: il setter non passa da <see cref="PathMission.RemoveMissionSteps"/>,
    /// che il lucchetto lo prende da se', e resta solo il suo.
    /// </remarks>
    [Fact]
    public async Task AChangeOfDestinationWaitsForAWriteInProgress()
    {
        var mission = new PathMission { DestinationItemId = null };
        var first = Step(mission, P, X);
        using var pause = new PauseOnce(mission);

        var routeWriter = new Writer(() => mission.TryWriteRoute(mission.RouteVersion, [first]));
        await pause.ReachedOrFinished(routeWriter);
        Assert.True(pause.Reached);

        var destinationWriter = new Writer(() =>
        {
            mission.DestinationItemId = Y;
            return RouteWriteOutcome.Written;
        });
        destinationWriter.WaitUntilBlockedOrFinished();
        Assert.False(destinationWriter.Finished.IsCompleted);

        pause.Release();
        Assert.Equal(RouteWriteOutcome.Written, await routeWriter.Finished);
        await destinationWriter.Finished;

        Assert.Equal(Y, mission.DestinationItemId);
        Assert.Empty(RouteOf(mission));
    }

    /// <summary>
    /// Il setter di <see cref="PathMission.MissionSteps"/> passa dalla primitiva: due assegnazioni
    /// insieme non si mescolano. La prima e' ferma fra la rimozione e l'aggiunta; la seconda aspetta,
    /// e scrive dopo per intero. Prima erano <c>RemoveAll</c> e <c>AddRange</c> separati, e la
    /// missione restava con le due rotte (la corsa 1 della ricognizione).
    /// </summary>
    [Fact]
    public async Task TwoAssignmentsOfTheStepsDoNotMix()
    {
        var mission = new PathMission();
        var firstA = Step(mission, P, X);
        var firstB = Step(mission, X, Y);
        var second = Step(mission, P, Y);
        using var pause = new PauseOnce(mission);

        var firstWriter = new Writer(() =>
        {
            mission.MissionSteps = [firstA, firstB];
            return RouteWriteOutcome.Written;
        });
        await pause.ReachedOrFinished(firstWriter);
        Assert.True(pause.Reached);

        var secondWriter = new Writer(() =>
        {
            mission.MissionSteps = [second];
            return RouteWriteOutcome.Written;
        });
        secondWriter.WaitUntilBlockedOrFinished();
        Assert.False(secondWriter.Finished.IsCompleted);

        pause.Release();
        await firstWriter.Finished;
        await secondWriter.Finished;

        Assert.Equal([second.StepId], RouteOf(mission));
    }

    /// <summary>
    /// Una rotta calcolata prima che qualcuno togliesse la rotta della missione non si scrive: la
    /// rimozione fa salire la versione.
    /// </summary>
    [Fact]
    public void ARouteComputedBeforeTheRouteWasRemovedLoses()
    {
        var mission = new PathMission();
        mission.MissionSteps = [Step(mission, P, X)];
        var seen = mission.RouteVersion;

        mission.RemoveMissionSteps();

        Assert.Equal(RouteWriteOutcome.Lost, mission.TryWriteRoute(seen, [Step(mission, P, Y)]));
        Assert.Empty(RouteOf(mission));
    }

    /// <summary>
    /// Togliere una rotta che non c'e' non e' una scrittura: la versione resta, e chi ha calcolato
    /// su di lei scrive.
    /// </summary>
    [Fact]
    public void RemovingARouteThatIsNotThereLeavesTheVersion()
    {
        var mission = new PathMission();
        var seen = mission.RouteVersion;

        mission.RemoveMissionSteps();

        var step = Step(mission, P, X);
        Assert.Equal(RouteWriteOutcome.Written, mission.TryWriteRoute(seen, [step]));
        Assert.Equal([step.StepId], RouteOf(mission));
    }

    /// <summary>
    /// Una rotta calcolata verso la destinazione di prima non si scrive: il cambio della destinazione
    /// fa salire la versione, anche quando la missione non aveva step da togliere.
    /// </summary>
    [Fact]
    public void ARouteComputedBeforeTheDestinationChangedLoses()
    {
        var mission = new PathMission { DestinationItemId = X };
        var seen = mission.RouteVersion;

        mission.DestinationItemId = Y;

        Assert.Equal(RouteWriteOutcome.Lost, mission.TryWriteRoute(seen, [Step(mission, P, X)]));
        Assert.Empty(RouteOf(mission));
    }

    /// <summary>
    /// Chi rinuncia nei suoi campi non scrive gli step, e la versione non sale per lui.
    /// </summary>
    [Fact]
    public void AWriterThatGivesUpInItsFieldsWritesNoSteps()
    {
        var mission = new PathMission();
        var old = Step(mission, P, X);
        mission.MissionSteps = [old];
        var seen = mission.RouteVersion;

        Assert.Equal(RouteWriteOutcome.Refused, mission.TryWriteRoute(seen, [Step(mission, P, Y)], () => false));

        Assert.Equal([old.StepId], RouteOf(mission));
        Assert.Equal(seen, mission.RouteVersion);
    }

    /// <summary>
    /// <see cref="PathMission.CommitRoute"/> e' un caso della primitiva: con la versione cambiata dopo
    /// la sonda non scrive niente, e la missione resta con la destinazione e la rotta di chi ha scritto
    /// nel frattempo.
    /// </summary>
    [Fact]
    public void ACommitOnAnOldVersionTouchesNothing()
    {
        var mission = new PathMission { DestinationItemId = X };
        var seen = mission.RouteVersion;
        var meanwhile = Step(mission, P, X);
        mission.MissionSteps = [meanwhile];

        var committed = mission.CommitRoute(Y, [Step(mission, P, Y)], Guid.Empty, null, null,
            operatorChoice: true, changeOfCell: false, seenRouteVersion: seen);

        Assert.Equal(RouteWriteOutcome.Lost, committed);
        Assert.Equal(X, mission.DestinationItemId);
        Assert.False(mission.OperatorDestination);
        Assert.Equal([meanwhile.StepId], RouteOf(mission));
    }

    /// <summary>
    /// Lo stesso commit sulla versione giusta scrive: destinazione con il segno, e la sua rotta.
    /// </summary>
    [Fact]
    public void ACommitOnTheCurrentVersionWrites()
    {
        var mission = new PathMission { DestinationItemId = X };
        mission.MissionSteps = [Step(mission, P, X)];
        var seen = mission.RouteVersion;
        var step = Step(mission, P, Y);

        var committed = mission.CommitRoute(Y, [step], Guid.Empty, null, null,
            operatorChoice: true, changeOfCell: false, seenRouteVersion: seen);

        Assert.Equal(RouteWriteOutcome.Written, committed);
        Assert.Equal(Y, mission.DestinationItemId);
        Assert.True(mission.OperatorDestination);
        Assert.Equal([step.StepId], RouteOf(mission));
    }

    /// <summary>
    /// L'ordine dei lucchetti della missione: il lucchetto della rotta prima di quello della scelta.
    /// Uno scrittore tiene la rotta e, nei suoi campi, chiedera' la scelta (come <c>CommitRoute</c>, che
    /// scrive la destinazione con <see cref="PathMission.UnlessChosenByOperator"/>); intanto un altro
    /// thread scrive la destinazione dalla configurazione, o l'operatore la sceglie
    /// (<see cref="PathMission.SetOperatorDestination"/>). Quel thread deve fermarsi sulla rotta senza
    /// tenere la scelta: se la prendesse prima, i due si fermerebbero per sempre. La prova guarda la
    /// scelta mentre il secondo thread e' fermo: deve essere libera.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AWriteOfTheDestinationWaitsForTheRouteBeforeTakingTheChoice(bool byTheOperator)
    {
        var mission = new PathMission { DestinationItemId = X };
        var inside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var gate = new ManualResetEventSlim();

        var routeWriter = new Writer(() => mission.TryWriteRoute(mission.RouteVersion, [Step(mission, P, Y)], () =>
        {
            inside.TrySetResult();
            gate.Wait();
            return mission.UnlessChosenByOperator(() => mission.DestinationItemId = Y);
        }));
        await Task.WhenAny(inside.Task, routeWriter.Finished);
        Assert.True(inside.Task.IsCompleted);

        var configurationWriter = new Writer(() =>
        {
            if (byTheOperator)
                mission.SetOperatorDestination(P);
            else
                mission.UnlessChosenByOperator(() => mission.DestinationItemId = P);
            return RouteWriteOutcome.Written;
        });
        configurationWriter.WaitUntilBlockedOrFinished();
        Assert.False(configurationWriter.Finished.IsCompleted);

        var choice = typeof(PathMission)
            .GetField("_destinationChoice", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(mission)!;
        var choiceIsFree = Monitor.TryEnter(choice);
        if (choiceIsFree) Monitor.Exit(choice);
        Assert.True(choiceIsFree, "La scrittura della destinazione tiene la scelta mentre aspetta la rotta");

        gate.Set();

        // I tetti trasformano in un rosso il blocco reciproco dell'ordine sbagliato dei lucchetti: se lo
        // sguardo qui sopra fosse scattato prima che la scrittura della destinazione prendesse la scelta,
        // con l'ordine sbagliato la prenderebbe dopo e si fermerebbe sulla rotta, e lo scrittore della
        // rotta si fermerebbe sulla scelta. Senza tetto la prova resterebbe appesa.
        Assert.Equal(RouteWriteOutcome.Written, await routeWriter.Finished.WaitAsync(TimeSpan.FromSeconds(30)));
        await configurationWriter.Finished.WaitAsync(TimeSpan.FromSeconds(30));

        // La scrittura della destinazione e' arrivata dopo la rotta, e l'ha cambiata: la rotta verso Y
        // non vale piu', e si toglie.
        Assert.Equal(P, mission.DestinationItemId);
        Assert.Equal(byTheOperator, mission.OperatorDestination);
        Assert.Empty(RouteOf(mission));
    }

    // ---------------------------------------------------------------------------------------
    // Scene
    // ---------------------------------------------------------------------------------------

    private static MissionStep Step(PathMission mission, Guid from, Guid to) => new()
    {
        StepId = Guid.NewGuid(),
        MissionId = mission.MissionId,
        FromNodeId = from,
        ToNodeId = to
    };

    private static List<Guid> RouteOf(PathMission mission) =>
        MissionSteps.Instance.Where(x => x.MissionId == mission.MissionId).Select(x => x.StepId).Order().ToList();

    /// <summary>
    /// Ferma il primo scrittore che arriva alla cucitura della primitiva, finche' la prova non lo
    /// lascia andare; quelli dopo passano.
    /// </summary>
    private sealed class PauseOnce : IDisposable
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _gate = new();
        private int _taken;

        public PauseOnce(PathMission mission)
        {
            mission.RouteWritePauseForTesting = () =>
            {
                if (Interlocked.Exchange(ref _taken, 1) != 0) return;
                _reached.TrySetResult();
                _gate.Wait();
            };
        }

        public bool Reached => _reached.Task.IsCompleted;

        /// <summary>Aspetta che lo scrittore sia fermo nella cucitura, o che abbia finito senza passarci.</summary>
        public Task ReachedOrFinished(Writer writer) => Task.WhenAny(_reached.Task, writer.Finished);

        public void Release() => _gate.Set();

        public void Dispose()
        {
            _gate.Set();
            _gate.Dispose();
        }
    }

    /// <summary>
    /// Uno scrittore sul suo thread, di sfondo: se una prova rossa lo lascia fermo, il processo delle
    /// prove esce lo stesso.
    /// </summary>
    private sealed class Writer
    {
        private readonly Thread _thread;
        private readonly TaskCompletionSource<RouteWriteOutcome> _finished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Writer(Func<RouteWriteOutcome> write)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    _finished.TrySetResult(write());
                }
                catch (Exception ex)
                {
                    _finished.TrySetException(ex);
                }
            }) { IsBackground = true };
            _thread.Start();
        }

        public Task<RouteWriteOutcome> Finished => _finished.Task;

        /// <summary>Fermo su un lucchetto o un'attesa.</summary>
        public bool IsBlocked => (_thread.ThreadState & ThreadState.WaitSleepJoin) != 0;

        /// <summary>
        /// Aspetta che il thread sia fermo (sul lucchetto della rotta, nelle prove qui sopra) o che
        /// abbia finito: e' la condizione, non un tempo.
        /// </summary>
        /// <remarks>
        /// Lo stato <c>WaitSleepJoin</c> non dice su che cosa il thread e' fermo: anche l'avvio del
        /// thread, il lucchetto del registro (<c>Navigator.WriteLog</c>) o un'altra attesa prima del
        /// lucchetto della rotta lo fanno scattare. E lo scrittore legge la versione sul suo thread, dopo
        /// la partenza. Se scatta prima, lo scrittore legge la versione dopo e scrive: nelle prove
        /// sull'ordine delle scritture e' un rosso spurio (gli stati finali lo prendono), non un verde
        /// cieco. Nella prova sull'ordine dei lucchetti l'ordine sbagliato finisce in un blocco reciproco,
        /// che i tetti di quella prova fanno rosso (revisione finale della voce 41).
        /// </remarks>
        public void WaitUntilBlockedOrFinished() => SpinWait.SpinUntil(() => Finished.IsCompleted || IsBlocked);
    }
}
