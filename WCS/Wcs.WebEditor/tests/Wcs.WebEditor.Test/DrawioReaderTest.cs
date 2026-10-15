using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawing;
using Wcs.WebEditor.Drawio;
using Wcs.WebEditor.Model;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

public sealed class DrawioReaderTest
{
    private const string Xml = """
        <mxGraphModel><root><mxCell id="0"/><mxCell id="1" parent="0"/>
        <object id="11111111-1111-1111-1111-111111111111" label="LF02.0001.0001" wcs="device"><mxCell style="shape=image;rotation=35;" vertex="1" parent="1"><mxGeometry x="100" y="200" width="130" height="184" as="geometry"/></mxCell></object>
        <object id="33333333-3333-3333-3333-333333333333" label="" wcs="junction"><mxCell style="exitX=0.96;exitY=0.5;entryX=0.04;entryY=0.5;" edge="1" parent="1" source="11111111-1111-1111-1111-111111111111" target="22222222-2222-2222-2222-222222222222"><mxGeometry relative="1" as="geometry"/></mxCell></object>
        <mxCell id="nota-1" value="Zona di carico" style="text;html=1;" vertex="1" parent="1"><mxGeometry x="900" y="900" width="320" height="40" as="geometry"/></mxCell>
        </root></mxGraphModel>
        """;

    [Fact]
    public void SplitsWhatIsOursFromWhatTheOperatorDrew()
    {
        var contents = DrawioReader.Read(Xml);

        Assert.Single(contents.Devices);
        Assert.Single(contents.Junctions);
        Assert.Contains("Zona di carico", contents.DecorationXml);
        // La decorazione NON deve portarsi dietro le nostre celle.
        Assert.DoesNotContain("wcs=", contents.DecorationXml);
    }

    /// <summary>
    /// Una cella marchiata che non riconosciamo non e' decorazione: e' un errore da dichiarare.
    /// Senza questa categoria finirebbe nel disegno libero portandosi dietro il proprio wcs=, e un
    /// dispositivo sparirebbe in silenzio dalle liste gestite. Il caso e' reale: draw.io lascia
    /// modificare l'XML a mano (Extras &gt; Edit Diagram), e una categoria futura aggiunta senza
    /// aggiornare il lettore darebbe lo stesso esito.
    /// </summary>
    [Theory]
    [InlineData("devise")]
    [InlineData("")]
    [InlineData("Device")]
    public void AMarkWeDoNotRecogniseIsDeclaredNotSwallowedIntoTheDecoration(string marchio)
    {
        var xml = $"""
            <mxGraphModel><root><mxCell id="0"/><mxCell id="1" parent="0"/>
            <object id="strano-1" label="" wcs="{marchio}"><mxCell style="shape=image;" vertex="1" parent="1"><mxGeometry x="1" y="2" width="3" height="4" as="geometry"/></mxCell></object>
            </root></mxGraphModel>
            """;

        var contents = DrawioReader.Read(xml);

        Assert.Equal("strano-1", Assert.Single(contents.Unknown).Id);
        Assert.Empty(contents.Devices);
        Assert.Empty(contents.Junctions);
        Assert.DoesNotContain("wcs=", contents.DecorationXml);
    }

    /// <summary>
    /// Il <c>parent</c> della cella, che prima di questo giro il lettore buttava via. Non e' un
    /// dato in piu': e' quello che dice <b>in che sistema di riferimento</b> siano i numeri qui
    /// sotto - sul livello predefinito sono quelli della tela, dentro un gruppo sono quelli del
    /// gruppo. Chi decide e' <c>DrawioDelta</c>; il lettore, come sempre, divide soltanto.
    /// </summary>
    [Fact]
    public void ExposesTheParentOfEveryCellItReads()
    {
        var contents = DrawioReader.Read(Xml);

        Assert.Equal(DrawioDocument.DefaultLayerId, contents.Devices.Single().Parent);
        Assert.Equal(DrawioDocument.DefaultLayerId, contents.Junctions.Single().Parent);

        var grouped = """
            <mxGraphModel><root><mxCell id="0"/><mxCell id="1" parent="0"/>
            <object id="11111111-1111-1111-1111-111111111111" label="A" wcs="device"><mxCell style="shape=image;" vertex="1" parent="gruppo-1"><mxGeometry width="120" height="60" as="geometry"/></mxCell></object>
            <mxCell id="gruppo-1" value="" style="group" vertex="1" connectable="0" parent="1"><mxGeometry x="390" y="290" width="518.58" height="309.61" as="geometry"/></mxCell>
            </root></mxGraphModel>
            """;

        // La forma che draw.io restituisce davvero, misurata il 2026-09-16: il primo figlio di un
        // gruppo non porta nemmeno x e y - e' a (0, 0) RELATIVO al gruppo. Letti come assoluti,
        // quegli zeri mandano il dispositivo sull'origine della tela: e' esattamente per questo che
        // il parent deve arrivare fino a chi decide.
        var cell = DrawioReader.Read(grouped).Devices.Single();
        Assert.Equal("gruppo-1", cell.Parent);
        Assert.Equal(0, cell.X);
        Assert.Equal(0, cell.Y);
    }

