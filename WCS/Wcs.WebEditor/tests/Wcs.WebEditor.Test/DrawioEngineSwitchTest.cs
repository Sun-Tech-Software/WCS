using Bunit;
using Wcs.WebEditor.Drawio.Components;
using Wcs.WebEditor.Drawio.Coordination;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

public sealed class DrawioEngineSwitchTest : PlantCanvasBunitContext
{
    private int _starts;
    private int _stops;

    private IRenderedComponent<DrawioEngineSwitch> Render(NavigatorStatusTypes? status, DrawioEngineState? state = null) =>
        Render<DrawioEngineSwitch>(parameters => parameters
            .Add(p => p.Status, status)
            .Add(p => p.State, state ?? new DrawioEngineState(null, null))
            .Add(p => p.OnStart, () => _starts++)
            .Add(p => p.OnStop, () => _stops++));

    [Fact]
    public void AStoppedEngineOffersStartAndAsksFirst()
    {
        var cut = Render(NavigatorStatusTypes.Stop);

        cut.Find("#drawio-engine-start").Click();
        Assert.Equal(0, _starts);
        cut.Find("#drawio-engine-confirm").Click();

        Assert.Equal(1, _starts);
    }

    [Fact]
    public void TheConfirmationCanBeTakenBack()
    {
        var cut = Render(NavigatorStatusTypes.Stop);

        cut.Find("#drawio-engine-start").Click();
        cut.Find("#drawio-engine-cancel").Click();

        Assert.Empty(cut.FindAll("#drawio-engine-confirm"));
        Assert.Equal(0, _starts);
    }

    [Fact]
    public void ARunningEngineOffersStop()
    {
        var cut = Render(NavigatorStatusTypes.Run);

        Assert.Empty(cut.FindAll("#drawio-engine-start"));
        cut.Find("#drawio-engine-stop").Click();
        cut.Find("#drawio-engine-confirm").Click();

        Assert.Equal(1, _stops);
    }

    /// <summary>
    /// "Start the engine?" asked with the engine stopped, and the engine started from elsewhere before
    /// the Yes: the question is withdrawn, and nothing is sent - above all, not a Stop.
    /// </summary>
    [Fact]
    public void AQuestionTheEngineOvertookIsWithdrawn()
    {
        var cut = Render(NavigatorStatusTypes.Stop);
        cut.Find("#drawio-engine-start").Click();

        cut.Render(parameters => parameters.Add(p => p.Status, NavigatorStatusTypes.Run));

        Assert.Empty(cut.FindAll("#drawio-engine-confirm"));
        Assert.Single(cut.FindAll("#drawio-engine-stop"));
        Assert.Equal(0, _starts);
        Assert.Equal(0, _stops);
    }

    [Fact]
    public void AnUnknownEngineOffersNothing()
    {
        var cut = Render(null);

        Assert.Empty(cut.FindAll("#drawio-engine-start"));
        Assert.Empty(cut.FindAll("#drawio-engine-stop"));
        Assert.Contains("unknown", cut.Find("#drawio-engine-state").TextContent);
    }

    /// <summary>
    /// The engine carries out a Run only from Stop or Pause, and a Stop only from Run
    /// (HandleSetNavigatorRun/HandleSetNavigatorStop): in any other status it answers with a Warning.
    /// The switch offers only what the engine would carry out, and says the status in words.
    /// </summary>
    [Theory]
    [InlineData(NavigatorStatusTypes.Stop, "Engine: Stop", true, false)]
    [InlineData(NavigatorStatusTypes.Pause, "Engine: Pause", true, false)]
    [InlineData(NavigatorStatusTypes.Run, "Engine: Run", false, true)]
    [InlineData(NavigatorStatusTypes.Recover, "Engine: Recovering after a failure", false, false)]
    [InlineData(NavigatorStatusTypes.Dispose, "Engine: Shut down", false, false)]
    public void TheSwitchOffersOnlyWhatTheEngineCarriesOut(NavigatorStatusTypes status, string said, bool start, bool stop)
    {
        var cut = Render(status);

        Assert.Equal(said, cut.Find("#drawio-engine-state").TextContent.Trim());
        Assert.Equal(start, cut.FindAll("#drawio-engine-start").Count == 1);
        Assert.Equal(stop, cut.FindAll("#drawio-engine-stop").Count == 1);
    }

    /// <summary>"Start the engine?" asked in Stop, and the engine went to Recover before the Yes: the question is withdrawn.</summary>
    [Fact]
    public void AStartQuestionOvertakenByRecoverIsWithdrawn()
    {
        var cut = Render(NavigatorStatusTypes.Stop);
        cut.Find("#drawio-engine-start").Click();

        cut.Render(parameters => parameters.Add(p => p.Status, NavigatorStatusTypes.Recover));

        Assert.Empty(cut.FindAll("#drawio-engine-confirm"));
        Assert.Equal(0, _starts);
    }

    [Fact]
    public void APendingCommandDisablesTheSwitchAndSaysSo()
    {
        var cut = Render(NavigatorStatusTypes.Stop, new DrawioEngineState("Starting the engine...", null));

        Assert.True(cut.Find("#drawio-engine-start").HasAttribute("disabled"));
        Assert.Equal("Starting the engine...", cut.Find("#drawio-engine-pending").TextContent.Trim());
    }

    /// <summary>
    /// A refusal is not the switch's to say: it is a sentence to read whole, and the page says it on a
    /// line of its own under the bar (DrawioTopBarTest.TheEnginesRefusalIsALineOfItsOwnUnderTheBar).
    /// Said in the switch, it was on the bar's one line, cut short, its end behind a tip (review of
    /// 29/9) - and said in both, its id would be on the page twice.
    /// </summary>
    [Fact]
    public void ARefusalIsNotSaidInTheSwitch()
    {
        const string Refusal = "The engine was NOT started. Read only: somebody else has been editing the plant since 10:00:00. Nothing was written.";
        var cut = Render(NavigatorStatusTypes.Stop, new DrawioEngineState(null, Refusal));

        Assert.Empty(cut.FindAll("#drawio-engine-error"));
        Assert.DoesNotContain("NOT started", cut.Markup);
        // The switch is still there, and still offers its gesture.
        Assert.Single(cut.FindAll("#drawio-engine-start"));
    }
}
