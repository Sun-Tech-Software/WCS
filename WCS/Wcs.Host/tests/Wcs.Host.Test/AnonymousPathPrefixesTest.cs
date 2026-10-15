using Wcs.Host.Security;
using Xunit;

namespace Wcs.Host.Test;

/// <summary>
/// Il cancello sull'elenco dei percorsi anonimi (revisione del compito 4b/B1): prima di questo
/// test <see cref="ApiKeyMiddleware.AnonymousPathPrefixes"/> poteva crescere - come e' successo,
/// da quattro voci a cinque, per servire il foglio di stile dell'Editor web - senza che niente se
/// ne accorgesse. Da cinque a sei per servire il modulo dei componenti web di Fluent UI Blazor.
/// Questo test non prova che l'elenco sia "giusto": prova che e' esattamente quello
/// dichiarato qui sotto, cosi' che aggiungerne o toglierne una diventi una decisione presa
/// guardando questo file, non un effetto collaterale silenzioso.
/// </summary>
/// <remarks>
/// Sullo stampo di <see cref="OnlyAllowlistedComponentsAreInteractiveTest"/> in
/// <c>Wcs.WebEditor.Test</c>: stesso mestiere, un elenco fisso e un messaggio di fallimento che
/// spiega perche' l'elenco non va allargato solo per far ripartire la build. Reso possibile da
/// <c>InternalsVisibleTo("Wcs.Host.Test")</c>, gia' dichiarato in <c>Wcs.Host.csproj</c> per altri
/// collaudi di questo stesso progetto.
/// </remarks>
public sealed class AnonymousPathPrefixesTest
{
    /// <summary>
    /// Ogni prefisso qui passa senza chiave. Aggiungerne uno significa aprire un pezzo di
    /// superficie di un host che protegge un impianto: prima di farlo, rispondi "perche' questo
    /// percorso non puo' chiedere la chiave?" - come gia' scritto nei commenti sopra
    /// <see cref="ApiKeyMiddleware.AnonymousPathPrefixes"/> per le sei voci di oggi - e aggiorna
    /// questo elenco di proposito, non per far tornare verde il test.
    /// <para>
    /// La sesta voce, <c>/_content/Microsoft.FluentUI.AspNetCore.Components</c>, e' entrata quando la
    /// barra dell'Editor e' passata ai controlli di Fluent UI Blazor. La risposta alla domanda e'
    /// quella delle altre voci dell'Editor: da li' il BROWSER chiede il modulo che definisce i
    /// componenti web resi dalla barra (<c>...lib.module.js</c>, dichiarato in
    /// <c>Components/App.razor</c>), e un browser non manda <c>X-Api-Key</c> su un
    /// <c>&lt;script src&gt;</c> - come non la manda su <c>_framework/blazor.web.js</c>, che e' gia'
    /// qui per la stessa ragione. Senza la voce quel file torna <b>401</b> e la barra non si carica,
    /// in silenzio: e' la voce <c>D200</c> del registro - misurato contro un host acceso che non
    /// aveva ancora questa voce, chiedendo i due percorsi uno dopo l'altro:
    /// <c>/_content/Wcs.WebEditor/wcs-webeditor.css</c> tornava <b>200</b> con 46.643 byte,
    /// <c>/_content/Microsoft.FluentUI.AspNetCore.Components/...lib.module.js</c> tornava <b>401</b>
    /// con 179 byte di ProblemDetails. Non un 404: e' l'autorizzazione a rifiutare, non il file a
    /// mancare. E' un prefisso di soli file statici di una libreria di componenti, non un'API: non
    /// apre nessun comando dell'impianto.
    /// </para>
    /// </summary>
    private static readonly string[] ExpectedAnonymousPathPrefixes =
    [
        "/openapi", "/editor", "/_blazor", "/_framework",
        "/_content/Wcs.WebEditor", "/_content/Microsoft.FluentUI.AspNetCore.Components",
    ];

    [Fact]
    public void TheAnonymousPathAllowlistIsExactlyTheDeclaredSixEntries()
    {
        Assert.True(
            ExpectedAnonymousPathPrefixes.SequenceEqual(ApiKeyMiddleware.AnonymousPathPrefixes),
            "L'elenco dei percorsi anonimi e' cambiato rispetto a quello dichiarato in questo file. " +
            "Ogni voce e' un pezzo di superficie raggiungibile senza chiave su un host che protegge " +
            "un impianto: non allargarla (ne' riordinarla, ne' toglierne una) solo per far ripartire " +
            "la build. Aggiorna ExpectedAnonymousPathPrefixes qui sopra soltanto dopo aver risposto " +
            "\"perche' questo percorso non puo' chiedere la chiave?\", con la stessa decisione " +
            "scritta anche nel commento sopra ApiKeyMiddleware.AnonymousPathPrefixes.");
    }
}