    [Fact]
    public void ReadsGeometryAndRotationOfADevice()
    {
        var device = DrawioReader.Read(Xml).Devices.Single();

        Assert.Equal("11111111-1111-1111-1111-111111111111", device.Id);
        Assert.Equal(100, device.X);
        Assert.Equal(200, device.Y);
        Assert.Equal(130, device.Width);
        Assert.Equal(184, device.Height);
        Assert.Equal(35, device.Rotation);
    }

    /// <summary>
    /// Decisione 3.3: una freccia e' una giunzione SOLO se aggancia una porta d'uscita a una
    /// d'entrata. Qui il lettore espone gli agganci; chi decide e' il compito 4.
    /// </summary>
    [Fact]
    public void ExposesThePortAnchorsOfAnEdge()
    {
        var junction = DrawioReader.Read(Xml).Junctions.Single();

        Assert.Equal("11111111-1111-1111-1111-111111111111", junction.Source);
        Assert.Equal("22222222-2222-2222-2222-222222222222", junction.Target);
        Assert.Contains("exitX=0.96", junction.Style);
        Assert.Contains("entryX=0.04", junction.Style);
    }

    /// <summary>
    /// Il giro completo sul formato vero: cio' che l'andata scrive, il ritorno lo rilegge identico.
    /// E' la rete che prende una divergenza fra i due lati del ponte, che nessuna delle due prove
    /// separate vedrebbe.
    /// <para>
    /// Adattata rispetto alla versione del brief: <c>DrawioDocument</c> non scrive piu' in
    /// mxGeometry l'inviluppo grezzo (item.X/Y/Width/Height, l'AABB della sagoma gia' ruotata), ma
    /// la dimensione PROPRIA del dispositivo (<see cref="NodePlacement.LocalSize"/>) centrata sul
    /// centro del mondo (<see cref="NodePlacement.WorldCenter"/>) - si veda il commento in testa a
    /// quella classe. Il giro completo deve quindi attendersi cio' che l'andata scrive davvero, non
    /// i valori grezzi del grafo di partenza: la geometria attesa qui e' ricalcolata con le stesse
    /// due funzioni, non ricopiata da X/Y/Width/Height dell'item.
    /// </para>
    /// </summary>
    [Fact]
    public void WhatTheBuilderWritesTheReaderReadsBack()
    {
        var itemId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var otherId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var junctionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var type = new DeviceTypeDto("automation", 0, "Automation", [new ShapePartDto("M 4 4 H 96 V 96 H 4 Z", "#334155", Filled: false, Rigid: false)],
            [new DevicePortDto("in", 4, 50, "in"), new DevicePortDto("out", 96, 50, "out")],
            new ShapePointDto(50, 50), new ShapePointDto(88, 60));
        var graph = new PlantGraph(
            [
                Item(itemId, "A", 10, 20, 130, 184, 35),
                Item(otherId, "B", 500, 20, 130, 184, -90),
            ],
            [new JunctionDto(junctionId, itemId, otherId, 99)],
            [type]);

        var contents = DrawioReader.Read(DrawioDocument.Build(graph, decorationXml: null));

        Assert.Equal(2, contents.Devices.Count);

        // Cio' che DrawioDocument scrive davvero per "A": dimensione propria a 35 gradi, riquadro
        // centrato sul centro del mondo - non i 130x184 e non il (10,20) grezzi dell'item.
        var (aWidth0, aHeight0) = NodePlacement.LocalSize(130, 184, 35);
        var (aCenterX, aCenterY) = NodePlacement.WorldCenter(10, 20, 130, 184);
        var a = contents.Devices.Single(d => d.Id == itemId.ToString());
        Assert.Equal(aCenterX - aWidth0 / 2, a.X, precision: 2);
        Assert.Equal(aCenterY - aHeight0 / 2, a.Y, precision: 2);
        Assert.Equal(aWidth0, a.Width, precision: 2);
        Assert.Equal(aHeight0, a.Height, precision: 2);
        Assert.Equal(35, a.Rotation);

        Assert.Equal(-90, contents.Devices.Single(d => d.Id == otherId.ToString()).Rotation);
        Assert.Equal(junctionId.ToString(), contents.Junctions.Single().Id);
    }

