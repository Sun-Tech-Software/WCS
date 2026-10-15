using Log.Interface;
using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Wms.Contracts;
using static Wcs.Common.Enums;

namespace Wcs.Engine;

/// <summary>
/// Le celle che il PLC ha rifiutato con l'errore 67 («cella piena») e che il gestionale non ha segnato
/// piene perche' non rispondeva (<c>AutomationDeviceConnectionManager.LockTheRefusedCellsAsync</c>): il
/// segno si ritenta a ogni giro del freno del navigatore finche' il gestionale risponde (risposta 3 del
/// proprietario del 9/10). Prima nessuno lo ritentava: la cella restava libera nel gestionale, e la
/// prossima UDC poteva prenotarla e farsi rifiutare il deposito di nuovo.
/// </summary>
/// <remarks>
/// <para>
/// L'elenco sta nel database del WCS (tabella <c>CellsToMarkFull</c>), e la ricarica lo rilegge
/// (<see cref="LoadAsync"/>, all'inizio di <c>DbInfo.RecoverFailure</c>): un riavvio non lo perde.
/// Riuscito il segno, la cella esce dall'elenco e dalla tabella.
/// </para>
/// <para>
/// <b>Una riga di avviso alla prima mancata</b>, quella del blocco dopo il 67, e non a ogni giro: i
/// ritentativi che non riescono non scrivono niente, e quello che riesce scrive un'informazione. Un
/// rifiuto del gestionale (un 4xx) e' una risposta: la cella esce dall'elenco con una riga di avviso,
/// perche' riprovare non lo cambia. Un guasto che non viene dal gestionale (il gestionale non
/// configurato, un difetto del WCS) non e' «il gestionale non risponde ancora»: si ritenta con il freno
/// come quello, ma una riga di avviso lo dice, con il tipo dell'eccezione, una volta per guasto uguale.
/// Prima ogni eccezione valeva il gestionale muto, e un difetto si ritentava per sempre senza traccia.
/// </para>
/// <para>
/// <b>Il giro.</b> <c>Navigator.BuildRoutePath</c>, che gira solo in Run, chiama <see cref="Retry"/> a
/// ogni giro: se l'elenco non e' vuoto, il freno (<see cref="Retries"/>, le misure del freno del giro
/// delle rotte) e' scaduto e nessun ritentativo e' in volo, ne parte uno in sottofondo. Nessuna chiamata
/// al gestionale in Recover o dentro un lucchetto: l'elenco si copia sotto il lucchetto, e il gestionale
/// si chiama fuori. Il ritentativo in volo guarda lo stato del motore prima di ogni porta, e fuori da
/// Run (Stop, o la ricarica in Recover) si ferma: le porte che restano stanno nell'elenco, e il primo
/// giro in Run riparte da loro. Prima finiva la sua copia dell'elenco anche durante la ricarica.
/// </para>
/// </remarks>
internal static class CellsToMarkFull
{
    /// <summary>La chiave del freno: l'elenco si ritenta tutto insieme, in un solo giro.</summary>
    private static readonly Guid TheList = new("67c0f011-0000-4000-8000-000000000067");

    private static readonly Lock _lock = new();
    private static List<string> _doors = [];

    /// <summary>
    /// Il giro dell'elenco: <see cref="Forget"/> lo fa avanzare, e un ritentativo partito prima non tocca
    /// piu' niente (le prove, che condividono lo stato statico, non se lo ritrovano nella prova dopo).
    /// </summary>
    private static int _generation;

    /// <summary>L'ultimo guasto non del gestionale scritto nel registro: lo stesso non si riscrive a ogni giro.</summary>
    private static string? _lastFailureWritten;

    /// <summary>
    /// Il freno dei ritentativi: le misure del freno del giro delle rotte (<c>Navigator.RoutePathRetries</c>,
    /// <see cref="Navigator.RouteRetryFirstWaitMs"/> e <see cref="Navigator.RouteRetryLongestWaitMs"/>).
    /// Assegnabile perche' le prove lo costruiscano con un orologio finto.
    /// </summary>
    internal static RetryBackoff Retries = new(TimeSpan.FromMilliseconds(Navigator.RouteRetryFirstWaitMs),
        TimeSpan.FromMilliseconds(Navigator.RouteRetryLongestWaitMs));

    /// <summary>
    /// L'ultimo ritentativo partito; completato se non ce n'e' stato nessuno. Non solleva. Serve alle
    /// prove, che lo aspettano invece del tempo, e al giro, che non ne avvia un secondo.
    /// </summary>
    internal static Task Retried { get; private set; } = Task.CompletedTask;

