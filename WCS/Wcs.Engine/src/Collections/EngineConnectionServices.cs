using Wcs.Connection.Interface;
using Wcs.Engine.Persistence;

namespace Wcs.Engine.Collections;

/// <summary>
/// The engine side of <see cref="IConnectionServices"/>: what a field driver writes goes to the
/// engine databases. One instance per <see cref="IConnectionFactory.Attach"/> call.
/// </summary>
/// <remarks>
/// These were the four static hooks <c>DeviceConnections.InstallPlcHooks</c> assigned on the
/// historical driver: the same code, behind the driver contract, so the engine no longer names a
/// driver.
/// </remarks>
internal sealed class EngineConnectionServices : IConnectionServices
{
    /// <summary>
    /// Command identifiers reserved in memory, per connection and per store. A new instance (a new
    /// attach) starts empty, and a store other than the one that reserved a block - a reload, a
    /// test that opens its own - discards it instead of handing out numbers reserved elsewhere.
    /// </summary>
    private readonly Dictionary<long, (object Store, int Next, int Last)> _blocks = new();
    private readonly object _blocksSync = new();

    /// <summary>
    /// One disk transaction every <see cref="DeviceConnections.CommandIdBlockSize"/> commands
    /// instead of one per command (see <see cref="PlcSettingsStore.ReserveCommandIdBlock"/>).
    /// </summary>
    public int ReserveCommandId(decimal connectionId)
    {
        var store = Navigator.Store ?? throw new InvalidOperationException(
            "The WCS database is required to reserve PLC command identifiers.");
        var id = (long)connectionId;
        lock (_blocksSync)
        {
            if (_blocks.TryGetValue(id, out var block) &&
                ReferenceEquals(block.Store, store) && block.Next <= block.Last)
            {
                _blocks[id] = block with { Next = block.Next + 1 };
                return block.Next;
            }

            using var context = store.CreateContext();
            var first = PlcSettingsStore.ReserveCommandIdBlock(context, id, DeviceConnections.CommandIdBlockSize);
            _blocks[id] = (store, first + 1, first + DeviceConnections.CommandIdBlockSize - 1);
            return first;
        }
    }

    public Task WriteStatusAsync(ConnectionStatusSnapshot status)
    {
        ArgumentNullException.ThrowIfNull(status);
        WcsStateWrites.Write(context => PlcSettingsStore.UpdateStatusAsync(context, (long)status.Id, (int)status.OpProgress,
            status.StatusSenderChannel, status.StatusReceiverChannel, status.OutboundChannelStatus, status.InboundChannelStatus));
        return Task.CompletedTask;
    }

    public Task WriteLogAsync(DriverLogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ORM.NavigatorLog.Add(new Items.NavigatorLogRecord
        {
            ASI = record.Asi,
            SUBITEM = record.Subitem,
            PARTITION = record.Partition,
            SourceItemId = record.IdItem,
            Status = record.Status,
            Description = record.Description
        });
        return Task.CompletedTask;
    }

    public Task WriteMessageAsync(MessageLogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ORM.PlcMessageLog.Add(record);
        return Task.CompletedTask;
    }

    /// <summary>
    /// A real deletion, like the old <c>DELETE FROM PCM_LOG</c>: rows older than the given days in
    /// the file of the current month.
    /// </summary>
    public Task ClearMessagesOlderThanAsync(int days) => ORM.PlcMessageLog.ClearOlderThan(days);
}
