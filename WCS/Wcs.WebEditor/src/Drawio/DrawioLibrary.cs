using System.Globalization;
using System.Net;
using System.Text.Json;
using Wcs.Api.Contracts.Dtos;

namespace Wcs.WebEditor.Drawio;

/// <summary>
/// The "devices" section of draw.io's shapes panel, <b>generated from the registry</b> the engine
/// declares: one entry per type, never a list written by hand. It is the same rule that holds
/// <see cref="DrawioDocument"/> up - the day a type is added, or a shape changes, the panel follows
/// by itself.
/// <para>
/// <b>What is dragged out of here is born on the plant.</b> Owner's decision of 2026-09-17: the
/// device is born AT THE DROP, not at the save - the same moment as in the WinForms Editor - so it
/// shows up in the lists at once and can be configured on the spot. The cells are born marked
/// <c>wcs="device"</c>, they carry <b>no identifier of ours</b> (draw.io decides the cell's own,
/// and it is not rewritten: the page keeps the drawn cell -&gt; plant device pair in a session map,
/// exactly as it already does for junctions), and they declare their TYPE in
/// <see cref="TypeAttribute"/>. Until this batch the two texts that a dragger reads - the section
/// title and <see cref="EntryNote"/> - sent them to <c>/editor</c>, which is being dismantled: they
/// now say what really happens.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>What this text is, and how it reaches draw.io.</b> It is the value of the <c>libraries</c>
/// entry of draw.io's configuration (its "custom entries"): a JSON list of sections, each with its
/// own entries, and each entry with its drawings <b>inline</b> - no file and no address to download
/// them from. It travels in the <c>configure</c> action of the embedding protocol, which
/// <c>drawio-bridge.js</c> answers the moment draw.io asks for it.
/// </para>
/// <para>
/// <b>Why inline and not a file to download</b> (measured in a real browser, not deduced). The
/// other road draw.io offers is an <c>.xml</c> library fetched from an address, through the
/// <c>clibs</c> parameter or <c>defaultCustomLibraries</c>. It works - it was seen appearing in the
/// panel - but it demands three things this one does not: an <b>absolute address of our own
/// origin</b> (<c>Editor.isCorsEnabledForUrl</c>: everything else goes through an external proxy,
/// which a plant with no internet does not have), hence a new HTTP route serving the registry
/// without a key; <b>valid XML</b> content, that is with the drawing's <c>&lt;</c> escaped (a file
/// written without escaping is refused <b>in silence</b> - the failure sits inside an empty
/// <c>catch</c> of draw.io's, and the panel is simply left without the section: it cost the writer
/// of this one bogus measurement); and one more entry in the drawer's own browser settings, which
/// draw.io writes there by itself and which stays even after the address changes.
/// </para>
/// <para>
/// <b>And what the chosen road costs</b>, because a list of the other one's costs alone would be
/// half a report. Carrying <c>libraries</c> in the configuration <b>closes General</b>, which was
/// the only section of the panel born open: in the bundle it is built with
/// <c>addGeneralPalette(null == this.customEntries)</c>, and that argument is <c>addPalette</c>'s
/// <i>expand</i> - as soon as the configuration carries <c>libraries</c>, <c>customEntries</c> is no
/// longer null and General is born closed like the others (Misc, Advanced and UML are called with
/// <c>false</c> written in the code, Basic and Arrows with no argument at all). Whoever opened
/// <c>/editor/drawio</c> before task 6 saw General's thumbnails at once, and now needs one click.
/// <b>It cannot be worked around on this road</b>: it is the same <i>expand</i> of
/// <c>addPalette</c> that makes ours be born closed - not the same line, which is in
/// <c>addCustomEntries</c> and carries a hand-written <c>false</c> - and in <c>addPalette</c> the
/// opening is <c>expand &amp;&amp; expandLibraries</c>, where the second can only close further,
/// never open again. The other road (<c>clibs</c>) would not pay this price - it does not go
/// through the configuration, so <c>customEntries</c> stays null, and its section was seen
/// appearing already open - but it carries the three costs above, which weigh more; the account was
/// done knowing both columns.
/// </para>
/// </remarks>
public static class DrawioLibrary
{
    /// <summary>
    /// The section's identifier inside draw.io's configuration. It is not a name for anybody to
    /// read: it is the key with which the <c>libs</c> address parameter asks for the section to be
    /// <b>visible</b> in the panel (see the comment on <c>DrawioEditor.FrameUrl</c>). Visible and
    /// not open, which are two different things: <c>libs</c> decides which sections the panel shows,
    /// and ours shows up <b>closed</b> - see the <c>remarks</c> at the head of this class.
    /// </summary>
    public const string SectionId = "wcsDevices";

