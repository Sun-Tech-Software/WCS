namespace Wcs.WebEditor.Drawio.Coordination;

internal enum DrawioPlantWriter
{
    Nobody,
    Drawing,
    Panel,
    Creating,
    Cells,
    Importing,
    Engine,

    /// <summary>The plant-wide connection mode switch (row 50, compito 1): <c>POST /plant/items/connection-type</c>.</summary>
    Mode,

    /// <summary>The device list's grid (row 58, compito 2): one cell of one device, <c>PUT /plant/items/{id}/config</c>.</summary>
    DeviceList,

    /// <summary>The grid of the PLC connections (row 62, compito 4): the whole list, <c>PUT /plc/connections</c>.</summary>
    PlcConnections,

    /// <summary>
    /// The duplication of a device (row 63, compito 5 fix round 2): <c>POST /plant/items</c> with the
    /// source's configuration copied, then <c>PUT .../geometry</c>. Not <see cref="Creating"/>, whose
    /// every refusal speaks of shapes dropped on the drawing.
    /// </summary>
    Duplicating,
}

internal sealed class DrawioPlantWriteGate
{
    private readonly object _sync = new();
    private DrawioPlantWriter _current;
    private long _leaseId;

    public event Action? Changed;

    public DrawioPlantWriter Current
    {
        get
        {
            lock (_sync) return _current;
        }
    }

    public bool TryEnter(DrawioPlantWriter writer, out DrawioPlantWriteLease? lease)
    {
        if (writer is DrawioPlantWriter.Nobody)
            throw new ArgumentOutOfRangeException(nameof(writer), writer, "Nobody cannot own the write gate.");

        Action? changed;
        lock (_sync)
        {
            if (_current is not DrawioPlantWriter.Nobody)
            {
                lease = null;
                return false;
            }

            _current = writer;
            var leaseId = ++_leaseId;
            lease = new DrawioPlantWriteLease(this, writer, leaseId);
            changed = Changed;
        }

        changed?.Invoke();
        return true;
    }

    /// <summary>
    /// The one way a configuration write of the page goes in: the gate taken by <paramref name="writer"/>
    /// (or its refusal, <paramref name="refusalFor"/> the writer that holds it), the edit permit and the
    /// engine rule asked for NOW (<paramref name="ensureWritable"/>: null lets the write through,
    /// otherwise its words), then <paramref name="write"/> - still inside the gate, which is let go
    /// whatever happens. Null when <paramref name="write"/> ran; otherwise the refusal, and nothing was
    /// sent. What <paramref name="write"/> throws is the caller's to tell apart.
    /// <paramref name="afterUnreadablePermit"/> is the caller's own sentence after the refusal of a
    /// permit answer that cannot be read (the grid of the PLC connections says its rows are kept).
    /// </summary>
    /// <remarks>
    /// A permit answer that cannot be read (a <see cref="System.Text.Json.JsonException"/>, which
    /// <c>EnsureWritableAsync</c> lets through) is a refusal too: nothing has been sent yet. Used by the
    /// connection mode (row 50), the configuration of a device (the panel and the device list) and the
    /// PLC connections (row 62), which carried three near-copies of this until compito 4's fix round 1.
    /// A refusal is a <see cref="DrawioSaid"/>: when it is about a request that failed, what came back
    /// is behind the "?" of the line that says it, not in the sentence (row 37 of the queue).
    /// </remarks>
    public async Task<DrawioSaid?> WriteThroughAsync(
        DrawioPlantWriter writer,
        Func<DrawioPlantWriter, string> refusalFor,
        Func<Task<DrawioSaid?>> ensureWritable,
        Func<Task> write,
        string? afterUnreadablePermit = null)
    {
        if (!TryEnter(writer, out var lease))
            return refusalFor(Current);

        using var held = lease!;
        try
        {
            if (await ensureWritable() is { } refused)
                return refused;
        }
        catch (System.Text.Json.JsonException ex)
        {
            return DrawioSaid.Failed(
                $"The edit permit could not be asked for because {DrawioFailureWords.Why(ex)}: nothing was written." +
                (afterUnreadablePermit is null ? "" : " " + afterUnreadablePermit),
                ex);
        }

        await write();
        return null;
    }

