using Wcs.Api.Client;
using Wcs.WebEditor.Live;

namespace Wcs.WebEditor.Session;

/// <summary>
/// Quello che una sessione dell'Editor web tiene per se': la chiave dell'API e il client costruito
/// con lei. Registrato come <c>Scoped</c>, quindi uno per circuito Blazor.
/// </summary>
/// <remarks>
/// La chiave **non lascia il server**: sta qui, e chi disegna la usa solo attraverso
/// <see cref="Client"/>. Nel markup non compare mai, e il browser non la riceve in nessuna forma -
/// e' la ragione per cui l'Editor web gira in Blazor Server e non nel browser.
/// </remarks>
public sealed class EditorSessionState : IDisposable
{
    private readonly Uri _hostAddress;
    private readonly HttpMessageHandler? _handlerForTesting;
    private WcsClient? _client;
    private WcsClientOptions? _options;

    /// <summary>
    /// L'identificativo di questo circuito Blazor, mandato all'host per chiedere, rinnovare e
    /// rilasciare il permesso di modifica (compito 4). Non e' un'autenticazione e non dice chi e'
    /// la persona: serve solo a distinguere una scheda del browser da un'altra, ed e' stabile per
    /// tutta la vita di questa sessione - un'istanza per circuito, come il resto di questa classe.
    /// </summary>
    public string Holder { get; } = Guid.NewGuid().ToString();

    public EditorSessionState(Uri hostAddress) : this(hostAddress, handlerForTesting: null)
    {
    }

    /// <summary>
    /// Solo per i test (compito 2, punto 5 della revisione): <paramref name="handlerForTesting"/>
    /// prende il posto del gestore HTTP vero dentro il <see cref="WcsClient"/> che <see cref="UseKey"/>
    /// costruisce, cosi' un test bUnit su <c>Key.razor</c> puo' simulare una chiave accettata,
    /// rifiutata o un host irraggiungibile senza aprire una connessione vera. In produzione questo
    /// costruttore non e' usato: <see cref="WebEditorExtensions.AddWebEditor"/> chiama l'altro.
    /// </summary>
    internal EditorSessionState(Uri hostAddress, HttpMessageHandler? handlerForTesting)
    {
        _hostAddress = hostAddress;
        _handlerForTesting = handlerForTesting;
    }

    public bool HasKey => _client != null;

    /// <summary>Il client verso l'host. Solleva se nessuno ha ancora dato la chiave.</summary>
    public WcsClient Client => _client
        ?? throw new InvalidOperationException("No key in this session: the Editor cannot talk to the host.");

    public void UseKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("La chiave non puo' essere vuota.", nameof(apiKey));

        _client?.Dispose();
        _options = new WcsClientOptions { BaseAddress = _hostAddress, ApiKey = apiKey };
        _client = new WcsClient(_options, _handlerForTesting)
        {
            // L'identita' del circuito viaggia su ogni scrittura da subito, non solo dopo che il
            // permesso e' stato concesso: l'host e' quello che decide se il titolare combacia con
            // EditLock.Current, questa sessione si limita a dire sempre chi sta chiedendo.
            EditHolder = Holder
        };
    }

    public void Forget()
    {
        _client?.Dispose();
        _client = null;
        _options = null;
    }

    /// <summary>A test's own channel; see <see cref="OpenEvents"/>.</summary>
    internal Func<IPlantEventSource>? EventSourceForTesting { get; set; }

    /// <summary>
    /// A new event channel towards the host, with this session's key - one per page, which starts it
    /// and disposes of it. A session built with a fake HTTP handler gets its test's channel, or a
    /// quiet one: never a real SignalR connection towards a host that is not there.
    /// </summary>
    public IPlantEventSource OpenEvents()
    {
        if (EventSourceForTesting is { } testing) return testing();
        if (_handlerForTesting is not null) return new QuietPlantEventSource();
        return new PlantEventsSource(_options
            ?? throw new InvalidOperationException("No key in this session: the Editor cannot listen to the host."));
    }

    /// <summary>
    /// Queue item 14: the connection of this session's circuit dropped and came back (the same
    /// circuit, reconnected by Blazor - not a new one). Whatever the browser sent while it was down did
    /// not reach the server: an interop call is not sent again, and nothing says it was lost. Raised by
    /// <see cref="EditorCircuitConnection"/>, on whatever thread Blazor calls it from.
    /// </summary>
    public event Action? ConnectionCameBack;

    internal void SayTheConnectionCameBack() => ConnectionCameBack?.Invoke();

    public void Dispose() => Forget();
}
