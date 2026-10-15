using Helper.Converters;
using Wcs.Common;
using Wcs.Engine.ORM;
using Log.Interface;
using Wcs.Connection.Interface;
using static Wcs.Engine.Navigator;

using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;

namespace Wcs.Engine.Collections;

/// <summary>
/// Gestisce la collezione delle connessioni ai dispositivi PLC utilizzando il pattern Singleton.
/// Fornisce metodi per inizializzare, aprire, chiudere e gestire le connessioni ai dispositivi di automazione.
/// </summary>
public sealed class DeviceConnections
{
    #region Private Fields

    /// <summary>
    /// Implementazione lazy del pattern Singleton per garantire una singola istanza della classe.
    /// </summary>
    private static readonly Lazy<DeviceConnections> LazyPlCs = new(() => new DeviceConnections());

    /// <summary>
    /// Connessione di test statica condivisa utilizzata per i dispositivi in modalità test.
    /// </summary>
    private static Wcs.Connection.Test.Connection? _tstConnection;

    #endregion Private Fields

    #region Public Properties

    /// <summary>
    /// Ottiene l'istanza singleton della classe DeviceConnections.
    /// </summary>
    /// <value>L'istanza singleton di DeviceConnections.</value>
    public static DeviceConnections Instance => LazyPlCs.Value;

    /// <summary>
    /// Ottiene o imposta la lista delle connessioni ai dispositivi.
    /// </summary>
    /// <value>Lista delle connessioni implementanti l'interfaccia IConnection.</value>
    /// <remarks>
    /// Resta una <c>List</c> e non diventa una collezione concorrente perche' la griglia dei PLC
    /// dell'Editor vi si lega come <c>DataSource</c> di un <c>BindingSource</c>: cambiarne il tipo
    /// romperebbe quel binding. La sicurezza fra thread e' data invece da <see cref="SyncItems"/> —
    /// ogni modifica passa di li', e chi la percorre lo fa su una copia presa con
    /// <see cref="Snapshot"/>. Il setter e' privato: nessuno la sostituiva, e sostituirla sotto il
    /// binding lo lascerebbe attaccato alla lista vecchia. Si veda la voce <c>C34</c> del registro in
    /// <c>WCS/doc/debito-tecnico.md</c>.
    /// </remarks>
    public List<IConnection> Items { get; private set; } = [];

    /// <summary>
    /// Lock che protegge <see cref="Items"/>: la lista e' scritta dalla griglia dell'Editor e letta
    /// dai cicli del motore, che girano su thread diversi.
    /// </summary>
    internal static readonly object SyncItems = new();

    /// <summary>
    /// Restituisce una copia della collezione, presa sotto lock.
    /// </summary>
    /// <returns>Le connessioni presenti al momento della chiamata</returns>
    /// <remarks>
    /// I cicli del motore percorrono questa copia e non la lista viva: una modifica dalla griglia
    /// durante un <c>foreach</c> era una <c>InvalidOperationException</c> in attesa.
    /// </remarks>
    internal static IConnection[] Snapshot()
    {
        lock (SyncItems) return Instance.Items.ToArray();
    }

    #endregion Public Properties

    #region Public Indexers

