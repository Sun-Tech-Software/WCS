using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Session;
using Xunit;
using static Wcs.Common.Enums;
// "Key" is ambiguous between Bunit.Key (used by the renderer for reconciliation) and
// Wcs.WebEditor.Pages.Key (the component under test): the alias removes the ambiguity without
// giving up the using of Bunit.
using KeyPage = Wcs.WebEditor.Pages.Key;

namespace Wcs.WebEditor.Test;

/// <summary>
/// The key's path (<c>Key.razor</c>): key accepted, key rejected, host unreachable, and the double
/// submit - the three cases and the defect found with not a single test covering them. What is
/// tested is the behaviour - what the operator sees (<c>_error</c> in the markup,
/// <c>OnKeyAccepted</c> invoked or not) and what state the session is left in
/// (<see cref="EditorSessionState.HasKey"/>) - not the shape of the markup: no selectors on the
/// form's structure beyond what it takes to drive it (field and button).
/// </summary>
/// <remarks>
/// <see cref="EditorSessionState"/> here is built with the internal constructor for tests, which
/// replaces the HTTP transport of <see cref="Wcs.Api.Client.WcsClient"/> with
/// <see cref="StubHandler"/>: no real network, and the answer is the one each test decides.
/// </remarks>
public sealed class KeyTest : PlantCanvasBunitContext
{
    private static readonly string ValidStatusJson = System.Text.Json.JsonSerializer.Serialize(
        new EngineStatus(NavigatorStatusTypes.Stop, DefragmentationTypes.None, "test", DateTime.UtcNow));

    [Fact]
    public async Task AnAcceptedKeyRaisesOnKeyAcceptedAndLeavesTheSessionWithAClient()
    {
        var handler = StubHandler.Returning(HttpStatusCode.OK, ValidStatusJson);
        var state = new EditorSessionState(new Uri("http://localhost:5580"), handler);
        Services.AddSingleton(state);

        var accepted = false;
        var cut = Render<KeyPage>(p => p.Add(c => c.OnKeyAccepted, EventCallback.Factory.Create(this, () => accepted = true)));

        await cut.Find("input[type=password]").InputAsync(new ChangeEventArgs { Value = "good-key" });
        await cut.Find("button").ClickAsync(new MouseEventArgs());

        Assert.True(accepted);
        Assert.True(state.HasKey);
        Assert.DoesNotContain("rejected", cut.Markup);
        Assert.DoesNotContain("not answering", cut.Markup);
        Assert.Equal(1, handler.CallCount);
    }

