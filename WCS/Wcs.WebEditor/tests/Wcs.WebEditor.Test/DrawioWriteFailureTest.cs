using Wcs.Api.Client;
using Wcs.WebEditor.Drawio.Coordination;
using Xunit;

namespace Wcs.WebEditor.Test;

/// <summary>
/// Final fix round, C4: every branch of <see cref="DrawioWriteFailure.Said"/> puts its why between the
/// head and the tail, so a head written for "because" - the creation's ends with a comma - reads the
/// same in all of them. The conflict and the page's own finding used to print the head and the tail
/// back to back, then the why after a colon.
/// </summary>
public sealed class DrawioWriteFailureTest
{
    private const string CreationHead = "cell-1: the device was not created, or not completely,";
    private const string CreationTail = ": go and look before dragging the shape again";
    private const string SaveHead = "Save interrupted at step 2 of 3";
    private const string SaveTail = ": the plant was left halfway. What follows says exactly what was written and what was not";

    [Fact]
    public void AConflictSaysItsWhyBetweenTheHeadAndTheTail()
    {
        var conflict = new GeometryConflictException(
            [Guid.Parse("33333333-3333-3333-3333-333333333333")], "");

        var said = DrawioWriteFailure.Said(CreationHead, CreationTail, conflict);

        Assert.Equal(
            "cell-1: the device was not created, or not completely, because the geometry changed elsewhere for 33333333-3333-3333-3333-333333333333: go and look before dragging the shape again. " +
            "No automatic retry was made, because it could overwrite the newer position, size or rotation. Reload the plant before saving again.",
            said.Text);
        Assert.Null(said.Detail);
    }

    [Fact]
    public void ThePagesOwnFindingIsTheClauseAfterBecause()
    {
        var said = DrawioWriteFailure.Said(SaveHead, SaveTail,
            new InvalidOperationException("The host confirmed an empty geometry version."));

        Assert.Equal(
            "Save interrupted at step 2 of 3 because the host confirmed an empty geometry version: the plant was left halfway. What follows says exactly what was written and what was not.",
            said.Text);
        Assert.Null(said.Detail);
    }

    [Fact]
    public void AFindingThatOpensWithAnAcronymKeepsIt()
    {
        var said = DrawioWriteFailure.Said(SaveHead, SaveTail, new InvalidOperationException("PLC 4 is not on the host any more."));

        Assert.Equal(
            "Save interrupted at step 2 of 3 because PLC 4 is not on the host any more: the plant was left halfway. What follows says exactly what was written and what was not.",
            said.Text);
    }

    [Fact]
    public void AnUnforeseenFailureIsSaidAsOneWithWhatItWasBehindTheQuestionMark()
    {
        var said = DrawioWriteFailure.Said("The creation stopped", "", new NotSupportedException("nobody expected it (test)"));

        Assert.Equal("The creation stopped on an unforeseen failure.", said.Text);
        Assert.Equal("NotSupportedException: nobody expected it (test)", said.Detail);
    }
}