    /// <summary>
    /// Ottiene o imposta una connessione in base al suo identificativo.
    /// </summary>
    /// <param name="id">L'identificativo decimale della connessione.</param>
    /// <returns>La connessione corrispondente all'ID specificato, o null se non trovata.</returns>
    public IConnection? this[decimal id]
    {
        get
        {
            var items = Items;
            return items.FirstOrDefault(t => t.Id == id);
        }
        set
        {
            if (value == null) return;

            var items = Items;
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].Id != id) continue;
                items[i] = value;
                break;
            }
        }
    }

    #endregion Public Indexers

    #region Public Methods

    /// <summary>
    /// Chiude tutte le connessioni attive, le dispone e svuota la collezione.
    /// </summary>
    public static void Clear()
    {
        // Copia e svuotamento sotto lock, chiusura fuori: chiudere e rilasciare una connessione
        // puo' essere lento, e tenerlo dentro il lock bloccherebbe la griglia dell'Editor.
        IConnection[] items;

        lock (SyncItems)
        {
            items = Instance.Items.ToArray();
            Instance.Items.Clear();
        }

        foreach (var item in items)
        {
            item.CloseConnection();
            item.Dispose();
        }

        // Il simulatore e' un singleton tenuto anche in un campo statico: senza azzerarlo qui
        // resterebbe raggiungibile da GetTestConnection() come oggetto gia' disposto e, non
        // essendo piu' in Items, non verrebbe mai piu' registrato ne' chiuso.
        _tstConnection = null;
    }

    public static void CloseConnection()
    {
        if (PathDevices.Instance.Count == 0) return;

        var pathDevices = PathDevices.Instance.List;
        foreach (var t in pathDevices)
        {
            t.DeviceConnection = null;
        }

        foreach (var item in Snapshot().Where(item => item.Managed))
        {
            item.AsiMovementIsCompleted -= AsiMovementIsCompleted;
            item.NewLuOnAsi -= NewLuOnASI;
            item.LuUnloadedFromAsi -= LuUnloadedFromAsi;
            item.AsiSendLuData -= AsiSendLuData;
            item.AsiRequestsLuInfo -= AsiRequestsLuData;
            item.AsiEvent -= AsiEvent;

            item.CloseConnection();
            item.Managed = false;
        }
    }

    /// <summary>
    /// How many command identifiers are reserved at a time. After a restart at most this many minus
    /// one are skipped per connection, on a 32-bit field: not a cost.
    /// </summary>
    internal const int CommandIdBlockSize = 100;

    /// <summary>
    /// The factory of the field driver, or <c>null</c> when only the loopback driver is available.
    /// </summary>
    /// <remarks>
    /// The host sets it at startup from the <c>Wcs:ConnectionFactory</c> setting (an assembly-qualified
    /// type name loaded by reflection), before its <c>StartEngine</c> guard: so, unlike
    /// <c>Navigator.Warehouse</c>, it is set also when the engine does not start, and it is not
    /// cleared when the host stops (several hosts can share the process). Without it a device whose
    /// connection type is <c>Connected</c> gets no connection, and Run is refused: see
    /// <see cref="OpenConnection"/> and <see cref="RequireConnectionsForRun"/>.
    /// </remarks>
    public static IConnectionFactory? ConnectionFactory { get; private set; }

    /// <summary>
    /// Whether a <c>Connected</c> device whose connection was not built may be served by the
    /// loopback driver (the simulator). <c>false</c> by default: fail-closed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The host sets it at startup from the <c>Wcs:AllowSimulatorForMissingConnections</c> setting,
    /// and refuses to start with it on in the Production environment. Off, a <c>Connected</c>
    /// device without its connection gets none, goes OffLine with an error in the navigator log, and
    /// <see cref="ExecutableCommands.StartNavigator"/> refuses Run (see
    /// <see cref="RequireConnectionsForRun"/>): a simulated device makes the engine believe it drives
    /// a machine while nothing moves.
    /// </para>
    /// <para>
    /// On, only a device whose connection id points to a connection that was not built (no driver
    /// settings row with that id, a row the driver refused, a plant imported without reloading it)
    /// is served by the simulator, with a warning. A device without any field driver installed or
    /// without a connection id still gets no connection and goes OffLine: there is nothing it
    /// could be mistaken for.
    /// </para>
    /// </remarks>
    public static bool AllowSimulatorForMissingConnections { get; set; }

    /// <summary>How a device is named in the lines about its connection: name, field address, item id.</summary>
    internal static string DeviceLabel(Items.AutomationDevice device) =>
        $"{device.Name ?? "(no name)"} [{device.ASI}.{device.SUBITEM}.{device.PARTITION}, itemId {device.ItemGuid}]";

    /// <summary>
    /// A <c>Connected</c> device that does not get its connection, why, and whether the explicit
    /// fallback (<see cref="AllowSimulatorForMissingConnections"/>) may serve it with the simulator.
    /// </summary>
    internal sealed record MissingConnection(Items.AutomationDevice Device, string Reason, bool CanBeSimulated);

    /// <summary>What a <c>Connected</c> device with no connection id says.</summary>
    internal const string NoConnectionIdMessage =
        "connection type Connected, but no connection id is set (DeviceConnectionId)";

    /// <summary>What a <c>Connected</c> device says when its connection id has no connection built.</summary>
    internal static string NotBuiltMessage(decimal id) =>
        $"connection type Connected, but the connection with id {id:0} was not built " +
        "(no driver settings row with that id, the driver refused the row, or the plant was imported " +
        "without reloading it: command 1003 in Stop)";

    /// <summary>
    /// Why <paramref name="device"/>, if it is <c>Connected</c>, does not get its connection; <c>null</c>
    /// when it does, or when it is not <c>Connected</c>.
    /// </summary>
    internal static MissingConnection? MissingConnectionOf(Items.AutomationDevice device)
    {
        if (device.ConnectionType != Enums.ConnectionTypes.Connected) return null;
        if (ConnectionFactory == null) return new(device, NoDriverMessage, false);
        if (device.DeviceConnectionId <= 0) return new(device, NoConnectionIdMessage, false);
        return Instance[device.DeviceConnectionId] == null
            ? new(device, NotBuiltMessage(device.DeviceConnectionId), true)
            : null;
    }

    /// <summary>Every <c>Connected</c> device of the plant that does not get its connection.</summary>
    internal static IReadOnlyList<MissingConnection> MissingConnections() =>
        PathDevices.Instance.List
            .Where(d => d != null && d.DeviceType is not (Enums.DeviceTypes.Storage or Enums.DeviceTypes.None))
            .Select(d => MissingConnectionOf(d!))
            .OfType<MissingConnection>()
            .ToList();

    /// <summary>
    /// The gate of Run: with a <c>Connected</c> device that does not get its connection, Run is
    /// refused (<see cref="EngineRunRefusedException"/>, naming every such device and its reason),
    /// and those devices go OffLine with their error line. With
    /// <see cref="AllowSimulatorForMissingConnections"/> on, Run goes ahead with a warning.
    /// </summary>
    /// <remarks>
    /// Called by <see cref="ExecutableCommands.StartNavigator"/> before it opens any connection, so
    /// a refused Run opens nothing. Command 1001 ends in error with the refusal as its
    /// <c>ErrorDescription</c>, which is what the web Editor's Run switch shows.
    /// </remarks>
    internal static void RequireConnectionsForRun()
    {
        var missing = MissingConnections();
        if (missing.Count == 0) return;

        var list = string.Join("; ", missing.Select(m => $"{DeviceLabel(m.Device)}: {m.Reason}"));
        if (AllowSimulatorForMissingConnections)
        {
            var warning =
                $"Run with {missing.Count} device(s) of connection type Connected without their connection, because " +
                "the setting Wcs:AllowSimulatorForMissingConnections is on: the ones whose connection was not built " +
                $"are served by the SIMULATOR, the others stay OffLine. {list}";
            RecordNavigatorLog(null, warning, LogEntryType.Warning);
            WriteLog(warning, LogEntryType.Warning);
            return;
        }

        foreach (var m in missing)
            LeaveWithoutConnection(m.Device, m.Reason);

        var refusal =
            $"Run refused: {missing.Count} device(s) of connection type Connected have no connection, and the engine " +
            "does not drive them with the simulator (setting Wcs:AllowSimulatorForMissingConnections is off). " +
            $"They were set OffLine. {list}. Fix the connections, reload the plant (command 1003), put the devices " +
            "back online (command 2003) and start again.";
        RecordNavigatorLog(null, refusal, LogEntryType.Error);
        throw new Api.EngineRunRefusedException(refusal);
    }

    /// <summary>
    /// Leaves a <c>Connected</c> device without connection: OffLine, so that no mission waits on
    /// it, and an error in the persistent navigator log (<c>WriteLog</c> is silent for information
    /// with <c>ActivateDebugLog</c> off, the default).
    /// </summary>
    private static void LeaveWithoutConnection(Items.AutomationDevice item, string reason)
    {
        RecordNavigatorLog(item,
            $"OpenConnection: {DeviceLabel(item)}: {reason}; the device stays WITHOUT CONNECTION and goes OffLine",
            LogEntryType.Error);
        item.DeviceConnection = null;
        if (item.Status != Enums.DeviceStatusTypes.OffLine)
            item.Status = Enums.DeviceStatusTypes.OffLine;
    }

    /// <summary>
    /// Installs the factory of the field driver (or removes it, with <c>null</c>) and hands it the
    /// engine services, so the driver can write as soon as it is installed.
    /// </summary>
    /// <remarks>
    /// <see cref="Initialize"/> hands the services over again before building the connections, so
    /// that no path that builds a connection reaches the driver without them. Calling this twice
    /// does no harm.
    /// </remarks>
    public static void UseConnectionFactory(IConnectionFactory? factory)
    {
        ConnectionFactory = factory;
        factory?.Attach(new EngineConnectionServices());
    }

    /// <summary>
    /// Inizializza la collezione delle connessioni caricando i dati dei PLC dal database.
    /// Crea una nuova connessione per ogni PLC configurato nel database.
    /// </summary>
    public static void Initialize()
    {
        // Clear() dispone le connessioni precedenti prima di sostituirle: usare
        // Items.Clear() qui le abbandonava senza mai chiamarne il Dispose (C35).
        Clear();

        // The engine services before the connections: a driver that writes while it is being
        // built must find them. The host has already attached them at startup; they are attached
        // again here because this is the path that builds the connections.
        var factory = ConnectionFactory;
        if (factory == null) return;
        factory.Attach(new EngineConnectionServices());

        var ids = PathDevices.Instance.List.OfType<Items.AutomationDevice>()
            .Select(d => (long)d.DeviceConnectionId)
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        var store = Navigator.Store;
        if (ids.Count == 0 || store == null) return;

        List<PlcConnection> connections;
        using (var context = store.CreateContext())
            connections = PlcSettingsStore.ConnectionsAsync(context, ids).GetAwaiter().GetResult();

        foreach (var connection in connections)
        {
            // A row the driver cannot use gives no connection: its devices get none in
            // OpenConnection and go OffLine, and Run is refused while they lack it (only the
            // explicit setting Wcs:AllowSimulatorForMissingConnections serves them with the
            // simulator).
            var plc = factory.Create(new ConnectionSettings((int)connection.Id,
                connection.PlcAddress, connection.PcmPort ?? 0,
                connection.LocalAddress, connection.LocalPort ?? 0,
                (uint)Math.Max(0, connection.OpProgress)));
            if (plc != null)
                Instance.Add(plc);
        }
    }

    /// <summary>
    /// Aggiunge una connessione alla collezione se non è già presente.
    /// </summary>
    /// <param name="value">La connessione da aggiungere alla collezione.</param>
    public void Add(IConnection value)
    {
        lock (SyncItems)
        {
            if (!Items.Contains(value))
                Items.Add(value);
        }
    }

    /// <summary>
    /// Rimuove una connessione dalla collezione.
    /// </summary>
    /// <param name="value">La connessione da rimuovere.</param>
    public void Remove(IConnection value)
    {
        try
        {
            lock (SyncItems) Items.Remove(value);
        }
        catch (Exception ex)
        {
            WriteLog($"Error On PLCs->Remove Description: {ex.Message}\r\nStackTrace: {ex.StackTrace}",
                LogEntryType.Warning);
        }
    }

    /// <summary>
    /// Rimuove una connessione dalla collezione in base all'indice specificato.
    /// </summary>
    /// <param name="index">L'indice della connessione da rimuovere.</param>
    public void Remove(int index)
    {
        try
        {
            IConnection missionStep;

            lock (SyncItems)
            {
                if (index < 0 || index >= Items.Count) return;
                missionStep = Items[index];
            }

            Remove(missionStep);
        }
        catch (Exception ex)
        {
            WriteLog($"Error On PLCs->Remove Description: {ex.Message}\r\nStackTrace: {ex.StackTrace}",
                LogEntryType.Warning);
        }
    }

    /// <summary>
    /// Rimuove una connessione dalla collezione se l'oggetto è del tipo corretto.
    /// </summary>
    /// <param name="value">L'oggetto da rimuovere, che deve implementare IConnection.</param>
    public void Remove(object value)
    {
        try
        {
            if (value is IConnection missionStep)
                Remove(missionStep);
        }
        catch (Exception ex)
        {
            WriteLog($"Error On PLCs->Remove Description: {ex.Message}\r\nStackTrace: {ex.StackTrace}",
                LogEntryType.Warning);
        }
    }

    #endregion Public Methods

    #region Internal Methods

    /// <summary>
    /// Apre le connessioni per tutti i dispositivi configurati e registra i gestori di eventi.
    /// Assegna le connessioni appropriate ai dispositivi in base al loro tipo di connessione.
    /// </summary>
    internal static void OpenConnection()
    {
        if (PathDevices.Instance.Count == 0) return;

        var pathDevicesItems = PathDevices.Instance;
        for (var i = 0; i < pathDevicesItems.Count; i++)
        {
            var item = pathDevicesItems[i];
            if (item?.DeviceType == Enums.DeviceTypes.Storage || item?.DeviceType == Enums.DeviceTypes.None) continue;
            if (item != null)
                switch (item.ConnectionType)
                {
                    case Enums.ConnectionTypes.Test:
                        item.DeviceConnection = GetTestConnection();
                        break;

                    case Enums.ConnectionTypes.Connected:
                        // La connessione reale e' quella costruita da Initialize() con
                        // Id = PCM_PLC.ID_PCM_PLC; il dispositivo la indica con
                        // DeviceConnectionId, che viene da NAVIGATOR_ITEMS.ID_PCM_PLC.
                        // E' la stessa relazione su cui il database fa la propria JOIN.
                        // Fail-closed: a Connected device whose connection is missing (no
                        // driver, no connection id, a connection that was not built) is NOT
                        // served by the simulator, which would make the engine believe it
                        // drives the machine while nothing moves. Only the explicit setting
                        // Wcs:AllowSimulatorForMissingConnections (refused by the host in
                        // Production) brings the simulator back, for a connection not built.
                        var missing = MissingConnectionOf(item);
                        if (missing == null)
                        {
                            var communicatorToPlc = Instance[item.DeviceConnectionId]!;
                            item.DeviceConnection = communicatorToPlc;
                            communicatorToPlc.Managed = true;
                        }
                        else if (missing.CanBeSimulated && AllowSimulatorForMissingConnections)
                        {
                            RecordNavigatorLog(item,
                                $"OpenConnection: {DeviceLabel(item)}: {missing.Reason}; the device is served by the SIMULATOR " +
                                "(setting Wcs:AllowSimulatorForMissingConnections)",
                                LogEntryType.Warning);
                            item.DeviceConnection = GetTestConnection();
                        }
                        else
                        {
                            LeaveWithoutConnection(item, missing.Reason);
                        }

                        break;

                    default:
                        // Un valore fuori intervallo arriva da una colonna MANUFACTURER non
                        // valida: si salta il dispositivo invece di far fallire l'avvio
                        // dell'intero impianto con un'eccezione che nessuno cattura.
                        // Il registro e' quello persistente e non WriteLog, altrimenti si
                        // sostituirebbe un'eccezione visibile con un dispositivo che sparisce
                        // in silenzio: WriteLog tace se ActivateDebugLog e' spento, e lo e'
                        // per impostazione predefinita.
                        RecordNavigatorLog(item,
                            $"OpenConnection: tipo di connessione non riconosciuto ({(int)item.ConnectionType}); dispositivo SALTATO, restera' senza connessione",
                            LogEntryType.Error);
                        break;
                }
        }

        foreach (var item in Snapshot().Where(item => item.Managed))
        {
            item.AsiMovementIsCompleted += AsiMovementIsCompleted;
            item.NewLuOnAsi += NewLuOnASI;
            item.LuUnloadedFromAsi += LuUnloadedFromAsi;
            item.AsiSendLuData += AsiSendLuData;
            item.AsiRequestsLuInfo += AsiRequestsLuData;
            item.AsiEvent += AsiEvent;

            item.OpenConnection();
        }
    }

    /// <summary>
    /// What the log says for a <c>Connected</c> device when no field driver is installed.
    /// </summary>
    internal const string NoDriverMessage =
        "connection type Connected, but no field driver is configured (setting Wcs:ConnectionFactory); " +
        "only the loopback driver (connection type Test) is available";

    #endregion Internal Methods

    #region Private Methods

    /// <summary>
    /// Ottiene o crea una connessione di test singleton.
    /// Registra tutti i gestori di eventi necessari per la connessione di test.
    /// </summary>
    /// <returns>La connessione di test configurata con tutti i gestori di eventi.</returns>
    private static Wcs.Connection.Test.Connection GetTestConnection()
    {
        if (_tstConnection != null) return _tstConnection;

        _tstConnection = new Wcs.Connection.Test.Connection(99);
        _tstConnection.AsiMovementIsCompleted += AsiMovementIsCompleted;
        _tstConnection.NewLuOnAsi += NewLuOnASI;
        _tstConnection.LuUnloadedFromAsi += LuUnloadedFromAsi;
        _tstConnection.AsiSendLuData += AsiSendLuData;
        _tstConnection.AsiRequestsLuInfo += AsiRequestsLuData;
        _tstConnection.AsiEvent += AsiEvent;

        // Registrata in Items perche' Clear() (C35) la raggiunga: prima restava
        // singleton fuori dalla collezione e non veniva mai chiusa (C36).
        Instance.Add(_tstConnection);

        return _tstConnection;
    }

    #endregion Private Methods
}
