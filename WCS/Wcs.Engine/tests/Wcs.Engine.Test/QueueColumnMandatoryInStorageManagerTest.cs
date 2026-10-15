using System.Data;
using System.Reflection;
using Wcs.Engine.Items.Managers;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// Voce R11, <c>AutomationDeviceStorageManager</c>: le colonne della coda di estrazione sono
/// obbligatorie (si veda <see cref="QueueColumnMandatoryInDeviceTest"/>). I metodi che leggono la
/// riga sono privati e la tabella la costruiscono da soli dallo store, quindi non c'e' un
/// ingresso in cui passare una riga mutilata: <c>ProcessExtractionRow</c>, il primo lettore del
/// giro di estrazione, si raggiunge per riflessione, come <c>EngineStartIsExplicitTest</c> fa con
/// le proprieta'.
/// </summary>
public sealed class QueueColumnMandatoryInStorageManagerTest : IDisposable
{
    private readonly WcsDatabase _database = WcsDatabase.InMemory();

    private static readonly MethodInfo ProcessExtractionRow =
        typeof(AutomationDeviceStorageManager).GetMethod("ProcessExtractionRow", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("AutomationDeviceStorageManager.ProcessExtractionRow non c'e' piu': il test va aggiornato.");

    public QueueColumnMandatoryInStorageManagerTest() => Navigator.Store = _database;

    public void Dispose()
    {
        Navigator.Store = null;
        _database.Dispose();
    }

    [Fact]
    public async Task AMissingColumnThrowsNamingIt()
    {
        var table = QueueTable(loadUnitId: 5m);
        table.Columns.Remove("ID_UDC");

        var error = await Assert.ThrowsAsync<ArgumentException>(() => Process(table.Rows[0]));

        Assert.Contains("ID_UDC", error.Message);
    }

    /// <summary>Cella polmone, porta e cella a <c>DBNull</c>: la riga passa come prima, e la lettura non solleva.</summary>
    [Fact]
    public async Task ANullCellStillReadsAsZero()
    {
        var table = QueueTable(loadUnitId: 5m);
        Assert.Equal(DBNull.Value, table.Rows[0]["ID_CELLA_POLMONE"]);

        Assert.True(await Process(table.Rows[0]));
    }

    private static Task<bool> Process(DataRow row) =>
        (Task<bool>)ProcessExtractionRow.Invoke(null, [row, new List<decimal>(), new List<string?>()])!;

    private static DataTable QueueTable(decimal loadUnitId) => ExtractionQueueStore.ToDataTable(
        [new ExtractionQueueItem { Id = 1, LoadUnitId = loadUnitId, SourceItemId = Guid.NewGuid() }]);
}
