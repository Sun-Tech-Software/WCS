using Microsoft.EntityFrameworkCore;
using System.Data;
using Wcs.Common;
using Wcs.Engine.Api;
using Wcs.Engine.Persistence.Entities;
using static Wcs.Engine.Items.CheckDataFromAsiInfo;

namespace Wcs.Engine.Persistence;

/// <summary>
/// Le connessioni al PLC e le due configurazioni a riga singola sul database del WCS: le mappe
/// con gli oggetti in memoria e le operazioni.
/// </summary>
internal static class PlcSettingsStore
{
    #region Connessioni

    /// <summary>Le connessioni con le chiavi date: quelle usate da almeno un dispositivo.</summary>
    public static Task<List<PlcConnection>> ConnectionsAsync(WcsDbContext context, IReadOnlyCollection<long> ids) =>
        context.PlcConnections.AsNoTracking().Where(p => ids.Contains(p.Id)).OrderBy(p => p.Id).ToListAsync();

    /// <summary>
    /// Sostituisce l'intera configurazione delle connessioni senza importare o azzerare lo stato
    /// operativo delle righe che conservano lo stesso identificativo.
    /// </summary>
    public static async Task ReplaceConnectionsAsync(WcsDbContext context,
        IReadOnlyCollection<PlcConnection> connections)
    {
        var proposedIds = connections.Select(connection => connection.Id).ToHashSet();
        var stored = await context.PlcConnections.ToDictionaryAsync(connection => connection.Id);
        context.PlcConnections.RemoveRange(stored.Values.Where(connection =>
            Removed(connection.Id, proposedIds)));

        foreach (var proposed in connections)
        {
            if (stored.TryGetValue(proposed.Id, out var existing))
            {
                existing.Description = proposed.Description;
                existing.PlcAddress = proposed.PlcAddress;
                existing.PcmPort = proposed.PcmPort;
                existing.LocalAddress = proposed.LocalAddress;
                existing.LocalPort = proposed.LocalPort;
            }
            else
            {
                context.PlcConnections.Add(new PlcConnection
                {
                    Id = proposed.Id,
                    Description = proposed.Description,
                    PlcAddress = proposed.PlcAddress,
                    PcmPort = proposed.PcmPort,
                    LocalAddress = proposed.LocalAddress,
                    LocalPort = proposed.LocalPort
                });
            }
        }
    }

    /// <summary>La regola della cancellazione di <see cref="ReplaceConnectionsAsync"/>: una riga il cui Id non e' proposto.</summary>
    private static bool Removed(long storedId, IReadOnlySet<long> proposedIds) => !proposedIds.Contains(storedId);

    /// <summary>
    /// Le connessioni che <see cref="ReplaceConnectionsAsync"/> toglierebbe con
    /// <paramref name="connections"/> e che un dispositivo dell'impianto usa ancora, ciascuna con quei
    /// dispositivi; vuoto se nessuna. Lette dallo store (<c>PlantItems</c>), che e' la configurazione
    /// da cui il caricamento costruisce le connessioni del driver.
    /// </summary>
    /// <remarks>
    /// Un dispositivo usa la connessione <c>k</c> quando il suo <c>DeviceConnectionId</c> vale
    /// esattamente <c>k</c> e <c>k</c> e' maggiore di zero: e' la stessa regola di
    /// <c>DeviceConnections.OpenConnection</c> (<c>DeviceConnectionId &gt; 0</c>, poi
    /// <c>Instance[DeviceConnectionId]</c>). Zero - o meno - vuol dire «nessuna connessione»: il
    /// dispositivo «Connected» resta senza connessione e va OffLine (Run e' rifiutato finche' manca:
    /// <c>DeviceConnections.RequireConnectionsForRun</c>), e non blocca la rimozione nemmeno di una
    /// connessione con Id 0.
    /// <para>
    /// Il confronto si fa in memoria, non in SQL: <c>DeviceConnectionId</c> e' un decimale, che SQLite
    /// tiene come testo, e un'uguaglianza tradotta in SQL confronterebbe stringhe. Le righe sono quelle
    /// di un impianto, centinaia.
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyList<PlcConnectionInUse>> RemovedConnectionsInUseAsync(WcsDbContext context,
        IReadOnlyCollection<PlcConnection> connections)
    {
        var proposedIds = connections.Select(connection => connection.Id).ToHashSet();
        var removed = (await context.PlcConnections.AsNoTracking().Select(connection => connection.Id).ToListAsync())
            .Where(id => id > 0 && Removed(id, proposedIds))
            .ToHashSet();
        if (removed.Count == 0) return [];

        var devices = await context.PlantItems.AsNoTracking()
            .Select(item => new { item.ItemId, item.Name, item.DeviceConnectionId })
            .ToListAsync();
        return devices
            .Where(device => device.DeviceConnectionId > 0 && device.DeviceConnectionId == decimal.Truncate(device.DeviceConnectionId) &&
                             device.DeviceConnectionId <= long.MaxValue && removed.Contains((long)device.DeviceConnectionId))
            .GroupBy(device => (long)device.DeviceConnectionId)
            .OrderBy(group => group.Key)
            .Select(group => new PlcConnectionInUse(group.Key, [.. group
                .OrderBy(device => device.Name, StringComparer.Ordinal).ThenBy(device => device.ItemId)
                .Select(device => new PlcConnectionUser(device.ItemId, device.Name))]))
            .ToList();
    }

