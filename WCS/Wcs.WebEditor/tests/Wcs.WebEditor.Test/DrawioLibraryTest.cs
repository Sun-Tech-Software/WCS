using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawio;
using Wcs.WebEditor.Model;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// La sezione dei dispositivi nel pannello delle forme di draw.io: che sia <b>generata dal
/// registro</b>, che porti la stessa sagoma dei dispositivi veri, e che dica da se' - prima del
/// gesto - che da li' non nasce nessun dispositivo.
/// </summary>
/// <remarks>
/// Nessuna prova qui elenca i sette tipi di oggi: ognuna cicla sul registro che le si e' dato. Un
/// sette scritto a mano renderebbe rossa la prova il giorno che si aggiunge un tipo, che e'
/// esattamente il giorno in cui il pannello deve aggiornarsi da solo.
/// </remarks>
public sealed class DrawioLibraryTest
{
    /// <summary>
    /// Un registro di tipi diversi l'uno dall'altro <b>nella sagoma e nel colore</b>: sono i due
    /// campi da cui <see cref="DrawioShape.ImageStyle"/> ricava l'immagine, quindi due tipi che li
    /// condividessero darebbero lo stesso stile e una voce scambiata con l'altra non si vedrebbe.
    /// <para>
    /// Every type carries an elastic body and a second, RIGID part, like the seven real ones:
    /// without a rigid part the image would not depend on the footprint at all, and the comparison
    /// between the thumbnail (square) and the real device (which is not) would fall back to what it
    /// was before - green either way, and blind on exactly the new thing.
    /// </para>
    /// </summary>
    private static IReadOnlyList<DeviceTypeDto> Registry() =>
    [
        new("automation", 0, "Automation",
            [new ShapePartDto("M 4 4 H 96 V 96 H 4 Z", "#334155", Filled: false, Rigid: false),
             new ShapePartDto("M 30 30 H 70 V 70 H 30 Z", "#334155", Filled: true, Rigid: true)], [],
            new ShapePointDto(50, 50), new ShapePointDto(88, 60)),
        new("loading", 1, "Loading",
            [new ShapePartDto("M 10 10 L 90 50 L 10 90 Z", "#b91c1c", Filled: false, Rigid: false),
             new ShapePartDto("M 35 35 H 65 V 65 H 35 Z", "#b91c1c", Filled: true, Rigid: true)], [],
            new ShapePointDto(50, 50), new ShapePointDto(88, 60)),
        new("buffer", 2, "Buffer",
            [new ShapePartDto("M 4 30 H 96 V 70 H 4 Z", "#0f766e", Filled: false, Rigid: false),
             new ShapePartDto("M 40 40 H 60 V 60 H 40 Z", "#0f766e", Filled: true, Rigid: true)], [],
            new ShapePointDto(50, 50), new ShapePointDto(88, 60)),
    ];

    /// <summary>Le voci della sezione, cosi' come draw.io le trovera' nella configurazione.</summary>
    private static IReadOnlyList<JsonElement> EntriesOf(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        var data = parsed.RootElement[0].GetProperty("entries")[0].GetProperty("libs")[0].GetProperty("data");
        // Clonate: JsonDocument e' usa-e-getta, e gli elementi muoiono con lui.
        return data.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    /// <summary>L'&lt;object&gt; di una voce: e' la cella che il trascinamento deposita sulla tela.</summary>
    private static XElement CellOf(JsonElement entry) =>
        XDocument.Parse(entry.GetProperty("xml").GetString()!)
            .Descendants("object").Single();

    /// <summary>Lo stile di una cella, letto dall'&lt;object&gt; che la porta.</summary>
    private static string StyleOf(XElement cell) => cell.Element("mxCell")!.Attribute("style")!.Value;

    /// <summary>La rotazione con cui i dispositivi veri di <see cref="DrawnDeviceStyles"/> stanno nell'impianto.</summary>
    private const double Rotation = 35;

    /// <summary>
    /// The box (the already rotated AABB, the way the graph carries it) of a device whose OWN
    /// dimensions are 122x191 at <see cref="Rotation"/> degrees - the footprint of 43 of the 51
    /// devices of the real plant. A non-square footprint is what it takes for a rigid part to show
    /// at all: on a square one rigid and elastic coincide.
    /// </summary>
    private const double RectangularBoxWidth = 209.5;
    private const double RectangularBoxHeight = 226.4;

    /// <summary>
    /// The same with SQUARE own dimensions: a square AABB stays square through the inversion of the
    /// rotation whatever the angle (the two formulas of <c>NodePlacement.LocalSize</c> collapse into
    /// the same expression), so here the two renderings of a rigid part have to come out equal
    /// character for character.
    /// </summary>
    private const double SquareBoxSide = 209;

    /// <summary>
    /// The style <see cref="DrawioDocument"/> gives the <b>real</b> devices of those types, one per
    /// type and in the same order: it is the other end of the comparison, the one whoever is
    /// drawing already sees on the canvas when he drags an entry out of the panel.
    /// </summary>
    private static IReadOnlyList<string> DrawnDeviceStyles(IReadOnlyList<DeviceTypeDto> types, double width, double height)
    {
        var items = types
            .Select((t, i) => new PlantItemDto(
                Guid.NewGuid(), $"dispositivo-{i}", (DeviceTypes)t.NumericCode, null, null, null,
                default, default, 0, null, 0, 0, 0, 0, default!, 100 * i, 200, width, height, Rotation))
            .ToList();

        var xml = DrawioDocument.Build(new PlantGraph(items, [], types), decorationXml: null);
        return XDocument.Parse(xml).Descendants("object")
            .Where(o => o.Attribute("wcs")?.Value == "device")
            .Select(StyleOf)
            .ToList();
    }

    private const string ImagePrefix = "image=data:image/svg+xml,";

    /// <summary>Where the image payload sits inside a style: the one piece the footprint can change.</summary>
    private static Range Payload(string style)
    {
        var start = style.IndexOf(ImagePrefix, StringComparison.Ordinal);
        Assert.True(start >= 0, $"the style carries no image at all: {style}");
        start += ImagePrefix.Length;
        return start..style.IndexOf(';', start);
    }

    /// <summary>The image inside a style, decoded.</summary>
    private static string SvgOf(string style) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(style[Payload(style)]));

