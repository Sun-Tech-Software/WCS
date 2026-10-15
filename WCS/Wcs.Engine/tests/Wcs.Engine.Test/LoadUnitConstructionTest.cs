using Wcs.Connection.Interface.ConnectionData;
using Wcs.Connection.Interface.Events;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Items.Managers;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Compito 10: le due letture tolte dai setter sincroni di <see cref="PathMission.LuId"/> e
/// <see cref="PathMission.IdLuType"/>, e la responsabilita' spostata su chi costruisce la missione.
/// Il "punto che conta" del brief: codice, tipo, modalita' di movimentazione e tara popolati come
/// prima, con una chiamata al gestionale invece delle due che i vecchi setter facevano in cascata.
/// </summary>
/// <remarks>
/// Dei quattro punti di costruzione ufficiali (dispositivo, gestore dei gruppi, gestore delle
/// connessioni, caricamento dallo store) qui se ne collaudano gli ultimi due, e la ragione era un
/// SQL Server: <c>AutomationDevice.AddMissionToPickUp</c> e
/// <c>AutomationDeviceGroupManager.AddMissionToGroupPickUp</c> chiamano entrambi
/// <c>DbInfo.GetLuStoringDepth</c> subito dopo, che fino al compito 12 leggeva
/// <c>Navigator.Database</c> senza guardia - lo stesso limite gia' incontrato nei compiti 5, 7 e 9
/// per i punti che toccavano quella connessione direttamente. Oggi quella lettura passa dal
/// confine e il limite non c'e' piu' (con il compito 16 nemmeno il campo), quindi quei due punti
/// sarebbero raggiungibili: allargare la copertura non e' lavoro di questo compito. La copertura
/// sui due punti sta nel
/// meccanismo condiviso (<see cref="TheSetterIsPureEvenWithoutAWarehouseConfigured"/> e
/// <see cref="DescribeLoadUnitAnswersCodeTypeHandlingModeAndTareInOneCall"/>): tutti e quattro i
/// punti seguono lo stesso schema, una <see cref="IWarehouse.DescribeLoadUnitAsync"/> e
/// l'assegnazione diretta dei quattro campi.
/// </remarks>
public sealed class LoadUnitConstructionTest : IDisposable
{
    private readonly IWarehouse? _warehousePrecedente = Navigator.Warehouse;
    private readonly NavigatorStatusTypes _statoPrecedente = Navigator.NavStatus;

    public LoadUnitConstructionTest()
    {
        Navigator.NavStatus = NavigatorStatusTypes.Recover;
    }

