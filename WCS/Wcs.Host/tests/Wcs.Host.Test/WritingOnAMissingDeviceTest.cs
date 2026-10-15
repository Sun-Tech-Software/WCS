using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.Engine;
using Wcs.Engine.Collections;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Host.Test;

/// <summary>
/// Il contratto del lotto: <b>scrivere su un dispositivo che non esiste fallisce</b> sulle rotte di
/// <c>/plant/*</c> che un dispositivo lo nominano, e creare ha una rotta sua che dice se
/// l'identificativo era libero.
/// </summary>
/// <remarks>
/// Prima di questo lotto la stessa situazione aveva tre esiti diversi, misurati: <c>PUT
/// .../{id}/config</c> era un <b>upsert</b> e ricreava il dispositivo cancellato; <c>PUT
/// .../{id}/geometry</c> rispondeva <c>404</c>; le tre rotte della mappa porte rispondevano
/// <c>204</c> e lasciavano righe orfane. Nessun chiamante poteva distinguere «aggiorno» da
/// «resuscito».
/// <para>
/// Ogni prova di qui guarda <b>due</b> cose: il codice di stato, e che l'impianto sia rimasto com'era
/// - un 404 restituito da una rotta che ha comunque scritto sarebbe il difetto peggiore di prima con
/// una risposta piu' onesta sopra.
/// </para>
/// </remarks>
[Collection("host")]
public sealed class WritingOnAMissingDeviceTest(HostFixture host) : IDisposable
{
    private const string Items = ApiRoutes.Prefix + ApiRoutes.Items;
    private const string DoorMappings = ApiRoutes.Prefix + ApiRoutes.DoorMappings;

    private readonly NavigatorStatusTypes _previous = Navigator.NavStatus;

    public void Dispose()
    {
        PathDevices.Instance.Clear();
        Navigator.NavStatus = _previous;
    }

    /// <summary>
    /// Le cinque rotte che questa prova percorre - configurazione, geometria, e le tre della mappa
    /// porte - tutte sullo stesso identificativo mai creato. Nessuna di loro deve farlo nascere.
    /// </summary>
    /// <remarks>
    /// Non sono tutte le scritture di <c>/plant/*</c>: restano fuori quelle che un dispositivo non lo
    /// nominano (svuotamento, «tieni solo questi», modalita' di connessione, decorazione), la
    /// cancellazione e la priorita' di una giunzione (gia' <c>404</c> prima di questo lotto, provate
    /// altrove), e <c>POST /plant/junctions</c>, che un estremo inesistente lo rifiuta da tempo con
    /// un <c>400</c> - un rifiuto, non un codice diverso per la stessa regola.
    /// </remarks>
    [Fact]
    public async Task TheFiveWriteRoutesOfADeviceAnswer404WhenItIsNotThereAndCreateNothing()
    {
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        var gone = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"{Items}/{gone}/config", new PlantItemConfigDto { ItemId = gone })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"{Items}/{gone}/geometry",
                new PlantItemGeometryUpdateDto(gone, 1, 2, 3, 4, 0, Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync(DoorMappings, new[] { new DoorMappingDto(0, gone, "PORTA", 1, "A", 0, false) })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync(DoorMappings + "/link", new DoorMappingLink(gone, "PORTA", 1))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{DoorMappings}/{gone}")).StatusCode);

        // E l'impianto non se n'e' accorto: nessun dispositivo, nessuna riga di mappa porte.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Items}/{gone}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Items}/{gone}/config")).StatusCode);
        Assert.DoesNotContain((await client.GetFromJsonAsync<List<DoorMappingDto>>(DoorMappings))!, m => m.ItemId == gone);
        await using var context = host.Store.CreateContext();
        Assert.False(await context.PlantItems.AnyAsync(i => i.ItemId == gone));
        Assert.False(await context.DoorMappings.AnyAsync(m => m.ItemId == gone));
    }

    /// <summary>
    /// La rotta della creazione: <c>201</c> la prima volta, <c>409</c> la seconda, e la seconda non
    /// sovrascrive la configurazione della prima.
    /// </summary>
    /// <remarks>
    /// Il nome della seconda richiesta e' diverso apposta: un <c>409</c> che avesse comunque scritto
    /// sarebbe l'upsert di prima con un codice di stato nuovo sopra, e senza questo controllo la
    /// prova non lo vedrebbe.
    /// </remarks>
    [Fact]
    public async Task CreatingAnswers201TheFirstTimeAnd409TheSecondWithoutOverwriting()
    {
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        var id = Guid.NewGuid();

        var created = await client.PostAsJsonAsync(Items, new PlantItemConfigDto { ItemId = id, Name = "il primo" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal($"{Items}/{id}", created.Headers.Location!.ToString());

        var again = await client.PostAsJsonAsync(Items, new PlantItemConfigDto { ItemId = id, Name = "il secondo" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("il primo", (await client.GetFromJsonAsync<PlantItemConfigDto>($"{Items}/{id}/config"))!.Name);

        // E la modifica, che e' l'altra meta' del contratto, passa: il 404 e' del dispositivo
        // assente, non di ogni PUT.
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync($"{Items}/{id}/config", new PlantItemConfigDto { ItemId = id, Name = "modificato" })).StatusCode);
        Assert.Equal("modificato", (await client.GetFromJsonAsync<PlantItemConfigDto>($"{Items}/{id}/config"))!.Name);
    }

    /// <summary>Un identificativo vuoto non e' un dispositivo: <c>400</c>, non una riga con <see cref="Guid.Empty"/>.</summary>
    [Fact]
    public async Task CreatingWithAnEmptyIdentifierIs400()
    {
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync(Items, new PlantItemConfigDto { ItemId = Guid.Empty })).StatusCode);

        await using var context = host.Store.CreateContext();
        Assert.False(await context.PlantItems.AnyAsync(i => i.ItemId == Guid.Empty));
    }

