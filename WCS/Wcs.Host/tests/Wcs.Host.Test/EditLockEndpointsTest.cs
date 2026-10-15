using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.Engine.Api;
using Xunit;

namespace Wcs.Host.Test;

/// <summary>
/// Le due rotte del permesso di modifica (compito 4, 4b): la prima richiesta lo concede, una
/// seconda identita' diversa trova 409 con chi lo tiene e da quando, e il rilascio lo libera per
/// davvero - non serve aspettare la scadenza per riprovare.
/// </summary>
[Collection("host")]
public sealed class EditLockEndpointsTest(HostFixture fixture) : IDisposable
{
    private const string Route = ApiRoutes.Prefix + ApiRoutes.EditLock;
    private readonly List<string> _holders = [];

    /// <summary>Il permesso e' statico e di processo: ogni titolare usato in un test va rilasciato, o resterebbe occupato (fino alla scadenza vera) per i test successivi.</summary>
    public void Dispose()
    {
        foreach (var holder in _holders) EditLock.Release(holder);
    }

    private HttpClient Client() => fixture.CreateAuthenticatedClient();

    private string NewHolder([CallerMemberName] string testName = "")
    {
        var holder = $"{testName}-{Guid.NewGuid()}";
        _holders.Add(holder);
        return holder;
    }

    [Fact]
    public async Task APostWithoutAnExistingPermitGrantsIt()
    {
        var holder = NewHolder();
        var response = await Client().PostAsJsonAsync(Route, new EditLockRequest(holder));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<EditLockDto>();
        Assert.NotNull(dto);
        Assert.True(dto!.IsMine);
        Assert.Equal(holder, dto.Holder);
    }

    [Fact]
    public async Task ASecondPostFromADifferentIdentityAnswers409WithWhoAndSince()
    {
        var client = Client();
        var first = NewHolder();
        var firstResponse = await client.PostAsJsonAsync(Route, new EditLockRequest(first));
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var granted = (await firstResponse.Content.ReadFromJsonAsync<EditLockDto>())!;

        var second = NewHolder();
        var secondResponse = await client.PostAsJsonAsync(Route, new EditLockRequest(second));
        Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);

        var conflict = await secondResponse.Content.ReadFromJsonAsync<EditLockDto>();
        Assert.NotNull(conflict);
        Assert.False(conflict!.IsMine);
        Assert.Equal(first, conflict.Holder);
        Assert.Equal(granted.SinceLocal, conflict.SinceLocal);
    }

    /// <summary>
    /// Compito 4b, correzione della revisione: acquisizione e rilascio in parallelo, non in
    /// sequenza come tutti gli altri test di questa classe - la sequenzialita' e' proprio quello
    /// che nascondeva il difetto. Molte identita' diverse chiedono e rilasciano concorrentemente
    /// lo stesso permesso globale (non uno per identita': <see cref="EditLock"/> e' statico e di
    /// processo), cosi' da far cadere per davvero una DELETE dentro la finestra fra
    /// un'acquisizione riuscita e la lettura dello stato che prima di questa correzione erano tre
    /// chiamate separate. Fallisce se una richiesta risponde con uno stato diverso da 200/409/204
    /// (un 500 e' esattamente il sintomo del difetto: l'eccezione su Current nullo), o se il corpo
    /// non si deserializza in un titolare non vuoto.
    /// <para>
    /// Non e' deterministico nel senso di forzare l'esatto interleaving del difetto originale -
    /// dopo la correzione quella finestra non esiste piu' strutturalmente, quindi non c'e' un modo
    /// di dimostrare "l'avrebbe preso" senza reintrodurre il difetto. E' pero' capace di fallire
    /// davvero: molte identita' (holders) per molti giri (roundsPerHolder), tutte contro lo stesso
    /// HttpClient e lo stesso TestServer in-process, generano centinaia di interleaving veri sullo
    /// scheduler del thread pool. Se qualcuno tornasse a comporre Renew/TryAcquire/Current come
    /// tre chiamate separate, questo test ha una probabilita' concreta - non garantita, ma reale -
    /// di incappare nella finestra e fallire con un errore di deserializzazione o uno stato diverso
    /// da 200/409/204.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ConcurrentAcquisitionsAndReleasesNeverThrowOrLeaveAnIncoherentState()
    {
        var client = Client();
        const int holderCount = 24;
        const int roundsPerHolder = 20;
        var holders = Enumerable.Range(0, holderCount).Select(_ => NewHolder()).ToArray();

        var tasks = holders.Select(holder => Task.Run(async () =>
        {
            for (var round = 0; round < roundsPerHolder; round++)
            {
                var post = await client.PostAsJsonAsync(Route, new EditLockRequest(holder));
                Assert.True(post.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict,
                    $"stato inatteso dal POST: {post.StatusCode} (un 500 e' il sintomo del difetto)");

                var dto = await post.Content.ReadFromJsonAsync<EditLockDto>();
                Assert.NotNull(dto);
                Assert.False(string.IsNullOrWhiteSpace(dto!.Holder), "il titolare nello stato tornato non puo' essere vuoto");

                var delete = await client.DeleteAsync($"{Route}?holder={Uri.EscapeDataString(holder)}");
                Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
            }
        }));

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task ADeleteReleasesThePermit()
    {
        var client = Client();
        var holder = NewHolder();
        await client.PostAsJsonAsync(Route, new EditLockRequest(holder));

        var delete = await client.DeleteAsync($"{Route}?holder={Uri.EscapeDataString(holder)}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        // Rilasciato per davvero: un'altra identita' lo ottiene subito, senza aspettare la scadenza.
        var otherHolder = NewHolder();
        var afterRelease = await client.PostAsJsonAsync(Route, new EditLockRequest(otherHolder));
        Assert.Equal(HttpStatusCode.OK, afterRelease.StatusCode);
        var dto = (await afterRelease.Content.ReadFromJsonAsync<EditLockDto>())!;
        Assert.True(dto.IsMine);
        Assert.Equal(otherHolder, dto.Holder);
    }
}
