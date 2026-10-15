using System.Globalization;
using Wcs.Wms.Contracts;

namespace Wcs.SampleWms;

/// <summary>
/// The sample WMS's warehouse: cells, load units, positions, reservations and extraction orders, in
/// memory, and the business rules the WCS relies on when it calls the contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>How to read it.</b> Every public method is one operation of the WCS -> WMS contract (or of the
/// sample's own admin endpoint, at the bottom), named after the <c>IWarehouse</c> method the WCS calls,
/// and returns the contract's record. <see cref="WarehouseEndpoints"/> only turns HTTP into calls to
/// these methods and the answers into status codes. If you write an adapter in front of your WMS, this
/// is the class to replace with queries and updates on your own database.
/// </para>
/// <para>
/// <b>One lock.</b> The WCS calls from several threads at once (its master loop, every device, the
/// durable queues). Every method takes the same lock, which is what a transaction would give a real
/// WMS: a cell is never reserved twice, and a load unit never has two physical positions.
/// </para>
/// </remarks>
public sealed class SampleWarehouse
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _clock;
    private readonly TimeSpan _reservationLifetime;

    private readonly Dictionary<decimal, SampleWarehouseSite> _warehouses;
    private readonly Dictionary<decimal, SampleArea> _areas;
    private readonly Dictionary<decimal, SampleCell> _cells;
    private readonly Dictionary<decimal, SampleLoadUnitType> _loadUnitTypes;
    private readonly Dictionary<decimal, SampleLoadUnit> _loadUnits;
    private readonly List<SamplePosition> _positions = [];
    private readonly Dictionary<Guid, SampleStorageHold> _holds = [];
    private readonly List<SampleExtractionOrder> _orders = [];
    private readonly List<SampleJournalEntry> _journal = [];
    private readonly List<SampleCall> _calls = [];
    private decimal _nextOrderId = 1;
    private decimal _nextRowId = 1;

    /// <summary>The journal keeps the last entries only: it is there to be looked at, not to be audited.</summary>
    private const int JournalLength = 500;

    /// <summary>How many of the last requests <see cref="RecordCall"/> keeps.</summary>
    private const int CallsKept = 200;

    public SampleWarehouse(TimeProvider clock, TimeSpan reservationLifetime)
    {
        _clock = clock;
        _reservationLifetime = reservationLifetime;
        _warehouses = SampleData.Warehouses().ToDictionary(w => w.Id);
        _areas = SampleData.Areas().ToDictionary(a => a.Id);
        _cells = SampleData.Cells().ToDictionary(c => c.Id);
        _loadUnitTypes = SampleData.LoadUnitTypes().ToDictionary(t => t.Id);
        _loadUnits = SampleData.LoadUnits().ToDictionary(l => l.Id);
    }

    // ------------------------------------------------------------------------------------------
    // Cells: what the WCS asks about a place
    // ------------------------------------------------------------------------------------------

    /// <summary><c>GET cells/{cellId}</c>: the cell's area, warehouse, depth, lock and doors. Null = unknown cell (404).</summary>
    public CellDescription? DescribeCell(decimal cellId)
    {
        lock (_gate)
            return _cells.TryGetValue(cellId, out var cell) ? Describe(cell) : null;
    }

    /// <summary><c>GET doors/{doorCode}/cells</c>: the cells that answer to a door code (A or B). Empty = none.</summary>
    public IReadOnlyList<CellDescription> DescribeCellsByDoor(string doorCode)
    {
        lock (_gate)
            return [.. _cells.Values.Where(c => SameDoor(c.DoorCodeA, doorCode) || SameDoor(c.DoorCodeB, doorCode)).Select(Describe)];
    }

    /// <summary>
    /// <c>GET cells/{cellId}/partitions</c>: the field address of each door and the depth positions of
    /// the cell. This is where a WMS cell becomes an address a machine understands.
    /// </summary>
    public CellPartitionMap? GetCellPartitionMap(decimal cellId)
    {
        lock (_gate)
        {
            if (!_cells.TryGetValue(cellId, out var cell)) return null;

            // Door B has no address in the sample: every cell has one door.
            return new CellPartitionMap(cell.Id, cell.FieldAsi, cell.FieldSubitem, string.Empty, string.Empty,
                [new CellPartition(cell.Partition, SampleCell.SinglePositionOffset)]);
        }
    }

    /// <summary>
    /// <c>POST cells/lock</c>: the WCS locks the cell whose door A is <paramref name="doorCode"/> - for
    /// example <see cref="CellLockState.Full"/> when the crane refused to store into it. False = no cell
    /// has that door A (404).
    /// </summary>
    public bool SetCellLockState(string doorCode, CellLockState lockState)
    {
        lock (_gate)
        {
            var cell = _cells.Values.FirstOrDefault(c => SameDoor(c.DoorCodeA, doorCode));
            if (cell == null) return false;

            cell.Locked = lockState;
            Journal(WarehouseRoutes.CellLock, $"cell {cell.Id} (door {doorCode}) locked as {lockState}");
            return true;
        }
    }

    /// <summary><c>GET cells/{cellId}/content</c>: the load units physically in the cell (not the reserved positions).</summary>
    public IReadOnlyList<decimal> GetCellContent(decimal cellId)
    {
        lock (_gate)
            return [.. _positions.Where(p => p.CellId == cellId && !p.Reserved).Select(p => p.LoadUnitId)];
    }

    /// <summary><c>GET cells/{cellId}/positions</c>: every position of the cell, physical and reserved.</summary>
    public IReadOnlyList<CellPosition> GetCellPositions(decimal cellId)
    {
        lock (_gate)
            return [.. _positions.Where(p => p.CellId == cellId).Select(p => new CellPosition(p.LoadUnitId, p.Reserved))];
    }

    /// <summary>
    /// <c>POST cells/clear-positions</c>: the WCS found the cell empty on the field (for example after an
    /// "empty cell" answer of the crane) and every position of the cell goes, reserved ones included.
    /// </summary>
    public void ClearCellPositions(decimal cellId)
    {
        lock (_gate)
        {
            var removed = _positions.RemoveAll(p => p.CellId == cellId);
            foreach (var hold in _holds.Values.Where(h => h.CellId == cellId).ToList()) _holds.Remove(hold.MissionId);
            Journal(WarehouseRoutes.ClearCellPositions, $"cell {cellId} cleared ({removed} positions)");
        }
    }

    /// <summary>
    /// <c>GET cells/{cellId}/picking-buffer-areas</c>: the storage areas a bay is served from, in order
    /// of priority. Empty for a storage cell: it is an answer, not an absence.
    /// </summary>
    public IReadOnlyList<decimal> GetPickingBufferAreas(decimal cellId)
    {
        lock (_gate)
            return _cells.TryGetValue(cellId, out var cell) ? cell.PickingAreaIds : [];
    }

    /// <summary>
    /// <c>POST cells/busy-state</c>: why the cell is busy, three separate answers the WCS decides on.
    /// <see cref="CellBusyState.Extraction"/>: a load unit of the cell is in an open extraction order.
    /// <see cref="CellBusyState.PickUp"/>: always false here - it is about shipping notes, which the
    /// sample does not have. <see cref="CellBusyState.Storing"/>: another load unit has a reserved
    /// position in the cell.
    /// </summary>
    public CellBusyState GetCellBusyState(decimal cellId, decimal loadUnitIdToExclude)
    {
        lock (_gate)
        {
            var physical = _positions.Where(p => p.CellId == cellId && !p.Reserved).Select(p => p.LoadUnitId).ToHashSet();
            var extraction = OpenRows().Any(row => physical.Contains(row.LoadUnitId));
            var storing = _positions.Any(p => p.CellId == cellId && p.Reserved && p.LoadUnitId != loadUnitIdToExclude);
            return new CellBusyState(extraction, PickUp: false, storing);
        }
    }

    /// <summary>
    /// <c>POST cells/storing-constraints</c>: at what depth a load unit would be stored in the cell.
    /// A cell holds one load unit, so the answer is the cell's depth when nobody else is in it, and
    /// <b>0 when it is taken</b>: 0 tells the WCS "no room", and it looks for another cell.
    /// </summary>
    public CellStoringConstraints GetCellStoringConstraints(decimal cellId, decimal loadUnitIdToExclude)
    {
        lock (_gate)
        {
            if (!_cells.TryGetValue(cellId, out var cell)) return new CellStoringConstraints(0);

            var taken = _positions.Any(p => p.CellId == cellId && p.LoadUnitId != loadUnitIdToExclude);
            return new CellStoringConstraints(taken ? 0 : cell.Depth);
        }
    }

    /// <summary>
    /// <c>GET warehouses/{warehouseId}/topology</c>: the automatic areas and which area each cell is in.
    /// An unknown warehouse answers an empty topology - a warehouse with no automatic area - because the
    /// WCS asks it on every storage search and a 404 would be read as "the WMS is down".
    /// </summary>
    public AreaTopology GetAreaTopology(decimal warehouseId)
    {
        lock (_gate)
        {
            var automatic = _areas.Values.Where(a => a.WarehouseId == warehouseId && a.Automatic && a.Kind == SampleAreaKind.Storage)
                .Select(a => a.Id).ToList();
            var cellToArea = _cells.Values.Where(c => c.WarehouseId == warehouseId).ToDictionary(c => c.Id, c => c.AreaId);
            return new AreaTopology(warehouseId, automatic, cellToArea);
        }
    }

    /// <summary>
    /// <c>GET areas/{areaId}/defragmentation</c>: does the area need compacting for this type of load
    /// unit? Never, here: compacting moves load units that block others in deep cells, and every cell of
    /// the sample holds one. The policy (thresholds, counts) belongs to the WMS, not to the WCS.
    /// </summary>
    public AreaDefragmentationVerdict GetAreaDefragmentationVerdict(decimal areaId, decimal loadUnitTypeId) =>
        new(areaId, loadUnitTypeId, Needed: false);

    /// <summary>
    /// <c>GET cells?areaType=&amp;automaticOnly=</c>: the cells the WCS editor lists when it links doors
    /// and bays to plant devices. No area type = the storage areas (types 0 and 1), for the doors of a
    /// Storage device. For the cell of any other linkable device the editor always asks
    /// <c>automaticOnly=true</c>, with area type 2 for Automation, Loading and Circular Distributor
    /// devices and area type 3 for Buffer and LoadingAndBuffer devices: the infeed bay (IN01, Loading)
    /// is found in area type 2, the outfeed bay (OUT01, Buffer) in area type 3. A WMS that answers
    /// these area types with other cells, or keeps its bays in non-automatic areas, leaves the editor's
    /// panel empty.
    /// </summary>
    public IReadOnlyList<EditorCellRow> GetCellsForEditor(short? areaType, bool automaticOnly)
    {
        lock (_gate)
        {
            return
            [
                .. _cells.Values
                    .Where(c => areaType is { } type ? (short)_areas[c.AreaId].Kind == type : _areas[c.AreaId].Kind == SampleAreaKind.Storage)
                    .Where(c => !automaticOnly || _areas[c.AreaId].Automatic)
                    .OrderBy(c => c.WarehouseId).ThenBy(c => c.AreaId).ThenBy(c => c.Column).ThenBy(c => c.Floor)
                    .Select(c => new EditorCellRow(c.Id, _warehouses[c.WarehouseId].Description, _areas[c.AreaId].Description,
                        c.Column, c.Floor, c.DoorCodeA, NullIfEmpty(c.DoorCodeB), c.Description))
            ];
        }
    }

    /// <summary>
    /// <c>GET configuration-check?mode=</c>: what does not add up in the WMS's own data, one row per
    /// problem, as free text columns the editor shows. No row = configuration fine. The sample checks
    /// that no two cells share a door code, the mistake that would send a crane to the wrong slot.
    /// </summary>
    public IReadOnlyList<ConfigurationWarning> GetConfigurationWarnings(int mode)
    {
        lock (_gate)
        {
            return
            [
                .. _cells.Values.GroupBy(c => c.DoorCodeA, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
                    .Select(g => new ConfigurationWarning(new Dictionary<string, string?>
                    {
                        ["Problem"] = "door code used by more than one cell",
                        ["DoorCode"] = g.Key,
                        ["Cells"] = string.Join(", ", g.Select(c => c.Id))
                    }))
            ];
        }
    }

    // ------------------------------------------------------------------------------------------
    // Storage reservations: the WCS asks for a cell, the WMS chooses it
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>POST reservations</c>: choose a storage cell for the load unit. "No free cell" is an
    /// outcome in the body (<see cref="StorageReservationOutcome.NoFreeCells"/>), not an error. With
    /// <see cref="ReserveStorageRequest.AssignPosition"/> the WMS <b>writes</b>: the load unit gets a
    /// reserved position in the cell (replacing the one it had: one per load unit), and with a mission
    /// the reservation belongs to that mission.
    /// </summary>
    public StorageReservation ReserveStorage(ReserveStorageRequest request)
    {
        lock (_gate)
        {
            ExpireOrphanHolds();

            if (request.LoadUnitId == 0) return NoReservation(request.MissionId, StorageReservationOutcome.LoadUnitIdRequired);
            if (request.WarehouseId == 0) return NoReservation(request.MissionId, StorageReservationOutcome.WarehouseRequired);
            if (!_loadUnits.ContainsKey(request.LoadUnitId))
                return NoReservation(request.MissionId, StorageReservationOutcome.LoadUnitCodeNotFound);

            var cell = FreeStorageCells(request.LoadUnitId, request.WarehouseId, request.SearchAreaIds, request.ExcludedCellIds,
                request.ReachableDeviceIds).FirstOrDefault();
            if (cell == null) return NoReservation(request.MissionId, StorageReservationOutcome.NoFreeCells);

            if (request.AssignPosition) Hold(request.MissionId, request.LoadUnitId, cell, WarehouseRoutes.Reservations);

            return Reservation(request.MissionId, cell);
        }
    }

    /// <summary>
    /// <c>GET reservations/{missionId}</c>: the cell this mission holds. <b>Asking renews it</b>: the
    /// WMS frees the reservations nobody asks about (<c>SampleWms:ReservationLifetimeMinutes</c>), and
    /// this question is how a living mission says "still mine". Null (404) = the mission holds nothing,
    /// or its reservation was taken away: the WCS reserves again.
    /// </summary>
    public StorageReservation? GetStorageReservation(Guid missionId)
    {
        lock (_gate)
        {
            ExpireOrphanHolds();

            if (!_holds.TryGetValue(missionId, out var hold)) return null;

            // The link alone is not enough: the load unit must still have its position in that cell
            // (reserved, or physical once stored), or the reservation was taken away.
            if (!_positions.Any(p => p.LoadUnitId == hold.LoadUnitId && p.CellId == hold.CellId))
            {
                _holds.Remove(missionId);
                return null;
            }

            hold.RenewedAt = _clock.GetUtcNow();
            return Reservation(missionId, _cells[hold.CellId]);
        }
    }

    /// <summary>
    /// <c>POST reservations/release</c>: the mission lets its reservation go (it ended, or was
    /// cancelled). The reserved position goes back to free; a load unit already stored keeps its
    /// physical position. Idempotent: releasing nothing is not an error.
    /// </summary>
    public void ReleaseStorageReservation(Guid missionId)
    {
        lock (_gate)
        {
            if (!_holds.Remove(missionId, out var hold)) return;

            _positions.RemoveAll(p => p.LoadUnitId == hold.LoadUnitId && p.CellId == hold.CellId && p.Reserved);
            Journal(WarehouseRoutes.ReleaseReservation, $"mission {missionId} released cell {hold.CellId}");
        }
    }

    /// <summary>
    /// <c>POST reservations/release-load-unit-cell</c>: the WCS gives back a cell it had reserved for a
    /// load unit (a change of cell redirected by the operator). Only a <b>reserved</b> position goes,
    /// never the place where the load unit really is. Sent by a durable queue of the WCS: the endpoint
    /// keeps a receipt per OperationId.
    /// </summary>
    public LoadUnitCellReleaseOutcome ReleaseLoadUnitCell(ReleaseLoadUnitCellRequest request)
    {
        lock (_gate)
        {
            var removed = _positions.RemoveAll(p => p.LoadUnitId == request.LoadUnitId && p.CellId == request.CellId && p.Reserved);
            foreach (var hold in _holds.Values.Where(h => h.LoadUnitId == request.LoadUnitId && h.CellId == request.CellId).ToList())
                _holds.Remove(hold.MissionId);

            var outcome = removed > 0 ? LoadUnitCellReleaseOutcome.Released : LoadUnitCellReleaseOutcome.NotHeld;
            Journal(WarehouseRoutes.ReleaseLoadUnitCell,
                $"load unit {request.LoadUnitId} gives back cell {request.CellId}: {outcome} ({request.Reason})");
            return outcome;
        }
    }

    /// <summary>
    /// <c>POST reservations/candidates</c>: the cells the operator can choose from in the WCS editor,
    /// the first <see cref="StorageCellCandidatesRequest.Limit"/> of the same search as
    /// <see cref="ReserveStorage"/>. A reading: nothing is reserved.
    /// </summary>
    public IReadOnlyList<StorageCellCandidate> ListStorageCellCandidates(StorageCellCandidatesRequest request)
    {
        lock (_gate)
        {
            ExpireOrphanHolds();

            return
            [
                .. FreeStorageCells(request.LoadUnitId, request.WarehouseId, request.SearchAreaIds, request.ExcludedCellIds,
                        request.ReachableDeviceIds)
                    .Take(StorageCellCandidatesRequest.Limit)
                    .Select(c => new StorageCellCandidate(c.Id, c.DoorCodeA, c.AreaId, _areas[c.AreaId].Description, c.Column,
                        c.Floor, c.Description, _positions.Count(p => p.CellId == c.Id && !p.Reserved)))
            ];
        }
    }

    /// <summary>
    /// <c>POST reservations/cell</c>: reserve the precise cell the operator chose, named by its door A.
    /// Refusals are answers in the body (unknown door, door B, cell taken, unknown load unit).
    /// </summary>
    public StorageCellReservationResult ReserveStorageCell(ReserveStorageCellRequest request)
    {
        lock (_gate)
        {
            ExpireOrphanHolds();

            var cell = _cells.Values.FirstOrDefault(c => SameDoor(c.DoorCodeA, request.DoorCode));
            if (cell == null)
            {
                return _cells.Values.Any(c => SameDoor(c.DoorCodeB, request.DoorCode))
                    ? new StorageCellReservationResult(StorageCellReservationOutcome.NotADoorA, null, $"{request.DoorCode} is a door B")
                    : new StorageCellReservationResult(StorageCellReservationOutcome.UnknownDoor, null, $"no cell has the door {request.DoorCode}");
            }

            if (!_loadUnits.ContainsKey(request.LoadUnitId))
                return new StorageCellReservationResult(StorageCellReservationOutcome.UnknownLoadUnit, null,
                    $"load unit {request.LoadUnitId} is unknown");

            if (!FreeStorageCells(request.LoadUnitId, cell.WarehouseId, [], [], []).Contains(cell))
                return new StorageCellReservationResult(StorageCellReservationOutcome.CellNotAvailable, null,
                    $"cell {cell.Id} is taken, locked or not a storage cell");

            Hold(request.MissionId, request.LoadUnitId, cell, WarehouseRoutes.ReserveStorageCell);
            return new StorageCellReservationResult(StorageCellReservationOutcome.Reserved, Reservation(request.MissionId, cell), null);
        }
    }

    // ------------------------------------------------------------------------------------------
    // Load units: where they are, what they are, and the two facts that move them
    // ------------------------------------------------------------------------------------------

    /// <summary><c>GET load-units/{loadUnitId}/description</c>: code, type, handling mode and dimensions. Null = unknown (404).</summary>
    public LoadUnitDescription? DescribeLoadUnit(decimal loadUnitId)
    {
        lock (_gate)
        {
            if (!_loadUnits.TryGetValue(loadUnitId, out var lu)) return null;

            var handlingMode = _loadUnitTypes.TryGetValue(lu.TypeId, out var type) ? type.HandlingMode : 0;
            return new LoadUnitDescription(lu.Id, lu.Code, lu.TypeId, handlingMode, lu.Tare, lu.Height, lu.Width, lu.Depth, lu.Weight);
        }
    }

    /// <summary><c>GET load-unit-types/{loadUnitTypeId}/handling-mode</c>. Null = unknown type (404).</summary>
    public LoadUnitTypeHandlingMode? GetLoadUnitTypeHandlingMode(decimal loadUnitTypeId)
    {
        lock (_gate)
            return _loadUnitTypes.TryGetValue(loadUnitTypeId, out var type) ? new LoadUnitTypeHandlingMode(type.Id, type.HandlingMode) : null;
    }

    /// <summary><c>GET load-units/by-code/{code}</c>: the id of the load unit with this label. Null = no such code (404).</summary>
    public LoadUnitIdentity? ResolveLoadUnitByCode(string code)
    {
        lock (_gate)
        {
            var lu = _loadUnits.Values.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
            return lu == null ? null : new LoadUnitIdentity(lu.Id);
        }
    }

    /// <summary>
    /// <c>GET load-units/by-code/{code}/stored</c>: is the load unit with this label already in a
    /// storage cell? The WCS refuses to let a load unit that is already stored enter again.
    /// </summary>
    public bool IsLoadUnitStored(string code)
    {
        lock (_gate)
        {
            var lu = _loadUnits.Values.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
            return lu != null && PhysicalPosition(lu.Id) is { } position && IsStorageCell(position.CellId);
        }
    }

    /// <summary><c>GET load-units/{loadUnitId}/location</c>: the cell the load unit is physically in, and at what depth. Null = nowhere (404).</summary>
    public LoadUnitLocation? LocateLoadUnit(decimal loadUnitId)
    {
        lock (_gate)
            return PhysicalPosition(loadUnitId) is { } position ? new LoadUnitLocation(loadUnitId, position.CellId, position.Depth) : null;
    }

    /// <summary>
    /// <c>GET load-units/{loadUnitId}/assignment-state</c>: is the load unit committed to a shipping
    /// note? Never here (the sample has no shipping notes). Always an answer, also for an unknown load unit.
    /// </summary>
    public LoadUnitAssignmentState GetLoadUnitAssignmentState(decimal loadUnitId) => new(loadUnitId, AssignedToNote: false);

    /// <summary>
    /// <c>POST load-units/find</c>: where the load unit is, within the areas or cells the WCS allows
    /// and with the lock filters it asks. Null (404) = not found within those limits, an answer.
    /// </summary>
    public LoadUnitSearchResult? FindLoadUnit(FindLoadUnitRequest request)
    {
        lock (_gate)
        {
            if (!_loadUnits.TryGetValue(request.LoadUnitId, out var lu) || PhysicalPosition(lu.Id) is not { } position) return null;

            var cell = _cells[position.CellId];
            if (request.AllowedAreaIds is { Count: > 0 } areas && !areas.Contains(cell.AreaId)) return null;
            if (request.AllowedCellIds is { Count: > 0 } cells && !cells.Contains(cell.Id)) return null;
            if (!Matches(request.LoadUnitLock, lu.Locked) || !Matches(request.CellLock, cell.Locked)) return null;

            return new LoadUnitSearchResult(lu.Id, cell.Id);
        }
    }

    /// <summary>
    /// <c>POST load-units/find-by-reference</c>: which stored load unit carries this article and can
    /// be brought to this bay (it must be in one of the bay's picking areas). The outcome is in the body.
    /// </summary>
    public ReferenceSearchResult FindLoadUnitByReference(FindLoadUnitByReferenceRequest request)
    {
        lock (_gate)
        {
            var areas = _cells.TryGetValue(request.BufferCellId, out var bay) ? bay.PickingAreaIds : [];
            if (areas.Count == 0) return new ReferenceSearchResult(ReferenceSearchOutcome.NoPickingAreas, 0, 0);

            // Areas in their order of priority, then the first unlocked load unit in an unlocked cell.
            foreach (var areaId in areas)
            {
                var found = _loadUnits.Values
                    .Where(lu => lu.Reference == request.ReferenceId && !lu.Locked)
                    .Select(lu => (lu, position: PhysicalPosition(lu.Id)))
                    .Where(x => x.position != null && _cells[x.position.CellId].AreaId == areaId &&
                                _cells[x.position.CellId].Locked is CellLockState.None or CellLockState.Loading)
                    .OrderBy(x => x.lu.Id)
                    .FirstOrDefault();

                if (found.position != null)
                    return new ReferenceSearchResult(ReferenceSearchOutcome.Found, found.lu.Id, found.position.CellId);
            }

            return new ReferenceSearchResult(ReferenceSearchOutcome.NotFound, 0, 0);
        }
    }

    /// <summary>
    /// <c>POST load-units/pickup-door</c>: from which door the load unit is picked, and which load units
    /// stand in front of it. A cell of the sample has one door and one place: door A, no obstacle.
    /// Null (404) = the cell is unknown.
    /// </summary>
    public PickUpDoorChoice? ChoosePickUpDoor(decimal loadUnitId, decimal cellId)
    {
        lock (_gate)
            return _cells.TryGetValue(cellId, out var cell) ? new PickUpDoorChoice(CellDoor.A, cell.DoorCodeA, 0, []) : null;
    }

    /// <summary>
    /// <c>POST load-units/allocate</c>: the fact "the load unit is now physically in this cell". It
    /// comes when a load unit is stored and when it reaches a device linked to a cell, such as a bay.
    /// The reserved position in that cell becomes physical, any other physical position of the load
    /// unit goes. Returns the reason of a refusal, or null: an unknown load unit, door or cell is
    /// refused (400), and the WCS parks the request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Which cell.</b> With <see cref="AllocateLoadUnitRequest.StorageDoorCode"/> (a storage into a
    /// real cell) the cell is the one with that door code, and
    /// <see cref="AllocateLoadUnitRequest.CellId"/> is ignored: that is what the contract says, and an
    /// adapter must not key the storage on <c>CellId</c> even where today the two agree. Without a
    /// door code (an operator cell, such as a bay) the cell is <c>CellId</c>.
    /// </para>
    /// <para>
    /// <b>ResetAssignment</b> means "first delete any previous assignment of the load unit": the
    /// shipping-note assignment a WMS with shipping notes keeps (see <see cref="GetLoadUnitAssignmentState"/>).
    /// The sample has no shipping notes, so the flag changes nothing here. It is <b>not</b> about cell
    /// reservations: a reservation the load unit holds in another cell stays, because it belongs to a
    /// mission and the WCS gives it back itself (<c>reservations/release-load-unit-cell</c>,
    /// <c>reservations/release</c>).
    /// </para>
    /// </remarks>
    public string? AllocateLoadUnit(AllocateLoadUnitRequest request)
    {
        lock (_gate)
        {
            if (!_loadUnits.TryGetValue(request.LoadUnitId, out var lu)) return $"load unit {request.LoadUnitId} is unknown";

            SampleCell? cell;
            if (request.StorageDoorCode is { Length: > 0 } doorCode)
            {
                cell = _cells.Values.FirstOrDefault(c => SameDoor(c.DoorCodeA, doorCode) || SameDoor(c.DoorCodeB, doorCode));
                if (cell == null) return $"no cell has the door {doorCode}";
            }
            else if (!_cells.TryGetValue(request.CellId, out cell)) return $"cell {request.CellId} is unknown";

            // A WMS with shipping notes deletes the load unit's note assignment here when
            // request.ResetAssignment is true. The sample has none.
            _positions.RemoveAll(p => p.LoadUnitId == lu.Id && (!p.Reserved || p.CellId == cell.Id));
            _positions.Add(new SamplePosition
            {
                LoadUnitId = lu.Id,
                CellId = cell.Id,
                Reserved = false,
                Depth = request.StorageDepth > 0 ? request.StorageDepth : cell.Depth
            });
            lu.Shipped = false;

            var how = request.StorageDoorCode is { Length: > 0 } door ? $"stored through door {door}" : "operator cell";
            Journal(WarehouseRoutes.AllocateLoadUnit, $"load unit {lu.Id} is in cell {cell.Id} ({how})");
            return null;
        }
    }

    /// <summary>
    /// <c>POST load-units/release</c>: the fact "the load unit left the automation" (unloaded from an
    /// outfeed bay). Its positions go and it is marked shipped. Returns the reason of a refusal, or null.
    /// </summary>
    public string? ReleaseLoadUnit(ReleaseLoadUnitRequest request)
    {
        lock (_gate)
        {
            if (!_loadUnits.TryGetValue(request.LoadUnitId, out var lu)) return $"load unit {request.LoadUnitId} is unknown";

            _positions.RemoveAll(p => p.LoadUnitId == lu.Id);
            foreach (var hold in _holds.Values.Where(h => h.LoadUnitId == lu.Id).ToList()) _holds.Remove(hold.MissionId);
            lu.Shipped = true;

            Journal(WarehouseRoutes.ReleaseLoadUnit, $"load unit {lu.Id} left the automation");
            return null;
        }
    }

    // ------------------------------------------------------------------------------------------
    // Work: what the WCS pulls on every turn of its master loop, and what it reports back
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>GET notes/pending-work</c>: shipping-note assignments ready for the automation. The sample has
    /// no shipping notes, so the queue is empty; a WMS that has them answers here the rows it already
    /// assigned and validated (the WCS does not assign).
    /// </summary>
    public IReadOnlyList<PendingNote> GetPendingNoteWork(bool limitOneShippingNotePerBuffer) => [];

    /// <summary>
    /// <c>GET extractions/pending-work</c>: the open extraction orders with the rows still to serve.
    /// <b>Pull model</b>: the WCS asks on every turn and queues what it can serve; the same row comes
    /// again until it is extracted, and the WCS recognises the rows it already queued.
    /// </summary>
    public IReadOnlyList<PendingExtraction> GetPendingExtractionWork()
    {
        lock (_gate)
        {
            return
            [
                .. _orders.Where(o => !o.Closed).Select(o => new PendingExtraction(o.Id, o.PickingCellId, o.BufferAreaIds,
                    [
                        .. o.Rows.Where(r => r.State != SampleRowState.Extracted)
                            .Select(r => new PendingExtractionRow(r.Id, r.LoadUnitId, _loadUnits[r.LoadUnitId].Locked, ProcessingId: o.Id))
                    ]))
            ];
        }
    }

    /// <summary>
    /// <c>POST extractions/outcomes</c>: what happened to some extraction rows. The WMS writes the
    /// states and closes an order itself when nothing is left to extract. Rows the WCS could not serve
    /// (not reachable, locked, in another extraction) stay waiting and are offered again: a real WMS
    /// may instead cancel them after a while and tell an operator.
    /// </summary>
    /// <remarks>
    /// <b>Find the rows by their own id.</b> The request comes from two places of the WCS: its master
    /// loop reports the rows of one order after asking for pending work (header = that order), and its
    /// durable queue reports "extracted" when a mission picks a load unit, with <b>header 0</b> - rows of
    /// any order, named by row id only. An empty list is legal (the WCS reports a turn with nothing to say).
    /// </remarks>
    public void ReportExtractionOutcomes(ReportExtractionOutcomesRequest request)
    {
        lock (_gate)
        {
            var orders = request.ExtractionHeaderId == 0
                ? _orders
                : _orders.Where(o => o.Id == request.ExtractionHeaderId).ToList();

            foreach (var outcome in request.Outcomes)
            {
                var order = orders.FirstOrDefault(o => o.Rows.Any(r => r.Id == outcome.ExtractionRowId));
                var row = order?.Rows.Single(r => r.Id == outcome.ExtractionRowId);
                if (order == null || row == null || row.State == SampleRowState.Extracted) continue;

                // The same outcome on every turn (a row that stays not reachable) is written once.
                if (row.LastOutcome != outcome.Status)
                    Journal(WarehouseRoutes.ExtractionOutcomes, $"order {order.Id}, row {row.Id} (load unit {row.LoadUnitId}): {outcome.Status}");

                row.LastOutcome = outcome.Status;
                row.State = outcome.Status switch
                {
                    ExtractionRowStatus.Extracted => SampleRowState.Extracted,
                    ExtractionRowStatus.InExtraction => SampleRowState.InExtraction,
                    _ => SampleRowState.Waiting
                };
            }

            foreach (var order in orders.Where(o => !o.Closed && o.Rows.All(r => r.State == SampleRowState.Extracted)))
            {
                order.Closed = true;
                Journal(WarehouseRoutes.ExtractionOutcomes, $"order {order.Id} closed: every row extracted");
            }
        }
    }

    /// <summary>
    /// <c>POST extractions/paused-among</c>: which of these rows belong to an order that is no longer in
    /// progress (closed, or unknown to the WMS). The WCS takes them out of its queue. A set: order
    /// does not matter.
    /// </summary>
    public IReadOnlyList<decimal> GetPausedExtractionRows(IReadOnlyList<decimal> rowIds)
    {
        lock (_gate)
        {
            var inProgress = _orders.Where(o => !o.Closed).SelectMany(o => o.Rows).Select(r => r.Id).ToHashSet();
            return [.. rowIds.Distinct().Where(id => !inProgress.Contains(id))];
        }
    }

    /// <summary>
    /// <c>POST notes/paused-among</c>: the twin of <see cref="GetPausedExtractionRows"/> for shipping-note
    /// assignments. The sample has none in progress, so every id asked is answered as paused.
    /// </summary>
    public IReadOnlyList<decimal> GetPausedNoteAssignments(IReadOnlyList<decimal> assignmentIds) => [.. assignmentIds.Distinct()];

    /// <summary>
    /// <c>POST load-units/left-extraction-queue</c>: the fact "the load unit left the WCS's extraction
    /// queue" (an operator removed it, or a change of cell). The WMS's consequence: the load unit's
    /// reserved position goes; with an assignment, the shipping note would become assignable again.
    /// </summary>
    public void ReportLoadUnitLeftExtractionQueue(LoadUnitLeftExtractionQueueRequest request)
    {
        lock (_gate)
        {
            _positions.RemoveAll(p => p.LoadUnitId == request.LoadUnitId && p.Reserved);
            Journal(WarehouseRoutes.LoadUnitLeftExtractionQueue,
                $"load unit {request.LoadUnitId} left the extraction queue (assignment {request.AssignmentId})");
        }
    }

    /// <summary>
    /// <c>POST assignments/picked-extraction-became-deposit</c>: an extraction the WCS had already picked
    /// was sent back to storage by an operator, so it will not reach the bay: reopen the extraction row
    /// (unless the order is already closed). The assignment side is about shipping notes: nothing to
    /// reopen here. Sent by a durable queue: the endpoint keeps a receipt per OperationId.
    /// </summary>
    public PickedExtractionBecameDepositResult ReportPickedExtractionBecameDeposit(PickedExtractionBecameDepositRequest request)
    {
        lock (_gate)
        {
            var rowOutcome = PickedExtractionReopenOutcome.NothingToReopen;
            if (request.ExtractionRowId > 0 && _orders.FirstOrDefault(o => o.Rows.Any(r => r.Id == request.ExtractionRowId)) is { } order)
            {
                if (order.Closed)
                {
                    rowOutcome = PickedExtractionReopenOutcome.OrderAlreadyClosed;
                }
                else
                {
                    var row = order.Rows.Single(r => r.Id == request.ExtractionRowId);
                    row.State = SampleRowState.Waiting;
                    rowOutcome = PickedExtractionReopenOutcome.Reopened;
                }
            }

            Journal(WarehouseRoutes.PickedExtractionBecameDeposit,
                $"load unit {request.LoadUnitId}: picked extraction became a deposit, row {request.ExtractionRowId} {rowOutcome}");
            return new PickedExtractionBecameDepositResult(PickedExtractionReopenOutcome.NothingToReopen, rowOutcome, null);
        }
    }

    /// <summary>
    /// The facts about shipping-note assignments (<c>notes/reject-assignment</c>,
    /// <c>assignments/mission-left-unserved</c>, <c>assignments/mission-served</c>,
    /// <c>assignments/queued-extraction-unservable</c>, <c>assignments/confirm-served</c>). The sample has
    /// no shipping notes: it writes them in the journal. A WMS with shipping notes deletes or confirms
    /// the assignment here; every one of these calls must be idempotent.
    /// </summary>
    public void RecordFact(string route, FormattableString what)
    {
        lock (_gate)
            Journal(route, what);
    }

    // ------------------------------------------------------------------------------------------
    // The sample's own admin endpoint (not part of the contract)
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Create an extraction order for these load units towards a bay cell: what an order picker or an
    /// ERP would do in a real WMS. The WCS sees it on its next turn (<c>extractions/pending-work</c>).
    /// Returns the order, or the reason it cannot be created.
    /// </summary>
    public (SampleExtractionOrder? Order, string? Error) CreateExtractionOrder(IReadOnlyList<decimal> loadUnitIds, decimal bayCellId)
    {
        lock (_gate)
        {
            if (loadUnitIds.Count == 0) return (null, "an order needs at least one load unit");
            if (!_cells.TryGetValue(bayCellId, out var bay) || bay.PickingAreaIds.Count == 0)
                return (null, $"cell {bayCellId} is not a bay served by a storage area");

            foreach (var id in loadUnitIds)
            {
                if (!_loadUnits.ContainsKey(id)) return (null, $"load unit {id} is unknown");
                if (OpenRows().Any(r => r.LoadUnitId == id)) return (null, $"load unit {id} is already in an open extraction order");
            }

            var order = new SampleExtractionOrder
            {
                Id = _nextOrderId++,
                PickingCellId = bay.Id,
                BufferAreaIds = bay.PickingAreaIds,
                Rows = [.. loadUnitIds.Distinct().Select(id => new SampleExtractionRow { Id = _nextRowId++, LoadUnitId = id })],
                CreatedAt = _clock.GetUtcNow()
            };
            _orders.Add(order);
            Journal("sample-admin", $"extraction order {order.Id} created: load units {string.Join(", ", loadUnitIds)} to cell {bay.Id}");
            return (order, null);
        }
    }

    /// <summary>
    /// One request received, with the status it was answered: the last ones are in the state, to see
    /// what the WCS asks and when. A request answered the same way as one of the last few is counted
    /// on that one instead of taking a line: the WCS asks for pending work on every turn of its master
    /// loop, about once a second, and those lines would push everything else out.
    /// </summary>
    public void RecordCall(string method, string path, int status)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();

            // The WCS alternates its two pending-work questions, so "the same as one of the last few"
            // rather than "the same as the last one".
            var repeated = _calls.FindLastIndex(c => c.Method == method && c.Path == path && c.Status == status);
            if (repeated >= 0 && repeated >= _calls.Count - 4)
            {
                var call = _calls[repeated];
                _calls.RemoveAt(repeated);
                _calls.Add(call with { LastAt = now, Count = call.Count + 1 });
                return;
            }

            _calls.Add(new SampleCall(now, now, method, path, status, 1));
            if (_calls.Count > CallsKept) _calls.RemoveAt(0);
        }
    }

    /// <summary>Everything the sample holds, for a person to look at (<c>GET /sample-admin/state</c>).</summary>
    public SampleState Snapshot()
    {
        lock (_gate)
        {
            return new SampleState(
                [
                    .. _cells.Values.OrderBy(c => c.Id).Select(c => new SampleCellState(c.Id, c.DoorCodeA, _areas[c.AreaId].Description,
                        c.Locked, [.. _positions.Where(p => p.CellId == c.Id).Select(p => new SamplePositionState(p.LoadUnitId, p.Reserved, p.Depth))]))
                ],
                [
                    .. _loadUnits.Values.OrderBy(l => l.Id).Select(l => new SampleLoadUnitState(l.Id, l.Code, l.Reference,
                        PhysicalPosition(l.Id)?.CellId, l.Locked, l.Shipped))
                ],
                [.. _holds.Values.Select(h => new SampleHoldState(h.MissionId, h.LoadUnitId, h.CellId, h.RenewedAt))],
                [
                    .. _orders.Select(o => new SampleOrderState(o.Id, o.PickingCellId, o.Closed,
                        [.. o.Rows.Select(r => new SampleRowStateDto(r.Id, r.LoadUnitId, r.State.ToString(), r.LastOutcome?.ToString()))]))
                ],
                [.. _journal],
                [.. _calls]);
        }
    }

    // ------------------------------------------------------------------------------------------
    // Helpers (called with the lock held)
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// The storage cells where this load unit can go, best first. A cell qualifies when it is in an
    /// automatic storage area of the warehouse (and in the areas asked, if any), is not excluded, is
    /// reachable by one of the devices the WCS names (if it names any), is not locked for storing, and
    /// is empty - or holds only this load unit's own reservation.
    /// </summary>
    /// <remarks>
    /// The request's search mode and fullness preference do not change anything here: with one place
    /// per cell every free cell is as full as any other. A WMS with deep or multi-place cells uses them.
    /// </remarks>
    private IEnumerable<SampleCell> FreeStorageCells(decimal loadUnitId, decimal warehouseId, IReadOnlyList<decimal> areaIds,
        IReadOnlyList<decimal> excludedCellIds, IReadOnlyList<Guid> reachableDeviceIds) =>
        _cells.Values
            .Where(c => c.WarehouseId == warehouseId)
            .Where(c => _areas[c.AreaId] is { Kind: SampleAreaKind.Storage, Automatic: true })
            .Where(c => areaIds.Count == 0 || areaIds.Contains(c.AreaId))
            .Where(c => !excludedCellIds.Contains(c.Id))
            .Where(c => reachableDeviceIds.Count == 0 || c.AccessDeviceIds.Any(reachableDeviceIds.Contains))
            .Where(c => c.Locked is CellLockState.None or CellLockState.Unloading)
            .Where(c => _positions.All(p => p.CellId != c.Id || (p.LoadUnitId == loadUnitId && p.Reserved)))
            .OrderBy(c => c.Column).ThenBy(c => c.Floor);

    /// <summary>Give the load unit a reserved position in the cell (its only one), owned by the mission when there is one.</summary>
    private void Hold(Guid missionId, decimal loadUnitId, SampleCell cell, string route)
    {
        _positions.RemoveAll(p => p.LoadUnitId == loadUnitId && p.Reserved);
        foreach (var old in _holds.Values.Where(h => h.LoadUnitId == loadUnitId).ToList()) _holds.Remove(old.MissionId);

        _positions.Add(new SamplePosition { LoadUnitId = loadUnitId, CellId = cell.Id, Reserved = true });

        // A reservation without a mission (the WCS reserves before the mission exists, for example to
        // move an obstacle) holds the cell through the reserved position only.
        if (missionId != Guid.Empty)
            _holds[missionId] = new SampleStorageHold { MissionId = missionId, LoadUnitId = loadUnitId, CellId = cell.Id, RenewedAt = _clock.GetUtcNow() };

        Journal(route, $"cell {cell.Id} (door {cell.DoorCodeA}) reserved for load unit {loadUnitId}, mission {missionId}");
    }

    /// <summary>Free the reservations nobody asked about for longer than the lifetime: their missions are gone.</summary>
    private void ExpireOrphanHolds()
    {
        var limit = _clock.GetUtcNow() - _reservationLifetime;
        foreach (var hold in _holds.Values.Where(h => h.RenewedAt < limit).ToList())
        {
            _holds.Remove(hold.MissionId);
            _positions.RemoveAll(p => p.LoadUnitId == hold.LoadUnitId && p.CellId == hold.CellId && p.Reserved);
            Journal("expiry", $"reservation of mission {hold.MissionId} on cell {hold.CellId} expired");
        }
    }

    private StorageReservation Reservation(Guid missionId, SampleCell cell) =>
        new(missionId, StorageReservationOutcome.CellFound, cell.Id, cell.DoorCodeA, cell.Partition, cell.Depth, NeedsCompaction: false);

    private static StorageReservation NoReservation(Guid missionId, StorageReservationOutcome outcome) =>
        new(missionId, outcome, 0, string.Empty, null, 0, NeedsCompaction: false);

    private CellDescription Describe(SampleCell cell) =>
        new(cell.Id, cell.AreaId, cell.WarehouseId, cell.Depth, Inverted: false, cell.Locked, AllowedLoadUnitTypeId: null, GroupId: null,
            GroupOrder: 0, cell.DoorCodeA, cell.DoorCodeB, DoorCount: string.IsNullOrEmpty(cell.DoorCodeB) ? 1 : 2, MultiDepthStorage: false);

    private SamplePosition? PhysicalPosition(decimal loadUnitId) => _positions.FirstOrDefault(p => p.LoadUnitId == loadUnitId && !p.Reserved);

    private bool IsStorageCell(decimal cellId) => _cells.TryGetValue(cellId, out var cell) && _areas[cell.AreaId].Kind == SampleAreaKind.Storage;

    private IEnumerable<SampleExtractionRow> OpenRows() =>
        _orders.Where(o => !o.Closed).SelectMany(o => o.Rows).Where(r => r.State != SampleRowState.Extracted);

    private static bool SameDoor(string door, string code) => door.Length > 0 && string.Equals(door, code, StringComparison.OrdinalIgnoreCase);

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static bool Matches(LoadUnitLockFilter filter, bool locked) => filter switch
    {
        LoadUnitLockFilter.Locked => locked,
        LoadUnitLockFilter.Unlocked => !locked,
        _ => true
    };

    /// <summary>
    /// The cell lock filters of <c>load-units/find</c>: "free for unloading" means a load unit can be
    /// picked from the cell (not locked for picking), "free for loading" that one can be stored into it.
    /// </summary>
    private static bool Matches(CellLockFilter filter, CellLockState state) => filter switch
    {
        CellLockFilter.Loading => state == CellLockState.Loading,
        CellLockFilter.Unloading => state == CellLockState.Unloading,
        CellLockFilter.Locked => state != CellLockState.None,
        CellLockFilter.Unlocked => state == CellLockState.None,
        CellLockFilter.FreeForLoading => state is CellLockState.None or CellLockState.Unloading,
        CellLockFilter.FreeForUnloading => state is not CellLockState.Unloading,
        _ => true
    };

    /// <summary>
    /// Ids are decimals on the wire, and the WCS may send 5001 as 5001.0: the journal writes them as
    /// numbers a person reads (5001), whatever the culture of the machine.
    /// </summary>
    private void Journal(string route, FormattableString what)
    {
        var arguments = what.GetArguments()
            .Select(a => a is decimal d ? d.ToString("0.##", CultureInfo.InvariantCulture) : a)
            .ToArray();
        _journal.Add(new SampleJournalEntry(_clock.GetUtcNow(), route, string.Format(CultureInfo.InvariantCulture, what.Format, arguments)));
        if (_journal.Count > JournalLength) _journal.RemoveAt(0);
    }
}