    /// <summary>
    /// Lo stato dei canali e l'avanzamento, scritti dal driver quando cambiano. Una connessione
    /// che il database non conosce viene ignorata: il driver puo' avere connessioni di prova.
    /// </summary>
    public static async Task UpdateStatusAsync(WcsDbContext context, long id, int opProgress,
        short statusSenderChannel, short statusReceiverChannel, short outboundChannelStatus, short inboundChannelStatus)
    {
        var existing = await context.PlcConnections.SingleOrDefaultAsync(p => p.Id == id);
        if (existing == null) return;

        existing.OpProgress = opProgress;
        existing.StatusSenderChannel = statusSenderChannel;
        existing.StatusReceiverChannel = statusReceiverChannel;
        existing.OutboundChannelStatus = outboundChannelStatus;
        existing.InboundChannelStatus = inboundChannelStatus;
    }

    public static async Task UpdateOpProgressAsync(WcsDbContext context, long id, int opProgress)
    {
        var existing = await context.PlcConnections.SingleOrDefaultAsync(p => p.Id == id);
        if (existing != null) existing.OpProgress = opProgress;
    }

    public static async Task UpsertConnectionAsync(WcsDbContext context, PlcConnection connection)
    {
        var existing = await context.PlcConnections.SingleOrDefaultAsync(p => p.Id == connection.Id);
        if (existing == null)
            context.PlcConnections.Add(connection);
        else
        {
            var lastCommandId = existing.LastCommandId;
            context.Entry(existing).CurrentValues.SetValues(connection);
            existing.LastCommandId = lastCommandId;
        }
    }

    /// <summary>Riserva e persiste il prossimo identificativo di comando del PLC richiesto.</summary>
    public static int ReserveNextCommandId(WcsDbContext context, long id) => ReserveCommandIdBlock(context, id, 1);

    /// <summary>
    /// Riserva <paramref name="count"/> identificativi consecutivi in una transazione sola, e
    /// restituisce il primo. Il contatore persistito sale subito di tutto il blocco: e' lui il limite
    /// superiore dopo un riavvio, quindi i numeri del blocco non ancora usati si saltano e nessun
    /// identificativo gia' consegnato torna mai in circolo.
    /// </summary>
    /// <remarks>
    /// <b>Perche' a blocchi.</b> Riservare un numero per comando voleva dire una transazione SQLite
    /// sincrona, fuori dalla coda di scrittura, per ogni movimento spedito al PLC - con il lock
    /// della connessione tenuto per tutta l'attesa. Mentre la coda teneva il file in scrittura, ogni
    /// invio aspettava il timeout di SQLite dietro un I/O che non lo riguardava, e allo scadere il
    /// comando non partiva affatto. Si veda <c>EngineConnectionServices.ReserveCommandId</c>, che tiene il
    /// blocco in memoria.
    /// </remarks>
    public static int ReserveCommandIdBlock(WcsDbContext context, long id, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        using var transaction = context.Database.BeginTransaction(IsolationLevel.Serializable);
        var existing = context.PlcConnections.SingleOrDefault(p => p.Id == id)
            ?? throw new InvalidOperationException($"PLC {id} not found while reserving a command identifier.");

        if (existing.LastCommandId > Int32.MaxValue - count)
            throw new InvalidOperationException($"PLC {id} command identifiers are exhausted.");

        var first = existing.LastCommandId + 1;
        existing.LastCommandId += count;
        context.SaveChanges();
        transaction.Commit();
        return first;
    }

    #endregion Connessioni

    #region Dati da registrare

