using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.Engine;
using Wcs.Engine.Collections;
using Wcs.Engine.Persistence.Entities;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Host.Test;

/// <summary>
/// La fotografia per la ricerca celle del WMS: celle automatiche con il blocco al deposito
/// aggregato per cella e lo stato dal vivo del ricollocatore, celle e UDC in coda. Il ricollocatore
/// nasce in memoria dal PUT della configurazione a motore fermo, come in
/// <see cref="PlantConfigurationEndpointsTest"/>, e il Dispose lo toglie.
/// </summary>
[Collection("host")]
public sealed class CellSearchContextEndpointTest(HostFixture host) : IAsyncLifetime
{
    private readonly NavigatorStatusTypes _previous = Navigator.NavStatus;
    private readonly Guid _storage = Guid.NewGuid();
    private const string Url = ApiRoutes.Prefix + ApiRoutes.WmsCellSearchContext;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var context = host.Store.CreateContext();
        await context.ExtractionQueue.Where(q => q.SourceItemId == _storage).ExecuteDeleteAsync();
        PathDevices.Instance.Clear();
        Navigator.NavStatus = _previous;
    }

    [Fact]
    public async Task TheContextIsOneSnapshotOfAutomaticCellsLocksRelocatorStatusAndTheQueue()
    {
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        var storage = _storage;
        var relocator = Guid.NewGuid();
        var farStorage = Guid.NewGuid();
        var absentRelocator = Guid.NewGuid();
        var manualStorage = Guid.NewGuid();
        await using (var context = host.Store.CreateContext())
        {
            context.PlantItems.Add(new PlantItem { ItemId = storage, RelocateItemId = relocator });
            context.PlantItems.Add(new PlantItem { ItemId = farStorage, RelocateItemId = absentRelocator });
            context.PlantItems.Add(new PlantItem { ItemId = manualStorage });
            context.ExtractionQueue.Add(new ExtractionQueueItem { LoadUnitId = 900, CellId = 300, SourceItemId = storage });
            context.ExtractionQueue.Add(new ExtractionQueueItem { LoadUnitId = 901, CellId = 301, SourceItemId = storage, Processed = true });
            await context.SaveChangesAsync();
        }

        // Il ricollocatore in memoria, a motore fermo, in uno stato diverso da quello di partenza
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"{ApiRoutes.Prefix}{ApiRoutes.Items}", new PlantItemConfigDto { ItemId = relocator })).StatusCode);
        PathDevices.Instance[relocator]!.Status = DeviceStatusTypes.Error;
        Assert.Equal(DeviceStatusTypes.Error, PathDevices.Instance[relocator]!.Status);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(ApiRoutes.Prefix + ApiRoutes.DoorMappings, new[]
        {
            new DoorMappingDto(0, storage, "A", 300, "A", 0, false),
            new DoorMappingDto(0, storage, "B", 300, "B", 0, true),      // due porte, bloccata solo una: la cella non e' bloccata
            new DoorMappingDto(0, storage, "C", 301, "A", 0, true),
            new DoorMappingDto(0, farStorage, "D", 302, "A", 0, false),
            new DoorMappingDto(0, manualStorage, "E", 303, "A", 0, false) // magazzino senza ricollocatore: non e' automatica
        })).StatusCode);

        var context1 = (await client.GetFromJsonAsync<CellSearchContextDto>(Url))!;

        var cell300 = Assert.Single(context1.AutomaticCells, c => c.CellId == 300);
        Assert.Equal((storage, relocator, false, DeviceStatusTypes.Error), (cell300.ItemId, cell300.RelocatorItemId, cell300.StoringLocked, cell300.RelocatorStatus));
        Assert.True(Assert.Single(context1.AutomaticCells, c => c.CellId == 301).StoringLocked);
        Assert.Equal(DeviceStatusTypes.OffLine, Assert.Single(context1.AutomaticCells, c => c.CellId == 302).RelocatorStatus);
        Assert.DoesNotContain(context1.AutomaticCells, c => c.CellId == 303);
        Assert.Contains(300m, context1.CellsInExtraction);
        Assert.DoesNotContain(301m, context1.CellsInExtraction);
        Assert.Contains(900m, context1.LoadUnitsInExtraction);
        Assert.DoesNotContain(901m, context1.LoadUnitsInExtraction);
    }
}