/// <summary>The answer of <c>GET /sample-admin/state</c>.</summary>
public sealed record SampleState(
    IReadOnlyList<SampleCellState> Cells,
    IReadOnlyList<SampleLoadUnitState> LoadUnits,
    IReadOnlyList<SampleHoldState> Reservations,
    IReadOnlyList<SampleOrderState> ExtractionOrders,
    IReadOnlyList<SampleJournalEntry> Journal,
    IReadOnlyList<SampleCall> RecentCalls);

public sealed record SampleCall(DateTimeOffset FirstAt, DateTimeOffset LastAt, string Method, string Path, int Status, int Count);

public sealed record SampleCellState(decimal CellId, string DoorCode, string Area, CellLockState Locked, IReadOnlyList<SamplePositionState> Positions);

public sealed record SamplePositionState(decimal LoadUnitId, bool Reserved, int Depth);

public sealed record SampleLoadUnitState(decimal LoadUnitId, string Code, string? Reference, decimal? CellId, bool Locked, bool Shipped);

public sealed record SampleHoldState(Guid MissionId, decimal LoadUnitId, decimal CellId, DateTimeOffset RenewedAt);

public sealed record SampleOrderState(decimal OrderId, decimal PickingCellId, bool Closed, IReadOnlyList<SampleRowStateDto> Rows);

public sealed record SampleRowStateDto(decimal RowId, decimal LoadUnitId, string State, string? LastOutcome);