    public void Dispose()
    {
        ActiveMissions.Instance.RemoveAll(_ => true);
        Navigator.Warehouse = _warehousePrecedente;
        Navigator.NavStatus = _statoPrecedente;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Il punto che conta del brief, sul terzo punto di costruzione ("nel gestore delle
    /// connessioni", <see cref="AutomationDeviceConnectionManager.NewLuOnASI"/>): quando il PLC manda
    /// gia' l'id dell'unita' di carico, una sola domanda al gestionale basta per codice, tipo,
    /// modalita' di movimentazione e tara - non piu' le due chiamate che i vecchi setter di
    /// LuId/IdLuType facevano in cascata.
    /// </summary>
    [Fact]
    public async Task NewLuOnAsiWithAKnownIdPopulatesTheMissionWithOneCall()
    {
        var warehouse = new FakeWarehouse();
        warehouse.LoadUnits[42] = new LoadUnitDescription(42, "UDC-042", LoadUnitTypeId: 7, HandlingMode: 3,
            Tare: 15, Height: 100, Width: 80, Depth: 120, Weight: 250);
        Navigator.Warehouse = warehouse;

        var device = new AutomationDevice(Guid.NewGuid());
        var connectionManager = new AutomationDeviceConnectionManager(device);

        await connectionManager.NewLuOnASI(new NewLuOnAsiEventArgs(new NewLuOnAsiData { LuId = 42 }));

        var mission = ActiveMissions.Instance.FirstOrDefault(m => m != null && m.LuId == 42);
        Assert.NotNull(mission);
        Assert.Equal("UDC-042", mission.LuCode);
        Assert.Equal(7m, mission.IdLuType);
        Assert.Equal(3, mission.HandlingMode);
        Assert.Equal(15m, mission.Tare);
        Assert.Single(warehouse.Calls, c => c == nameof(IWarehouse.DescribeLoadUnitAsync));
        Assert.DoesNotContain(warehouse.Calls, c => c == nameof(IWarehouse.ResolveLoadUnitByCodeAsync));
    }

    /// <summary>
    /// Stesso punto di costruzione, ramo col solo codice: senza un id gia' noto servono due
    /// domande, non una - prima <see cref="IWarehouse.ResolveLoadUnitByCodeAsync"/> per risolvere
    /// l'identificativo, poi <see cref="IWarehouse.DescribeLoadUnitAsync"/> per il resto. E' l'unico
    /// dei sei punti di costruzione trovati in cui una sola chiamata non basta, perche' senza id non
    /// si puo' ancora chiedere la descrizione.
    /// </summary>
    [Fact]
    public async Task NewLuOnAsiWithOnlyACodeResolvesFirstThenDescribes()
    {
        var warehouse = new FakeWarehouse();
        warehouse.LoadUnitIdsByCode["UDC-777"] = 777;
        warehouse.LoadUnits[777] = new LoadUnitDescription(777, "UDC-777", LoadUnitTypeId: 9, HandlingMode: 1,
            Tare: 20, Height: 0, Width: 0, Depth: 0, Weight: 0);
        Navigator.Warehouse = warehouse;

        var device = new AutomationDevice(Guid.NewGuid());
        var connectionManager = new AutomationDeviceConnectionManager(device);

        await connectionManager.NewLuOnASI(new NewLuOnAsiEventArgs(new NewLuOnAsiData { LuCode = "UDC-777" }));

        var mission = ActiveMissions.Instance.FirstOrDefault(m => m != null && m.LuId == 777);
        Assert.NotNull(mission);
        Assert.Equal("UDC-777", mission.LuCode);
        Assert.Equal(9m, mission.IdLuType);
        Assert.Equal(1, mission.HandlingMode);
        Assert.Equal(20m, mission.Tare);
        Assert.Equal(2, warehouse.Calls.Count);
        Assert.Equal(nameof(IWarehouse.ResolveLoadUnitByCodeAsync), warehouse.Calls[0]);
        Assert.Equal(nameof(IWarehouse.DescribeLoadUnitAsync), warehouse.Calls[1]);
    }

    /// <summary>
    /// Un codice che il gestionale non conosce: <see cref="IWarehouse.ResolveLoadUnitByCodeAsync"/>
    /// risponde null (compito 10, «non trovato» e' una risposta), la missione resta con l'id a zero
    /// e non si prova nemmeno a descriverla - non c'e' niente da descrivere senza un id.
    /// </summary>
    [Fact]
    public async Task NewLuOnAsiWithAnUnknownCodeLeavesTheIdAtZeroWithoutDescribing()
    {
        var warehouse = new FakeWarehouse();
        Navigator.Warehouse = warehouse;

        var device = new AutomationDevice(Guid.NewGuid());
        var connectionManager = new AutomationDeviceConnectionManager(device);

        await connectionManager.NewLuOnASI(new NewLuOnAsiEventArgs(new NewLuOnAsiData { LuCode = "SCONOSCIUTO" }));

        var mission = ActiveMissions.Instance.FirstOrDefault(m => m != null && m.LuCode == "SCONOSCIUTO");
        Assert.NotNull(mission);
        Assert.Equal(0m, mission.LuId);
        Assert.Single(warehouse.Calls, c => c == nameof(IWarehouse.ResolveLoadUnitByCodeAsync));
        Assert.DoesNotContain(warehouse.Calls, c => c == nameof(IWarehouse.DescribeLoadUnitAsync));
    }

    /// <summary>
    /// Il quarto punto di costruzione, "nel caricamento dallo store"
    /// (<see cref="MissionStore.ToPathMission"/>): la riga persistita porta gia' codice, tipo,
    /// modalita' e tara, quindi qui la chiamata al gestionale non e' una invece di due - e' zero.
    /// Prima della riparazione del compito 10 le due chiamate in cascata dei vecchi setter
    /// giravano comunque (e venivano scartate dai valori successivi dello stesso inizializzatore):
    /// lavoro sprecato a ogni riavvio, non solo un rischio latente.
    /// </summary>
    [Fact]
    public void ToPathMissionNeedsNoWarehouseCallBecauseTheRowAlreadyHasEverything()
    {
        var warehouse = new FakeWarehouse { Unavailable = true };
        Navigator.Warehouse = warehouse;

        var row = new Mission
        {
            MissionId = Guid.NewGuid(),
            LoadUnitId = 55,
            LoadUnitCode = "UDC-055",
            LoadUnitTypeId = 4,
            HandlingMode = 2,
            Tare = 30,
            Status = (short)MissionStatusTypes.Stop,
            MissionType = (short)MissionTypes.None,
            OperationType = (short)OperationTypes.None
        };

        var mission = MissionStore.ToPathMission(row);

        Assert.Equal(55m, mission.LuId);
        Assert.Equal("UDC-055", mission.LuCode);
        Assert.Equal(4m, mission.IdLuType);
        Assert.Equal(2, mission.HandlingMode);
        Assert.Equal(30m, mission.Tare);
        Assert.Empty(warehouse.Calls);
    }

    /// <summary>
    /// Il punto centrale della riparazione: <see cref="PathMission.LuId"/> e
    /// <see cref="PathMission.IdLuType"/> non toccano piu' <see cref="Navigator.Warehouse"/> dentro
    /// il loro setter. Prima del compito 10 questo stesso test avrebbe sollevato
    /// <see cref="NullReferenceException"/> da dentro <c>DbInfo.GetLuData</c>/<c>GetHandlingMode</c>
    /// appena impostato <see cref="PathMission.LuId"/>, con <c>Navigator.Warehouse</c> non impostato.
    /// </summary>
    [Fact]
    public void TheSetterIsPureEvenWithoutAWarehouseConfigured()
    {
        Navigator.Warehouse = null;

        var mission = new PathMission(Guid.NewGuid())
        {
            LuId = 999,
            IdLuType = 3
        };

        Assert.Equal(999m, mission.LuId);
        Assert.Equal(3m, mission.IdLuType);
        // Nessuna eccezione: il setter non ha chiesto niente al gestionale.
    }

    /// <summary>
    /// Il contratto stesso, indipendente da un punto di costruzione particolare:
    /// <see cref="IWarehouse.DescribeLoadUnitAsync"/> risponde codice, tipo, modalita' di
    /// movimentazione e tara in una sola risposta - la domanda unica che sostituisce
    /// <c>DbInfo.GetLuCode</c>, <c>DbInfo.GetLuData</c> e <c>DbInfo.GetHandlingMode</c>.
    /// </summary>
    [Fact]
    public async Task DescribeLoadUnitAnswersCodeTypeHandlingModeAndTareInOneCall()
    {
        var warehouse = new FakeWarehouse();
        warehouse.LoadUnits[8] = new LoadUnitDescription(8, "UDC-008", LoadUnitTypeId: 2, HandlingMode: 5,
            Tare: 40, Height: 10, Width: 20, Depth: 30, Weight: 500);

        var description = await warehouse.DescribeLoadUnitAsync(8);

        Assert.NotNull(description);
        Assert.Equal("UDC-008", description.Code);
        Assert.Equal(2m, description.LoadUnitTypeId);
        Assert.Equal(5m, description.HandlingMode);
        Assert.Equal(40, description.Tare);
        Assert.Single(warehouse.Calls, c => c == nameof(IWarehouse.DescribeLoadUnitAsync));
    }

    /// <summary>
    /// <see cref="IWarehouse.IsLoadUnitStoredAsync"/> (ex <c>DbInfo.IsLuIntoWarehouse</c>), chiamata
    /// reale in <c>AutomationDeviceConnectionManager.ProcessLuData</c>.
    /// </summary>
    [Fact]
    public async Task IsLoadUnitStoredAnswersAccordingToTheCode()
    {
        var warehouse = new FakeWarehouse();
        warehouse.StoredLoadUnitCodes.Add("UDC-A");

        Assert.True(await warehouse.IsLoadUnitStoredAsync("UDC-A"));
        Assert.False(await warehouse.IsLoadUnitStoredAsync("UDC-B"));
    }

    /// <summary>
    /// <see cref="IWarehouse.ResolveLoadUnitByCodeAsync"/> (ex <c>DbInfo.GetLuId</c>): un codice
    /// senza unita' di carico e' una risposta di dominio (null), non un guasto.
    /// </summary>
    [Fact]
    public async Task ResolveLoadUnitByCodeAnswersNullWhenTheCodeIsUnknown()
    {
        var warehouse = new FakeWarehouse();
        warehouse.LoadUnitIdsByCode["NOTO"] = 4711;

        Assert.Equal(4711m, (await warehouse.ResolveLoadUnitByCodeAsync("NOTO"))?.LoadUnitId);
        Assert.Null(await warehouse.ResolveLoadUnitByCodeAsync("SCONOSCIUTO"));
    }
}
