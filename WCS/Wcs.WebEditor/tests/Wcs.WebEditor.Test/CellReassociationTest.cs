using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawio;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// <b>Row 15: the rules that decide whether a shape may be pointed at a device by hand.</b> They
/// live apart from the page on purpose - what the page adds is three facts and a button - so the
/// refusals can be asked for one by one instead of being reached through a render.
/// </summary>
/// <remarks>
/// Every test below asserts on the SENTENCE as well as on the refusal, and not only that one came
/// back. A refusal whose words do not say which of the four happened is, for whoever is looking at
/// an orphan shape, the same as no answer at all: the whole point of this row is that writing an
/// identifier by hand fails <b>out loud</b>.
/// </remarks>
public sealed class CellReassociationTest
{
    private static readonly Guid Drawn = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Undrawn = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Dropped = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private const string OrphanCell = "orphan-1";

    private static PlantItemDto Item(Guid id, string name, double width, DeviceTypes type = DeviceTypes.Automation) =>
        new(id, name, type, null, null, null, default, default, 0, null, 0, 0, 0, 0, default!,
            10, 20, width, 184, 0);

    private static DrawioMarkedCell[] CanvasWith(params string[] deviceCells) =>
        [.. deviceCells.Select(id => new DrawioMarkedCell(id, CellReassociation.DeviceMark))];

    private static Dictionary<string, Guid> NothingBound() => [];

    // ---------------------------------------------------------------- what was typed

    [Fact]
    public void AnEmptyBoxIsRefusedAndSaysThereIsNothingToClear()
    {
        Assert.Null(CellReassociation.Parse("   ", out var refusal));
        Assert.NotNull(refusal);
        Assert.Contains("Nothing was typed", refusal);
        // The half that matters: whoever comes from the WinForms Editor emptied the box to CLEAR
        // the tag, and this sentence is the only thing that tells them that gesture is gone.
        Assert.Contains("does not take one away", refusal);
        Assert.Contains("Nothing was written", refusal);
    }

    [Fact]
    public void SomethingThatIsNotAnIdentifierIsRefusedByName()
    {
        Assert.Null(CellReassociation.Parse("the conveyor by the door", out var refusal));
        Assert.NotNull(refusal);
        // What was typed is quoted back: a refusal that does not say what it refused reads like a
        // page that did nothing.
        Assert.Contains("the conveyor by the door", refusal);
        Assert.Contains("Nothing was written", refusal);
    }

    [Fact]
    public void AnIdentifierIsReadWithTheSpaceAroundItTakenOff()
    {
        // Pasted out of a list, an identifier arrives with whitespace on it, and refusing that
        // would be refusing the only way anybody will ever get one into this box.
        Assert.Equal(Drawn, CellReassociation.Parse($"  {Drawn}\n", out var refusal));
        Assert.Null(refusal);
    }

    // ---------------------------------------------------------------- the four refusals

    [Fact]
    public void ADrawingThatCouldNotBeAskedRefusesInsteadOfBeingReadAsEmpty()
    {
        var why = CellReassociation.WhyNot(OrphanCell, Undrawn, Item(Undrawn, "Undrawn", 0), null, NothingBound());

        Assert.NotNull(why);
        Assert.Contains("could not be asked", why);
        Assert.Contains("Nothing was written", why);
    }

    [Fact]
    public void AShapeTheDrawingDoesNotMarkAsOneOfOursIsRefused()
    {
        // The note is on the canvas, it is even the selected cell - it simply carries no mark. The
        // page would record the pair, the reader would go on calling it decoration, and the save
        // would write nothing while the page claimed the shape stood for a device.
        var canvas = new DrawioMarkedCell[] { new(Drawn.ToString(), CellReassociation.DeviceMark) };

        var why = CellReassociation.WhyNot("a-free-note", Undrawn, Item(Undrawn, "Undrawn", 0), canvas, NothingBound());

        Assert.NotNull(why);
        Assert.Contains("not marked as a device", why);
        Assert.Contains("Nothing was written", why);
    }

    /// <summary>
    /// <b>The loaded gun.</b> An identifier the host does not answer for is refused by name and
    /// nothing at all is recorded - which is the whole reason this row is allowed to exist.
    /// </summary>
    [Fact]
    public void AnIdentifierThePlantDoesNotKnowIsRefusedByNameAndNothingIsWritten()
    {
        var unknown = Guid.Parse("99999999-9999-9999-9999-999999999999");

        var why = CellReassociation.WhyNot(OrphanCell, unknown, device: null, CanvasWith(OrphanCell), NothingBound());

        Assert.NotNull(why);
        Assert.Contains(unknown.ToString(), why);
        Assert.Contains("has no device", why);
        Assert.Contains("Nothing was written", why);
    }

    [Fact]
    public void ADeviceAlreadyDrawnByASeededShapeIsRefusedAndTheOtherCellIsNamed()
    {
        var why = CellReassociation.WhyNot(
            OrphanCell, Drawn, Item(Drawn, "Conveyor", 130),
            CanvasWith(OrphanCell, Drawn.ToString()), NothingBound());

        Assert.NotNull(why);
        Assert.Contains("Conveyor", why);
        // The OTHER cell by name: without it the operator has been told "no" about a drawing of
        // fifty shapes and no way of finding the one in the way.
        Assert.Contains(Drawn.ToString(), why);
        Assert.Contains("Nothing was written", why);
    }

