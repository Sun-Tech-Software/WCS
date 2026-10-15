using Wcs.Engine.Persistence;

namespace Wcs.Engine.ORM;

/// <summary>
/// Le letture e le scritture della giunzione sul database del WCS: la riga di
/// <c>PlantJunctions</c>, attraverso <see cref="PlantStore"/>.
/// </summary>
/// <remarks>
/// Prima erano T-SQL su <c>NAVIGATOR_JUNCTIONS</c>. La lettura e' sincrona sullo store, perche'
/// serve quando la giunzione nasce; le scritture passano dallo scrittore di stato.
/// </remarks>
internal static class AutomationJunction
{
    #region Public Methods

    /// <summary>
    /// Copia nella giunzione la riga che ha la stessa coppia sorgente-destinazione, oppure, se la
    /// coppia manca, la stessa chiave. Restituisce false se non c'e' o non c'e' lo store.
    /// </summary>
    /// <remarks>
    /// Prima la lettura rifiutava anche una giunzione senza chiave: una giunzione nata dalla
    /// coppia sorgente-destinazione non trovava mai la propria riga, si dava una chiave nuova e
    /// l'aggiornamento per coppia lasciava sul database quella vecchia. Ora la chiave in memoria e'
    /// quella della riga.
    /// </remarks>
    public static bool Get(Items.AutomationJunction value)
    {
        var byEndpoints = value.SourceId != Guid.Empty && value.DestinationId != Guid.Empty;
        if (!byEndpoints && value.ItemGuid == Guid.Empty)
            return false;

        var store = Navigator.Store;
        if (store == null) return false;

        using var context = store.CreateContext();
        var row = byEndpoints
            ? PlantStore.FindJunctionAsync(context, value.SourceId, value.DestinationId).GetAwaiter().GetResult()
            : PlantStore.FindJunctionAsync(context, value.ItemGuid).GetAwaiter().GetResult();
        if (row == null) return false;

        PlantStore.Apply(row, value);
        return true;
    }

    #endregion Public Methods

    #region Internal Methods

    internal static void Delete(Items.AutomationJunction value)
    {
        if (value.ItemGuid == Guid.Empty) return;
        var junctionId = value.ItemGuid;
        Write(context => PlantStore.DeleteJunctionAsync(context, junctionId));
    }

    internal static void Update(Items.AutomationJunction value)
    {
        var snapshot = PlantStore.Snapshot(value);
        Write(context => PlantStore.UpsertJunctionAsync(context, snapshot));
    }

    #endregion Internal Methods

    #region Private Methods

    private static void Write(WcsWrite write)
    {
        if (Navigator.StateWriter is { } writer && writer.EnqueueOrWait(write))
            return;

        var store = Navigator.Store;
        if (store == null)
            return;

        using var context = store.CreateContext();
        write(context).GetAwaiter().GetResult();
        context.SaveChanges();
    }

    #endregion Private Methods
}
