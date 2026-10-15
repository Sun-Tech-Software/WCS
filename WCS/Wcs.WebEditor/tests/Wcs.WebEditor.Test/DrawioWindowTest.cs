using System.Collections.Concurrent;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Wcs.WebEditor.Drawio.Components;
using Xunit;

namespace Wcs.WebEditor.Test;

/// <summary>
/// The frame the six sections of the draw.io page open in (spec 2026-09-29): hidden, not removed,
/// while shut - a section keeps its state (a pending import, a half-typed grid) across a close - and
/// a non-modal dialog while open, which the X closes. Esc is drawio-windows.js's (it presses the X
/// of the window that has the focus, unless the focus is in a field): bUnit runs no JavaScript, so
/// Esc is measured in the browser, DrawioWindowsBrowserTest.
/// </summary>
public sealed class DrawioWindowTest : BunitContext
{
    private static readonly DrawioWindowPlace Place = new(40, 140, 760, 440);

    private IRenderedComponent<DrawioWindow> RenderWindow(bool open, Action? onClose = null) =>
        Render<DrawioWindow>(p => p
            .Add(w => w.Id, "drawio-device-list")
            .Add(w => w.Title, "Devices")
            .Add(w => w.Open, open)
            .Add(w => w.Place, Place)
            .Add(w => w.OnClose, EventCallback.Factory.Create(this, onClose ?? (() => { })))
            .AddChildContent("<p id=\"inside\">rows</p>"));

    public DrawioWindowTest() => JSInterop.Mode = JSRuntimeMode.Loose;

    [Fact]
    public void AShutWindowIsHiddenButKeepsItsContent()
    {
        var cut = RenderWindow(open: false);
        Assert.True(cut.Find("#drawio-device-list").HasAttribute("hidden"));
        Assert.Equal("rows", cut.Find("#inside").TextContent);
    }

    [Fact]
    public void AnOpenWindowIsANonModalDialogNamedAfterItsSection()
    {
        var window = RenderWindow(open: true).Find("#drawio-device-list");
        Assert.False(window.HasAttribute("hidden"));
        Assert.Equal("dialog", window.GetAttribute("role"));
        Assert.Equal("false", window.GetAttribute("aria-modal"));
        Assert.Equal("Devices", window.GetAttribute("aria-label"));
    }

    [Fact]
    public void TheCrossClosesIt()
    {
        var closed = 0;
        var cut = RenderWindow(open: true, () => closed++);
        cut.Find("#drawio-device-list-close").Click();
        Assert.Equal(1, closed);
    }

    [Fact]
    public void TheModuleIsAttachedOnceAndShownAtEveryOpeningAndToldAtEveryShutting()
    {
        JSInterop.Mode = JSRuntimeMode.Strict;
        var module = JSInterop.SetupModule(DrawioWindow.ModulePath);
        module.SetupVoid("attach", _ => true).SetVoidResult();
        module.SetupVoid("show", _ => true).SetVoidResult();
        module.SetupVoid("shut", _ => true).SetVoidResult();
        module.SetupVoid("detach", _ => true).SetVoidResult();

        var cut = RenderWindow(open: false);
        cut.Render(p => p.Add(w => w.Open, true));
        cut.Render(p => p.Add(w => w.Open, false));
        cut.Render(p => p.Add(w => w.Open, true));
        // A render while it stays open (the page renders at every live frame) is not an opening:
        // show() would pull this window over the one being used.
        cut.Render(p => p.Add(w => w.Open, true));

        Assert.Single(module.Invocations["attach"]);
        Assert.Equal(2, module.Invocations["show"].Count);
        // Shut once after being shown: the focus goes back to the strip's button (the module's
        // shut(), measured in the browser). The first render, shut from the start, is not a shutting.
        Assert.IsType<ElementReference>(Assert.Single(Assert.Single(module.Invocations["shut"]).Arguments));
    }

    [Fact]
    public void AttachGetsTheElementTheIdAndThePlaceInThatOrder()
    {
        JSInterop.Mode = JSRuntimeMode.Strict;
        var module = JSInterop.SetupModule(DrawioWindow.ModulePath);
        module.SetupVoid("attach", _ => true).SetVoidResult();
        module.SetupVoid("show", _ => true).SetVoidResult();
        module.SetupVoid("detach", _ => true).SetVoidResult();

        RenderWindow(open: true);

        var attach = Assert.Single(module.Invocations["attach"]);
        Assert.Equal(4, attach.Arguments.Count);
        Assert.IsType<ElementReference>(attach.Arguments[0]);
        Assert.Equal("drawio-device-list", attach.Arguments[1]);
        Assert.Equal(Place, attach.Arguments[2]);
        Assert.Contains("Resizable = True", attach.Arguments[3]?.ToString());
        Assert.Contains("AutoSize = False", attach.Arguments[3]?.ToString());
        var show = Assert.Single(module.Invocations["show"]);
        Assert.IsType<ElementReference>(Assert.Single(show.Arguments));
    }

