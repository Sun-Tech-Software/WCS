using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Wcs.WebEditor.Session;

namespace Wcs.WebEditor;

/// <summary>
/// Il cablaggio che l'host chiama: due righe in <c>Program.cs</c>, e l'Editor web e' servito.
/// </summary>
/// <remarks>
/// Sta qui e non nell'host perche' l'host non deve conoscere i componenti: gli basta accendere
/// l'area. A lasciar passare l'Editor senza <c>X-Api-Key</c> - che un browser non manda e non deve
/// - e' l'allowlist dentro <c>ApiKeyMiddleware</c>, non la posizione di <c>MapWebEditor</c> in
/// <c>Program.cs</c> (il perché e' nel commento sopra <c>app.MapWebEditor()</c> li').
/// </remarks>
public static class WebEditorExtensions
{
    /// <summary>
    /// Queue item 13: the ceiling on ONE message from the browser to a page's circuit, in bytes -
    /// SignalR's own is 32 KB, and a message over it closes the connection with nothing said on the
    /// page (the interop call it carried is lost: a save round that never ends, a paste never seen).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not for the drawing</b>, which never crosses in one message: it is streamed in pieces under
    /// this ceiling (<see cref="Drawio.DrawioDrawingTransfer"/>). This is for the messages that grow
    /// with the number of cells a gesture touches, measured on a plant of 152 devices and 294
    /// junctions (446 cells), three times DEMOPLANT: a paste of the whole drawing tells the page
    /// of its cells in 66,331 bytes (<c>OnDrawioCellsAdded</c>), the answer to <c>markedCells</c> is
    /// about 28 KB, a selection of everything 17,461 bytes (<c>OnDrawioSelectionChanged</c>), a
    /// refused cut of everything 4,056. The largest, four times over, is 265,324 bytes: 320 KB, a
    /// paste of some 2,000 cells in one go.
    /// </para>
    /// <para>
    /// <b>What the ceiling still protects.</b> SignalR holds a message whole in the server's memory
    /// before it reads a byte of it, so this is what one browser can make the server hold at once per
    /// connection; and a drawing sent in one message by mistake - a regression of item 13 - is still
    /// over it from a plant of about 115 devices, and a plant of that size is what the browser test
    /// of item 13 saves.
    /// </para>
    /// <para>
    /// <b>Queue item 15.</b> The two messages that grow with a gesture - the cells added and the cells
    /// selected - have a ceiling of their own in the bridge, under this one (drawio-bridge.js,
    /// <c>largestGestureMessage</c>, 300 KB): over it the bridge sends the page their count instead.
    /// Lowering this ceiling under that one would bring back the closed connection for them.
    /// </para>
    /// </remarks>
    internal const long CircuitMessageCeiling = 320 * 1024;

    public static IServiceCollection AddWebEditor(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddRazorComponents().AddInteractiveServerComponents()
            .AddHubOptions(options => options.MaximumReceiveMessageSize = CircuitMessageCeiling);

        // I servizi di Fluent UI Blazor (la barra dell'Editor e' fatta con i suoi controlli): stanno
        // qui e non nel Program.cs dell'host per la stessa ragione di tutto il resto di questo metodo
        // - la libreria si porta dietro cio' che le serve, e chi la ospita accende un'area, non una
        // lista di dipendenze altrui.
        // Il README di Fluent chiede anche AddHttpClient() per Blazor Server: NON si registra qui
        // perche' c'e' gia' - "builder.Services.AddHttpClient()" in Wcs.Host/src/Program.cs, messo li'
        // per altro. Registrarlo due volte non romperebbe niente, ma non e' vero che serve.
        services.AddFluentUIComponents();

        var hostAddress = ResolveHostAddress(configuration);
        // Scoped: un'istanza per circuito Blazor, cosi' la chiave di una sessione non trapela
        // nell'altra (compito 2).
        services.AddScoped(_ => new EditorSessionState(hostAddress));
        // Queue item 14: the circuit's handler that tells the session its connection came back. Scoped,
        // so that it is the circuit's own and speaks to the session of the same circuit.
        services.AddScoped<CircuitHandler, EditorCircuitConnection>();
        return services;
    }