    internal void Release(DrawioPlantWriter writer, long leaseId)
    {
        Action? changed = null;
        lock (_sync)
        {
            if (_current != writer || _leaseId != leaseId) return;
            _current = DrawioPlantWriter.Nobody;
            changed = Changed;
        }

        changed?.Invoke();
    }
}

internal sealed class DrawioPlantWriteLease(
    DrawioPlantWriteGate gate,
    DrawioPlantWriter writer,
    long leaseId) : IDisposable
{
    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            gate.Release(writer, leaseId);
    }
}

internal static class DrawioWriteRefusal
{
    public static string ForCreation(DrawioPlantWriter owner) => owner switch
    {
        DrawioPlantWriter.Drawing => "The drawing is being saved to the plant right now: nothing was created. The shapes are on the canvas and the plant is untouched - delete them and drag them again once the save is over.",
        DrawioPlantWriter.Cells => "The warehouse cells of the selected device are being written to the plant right now: nothing was created. The shapes are on the canvas and the plant is untouched - delete them and drag them again in a moment.",
        DrawioPlantWriter.Creating => "Devices dropped on the drawing are already being created on the plant: nothing more was created. The shapes are on the canvas and the plant is untouched - delete them and drag them again in a moment.",
        DrawioPlantWriter.Importing => "A whole plant is being imported from a file right now: nothing was created. The shapes are on the canvas and the plant is untouched - and the import is about to replace it anyway, so wait for it to finish and for the page to reload before dragging them again.",
        DrawioPlantWriter.Panel => "A configuration from the properties panel is being written to the plant right now: nothing was created. The shapes are on the canvas and the plant is untouched - delete them and drag them again in a moment.",
        DrawioPlantWriter.Engine => "The engine is being started from this page right now: nothing was created. The shapes are on the canvas and the plant is untouched - if the engine starts, nothing can be created until it is stopped; if the start fails, delete them and drag them again.",
        DrawioPlantWriter.Mode => "The connection mode of the whole plant is being written right now: nothing was created. The shapes are on the canvas and the plant is untouched - delete them and drag them again once it is over.",
        DrawioPlantWriter.DeviceList => "A cell of the device list is being written to the plant right now: nothing was created. The shapes are on the canvas and the plant is untouched - delete them and drag them again in a moment.",
        DrawioPlantWriter.PlcConnections => "The PLC connections are being saved to the host right now: nothing was created. The shapes are on the canvas and the plant is untouched - delete them and drag them again in a moment.",
        DrawioPlantWriter.Duplicating => "A device is being duplicated on the plant right now: nothing was created. The shapes are on the canvas and the plant is untouched - delete them and drag them again in a moment.",
        _ => throw Unexpected(owner),
    };

    public static string ForDrawing(DrawioPlantWriter owner) => owner switch
    {
        DrawioPlantWriter.Panel => "A configuration from the properties panel is being written to the plant right now: nothing of the drawing was written. The drawing is untouched on the canvas - save again in a moment.",
        DrawioPlantWriter.Creating => "A device dropped on the drawing is being created on the plant right now: nothing of the drawing was written. The drawing is untouched on the canvas - save again in a moment.",
        DrawioPlantWriter.Cells => "The warehouse cells of the selected device are being written to the plant right now: nothing of the drawing was written. The drawing is untouched on the canvas - save again in a moment.",
        DrawioPlantWriter.Importing => "A whole plant is being imported from a file right now: nothing of the drawing was written. The drawing is untouched on the canvas, and saving it once the import is over would write it onto ANOTHER plant - the page reloads by itself when the import ends, and the drawing that comes back is the imported one.",
        DrawioPlantWriter.Drawing => "The drawing is already being saved to the plant: this second save wrote nothing of its own. The drawing is untouched on the canvas - wait for the outcome of the save already on its way.",
        DrawioPlantWriter.Engine => "The engine is being started from this page right now: nothing of the drawing was written. The drawing is untouched on the canvas - if the engine starts, the save writes nothing until it is stopped; if the start fails, save again.",
        DrawioPlantWriter.Mode => "The connection mode of the whole plant is being written right now: nothing of the drawing was written. The drawing is untouched on the canvas - save again once it is over.",
        DrawioPlantWriter.DeviceList => "A cell of the device list is being written to the plant right now: nothing of the drawing was written. The drawing is untouched on the canvas - save again in a moment.",
        DrawioPlantWriter.PlcConnections => "The PLC connections are being saved to the host right now: nothing of the drawing was written. The drawing is untouched on the canvas - save again in a moment.",
        DrawioPlantWriter.Duplicating => "A device is being duplicated on the plant right now: nothing of the drawing was written. The drawing is untouched on the canvas - save again in a moment.",
        _ => throw Unexpected(owner),
    };

