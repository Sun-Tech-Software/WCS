using System.Linq;
using System.Net;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawio;
using Wcs.WebEditor.Pages;
using Wcs.WebEditor.Session;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// The new page (decision 3.9: it is born NEXT TO /editor, not in its place). Since task 5 it talks
/// to the host (<c>PlantGraph.LoadAsync</c>, the same bridge as <c>Editor.razor</c>): the key has
/// become a real precondition, not just an implementation detail, and the tests below reflect that -
/// see also <see cref="WithoutAKeyOnlyTheKeyFormIsReachable"/> and the list's comment in
/// <c>OnlyAllowlistedComponentsAreInteractiveTest</c>.
/// </summary>
public sealed class DrawioEditorPageTest : PlantCanvasBunitContext
{
    /// <summary>The name of the only device of these tests' plant: what the page must show when it is selected.</summary>
    private const string DeviceName = "LF02.0001.0001";

    private static PlantItemDto Item(Guid id, string name, double x, double y, double w, double h, double rot) =>
        new(id, name, DeviceTypes.Automation, null, null, null, default, default, 0, null, 0, 0, 0, 0, default!,
            x, y, w, h, rot);

    // Same shape as DrawioDocumentTest.Automation(): the minimal type that makes a device drawable
    // (DrawioDocument.IsDrawn only requires the type to be in the registry). No ports: these tests
    // do not touch junctions.
    private static DeviceTypeDto Automation() => new(
        "automation", 0, "Automation",
        [new ShapePartDto("M 4 4 H 96 V 96 H 4 Z", "#334155", Filled: false, Rigid: false)], [], new ShapePointDto(50, 50), new ShapePointDto(88, 60));

