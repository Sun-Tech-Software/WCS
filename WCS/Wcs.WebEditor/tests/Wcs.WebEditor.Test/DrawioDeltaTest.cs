using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawing;
using Wcs.WebEditor.Drawio;
using Wcs.WebEditor.Editing;
using Wcs.WebEditor.Model;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// Il delta e la guardia anti-deriva: che cosa si scrive sull'impianto e che cosa si rifiuta.
/// </summary>
/// <remarks>
/// Due adattamenti rispetto alla versione del brief, entrambi dichiarati nel rapporto del compito 4.
/// <para>
/// Primo: <c>PlantItemDto</c> e' un record posizionale senza costruttore vuoto, quindi l'aiutante
/// <c>Item</c> lo costruisce per posizione come fanno <see cref="DrawioDocumentTest"/> e
/// <see cref="DrawioReaderTest"/>, non con un inizializzatore di oggetto.
/// </para>
/// <para>
/// Secondo: i confronti sui numeri del giro completo hanno una precisione. Il disegno che va e
/// torna passa da <c>DrawioDocument</c>, che scrive le coordinate a due decimali ("0.##"): un
/// dispositivo riportato nello spazio dell'impianto torna a 99.0027, non a 99 esatto. I valori
/// attesi restano quelli del brief - 99, 77, 130, 184 - cambia solo il confronto, che e' a due
/// decimali invece che sul bit.
/// </para>
/// </remarks>
public sealed class DrawioDeltaTest
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid J = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid C = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static DeviceTypeDto Type() => new("automation", 0, "Automation", [new ShapePartDto("M 4 4 H 96 V 96 H 4 Z", "#334155", Filled: false, Rigid: false)],
        [new DevicePortDto("in", 4, 50, "in"), new DevicePortDto("out", 96, 50, "out")],
        new ShapePointDto(50, 50), new ShapePointDto(88, 60));

    // Stesso schema di DrawioDocumentTest.Item: record posizionale, solo geometria e rotazione
    // contano per queste prove. La rotazione di 35 gradi non e' decorativa: e' l'angolo che rende
    // il riquadro dell'impianto (l'AABB della sagoma gia' ruotata) diverso dal riquadro del
    // disegno, e quindi l'angolo su cui un delta che confronta i numeri grezzi sbaglia.
    private static PlantItemDto Item(Guid id, double x, double y, double rotation = 35,
        double width = 130, double height = 184) =>
        new(id, id.ToString()[..4], DeviceTypes.Automation, null, null, null, default, default, 0, null,
            0, 0, 0, 0, default!, x, y, width, height, rotation);

    /// <summary>
    /// La mappa delle giunzioni create nella sessione, vuota: in queste prove il disegno arriva da
    /// <see cref="DrawioDocument.Build"/>, che scrive ogni giunzione con il proprio
    /// <c>JunctionId</c>, quindi non c'e' nessuna freccia che porti ancora un identificativo di
    /// draw.io. Le due prove che la mappa la usano davvero se la costruiscono.
    /// </summary>
    private static readonly Dictionary<string, Guid> NothingCreatedInThisSession = [];

    /// <summary>
    /// La mappa dei DISPOSITIVI nati da un rilascio in questa sessione, vuota per la stessa ragione
    /// della precedente: il disegno di queste prove viene da <see cref="DrawioDocument.Build"/>, che
    /// scrive ogni dispositivo con il proprio <c>ItemId</c>. Le prove del rilascio se la
    /// costruiscono.
    /// </summary>
    private static readonly Dictionary<string, Guid> NoDeviceDroppedInThisSession = [];

    private static PlantGraph Graph(params PlantItemDto[] items) => new(items, [], [Type()]);

    private static PlantGraph Graph(PlantItemDto[] items, JunctionDto[] junctions) =>
        new(items, junctions, [Type()]);

    /// <summary>
    /// Review finding 2: the "before" value a junction is drawn with, computed by calling the SAME
    /// rule the product uses (<see cref="JunctionWeights.FromPriority"/>) instead of reimplementing
    /// it inline in the test. A reimplementation is blind by construction the day it disagrees with
    /// the real rule: on an in-between priority the search string built from the wrong rule would
    /// not be in the drawing at all, <c>Replace</c> would match nothing, and the test would fail for
    /// a reason that has nothing to do with what it claims to measure - or, worse, pass on a
    /// substitution that never happened.
    /// </summary>
    private static string WeightText(int priority) =>
        JunctionWeights.FromPriority(priority) == JunctionWeight.High
            ? DrawioDocument.HighPriorityValue
            : DrawioDocument.LowPriorityValue;

    [Fact]
    public void OnlyTheDevicesTheOperatorMovedAreWritten()
    {
        var opened = Graph(Item(A, 10, 20), Item(B, 500, 20));
        var current = Graph(Item(A, 10, 20), Item(B, 500, 20));
        // L'operatore ha spostato solo A.
        var drawn = DrawioReader.Read(DrawioDocument.Build(Graph(Item(A, 99, 77), Item(B, 500, 20)), null));

        var delta = DrawioDelta.Compute(drawn, opened, current, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        var move = Assert.Single(delta.Moves);
        Assert.Equal(A, move.ItemId);
        Assert.Equal(99, move.X!.Value, precision: 2);
        Assert.Empty(delta.Refusals);
    }

    /// <summary>
    /// Il punto 2 del compito per il gesto piu' comune, lo SPOSTAMENTO: la dimensione propria e la
    /// rotazione sono quelle che avevamo scritto, quindi viaggia solo il centro e la dimensione
    /// dell'impianto si ricopia tale e quale. <c>DrawioDocument</c> scrive in mxGeometry la
    /// dimensione PROPRIA del dispositivo (<see cref="NodePlacement.LocalSize"/>) centrata sul
    /// centro del mondo, perche' draw.io ruota da se'; l'impianto memorizza invece l'inviluppo della
    /// sagoma gia' ruotata. A 35 gradi i due riquadri non si somigliano nemmeno: 130x184
    /// nell'impianto, meno di 3 unita' di larghezza nel disegno. Un delta che riportasse i numeri
    /// letti cosi' come sono schiaccerebbe ogni dispositivo ruotato al primo salvataggio - senza
    /// errori, senza rifiuti, senza che nessuno se ne accorga fino al disegno dopo. (Il ramo che
    /// RICALCOLA l'inviluppo, l'altro mezzo punto 2, e' provato da
    /// <see cref="AResizedDeviceGetsThePlantEnvelopeOfItsNewOwnSize"/>: questa prova non ci passa.)
    /// </summary>
    [Fact]
    public void AMovedDeviceKeepsThePlantSizeAndOnlyItsCentreTravels()
    {
        var opened = Graph(Item(A, 10, 20));
        var drawn = DrawioReader.Read(DrawioDocument.Build(Graph(Item(A, 99, 77)), null));

        var geometry = Assert.Single(DrawioDelta.Compute(drawn, opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession).Moves);

        Assert.Equal(99, geometry.X!.Value, precision: 2);
        Assert.Equal(77, geometry.Y!.Value, precision: 2);
        // Le dimensioni dell'impianto tornano intatte: il dispositivo e' stato spostato, non
        // ridimensionato. Col riquadro del disegno preso cosi' com'e' qui comparirebbe 2.78x222.67.
        Assert.Equal(130, geometry.Width!.Value, precision: 2);
        Assert.Equal(184, geometry.Height!.Value, precision: 2);
        Assert.Equal(35, geometry.Rotation!.Value, precision: 2);
    }

    /// <summary>
    /// Il ramo piu' pericoloso del file, e fino alla revisione l'unico senza una prova: quello in
    /// cui la geometria dell'impianto non si ricopia ma si RICALCOLA dal disegno
    /// (<see cref="NodePlacement.EnvelopeBox"/>). E' la scrittura che puo' deformare un dispositivo,
    /// e va provata a una rotazione non degenere, dove inviluppo e dimensione propria sono davvero
    /// due numeri diversi.
    /// <para>
    /// Il disegno qui non puo' venire da <c>DrawioDocument.Build</c> - Build sa scrivere solo cio'
    /// che l'impianto ha gia' - quindi il gesto dell'operatore (ridimensionare a 40x60 E ruotare a
    /// 35 gradi un dispositivo che era dritto e 130x184) si scrive nella forma in cui draw.io
    /// restituisce quel gesto: il riquadro PROPRIO in mxGeometry, la rotazione nello stile.
    /// </para>
    /// <para>
    /// La rotazione di prima e quella del disegno sono DIVERSE apposta: e' l'unico modo perche' uno
    /// scambio fra le due dentro <c>Geometry</c> - l'errore che nessuna prova prendeva - si veda.
    /// Con la rotazione di prima al posto di quella del disegno qui comparirebbe 40x60 a (100,200),
    /// cioe' il riquadro del disegno preso cosi' com'e'.
    /// </para>
    /// </summary>
    [Fact]
    public void AResizedDeviceGetsThePlantEnvelopeOfItsNewOwnSize()
    {
        var opened = Graph(Item(A, 10, 20, rotation: 0));
        var xml = $"""<mxGraphModel><root><mxCell id="0"/><mxCell id="1" parent="0"/><object id="{A}" label="A" wcs="device"><mxCell style="shape=image;rotation=35;" vertex="1" parent="1"><mxGeometry x="100" y="200" width="40" height="60" as="geometry"/></mxCell></object></root></mxGraphModel>""";

        var geometry = Assert.Single(DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession).Moves);

        // L'inviluppo di 40x60 a 35 gradi: la sagoma inclinata ingombra di piu' in tutt'e due le
        // direzioni, ed e' quell'ingombro - non il riquadro proprio - che l'impianto memorizza.
        Assert.Equal(67.18, geometry.Width!.Value, precision: 2);
        Assert.Equal(72.09, geometry.Height!.Value, precision: 2);
        // Ruotare non sposta il centro: (120, 230) meno mezzo inviluppo.
        Assert.Equal(86.41, geometry.X!.Value, precision: 2);
        Assert.Equal(193.95, geometry.Y!.Value, precision: 2);
        Assert.Equal(35, geometry.Rotation!.Value, precision: 2);
    }

    /// <summary>
    /// L'altra faccia del punto 2, e la piu' pericolosa: un dispositivo che NESSUNO ha toccato non
    /// deve risultare spostato. Confrontare il riquadro del disegno con quello dell'impianto senza
    /// riportarlo prima nello spazio giusto darebbe "spostato" per ogni dispositivo ruotato del
    /// disegno, e il salvataggio riscriverebbe l'impianto intero con geometrie sbagliate.
    /// </summary>
    [Fact]
    public void ADeviceNobodyTouchedIsNotSeenAsMovedAtAnyRotation()
    {
        var items = new[] { Item(A, 10, 20, rotation: 0), Item(B, 500, 20, rotation: 35) };
        var opened = Graph(items);
        var drawn = DrawioReader.Read(DrawioDocument.Build(Graph(items), null));

        var delta = DrawioDelta.Compute(drawn, opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.Moves);
        Assert.Empty(delta.Deleted);
        Assert.Empty(delta.Refusals);
    }

    /// <summary>
    /// La guardia anti-deriva (decisione 3.12): se un dispositivo e' cambiato ANCHE sull'host
    /// mentre disegnavo, quello non si scrive e si dice quale. Gli altri passano lo stesso: un
    /// rifiuto in blocco farebbe perdere lavoro che nessun altro aveva toccato.
    /// </summary>
    [Fact]
    public void ADeviceChangedOnBothSidesIsRefusedByNameAndTheOthersStillPass()
    {
        var opened = Graph(Item(A, 10, 20), Item(B, 500, 20));
        var current = Graph(Item(A, 10, 20), Item(B, 555, 20));   // B e' cambiato sull'host
        var drawn = DrawioReader.Read(DrawioDocument.Build(Graph(Item(A, 99, 77), Item(B, 888, 20)), null));

        var delta = DrawioDelta.Compute(drawn, opened, current, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Equal(A, Assert.Single(delta.Moves).ItemId);
        var refusal = Assert.Single(delta.Refusals);
        Assert.Equal(B.ToString(), refusal.Id);
        Assert.Contains("changed on the host as well", refusal.Reason);
    }

    /// <summary>Decisione 3.2: cancellare e' immediato, e il delta lo riporta senza chiedere.</summary>
    [Fact]
    public void ADeviceMissingFromTheDrawingIsADeletion()
    {
        var opened = Graph(Item(A, 10, 20), Item(B, 500, 20));
        var drawn = DrawioReader.Read(DrawioDocument.Build(Graph(Item(A, 10, 20)), null));

        var delta = DrawioDelta.Compute(drawn, opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Equal(B, Assert.Single(delta.Deleted));
    }

    /// <summary>
    /// La stessa guardia anti-deriva vale per la cancellazione, e li' costa di piu': sovrascrivere
    /// uno spostamento altrui si vede e si rifa', cancellare il dispositivo che un altro stava
    /// sistemando no. Il disegno non porta la differenza fra "cancellato" e "mai arrivato": si
    /// rifiuta per nome.
    /// </summary>
    [Fact]
    public void ADeletionOfADeviceChangedOnTheHostMeanwhileIsRefusedByName()
    {
        var opened = Graph(Item(A, 10, 20), Item(B, 500, 20));
        var current = Graph(Item(A, 10, 20), Item(B, 555, 20));   // B e' cambiato sull'host
        var drawn = DrawioReader.Read(DrawioDocument.Build(Graph(Item(A, 10, 20)), null));

        var delta = DrawioDelta.Compute(drawn, opened, current, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.Deleted);
        var refusal = Assert.Single(delta.Refusals);
        Assert.Equal(B.ToString(), refusal.Id);
        Assert.Contains("changed on the host as well", refusal.Reason);
    }

    /// <summary>
    /// La guardia sulla cancellazione non puo' guardare solo la geometria. L'argomento con cui
    /// esiste - non cancellare il dispositivo che un altro stava sistemando - vale identico per chi
    /// lo RINOMINA, gli cambia TIPO o gli rifa' le MAPPATURE: quelle modifiche non muovono niente,
    /// e un confronto di X/Y/W/H/rotazione non le vede. Il dispositivo verrebbe cancellato mentre
    /// un altro ci stava lavorando, senza nemmeno un rifiuto. <c>PlantItemDto</c> intero e' in mano
    /// a <c>Compute</c>: l'informazione c'e', bastava guardarla.
    /// </summary>
    [Fact]
    public void ADeletionOfADeviceRenamedOnTheHostMeanwhileIsRefusedByName()
    {
        var opened = Graph(Item(A, 10, 20), Item(B, 500, 20));
        // B non e' stato spostato sull'host: e' stato rinominato. Non si muove niente.
        var current = Graph(Item(A, 10, 20), Item(B, 500, 20) with { Name = "LF09.0001.0001" });
        var drawn = DrawioReader.Read(DrawioDocument.Build(Graph(Item(A, 10, 20)), null));

        var delta = DrawioDelta.Compute(drawn, opened, current, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.Deleted);
        var refusal = Assert.Single(delta.Refusals);
        Assert.Equal(B.ToString(), refusal.Id);
        Assert.Contains("changed on the host as well", refusal.Reason);
    }

    /// <summary>
    /// L'altra meta' della decisione sopra, e la ragione per cui la guardia larga sta solo sulla
    /// cancellazione: una SCRITTURA di geometria non ha niente da spartire con un nome. Rifiutarla
    /// perche' qualcuno ha rinominato il dispositivo mentre disegnavo farebbe perdere lo
    /// spostamento senza proteggere niente - e chi ha rinominato ritrova il proprio nome intatto,
    /// perche' il delta scrive solo X/Y/W/H/rotazione. Cancellare invece e' irreversibile: e' li'
    /// che si guarda largo.
    /// </summary>
    [Fact]
    public void ARenameOnTheHostDoesNotStopAGeometryWrite()
    {
        var opened = Graph(Item(A, 10, 20));
        var current = Graph(Item(A, 10, 20) with { Name = "LF09.0001.0001" });
        var drawn = DrawioReader.Read(DrawioDocument.Build(Graph(Item(A, 99, 77)), null));

        var delta = DrawioDelta.Compute(drawn, opened, current, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Equal(A, Assert.Single(delta.Moves).ItemId);
        Assert.Empty(delta.Refusals);
    }

    /// <summary>
    /// Un dispositivo che il disegno non ha MAI portato - non collocato, o di un tipo che il
    /// registro non dichiara - torna dal disegno mancante esattamente come uno cancellato. Senza
    /// questa distinzione il primo salvataggio dopo un'apertura cancellerebbe dall'impianto ogni
    /// dispositivo non collocato, che nessuno aveva toccato: e' il difetto D203 applicato
    /// all'impianto intero, la via sbagliata che questo compito esiste per non prendere.
    /// </summary>
    [Fact]
    public void ADeviceTheDrawingNeverCarriedIsNotADeletion()
    {
        var unplaced = Item(B, 0, 0, width: 0, height: 0);
        var opened = Graph(Item(A, 10, 20), unplaced);
        var drawn = DrawioReader.Read(DrawioDocument.Build(opened, null));

        var delta = DrawioDelta.Compute(drawn, opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.Deleted);
        Assert.Empty(delta.Refusals);
    }

    /// <summary>
    /// Decisione 3.1: una cella marchiata "device" con un identificativo che non conosciamo e' una
    /// copia, e diventa un dispositivo NUOVO da configurare - non un rifiuto, non una decorazione.
    /// </summary>
    [Fact]
    public void AMarkedDeviceWithAnUnknownIdBecomesANewDeviceToConfigure()
    {
        var opened = Graph(Item(A, 10, 20));
        var xml = DrawioDocument.Build(Graph(Item(A, 10, 20)), null)
            .Replace("</root>", """<object id="copia-1" label="" wcs="device"><mxCell style="shape=image;" vertex="1" parent="1"><mxGeometry x="700" y="700" width="130" height="184" as="geometry"/></mxCell></object></root>""");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Equal("copia-1", Assert.Single(delta.NewDevices).Id);
        Assert.Empty(delta.Refusals);
    }

    /// <summary>
    /// Il consumatore naturale della quarta lista di <see cref="DrawioContents"/> (aggiunta dalla
    /// revisione del compito 3): una cella che porta un marchio nostro che non riconosciamo -
    /// <c>wcs="devise"</c>, <c>wcs=""</c>, <c>wcs="Device"</c> con la maiuscola, o il marchio di una
    /// categoria futura - non e' un dispositivo, non e' una giunzione e non e' decorazione. Il
    /// lettore l'ha messa da parte apposta per non farla sparire in silenzio; il delta la nomina.
    /// Ignorarla qui rimetterebbe il silenzio dove la revisione l'aveva tolto.
    /// </summary>
    [Theory]
    [InlineData("devise")]
    [InlineData("")]
    [InlineData("Device")]
    public void ACellWithAMarkWeDoNotRecogniseIsRefusedByName(string mark)
    {
        var opened = Graph(Item(A, 10, 20));
        var xml = DrawioDocument.Build(opened, null)
            .Replace("</root>", $"""<object id="strano-1" label="" wcs="{mark}"><mxCell style="shape=image;" vertex="1" parent="1"><mxGeometry x="1" y="2" width="3" height="4" as="geometry"/></mxCell></object></root>""");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        var refusal = Assert.Single(delta.Refusals);
        Assert.Equal("strano-1", refusal.Id);
        Assert.Contains("mark", refusal.Reason);
        Assert.Empty(delta.NewDevices);
        Assert.Empty(delta.Moves);
    }

    /// <summary>
    /// Decisione 3.3: una freccia e' una giunzione solo se aggancia una porta d'uscita a una
    /// d'entrata. Senza agganci resta decorazione, e il delta non la propone.
    /// </summary>
    [Fact]
    public void AnEdgeWithoutPortAnchorsIsNotAJunction()
    {
        var opened = Graph(Item(A, 10, 20), Item(B, 500, 20));
        var xml = DrawioDocument.Build(opened, null)
            .Replace("</root>", $"""<object id="freccia-1" label="" wcs="junction"><mxCell style="endArrow=block;" edge="1" parent="1" source="{A}" target="{B}"><mxGeometry relative="1" as="geometry"/></mxCell></object></root>""");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.NewJunctions);
        var refusal = Assert.Single(delta.Refusals);
        Assert.Contains("does not hook two ports", refusal.Reason);
    }

    /// <summary>Con gli agganci, e fra due dispositivi che conosciamo, la freccia e' una giunzione nuova.</summary>
    [Fact]
    public void AnEdgeAnchoredToTwoPortsBetweenKnownDevicesIsANewJunction()
    {
        var opened = Graph(Item(A, 10, 20), Item(B, 500, 20));
        var xml = DrawioDocument.Build(opened, null)
            .Replace("</root>", $"""<object id="freccia-1" label="" wcs="junction"><mxCell style="exitX=0.96;exitY=0.5;entryX=0.04;entryY=0.5;" edge="1" parent="1" source="{A}" target="{B}"><mxGeometry relative="1" as="geometry"/></mxCell></object></root>""");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        var created = Assert.Single(delta.NewJunctions);
        Assert.Equal(A, created.Source);
        Assert.Equal(B, created.Destination);
        Assert.Empty(delta.Refusals);
    }

    /// <summary>
    /// La freccia COPIATA: identificativo nuovo, ma stessi estremi e stessi agganci di una
    /// giunzione che l'impianto ha gia'. Proposta come nuova, chi scrive ne creerebbe una seconda
    /// fra gli stessi due dispositivi - un doppione che nessuno ha chiesto e che il disegno non
    /// mostra come tale, perche' le due frecce si sovrappongono.
    /// </summary>
    [Fact]
    public void AnEdgeCopiedBetweenTwoAlreadyJoinedDevicesIsRefusedNotDuplicated()
    {
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, 99)]);
        var xml = DrawioDocument.Build(opened, null)
            .Replace("</root>", $"""<object id="copia-1" label="" wcs="junction"><mxCell style="exitX=0.96;exitY=0.5;entryX=0.04;entryY=0.5;" edge="1" parent="1" source="{A}" target="{B}"><mxGeometry relative="1" as="geometry"/></mxCell></object></root>""");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.NewJunctions);
        var refusal = Assert.Single(delta.Refusals);
        Assert.Equal("copia-1", refusal.Id);
        Assert.Contains("already exists", refusal.Reason);
    }

    /// <summary>
    /// Una giunzione che c'era e non torna dal disegno e' stata cancellata; una che torna resta.
    /// Il giro passa dal formato vero - <c>DrawioDocument</c> scrive l'arco marchiato,
    /// <c>DrawioReader</c> lo rilegge - non da un XML scritto a mano che potrebbe non somigliargli.
    /// </summary>
    [Fact]
    public void AJunctionMissingFromTheDrawingIsADeletionAndOneStillThereIsNot()
    {
        var junction = new JunctionDto(J, A, B, 99);
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20)], [junction]);

        var still = DrawioDelta.Compute(DrawioReader.Read(DrawioDocument.Build(opened, null)), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);
        Assert.Empty(still.DeletedJunctions);
        Assert.Empty(still.NewJunctions);

        var without = Graph([Item(A, 10, 20), Item(B, 500, 20)], []);
        var removed = DrawioDelta.Compute(DrawioReader.Read(DrawioDocument.Build(without, null)), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);
        Assert.Equal(J, Assert.Single(removed.DeletedJunctions));
    }

    /// <summary>
    /// La forma esatta di D203 dentro il giro delle giunzioni, e il punto b del compito 7b.
    /// L'operatore stacca la freccia da B e la attacca a C, salva: il giro riconosceva
    /// l'identificativo della giunzione e passava oltre senza guardare gli estremi disegnati -
    /// nessuna modifica, nessun rifiuto, il gesto evaporava. Il compito 4 lo aveva reso un rifiuto;
    /// qui diventa quello che l'operatore ha chiesto, <b>cancellazione piu' creazione</b>, per la
    /// via gia' provata da
    /// <see cref="ADeletedAndRedrawnJunctionBetweenTheSameTwoDevicesIsRecreatedNotRefusedAsADuplicate"/>:
    /// la vecchia non entra fra le celle riconosciute, quindi finisce fra le cancellazioni, e la
    /// freccia prosegue come se fosse nuova.
    /// <para>
    /// L'identificativo cambia - <c>JunctionRequest</c> non ne porta uno, lo assegna l'host - e la
    /// cosa e' <b>dichiarata</b>, non taciuta: e' una voce, non un rifiuto, perche' il gesto e'
    /// stato scritto.
    /// </para>
    /// </summary>
    [Fact]
    public void AJunctionReattachedToAnotherDeviceIsRemadeNotRefused()
    {
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20), Item(C, 900, 20)],
            [new JunctionDto(J, A, B, 0)]);
        var xml = DrawioDocument.Build(opened, null).Replace($"target=\"{B}\"", $"target=\"{C}\"");
        Assert.Contains($"target=\"{C}\"", xml);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        // La vecchia se ne va, la nuova nasce fra gli estremi che l'operatore ha disegnato.
        Assert.Equal(J, Assert.Single(delta.DeletedJunctions));
        var remade = Assert.Single(delta.NewJunctions);
        Assert.Equal(A, remade.Source);
        Assert.Equal(C, remade.Destination);
        // E porta la priorita' di prima (alta = 0), letta dall'attributo che la freccia si e'
        // trascinata dietro: riagganciare una freccia non e' declassarla.
        Assert.Equal(0, remade.Priority);
        Assert.Empty(delta.Refusals);
        Assert.Contains(delta.Notices, n => n.Message.Contains("re-hooked"));
    }

    /// <summary>
    /// L'altra meta' del punto b, e la sola cosa che non si deve MAI fare qui: cancellare senza
    /// ricreare. La freccia e' stata riagganciata a un dispositivo che l'impianto non conosce (una
    /// copia, ancora da configurare), quindi la giunzione nuova non e' scrivibile - e allora quella
    /// di prima resta dov'e', con un rifiuto che dice perche'. Meta' gesto scritto sarebbe la
    /// perdita peggiore di tutto questo file: un legame sparito dall'impianto senza che nessuno lo
    /// abbia chiesto.
    /// </summary>
    [Fact]
    public void AJunctionReattachedToAnUnknownDeviceIsRefusedAndTheOldOneSurvives()
    {
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, 99)]);
        var stranger = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var xml = DrawioDocument.Build(opened, null).Replace($"target=\"{B}\"", $"target=\"{stranger}\"");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.NewJunctions);
        Assert.Empty(delta.DeletedJunctions);
        var refusal = Assert.Single(delta.Refusals);
        Assert.Equal(J.ToString(), refusal.Id);
        Assert.Contains("re-hooked", refusal.Reason);
    }

    /// <summary>
    /// Queue item 11, the first false sentence. A junction another hand removed from the plant while
    /// the drawing was open, whose arrow the operator re-hooked onto another device: the save
    /// creates the connection he drew, and deletes NOTHING - the old one was already gone, taken by
    /// somebody else. The line used to say "redone as a deletion plus a creation".
    /// </summary>
    [Fact]
    public void AJunctionAnotherHandRemovedAndTheOperatorReHookedIsCreatedAndTheLineSaysNothingWasDeletedHere()
    {
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20), Item(C, 900, 20)], [new JunctionDto(J, A, B, 0)]);
        var current = Graph([Item(A, 10, 20), Item(B, 500, 20), Item(C, 900, 20)], []);
        var xml = DrawioDocument.Build(opened, null).Replace($"target=\"{B}\"", $"target=\"{C}\"");
        Assert.Contains($"target=\"{C}\"", xml);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, current, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.DeletedJunctions);
        var created = Assert.Single(delta.NewJunctions);
        Assert.Equal((A, C), (created.Source, created.Destination));
        Assert.Empty(delta.Refusals);
        Assert.Contains(delta.Notices, n => n.Id == J.ToString() && n.Message ==
            "junction re-hooked: somebody else had already removed it from the plant while you were drawing, so this save deleted nothing - it created the connection you drew, with a new identifier");
        Assert.DoesNotContain(delta.Notices, n => n.Message.Contains("deletion plus a creation", StringComparison.Ordinal));
        // Fix round, IMPORTANT 1: nor the other-hand line about the same junction, which said "this
        // save did not touch it, and the line goes off your drawing" beside the creation above.
        Assert.DoesNotContain(delta.Notices, n => n.Message.Contains("somebody else removed this connection", StringComparison.Ordinal));
        Assert.Single(delta.Notices, n => n.Id == J.ToString() && n.Message.StartsWith("junction re-hooked", StringComparison.Ordinal));
    }

    /// <summary>
    /// Queue item 11, the second false sentence: the same re-hook refused - here it doubles a
    /// junction the plant still holds - used to say "the plant still holds the ends it had", and the
    /// plant holds nothing of that junction any more: somebody else removed it.
    /// </summary>
    [Fact]
    public void AJunctionAnotherHandRemovedAndTheOperatorReHookedOntoADuplicateIsRefusedWithoutClaimingThePlantKeptIt()
    {
        var j2 = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20), Item(C, 900, 20)],
            [new JunctionDto(J, A, B, 0), new JunctionDto(j2, A, C, 99)]);
        var current = Graph([Item(A, 10, 20), Item(B, 500, 20), Item(C, 900, 20)], [new JunctionDto(j2, A, C, 99)]);
        var xml = DrawioDocument.Build(opened, null).Replace($"target=\"{B}\"", $"target=\"{C}\"");
        Assert.Contains($"target=\"{C}\"", xml);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, current, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.DeletedJunctions);
        Assert.Empty(delta.NewJunctions);
        var refusal = Assert.Single(delta.Refusals);
        Assert.Equal(J.ToString(), refusal.Id);
        Assert.Equal(
            "the junction has been re-hooked, or detached, in the drawing, but the new arrow doubles a junction that already exists between these two devices: it was not written, and the plant does not hold the junction any more either - somebody else removed it while you were drawing",
            refusal.Reason);
    }

    /// <summary>
    /// Il gesto che l'operatore fa SUBITO DOPO il rifiuto "giunzione riagganciata" qui sopra:
    /// invece di lasciare la freccia sbagliata sulla tela, la cancella e ne ridisegna una nuova fra
    /// gli stessi due dispositivi. J non e' piu' fra le celle marchiate del disegno - la sua cella
    /// non c'e' proprio piu' - quindi finisce fra le candidate alla cancellazione; la freccia nuova
    /// ha un identificativo che l'impianto non conosce, e incontra il controllo del doppione contro
    /// <c>opened.Junctions</c>. Senza l'esclusione delle giunzioni gia' avviate alla cancellazione,
    /// il controllo vedeva ancora J (che sta in <c>opened</c>, non nel disegno che torna) e
    /// rifiutava la freccia nuova con "esiste gia'": la giunzione spariva dall'impianto (J
    /// cancellata) e non veniva ricreata, con un motivo che diceva l'opposto di quello che stava
    /// succedendo.
    /// </summary>
    [Fact]
    public void ADeletedAndRedrawnJunctionBetweenTheSameTwoDevicesIsRecreatedNotRefusedAsADuplicate()
    {
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, 99)]);
        var built = DrawioDocument.Build(opened, null);

        // Toglie la cella di J per intero - non solo il marchio, come in MangleMark: qui
        // l'operatore l'ha proprio cancellata da draw.io.
        var marker = "<object id=\"" + J + "\"";
        var start = built.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = built.IndexOf("</object>", start, StringComparison.Ordinal) + "</object>".Length;
        var withoutJ = built.Remove(start, end - start);
        Assert.DoesNotContain(J.ToString(), withoutJ);

        // E ridisegna una freccia nuova fra A e B, con un identificativo che l'impianto non ha mai
        // visto: lo stesso gesto di AnEdgeAnchoredToTwoPortsBetweenKnownDevicesIsANewJunction.
        var xml = withoutJ.Replace("</root>",
            $"""<object id="nuova-1" label="" wcs="junction"><mxCell style="exitX=0.96;exitY=0.5;entryX=0.04;entryY=0.5;" edge="1" parent="1" source="{A}" target="{B}"><mxGeometry relative="1" as="geometry"/></mxCell></object></root>""");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        var redrawn = Assert.Single(delta.NewJunctions);
        Assert.Equal(A, redrawn.Source);
        Assert.Equal(B, redrawn.Destination);
        Assert.Equal(J, Assert.Single(delta.DeletedJunctions));
        Assert.DoesNotContain(delta.Refusals, r => r.Reason.Contains("already exists"));
    }

    /// <summary>
    /// Alla rotazione degenere (45 gradi e multipli) <see cref="NodePlacement.LocalSize"/> non sa
    /// invertire l'inviluppo e ripiega sul riquadro cosi' com'e': il disegno non porta piu' la
    /// dimensione propria del dispositivo. Uno spostamento si scrive lo stesso - il centro si
    /// sposta e basta, la dimensione dell'impianto resta quella di prima - ma un
    /// RIDIMENSIONAMENTO li' non e' ricostruibile, e si rifiuta per nome invece di riscrivere un
    /// dispositivo ingrassato dal giro di ritorno.
    /// </summary>
    [Fact]
    public void AtADegenerateRotationAMoveIsWrittenButAResizeIsRefused()
    {
        var opened = Graph(Item(A, 10, 20, rotation: 45, width: 100, height: 100));

        var moved = DrawioDocument.Build(Graph(Item(A, 300, 20, rotation: 45, width: 100, height: 100)), null);
        var delta = DrawioDelta.Compute(DrawioReader.Read(moved), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);
        var geometry = Assert.Single(delta.Moves);
        Assert.Equal(300, geometry.X!.Value, precision: 2);
        Assert.Equal(100, geometry.Width!.Value, precision: 2);
        Assert.Empty(delta.Refusals);

        var enlarged = DrawioDocument.Build(Graph(Item(A, 10, 20, rotation: 45, width: 200, height: 200)), null);
        var refused = DrawioDelta.Compute(DrawioReader.Read(enlarged), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);
        Assert.Empty(refused.Moves);
        var reason = Assert.Single(refused.Refusals).Reason;
        Assert.Contains("45", reason);
        Assert.Contains("resized", reason);
    }

    /// <summary>
    /// Il rifiuto deve nominare il gesto che l'operatore ha fatto. Chi prende un dispositivo a 45
    /// gradi e lo RADDRIZZA non l'ha ridimensionato: sentirsi rispondere "ridimensionato a una
    /// rotazione di 45 gradi" vuol dire non riconoscere il proprio gesto, e un rifiuto che non si
    /// capisce e' peggio di nessun rifiuto - chi legge lo prende per un guasto nostro e lo ignora.
    /// Il rifiuto in se' resta giusto: la dimensione propria non e' ricostruibile a quell'angolo.
    /// </summary>
    [Fact]
    public void StraighteningADeviceFromFortyFiveDegreesIsRefusedAsARotationNotAsAResize()
    {
        var opened = Graph(Item(A, 10, 20, rotation: 45, width: 100, height: 100));
        // Stesso riquadro, solo la rotazione tolta: il gesto e' una rotazione pura.
        var straightened = DrawioDocument.Build(opened, null).Replace("rotation=45;", "rotation=0;");
        Assert.Contains("rotation=0;", straightened);

        var delta = DrawioDelta.Compute(DrawioReader.Read(straightened), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.Moves);
        var reason = Assert.Single(delta.Refusals).Reason;
        Assert.Contains("rotated", reason);
        Assert.DoesNotContain("ridimensionato", reason);
    }

    /// <summary>
    /// L'altro gesto che quell'angolo rende impossibile, e che non era presidiato: PORTARE a 45
    /// gradi un dispositivo che stava a un angolo qualunque. La guardia interrogava solo la
    /// rotazione di PRIMA, quindi il gesto passava: si scriveva l'inviluppo, e da quel momento
    /// l'impianto non puo' piu' rappresentare la dimensione propria del dispositivo - alla
    /// riapertura torna come un quadrato gonfiato di radice di due (misurato: 100 -> 141.42). E' un
    /// limite del formato, non un errore di calcolo, ma e' una perdita silenziosa esattamente
    /// all'angolo che questo compito isola: si rifiuta, dicendo perche'.
    /// </summary>
    [Fact]
    public void BringingADeviceToFortyFiveDegreesIsRefusedBecauseThePlantCannotHoldIt()
    {
        var opened = Graph(Item(A, 10, 20, rotation: 0, width: 100, height: 100));
        var turned = DrawioDocument.Build(opened, null).Replace("rotation=0;", "rotation=45;");
        Assert.Contains("rotation=45;", turned);

        var delta = DrawioDelta.Compute(DrawioReader.Read(turned), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.Moves);
        var reason = Assert.Single(delta.Refusals).Reason;
        Assert.Contains("45 degrees", reason);
        Assert.Contains("its own size would be lost", reason);
    }

    /// <summary>
    /// Una riserva del rapporto precedente diceva che questo rifiuto sarebbe superfluo per un
    /// dispositivo con dimensione propria gia' QUADRATA, "dove la perdita non ci sarebbe": e'
    /// falso, verificato qui coi numeri. Un quadrato 100x100 a 45 gradi ha inviluppo
    /// 141.42x141.42 - non 100x100 - e alla riapertura <see cref="NodePlacement.LocalSize"/> e'
    /// ANCH'ESSA degenere su quell'angolo e restituisce 141.42 tale e quale, non 100: il quadrato
    /// crescerebbe di radice di due A OGNI GIRO, senza fondo. Distinguere il caso quadrato -
    /// come la riserva proponeva - avrebbe introdotto proprio questo difetto, non l'avrebbe tolto:
    /// il rifiuto qui sotto e' quindi giusto anche per un dispositivo quadrato, senza eccezioni.
    /// </summary>
    [Fact]
    public void BringingASquareDeviceToFortyFiveDegreesIsRefusedTooBecauseTheGrowthHasNoFloor()
    {
        var opened = Graph(Item(A, 10, 20, rotation: 0, width: 100, height: 100));
        var turned = DrawioDocument.Build(opened, null).Replace("rotation=0;", "rotation=45;");
        Assert.Contains("rotation=45;", turned);

        var delta = DrawioDelta.Compute(DrawioReader.Read(turned), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.Moves);
        var reason = Assert.Single(delta.Refusals).Reason;
        Assert.Contains("45 degrees", reason);
        Assert.Contains("its own size would be lost", reason);

        // Il numero che la riserva sbagliava: 100x100 a 45 gradi non resta 100x100.
        var (envelopeWidth, envelopeHeight) = NodePlacement.Envelope(100, 100, 45);
        Assert.Equal(141.42, envelopeWidth, precision: 2);
        Assert.Equal(141.42, envelopeHeight, precision: 2);

        // E il giro di ritorno non lo riporta a 100: a 45 gradi LocalSize e' degenere anche
        // sull'inviluppo appena cresciuto, e ripiega su se stesso invece di ridursi - il prossimo
        // giro partirebbe da 141.42, non da 100, e crescerebbe ancora.
        var (localWidth, localHeight) = NodePlacement.LocalSize(envelopeWidth, envelopeHeight, 45);
        Assert.Equal(envelopeWidth, localWidth, precision: 2);
        Assert.Equal(envelopeHeight, localHeight, precision: 2);
    }

    /// <summary>
    /// Il giro non si consuma. Aprire il disegno e salvarlo passa le coordinate attraverso due
    /// decimali ("0.##") e attraverso l'inverso dell'inviluppo: cinque aperture e cinque
    /// salvataggi di seguito, con l'operatore che ogni volta sposta davvero il dispositivo, devono
    /// lasciare la dimensione dell'impianto ESATTAMENTE quella di partenza.
    /// <para>
    /// Che cosa prende davvero, e che cosa no. Lo scarto che il ramo <c>sizeKept</c> di
    /// <c>DrawioDelta.Geometry</c> evita e' SECCO, non cumulativo: misurato spegnendo quel ramo,
    /// 130 a 35 gradi diventa 129.995508 al primo giro e resta quel numero al secondo, al quinto e
    /// al ventesimo. Il primo <c>Assert.Equal(130, ...)</c> qui sotto pretende l'uguaglianza esatta
    /// e lo prenderebbe gia' dopo un giro solo; i cinque giri aggiungono la prova che nemmeno
    /// ripetendo il giro compare una deriva - una domanda diversa, e che vale la pena fare, perche'
    /// e' la forma che il difetto avrebbe se l'arrotondamento non fosse idempotente.
    /// </para>
    /// </summary>
    [Fact]
    public void FiveOpenAndSaveCyclesDoNotEatTheDevicesSize()
    {
        var plant = Graph(Item(A, 10, 20));

        for (var cycle = 0; cycle < 5; cycle++)
        {
            var drawn = Graph(plant.Items.Select(i => i with { X = (i.X ?? 0) + 10 }).ToArray());
            var delta = DrawioDelta.Compute(DrawioReader.Read(DrawioDocument.Build(drawn, null)), plant, plant, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

            var move = Assert.Single(delta.Moves);
            plant.ApplyGeometry(
                [move],
                [new PlantItemGeometryVersionDto(move.ItemId, Guid.NewGuid())]);
        }

        var device = Assert.Single(plant.Items);
        Assert.Equal(130, device.Width);
        Assert.Equal(184, device.Height);
        Assert.Equal(60, device.X!.Value, precision: 2);   // 10 + 5 spostamenti da 10
    }

    /// <summary>La decorazione dell'operatore attraversa il delta intatta: non la interpretiamo.</summary>
    [Fact]
    public void TheOperatorsDecorationCrossesTheDeltaUntouched()
    {
        var opened = Graph(Item(A, 10, 20));
        var xml = DrawioDocument.Build(opened, null)
            .Replace("</root>", """<mxCell id="nota-1" value="Zona di carico" style="text;html=1;" vertex="1" parent="1"><mxGeometry x="900" y="900" width="320" height="40" as="geometry"/></mxCell></root>""");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Contains("Zona di carico", delta.DecorationXml);
        Assert.DoesNotContain("wcs=", delta.DecorationXml);
    }

    // ---- La freccia tirata a mano che il delta NON scrive ---------------------------------------
    //
    // Fino al lotto precedente, USCIRE dalla decorazione era riservato alle celle che portano un
    // marchio nostro: il disegno che l'operatore possiede non poteva perdersi, perche' non lo
    // toccava nessuno. La freccia tirata a mano e' la prima cella SENZA marchio che ne esce, e
    // questo sposta il confine: da quel momento la classificazione del lettore decide anche che
    // cosa sopravvive al giro. Il rilievo della revisione sta proprio qui - una freccia promossa a
    // giunzione e poi rifiutata dal delta non era ne' sull'impianto ne' nel disegno, e il rifiuto
    // le diceva «resta un disegno» mentre il disegno spariva.
    //
    // La regola che queste prove sorvegliano: la cella esce dalla decorazione SOLO se il delta la
    // scrive davvero. Le tre vie del rifiuto si provano una per una perche' sono tre rami diversi
    // di Writable, e una rete che ne guardasse uno solo lascerebbe passare gli altri due.

    /// <summary>
    /// La freccia rilasciata <b>in mezzo</b> alla sagoma invece che sul punto di connessione: non
    /// aggancia due porte (decisione 3.3), quindi la giunzione non nasce - e il disegno
    /// dell'operatore resta dov'e'. E' il gesto piu' probabile di chi l'Editor non l'ha mai usato,
    /// e la cancellazione silenziosa del suo disegno era il difetto piu' grave del lotto.
    /// </summary>
    [Fact]
    public void AnArrowRefusedForItsPortsStaysInTheOperatorsDrawing()
    {
        var opened = Graph(Item(A, 10, 20), Item(B, 500, 20));
        var xml = DrawioDocument.Build(opened, null)
            .Replace("</root>", ArrowByHand("freccia-1", A.ToString(), B.ToString(), hookedToPorts: false) + "</root>");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.NewJunctions);
        var refusal = Assert.Single(delta.Refusals);
        Assert.Equal("freccia-1", refusal.Id);
        // Il rifiuto lo promette a parole; questa riga e' la prova che sia vero.
        Assert.Contains("it stays a drawing", refusal.Reason);
        Assert.Contains("freccia-1", delta.DecorationXml);
    }

    /// <summary>
    /// La freccia che <b>raddoppia</b> una giunzione gia' presente fra gli stessi due dispositivi:
    /// rifiutata, e restituita al disegno. Sulla tela le due si sovrappongono, quindi l'operatore
    /// non vedrebbe sparire niente - ed e' esattamente per questo che la perdita andava misurata
    /// qui invece che a occhio.
    /// </summary>
    [Fact]
    public void AnArrowRefusedAsADuplicateStaysInTheOperatorsDrawing()
    {
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, 99)]);
        var xml = DrawioDocument.Build(opened, null)
            .Replace("</root>", ArrowByHand("freccia-1", A.ToString(), B.ToString()) + "</root>");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.NewJunctions);
        Assert.Contains(delta.Refusals, r => r.Id == "freccia-1" && r.Reason.Contains("doubles a junction"));
        Assert.Contains("freccia-1", delta.DecorationXml);
    }

    /// <summary>
    /// La freccia tirata su una <b>copia</b> - una cella marchiata dispositivo che l'impianto non
    /// conosce ancora - non ha niente da collegare, e nemmeno qui il disegno si perde.
    /// </summary>
    [Fact]
    public void AnArrowOntoADeviceThePlantDoesNotKnowStaysInTheOperatorsDrawing()
    {
        var opened = Graph(Item(A, 10, 20));
        var xml = DrawioDocument.Build(opened, null).Replace("</root>",
            """<object id="copia-1" label="copia" wcs="device"><mxCell style="shape=image;" vertex="1" parent="1"><mxGeometry x="700" y="700" width="130" height="184" as="geometry"/></mxCell></object>"""
            + ArrowByHand("freccia-1", A.ToString(), "copia-1") + "</root>");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.NewJunctions);
        Assert.Contains(delta.Refusals, r => r.Id == "freccia-1" && r.Reason.Contains("does not join two devices the plant knows"));
        Assert.Contains("freccia-1", delta.DecorationXml);
    }

    /// <summary>
    /// L'altra meta', e quella che tiene stretta la restituzione: la freccia che <b>diventa</b> una
    /// giunzione non torna nel disegno. Senza questa prova, restituire tutto sarebbe verde: alla
    /// riapertura la tela porterebbe due frecce sovrapposte fra gli stessi due dispositivi - quella
    /// dell'impianto e questa - e a schermo se ne vedrebbe una sola.
    /// </summary>
    [Fact]
    public void AnArrowThatBecomesAJunctionDoesNotStayInTheDrawing()
    {
        var opened = Graph(Item(A, 10, 20), Item(B, 500, 20));
        var xml = DrawioDocument.Build(opened, null)
            .Replace("</root>", ArrowByHand("freccia-1", A.ToString(), B.ToString()) + "</root>");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Equal("freccia-1", Assert.Single(delta.NewJunctions).Id);
        Assert.Empty(delta.Refusals);
        Assert.DoesNotContain("freccia-1", delta.DecorationXml);
    }

    /// <summary>
    /// <b>The copy of a marked arrow, refused because it doubles a junction, stays in the drawing
    /// - exactly like an unmarked copy.</b> The refusal says <i>"it was not created, it stays a
    /// drawing"</i>, and from the invariant of <c>DrawioDelta</c> onwards that sentence is true
    /// of both.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This test used to assert the opposite</b>, on a premise that has since become false:
    /// "the mark is written by <c>DrawioDocument.Build</c>, so the cell is ours and the plant
    /// redraws it". The plant redraws the junction it HOLDS - the original, under its own
    /// identifier - and it has never held this copy: a Ctrl+C of an arrow gives the new cell a
    /// new identifier. So the copy vanished at the next opening while the report told the
    /// operator it stayed, which is word for word the defect this branch has paid for three
    /// times, and the unmarked copy of the same gesture never had it.
    /// </para>
    /// <para>
    /// <b>What it costs, said and not hidden</b>: the canvas comes back with the operator's copy
    /// lying over the plant's arrow. That is a drawing he made and can see and delete; the other
    /// way round is a drawing that disappears while the save says it stays, and between a visible
    /// duplicate of his own and a silent loss this file chooses the first. The half that stays
    /// untouched is the one below: the cell the plant DOES hold is not handed back.
    /// </para>
    /// </remarks>
    [Fact]
    public void AMarkedCopyRefusedAsADuplicateStaysInTheDrawingLikeAnUnmarkedOne()
    {
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, 99)]);
        var xml = DrawioDocument.Build(opened, null).Replace("</root>",
            $"""<object id="doppione-1" label="" wcs="junction"><mxCell style="exitX=0.96;exitY=0.5;entryX=0.04;entryY=0.5;" edge="1" parent="1" source="{A}" target="{B}"><mxGeometry relative="1" as="geometry"/></mxCell></object></root>""");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.NewJunctions);
        Assert.Contains(delta.Refusals, r => r.Id == "doppione-1" && r.Reason.Contains("it stays a drawing"));
        Assert.Contains("doppione-1", delta.DecorationXml);
        // E l'altra meta': la giunzione che l'impianto tiene non e' nella decorazione, o alla
        // riapertura sarebbe disegnata due volte - una dall'impianto e una dal disegno.
        Assert.DoesNotContain(J.ToString(), delta.DecorationXml);
    }

    /// <summary>
    /// La cella che torna nel disegno e' quella <b>vera</b>, con il suo stile e i suoi estremi -
    /// non un guscio con il solo identificativo. Un reinnesto che perdesse lo stile ridisegnerebbe
    /// la freccia dell'operatore diversa da come lui l'ha lasciata: una perdita piu' piccola, della
    /// stessa famiglia.
    /// </summary>
    [Fact]
    public void TheArrowGivenBackToTheDrawingKeepsItsStyleAndItsEnds()
    {
        var opened = Graph(Item(A, 10, 20), Item(B, 500, 20));
        var xml = DrawioDocument.Build(opened, null)
            .Replace("</root>", ArrowByHand("freccia-1", A.ToString(), B.ToString(), hookedToPorts: false) + "</root>");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Contains("edgeStyle=orthogonalEdgeStyle", delta.DecorationXml);
        Assert.Contains($"source=\"{A}\"", delta.DecorationXml);
        Assert.Contains($"target=\"{B}\"", delta.DecorationXml);
        // E non si porta dietro un marchio che non ha mai avuto: quello che torna e' disegno.
        Assert.DoesNotContain("wcs=\"junction\"", delta.DecorationXml);
    }

    /// <summary>
    /// La freccia lasciata <b>accanto</b> al secondo dispositivo invece che sopra: un capo su un
    /// dispositivo, l'altro sul nulla. Non e' una giunzione e non lo diventa - non c'e' niente da
    /// collegare - ma il salvataggio lo dice, nominandola. Prima non diceva niente affatto, ed e'
    /// lo stesso sbaglio di mira della freccia rilasciata in mezzo alla sagoma, che invece un
    /// rifiuto ce l'ha.
    /// </summary>
    [Fact]
    public void AnArrowWithAnEndInTheAirIsSaidAndStaysInTheDrawing()
    {
        var opened = Graph(Item(A, 10, 20), Item(B, 500, 20));
        var xml = DrawioDocument.Build(opened, null).Replace("</root>",
            $"""<mxCell id="freccia-1" style="edgeStyle=orthogonalEdgeStyle;exitX=1;exitY=0.5;" edge="1" parent="1" source="{A}"><mxGeometry relative="1" as="geometry"/></mxCell></root>""");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.NewJunctions);
        var notice = Assert.Single(delta.Notices, n => n.Id == "freccia-1");
        Assert.Contains("joins no two devices", notice.Message);
        // Una VOCE, non un rifiuto: non e' stato rifiutato niente, e l'elenco dei rifiuti deve
        // restare il posto dove si trovano quelli veri.
        Assert.DoesNotContain(delta.Refusals, r => r.Id == "freccia-1");
        // E il disegno non si tocca: la cella non era mai uscita dalla decorazione, e nemmeno la
        // voce la porta via. Misurato, non dedotto - e' la famiglia di difetti di questo lotto.
        Assert.Contains("freccia-1", delta.DecorationXml);
    }

    /// <summary>
    /// La linea senza punta fra due dispositivi, che e' quello che la decisione del proprietario
    /// del 19/9 ha tolto dalle giunzioni: <b>non</b> diventa un collegamento, <b>resta</b> il
    /// disegno dell'operatore, e <b>viene detta</b>. Le tre cose insieme, perche' due su tre sono
    /// gia' state un difetto su questo ramo: prima la linea diventava una giunzione in silenzio,
    /// e la riparazione sbagliata sarebbe stata cancellarla dalla tela.
    /// </summary>
    [Fact]
    public void ALineWithNoHeadBetweenTwoDevicesIsSaidAndStaysInTheDrawing()
    {
        var opened = Graph(Item(A, 10, 20), Item(B, 500, 20));
        var xml = DrawioDocument.Build(opened, null).Replace("</root>",
            $"{LineByHand("tratto-1", A.ToString(), B.ToString(), "endArrow=none;")}</root>");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.NewJunctions);
        var notice = Assert.Single(delta.Notices, n => n.Id == "tratto-1");
        Assert.Contains("no arrowhead", notice.Message);
        Assert.DoesNotContain(delta.Refusals, r => r.Id == "tratto-1");
        Assert.Contains("tratto-1", delta.DecorationXml);
    }

    /// <summary>
    /// Due punte: il proprietario ha detto che i due sensi sono <b>due</b> frecce, quindi non si
    /// indovina un verso. La linea resta un disegno e la voce dice che cosa fare, invece di
    /// lasciare l'operatore convinto di aver collegato nei due sensi.
    /// </summary>
    [Fact]
    public void ALineWithAHeadAtBothEndsIsToldToBeDrawnAsTwoArrows()
    {
        var opened = Graph(Item(A, 10, 20), Item(B, 500, 20));
        var xml = DrawioDocument.Build(opened, null).Replace("</root>",
            $"{LineByHand("tratto-1", A.ToString(), B.ToString(), "endArrow=block;startArrow=block;")}</root>");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.NewJunctions);
        var notice = Assert.Single(delta.Notices, n => n.Id == "tratto-1");
        Assert.Contains("BOTH ends", notice.Message);
        Assert.DoesNotContain(delta.Refusals, r => r.Id == "tratto-1");
        Assert.Contains("tratto-1", delta.DecorationXml);
    }

    /// <summary>
    /// La punta indica il verso: la giunzione nasce <b>dalla coda alla punta</b>, non nel verso in
    /// cui l'operatore ha trascinato il mouse. Senza questa lettura la freccia con la punta a
    /// rovescio creava una giunzione al contrario, in silenzio - e il motore segue la giunzione,
    /// non il disegno.
    /// </summary>
    [Fact]
    public void AnArrowWhoseHeadIsAtTheStartMakesTheJunctionTheOtherWayRound()
    {
        var opened = Graph(Item(A, 10, 20), Item(B, 500, 20));
        var xml = DrawioDocument.Build(opened, null).Replace("</root>",
            $"{LineByHand("freccia-1", A.ToString(), B.ToString(), "endArrow=none;startArrow=classic;startFill=1;")}</root>");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        var born = Assert.Single(delta.NewJunctions);
        Assert.Equal("freccia-1", born.Id);
        Assert.Equal(B, born.Source);
        Assert.Equal(A, born.Destination);
        Assert.Empty(delta.Refusals);
        // Scritta, quindi fuori dal disegno libero: sulla tela la ridisegna l'impianto.
        Assert.DoesNotContain("freccia-1", delta.DecorationXml);

        // RILIEVO 3 DELLA REVISIONE, la guardia che nessuna prova sorvegliava. Sulla freccia
        // tirata a mano la punta si legge per sapere il VERSO, e su quel verso non si dice
        // niente: non e' un gesto su una giunzione dell'impianto, e' il modo in cui la freccia e'
        // stata disegnata. Una voce sulla punta che arrivasse anche qui - "the head of this
        // junction's arrow has been moved to the other end" - sarebbe falsa parola per parola per
        // una freccia nuova, e la suite intera restava verde quando lo era.
        //
        // Le due voci che ci vogliono, e nessuna terza: l'aggancio della giunzione nuova e la
        // priorita' scelta al posto di chi disegna. Nessuna delle due nomina la punta, quindi la
        // riga qui sotto e' una rete e non una formalita'.
        Assert.DoesNotContain(delta.Notices, n => n.Message.Contains("head", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(delta.Notices, n => n.Message.Contains("new junction", StringComparison.Ordinal));
        Assert.Contains(delta.Notices, n => n.Message.Contains("born at low priority", StringComparison.Ordinal));
        Assert.Equal(2, delta.Notices.Count);
    }

    /// <summary>
    /// L'altra meta' della frase del proprietario: <b>il collegamento nei due sensi sono due
    /// frecce</b>, una per verso, e fanno due giunzioni distinte. Il controllo del doppione e'
    /// direzionale da prima di questo lotto; qui si misura che lo sia davvero, con le due frecce
    /// tirate a mano nello stesso salvataggio.
    /// </summary>
    [Fact]
    public void TwoArrowsDrawnTheOppositeWayMakeTwoJunctions()
    {
        var opened = Graph(Item(A, 10, 20), Item(B, 500, 20));
        var xml = DrawioDocument.Build(opened, null).Replace("</root>",
            $"{ArrowByHand("andata", A.ToString(), B.ToString())}{ArrowByHand("ritorno", B.ToString(), A.ToString())}</root>");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.Refusals);
        Assert.Equal(2, delta.NewJunctions.Count);
        Assert.Single(delta.NewJunctions, j => j.Source == A && j.Destination == B);
        Assert.Single(delta.NewJunctions, j => j.Source == B && j.Destination == A);
    }

    /// <summary>
    /// <b>Spostare la punta rovescia il collegamento</b>, e dopo un ricaricamento come prima. La
    /// giunzione si rifa' - cancellazione piu' creazione, con gli estremi scambiati - ed e' la
    /// stessa strada che la freccia di questa sessione percorreva gia' da sola.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rilievo 2 della revisione. Prima di questo, la stessa gesture faceva due cose opposte: nella
    /// sessione che aveva disegnato la freccia il rovesciamento avveniva davvero (il lettore
    /// scambia gli estremi, <c>SameEnds</c> non li riconosce piu', e il delta cancella e ricrea),
    /// dopo un ricaricamento non succedeva niente e la voce consigliava di cancellare la freccia e
    /// ridisegnarla - un consiglio vero solo in quello stato.
    /// </para>
    /// <para>
    /// La voce e' quella del riaggancio, la stessa nelle due condizioni, e dichiara la sola cosa
    /// che l'operatore non puo' dedurre da solo: <b>l'identificativo cambia</b>.
    /// </para>
    /// </remarks>
    [Fact]
    public void MovingTheHeadOfAJunctionOfOursTurnsTheConnectionRoundAfterAReloadToo()
    {
        const string heads = "endArrow=none;startArrow=block;";
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, 99)]);
        var xml = DrawioDocument.Build(opened, null)
            .Replace("endArrow=block;endFill=1;", heads, StringComparison.Ordinal);
        Assert.Contains(heads, xml, StringComparison.Ordinal);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Equal(J, Assert.Single(delta.DeletedJunctions));
        var born = Assert.Single(delta.NewJunctions);
        Assert.Equal(B, born.Source);
        Assert.Equal(A, born.Destination);
        // Il peso viaggia con la freccia (wcsPriority), quindi il rifacimento non lo perde.
        Assert.Equal(99, born.Priority);
        var notice = Assert.Single(delta.Notices, n => n.Message.Contains("re-hooked", StringComparison.Ordinal));
        Assert.Equal(J.ToString(), notice.Id);
        Assert.Contains("new identifier", notice.Message, StringComparison.Ordinal);
        // Il consiglio che valeva solo dopo il ricaricamento, e che adesso sarebbe falso.
        Assert.DoesNotContain(delta.Notices,
            n => n.Message.Contains("delete this arrow and draw one the other way", StringComparison.Ordinal));
        Assert.Empty(delta.Refusals);
        Assert.Empty(delta.JunctionPriorities);
    }

    /// <summary>
    /// Taking the head off a line the plant holds a junction for <b>deletes that junction</b>, and
    /// the line says so. This is the arrow of THIS session - no mark, recognized through the
    /// session map - which is what the canvas carries until the page is reloaded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Review finding 1, first half. The deletion already happened before this test existed - the
    /// line falls out of <c>drawn.Junctions</c>, so nothing claims it and the deletion round takes
    /// the junction away - but the line the operator read said <i>"No junction was created and it
    /// stays a drawing"</i>, which is the opposite of what the same save had just done. Measured in
    /// a real browser by the reviewer: plant at 1 junction before, 0 after, with that sentence in
    /// the report.
    /// </para>
    /// <para>
    /// <b>The drawing is not touched</b>, and it is asked here as well: the line is the operator's
    /// own - draw.io built it, it carries no mark of ours - and it has to stay on the canvas. Only
    /// the plant's junction goes.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("endArrow=none;", "the arrowhead has been taken off")]
    [InlineData("endArrow=block;startArrow=block;", "a head at BOTH ends")]
    public void TakingTheHeadOffAnArrowOfThisSessionDeletesItsJunctionAndSaysSo(string headStyle, string expected)
    {
        // The plant holds the junction; the CANVAS does not carry it as a cell of ours, because
        // the document never comes back to draw.io after a save: what is on it is the operator's
        // own arrow, with draw.io's identifier and no mark.
        var drawnPlant = Graph(Item(A, 10, 20), Item(B, 500, 20));
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, 99)]);
        var xml = DrawioDocument.Build(drawnPlant, null).Replace("</root>",
            $"{LineByHand("freccia-1", A.ToString(), B.ToString(), headStyle)}</root>");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened,
            new Dictionary<string, Guid> { ["freccia-1"] = J }, NoDeviceDroppedInThisSession);

        Assert.Equal(J, Assert.Single(delta.DeletedJunctions));
        var notice = Assert.Single(delta.Notices, n => n.Id == "freccia-1");
        Assert.Contains(expected, notice.Message, StringComparison.Ordinal);
        Assert.Contains("has been DELETED from the plant", notice.Message, StringComparison.Ordinal);
        // The sentence that used to come out here, and that was false word for word: a junction
        // WAS there, and this very save took it away.
        Assert.DoesNotContain("No junction was created", notice.Message, StringComparison.Ordinal);
        Assert.Empty(delta.NewJunctions);
        Assert.DoesNotContain(delta.Refusals, r => r.Id == "freccia-1");
        Assert.Contains("freccia-1", delta.DecorationXml);
    }

    /// <summary>
    /// The same gesture <b>after a reload</b>, where the arrow is a cell of ours carrying the
    /// plant's identifier: same outcome, same line. Until this test existed the two conditions did
    /// the opposite of each other - the junction died in one and survived in the other - and
    /// nothing said which was which.
    /// </summary>
    /// <remarks>
    /// Review finding 1, second half. The condition is the only difference between the two tests,
    /// and the difference is exactly what the defect was made of: the drawing came from
    /// <see cref="DrawioDocument.Build"/>, so the cell is marked and its head used to be read only
    /// to produce a line saying nothing was written.
    /// </remarks>
    [Theory]
    [InlineData("endArrow=none;", "the arrowhead has been taken off")]
    [InlineData("endArrow=block;startArrow=block;", "a head at BOTH ends")]
    public void TakingTheHeadOffAJunctionOfOursDeletesItAndSaysSoAfterAReloadToo(string headStyle, string expected)
    {
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, 99)]);
        var xml = DrawioDocument.Build(opened, null)
            .Replace("endArrow=block;endFill=1;", headStyle, StringComparison.Ordinal);
        Assert.Contains(headStyle, xml, StringComparison.Ordinal);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened,
            NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Equal(J, Assert.Single(delta.DeletedJunctions));
        var notice = Assert.Single(delta.Notices, n => n.Id == J.ToString());
        Assert.Contains(expected, notice.Message, StringComparison.Ordinal);
        Assert.Contains("has been DELETED from the plant", notice.Message, StringComparison.Ordinal);
        Assert.Empty(delta.NewJunctions);
        Assert.Empty(delta.Refusals);
    }

    /// <summary>
    /// La voce della punta tolta <b>tace quando la cancellazione non c'e' stata</b>. La difesa di
    /// ultima istanza - l'identificativo della giunzione e' rimasto in una nota dell'operatore -
    /// trasforma la cancellazione in un rifiuto nominato: una riga che promettesse lo stesso «e'
    /// stata cancellata» sarebbe il difetto del rilievo 1 con il segno rovesciato.
    /// </summary>
    [Fact]
    public void TheHeadTakenOffSaysNothingAboutADeletionThatWasRefused()
    {
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, 99)]);
        var xml = DrawioDocument.Build(opened, null)
            .Replace("endArrow=block;endFill=1;", "endArrow=none;", StringComparison.Ordinal)
            .Replace("</root>", $"""<mxCell id="nota-1" value="giunzione {J} da rifare" style="text;html=1;" vertex="1" parent="1"><mxGeometry x="900" y="900" width="320" height="40" as="geometry"/></mxCell></root>""");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened,
            NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.DeletedJunctions);
        var refusal = Assert.Single(delta.Refusals);
        Assert.Equal(J.ToString(), refusal.Id);
        Assert.Contains("it was not deleted", refusal.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(delta.Notices,
            n => n.Message.Contains("has been DELETED from the plant", StringComparison.Ordinal));
    }

    /// <summary>
    /// The remedy the new line names, measured in one save: the head is taken off the junction of
    /// the plant - so that junction is on its way out - and a new arrow is drawn between the same
    /// two devices. The new one must be <b>written</b>, not refused as a duplicate of the one that
    /// is being deleted in the same breath.
    /// </summary>
    /// <remarks>
    /// The duplicate check asks whether the drawing still carries the plant's junction AS ITSELF,
    /// and a line that has lost its head is not itself any more: it is exactly the case the
    /// re-hooked junction already had, where refusing the second arrow would have said "it already
    /// exists" about a junction that this very save takes away. The mutation that drops the head
    /// from that question left the whole suite green until this test existed.
    /// </remarks>
    [Fact]
    public void AnArrowDrawnAgainAfterTheHeadWasTakenOffIsNotRefusedAsADuplicate()
    {
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, 99)]);
        var xml = DrawioDocument.Build(opened, null)
            .Replace("endArrow=block;endFill=1;", "endArrow=none;", StringComparison.Ordinal)
            .Replace("</root>", $"{ArrowByHand("freccia-2", A.ToString(), B.ToString())}</root>");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened,
            NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.Refusals);
        Assert.Equal(J, Assert.Single(delta.DeletedJunctions));
        var born = Assert.Single(delta.NewJunctions);
        Assert.Equal("freccia-2", born.Id);
        Assert.Equal(A, born.Source);
        Assert.Equal(B, born.Destination);
    }

    /// <summary>
    /// <b>Turning a junction round keeps the priority it had</b>, including one the drawing
    /// cannot even say.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MEDIO 1 of the second review, measured there: 50 became 0 and 98 became 0, with nothing
    /// said. The chain was two copies of one translation meeting - the outward trip writes
    /// "alta" for every priority under 99, and reading that word back as a number gives 0 - and
    /// the test that guarded the turning round fixed the priority at 99, the one value where
    /// nothing is lost.
    /// </para>
    /// <para>
    /// <b>The intermediate values are the point</b>, so 50 and 98 are here and not only the two
    /// ends. 0 and 99 would pass under the old code as well: a theory that only carried them
    /// would be green on the defect.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(98)]
    [InlineData(99)]
    public void TurningAJunctionRoundKeepsThePriorityItHad(int priority)
    {
        const string heads = "endArrow=none;startArrow=block;";
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, priority)]);
        var xml = DrawioDocument.Build(opened, null)
            .Replace("endArrow=block;endFill=1;", heads, StringComparison.Ordinal);
        Assert.Contains(heads, xml, StringComparison.Ordinal);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened,
            NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Equal(J, Assert.Single(delta.DeletedJunctions));
        var born = Assert.Single(delta.NewJunctions);
        Assert.Equal(B, born.Source);
        Assert.Equal(A, born.Destination);
        Assert.Equal(priority, born.Priority);
        // E in silenzio: la giunzione conserva quello che aveva, quindi non c'e' niente da
        // dichiarare sulla priorita'. Una voce qui sarebbe rumore a ogni rovesciamento.
        Assert.DoesNotContain(delta.Notices,
            n => n.Message.Contains("priorit", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// L'altra meta': se la cella <b>dichiara</b> un peso diverso da quello che il numero
    /// dell'impianto significa, quello vince - il rifacimento non e' un posto dove i gesti si
    /// perdono. Senza questa riga, «conserva il peso» si sarebbe potuto scrivere come «non
    /// guardare mai la cella», che e' il difetto opposto.
    /// </summary>
    [Fact]
    public void TurningAJunctionRoundObeysAWeightTheDrawingDeclaresAnew()
    {
        const string heads = "endArrow=none;startArrow=block;";
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, 50)]);
        var xml = DrawioDocument.Build(opened, null)
            .Replace("endArrow=block;endFill=1;", heads, StringComparison.Ordinal)
            .Replace($"{DrawioDocument.PriorityAttribute}=\"{DrawioDocument.HighPriorityValue}\"",
                $"{DrawioDocument.PriorityAttribute}=\"{DrawioDocument.LowPriorityValue}\"",
                StringComparison.Ordinal);
        Assert.Contains($"{DrawioDocument.PriorityAttribute}=\"{DrawioDocument.LowPriorityValue}\"",
            xml, StringComparison.Ordinal);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened,
            NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        var born = Assert.Single(delta.NewJunctions);
        Assert.Equal(JunctionWeights.LowPriority, born.Priority);
    }

    /// <summary>
    /// <b>A cell marked <c>wcs="junction"</c> that the plant does NOT hold goes under the head
    /// rule too</b>: with no head, or with one at each end, it does not become a junction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// GRAVE 2 of the second review, measured there and red here before the repair. The rule used
    /// to be asked in ONE branch of this file - the branch where the plant still holds a junction
    /// for the arrow - so a marked cell the plant does not know fell straight through to
    /// <c>Writable</c> and was born as a junction, head or no head. No mutation could find it:
    /// the rule was not broken, it had never been written on this road.
    /// </para>
    /// <para>
    /// The drawing here is the one <see cref="DrawioDocument.Build"/> writes for a plant that HAD
    /// the junction, with the head taken off - anchors, style and identifier all real - and the
    /// plant handed to the delta no longer holds it. That is not a contrived state: it is exactly
    /// the canvas of any save after the one that deleted the junction, because the document does
    /// not come back to draw.io after a save.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("endArrow=none;", "no arrowhead")]
    [InlineData("endArrow=block;startArrow=block;", "BOTH ends")]
    public void ACellOfOursThePlantDoesNotHoldIsNotAJunctionWithoutAHeadEither(string headStyle, string expected)
    {
        var drawnPlant = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, 99)]);
        var xml = DrawioDocument.Build(drawnPlant, null)
            .Replace("endArrow=block;endFill=1;", headStyle, StringComparison.Ordinal);
        Assert.Contains(headStyle, xml, StringComparison.Ordinal);
        var opened = Graph(Item(A, 10, 20), Item(B, 500, 20));

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened,
            NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.NewJunctions);
        Assert.Empty(delta.DeletedJunctions);
        var notice = Assert.Single(delta.Notices, n => n.Id == J.ToString());
        Assert.Contains(expected, notice.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(delta.Refusals, r => r.Id == J.ToString());
        // And the line the operator is looking at stays on the canvas: it is not the plant's any
        // more - the plant has nothing to redraw it from - so it goes back to his drawing, which
        // is what the very notice above promises when it says "it stays a drawing".
        Assert.Contains(J.ToString(), delta.DecorationXml, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The save AFTER the one that took the head off does not bring the junction back.</b> Two
    /// deltas, the same drawing, and between them only what the first save did to the plant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// GRAVE 1 of the second review, measured in a real browser: take the head off and save - the
    /// junction is deleted and the report says so - then press Save again without touching
    /// anything, and a junction was written back, <b>with no head on the line</b>. The report of
    /// the first save ("to join them again, give the line a head and save") was denied by the
    /// second, which joined them without one. It needed no double click on purpose: the marked
    /// cell stays on the canvas, so ANY later save did it.
    /// </para>
    /// <para>
    /// It is the same defect as
    /// <see cref="ACellOfOursThePlantDoesNotHoldIsNotAJunctionWithoutAHeadEither"/> seen from the
    /// operator's side, and both are here because a rule written in one branch of two is not
    /// findable by mutating the branch that has it.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheSaveAfterTheHeadWasTakenOffDoesNotBringTheJunctionBack()
    {
        var withTheJunction = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, 99)]);
        var xml = DrawioDocument.Build(withTheJunction, null)
            .Replace("endArrow=block;endFill=1;", "endArrow=none;", StringComparison.Ordinal);

        var first = DrawioDelta.Compute(DrawioReader.Read(xml), withTheJunction, withTheJunction,
            NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Equal(J, Assert.Single(first.DeletedJunctions));
        Assert.Empty(first.NewJunctions);

        // The plant as the first save left it. The DRAWING is the same string: draw.io keeps the
        // canvas it had, so the cell is still marked and still has no head.
        var withoutIt = Graph(Item(A, 10, 20), Item(B, 500, 20));

        var second = DrawioDelta.Compute(DrawioReader.Read(xml), withoutIt, withoutIt,
            NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(second.NewJunctions);
        Assert.Empty(second.DeletedJunctions);
        Assert.Empty(second.Refusals);
        Assert.DoesNotContain(second.Notices,
            n => n.Message.Contains("new junction", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>The line stays the operator's drawing when its junction dies</b>, after a reload as well
    /// as inside the session that drew it.
    /// </summary>
    /// <remarks>
    /// MEDIO 2 of the second review, measured: the marked cell did not come back into the
    /// decoration, so at the next opening the line vanished from the canvas with nothing said -
    /// while the report was telling the operator to give that very line a head and save. It is
    /// repaired with the two GRAVE and not after them, because the notice they produce says "it
    /// stays a drawing", and a line that disappears would make that sentence false the same day it
    /// was written. The unmarked half of this - the arrow of the session - was already measured by
    /// <see cref="TakingTheHeadOffAnArrowOfThisSessionDeletesItsJunctionAndSaysSo"/>.
    /// </remarks>
    [Fact]
    public void TheLineWhoseJunctionWasDeletedGoesBackToTheDrawingAfterAReloadToo()
    {
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, 99)]);
        var xml = DrawioDocument.Build(opened, null)
            .Replace("endArrow=block;endFill=1;", "endArrow=none;", StringComparison.Ordinal);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened,
            NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Equal(J, Assert.Single(delta.DeletedJunctions));
        Assert.Contains(J.ToString(), delta.DecorationXml, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other side of the same rule: when the deletion is <b>refused</b> the plant keeps the
    /// junction, so the plant redraws the line and the decoration must NOT carry a second copy of
    /// it. One fact, one copy - the rule the unmarked arrows' round already lives by.
    /// </summary>
    [Fact]
    public void TheLineWhoseDeletionWasRefusedIsNotAlsoPutIntoTheDrawing()
    {
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, 99)]);
        var xml = DrawioDocument.Build(opened, null)
            .Replace("endArrow=block;endFill=1;", "endArrow=none;", StringComparison.Ordinal)
            .Replace("</root>", $"""<mxCell id="nota-1" value="giunzione {J} da rifare" style="text;html=1;" vertex="1" parent="1"><mxGeometry x="900" y="900" width="320" height="40" as="geometry"/></mxCell></root>""");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened,
            NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.DeletedJunctions);
        // The note carries the identifier as TEXT - that is what refused the deletion - so the
        // count is what matters here: the CELL is not there, only the note that names it.
        Assert.Single(delta.Refusals);
        Assert.DoesNotContain("""<object id="33333333-3333-3333-3333-333333333333""", delta.DecorationXml, StringComparison.Ordinal);
    }

    /// <summary>
    /// Il controllo del rumore, e vale quanto la voce: la giunzione disegnata come la disegniamo
    /// noi - <c>endArrow=block</c>, nessuna punta all'inizio - non produce <b>nessuna</b> riga. Chi
    /// riapre un impianto con cento giunzioni e salva non deve trovarsi cento voci sulle punte.
    /// </summary>
    [Fact]
    public void AJunctionDrawnTheWayWeDrawItSaysNothingAboutItsHead()
    {
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, 99)]);

        var delta = DrawioDelta.Compute(
            DrawioReader.Read(DrawioDocument.Build(opened, null)), opened, opened,
            NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.Notices);
        Assert.Empty(delta.Refusals);
    }

    /// <summary>
    /// La stessa cella di <see cref="ArrowByHand"/> con la punta scritta davanti, che e' dove
    /// <c>setCellStyles</c> la mette quando l'operatore la cambia dal pannello Formato o da Edit
    /// Style: la cella nata dal gesto quella chiave non ce l'ha affatto.
    /// </summary>
    private static string LineByHand(string id, string source, string target, string headStyle) =>
        $"""<mxCell id="{id}" style="{headStyle}edgeStyle=orthogonalEdgeStyle;rounded=0;html=1;exitX=1;exitY=0.5;exitDx=0;exitDy=0;entryX=0;entryY=0.5;entryDx=0;entryDy=0;" edge="1" parent="1" source="{source}" target="{target}"><mxGeometry relative="1" as="geometry"/></mxCell>""";

    /// <summary>
    /// La cella di una freccia come la fabbrica draw.io quando l'operatore la tira con il mouse:
    /// nessun <c>&lt;object&gt;</c> e nessun marchio - copiata da una misura in un browser vero il
    /// 2026-09-19, non inventata. Con <paramref name="hookedToPorts"/> falso e' la stessa freccia
    /// rilasciata in mezzo alla sagoma: lo stile non porta ne' <c>exitX</c> ne' <c>entryX</c>.
    /// </summary>
    private static string ArrowByHand(string id, string source, string target, bool hookedToPorts = true) =>
        $"""<mxCell id="{id}" style="edgeStyle=orthogonalEdgeStyle;rounded=0;html=1;{(hookedToPorts ? "exitX=1;exitY=0.5;exitDx=0;exitDy=0;entryX=0;entryY=0.5;entryDx=0;entryDy=0;" : "")}" edge="1" parent="1" source="{source}" target="{target}"><mxGeometry relative="1" as="geometry"/></mxCell>""";

    /// <summary>
    /// Il critico della revisione, prima meta'. Una cella che porta l'identificativo di un
    /// dispositivo noto ma un marchio storpiato produceva un rifiuto E una cancellazione insieme: il
    /// giro delle celle sconosciute nominava la cella, ma non diceva a quello delle cancellazioni
    /// che il disegno l'aveva comunque portata. Il dispositivo che l'operatore vede benissimo sulla
    /// tela sparirebbe dall'impianto, e senza chiedere.
    /// </summary>
    [Fact]
    public void ACellWithAMangledMarkOnAKnownDeviceIsRefusedAndNeverDeleted()
    {
        var opened = Graph(Item(A, 10, 20), Item(B, 500, 20));
        var xml = MangleMark(DrawioDocument.Build(opened, null), B, "devise");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.Deleted);
        var refusal = Assert.Single(delta.Refusals);
        Assert.Equal(B.ToString(), refusal.Id);
        Assert.Contains("mark", refusal.Reason);
    }

    /// <summary>La stessa trappola sulle giunzioni: un marchio storpiato non e' una cancellazione.</summary>
    [Fact]
    public void ACellWithAMangledMarkOnAKnownJunctionIsRefusedAndNeverDeleted()
    {
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, 99)]);
        var xml = MangleMark(DrawioDocument.Build(opened, null), J, "junctions");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.DeletedJunctions);
        Assert.Equal(J.ToString(), Assert.Single(delta.Refusals).Id);
    }

    /// <summary>
    /// Il critico, seconda meta' - ed era la piu' pericolosa, perche' qui non c'era nemmeno un
    /// rifiuto a coprire la perdita. Tolto il marchio, la cella scende nella decorazione e il delta
    /// non la vedeva affatto: il dispositivo restava sotto gli occhi dell'operatore sulla tela e
    /// spariva dall'impianto in silenzio. La regola generale che chiude ogni forma di questa
    /// trappola: non si cancella mai un dispositivo il cui identificativo compare ancora da qualche
    /// parte nell'XML tornato indietro.
    /// </summary>
    /// <remarks>
    /// <b>E adesso la cella esce anche dalla decorazione</b>, con la frase che lo dice. Il
    /// dispositivo non era mai cancellato, ma l'impianto ne ridisegna la sagoma sotto lo stesso
    /// identificativo: lasciando li' anche la cella spogliata, la tela tornava con DUE elementi
    /// sotto un identificativo solo - sullo schermo una sagoma sola, e chi la sposta ne muove una
    /// delle due. Misurato su 1920 strade dalla rete del disegno, dopo che i suoi soggetti sono
    /// stati estesi alle celle di dispositivo.
    /// <para>
    /// Togliere il marchio costa tre gesti in draw.io: Ctrl+M, il cestino della riga, Apply.
    /// </para>
    /// </remarks>
    [Fact]
    public void ADeviceWhoseIdIsStillSomewhereInTheDrawingIsNeverDeleted()
    {
        var opened = Graph(Item(A, 10, 20), Item(B, 500, 20));
        var label = B.ToString()[..4];
        var xml = DrawioDocument.Build(opened, null)
            .Replace($"""<object id="{B}" label="{label}" wcs="device">""", $"""<object id="{B}" label="{label}">""");
        Assert.Contains($"""<object id="{B}" label="{label}">""", xml);   // il marchio e' tolto davvero

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.Deleted);
        var refusal = Assert.Single(delta.Refusals);
        Assert.Equal(B.ToString(), refusal.Id);
        Assert.Contains("the mark was taken off this cell", refusal.Reason);
        Assert.Contains("it comes back as a device of ours", refusal.Reason);

        // E NESSUN DOPPIONE: la cella spogliata non e' anche nella decorazione, perche' la sagoma
        // la ridisegna l'impianto sotto quello stesso identificativo.
        Assert.DoesNotContain(B.ToString(), delta.DecorationXml, StringComparison.Ordinal);
    }

    /// <summary>
    /// L'altra meta' del punto c: quando l'identificativo compare in una cella che porta ancora un
    /// marchio nostro, ma storpiato, il rifiuto lo dice - e dice anche QUALE marchio. E' il caso
    /// che si rimedia in dieci secondi (Extras &gt; Edit Diagram, si riscrive il marchio), ma solo
    /// se il messaggio dice dove guardare.
    /// </summary>
    [Fact]
    public void TheStickyRefusalSaysTheMarkOfTheCellThatStillCarriesTheId()
    {
        var opened = Graph(Item(A, 10, 20), Item(B, 500, 20));
        var xml = MangleMark(DrawioDocument.Build(opened, null), B, "devise");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        // Il rifiuto e' quello della cella sconosciuta (il dispositivo non e' fra i cancellati:
        // ACellWithAMangledMarkOnAKnownDeviceIsRefusedAndNeverDeleted lo prova), e nomina il marchio.
        var refusal = Assert.Single(delta.Refusals);
        Assert.Contains("devise", refusal.Reason);
    }

    /// <summary>
    /// Punto a del compito 7b, il verso predefinito. Una freccia NUOVA, tirata dall'operatore, non
    /// porta l'attributo <c>wcsPriority</c>: nasce a priorita' bassa - che nel WCS e' il numero PIU'
    /// ALTO, 99 - e la scelta si <b>dichiara</b>. Non e' un rifiuto (la giunzione si crea davvero),
    /// e' una voce: abbiamo scelto noi al posto di chi disegna, e chi disegna deve poterlo leggere.
    /// </summary>
    [Fact]
    public void ANewArrowWithoutOurPriorityAttributeIsBornLowAndTheChoiceIsDeclared()
    {
        var opened = Graph(Item(A, 10, 20), Item(B, 500, 20));
        var xml = DrawioDocument.Build(opened, null)
            .Replace("</root>", $"""<object id="freccia-1" label="" wcs="junction"><mxCell style="exitX=0.96;exitY=0.5;entryX=0.04;entryY=0.5;" edge="1" parent="1" source="{A}" target="{B}"><mxGeometry relative="1" as="geometry"/></mxCell></object></root>""");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Equal(99, Assert.Single(delta.NewJunctions).Priority);
        Assert.Empty(delta.Refusals);
        // La voce della priorita' si sceglie per nome: accanto a lei ce n'e' un'altra, quella
        // dell'aggancio, che ogni giunzione nuova porta (si veda
        // ANewJunctionDeclaresThatItsDrawnAnchorIsNotKept).
        var notice = Assert.Single(delta.Notices, n => n.Message.Contains("low priority"));
        Assert.Equal("freccia-1", notice.Id);
        Assert.Contains("99", notice.Message);
    }

    /// <summary>
    /// L'altro verso del punto a, e quello controintuitivo: <c>wcsPriority="alta"</c> vale
    /// <b>0</b>, non 99 - <c>RouteGraph</c> esplora per priorita' crescente, quindi il numero piu'
    /// basso viene per primo (si veda <c>JunctionWeights</c>). Qui non c'e' niente da dichiarare: la
    /// priorita' l'ha scelta il disegno, non noi.
    /// </summary>
    [Fact]
    public void AnArrowThatDeclaresHighPriorityIsBornAtZero()
    {
        var opened = Graph(Item(A, 10, 20), Item(B, 500, 20));
        var xml = DrawioDocument.Build(opened, null)
            .Replace("</root>", $"""<object id="freccia-1" label="" wcs="junction" wcsPriority="alta"><mxCell style="exitX=0.96;exitY=0.5;entryX=0.04;entryY=0.5;" edge="1" parent="1" source="{A}" target="{B}"><mxGeometry relative="1" as="geometry"/></mxCell></object></root>""");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Equal(0, Assert.Single(delta.NewJunctions).Priority);
        // Sulla PRIORITA' non c'e' niente da dichiarare: l'ha scelta il disegno, non noi. La sola
        // voce che resta e' quella dell'aggancio, che ogni giunzione nuova porta: si conta, non si
        // cerca soltanto l'assenza della frase sbagliata - contare prende anche una voce spuria in
        // piu', che una DoesNotContain lascerebbe passare.
        Assert.Single(delta.Notices);
        Assert.DoesNotContain(delta.Notices, n => n.Message.Contains("low priority"));
        Assert.Empty(delta.Refusals);
    }

    /// <summary>
    /// Il giro completo del punto a: una giunzione ad alta priorita' dell'impianto va nel disegno e
    /// torna indietro - senza che nessuno la tocchi - senza essere degradata a bassa. Il rischio
    /// vero di un attributo nuovo e' che l'andata e il ritorno non si parlino: qui si misura la
    /// coppia, non le due meta' separate. La freccia si ricancella e si ridisegna identica (stesso
    /// attributo, estremi e agganci), cosi' il delta la propone come nuova e la priorita' letta si
    /// vede davvero.
    /// </summary>
    [Fact]
    public void AHighPriorityJunctionSurvivesTheRoundTripThroughTheDrawing()
    {
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, 0)]);
        var built = DrawioDocument.Build(opened, null);
        Assert.Contains("wcsPriority=\"alta\"", built);

        // La cella di J via, e una freccia nuova con lo stesso attributo che l'andata ha scritto.
        var start = built.IndexOf($"<object id=\"{J}\"", StringComparison.Ordinal);
        var end = built.IndexOf("</object>", start, StringComparison.Ordinal) + "</object>".Length;
        var xml = built.Remove(start, end - start).Replace("</root>",
            $"""<object id="nuova-1" label="" wcs="junction" wcsPriority="alta"><mxCell style="exitX=0.96;exitY=0.5;entryX=0.04;entryY=0.5;" edge="1" parent="1" source="{A}" target="{B}"><mxGeometry relative="1" as="geometry"/></mxCell></object></root>""");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Equal(0, Assert.Single(delta.NewJunctions).Priority);
    }

    /// <summary>
    /// Punto d del compito 7b. L'operatore sposta la freccia su un'ALTRA porta degli stessi due
    /// dispositivi: gli estremi non cambiano, quindi il delta non vede niente da riscrivere - e non
    /// ha nemmeno dove scriverlo, perche' <c>JunctionDto</c> porta origine, destinazione e
    /// priorita', non la porta. Cambiare il contratto non e' di questo compito; <b>tacere</b> si':
    /// il disegno mostrerebbe un aggancio che l'impianto non ha, e alla prima riapertura
    /// tornerebbe dov'era senza che nessuno abbia detto niente.
    /// </summary>
    [Fact]
    public void AJunctionMovedToAnotherPortOfTheSameTwoDevicesIsDeclaredNotSilentlyDropped()
    {
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, 99)]);
        // L'andata aggancia la porta "out" (x=96 su 100): qui la freccia viene spostata al centro.
        var xml = DrawioDocument.Build(opened, null).Replace("exitX=0.96;", "exitX=0.5;");
        Assert.Contains("exitX=0.5;", xml);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        // Niente si scrive e niente si cancella: la giunzione resta quella che era.
        Assert.Empty(delta.NewJunctions);
        Assert.Empty(delta.DeletedJunctions);
        var refusal = Assert.Single(delta.Refusals);
        Assert.Equal(J.ToString(), refusal.Id);
        Assert.Contains("ports", refusal.Reason);
    }

    /// <summary>
    /// Il contrario della prova qui sopra, per non trasformare il punto d in un rifiuto che compare
    /// a ogni salvataggio: una giunzione che nessuno ha toccato non deve dichiarare nessuno
    /// spostamento di porta. L'andata e il ritorno passano dagli stessi numeri arrotondati a due
    /// decimali, e la tolleranza deve coprirli.
    /// </summary>
    [Fact]
    public void AJunctionNobodyTouchedDeclaresNoPortChange()
    {
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, 99)]);

        var delta = DrawioDelta.Compute(DrawioReader.Read(DrawioDocument.Build(opened, null)), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.Refusals);
        Assert.Empty(delta.Notices);
    }

    /// <summary>
    /// L'altra meta' del punto d, e quella comune: non lo spostamento di una freccia gia'
    /// dell'impianto, ma <b>l'aggancio iniziale</b> di una freccia nuova. L'operatore la tira sul
    /// lato destro di A (<c>exitX=1</c>); l'impianto non memorizza la porta - <c>JunctionRequest</c>
    /// porta origine, destinazione e priorita' e basta - e alla riapertura <c>DrawioDocument.Build</c>
    /// la riscrive sulla porta del registro: la freccia salta altrove, ed e' parola per parola la
    /// ragione per cui il punto d non tace.
    /// <para>
    /// Non e' un rifiuto - la giunzione si crea davvero - ma una voce: come per la priorita',
    /// abbiamo scelto noi al posto di chi disegna.
    /// </para>
    /// <para>
    /// La seconda meta' della prova non crede all'avviso, lo <b>misura</b>: costruisce il disegno
    /// dell'impianto come sara' dopo la creazione e guarda dove finisce l'aggancio. Senza, la prova
    /// direbbe soltanto che una certa frase compare.
    /// </para>
    /// </summary>
    [Fact]
    public void ANewJunctionDeclaresThatItsDrawnAnchorIsNotKept()
    {
        var opened = Graph(Item(A, 10, 20), Item(B, 500, 20));
        // exitX=1 e' il lato destro della cella, non la porta "out" del registro (96 su 100).
        var xml = DrawioDocument.Build(opened, null).Replace("</root>",
            $"""<object id="freccia-1" label="" wcs="junction"><mxCell style="exitX=1;exitY=0.5;entryX=0;entryY=0.5;" edge="1" parent="1" source="{A}" target="{B}"><mxGeometry relative="1" as="geometry"/></mxCell></object></root>""");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        var created = Assert.Single(delta.NewJunctions);
        Assert.Empty(delta.Refusals);
        var notice = Assert.Single(delta.Notices, n => n.Message.Contains("registry's ports"));
        Assert.Contains("freccia-1", notice.Id);

        // E l'avviso dice il vero: l'impianto tiene solo gli estremi e la priorita', quindi il
        // disegno della prossima apertura rimette la freccia sulla porta del registro.
        var afterReopening = DrawioDocument.Build(
            Graph([Item(A, 10, 20), Item(B, 500, 20)],
                [new JunctionDto(J, created.Source, created.Destination, created.Priority)]), null);
        Assert.Contains("exitX=0.96;", afterReopening);
        Assert.DoesNotContain("exitX=1;", afterReopening);
    }

    /// <summary>
    /// La stessa voce, ma su un disegno con piu' frecce nuove: <b>una riga sola</b> che le nomina
    /// tutte, non una riga identica per ognuna. La condizione e' la stessa per ogni freccia nuova -
    /// draw.io non offre affatto i punti delle nostre porte - quindi ripeterla per freccia non
    /// aggiunge niente a chi legge e allunga il verbale proprio dove ci sono i rifiuti veri da
    /// trovare. Il verbale non deve diventare il posto in cui le cose importanti si nascondono.
    /// </summary>
    [Fact]
    public void ManyNewJunctionsShareOneAnchorNoticeThatNamesThemAll()
    {
        var opened = Graph(Item(A, 10, 20), Item(B, 500, 20), Item(C, 900, 20));
        var xml = DrawioDocument.Build(opened, null).Replace("</root>",
            $"""<object id="freccia-1" label="" wcs="junction"><mxCell style="exitX=1;exitY=0.5;entryX=0;entryY=0.5;" edge="1" parent="1" source="{A}" target="{B}"><mxGeometry relative="1" as="geometry"/></mxCell></object>"""
            + $"""<object id="freccia-2" label="" wcs="junction"><mxCell style="exitX=1;exitY=0.5;entryX=0;entryY=0.5;" edge="1" parent="1" source="{B}" target="{C}"><mxGeometry relative="1" as="geometry"/></mxCell></object>"""
            + $"""<object id="freccia-3" label="" wcs="junction"><mxCell style="exitX=1;exitY=0.5;entryX=0;entryY=0.5;" edge="1" parent="1" source="{C}" target="{A}"><mxGeometry relative="1" as="geometry"/></mxCell></object></root>""");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Equal(3, delta.NewJunctions.Count);
        var notice = Assert.Single(delta.Notices, n => n.Message.Contains("registry's ports"));
        // Raggruppata non vuol dire anonima: le tre frecce restano nominate.
        Assert.Contains("freccia-1", notice.Id);
        Assert.Contains("freccia-2", notice.Id);
        Assert.Contains("freccia-3", notice.Id);

        // E la misura che tiene il verbale corto: tre frecce nuove fanno QUATTRO voci - una per
        // freccia sulla priorita', che nomina ciascuna la sua e che non tutte le frecce portano, e
        // UNA sola sull'aggancio, condizione identica per tutte. Se domani l'aggancio tornasse una
        // voce per freccia, questa riga lo dice.
        Assert.Equal(4, delta.Notices.Count);
    }

    /// <summary>
    /// Il documento che <c>DrawioDocument.Build</c> scriverebbe per questo impianto, con la freccia
    /// della giunzione indicata rimessa all'identificativo che draw.io le aveva dato: e' la tela di
    /// chi ha appena creato quella giunzione e non ha ricaricato la pagina. Si parte da
    /// <c>Build</c>, e non da una freccia scritta a mano, perche' cosi' gli AGGANCI alle porte sono
    /// quelli veri e la prova misura l'identita', non la tolleranza degli agganci.
    /// </summary>
    private static string DrawnWithTheDrawioId(PlantGraph plant, Guid junctionId, string drawioId) =>
        DrawioDocument.Build(plant, null)
            .Replace($"""<object id="{junctionId}" """, $"""<object id="{drawioId}" """);

    /// <summary>
    /// Una freccia che porta ancora l'identificativo di draw.io - la giunzione e' nata in questa
    /// sessione, e da quando il documento non torna piu' indietro dopo un salvataggio la tela lo
    /// tiene fino al ricaricamento - si riconosce lo stesso, attraverso la mappa della sessione.
    /// Senza, il salvataggio successivo cancella quella giunzione e la ricrea: l'identita' ruota a
    /// ogni salvataggio, e ogni giro e' una finestra in cui un guasto la lascia cancellata e non
    /// ricreata.
    /// </summary>
    [Fact]
    public void AnArrowThatStillCarriesItsDrawioIdIsRecognizedThroughTheSessionMap()
    {
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, 99)]);
        var xml = DrawnWithTheDrawioId(opened, J, "freccia-1");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened,
            new Dictionary<string, Guid> { ["freccia-1"] = J }, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.DeletedJunctions);
        Assert.Empty(delta.NewJunctions);
        Assert.Empty(delta.Refusals);
        Assert.Empty(delta.Notices);
    }

    /// <summary>
    /// Riconosciuta vuol dire riconosciuta <b>anche</b> dal controllo del doppione. Una freccia che
    /// la mappa riconosce non e' avviata alla cancellazione, quindi la sua giunzione continua a
    /// impedire un secondo arco fra gli stessi due dispositivi - che sul disegno si sovrappone al
    /// primo, e che nessuno vedrebbe. Senza questa meta', il rimedio all'identita' aprirebbe un
    /// buco dove prima non c'era.
    /// </summary>
    [Fact]
    public void AJunctionRecognizedThroughTheSessionMapStillBlocksADuplicate()
    {
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, 99)]);
        var xml = DrawnWithTheDrawioId(opened, J, "freccia-1")
            .Replace("</root>", $"""<object id="freccia-2" label="" wcs="junction"><mxCell style="exitX=0.96;exitY=0.5;entryX=0.04;entryY=0.5;" edge="1" parent="1" source="{A}" target="{B}"><mxGeometry relative="1" as="geometry"/></mxCell></object></root>""");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened,
            new Dictionary<string, Guid> { ["freccia-1"] = J }, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.NewJunctions);
        Assert.Empty(delta.DeletedJunctions);
        var refusal = Assert.Single(delta.Refusals);
        Assert.Equal("freccia-2", refusal.Id);
        Assert.Contains("doubles", refusal.Reason);
    }

    /// <summary>
    /// La difesa di ultima istanza vale anche per le giunzioni nate in questa sessione: il marchio
    /// storpiato a mano (Extras &gt; Modifica diagramma) fa arrivare la freccia fra le celle non
    /// riconosciute, e senza la mappa il suo identificativo non dice piu' niente - la giunzione
    /// finirebbe cancellata mentre l'operatore la vede benissimo sulla tela.
    /// </summary>
    [Fact]
    public void AJunctionCreatedInThisSessionIsNotDeletedWhenItsMarkIsMangled()
    {
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, 99)]);
        var xml = MangleMark(DrawnWithTheDrawioId(opened, J, "freccia-1"), "freccia-1", "junctions");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened,
            new Dictionary<string, Guid> { ["freccia-1"] = J }, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.DeletedJunctions);
        var refusal = Assert.Single(delta.Refusals);
        Assert.Equal("freccia-1", refusal.Id);
        Assert.Contains("junctions", refusal.Reason);
    }

    /// <summary>
    /// Un dispositivo dentro un gruppo si <b>rifiuta</b>, non si scrive: nel formato mxGraph la sua
    /// geometria e' relativa al gruppo, non alla tela, e riscriverla tale e quale manda il
    /// dispositivo da un'altra parte dichiarando successo.
    /// </summary>
    /// <remarks>
    /// La misura nel browser vero e' in <c>DrawioGroupedCellsTest</c> - due dispositivi trascinati
    /// di +180/+110 pixel e scritti sull'impianto a (-6.96, -25.98) e (413.05, 244.02) - ma la
    /// regola va sorvegliata anche da vicino: le prove nel browser sono lente, e questa e' una
    /// riga sola di <c>DrawioDelta</c> che, tolta per sbaglio, non farebbe rosso nient'altro.
    /// <para>
    /// Il disegno qui porta i numeri del difetto, non quelli giusti: il dispositivo raggruppato e'
    /// costruito come se fosse stato spostato a (300, 400), cosi' che un delta che non guardi il
    /// <c>parent</c> abbia davvero qualcosa da scrivere - e sia rosso.
    /// </para>
    /// </remarks>
    [Fact]
    public void ADeviceInsideAGroupIsRefusedByNameAndNotWritten()
    {
        var opened = Graph(Item(A, 10, 20), Item(B, 500, 20));
        var xml = InsideAGroup(DrawioDocument.Build(Graph(Item(A, 300, 400), Item(B, 500, 20)), null), A);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.Moves);
        var refusal = Assert.Single(delta.Refusals);
        Assert.Equal(A.ToString(), refusal.Id);
        Assert.Contains("group", refusal.Reason, StringComparison.OrdinalIgnoreCase);
        // La via d'uscita, che e' la meta' utile del rifiuto: senza di lei l'operatore resta fermo.
        Assert.Contains("Ctrl+Shift+U", refusal.Reason, StringComparison.OrdinalIgnoreCase);

        // E il dispositivo che NON e' nel gruppo continua a passare: il rifiuto e' per cella, non
        // un blocco di tutto il salvataggio.
        Assert.DoesNotContain(delta.Deleted, id => id == B);
    }

    /// <summary>
    /// La difesa di ultima istanza applicata al caso nuovo: una cella rifiutata perche' raggruppata
    /// <b>non deve anche essere cancellata</b>. E' il difetto peggiore di questo file - gia'
    /// capitato una volta in questo ramo - perche' il dispositivo sparirebbe dall'impianto
    /// portandosi via le proprie giunzioni, e il verbale direbbe soltanto "non scritto".
    /// </summary>
    [Fact]
    public void ADeviceRefusedBecauseItIsGroupedIsNotAlsoDeleted()
    {
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, 99)]);
        var xml = InsideAGroup(DrawioDocument.Build(Graph([Item(A, 300, 400), Item(B, 500, 20)], [new JunctionDto(J, A, B, 99)]), null), A);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.Deleted);
        Assert.Empty(delta.DeletedJunctions);
        Assert.Empty(delta.Moves);
        Assert.Single(delta.Refusals);
    }

    /// <summary>
    /// L'altra meta' della domanda, e la ragione per cui il rifiuto NON e' per ogni cella marchiata:
    /// una <b>giunzione</b> dentro un gruppo non porta il difetto. Misurato nel browser il
    /// 2026-09-16 - raggruppando due dispositivi, draw.io porta nel gruppo anche la freccia fra
    /// loro - ma di una freccia questo delta non legge nessuna geometria: legge l'identificativo,
    /// gli estremi e gli agganci dello stile, che il <c>parent</c> non tocca.
    /// <para>
    /// La prova sta qui perche' e' il verso in cui si sbaglia in silenzio: rifiutarla direbbe di no
    /// a un gesto legittimo, e <b>cancellarla</b> sarebbe peggio ancora.
    /// </para>
    /// </summary>
    [Fact]
    public void AJunctionInsideAGroupIsStillRecognisedAndNotDeleted()
    {
        var plant = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, 99)]);
        var xml = InsideAGroup(DrawioDocument.Build(plant, null), A, B, J);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), plant, plant, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.DeletedJunctions);
        Assert.Empty(delta.NewJunctions);
        Assert.DoesNotContain(delta.Refusals, r => r.Id == J.ToString());
    }

    /// <summary>L'identificativo del gruppo finto: un id di draw.io qualunque, purche' non sia il livello predefinito.</summary>
    private const string GroupId = "d7n2k0YsZD8LkEQScBf0-1";

    /// <summary>
    /// Mette le celle indicate <b>dentro un gruppo</b>, come draw.io le restituisce dopo un Ctrl+G:
    /// il loro <c>parent</c> diventa quello del gruppo, e il gruppo compare come cella non marchiata
    /// accanto a loro.
    /// </summary>
    /// <remarks>
    /// La geometria dei figli <b>non</b> viene riportata nel sistema di riferimento del gruppo, e
    /// non serve che lo sia: quello che si sta provando e' che quei numeri non vengano letti
    /// affatto. Riscriverli qui vorrebbe dire duplicare nella prova la trasformazione che il
    /// prodotto ha deciso di non fare - un aiutante che ricalcola l'atteso e' cieco per costruzione.
    /// </remarks>
    private static string InsideAGroup(string xml, params Guid[] ids)
    {
        var document = System.Xml.Linq.XDocument.Parse(xml);
        var root = document.Descendants("root").Single();
        foreach (var id in ids)
        {
            var owner = root.Elements("object").SingleOrDefault(o => (string?)o.Attribute("id") == id.ToString());
            Assert.True(owner is not null, $"la cella {id} non e' nel disegno");
            owner!.Element("mxCell")!.SetAttributeValue("parent", GroupId);
        }

        root.Add(System.Xml.Linq.XElement.Parse(
            $"""<mxCell id="{GroupId}" value="" style="group" vertex="1" connectable="0" parent="1"><mxGeometry x="390" y="290" width="518.58" height="309.61" as="geometry"/></mxCell>"""));
        return document.ToString();
    }

    /// <summary>
    /// The net against the trap, and the reason part one had to come first. A junction the plant
    /// carries at 50 - neither of the two numbers we write - is reopened and saved with nobody
    /// laying a finger on it: <b>nothing at all</b> must be written.
    /// <para>
    /// It holds because the two halves of the round trip ask the same question of the same function:
    /// <c>DrawioDocument.Build</c> draws the arrow with the weight
    /// <c>JunctionWeights.FromPriority(50)</c> gives, and the comparison here compares the weight
    /// that came back against the weight the same function gives for the same 50. With the rule that
    /// used to live in <c>DrawioDocument</c> (<c>priority == 0</c>) the two halves disagreed: the
    /// arrow went out "bassa" and was read as a demand to make it low, against a plant the
    /// comparison called high - a difference that does not exist, and a silent write of 99 over a
    /// 50 nobody had touched. This test was seen failing exactly that way, with the wrong rule
    /// wired into the comparison, before the right one was put in.
    /// </para>
    /// </summary>
    [Fact]
    public void AJunctionAtAnInBetweenPriorityReopenedAndSavedUntouchedIsNotWrittenAtAll()
    {
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, 50)]);

        // The whole round trip, with no gesture in the middle: the drawing goes out and comes back
        // as it is.
        var delta = DrawioDelta.Compute(DrawioReader.Read(DrawioDocument.Build(opened, null)),
            opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.JunctionPriorities);
        Assert.Empty(delta.NewJunctions);
        Assert.Empty(delta.DeletedJunctions);
        Assert.Empty(delta.Refusals);
        Assert.Empty(delta.Notices);
    }

    /// <summary>
    /// The gesture of row 20 itself, in both directions: the operator changes <c>wcsPriority</c> by
    /// hand on an arrow the plant already knows (draw.io shows the attribute in "Edit Data"), and
    /// the change reaches the plant. Until this comparison existed the branch that recognizes the
    /// arrow never looked at <c>edge.Priority</c> at all: nothing was written and nothing was said.
    /// </summary>
    [Theory]
    [InlineData(99, "alta", 0)]
    [InlineData(0, "bassa", 99)]
    // The case that tells the two rules apart, and the reason this test exists at all: 50 is drawn
    // "alta" by the one true rule (JunctionWeights.FromPriority, since 50 < 99), never "bassa". The
    // cancelled inline rule (priority == 0) would have called it "bassa" and made this row blind.
    [InlineData(50, "bassa", 99)]
    public void TheWeightChangedByHandOnAKnownArrowIsWritten(int onThePlant, string drawn, int expected)
    {
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, onThePlant)]);
        var xml = DrawioDocument.Build(opened, null)
            .Replace($"wcsPriority=\"{WeightText(onThePlant)}\"", $"wcsPriority=\"{drawn}\"");
        // Anchors the substitution to a fact: a "before" guess that does not match what the drawing
        // really carries would make Replace match nothing, and the drawing would still say
        // WeightText(onThePlant) instead of drawn - this line catches that instead of letting the
        // asserts below fail for an unrelated reason (or, worse, pass by accident).
        Assert.Contains($"wcsPriority=\"{drawn}\"", xml);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        var change = Assert.Single(delta.JunctionPriorities);
        Assert.Equal(J, change.JunctionId);
        Assert.Equal(expected, change.NewPriority);
        // The junction is NOT remade: it keeps its identity, and its ends were never touched.
        Assert.Empty(delta.NewJunctions);
        Assert.Empty(delta.DeletedJunctions);
        Assert.Empty(delta.Refusals);
    }

    /// <summary>
    /// The ordinary case, and the one that runs on every single save: the arrow comes back declaring
    /// the weight it went out with. Nothing is written - a write per junction per save would be a
    /// storm of PUTs nobody asked for, and every one of them a chance to overwrite somebody else.
    /// </summary>
    [Fact]
    public void AnArrowThatComesBackDeclaringTheWeightItWentOutWithIsNotWritten()
    {
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, 99)]);

        var delta = DrawioDelta.Compute(DrawioReader.Read(DrawioDocument.Build(opened, null)),
            opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.JunctionPriorities);
        Assert.Empty(delta.Notices);
    }

    /// <summary>
    /// <b>Absent is not an order.</b> An arrow born in this session carries no <c>wcsPriority</c>:
    /// the operator drew it, we never wrote the attribute on it. Reading that absence as "make it
    /// low" would demote, at the save after, every junction ever created by hand - and it would do
    /// it silently, which is the defect this whole file is built against. Silence in, silence out:
    /// no write, and no notice either, because there is nothing to tell.
    /// </summary>
    [Fact]
    public void AnArrowWithNoPriorityAttributeIsNotAnOrderToLowerIt()
    {
        // The junction is high on the plant, and the arrow that comes back is the one the operator
        // drew in this session: recognized through the session map, without our attribute.
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, 0)]);
        var xml = DrawioDocument.Build(Graph([Item(A, 10, 20), Item(B, 500, 20)]), null).Replace("</root>",
            $"""<object id="freccia-1" label="" wcs="junction"><mxCell style="exitX=0.96;exitY=0.5;entryX=0.04;entryY=0.5;" edge="1" parent="1" source="{A}" target="{B}"><mxGeometry relative="1" as="geometry"/></mxCell></object></root>""");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened,
            new Dictionary<string, Guid> { ["freccia-1"] = J }, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.JunctionPriorities);
        Assert.Empty(delta.Notices);
        Assert.Empty(delta.Refusals);
        Assert.Empty(delta.NewJunctions);
        Assert.Empty(delta.DeletedJunctions);
    }

    /// <summary>
    /// <b>A value we do not know does not demote.</b> Somebody typed <c>wcsPriority="media"</c> by
    /// hand. On a junction being BORN the answer is the plant's default, low, and it is declared
    /// (<c>PriorityOf</c>); on one that already exists the same answer would be a demotion nobody
    /// asked for, so the plant's number stays. But it is <b>said</b>, naming the arrow and the
    /// value: without that line the operator walks away believing he changed the weight.
    /// </summary>
    [Fact]
    public void APriorityValueWeDoNotRecogniseLeavesTheJunctionAloneAndIsDeclared()
    {
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, 0)]);
        var xml = DrawioDocument.Build(opened, null).Replace($"wcsPriority=\"{WeightText(0)}\"", "wcsPriority=\"media\"");
        Assert.Contains("wcsPriority=\"media\"", xml);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.JunctionPriorities);
        Assert.Empty(delta.Refusals);
        var notice = Assert.Single(delta.Notices);
        Assert.Equal(J.ToString(), notice.Id);
        Assert.Contains("media", notice.Message);
        // And it says what the plant keeps, so that "not changed" is not just a claim.
        Assert.Contains("(0)", notice.Message);
    }

    /// <summary>
    /// Review finding 4: the notice above used to name <c>before.Priority</c> - the number read when
    /// the drawing was OPENED - while the "written" line the page builds elsewhere names the number
    /// the plant holds NOW. Here <c>opened</c> and <c>current</c> disagree on purpose (0 vs 99): if
    /// the notice still read from <c>opened</c> this would fail on "(0)", not "(99)".
    /// </summary>
    [Fact]
    public void AnUnrecognisedValueNamesTheCurrentPriorityNotTheOneAtOpening()
    {
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, 0)]);
        var current = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, 99)]);
        var xml = DrawioDocument.Build(opened, null).Replace($"wcsPriority=\"{WeightText(0)}\"", "wcsPriority=\"media\"");
        Assert.Contains("wcsPriority=\"media\"", xml);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, current, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.JunctionPriorities);
        var notice = Assert.Single(delta.Notices);
        Assert.Contains("(99)", notice.Message);
        Assert.DoesNotContain("(0)", notice.Message);
    }

    /// <summary>
    /// The two gestures together, and the one the brief asked to MEASURE instead of assuming: the
    /// arrow is re-hooked onto another device <b>and</b> its weight is changed in the same save. The
    /// re-hook branch remakes the junction - deletion plus creation - and that new junction is born
    /// through <c>PriorityOf</c>, so the new weight has to arrive <b>there</b>. What must not happen
    /// is the change being counted twice: once as a priority to write on the old junction, and once
    /// as the priority of the new one. The old junction is about to disappear, and a PUT on it would
    /// be a write onto a corpse - reported as done, and gone an instant later.
    /// </summary>
    [Fact]
    public void AnArrowReHookedAndReWeightedInTheSameSaveIsWrittenOnceThroughTheNewJunction()
    {
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20), Item(C, 900, 20)],
            [new JunctionDto(J, A, B, 99)]);
        var xml = DrawioDocument.Build(opened, null)
            .Replace($"target=\"{B}\"", $"target=\"{C}\"")
            .Replace($"wcsPriority=\"{WeightText(99)}\"", "wcsPriority=\"alta\"");
        Assert.Contains("wcsPriority=\"alta\"", xml);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        // Once, and through the junction that will exist: the new weight is on the creation.
        Assert.Empty(delta.JunctionPriorities);
        Assert.Equal(J, Assert.Single(delta.DeletedJunctions));
        var remade = Assert.Single(delta.NewJunctions);
        Assert.Equal(C, remade.Destination);
        Assert.Equal(0, remade.Priority);
        Assert.Empty(delta.Refusals);
    }

    /// <summary>
    /// <b>Both ends of a junction of ours dropped onto ONE device: refused, and the plant keeps
    /// the junction it had.</b> A device joined to itself is not a connection - the plant says so
    /// on its own route - so the re-hooking cannot be written, and this file's rule is that a
    /// re-hooking which cannot be written leaves the old junction where it is.
    /// </summary>
    /// <remarks>
    /// Third review, first MEDIO, measured against the real host afterwards: the delta proposed
    /// the loop, the deletion of the old junction was written FIRST, and
    /// <c>POST /plant/junctions</c> answered 400 - <i>"Una giunzione non puo' avere lo stesso
    /// dispositivo come origine e destinazione"</i>. The save stopped at step 2 of 3 with the
    /// connection gone and nothing in its place. The unmarked road never had it: there
    /// <c>BetweenTwoDevices</c> asks for two DIFFERENT devices in the reader.
    /// </remarks>
    [Fact]
    public void BothEndsOnOneDeviceIsRefusedAndTheJunctionTheyCameFromIsKept()
    {
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, 99)]);
        var xml = DrawioDocument.Build(opened, null).Replace($"target=\"{B}\"", $"target=\"{A}\"");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.NewJunctions);
        // La meta' che costava il dato: la vecchia NON si cancella.
        Assert.Empty(delta.DeletedJunctions);
        var refusal = Assert.Single(delta.Refusals);
        Assert.Equal(J.ToString(), refusal.Id);
        Assert.Contains("itself", refusal.Reason);
    }

    /// <summary>
    /// The same loop drawn from nothing - a marked cell the plant holds no junction for, both ends
    /// on one device: nothing is written, it is refused by name, and it stays the operator's
    /// drawing.
    /// </summary>
    [Fact]
    public void ALoopThePlantDoesNotHoldIsRefusedAndStaysADrawing()
    {
        var opened = Graph(Item(A, 10, 20), Item(B, 500, 20));
        var xml = DrawioDocument.Build(opened, null).Replace("</root>",
            $"""<object id="cappio-1" label="" wcs="junction"><mxCell style="endArrow=block;exitX=0.96;exitY=0.5;entryX=0.04;entryY=0.5;" edge="1" parent="1" source="{A}" target="{A}"><mxGeometry relative="1" as="geometry"/></mxCell></object></root>""");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.NewJunctions);
        var refusal = Assert.Single(delta.Refusals);
        Assert.Equal("cappio-1", refusal.Id);
        Assert.Contains("itself", refusal.Reason);
        Assert.Contains("cappio-1", delta.DecorationXml);
    }

    /// <summary>
    /// <b>A junction being REDONE with a weight nobody recognizes keeps its number, and says
    /// so.</b> Third review, second MEDIO: the branch existed and no test asked for it - a
    /// mutation that degraded it silently to 99 was green on the whole suite. What it protects is
    /// the repair of the round before, the intermediate priority surviving a re-hooking.
    /// </summary>
    [Fact]
    public void AJunctionRedoneWithAnUnrecognisedWeightKeepsItsNumberAndIsDeclared()
    {
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20), Item(C, 900, 20)],
            [new JunctionDto(J, A, B, 50)]);
        var xml = DrawioDocument.Build(opened, null)
            .Replace($"target=\"{B}\"", $"target=\"{C}\"")
            .Replace($"wcsPriority=\"{WeightText(50)}\"", "wcsPriority=\"media\"");
        Assert.Contains("wcsPriority=\"media\"", xml);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        var remade = Assert.Single(delta.NewJunctions);
        Assert.Equal(C, remade.Destination);
        // 50, non 99: la degradazione silenziosa e' proprio quello che non deve succedere.
        Assert.Equal(50, remade.Priority);
        Assert.Contains(delta.Notices, n => n.Id == remade.Id && n.Message.Contains("media") && n.Message.Contains("(50)"));
    }

    /// <summary>
    /// Third review, second minor finding: the number a redone junction keeps is read from
    /// <c>current</c> and not from <c>opened</c>, the same way the <c>SameEnds</c> branch answers
    /// the same question. Here the two disagree on purpose - 99 when the drawing was opened, 50
    /// now - and writing 99 back would undo, in silence, the change somebody else made meanwhile.
    /// </summary>
    [Fact]
    public void AJunctionRedoneKeepsThePriorityThePlantHasNowNotTheOneAtOpening()
    {
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20), Item(C, 900, 20)],
            [new JunctionDto(J, A, B, 99)]);
        var current = Graph([Item(A, 10, 20), Item(B, 500, 20), Item(C, 900, 20)],
            [new JunctionDto(J, A, B, 50)]);
        // Il disegno e' quello dell'apertura, e dichiara la stessa cosa che l'apertura diceva:
        // nessun gesto sul peso.
        var xml = DrawioDocument.Build(opened, null).Replace($"target=\"{B}\"", $"target=\"{C}\"");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, current, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        var remade = Assert.Single(delta.NewJunctions);
        Assert.Equal(50, remade.Priority);
    }

    /// <summary>
    /// <b>When the head could not be read, it is said.</b> Third review, first minor finding: an
    /// arrow of ours without a head between a device and a cell whose mark is mangled goes on
    /// being a junction - the prudent outcome, and unchanged - but nothing used to tell the
    /// operator that the head he had just taken off had no effect at all. Two drawings that look
    /// the same on screen, two different outcomes, and no line to tell them apart.
    /// </summary>
    [Fact]
    public void AHeadThatCouldNotBeReadIsDeclaredInsteadOfBeingSkippedInSilence()
    {
        var opened = Graph([Item(A, 10, 20), Item(B, 500, 20)], [new JunctionDto(J, A, B, 99)]);
        var xml = MangleMark(DrawioDocument.Build(opened, null), B, "devise")
            .Replace("endArrow=block", "endArrow=none");
        Assert.Contains("endArrow=none", xml);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        // L'esito prudente resta: la giunzione non si cancella.
        Assert.Empty(delta.DeletedJunctions);
        Assert.Empty(delta.NewJunctions);
        // E adesso lo dice, nominando la linea.
        Assert.Contains(delta.Notices, n => n.Id == J.ToString()
            && n.Message.Contains("the head was not read at all"));
    }

    // ---------------------------------------------------------------- i dispositivi nati da un rilascio

    /// <summary>
    /// La cella che un RILASCIO lascia sulla tela: l'identificativo e' quello di draw.io, non quello
    /// dell'impianto, ed e' proprio questo che rende necessaria la mappa di sessione.
    /// </summary>
    private static string WithADroppedCell(string document, string cellId, string mark, double x, double y)
    {
        var n = (double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return document.Replace("</root>",
            $"<object id=\"{cellId}\" label=\"\" wcs=\"{mark}\">"
            + "<mxCell style=\"x\" vertex=\"1\" parent=\"1\">"
            + $"<mxGeometry x=\"{n(x)}\" y=\"{n(y)}\" width=\"80\" height=\"80\" as=\"geometry\"/>"
            + "</mxCell></object></root>");
    }

    /// <summary>
    /// Un dispositivo nato da un rilascio si riconosce attraverso la mappa di sessione: lo
    /// spostamento della sua cella si scrive sul dispositivo giusto, la cella non finisce fra i
    /// "nuovi" e il dispositivo non finisce fra le cancellazioni.
    /// <para>
    /// Le tre affermazioni vanno insieme: senza la mappa il delta farebbe tutte e tre le cose
    /// sbagliate <b>nello stesso salvataggio</b> - cancellare il dispositivo e rifiutare la cella -
    /// e provarne una sola avrebbe lasciato verde la piu' grave.
    /// </para>
    /// </summary>
    [Fact]
    public void ADeviceBornFromADropIsRecognizedThroughTheSessionMap()
    {
        // Il dispositivo B esiste sull'impianto - lo ha creato il rilascio - ma NON e' nel documento
        // seminato: la sua cella e' quella che draw.io ha depositato, con l'identificativo suo.
        var opened = Graph(Item(A, 10, 20), Item(B, 500, 20, rotation: 0, width: 80, height: 80));
        var seeded = DrawioDocument.Build(Graph(Item(A, 10, 20)), null);
        var xml = WithADroppedCell(seeded, "drawio-1", "device", 300, 400);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened,
            NothingCreatedInThisSession, new Dictionary<string, Guid> { ["drawio-1"] = B });

        var move = Assert.Single(delta.Moves);
        Assert.Equal(B, move.ItemId);
        Assert.Equal(300, move.X!.Value, precision: 2);
        Assert.Equal(400, move.Y!.Value, precision: 2);
        Assert.Empty(delta.NewDevices);
        Assert.Empty(delta.Deleted);
    }

    /// <summary>
    /// Senza la mappa lo stesso disegno dice l'opposto, ed e' la misura che rende una prova quella
    /// qui sopra: il dispositivo finisce fra le cancellazioni e la cella fra i "nuovi". Se un domani
    /// il riconoscimento passasse per un'altra via, questa riga diventa rossa e dice che la mappa
    /// non serve piu' - cosa che va saputa, non scoperta.
    /// </summary>
    [Fact]
    public void WithoutTheSessionMapTheSameDrawingDeletesTheDeviceAndRefusesTheCell()
    {
        var opened = Graph(Item(A, 10, 20), Item(B, 500, 20, rotation: 0, width: 80, height: 80));
        var seeded = DrawioDocument.Build(Graph(Item(A, 10, 20)), null);
        var xml = WithADroppedCell(seeded, "drawio-1", "device", 300, 400);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened,
            NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Equal(B, Assert.Single(delta.Deleted));
        Assert.Equal("drawio-1", Assert.Single(delta.NewDevices).Id);
    }

    /// <summary>
    /// Il marchio storpiato a mano (Extras &gt; Edit Diagram) su una cella nata da un rilascio: la
    /// cella viene rifiutata per nome e il dispositivo <b>non</b> viene cancellato. E' la difesa di
    /// ultima istanza, che per una cella senza identificativo dell'impianto non puo' funzionare per
    /// la via dell'XML - il guid nel disegno non compare da nessuna parte - e passa dalla mappa.
    /// </summary>
    [Fact]
    public void AMangledMarkOnADroppedCellDoesNotDeleteTheDeviceItStandsFor()
    {
        var opened = Graph(Item(A, 10, 20), Item(B, 500, 20, rotation: 0, width: 80, height: 80));
        var seeded = DrawioDocument.Build(Graph(Item(A, 10, 20)), null);
        var xml = WithADroppedCell(seeded, "drawio-1", "devise", 300, 400);

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened,
            NothingCreatedInThisSession, new Dictionary<string, Guid> { ["drawio-1"] = B });

        Assert.Contains(delta.Refusals, r => r.Id == "drawio-1");
        Assert.Empty(delta.Deleted);
    }

    /// <summary>
    /// La cella di un dispositivo nato da un rilascio, messa dentro un gruppo: si rifiuta di
    /// scriverne la posizione - dentro un gruppo la geometria e' relativa al gruppo - e <b>non</b>
    /// si cancella il dispositivo. Prima della mappa quel ramo chiedeva <c>Guid.TryParse</c>, che su
    /// un identificativo di draw.io e' falso: la cella non finiva fra quelle viste, e il rifiuto
    /// diventava anche una cancellazione.
    /// </summary>
    [Fact]
    public void ADroppedCellInsideAGroupIsRefusedWithoutBeingDeleted()
    {
        var opened = Graph(Item(A, 10, 20), Item(B, 500, 20, rotation: 0, width: 80, height: 80));
        var seeded = DrawioDocument.Build(Graph(Item(A, 10, 20)), null);
        var xml = WithADroppedCell(seeded, "drawio-1", "device", 300, 400)
            .Replace("<mxCell style=\"x\" vertex=\"1\" parent=\"1\">",
                     "<mxCell style=\"x\" vertex=\"1\" parent=\"gruppo-7\">");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened,
            NothingCreatedInThisSession, new Dictionary<string, Guid> { ["drawio-1"] = B });

        var refusal = Assert.Single(delta.Refusals, r => r.Id == "drawio-1");
        Assert.Contains("group", refusal.Reason);
        Assert.Empty(delta.Deleted);
        Assert.Empty(delta.Moves);
    }

    /// <summary>
    /// An arrow drawn by hand between two cells, added to <paramref name="document"/>. The ends are
    /// given as CELL identifiers and not as guids on purpose: that is what the canvas really holds
    /// after a drop, and it is the whole point of the three measurements below.
    /// </summary>
    /// <remarks>
    /// The port anchors are written because a junction has to hook two ports to be writable at all
    /// (decision 3.3), and they are the registry's own 0.96/0.04 so that the arrow looks exactly
    /// like one the operator pulled between two ports. No <c>wcsPriority</c>: an arrow the operator
    /// has just drawn does not carry one, and putting one in would hide the notice that says so.
    /// </remarks>
    private static string WithAHandDrawnArrow(string document, string edgeId, string source, string target) =>
        document.Replace("</root>",
            $"""<object id="{edgeId}" label="" wcs="junction"><mxCell style="exitX=0.96;exitY=0.5;entryX=0.04;entryY=0.5;" edge="1" parent="1" source="{source}" target="{target}"><mxGeometry relative="1" as="geometry"/></mxCell></object></root>""");

    /// <summary>
    /// <b>Review finding 1.</b> An arrow from a SEEDED device to one born from a drop becomes a
    /// junction. The ends of an arrow are cell identifiers, exactly like the identifier of a device
    /// cell, and for a device dropped in this session they are draw.io's own: the translation is the
    /// session map, and it is the same one the recognition of the cell itself goes through
    /// (<see cref="ADeviceBornFromADropIsRecognizedThroughTheSessionMap"/>).
    /// <para>
    /// Measured before the repair: <c>NewJunctions</c> came back EMPTY and the arrow was refused
    /// with "non collega due dispositivi noti all'impianto" - which is the message sending the
    /// operator to look for a fault that is not there, because the plant knows both devices
    /// perfectly well. On a plant built from scratch - every device dropped in this session - that
    /// meant no junction at all could be drawn until the page was reloaded.
    /// </para>
    /// </summary>
    [Fact]
    public void AnArrowToADeviceBornFromADropBecomesAJunction()
    {
        var opened = Graph(Item(A, 10, 20), Item(B, 300, 400, rotation: 0, width: 80, height: 80));
        var seeded = DrawioDocument.Build(Graph(Item(A, 10, 20)), null);
        var xml = WithAHandDrawnArrow(
            WithADroppedCell(seeded, "drawio-1", "device", 300, 400), "freccia-1", A.ToString(), "drawio-1");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened,
            NothingCreatedInThisSession, new Dictionary<string, Guid> { ["drawio-1"] = B });

        var created = Assert.Single(delta.NewJunctions);
        Assert.Equal("freccia-1", created.Id);
        Assert.Equal(A, created.Source);
        Assert.Equal(B, created.Destination);
        Assert.Empty(delta.Refusals);
        Assert.Empty(delta.Deleted);
    }

    /// <summary>
    /// The same thing with BOTH ends born from a drop, which is the plant built from scratch the
    /// beta tester will really have in front of him: the first arrow of a brand new plant joins two
    /// cells neither of which carries an identifier of the plant's own kind. One end translated and
    /// the other not would still leave this one refused, so it is measured apart.
    /// </summary>
    [Fact]
    public void AnArrowBetweenTwoDevicesBornFromADropBecomesAJunction()
    {
        var opened = Graph(Item(A, 300, 400, rotation: 0, width: 80, height: 80),
            Item(B, 700, 400, rotation: 0, width: 80, height: 80));
        var seeded = DrawioDocument.Build(Graph([], []), null);
        var xml = WithAHandDrawnArrow(
            WithADroppedCell(WithADroppedCell(seeded, "drawio-1", "device", 300, 400),
                "drawio-2", "device", 700, 400),
            "freccia-1", "drawio-1", "drawio-2");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened,
            NothingCreatedInThisSession,
            new Dictionary<string, Guid> { ["drawio-1"] = A, ["drawio-2"] = B });

        var created = Assert.Single(delta.NewJunctions);
        Assert.Equal(A, created.Source);
        Assert.Equal(B, created.Destination);
        Assert.Empty(delta.Refusals);
        Assert.Empty(delta.Deleted);
    }

    /// <summary>
    /// The other half of the same defect, and the one that keeps coming back: the SECOND save. The
    /// junction already exists on the plant - the first save created it - and the arrow still
    /// carries draw.io's identifiers, both its own and its two ends'. Recognizing the arrow is not
    /// enough: its ENDS have to be translated too, or the junction looks re-hooked at every save
    /// and the operator is accused of a gesture he never made.
    /// <para>
    /// Measured before the repair: a refusal "la giunzione e' stata riagganciata, o staccata, nel
    /// disegno, ma la freccia nuova non collega due dispositivi noti all'impianto", at every save,
    /// on a canvas nobody had touched.
    /// </para>
    /// </summary>
    [Fact]
    public void AJunctionToADeviceBornFromADropIsNotReHookedAtTheNextSave()
    {
        var opened = Graph([Item(A, 10, 20), Item(B, 300, 400, rotation: 0, width: 80, height: 80)],
            [new JunctionDto(J, A, B, 99)]);
        var seeded = DrawioDocument.Build(Graph(Item(A, 10, 20)), null);
        var xml = WithAHandDrawnArrow(
            WithADroppedCell(seeded, "drawio-1", "device", 300, 400), "freccia-1", A.ToString(), "drawio-1");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened,
            new Dictionary<string, Guid> { ["freccia-1"] = J },
            new Dictionary<string, Guid> { ["drawio-1"] = B });

        Assert.Empty(delta.Refusals);
        Assert.Empty(delta.NewJunctions);
        Assert.Empty(delta.DeletedJunctions);
        Assert.Empty(delta.Notices);
    }

    /// <summary>
    /// And the re-hook that really is one: the operator drags the arrow's end off the seeded device
    /// and onto one born from a drop. It has to be redone - deletion plus creation, point b of task
    /// 7b - towards the RIGHT device, which is the one the session map names.
    /// <para>
    /// Before the repair this was refused instead, and the refusal said the new arrow "non collega
    /// due dispositivi noti all'impianto": the gesture had no effect, and the reason given for it
    /// was false.
    /// </para>
    /// </summary>
    [Fact]
    public void AJunctionReHookedOntoADeviceBornFromADropIsRedoneTowardsIt()
    {
        var opened = Graph(
            [Item(A, 10, 20), Item(B, 500, 20), Item(C, 900, 20, rotation: 0, width: 80, height: 80)],
            [new JunctionDto(J, A, B, 99)]);
        var seeded = DrawioDocument.Build(Graph(Item(A, 10, 20), Item(B, 500, 20)), null);
        var xml = WithAHandDrawnArrow(
            WithADroppedCell(seeded, "drawio-1", "device", 900, 20), J.ToString(), A.ToString(), "drawio-1");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened,
            NothingCreatedInThisSession, new Dictionary<string, Guid> { ["drawio-1"] = C });

        var created = Assert.Single(delta.NewJunctions);
        Assert.Equal(A, created.Source);
        Assert.Equal(C, created.Destination);
        Assert.Equal(J, Assert.Single(delta.DeletedJunctions));
        Assert.Empty(delta.Refusals);
    }

    /// <summary>
    /// The refusal the repair must NOT take away: an arrow pulled onto a cell that is not a device
    /// of the plant's at all - a copy still to be configured, a note - is still refused, and by
    /// name. Reading the ends through the session map widens what is recognized, it does not remove
    /// the question; without this line the repair could have been "accept anything" and nothing
    /// would have gone red.
    /// </summary>
    [Fact]
    public void AnArrowOntoACellTheSessionMapDoesNotKnowIsStillRefused()
    {
        var opened = Graph(Item(A, 10, 20), Item(B, 500, 20));
        var seeded = DrawioDocument.Build(Graph(Item(A, 10, 20), Item(B, 500, 20)), null);
        var xml = WithAHandDrawnArrow(seeded, "freccia-1", A.ToString(), "una-copia-3");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened,
            NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.NewJunctions);
        var refusal = Assert.Single(delta.Refusals);
        Assert.Equal("freccia-1", refusal.Id);
        Assert.Contains("does not join two devices the plant knows", refusal.Reason);
    }

    /// <summary>
    /// The mirroring: the plant has nowhere to keep it, so it is refused BY NAME instead of
    /// evaporating. And the refusal has to say WHICH way, because "mirror" alone does not let the
    /// operator recognize the gesture he made - the same reason the 45 degree refusals name the
    /// resize apart from the rotation.
    /// <para>
    /// The third case is the one that could have come out twice: <c>flipH</c> and <c>flipV</c> are
    /// two keys of the style, but mirroring both ways is ONE gesture, and <c>Assert.Single</c> here
    /// is what holds the report to one line.
    /// </para>
    /// <para>
    /// And it has to say HOW TO MAKE IT STOP (rilievo 1 of the review). The document does not come
    /// back to the canvas after a save, so the cell stays mirrored and this very line arrives again
    /// at every save of the session: without the remedy the operator reads the same refusal over and
    /// over with nothing telling him that taking the flip out of the style is all it takes - the
    /// same metre as the group refusal's "dissolve the group (Ctrl+Shift+U)".
    /// </para>
    /// <para>
    /// <b>The gesture named is Edit Style, and it used to be "Arrange &gt; Flip Horizontal / Flip
    /// Vertical", which in THIS page does not exist.</b> Measured on 2026-09-19 in the real page:
    /// the Editor opens draw.io with <c>ui=min</c>, where <c>ui.format</c> is null with an empty
    /// <c>formatContainer</c> - no Format panel, so no Arrange tab - and <c>ui.actions.actions</c>
    /// carries neither <c>flipH</c> nor <c>flipV</c>; the right-click menu on one device offers To
    /// Front, To Back, Cut, Copy, Duplicate, Delete, Lock/Unlock and Edit, with no Flip. What was
    /// measured to work, on the same device in the same page, is Ctrl+E: it opens Edit Style with
    /// the cell's style in it, and taking <c>flipH</c> out and pressing Apply really leaves the
    /// style without it. That is also the way out the arrow's refusal has always named, so the two
    /// lines now name one gesture - see
    /// <see cref="AMirroredJunctionIsRefusedWithoutBeingDeletedOrRecreated"/>, which no longer needs
    /// to say the device is told something else.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("flipH=1;", "mirrored horizontally")]
    [InlineData("flipV=1;", "mirrored vertically")]
    [InlineData("flipH=1;flipV=1;", "mirrored both ways")]
    public void AMirroredDeviceIsRefusedByNameAndTheRefusalSaysWhichWay(string flip, string expected)
    {
        var opened = Graph(Item(A, 10, 20));
        var mirrored = DrawioDocument.Build(opened, null).Replace("rotation=35;", flip + "rotation=35;");
        Assert.Contains(flip, mirrored);

        var delta = DrawioDelta.Compute(DrawioReader.Read(mirrored), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        var refusal = Assert.Single(delta.Refusals);
        Assert.Equal(A.ToString(), refusal.Id);
        Assert.Contains(expected, refusal.Reason);
        Assert.Contains(opened.Items[0].Name!, refusal.Reason);
        // The way out, named. Asserted on all three cases because it is the part the operator acts
        // on: a refusal that only says no is the one he reads once and stops reading.
        Assert.Contains("Ctrl+E", refusal.Reason);
        Assert.Contains("Edit Style", refusal.Reason);
        // And the way out that this page does not have must not come back. The two names are asked
        // for separately because the old line carried both, and a check on the pair alone would let
        // a half of it back in.
        Assert.DoesNotContain("Arrange", refusal.Reason);
        Assert.DoesNotContain("Flip Horizontal", refusal.Reason);
        Assert.DoesNotContain("Flip Vertical", refusal.Reason);
        // And it must not turn into a deletion: the drawing was carrying that device.
        Assert.Empty(delta.Deleted);
    }

    /// <summary>
    /// The proof that counts, and the reason the mirroring is refused instead of the whole cell:
    /// the operator mirrors AND moves in the same gesture, and the move must still be written. The
    /// house precedent is the port anchor of a junction - the rest goes to the plant, the part the
    /// plant cannot hold is declared - and this is that precedent applied to a device.
    /// </summary>
    [Fact]
    public void AMirroredDeviceThatWasAlsoMovedStillHasItsMoveWritten()
    {
        var opened = Graph(Item(A, 10, 20));
        var moved = DrawioDocument.Build(Graph(Item(A, 300, 20)), null).Replace("rotation=35;", "flipH=1;rotation=35;");

        var delta = DrawioDelta.Compute(DrawioReader.Read(moved), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        var geometry = Assert.Single(delta.Moves);
        Assert.Equal(300, geometry.X!.Value, precision: 2);
        // One decimal on Y and not two: the drawing writes its numbers with "0.##" and the return
        // trip through the envelope of a device rotated by 35 degrees lands on 19.99 (measured).
        // It is the rounding this class's remarks describe, not the mirroring.
        Assert.Equal(20, geometry.Y!.Value, precision: 1);
        Assert.Contains("mirrored horizontally", Assert.Single(delta.Refusals).Reason);
    }

    /// <summary>
    /// The other half, and the one that makes the refusal worth having: a device NOBODY mirrored
    /// produces no line at all. A check that answered "mirrored" for every cell would be a refusal
    /// on every device of every save, on a drawing the operator had not touched.
    /// </summary>
    [Fact]
    public void ADeviceThatWasNotMirroredIsNotAccusedOfAnything()
    {
        var opened = Graph(Item(A, 10, 20));
        var moved = DrawioDocument.Build(Graph(Item(A, 300, 20)), null);

        var delta = DrawioDelta.Compute(DrawioReader.Read(moved), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Single(delta.Moves);
        Assert.Empty(delta.Refusals);
    }

    /// <summary>
    /// Rilievo 2 of the review: a cell the plant does not know gets NO mirroring line, because the
    /// one it would get says the opposite of what happens. A copy pasted in draw.io keeps our
    /// <c>wcs="device"</c> mark with a new identifier, so it lands in <c>NewDevices</c> and the page
    /// answers "the save does not create it... It was not written": at the next opening that cell is
    /// gone, while the mirroring line promises "the shape comes back as it was".
    /// <para>
    /// Could this pass for the wrong reason? No: the same drawing without the flip is already the
    /// subject of <c>AMarkedDeviceWithAnUnknownIdBecomesANewDeviceToConfigure</c>, and here the
    /// <c>Single</c> on <c>NewDevices</c> holds the road the cell took. Take the guard out of
    /// <c>DrawioDelta</c> and this is red on <c>Refusals</c>, not on a setup detail.
    /// </para>
    /// </summary>
    [Fact]
    public void AMirroredCopyThePlantDoesNotKnowIsNotPromisedItsShapeBack()
    {
        var opened = Graph(Item(A, 10, 20));
        var xml = DrawioDocument.Build(opened, null)
            .Replace("</root>", """<object id="copia-1" label="" wcs="device"><mxCell style="shape=image;flipH=1;" vertex="1" parent="1"><mxGeometry x="700" y="700" width="130" height="184" as="geometry"/></mxCell></object></root>""");

        var delta = DrawioDelta.Compute(DrawioReader.Read(xml), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        // The reader DID see the mirroring - it is the delta that chooses not to speak about it.
        Assert.True(Assert.Single(DrawioReader.Read(xml).Devices, cell => cell.Id == "copia-1").FlipH);
        Assert.Equal("copia-1", Assert.Single(delta.NewDevices).Id);
        Assert.Empty(delta.Refusals);
    }

    /// <summary>
    /// A mirrored DECORATION is not our business, and there is nothing to declare: the drawer's own
    /// shapes go back to draw.io exactly as he wrote them, mirroring included, so nothing is lost.
    /// A refusal here would be a refusal with no loss behind it - and it would arrive on a legitimate
    /// gesture, which is how a report stops being read at all.
    /// </summary>
    [Fact]
    public void AMirroredDecorationIsNotRefusedAndKeepsItsMirror()
    {
        var opened = Graph(Item(A, 10, 20));
        const string Note = """<mxCell id="nota-1" value="Zona" style="text;html=1;flipH=1;flipV=1;" vertex="1" parent="1"><mxGeometry x="900" y="900" width="320" height="40" as="geometry"/></mxCell>""";

        var delta = DrawioDelta.Compute(DrawioReader.Read(DrawioDocument.Build(opened, Note)), opened, opened,
            NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        Assert.Empty(delta.Refusals);
        Assert.Contains("flipH=1;flipV=1;", delta.DecorationXml);
    }

    /// <summary>
    /// An arrow with a mirror in its style: the plant holds even less of it - the two ends and the
    /// priority - so the loss is the same and it is declared the same way. The arrow's own identity
    /// must survive it: a mirrored junction is not a junction to delete and recreate.
    /// <para>
    /// WHAT THIS PROVES, AND WHAT IT DOES NOT (rilievo 5 of the review, measured, no longer a
    /// doubt). This test builds its input with a <c>Replace</c> that puts <c>flipV=1;</c> into an
    /// EDGE's style, and draw.io on its own never produces that style: in the bundle we serve,
    /// <c>Graph.prototype.flipCells</c> (<c>src/wwwroot/drawio/js/app.min.js</c>, line 3302) sends
    /// edges to <c>flipEdgePoints</c> and passes to
    /// <c>toggleCellStyles(STYLE_FLIPH/STYLE_FLIPV)</c> only the cells that are NOT edges - so even
    /// where draw.io has Flip buttons, on an arrow they do not write <c>flipH</c>/<c>flipV</c> at
    /// all. And in THIS page there are no Flip buttons to begin with (measured on 2026-09-19: with
    /// <c>ui=min</c>, <c>ui.format</c> is null and <c>ui.actions.actions.flipH</c> is undefined -
    /// see <see cref="AMirroredDeviceIsRefusedByNameAndTheRefusalSaysWhichWay"/>). So from draw.io's
    /// own gestures this branch CANNOT fire: it proves the branch, not the product. It is kept
    /// because the flag is still writable by hand through Edit Style, which is the remedy the
    /// refusal names - the same one the device's refusal names since <c>Ctrl+E</c> was measured to
    /// be the way out that this page really has.
    /// </para>
    /// </summary>
    [Fact]
    public void AMirroredJunctionIsRefusedWithoutBeingDeletedOrRecreated()
    {
        var opened = Graph([Item(A, 10, 20, rotation: 0), Item(B, 500, 20, rotation: 0)],
            [new JunctionDto(J, A, B, 99)]);
        var mirrored = DrawioDocument.Build(opened, null).Replace("edgeStyle=none;", "edgeStyle=none;flipV=1;");
        Assert.Contains("flipV=1;", mirrored);

        var delta = DrawioDelta.Compute(DrawioReader.Read(mirrored), opened, opened, NothingCreatedInThisSession, NoDeviceDroppedInThisSession);

        var refusal = Assert.Single(delta.Refusals);
        Assert.Equal(J.ToString(), refusal.Id);
        Assert.Contains("mirrored vertically", refusal.Reason);
        Assert.Contains("arrow", refusal.Reason);
        // The remedy, and the only one that can work on an edge.
        Assert.Contains("Ctrl+E", refusal.Reason);
        Assert.Contains("Edit Style", refusal.Reason);
        Assert.DoesNotContain("Arrange", refusal.Reason);
        Assert.DoesNotContain("Flip Horizontal", refusal.Reason);
        Assert.Empty(delta.DeletedJunctions);
        Assert.Empty(delta.NewJunctions);
        Assert.Empty(delta.Moves);
    }

    /// <summary>
    /// Storpia il marchio della sola cella indicata, lasciando intatto il resto del disegno - e il
    /// resto della cella: da quando la giunzione porta anche <c>wcsPriority</c> dopo il marchio
    /// (compito 7b, punto a) la sostituzione non puo' piu' ancorarsi al '&gt;' finale del tag, che
    /// non viene piu' subito dopo il marchio. Si lavora sul solo tag di apertura, dall'id al primo
    /// '&gt;'.
    /// </summary>
    private static string MangleMark(string xml, Guid id, string mangled) =>
        MangleMark(xml, id.ToString(), mangled);

    private static string MangleMark(string xml, string id, string mangled)
    {
        var start = xml.IndexOf($"""<object id="{id}" """, StringComparison.Ordinal);
        Assert.True(start >= 0, $"la cella {id} non e' nel disegno");
        var end = xml.IndexOf('>', start);
        var opening = xml[start..end];

        var was = opening.Contains("wcs=\"junction\"") ? "wcs=\"junction\"" : "wcs=\"device\"";
        Assert.Contains(was, opening);
        return xml[..start] + opening.Replace(was, $"wcs=\"{mangled}\"") + xml[end..];
    }
}