    /// <summary>
    /// The same refusal through the OTHER road the save follows: a device drawn by a shape born
    /// from a drop, whose cell carries draw.io's identifier and not the device's.
    /// </summary>
    /// <remarks>
    /// Asking only the first road would let this one through, and the two shapes would send two
    /// geometries for one device at every save - the exact fault the refusal above exists for,
    /// reached by the door nobody looks at.
    /// </remarks>
    [Fact]
    public void ADeviceAlreadyDrawnByADroppedShapeIsRefusedToo()
    {
        var bound = new Dictionary<string, Guid> { ["drawio-7"] = Dropped };

        var why = CellReassociation.WhyNot(
            OrphanCell, Dropped, Item(Dropped, "Buffer 1", 130),
            CanvasWith(OrphanCell, "drawio-7"), bound);

        Assert.NotNull(why);
        Assert.Contains("drawio-7", why);
    }

    /// <summary>
    /// A pair whose cell is no longer on the canvas does <b>not</b> stand in the way: the shape it
    /// described has been deleted, and refusing on it would leave a gesture that can never go
    /// through and nothing on screen to explain why.
    /// </summary>
    [Fact]
    public void APairWhoseShapeHasBeenDeletedDoesNotStandInTheWay()
    {
        var bound = new Dictionary<string, Guid> { ["drawio-7"] = Dropped };

        Assert.Null(CellReassociation.WhyNot(
            OrphanCell, Dropped, Item(Dropped, "Buffer 1", 130), CanvasWith(OrphanCell), bound));
    }

    [Fact]
    public void PointingTheSameShapeAtTheSameDeviceTwiceSaysSoInsteadOfLookingLikeAFailure()
    {
        var bound = new Dictionary<string, Guid> { [OrphanCell] = Undrawn };

        var why = CellReassociation.WhyNot(
            OrphanCell, Undrawn, Item(Undrawn, "Undrawn", 0), CanvasWith(OrphanCell), bound);

        Assert.NotNull(why);
        Assert.Contains("already stands for", why);
        Assert.Contains("nothing needed to be", why);
    }

    [Fact]
    public void ADeviceNoCellStandsForIsLetThrough()
    {
        Assert.Null(CellReassociation.WhyNot(
            OrphanCell, Undrawn, Item(Undrawn, "Undrawn", 0),
            CanvasWith(OrphanCell, Drawn.ToString()), NothingBound()));
    }

    // ---------------------------------------------------------------- what the outcome promises

    [Fact]
    public void TheOutcomeSaysNothingHasReachedThePlantYet()
    {
        var said = CellReassociation.WhatItMeans(Item(Undrawn, "Undrawn", 0), theTypeIsDrawn: true);

        Assert.Contains("Undrawn", said);
        Assert.Contains("Nothing has reached the plant yet", said);
        Assert.Contains("next save", said);
    }

    /// <summary>
    /// The half-truth this Editor refuses to leave standing: a device whose type the registry does
    /// not declare is undrawn for that reason as well, so giving it a box repairs one of its two
    /// reasons and the drawing still shows nothing at the next opening.
    /// </summary>
    [Fact]
    public void ADeviceOfATypeTheRegistryDoesNotDeclareIsNotPromisedAShape()
    {
        var said = CellReassociation.WhatItMeans(
            Item(Undrawn, "Undrawn", 0, DeviceTypes.None), theTypeIsDrawn: false);

        Assert.Contains("device type", said);
        Assert.Contains("the type", said);
        Assert.DoesNotContain("draws it from the plant at every opening", said);
    }

    // ---------------------------------------------------------------- the candidates

    [Fact]
    public void TheCandidatesAreTheDevicesNoCellOfTheCanvasStandsFor()
    {
        var items = new[] { Item(Drawn, "Conveyor", 130), Item(Undrawn, "Undrawn", 0), Item(Dropped, "Buffer 1", 130) };
        var bound = new Dictionary<string, Guid> { ["drawio-7"] = Dropped };

        var candidates = CellReassociation.DevicesNoCellStandsFor(
            items, CanvasWith(Drawn.ToString(), "drawio-7", OrphanCell), bound);

        var only = Assert.Single(candidates);
        Assert.Equal(Undrawn, only.ItemId);
    }

    /// <summary>
    /// On a drawing nobody could look at the list is EMPTY and not the whole plant: the question is
    /// about the canvas, and a page that could not read the canvas has no business naming devices
    /// as "without a shape". It is the same answer the gesture gives a moment later.
    /// </summary>
    [Fact]
    public void ADrawingThatCouldNotBeAskedOffersNoCandidatesAtAll()
    {
        var items = new[] { Item(Drawn, "Conveyor", 130), Item(Undrawn, "Undrawn", 0) };

        Assert.Empty(CellReassociation.DevicesNoCellStandsFor(items, null, NothingBound()));
    }
}
