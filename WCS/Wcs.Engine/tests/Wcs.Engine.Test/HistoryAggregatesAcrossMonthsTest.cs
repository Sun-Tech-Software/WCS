using Wcs.Engine.Api;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Voce D82: il riepilogo delle UDC movimentate e i tempi di attraversamento su un intervallo che
/// attraversa due mesi. Il test fissa il risultato <b>prima</b> di spostare l'aggregazione nel
/// database: e' stato scritto verde sull'implementazione in memoria, e deve restare verde su
/// quella per mese.
/// </summary>
/// <remarks>
/// I casi scelti sono quelli in cui una composizione per mese sbaglia se e' fatta male: una
/// settimana ISO a cavallo del cambio di mese; una missione con l'arrivo <i>prima</i> della
/// partenza (da scartare) e uno dopo; una con due partenze (vale la prima); una con partenza a
/// settembre e arrivo a ottobre; una con la sola partenza (fuori). Due file su disco con l'orologio
/// iniettato, come <see cref="HistoryMonthsTest"/>: lo storico in memoria non ruota.
/// </remarks>
public sealed class HistoryAggregatesAcrossMonthsTest : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wcs-history-aggregates-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 9, 10, 10, 0, 0);
    private readonly WcsHistoryDatabase _history;

    private static readonly Guid AcrossMonths = Guid.NewGuid();
    private static readonly Guid ArrivalBeforeDeparture = Guid.NewGuid();
    private static readonly Guid TwoDepartures = Guid.NewGuid();
    private static readonly Guid DepartureOnly = Guid.NewGuid();
    private static readonly Guid LateArrivalNextMonth = Guid.NewGuid();

    public HistoryAggregatesAcrossMonthsTest()
    {
        _history = WcsHistoryDatabase.Open(_directory, monthsToKeep: 3, clock: () => _now);
        using (var september = _history.CreateContext())
        {
            // Settimana ISO 2026-09-07 (lunedi'): una riga il 10. Settimana 2026-09-28: due il 29.
            september.HandledLoadUnits.Add(Handled(1, new DateTime(2026, 9, 10, 8, 0, 0)));
            september.HandledLoadUnits.Add(Handled(1, new DateTime(2026, 9, 29, 8, 0, 0)));
            september.HandledLoadUnits.Add(Handled(1, new DateTime(2026, 9, 29, 9, 0, 0)));

            // Partenza a settembre, arrivo a ottobre: due ore.
            september.NavigatorLog.Add(Log(AcrossMonths, "0100", new DateTime(2026, 9, 30, 23, 0, 0)));
            // Un arrivo prima della partenza non conta; conta quello dopo.
            september.NavigatorLog.Add(Log(ArrivalBeforeDeparture, "0200", new DateTime(2026, 9, 29, 10, 0, 0)));
            september.NavigatorLog.Add(Log(ArrivalBeforeDeparture, "0100", new DateTime(2026, 9, 29, 11, 0, 0)));
            september.NavigatorLog.Add(Log(ArrivalBeforeDeparture, "0200", new DateTime(2026, 9, 29, 11, 30, 0)));
            // Solo la partenza: fuori dal risultato.
            september.NavigatorLog.Add(Log(DepartureOnly, "0100", new DateTime(2026, 9, 20, 8, 0, 0)));
            // Partenza a settembre con un arrivo precedente nello stesso mese, e l'arrivo buono a ottobre.
            september.NavigatorLog.Add(Log(LateArrivalNextMonth, "0200", new DateTime(2026, 9, 25, 7, 0, 0)));
            september.NavigatorLog.Add(Log(LateArrivalNextMonth, "0100", new DateTime(2026, 9, 25, 8, 0, 0)));
            // Una riga senza missione e una su un ASI estraneo: il filtro le scarta.
            september.NavigatorLog.Add(Log(null, "0100", new DateTime(2026, 9, 25, 8, 30, 0)));
            september.NavigatorLog.Add(Log(AcrossMonths, "0900", new DateTime(2026, 9, 30, 23, 30, 0)));
            september.SaveChanges();
        }
        _now = new DateTime(2026, 10, 5, 10, 0, 0);
        using (var october = _history.CreateContext())
        {
            // Ancora settimana 2026-09-28: il 2 ottobre, un tipo 1 e un tipo 2.
            october.HandledLoadUnits.Add(Handled(1, new DateTime(2026, 10, 2, 8, 0, 0)));
            october.HandledLoadUnits.Add(Handled(2, new DateTime(2026, 10, 2, 9, 0, 0)));

            october.NavigatorLog.Add(Log(AcrossMonths, "0200", new DateTime(2026, 10, 1, 1, 0, 0)));
            // Due partenze: vale la prima (08:00), e l'arrivo delle 08:30 sta in mezzo.
            october.NavigatorLog.Add(Log(TwoDepartures, "0100", new DateTime(2026, 10, 3, 9, 0, 0)));
            october.NavigatorLog.Add(Log(TwoDepartures, "0100", new DateTime(2026, 10, 3, 8, 0, 0)));
            october.NavigatorLog.Add(Log(TwoDepartures, "0200", new DateTime(2026, 10, 3, 8, 30, 0)));
            october.NavigatorLog.Add(Log(LateArrivalNextMonth, "0200", new DateTime(2026, 10, 4, 6, 0, 0)));
            october.SaveChanges();
        }
        Navigator.History = _history;
    }

    public void Dispose()
    {
        Navigator.History = null;
        _history.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static readonly DateTime From = new(2026, 9, 1);
    private static readonly DateTime To = new(2026, 10, 31);

    [Fact]
    public async Task TheWeeklySummaryJoinsTheTwoHalvesOfAWeekAcrossTheMonthBoundary()
    {
        var byWeek = await HistoryQueries.HandledLoadUnitSummaryAsync(From, To, SummaryPeriod.Week);

        Assert.Equal(
        [
            (new DateOnly(2026, 9, 7), (short)1, 1),
            (new DateOnly(2026, 9, 28), (short)1, 3),
            (new DateOnly(2026, 9, 28), (short)2, 1)
        ], byWeek.Select(r => (r.Period, r.Type, r.Count)).ToList());
    }

    [Fact]
    public async Task TheDailyMonthlyAndYearlySummariesCountTheSameRows()
    {
        var byDay = await HistoryQueries.HandledLoadUnitSummaryAsync(From, To, SummaryPeriod.Day);
        Assert.Equal(
        [
            (new DateOnly(2026, 9, 10), (short)1, 1),
            (new DateOnly(2026, 9, 29), (short)1, 2),
            (new DateOnly(2026, 10, 2), (short)1, 1),
            (new DateOnly(2026, 10, 2), (short)2, 1)
        ], byDay.Select(r => (r.Period, r.Type, r.Count)).ToList());

        var byMonth = await HistoryQueries.HandledLoadUnitSummaryAsync(From, To, SummaryPeriod.Month);
        Assert.Equal(
        [
            (new DateOnly(2026, 9, 1), (short)1, 3),
            (new DateOnly(2026, 10, 1), (short)1, 1),
            (new DateOnly(2026, 10, 1), (short)2, 1)
        ], byMonth.Select(r => (r.Period, r.Type, r.Count)).ToList());

        var byYear = await HistoryQueries.HandledLoadUnitSummaryAsync(From, To, SummaryPeriod.Year);
        Assert.Equal(
        [
            (new DateOnly(2026, 1, 1), (short)1, 4),
            (new DateOnly(2026, 1, 1), (short)2, 1)
        ], byYear.Select(r => (r.Period, r.Type, r.Count)).ToList());
    }

    /// <summary>L'intervallo che copre solo ottobre non vede le righe di settembre della stessa settimana.</summary>
    [Fact]
    public async Task TheSummaryRespectsTheIntervalInsideAMonth()
    {
        var byWeek = await HistoryQueries.HandledLoadUnitSummaryAsync(new DateTime(2026, 10, 1), To, SummaryPeriod.Week);
        Assert.Equal(
        [
            (new DateOnly(2026, 9, 28), (short)1, 1),
            (new DateOnly(2026, 9, 28), (short)2, 1)
        ], byWeek.Select(r => (r.Period, r.Type, r.Count)).ToList());
    }

    [Fact]
    public async Task TransitTimesPairTheFirstDepartureWithTheFirstArrivalAfterItAcrossMonths()
    {
        var transit = await HistoryQueries.TransitTimesAsync(From, To, "0100", "0200");

        // In ordine di partenza.
        Assert.Equal(
        [
            (LateArrivalNextMonth, new DateTime(2026, 9, 25, 8, 0, 0), new DateTime(2026, 10, 4, 6, 0, 0), 8 * 86400 + 22 * 3600),
            (ArrivalBeforeDeparture, new DateTime(2026, 9, 29, 11, 0, 0), new DateTime(2026, 9, 29, 11, 30, 0), 1800),
            (AcrossMonths, new DateTime(2026, 9, 30, 23, 0, 0), new DateTime(2026, 10, 1, 1, 0, 0), 7200),
            (TwoDepartures, new DateTime(2026, 10, 3, 8, 0, 0), new DateTime(2026, 10, 3, 8, 30, 0), 1800)
        ], transit.Select(t => (t.MissionId, t.Start, t.End, t.Seconds)).ToList());
        Assert.All(transit, t => Assert.Equal(("0100", "0200"), (t.FromAsi, t.ToAsi)));
    }

    /// <summary>I pattern seguono LIKE: '01%' prende la partenza, e un intervallo che parte a ottobre lascia fuori la partenza di settembre.</summary>
    [Fact]
    public async Task TransitTimesFollowLikePatternsAndTheInterval()
    {
        var transit = await HistoryQueries.TransitTimesAsync(new DateTime(2026, 10, 1), To, "01%", "02%");

        Assert.Equal([TwoDepartures], transit.Select(t => t.MissionId).ToList());
    }

    private static HandledLoadUnit Handled(short type, DateTime date) =>
        new() { MissionId = Guid.NewGuid(), Type = type, Date = date };

    private static NavigatorLogEntry Log(Guid? mission, string asi, DateTime date) =>
        new() { MissionId = mission, Asi = asi, Date = date, Status = 1, Description = asi };
}