    /// <summary>
    /// The title the panel shows above the entries, and it is the place where the fact arrives
    /// <b>before the gesture</b>: devices really are created from here. There is no on-screen notice
    /// besides this one and <see cref="EntryNote"/> - an honest title is enough, and one more notice
    /// is one people learn to dismiss. The fact changed sign with the creation at the drop, and the
    /// title with it: leaving it as it was would have put in writing, above the entries, the
    /// opposite of what the gesture does.
    /// <para>
    /// <b>It is short of necessity, and this is the measurement</b>: draw.io draws a section's
    /// title with <c>white-space:nowrap; overflow:hidden; text-overflow:ellipsis</c> inside a panel
    /// 212 pixels wide, 22 of which go to the handle that opens and closes it. Measured in the real
    /// page: <b>182 pixels</b> are left, in Arial 13 weight 500 - the Italian title of 26/9
    /// ("Dispositivi WCS: li crea") took 160, the first draft ("Dispositivi WCS - solo disegno, non
    /// creano un dispositivo") took 347. The English one is shorter than either, so the measurement
    /// holds - but it is <c>DrawioShapesPanelTest</c> that really measures it, not this comment. Whoever looked would
    /// have read the ellipsis instead of the promise, with every test on the TEXT still green: that
    /// is why <c>DrawioShapesPanelTest</c> asks for the title to be <b>readable</b>, not just
    /// present.
    /// </para>
    /// <para>
    /// <b>The value is in English</b> since 27/9, like <see cref="EntryNote"/> and
    /// <see cref="SectionDescription"/>: those three are the text the OPERATOR reads, and the owner
    /// decided that the Editor speaks English (they were the last Italian of the draw.io page, with
    /// the panel's and the checks' own words). <c>DrawioLibraryTest</c> and
    /// <c>DrawioShapesPanelTest</c> hold the promise they carry.
    /// </para>
    /// </summary>
    public const string SectionTitle = "Create WCS devices";

    /// <summary>
    /// What every entry adds to the name of its own type. It is the second place where the fact
    /// arrives before the gesture: draw.io shows it on hovering the entry, that is in the very
    /// moment that precedes the drag.
    /// </summary>
    public const string EntryNote = " - creates the device";

    /// <summary>
    /// The line of explanation draw.io shows next to the section in the "More Shapes" window.
    /// </summary>
    public const string SectionDescription =
        "Drag an entry onto the canvas and the device is born on the plant, with a suggested name (the type plus the first free number): rename it and configure it in the panel next to the drawing.";

    /// <summary>
    /// The attribute in which a panel entry declares <b>which device type it is</b>: the registry's
    /// <see cref="DeviceTypeDto.NumericCode"/>, written as a plain number on the marked
    /// <c>&lt;object&gt;</c> next to <c>wcs="device"</c>.
    /// <para>
    /// <b>Explicit, and not worked out from the style or from the label.</b> Those two are the only
    /// other places a type could be read from, and both are the drawer's to change: draw.io lets a
    /// cell's style be edited (Edit Style) and its label retyped, so a type read from either would
    /// be a type the plant takes from whoever renames a shape. The style is also written in the
    /// image itself (<see cref="DrawioShape.DeviceStyle"/>), which has already cost one round of
    /// defects when the same fact lived in two places. An attribute of ours, like
    /// <see cref="DrawioDocument.PriorityAttribute"/>, travels through moves, copies and pastes and
    /// shows up in draw.io's own "Edit Data".
    /// </para>
    /// <para>
    /// The value is the numeric code and not <see cref="DeviceTypeDto.Code"/>, because that is what
    /// <c>PlantItemConfigDto.DeviceType</c> holds: one hop fewer between what the drawing says and
    /// what the plant is written with.
    /// </para>
    /// <para>
    /// <b>The bridge reads this name as a literal</b> (<c>drawio-bridge.js</c>, the additions
    /// listener), as it must - it is JavaScript inside draw.io, it does not see this file. Renaming
    /// the constant here without renaming it there breaks the drop in silence, which is why
    /// <c>DrawioLibraryTest</c> holds the exact spelling.
    /// </para>
    /// </summary>
    public const string TypeAttribute = "wcsType";

