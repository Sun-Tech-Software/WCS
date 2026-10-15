using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Wcs.WebEditor.Drawio.Components;
using Xunit;

namespace Wcs.WebEditor.Test;

/// <summary>
/// The "?" of the draw.io page (passata grafica, 2026-09-29): the long explanation a window or a panel
/// carries today goes behind it, word for word. Shut, the text is still in the page - hidden, not
/// removed - so whoever reads it by id still finds it. The "?" opens and shuts it, and Esc shuts it.
/// A click anywhere else shuts it too, but that is a press outside the note or focus leaving it,
/// which only the browser knows (drawio-help.js tells the component through
/// <see cref="DrawioHelp.Shut"/>): the gesture itself is measured in the browser, the component's
/// side of it here.
/// </summary>
public sealed class DrawioHelpTest : BunitContext
{
    // The note fetches its module the first time it opens; loose, so that the tests about the note
    // itself need not plan the module's calls. The module's own test below plans them strictly.
    public DrawioHelpTest() => JSInterop.Mode = JSRuntimeMode.Loose;

    private IRenderedComponent<DrawioHelp> RenderHelp() => Render<DrawioHelp>(p => p
        .Add(h => h.Id, "x-help").Add(h => h.Label, "About X")
        .AddChildContent("<p id=\"scope\">The long text of today.</p>"));

    /// <summary>
    /// The element a component holds in one of its <c>@ref</c> fields. bUnit 2 does not write the
    /// reference's id on the rendered element (its <c>blazor:elementReference</c> is empty), so the
    /// element the page focused is told by the reference the component itself holds for it.
    /// </summary>
    private static ElementReference ReferenceIn(object component, string field) =>
        (ElementReference)component.GetType().GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(component)!;

    [Fact]
    public void ShutTheTextIsStillInThePageButHidden()
    {
        var cut = RenderHelp();
        Assert.Equal("false", cut.Find("#x-help").GetAttribute("aria-expanded"));
        Assert.True(cut.Find("#x-help-body").HasAttribute("hidden"));
        Assert.Equal("The long text of today.", cut.Find("#scope").TextContent);
    }

    [Fact]
    public void TheQuestionMarkOpensAndShutsIt()
    {
        var cut = RenderHelp();
        cut.Find("#x-help").Click();
        Assert.False(cut.Find("#x-help-body").HasAttribute("hidden"));
        Assert.Equal("true", cut.Find("#x-help").GetAttribute("aria-expanded"));
        cut.Find("#x-help").Click();
        Assert.True(cut.Find("#x-help-body").HasAttribute("hidden"));
    }

    /// <summary>Esc shuts it, and only Esc: a key typed while reading the note - Tab, Enter, a letter - leaves it open.</summary>
    [Fact]
    public void EscapeShutsItAndNoOtherKeyDoes()
    {
        var cut = RenderHelp();
        cut.Find("#x-help").Click();

        foreach (var key in new[] { "Tab", "Enter", " ", "a", "ArrowDown" })
        {
            cut.Find("#x-help-body").KeyDown(new KeyboardEventArgs { Key = key });
            Assert.False(cut.Find("#x-help-body").HasAttribute("hidden"), $"The key \"{key}\" shut the note.");
        }

        cut.Find("#x-help-body").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        Assert.True(cut.Find("#x-help-body").HasAttribute("hidden"));
    }

    /// <summary>
    /// The module tells the component that the focus left the note, and it may say so late - after
    /// the "?" has already shut it, or twice. A shut that toggled would open the note again under a
    /// click elsewhere; it only ever shuts.
    /// </summary>
    [Fact]
    public async Task FocusLeavingTheNoteShutsItAndNeverOpensIt()
    {
        var cut = RenderHelp();
        cut.Find("#x-help").Click();

        await cut.InvokeAsync(cut.Instance.Shut);
        Assert.True(cut.Find("#x-help-body").HasAttribute("hidden"));
        Assert.Equal("false", cut.Find("#x-help").GetAttribute("aria-expanded"));

        await cut.InvokeAsync(cut.Instance.Shut);
        Assert.True(cut.Find("#x-help-body").HasAttribute("hidden"));
    }

    /// <summary>
    /// The first opening waits for the module to watch the note: the module places the note, inside
    /// the browser's window, as the browser un-hides it (drawio-help.js). Un-hidden before, the note
    /// was painted where the stylesheet hangs it - its start past the browser's left edge, from a
    /// window dragged there - until the module's round trips came back. While the module has not
    /// answered, the "?" leaves the note shut and says so; once it watches, the note opens.
    /// </summary>
    [Fact]
    public void TheFirstOpeningWaitsForTheModuleToWatchTheNote()
    {
        JSInterop.Mode = JSRuntimeMode.Strict;
        var module = JSInterop.SetupModule(DrawioHelp.ModulePath);
        var watch = module.SetupVoid("watchHelp", _ => true);
        module.SetupVoid("unwatchHelp", _ => true).SetVoidResult();

        var cut = RenderHelp();
        cut.Find("#x-help").Click();
        cut.WaitForAssertion(() => Assert.Single(module.Invocations["watchHelp"]));
        Assert.True(cut.Find("#x-help-body").HasAttribute("hidden"), "The note opened before the module watched it.");
        Assert.Equal("false", cut.Find("#x-help").GetAttribute("aria-expanded"));

        watch.SetVoidResult();
        cut.WaitForAssertion(() => Assert.False(cut.Find("#x-help-body").HasAttribute("hidden")));
        Assert.Equal("true", cut.Find("#x-help").GetAttribute("aria-expanded"));
    }

