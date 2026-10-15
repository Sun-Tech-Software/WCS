using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wcs.Api.Contracts;
using Wcs.Engine;
using Wcs.Engine.Api;
using Wcs.Engine.Persistence;
using Wcs.Host.Engine;
using Wcs.Host.Hubs;
using Wcs.Wms.Client;
using Wcs.Wms.Contracts;
using Xunit;

namespace Wcs.Host.Test;

/// <summary>
/// Il cablaggio del confine verso il gestionale: <c>Navigator.Warehouse</c> lo mette l'host, prima
/// di avviare il motore, e se la configurazione manca l'host non parte affatto. Sono i due fatti
/// che questa classe prova, e sono l'uno la ragione dell'altro: il motore avviato senza magazzino
/// non fa un servizio ridotto, solleva al primo movimento.
/// </summary>
/// <remarks>
/// <para>
/// I test costruiscono <see cref="EngineHostedService"/> a mano invece di avviare un host: quello
/// che si guarda e' l'ordine dentro <c>StartAsync</c>, e un avvio vero che fallisce lascerebbe in
/// giro lo stato statico di <c>Navigator</c> (e allo spegnimento chiamerebbe
/// <c>Navigator.Dispose</c>, che porterebbe via lo store in memoria condiviso dalla collezione).
/// Sta nella collezione "host" perche' <c>Navigator</c> e' statico: i test della collezione girano
/// in fila, e quello che questa classe tocca lo rimette a posto <see cref="Dispose"/>.
/// </para>
/// <para>
/// <b>Tre prove pero' il motore lo avviano davvero</b> — <see cref="TheWarehouseIsWiredBeforeTheEngineStarts"/>,
/// <see cref="TheWarehouseClientHasThirtySecondsByDefault"/> e
/// <see cref="TheConfiguredTimeoutReachesTheClient"/>: da quando <c>Wms:ConnectionString</c> non
/// esiste piu', fra il cablaggio del magazzino e il caricamento non c'e' nessuna guardia che
/// sollevi, quindi per vedere il magazzino cablato e il client bisogna passare oltre. Il
/// caricamento si fa fallire su <see cref="UnopenableDataDirectory"/> e si chiude con
/// <c>StopAsync</c>. E' esattamente la situazione descritta qui sopra, ed e' la ragione del
/// costruttore: <c>Navigator.Store</c> e <c>Navigator.History</c> si mettono da parte <b>prima</b>,
/// perche' <c>StopAsync</c> chiama <c>Navigator.Dispose</c> e porterebbe via lo store in memoria
/// che le altre prove della collezione si passano.
/// </para>
/// </remarks>
[Collection("host")]
public sealed class EngineWarehouseWiringTest : IDisposable
{
    private const string Address = "http://localhost:5142";
    private const string Key = "chiave-di-prova";

    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly IEngineEventSink? _previousSink = Navigator.EventSink;
    private readonly Action<Exception>? _previousErrorLog = Navigator.HostErrorLog;
    private readonly Action<string>? _previousNotice = WcsHistoryDatabase.Notice;
    private readonly string _previousDirectory = Navigator.DatabaseDirectory;
    private readonly int _previousMonths = Navigator.HistoryMonthsToKeep;
    private readonly WcsDatabase? _previousStore = Navigator.Store;
    private readonly WcsHistoryDatabase? _previousHistory = Navigator.History;
    private readonly WcsPlcLogDatabase? _previousPlcLog = Navigator.PlcLog;
    private readonly Action<string>? _previousPlcLogNotice = WcsPlcLogDatabase.Notice;
    private readonly string? _previousLoadError = Navigator.LoadError;
    private readonly RecordingClientFactory _factory = new();

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"wcs-wiring-{Guid.NewGuid():N}");

    public EngineWarehouseWiringTest()
    {
        Directory.CreateDirectory(_root);
        // Lo store, lo storico e il registro dei messaggi dei PLC della collezione non devono
        // finire disposti dallo spegnimento del servizio costruito qui (StopAsync chiama
        // Navigator.Dispose quando il motore e' acceso).
        Navigator.Store = null;
        Navigator.History = null;
        Navigator.PlcLog = null;
    }

    public void Dispose()
    {
        Navigator.Warehouse = _previousWarehouse;
        Navigator.EventSink = _previousSink;
        Navigator.HostErrorLog = _previousErrorLog;
        WcsHistoryDatabase.Notice = _previousNotice;
        Navigator.DatabaseDirectory = _previousDirectory;
        Navigator.HistoryMonthsToKeep = _previousMonths;
        Navigator.Store = _previousStore;
        Navigator.History = _previousHistory;
        Navigator.PlcLog = _previousPlcLog;
        WcsPlcLogDatabase.Notice = _previousPlcLogNotice;
        Navigator.LoadError = _previousLoadError;
        _factory.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* un file ancora aperto: la cartella temporanea resta */ }
    }

    /// <summary>
    /// Una cartella dei dati che <c>WcsDatabase.Open</c> non puo' creare, perche' con quel nome
    /// c'e' gia' un <b>file</b>: e' l'ostacolo deterministico con cui il caricamento del motore
    /// fallisce senza dipendere da niente di esterno. Serve dove la prova deve arrivare oltre il
    /// cablaggio del magazzino: fin li' non c'e' piu' nessuna guardia che sollevi, perche' quella
    /// su <c>Wms:ConnectionString</c> — la connessione a SQL Server del gestionale — non c'e'
    /// piu'.
    /// </summary>
    private string UnopenableDataDirectory()
    {
        var path = Path.Combine(_root, $"file-{Guid.NewGuid():N}");
        File.WriteAllText(path, "non e' una cartella");
        return path;
    }

    /// <summary>
    /// Indirizzo assente: il messaggio nomina la chiave e dice dove si mette. E il magazzino resta
    /// non impostato, cioe' l'host non lascia dietro di se' un cablaggio a meta'.
    /// </summary>
    [Fact]
    public async Task StartupFailsNamingTheMissingApiAddress()
    {
        Navigator.Warehouse = null;
        var service = CreateService(new WmsOptions { ApiAddress = "", ApiKey = Key });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(default));

        Assert.Contains("Wms:ApiAddress", error.Message);
        Assert.Contains("Wms__ApiAddress", error.Message);
        Assert.Null(Navigator.Warehouse);
    }

    /// <summary>Un indirizzo che non e' un'origine http assoluta e' un errore di configurazione come l'assenza, non un guasto a meta' giro.</summary>
    [Theory]
    [InlineData("localhost:5142")]
    [InlineData("/api/wcs/warehouse")]
    [InlineData("ftp://localhost:5142")]
    public async Task StartupFailsOnAnAddressThatIsNotAnHttpOrigin(string address)
    {
        Navigator.Warehouse = null;
        var service = CreateService(new WmsOptions { ApiAddress = address, ApiKey = Key });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(default));

        Assert.Contains("Wms:ApiAddress", error.Message);
        Assert.Contains(address, error.Message);
        Assert.Null(Navigator.Warehouse);
    }

    /// <summary>
    /// Chiave assente o ancora quella di esempio del file tracciato: nello stesso modo in cui
    /// Program rifiuta <c>Wcs:ApiKey</c>. Il messaggio nomina anche la chiave gemella dal lato del
    /// gestionale, perche' l'errore vero e' quasi sempre che le due non coincidono.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(WmsOptions.SampleApiKey)]
    public async Task StartupFailsNamingTheMissingApiKey(string key)
    {
        Navigator.Warehouse = null;
        var service = CreateService(new WmsOptions { ApiAddress = Address, ApiKey = key });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(default));

        Assert.Contains("Wms:ApiKey", error.Message);
        Assert.Contains("Wms__ApiKey", error.Message);
        Assert.Contains("WcsWarehouse:ApiKey", error.Message);
        Assert.Null(Navigator.Warehouse);
    }

    /// <summary>
    /// La sostanza del cablaggio: quando la configurazione c'e', il magazzino e' gia' su
    /// <c>Navigator</c> prima che il motore parta.
    /// </summary>
    /// <remarks>
    /// <para>
    /// L'osservazione sull'ordine la reggeva la guardia su <c>Wms:ConnectionString</c>, che stava
    /// fra il cablaggio e <c>Navigator.StartAsync</c>: si arrivava all'errore del passo successivo
    /// con il magazzino gia' impostato. Quella chiave non esiste piu' - il motore non apre nessun
    /// SQL Server - e dopo il cablaggio non c'e' piu' niente che sollevi: guardare
    /// <c>Navigator.Warehouse</c> <b>dopo</b> che <c>StartAsync</c> e' tornata darebbe verde anche
    /// spostando le due righe del cablaggio <b>dopo</b> <c>LoadEngineAsync</c>, cioe' non
    /// proverebbe piu' il nome di questo test.
    /// </para>
    /// <para>
    /// Quindi l'osservazione si prende <b>durante</b> il caricamento, con
    /// <see cref="LoadTimeWarehouseRecorder"/> al posto del registro muto: <c>LoadEngineAsync</c>,
    /// quando il motore non si carica, scrive «Motore non caricato: ...» — e il registratore, su
    /// ogni riga, si fotografa accanto il valore di <c>Navigator.Warehouse</c> in quel momento. La
    /// riga arriva da dentro <c>LoadEngineAsync</c>, quindi la fotografia dice che cosa c'era su
    /// <c>Navigator</c> mentre il motore veniva caricato: se il cablaggio si spostasse dopo, li'
    /// sarebbe nullo e l'asserzione diventerebbe rossa (controprova fatta a mano).
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheWarehouseIsWiredBeforeTheEngineStarts()
    {
        Navigator.Warehouse = null;
        Navigator.LoadError = null;
        var recorder = new LoadTimeWarehouseRecorder();
        var service = CreateService(new WmsOptions { ApiAddress = Address, ApiKey = Key },
            dataDirectory: UnopenableDataDirectory(), logger: recorder);

        await service.StartAsync(default);
        try
        {
            // L'ordine: la riga scritta da dentro LoadEngineAsync ha gia' il magazzino accanto.
            var duringLoad = Assert.Single(recorder.Entries, e => e.Message.Contains("Motore non caricato"));
            Assert.IsType<HttpWarehouse>(duringLoad.Warehouse);

            // E la sostanza: e' il client costruito con l'indirizzo configurato.
            Assert.NotNull(Navigator.LoadError);
            Assert.Same(Navigator.Warehouse, duringLoad.Warehouse);
            Assert.Equal(EngineHostedService.WarehouseClientName, _factory.LastName);
            Assert.Equal(new Uri(Address), _factory.LastClient?.BaseAddress);
        }
        finally
        {
            // Ferma il ritentativo del caricamento, che altrimenti resta a girare per tutta la
            // suite. Il motore non e' caricato, quindi Navigator.Dispose non ha niente da chiudere.
            await service.StopAsync(default);
        }
    }

    /// <summary>
    /// Con il motore spento (<c>Wcs:StartEngine</c> falso, come girano i collaudi) il confine non
    /// serve: l'host espone l'API senza magazzino e senza chiedere la configurazione del
    /// gestionale. E' la ragione per cui le altre suite non hanno bisogno di queste due voci.
    /// </summary>
    [Fact]
    public async Task WithoutTheEngineTheWarehouseIsNotRequired()
    {
        Navigator.Warehouse = null;
        var service = CreateService(new WmsOptions(), startEngine: false);

        await service.StartAsync(default);

        Assert.Null(Navigator.Warehouse);
        Assert.Null(_factory.LastClient);
    }

    /// <summary>
    /// Una ritenzione dello storico minore di un mese e' un errore di configurazione, non una
    /// politica: l'host non parte e lo dice.
    /// </summary>
    /// <remarks>
    /// Il valore attraversava tre passaggi - <c>WcsHostOptions</c>, <c>EngineHostedService</c>,
    /// <c>Navigator</c> - senza che nessuno lo guardasse, e <c>ApplyRetention</c> calcola
    /// l'estremo come «primo del mese corrente + (1 - MonthsToKeep) mesi»: con <c>0</c> l'estremo
    /// cade nel mese prossimo e ogni file mensile esistente ci sta prima. Dopo
    /// un'importazione quello e' l'unico storico che c'e'. <c>Wcs__HistoryMonthsToKeep=0</c> e' un
    /// refuso plausibile in una variabile d'ambiente di installazione. E' la voce D109 vista dal
    /// lato che non copre: li' il minimo lo stampa l'importatore interno (non pubblicato) e non lo applica nessuno.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task StartupFailsOnAHistoryRetentionBelowOneMonth(int months)
    {
        var previousMonths = Navigator.HistoryMonthsToKeep;
        var service = CreateService(new WmsOptions { ApiAddress = Address, ApiKey = Key },
            historyMonthsToKeep: months);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(default));

        Assert.Contains("Wcs:HistoryMonthsToKeep", error.Message);
        Assert.Contains("Wcs__HistoryMonthsToKeep", error.Message);
        // Il rifiuto arriva PRIMA che il valore finisca sul motore: altrimenti la prima
        // ritenzione porterebbe via lo storico anche con l'host che non parte.
        Assert.Equal(previousMonths, Navigator.HistoryMonthsToKeep);
    }

    /// <summary>
    /// Un mese e' il minimo, e passa: senza questo collaudo il precedente sarebbe verde anche con
    /// una guardia che rifiuta qualunque valore.
    /// </summary>
    [Fact]
    public async Task AHistoryRetentionOfOneMonthIsAccepted()
    {
        var service = CreateService(
            new WmsOptions { ApiAddress = Address, ApiKey = Key, TimeoutSeconds = 0 },
            historyMonthsToKeep: 1);

        // Si arriva alla guardia del passo successivo - era Wms:ConnectionString, che non esiste
        // piu'; adesso e' Wms:TimeoutSeconds, dentro la costruzione del magazzino - e questo vuol
        // dire che la ritenzione e' passata.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(default));

        Assert.Contains("Wms:TimeoutSeconds", error.Message);
        Assert.Equal(1, Navigator.HistoryMonthsToKeep);
    }

    /// <summary>
    /// Il tempo massimo sulle chiamate al gestionale. Senza, il client restava a quello di serie di
    /// <c>HttpClient</c>: <b>cento secondi</b>. Le chiamate stanno dentro i cicli di decisione del
    /// motore, e un gestionale appeso - processo vivo, TCP accettato, risposta che non arriva -
    /// teneva il ciclo per tutto quel tempo. Alla prova generale del 10/9 la prima ricerca della
    /// cella a database freddo ha fatto quindici secondi, gia' visibili in sala.
    /// </summary>
    [Fact]
    public async Task TheWarehouseClientHasThirtySecondsByDefault()
    {
        Navigator.Warehouse = null;
        var service = CreateService(new WmsOptions { ApiAddress = Address, ApiKey = Key },
            dataDirectory: UnopenableDataDirectory());

        // Il client si costruisce in fondo a CreateWarehouse: per vederlo bisogna passare oltre il
        // cablaggio, e li' non c'e' piu' nessuna guardia che sollevi. Il caricamento del motore
        // fallisce sulla cartella dei dati e l'host resta vivo (voce D145).
        await service.StartAsync(default);
        try
        {
            Assert.Equal(TimeSpan.FromSeconds(30), _factory.LastClient?.Timeout);
        }
        finally
        {
            await service.StopAsync(default);
        }
    }

    /// <summary>Il valore configurato arriva sul client: senza questo, un 30 scritto a mano passerebbe il collaudo qui sopra.</summary>
    [Fact]
    public async Task TheConfiguredTimeoutReachesTheClient()
    {
        Navigator.Warehouse = null;
        var service = CreateService(new WmsOptions { ApiAddress = Address, ApiKey = Key, TimeoutSeconds = 7 },
            dataDirectory: UnopenableDataDirectory());

        await service.StartAsync(default);
        try
        {
            Assert.Equal(TimeSpan.FromSeconds(7), _factory.LastClient?.Timeout);
        }
        finally
        {
            await service.StopAsync(default);
        }
    }

    /// <summary>
    /// La chiave in configurazione si chiama <c>Wms:TimeoutSeconds</c>: i due collaudi qui sopra
    /// costruiscono <see cref="WmsOptions"/> a mano e non proverebbero il nome.
    /// </summary>
    [Fact]
    public void TheTimeoutIsReadFromWmsTimeoutSeconds()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Wms:TimeoutSeconds"] = "7" }).Build();

        var options = configuration.GetSection(WmsOptions.Section).Get<WmsOptions>();

        Assert.Equal(7, options?.TimeoutSeconds);
        Assert.Equal(30, new WmsOptions().TimeoutSeconds);
    }

    /// <summary>
    /// Sotto i cinque secondi non e' una politica ma un refuso: ogni chiamata al gestionale
    /// scadrebbe. L'host non parte e lo dice, come gia' per <c>Wcs:HistoryMonthsToKeep</c>.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    public async Task StartupFailsOnATimeoutBelowFiveSeconds(int seconds)
    {
        Navigator.Warehouse = null;
        var service = CreateService(new WmsOptions { ApiAddress = Address, ApiKey = Key, TimeoutSeconds = seconds });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(default));

        Assert.Contains("Wms:TimeoutSeconds", error.Message);
        Assert.Contains("Wms__TimeoutSeconds", error.Message);
    }

    /// <summary>L'attesa dello scarico automatico arriva al motore dalla configurazione (<c>Wcs:Engine:AutoUnloadDelayMilliseconds</c>); un valore negativo diventa zero.</summary>
    [Fact]
    public async Task TheAutoUnloadDelayReachesTheEngineFromTheConfiguration()
    {
        var previous = Navigator.AutoUnloadDelay;
        try
        {
            // Come sopra: la guardia che ferma l'avvio dopo che il valore e' arrivato al motore e'
            // Wms:TimeoutSeconds, perche' Wms:ConnectionString non c'e' piu'.
            var service = CreateService(new WmsOptions { ApiAddress = Address, ApiKey = Key, TimeoutSeconds = 0 },
                historyMonthsToKeep: 1, autoUnloadDelayMilliseconds: 1500);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(default));
            Assert.Equal(TimeSpan.FromMilliseconds(1500), Navigator.AutoUnloadDelay);

            service = CreateService(new WmsOptions { ApiAddress = Address, ApiKey = Key, TimeoutSeconds = 0 },
                historyMonthsToKeep: 1, autoUnloadDelayMilliseconds: -5);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(default));
            Assert.Equal(TimeSpan.Zero, Navigator.AutoUnloadDelay);
        }
        finally
        {
            Navigator.AutoUnloadDelay = previous;
        }
    }

    /// <summary>
    /// Due host nello stesso processo, com'e' la suite in memoria: il primo si spegne mentre il
    /// secondo e' ancora vivo. <c>Navigator.EventSink</c> e' statico come <c>HostErrorLog</c>, e il
    /// secondo ha gia' preso il posto: lo spegnimento del primo deve staccare solo il proprio
    /// relay, non lasciare il secondo senza destinazione degli eventi (voce D104).
    /// </summary>
    [Fact]
    public async Task StoppingAnEarlierHostLeavesTheLiveHostEventSinkInPlace()
    {
        var firstRelay = NewRelay();
        var secondRelay = NewRelay();
        var first = CreateService(new WmsOptions(), startEngine: false, relay: firstRelay);
        var second = CreateService(new WmsOptions(), startEngine: false, relay: secondRelay);
        await first.StartAsync(default);
        await second.StartAsync(default);
        Assert.Same(secondRelay, Navigator.EventSink);

        await first.StopAsync(default);

        Assert.Same(secondRelay, Navigator.EventSink);

        // Il proprio invece si stacca: senza questo, una StopAsync che non tocca mai EventSink
        // passerebbe l'asserzione qui sopra.
        await second.StopAsync(default);
        Assert.Null(Navigator.EventSink);
    }

    private static PlantEventRelay NewRelay() => new(new SilentHub(), NullLogger<PlantEventRelay>.Instance);

    private EngineHostedService CreateService(WmsOptions wms,
        bool startEngine = true, int historyMonthsToKeep = 12, int autoUnloadDelayMilliseconds = 3000,
        PlantEventRelay? relay = null, string? dataDirectory = null,
        ILogger<EngineHostedService>? logger = null)
    {
        relay ??= NewRelay();
        return new EngineHostedService(
            Options.Create(new WcsHostOptions
            {
                ApiKey = "test-key", StartEngine = startEngine, HistoryMonthsToKeep = historyMonthsToKeep,
                DatabaseDirectory = dataDirectory ?? Path.Combine(_root, "data"),
                Engine = new WcsHostOptions.EngineSettings { AutoUnloadDelayMilliseconds = autoUnloadDelayMilliseconds }
            }),
            Options.Create(wms), _factory, relay,
            logger ?? NullLogger<EngineHostedService>.Instance);
    }

    /// <summary>
    /// Il registro che serve a vedere l'<b>ordine</b>: a ogni riga scattata si fotografa accanto il
    /// valore di <c>Navigator.Warehouse</c> in quel momento. E' l'unico modo di osservare che cosa
    /// c'era su <c>Navigator</c> <b>mentre</b> il motore veniva caricato, visto che dopo il
    /// cablaggio non resta nessuna guardia che sollevi: guardare a <c>StartAsync</c> finita non
    /// distinguerebbe il cablaggio prima dal cablaggio dopo.
    /// </summary>
    /// <remarks>
    /// Le righe arrivano dal thread che sta caricando, e il ritentativo (a trenta secondi di serie,
    /// molto oltre la durata della prova) potrebbe scriverne altre: la lista si tocca sotto lucchetto
    /// e <see cref="Entries"/> ne consegna una copia.
    /// </remarks>
    private sealed class LoadTimeWarehouseRecorder : ILogger<EngineHostedService>
    {
        private readonly List<(string Message, IWarehouse? Warehouse)> _entries = [];

        public IReadOnlyList<(string Message, IWarehouse? Warehouse)> Entries
        {
            get { lock (_entries) return _entries.ToArray(); }
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var entry = (formatter(state, exception), Navigator.Warehouse);
            lock (_entries) _entries.Add(entry);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    }

    /// <summary>La fabbrica dei client: si tiene l'ultimo nome chiesto e l'ultimo client, per guardare che l'indirizzo configurato finisca davvero sul client.</summary>
    private sealed class RecordingClientFactory : IHttpClientFactory, IDisposable
    {
        public string? LastName { get; private set; }
        public HttpClient? LastClient { get; private set; }

        public HttpClient CreateClient(string name)
        {
            LastName = name;
            return LastClient = new HttpClient();
        }

        public void Dispose() => LastClient?.Dispose();
    }

    /// <summary>L'hub che il relay riceve e non usa: in questi test nessun evento parte.</summary>
    private sealed class SilentHub : IHubContext<PlantHub, IPlantEvents>
    {
        public IHubClients<IPlantEvents> Clients => throw new NotSupportedException("il cablaggio non pubblica eventi");
        public IGroupManager Groups => throw new NotSupportedException("il cablaggio non pubblica eventi");
    }
}
