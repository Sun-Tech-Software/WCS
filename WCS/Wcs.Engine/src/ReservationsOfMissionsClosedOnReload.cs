using Log.Interface;
using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;

namespace Wcs.Engine;

/// <summary>
/// Le prenotazioni di deposito delle missioni che la ricarica chiude (specifica, risposta 10 dell'8/10
/// sera): un'estrazione ricaricata con il segno «segnalazioni partite» e senza il cambio cella
/// registrato (<c>DbInfo.CloseAnExtractionWhoseReportsWereSent</c>) puo' aver preso una cella prima
/// del riavvio, fra la prenotazione e il commit. La si restituisce al gestionale appena il motore va in
/// Run, una volta; durante il caricamento nessuna chiamata.
/// </summary>
/// <remarks>
/// <para>
/// La ricarica annota la missione (<see cref="AddAsync"/>); il passaggio a Run (il setter di
/// <see cref="Navigator.NavStatus"/>) le restituisce tutte in sottofondo (<see cref="GiveBack"/>), fuori
/// da ogni lucchetto. Il rilascio va per missione ed e' idempotente, come quello di
/// <c>ActiveMissions.Remove</c>: chi non aveva una prenotazione non perde niente.
/// </para>
/// <para>
/// <b>L'elenco sta anche nel database del WCS</b> (tabella <c>ReservationsToGiveBack</c>, risposta 2
/// del proprietario del 9/10): la riga si scrive prima che la ricarica tolga quella della missione, e
/// la ricarica successiva la rilegge (<see cref="LoadAsync"/>), cosi' se l'host si ferma prima del Run
/// la prenotazione resta da restituire. Prima stava solo in memoria, e con l'host fermato prima del Run
/// la cella restava presa fino alla scadenza del gestionale.
/// </para>
/// <para>
/// <b>Un tentativo solo.</b> Se il gestionale non risponde in quel momento, una riga di avviso per
/// missione e la prenotazione resta alla scadenza del gestionale, che la raccoglie: nessun
/// ritentativo, ne' a questo passaggio a Run ne' ai prossimi. Restituita, o scartata con la riga di
/// avviso, la riga si cancella.
/// </para>
/// <para>
/// <see cref="LoadAsync"/> unisce le righe all'elenco in memoria invece di sostituirlo: si equivalgono,
/// perche' <see cref="AddAsync"/> scrive la riga prima della memoria, e una ricarica fallita e ripresa
/// dall'host ritrova la prenotazione della missione che la prima ha chiuso nella riga di questa tabella,
/// anche se la riga della missione non c'e' piu'. Una riga che un rilascio ancora
/// in corso non ha tolto si rilegge, e il rilascio, idempotente, si richiede una volta in piu'.
/// </para>
/// </remarks>
internal static class ReservationsOfMissionsClosedOnReload
{
    private static readonly Lock _lock = new();
    private static List<Guid> _missions = [];

    /// <summary>
    /// Il rilascio partito all'ultimo passaggio a Run che aveva qualcosa da restituire; completato se
    /// non ce n'e' stato nessuno. Non solleva. Serve alle prove, che lo aspettano invece del tempo.
    /// </summary>
    internal static Task GivenBack { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// La missione chiusa dalla ricarica, la cui prenotazione si restituisce al passaggio a Run. La riga
    /// nel database del WCS e' scritta quando il compito finisce: chi chiama toglie dopo quella della
    /// missione. Un riavvio fra le due ricarica la missione, che si richiude e si riannota; la riga e'
    /// una per missione.
    /// </summary>
    internal static async Task AddAsync(Guid missionId)
    {
        await WcsStateWrites.WriteAndWaitAsync(async context =>
        {
            if (!context.ReservationsToGiveBack.Local.Any(row => row.MissionId == missionId) &&
                !await context.ReservationsToGiveBack.AnyAsync(row => row.MissionId == missionId))
                context.ReservationsToGiveBack.Add(new ReservationToGiveBack { MissionId = missionId });
        });

        lock (_lock)
            if (!_missions.Contains(missionId))
                _missions.Add(missionId);
    }

    /// <summary>
    /// Rilegge dal database del WCS le prenotazioni ancora da restituire, all'inizio della ricarica
    /// (<c>DbInfo.RecoverFailure</c>): quelle di un host fermato prima del Run tornano nell'elenco. Una
    /// lettura del database del WCS soltanto, niente gestionale.
    /// </summary>
    internal static async Task LoadAsync()
    {
        var store = Navigator.Store;
        if (store == null) return;

        List<Guid> stored;
        await using (var context = store.CreateContext())
            stored = await context.ReservationsToGiveBack.AsNoTracking()
                .OrderBy(row => row.Id)
                .Select(row => row.MissionId)
                .ToListAsync();

        lock (_lock)
            foreach (var missionId in stored.Where(missionId => !_missions.Contains(missionId)))
                _missions.Add(missionId);
    }

    /// <summary>
    /// Il motore e' andato in Run: le prenotazioni annotate si restituiscono in sottofondo, e l'elenco
    /// si svuota subito, cosi' che un altro passaggio a Run non le richieda.
    /// </summary>
    internal static void GiveBack()
    {
        lock (_lock)
        {
            if (_missions.Count == 0) return;
            var missions = _missions;
            _missions = [];

            // Sotto il lucchetto, come lo svuotamento: due passaggi a Run insieme non scrivono il
            // compito uno sopra l'altro, e Forget non ne perde uno a meta'. Task.Run non aspetta.
            GivenBack = Task.Run(() => GiveBackAsync(missions));
        }
    }

    /// <summary>Dimentica le missioni annotate senza restituire niente: per le prove, che condividono lo stato statico.</summary>
    internal static void Forget()
    {
        lock (_lock)
        {
            _missions = [];
            GivenBack = Task.CompletedTask;
        }
    }

    private static async Task GiveBackAsync(List<Guid> missions)
    {
        foreach (var missionId in missions)
        {
            try
            {
                var warehouse = Navigator.Warehouse
                                ?? throw new InvalidOperationException("il gestionale non e' configurato");
                await warehouse.ReleaseStorageReservationAsync(missionId);
                Navigator.WriteLog(
                    $"Missione {missionId}, chiusa alla ricarica: la sua prenotazione di deposito e' stata restituita al gestionale",
                    LogEntryType.Information);
            }
            catch (Exception ex)
            {
                Navigator.WriteLog(
                    $"Missione {missionId}, chiusa alla ricarica: la prenotazione di deposito non e' stata restituita al gestionale ({ex.Message}). Non si riprova: la liberera' la scadenza del gestionale",
                    LogEntryType.Warning);
            }

            // Restituita, o scartata con la riga di avviso: non e' piu' da restituire, e la riga si
            // toglie. Rimasta per un guasto, la prossima ricarica la rilegge e il rilascio, idempotente,
            // si richiede una volta in piu'.
            try
            {
                await WcsStateWrites.WriteAndWaitAsync(async context =>
                {
                    var row = await context.ReservationsToGiveBack.SingleOrDefaultAsync(r => r.MissionId == missionId);
                    if (row != null) context.ReservationsToGiveBack.Remove(row);
                });
            }
            catch (Exception ex)
            {
                Navigator.WriteLog(
                    $"Missione {missionId}, chiusa alla ricarica: la riga della prenotazione da restituire non e' stata tolta dal database del WCS ({ex.Message}); alla prossima ricarica il rilascio si richiede",
                    LogEntryType.Warning);
            }
        }
    }
}
