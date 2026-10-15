using Wcs.Engine.Persistence;

namespace Wcs.Engine.Api;

/// <summary>Letture e scritture delle facciate: una lettura apre un contesto sullo store, una scrittura passa dallo scrittore di stato e viene attesa.</summary>
internal static class StoreAccess
{
    public static async Task<T> ReadAsync<T>(Func<WcsDbContext, Task<T>> read)
    {
        var store = Navigator.Store
                    ?? throw new EngineStoreUnavailableException("Il database del WCS non e' aperto: la lettura non e' possibile.");
        await using var context = store.CreateContext();
        return await read(context);
    }

    public static Task WriteAsync(WcsWrite write)
    {
        if (Navigator.StateWriter == null && Navigator.Store == null)
            throw new EngineStoreUnavailableException("Il database del WCS non e' aperto: la scrittura non e' possibile.");
        return WcsStateWrites.WriteAndWaitAsync(write);
    }

    public static async Task<T> ReadHistoryAsync<T>(Func<WcsHistoryDbContext, Task<T>> read)
    {
        var history = Navigator.History
                      ?? throw new EngineStoreUnavailableException("Lo storico del WCS non e' aperto: la lettura non e' possibile.");
        await using var context = history.CreateContext();
        return await read(context);
    }

    /// <summary>
    /// Una lettura dello storico su un intervallo che puo' attraversare piu' mesi: ogni file
    /// coinvolto viene interrogato con la stessa query e i risultati concatenati dal mese piu'
    /// vecchio. Un intervallo che parte prima del primo mese presente e' rifiutato con
    /// <see cref="HistoryRangeNotAvailableException"/>, che dice qual e' il primo mese.
    /// </summary>
    public static async Task<List<T>> ReadHistoryMonthsAsync<T>(DateTime start, DateTime end,
        Func<WcsHistoryDbContext, Task<List<T>>> read)
    {
        var history = Navigator.History
                      ?? throw new EngineStoreUnavailableException("Lo storico del WCS non e' aperto: la lettura non e' possibile.");
        var months = history.MonthsAvailable();
        if (months.Count == 0)
            throw new HistoryRangeNotAvailableException("lo storico non ha nessun mese in linea");
        var oldest = months[0];
        if (start < new DateTime(oldest.Year, oldest.Month, 1))
            throw new HistoryRangeNotAvailableException(
                $"lo storico in linea comincia da {oldest.Year:0000}-{oldest.Month:00}: l'intervallo non puo' partire prima");

        var result = new List<T>();
        foreach (var (year, month) in months)
        {
            var monthStart = new DateTime(year, month, 1);
            if (monthStart >= end || monthStart.AddMonths(1) <= start) continue;
            await using var context = history.CreateReadContext(year, month);
            result.AddRange(await read(context));
        }
        return result;
    }

    /// <summary>
    /// Una lettura del registro dei messaggi dei PLC su un intervallo che puo' attraversare piu'
    /// mesi: come <see cref="ReadHistoryMonthsAsync{T}"/>, sul database dei PLC.
    /// </summary>
    /// <remarks>
    /// I file mensili sono due serie separate (voce <c>D183</c>): quelli del registro dei messaggi
    /// vivono meno — la potatura e' a sette giorni e la rotazione ne tiene due mesi — quindi
    /// l'intervallo ammesso qui puo' essere piu' stretto di quello dello storico. Chiedere un mese
    /// che non c'e' piu' e' rifiutato con <see cref="HistoryRangeNotAvailableException"/>, che dice
    /// da quando si puo' partire, invece di rispondere una lista vuota.
    /// </remarks>
    public static async Task<List<T>> ReadPlcLogMonthsAsync<T>(DateTime start, DateTime end,
        Func<WcsPlcLogDbContext, Task<List<T>>> read)
    {
        var plcLog = Navigator.PlcLog
                     ?? throw new EngineStoreUnavailableException("Il registro dei messaggi dei PLC non e' aperto: la lettura non e' possibile.");
        var months = plcLog.MonthsAvailable();
        if (months.Count == 0)
            throw new HistoryRangeNotAvailableException("il registro dei messaggi dei PLC non ha nessun mese in linea");
        var oldest = months[0];
        if (start < new DateTime(oldest.Year, oldest.Month, 1))
            throw new HistoryRangeNotAvailableException(
                $"il registro dei messaggi dei PLC in linea comincia da {oldest.Year:0000}-{oldest.Month:00}: l'intervallo non puo' partire prima");

        var result = new List<T>();
        foreach (var (year, month) in months)
        {
            var monthStart = new DateTime(year, month, 1);
            if (monthStart >= end || monthStart.AddMonths(1) <= start) continue;
            await using var context = plcLog.CreateReadContext(year, month);
            result.AddRange(await read(context));
        }
        return result;
    }

    /// <summary>
    /// La stessa data, riportata all'ora locale se e' marcata UTC.
    /// </summary>
    /// <remarks>
    /// Le colonne di data del WCS sono scritte con <c>DateTime.Now</c>, cioe' ora locale senza
    /// offset. Un chiamante che formatta con <c>"O"</c> porta con se' l'offset, e il binder di
    /// ASP.NET Core consegna la data in <see cref="DateTimeKind.Utc"/>: confrontarla cosi'
    /// sfaserebbe la finestra dell'offset e le finestre strette non troverebbero piu' nulla. La
    /// normalizzazione sta qui, all'ingresso delle facciate, perche' il difetto ha la stessa
    /// forma per ogni chiamante — HTTP o in-process.
    /// </remarks>
    public static DateTime Local(DateTime value) => value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;

    /// <inheritdoc cref="Local(DateTime)"/>
    public static DateTime? Local(DateTime? value) => value is { } present ? Local(present) : null;
}
