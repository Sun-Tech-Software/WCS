using System.Net;
using Xunit;

namespace Wcs.Host.Test;

/// <summary>
/// Il foglio di stile dell'Editor web (compito 4b/B1) raggiunge il browser da
/// <c>_content/Wcs.WebEditor/wcs-webeditor.css</c> - la convenzione con cui una libreria di
/// componenti Razor serve le proprie risorse statiche all'host che la ospita. Questo file lo prova due volte,
/// per la stessa ragione della voce <c>D200</c> ("l'Editor web non dice niente quando il suo
/// runtime non si carica"): un file statico può "sembrare" servito - la rotta risponde - e non
/// esserlo davvero, con un corpo vuoto o un content-type sbagliato. Misurato prima di scriverlo
/// (si veda il rapporto del compito): senza la correzione qui sotto, una richiesta vera contro
/// l'host pubblicato tornava <b>401</b>, non il foglio di stile - <c>ApiKeyMiddleware</c> non
/// aveva <c>/_content</c> nel proprio elenco di percorsi anonimi, a differenza di
/// <c>/_framework</c> (che serve <c>blazor.web.js</c> con lo stesso identico bisogno: un browser
/// non manda <c>X-Api-Key</c> su un <c>&lt;link&gt;</c> più di quanto la manda su uno
/// <c>&lt;script&gt;</c>).
/// </summary>
[Collection("host")]
public sealed class WebEditorStaticAssetsTest(HostFixture host)
{
    [Fact]
    public async Task TheStylesheetIsReachableWithoutTheApiKey()
    {
        var response = await host.CreateClient().GetAsync("/_content/Wcs.WebEditor/wcs-webeditor.css");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/css", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// Non basta il 200: la voce D200 e' nata proprio da una risposta 200 con zero byte
    /// (<c>WebRootPath</c> assente in un ambiente diverso da questo). Qui si conta il corpo vero,
    /// e si controlla che porti una regola vera del foglio - non un file vuoto o un ripiego
    /// generico che capiterebbe a rispondere 200 per un'altra ragione.
    /// <para>
    /// The rule looked for is one the page renders, the draw.io page's bar (.drawio-sections).
    /// Until 30/9 it was .toolbar-button, whose rules no markup wore any more: this test alone kept
    /// them in the stylesheet.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheStylesheetHasRealContentNotAnEmptyOrPlaceholderBody()
    {
        var response = await host.CreateClient().GetAsync("/_content/Wcs.WebEditor/wcs-webeditor.css");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(body.Length > 1000, $"Il foglio di stile e' arrivato con {body.Length} byte: sembra vuoto o troncato.");
        Assert.Contains(".drawio-sections", body);
    }

    /// <summary>
    /// L'allowlist si allarga per le risorse statiche, non per l'API: una rotta vera del motore
    /// resta protetta esattamente come prima - la correzione di questo compito non e' un
    /// indebolimento del cancello, solo l'aggiunta del prefisso che mancava.
    /// </summary>
    [Fact]
    public async Task WideningTheAllowlistForStaticAssetsDoesNotWeakenTheApiItself()
    {
        var response = await host.CreateClient().GetAsync("/plant/items");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Misura vera, ma di una cosa sola: il confronto del prefisso in <c>ApiKeyMiddleware</c> e'
    /// insensibile alle maiuscole, quindi <c>/_CONTENT/...</c> raggiunge comunque solo il foglio di
    /// stile vero, non un'altra rotta. <b>Che cosa non prova questo test</b>: i due tentativi di
    /// risalita con <c>..</c> non sono qui - una versione precedente li includeva, dichiarando che
    /// misurassero la normalizzazione di Kestrel su <c>.</c>/<c>..</c> prima che il middleware veda
    /// <c>Request.Path</c>. Misurato: in questo test Kestrel non partecipa affatto -
    /// <see cref="HostFixture"/> e' un <c>WebApplicationFactory</c>, server in memoria, nessun
    /// ascoltatore HTTP - ed e' <c>host.CreateClient().GetAsync(path)</c> a normalizzare da solo,
    /// lato client, prima che parta un byte: <c>System.Uri</c> collassa <c>/_content/../plant/items</c>
    /// e <c>/_content/%2e%2e/plant/items</c> nello stesso identico <c>/plant/items</c> che un'altra
    /// prova di questo file gia' chiede senza chiave. Le due righe sarebbero rimaste verdi anche con
    /// un server che non normalizza niente. La normalizzazione di un percorso vero contro Kestrel non
    /// e' misurata da questa suite: servirebbe un socket vero con una riga di richiesta non
    /// normalizzata (un <c>HttpRequestMessage</c> costruito con <c>System.Uri</c> non basta - e' lo
    /// stesso <c>Uri</c> a collassarla).
    /// </summary>
    [Fact]
    public async Task TheAllowlistPrefixComparisonIsCaseInsensitiveButStillOnlyReachesTheStylesheet()
    {
        var response = await host.CreateClient().GetAsync("/_CONTENT/Wcs.WebEditor/wcs-webeditor.css");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Il modulo JS dell'esportazione (compito 12, <c>ImportExport.razor</c>): stessa cautela della
    /// D200 - un file non tracciato come asset statico della libreria risponderebbe comunque 200,
    /// ma con zero byte, senza dirlo. Non basta che la rotta risponda: si conta il corpo vero.
    /// </summary>
    [Fact]
    public async Task ThePlantGraphDownloadScriptIsReachableAndHasRealContent()
    {
        var response = await host.CreateClient().GetAsync("/_content/Wcs.WebEditor/plant-graph-download.js");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.Length > 100, $"Lo script e' arrivato con {body.Length} byte: sembra vuoto o troncato.");
        Assert.Contains("downloadFileFromStream", body);
    }
}
