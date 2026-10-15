using System.Net;
using System.Net.Http.Json;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.Engine;
using Wcs.Engine.Collections;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Host.Test;

[Collection("host")]
public sealed class PlantConfigurationEndpointsTest(HostFixture host) : IDisposable
{
    private readonly NavigatorStatusTypes _previous = Navigator.NavStatus;
    private const string Items = ApiRoutes.Prefix + ApiRoutes.Items;

    public void Dispose()
    {
        PathDevices.Instance.Clear();
        PathJunctions.Instance.Clear();
        Navigator.NavStatus = _previous;
    }

    [Fact]
    public async Task AConfigurationIsWrittenAndReadBackWithEveryField()
    {
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        var id = Guid.NewGuid();
        var config = new PlantItemConfigDto { ItemId = id, Name = "Nastro", Asi = "0100", DelayBlind = 4, MaxUnloadWeight = 900, StoragePartition = "P1" };
        // Il dispositivo nasce dalla POST; la PUT lo modifica e basta, e su un dispositivo che non
        // c'e' risponde 404 invece di ricrearlo (l'ultima riga di questo test).
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(Items, config)).StatusCode);
        var put = await client.PutAsJsonAsync($"{Items}/{id}/config", config);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var read = await client.GetFromJsonAsync<PlantItemConfigDto>($"{Items}/{id}/config");
        Assert.Equal(("Nastro", "0100", 4, 900, "P1"), (read!.Name, read.Asi, read.DelayBlind, read.MaxUnloadWeight, read.StoragePartition));
        Assert.Contains((await client.GetFromJsonAsync<List<PlantItemConfigDto>>(Items + "/config"))!, c => c.ItemId == id);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Items}/{Guid.NewGuid()}/config")).StatusCode);

        var gone = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"{Items}/{gone}/config", new PlantItemConfigDto { ItemId = gone })).StatusCode);
        // E il 404 non e' solo la risposta: il dispositivo non deve essere nato lo stesso.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Items}/{gone}")).StatusCode);
        Assert.DoesNotContain((await client.GetFromJsonAsync<List<PlantItemConfigDto>>(Items + "/config"))!, c => c.ItemId == gone);
    }

    [Fact]
    public async Task AMismatchedIdIs400AndARunningEngineIs409()
    {
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        var mismatch = await client.PutAsJsonAsync($"{Items}/{Guid.NewGuid()}/config", new PlantItemConfigDto { ItemId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Navigator.NavStatus = NavigatorStatusTypes.Run;
        var id = Guid.NewGuid();
        var busy = await client.PutAsJsonAsync($"{Items}/{id}/config", new PlantItemConfigDto { ItemId = id });
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.Equal("application/problem+json", busy.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"{Items}/connection-type", new ConnectionTypeUpdate(ConnectionTypes.Test))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"{ApiRoutes.Prefix}{ApiRoutes.Junctions}/{Guid.NewGuid()}", new JunctionRequest(Guid.NewGuid(), Guid.NewGuid(), 1))).StatusCode);
    }

    [Fact]
    public async Task JunctionsAreCreatedOnceAndReprioritised()
    {
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        await client.PostAsJsonAsync(Items, new PlantItemConfigDto { ItemId = a });
        await client.PostAsJsonAsync(Items, new PlantItemConfigDto { ItemId = b });
        var junctions = ApiRoutes.Prefix + ApiRoutes.Junctions;
        var created = await client.PostAsJsonAsync(junctions, new JunctionRequest(a, b, 5));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = (await created.Content.ReadFromJsonAsync<JunctionDto>())!;
        Assert.Equal((a, b, 5), (dto.SourceItemId, dto.DestinationItemId, dto.Priority));
        var again = await (await client.PostAsJsonAsync(junctions, new JunctionRequest(a, b, 7))).Content.ReadFromJsonAsync<JunctionDto>();
        Assert.Equal(dto.JunctionId, again!.JunctionId);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"{junctions}/{dto.JunctionId}", new JunctionRequest(a, b, 1))).StatusCode);
        Assert.Contains((await client.GetFromJsonAsync<List<JunctionDto>>(junctions))!, j => j.JunctionId == dto.JunctionId && j.Priority == 1);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"{junctions}/{Guid.NewGuid()}", new JunctionRequest(a, b, 1))).StatusCode);
    }

    /// <summary>
    /// <c>DELETE /plant/items</c> e' «Nuovo diagramma» dell'Editor: svuota l'impianto intero.
    /// <c>POST /plant/items/keep</c> con lista vuota non lo fa, e non deve farlo.
    /// </summary>
    [Fact]
    public async Task EveryItemIsRemovedAtOnceAndTheCallIsRefusedWhileRunning()
    {
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        await client.PostAsJsonAsync(Items, new PlantItemConfigDto { ItemId = a, Name = "A" });
        await client.PostAsJsonAsync(Items, new PlantItemConfigDto { ItemId = b, Name = "B" });
        // Contiene, non conta: lo store dell'host e' condiviso da tutta la collezione di test.
        var written = (await client.GetFromJsonAsync<List<PlantItemConfigDto>>(Items + "/config"))!;
        Assert.Contains(written, c => c.ItemId == a);
        Assert.Contains(written, c => c.ItemId == b);

        Navigator.NavStatus = NavigatorStatusTypes.Run;
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync(Items)).StatusCode);
        var afterRefusal = (await client.GetFromJsonAsync<List<PlantItemConfigDto>>(Items + "/config"))!;
        Assert.Contains(afterRefusal, c => c.ItemId == a);
        Assert.Contains(afterRefusal, c => c.ItemId == b);

        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Items)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<PlantItemConfigDto>>(Items + "/config"))!);
        Assert.Empty((await client.GetFromJsonAsync<List<JunctionDto>>(ApiRoutes.Prefix + ApiRoutes.Junctions))!);
    }

    [Fact]
    public async Task ItemsCanBeFilteredByDeviceType()
    {
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        var buffer = Guid.NewGuid();
        var storage = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(Items, new PlantItemConfigDto { ItemId = buffer, DeviceType = (short)DeviceTypes.Buffer })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(Items, new PlantItemConfigDto { ItemId = storage, DeviceType = (short)DeviceTypes.Storage })).StatusCode);

        var buffers = await client.GetFromJsonAsync<List<PlantItemDto>>($"{Items}?deviceType={(int)DeviceTypes.Buffer}");
        Assert.Contains(buffers!, d => d.ItemId == buffer);
        Assert.DoesNotContain(buffers!, d => d.ItemId == storage);
        var both = await client.GetFromJsonAsync<List<PlantItemDto>>($"{Items}?deviceType={(int)DeviceTypes.Buffer}&deviceType={(int)DeviceTypes.Storage}");
        Assert.Contains(both!, d => d.ItemId == storage);
        Assert.Contains((await client.GetFromJsonAsync<List<PlantItemDto>>(Items))!, d => d.ItemId == storage);
    }

    [Fact]
    public async Task TheConnectionTypeIsAppliedToEveryDevice()
    {
        Navigator.NavStatus = NavigatorStatusTypes.Stop;
        var client = host.CreateAuthenticatedClient();
        using var _ = host.GrantEditPermission(client);
        var id = Guid.NewGuid();
        await client.PostAsJsonAsync(Items, new PlantItemConfigDto { ItemId = id });
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"{Items}/connection-type", new ConnectionTypeUpdate(ConnectionTypes.Test))).StatusCode);
        Assert.Equal((short)ConnectionTypes.Test, (await client.GetFromJsonAsync<PlantItemConfigDto>($"{Items}/{id}/config"))!.ConnectionType);
    }
}