    /// <summary>I codici porta delle celle da segnare piene, nell'ordine in cui sono arrivate.</summary>
    internal static IReadOnlyList<string> Doors
    {
        get
        {
            lock (_lock) return [.. _doors];
        }
    }

    /// <summary>
    /// Le celle che il gestionale non ha segnato piene dopo il 67: si scrivono nel database del WCS, e
    /// poi nell'elenco. Una porta gia' nell'elenco non si ripete.
    /// </summary>
    internal static async Task AddAsync(IReadOnlyCollection<string> doors)
    {
        if (doors.Count == 0) return;

        await WcsStateWrites.WriteAndWaitAsync(async context =>
        {
            foreach (var door in doors.Distinct())
            {
                if (context.CellsToMarkFull.Local.Any(row => row.DoorCode == door) ||
                    await context.CellsToMarkFull.AnyAsync(row => row.DoorCode == door))
                    continue;

                context.CellsToMarkFull.Add(new CellToMarkFull { DoorCode = door });
            }
        });

        lock (_lock)
            foreach (var door in doors.Where(door => !_doors.Contains(door)))
                _doors.Add(door);
    }

    /// <summary>
    /// Il gestionale ha risposto al blocco di queste porte dopo un 67 nuovo
    /// (<c>AutomationDeviceConnectionManager.LockTheRefusedCellsAsync</c>): le ha segnate piene, o ha
    /// rifiutato con un 4xx. Una porta rimasta nell'elenco da un blocco di prima, con il gestionale muto,
    /// ne esce, con la sua riga del database del WCS: la risposta c'e', e ritentarla non serve piu'. Le
    /// altre non si toccano. Una scrittura del database del WCS soltanto, niente gestionale.
    /// </summary>
    /// <remarks>
    /// Prima la porta restava nell'elenco, e il primo ritentativo chiedeva di nuovo al gestionale il segno
    /// a cui aveva gia' risposto: dopo un 4xx lo rifiutava una seconda volta, con la sua riga di avviso
    /// (revisione finale del quinto giro della voce 39).
    /// </remarks>
    internal static async Task AnsweredAsync(IReadOnlyCollection<string> doors)
    {
        List<string> listed;
        lock (_lock) listed = doors.Distinct().Where(_doors.Contains).ToList();

        foreach (var door in listed)
            await TakeOutAsync(door);
    }

    /// <summary>
    /// Rilegge l'elenco dal database del WCS, all'inizio della ricarica: il database e' quello che vale.
    /// Una lettura del database del WCS soltanto, niente gestionale.
    /// </summary>
    /// <remarks>
    /// Sostituisce l'elenco in memoria, dove <c>ReservationsOfMissionsClosedOnReload.LoadAsync</c> lo unisce:
    /// le due cose si equivalgono, perche' tutti e due scrivono la riga prima della memoria
    /// (<see cref="AddAsync"/>), e il database ha gia' ogni voce che la memoria ha.
    /// </remarks>
    internal static async Task LoadAsync()
    {
        var store = Navigator.Store;
        if (store == null) return;

        List<string> stored;
        await using (var context = store.CreateContext())
            stored = await context.CellsToMarkFull.AsNoTracking()
                .OrderBy(row => row.Id)
                .Select(row => row.DoorCode)
                .ToListAsync();

        lock (_lock) _doors = stored;
        Retries.Clear();
    }

    /// <summary>
    /// Un giro del freno del navigatore: con l'elenco non vuoto, il freno scaduto e nessun ritentativo
    /// in volo, ne avvia uno in sottofondo e torna subito.
    /// </summary>
    internal static void Retry()
    {
        lock (_lock)
        {
            if (_doors.Count == 0 || !Retried.IsCompleted || Retries.IsWaiting(TheList)) return;

            var doors = _doors.ToList();
            var generation = _generation;
            Retried = Task.Run(() => RetryAsync(doors, generation));
        }
    }

    /// <summary>
    /// Dimentica l'elenco in memoria, senza toccare il database: per le prove, che condividono lo stato
    /// statico. Un ritentativo ancora in volo non chiama piu' il gestionale e non tocca l'elenco nuovo.
    /// </summary>
    internal static void Forget()
    {
        lock (_lock)
        {
            _doors = [];
            _generation++;
            _lastFailureWritten = null;
            Retried = Task.CompletedTask;
        }

        Retries.Clear();
    }

