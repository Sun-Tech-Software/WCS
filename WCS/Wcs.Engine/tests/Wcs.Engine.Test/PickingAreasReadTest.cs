using Wcs.Engine.ORM;
using PathMission = Wcs.Engine.Items.PathMission;
using Wcs.Engine.Persistence;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// La lettura delle aree di prelievo di una cella polmone, e la sua conseguenza: su lista vuota il
/// motore <b>cancella la missione</b> dal database del WCS.
/// </summary>
/// <remarks>
/// <para>
/// <c>DbInfo.PickingBufferAreas</c> leggeva <c>AREE_PRELIEVO_PICKING</c> in T-SQL e distingueva
/// «nessuna area» da «la lettura non e' riuscita» con un <see langword="null"/> che tre chiamanti su
/// tre dovevano ricordarsi di guardare. Con il compito 2 la domanda passa dal confine
/// (<see cref="IWarehouse.GetPickingBufferAreasAsync"/>): la lista vuota e' il dato, il guasto e'
/// <see cref="WarehouseUnavailableException"/>, e nessuno dei due puo' piu' essere preso per
/// l'altro per distrazione.
/// </para>
/// <para>
/// Quello che questa prova sorveglia non cambia: <c>AddMissionToPickUpQueue</c> e' chiamata da
/// <c>LoadMissions</c> dentro la ripresa all'avvio, e con il gestionale irraggiungibile le missioni
/// non devono essere cancellate. La cancellazione avviene <b>prima</b> della chiamata al confine
/// che segue, quindi non basta che quella sollevi: deve sollevare questa.
/// </para>
/// </remarks>
public sealed class PickingAreasReadTest : IDisposable
{
    private const decimal BufferCellId = 5;

    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly FakeWarehouse _warehouse = new();

    public PickingAreasReadTest()
    {
        Navigator.NavStatus = NavigatorStatusTypes.Recover;
        Navigator.Store = _database;
        Navigator.StateWriter = null;
        Navigator.Warehouse = _warehouse;
    }

    public void Dispose()
    {
        Navigator.Store = null;
        Navigator.Warehouse = _previousWarehouse;
        Navigator.NavStatus = _previousStatus;
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<PathMission> AMissionInTheQueue(decimal bufferCellId)
    {
        var mission = new PathMission(Guid.NewGuid())
        {
            LuId = 1234,
            LuCode = "SSCC-1",
            MissionType = MissionTypes.Extraction,
            OperationType = OperationTypes.Outbound,
            BufferCellId = bufferCellId,
            ActiveFilter = true
        };

        await using var context = _database.CreateContext();
        await MissionStore.UpsertAsync(context, MissionStore.Snapshot(mission));
        await context.SaveChangesAsync();

        return mission;
    }

    /// <summary>
    /// <b>La prova che conta:</b> con il gestionale che non risponde la missione resta in
    /// <c>wcs.db</c> e si ritenta al giro dopo. Il guasto risale - non c'e' nessuna lista vuota
    /// inventata che faccia cancellare la riga con l'unita' di carico ancora da prelevare.
    /// </summary>
    [Fact]
    public async Task AMissionIsNotDeletedWhenThePickingAreasCouldNotBeRead()
    {
        var mission = await AMissionInTheQueue(BufferCellId);

        _warehouse.Unavailable = true;

        await Assert.ThrowsAsync<WarehouseUnavailableException>(
            () => DbInfo.AddMissionToPickUpQueue(mission));

        await using var reading = _database.CreateContext();
        Assert.Single(reading.Missions);
    }

    /// <summary>
    /// La meta' che deve riuscire: senza questa, un <c>AddMissionToPickUpQueue</c> che sollevasse
    /// sempre passerebbe la prova qui sopra. Il polmone non serve nessuna area - lista vuota, che e'
    /// un dato - e la missione non ha dove andare: la riga sparisce.
    /// </summary>
    [Fact]
    public async Task AMissionWhoseBufferServesNoAreaIsDeleted()
    {
        var mission = await AMissionInTheQueue(BufferCellId);

        await DbInfo.AddMissionToPickUpQueue(mission);

        Assert.Contains(nameof(IWarehouse.GetPickingBufferAreasAsync), _warehouse.Calls);

        await using var reading = _database.CreateContext();
        Assert.Empty(reading.Missions);
    }

    /// <summary>
    /// La cella che non c'e' - identificativo zero - non ha aree, e la domanda non parte nemmeno: e'
    /// un ramo suo (<c>case { BufferCellId: 0 }</c>), a monte della lettura. Vale la pena provarlo a
    /// parte perche' e' il solo punto in cui la missione si cancella <b>senza</b> aver chiesto
    /// niente al gestionale.
    /// </summary>
    [Fact]
    public async Task AMissionWithoutABufferCellIsDeletedWithoutAskingTheWarehouse()
    {
        var mission = await AMissionInTheQueue(0);

        await DbInfo.AddMissionToPickUpQueue(mission);

        Assert.Empty(_warehouse.Calls);

        await using var reading = _database.CreateContext();
        Assert.Empty(reading.Missions);
    }

    /// <summary>
    /// L'ordine e' il dato: le aree arrivano a <see cref="IWarehouse.FindLoadUnitAsync"/> nello
    /// stesso ordine di priorita' in cui il gestionale le ha date, perche' li' la prima che risponde
    /// vince. Una lista riordinata - o raccolta in un insieme - cambierebbe la cella da cui si
    /// preleva senza che niente lo segnali.
    /// </summary>
    [Fact]
    public async Task ThePickingAreasReachTheSearchInPriorityOrder()
    {
        var mission = await AMissionInTheQueue(BufferCellId);

        _warehouse.PickingBufferAreas[BufferCellId] = [3, 1, 2];

        await DbInfo.AddMissionToPickUpQueue(mission);

        var search = Assert.Single(_warehouse.LoadUnitSearches);
        Assert.Equal(new decimal[] { 3, 1, 2 }, search.AllowedAreaIds);
        Assert.Equal(mission.LuId, search.LoadUnitId);
    }
}