    /// <summary>
    /// The reader says <b>how many pages</b> the drawing that comes back carries, instead of taking
    /// the first one and keeping quiet. It is the half of the remedy that counts: the roads through
    /// draw.io's interface are shut in the bridge, but that block is our code running inside
    /// somebody else's application - it can fail to install, and a draw.io upgrade can open a road
    /// nobody has measured. A second page has to be <b>visible</b> wherever it came from, because
    /// the decoration is rewritten from what was read and everything on that page would be gone at
    /// the next opening.
    /// <para>
    /// The three shapes are the ones <b>measured</b> in a real browser on 2026-09-17, not invented:
    /// with a single page draw.io hands back <c>&lt;mxfile host="127.0.0.1"&gt;</c> with
    /// <b>one</b> <c>&lt;diagram name="Page-1"&gt;</c> inside it - not the naked
    /// <c>&lt;mxGraphModel&gt;</c>, which is what WE send on the way out; with two pages the same
    /// <c>&lt;mxfile&gt;</c> with <c>pages="2"</c> and two <c>&lt;diagram&gt;</c>, both of them
    /// uncompressed.
    /// </para>
    /// </summary>
    [Fact]
    public void SaysHowManyPagesTheDrawingCarriesInsteadOfTakingTheFirst()
    {
        // The naked <mxGraphModel>: no <diagram> at all, and that is one canvas that does not say so.
        Assert.Equal(0, DrawioReader.Read(Xml).Diagrams);

        var onePage = $"""<mxfile host="127.0.0.1"><diagram id="pag-1" name="Page-1">{Xml}</diagram></mxfile>""";
        Assert.Equal(1, DrawioReader.Read(onePage).Diagrams);

        const string SecondPage = """<diagram id="pag-2" name="Page-2"><mxGraphModel><root><mxCell id="0"/><mxCell id="1" parent="0"/><mxCell id="nota-2" value="Drawn on page two" style="text;html=1;" vertex="1" parent="1"><mxGeometry x="10" y="10" width="200" height="40" as="geometry"/></mxCell></root></mxGraphModel></diagram>""";
        var twoPages = $"""<mxfile host="127.0.0.1" pages="2"><diagram id="pag-1" name="Page-1">{Xml}</diagram>{SecondPage}</mxfile>""";

        var contents = DrawioReader.Read(twoPages);
        Assert.Equal(2, contents.Diagrams);

        // And what the reader reads does NOT change: it is still page one, exactly as before this
        // round. The reader does not decide - the save does, and it stops.
        Assert.Single(contents.Devices);
        Assert.Single(contents.Junctions);
        Assert.Contains("Zona di carico", contents.DecorationXml);
        Assert.DoesNotContain("Drawn on page two", contents.DecorationXml);
    }

    /// <summary>
    /// A drawing with no <c>&lt;root&gt;</c> at all - the empty return the reader already knew how
    /// to handle - must still say how many pages it had. Without this, the early way out would
    /// report zero pages for a document that has two, and the save would have nothing to refuse.
    /// </summary>
    [Fact]
    public void CountsThePagesEvenWhenThereIsNoRootToRead()
    {
        const string NoRoot = """<mxfile host="127.0.0.1" pages="2"><diagram id="a" name="Page-1"/><diagram id="b" name="Page-2"/></mxfile>""";

        var contents = DrawioReader.Read(NoRoot);

        Assert.Equal(2, contents.Diagrams);
        Assert.Empty(contents.Devices);
        Assert.Empty(contents.Junctions);
        Assert.Empty(contents.DecorationXml);
    }

    /// <summary>
    /// The mirroring, which until this round the reader threw away with the rest of the style: an
    /// operator mirrored a device and NOTHING of the product ever looked at the fact again. The
    /// reader does not judge it - the refusal is <see cref="DrawioDeltaTest"/>'s business - it only
    /// has to bring it out, and to bring out WHICH of the two, because one gesture and the other
    /// are not the same thing to whoever reads the refusal.
    /// </summary>
    [Theory]
    [InlineData("shape=image;rotation=35;", false, false)]
    [InlineData("shape=image;flipH=1;rotation=35;", true, false)]
    [InlineData("shape=image;flipV=1;rotation=35;", false, true)]
    [InlineData("shape=image;flipH=1;flipV=1;rotation=35;", true, true)]
    public void ReadsWhetherACellIsMirroredAndWhichWay(string style, bool flipH, bool flipV)
    {
        var xml = $"""
            <mxGraphModel><root><mxCell id="0"/><mxCell id="1" parent="0"/>
            <object id="11111111-1111-1111-1111-111111111111" label="A" wcs="device"><mxCell style="{style}" vertex="1" parent="1"><mxGeometry x="1" y="2" width="3" height="4" as="geometry"/></mxCell></object>
            </root></mxGraphModel>
            """;

        var cell = Assert.Single(DrawioReader.Read(xml).Devices);

        Assert.Equal(flipH, cell.FlipH);
        Assert.Equal(flipV, cell.FlipV);
        // The rest of the style keeps being read as before: a flag added to the reading must not
        // have cost the cell its rotation.
        Assert.Equal(35, cell.Rotation);
    }

    /// <summary>
    /// <c>flipH=0</c> is not a mirroring, and it is the case a substring search would get wrong -
    /// which is why the flag goes through the same number reading as the port anchors. A reader
    /// that called this cell mirrored would produce a refusal on a device nobody had touched, at
    /// every single save.
    /// </summary>
    [Fact]
    public void AFlipWrittenAsZeroIsNotAMirror()
    {
        var xml = """
            <mxGraphModel><root><mxCell id="0"/><mxCell id="1" parent="0"/>
            <object id="11111111-1111-1111-1111-111111111111" label="A" wcs="device"><mxCell style="shape=image;flipH=0;flipV=0;" vertex="1" parent="1"><mxGeometry x="1" y="2" width="3" height="4" as="geometry"/></mxCell></object>
            </root></mxGraphModel>
            """;

        var cell = Assert.Single(DrawioReader.Read(xml).Devices);

        Assert.False(cell.FlipH);
        Assert.False(cell.FlipV);
    }

