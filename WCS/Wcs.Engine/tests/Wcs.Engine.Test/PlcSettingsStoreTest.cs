using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Api;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Xunit;
using Wcs.Engine.Collections;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Le connessioni al PLC e le due configurazioni a riga singola sul database del WCS.
/// </summary>
/// <remarks>
/// Le due configurazioni nascono nel costruttore leggendo la riga, e ogni setter la riscrive: il
/// test scrive con un oggetto e rilegge con un altro, senza scrittore, direttamente sullo store.
/// </remarks>
public sealed class PlcSettingsStoreTest : IDisposable
{
    private readonly WcsDatabase _database = WcsDatabase.InMemory();

    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;

    public PlcSettingsStoreTest()
    {
        Navigator.Store = _database;
        // PlcSettingsFacade.ReplaceAsync esige il motore fermo (PlantQueries.RequireStopped): lo
        // stato e' statico, e qui non si eredita da chi e' passato prima.
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
    }

    public void Dispose()
    {
        Navigator.NavStatus = _previousStatus;
        Navigator.Store = null;
        _database.Dispose();
    }

    [Fact]
    public async Task ConnectionsComeBackByKeyAndTheDriverStatusIsWrittenOnThem()
    {
        await using (var context = _database.CreateContext())
        {
            await PlcSettingsStore.UpsertConnectionAsync(context, new PlcConnection
                { Id = 1, Description = "PLC linea 1", PlcAddress = "10.0.0.10", PcmPort = 2000, LocalAddress = "10.0.0.2", LocalPort = 2001 });
            await PlcSettingsStore.UpsertConnectionAsync(context, new PlcConnection { Id = 2, PlcAddress = "10.0.0.20", LocalAddress = "10.0.0.2" });
            await context.SaveChangesAsync();
        }

        await WcsStateWrites.WriteAndWaitAsync(context =>
            PlcSettingsStore.UpdateStatusAsync(context, 1, opProgress: 42, statusSenderChannel: 1, statusReceiverChannel: 1,
                outboundChannelStatus: 2, inboundChannelStatus: 2));

        // Una connessione che il database non conosce non e' un errore: il driver ne ha di prova.
        await WcsStateWrites.WriteAndWaitAsync(context => PlcSettingsStore.UpdateStatusAsync(context, 99, 1, 1, 1, 1, 1));

        await using var reading = _database.CreateContext();
        var wanted = await PlcSettingsStore.ConnectionsAsync(reading, [1]);
        var first = Assert.Single(wanted);
        Assert.Equal("10.0.0.10", first.PlcAddress);
        Assert.Equal(2000, first.PcmPort);
        Assert.Equal(42, first.OpProgress);
        Assert.Equal(2, first.OutboundChannelStatus);
        Assert.Equal(2, reading.PlcConnections.Count());
    }

    [Fact]
    public async Task ReservingCommandIdsPersistsEachNextValueForThePlc()
    {
        await using (var context = _database.CreateContext())
        {
            context.PlcConnections.Add(new PlcConnection { Id = 7, LastCommandId = 41 });
            await context.SaveChangesAsync();
        }

        await using (var first = _database.CreateContext())
            Assert.Equal(42, PlcSettingsStore.ReserveNextCommandId(first, 7));

        await using (var second = _database.CreateContext())
            Assert.Equal(43, PlcSettingsStore.ReserveNextCommandId(second, 7));

        await using var reading = _database.CreateContext();
        Assert.Equal(43, (await reading.PlcConnections.SingleAsync(p => p.Id == 7)).LastCommandId);
    }

    /// <summary>
    /// Un blocco sale di tutto il suo passo in una transazione sola e restituisce il primo numero:
    /// il contatore persistito e' il limite superiore, quindi dopo un riavvio nessun numero del
    /// blocco torna in circolo.
    /// </summary>
    [Fact]
    public async Task ReservingABlockAdvancesTheStoredValueByTheWholeBlockAndReturnsItsFirstId()
    {
        await using (var context = _database.CreateContext())
        {
            context.PlcConnections.Add(new PlcConnection { Id = 7, LastCommandId = 41 });
            await context.SaveChangesAsync();
        }

        await using (var reserving = _database.CreateContext())
            Assert.Equal(42, PlcSettingsStore.ReserveCommandIdBlock(reserving, 7, 100));

        await using var reading = _database.CreateContext();
        Assert.Equal(141, (await reading.PlcConnections.SingleAsync(p => p.Id == 7)).LastCommandId);
    }

    /// <summary>
    /// <b>Una transazione ogni cento comandi, non una per comando.</b> Il fornitore che il motore
    /// passa al driver (<c>IConnectionServices.ReserveCommandId</c>) consegna i cento numeri del blocco dalla memoria, in ordine e senza buchi, e torna su
    /// SQLite soltanto al centunesimo - che e' il primo del blocco successivo.
    /// </summary>
    [Fact]
    public async Task TheInstalledProviderHandsOutABlockFromMemoryAndReservesTheNextOneOnlyWhenItRunsOut()
    {
        await using (var context = _database.CreateContext())
        {
            context.PlcConnections.Add(new PlcConnection { Id = 7, LastCommandId = 41 });
            await context.SaveChangesAsync();
        }

        var previousStore = Navigator.Store;
        var factory = new RecordingConnectionFactory();
        using var installed = ConnectionFactoryScope.Install(factory);
        try
        {
            Navigator.Store = _database;
            Func<decimal, int> provider = factory.Services!.ReserveCommandId;

            var firstBlock = Enumerable.Range(0, DeviceConnections.CommandIdBlockSize).Select(_ => provider(7)).ToArray();
            Assert.Equal(Enumerable.Range(42, DeviceConnections.CommandIdBlockSize), firstBlock);
            await using (var reading = _database.CreateContext())
                Assert.Equal(141, (await reading.PlcConnections.SingleAsync(p => p.Id == 7)).LastCommandId);

            Assert.Equal(142, provider(7));
            await using (var reading = _database.CreateContext())
                Assert.Equal(241, (await reading.PlcConnections.SingleAsync(p => p.Id == 7)).LastCommandId);
        }
        finally
        {
            Navigator.Store = previousStore;
        }
    }