    /// <summary>
    /// The side of the box an entry is born with on the canvas, in the drawing's units.
    /// <b>Square</b>, and not any rectangle, because a type's shape is made of paths inside a square
    /// 0..100 box (<see cref="DeviceTypeDto.Shape"/>): with <c>imageAspect=0</c> any other ratio
    /// would distort it at the very moment of the drag.
    /// <para>
    /// <b>And since this batch it is also the size the device is BORN with on the plant</b>: the
    /// geometry written at the creation is the box draw.io gives the cell at the drop, and that is
    /// this one. As arbitrary as <c>PlantCanvas.DefaultCreatedItemSize</c> (100), which is the other
    /// door devices are created from, and declared for the same reason: neither number comes from
    /// the plant, and both are adjusted by dragging or from the panel.
    /// </para>
    /// <para>
    /// <b>The language of this file</b>, since the review of the creation batch asked for the choice
    /// to be made and declared: the documentation is ENGLISH, like the rest of
    /// <c>Wcs.WebEditor</c>'s. It used to be Italian with an English paragraph grafted on at the
    /// head, which is the one thing worse than either. The three constants the operator reads were
    /// translated on 27/9 - see <see cref="SectionTitle"/>.
    /// </para>
    /// </summary>
    public const double ThumbSize = 80;

    /// <summary>
    /// The section's JSON text, one entry per type of the registry, in the order the registry
    /// declares them.
    /// </summary>
    public static string Build(IReadOnlyList<DeviceTypeDto> types)
    {
        var entries = types.Select(Entry).ToArray();
        // Three titles, and they are the same one: draw.io shows the "libs" one above the panel's
        // entries, and the "entries" one and the section's in the "More Shapes" window. Saying
        // different things in the three places would mean that whoever arrives from the window
        // reads one notice and whoever arrives from the panel reads another.
        var section = new[]
        {
            new
            {
                title = new { main = SectionTitle },
                entries = new[]
                {
                    new
                    {
                        id = SectionId,
                        title = new { main = SectionTitle },
                        desc = new { main = SectionDescription },
                        libs = new[] { new { title = new { main = SectionTitle }, data = entries } },
                    },
                },
            },
        };
        return JsonSerializer.Serialize(section);
    }

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);

    /// <summary>
    /// One entry of the panel. <c>title</c> is what draw.io shows on hover; <c>xml</c> is the
    /// drawing the drag leaves on the canvas.
    /// </summary>
    private static object Entry(DeviceTypeDto type) => new
    {
        title = type.Label + EntryNote,
        w = ThumbSize,
        h = ThumbSize,
        xml = CellXml(type),
    };

    /// <summary>
    /// The cell the drag gives birth to: the <b>same</b> style <see cref="DrawioDocument"/> gives
    /// the real devices, because it is the same call (<see cref="DrawioShape.DeviceStyle"/>) and not
    /// a copy written twice - so what is dragged is identical to what is already on the canvas, and
    /// the promise does not depend on somebody remembering to change both. Plus the
    /// <c>wcs="device"</c> mark <b>with no id</b> and the type declared in
    /// <see cref="TypeAttribute"/>: see the comment at the head of the class.
    /// </summary>
    private static string CellXml(DeviceTypeDto type)
    {
        // No rotation: a panel entry does not stand in any plant yet. The thumbnail is square
        // (ThumbSize x ThumbSize), and on a square footprint a rigid part and an elastic one are
        // drawn alike: the style that comes out of here is the same, character for character, as
        // before rigid parts existed.
        var style = DrawioShape.DeviceStyle(type, ThumbSize, ThumbSize);
        // A whole <mxGraphModel>, and not the cell alone: it is the format draw.io decodes for a
        // library's entries (stringToCells), the same one a copied clipboard item has.
        return "<mxGraphModel><root><mxCell id=\"0\"/><mxCell id=\"1\" parent=\"0\"/>"
             + $"<object label=\"{E(type.Label)}\" wcs=\"device\" {TypeAttribute}=\"{type.NumericCode}\">"
             + $"<mxCell style=\"{E(style)}\" vertex=\"1\" parent=\"1\">"
             + $"<mxGeometry x=\"0\" y=\"0\" width=\"{N(ThumbSize)}\" height=\"{N(ThumbSize)}\" as=\"geometry\"/>"
             + "</mxCell></object></root></mxGraphModel>";
    }
}