    // ---- La freccia che l'operatore tira a mano -------------------------------------------------

    /// <summary>
    /// La misura che ha aperto questo lotto, nella sua forma piu' piccola: la cella di una freccia
    /// tirata con il mouse, com'e' tornata da un browser vero il 2026-09-19 - un
    /// <c>&lt;mxCell edge="1"&gt;</c> nudo, senza <c>&lt;object&gt;</c> e senza marchio. Se resta
    /// decorazione, un beta-tester non puo' collegare due dispositivi.
    /// </summary>
    [Fact]
    public void AnUnmarkedArrowBetweenTwoDeviceCellsIsReadAsAJunction()
    {
        var contents = DrawioReader.Read(TwoDevicesAnd(ArrowByHand("freccia-1", DeviceA, DeviceB)));

        var junction = Assert.Single(contents.Junctions);
        Assert.Equal("freccia-1", junction.Id);
        Assert.Equal(DeviceA, junction.Source);
        Assert.Equal(DeviceB, junction.Target);
        // Non deve stare anche nella decorazione: reinnestata tale e quale al prossimo
        // ricaricamento, la freccia comparirebbe DUE volte - quella dell'impianto e questa.
        Assert.DoesNotContain("freccia-1", contents.DecorationXml);
    }

    /// <summary>
    /// L'ordine del disegno non deve contare. draw.io scrive oggi le sagome prima delle frecce, e
    /// un lettore che ci si appoggiasse manderebbe la freccia in decorazione il giorno in cui
    /// smette - in silenzio, che e' il modo peggiore.
    /// </summary>
    [Fact]
    public void AnUnmarkedArrowIsReadAsAJunctionEvenWhenItComesBeforeTheDevicesItJoins()
    {
        var xml = $"""
            <mxGraphModel><root><mxCell id="0"/><mxCell id="1" parent="0"/>
            {ArrowByHand("freccia-1", DeviceA, DeviceB)}
            {Device(DeviceA)}
            {Device(DeviceB)}
            </root></mxGraphModel>
            """;

        Assert.Equal("freccia-1", Assert.Single(DrawioReader.Read(xml).Junctions).Id);
    }

    /// <summary>
    /// L'altra meta', e quella che una riparazione puo' rompere senza accorgersene: una freccia con
    /// un solo capo su un dispositivo - verso una nota, verso una forma libera - <b>non</b> e' una
    /// giunzione, e deve restare nel disegno dell'operatore. Le due direzioni si provano tutte e
    /// due: una regola scritta su un capo solo passerebbe meta' di questa prova.
    /// </summary>
    [Theory]
    [InlineData(DeviceA, "nota-1")]
    [InlineData("nota-1", DeviceA)]
    public void AnUnmarkedArrowWithOneEndOffTheDevicesStaysDecoration(string source, string target)
    {
        var xml = $"""
            <mxGraphModel><root><mxCell id="0"/><mxCell id="1" parent="0"/>
            {Device(DeviceA)}
            {Device(DeviceB)}
            <mxCell id="nota-1" value="Zona di carico" style="shape=note;" vertex="1" parent="1"><mxGeometry x="900" y="900" width="320" height="40" as="geometry"/></mxCell>
            {ArrowByHand("freccia-1", source, target)}
            </root></mxGraphModel>
            """;

        var contents = DrawioReader.Read(xml);

        Assert.Empty(contents.Junctions);
        Assert.Contains("freccia-1", contents.DecorationXml);
    }

    /// <summary>
    /// Due forme libere legate da una freccia: il disegno dell'operatore, che questa regola non
    /// deve sfiorare.
    /// </summary>
    [Fact]
    public void AnUnmarkedArrowBetweenTwoFreeShapesStaysDecoration()
    {
        var xml = $"""
            <mxGraphModel><root><mxCell id="0"/><mxCell id="1" parent="0"/>
            <mxCell id="nota-1" value="A" style="shape=note;" vertex="1" parent="1"><mxGeometry x="10" y="10" width="80" height="40" as="geometry"/></mxCell>
            <mxCell id="nota-2" value="B" style="shape=note;" vertex="1" parent="1"><mxGeometry x="300" y="10" width="80" height="40" as="geometry"/></mxCell>
            {ArrowByHand("freccia-1", "nota-1", "nota-2")}
            </root></mxGraphModel>
            """;

        var contents = DrawioReader.Read(xml);

        Assert.Empty(contents.Junctions);
        Assert.Contains("freccia-1", contents.DecorationXml);
    }

    /// <summary>
    /// Una freccia che torna sul dispositivo da cui parte unisce UN dispositivo, non due:
    /// sull'impianto non c'e' niente da scriverci. Resta decorazione, che e' cio' che e' oggi.
    /// </summary>
    [Fact]
    public void AnUnmarkedArrowBackOntoItsOwnDeviceStaysDecoration()
    {
        var contents = DrawioReader.Read(TwoDevicesAnd(ArrowByHand("freccia-1", DeviceA, DeviceA)));

        Assert.Empty(contents.Junctions);
        Assert.Contains("freccia-1", contents.DecorationXml);
    }