    [Fact]
    public async Task ReservingAfterTheLargestCommandIdFailsWithoutChangingTheStoredValue()
    {
        await using (var context = _database.CreateContext())
        {
            context.PlcConnections.Add(new PlcConnection { Id = 7, LastCommandId = Int32.MaxValue });
            await context.SaveChangesAsync();
        }

        await using (var reserving = _database.CreateContext())
            Assert.Throws<InvalidOperationException>(() => PlcSettingsStore.ReserveNextCommandId(reserving, 7));

        await using var reading = _database.CreateContext();
        Assert.Equal(Int32.MaxValue, (await reading.PlcConnections.SingleAsync(p => p.Id == 7)).LastCommandId);
    }

    [Fact]
    public async Task ReplacingPlcConfigurationPreservesTheReservedCommandId()
    {
        await using (var context = _database.CreateContext())
        {
            context.PlcConnections.Add(new PlcConnection { Id = 7, Description = "before save", LastCommandId = 43 });
            await context.SaveChangesAsync();
        }

        await PlcSettingsFacade.ReplaceAsync([new PlcConnection { Id = 7, Description = "after save" }]);

        await using var reserving = _database.CreateContext();
        Assert.Equal(44, PlcSettingsStore.ReserveNextCommandId(reserving, 7));
    }

    [Fact]
    public async Task ReplacingPlcConfigurationDoesNotRestartAnExhaustedCommandSequence()
    {
        await using (var context = _database.CreateContext())
        {
            context.PlcConnections.Add(new PlcConnection { Id = 7, LastCommandId = Int32.MaxValue });
            await context.SaveChangesAsync();
        }

        await PlcSettingsFacade.ReplaceAsync([new PlcConnection { Id = 7, Description = "saved configuration" }]);

        await using (var reserving = _database.CreateContext())
            Assert.Throws<InvalidOperationException>(() => PlcSettingsStore.ReserveNextCommandId(reserving, 7));

        await using var reading = _database.CreateContext();
        Assert.Equal(Int32.MaxValue, (await reading.PlcConnections.SingleAsync(p => p.Id == 7)).LastCommandId);
    }

    [Fact]
    public void RecordSettingsSurviveTheRoundTripThroughTheObject()
    {
        var written = new RecordDataFromAsiInfo
        {
            RECORD_LU_HEIGHT = true,
            RECORD_LU_WEIGTH = true,
            DECODE_LU_LABEL_CODES = true,
            NEWLU_ID_LU_TYPE = 3,
            NEWLU_ID_REFERENCE = "RIF-1",
            NEWLU_QT = 12.5m,
            NEWLU_ID_QT = "Q1"
        };

        var read = new RecordDataFromAsiInfo();

        Assert.True(read.RECORD_LU_HEIGHT);
        Assert.True(read.RECORD_LU_WEIGTH);
        Assert.False(read.RECORD_LU_WIDTH);
        Assert.True(read.DECODE_LU_LABEL_CODES);
        Assert.Equal(3, read.NEWLU_ID_LU_TYPE);
        Assert.Equal("RIF-1", read.NEWLU_ID_REFERENCE);
        Assert.Equal(12.5m, read.NEWLU_QT);
        Assert.Equal("Q1", read.NEWLU_ID_QT);
        Assert.Equal(written.NEWLU_QT, read.NEWLU_QT);
    }

    [Fact]
    public void CheckSettingsSurviveTheRoundTripThroughTheObject()
    {
        _ = new CheckDataFromAsiInfo
        {
            COMP_LU_HEIGHT = CheckDataFromAsiInfo.CompareTypes.LessThan,
            LU_HEIGHT = 1800,
            COMP_LU_WEIGTH = CheckDataFromAsiInfo.CompareTypes.MoreThan,
            LU_WEIGTH = 50,
            COMP_LU_CODE = CheckDataFromAsiInfo.CompareTypes.Equal,
            LU_CODE = "ABC"
        };

        var read = new CheckDataFromAsiInfo();

        Assert.Equal(CheckDataFromAsiInfo.CompareTypes.LessThan, read.COMP_LU_HEIGHT);
        Assert.Equal(1800, read.LU_HEIGHT);
        Assert.Equal(CheckDataFromAsiInfo.CompareTypes.MoreThan, read.COMP_LU_WEIGTH);
        Assert.Equal(50, read.LU_WEIGTH);
        Assert.Equal(CheckDataFromAsiInfo.CompareTypes.Equal, read.COMP_LU_CODE);
        Assert.Equal("ABC", read.LU_CODE);
        Assert.Equal(CheckDataFromAsiInfo.CompareTypes.NoComparision, read.COMP_LU_TEMP);
    }
}
