using System.Net;
using System.Net.Http.Json;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawio;
using Wcs.WebEditor.Drawio.Coordination;
using Wcs.WebEditor.Model;
using Wcs.WebEditor.Session;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

public sealed class DrawioSelectionCoordinatorTest
{
    private static PlantItemDto Item(Guid id, string name) => new(
        id, name, DeviceTypes.Automation, null, null, null, default, default, 0, null,
        0, 0, 0, 0, default!, 10, 20, 130, 184, 0);

    private static PlantItemConfigDto Config(Guid id, string name) => new()
    {
        ItemId = id,
        Name = name,
        DeviceType = (short)DeviceTypes.None,
    };

    [Fact]
    public async Task ASelectionResponseFromAnOlderGenerationIsDiscarded()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var held = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new AsyncHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith($"/{first}/config", StringComparison.Ordinal)
                ? held.Task
                : Task.FromResult(Json(Config(second, "second"))));
        var coordinator = Create(handler, new PlantGraph([Item(first, "first"), Item(second, "second")], [], []));

        var firstRead = coordinator.SelectionChangedAsync([first.ToString()]);
        await Task.Yield();
        await coordinator.SelectionChangedAsync([second.ToString()]);
        Assert.Equal("second", coordinator.State.Config?.Name);

        held.SetResult(Json(Config(first, "first")));
        await firstRead;

        Assert.Equal(second, coordinator.State.ConfigItemId);
        Assert.Equal("second", coordinator.State.Config?.Name);
    }

    [Fact]
    public async Task SelectionUnavailableClearsEveryVisibleSelectionValue()
    {
        var itemId = Guid.NewGuid();
        var coordinator = Create(
            new AsyncHandler(_ => Task.FromResult(Json(Config(itemId, "device")))),
            new PlantGraph([Item(itemId, "device")], [], []));
        await coordinator.SelectionChangedAsync([itemId.ToString()]);

        coordinator.SelectionUnavailable();

        Assert.True(coordinator.State.SelectionUnavailable);
        Assert.Null(coordinator.State.SelectedCellIds);
        Assert.Null(coordinator.State.ConfigItemId);
        Assert.Null(coordinator.State.Config);
    }

    /// <summary>
    /// Final review: a 409 is the host saying the plant's state does not allow the write NOW, and
    /// the host has more than one: the engine not stopped (in Run, Recover or Dispose - its title is
    /// "Il motore non è fermo" and its detail names the state), a PLC connection still in use, work
    /// pending. The line says what a 409 means, never a fixed "the engine is in Run", and since row 37
    /// was extended to the whole page (10/10) nothing of the host's words: they are behind its "?".
    /// </summary>
    [Theory]
    [InlineData("Il motore non è fermo", "configurazione del dispositivo: il motore è in Recover")]
    [InlineData("Connessione PLC ancora usata da dispositivi", "connessione 3: Shuttle 1")]
    public void A409SaysWhatA409MeansAndNothingOfTheHostsWords(string title, string detail)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new { title, status = 409, detail });

        var meaning = DrawioSelectionCoordinator.HostRefusalMeaning(new Wcs.Api.Client.WcsApiException(HttpStatusCode.Conflict, body));

        Assert.Equal("the plant's state does not allow it now", meaning);
    }

    [Fact]
    public void A423StillSaysThePermitIsNotThisSessions()
    {
        var meaning = DrawioSelectionCoordinator.HostRefusalMeaning(new Wcs.Api.Client.WcsApiException(HttpStatusCode.Locked, "{\"title\":\"Permesso di qualcun altro\"}"));

        Assert.Equal("the edit permit is not (any longer) this session's", meaning);
    }

    private static DrawioSelectionCoordinator Create(HttpMessageHandler handler, PlantGraph graph)
    {
        var session = new EditorSessionState(new Uri("http://localhost:5580"), handler);
        session.UseKey("test-key");
        return new DrawioSelectionCoordinator(
            session,
            new DrawioPlantWriteGate(),
            () => graph,
            new Dictionary<string, Guid>(),
            () => Task.FromResult<IReadOnlyList<DrawioMarkedCell>?>([]),
            (_, _, _) => Task.FromResult(new DrawioCanvasRenameResult(true, true, null)),
            () => Task.FromResult<DrawioSaid?>(null),
            () => NavigatorStatusTypes.Stop);
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(value),
    };

    private sealed class AsyncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            respond(request);
    }
}
