using System.Net;
using System.Net.Http.Json;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.Engine;
using Wcs.Engine.Collections;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Host.Test;

/// <summary>
/// Le due rotte che collocano i dispositivi, e i cinque campi nuovi di PlantItemDto.
/// </summary>
/// <remarks>
/// Il dispositivo si crea attraverso <c>PUT .../config</c>, lo stesso passo di
/// <see cref="PlantConfigurationEndpointsTest"/>: e' l'unico modo verificato per farlo comparire
/// sia in memoria (<see cref="PathDevices"/>, da cui legge <c>GET /plant/items</c>) sia sullo
/// store (da cui leggono e scrivono le rotte di geometria) senza correre contro i cicli di
/// sfondo del motore, che con <c>NavStatus</c> a <c>Run</c> scrivono anch'essi sullo store.
/// </remarks>
[Collection("host")]
public sealed class PlantGeometryEndpointsTest(HostFixture fixture) : IDisposable
{
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;
    private const string Items = ApiRoutes.Prefix + ApiRoutes.Items;

    public void Dispose()
    {
        PathDevices.Instance.Clear();
        Navigator.NavStatus = _previousStatus;
    }

    private HttpClient Client() => fixture.CreateAuthenticatedClient();

    private static async Task<Guid> AddDeviceAsync(HttpClient client)
    {
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        var itemId = Guid.NewGuid();
        var created = await client.PostAsJsonAsync(Items, new PlantItemConfigDto { ItemId = itemId });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return itemId;
    }

    [Fact]
    public async Task PlacingADeviceShowsUpInTheList()
    {
        var client = Client();
        using var _ = fixture.GrantEditPermission(client);
        var itemId = await AddDeviceAsync(client);
        var before = await client.GetFromJsonAsync<PlantItemDto>($"{Items}/{itemId}");
        Assert.NotNull(before);
        Assert.NotEqual(Guid.Empty, before.GeometryVersion);

        var put = await client.PutAsJsonAsync(
            $"{Items}/{itemId}/geometry",
            new PlantItemGeometryUpdateDto(itemId, 11.5, 22.5, 33, 44, 15, before.GeometryVersion));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var confirmation = await put.Content.ReadFromJsonAsync<PlantItemGeometryVersionDto>();
        Assert.NotNull(confirmation);
        Assert.Equal(itemId, confirmation.ItemId);
        Assert.NotEqual(before.GeometryVersion, confirmation.GeometryVersion);

        var after = await client.GetFromJsonAsync<List<PlantItemDto>>(Items);
        var moved = after!.Single(i => i.ItemId == itemId);
        Assert.Equal(11.5, moved.X);
        Assert.Equal(22.5, moved.Y);
        Assert.Equal(33, moved.Width);
        Assert.Equal(44, moved.Height);
        Assert.Equal(15, moved.Rotation);
        Assert.Equal(confirmation.GeometryVersion, moved.GeometryVersion);
    }

