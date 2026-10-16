using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Api;

public sealed record HandledLoadUnitSummaryRow(DateOnly Period, short Type, int Count);
public sealed record TransitTimeRow(Guid MissionId, string FromAsi, DateTime Start, string ToAsi, DateTime End, int Seconds);

/// <summary>
/// Lo storico interrogato qui sono i file dei mesi in linea
/// (<see cref="WcsHistoryDatabase.MonthsAvailable"/>): un intervallo puo' attraversare piu' mesi e
/// i risultati si concatenano; un intervallo che comincia prima del primo mese presente e'
/// rifiutato con <see cref="HistoryRangeNotAvailableException"/>, invece di rispondere una lista
/// vuota.
/// </summary>
public static class HistoryQueries
{
    public static async Task<List<HandledLoadUnit>> HandledLoadUnitsAsync(DateTime from, DateTime to, short? type,
        decimal? loadUnitId, decimal? processId, int take)
    {
        var (start, end) = Window(from, to);
        var rows = await StoreAccess.ReadHistoryMonthsAsync(start, end, c =>
        {
            var query = c.HandledLoadUnits.AsNoTracking().Where(x => x.Date >= start && x.Date < end);
            if (type != null) query = query.Where(x => x.Type == type);
            if (loadUnitId != null) query = query.Where(x => x.LoadUnitId == loadUnitId);
            if (processId != null) query = query.Where(x => x.ProcessId == processId);
            // Il taglio si applica gia' qui, per mese: una riga che sta nella graduatoria dei
            // primi N sull'intervallo intero sta anche nella graduatoria dei primi N del proprio
            // file (e' un sottoinsieme piu' piccolo, mai piu' vecchio), quindi il risultato dopo
            // la concatenazione non cambia; ma la memoria resta limitata a mesi coinvolti * take,
            // invece di tutte le righe dell'intervallo.
            return query.OrderByDescending(x => x.Date).Take(Math.Clamp(take, 1, 5000)).ToListAsync();
        });
        // Il taglio si riapplica sull'intervallo intero, non su un mese: si ordina di nuovo dopo
        // la concatenazione.
        return rows.OrderByDescending(x => x.Date).Take(Math.Clamp(take, 1, 5000)).ToList();
    }

    /// <summary>
    /// Il conteggio delle UDC movimentate per periodo e tipo. Il lavoro sta nel database di ogni
    /// mese: un <c>COUNT</c> per giorno e tipo, e i giorni si ripiegano nel periodo chiesto qui
    /// (voce D82). Prima ogni riga dell'intervallo veniva portata in memoria e raggruppata dopo:
    /// su un anno di storico era un anno di righe nel processo dell'host. I parziali per giorno
    /// sono al piu' 31 per tipo per mese, e si compongono esattamente in giorni, settimane ISO,
    /// mesi e anni: il risultato e' lo stesso.
    /// </summary>
    public static async Task<List<HandledLoadUnitSummaryRow>> HandledLoadUnitSummaryAsync(DateTime from, DateTime to, SummaryPeriod period)
    {
        var (start, end) = Window(from, to);
        var days = await StoreAccess.ReadHistoryMonthsAsync(start, end, c => c.HandledLoadUnits.AsNoTracking()
            .Where(x => x.Date >= start && x.Date < end)
            .GroupBy(x => new { Day = x.Date.Date, x.Type })
            .Select(g => new { g.Key.Day, g.Key.Type, Count = g.Count() })
            .ToListAsync());
        return days.GroupBy(x => (Period: PeriodStart(DateOnly.FromDateTime(x.Day), period), x.Type))
            .Select(g => new HandledLoadUnitSummaryRow(g.Key.Period, g.Key.Type, g.Sum(x => x.Count)))
            .OrderBy(x => x.Period).ThenBy(x => x.Type).ToList();
    }

