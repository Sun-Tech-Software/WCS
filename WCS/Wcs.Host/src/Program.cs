using Wcs.Api.Contracts;
using Wcs.Engine;
using Wcs.Engine.Api;
using Wcs.Host;
using Wcs.Host.Endpoints;
using Wcs.Host.Engine;
using Wcs.Host.Hubs;
using Wcs.Host.Security;
using Wcs.WebEditor;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options => options.ServiceName = "Wcs.Host");

// Il file dei segreti che appsettings.json indica per Wms:ApiKey e Wcs:ApiKey. Non era letto da
// nessuno - AddJsonFile non compariva in tutto il repository - quindi chi installava seguiva
// l'istruzione, metteva la chiave nel file, riavviava, e l'host si rifiutava di partire nominando
// un file che non veniva aperto.
builder.Configuration.AddSecretsFile();

// Il limite predefinito di Kestrel sul corpo delle richieste è 30 MB, e il diagramma dell'impianto
// di prova ne pesa 48 come JSON base64: senza questa riga il PUT del diagramma riceve un 413.
// La configurazione si legge dentro la lambda perché ConfigureKestrel la esegue dopo Build(),
// quando anche le sorgenti aggiunte dal test harness sono in piedi (vedi il controllo della chiave).
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize =
    builder.Configuration.GetValue($"{WcsHostOptions.Section}:MaxRequestBodyMegabytes", 256) * 1024L * 1024L);

builder.Services.Configure<WcsHostOptions>(builder.Configuration.GetSection(WcsHostOptions.Section));
builder.Services.Configure<WmsOptions>(builder.Configuration.GetSection(WmsOptions.Section));
// La fabbrica dei client: da qui EngineHostedService prende quello verso l'API del gestionale
// (EngineHostedService.WarehouseClientName) prima di avviare il motore.
builder.Services.AddHttpClient();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<EngineExceptionHandler>();
builder.Services.AddSignalR();
builder.Services.AddSingleton<PlantEventRelay>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PlantEventRelay>());
builder.Services.AddHostedService<EngineHostedService>();
builder.Services.AddWebEditor(builder.Configuration);

var app = builder.Build();

// Il controllo sta dopo Build() e legge app.Configuration, non builder.Configuration: nel test
// harness WebApplicationFactory applica la configurazione in memoria (ConfigureAppConfiguration)
// dentro Build(), quindi leggerla prima vedrebbe ancora la chiave di esempio di appsettings.json.
var hostOptions = app.Configuration.GetSection(WcsHostOptions.Section).Get<WcsHostOptions>() ?? new();
TransportSecurityPolicy.Enforce(app.Configuration, hostOptions.AllowInsecureRemoteHttp, app.Logger);

// The simulator in place of a missing field connection only by explicit choice, and never in
// Production: there the host does not start with Wcs:AllowSimulatorForMissingConnections on.
SimulatorFallbackPolicy.Enforce(hostOptions.AllowSimulatorForMissingConnections, app.Environment, app.Logger);

ApiKeySecurity.Validate(hostOptions);
ApiKeySecurity.WarnIfWeak(hostOptions, app.Logger);

app.UseExceptionHandler();
app.UseStatusCodePages();

// Il contratto resta leggibile senza chiave dove serve - sviluppo, collaudo - ma non in
// produzione: l'host ascolta su 0.0.0.0:5580 e il middleware lascia passare /openapi senza
// chiave, quindi chiunque veda la porta leggerebbe l'elenco intero delle rotte. Non e' un dato
// di esercizio, ma e' la mappa per arrivarci.
if (!app.Environment.IsProduction()) app.MapOpenApi();

// I componenti Razor mappati da MapWebEditor portano metadati anti-forgery: senza questo
// middleware EndpointMiddleware lancia InvalidOperationException su /editor ("a middleware was
// not found that supports anti-forgery"), scoperto provando la rotta e non nel brief.
app.UseAntiforgery();

// Senza questa riga _framework/blazor.web.js risponde 404 (non e' un endpoint mappato altrimenti):
// serve gli asset statici fingerprint, framework compreso, come nel modello di progetto Blazor.
app.MapStaticAssets();