    public static string ForPanel(DrawioPlantWriter owner) => owner switch
    {
        DrawioPlantWriter.Drawing => "The drawing is being saved to the plant right now: the configuration was NOT written. Nothing has been lost - the values are still here, confirm again once the save is over.",
        DrawioPlantWriter.Creating => "A device dropped on the drawing is being created on the plant right now: the configuration was NOT written. Nothing has been lost - the values are still here, confirm again in a moment.",
        DrawioPlantWriter.Cells => "The warehouse cells of this device are being written to the plant right now: the configuration was NOT written. Nothing has been lost - the values are still here, confirm again in a moment.",
        DrawioPlantWriter.Importing => "A whole plant is being imported from a file right now: the configuration was NOT written. Nothing has been lost, but do not confirm again until the import is over: it replaces the plant, this device included, and the page reloads by itself when it ends.",
        DrawioPlantWriter.Panel => "This panel is already writing to the plant: the previous confirmation is still on its way, and this one wrote nothing. Nothing has been lost - the values are still here, and the outcome of the first one will show up below.",
        DrawioPlantWriter.Engine => "The engine is being started from this page right now: the configuration was NOT written. Nothing has been lost - the values are still here: if the engine starts, nothing is written until it is stopped; if the start fails, confirm again.",
        DrawioPlantWriter.Mode => "The connection mode of the whole plant is being written right now: the configuration was NOT written. Nothing has been lost - the values are still here, confirm again once it is over.",
        DrawioPlantWriter.DeviceList => "A cell of the device list is being written to the plant right now: the configuration was NOT written. Nothing has been lost - the values are still here, confirm again in a moment.",
        DrawioPlantWriter.PlcConnections => "The PLC connections are being saved to the host right now: the configuration was NOT written. Nothing has been lost - the values are still here, confirm again in a moment.",
        DrawioPlantWriter.Duplicating => "A device is being duplicated on the plant right now: the configuration was NOT written. Nothing has been lost - the values are still here, confirm again in a moment.",
        _ => throw Unexpected(owner),
    };

    public static string ForCells(DrawioPlantWriter owner, string gesture) => owner switch
    {
        DrawioPlantWriter.Drawing => $"The drawing is being saved to the plant right now: {gesture} wrote NOTHING. Nothing has been lost - what is selected here is still selected, try again once the save is over.",
        DrawioPlantWriter.Panel => $"A configuration from the properties panel is being written to the plant right now: {gesture} wrote NOTHING. Nothing has been lost - what is selected here is still selected, try again in a moment.",
        DrawioPlantWriter.Creating => $"A device dropped on the drawing is being created on the plant right now: {gesture} wrote NOTHING. Nothing has been lost - what is selected here is still selected, try again in a moment.",
        DrawioPlantWriter.Importing => $"A whole plant is being imported from a file right now: {gesture} wrote NOTHING. Nothing has been lost, but do not try again until the import is over: it replaces the plant, this device included, and the page reloads by itself when it ends.",
        DrawioPlantWriter.Cells => $"The cells of this device are already being written to the plant: {gesture} wrote nothing of its own. Nothing has been lost, and the outcome of the first one will show up here.",
        DrawioPlantWriter.Engine => $"The engine is being started from this page right now: {gesture} wrote NOTHING. Nothing has been lost - if the engine starts, nothing is written until it is stopped; if the start fails, try again.",
        DrawioPlantWriter.Mode => $"The connection mode of the whole plant is being written right now: {gesture} wrote NOTHING. Nothing has been lost - what is selected here is still selected, try again once it is over.",
        DrawioPlantWriter.DeviceList => $"A cell of the device list is being written to the plant right now: {gesture} wrote NOTHING. Nothing has been lost - what is selected here is still selected, try again in a moment.",
        DrawioPlantWriter.PlcConnections => $"The PLC connections are being saved to the host right now: {gesture} wrote NOTHING. Nothing has been lost - what is selected here is still selected, try again in a moment.",
        DrawioPlantWriter.Duplicating => $"A device is being duplicated on the plant right now: {gesture} wrote NOTHING. Nothing has been lost - what is selected here is still selected, try again in a moment.",
        _ => throw Unexpected(owner),
    };

