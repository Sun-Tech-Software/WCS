namespace Wcs.Host;

/// <summary>La sezione "Wcs" di appsettings: quello che prima stava nelle Settings dell'Editor e nella riga di comando.</summary>
public sealed class WcsHostOptions
{
    public const string Section = "Wcs";

    public string ApiKey { get; set; } = string.Empty;
    public string ReadOnlyApiKey { get; set; } = string.Empty;
    public bool AllowInsecureRemoteHttp { get; set; }
    public string DatabaseDirectory { get; set; } = "Data";
    public int HistoryMonthsToKeep { get; set; } = 12;
    /// <summary>Quanti megabyte può pesare il corpo di una richiesta HTTP: il diagramma viaggia come JSON base64 e quello dell'impianto di prova arriva a 48 MB, ben oltre i 30 predefiniti di Kestrel.</summary>
    public int MaxRequestBodyMegabytes { get; set; } = 256;
    /// <summary>Se falso, l'host espone l'API senza avviare il motore: è così che girano i test.</summary>
    public bool StartEngine { get; set; } = true;
    /// <summary>Se vero, dopo il caricamento il motore va in Run da solo (apre le connessioni ai PLC).</summary>
    public bool AutoRun { get; set; }
    /// <summary>
    /// Ogni quanti secondi l'host ritenta il caricamento del motore quando e' fallito (gestionale
    /// o database non raggiungibili all'avvio, voce D145). L'host intanto e' vivo e
    /// <c>/status</c> porta il motivo. Minimo un secondo.
    /// </summary>
    public int EngineLoadRetrySeconds { get; set; } = 30;
    /// <summary>
    /// The field driver: the assembly-qualified name of an <c>IConnectionFactory</c>
    /// (<c>"Namespace.TypeName, AssemblyName"</c>), loaded by reflection from the host folder. Absent
    /// (<c>null</c>): the default of the build, if it has one (see <c>ConnectionFactoryLoader</c>).
    /// <c>none</c> (any case) or empty: no field driver, only the loopback driver (connection type
    /// <c>Test</c>). From an environment variable only <c>none</c> works: on Windows setting a
    /// variable to empty deletes it, and the key is then absent.
    /// </summary>
    public string? ConnectionFactory { get; set; }
    /// <summary>
    /// The explicit fallback for a device of connection type <c>Connected</c> whose connection was not
    /// built: <c>true</c> serves it with the loopback driver (the simulator), with a warning, and Run
    /// is not refused for it. <c>false</c> (the default): the device gets no connection and goes
    /// OffLine, and Run is refused while it lacks it. The host does not start with <c>true</c> in the
    /// Production environment (<c>SimulatorFallbackPolicy</c>).
    /// </summary>
    public bool AllowSimulatorForMissingConnections { get; set; }
    public EngineSettings Engine { get; set; } = new();

    public sealed class EngineSettings
    {
        public Guid DefaultRejectItem { get; set; }
        public int MasterProcessOnEvery { get; set; } = 1000;
        public int NumCurrentStoringStep { get; set; }
        public bool LockCellOnStoringError { get; set; }
        public bool LimitOneShippingNotePerBuffer { get; set; }
        public bool DiscardLuManagedInWarehouse { get; set; }
        public bool DisableUnloadLu { get; set; }
        public bool SwitchLuLengthVsLuWidth { get; set; }
        public bool ActivateDebugLog { get; set; }
        /// <summary>
        /// Quanto aspetta lo scarico automatico all'arrivo (<c>AutoUnloadLu</c>) prima di scaricare
        /// l'unita' di carico: senza attesa l'UDC spariva dall'elemento di uscita nell'istante
        /// dell'arrivo, e chi guarda non la vedeva mai. Zero = subito.
        /// </summary>
        public int AutoUnloadDelayMilliseconds { get; set; } = 3000;
    }
}