    public static RecordDataSettings Snapshot(Items.RecordDataFromAsiInfo value) => new()
    {
        Id = RecordDataSettings.DefaultId,
        RecordContainerType = value.RECORD_LU_CONTAINER_TYPE,
        RecordContainerHeight = value.RECORD_LU_CONTAINER_SIZE_HEIGTH,
        RecordContainerLength = value.RECORD_LU_CONTAINER_SIZE_LENGTH,
        RecordContainerWidth = value.RECORD_LU_CONTAINER_SIZE_WIDTH,
        RecordHeight = value.RECORD_LU_HEIGHT,
        RecordLength = value.RECORD_LU_LENGTH,
        RecordWidth = value.RECORD_LU_WIDTH,
        RecordWeight = value.RECORD_LU_WEIGTH,
        RecordLoadUnitType = value.RECORD_LU_TYPE,
        RecordSide1Surplus = value.RECORD_LU_SIDE1SURPLUS,
        RecordSide2Surplus = value.RECORD_LU_SIDE2SURPLUS,
        DecodeLabelCodes = value.DECODE_LU_LABEL_CODES,
        NewLoadUnitTypeId = value.NEWLU_ID_LU_TYPE,
        NewLoadUnitReference = value.NEWLU_ID_REFERENCE,
        NewLoadUnitQuantity = value.NEWLU_QT,
        NewLoadUnitQuantityId = value.NEWLU_ID_QT
    };

    public static void Apply(RecordDataSettings row, Items.RecordDataFromAsiInfo value)
    {
        value.RECORD_LU_CONTAINER_TYPE = row.RecordContainerType;
        value.RECORD_LU_CONTAINER_SIZE_HEIGTH = row.RecordContainerHeight;
        value.RECORD_LU_CONTAINER_SIZE_LENGTH = row.RecordContainerLength;
        value.RECORD_LU_CONTAINER_SIZE_WIDTH = row.RecordContainerWidth;
        value.RECORD_LU_HEIGHT = row.RecordHeight;
        value.RECORD_LU_LENGTH = row.RecordLength;
        value.RECORD_LU_WIDTH = row.RecordWidth;
        value.RECORD_LU_WEIGTH = row.RecordWeight;
        value.RECORD_LU_TYPE = row.RecordLoadUnitType;
        value.NEWLU_ID_LU_TYPE = row.NewLoadUnitTypeId;
        value.NEWLU_ID_REFERENCE = row.NewLoadUnitReference;
        value.NEWLU_QT = row.NewLoadUnitQuantity;
        value.RECORD_LU_SIDE1SURPLUS = row.RecordSide1Surplus;
        value.RECORD_LU_SIDE2SURPLUS = row.RecordSide2Surplus;
        value.DECODE_LU_LABEL_CODES = row.DecodeLabelCodes;
        value.NEWLU_ID_QT = row.NewLoadUnitQuantityId;
    }

