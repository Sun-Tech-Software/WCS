using Wcs.Engine.Persistence;

namespace Wcs.Engine.ORM;

/// <summary>
/// Le letture e le scritture del dispositivo sul database del WCS: la riga di <c>PlantItems</c>,
/// attraverso <see cref="PlantStore"/>.
/// </summary>
/// <remarks>
/// Prima erano T-SQL su <c>NAVIGATOR_ITEMS</c>: una lettura per colonna all'avvio, e la riga
/// intera riscritta a ogni setter dal thread che passava. Ora la lettura e' sincrona sullo store
/// — serve all'avvio, quando il dispositivo nasce — e le scritture passano dallo scrittore di
/// stato. Senza store non c'e' nulla da leggere ne' da scrivere.
/// </remarks>
internal static class AutomationDevice
{
    #region Public Methods

    /// <summary>
    /// Copia la riga nel dispositivo. Restituisce false se non c'e' una riga, o non c'e' lo store.
    /// </summary>
    public static bool Get(Items.AutomationDevice value)
    {
        var store = Navigator.Store;
        if (store == null) return false;

        using var context = store.CreateContext();
        var row = PlantStore.FindItemAsync(context, value.ItemGuid).GetAwaiter().GetResult();
        if (row == null) return false;

        PlantStore.Apply(row, value);
        return true;
    }

    #endregion Public Methods

    #region Internal Methods

    /// <summary>
    /// Toglie il dispositivo e le giunzioni che lo toccano. Le sue righe del registro del
    /// navigatore restano: lo storico e' in sola aggiunta, e un dispositivo tolto dal diagramma
    /// non cancella quello che ha fatto (prima la cancellazione toglieva anche il log).
    /// </summary>
    internal static void Delete(Guid itemGuid)
    {
        Write(context => PlantStore.DeleteItemAsync(context, itemGuid));
    }

    /// <summary>
    /// Scrive la riga del dispositivo, solo se c'e'. Una riga che non c'e' piu' non si reinserisce.
    /// </summary>
    /// <remarks>
    /// Era un inserimento-o-aggiornamento: una scrittura di stato rimasta in coda, o in volo in un
    /// <c>Task.Run</c>, per un dispositivo tolto nel frattempo reinseriva la riga, e il dispositivo
    /// rinasceva al ricaricamento successivo (coda, voce 42). La scrittura scartata lascia una riga
    /// Warning: in silenzio si nasconderebbe proprio questo genere di difetto. La creazione voluta
    /// passa da <see cref="Create"/>.
    /// </remarks>
    internal static void Update(Items.AutomationDevice value)
    {
        var snapshot = PlantStore.Snapshot(value);
        Write(async context =>
        {
            if (!await PlantStore.UpdateItemAsync(context, snapshot))
                Navigator.WriteLog(
                    $"Scrittura della riga del dispositivo '{snapshot.Name}' ({snapshot.ItemId}) scartata: " +
                    "la riga non c'e' piu', il dispositivo e' stato tolto dall'impianto",
                    Log.Interface.LogEntryType.Warning);
        });
    }

    /// <summary>
    /// Scrive la riga del dispositivo, inserendola se non c'e': la usa solo il caricamento di un
    /// dispositivo che non ha ancora una riga.
    /// </summary>
    internal static void Create(Items.AutomationDevice value)
    {
        var snapshot = PlantStore.Snapshot(value);
        Write(context => PlantStore.UpsertItemAsync(context, snapshot));
    }

    /// <summary>
    /// Stato, stato della UDC e step in corso: sono campi della stessa riga, si scrive l'istantanea
    /// intera. Il nome resta per i chiamanti.
    /// </summary>
    internal static Task UpdateStatus(Items.AutomationDevice? value)
    {
        if (value != null) Update(value);
        return Task.CompletedTask;
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
