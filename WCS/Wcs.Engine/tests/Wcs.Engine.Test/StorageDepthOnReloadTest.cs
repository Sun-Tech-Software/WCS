using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.ORM;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using AutomationDevice = Wcs.Engine.Items.AutomationDevice;
using PathMission = Wcs.Engine.Items.PathMission;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Compito 20: la quota di deposito che la ricarica delle missioni colma quando la riga non ce
/// l'ha (<c>DbInfo.PrepareLoadedMission</c>) arriva dal confine, non piu' da
/// <c>WmsBusinessArea.OperazioniUdc.GetQuotaUdc</c>. Non e' un'operazione nuova: la quota e' la
/// profondita' di <see cref="LoadUnitLocation"/>, che <see cref="IWarehouse.LocateLoadUnitAsync"/>
/// gia' prometteva - la stessa riga di <c>POSIZIONI_UDC</c> con <c>ASSEGNAZIONE = 0</c> che
/// <c>GetQuotaUdc</c> leggeva.
/// <para>
/// La ricarica passa da <c>DbInfo.RecoverFailure</c>, che e' l'unico ingresso interno di
/// <c>LoadMissions</c>. Il dispositivo nel diagramma serve solo a superare la sua guardia iniziale
/// (con il diagramma vuoto <c>RecoverFailure</c> esce subito): non e' collegato a uno stoccaggio,
/// cosi' la ricarica non arriva a <c>RecoverPickUpQueue</c>, che al tempo di questa prova avrebbe
/// letto <c>Navigator.Database</c> - la connessione a SQL Server, nulla in prova e cancellata con
/// il compito 16.
/// </para>
/// </summary>
public sealed class StorageDepthOnReloadTest : IDisposable
{
    private const decimal LoadUnitId = 6100m;

    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly NavigatorStatusTypes _previousNavStatus = Navigator.NavStatus;
    private readonly List<AutomationDevice> _devices = [];
    private readonly List<Guid> _missions = [];

    public StorageDepthOnReloadTest()
    {
        Navigator.Store = _database;

        // Il navigatore in marcia serve alla collezione, non al metodo sotto prova:
        // ActiveMissions.Add esce subito se lo stato e' Stop (il valore che il campo statico ha nei
        // test) o Pause, e la missione ricaricata non entrerebbe da nessuna parte.
        Navigator.NavStatus = NavigatorStatusTypes.Run;
    }

    public void Dispose()
    {
        // Prima si aspetta il lavoro di sfondo delle missioni ricaricate. Il ricaricamento imposta
        // PositionItemId, e il setter lancia PositionWork in un Task.Run che alla fine riscrive la
        // missione: senza attenderlo, sotto carico quella scrittura arrivava nella prova dopo
        // (era la riga (Run, null) estranea di ScrittureDellaMissioneTest; coda, voci 42 e 46).
        foreach (var missionId in _missions)
            if (ActiveMissions.Instance[missionId] is { } reloaded)
                Assert.True(reloaded.PositionWork.Wait(TimeSpan.FromSeconds(30)), $"PositionWork di {missionId} non finito");

        foreach (var missionId in _missions) ActiveMissions.Instance.RemoveAll(m => m.MissionId == missionId);

        foreach (var device in _devices)
        {
            PathDevices.Instance.Remove(device.ItemGuid);
            device.Dispose();
        }

        Navigator.NavStatus = _previousNavStatus;
        Navigator.Store = null;
        Navigator.Warehouse = _previousWarehouse;
        _database.Dispose();
    }

    /// <summary>
    /// Una missione di estrazione ricaricata senza quota la prende dal confine, e la quota e' la
    /// profondita' che <see cref="IWarehouse.LocateLoadUnitAsync"/> risponde.
    /// </summary>
    [Fact]
    public async Task AnExtractionMissionReloadedWithoutDepthTakesItFromTheBoundary()
    {
        var warehouse = new FakeWarehouse();
        warehouse.LoadUnitLocations[LoadUnitId] = new LoadUnitLocation(LoadUnitId, CellId: 4200m, StoringDepth: 3);
        Navigator.Warehouse = warehouse;

        var missionId = await SaveReloadableExtractionMissionAsync(storageDepth: 0);

        await DbInfo.RecoverFailure();

        Assert.Equal(3, ReloadedMission(missionId).StorageDepth);
        Assert.Single(warehouse.Calls, c => c == nameof(IWarehouse.LocateLoadUnitAsync));
    }