    /// <summary>
    /// The title bar of a section's window (passata grafica, 2026-09-29): the section's icon, its name,
    /// the count it has, the "?" with the long explanation behind it, and the X last. The explanation
    /// is in the title bar's note, shut, and nowhere in the content.
    /// </summary>
    [Fact]
    public void TheTitleBarHoldsTheIconTheNameTheCountAndTheQuestionMarkBeforeTheX()
    {
        var cut = Render<DrawioWindow>(p => p
            .Add(w => w.Id, "drawio-device-list")
            .Add(w => w.Title, "Devices")
            .Add(w => w.Icon, "devices")
            .Add(w => w.Count, "51")
            .Add(w => w.Open, true)
            .Add(w => w.Place, Place)
            .Add(w => w.Help, "<p id=\"about\">What the list is about.</p>")
            .AddChildContent("<p id=\"inside\">rows</p>"));

        var title = cut.Find("#drawio-device-list > .drawio-window-title");
        Assert.Equal(
            ["svg.drawio-icon", "span.drawio-window-name", "span.drawio-window-count", "div.drawio-help-wrap", "span#drawio-device-list-popout-status", "button#drawio-device-list-popout", "button#drawio-device-list-close"],
            title.Children.Select(Describe));
        Assert.Equal("Devices", cut.Find("#drawio-device-list > .drawio-window-title > .drawio-window-name").TextContent);
        Assert.Equal("51", cut.Find("#drawio-device-list > .drawio-window-title > .drawio-window-count").TextContent);
        Assert.Equal("About Devices",
            cut.Find("#drawio-device-list > .drawio-window-title button#drawio-device-list-help").GetAttribute("aria-label"));
        Assert.True(cut.Find("#drawio-device-list-help-body").HasAttribute("hidden"));
        Assert.Equal("What the list is about.", cut.Find("#drawio-device-list-help-body > #about").TextContent);
        Assert.Null(cut.Find("#drawio-device-list > .drawio-window-content").QuerySelector("#about"));
        var popout = cut.Find("#drawio-device-list-popout");
        Assert.Equal("Open Devices in a separate window", popout.GetAttribute("aria-label"));
        Assert.Equal("popout", popout.QuerySelector("svg")?.GetAttribute("data-icon"));
        Assert.Equal("status", cut.Find("#drawio-device-list-popout-status").GetAttribute("role"));
        Assert.Equal("polite", cut.Find("#drawio-device-list-popout-status").GetAttribute("aria-live"));

        cut.Find("#drawio-device-list-help").Click();
        Assert.False(cut.Find("#drawio-device-list-help-body").HasAttribute("hidden"));
    }

    /// <summary>
    /// A window with nothing to count and nothing to explain - a report's, behind "Details" in the row
    /// of outcomes - has the name, the pop-out's status line and button, and the X, and no empty count
    /// or "?" to press for nothing.
    /// </summary>
    [Fact]
    public void WithoutIconCountOrHelpTheTitleBarIsTheNameThePopOutAndTheX()
    {
        var title = RenderWindow(open: true).Find("#drawio-device-list > .drawio-window-title");
        Assert.Equal(
            ["span.drawio-window-name", "span#drawio-device-list-popout-status", "button#drawio-device-list-popout", "button#drawio-device-list-close"],
            title.Children.Select(Describe));
    }

    /// <summary>An element as "tag#id", or "tag.first-class" when it has no id.</summary>
    private static string Describe(AngleSharp.Dom.IElement element) =>
        element.Id is { Length: > 0 } id ? $"{element.LocalName}#{id}" : $"{element.LocalName}.{element.ClassList.FirstOrDefault()}";

    // The place a window opens at when the browser remembers nothing: a step down and to the right
    // per window, so none hides another's title bar. Literal values, not the formula.
    [Theory]
    [InlineData(0, 40, 140)]
    [InlineData(2, 104, 204)]
    public void TheCascadeStartsAt40x140AndStepsBy32(int index, int left, int top) =>
        Assert.Equal(new DrawioWindowPlace(left, top, 640, 400), DrawioWindowPlace.Cascade(index, 640, 400));

    [Fact]
    public async Task AWindowOpenedWhileTheModuleLoadsIsShownWhenItArrives()
    {
        // Blazor does not serialize after-render calls: the render that opens the window runs while
        // the first one still awaits the import.
        var runtime = new PendingImportRuntime();
        Services.AddSingleton<IJSRuntime>(runtime);

        var cut = RenderWindow(open: false);
        cut.Render(p => p.Add(w => w.Open, true));
        Assert.Empty(runtime.Calls);

        // The after-render calls that waited for the import run one after the other on the renderer's
        // context: when the first show() finishes, a second one - if there were one - has started,
        // and the empty round trip through the context below lets it be recorded.
        runtime.CompleteImport();
        await runtime.FirstShowFinished.WaitAsync(TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() => { });
        Assert.Equal(new[] { "attach", "show" }, runtime.Calls.ToArray());
    }

    /// <summary>A JS runtime whose module import stays pending until the test completes it.</summary>
    private sealed class PendingImportRuntime : IJSRuntime
    {
        private readonly TaskCompletionSource<IJSObjectReference> _import = new();
        private readonly TaskCompletionSource _firstShowFinished = new();
        public ConcurrentQueue<string> Calls { get; } = new();
        public Task FirstShowFinished => _firstShowFinished.Task;

        public void CompleteImport() => _import.SetResult(new Module(this));

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            identifier == "import"
                ? new ValueTask<TValue>(_import.Task.ContinueWith(t => (TValue)t.Result))
                : throw new InvalidOperationException(identifier);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);

        private sealed class Module(PendingImportRuntime owner) : IJSObjectReference
        {
            // A call that yields, like a real round trip: the after-render calls waiting for the
            // import get to run while a show() is still in flight.
            public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            {
                owner.Calls.Enqueue(identifier);
                await Task.Yield();
                if (identifier == "show") owner._firstShowFinished.TrySetResult();
                return default!;
            }

            public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) =>
                InvokeAsync<TValue>(identifier, args);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