    public static WebApplication MapWebEditor(this WebApplication app)
    {
        app.MapRazorComponents<Components.App>().AddInteractiveServerRenderMode();
        return app;
    }

    /// <summary>
    /// Riconosce un endpoint Kestrel (<c>schema://host:porta</c>, eventuale <c>/</c> finale) senza
    /// passare da <see cref="Uri.TryCreate(string?, UriKind, out Uri)"/>: quel parsing rifiuta un
    /// host jolly come <c>*</c> o <c>+</c> (non sono host di URI validi, <c>TryCreate</c> restituisce
    /// <c>false</c> e non un URI con quell'host), e per un host IPv6 letterale come <c>[::]</c>
    /// tiene le parentesi dentro <see cref="Uri.Host"/> - un confronto con <c>"::"</c> non
    /// scatterebbe mai. Qui l'host jolly e' catturato cosi' com'e' scritto, parentesi comprese.
    /// </summary>
    private static readonly Regex KestrelEndpointPattern =
        new(@"^(?<scheme>https?)://(?<host>\[[^\]]*\]|[^:/\[\]]+):(?<port>\d+)/?$", RegexOptions.Compiled);

    private static readonly string[] WildcardHosts = ["0.0.0.0", "*", "+", "::", "[::]"];

    /// <summary>
    /// L'indirizzo a cui il client dentro l'Editor web chiama l'host - se stesso. Preso da
    /// <c>Kestrel:Endpoints:Http:Url</c> (o, in sua assenza, da <c>Kestrel:Endpoints:Https:Url</c>),
    /// le stesse voci che aprono la porta: niente da tenere allineato a mano in un'opzione separata.
    /// Un endpoint puo' elencarne piu' d'uno separati da <c>;</c> (sintassi di Kestrel): si usa il
    /// primo.
    /// </summary>
    /// <remarks>
    /// Quella voce e' pero' un indirizzo di ascolto, non uno a cui collegarsi: in
    /// <c>appsettings.json</c> vale <c>http://0.0.0.0:5580</c>, e "qualunque interfaccia" non e'
    /// una destinazione valida per una richiesta in uscita. Un host jolly (<c>0.0.0.0</c>, <c>*</c>,
    /// <c>+</c>, <c>::</c>) diventa quindi <c>localhost</c>, schema e porta restano quelli
    /// configurati (si veda <see cref="KestrelEndpointPattern"/> per il perche' non basta
    /// <c>Uri.TryCreate</c>).
    /// <para>
    /// Senza una voce valida l'avvio fallisce con un'eccezione che dice che cosa configurare, invece
    /// di ripiegare in silenzio su un indirizzo di sviluppo: un ripiego cosi', in produzione, si
    /// manifesterebbe solo come «l'Editor non parla con l'host», senza dire perche' - il guasto che
    /// questo metodo deve evitare.
    /// </para>
    /// </remarks>
    internal static Uri ResolveHostAddress(IConfiguration configuration)
    {
        var configured = configuration["Kestrel:Endpoints:Http:Url"] ?? configuration["Kestrel:Endpoints:Https:Url"];
        var firstEndpoint = configured?
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        var match = firstEndpoint is null ? null : KestrelEndpointPattern.Match(firstEndpoint);
        if (match is not { Success: true })
        {
            throw new InvalidOperationException(
                "Impossibile ricavare l'indirizzo a cui l'Editor web deve collegarsi per parlare " +
                $"all'host: 'Kestrel:Endpoints:Http:Url' (o ':Https:Url') vale " +
                $"'{configured ?? "(assente)"}', non nel formato atteso 'schema://host:porta'. " +
                "Configura un endpoint Kestrel valido in appsettings.json, per esempio " +
                "\"http://0.0.0.0:5580\".");
        }

        var scheme = match.Groups["scheme"].Value;
        var host = match.Groups["host"].Value;
        var port = match.Groups["port"].Value;

        // Un host non jolly resta cosi' com'e' scritto - comprese le parentesi di un letterale
        // IPv6 vero (non wildcard): tolte, "http://2001:db8::1:5580" non sarebbe piu' un URI valido.
        var effectiveHost = WildcardHosts.Contains(host) ? "localhost" : host;
        return new Uri($"{scheme}://{effectiveHost}:{port}");
    }
}
