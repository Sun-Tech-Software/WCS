using Wcs.Engine.Persistence;

namespace Wcs.Engine.ORM;

/// <summary>
/// La configurazione «quali dati del PLC registrare» sul database del WCS: una riga sola,
/// attraverso <see cref="PlcSettingsStore"/>.
/// </summary>
/// <remarks>
/// Prima era T-SQL su <c>RECORD_DATA_FROM_ASI</c>. La lettura e' sincrona sullo store, perche'
/// avviene nel costruttore; le scritture passano dallo scrittore di stato. Senza store non c'e'
/// nulla da leggere ne' da scrivere.
/// </remarks>
internal static class RecordDataFromAsiInfo
{
    #region Public Methods

    /// <summary>Copia la riga nell'oggetto. Restituisce false se non c'e' una riga, o non c'e' lo store.</summary>
    public static bool Get(Items.RecordDataFromAsiInfo value)
    {
        var store = Navigator.Store;
        if (store == null) return false;

        using var context = store.CreateContext();
        var row = PlcSettingsStore.LoadRecordSettingsAsync(context).GetAwaiter().GetResult();
        if (row == null) return false;

        PlcSettingsStore.Apply(row, value);
        return true;
    }

    #endregion Public Methods

    #region Internal Methods

    internal static void Update(Items.RecordDataFromAsiInfo value)
    {
        var snapshot = PlcSettingsStore.Snapshot(value);
        WcsStateWrites.Write(context => PlcSettingsStore.SaveRecordSettingsAsync(context, snapshot));
    }

    #endregion Internal Methods
}
