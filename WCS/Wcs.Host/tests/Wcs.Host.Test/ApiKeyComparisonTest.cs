using System.Net;
using System.Text.Json;
using Wcs.Api.Contracts;
using Xunit;

namespace Wcs.Host.Test;

/// <summary>
/// La rete intorno al confronto della chiave, che e' passato a <c>FixedTimeEquals</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Che cosa prova.</b> Che una chiave sbagliata riceve <c>401</c> con lo stesso corpo
/// dell'assenza di chiave, e che la risposta e' la <b>stessa</b> qualunque sia la forma dello
/// sbaglio: differenza al primo byte, differenza all'ultimo (cioe' prefisso uguale), piu' corta,
/// piu' lunga. E' la meta' osservabile della proprieta' che si vuole: la risposta non dice dove
/// stia la differenza. I collaudi che c'erano provavano solo l'<b>assenza</b> della chiave;
/// nessuno ne mandava una sbagliata, che e' il caso reale (le due chiavi disallineate fra host ed
/// Editor).
/// </para>
/// <para>
/// <b>Che cosa NON prova.</b> Il tempo. Un confronto a tempo costante e un
/// <c>string.Equals</c> danno lo stesso identico comportamento osservabile da qui, quindi questa
/// classe era verde <b>anche prima</b> della riparazione <c>M1</c> e lo resterebbe se la riga
/// tornasse indietro: e' una rete di sicurezza, non un discriminante. Misurare la differenza
/// vorrebbe dire cronometrare un percorso HTTP in memoria dove il jitter e' di ordini di grandezza
/// maggiore dei nanosecondi in gioco: un collaudo del genere sarebbe rumore travestito da prova.
/// Che il confronto sia a tempo costante lo tiene fermo la riga di
/// <c>ApiKeyMiddleware.KeysMatch</c>, che chiama
/// <c>CryptographicOperations.FixedTimeEquals</c>, e si verifica leggendola.
/// </para>
/// </remarks>
[Collection("host")]
public sealed class ApiKeyComparisonTest(HostFixture host)
{
    /// <summary>
    /// La chiave vera e' <c>test-key</c> (<c>HostFixture.ApiKey</c>). I quattro casi sono le
    /// quattro forme in cui una chiave puo' essere sbagliata, e la risposta deve essere la stessa
    /// per tutte.
    /// </summary>
    [Theory]
    // Differisce al PRIMO byte: un confronto che esce presto qui esce prestissimo.
    [InlineData("Xest-key")]
    // Differisce all'ULTIMO byte, cioe' prefisso uguale: un confronto che esce presto qui
    // percorre quasi tutto. La coppia con il caso di sopra e' la forma della proprieta'.
    [InlineData("test-keX")]
    // Piu' corta e piu' lunga: FixedTimeEquals le tratta come disuguaglianza immediata, e la
    // lunghezza della chiave non e' il segreto.
    [InlineData("test-ke")]
    [InlineData("test-key-piu-lunga")]
    public async Task AWrongKeyIs401WhateverTheShapeOfTheMistake(string key)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(ApiRoutes.ApiKeyHeader, key);

        var response = await client.GetAsync(ApiRoutes.Prefix + ApiRoutes.Status);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        // Il termine di paragone, preso nella stessa esecuzione: la risposta a chi la chiave non
        // la manda affatto. Siccome il paragone e' lo stesso per tutti e quattro i casi, questo
        // lega anche i quattro fra loro: chi prova non puo' distinguere ne' l'assenza dallo
        // sbaglio, ne' uno sbaglio dall'altro.
        var withoutKey = await host.CreateClient().GetAsync(ApiRoutes.Prefix + ApiRoutes.Status);
        Assert.Equal(
            await ComparableBodyAsync(withoutKey),
            await ComparableBodyAsync(response));
    }

    [Fact]
    public async Task TheRightKeyStillPasses()
    {
        var response = await host.CreateAuthenticatedClient().GetAsync(ApiRoutes.Prefix + ApiRoutes.Status);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Il corpo <c>ProblemDetails</c> senza <c>traceId</c>. Quel campo lo mette
    /// <c>AddProblemDetails</c> ed e' diverso a ogni richiesta - identifica la richiesta, non la
    /// chiave - quindi due corpi non sono mai uguali byte per byte; tutto il resto (<c>type</c>,
    /// <c>title</c>, <c>status</c>) e' quello che deve essere identico.
    /// </summary>
    private static async Task<string> ComparableBodyAsync(HttpResponseMessage response)
    {
        var fields = JsonSerializer.Deserialize<SortedDictionary<string, JsonElement>>(
            await response.Content.ReadAsStringAsync())!;
        fields.Remove("traceId");

        return string.Join('\n', fields.Select(field => $"{field.Key}={field.Value.GetRawText()}"));
    }
}
