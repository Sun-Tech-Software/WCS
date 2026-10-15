using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api.Contracts;
using Wcs.Host.Security;
using Xunit;

namespace Wcs.Host.Test;

/// <summary>
/// Il cancello del permesso di modifica (compito 4b, estensione oltre le rotte del permesso
/// stesso, che si fermavano a se' stesse): fallisce se una rotta di scrittura di <c>/plant/*</c>
/// non esige <see cref="EditPermissionFilter"/>, e (compito 4b della console) se una rotta di
/// scrittura dell'API, ovunque sia, non dichiara se lo esige.
/// </summary>
/// <remarks>
/// Stesso spirito di <c>Wcs.Engine.Test.EngineDependenciesTest</c> (il confine verso il
/// gestionale) e di <c>Wcs.WebEditor.Test.BoundaryTest</c> (l'Editor web non conosce il motore): un
/// confine sorvegliato a mano si perde alla prima comodita'. Qui non si scrive a mano l'elenco
/// delle rotte - si enumerano quelle <b>registrate davvero</b> dall'host (<see cref="EndpointDataSource"/>),
/// filtrate su percorso <c>/plant/</c> e metodo HTTP di scrittura (<c>POST</c>, <c>PUT</c>,
/// <c>DELETE</c>, <c>PATCH</c>), escludendo solo <c>/plant/edit-lock</c>: le due rotte del
/// permesso non possono esigere se stesse. Dal 27/9 c'e' una seconda eccezione, nominata per nome
/// nel test e non dedotta: <c>POST /plant/graph/legacy-geometry</c>, che legge un file e non scrive
/// niente.
/// <para>
/// <b>Il numero fisso (14) e' la seconda rete, non un capriccio.</b> L'enumerazione sopra e'
/// euristica - percorso piu' metodo HTTP - e se domani mancasse di riconoscere una rotta mappata
/// in un modo diverso da quelli visti oggi (<c>MapPost</c>/<c>MapPut</c>/<c>MapDelete</c> con
/// <see cref="HttpMethodMetadata"/>), quella rotta uscirebbe dall'elenco <c>plantWrites</c> senza
/// che la prima assert se ne accorga: non sarebbe "protetta", sarebbe "mai guardata". Un conteggio
/// che cambia senza che nessuno l'abbia toccato qui e' il segnale che l'enumerazione stessa ha
/// perso o guadagnato una rotta: <b>aggiornarlo solo dopo aver capito perche'</b> - una rotta nuova
/// (o tolta) sotto <c>/plant/*</c> verificata a mano, mai per spegnere il rosso. Il messaggio di
/// fallimento della prima assert dice cosa fare: agganciare <c>.RequiresEditPermission()</c> nella
/// mappatura della rotta elencata.
/// </para>
/// </remarks>
[Collection("host")]
public sealed class EditPermissionGateTest(HostFixture fixture)
{
    private static readonly string[] WriteMethods = ["POST", "PUT", "DELETE", "PATCH"];

    [Fact]
    public void EveryPlantWriteRouteRequiresTheEditPermission()
    {
        var plantWrites = PlantWriteEndpoints().ToList();

        // La geometria (singola e d'insieme), la configurazione di un dispositivo, la modalita' di
        // connessione per tutti, la cancellazione di un dispositivo, lo svuotamento, "tieni solo
        // questi", le tre giunzioni, le due mappature porte, il collegamento celle, e il disegno
        // legacy pre-migrazione (ancora scritto da PlantGateway.SaveAsync dell'Editor WinForms):
        // quattordici rotte. Il compito 12 (esportazione/importazione) ne ha aggiunte due -
        // PUT /plant/graph (sostituisce l'impianto) e POST /plant/graph/legacy-geometry (non scrive
        // nulla; ha condiviso il permesso dell'importazione fino al 27/9, poi e' diventata l'eccezione
        // nominata qui sotto) - portando il totale a sedici. Si veda l'inventario nel rapporto del compito.
        // La diciassettesima e' PUT /plant/decoration (l'Editor su draw.io, decisione 3.6 del
        // progetto): la decorazione - scritte e riquadri liberi dell'operatore - vive nel wcs.db
        // accanto all'impianto, quindi scriverla e' una scrittura sull'impianto a tutti gli effetti.
        // Verificato a mano nella sua mappatura in PlantEndpoints che porti .RequiresEditPermission(),
        // come questo commento pretende: il numero non e' stato alzato per spegnere il rosso.
        // La diciottesima e' POST /plant/items (il lotto del 404): creare un dispositivo era finora
        // un effetto collaterale della PUT della configurazione, che il permesso ce l'aveva gia' -
        // la rotta e' nuova, il gesto no. Verificata a mano nella sua mappatura, come sopra.
        Assert.Equal(18, plantWrites.Count);

        // ONE exception besides the permit's own routes (/plant/edit-lock, already left out by
        // PlantWriteEndpoints), named here and not inferred from anything (27/9): POST
        // /plant/graph/legacy-geometry is a POST because it carries a file in its body, but it only
        // parses that file and writes nothing. It used to share the import's permit; on
        // /editor/drawio, which takes the permit only while it writes, that made the pick of every
        // .edd/.xml come back 423 (measured in a real browser). The import's write, PUT
        // /plant/graph, still requires the permit. Any other route under /plant/ that drops it
        // still fails here, whatever it declares.
        var readsByPost = plantWrites
            .Where(e => e.RoutePattern.RawText == ApiRoutes.Prefix + ApiRoutes.PlantGraphLegacyGeometry)
            .ToList();
        var legacyRead = Assert.Single(readsByPost);
        Assert.Null(legacyRead.Metadata.GetMetadata<RequiresEditPermissionMarker>());
        Assert.NotNull(legacyRead.Metadata.GetMetadata<EditPermissionNotRequiredMarker>());

        var unprotected = plantWrites
            .Except(readsByPost)
            .Where(e => e.Metadata.GetMetadata<RequiresEditPermissionMarker>() is null)
            .Select(e => $"{string.Join("/", e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods)} {e.RoutePattern.RawText}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(unprotected.Count == 0,
            "Rotte di scrittura di /plant/* senza il permesso di modifica agganciato - aggiungere " +
            ".RequiresEditPermission() nella loro mappatura in PlantEndpoints (o dove la rotta e' " +
            "definita): " + string.Join(", ", unprotected));
    }

