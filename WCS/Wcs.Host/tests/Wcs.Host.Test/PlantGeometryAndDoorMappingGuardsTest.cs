using System.Net;
using System.Net.Http.Json;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.Engine;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Host.Test;

/// <summary>
/// Le cinque rotte che il compito "run sola lettura" ha messo sotto la stessa guardia
/// (<c>RequireNotRunning</c>, <c>PlantQueries.cs</c>) delle loro sorelle: la posizione di un
/// dispositivo, singola e d'insieme, e le tre rotte delle mappature porte. Schema di
/// <see cref="PlantDeletionGuardsTest"/>: qui si guarda solo il 409 in Run, il successo a motore
/// fermo e' gia' provato altrove (<see cref="PlantGeometryEndpointsTest"/> per la geometria,
/// <see cref="PlantEndpointsTest"/> per le mappature porte).
/// </summary>
[Collection("host")]
public sealed class PlantGeometryAndDoorMappingGuardsTest(HostFixture host) : IDisposable
{
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;

    public void Dispose() => Navigator.NavStatus = _previousStatus;

    /// <summary>
    /// La guardia e' la prima riga di ognuna delle cinque facciate (si veda il remarks su
    /// <c>SetItemGeometryAsync</c> in <c>PlantQueries.cs</c>): solleva prima di guardare se il
    /// dispositivo esiste, quindi un identificativo mai creato risponde comunque 409, non 404.
    /// </summary>
    [Fact]
    public async Task InRunTheFiveNewlyGuardedRoutesAre409()
    {
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        Navigator.NavStatus = NavigatorStatusTypes.Run;

        var itemId = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PutAsJsonAsync($"{ApiRoutes.Prefix}{ApiRoutes.Items}/{itemId}/geometry",
                new PlantItemGeometryUpdateDto(itemId, 1, 2, 3, 4, 0, Guid.NewGuid()))).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PutAsJsonAsync($"{ApiRoutes.Prefix}{ApiRoutes.ItemsGeometry}",
                new[] { new PlantItemGeometryUpdateDto(itemId, 1, 2, 3, 4, 0, Guid.NewGuid()) })).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PutAsJsonAsync(ApiRoutes.Prefix + ApiRoutes.DoorMappings,
                new[] { new DoorMappingDto(0, itemId, "A", 1, null, 0, false) })).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync(ApiRoutes.Prefix + ApiRoutes.DoorMappings + "/link",
                new DoorMappingLink(itemId, "A", 1))).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict,
            (await client.DeleteAsync($"{ApiRoutes.Prefix}{ApiRoutes.DoorMappings}/{itemId}")).StatusCode);
    }
}