    /// <summary>
    /// Una freccia con un capo per aria - l'operatore l'ha lasciata a mezz'aria, o l'ha staccata -
    /// non collega niente e resta decorazione. E' anche la risposta a "e se poi la stacca?": la
    /// freccia esce dalle giunzioni da sola, senza che nessuno debba disfare un marchio.
    /// <para>
    /// <b>Ed e' anche registrata come tale.</b> Restare decorazione era gia' misurato, ma passava
    /// in silenzio: il fatto - un capo su un dispositivo, l'altro sul nulla - adesso arriva a chi
    /// puo' dirlo all'operatore. La cella resta comunque nella decorazione: chi legge l'elenco non
    /// deve reinnestarla, o sulla tela comparirebbe due volte.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnUnmarkedArrowWithAnEndInTheAirStaysDecoration(bool deviceIsTheSource)
    {
        var end = deviceIsTheSource ? $"source=\"{DeviceA}\"" : $"target=\"{DeviceA}\"";
        var xml = $"""
            <mxGraphModel><root><mxCell id="0"/><mxCell id="1" parent="0"/>
            {Device(DeviceA)}
            {Device(DeviceB)}
            <mxCell id="freccia-1" style="edgeStyle=orthogonalEdgeStyle;exitX=1;exitY=0.5;" edge="1" parent="1" {end}><mxGeometry relative="1" as="geometry"/></mxCell>
            </root></mxGraphModel>
            """;

        var contents = DrawioReader.Read(xml);

        Assert.Empty(contents.Junctions);
        Assert.Contains("freccia-1", contents.DecorationXml);
        Assert.Equal("freccia-1", Assert.Single(contents.ArrowsWithAnEndInTheAir).Id);
    }

    /// <summary>
    /// Le tre frecce che <b>non</b> sono un capo per aria, e che senza questa prova un elenco
    /// troppo largo raccoglierebbe: quella verso una nota (un disegno voluto), quella con tutti e
    /// due i capi nel vuoto (nessuno stava mirando a un dispositivo) e quella fra due dispositivi
    /// (e' una giunzione). Una riga in piu' a ogni salvataggio su un disegno che va benissimo
    /// insegna all'operatore a non leggere piu' il verbale, ed e' il modo in cui una voce
    /// utile diventa rumore.
    /// </summary>
    [Fact]
    public void OnlyAnArrowAimedAtADeviceAndMissedCountsAsAnEndInTheAir()
    {
        var xml = $"""
            <mxGraphModel><root><mxCell id="0"/><mxCell id="1" parent="0"/>
            {Device(DeviceA)}
            {Device(DeviceB)}
            <mxCell id="nota-1" value="Zona di carico" style="shape=note;" vertex="1" parent="1"><mxGeometry x="900" y="900" width="320" height="40" as="geometry"/></mxCell>
            {ArrowByHand("verso-la-nota", DeviceA, "nota-1")}
            <mxCell id="tutta-per-aria" style="edgeStyle=orthogonalEdgeStyle;" edge="1" parent="1"><mxGeometry relative="1" as="geometry"/></mxCell>
            {ArrowByHand("giunzione", DeviceA, DeviceB)}
            </root></mxGraphModel>
            """;

        Assert.Empty(DrawioReader.Read(xml).ArrowsWithAnEndInTheAir);
    }

    /// <summary>
    /// Una freccia marchiata che aggancia due celle NON marchiate resta una giunzione proposta: la
    /// regola nuova si aggiunge al marchio, non lo sostituisce. Senza questa prova, una riparazione
    /// che scambiasse le due strade passerebbe tutte le altre.
    /// </summary>
    [Fact]
    public void AMarkedArrowIsStillAJunctionWhateverItHangsFrom()
    {
        var xml = """
            <mxGraphModel><root><mxCell id="0"/><mxCell id="1" parent="0"/>
            <mxCell id="nota-1" value="A" style="shape=note;" vertex="1" parent="1"><mxGeometry x="10" y="10" width="80" height="40" as="geometry"/></mxCell>
            <mxCell id="nota-2" value="B" style="shape=note;" vertex="1" parent="1"><mxGeometry x="300" y="10" width="80" height="40" as="geometry"/></mxCell>
            <object id="33333333-3333-3333-3333-333333333333" label="" wcs="junction"><mxCell style="exitX=0.96;entryX=0.04;" edge="1" parent="1" source="nota-1" target="nota-2"><mxGeometry relative="1" as="geometry"/></mxCell></object>
            </root></mxGraphModel>
            """;

        Assert.Equal("33333333-3333-3333-3333-333333333333", Assert.Single(DrawioReader.Read(xml).Junctions).Id);
    }