    /// <summary>The style without the image: everything the footprint cannot touch.</summary>
    private static string WithoutTheImage(string style)
    {
        var payload = Payload(style).GetOffsetAndLength(style.Length);
        return style.Remove(payload.Offset, payload.Length);
    }

    /// <summary>
    /// The same image with the rigid parts' fit to the footprint taken out: once it is gone, the
    /// panel thumbnail and the device on the canvas have to become the same thing again.
    /// </summary>
    private static string WithoutTheRigidFit(string svg) => Regex.Replace(svg, " transform=\"[^\"]*\"", "");

    [Fact]
    public void OneEntryPerTypeOfTheRegistryInTheSameOrder()
    {
        var types = Registry();

        var entries = EntriesOf(DrawioLibrary.Build(types));

        Assert.Equal(types.Count, entries.Count);
        for (var i = 0; i < types.Count; i++)
            Assert.Equal(types[i].Label + DrawioLibrary.EntryNote, entries[i].GetProperty("title").GetString());
    }

    /// <summary>
    /// Il numero dei tipi non e' scritto da nessuna parte nel codice: un registro vuoto da una
    /// sezione vuota, non le sette voci di oggi.
    /// </summary>
    [Fact]
    public void AnEmptyRegistryGivesAnEmptySection()
    {
        Assert.Empty(EntriesOf(DrawioLibrary.Build([])));
    }

