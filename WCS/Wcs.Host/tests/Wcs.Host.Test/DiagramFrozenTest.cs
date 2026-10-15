using System.Net;
using System.Net.Http.Json;
using System.Text;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.Engine;
using Wcs.Engine.Persistence;
using Xunit;

namespace Wcs.Host.Test;

/// <summary>
/// Dopo la migrazione della geometria il disegno e' congelato: la PUT non accetta piu', la GET si'.
/// </summary>
/// <remarks>
/// La migrazione marca <c>PlantDiagram.GeometryMigratedAt</c> in modo permanente e irreversibile:
/// girare su <see cref="HostFixture.Store"/> inquinerebbe lo store condiviso da tutta la
/// collezione "host" e ogni futura PUT su <c>/plant/diagram</c> di un altro test tornerebbe 405.
/// Ogni caso scambia percio' <c>Navigator.Store</c> - quello che le rotte leggono davvero, tramite
/// <c>StoreAccess.ReadAsync</c> - con uno store usa-e-getta, e lo ripristina in un
/// <c>finally</c>: lo stesso schema di <c>DeletingAnItemOrAJunctionThatIsNotThereIs404</c>, che
/// fa il cambio-e-ripristino con <c>Navigator.NavStatus</c>.
/// </remarks>
[Collection("host")]
public sealed class DiagramFrozenTest(HostFixture fixture)
{
    [Fact]
    public async Task ThePutOfTheDiagramAnswers405AfterTheMigration()
    {
        var previousStore = Navigator.Store;
        using var scratchStore = WcsDatabase.InMemory();
        Navigator.Store = scratchStore;
        try
        {
            await GeometryMigration.RunOnceAsync(scratchStore);

            // Il permesso e' esterno al congelamento: senza, il cancello risponderebbe 423 prima
            // ancora che la rotta guardi se la geometria e' migrata - qui si vuole provare il 405.
            var client = fixture.CreateAuthenticatedClient();
            using var _ = fixture.GrantEditPermission(client);
            var response = await client.PutAsJsonAsync(
                $"{ApiRoutes.Prefix}{ApiRoutes.Diagram}",
                new DiagramDto([], Encoding.UTF8.GetBytes("<x/>"), null, 0, 0, 0));

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }
        finally
        {
            Navigator.Store = previousStore;
        }
    }

    [Fact]
    public async Task ThePutOfTheDiagramAnswers405WithCorrectAllowHeader()
    {
        var previousStore = Navigator.Store;
        using var scratchStore = WcsDatabase.InMemory();
        Navigator.Store = scratchStore;
        try
        {
            await GeometryMigration.RunOnceAsync(scratchStore);

            var client = fixture.CreateAuthenticatedClient();
            using var _ = fixture.GrantEditPermission(client);
            var response = await client.PutAsJsonAsync(
                $"{ApiRoutes.Prefix}{ApiRoutes.Diagram}",
                new DiagramDto([], Encoding.UTF8.GetBytes("<x/>"), null, 0, 0, 0));

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);

            // L'intestazione Allow deve elencare solo i metodi davvero ammessi su questa risorsa.
            // RFC 9110 §15.5.6: PUT è ritirato, dunque Allow deve contenere solo GET.
            // Nota: ASP.NET Core aggiunge l'header ai Content.Headers anziché a Headers
            // quando la risposta viene scritta con WriteAsync.
            var success = response.Headers.TryGetValues("Allow", out var allowValues) ||
                         response.Content.Headers.TryGetValues("Allow", out allowValues);
            Assert.True(success && allowValues != null, "Header Allow non trovato");
            Assert.Equal("GET", Assert.Single(allowValues!));
        }
        finally
        {
            Navigator.Store = previousStore;
        }
    }

    [Fact]
    public async Task TheGetOfTheDiagramKeepsAnswering()
    {
        var previousStore = Navigator.Store;
        using var scratchStore = WcsDatabase.InMemory();
        Navigator.Store = scratchStore;
        try
        {
            // GeometryMigration.RunOnceAsync crea la riga (PlantStore.GetOrCreateDiagramAsync) se
            // manca: dopo la migrazione la riga c'e' sempre, quindi la GET deve rispondere 200 e
            // basta - e' l'unica cosa che il congelamento promette di non toccare. Un "200 oppure
            // 404" passerebbe anche se la GET si fosse rotta.
            await GeometryMigration.RunOnceAsync(scratchStore);

            var response = await fixture.CreateAuthenticatedClient()
                .GetAsync($"{ApiRoutes.Prefix}{ApiRoutes.Diagram}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            Navigator.Store = previousStore;
        }
    }
}