    /// <summary>
    /// <b>A cell of OURS between two devices that does not point one way is not proposed as a
    /// junction either</b>: it goes where the unmarked lines of the same shape go, and what
    /// becomes of it is <c>DrawioDelta</c>'s - which alone knows whether the plant holds one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is where the owner's rule of 2026-09-19 lives now, and the two GRAVE findings of the
    /// second review are what moved it here. The head used to be asked of the unmarked cells in
    /// this file and of the marked ones in a single branch of <c>DrawioDelta</c> - the branch
    /// where the plant still holds the junction - so the same drawing got two different answers
    /// depending on what the plant happened to know. Asking it here asks it of both, once: whether
    /// a drawing says "connection" is a question about the drawing, and dividing the drawing up is
    /// this file's whole job.
    /// </para>
    /// <para>
    /// <b>Only when the two ends are devices</b>, because that is the whole of the rule: a marked
    /// arrow hanging from something else is refused by name further down for its ends, head or no
    /// head, and <see cref="AMarkedArrowIsStillAJunctionWhateverItHangsFrom"/> holds that road
    /// open.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("endArrow=none;")]
    [InlineData("endArrow=block;startArrow=block;")]
    public void ACellOfOursThatDoesNotPointOneWayIsNotProposedAsAJunction(string headStyle)
    {
        var xml = TwoDevicesAnd($"""<object id="33333333-3333-3333-3333-333333333333" label="" wcs="junction"><mxCell style="{headStyle}exitX=0.96;exitY=0.5;entryX=0.04;entryY=0.5;" edge="1" parent="1" source="{DeviceA}" target="{DeviceB}"><mxGeometry relative="1" as="geometry"/></mxCell></object>""");

        var contents = DrawioReader.Read(xml);

        Assert.Empty(contents.Junctions);
        Assert.Equal("33333333-3333-3333-3333-333333333333",
            Assert.Single(contents.UndirectedLinesBetweenDevices).Id);
        // NOT in the decoration, unlike its unmarked twin: the cell was ours, and whether the
        // plant is about to redraw it is a question this file cannot answer. Putting it in the
        // drawing here would give the canvas two copies of the same line every time the plant
        // keeps the junction.
        Assert.DoesNotContain("wcs=\"junction\"", contents.DecorationXml, StringComparison.Ordinal);
    }

    // ---- La punta: che cosa vuol dire un tratto fra due dispositivi ----------------------------

    /// <summary>
    /// Come si legge una punta, caso per caso. La riga che conta piu' di tutte e' la prima:
    /// <b>uno stile senza <c>endArrow</c> ha la punta</b>, perche' lo stile predefinito degli archi
    /// di draw.io porta <c>endArrow=classic</c> - e la cella che draw.io fabbrica per una freccia
    /// tirata con il mouse non scrive <c>endArrow</c> affatto. Una regola che pretendesse la chiave
    /// rifiuterebbe ogni freccia che un operatore disegnera' mai.
    /// </summary>
    [Theory]
    // Quello che il gesto produce davvero, misurato in un browser vero il 2026-09-19.
    [InlineData("edgeStyle=orthogonalEdgeStyle;rounded=0;html=1;exitX=1;exitY=0.5;", DrawioArrowHeads.AtEnd)]
    [InlineData("", DrawioArrowHeads.AtEnd)]
    [InlineData("endArrow=block;endFill=1;", DrawioArrowHeads.AtEnd)]
    [InlineData("endArrow=none;", DrawioArrowHeads.None)]
    [InlineData("endArrow=none;startArrow=none;", DrawioArrowHeads.None)]
    // Valore vuoto: draw.io non disegna nessun marcatore, e nemmeno noi lo leggiamo come punta.
    [InlineData("endArrow=;", DrawioArrowHeads.None)]
    [InlineData("endArrow=none;startArrow=classic;startFill=1;", DrawioArrowHeads.AtStart)]
    [InlineData("startArrow=block;", DrawioArrowHeads.Both)]
    [InlineData("endArrow=classic;startArrow=classic;", DrawioArrowHeads.Both)]
    // Una chiave che FINISCE per endArrow non e' endArrow: e' la stessa distinzione che gli
    // agganci delle porte pretendono da TryStyleNumber.
    [InlineData("myEndArrow=none;", DrawioArrowHeads.AtEnd)]
    // L'ultima occorrenza vince, come in mxGraph, che lo stile lo riduce a un dizionario.
    [InlineData("endArrow=none;endArrow=block;", DrawioArrowHeads.AtEnd)]
    [InlineData("endArrow=block;endArrow=none;", DrawioArrowHeads.None)]
    public void WhereTheHeadsOfALineAre(string style, DrawioArrowHeads expected) =>
        Assert.Equal(expected, DrawioReader.HeadsOf(style));

    /// <summary>
    /// La decisione del proprietario del 19/9, nella sua forma piu' piccola: <b>fra due dispositivi
    /// una linea senza punta non collega niente</b>. Non e' una giunzione, e - cosa che la regola
    /// troppo larga di prima non sapeva fare - <b>resta il disegno dell'operatore</b>, perche' non
    /// esce mai dalla decorazione.
    /// </summary>
    /// <remarks>
    /// Il fatto viene registrato a parte perche' qualcuno lo dica: la cella e' nella decorazione
    /// <b>e</b> nell'elenco, come per la freccia con un capo per aria, e chi legge l'elenco non
    /// deve reinnestarla.
    /// </remarks>
    [Fact]
    public void ALineWithNoHeadBetweenTwoDevicesIsNotAJunctionAndStaysInTheDrawing()
    {
        var contents = DrawioReader.Read(TwoDevicesAnd(
            LineByHand("tratto-1", DeviceA, DeviceB, "endArrow=none;")));

        Assert.Empty(contents.Junctions);
        Assert.Contains("tratto-1", contents.DecorationXml);
        Assert.Equal("tratto-1", Assert.Single(contents.UndirectedLinesBetweenDevices).Id);
    }

