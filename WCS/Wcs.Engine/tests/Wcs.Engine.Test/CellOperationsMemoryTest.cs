using Wcs.Engine.Items;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// Le cinque operazioni del compito 9, viste dal lato del motore: la mappa deve stare (stessa
/// chiave, stessa forma, stessa semantica) prima di fidarsi che l'operazione risponda giusto.
/// </summary>
public sealed class CellOperationsMemoryTest : IDisposable
{
    private readonly IWarehouse? _warehousePrecedente = Navigator.Warehouse;

    public void Dispose() => Navigator.Warehouse = _warehousePrecedente;

    /// <summary>
    /// <see cref="PathMission.GetCellContentAsync"/> (ex <c>DbInfo.GetLUsInCell</c>): non è
    /// memorizzata per missione come <see cref="PathMission.DescribeCellAsync(decimal,System.Threading.CancellationToken)"/>
    /// - due chiamate sulla stessa cella devono raggiungere il gestionale due volte, perché il
    /// contenuto di una cella può cambiare fra l'una e l'altra.
    /// </summary>
    [Fact]
    public async Task CellContentIsNotMemorizedBecauseItChangesDuringTheMission()
    {
        var warehouse = new FakeWarehouse();
        warehouse.CellContents[13] = [201, 202];
        Navigator.Warehouse = warehouse;

        var mission = new PathMission(Guid.NewGuid());

        var first = await mission.GetCellContentAsync(13);
        var second = await mission.GetCellContentAsync(13);

        Assert.Equal([201m, 202m], first);
        Assert.Equal([201m, 202m], second);
        Assert.Equal(2, warehouse.Calls.Count(c => c == nameof(IWarehouse.GetCellContentAsync)));
    }

    /// <summary>
    /// <see cref="PathMission.GetCellBusyStateAsync"/> (ex <c>IsCellBusyByExtraction</c>/
    /// <c>IsCellBusyByPickUp</c>/<c>IsCellBusyByStoring</c>): tre stati distinti nella stessa
    /// risposta, non un booleano - il punto che conta più di tutti nel brief del compito 9.
    /// </summary>
    [Fact]
    public async Task CellBusyStateCarriesTheThreeStatesSeparately()
    {
        var warehouse = new FakeWarehouse();
        warehouse.CellBusyStates[21] = new CellBusyState(Extraction: true, PickUp: false, Storing: true);
        Navigator.Warehouse = warehouse;

        var mission = new PathMission(Guid.NewGuid());

        var busyState = await mission.GetCellBusyStateAsync(21, loadUnitIdToExclude: 999);

        Assert.True(busyState.Extraction);
        Assert.False(busyState.PickUp);
        Assert.True(busyState.Storing);
    }

    /// <summary>
    /// <see cref="PathMission.GetCellStoringConstraintsAsync"/> (ex <c>QuotaDepositoUdc</c>): la
    /// profondità teorica di deposito, non memorizzata per lo stesso motivo del contenuto.
    /// </summary>
    [Fact]
    public async Task StoringConstraintsAreAskedEachTimeBecauseOccupancyChanges()
    {
        var warehouse = new FakeWarehouse();
        warehouse.CellStoringConstraintsByCell[7] = new CellStoringConstraints(TheoreticalStoreDepth: 350);
        Navigator.Warehouse = warehouse;

        var mission = new PathMission(Guid.NewGuid());

        var constraints = await mission.GetCellStoringConstraintsAsync(7, CellDoor.A, loadUnitDepth: 100, loadUnitIdToExclude: 0);

        Assert.Equal(350, constraints.TheoreticalStoreDepth);
    }

    /// <summary>
    /// <see cref="PathMission.SetCellLockStateAsync"/> (ex <c>DbInfo.SetLockCell</c>): scrive, non
    /// legge - la controfigura registra il codice porta e lo stato passati, non un valore da cella.
    /// </summary>
    [Fact]
    public async Task SetCellLockStateSendsTheDoorCodeAndTheRequestedState()
    {
        var warehouse = new FakeWarehouse();
        Navigator.Warehouse = warehouse;

        var mission = new PathMission(Guid.NewGuid());

        await mission.SetCellLockStateAsync("A123", CellLockState.Full);

        var change = Assert.Single(warehouse.CellLockStateChanges);
        Assert.Equal("A123", change.DoorCode);
        Assert.Equal(CellLockState.Full, change.LockState);
    }

    /// <summary>
    /// <see cref="IWarehouse.DescribeCellsByDoorAsync"/>: risponde una lista, non una cella sola -
    /// <c>CODICE_PORTA_A</c>/<c>CODICE_PORTA_B</c> non hanno un vincolo di unicità (compito 8), quindi
    /// una porta può risolvere più di una cella. Qui la controfigura, non il motore: i quattro
    /// chiamanti reali (in <c>ExecutableCommands</c>) non hanno una missione da cui partire, quindi
    /// non passano da <see cref="PathMission"/> - vedi il rapporto del compito 9.
    /// </summary>
    [Fact]
    public async Task DescribeCellsByDoorAnswersWithAListBecauseADoorCanResolveMoreThanOneCell()
    {
        var warehouse = new FakeWarehouse();
        warehouse.CellsByDoor["P1"] =
        [
            new CellDescription(11, 1, 1, Depth: 2, Inverted: false, Locked: CellLockState.None,
                AllowedLoadUnitTypeId: null, GroupId: null, GroupOrder: 0, DoorCodeA: "P1", DoorCodeB: "P1B",
                DoorCount: 2, MultiDepthStorage: false),
            new CellDescription(12, 1, 1, Depth: 2, Inverted: false, Locked: CellLockState.None,
                AllowedLoadUnitTypeId: null, GroupId: null, GroupOrder: 0, DoorCodeA: "P1", DoorCodeB: "P1B2",
                DoorCount: 2, MultiDepthStorage: false)
        ];

        var cells = await warehouse.DescribeCellsByDoorAsync("P1");

        Assert.Equal(2, cells.Count);
        Assert.Equal([11m, 12m], cells.Select(c => c.CellId));
    }
}