    public static string ForImport(DrawioPlantWriter owner) => owner switch
    {
        DrawioPlantWriter.Drawing => "The drawing is being saved to the plant right now: NOTHING of the file was imported, and the plant was not emptied. The file you chose is still selected - confirm again once the save is over.",
        DrawioPlantWriter.Panel => "A configuration from the properties panel is being written to the plant right now: NOTHING of the file was imported, and the plant was not emptied. The file you chose is still selected - confirm again in a moment.",
        DrawioPlantWriter.Creating => "A device dropped on the drawing is being created on the plant right now: NOTHING of the file was imported, and the plant was not emptied. The file you chose is still selected - confirm again in a moment.",
        DrawioPlantWriter.Cells => "The warehouse cells of the selected device are being written to the plant right now: NOTHING of the file was imported, and the plant was not emptied. The file you chose is still selected - confirm again in a moment.",
        DrawioPlantWriter.Importing => "An import is already on its way to the plant: this second confirmation imported NOTHING of its own. Wait for the first one to finish - the page reloads by itself when it does.",
        DrawioPlantWriter.Engine => "The engine is being started from this page right now: NOTHING of the file was imported, and the plant was not emptied. An import needs the engine stopped: wait for the outcome of the start.",
        DrawioPlantWriter.Mode => "The connection mode of the whole plant is being written right now: NOTHING of the file was imported, and the plant was not emptied. The file you chose is still selected - confirm again once it is over.",
        DrawioPlantWriter.DeviceList => "A cell of the device list is being written to the plant right now: NOTHING of the file was imported, and the plant was not emptied. The file you chose is still selected - confirm again in a moment.",
        DrawioPlantWriter.PlcConnections => "The PLC connections are being saved to the host right now: NOTHING of the file was imported, and the plant was not emptied. The file you chose is still selected - confirm again in a moment.",
        DrawioPlantWriter.Duplicating => "A device is being duplicated on the plant right now: NOTHING of the file was imported, and the plant was not emptied. The file you chose is still selected - confirm again in a moment.",
        _ => throw Unexpected(owner),
    };

    public static string ForEngine(DrawioPlantWriter owner) => owner switch
    {
        DrawioPlantWriter.Drawing => "The drawing is being saved to the plant right now: the engine was NOT started. Start it again once the save is over.",
        DrawioPlantWriter.Panel => "A configuration from the properties panel is being written to the plant right now: the engine was NOT started. Start it again in a moment.",
        DrawioPlantWriter.Creating => "A device dropped on the drawing is being created on the plant right now: the engine was NOT started. Start it again in a moment.",
        DrawioPlantWriter.Cells => "The warehouse cells of the selected device are being written to the plant right now: the engine was NOT started. Start it again in a moment.",
        DrawioPlantWriter.Importing => "A whole plant is being imported from a file right now: the engine was NOT started. The page reloads by itself when the import ends.",
        DrawioPlantWriter.Engine => "The engine is already being started from this page: this second press sent nothing.",
        DrawioPlantWriter.Mode => "The connection mode of the whole plant is being written right now: the engine was NOT started. Start it again once it is over.",
        DrawioPlantWriter.DeviceList => "A cell of the device list is being written to the plant right now: the engine was NOT started. Start it again in a moment.",
        DrawioPlantWriter.PlcConnections => "The PLC connections are being saved to the host right now: the engine was NOT started. Start it again in a moment.",
        DrawioPlantWriter.Duplicating => "A device is being duplicated on the plant right now: the engine was NOT started. Start it again in a moment.",
        _ => throw Unexpected(owner),
    };

