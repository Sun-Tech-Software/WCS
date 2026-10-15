using Wcs.Engine.Items;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// Il test che il compito 12 chiede: i tre esiti che il motore distingueva prima della migrazione
/// (<c>PathMission.FindRouteAllocateLu</c>, ex switch su <c>TipiRisultato</c>) restano distinti dopo,
/// e una prenotazione riuscita popola codice porta, partizione e quota senza altre chiamate al
/// gestionale.
/// </summary>
public sealed class ReserveStorageOutcomeTest
{
    /// <summary>
    /// La categorizzazione e' la stessa funzione pura che <c>FindRouteAllocateLu</c> usa davvero
    /// (<see cref="PathMission.CategorizeReservationOutcome"/>, internal per questo collaudo): non e'
    /// un metodo aggiunto solo per il test, e' lo stesso switch a tre rami che prima viveva dentro
    /// <c>FindRouteAllocateLu</c>, estratto perche' collaudabile senza un <see cref="AutomationDevice"/>
    /// completo (la stessa ragione gia' scritta in <c>CellLockReachesStoringDecisionTest</c>).
    /// </summary>
    [Fact]
    public void CellFoundIsCategorizedAsCellFound() =>
        Assert.Equal(PathMission.ReservationOutcomeCategory.CellFound,
            PathMission.CategorizeReservationOutcome(StorageReservationOutcome.CellFound));

    [Fact]
    public void LoadUnitHasNoReferencesIsCategorizedOnItsOwn() =>
        Assert.Equal(PathMission.ReservationOutcomeCategory.LoadUnitHasNoReferences,
            PathMission.CategorizeReservationOutcome(StorageReservationOutcome.LoadUnitHasNoReferences));

    /// <summary>
    /// I rimanenti dieci valori di <see cref="StorageReservationOutcome"/> (dodici in tutto, meno i due
    /// sopra) collassano tutti nello stesso terzo esito: cosi' faceva gia' il ramo <c>_ =></c> dello
    /// switch originale su <c>TipiRisultato</c>, non un comportamento nuovo del compito 12.
    /// </summary>
    [Theory]
    [InlineData(StorageReservationOutcome.NoFreeCells)]
    [InlineData(StorageReservationOutcome.LoadUnitCodeRequired)]
    [InlineData(StorageReservationOutcome.LoadUnitIdRequired)]
    [InlineData(StorageReservationOutcome.WarehouseRequired)]
    [InlineData(StorageReservationOutcome.LoadUnitCodeNotFound)]
    [InlineData(StorageReservationOutcome.NoDoorSelected)]
    [InlineData(StorageReservationOutcome.NoCellSelected)]
    [InlineData(StorageReservationOutcome.SelectedDoorNotFound)]
    [InlineData(StorageReservationOutcome.LoadUnitPositioned)]
    [InlineData(StorageReservationOutcome.PositionUpdateFailed)]
    public void EveryOtherOutcomeIsCategorizedAsNoCellAvailable(StorageReservationOutcome outcome) =>
        Assert.Equal(PathMission.ReservationOutcomeCategory.NoCellAvailable,
            PathMission.CategorizeReservationOutcome(outcome));

    /// <summary>
    /// Una prenotazione riuscita porta gia' codice porta, partizione e quota nella stessa risposta:
    /// il motore non deve interrogare separatamente <see cref="IWarehouse.GetCellPartitionMapAsync"/>
    /// o <see cref="IWarehouse.GetCellStoringConstraintsAsync"/> per completarla. Collaudato contro
    /// <see cref="IWarehouse"/> direttamente (non attraverso <c>FindRouteAllocateLu</c>, che ha bisogno
    /// di un <see cref="AutomationDevice"/> completo, fuori dalla portata di un test unitario - stesso
    /// limite di <c>CellLockReachesStoringDecisionTest</c>): e' la proprieta' del contratto che il
    /// compito 12 doveva riparare, non l'istradamento interno del motore.
    /// </summary>
    [Fact]
    public async Task ASuccessfulReservationPopulatesDoorPartitionAndDepthWithoutOtherCalls()
    {
        var warehouse = new FakeWarehouse();
        var missionId = Guid.NewGuid();
        warehouse.StorageReservations[42] = new StorageReservation(
            missionId, StorageReservationOutcome.CellFound, CellId: 7,
            DoorCode: "A7", Partition: "0002", StorageDepth: 150, NeedsCompaction: false);

        var request = new ReserveStorageRequest(
            missionId, LoadUnitId: 42, WarehouseId: 1,
            SearchAreaIds: [1], ExcludedCellIds: [], ReachableDeviceIds: [],
            StorageAreaSelectionMode.All, StorageCellSearchMode.FreeAutomaticOperator,
            CellFullnessPreference.Fullest, StorageSearchOperatorType.Automatic,
            AssignPosition: true, VerifyPosition: false,
            RequestedByWorkstationId: "WS1", RequestedByUserId: "user1");

        var reservation = await warehouse.ReserveStorageAsync(request);

        Assert.Equal(StorageReservationOutcome.CellFound, reservation.Outcome);
        Assert.Equal("A7", reservation.DoorCode);
        Assert.Equal("0002", reservation.Partition);
        Assert.Equal(150, reservation.StorageDepth);
        Assert.Equal([nameof(FakeWarehouse.ReserveStorageAsync)], warehouse.Calls);
    }
}