// L'Editor web (compito 4b/1): un browser che chiede /editor non manda X-Api-Key e non deve - la
// chiave la usera' il server per conto suo dal compito 2. La posizione di MapWebEditor qui, prima
// di UseMiddleware<ApiKeyMiddleware>, non e' cio' che lo fa passare (con l'hosting minimale
// l'ordine dei Map non conta, solo quello degli Use): a farlo passare e' l'allowlist dentro
// ApiKeyMiddleware, sullo stesso principio di /openapi qui sopra.
app.MapWebEditor();

// La rete di un guasto che nessuno segnala, perche' NON E' UN ERRORE: e' un 200.
// Le risorse statiche dell'Editor (blazor.web.js, draw.io, il ponte JS, il foglio di stile) non
// stanno accanto all'eseguibile di una compilazione - ci stanno solo dopo una dotnet publish. In una
// compilazione restano nelle cartelle dei progetti, e a trovarcele e' il manifesto
// .staticwebassets.runtime.json, che ASP.NET Core carica SOLO in ambiente di sviluppo. Fuori da li'
// - misurato su questo eseguibile - /_framework/blazor.web.js risponde 200 con corpo VUOTO e senza
// content-type (e cosi' /_content/Wcs.WebEditor/wcs-webeditor.css), mentre /editor/drawio risponde
// 200 con la pagina intera. Nessun 404, nessuna eccezione: uno strumento che guardi i codici di
// stato non ha niente da segnalare. La pagina arriva, il circuito Blazor non parte mai, e l'Editor
// resta una schermata morta.
// ASP.NET Core una riga la scrive ("The WebRootPath was not found ... Static files may be
// unavailable", da StaticFileMiddleware): dice che manca una cartella, non che l'Editor e' morto ne'
// che si rimedia pubblicando, e si legge benissimo come un host che di file statici non ne ha.
// La rete che esisterebbe nella pagina (_bridgeUnavailable in DrawioEditor.razor) non puo'
// scattare: si accende in OnAfterRenderAsync, che senza circuito non viene mai eseguito. Doveva
// quindi stare un piano piu' sotto, qui.
// Non si rifiuta l'avvio, di proposito: questo processo e' anche il server dell'API, che funziona
// benissimo senza l'Editor. Fermare l'avvio per un pezzo facoltativo sarebbe un rimedio peggiore
// del difetto.
if (!app.Environment.IsDevelopment() &&
    !Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot", "_framework")))
{
    app.Logger.LogWarning(
        "L'Editor web NON funzionera': accanto all'eseguibile ({Directory}) non c'e' wwwroot/_framework, quindi le " +
        "risorse statiche di Blazor non sono servibili. La pagina /editor arrivera' comunque e restera' vuota senza " +
        "nessun errore, perche' le richieste mancate rispondono 200 con un corpo vuoto invece di 404. L'API non e' " +
        "toccata e continua a funzionare. Rimedio: installare l'host con dotnet publish, non con dotnet build " +
        "(WCS/Wcs.Host/doc/architettura.md).",
        AppContext.BaseDirectory);
}

app.UseMiddleware<ApiKeyMiddleware>();

var api = app.MapGroup(ApiRoutes.Prefix);
api.MapStatus();
// Il permesso di modifica non tocca lo store ne' Navigator: sta fuori dal sottogruppo "engine" qui
// sotto, come /status, cosi' resta raggiungibile - per vedere chi lo tiene, o per rilasciarlo -
// anche quando il motore non e' caricato.
api.MapEditLock();

// Le rotte che toccano il motore rispondono 503 finche' il motore non e' caricato (voce D145):
// dopo un caricamento fallito lo store puo' essere aperto e le collezioni in memoria vuote o a
// meta', e senza questa guardia GET /plant/items rispondeva 200 con una lista vuota,
// indistinguibile da un impianto vuoto. /status sta fuori dal sottogruppo: e' li' che si legge il
// motivo, ed e' l'unica rotta che deve rispondere sempre. Il 503 lo scrive EngineExceptionHandler,
// come per lo store nullo.
var engine = api.MapGroup(string.Empty);
engine.AddEndpointFilter((context, next) =>
    Navigator.LoadError is { } reason ? throw new EngineNotLoadedException(reason) : next(context));
engine.MapCommands();
engine.MapPlant();
engine.MapGraph();
engine.MapMissions().MapExtractionQueue();
engine.MapHistory().MapPlc().MapWms();
app.MapHub<PlantHub>(ApiRoutes.Hub);

app.Run();

public partial class Program;
