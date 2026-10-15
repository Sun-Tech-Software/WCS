using System.Text;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawing;

namespace Wcs.WebEditor.Drawio;

/// <summary>
/// La sagoma di un tipo di dispositivo come immagine per draw.io. Le parti vengono dal registro
/// (<see cref="DeviceTypeDto.Shape"/>), le stesse che disegnano la tela SVG: una sola fonte per
/// due superfici.
/// </summary>
public static class DrawioShape
{
    /// <summary>
    /// The fill of the FIRST part when it is drawn as an outline only
    /// (<see cref="ShapePartDto.Filled"/> is false). White, not "none", because that is what this
    /// surface has always painted: a draw.io cell shows the image over the sheet and over whatever
    /// edge passes beneath it, and an unpainted body would let all of that through. The canvas makes
    /// the opposite choice for the same part - see <c>FillOf</c> in <c>PlantCanvas.razor</c> - and
    /// the two surfaces have differed on this since before parts existed.
    /// </summary>
    private const string OutlineFill = "#ffffff";

    /// <summary>
    /// The fill of an outlined part that is NOT the first one: nothing at all, the same answer the
    /// canvas gives every outlined part.
    /// <para>
    /// <b>Measured in a real browser before the composite shapes were drawn</b>, on one shape, with
    /// the image this very method produces rasterised in Chromium and the pixels read back: with the
    /// white given to every outlined part, an outlined arrow declared over a filled gear turned the
    /// gear white underneath it - the pixel at (25,50), a gear tooth, read <c>#ffffff</c> instead of
    /// <c>#334155</c>. An outlined part drawn over a filled one does not decorate it, it rubs it
    /// out; and an arrow written as a bare polyline (shaft plus head, no <c>Z</c>) closes itself for
    /// filling purposes and would have become a white wedge the same way.
    /// </para>
    /// <para>
    /// The white belongs to the BODY, not to "being outlined": the first part is the one that sits
    /// on the sheet, everything after it is drawn upon the body and has no sheet of its own to
    /// stand for. Careful with what the registry's tests actually hold it to: they check the
    /// first part's BOUNDING BOX against the unit square, not how much of it the outline encloses -
    /// measured, a body made of bare lines with the same bounding box leaves that test green, and
    /// the red comes from elsewhere (the centre of the shape no longer falling inside any filled
    /// part). So "carries the whole footprint" is the intent, not a guarded fact. That is why this is a rule about position and not a third value on
    /// <see cref="ShapePartDto.Filled"/>: a part that wants its area painted says so with
    /// <c>Filled</c>, and a part that does not want it painted must not be given white by accident.
    /// </para>
    /// </summary>
    private const string DecorationFill = "none";

    /// <summary>
    /// The image of a type <b>on a device of these OWN dimensions</b>: one <c>&lt;path&gt;</c> per
    /// part, <b>in the order the registry declares them</b>, so the last part is drawn over the
    /// previous ones exactly as SVG stacks them.
    /// <para>
    /// The size is a parameter and not a constant because draw.io stretches this image onto the
    /// cell (<c>imageAspect=0</c> in <see cref="DeviceStyle"/>) and the cell is not square: a
    /// RIGID part (<see cref="ShapePartDto.Rigid"/>) has to be shrunk inside the unit box first, so
    /// that the stretch puts it back in proportion - see <see cref="RigidPartFit"/>. An image
    /// composed without knowing the footprint is an image that can only be right on a square one,
    /// and that is the whole defect this fixes. The palette thumbnail passes its own square size
    /// and gets back, character for character, what it got before rigid parts existed.
    /// </para>
    /// </summary>
    public static string ImageStyle(DeviceTypeDto type, double width, double height)
    {
        var rigid = RigidPartFit.Transform(width, height);
        var paths = string.Concat(type.Shape.Select((part, index) =>
            $"""<path d="{part.Path}"{TransformOf(part, rigid)} fill="{FillOf(part, index)}" stroke="{part.Color}" stroke-width="3" stroke-linejoin="round"/>"""));
        var svg = $"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">{paths}</svg>""";
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
        // Senza ";base64": si veda la prova che sorveglia questa riga.
        return $"image=data:image/svg+xml,{base64}";
    }

    /// <summary>
    /// The attribute a rigid part carries, with its leading space, or nothing at all for an elastic
    /// part and for a footprint that needs none. Written as "nothing at all" and not as
    /// <c>transform=""</c>: the point of this branch is that a square device must produce the very
    /// same bytes as before rigid parts existed, and an attribute that was not there before would
    /// break that whatever a renderer makes of it.
    /// </summary>
    private static string TransformOf(ShapePartDto part, string? rigid) =>
        part.Rigid && rigid is not null ? $" transform=\"{rigid}\"" : string.Empty;

    /// <summary>
    /// Three answers, not two: the part's own colour when it is filled, the sheet white when it is
    /// the outlined body, nothing at all for an outlined part above the body. See
    /// <see cref="OutlineFill"/> and <see cref="DecorationFill"/>.
    /// <para>
    /// <b>This is a rule about POSITION, and it is not the same rule as
    /// <see cref="ShapePartDto.Rigid"/></b> even though on the seven types of today the two agree
    /// part for part - the body is the first one and the only elastic one. Here "first" means "the
    /// one standing on the sheet", which is why it gets the sheet's white; <c>Rigid</c> means "does
    /// not stretch with the footprint". A type that wanted an elastic decoration, or a rigid body,
    /// would need to say one without the other, and it can. Two rules that today give the same
    /// answer, kept apart on purpose: merging them would save a field and lose a distinction.
    /// </para>
    /// </summary>
    private static string FillOf(ShapePartDto part, int index) =>
        part.Filled ? part.Color : index == 0 ? OutlineFill : DecorationFill;

    /// <summary>
    /// Lo stile intero con cui un dispositivo di quel tipo appare in draw.io: l'immagine qui sopra
    /// piu' il modo in cui il nome sta sotto la sagoma. <b>L'unico posto che lo produce</b>, e le
    /// due superfici che lo mostrano lo chiamano tutte e due - il pannello delle forme
    /// (<see cref="DrawioLibrary"/>) e la tela (<see cref="DrawioDocument"/>), che gli appende la
    /// sola rotazione perche' e' l'unico a cui serve.
    /// <para>
    /// Nasce perche' era scritto due volte a mano: misurato, <c>fontSize=9</c> cambiato in
    /// <c>fontSize=12</c> in una sola delle due copie lasciava l'intera suite verde, e la miniatura
    /// del pannello e il dispositivo sulla tela divergevano senza che niente lo dicesse. Chi trascina
    /// una voce si aspetta di deporre cio' che vede gia' accanto.
    /// </para>
    /// </summary>
    /// <remarks>
    /// The own dimensions travel with the type since rigid parts exist: <c>imageAspect=0</c> below
    /// is what stretches the image onto the cell, so the cell's size is part of what the image has
    /// to be. Whoever asks for a style already knows the box it will go in - the document knows the
    /// device's own box, the palette knows its thumbnail is square - and asking them to say it is
    /// what keeps the two surfaces in step.
    /// </remarks>
    public static string DeviceStyle(DeviceTypeDto type, double width, double height) =>
        $"shape=image;{ImageStyle(type, width, height)};imageAspect=0;"
        + "verticalLabelPosition=bottom;verticalAlign=top;labelBackgroundColor=#ffffff;fontSize=9;";
}
