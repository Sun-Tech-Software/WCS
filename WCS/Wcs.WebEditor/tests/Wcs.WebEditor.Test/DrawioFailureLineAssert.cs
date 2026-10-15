using Bunit;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Wcs.WebEditor.Test;

/// <summary>
/// Row 37 of the queue: a line of the properties panel that says a failure carries what came back -
/// the host's answer as it came, or the failure's type and message - behind the "?" at its right
/// (<c>DrawioFailureLine</c>), and not in its sentence. So do the lines the panel shares with other
/// windows, in those windows too (the owner's answer of 9/10): the edit permit that could not be asked
/// for, a command's outcome, the label the drawing did not take.
/// </summary>
internal static class DrawioFailureLineAssert
{
    /// <summary>
    /// What is behind the "?" of the line <paramref name="lineId"/>: checked to be inside the note the
    /// "?" opens, shut until it is pressed, and apart from the sentence.
    /// </summary>
    public static string BehindTheQuestionMark<TComponent>(IRenderedComponent<TComponent> cut, string lineId)
        where TComponent : IComponent
    {
        var detail = cut.Find($"#{lineId}-detail");
        Assert.NotNull(detail.Closest($"#{lineId}-help-body"));
        Assert.True(cut.Find($"#{lineId}-help-body").HasAttribute("hidden"), "What came back is open before its \"?\" is pressed.");
        Assert.Null(cut.Find($"#{lineId}").Closest($"#{lineId}-help-body"));
        return detail.TextContent;
    }
}
