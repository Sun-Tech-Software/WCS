namespace Wcs.Engine.Api;

/// <summary>
/// Il motore non ha un database su cui lavorare: <see cref="Navigator.Store"/> — o, per lo
/// storico, <see cref="Navigator.History"/> — e' nullo.
/// </summary>
/// <remarks>
/// E' la variante severa della regola che il motore applica in modo lenitivo al proprio interno
/// (<c>WcsStateWrites</c> senza store non scrive e non dice nulla, perche' l'ORM lo chiama anche
/// prima che il database sia aperto). Chi entra dalle facciate, invece, e' un chiamante di fuori:
/// deve sapere che la sua scrittura non e' avvenuta, non vedersi rispondere «fatto». L'host la
/// mappa a <c>503</c>.
/// </remarks>
public sealed class EngineStoreUnavailableException(string message) : InvalidOperationException(message);

/// <summary>
/// Il motore non e' caricato: l'ultimo caricamento e' fallito (<see cref="Navigator.LoadError"/>
/// porta il motivo) e nessuno e' riuscito dopo. Anche con lo store aperto — il guasto sul
/// gestionale lascia il database aperto e le collezioni a meta' — le rotte che toccano il motore
/// non devono rispondere 200 su un impianto che non c'e'. L'host la mappa a <c>503</c>; solo
/// <c>/status</c> resta fuori, ed e' li' che si legge il motivo (voce D145).
/// </summary>
public sealed class EngineNotLoadedException(string message) : InvalidOperationException(message);

/// <summary>
/// L'intervallo chiesto allo storico esce dal file che l'API sa aprire.
/// </summary>
/// <remarks>
/// Lo storico e' un file al mese e <c>WcsHistoryDatabase.CreateContext</c> apre quello del mese
/// corrente: una domanda su un mese passato non troverebbe righe e risponderebbe una lista vuota,
/// che chi legge scambia per «non e' successo niente». L'host la mappa a <c>400</c>.
/// </remarks>
public sealed class HistoryRangeNotAvailableException(string message) : InvalidOperationException(message);

/// <summary>Una scrittura di configurazione mentre il motore è in Run: l'host risponde 409. Si ferma il navigatore e si riprova.</summary>
public sealed class EngineBusyException(string message) : InvalidOperationException(message);

/// <summary>Il ripristino del grafo e' rifiutato perche' ci sono missioni o estrazioni pendenti.</summary>
public sealed class PlantGraphWorkPendingException(string message) : InvalidOperationException(message);

/// <summary>Un dispositivo dell'impianto che usa una connessione PLC: il suo Id e il suo nome, se ne ha uno.</summary>
public sealed record PlcConnectionUser(Guid ItemId, string? Name);

/// <summary>Una connessione PLC che una sostituzione toglierebbe, con i dispositivi che la usano ancora.</summary>
public sealed record PlcConnectionInUse(long ConnectionId, IReadOnlyList<PlcConnectionUser> Devices);

/// <summary>
/// La sostituzione delle connessioni PLC toglierebbe connessioni che dispositivi dell'impianto usano
/// ancora (<c>DeviceConnectionId</c>): e' rifiutata e non sostituisce niente (compito 4c della console,
/// decisione del proprietario del 27/9). L'host risponde <c>409</c> - lo stato dell'impianto lo vieta
/// adesso, la richiesta in se' e' ben formata - con <see cref="InUse"/> nel campo
/// <c>plcConnectionsInUse</c>. Si spostano quei dispositivi su un'altra connessione e si riprova.
/// </summary>
public sealed class PlcConnectionInUseException(IReadOnlyList<PlcConnectionInUse> inUse)
    : InvalidOperationException(Describe(inUse))
{
    public IReadOnlyList<PlcConnectionInUse> InUse { get; } = inUse;

    private static string Describe(IReadOnlyList<PlcConnectionInUse> inUse) =>
        "connessioni PLC: " +
        string.Join("; ", inUse.Select(connection =>
            $"la connessione {connection.ConnectionId} è ancora usata da " +
            (connection.Devices.Count == 1 ? "un dispositivo: " : $"{connection.Devices.Count} dispositivi: ") +
            string.Join(", ", connection.Devices.Select(device =>
                string.IsNullOrWhiteSpace(device.Name) ? device.ItemId.ToString() : $"{device.Name} ({device.ItemId})")))) +
        ". Spostarli su un'altra connessione prima di toglierla: l'elenco non è stato sostituito.";
}

/// <summary>
/// Run refused: a device of connection type Connected lacks its connection, and the setting
/// <c>Wcs:AllowSimulatorForMissingConnections</c> is off (<c>DeviceConnections.RequireConnectionsForRun</c>).
/// The message names every such device and its reason; command 1001 ends in error with it, and the
/// engine stays in Stop.
/// </summary>
public sealed class EngineRunRefusedException(string message) : InvalidOperationException(message);
