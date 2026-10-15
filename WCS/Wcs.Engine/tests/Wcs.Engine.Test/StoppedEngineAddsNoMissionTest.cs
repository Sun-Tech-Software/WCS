using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Una volta impostato lo Stop, nessuna missione entra piu' fra le attive.
/// </summary>
/// <remarks>
/// <see cref="ActiveMissions.Add"/> controllava lo stato del navigatore prima di prendere il
/// lucchetto dell'inserimento: chi aveva passato il controllo in Run inseriva anche se nel frattempo
/// lo Stop era stato scritto. Una missione comparsa a motore fermo e' quella che la sostituzione
/// dell'impianto rifiuta («Ci sono missioni attive in memoria»), e nelle prove e' sopravvissuta al
/// Dispose di GetFromFailureTest arrivando nella classe successiva. Una missione entrata
/// <b>prima</b> dello Stop resta, come sempre: lo Stop non svuota le attive.
/// </remarks>
public sealed class StoppedEngineAddsNoMissionTest : IDisposable
{
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;
    private readonly WcsDatabase? _previousStore = Navigator.Store;
    private readonly QueuedDatabaseWriter<WcsWrite>? _previousWriter = Navigator.StateWriter;

    public StoppedEngineAddsNoMissionTest()
    {
        // Senza store l'ORM non scrive: qui conta solo la collezione in memoria.
        Navigator.Store = null;
        Navigator.StateWriter = null;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        ActiveMissions.Instance.Clear(removeFromDb: false).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        ActiveMissions.Instance.Clear(removeFromDb: false).GetAwaiter().GetResult();
        Navigator.NavStatus = _previousStatus;
        Navigator.Store = _previousStore;
        Navigator.StateWriter = _previousWriter;
    }

    [Theory]
    [InlineData(NavigatorStatusTypes.Stop)]
    [InlineData(NavigatorStatusTypes.Pause)]
    public async Task NoMissionEntersAfterTheStatusThatRefusesThemHasBeenSet(NavigatorStatusTypes refusing)
    {
        long luId = 0;
        for (var round = 0; round < 20; round++)
        {
            await ActiveMissions.Instance.Clear(removeFromDb: false);
            Navigator.NavStatus = NavigatorStatusTypes.Run;

            // Ogni inserimento con una UDC scorre le attive per il controllo dei doppioni, sotto il
            // lucchetto: con molti inseritori in coda la finestra fra il controllo dello stato e
            // l'inserimento e' larga, che e' quello che si vuole provare.
            var stopped = 0;
            var started = new CountdownEvent(Environment.ProcessorCount);
            var adders = Enumerable.Range(0, Environment.ProcessorCount).Select(_ => Task.Run(() =>
            {
                started.Signal();
                while (Volatile.Read(ref stopped) == 0)
                {
                    var mission = new PathMission(Guid.NewGuid());
                    mission.LuId = Interlocked.Increment(ref luId);
                    ActiveMissions.Instance.Add(mission, buildRoute: false);
                }
            })).ToArray();

            started.Wait();
            SpinWait.SpinUntil(() => ActiveMissions.Instance.Count >= 200);

            Navigator.NavStatus = refusing;
            var whenRefused = ActiveMissions.Instance.Count;

            Volatile.Write(ref stopped, 1);
            await Task.WhenAll(adders);

            Assert.Equal(whenRefused, ActiveMissions.Instance.Count);
        }
    }
}