    /// <summary>
    /// Il motore in marcia resta un <c>409</c> e non diventa un <c>404</c>: la guardia del Run e'
    /// prima del controllo di esistenza, sulle cinque rotte che questa prova percorre - la creazione,
    /// la configurazione e le tre della mappa porte.
    /// </summary>
    /// <remarks>
    /// Non e' una sottigliezza: le due risposte mandano chi legge in due posti diversi - «aspetta che
    /// il motore si fermi» contro «quel dispositivo non c'e' piu'» - e con l'ordine rovesciato la
    /// seconda sarebbe quella che arriva sempre, a motore acceso, per un dispositivo appena
    /// cancellato da un altro.
    /// <para>
    /// <b>Sulla creazione l'ordine si vede solo su un dispositivo che c'e'.</b> Su uno assente il
    /// <c>409</c> puo' venire solo dal Run - il controllo di esistenza non avrebbe niente da
    /// rifiutare - e la riga sarebbe verde anche con le due guardie scambiate. La prova usa quindi un
    /// dispositivo <b>gia' creato</b> e guarda il <b>corpo</b>: con la guardia del Run per prima il
    /// titolo e' «Il motore non è fermo» (fino al compito 4b della console «Il motore è in Run»);
    /// scambiandole sarebbe «Il dispositivo … esiste già», cioe' il
    /// rimedio sbagliato.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ARunningEngineIsStill409AndNot404()
    {
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        var gone = Guid.NewGuid();
        var present = Guid.NewGuid();

        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync(Items, new PlantItemConfigDto { ItemId = present })).StatusCode);

        Navigator.NavStatus = NavigatorStatusTypes.Run;

        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync(Items, new PlantItemConfigDto { ItemId = gone })).StatusCode);

        var onAnExistingDevice = await client.PostAsJsonAsync(Items, new PlantItemConfigDto { ItemId = present });
        Assert.Equal(HttpStatusCode.Conflict, onAnExistingDevice.StatusCode);
        var problem = await onAnExistingDevice.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("Il motore non è fermo", problem!.Title);

        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PutAsJsonAsync($"{Items}/{gone}/config", new PlantItemConfigDto { ItemId = gone })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PutAsJsonAsync(DoorMappings, new[] { new DoorMappingDto(0, gone, "PORTA", 1, "A", 0, false) })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync(DoorMappings + "/link", new DoorMappingLink(gone, "PORTA", 1))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"{DoorMappings}/{gone}")).StatusCode);
    }

    /// <summary>
    /// La PUT della mappa porte con piu' dispositivi: se anche uno solo non c'e', <b>nessuno</b> viene
    /// scritto, e il corpo dice quale mancava.
    /// </summary>
    [Fact]
    public async Task ADoorMapBatchWithOneMissingDeviceWritesNobody()
    {
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        var present = Guid.NewGuid();
        var gone = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(Items, new PlantItemConfigDto { ItemId = present })).StatusCode);

        var refused = await client.PutAsJsonAsync(DoorMappings, new[]
        {
            new DoorMappingDto(0, present, "PORTA-OK", 10, "A", 0, false),
            new DoorMappingDto(0, gone, "PORTA-NO", 11, "A", 0, false)
        });

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Contains(gone.ToString(), await refused.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var mappings = (await client.GetFromJsonAsync<List<DoorMappingDto>>(DoorMappings))!;
        Assert.DoesNotContain(mappings, m => m.ItemId == present); // nemmeno quello che esisteva
        Assert.DoesNotContain(mappings, m => m.ItemId == gone);
    }

    /// <summary>
    /// Cancellare un dispositivo si porta via la sua mappa porte, e non tocca quella degli altri.
    /// </summary>
    /// <remarks>
    /// E' la scelta fra le due che la chiave esterna rendeva possibili: la cascata, non il rifiuto.
    /// Il rifiuto avrebbe cambiato quello che si vede - la cancellazione di un magazzino mappato
    /// riusciva e da oggi non sarebbe piu' riuscita; la cascata la lascia riuscire e si limita a non
    /// lasciare piu' indietro righe orfane.
    /// </remarks>
    [Fact]
    public async Task DeletingADeviceTakesItsDoorMapWithItAndLeavesTheOthersAlone()
    {
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        var doomed = Guid.NewGuid();
        var neighbour = Guid.NewGuid();
        foreach (var id in new[] { doomed, neighbour })
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(Items, new PlantItemConfigDto { ItemId = id })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(DoorMappings, new[]
        {
            new DoorMappingDto(0, doomed, "PORTA-A", 20, "A", 0, false),
            new DoorMappingDto(0, neighbour, "PORTA-B", 21, "A", 0, false)
        })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Items}/{doomed}")).StatusCode);

        var mappings = (await client.GetFromJsonAsync<List<DoorMappingDto>>(DoorMappings))!;
        Assert.DoesNotContain(mappings, m => m.ItemId == doomed);
        Assert.Contains(mappings, m => m.ItemId == neighbour);

        await using var context = host.Store.CreateContext();
        Assert.False(await context.DoorMappings.AnyAsync(m => m.ItemId == doomed));
    }
}
