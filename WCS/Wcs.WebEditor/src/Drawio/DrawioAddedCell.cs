namespace Wcs.WebEditor.Drawio;

/// <summary>
/// A cell that has just entered draw.io's model, as the bridge describes it
/// (<c>drawio-bridge.js</c>, <c>watchAdditions</c>): the raw facts, and no judgement.
/// </summary>
/// <remarks>
/// <para>
/// It is deliberately <b>not</b> <see cref="DrawioCell"/>, which is what comes back from a SAVE
/// (<see cref="DrawioReader"/>): that one is read out of the returned XML and carries the style, the
/// priority attribute, the arrow's ends and the cell's own XML, none of which exists - or matters -
/// at the moment a shape lands on the canvas. Reusing it would have meant filling eight fields with
/// zeros and empty strings at the JS boundary, and the first reader would have had to work out which
/// of them were measured.
/// </para>
/// <para>
/// The names are lower case on the JS side and matched case-insensitively by the interop's JSON
/// (<c>JsonSerializerDefaults.Web</c>): <c>id</c>, <c>mark</c>, <c>type</c>, <c>parent</c>, <c>x</c>,
/// <c>y</c>, <c>width</c>, <c>height</c>.
/// </para>
/// </remarks>
/// <param name="Id">draw.io's own identifier for the cell. It is never rewritten - see the session map in <c>DrawioEditor</c> - so it is the key the plant device is remembered under.</param>
/// <param name="Mark">The <c>wcs</c> attribute, or null when the cell carries none: a note, a box, an arrow the operator has just drawn.</param>
/// <param name="Type">The <c>wcsType</c> attribute (<see cref="DrawioLibrary.TypeAttribute"/>): the registry's numeric code, as text. Null when the cell does not declare one.</param>
/// <param name="Parent">The cell it hangs from, which for a cell on the canvas is <see cref="DrawioDocument.DefaultLayerId"/>. Anything else is refused rather than written: inside a group the box below is relative to the GROUP, and on a second layer it is absolute but the save would refuse the cell for good (<c>DrawioDelta.OnTheCanvas</c>).</param>
public readonly record struct DrawioAddedCell(
    string Id, string? Mark, string? Type, string? Parent,
    double X, double Y, double Width, double Height);
