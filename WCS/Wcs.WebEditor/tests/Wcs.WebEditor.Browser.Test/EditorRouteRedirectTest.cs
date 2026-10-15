using System.Net;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// What happens to somebody who types <c>/editor</c>, measured against a real host instead of
/// deduced. The old SVG canvas is condemned (owner's decision of 2026-09-17) and this stage of the
/// dismantling is only its route: it took the edit permit as it opened, so a beta tester who landed
/// there by accident stopped draw.io from saving.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is not enough to test this with bUnit.</b> bUnit renders components directly and never
/// goes through the routing: it cannot tell a page that is routed from one that is not, and it says
/// nothing about what a browser gets back. <c>Wcs.WebEditor.Test.EditorRouteTest</c> reads the route
/// attributes, which is one step closer; only this one asks the product.
/// </para>
/// <para>
/// <b>THIS TEST IS ON ITS OWN, and that is measured</b> (rilievo 4 of the review). Moving
/// <c>EditorRedirect</c>'s navigation from <c>OnInitialized</c> to <c>OnAfterRender</c> leaves all
/// 981 of <c>Wcs.WebEditor.Test</c> green - bUnit runs <c>OnAfterRender</c>, static rendering does
/// not - and turns <c>GET /editor</c> into <c>200 OK</c> with an empty <c>Location</c>: a blank page
/// where the Editor used to be. This test is the one that goes red (the bUnit project was run whole
/// under that mutation; the rest of THIS project was not, so "the only one" is not claimed). It is
/// not to be deleted or skipped without something taking its place.
/// </para>
/// <para>
/// <b>And it is the guard on the trap of this batch.</b> <c>"/editor"</c> is a PREFIX in
/// <c>ApiKeyMiddleware.AnonymousPathPrefixes</c>, and it covers <c>/editor/drawio</c> too: taking it
/// out - the obvious move when the old page goes away - would lock the NEW Editor out. Both halves
/// here travel with <b>no key at all</b>. MEASURED, by taking that entry out of the list and
/// rebuilding the host: <c>GET /editor</c> came back <c>401 Unauthorized</c> and this test went red
/// there. With the entry in place it is <c>302 Found</c> to <c>/editor/drawio</c>, and following it
/// is <c>200</c>.
/// </para>
/// <para>
/// No browser: an HTTP client is the whole of what is being measured here, and a Playwright browser
/// would add a minute of setup to a question about a status code and a header.
/// </para>
/// </remarks>
public sealed class EditorRouteRedirectTest(ITestOutputHelper output)
{
    [Fact]
    public async Task TheOldEditorAddressRedirectsToTheDrawioEditorAndNeedsNoKey()
    {
        await using var host = await WcsHostProcess.StartAsync();
        output.WriteLine(host.Declaration);

        // First half: the answer itself. No key on the request - that is the point - and no
        // following of the redirect, because what is being read is the redirect.
        using var stopping = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = host.BaseAddress,
        };
        using var redirect = await stopping.GetAsync("editor");
        output.WriteLine($"GET /editor -> {(int)redirect.StatusCode} {redirect.StatusCode}, Location: {redirect.Headers.Location}");

        Assert.InRange((int)redirect.StatusCode, 300, 399);
        Assert.NotNull(redirect.Headers.Location);
        Assert.EndsWith("/editor/drawio", redirect.Headers.Location!.ToString());

        // Second half, and the one that matters more: following it arrives at a page that ANSWERS,
        // without a key. A redirect onto a 401 or onto "Pagina non trovata." would be worse than
        // leaving the old canvas where it was.
        using var following = new HttpClient { BaseAddress = host.BaseAddress };
        using var landed = await following.GetAsync("editor");

        Assert.Equal(HttpStatusCode.OK, landed.StatusCode);
        Assert.EndsWith("/editor/drawio", landed.RequestMessage!.RequestUri!.ToString());

        // And what came back is a page, not an empty body that a status code alone would not catch.
        var body = await landed.Content.ReadAsStringAsync();
        Assert.Contains("<html", body, StringComparison.OrdinalIgnoreCase);
    }
}