    /// <summary>
    /// L'unita' di carico senza posizione fisica: il confine risponde «non c'e'» (un 404, che il
    /// client traduce in <see langword="null"/>) e la quota resta zero, come rispondeva
    /// <c>GetQuotaUdc</c> quando la riga di <c>POSIZIONI_UDC</c> non c'era. E' una risposta di
    /// dominio, non un guasto.
    /// </summary>
    [Fact]
    public async Task ALoadUnitWithNoPhysicalPositionLeavesTheDepthAtZero()
    {
        var warehouse = new FakeWarehouse();
        Navigator.Warehouse = warehouse;

        var missionId = await SaveReloadableExtractionMissionAsync(storageDepth: 0);

        await DbInfo.RecoverFailure();

        Assert.Equal(0, ReloadedMission(missionId).StorageDepth);
        Assert.Single(warehouse.Calls, c => c == nameof(IWarehouse.LocateLoadUnitAsync));
    }

    /// <summary>
    /// La quota si colma solo se manca: una missione che la porta gia' non fa nessuna domanda al
    /// gestionale. E' la condizione <c>StorageDepth: 0</c> della guardia, ed e' quella che tiene
    /// bassa la quantita' di chiamate a ogni ricarica.
    /// </summary>
    [Fact]
    public async Task AMissionThatAlreadyCarriesItsDepthAsksTheWarehouseNothing()
    {
        var warehouse = new FakeWarehouse();
        warehouse.LoadUnitLocations[LoadUnitId] = new LoadUnitLocation(LoadUnitId, CellId: 4200m, StoringDepth: 3);
        Navigator.Warehouse = warehouse;

        var missionId = await SaveReloadableExtractionMissionAsync(storageDepth: 5);

        await DbInfo.RecoverFailure();

        Assert.Equal(5, ReloadedMission(missionId).StorageDepth);
        Assert.DoesNotContain(nameof(IWarehouse.LocateLoadUnitAsync), warehouse.Calls);
    }

    /// <summary>
    /// La riga minima che <c>LoadMissions</c> ricarica e che arriva fino alla guardia della quota:
    /// nessun codice porta (i due rami che li risolvono non hanno niente da fare, e quello che
    /// scarta la missione gia' a destinazione non scatta), tipo estrazione, e un'unita' di carico
    /// vera. Un dispositivo nel diagramma perche' <c>RecoverFailure</c> non esca subito.
    /// </summary>
    private async Task<Guid> SaveReloadableExtractionMissionAsync(int storageDepth)
    {
        var device = new AutomationDevice(Guid.NewGuid()) { DeviceType = DeviceTypes.CircularDistributor };
        PathDevices.Instance.Add(device);
        _devices.Add(device);

        var missionId = Guid.NewGuid();
        _missions.Add(missionId);

        await using var context = _database.CreateContext();
        context.Missions.Add(new Mission
        {
            MissionId = missionId,
            LoadUnitId = LoadUnitId,
            PositionItemId = device.ItemGuid,
            Date = DateTime.Now,
            MissionType = (short)MissionTypes.Extraction,
            OperationType = (short)OperationTypes.Outbound,
            ActiveFilter = true,
            StorageDepth = storageDepth
        });
        await context.SaveChangesAsync();

        return missionId;
    }

    /// <summary>
    /// La missione ricaricata e' un oggetto <b>nuovo</b>, non quello salvato: si cerca fra le
    /// attive. L'asserzione che sia entrata davvero e' qui e non nei test, perche' senza di lei un
    /// <c>FirstOrDefault</c> nullo si leggerebbe come «quota zero».
    /// </summary>
    private static PathMission ReloadedMission(Guid missionId)
    {
        var mission = ActiveMissions.Instance.FirstOrDefault(m => m != null && m.MissionId == missionId);
        Assert.NotNull(mission);
        return mission;
    }
}