    /// <summary>
    /// Lo stile di ogni voce del pannello e' <b>quello intero</b> di un dispositivo vero di quel
    /// tipo, e la rotazione e' l'unica differenza ammessa: una voce del pannello non sta ancora in
    /// nessun impianto, quindi non ne ha una.
    /// <para>
    /// I due capi del confronto sono i <b>due produttori veri</b> - <see cref="DrawioLibrary"/> e
    /// <see cref="DrawioDocument"/> - chiamati tutti e due qui, e non una stringa riscritta nella
    /// prova: e' la promessa del commento in testa a <c>DrawioLibrary.CellXml</c> ("la stessa
    /// sagoma che DrawioDocument da ai dispositivi veri"), ed e' l'unico modo di sorvegliarla che
    /// non sia cieco per costruzione. Prima di questa stesura la prova guardava il <b>solo</b>
    /// pezzo dell'immagine: misurato, <c>fontSize=9</c> cambiato in <c>fontSize=12</c> in uno dei
    /// due produttori lasciava la suite verde, 753 su 753, mentre la miniatura del pannello e il
    /// dispositivo sulla tela divergevano in silenzio.
    /// </para>
    /// <para>
    /// <b>Since a part can be rigid, "the same style" has two measures</b>, and both of them are
    /// here. On a SQUARE footprint the promise is the one it always was, unchanged: the same string
    /// character for character, rotation aside. On a rectangular footprint it is not, and it cannot
    /// be: draw.io stretches the image onto the cell, so putting the gear back in proportion means
    /// composing a DIFFERENT image for a different footprint - the panel thumbnail is square and the
    /// real device is not. What stays equal, and what this test goes on watching, is <b>everything
    /// else</b>: take the fit to the footprint out and the two images are identical again, so a
    /// <c>fontSize</c>, a colour or a path that changed in one producer only is still caught.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryEntryCarriesTheSameStyleAsTheRealDevicesOfThatType()
    {
        var types = Registry();

        var entries = EntriesOf(DrawioLibrary.Build(types));
        var drawnOnASquare = DrawnDeviceStyles(types, SquareBoxSide, SquareBoxSide);
        var drawnOnTheRealBox = DrawnDeviceStyles(types, RectangularBoxWidth, RectangularBoxHeight);

        Assert.NotEmpty(types);
        Assert.Equal(types.Count, entries.Count);
        Assert.Equal(types.Count, drawnOnASquare.Count);
        Assert.Equal(types.Count, drawnOnTheRealBox.Count);
        for (var i = 0; i < types.Count; i++)
        {
            var entryStyle = StyleOf(CellOf(entries[i]));

            // I due capi si confrontano fra loro, quindi si muovono INSIEME: se la sagoma sparisse
            // da tutti e due resterebbero uguali, e la prova non se ne accorgerebbe - misurato,
            // togliendo "shape=image" dal produttore unico la suite resta verde. Serve percio' un
            // ancoraggio ESTERNO alla coppia, e l'unico che conta e' l'immagine: senza "image="
            // draw.io disegna il proprio ripiego (un rettangolo) senza dire niente, che e' il
            // difetto contro cui esiste ShapeTravelsAsBase64RightAfterTheCommaWithoutTheBase64Marker.
            Assert.Contains(DrawioShape.ImageStyle(types[i], DrawioLibrary.ThumbSize, DrawioLibrary.ThumbSize), entryStyle);

            // The thumbnail is square: against a device whose own footprint is square too, the
            // promise from before rigid parts existed still holds whole.
            Assert.Equal(entryStyle + $"rotation={Rotation};", drawnOnASquare[i]);

            // On the real footprint ONLY the rigid parts' fit changes. The "different" is part of
            // the assertion: if one day the shape stopped fitting itself to the footprint, the two
            // would become equal again and this line would say so, instead of letting an oval gear
            // through.
            var drawnStyle = drawnOnTheRealBox[i];
            Assert.NotEqual(entryStyle + $"rotation={Rotation};", drawnStyle);
            Assert.Equal(
                WithoutTheRigidFit(SvgOf(entryStyle)),
                WithoutTheRigidFit(SvgOf(drawnStyle)));
            Assert.Equal(
                WithoutTheImage(entryStyle),
                WithoutTheImage(drawnStyle).Replace($"rotation={Rotation};", string.Empty));
        }
    }

    [Fact]
    public void EveryEntryIsMarkedAsADevice()
    {
        var entries = EntriesOf(DrawioLibrary.Build(Registry()));

        Assert.NotEmpty(entries);
        foreach (var entry in entries)
            Assert.Equal("device", CellOf(entry).Attribute("wcs")?.Value);
    }

    /// <summary>
    /// Ogni voce dichiara il proprio TIPO in un attributo nostro, con il codice numerico del
    /// registro: e' l'unico dato da cui la pagina ricava che dispositivo creare al rilascio.
    /// <para>
    /// La prova tiene <b>la grafia esatta dell'attributo</b> e non solo la costante, ed e' il punto:
    /// <c>drawio-bridge.js</c> quella grafia la porta scritta a mano - e' JavaScript dentro draw.io,
    /// il lato C# non lo vede - quindi rinominare la costante qui senza rinominarla la' spegnerebbe
    /// ogni creazione in silenzio. Questa riga non lo impedisce (nessuna prova unitaria esegue quel
    /// file), ma lo rende una modifica che si vede: chi cambia la costante trova questa prova rossa
    /// e va a cercare l'altra meta'. Chi misura davvero i due lati insieme e' la prova nel browser
    /// che trascina sul serio.
    /// </para>
    /// <para>
    /// E il codice e' quello NUMERICO, non <c>Code</c>: e' quello che porta
    /// <c>PlantItemConfigDto.DeviceType</c>, cioe' un passaggio in meno fra quello che il disegno
    /// dichiara e quello con cui l'impianto viene scritto.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryEntryDeclaresItsDeviceType()
    {
        var types = Registry();
        var entries = EntriesOf(DrawioLibrary.Build(types));

        Assert.Equal("wcsType", DrawioLibrary.TypeAttribute);
        Assert.NotEmpty(entries);
        Assert.Equal(types.Count, entries.Count);
        for (var i = 0; i < types.Count; i++)
        {
            var declared = CellOf(entries[i]).Attribute(DrawioLibrary.TypeAttribute)?.Value;
            Assert.Equal(types[i].NumericCode.ToString(), declared);
        }

        // E i codici del registro non sono tutti zero: senza questa riga l'uguaglianza qui sopra
        // sarebbe vera anche se l'attributo portasse sempre lo stesso numero, e la prova non
        // direbbe piu' che ogni voce dichiara il PROPRIO tipo.
        Assert.True(types.Select(t => t.NumericCode).Distinct().Count() > 1,
            "il registro di prova ha un solo codice numerico: questa prova non distinguerebbe un attributo giusto da uno costante.");
    }