    /// <summary>
    /// Refusals of the connection-mode switch itself (row 50, compito 1) caused by another writer
    /// already holding the gate - the mirror of <see cref="ForEngine"/>, worded for this gesture.
    /// </summary>
    public static string ForMode(DrawioPlantWriter owner) => owner switch
    {
        DrawioPlantWriter.Drawing => "The drawing is being saved to the plant right now: the connection mode was NOT written. Confirm again once the save is over.",
        DrawioPlantWriter.Panel => "A configuration from the properties panel is being written to the plant right now: the connection mode was NOT written. Confirm again in a moment.",
        DrawioPlantWriter.Creating => "A device dropped on the drawing is being created on the plant right now: the connection mode was NOT written. Confirm again in a moment.",
        DrawioPlantWriter.Cells => "The warehouse cells of the selected device are being written to the plant right now: the connection mode was NOT written. Confirm again in a moment.",
        DrawioPlantWriter.Importing => "A whole plant is being imported from a file right now: the connection mode was NOT written. The page reloads by itself when the import ends.",
        DrawioPlantWriter.Engine => "The engine is being started from this page right now: the connection mode was NOT written. Confirm again once the start is over.",
        DrawioPlantWriter.Mode => "The connection mode is already being written from this page: this second confirmation sent nothing.",
        DrawioPlantWriter.DeviceList => "A cell of the device list is being written to the plant right now: the connection mode was NOT written. Confirm again in a moment.",
        DrawioPlantWriter.PlcConnections => "The PLC connections are being saved to the host right now: the connection mode was NOT written. Confirm again in a moment.",
        DrawioPlantWriter.Duplicating => "A device is being duplicated on the plant right now: the connection mode was NOT written. Confirm again in a moment.",
        _ => throw Unexpected(owner),
    };

    /// <summary>
    /// Refusals of a cell of the device list (row 58, compito 2) caused by another writer already
    /// holding the gate. The row keeps what the plant holds - the grid never shows a value as written
    /// before the host has taken it - so every sentence says the value was NOT written and when to try
    /// again; the value itself is named by the caller, in front of these words.
    /// </summary>
    public static string ForDeviceList(DrawioPlantWriter owner) => owner switch
    {
        DrawioPlantWriter.Drawing => "The drawing is being saved to the plant right now: the value was NOT written. The row shows what the plant holds - change the cell again once the save is over.",
        DrawioPlantWriter.Panel => "A configuration from the properties panel is being written to the plant right now: the value was NOT written. The row shows what the plant holds - change the cell again in a moment.",
        DrawioPlantWriter.Creating => "A device dropped on the drawing is being created on the plant right now: the value was NOT written. The row shows what the plant holds - change the cell again in a moment.",
        DrawioPlantWriter.Cells => "The warehouse cells of the selected device are being written to the plant right now: the value was NOT written. The row shows what the plant holds - change the cell again in a moment.",
        DrawioPlantWriter.Importing => "A whole plant is being imported from a file right now: the value was NOT written. Do not change the cell again until the import is over: it replaces the plant, this device included, and the page reloads by itself when it ends.",
        DrawioPlantWriter.Engine => "The engine is being started from this page right now: the value was NOT written. If the engine starts, nothing is written until it is stopped; if the start fails, change the cell again.",
        DrawioPlantWriter.Mode => "The connection mode of the whole plant is being written right now: the value was NOT written. The row shows what the plant holds - change the cell again once it is over.",
        DrawioPlantWriter.DeviceList => "Another cell of the device list is already being written to the plant: this one was NOT written. The row shows what the plant holds - change the cell again once the first write has its outcome.",
        DrawioPlantWriter.PlcConnections => "The PLC connections are being saved to the host right now: the value was NOT written. The row shows what the plant holds - change the cell again in a moment.",
        DrawioPlantWriter.Duplicating => "A device is being duplicated on the plant right now: the value was NOT written. The row shows what the plant holds - change the cell again in a moment.",
        _ => throw Unexpected(owner),
    };

