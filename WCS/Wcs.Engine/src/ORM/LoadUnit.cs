using Wcs.Common;
using Wcs.Engine.Persistence;

namespace Wcs.Engine.ORM;

/// <summary>
/// Le UDC movimentate sullo storico del WCS: la riga di <c>HandledLoadUnits</c> nel file del mese,
/// attraverso <see cref="HistoryStore"/>.
/// </summary>
/// <remarks>
/// Prima erano T-SQL su <c>LU_HANDLED</c>, con una chiave tecnica che l'inserimento rileggeva e
/// che ogni setter aspettava. Ora la chiave logica e' la missione: la lettura e' sincrona sullo
/// storico, perche' avviene nel costruttore, e le scritture vanno allo scrittore dello storico e
/// tornano subito. Una missione a cavallo del cambio di mese ha la riga nel mese in cui e'
/// partita e, se cambia ancora, una nel mese nuovo: e' l'imprecisione di uno storico mensile.
/// Senza storico non c'e' nulla da leggere ne' da scrivere.
/// </remarks>
internal static class LoadUnit
{
    #region Internal Methods

    /// <summary>Copia nella UDC la riga della sua missione, se c'e' nel mese corrente.</summary>
    /// <remarks>
    /// Non guarda <c>RecorevingDataFromDb</c>: e' il costruttore ad alzarlo prima di chiamare qui,
    /// e serve a <see cref="Update"/> per non riscrivere quello che si sta copiando. La versione
    /// T-SQL lo guardava, e per questo non ha mai riletto una riga.
    /// </remarks>
    internal static void Get(Items.LoadUnit value)
    {
        if (Navigator.NavStatus == Enums.NavigatorStatusTypes.Recover)
            return;

        var history = Navigator.History;
        if (history == null) return;

        using var context = history.CreateContext();
        var row = HistoryStore.FindByMissionAsync(context, value.MissionId).GetAwaiter().GetResult();
        if (row != null) HistoryStore.Apply(row, value);
    }

    internal static void Remove(Items.LoadUnit value)
    {
        if (Navigator.NavStatus == Enums.NavigatorStatusTypes.Recover || value.RecorevingDataFromDb)
            return;

        var missionId = value.MissionId;
        Write(context => HistoryStore.DeleteByMissionAsync(context, missionId));
    }

    internal static void Update(Items.LoadUnit value)
    {
        if (Navigator.NavStatus == Enums.NavigatorStatusTypes.Recover || value.RecorevingDataFromDb)
            return;

        var snapshot = HistoryStore.Snapshot(value);
        Write(context => HistoryStore.UpsertByMissionAsync(context, snapshot));
    }

    #endregion Internal Methods

    #region Private Methods

    private static void Write(HistoryWrite write)
    {
        if (Navigator.HistoryWriter is { } writer && writer.Enqueue(write))
            return;

        var history = Navigator.History;
        if (history == null) return;

        using var context = history.CreateContext();
        write(context).GetAwaiter().GetResult();
        context.SaveChanges();
    }

    #endregion Private Methods
}
