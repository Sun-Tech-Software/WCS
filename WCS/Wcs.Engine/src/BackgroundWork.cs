using System.Collections.Concurrent;

namespace Wcs.Engine;

/// <summary>
/// I lavori di sfondo «lancia e prosegui» del motore che finiscono da soli: chi li lancia non li
/// attende, ma da qui si puo' sapere quando sono finiti tutti.
/// </summary>
/// <remarks>
/// <para>
/// Sono cinque, e scrivono tutti sullo store o sullo scrittore di stato <b>correnti quando
/// arrivano</b>: il <c>PositionWork</c> del cambio di posizione di una missione, la scrittura
/// dello stato che segue un cambio di <c>RunningStepId</c>, la costruzione della rotta di una
/// missione appena aggiunta, le operazioni che un dispositivo avvia con <c>RunObserved</c> e le
/// esecuzioni con la pausa che il comando «sposta l'UDC» chiede ai due dispositivi
/// (<c>ExecutableCommands.MoveLuToItem</c>, dal giro finale delle correzioni della voce 41).
/// I cicli di sfondo (<c>BackgroundLoop</c>, i processi dei dispositivi) non ci stanno: non
/// finiscono, si fermano con la cancellazione.
/// </para>
/// <para>
/// Serve alle prove, che condividono lo stato statico del motore: se un test finisce con uno di
/// questi lavori in volo, la sua scrittura arriva nello store del test successivo. Sotto carico
/// erano una trentina le classi che ne lasciavano (coda dei lavori, voci 42 e 46).
/// </para>
/// </remarks>
internal static class BackgroundWork
{
    private static readonly ConcurrentDictionary<Task, byte> Pending = new();

    /// <summary>Tiene il lavoro nell'elenco finche' non finisce, e lo restituisce.</summary>
    internal static Task Track(Task work)
    {
        if (work.IsCompleted) return work;
        Pending.TryAdd(work, 0);
        work.ContinueWith(static done => Pending.TryRemove(done, out _), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return work;
    }

    /// <summary>Quanti lavori sono ancora in corso.</summary>
    internal static int Count => Pending.Count;

    /// <summary>
    /// Aspetta che non ci sia piu' nessun lavoro in corso, compresi quelli che i lavori attesi
    /// lanciano a loro volta. Restituisce false se il tempo finisce prima.
    /// </summary>
    internal static bool WaitUntilIdle(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var running = Pending.Keys.ToArray();
            if (running.Length == 0) return true;

            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return false;

            // Come sono finiti non conta qui: ognuno registra da se' i propri errori.
            Task.WhenAll(running).ContinueWith(static _ => { }, TaskScheduler.Default).Wait(left);
        }
    }
}
