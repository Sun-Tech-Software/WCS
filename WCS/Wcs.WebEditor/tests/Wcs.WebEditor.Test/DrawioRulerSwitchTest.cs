using Bunit;
using Wcs.WebEditor.Drawio.Components;
using Xunit;

namespace Wcs.WebEditor.Test;

/// <summary>
/// Row 33 of the parity inventory: the page switch that turns draw.io's rulers on and off. This
/// component decides nothing about draw.io or about localStorage - both live in
/// <c>drawio-bridge.js</c>, which bUnit cannot run (no JavaScript engine here, see
/// <c>Wcs.WebEditor.Browser.Test</c> for that half). What is provable here is the one thing a Razor
/// component owns: it shows what <see cref="DrawioRulerSwitch.Enabled"/> says, and a click tells the
/// page - through <see cref="DrawioRulerSwitch.OnToggle"/> - what the operator asked for.
/// </summary>
public sealed class DrawioRulerSwitchTest : PlantCanvasBunitContext
{
    private bool? _lastToggle;

    private IRenderedComponent<DrawioRulerSwitch> Render(bool enabled) =>
        Render<DrawioRulerSwitch>(parameters => parameters
            .Add(p => p.Enabled, enabled)
            .Add(p => p.OnToggle, value => _lastToggle = value));

    [Fact]
    public void StartsUncheckedWhenTheMemorySaysOff()
    {
        var cut = Render(false);

        Assert.False(cut.Find("#drawio-ruler-switch").HasAttribute("checked"));
    }

    [Fact]
    public void StartsCheckedWhenTheMemorySaysOn()
    {
        var cut = Render(true);

        Assert.True(cut.Find("#drawio-ruler-switch").HasAttribute("checked"));
    }

    [Fact]
    public void TurningItOnTellsThePage()
    {
        var cut = Render(false);

        cut.Find("#drawio-ruler-switch").Change(true);

        Assert.Equal(true, _lastToggle);
    }

    [Fact]
    public void TurningItOffTellsThePage()
    {
        var cut = Render(true);

        cut.Find("#drawio-ruler-switch").Change(false);

        Assert.Equal(false, _lastToggle);
    }
}
