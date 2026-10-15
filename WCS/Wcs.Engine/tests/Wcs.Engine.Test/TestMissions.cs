using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;

namespace Wcs.Engine.Test;

/// <summary>
/// Missioni pronte per i test che hanno bisogno di una destinazione vera, non solo di un id.
/// Richiede <see cref="Navigator.Store"/> gia' impostato (come le mappe porte di
/// <see cref="DoorMappingStoreTest"/>): crea una porta dedicata e la mappa sulla cella data, cosi'
/// che <see cref="ORM.DbInfo.GetCellId(string?)"/> la risolva. Il chiamante resta responsabile di
/// aprire e chiudere lo store, come per ogni altro test che tocca la mappa porte.
/// </summary>
internal static class TestMissions
{
    /// <summary>Una missione la cui cella di deposito e' <paramref name="cellId"/>.</summary>
    internal static async Task<PathMission> WithDestinationCell(decimal cellId)
    {
        var item = Guid.NewGuid();
        var doorCode = $"TM{cellId}";

        // La riga del dispositivo prima della porta: DoorMapping.ItemId ha una chiave esterna verso
        // PlantItems, e una porta appesa a un identificativo inventato non si scrive piu'.
        await PlantRows.EnsureAsync(item);
        await PlantPersistence.SaveDoorMappingsAsync(item,
            [new DoorMapping { ItemId = item, DoorCode = doorCode, CellId = cellId, Side = "A" }]);

        return new PathMission(Guid.NewGuid()) { StorageDoorCode = doorCode };
    }
}