    /// <summary>Without the module - its fetch or its watch refused - the "?" still opens and shuts the note: only a click elsewhere leaves it open.</summary>
    [Fact]
    public void WithoutTheModuleTheQuestionMarkStillOpensAndShutsTheNote()
    {
        JSInterop.Mode = JSRuntimeMode.Strict;
        var module = JSInterop.SetupModule(DrawioHelp.ModulePath);
        module.SetupVoid("watchHelp", _ => true).SetException(new JSException("the module could not watch"));

        var cut = RenderHelp();
        cut.Find("#x-help").Click();
        cut.WaitForAssertion(() => Assert.False(cut.Find("#x-help-body").HasAttribute("hidden")));
        Assert.Equal("true", cut.Find("#x-help").GetAttribute("aria-expanded"));
        cut.Find("#x-help").Click();
        cut.WaitForAssertion(() => Assert.True(cut.Find("#x-help-body").HasAttribute("hidden")));
    }

    /// <summary>
    /// The module is fetched the first time the note opens, not at the first render: a page carries a
    /// "?" in every window and most of them are never opened. It is watched once, whatever the openings
    /// after, and let go when the component goes, so that nothing of it stays on an element that is gone:
    /// the element it is told to let go of is the one it was told to watch - the "?" with its note - and
    /// the component it was given to talk back to is this one.
    /// </summary>
    [Fact]
    public async Task TheModuleWatchesTheNoteFromItsFirstOpeningAndLetsGoOfItAtTheEnd()
    {
        JSInterop.Mode = JSRuntimeMode.Strict;
        var module = JSInterop.SetupModule(DrawioHelp.ModulePath);
        module.SetupVoid("watchHelp", _ => true).SetVoidResult();
        module.SetupVoid("unwatchHelp", _ => true).SetVoidResult();

        var cut = RenderHelp();
        Assert.Empty(JSInterop.Invocations);

        cut.Find("#x-help").Click();
        cut.WaitForAssertion(() => Assert.Single(module.Invocations["watchHelp"]));
        cut.Find("#x-help").Click();
        cut.Find("#x-help").Click();
        cut.WaitForAssertion(() => Assert.False(cut.Find("#x-help-body").HasAttribute("hidden")));
        var watch = Assert.Single(module.Invocations["watchHelp"]);
        var watchedElement = Assert.IsType<ElementReference>(watch.Arguments[0]);
        Assert.Equal(ReferenceIn(cut.Instance, "_element").Id, watchedElement.Id);
        Assert.Same(cut.Instance, Assert.IsType<DotNetObjectReference<DrawioHelp>>(watch.Arguments[1]).Value);

        await DisposeComponentsAsync();
        var unwatch = Assert.Single(module.Invocations["unwatchHelp"]);
        Assert.Equal(watchedElement.Id, Assert.IsType<ElementReference>(unwatch.Arguments[0]).Id);
    }

    /// <summary>
    /// The component can go while its first opening is still waiting for the module: the "?" beside
    /// the sentence of the cells that could not be read (row 29 of the queue) goes with every change
    /// of selection. The module that arrives afterwards is let go, and told to watch nothing. Before,
    /// the late module was kept, a reference to the gone component was made and the module told to
    /// watch an element that is gone - and neither reference was ever released, since the disposal
    /// had already run and found nothing to release.
    /// </summary>
    [Fact]
    public async Task AModuleThatArrivesAfterTheComponentWentIsLetGoAndWatchesNothing()
    {
        // bUnit's own interop answers an import at once (SetupModule) and refuses to plan one that
        // returns a module, so the import is held back by a runtime of the test's own.
        var runtime = new LateImport();
        Services.AddSingleton<IJSRuntime>(runtime);

        var cut = RenderHelp();
        cut.Find("#x-help").Click();
        Assert.Equal([DrawioHelp.ModulePath], runtime.Imported);

        await DisposeComponentsAsync();
        var late = new RecordingModule();
        runtime.Arrive(late);

        // Waited for until the component has done something with the module, whatever it is.
        await late.Touched.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(late.Calls);
        Assert.True(late.IsDisposed, "The module that arrived after the component went was not let go.");
    }

    /// <summary>A runtime that answers the module's import only when the test says so, and is asked nothing else.</summary>
    private sealed class LateImport : IJSRuntime
    {
        private readonly TaskCompletionSource<IJSObjectReference> _module = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string?> Imported { get; } = [];

        public void Arrive(IJSObjectReference module) => _module.SetResult(module);

        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (identifier != "import") throw new InvalidOperationException($"Only the module's import was expected, not {identifier}.");
            Imported.Add(args is { Length: > 0 } ? args[0] as string : null);
            return (TValue)await _module.Task;
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }

    /// <summary>
    /// A module of the test's own: what it was asked to do, whether it was let go, and
    /// <see cref="Touched"/> at the first of the two.
    /// </summary>
    private sealed class RecordingModule : IJSObjectReference
    {
        public List<string> Calls { get; } = [];

        public bool IsDisposed { get; private set; }

        public TaskCompletionSource Touched { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Calls.Add(identifier);
            Touched.TrySetResult();
            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            Touched.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
