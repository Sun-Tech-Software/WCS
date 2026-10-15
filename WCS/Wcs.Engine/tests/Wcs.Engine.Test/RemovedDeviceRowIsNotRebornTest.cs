using Log.Interface;
using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Api;
using Wcs.Engine.Collections;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Una scrittura di stato non reinserisce la riga di un dispositivo tolto: la scarta, e lo dice
/// con una riga di registro Warning.
/// </summary>
/// <remarks>
/// <para>
/// Ogni scrittura della riga di un dispositivo - i setter, <c>FireStatusChanged</c>,
/// <c>FireLuLoadStatusChanged</c>, il <c>Task.Run</c> di fine passo - passava da
/// <c>PlantStore.UpsertItemAsync</c>, che inserisce la riga se non la trova. Un dispositivo tolto
/// dall'impianto mentre una sua scrittura era ancora in coda (o in volo in un <c>Task.Run</c>)
/// rinasceva sul database, senza geometria, e al ricaricamento successivo tornava in memoria
/// (coda, voce 42; revisione finale delle missioni, compito 1, punto 6).
/// </para>
/// <para>
/// La creazione resta, esplicita, dove serve: un dispositivo che si carica senza riga
/// (<c>AutomationDevice.Load</c>) la scrive con i valori correnti - e' provato in
/// <see cref="PlantStoreTest.ADeviceWithoutARowWritesOneWithItsCurrentValuesAndTheSnapshotRoundTrips"/>.
/// </para>
/// </remarks>
public sealed class RemovedDeviceRowIsNotRebornTest : IDisposable
{
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;
    private readonly WcsDatabase _store = WcsDatabase.InMemory();

    public RemovedDeviceRowIsNotRebornTest()
    {
        Navigator.Store = _store;
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
    }

    public void Dispose()
    {
        PathDevices.Instance.Clear();
        Navigator.NavStatus = _previousStatus;
        Navigator.Store = null;
        _store.Dispose();
    }

    [Fact]
    public async Task AStatusWriteAfterTheRemovalDoesNotBringTheRowBack()
    {
        using var log = new RecordingLogWriter();
        var id = Guid.NewGuid();
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem { ItemId = id, Name = "Tolto" });
        var device = PathDevices.Instance[id]!;

        ORM.AutomationDevice.Delete(id);
        await device.FireStatusChanged();
        if (Navigator.StateWriter is { } writer) await writer.FlushAsync();

        Assert.False(await RowExists(id));
        await log.Wait((line, level) => level == LogEntryType.Warning && line.Contains(id.ToString()))
            .WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AStatusWriteAndARemovalInTheSameBatchLeaveNoRow()
    {
        var id = Guid.NewGuid();
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem { ItemId = id, Name = "Tolto" });
        var snapshot = PlantStore.Snapshot(PathDevices.Instance[id]!);

        await using (var context = _store.CreateContext())
        {
            await PlantStore.DeleteItemAsync(context, id);
            Assert.False(await PlantStore.UpdateItemAsync(context, snapshot));
            await context.SaveChangesAsync();
        }

        Assert.False(await RowExists(id));
    }

    [Fact]
    public async Task AStatusWriteStillUpdatesAnExistingRow()
    {
        var id = Guid.NewGuid();
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem { ItemId = id, Name = "Presente" });
        var device = PathDevices.Instance[id]!;

        device.Status = DeviceStatusTypes.Busy;
        await device.FireStatusChanged();
        if (Navigator.StateWriter is { } writer) await writer.FlushAsync();

        await using var context = _store.CreateContext();
        var stored = await context.PlantItems.AsNoTracking().SingleAsync(item => item.ItemId == id);
        Assert.Equal((short)DeviceStatusTypes.Busy, stored.Status);
    }

    private async Task<bool> RowExists(Guid id)
    {
        await using var context = _store.CreateContext();
        return await context.PlantItems.AnyAsync(item => item.ItemId == id);
    }
}