    /// <summary>
    /// Nessuna voce porta un identificativo nostro, ed e' <b>cio' che rende la mappa di sessione
    /// necessaria</b>: la cella che il trascinamento deposita porta l'identificativo che draw.io le
    /// da, e il dispositivo nato al rilascio porta quello che l'host gli assegna. La pagina tiene la
    /// coppia (<c>DrawioEditor._plantItemByDrawnCell</c>) invece di riscrivere la cella.
    /// </summary>
    [Fact]
    public void NoEntryCarriesAnIdentifierOfOurs()
    {
        var entries = EntriesOf(DrawioLibrary.Build(Registry()));

        Assert.NotEmpty(entries);
        foreach (var entry in entries)
        {
            var cell = CellOf(entry);
            Assert.Null(cell.Attribute("id"));
            Assert.Null(cell.Element("mxCell")!.Attribute("id"));
        }
    }

    /// <summary>
    /// Il titolo della sezione e' il primo posto in cui chi trascina scopre, <b>prima del gesto</b>,
    /// che cosa il gesto fa: da questo lotto il dispositivo nasce davvero, e il titolo lo dice. Se
    /// un domani lo si accorcia a "WCS devices" la promessa cade in silenzio, e questa prova
    /// esiste per non lasciarlo succedere. I tre titoli che draw.io mostra - pannello, elenco,
    /// sezione - sono lo stesso.
    /// <para>
    /// <b>E i due testi non devono mandare a <c>/editor</c></b>, che si sta smontando: la prova lo
    /// chiede esplicitamente, perche' era esattamente quello che dicevano prima e una promessa che
    /// indica una porta murata e' peggio di nessuna promessa.
    /// </para>
    /// <para>
    /// Che il titolo <b>ci sia</b> lo dice questa prova; che si <b>legga per intero</b> nel
    /// pannello, che e' stretto e taglia con i puntini, lo dice <c>DrawioShapesPanelTest</c> in un
    /// browser vero. Le due domande sono diverse, e la seconda e' quella che qui non si puo' fare.
    /// </para>
    /// <para>
    /// La nota di ogni voce (<see cref="DrawioLibrary.EntryNote"/>) e' <b>l'altra meta'</b> della
    /// stessa promessa: draw.io la mostra passando il mouse sopra la voce, cioe' nel momento che
    /// precede il trascinamento. Fino a questa stesura non era sorvegliata da niente - misurato,
    /// azzerandola l'intera suite restava verde: <see cref="OneEntryPerTypeOfTheRegistryInTheSameOrder"/>
    /// ricava l'atteso dalla stessa costante, quindi una nota vuota le va bene, e nessuna prova nel
    /// browser legge un fumetto.
    /// </para>
    /// </summary>
    [Fact]
    public void TheTitleAndTheEntryNoteSayThatTheDeviceIsCreatedFromHere()
    {
        using var parsed = JsonDocument.Parse(DrawioLibrary.Build(Registry()));
        var section = parsed.RootElement[0];
        var entry = section.GetProperty("entries")[0];

        Assert.Contains("Create", DrawioLibrary.SectionTitle);
        Assert.DoesNotContain("not", DrawioLibrary.SectionTitle);
        Assert.Contains("creates the device", DrawioLibrary.EntryNote);
        // Nessuno dei tre testi manda piu' all'Editor condannato.
        Assert.DoesNotContain("/editor", DrawioLibrary.SectionTitle);
        Assert.DoesNotContain("/editor", DrawioLibrary.EntryNote);
        Assert.DoesNotContain("/editor", DrawioLibrary.SectionDescription);
        Assert.Equal(DrawioLibrary.SectionTitle, section.GetProperty("title").GetProperty("main").GetString());
        Assert.Equal(DrawioLibrary.SectionTitle, entry.GetProperty("title").GetProperty("main").GetString());
        Assert.Equal(DrawioLibrary.SectionTitle,
            entry.GetProperty("libs")[0].GetProperty("title").GetProperty("main").GetString());
        Assert.Equal(DrawioLibrary.SectionId, entry.GetProperty("id").GetString());
    }
}
