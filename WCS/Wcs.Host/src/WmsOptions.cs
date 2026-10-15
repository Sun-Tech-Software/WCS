namespace Wcs.Host;

/// <summary>
/// La sezione "Wms" di appsettings per la parte HTTP del confine: dove sta l'API del gestionale e
/// con quale chiave la si chiama. Sono i due valori che servono a costruire
/// <c>Wcs.Wms.Client.HttpWarehouse</c>, cioe' l'unica implementazione vera di <c>IWarehouse</c>.
/// </summary>
/// <remarks>
/// Nella stessa sezione viveva <c>Wms:ConnectionString</c>, la connessione a SQL Server che
/// <see cref="Engine.EngineHostedService"/> leggeva da <see cref="IConfiguration"/> per il motore.
/// Non c'e' piu': il motore non ha piu' query dirette sul database del gestionale, e il confine
/// HTTP non si aggiunge a quella connessione, l'ha sostituita.
/// </remarks>
public sealed class WmsOptions
{
    public const string Section = "Wms";

    /// <summary>
    /// Il valore di esempio che il file tracciato porta al posto della chiave vera: l'host lo
    /// rifiuta come rifiuta la chiave vuota, esattamente come fa Program con <c>Wcs:ApiKey</c>.
    /// </summary>
    public const string SampleApiKey = "cambiami-prima-di-installare";

    /// <summary>L'origine dell'API del gestionale: schema, nome e porta, senza percorso (i percorsi li mette <c>WarehouseRoutes</c>).</summary>
    public string ApiAddress { get; set; } = string.Empty;

    /// <summary>La chiave dell'intestazione X-Api-Key verso il gestionale: deve essere identica a <c>WcsWarehouse:ApiKey</c> di WMS.Services.API.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Il tempo massimo di una chiamata al gestionale, in secondi. Meno di <see cref="MinimumTimeoutSeconds"/>
    /// e' un refuso, non una politica, e l'host non parte.
    /// </summary>
    /// <remarks>
    /// Senza, il client resta al valore di serie di <c>HttpClient</c>: cento secondi. Le chiamate
    /// stanno dentro i cicli di decisione del motore, e il ciclo che aspetta non decide. Alla prova
    /// generale del 10/9 la prima ricerca della cella nel gestionale ha fatto <b>quindici secondi</b>
    /// a database freddo, con l'unita' di carico ferma: quindici secondi sono gia' visibili in sala,
    /// e il tetto era cento. Un timeout di <c>HttpClient</c> solleva <c>TaskCanceledException</c> con
    /// il gettone del chiamante non annullato, quindi il confine lo traduce gia' in
    /// <c>WarehouseUnavailableException</c>.
    /// </remarks>
    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;

    /// <summary>Il tempo massimo di serie: sopra i quindici secondi osservati alla prova generale.</summary>
    public const int DefaultTimeoutSeconds = 30;

    /// <summary>Sotto questo valore ogni chiamata scadrebbe: e' un errore di configurazione.</summary>
    public const int MinimumTimeoutSeconds = 5;
}
