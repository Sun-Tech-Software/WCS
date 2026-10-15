using Wcs.Engine.Persistence;

namespace Wcs.Engine.ORM;

/// <summary>
/// La configurazione «quali dati del PLC verificare» sul database del WCS: una riga sola,
/// attraverso <see cref="PlcSettingsStore"/>.
/// </summary>
/// <remarks>
/// Prima era T-SQL su <c>CHECK_DATA_FROM_ASI</c>. La lettura e' sincrona sullo store, perche'
/// avviene nel costruttore; le scritture passano dallo scrittore di stato.
/// </remarks>
internal static class CheckDataFromAsiInfo
{
    #region Public Methods

    /// <summary>Copia la riga nell'oggetto. Restituisce false se non c'e' una riga, o non c'e' lo store.</summary>
    public static bool Get(Items.CheckDataFromAsiInfo value)
    {
        var store = Navigator.Store;
        if (store == null) return false;

        using var context = store.CreateContext();
        var row = PlcSettingsStore.LoadCheckSettingsAsync(context).GetAwaiter().GetResult();
        if (row == null) return false;

        PlcSettingsStore.Apply(row, value);
        return true;
    }

    #endregion Public Methods

    #region Internal Methods

    internal static void Update(Items.CheckDataFromAsiInfo value)
    {
        var snapshot = PlcSettingsStore.Snapshot(value);
        WcsStateWrites.Write(context => PlcSettingsStore.SaveCheckSettingsAsync(context, snapshot));
    }

    #endregion Internal Methods
}
