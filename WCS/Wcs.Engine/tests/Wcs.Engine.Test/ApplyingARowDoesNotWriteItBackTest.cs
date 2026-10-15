using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Api;
using Wcs.Engine.Collections;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Copiare una riga del database dentro un dispositivo non riscrive la riga: nessun setter scrive
/// mentre la si sta leggendo.
/// </summary>
/// <remarks>
/// <para>
/// La guardia <c>_loadingFromDatabase</c> tace le scritture dei setter durante il caricamento, ma
/// <c>IdCellToDefragment</c> e <c>StorageOffsetReversed</c> chiamavano
/// <c>ORM.AutomationDevice.Update</c> per conto proprio, fuori dalla guardia: due scritture della
/// riga intera per dispositivo a ogni ricaricamento della memoria, con l'istantanea presa a meta'
/// della copia. Erano le scritture in piu' che <c>PlantConfigurationTest</c> contava come
/// tentativi della sostituzione del grafo (coda, voce 42).
/// </para>
/// <para>
/// La sentinella e' lo stato: <c>ApplyConfiguration</c> non lo tocca, l'istantanea di una
/// scrittura lo porta. Sul database c'e' <c>Busy</c>, in memoria no: se un setter scrive, lo
/// stato sul database cambia.
/// </para>
/// </remarks>
public sealed class ApplyingARowDoesNotWriteItBackTest : IDisposable
{
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;
    private readonly WcsDatabase _store = WcsDatabase.InMemory();

    public ApplyingARowDoesNotWriteItBackTest()
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
    public async Task ApplyingAConfigurationLeavesTheStoredRowAlone()
    {
        var id = Guid.NewGuid();
        await PlantQueries.UpsertItemConfigurationAsync(new PlantItem { ItemId = id, Name = "A" });
        var device = PathDevices.Instance[id]!;
        Assert.NotEqual(DeviceStatusTypes.Busy, device.Status);
        await using (var context = _store.CreateContext())
        {
            var row = await context.PlantItems.SingleAsync(item => item.ItemId == id);
            row.Status = (short)DeviceStatusTypes.Busy;
            await context.SaveChangesAsync();
        }

        device.ApplyConfiguration(new PlantItem
        {
            ItemId = id, Name = "A", StorageOffsetReversed = 5, CellToDefragmentId = 7
        });

        Assert.Equal((5, 7m), (device.StorageOffsetReversed, device.IdCellToDefragment));
        if (Navigator.StateWriter is { } writer) await writer.FlushAsync();
        await using (var context = _store.CreateContext())
        {
            var stored = await context.PlantItems.AsNoTracking().SingleAsync(item => item.ItemId == id);
            Assert.Equal((short)DeviceStatusTypes.Busy, stored.Status);
        }
    }
}