    private sealed class RoutingStubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    private static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) };

    /// <summary>
    /// The configuration the host hands over for a device of these tests' plant.
    /// <see cref="PlantItemConfigDto.DelayBlind"/> is not zero on purpose: it is an <c>int</c>, and
    /// a panel showing the DEFAULTS of a freshly built DTO instead of what the host said would be
    /// indistinguishable from a working one if every number in it were zero anyway.
    /// </summary>
    private static PlantItemConfigDto Config(Guid itemId, string name) => new()
    {
        ItemId = itemId,
        Name = name,
        Asi = name,
        DeviceType = (short)DeviceTypes.Automation,
        ConnectionType = (short)ConnectionTypes.Test,
        DelayBlind = ConfiguredDelayBlind,
    };

    /// <summary>The one value in <see cref="Config"/> that no default would produce: what proves the panel is showing the HOST's configuration.</summary>
    private const int ConfiguredDelayBlind = 4321;

    /// <summary>
    /// The answers of a host whose plant is made of these devices: the four GETs of
    /// <c>PlantGraph.LoadAsync</c> (items, junctions, device-types and - since queue item 21 - every
    /// configuration, for the load unit sizes), the two that task 7b added at opening, and - since
    /// batch B - the configuration of each device.
    /// </summary>
    /// <remarks>
    /// The decoration answers empty (the normal case of a plant that never had one), the live status
    /// has no device to show and the engine answers Stop: without them the page would declare
    /// "decoration not read" (or "live status not being updated") in tests that are not about
    /// either, leaving a warning none of these tests wants to measure.
    /// </remarks>
    private static Func<HttpRequestMessage, HttpResponseMessage> RespondWithPlant(params (Guid Id, string Name)[] devices)
    {
        var items = devices.Select((device, index) => Item(device.Id, device.Name, 100 + 300 * index, 200, 130, 184, 0)).ToList();
        return request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/plant/items")) return Json(items);
            if (path.EndsWith("/plant/junctions")) return Json(new List<JunctionDto>());
            if (path.EndsWith("/plant/device-types")) return Json(new List<DeviceTypeDto> { Automation() });
            if (path.EndsWith("/plant/items/config")) return Json(devices.Select(device => Config(device.Id, device.Name)).ToList());
            if (path.EndsWith("/plant/decoration")) return Json(new PlantDecorationDto(string.Empty));
            // The live status: every device Ready and empty. Before "/status", which it also ends
            // with: answered with the engine's status, the page's live feed would read a body it
            // cannot parse and say "interrupted" in tests that are not about the live status.
            if (path.EndsWith("/plant/items/status")) return Json(new List<ItemStatus>());
            if (path.EndsWith("/status"))
                return Json(new EngineStatus(NavigatorStatusTypes.Stop, DefragmentationTypes.None, "test", DateTime.UtcNow));
            foreach (var device in devices)
                if (path.EndsWith($"/plant/items/{device.Id}/config")) return Json(Config(device.Id, device.Name));
            // Everything else - and above all the configuration of a device this plant does not
            // have - is a 404, which is what the real host answers and what the client turns into a
            // null (GetOrNullAsync).
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
    }

    /// <summary>
    /// A handler for a plant of one drawable device only - enough to prove that the real graph, read
    /// from the host, reaches draw.io.
    /// </summary>
    private static RoutingStubHandler HandlerWithOneDevice(Guid itemId) =>
        new(RespondWithPlant((itemId, DeviceName)));

    private IRenderedComponent<DrawioEditor> RenderAuthenticated(Guid itemId) =>
        RenderAuthenticated(HandlerWithOneDevice(itemId));

    private IRenderedComponent<DrawioEditor> RenderAuthenticated(HttpMessageHandler handler)
    {
        var state = new EditorSessionState(new Uri("http://localhost:5580"), handler);
        state.UseKey("test-key");
        Services.AddSingleton(state);
        return Render<DrawioEditor>();
    }

    /// <summary>
    /// draw.io's address is no longer read from the iframe's <c>src</c> attribute, and not because
    /// the test got more convenient that way: the iframe <b>is born without src</b>, and what starts
    /// it is <c>attach</c>, which first subscribes to the messages. The reason is measured and is in
    /// the comment next to the iframe in DrawioEditor.razor. The guarantee is the same as before -
    /// it is the place it is read from that changed - plus the two entries task 6 added.
    /// </summary>
    [Fact]
    public void TheBridgeIsToldWhichSelfHostedDrawioToStartOnceAuthenticated()
    {
        var cut = RenderAuthenticated(Guid.NewGuid());

        Assert.Empty(cut.Find("iframe#drawio-frame").GetAttribute("src") ?? string.Empty);

        // The bridge's attach, the one with the address: the floating windows (DrawioWindow) attach
        // their own module too, with a place as the third argument.
        var attach = Assert.Single(JSInterop.Invocations, i => i.Identifier == "attach" && i.Arguments.Count >= 3 && i.Arguments[2] is string);
        var src = Assert.IsType<string>(attach.Arguments[2]);

        Assert.StartsWith("_content/Wcs.WebEditor/drawio/index.html", src);
        Assert.Contains("embed=1", src);
        Assert.Contains("proto=json", src);
        // The links stay ON: it is the difference from KazamWeb's plant editor, where connect=0
        // turned them off, and it is the case this project has to support.
        Assert.DoesNotContain("connect=0", src);

        // The two entries of task 6: draw.io waits for the configuration before starting, and the
        // device section is among those SHOWN in the panel. Without the second one the section
        // exists and is not visible, which is how this task can fail while staying green.
        Assert.Contains("configure=1", src);
        Assert.Contains("libs=" + DrawioLibrary.SectionId, src);
    }

    /// <summary>
    /// The device section reaches draw.io, and it is generated from the registry the host declared -
    /// not from a list written in the page. bUnit does not run JavaScript: what is tested here is
    /// that the page SENDS it and what is inside; that the panel really shows it is tested by
    /// <c>DrawioShapesPanelTest</c>, in a real browser.
    /// </summary>
    [Fact]
    public void ThePageSendsTheDeviceSectionBuiltFromTheRegistry()
    {
        var cut = RenderAuthenticated(Guid.NewGuid());

        var configure = Assert.Single(JSInterop.Invocations, i => i.Identifier == "configure");
        var json = Assert.IsType<string>(configure.Arguments[0]);

        // Only one type in this test's registry (see HandlerWithOneDevice): a single entry.
        Assert.Equal(DrawioLibrary.Build([Automation()]), json);
        Assert.Contains(DrawioLibrary.SectionTitle, json);
    }

    /// <summary>
    /// The local test that <c>OnlyAllowlistedComponentsAreInteractiveTest</c> asks of every
    /// interactive component that talks to the host (see the comment of its list): since this task
    /// <c>DrawioEditor</c> is no longer harmless as it was in task 1 (no @inject, nothing to deny) -
    /// it loads the real plant, so it must deny it to whoever has no key exactly like
    /// <c>Editor.razor</c> (<see cref="WithoutAKeyNothingIsReachableTest"/>). Different from task 1's
    /// <c>RendersTheSameShellRegardlessOfSessionKeyState</c> - that test proved the opposite, and it
    /// was true back then: this one is its replacement, not an addition alongside it.
    /// </summary>
    [Fact]
    public void WithoutAKeyOnlyTheKeyFormIsReachable()
    {
        var state = new EditorSessionState(new Uri("http://localhost:5580"));
        Services.AddSingleton(state);
        Assert.False(state.HasKey);

        var cut = Render<DrawioEditor>();

        Assert.NotNull(cut.Find("input[type=password]"));
        Assert.Empty(cut.FindAll("iframe#drawio-frame"));
    }

    /// <summary>
    /// bUnit does not run JavaScript: what is tested is that the page ASKS for the load with the
    /// right XML, not that the iframe receives it. The test that the iframe really receives it is in
    /// task 9, in a real browser.
    /// </summary>
    [Fact]
    public async Task WhenDrawioIsReadyThePageSendsThePlantXml()
    {
        var itemId = Guid.NewGuid();
        var cut = RenderAuthenticated(itemId);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());

        var chiamata = Assert.Single(JSInterop.Invocations, i => i.Identifier == "load");
        var xml = Assert.IsType<string>(chiamata.Arguments[1]);
        Assert.Contains("wcs=\"device\"", xml);
        Assert.Contains(itemId.ToString(), xml);
    }

    /// <summary>
    /// Before receiving 'init' from draw.io, <c>OnDrawioReady</c> sends nothing if the graph has not
    /// arrived yet - the branch described in its own comment in DrawioEditor.razor. It is measured
    /// here by calling it on a component just rendered, before the wait of
    /// <c>PlantGraph.LoadAsync</c> has finished: no "load" invocation must show up.
    /// </summary>
    [Fact]
    public async Task ReadyBeforeTheGraphHasLoadedSendsNothing()
    {
        // A handler that never answers (no TaskCompletionSource completed): the LoadAsync inside
        // OnInitializedAsync stays suspended, exactly like a slow host in real life.
        var pending = new TaskCompletionSource<HttpResponseMessage>();
        var handler = new RoutingStubHandlerAsync(_ => pending.Task);
        var state = new EditorSessionState(new Uri("http://localhost:5580"), handler);
        state.UseKey("test-key");
        Services.AddSingleton(state);

        var cut = Render<DrawioEditor>();
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());

        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "load");
    }

    private sealed class RoutingStubHandlerAsync(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
    }

    /// <summary>
    /// bUnit does not let you configure "import" to throw through <c>JSInterop.Setup&lt;T&gt;</c>
    /// when <c>T</c> is an <see cref="IJSObjectReference"/> (it refuses with
    /// <c>ArgumentException</c>, "Use one of the SetupModule() methods instead" - measured, not
    /// deduced: <c>SetupModule</c>, though, only configures an import that SUCCEEDS, none of its
    /// overloads makes the import itself fail). The only public way left is to go one level down:
    /// <see cref="Bunit.JSRuntimeInvocationHandler{T}"/> has a protected constructor (reachable by
    /// inheritance from this assembly too) and a public <c>SetException</c> - this minimal subclass
    /// exposes it, and <c>JSInterop.AddInvocationHandler</c> (public as well) registers it, stepping
    /// over the guard of <c>Setup&lt;T&gt;</c>.
    /// </summary>
    private sealed class ImportFailureHandler : Bunit.JSRuntimeInvocationHandler<IJSObjectReference>
    {
        public ImportFailureHandler(Bunit.InvocationMatcher matcher) : base(matcher, isCatchAllHandler: false) { }
    }

    /// <summary>
    /// The real path of the bridge module, repeated here because <c>DrawioEditor.BridgeModulePath</c>
    /// is private: used to narrow <see cref="ImportFailureHandler"/> down to this one import,
    /// instead of intercepting whatever <c>import()</c> the page makes (a minor finding of task 5's
    /// re-review - today there is only one and it does no harm, but a second module imported
    /// tomorrow would fall silently into the same trap without this narrowing).
    /// </summary>
    private const string BridgeModulePath = "./_content/Wcs.WebEditor/drawio-bridge.js";

    private static bool MatchesBridgeImport(Bunit.JSRuntimeInvocation inv) =>
        inv.Identifier == "import" && inv.Arguments is [string path] && path == BridgeModulePath;

    /// <summary>
    /// The real case of the critical finding (task 5 review): in a real browser an <c>import()</c>
    /// that does not resolve THROWS, it does not return null - see the comment of
    /// <c>DrawioEditor.OnAfterRenderAsync</c>. <see cref="ImportFailureHandler"/> is how this test
    /// makes it really happen: it is measured that without it (no configuration at all) the import
    /// in Loose mode always returns an object, never an exception - the branch this test wants to
    /// exercise would not show up.
    /// </summary>
    [Fact]
    public void WhenTheBridgeModuleFailsToImportTheBridgeIsDeclaredUnavailable()
    {
        var handler = new ImportFailureHandler(MatchesBridgeImport);
        handler.SetException(new JSException("module not found (test)"));
        JSInterop.AddInvocationHandler(handler);

        var cut = RenderAuthenticated(Guid.NewGuid());

        var banner = cut.Find("p.drawio-bridge-error");
        Assert.Contains("did not load", banner.TextContent);
        Assert.DoesNotContain("did not answer", banner.TextContent);
    }

    /// <summary>
    /// The timeout case (task 5's re-review): neither <c>JSException</c> nor
    /// <c>JSDisconnectedException</c> covers the cancellation on timeout of
    /// <c>CircuitOptions.JSInteropDefaultCallTimeout</c> - see the comment on
    /// <c>DrawioEditor.OnAfterRenderAsync</c>. Same technique as the two tests above and below
    /// (<see cref="ImportFailureHandler"/> for the first catch, which here throws
    /// <see cref="TaskCanceledException"/> instead of <see cref="JSException"/>), to measure that
    /// the message shown is the third one, not one of the two already tested.
    /// </summary>
    [Fact]
    public void WhenTheBridgeModuleImportTimesOutTheBridgeIsDeclaredUnavailable()
    {
        var handler = new ImportFailureHandler(MatchesBridgeImport);
        handler.SetException(new TaskCanceledException("call cancelled on timeout (test)"));
        JSInterop.AddInvocationHandler(handler);

        var cut = RenderAuthenticated(Guid.NewGuid());

        var banner = cut.Find("p.drawio-bridge-error");
        Assert.Contains("did not answer in time", banner.TextContent);
        Assert.DoesNotContain("did not load", banner.TextContent);
        Assert.DoesNotContain("loaded but did not answer", banner.TextContent);
    }

    /// <summary>
    /// The same timeout, but on the <c>attach</c> call instead of on <c>import</c> - the second
    /// catch widened in task 5's re-review. Same technique as
    /// <see cref="WhenAttachFailsTheBridgeIsDeclaredUnavailable"/> (a module that imports without
    /// trouble, only <c>attach</c> configured to throw), just with
    /// <see cref="TaskCanceledException"/> in place of <see cref="JSDisconnectedException"/>.
    /// </summary>
    [Fact]
    public void WhenAttachTimesOutTheBridgeIsDeclaredUnavailable()
    {
        var module = JSInterop.SetupModule(BridgeModulePath);
        module.SetupVoid("attach", _ => true)
            .SetException(new TaskCanceledException("call cancelled on timeout (test)"));

        var cut = RenderAuthenticated(Guid.NewGuid());

        var banner = cut.Find("p.drawio-bridge-error");
        Assert.Contains("did not answer in time", banner.TextContent);
        Assert.DoesNotContain("did not load", banner.TextContent);
        Assert.DoesNotContain("loaded but did not answer", banner.TextContent);
    }

    /// <summary>
    /// The other real case of the critical finding: <c>attach</c> called on a closing circuit throws
    /// <see cref="JSDisconnectedException"/>, it does not return silently. The module imports
    /// without trouble (<c>SetupModule</c>, as the base class does for the keyboard - see
    /// <c>PlantCanvasBunitContext.SetupTypingTargetToThrow</c> for the same pattern): only the
    /// <c>attach</c> call is configured to throw, so the test tells this case apart from the
    /// previous one by the banner's text.
    /// </summary>
    [Fact]
    public void WhenAttachFailsTheBridgeIsDeclaredUnavailable()
    {
        var module = JSInterop.SetupModule("./_content/Wcs.WebEditor/drawio-bridge.js");
        module.SetupVoid("attach", _ => true)
            .SetException(new JSDisconnectedException("circuit closing (test)"));

        var cut = RenderAuthenticated(Guid.NewGuid());

        var banner = cut.Find("p.drawio-bridge-error");
        Assert.Contains("did not answer", banner.TextContent);
        Assert.DoesNotContain("draw.io did not load", banner.TextContent);
    }

    /// <summary>
    /// The bridge's fourth failure, and it is the one task 6 introduced: the configuration does not
    /// reach draw.io. Since draw.io starts with <c>configure=1</c>, a lost <c>configure</c> is not
    /// an empty canvas - draw.io <b>does not start at all</b> and sits staring at nothing without
    /// saying a word. It is the only one of the four messages that had no test of its own.
    /// <para>
    /// Same technique as the tests above: the module imports and <c>attach</c> succeeds, only
    /// <c>configure</c> is configured to throw, so what is measured is that the banner carries THIS
    /// message and not one of the other three - which is the only thing that tells whoever reads it
    /// which of the four steps broke.
    /// </para>
    /// </summary>
    [Fact]
    public void WhenTheLibraryCannotBeSentTheBridgeIsDeclaredUnavailable()
    {
        var module = JSInterop.SetupModule(BridgeModulePath);
        module.SetupVoid("attach", _ => true).SetVoidResult();
        module.SetupVoid("configure", _ => true)
            .SetException(new JSException("configure not delivered (test)"));

        var cut = RenderAuthenticated(Guid.NewGuid());

        var banner = cut.Find("p.drawio-bridge-error");
        Assert.Contains("The configuration never reached", banner.TextContent);
        // Not one of the other three: the bridge here loaded, answered and made it in time.
        Assert.DoesNotContain("The bridge to draw.io", banner.TextContent);
        Assert.DoesNotContain("The plant drawing", banner.TextContent);
    }

    /// <summary>
    /// The normal path, by contrast with the two tests above: import and attach configured
    /// explicitly to succeed (not the base class's loose fallback, to be explicit about the
    /// precondition - the same reason as the comment on <c>SetupTypingTarget</c> in the base class)
    /// must never show the banner.
    /// </summary>
    [Fact]
    public void WhenTheBridgeLoadsAndAttachesNormallyNoBannerAppears()
    {
        var module = JSInterop.SetupModule("./_content/Wcs.WebEditor/drawio-bridge.js");
        module.SetupVoid("attach", _ => true).SetVoidResult();

        var cut = RenderAuthenticated(Guid.NewGuid());

        Assert.Empty(cut.FindAll("p.drawio-bridge-error"));
    }

    /// <summary>
    /// Row 33: the plain default, with nobody having configured "loadRulerPreference" - the loose
    /// module the base class's JSRuntimeMode.Loose hands back for anything not set up explicitly,
    /// which is what a browser with no memory yet (or one that refuses localStorage) looks like from
    /// the page's side. The switch must show off, and OnDrawioReady - draw.io's own 'init' - still
    /// calls setRulerVisible, with false: turning rulers off IS a call and not the absence of one
    /// (fix round 1) - draw.io can arrive already showing rulers of its OWN accord, from its own
    /// shared mxSettings memory, whatever our own remembers, and only a call can put that right.
    /// </summary>
    [Fact]
    public async Task WithNothingRememberedTheSwitchStartsOffAndDrawioIsToldOffAtReady()
    {
        var module = JSInterop.SetupModule(BridgeModulePath);
        module.SetupVoid("attach", _ => true).SetVoidResult();
        module.SetupVoid("setRulerVisible", _ => true).SetVoidResult();

        var cut = RenderAuthenticated(Guid.NewGuid());

        Assert.False(cut.Find("#drawio-ruler-switch").HasAttribute("checked"));

        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());

        var call = Assert.Single(JSInterop.Invocations, i => i.Identifier == "setRulerVisible");
        Assert.Equal(false, call.Arguments[0]);
    }

    /// <summary>
    /// Row 33's own rule, spelt out for the switch: "if at startup the memory says on, they turn on
    /// as soon as draw.io is ready". The switch shows the remembered "on" straight away - read during
    /// <c>OnAfterRenderAsync</c>, before draw.io even exists - and the call to draw.io itself is made
    /// only once <c>OnDrawioReady</c> runs, which here is triggered by hand the same way
    /// <see cref="WhenDrawioIsReadyThePageSendsThePlantXml"/> does: bUnit has no real draw.io to post
    /// the 'init' message.
    /// </summary>
    [Fact]
    public async Task RulersTurnOnAtStartupWhenTheMemorySaysOn()
    {
        var module = JSInterop.SetupModule(BridgeModulePath);
        module.SetupVoid("attach", _ => true).SetVoidResult();
        module.Setup<bool>("loadRulerPreference").SetResult(true);
        module.SetupVoid("setRulerVisible", _ => true).SetVoidResult();

        var cut = RenderAuthenticated(Guid.NewGuid());

        Assert.True(cut.Find("#drawio-ruler-switch").HasAttribute("checked"));
        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "setRulerVisible");

        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());

        var call = Assert.Single(JSInterop.Invocations, i => i.Identifier == "setRulerVisible");
        Assert.Equal(true, call.Arguments[0]);
    }

    /// <summary>
    /// THE RACE this cheap fix closes: <c>OnDrawioReady</c>'s own reconciliation is not ordered
    /// against <c>loadRulerPreference</c>'s reply - draw.io's 'init' can arrive first, which here is
    /// simulated the same way as the two tests above (<c>OnDrawioReady</c> triggered by hand). When
    /// it does, <c>OnDrawioReady</c> reconciles with the <c>false</c> the field still holds (nothing
    /// loaded yet) - if the memory's late "on" answer only updated the field and never called the
    /// canvas again, the switch would show on while the rulers stayed off. It must call again.
    /// </summary>
    [Fact]
    public async Task RulersReconcileAgainWhenTheMemoryAnswersAfterDrawioIsAlreadyReady()
    {
        var module = JSInterop.SetupModule(BridgeModulePath);
        module.SetupVoid("attach", _ => true).SetVoidResult();
        var preference = module.Setup<bool>("loadRulerPreference");
        module.SetupVoid("setRulerVisible", _ => true).SetVoidResult();

        var cut = RenderAuthenticated(Guid.NewGuid());

        // draw.io's 'init' arrives before the memory has answered.
        await cut.InvokeAsync(() => cut.Instance.OnDrawioReady());
        var first = Assert.Single(JSInterop.Invocations, i => i.Identifier == "setRulerVisible");
        Assert.Equal(false, first.Arguments[0]);

        // The memory answers "on", late. Waited for and not asserted right away: the continuation
        // that resumes LoadRulerPreferenceAsync runs through the renderer's own dispatch and is not
        // guaranteed to have completed by the time SetResult's call returns.
        preference.SetResult(true);

        cut.WaitForAssertion(() =>
        {
            Assert.True(cut.Find("#drawio-ruler-switch").HasAttribute("checked"));
            var calls = JSInterop.Invocations.Where(i => i.Identifier == "setRulerVisible").ToList();
            Assert.Equal(2, calls.Count);
            Assert.Equal(true, calls[^1].Arguments[0]);
        });
    }

    /// <summary>
    /// The operator's own gesture: a click tells draw.io AND the browser's memory, both - the switch
    /// is not only a visual toggle, and the round trip to draw.io failing must not stop it from being
    /// remembered for the next reload (the two calls are independent in <c>OnRulerToggleAsync</c>).
    /// </summary>
    [Fact]
    public async Task TurningTheSwitchOnCallsDrawioAndRemembersTheChoice()
    {
        var module = JSInterop.SetupModule(BridgeModulePath);
        module.SetupVoid("attach", _ => true).SetVoidResult();
        module.SetupVoid("setRulerVisible", _ => true).SetVoidResult();
        module.SetupVoid("saveRulerPreference", _ => true).SetVoidResult();

        var cut = RenderAuthenticated(Guid.NewGuid());

        await cut.InvokeAsync(() => cut.Find("#drawio-ruler-switch").Change(true));

        var toggled = Assert.Single(JSInterop.Invocations, i => i.Identifier == "setRulerVisible");
        Assert.Equal(true, toggled.Arguments[0]);
        var saved = Assert.Single(JSInterop.Invocations, i => i.Identifier == "saveRulerPreference");
        Assert.Equal(true, saved.Arguments[0]);
        Assert.True(cut.Find("#drawio-ruler-switch").HasAttribute("checked"));
    }

    /// <summary>
    /// The batch A question: with a device selected inside draw.io, does the PAGE name it? bUnit
    /// runs no JavaScript, so the bridge's call is made here the way the bridge makes it - straight
    /// on the instance, like <see cref="WhenDrawioIsReadyThePageSendsThePlantXml"/> above - and what
    /// is measured is the line the operator reads.
    /// </summary>
    /// <remarks>
    /// The guid is asserted ABSENT and not only the name present: the identifier is what travels,
    /// and a line showing it would technically be "the selection reached the page" while being
    /// useless to whoever is drawing.
    /// </remarks>
    [Fact]
    public async Task ASelectedDeviceIsNamedOnThePage()
    {
        var itemId = Guid.NewGuid();
        var cut = RenderAuthenticated(itemId);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([itemId.ToString()]));

        var line = cut.Find("#drawio-selection");
        Assert.Contains(DeviceName, line.TextContent);
        Assert.DoesNotContain(itemId.ToString(), line.TextContent);
    }

    /// <summary>
    /// The defect this batch exists not to produce: the selection empties - the operator clicked on
    /// the empty canvas - and the line goes on naming the device that is no longer selected.
    /// </summary>
    [Fact]
    public async Task AnEmptiedSelectionDoesNotLeaveTheLastNameOnScreen()
    {
        var itemId = Guid.NewGuid();
        var cut = RenderAuthenticated(itemId);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([itemId.ToString()]));
        Assert.Contains(DeviceName, cut.Find("#drawio-selection").TextContent);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([]));

        var line = cut.Find("#drawio-selection");
        Assert.DoesNotContain(DeviceName, line.TextContent);
        Assert.Contains("Nothing selected", line.TextContent);
    }

    /// <summary>
    /// The same defect through the other door: what is selected is not a device of the plant at all
    /// - a shape the operator drew, whose identifier mxGraph made up, or a device dragged out of our
    /// own section and not yet saved, which carries the mark but no guid. The previous name must not
    /// survive that either.
    /// </summary>
    [Theory]
    [InlineData("7")]                                       // mxGraph's own numbering: not a guid
    [InlineData("11111111-2222-3333-4444-555555555555")]    // a guid, but of nothing on this plant
    public async Task ACellThatIsNotAPlantDeviceIsSaidToBeOne(string foreignId)
    {
        var itemId = Guid.NewGuid();
        var cut = RenderAuthenticated(itemId);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([itemId.ToString()]));
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([foreignId]));

        var line = cut.Find("#drawio-selection");
        Assert.DoesNotContain(DeviceName, line.TextContent);
        Assert.Contains("not a device of the plant", line.TextContent);
    }

    /// <summary>
    /// Several cells selected: the line says how many, and how many of them the plant knows, instead
    /// of naming the first one - which would read exactly like "one device is selected" and would be
    /// false.
    /// </summary>
    [Fact]
    public async Task SeveralSelectedCellsAreCountedInsteadOfPickingOne()
    {
        var itemId = Guid.NewGuid();
        var cut = RenderAuthenticated(itemId);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([itemId.ToString(), "7"]));

        var line = cut.Find("#drawio-selection");
        Assert.DoesNotContain(DeviceName, line.TextContent);
        Assert.Contains("2 cells", line.TextContent);
        Assert.Contains("1 device of the plant", line.TextContent);
    }

    /// <summary>
    /// Before draw.io has said anything there is NO line: not an empty one, and above all not
    /// "nothing is selected" - which at that moment would be a claim nobody has measured, and which
    /// would be indistinguishable from a subscription that never happened.
    /// </summary>
    [Fact]
    public void BeforeDrawioSaysAnythingThereIsNoSelectionLine()
    {
        var cut = RenderAuthenticated(Guid.NewGuid());

        Assert.Empty(cut.FindAll("#drawio-selection"));
    }

    /// <summary>
    /// The bridge could not subscribe: it is SAID (defect D200 - a failure is declared), and it is
    /// said on the selection's own line. The drawing is NOT covered by the bridge's error paragraph,
    /// and that is the decision this test pins down: without the selection the Editor still draws
    /// and still saves, so hiding it would cost more than the failure. See
    /// <c>SelectionUnavailableMessage</c> in DrawioEditor.razor.
    /// </summary>
    [Fact]
    public async Task WhenTheSelectionCannotBeFollowedItIsSaidWithoutHidingTheDrawing()
    {
        var itemId = Guid.NewGuid();
        var cut = RenderAuthenticated(itemId);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([itemId.ToString()]));
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionUnavailable());

        var line = cut.Find("#drawio-selection");
        Assert.Contains("cannot follow the selection", line.TextContent);
        // And the last name heard does not stay next to the declaration of the failure.
        Assert.DoesNotContain(DeviceName, line.TextContent);

        Assert.Empty(cut.FindAll("p.drawio-bridge-error"));
        Assert.NotNull(cut.Find("iframe#drawio-frame"));
    }

    /// <summary>
    /// A subscription that answers is the proof that it works: a failure declared before must not
    /// stay on screen next to a line that is naming devices.
    /// </summary>
    [Fact]
    public async Task ASelectionArrivingAfterAFailureClearsTheDeclaredFailure()
    {
        var itemId = Guid.NewGuid();
        var cut = RenderAuthenticated(itemId);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionUnavailable());
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([itemId.ToString()]));

        var line = cut.Find("#drawio-selection");
        Assert.Contains(DeviceName, line.TextContent);
        Assert.DoesNotContain("cannot follow the selection", line.TextContent);
    }

    /// <summary>
    /// Queue item 15: draw.io selected more cells than one message of the circuit can name, and the
    /// bridge sent their count instead. The line says how many and that the panel shows none of them -
    /// not as a failure, the subscription works - and the panel lets go of the device it was about.
    /// The next selection that can be named is followed again.
    /// </summary>
    [Fact]
    public async Task ASelectionTooLargeToFollowIsSaidOnItsLineAndThePanelLetsGoOfTheDevice()
    {
        var itemId = Guid.NewGuid();
        var cut = RenderAuthenticated(itemId);
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([itemId.ToString()]));
        Assert.Single(cut.FindAll("#drawio-property-panel"));

        await cut.InvokeAsync(() => cut.Instance.OnDrawioTooManyCellsSelected(9000));

        var line = cut.Find("#drawio-selection");
        Assert.Equal("Selected: 9000 cells, too many to follow: the panel shows none of them.", line.TextContent);
        Assert.DoesNotContain("drawio-selection-error", line.ClassList);
        Assert.Empty(cut.FindAll("#drawio-property-panel"));

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([itemId.ToString()]));

        Assert.Equal($"Selected: {DeviceName}", cut.Find("#drawio-selection").TextContent);
        Assert.Single(cut.FindAll("#drawio-property-panel"));
    }

    // ---- Batch B: the properties panel, read only -------------------------------------------

    /// <summary>The second device of the two-device plant the race test needs.</summary>
    private const string OtherDeviceName = "LF02.0001.0002";

    private static string? ValueOf(IRenderedComponent<DrawioEditor> cut, string propertyName) =>
        cut.Find($"#drawio-property-panel div.property-panel-field[data-property='{propertyName}'] input").GetAttribute("value");

    /// <summary>
    /// The batch B question: with a device selected inside draw.io, does the page show <b>its
    /// configuration</b>? And is what it shows really the host's, not the defaults of a freshly
    /// built DTO - which is why <c>DelayBlind</c> is asserted (see <see cref="Config"/>) and not only
    /// the name, that a page could have taken from the plant it already holds without reading
    /// anything.
    /// </summary>
    [Fact]
    public async Task ASelectedDeviceShowsItsConfigurationInTheDockedPanel()
    {
        var itemId = Guid.NewGuid();
        var cut = RenderAuthenticated(itemId);

        // Before any click there is no panel at all: not an empty one, which would read as "this
        // plant has nothing to configure".
        Assert.Empty(cut.FindAll("#drawio-property-panel"));

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([itemId.ToString()]));

        Assert.Single(cut.FindAll("#drawio-property-dock"));
        Assert.Single(cut.FindAll("#drawio-property-panel"));
        Assert.Equal(DeviceName, ValueOf(cut, nameof(PlantItemConfigDto.Name)));
        Assert.Equal(ConfiguredDelayBlind.ToString(), ValueOf(cut, nameof(PlantItemConfigDto.DelayBlind)));
        Assert.Equal(
            itemId.ToString(),
            cut.Find($"#drawio-property-panel div.property-panel-field[data-property='{nameof(PlantItemConfigDto.ItemId)}'] span").TextContent);

        // The sixty properties arrive grouped as the contract declares them, because the very
        // component /editor docks is doing the work: several categories, not one flat list.
        var categories = cut.FindAll("#drawio-property-panel fieldset.property-panel-group")
            .Select(group => group.GetAttribute("data-category")).ToList();
        Assert.Contains("Basic Option", categories);
        Assert.Contains("Device Option", categories);
        Assert.True(
            categories.Count > 2,
            $"One category or two: the panel is not showing the contract ({string.Join(", ", categories)}).");

        // And nothing is still on its way: the "reading..." line is gone once the values are there.
        Assert.Empty(cut.FindAll("#drawio-property-loading"));
    }

    /// <summary>
    /// Batch C turns the panel round: the fields are live and the permit is asked for at the
    /// confirmation, not when the panel opens. Both halves are asserted - the note in words and the
    /// locks in the DOM - because either one without the other is its own kind of lie: a note
    /// promising an editable panel over dead fields, or sixty live fields with nothing saying when
    /// the permit is taken.
    /// </summary>
    /// <remarks>
    /// The fields are measured one by one, not deduced from the <c>CanEdit</c> handed to the
    /// component: they are sixty of five different shapes, and a single one left dead would be a
    /// property this page cannot change while claiming it can.
    /// </remarks>
    [Fact]
    public async Task ThePanelCanBeChangedAndSaysWhenThePermitIsAskedFor()
    {
        var itemId = Guid.NewGuid();
        var cut = RenderAuthenticated(itemId);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([itemId.ToString()]));

        var note = cut.Find("#drawio-property-subject").TextContent;
        // It names its subject: a note about "the configuration" with no device in it would leave
        // the operator wondering which one is on screen.
        Assert.Contains(DeviceName, note);
        Assert.Contains("\"Save\"", note);
        // And the promise the refusal path has to keep: see the write tests.
        Assert.Contains("stays here", note);

        Assert.False(cut.Find("#property-panel-save-button").HasAttribute("disabled"));

        // ANCHORED, and not out of zeal: Assert.All over an empty list passes without looking at
        // anything, so a selector that stopped matching - a renamed class, a panel that failed to
        // render its fields - would turn this into a test that can no longer fail. The numbers are
        // the contract's: sixty-odd properties, of which the enumerations and the flags are drawn as
        // <select> and everything else as <input>.
        var inputs = cut.FindAll("#drawio-property-panel input");
        var selects = cut.FindAll("#drawio-property-panel select");
        Assert.True(
            inputs.Count > 30 && selects.Count > 5,
            $"The panel is not showing the contract's properties: {inputs.Count} input, {selects.Count} select.");

        Assert.All(inputs, input => Assert.False(input.HasAttribute("disabled")));
        Assert.All(selects, select => Assert.False(select.HasAttribute("disabled")));
    }

    /// <summary>
    /// The read that batch B did on every click of the SAME device is not done any more, and it is a
    /// decision of batch C: with fields that can be typed into, a fresh configuration from the host
    /// hands <c>PropertyPanel</c> a new <c>Config</c> object, which resyncs its working copy and
    /// throws away everything typed and not yet confirmed. draw.io reports a selection change on
    /// every mousedown, the one on the already selected device included, so that is a click away at
    /// any moment.
    /// </summary>
    /// <remarks>
    /// Measured on the HTTP calls - how many configuration reads left the page - and on the field,
    /// which still carries what was typed. Counting alone would not do: a page that read again and
    /// somehow kept the typing would be right, and one that skipped the read and emptied the panel
    /// for another reason would be wrong.
    /// </remarks>
    [Fact]
    public async Task ClickingTheSameDeviceAgainDoesNotThrowAwayWhatWasTypedIntoThePanel()
    {
        var itemId = Guid.NewGuid();
        var plant = RespondWithPlant((itemId, DeviceName));
        var configReads = 0;
        var cut = RenderAuthenticated(new RoutingStubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith($"/{itemId}/config")) configReads++;
            return plant(request);
        }));

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([itemId.ToString()]));
        Assert.Equal(1, configReads);

        cut.Find($"#drawio-property-panel div.property-panel-field[data-property='{nameof(PlantItemConfigDto.Name)}'] input")
            .Change("typed-and-not-confirmed");

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([itemId.ToString()]));

        Assert.Equal(1, configReads);
        Assert.Equal("typed-and-not-confirmed", ValueOf(cut, nameof(PlantItemConfigDto.Name)));
    }

    /// <summary>
    /// The defect this batch exists not to produce, in its sixty-field version: the operator clicks
    /// the empty canvas and the panel goes on showing the properties of the device that is no longer
    /// selected.
    /// </summary>
    [Fact]
    public async Task AnEmptiedSelectionTakesThePanelAway()
    {
        var itemId = Guid.NewGuid();
        var cut = RenderAuthenticated(itemId);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([itemId.ToString()]));
        Assert.NotNull(cut.Find("#drawio-property-panel"));

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([]));

        Assert.Empty(cut.FindAll("#drawio-property-panel"));
        // And the line of batch A goes on telling the truth next to the missing panel.
        Assert.Contains("Nothing selected", cut.Find("#drawio-selection").TextContent);
    }

    /// <summary>
    /// Several cells selected: the panel shows one device at a time, and picking one of them would
    /// teach the operator to trust a panel that guesses. No panel, and the line above says how many.
    /// </summary>
    [Fact]
    public async Task SeveralSelectedCellsShowNoPanelAtAll()
    {
        var itemId = Guid.NewGuid();
        var cut = RenderAuthenticated(itemId);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([itemId.ToString()]));
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([itemId.ToString(), "7"]));

        Assert.Empty(cut.FindAll("#drawio-property-panel"));
        Assert.Contains("2 cells", cut.Find("#drawio-selection").TextContent);
    }

    /// <summary>
    /// The same through the other door: one cell, but not a device of the plant - a shape the
    /// operator drew, or one dragged out of our own section and not saved yet. There is nothing to
    /// read from the host, and the previous device's properties must not survive it.
    /// </summary>
    [Theory]
    [InlineData("7")]                                       // mxGraph's own numbering: not a guid
    [InlineData("11111111-2222-3333-4444-555555555555")]    // a guid, but of nothing on this plant
    public async Task ACellThatIsNotAPlantDeviceShowsNoPanel(string foreignId)
    {
        var itemId = Guid.NewGuid();
        var cut = RenderAuthenticated(itemId);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([itemId.ToString()]));
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([foreignId]));

        Assert.Empty(cut.FindAll("#drawio-property-panel"));
    }

    /// <summary>
    /// The read can fail - host down, a 500, a request cancelled at the client's thirty second
    /// ceiling. The panel neither stays blank nor keeps the values of before: it says so, like
    /// everything else on this page (defect D200). Row 37 of the queue: in one sentence, with the why
    /// in the page's words, and the host's answer - its problem document, trace id and all - behind
    /// the "?" next to it, where the sentence used to carry it in the middle.
    /// </summary>
    [Fact]
    public async Task WhenTheConfigurationCannotBeReadItIsSaidInsteadOfAnEmptyPanel()
    {
        const string answer =
            "{\"type\":\"https://tools.ietf.org/html/rfc9110#section-15.6.1\",\"title\":\"An error occurred while processing your request.\"," +
            "\"status\":500,\"traceId\":\"00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-00\"}";
        var itemId = Guid.NewGuid();
        var plant = RespondWithPlant((itemId, DeviceName));
        var cut = RenderAuthenticated(new RoutingStubHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith($"/{itemId}/config")
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent(answer) }
                : plant(request)));

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([itemId.ToString()]));

        Assert.Equal(
            "The configuration of this device could not be read because the host answered with an error.",
            cut.Find("#drawio-property-error").TextContent);
        Assert.Equal("the host answered 500: " + answer, DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-property-error"));
        // No fields, and above all no "reading..." left hanging: the read is over, it just went
        // badly.
        Assert.Empty(cut.FindAll("#drawio-property-panel fieldset.property-panel-group"));
        Assert.Empty(cut.FindAll("#drawio-property-loading"));
    }

    /// <summary>
    /// Final review, C11: a failure of the read that is none of those the page knows - here the
    /// request's own handler throws something no client would - still ends in the line, with the why
    /// row 37 gave it in words and the failure itself behind the "?".
    /// </summary>
    [Fact]
    public async Task AnUnforeseenFailureOfTheConfigurationsReadIsSaidAsThat()
    {
        var itemId = Guid.NewGuid();
        var plant = RespondWithPlant((itemId, DeviceName));
        var cut = RenderAuthenticated(new RoutingStubHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith($"/{itemId}/config")
                ? throw new ArgumentException("the stub broke (test)")
                : plant(request)));

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([itemId.ToString()]));

        Assert.Equal(
            "The configuration of this device could not be read because an unforeseen failure stopped it.",
            cut.Find("#drawio-property-error").TextContent);
        Assert.Equal("ArgumentException: the stub broke (test)", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "drawio-property-error"));
        Assert.Empty(cut.FindAll("#drawio-property-loading"));
    }

    /// <summary>
    /// The device vanishes between the click and the answer - another editor deletes it - and the
    /// host answers 404, which the client turns into a null. That is not a failed read and it is not
    /// an empty configuration: it is a fact about the plant, and it is said as one.
    /// </summary>
    [Fact]
    public async Task ADeviceGoneFromTheHostIsSaidSoInsteadOfAPanelWithNothingInIt()
    {
        var itemId = Guid.NewGuid();
        var plant = RespondWithPlant((itemId, DeviceName));
        var cut = RenderAuthenticated(new RoutingStubHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith($"/{itemId}/config")
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : plant(request)));

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([itemId.ToString()]));

        var said = cut.Find("#drawio-property-error").TextContent;
        Assert.Contains("not (any more) on the host", said);
        Assert.Contains(itemId.ToString(), said);
        Assert.Empty(cut.FindAll("#drawio-property-panel fieldset.property-panel-group"));
    }

    /// <summary>
    /// The bridge stops being able to follow the selection: the panel goes away with the name on the
    /// line above it. Sixty fields describing a selection nobody is following any more would be the
    /// same stale claim, only larger.
    /// </summary>
    [Fact]
    public async Task WhenTheSelectionCannotBeFollowedThePanelGoesAwayToo()
    {
        var itemId = Guid.NewGuid();
        var cut = RenderAuthenticated(itemId);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([itemId.ToString()]));
        Assert.NotNull(cut.Find("#drawio-property-panel"));

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionUnavailable());

        Assert.Empty(cut.FindAll("#drawio-property-panel"));
    }

    /// <summary>
    /// <b>The race, measured.</b> The selection changes at every click and the read is an HTTP call:
    /// two answers can come back in the opposite order to the one they left in, and the late one
    /// would walk into the panel and leave it showing the properties of a device that is no longer
    /// selected - with the line above naming the other one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order is not hoped for, it is IMPOSED: the first device's answer is held on a
    /// <see cref="TaskCompletionSource{T}"/> and released only after the second one has already been
    /// shown. Without the generation check in <c>RefreshSelectedConfigAsync</c> this test fails on
    /// the last assertion (measured by taking the check out: the panel showed
    /// <see cref="DeviceName"/> where <see cref="OtherDeviceName"/> was expected).
    /// </para>
    /// <para>
    /// The panel's HEADER is asserted as well as its fields, and they are two different failures:
    /// the header follows the selection and the fields follow the answer, so a page that got this
    /// wrong would show one device's name over another device's values - which is worse than either
    /// half alone, and the only way to catch it is to look at both.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ALateAnswerDoesNotShowTheWrongDevicesProperties()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var plant = RespondWithPlant((first, DeviceName), (second, OtherDeviceName));

        var held = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = RenderAuthenticated(new RoutingStubHandlerAsync(request =>
            request.RequestUri!.AbsolutePath.EndsWith($"/{first}/config")
                ? held.Task
                : Task.FromResult(plant(request))));

        // First click: the answer will not come until this test says so. Not awaited - that is the
        // point - so the panel is waited FOR instead of read straight away: InvokeAsync only queues
        // the call on the renderer's dispatcher, and reading the page before it has run would be
        // reading the render before.
        var readingTheFirst = cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([first.ToString()]));
        cut.WaitForAssertion(() => Assert.Contains(DeviceName, cut.Find("#drawio-property-subject").TextContent));
        Assert.NotNull(cut.Find("#drawio-property-loading"));
        Assert.False(readingTheFirst.IsCompleted, "The first device's read did not stay suspended: the race was never staged.");

        // Second click, answered at once: from here on the panel is about the second device.
        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([second.ToString()]));
        Assert.Equal(OtherDeviceName, ValueOf(cut, nameof(PlantItemConfigDto.Name)));

        // And now the first device's answer arrives, out of order.
        held.SetResult(Json(Config(first, DeviceName)));
        await readingTheFirst;

        Assert.Contains(OtherDeviceName, cut.Find("#drawio-property-subject").TextContent);
        Assert.Equal(OtherDeviceName, ValueOf(cut, nameof(PlantItemConfigDto.Name)));
    }

    /// <summary>
    /// A failed read is not a condition the panel keeps: the next read of the same device, which is
    /// what a second click on it produces, either replaces the message with the values or replaces
    /// it with a new message. A declaration nobody takes down is the same kind of stale claim as a
    /// name left over from the click before.
    /// </summary>
    [Fact]
    public async Task AFailedReadIsTakenDownWhenTheNextOneSucceeds()
    {
        var itemId = Guid.NewGuid();
        var plant = RespondWithPlant((itemId, DeviceName));
        var configReads = 0;
        var cut = RenderAuthenticated(new RoutingStubHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith($"/{itemId}/config") && ++configReads == 1
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : plant(request)));

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([itemId.ToString()]));
        Assert.Contains("could not be read", cut.Find("#drawio-property-error").TextContent);

        await cut.InvokeAsync(() => cut.Instance.OnDrawioSelectionChanged([itemId.ToString()]));

        Assert.Empty(cut.FindAll("#drawio-property-error"));
        Assert.Equal(DeviceName, ValueOf(cut, nameof(PlantItemConfigDto.Name)));
    }
}