    /// <summary>
    /// Refusals of the save of the PLC connections (row 62, compito 4) caused by another writer
    /// already holding the gate. The grid keeps the rows as they were typed - nothing is thrown away by
    /// a refusal - so every sentence says the list was NOT saved and when to save again.
    /// </summary>
    public static string ForPlcConnections(DrawioPlantWriter owner) => owner switch
    {
        DrawioPlantWriter.Drawing => "The drawing is being saved to the plant right now: the PLC connections were NOT saved. Your changes are still in the grid - save again once the save of the drawing is over.",
        DrawioPlantWriter.Panel => "A configuration from the properties panel is being written to the plant right now: the PLC connections were NOT saved. Your changes are still in the grid - save again in a moment.",
        DrawioPlantWriter.Creating => "A device dropped on the drawing is being created on the plant right now: the PLC connections were NOT saved. Your changes are still in the grid - save again in a moment.",
        DrawioPlantWriter.Cells => "The warehouse cells of the selected device are being written to the plant right now: the PLC connections were NOT saved. Your changes are still in the grid - save again in a moment.",
        DrawioPlantWriter.Importing => "A whole plant is being imported from a file right now: the PLC connections were NOT saved. Do not save again: the import may replace the PLC connections too, and the page reloads by itself when it ends.",
        DrawioPlantWriter.Engine => "The engine is being started from this page right now: the PLC connections were NOT saved. If the engine starts, nothing is saved until it is stopped; if the start fails, save again.",
        DrawioPlantWriter.Mode => "The connection mode of the whole plant is being written right now: the PLC connections were NOT saved. Your changes are still in the grid - save again once it is over.",
        DrawioPlantWriter.DeviceList => "A cell of the device list is being written to the plant right now: the PLC connections were NOT saved. Your changes are still in the grid - save again in a moment.",
        DrawioPlantWriter.PlcConnections => "The PLC connections are already being saved from this page: this second confirmation sent nothing. The outcome of the first one will show up here.",
        DrawioPlantWriter.Duplicating => "A device is being duplicated on the plant right now: the PLC connections were NOT saved. Your changes are still in the grid - save again in a moment.",
        _ => throw Unexpected(owner),
    };

    /// <summary>
    /// Refusals of the duplication of a device (row 63, compito 5 fix round 2) caused by another
    /// writer already holding the gate. Nothing was created and nothing was drawn, so every sentence
    /// says so and when to try again.
    /// </summary>
    public static string ForDuplication(DrawioPlantWriter owner) => owner switch
    {
        DrawioPlantWriter.Drawing => "The drawing is being saved to the plant right now: the device was NOT duplicated. Nothing was created - duplicate it again once the save is over.",
        DrawioPlantWriter.Panel => "A configuration from the properties panel is being written to the plant right now: the device was NOT duplicated. Nothing was created - duplicate it again in a moment.",
        DrawioPlantWriter.Creating => "A device dropped on the drawing is being created on the plant right now: the device was NOT duplicated. Nothing was created - duplicate it again in a moment.",
        DrawioPlantWriter.Cells => "The warehouse cells of the selected device are being written to the plant right now: the device was NOT duplicated. Nothing was created - duplicate it again in a moment.",
        DrawioPlantWriter.Importing => "A whole plant is being imported from a file right now: the device was NOT duplicated. The import replaces the plant, this device included, and the page reloads by itself when it ends.",
        DrawioPlantWriter.Engine => "The engine is being started from this page right now: the device was NOT duplicated. If the engine starts, nothing can be created until it is stopped; if the start fails, duplicate it again.",
        DrawioPlantWriter.Mode => "The connection mode of the whole plant is being written right now: the device was NOT duplicated. Nothing was created - duplicate it again once it is over.",
        DrawioPlantWriter.DeviceList => "A cell of the device list is being written to the plant right now: the device was NOT duplicated. Nothing was created - duplicate it again in a moment.",
        DrawioPlantWriter.PlcConnections => "The PLC connections are being saved to the host right now: the device was NOT duplicated. Nothing was created - duplicate it again in a moment.",
        DrawioPlantWriter.Duplicating => "A device is already being duplicated from this page: this second confirmation created nothing of its own. The outcome of the first one will show up here.",
        _ => throw Unexpected(owner),
    };

    private static ArgumentOutOfRangeException Unexpected(DrawioPlantWriter owner) =>
        new(nameof(owner), owner, "Nobody cannot cause a write refusal.");
}
