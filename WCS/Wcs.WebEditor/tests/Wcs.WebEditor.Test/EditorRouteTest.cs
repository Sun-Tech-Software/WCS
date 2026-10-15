using System.Reflection;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Wcs.WebEditor;
using Xunit;
// The three page components are named without importing Wcs.WebEditor.Pages whole: that namespace
// also holds a "Key", which collides with Bunit.Key, and the using of Bunit is needed to render.
using DrawioEditorPage = Wcs.WebEditor.Pages.DrawioEditor;
using EditorRedirectPage = Wcs.WebEditor.Pages.EditorRedirect;

namespace Wcs.WebEditor.Test;

/// <summary>
/// Who answers at <c>/editor</c>. The old SVG canvas is condemned (owner's decision of 2026-09-17)
/// and it is GONE: the route lost it first - a beta tester who opened it by accident TOOK THE EDIT
/// PERMIT and so stopped draw.io from saving - and the files followed once no row of the parity
/// table needed them. What is left here is the promise that the address still answers.
/// </summary>
/// <remarks>
/// bUnit renders components directly and never goes through the routing, so nothing it does could
/// notice this change - which is exactly why the routing needs a test of its own here. This one
/// reads the route attributes off the assembly, which is the same thing <c>Router</c> reads at run
/// time. The end to end half - a browser typing the address and landing on the other page - is
/// <c>Wcs.WebEditor.Browser.Test.EditorRouteRedirectTest</c>, against a real host.
/// </remarks>
public sealed class EditorRouteTest : PlantCanvasBunitContext
{
    private static List<Type> ComponentsRoutedTo(string template) =>
        typeof(WebEditorExtensions).Assembly.GetTypes()
            .Where(type => typeof(IComponent).IsAssignableFrom(type))
            .Where(type => type.GetCustomAttributes<RouteAttribute>().Any(route => route.Template == template))
            .ToList();

    /// <summary>
    /// The route belongs to the redirect, and to nothing else. Two components claiming one template
    /// is not a quieter version of this: the router throws at the first navigation, so the
    /// <c>Single</c> here is a real question and not decoration.
    /// </summary>
    [Fact]
    public void TheEditorAddressIsAnsweredByTheRedirectAndNotByTheOldCanvas()
    {
        Assert.Equal(typeof(EditorRedirectPage), Assert.Single(ComponentsRoutedTo("/editor")));

        // The second half of this used to assert that the old canvas carried no route. It carries
        // no route because it no longer exists, and a test cannot name a deleted type: what stands
        // in its place is the Single above - one component answers here, and it is the redirect.
    }

    /// <summary>
    /// The address the redirect sends to has to exist, and to be the new Editor's. Without this the
    /// pair could go green while sending everybody who types <c>/editor</c> onto the router's
    /// "Pagina non trovata." - a redirect into nothing is worse than no redirect.
    /// </summary>
    [Fact]
    public void TheAddressTheRedirectSendsToIsTheDrawioEditor()
    {
        Assert.Equal(typeof(DrawioEditorPage), Assert.Single(ComponentsRoutedTo("/editor/drawio")));
    }

    /// <summary>
    /// And what the redirect does when it is rendered. Static rendering turns this navigation into
    /// an HTTP redirect issued by the server; here there is no server, so what is measured is the
    /// navigation itself - the component asked to go to <c>/editor/drawio</c> and did it on its own,
    /// without anybody clicking anything. The <c>replace: true</c> is asked of the recorded
    /// <see cref="NavigationOptions"/>, not deduced: without it the Back button lands back on
    /// <c>/editor</c>, which bounces the operator forward again and leaves him unable to go back at
    /// all. (Rilievo 3 of the review: nothing was proving that argument.)
    /// </summary>
    /// <remarks>
    /// <b>WHAT THIS TEST CANNOT CATCH, AND WHO CATCHES IT</b> (rilievo 4 of the review, measured by
    /// mutation). bUnit runs <c>OnAfterRender</c> just like <c>OnInitialized</c>; STATIC rendering,
    /// which is how this component actually runs, does not run <c>OnAfterRender</c> at all. Move the
    /// navigation from <c>OnInitialized</c> to <c>OnAfterRender</c> and this whole project stays
    /// green while <c>GET /editor</c> answers <c>200 OK</c> with an empty <c>Location</c> and serves
    /// a blank page. So this test cannot fail for the reason that counts, and the whole guarantee
    /// "the old canvas does not open" rests on ONE test:
    /// <c>Wcs.WebEditor.Browser.Test.EditorRouteRedirectTest</c>, against a real host. If that one is
    /// ever deleted or skipped, this file goes on saying yes to a route that no longer redirects.
    /// </remarks>
    [Fact]
    public void RenderingTheRedirectNavigatesToTheDrawioEditorReplacingTheHistoryEntry()
    {
        var navigation = Services.GetRequiredService<BunitNavigationManager>();
        var before = navigation.Uri;

        Render<EditorRedirectPage>();

        Assert.NotEqual(before, navigation.Uri);
        Assert.EndsWith("/editor/drawio", navigation.Uri);

        var hop = Assert.Single(navigation.History);
        Assert.EndsWith("/editor/drawio", hop.Uri);
        Assert.True(hop.Options.ReplaceHistoryEntry);
    }
}