    [Fact]
    public async Task ADeviceThatDoesNotExistAnswers404()
    {
        var client = Client();
        using var _ = fixture.GrantEditPermission(client);
        // Il motore si ferma qui apposta. Questa prova non crea nessun dispositivo, quindi non passa
        // da AddDeviceAsync, che e' il punto in cui le altre lo fermano: senza questa riga lo stato
        // resta quello lasciato dalla prova precedente della collezione, e da quando la geometria
        // chiama RequireNotRunning un motore in Run risponde 409 (il rifiuto vale per l'operazione
        // intera) prima ancora di cercare il dispositivo. La prova mirava al 404 e passava per
        // ambiente, non per costruzione.
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        var ignoto = Guid.NewGuid();
        var put = await client.PutAsJsonAsync(
            $"{Items}/{ignoto}/geometry",
            new PlantItemGeometryUpdateDto(ignoto, 1, 2, 3, 4, 0, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
    }

    [Fact]
    public async Task ABatchWithAnUnknownIdAnswers404AndMovesNobody()
    {
        var client = Client();
        using var _ = fixture.GrantEditPermission(client);
        var noto = await AddDeviceAsync(client);
        var ignoto = Guid.NewGuid();
        var current = await client.GetFromJsonAsync<PlantItemDto>($"{Items}/{noto}");
        Assert.NotNull(current);

        var put = await client.PutAsJsonAsync($"{ApiRoutes.Prefix}{ApiRoutes.ItemsGeometry}", new[]
        {
            new PlantItemGeometryUpdateDto(noto, 99, 99, 99, 99, 99, current.GeometryVersion),
            new PlantItemGeometryUpdateDto(ignoto, 1, 1, 1, 1, 1, Guid.NewGuid())
        });
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);

        var after = await client.GetFromJsonAsync<List<PlantItemDto>>(Items);
        Assert.NotEqual(99, after!.Single(i => i.ItemId == noto).X);
    }

    [Fact]
    public async Task AGeometryWithoutTheVersionItReplacesAnswers400AndChangesNothing()
    {
        var client = Client();
        using var _ = fixture.GrantEditPermission(client);
        var itemId = await AddDeviceAsync(client);

        var put = await client.PutAsJsonAsync($"{Items}/{itemId}/geometry",
            new PlantItemGeometryUpdateDto(itemId, 10, 20, 30, 40, 50, null));

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        var after = await client.GetFromJsonAsync<PlantItemDto>($"{Items}/{itemId}");
        Assert.Null(after!.X);
    }

    [Fact]
    public async Task ABatchMemberWithoutTheVersionItReplacesAnswers400AndMovesNobody()
    {
        var client = Client();
        using var _ = fixture.GrantEditPermission(client);
        var versionedId = await AddDeviceAsync(client);
        var unversionedId = await AddDeviceAsync(client);
        var versioned = await client.GetFromJsonAsync<PlantItemDto>($"{Items}/{versionedId}");
        Assert.NotNull(versioned);

        var put = await client.PutAsJsonAsync($"{ApiRoutes.Prefix}{ApiRoutes.ItemsGeometry}", new[]
        {
            new PlantItemGeometryUpdateDto(versionedId, 10, 20, 30, 40, 50, versioned.GeometryVersion),
            new PlantItemGeometryUpdateDto(unversionedId, 60, 70, 80, 90, 100, null)
        });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        var after = await client.GetFromJsonAsync<List<PlantItemDto>>(Items);
        Assert.NotNull(after);
        Assert.Null(after.Single(i => i.ItemId == versionedId).X);
        Assert.Null(after.Single(i => i.ItemId == unversionedId).X);
    }

    [Fact]
    public async Task AGeometryWhoseBodyIdDiffersFromThePathAnswers400AndChangesNothing()
    {
        var client = Client();
        using var _ = fixture.GrantEditPermission(client);
        var pathId = await AddDeviceAsync(client);
        var bodyId = await AddDeviceAsync(client);
        var before = await client.GetFromJsonAsync<PlantItemDto>($"{Items}/{pathId}");
        Assert.NotNull(before);

        var put = await client.PutAsJsonAsync($"{Items}/{pathId}/geometry",
            new PlantItemGeometryUpdateDto(bodyId, 10, 20, 30, 40, 50, before.GeometryVersion));

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        var after = await client.GetFromJsonAsync<List<PlantItemDto>>(Items);
        Assert.NotNull(after);
        Assert.Null(after.Single(i => i.ItemId == pathId).X);
        Assert.Null(after.Single(i => i.ItemId == bodyId).X);
    }

    [Fact]
    public async Task AStaleSingleWriteAnswersTyped409AndKeepsTheWinningGeometry()
    {
        var client = Client();
        using var _ = fixture.GrantEditPermission(client);
        var itemId = await AddDeviceAsync(client);
        var original = await client.GetFromJsonAsync<PlantItemDto>($"{Items}/{itemId}");
        Assert.NotNull(original);

        var first = await client.PutAsJsonAsync($"{Items}/{itemId}/geometry",
            new PlantItemGeometryUpdateDto(itemId, 1, 2, 3, 4, 5, original.GeometryVersion));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var stale = await client.PutAsJsonAsync($"{Items}/{itemId}/geometry",
            new PlantItemGeometryUpdateDto(itemId, 90, 91, 92, 93, 94, original.GeometryVersion));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var conflict = await stale.Content.ReadFromJsonAsync<PlantItemGeometryConflictDto>();
        Assert.Equal([itemId], conflict!.ItemIds);
        var after = await client.GetFromJsonAsync<PlantItemDto>($"{Items}/{itemId}");
        Assert.Equal((1d, 2d, 3d, 4d, 5d),
            (after!.X, after.Y, after.Width, after.Height, after.Rotation));
    }

    [Fact]
    public async Task OneStaleBatchMemberAnswersTyped409AndMovesNobody()
    {
        var client = Client();
        using var _ = fixture.GrantEditPermission(client);
        var firstId = await AddDeviceAsync(client);
        var staleId = await AddDeviceAsync(client);
        var first = await client.GetFromJsonAsync<PlantItemDto>($"{Items}/{firstId}");
        var staleBefore = await client.GetFromJsonAsync<PlantItemDto>($"{Items}/{staleId}");
        Assert.NotNull(first);
        Assert.NotNull(staleBefore);
        var advance = await client.PutAsJsonAsync($"{Items}/{staleId}/geometry",
            new PlantItemGeometryUpdateDto(staleId, 7, 8, 9, 10, 11, staleBefore.GeometryVersion));
        Assert.Equal(HttpStatusCode.OK, advance.StatusCode);

        var put = await client.PutAsJsonAsync($"{ApiRoutes.Prefix}{ApiRoutes.ItemsGeometry}", new[]
        {
            new PlantItemGeometryUpdateDto(firstId, 20, 20, 20, 20, 20, first.GeometryVersion),
            new PlantItemGeometryUpdateDto(staleId, 30, 30, 30, 30, 30, staleBefore.GeometryVersion)
        });

        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        var conflict = await put.Content.ReadFromJsonAsync<PlantItemGeometryConflictDto>();
        Assert.Equal([staleId], conflict!.ItemIds);
        var after = await client.GetFromJsonAsync<List<PlantItemDto>>(Items);
        Assert.Null(after!.Single(i => i.ItemId == firstId).X);
        Assert.Equal(7, after!.Single(i => i.ItemId == staleId).X);
    }

    [Fact]
    public async Task DuplicateBatchIdsAnswer400AndMoveNothing()
    {
        var client = Client();
        using var _ = fixture.GrantEditPermission(client);
        var itemId = await AddDeviceAsync(client);
        var item = await client.GetFromJsonAsync<PlantItemDto>($"{Items}/{itemId}");
        Assert.NotNull(item);

        var put = await client.PutAsJsonAsync($"{ApiRoutes.Prefix}{ApiRoutes.ItemsGeometry}", new[]
        {
            new PlantItemGeometryUpdateDto(itemId, 10, 10, 10, 10, 10, item.GeometryVersion),
            new PlantItemGeometryUpdateDto(itemId, 20, 20, 20, 20, 20, item.GeometryVersion)
        });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        var after = await client.GetFromJsonAsync<PlantItemDto>($"{Items}/{itemId}");
        Assert.Null(after!.X);
    }

    [Fact]
    public async Task TheDeviceTypeRouteAnswersTheSevenBuiltInTypes()
    {
        var types = await Client().GetFromJsonAsync<List<DeviceTypeDto>>($"{ApiRoutes.Prefix}{ApiRoutes.DeviceTypes}");
        Assert.NotNull(types);
        Assert.Equal(7, types!.Count);
        var storage = types.Single(t => t.Code == "storage");
        Assert.Equal("Storage", storage.Label);
        // La sagoma viaggia come elenco di parti: non basta che il JSON arrivi, devono arrivare le
        // parti con dentro i loro tracciati - un elenco vuoto o di parti vuote passerebbe la
        // deserializzazione e disegnerebbe niente.
        Assert.NotEmpty(storage.Shape);
        Assert.All(storage.Shape, part => Assert.NotEmpty(part.Path));
        Assert.NotEmpty(storage.Ports);
    }

    /// <summary>
    /// Il colore e' un campo nuovo del contratto (compito B3-ter): questa rotta lo deve passare per
    /// ogni tipo, con il valore vero del registro. Dal lotto delle parti il colore sta sulla parte e
    /// non piu' sul tipo, e con le sagome del proprietario un tipo ne porta <b>piu' di uno</b>: la
    /// struttura il colore del tipo, le frecce i due colori suoi. Qui si confronta la sequenza
    /// intera, parte per parte e nell'ordine: un colore appiattito su tutte le parti dalla
    /// serializzazione, o due parti scambiate, non passerebbero.
    /// </summary>
    [Fact]
    public async Task TheDeviceTypeRouteAnswersTheColourOfEveryPart()
    {
        var types = await Client().GetFromJsonAsync<List<DeviceTypeDto>>($"{ApiRoutes.Prefix}{ApiRoutes.DeviceTypes}");
        Assert.NotNull(types);

        const string green = "#15956b";
        const string orange = "#e57924";
        var expected = new Dictionary<string, string[]>
        {
            ["automation"] = ["#334155", "#334155"],
            ["loading"] = ["#1d4ed8", "#1d4ed8", green],
            ["buffer"] = ["#0f766e", "#0f766e", orange],
            ["loading-and-buffer"] = ["#0e7490", "#0e7490", green, orange],
            ["group-automation"] = ["#15803d", "#15803d", "#15803d"],
            ["storage"] = ["#a16207", "#a16207"],
            ["circular-distributor"] = ["#4338ca", "#4338ca", "#4338ca"],
        };
        foreach (var (code, colours) in expected)
        {
            var parts = types!.Single(t => t.Code == code).Shape;
            Assert.NotEmpty(parts);
            Assert.Equal(colours, parts.Select(p => p.Color.ToLowerInvariant()));
        }
    }
}
