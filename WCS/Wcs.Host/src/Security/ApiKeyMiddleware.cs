using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using Wcs.Api.Contracts;

namespace Wcs.Host.Security;

/// <summary>
/// Due chiavi statiche, completa e facoltativa di sola lettura, lette esclusivamente dall'header
/// X-Api-Key anche per l'hub. La query access_token non e' accettata. Senza chiave completa
/// configurata l'host non parte: lo verifica Program. Passa liberamente /openapi, per leggere il contratto, e le
/// pagine dell'Editor web con la loro infrastruttura Blazor (compito 4b/1): un browser non manda
/// X-Api-Key e non deve, la chiave la usa il server per conto suo (compito 2).
/// </summary>
/// <remarks>
/// Qui è dove si decide chi passa: la posizione di <c>MapWebEditor</c> in <c>Program</c> rispetto
/// a <c>UseMiddleware&lt;ApiKeyMiddleware&gt;</c> no (si veda il commento sopra
/// <c>app.MapWebEditor()</c> in <c>Program.cs</c> per il perché).
/// </remarks>
public sealed class ApiKeyMiddleware(
    RequestDelegate next, IOptions<WcsHostOptions> options, ILogger<ApiKeyMiddleware> logger)
{
    /// <summary>
    /// <c>/_blazor</c> è il circuito SignalR del rendering interattivo lato server (negoziazione,
    /// disconnessione, inizializzatori); <c>/_framework</c> serve gli script del framework, come
    /// <c>blazor.web.js</c>, che il browser richiede per far partire la pagina; <c>/editor</c> è la
    /// pagina stessa; <c>/_content/Wcs.WebEditor</c> serve le risorse statiche della libreria di
    /// componenti Razor dell'Editor web (compito 4b/B1) - il foglio di stile
    /// <c>wcs-webeditor.css</c> arriva da li', con la convenzione
    /// <c>_content/{nome del progetto}/...</c> di ASP.NET Core. Il prefisso è quello della singola
    /// libreria, non l'intero spazio dei nomi <c>/_content</c>: un'altra libreria di componenti
    /// referenziata in futuro dall'host, con un proprio <c>wwwroot</c>, non diventa raggiungibile
    /// senza chiave solo per essere stata aggiunta - va aggiunta qui, di proposito.
    /// <c>/_content/Microsoft.FluentUI.AspNetCore.Components</c> è esattamente quel caso: la sesta
    /// voce, aggiunta di proposito quando la barra dell'Editor è passata ai controlli di Fluent UI
    /// Blazor. Da lì arriva il modulo che DEFINISCE i componenti web che la barra rende
    /// (<c>...lib.module.js</c>, richiesto da <c>Components/App.razor</c>) più i moduli JS dei singoli
    /// componenti. Non può chiedere la chiave per la stessa ragione delle altre voci: è il browser a
    /// chiederlo, con un <c>&lt;script src&gt;</c>, e un browser non manda <c>X-Api-Key</c> - come
    /// non la manda su <c>_framework/blazor.web.js</c>. Senza questa voce la barra non si carica e la
    /// pagina non lo dice (voce <c>D200</c> del registro).
    /// </summary>
    /// <remarks>
    /// Queste sei voci non sono una svista né un compromesso temporaneo: sono necessarie, e lo
    /// resteranno anche dopo il compito 2. La pagina che chiederà la chiave (compito 2) è essa
    /// stessa una pagina Blazor - il canale <c>/_blazor</c> deve vivere prima che qualcuno possa
    /// autenticarsi, quindi non può essere lui a proteggerlo. La sicurezza dell'Editor non vive
    /// (e non può vivere) in questo middleware: vive dentro l'applicazione, nella sessione - il
    /// compito 2 insegna all'applicazione stessa a negare ogni azione a una sessione senza chiave.
    /// Oggi il canale è acceso ma inerte, perché nessun componente dichiara una modalità di
    /// rendering interattiva: <c>Wcs.WebEditor.Test.InteractiveRenderModeSentinelTest</c> fallisce
    /// nel momento in cui il primo componente la dichiara, finché il compito 2 non è fatto.
    /// <para>
    /// <c>/_content/Wcs.WebEditor</c> è stato trovato mancante misurando, non leggendo (compito
    /// B1): un browser non manda <c>X-Api-Key</c> su un <c>&lt;link rel="stylesheet"&gt;</c> più di
    /// quanto la manda su <c>&lt;script src="_framework/blazor.web.js"&gt;</c>, ed è esattamente lo
    /// stesso motivo per cui quelle due voci ci sono già - ma prima di questa correzione una
    /// richiesta vera a <c>_content/Wcs.WebEditor/wcs-webeditor.css</c> contro l'host pubblicato
    /// tornava <b>401</b>, non il foglio di stile: lo stesso "sembra servito e non lo è" della voce
    /// <c>D200</c>, un livello più a monte (l'autorizzazione, non l'assenza del file). Il prefisso
    /// e' stato ristretto alla libreria durante la revisione di questo stesso compito: la prima
    /// versione lasciava passare l'intero <c>/_content</c>, piu' largo del bisogno accertato.
    /// </para>
    /// <para>
    /// L'elenco esatto e' sorvegliato da <c>Wcs.Host.Test.AnonymousPathPrefixesTest</c>: aggiungere,
    /// togliere o modificare una voce qui fa fallire quel test finche' non lo si aggiorna di
    /// proposito, con lo stesso messaggio che spiega perche' la voce serve. Non e' una lista che si
    /// allarga per comodita' - ogni voce e' un pezzo di superficie raggiungibile senza chiave su un
    /// host che protegge un impianto.
    /// </para>
    /// </remarks>
    internal static readonly ImmutableArray<string> AnonymousPathPrefixes =
    [
        "/openapi", "/editor", "/_blazor", "/_framework",
        "/_content/Wcs.WebEditor", "/_content/Microsoft.FluentUI.AspNetCore.Components",
    ];

    private readonly WcsHostOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        if (AnonymousPathPrefixes.Any(prefix => context.Request.Path.StartsWithSegments(prefix)))
        {
            await next(context);
            return;
        }

        var provided = context.Request.Headers[ApiRoutes.ApiKeyHeader].ToString();

        var access = ApiKeySecurity.Authenticate(provided, _options);

        // LA CHIAVE NELL'URL NON PASSA PIU', E LO SI DICE. Fino a 69e7036 l'hub la accettava anche
        // come parametro access_token; adesso solo nell'header, perche' un URL finisce nei registri.
        // La scelta resta - ma un client costruito prima (il Client di Kazam con un sottomodulo
        // vecchio) riceveva un 401 identico a quello di una chiave sbagliata e riprovava per sempre
        // senza che nessuno capisse perche'. Qui si riconosce il caso, gli si risponde con la sua
        // ragione e lo si scrive nel registro dell'host: host e client vanno aggiornati insieme.
        if (access == ApiKeyAccess.None && context.Request.Query.ContainsKey("access_token"))
        {
            logger.LogWarning(
                "Richiesta a {Path} con la chiave nel parametro access_token, che non e' piu' accettato: " +
                "il client e' stato costruito prima di 69e7036 e va aggiornato insieme all'host.",
                context.Request.Path);
            await Results.Problem(
                title: "Chiave nell'URL non più accettata",
                detail: "Il parametro access_token non è più supportato: la chiave va mandata nell'header " +
                        $"{ApiRoutes.ApiKeyHeader}. Aggiorna il client insieme all'host.",
                statusCode: StatusCodes.Status401Unauthorized).ExecuteAsync(context);
            return;
        }

        if (access == ApiKeyAccess.None)
        {
            // ProblemDetails come ogni altro rifiuto dell'API, compreso il content type
            // application/problem+json: un client che legge gli errori in un modo solo li legge tutti.
            await Results.Problem(title: "Chiave assente o sbagliata",
                statusCode: StatusCodes.Status401Unauthorized).ExecuteAsync(context);
            return;
        }

        var readOnlyAllowed = HttpMethods.IsGet(context.Request.Method) ||
                              HttpMethods.IsHead(context.Request.Method) ||
                              context.Request.Path.StartsWithSegments(ApiRoutes.Hub);
        if (access == ApiKeyAccess.ReadOnly && !readOnlyAllowed)
        {
            await Results.Problem(title: "Chiave priva del permesso di scrittura",
                statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(context);
            return;
        }

        await next(context);
    }
}
