namespace Wcs.SampleWms;

/// <summary>
/// The warehouse the sample starts with: one site, a rack of six storage cells served by one stacker
/// crane, an infeed bay and an outfeed bay, and six load units waiting outside the warehouse.
/// </summary>
/// <remarks>
/// <para>
/// The plant the WCS runs is in <c>samples/plant/sample-plant.json</c>, and the two files describe
/// the same building from two sides. What must agree, and why:
/// </para>
/// <list type="bullet">
/// <item>the device ids below are the <c>ItemId</c>s of that file: a cell is reached "through" the
/// storage device ST01 and the crane SC01, and the WCS asks for a cell reachable by its crane;</item>
/// <item>each cell's door code and id are a door mapping of that file (door code -> cell -> device);</item>
/// <item>the infeed device IN01 has <c>LinkedCellId</c> 800: the WCS reads the warehouse of a storage
/// from the cell linked to the device where the load unit enters. The outfeed device OUT01 has 900.</item>
/// </list>
/// <para>
/// In a real installation the device ids come from the WCS (<c>GET /api/v1/plant/items</c>, or the
/// editor) and are stored by the WMS next to its cells, as here.
/// </para>
/// </remarks>
public static class SampleData
{
    public const decimal WarehouseId = 1;
    public const decimal StorageAreaId = 10;
    public const decimal InfeedBayAreaId = 20;
    public const decimal OutfeedBayAreaId = 30;

    /// <summary>The infeed bay cell (linked to the device IN01).</summary>
    public const decimal InfeedBayCellId = 800;

    /// <summary>The outfeed bay cell (linked to the device OUT01): where extraction orders deliver by default.</summary>
    public const decimal OutfeedBayCellId = 900;

    public const decimal EuroPalletTypeId = 1;

    // The plant devices, with the ids they have in samples/plant/sample-plant.json.
    public static readonly Guid Infeed = Guid.Parse("5a000000-0000-4000-8000-000000000001");          // IN01
    public static readonly Guid StackerCrane = Guid.Parse("5a000000-0000-4000-8000-000000000004");    // SC01
    public static readonly Guid Storage = Guid.Parse("5a000000-0000-4000-8000-000000000005");         // ST01
    public static readonly Guid Outfeed = Guid.Parse("5a000000-0000-4000-8000-000000000007");         // OUT01

    public static IReadOnlyList<SampleWarehouseSite> Warehouses() => [new(WarehouseId, "Sample warehouse")];

    public static IReadOnlyList<SampleArea> Areas() =>
    [
        new(StorageAreaId, WarehouseId, "Rack A (automatic)", SampleAreaKind.Storage, Automatic: true),
        // One area per bay type: the editor asks for the cell of a Loading device (IN01) in area type 2
        // and for the cell of a Buffer device (OUT01) in area type 3, automatic areas only.
        new(InfeedBayAreaId, WarehouseId, "Infeed bays", SampleAreaKind.AutomationItems, Automatic: true),
        new(OutfeedBayAreaId, WarehouseId, "Outfeed bays", SampleAreaKind.BufferItems, Automatic: true)
    ];

    /// <summary>
    /// Six storage cells, three columns by two floors, door codes <c>A0101</c> to <c>A0302</c>
    /// (column, floor), plus the two bay cells. The field address of a storage cell is the storage
    /// device's ASI (3001) and a subitem made of column and floor, the address a crane controller
    /// would use for that slot.
    /// </summary>
    public static IReadOnlyList<SampleCell> Cells()
    {
        List<SampleCell> cells = [];
        var id = 101m;

        for (var column = 1; column <= 3; column++)
        for (var floor = 1; floor <= 2; floor++)
        {
            cells.Add(new SampleCell
            {
                Id = id++,
                WarehouseId = WarehouseId,
                AreaId = StorageAreaId,
                Column = column,
                Floor = floor,
                DoorCodeA = $"A{column:00}{floor:00}",
                FieldAsi = "3001",
                FieldSubitem = $"{column:00}{floor:00}",
                Partition = "0000",
                Depth = 1200,
                AccessDeviceIds = [Storage, StackerCrane],
                Description = $"Rack A, column {column}, floor {floor}"
            });
        }

        cells.Add(new SampleCell
        {
            Id = InfeedBayCellId,
            WarehouseId = WarehouseId,
            AreaId = InfeedBayAreaId,
            Column = 0,
            Floor = 0,
            DoorCodeA = "BAY-IN01",
            FieldAsi = "1001",
            FieldSubitem = "0001",
            Partition = "0000",
            Depth = 1200,
            AccessDeviceIds = [Infeed],
            Description = "Infeed bay IN01"
        });

        cells.Add(new SampleCell
        {
            Id = OutfeedBayCellId,
            WarehouseId = WarehouseId,
            AreaId = OutfeedBayAreaId,
            Column = 0,
            Floor = 0,
            DoorCodeA = "BAY-OUT01",
            FieldAsi = "1005",
            FieldSubitem = "0001",
            Partition = "0000",
            Depth = 1200,
            AccessDeviceIds = [Outfeed],
            Description = "Outfeed bay OUT01",
            // The outfeed bay is served from the rack: an extraction for this bay looks for the
            // load unit in area 10.
            PickingAreaIds = [StorageAreaId]
        });

        return cells;
    }

    public static IReadOnlyList<SampleLoadUnitType> LoadUnitTypes() =>
        [new(EuroPalletTypeId, "Euro pallet 1200 x 800", HandlingMode: 0)];

    /// <summary>Six pallets, ids 5001 to 5006, none of them in the warehouse yet.</summary>
    public static IReadOnlyList<SampleLoadUnit> LoadUnits()
    {
        string[] references = ["ART-RED", "ART-RED", "ART-BLUE", "ART-BLUE", "ART-GREEN", "ART-GREEN"];

        return
        [
            .. references.Select((reference, index) => new SampleLoadUnit
            {
                Id = 5001 + index,
                Code = $"LU{5001 + index:000000}",
                TypeId = EuroPalletTypeId,
                Reference = reference,
                Tare = 25,
                Height = 1000,
                Width = 800,
                Depth = 1200,
                Weight = 300
            })
        ];
    }
}
