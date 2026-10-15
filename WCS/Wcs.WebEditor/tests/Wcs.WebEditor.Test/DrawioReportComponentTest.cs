using Bunit;
using Wcs.WebEditor.Drawio;
using Wcs.WebEditor.Drawio.Components;
using Wcs.WebEditor.Drawio.Coordination;
using Xunit;

namespace Wcs.WebEditor.Test;

public sealed class DrawioReportComponentTest : BunitContext
{
    [Fact]
    public void SaveReportKeepsEveryStableClassAndOutcomeOrder()
    {
        var state = new DrawioSaveReportState(
            Error: "error",
            Summary: "summary",
            Written: ["written"],
            Unconfirmed: ["uncertain"],
            NotWritten: ["not written"],
            Refusals: [new DrawioRefusal("cell-1", "refused")],
            Notices: [new DrawioNotice("cell-2", "notice")]);

        var cut = Render<DrawioSaveReport>(parameters => parameters.Add(p => p.State, state));

        Assert.Equal("error", cut.Find("#drawio-save-error").TextContent);
        Assert.Equal("summary", cut.Find("#drawio-save-summary").TextContent);
        Assert.Single(cut.FindAll("li.drawio-written"));
        Assert.Single(cut.FindAll("li.drawio-unconfirmed"));
        Assert.Single(cut.FindAll("li.drawio-not-written"));
        Assert.Single(cut.FindAll("li.drawio-refusal"));
        Assert.Single(cut.FindAll("li.drawio-notice"));

        var outcomes = cut.FindAll("li").Select(node => node.ClassList.Single()).ToArray();
        Assert.Equal(
            ["drawio-written", "drawio-unconfirmed", "drawio-not-written", "drawio-refusal", "drawio-notice"],
            outcomes);
    }

    /// <summary>
    /// The report's own branches. Since the passata grafica (2026-09-29) the report is the window behind
    /// the creation's line in the row under the bar, and two pieces that were not the report left it:
    /// the hidden "creating/idle" stays above the bar (DrawioEditorStatus), and the declaration that a
    /// drop can no longer become a device is a line of its own in the row - both measured on the page
    /// by DrawioNoticesTest. Here, that the report does not render them twice.
    /// </summary>
    [Fact]
    public void CreationReportRendersCreatedMissedAndRefusedBranchesAndNothingElse()
    {
        var state = new DrawioCreationReportState(
            Error: "creation error",
            Created:
            [
                new CreationLine("created", CanvasFollowed: true),
                new CreationLine("created but rename missed", CanvasFollowed: false),
            ],
            NotCreated: ["not created"],
            Unavailable: true,
            UnavailableMessage: "listener unavailable",
            Creating: true);

        var cut = Render<DrawioCreationReport>(parameters => parameters.Add(p => p.State, state));

        Assert.Equal("creation error", cut.Find("#drawio-creation-error").TextContent);
        Assert.Equal(2, cut.FindAll("li.drawio-created").Count);
        Assert.Single(cut.FindAll("li.drawio-created-label-missed"));
        Assert.Single(cut.FindAll("li.drawio-not-created"));
        Assert.Empty(cut.FindAll("#drawio-creation-state"));
        Assert.Empty(cut.FindAll("#drawio-creation-unavailable"));
    }

    [Fact]
    public void AnEmptyCreationReportRendersNothing()
    {
        var state = new DrawioCreationReportState(
            Error: null,
            Created: [],
            NotCreated: [],
            Unavailable: false,
            UnavailableMessage: "unused",
            Creating: false);

        var cut = Render<DrawioCreationReport>(parameters => parameters.Add(p => p.State, state));

        Assert.Empty(cut.FindAll("#drawio-creation-report"));
        Assert.Equal("", cut.Markup.Trim());
    }
}
