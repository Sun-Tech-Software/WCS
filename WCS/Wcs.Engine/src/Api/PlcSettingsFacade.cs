using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;

namespace Wcs.Engine.Api;

public static class PlcSettingsFacade
{
    public static Task<List<PlcConnection>> ConnectionsAsync() =>
        StoreAccess.ReadAsync(c => c.PlcConnections.AsNoTracking().OrderBy(x => x.Id).ToListAsync());

    public static Task<PlcConnection?> FindAsync(long id) =>
        StoreAccess.ReadAsync(c => c.PlcConnections.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id));

    /// <summary>
    /// L'elenco intero, come lo salvava la griglia di FrmPcmPlc: le chiavi si assegnano, non si
    /// generano. Prende effetto al prossimo caricamento dell'impianto (avvio dell'host o
    /// ricaricamento del diagramma), non al prossimo avvio del motore. Rifiutata
    /// (<see cref="EngineBusyException"/>) in ogni stato che non sia Stop.
    /// </summary>
    /// <remarks>
    /// Questa API scrive la configurazione, non lo stato operativo. Passa dal cancello delle
    /// scritture dell'impianto e con la sua guardia piu' severa (<see cref="PlantQueries.RequireStopped"/>,
    /// dove sta il perche' di ogni stato): fino al compito 4b della console non chiedeva niente al
    /// motore, e togliere una connessione mentre il motore la usava lasciava i suoi dispositivi con
    /// comandi destinati a fallire.
    /// <para>
    /// <b>Non toglie una connessione che un dispositivo usa ancora</b> (compito 4c della console,
    /// decisione del proprietario del 27/9): un elenco che lo farebbe e' rifiutato con
    /// <see cref="PlcConnectionInUseException"/> (il <c>409</c> dell'host), che nomina connessioni e
    /// dispositivi, e non sostituisce niente. Il controllo corre dentro la stessa scrittura della
    /// sostituzione, sotto il cancello delle scritture dell'impianto: ogni scrittura che cambia il
    /// <c>DeviceConnectionId</c> di un dispositivo (configurazione, creazione, sostituzione
    /// dell'impianto) passa da quel cancello, quindi un dispositivo spostato sulla connessione da
    /// un'altra scrittura non puo' infilarsi fra il controllo e la cancellazione. Dopo il controllo
    /// del motore: la connessione in uso e' una domanda da fare solo a una sostituzione che potrebbe
    /// avvenire.
    /// </para>
    /// </remarks>
    public static Task ReplaceAsync(IReadOnlyCollection<PlcConnection> connections) =>
        PlantQueries.MutatePlantAsync(async () =>
        {
            PlantQueries.RequireStopped("connessioni PLC");
            IReadOnlyList<PlcConnectionInUse> inUse = [];
            await StoreAccess.WriteAsync(async context =>
            {
                // Un rifiuto, non un guasto della scrittura: si esce senza toccare niente e si solleva
                // fuori dallo scrittore, come fa ReplaceGraphCoreAsync con il lavoro pendente - un'eccezione
                // qui dentro entrerebbe nel conto dei tentativi falliti dello scrittore di stato.
                inUse = await PlcSettingsStore.RemovedConnectionsInUseAsync(context, connections);
                if (inUse.Count > 0) return;
                await PlcSettingsStore.ReplaceConnectionsAsync(context, connections);
            });
            if (inUse.Count > 0) throw new PlcConnectionInUseException(inUse);
        });
}