    public static Task<RecordDataSettings?> LoadRecordSettingsAsync(WcsDbContext context) =>
        context.RecordDataSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == RecordDataSettings.DefaultId);

    public static async Task SaveRecordSettingsAsync(WcsDbContext context, RecordDataSettings snapshot)
    {
        var existing = await context.RecordDataSettings.SingleOrDefaultAsync(s => s.Id == RecordDataSettings.DefaultId);
        if (existing == null)
            context.RecordDataSettings.Add(snapshot);
        else
            context.Entry(existing).CurrentValues.SetValues(snapshot);
    }

    #endregion Dati da registrare

    #region Dati da verificare

    public static CheckDataSettings Snapshot(Items.CheckDataFromAsiInfo value) => new()
    {
        Id = CheckDataSettings.DefaultId,
        CompareContainerType = (short)value.COMP_LU_CONTAINER_TYPE,
        ContainerType = value.LU_CONTAINER_TYPE,
        CompareContainerHeight = (short)value.COMP_LU_CONTAINER_SIZE_HEIGTH,
        ContainerHeight = value.LU_CONTAINER_SIZE_HEIGTH,
        CompareContainerLength = (short)value.COMP_LU_CONTAINER_SIZE_LENGTH,
        ContainerLength = value.LU_CONTAINER_SIZE_LENGTH,
        CompareContainerWidth = (short)value.COMP_LU_CONTAINER_SIZE_WIDTH,
        ContainerWidth = value.LU_CONTAINER_SIZE_WIDTH,
        CompareHeight = (short)value.COMP_LU_HEIGHT,
        Height = value.LU_HEIGHT,
        CompareLength = (short)value.COMP_LU_LENGTH,
        Length = value.LU_LENGTH,
        CompareWidth = (short)value.COMP_LU_WIDTH,
        Width = value.LU_WIDTH,
        CompareWeight = (short)value.COMP_LU_WEIGTH,
        Weight = value.LU_WEIGTH,
        CompareTemperature = (short)value.COMP_LU_TEMP,
        Temperature = value.LU_TEMP,
        CompareLengthSide1Surplus = (short)value.COMP_LU_LENGTH_SIDE_1_SURPLUS,
        LengthSide1Surplus = value.LU_LENGTH_SIDE_1_SURPLUS,
        CompareLengthSide2Surplus = (short)value.COMP_LU_LENGTH_SIDE_2_SURPLUS,
        LengthSide2Surplus = value.LU_LENGTH_SIDE_2_SURPLUS,
        CompareWidthSide1Surplus = (short)value.COMP_LU_WIDTH_SIDE_1_SURPLUS,
        WidthSide1Surplus = value.LU_WIDTH_SIDE_1_SURPLUS,
        CompareWidthSide2Surplus = (short)value.COMP_LU_WIDTH_SIDE_2_SURPLUS,
        WidthSide2Surplus = value.LU_WIDTH_SIDE_2_SURPLUS,
        CompareHeightSurplus = (short)value.COMP_LU_HEIGTH_SURPLUS,
        HeightSurplus = value.LU_HEIGTH_SURPLUS,
        CompareCode = (short)value.COMP_LU_CODE,
        Code = value.LU_CODE
    };

    public static void Apply(CheckDataSettings row, Items.CheckDataFromAsiInfo value)
    {
        value.COMP_LU_CONTAINER_TYPE = (CompareTypes)row.CompareContainerType;
        value.LU_CONTAINER_TYPE = row.ContainerType;
        value.COMP_LU_CONTAINER_SIZE_HEIGTH = (CompareTypes)row.CompareContainerHeight;
        value.LU_CONTAINER_SIZE_HEIGTH = row.ContainerHeight;
        value.COMP_LU_CONTAINER_SIZE_LENGTH = (CompareTypes)row.CompareContainerLength;
        value.LU_CONTAINER_SIZE_LENGTH = row.ContainerLength;
        value.COMP_LU_CONTAINER_SIZE_WIDTH = (CompareTypes)row.CompareContainerWidth;
        value.LU_CONTAINER_SIZE_WIDTH = row.ContainerWidth;
        value.COMP_LU_HEIGHT = (CompareTypes)row.CompareHeight;
        value.LU_HEIGHT = row.Height;
        value.COMP_LU_LENGTH = (CompareTypes)row.CompareLength;
        value.LU_LENGTH = row.Length;
        value.COMP_LU_WIDTH = (CompareTypes)row.CompareWidth;
        value.LU_WIDTH = row.Width;
        value.COMP_LU_WEIGTH = (CompareTypes)row.CompareWeight;
        value.LU_WEIGTH = row.Weight;
        value.COMP_LU_TEMP = (CompareTypes)row.CompareTemperature;
        value.LU_TEMP = row.Temperature;
        value.COMP_LU_LENGTH_SIDE_1_SURPLUS = (CompareTypes)row.CompareLengthSide1Surplus;
        value.LU_LENGTH_SIDE_1_SURPLUS = row.LengthSide1Surplus;
        value.COMP_LU_LENGTH_SIDE_2_SURPLUS = (CompareTypes)row.CompareLengthSide2Surplus;
        value.LU_LENGTH_SIDE_2_SURPLUS = row.LengthSide2Surplus;
        value.COMP_LU_WIDTH_SIDE_1_SURPLUS = (CompareTypes)row.CompareWidthSide1Surplus;
        value.LU_WIDTH_SIDE_1_SURPLUS = row.WidthSide1Surplus;
        value.COMP_LU_WIDTH_SIDE_2_SURPLUS = (CompareTypes)row.CompareWidthSide2Surplus;
        value.LU_WIDTH_SIDE_2_SURPLUS = row.WidthSide2Surplus;
        value.COMP_LU_HEIGTH_SURPLUS = (CompareTypes)row.CompareHeightSurplus;
        value.LU_HEIGTH_SURPLUS = row.HeightSurplus;
        value.COMP_LU_CODE = (CompareTypes)row.CompareCode;
        value.LU_CODE = row.Code;
    }

    public static Task<CheckDataSettings?> LoadCheckSettingsAsync(WcsDbContext context) =>
        context.CheckDataSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == CheckDataSettings.DefaultId);

    public static async Task SaveCheckSettingsAsync(WcsDbContext context, CheckDataSettings snapshot)
    {
        var existing = await context.CheckDataSettings.SingleOrDefaultAsync(s => s.Id == CheckDataSettings.DefaultId);
        if (existing == null)
            context.CheckDataSettings.Add(snapshot);
        else
            context.Entry(existing).CurrentValues.SetValues(snapshot);
    }

    #endregion Dati da verificare
}