    /// <summary>
    /// L'altra meta' della decisione: la punta indica il verso, e draw.io la lascia mettere
    /// all'inizio. La giunzione proposta va <b>dalla coda alla punta</b>, cioe' al contrario degli
    /// attributi <c>source</c>/<c>target</c> della cella - che sono il verso in cui l'operatore ha
    /// trascinato, non quello che sta guardando.
    /// </summary>
    [Fact]
    public void AnArrowWhoseHeadIsAtTheStartIsAJunctionTheOtherWayRound()
    {
        var contents = DrawioReader.Read(TwoDevicesAnd(
            LineByHand("freccia-1", DeviceA, DeviceB, "endArrow=none;startArrow=classic;startFill=1;")));

        var junction = Assert.Single(contents.Junctions);
        Assert.Equal("freccia-1", junction.Id);
        Assert.Equal(DeviceB, junction.Source);
        Assert.Equal(DeviceA, junction.Target);
        Assert.DoesNotContain("freccia-1", contents.DecorationXml);
    }

    /// <summary>
    /// Due punte: l'operatore la legge come "nei due sensi", ma il proprietario ha detto che i due
    /// sensi sono <b>due</b> frecce. Quindi non si sceglie un verso a caso e non si crea niente: la
    /// linea resta un disegno e viene dichiarata.
    /// </summary>
    [Fact]
    public void ALineWithAHeadAtBothEndsIsNotAJunction()
    {
        var contents = DrawioReader.Read(TwoDevicesAnd(
            LineByHand("freccia-1", DeviceA, DeviceB, "endArrow=block;startArrow=block;")));

        Assert.Empty(contents.Junctions);
        Assert.Contains("freccia-1", contents.DecorationXml);
        Assert.Equal("freccia-1", Assert.Single(contents.UndirectedLinesBetweenDevices).Id);
    }

    /// <summary>
    /// Il richiamo alla didascalia, che e' la domanda che il codice stesso poneva: una linea
    /// <b>senza punta</b> tirata apposta da un dispositivo verso il vuoto non e' un collegamento
    /// mancato, e non deve essere segnalata a ogni salvataggio. Quella con la punta si'.
    /// </summary>
    [Fact]
    public void AHeadlessLineFromADeviceIntoEmptySpaceIsNotAnArrowThatMissed()
    {
        var xml = $"""
            <mxGraphModel><root><mxCell id="0"/><mxCell id="1" parent="0"/>
            {Device(DeviceA)}
            {Device(DeviceB)}
            <mxCell id="richiamo" style="endArrow=none;html=1;exitX=1;exitY=0.5;" edge="1" parent="1" source="{DeviceA}"><mxGeometry relative="1" as="geometry"/></mxCell>
            <mxCell id="mirata-e-mancata" style="html=1;exitX=1;exitY=0.5;" edge="1" parent="1" source="{DeviceB}"><mxGeometry relative="1" as="geometry"/></mxCell>
            </root></mxGraphModel>
            """;

        var contents = DrawioReader.Read(xml);

        Assert.Equal("mirata-e-mancata", Assert.Single(contents.ArrowsWithAnEndInTheAir).Id);
        Assert.Contains("richiamo", contents.DecorationXml);
    }

    /// <summary>
    /// L'elenco da dichiarare non deve allargarsi oltre i due dispositivi: fra due forme libere,
    /// e da un dispositivo a una nota, una linea senza punta e' un disegno e basta - la regola del
    /// proprietario parla di che cosa si puo' tirare <b>fra due dispositivi</b>. Una voce a ogni
    /// salvataggio su un disegno che va benissimo insegna a non leggere piu' il verbale.
    /// </summary>
    [Fact]
    public void ALineWithNoHeadThatDoesNotJoinTwoDevicesIsNotDeclaredAtAll()
    {
        var xml = $"""
            <mxGraphModel><root><mxCell id="0"/><mxCell id="1" parent="0"/>
            {Device(DeviceA)}
            {Device(DeviceB)}
            <mxCell id="nota-1" value="Zona di carico" style="shape=note;" vertex="1" parent="1"><mxGeometry x="900" y="900" width="320" height="40" as="geometry"/></mxCell>
            <mxCell id="nota-2" value="Uscita" style="shape=note;" vertex="1" parent="1"><mxGeometry x="900" y="980" width="320" height="40" as="geometry"/></mxCell>
            {LineByHand("fra-due-note", "nota-1", "nota-2", "endArrow=none;")}
            {LineByHand("verso-la-nota", DeviceA, "nota-1", "endArrow=none;")}
            {LineByHand("su-se-stesso", DeviceA, DeviceA, "endArrow=none;")}
            </root></mxGraphModel>
            """;

        var contents = DrawioReader.Read(xml);

        Assert.Empty(contents.UndirectedLinesBetweenDevices);
        Assert.Empty(contents.ArrowsWithAnEndInTheAir);
        Assert.Empty(contents.Junctions);
    }