    /// <summary>
    /// Oltre <c>/plant/*</c> (compito 4b della console, decisione del proprietario del 26/9): ogni
    /// rotta di scrittura dell'API - tutte quelle registrate sotto <see cref="ApiRoutes.Prefix"/>, non
    /// un elenco scritto qui - dichiara nella propria mappatura se esige il permesso
    /// (<c>.RequiresEditPermission()</c>) o perche' no (<c>.DoesNotRequireEditPermission("...")</c>).
    /// Una rotta nuova senza nessuna delle due fa fallire questa prova ovunque sia mappata: e' cosi'
    /// che <c>PUT /plc/connections</c>, che fino a questo compito non aveva il permesso ed era fuori
    /// dal filtro su <c>/plant/</c>, non sarebbe potuta sfuggire.
    /// </summary>
    [Fact]
    public void EveryApiWriteRouteDeclaresWhetherItRequiresTheEditPermission()
    {
        var apiWrites = WriteEndpoints()
            .Where(e => (e.RoutePattern.RawText ?? "").StartsWith(ApiRoutes.Prefix + "/", StringComparison.Ordinal))
            .ToList();

        // La seconda rete, come il 18 qui sopra: le 17 di /plant/* col permesso, PUT /plc/connections
        // col permesso (18), la lettura di un vecchio disegno che dal 27/9 dichiara di non esigerlo, e
        // le 9 che dichiarano di non esigerlo - le due del permesso stesso, i due
        // comandi (accoda, aggiorna), le due della coda di estrazione (priorita', togli), la riga di
        // registro, lo spostamento di una missione e, dall'1/10, il cambio della sua destinazione
        // (PUT /missions/{id}/destination, lavoro del motore come lo spostamento). Verificate a mano
        // nelle loro mappature.
        Assert.Equal(28, apiWrites.Count);

        var undeclared = apiWrites
            .Where(e => e.Metadata.GetMetadata<RequiresEditPermissionMarker>() is null ==
                        e.Metadata.GetMetadata<EditPermissionNotRequiredMarker>() is null)
            .Select(Describe)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(undeclared.Count == 0,
            "Rotte di scrittura dell'API che non dichiarano (o dichiarano in tutti e due i modi) se esigono " +
            "il permesso di modifica - aggiungere nella loro mappatura .RequiresEditPermission() se cambiano " +
            "la configurazione dell'impianto, altrimenti .DoesNotRequireEditPermission(\"perche'\"): " +
            string.Join(", ", undeclared));
    }

    /// <summary>
    /// La rotta del permesso stesso non lo esige da se': un cancello di test dedicato lo
    /// escluderebbe comunque dall'elenco, ma qui si prova anche il comportamento vero - una
    /// scrittura reale senza permesso risponde 423, non passa.
    /// </summary>
    [Fact]
    public async Task AWriteWithoutThePermissionHeaderIsRefusedWith423()
    {
        var client = fixture.CreateAuthenticatedClient();
        var response = await client.PostAsJsonAsync(
            ApiRoutes.Prefix + ApiRoutes.Items + "/keep", new List<Guid>());

        Assert.Equal(423, (int)response.StatusCode);
    }

    private IEnumerable<RouteEndpoint> PlantWriteEndpoints() =>
        WriteEndpoints()
            .Where(e => (e.RoutePattern.RawText ?? "").Contains("/plant/", StringComparison.Ordinal))
            .Where(e => !(e.RoutePattern.RawText ?? "").Contains(ApiRoutes.EditLock, StringComparison.Ordinal));

    private IEnumerable<RouteEndpoint> WriteEndpoints() =>
        fixture.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>() is { } m && m.HttpMethods.Any(WriteMethods.Contains));

    private static string Describe(RouteEndpoint e) =>
        $"{string.Join("/", e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods)} {e.RoutePattern.RawText}";
}