    /// <summary>Il primo giorno del periodo che contiene la data: la settimana comincia di lunedì (ISO).</summary>
    internal static DateOnly PeriodStart(DateOnly day, SummaryPeriod period) => period switch
    {
        SummaryPeriod.Week => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)),
        SummaryPeriod.Month => new DateOnly(day.Year, day.Month, 1),
        SummaryPeriod.Year => new DateOnly(day.Year, 1, 1),
        _ => day
    };

    /// <summary>
    /// Il primo mese di storico ancora in linea, o nullo se lo storico non e' aperto (motore non
    /// configurato). E' quello che <c>GET /status</c> riporta in <c>HistoryFrom</c>: <see cref="Navigator.History"/>
    /// resta interno al motore, questo e' il modo pubblico con cui l'host lo legge.
    /// </summary>
    public static DateOnly? FirstMonthAvailable() =>
        Navigator.History is { } h && h.MonthsAvailable() is [var first, ..] ? new DateOnly(first.Year, first.Month, 1) : null;

    /// <summary>
    /// Esiste una UDC movimentata con uno di questi riferimenti? Non c'e' un intervallo: si guardano
    /// i mesi in linea dal piu' recente e si risponde al primo che la trova.
    /// </summary>
    public static async Task<bool> HandledLoadUnitExistsAsync(decimal? processId, decimal? loadUnitProcessId, decimal? noteHeaderId)
    {
        var history = Navigator.History
                      ?? throw new EngineStoreUnavailableException("Lo storico del WCS non e' aperto: la lettura non e' possibile.");
        foreach (var (year, month) in history.MonthsAvailable().Reverse())
        {
            await using var context = history.CreateReadContext(year, month);
            if (await context.HandledLoadUnits.AsNoTracking().AnyAsync(x =>
                    (processId != null && x.ProcessId == processId) ||
                    (loadUnitProcessId != null && x.LoadUnitProcessId == loadUnitProcessId) ||
                    (noteHeaderId != null && x.NoteHeaderId == noteHeaderId)))
                return true;
        }
        return false;
    }

    public static async Task<List<NavigatorLogEntry>> LogAsync(DateTime from, DateTime to, Guid? itemId, Guid? missionId, int take)
    {
        var (start, end) = Window(from, to);
        var rows = await StoreAccess.ReadHistoryMonthsAsync(start, end, c =>
        {
            var query = c.NavigatorLog.AsNoTracking().Where(x => x.Date >= start && x.Date < end);
            if (itemId != null) query = query.Where(x => x.ItemId == itemId);
            if (missionId != null) query = query.Where(x => x.MissionId == missionId);
            // Il taglio si applica gia' qui, per mese, come in HandledLoadUnitsAsync: il
            // risultato dopo la concatenazione non cambia (i primi N sull'intervallo sono un
            // sottoinsieme dei primi N di ogni singolo file), ma la memoria resta limitata a
            // mesi coinvolti * take.
            return query.OrderByDescending(x => x.Date).Take(Math.Clamp(take, 1, 5000)).ToListAsync();
        });
        // Il taglio si riapplica sull'intervallo intero, non su un mese: si ordina di nuovo dopo
        // la concatenazione.
        return rows.OrderByDescending(x => x.Date).Take(Math.Clamp(take, 1, 5000)).ToList();
    }

    /// <summary>I tempi di attraversamento (ex <c>Sp_GetAttraversamentoMissioni</c>, cancellata con 3c-2): per ogni missione, la prima riga sull'ASI di partenza e la prima riga successiva sull'ASI di arrivo. I pattern seguono LIKE: '01%' vale per tutti gli ASI che iniziano con 01.</summary>
    /// <remarks>
    /// Voce D82. Prima ogni riga del registro con missione e ASI somigliante a uno dei due pattern
    /// veniva portata in memoria, ordinata e accoppiata: su un anno di storico era un anno di
    /// righe. Ora a ogni file di mese si chiedono tre aggregati per missione — la prima partenza,
    /// il primo arrivo dopo quella partenza nello stesso mese, il primo arrivo del mese — in una sola
    /// query che legge le righe del mese una volta (voce 56, O3: erano tre query, quattro volte piu'
    /// lente su 200.000 righe), e si
    /// compongono qui: la partenza e' quella del primo mese che ne ha una (i mesi sono in ordine,
    /// e ogni riga di un mese e' dopo ogni riga del precedente); l'arrivo e' quello «dopo la
    /// partenza» nel mese della partenza, altrimenti il primo arrivo del primo mese successivo,
    /// che e' per forza dopo. E' la stessa coppia dell'ordinamento in memoria, a cavallo di mese
    /// compreso; la memoria e' limitata dalle missioni, non dalle righe.
    /// </remarks>
    public static async Task<List<TransitTimeRow>> TransitTimesAsync(DateTime from, DateTime to, string fromAsiPattern, string toAsiPattern)
    {
        var (start, end) = Window(from, to);
        var months = await StoreAccess.ReadHistoryMonthsAsync<TransitPartials>(start, end, async c =>
        {
            // c: the rows of the month on either ASI, read once; d: the first departure of each
            // mission. A row whose ASI matches both patterns counts as a departure and as an
            // arrival, as with the three separate queries. Ties on the date go to the first written.
            var points = await c.Database.SqlQuery<TransitPoint>($"""
                WITH c AS MATERIALIZED (
                  SELECT Id, MissionId, Asi, Date, (Asi LIKE {fromAsiPattern}) AS IsDeparture, (Asi LIKE {toAsiPattern}) AS IsArrival
                  FROM NavigatorLog
                  WHERE Date >= {start} AND Date < {end} AND MissionId IS NOT NULL AND Asi IS NOT NULL
                    AND (Asi LIKE {fromAsiPattern} OR Asi LIKE {toAsiPattern})),
                d AS (SELECT MissionId, MIN(Date) AS DepartureDate FROM c WHERE IsDeparture GROUP BY MissionId)
                SELECT Kind, MissionId, Asi, Date FROM (
                  SELECT 0 AS Kind, MissionId, Asi, Date, ROW_NUMBER() OVER (PARTITION BY MissionId ORDER BY Date, Id) AS Position
                  FROM c WHERE IsDeparture
                  UNION ALL
                  SELECT 1, c.MissionId, c.Asi, c.Date, ROW_NUMBER() OVER (PARTITION BY c.MissionId ORDER BY c.Date, c.Id)
                  FROM c JOIN d ON d.MissionId = c.MissionId WHERE c.IsArrival AND c.Date > d.DepartureDate
                  UNION ALL
                  SELECT 2, MissionId, Asi, Date, ROW_NUMBER() OVER (PARTITION BY MissionId ORDER BY Date, Id)
                  FROM c WHERE IsArrival)
                WHERE Position = 1
                """).ToListAsync();

            Dictionary<Guid, LogPoint> Of(int kind) =>
                points.Where(p => p.Kind == kind).ToDictionary(p => p.MissionId, p => new LogPoint(p.Asi, p.Date));
            return [new TransitPartials(Of(0), Of(1), Of(2))];
        });

        var result = new List<TransitTimeRow>();
        var seen = new HashSet<Guid>();
        for (var i = 0; i < months.Count; i++)
        {
            foreach (var (missionId, departure) in months[i].FirstDepartures)
            {
                // La prima partenza sta nel primo mese che ne ha una.
                if (!seen.Add(missionId)) continue;

                LogPoint? arrival = null;
                if (months[i].ArrivalsAfterDeparture.TryGetValue(missionId, out var sameMonth))
                    arrival = sameMonth;
                for (var j = i + 1; arrival == null && j < months.Count; j++)
                    if (months[j].FirstArrivals.TryGetValue(missionId, out var later))
                        arrival = later;
                if (arrival == null) continue;

                result.Add(new TransitTimeRow(missionId, departure.Asi, departure.Date, arrival.Asi, arrival.Date,
                    (int)(arrival.Date - departure.Date).TotalSeconds));
            }
        }
        return result.OrderBy(x => x.Start).ToList();
    }

    /// <summary>A row of the query of <see cref="TransitTimesAsync"/>: 0 first departure, 1 first arrival after it, 2 first arrival.</summary>
    private sealed record TransitPoint(int Kind, Guid MissionId, string Asi, DateTime Date);

    /// <summary>Una riga del registro ridotta a quello che l'attraversamento usa.</summary>
    private sealed record LogPoint(string Asi, DateTime Date);

    /// <summary>I tre aggregati per missione di un file di mese (si veda <see cref="TransitTimesAsync"/>).</summary>
    private sealed record TransitPartials(
        Dictionary<Guid, LogPoint> FirstDepartures,
        Dictionary<Guid, LogPoint> ArrivalsAfterDeparture,
        Dictionary<Guid, LogPoint> FirstArrivals);

    /// <summary>
    /// I messaggi scambiati con i PLC nell'intervallo, dal piu' recente. Stessa forma della
    /// <see cref="LogAsync"/>: il taglio si applica per mese e si riapplica sull'intervallo.
    /// </summary>
    /// <remarks>
    /// E' la lettura che <c>FrmPcmLog</c> del Client faceva con una <c>SELECT</c> su
    /// <c>PCM_LOG</c> del gestionale, tabella che nessuno scrive piu': quando il Client passera'
    /// dall'host, verra' qui.
    /// <para>
    /// Dalla voce <c>D183</c> le righe non stanno piu' nei file dello storico ma nei file del
    /// registro dei messaggi dei PLC. La rotta resta <c>GET /api/v1/history/plc-messages</c> e il
    /// metodo resta in questa classe: e' la stessa domanda, cambia solo dove sta il dato, e
    /// spostare la rotta romperebbe un contratto gia' pubblicato per un dettaglio di collocazione.
    /// </para>
    /// </remarks>
    public static async Task<List<PlcMessage>> PlcMessagesAsync(DateTime from, DateTime to, int take)
    {
        var (start, end) = Window(from, to);
        var rows = await StoreAccess.ReadPlcLogMonthsAsync(start, end, c =>
            c.PlcMessages.AsNoTracking().Where(x => x.Date >= start && x.Date < end)
                .OrderByDescending(x => x.Date).Take(Math.Clamp(take, 1, 5000)).ToListAsync());
        return rows.OrderByDescending(x => x.Date).Take(Math.Clamp(take, 1, 5000)).ToList();
    }

    public static async Task AppendLogAsync(NavigatorLogEntry entry)
    {
        entry.Id = 0;
        var dateProvided = entry.Date != default;
        // La data e' il momento dell'evento: chi non la porta prende adesso, chi la porta in UTC
        // la vede riportata a locale come le colonne di data del WCS (StoreAccess.Local).
        entry.Date = dateProvided ? StoreAccess.Local(entry.Date) : DateTime.Now;
        if (dateProvided)
        {
            var now = DateTime.Now;
            var monthStart = new DateTime(now.Year, now.Month, 1);
            if (entry.Date < monthStart || entry.Date >= monthStart.AddMonths(1))
                throw new HistoryRangeNotAvailableException(
                    "lo storico via API copre il mese corrente; Date, se indicata, deve cadere nel mese corrente");
        }
        if (Navigator.HistoryWriter is { } writer && writer.Enqueue(context => { context.NavigatorLog.Add(entry); return Task.CompletedTask; }))
            return;

        var history = Navigator.History
                      ?? throw new EngineStoreUnavailableException("Lo storico del WCS non e' aperto: la riga di registro non e' stata scritta.");
        await using var direct = history.CreateContext();
        direct.NavigatorLog.Add(entry);
        await direct.SaveChangesAsync();
    }

    /// <summary>
    /// L'intervallo normalizzato all'ora locale. Che l'intervallo cada nei mesi in linea lo
    /// verifica <see cref="StoreAccess.ReadHistoryMonthsAsync"/>, che conosce i file presenti.
    /// </summary>
    private static (DateTime From, DateTime To) Window(DateTime from, DateTime to) =>
        (StoreAccess.Local(from), StoreAccess.Local(to));
}