    /// <summary>
    /// <b>The ends of a cell of OURS come back as they are written</b>, head at the start or not:
    /// which way a junction goes is the plant's, and only <c>DrawioDelta</c> - which knows the
    /// plant - turns it round. On an unmarked arrow the swap happens here instead
    /// (<see cref="AnArrowWhoseHeadIsAtTheStartIsAJunctionTheOtherWayRound"/>), and swapping in
    /// both places would put the ends back the way the drag went.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This test used to say something else: that a marked arrow <b>with no head</b> was still a
    /// junction proposed, for fear that letting it go would put the same arrow on the canvas
    /// twice. The two GRAVE findings of the second review are what that rule cost - a marked cell
    /// walked past the owner's rule of 2026-09-19 entirely, so the save after the one that
    /// deleted a junction wrote it straight back under a headless line.
    /// </para>
    /// <para>
    /// <b>The fear was right and it is answered where it belongs.</b> The cell does not go into
    /// this file's decoration (see
    /// <see cref="ACellOfOursThatDoesNotPointOneWayIsNotProposedAsAJunction"/>); it is
    /// <c>DrawioDelta</c> that hands it back, and only when the plant holds nothing to redraw it
    /// with. What survives of the old test is the half that has nothing to do with the head, and
    /// it is here.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheEndsOfAMarkedArrowAreNotSwappedByItsHead()
    {
        var xml = $"""
            <mxGraphModel><root><mxCell id="0"/><mxCell id="1" parent="0"/>
            {Device(DeviceA)}
            {Device(DeviceB)}
            <object id="33333333-3333-3333-3333-333333333333" label="" wcs="junction"><mxCell style="endArrow=none;startArrow=block;exitX=0.96;entryX=0.04;" edge="1" parent="1" source="{DeviceA}" target="{DeviceB}"><mxGeometry relative="1" as="geometry"/></mxCell></object>
            </root></mxGraphModel>
            """;

        var contents = DrawioReader.Read(xml);

        var junction = Assert.Single(contents.Junctions);
        Assert.Equal("33333333-3333-3333-3333-333333333333", junction.Id);
        Assert.Equal(DeviceA, junction.Source);
        Assert.Equal(DeviceB, junction.Target);
        Assert.Empty(contents.UndirectedLinesBetweenDevices);
        Assert.DoesNotContain("33333333", contents.DecorationXml);
    }

    private const string DeviceA = "11111111-1111-1111-1111-111111111111";
    private const string DeviceB = "22222222-2222-2222-2222-222222222222";

    /// <summary>
    /// La stessa cella di <see cref="ArrowByHand"/> con un pezzo di stile in piu' davanti: e' cosi'
    /// che draw.io scrive una punta diversa: <c>setCellStyles</c> mette la chiave nello stile della
    /// cella, quella che il gesto aveva lasciato senza.
    /// </summary>
    private static string LineByHand(string id, string source, string target, string headStyle) =>
        $"""<mxCell id="{id}" style="{headStyle}edgeStyle=orthogonalEdgeStyle;rounded=0;html=1;exitX=1;exitY=0.5;exitDx=0;exitDy=0;entryX=0;entryY=0.5;entryDx=0;entryDy=0;" edge="1" parent="1" source="{source}" target="{target}"><mxGeometry relative="1" as="geometry"/></mxCell>""";

    /// <summary>
    /// La cella di una freccia come la fabbrica draw.io quando l'operatore la tira: nessun
    /// <c>&lt;object&gt;</c>, nessun marchio, e nello stile i due agganci che il gesto le da'.
    /// Copiata da una misura in un browser vero, non inventata.
    /// </summary>
    private static string ArrowByHand(string id, string source, string? target = null) =>
        $"""<mxCell id="{id}" style="edgeStyle=orthogonalEdgeStyle;rounded=0;html=1;exitX=1;exitY=0.5;exitDx=0;exitDy=0;entryX=0;entryY=0.5;entryDx=0;entryDy=0;" edge="1" parent="1" source="{source}"{(target is null ? "" : $" target=\"{target}\"")}><mxGeometry relative="1" as="geometry"/></mxCell>""";

    private static string Device(string id) =>
        $"""<object id="{id}" label="D" wcs="device"><mxCell style="shape=image;" vertex="1" parent="1"><mxGeometry x="10" y="10" width="120" height="60" as="geometry"/></mxCell></object>""";

    private static string TwoDevicesAnd(string cell) =>
        $"""
        <mxGraphModel><root><mxCell id="0"/><mxCell id="1" parent="0"/>
        {Device(DeviceA)}
        {Device(DeviceB)}
        {cell}
        </root></mxGraphModel>
        """;

    // Stesso schema di DrawioDocumentTest.Item: PlantItemDto e' un record posizionale senza
    // costruttore vuoto. Solo geometria e rotazione contano per queste prove.
    private static PlantItemDto Item(Guid id, string name, double x, double y, double w, double h, double rot) =>
        new(id, name, DeviceTypes.Automation, null, null, null, default, default, 0, null, 0, 0, 0, 0, default!,
            x, y, w, h, rot);
}