    /// <summary>
    /// Il ritentativo puo' andare avanti: il motore e' ancora in Run, e l'elenco e' quello da cui e'
    /// partito (<see cref="Forget"/>).
    /// </summary>
    private static bool MayGoOn(int generation)
    {
        lock (_lock)
            return generation == _generation && Navigator.NavStatus == NavigatorStatusTypes.Run;
    }

    private static async Task RetryAsync(List<string> doors, int generation)
    {
        foreach (var door in doors)
        {
            // Fuori da Run nessuna chiamata al gestionale (risposta 3 del proprietario del 9/10): le porte
            // che restano stanno nell'elenco, e il primo giro in Run riparte da loro, senza freno.
            if (!MayGoOn(generation)) return;

            try
            {
                var warehouse = Navigator.Warehouse
                                ?? throw new InvalidOperationException("il gestionale non e' configurato");
                await warehouse.SetCellLockStateAsync(door, CellLockState.Full);
                Navigator.WriteLog(
                    $"La cella della porta {door}, rifiutata dal PLC con l'errore 67, e' stata segnata piena nel gestionale",
                    LogEntryType.Information);
            }
            catch (WarehouseUnavailableException refused) when (refused.IsPermanentRejection)
            {
                Navigator.WriteLog(
                    // Il codice della risposta, non il messaggio dell'eccezione: «Il gestionale non ha
                    // risposto a ...» in una riga di rifiuto diceva il contrario di quello che e' successo
                    // (revisione finale del quinto giro della voce 39), come per il blocco dopo il 67.
                    $"Il gestionale ha rifiutato il segno di cella piena della porta {door}, rifiutata dal PLC con l'errore 67 (risposta {(int)refused.StatusCode!.Value}): non si ritenta",
                    LogEntryType.Warning);
            }
            catch (WarehouseUnavailableException)
            {
                // Il gestionale non risponde ancora: il freno cresce, e il prossimo giro scaduto riprova
                // da questa cella. Nessuna riga: l'avviso l'ha scritto la prima mancata.
                Retries.RegisterFailure(TheList);
                return;
            }
            catch (Exception failure)
            {
                // Non e' il gestionale: si ritenta con il freno, ma il registro lo dice, una volta per
                // guasto uguale, con il tipo dell'eccezione, come il blocco dopo il 67.
                WriteTheFailureOnce(door, failure, generation);
                Retries.RegisterFailure(TheList);
                return;
            }

            // Segnata (o rifiutata) anche se il motore e' uscito da Run intanto: esce dall'elenco, che e'
            // una scrittura del database del WCS. Non se l'elenco e' stato dimenticato: e' di un'altra prova.
            lock (_lock)
                if (generation != _generation)
                    return;

            await TakeOutAsync(door);
        }

        lock (_lock)
            if (generation == _generation)
                _lastFailureWritten = null;

        Retries.Forget(TheList);
    }

    /// <summary>
    /// La riga di avviso di un guasto del ritentativo che non viene dal gestionale; lo stesso guasto
    /// (tipo e messaggio) del giro prima non si riscrive, finche' un giro non arriva in fondo all'elenco.
    /// </summary>
    private static void WriteTheFailureOnce(string door, Exception failure, int generation)
    {
        var what = $"{failure.GetType().Name}: {failure.Message}";

        lock (_lock)
        {
            if (generation != _generation || what == _lastFailureWritten) return;
            _lastFailureWritten = what;
        }

        Navigator.WriteLog(
            $"Il segno di cella piena della porta {door}, rifiutata dal PLC con l'errore 67, e' caduto per un guasto del WCS, non del gestionale ({what}): si ritenta con il freno",
            LogEntryType.Warning);
    }

    /// <summary>La cella esce dall'elenco e dal database del WCS.</summary>
    private static async Task TakeOutAsync(string door)
    {
        try
        {
            await WcsStateWrites.WriteAndWaitAsync(async context =>
            {
                var row = await context.CellsToMarkFull.SingleOrDefaultAsync(r => r.DoorCode == door);
                if (row != null) context.CellsToMarkFull.Remove(row);
            });
        }
        catch (Exception ex)
        {
            Navigator.WriteLog(
                $"La riga della cella da segnare piena della porta {door} non e' stata tolta dal database del WCS ({ex.Message}): alla prossima ricarica il segno si richiede",
                LogEntryType.Warning);
        }

        lock (_lock) _doors.Remove(door);
    }
}
