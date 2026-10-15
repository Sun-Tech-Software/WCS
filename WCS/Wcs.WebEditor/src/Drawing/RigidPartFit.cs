using System.Globalization;

namespace Wcs.WebEditor.Drawing;

/// <summary>
/// Where a RIGID part (<see cref="Wcs.Api.Contracts.Dtos.ShapePartDto.Rigid"/>) goes when the
/// device it belongs to is not square. The shapes are written inside a SQUARE unit box
/// (0 0 100 100) and both surfaces stretch that box onto the device's own footprint - which on the
/// real plant is 122x191 for 43 devices out of 51. The frame must reach all four sides and so it
/// stretches; a gear that stretches becomes an oval, which is what the owner saw on his plant.
/// <para>
/// The remedy he chose: a rigid part keeps its proportion and stays centred on the footprint. This
/// is the one place that says what that means, because the two surfaces reach it by different
/// roads - <c>DrawioShape</c> composes an image, <c>PlantCanvas</c> draws paths - and a formula
/// written twice is a formula that will diverge.
/// </para>
/// </summary>
/// <remarks>
/// <b>How it composes.</b> Both surfaces already put the unit box on the device with the same
/// non-uniform scale: <c>scale(width/100, height/100)</c> about the device centre (the group
/// transform in <c>PlantCanvas.Transform</c>, and, for draw.io, the cell's own box with
/// <c>imageAspect=0</c>). The transform returned here is applied to the part FIRST, inside that
/// box, and the two multiply out to a UNIFORM scale: <c>kx * width/100 = ky * height/100 =
/// min(width,height)/100</c>. So a rigid part is drawn at one single scale, the smaller of the two,
/// about the same centre the elastic parts turn around - in proportion, centred, and never wider
/// than the footprint. The smaller of the two and not the larger: with the larger the gear would
/// spill out of the frame instead of sitting inside it, which is not how the owner's element is
/// built (frame 160x100, round gear inside).
/// <para>
/// <b>On a square footprint it returns nothing at all</b> - not an identity transform, no
/// attribute: there <c>kx</c> and <c>ky</c> are both 1 and rigid and elastic coincide, so the two
/// surfaces must emit exactly the character-for-character output they emitted before rigid parts
/// existed. A square that quietly gained a <c>transform="..."</c> would be a defect, and it is a
/// test that says so, not this comment.
/// </para>
/// <para>
/// <b>Degenerate footprints fall back to elastic.</b> A zero or a negative own size is not a
/// mistake here: both come out of <c>NodePlacement.LocalSize</c> for the SAME cause, a PARTIAL
/// geometry - a width or a height at zero, which <c>DiagramGeometryReader</c> admits on purpose -
/// and which of the two comes out is decided by the ANGLE, not by the cause. Unrotated, a zero side
/// stays a zero side. At every other angle inverting the system turns it into a NEGATIVE own size,
/// which is what <c>NodePlacement.IsMirroredSize</c> reports: a mirrored shape, not an invisible
/// one. <c>PlantCanvas</c> flags both on screen rather than hiding them - but a zero would divide
/// by zero, and a negative sign belongs to the OUTER scale, not to this ratio: mirroring a device
/// must not mirror its gear a second time. So the sign is taken off before the ratio is formed, and
/// a zero side gives no transform: a device that is already drawn as an exception is not also given
/// a silent NaN.
/// </para>
/// <para>
/// <b>The zero guard is an EXACT zero</b>, and only an unrotated device gives one. Measured: the
/// same partial geometry (122x0) comes back from <c>LocalSize</c> as a clean <c>0</c> at 0 degrees,
/// but as float dust a hair below zero at 90 and at 180 (-7e-15, -1e-14). Dust is not zero, so this
/// branch does not fall back there: it forms the ratio and returns a transform with a zero factor
/// (<c>scale(1,0)</c> at 90, <c>scale(0,1)</c> at 180), which flattens the rigid part onto a line.
/// That is not a hidden defect, because the OUTER scale carries the very same dust and flattens the
/// whole device with it - the node is already an exception, <c>NodePlacement.IsMirroredSize</c> says
/// so, and <c>PlantCanvas</c> flags it. It is written down because "a zero side falls back" is true
/// of the number this method receives, not of every partial geometry that reaches the canvas.
/// </para>
/// </remarks>
public static class RigidPartFit
{
    /// <summary>
    /// The SVG <c>transform</c> a rigid part needs inside a device of these OWN dimensions (the
    /// ones before rotation - <c>NodePlacement.LocalSize</c>), or <c>null</c> when it needs none:
    /// a square footprint, or a degenerate one. Callers must omit the attribute entirely on
    /// <c>null</c>, not write an empty one.
    /// </summary>
    public static string? Transform(double width, double height)
    {
        var w = Math.Abs(width);
        var h = Math.Abs(height);
        if (!double.IsFinite(w) || !double.IsFinite(h) || w == 0 || h == 0) return null;
        if (w == h) return null;

        var side = Math.Min(w, h);
        return string.Create(CultureInfo.InvariantCulture,
            $"translate(50,50) scale({side / w:0.#####},{side / h:0.#####}) translate(-50,-50)");
    }
}