    /// <summary>
    /// The defect found by task B1's review: the card reads as a sign-in form (full-width field,
    /// primary button underneath), and whoever types in it presses Enter - which before did nothing,
    /// because there was neither a &lt;form&gt; nor an @onsubmit. What is tested here is the form,
    /// not the key: <c>SubmitAsync</c> is the "submit" event a real browser sends both for Enter in
    /// a text field and for a click on a "submit" button - the same event, not two different
    /// mechanisms to test separately.
    /// </summary>
    [Fact]
    public async Task SubmittingTheFormWithoutClickingTheButtonAlsoTriesTheKey()
    {
        var handler = StubHandler.Returning(HttpStatusCode.OK, ValidStatusJson);
        var state = new EditorSessionState(new Uri("http://localhost:5580"), handler);
        Services.AddSingleton(state);

        var accepted = false;
        var cut = Render<KeyPage>(p => p.Add(c => c.OnKeyAccepted, EventCallback.Factory.Create(this, () => accepted = true)));

        await cut.Find("input[type=password]").InputAsync(new ChangeEventArgs { Value = "good-key" });
        await cut.Find("form").SubmitAsync();

        Assert.True(accepted);
        Assert.True(state.HasKey);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ARejectedKeyShowsTheRejectionMessageAndForgetsTheClient()
    {
        var handler = StubHandler.Returning(HttpStatusCode.Unauthorized, "invalid key");
        var state = new EditorSessionState(new Uri("http://localhost:5580"), handler);
        Services.AddSingleton(state);

        var accepted = false;
        var cut = Render<KeyPage>(p => p.Add(c => c.OnKeyAccepted, EventCallback.Factory.Create(this, () => accepted = true)));

        await cut.Find("input[type=password]").InputAsync(new ChangeEventArgs { Value = "wrong-key" });
        await cut.Find("button").ClickAsync(new MouseEventArgs());

        Assert.False(accepted);
        Assert.False(state.HasKey);
        Assert.Contains("Key rejected by the host", cut.Markup);
    }

    [Fact]
    public async Task AnUnreachableHostShowsADifferentMessageThanARejectedKey()
    {
        var handler = StubHandler.Throwing(new HttpRequestException("connection refused"));
        var state = new EditorSessionState(new Uri("http://localhost:5580"), handler);
        Services.AddSingleton(state);

        var accepted = false;
        var cut = Render<KeyPage>(p => p.Add(c => c.OnKeyAccepted, EventCallback.Factory.Create(this, () => accepted = true)));

        await cut.Find("input[type=password]").InputAsync(new ChangeEventArgs { Value = "a-key" });
        await cut.Find("button").ClickAsync(new MouseEventArgs());

        Assert.False(accepted);
        Assert.False(state.HasKey);
        // Two different problems must produce two different messages: whoever reads "key rejected"
        // types it again, whoever reads "the host is not answering" goes and checks the plant.
        Assert.DoesNotContain("Key rejected by the host", cut.Markup);
        Assert.Contains("not answering", cut.Markup);
    }

    [Fact]
    public async Task ATimedOutHostAlsoShowsTheUnreachableMessage()
    {
        var handler = StubHandler.Throwing(new TaskCanceledException("timeout"));
        var state = new EditorSessionState(new Uri("http://localhost:5580"), handler);
        Services.AddSingleton(state);

        var cut = Render<KeyPage>();

        await cut.Find("input[type=password]").InputAsync(new ChangeEventArgs { Value = "a-key" });
        await cut.Find("button").ClickAsync(new MouseEventArgs());

        Assert.False(state.HasKey);
        Assert.Contains("not answering", cut.Markup);
    }

    [Fact]
    public async Task ASecondClickWhileTheFirstIsStillInFlightIsIgnored()
    {
        // The first call stays suspended until the test releases it: it is the window in which a
        // second click, before the correction, made State.UseKey be called a second time while the
        // first await was still using the client UseKey had just disposed.
        var release = new TaskCompletionSource();
        var handler = StubHandler.WaitingFor(release.Task, HttpStatusCode.OK, ValidStatusJson);
        var state = new EditorSessionState(new Uri("http://localhost:5580"), handler);
        Services.AddSingleton(state);

        var cut = Render<KeyPage>();
        await cut.Find("input[type=password]").InputAsync(new ChangeEventArgs { Value = "a-key" });

        var button = cut.Find("button");
        var firstClick = button.ClickAsync(new MouseEventArgs());

        // The button is disabled while the first attempt is in flight...
        Assert.True(cut.Find("button").HasAttribute("disabled"));
        // ...and a second explicit click, were it to arrive, must not produce a second call either:
        // it is the guard in TryConnectAsync, not the HTML attribute alone, that prevents it.
        await cut.Find("button").ClickAsync(new MouseEventArgs());

        release.SetResult();
        await firstClick;

        Assert.Equal(1, handler.CallCount);
    }

    /// <summary>A fake <see cref="HttpMessageHandler"/> that answers, fails or waits as it is told - without touching the network.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode? _status;
        private readonly string? _body;
        private readonly Exception? _toThrow;
        private readonly Task? _waitFor;

        public int CallCount { get; private set; }

        private StubHandler(HttpStatusCode? status, string? body, Exception? toThrow, Task? waitFor)
        {
            _status = status;
            _body = body;
            _toThrow = toThrow;
            _waitFor = waitFor;
        }

        public static StubHandler Returning(HttpStatusCode status, string body) => new(status, body, null, null);
        public static StubHandler Throwing(Exception ex) => new(null, null, ex, null);
        public static StubHandler WaitingFor(Task gate, HttpStatusCode status, string body) => new(status, body, null, gate);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            if (_waitFor is not null) await _waitFor;
            if (_toThrow is not null) throw _toThrow;
            return new HttpResponseMessage(_status!.Value) { Content = new StringContent(_body!) };
        }
    }
}
