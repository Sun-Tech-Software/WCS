using System.Globalization;
using System.Text.RegularExpressions;
using Wcs.Engine.Api;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Il registro dei tipi: ci sono tutti quelli di serie, i codici sono stabili e distinti, e ogni
/// tipo porta la sua forma con le sue porte.
/// </summary>
public sealed class DeviceTypeRegistryTest
{
    [Fact]
    public void TheSevenBuiltInTypesAreThere()
    {
        var codes = DeviceTypeRegistry.All.Select(t => t.Code).ToList();
        Assert.Equal(
            ["automation", "loading", "buffer", "storage", "circular-distributor", "group-automation", "loading-and-buffer"],
            codes);
    }

    [Fact]
    public void TheNoneTypeIsNotInTheRegistry()
    {
        // None = 99 e' l'assenza di tipo, non un tipo: nella palette non ci va.
        Assert.DoesNotContain(DeviceTypeRegistry.All, t => t.NumericCode == (short)DeviceTypes.None);
    }

    [Fact]
    public void EveryTypeCarriesAShapePortsAndTheTwoSlots()
    {
        foreach (var type in DeviceTypeRegistry.All)
        {
            // Almeno una parte, e nessuna vuota: un tipo che non disegna niente sulla tela e' un
            // dispositivo invisibile, che si porta dietro in silenzio le giunzioni che lo toccano -
            // lo stesso guasto per cui esiste il ripiego del tipo sconosciuto in PlantCanvas.
            Assert.NotEmpty(type.Shape);
            foreach (var part in type.Shape)
            {
                Assert.False(string.IsNullOrWhiteSpace(part.Path));
                Assert.False(string.IsNullOrWhiteSpace(part.Color));
            }

            Assert.False(string.IsNullOrWhiteSpace(type.Label));
            Assert.NotEmpty(type.Ports);
            Assert.InRange(type.LoadUnitSlot.X, 0, 100);
            Assert.InRange(type.LoadUnitSlot.Y, 0, 100);
            Assert.InRange(type.StatusSlot.X, 0, 100);
            Assert.InRange(type.StatusSlot.Y, 0, 100);
            foreach (var port in type.Ports)
            {
                Assert.InRange(port.X, 0, 100);
                Assert.InRange(port.Y, 0, 100);
                Assert.Contains(port.Direction, new[] { "in", "out", "both" });
            }

            // Un tipo senza nessuna porta che possa originare (out/both) o ricevere (in/both) una
            // giunzione non e' disegnabile dall'Editor web: i due controlli di direzione di
            // PlantCanvas.OnPortMouseDown/OnPortMouseUp non gli lascerebbero mai cominciare ne'
            // finire un trascinamento. Vero oggi per tutti e sette (contato a mano), ma niente lo
            // fissava.
            Assert.Contains(type.Ports, p => p.Direction is "out" or "both");
            Assert.Contains(type.Ports, p => p.Direction is "in" or "both");
        }
    }

    [Fact]
    public void TheNumericCodesMatchTheEnum()
    {
        // Tutti e sette, confrontati col vero valore dell'enumeratore: non con la posizione
        // nell'elenco, che poteva restare verde anche con un codice scambiato.
        var expected = new Dictionary<string, DeviceTypes>
        {
            ["automation"] = DeviceTypes.Automation,
            ["loading"] = DeviceTypes.Loading,
            ["buffer"] = DeviceTypes.Buffer,
            ["storage"] = DeviceTypes.Storage,
            ["circular-distributor"] = DeviceTypes.CircularDistributor,
            ["group-automation"] = DeviceTypes.GroupOfAutomationDevices,
            ["loading-and-buffer"] = DeviceTypes.LoadingAndBuffer,
        };
        foreach (var (code, deviceType) in expected)
            Assert.Equal((short)deviceType, DeviceTypeRegistry.All.Single(t => t.Code == code).NumericCode);
    }

    /// <summary>
    /// Il colore e' una stringa esadecimale <c>#RRGGBB</c>, non un nome libero: un valore fuori
    /// forma (mancante il cancelletto, o con meno di sei cifre) non si vedrebbe a schermo come
    /// colore - si vedrebbe come niente, un dispositivo trasparente. Vale per <b>ogni parte</b>: il
    /// colore sta sulla parte, non sul tipo, e una parte sola con un colore storto basta a bucare la
    /// sagoma.
    /// </summary>
    [Fact]
    public void EveryTypeCarriesAWellFormedColour()
    {
        foreach (var type in DeviceTypeRegistry.All)
        {
            // Senza questa riga l'asserzione sta DENTRO il ciclo sulle parti, e un tipo con zero
            // parti la farebbe passare a vuoto: zero non e' una prova. Il gemello nell'host
            // (PlantGeometryEndpointsTest) la guardia ce l'ha gia'; qui mancava.
            Assert.NotEmpty(type.Shape);
            foreach (var part in type.Shape)
                Assert.Matches(@"^#[0-9a-fA-F]{6}$", part.Color);
        }
    }

    /// <summary>I sette colori scelti alla tappa B3-ter, uno per tipo.</summary>
    private static readonly Dictionary<string, string> ApprovedPalette = new()
    {
        ["automation"] = "#334155",
        ["loading"] = "#1d4ed8",
        ["buffer"] = "#0f766e",
        ["loading-and-buffer"] = "#0e7490",
        ["group-automation"] = "#15803d",
        ["storage"] = "#a16207",
        ["circular-distributor"] = "#4338ca",
    };

    /// <summary>I due colori delle frecce del proprietario: il significato di una freccia, non l'identita' di un tipo.</summary>
    private const string InboundGreen = "#15956b";
    private const string OutboundOrange = "#e57924";

    /// <summary>
    /// La tavolozza approvata, in un confronto contro valore vero - non solo contro la forma - cosi'
    /// un colore scambiato fra due tipi non passerebbe.
    /// <para>
    /// Con le sagome composte del proprietario un tipo porta <b>due</b> famiglie di colore, e la
    /// distinzione e' il punto: la STRUTTURA (il riquadro, l'ingranaggio, gli scaffali) porta il
    /// colore del tipo, perche' a impianto intero si legge solo la silhouette e senza un colore per
    /// tipo cinque sagome su sette tornerebbero a confondersi; le FRECCE portano invece i due colori
    /// del proprietario, uguali su ogni tipo che le porta, perche' dicono un verso e non un tipo.
    /// Una parte con un colore che non e' ne' l'uno ne' gli altri due e' un colore inventato, e qui
    /// fallisce.
    /// </para>
    /// </summary>
    [Fact]
    public void TheStructureCarriesTheTypeColourAndTheArrowsTheOwnersTwo()
    {
        // I sette colori devono essere DISTINTI, e da questa tappa conta piu' di prima: finche' le
        // sagome erano sette silhouette diverse, due tipi dello stesso colore si distinguevano lo
        // stesso; adesso sei su sette sono lo stesso riquadro con un ingranaggio dentro, e alla
        // scala dell'impianto il colore e' l'unico segno d'identita' rimasto. Senza questa riga la
        // tavolozza e' un elenco di letterali, e due tipi con lo stesso valore passerebbero.
        Assert.Equal(
            ApprovedPalette.Count,
            ApprovedPalette.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var (code, colour) in ApprovedPalette)
        {
            var shape = DeviceTypeRegistry.All.Single(t => t.Code == code).Shape;
            // Stessa ragione di EveryTypeCarriesAWellFormedColour: un tipo senza parti
            // farebbe passare a vuoto il confronto con la tavolozza approvata.
            Assert.NotEmpty(shape);

            // Il corpo, la prima parte, e' quello che si vede alla scala dell'impianto: e' lui a
            // dover portare il colore del tipo, non "almeno una parte".
            Assert.Equal(colour, shape[0].Color, ignoreCase: true);

            foreach (var part in shape)
                Assert.True(
                    string.Equals(part.Color, colour, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(part.Color, InboundGreen, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(part.Color, OutboundOrange, StringComparison.OrdinalIgnoreCase),
                    $"{code}: la parte con tracciato \"{part.Path[..Math.Min(24, part.Path.Length)]}...\" porta {part.Color}, " +
                    $"che non e' ne' il colore del tipo ({colour}) ne' uno dei due colori delle frecce " +
                    $"({InboundGreen}, {OutboundOrange}).");
        }
    }

    /// <summary>
    /// La mappatura decisa dal proprietario fra i suoi quattro elementi e i sette tipi: il verde
    /// entra, l'arancione esce, e stanno esattamente sui tipi che li devono portare. Non "almeno
    /// uno": anche l'ASSENZA e' provata, perche' una freccia verde comparsa su <c>automation</c>
    /// direbbe a chi guarda l'impianto che li' si carica, e non e' vero.
    /// </summary>
    [Fact]
    public void TheArrowsAreOnExactlyTheTypesTheOwnerMappedThem()
    {
        var green = new[] { "loading", "loading-and-buffer" };
        var orange = new[] { "buffer", "loading-and-buffer" };

        foreach (var type in DeviceTypeRegistry.All)
        {
            Assert.Equal(green.Contains(type.Code),
                type.Shape.Any(p => string.Equals(p.Color, InboundGreen, StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(orange.Contains(type.Code),
                type.Shape.Any(p => string.Equals(p.Color, OutboundOrange, StringComparison.OrdinalIgnoreCase)));
        }
    }

    /// <summary>
    /// La regola vera delle sagome composte, al posto di "una parte sola contornata" che il lotto
    /// delle parti aveva lasciato in eredita' da riscrivere qui.
    /// <para>
    /// <b>La prima parte e' il corpo</b>: contornata, e da sola tocca tutti e quattro i lati del
    /// riquadro unitario. Non e' una preferenza di disegno, e' la condizione su cui poggia la
    /// scelta fatta in <c>DrawioShape</c> dopo la misura in un browser vero: in draw.io il bianco
    /// del foglio va <b>solo alla prima parte</b>, perche' solo la prima parte E' il foglio su cui
    /// stanno le altre. Se un giorno la prima parte non coprisse piu' l'impronta, quel bianco
    /// coprirebbe meno del dovuto e questa prova lo direbbe prima che lo dica il proprietario.
    /// </para>
    /// <para>
    /// <b>Ogni parte sopra il corpo, se racchiude un'area, e' piena.</b> Una parte che racchiude
    /// un'area e non e' piena viene dipinta di bianco in draw.io e <b>cancella</b> l'ingranaggio
    /// sotto di se' - misurato in Chromium sui pixel dell'immagine vera, non dedotto: un dente a
    /// (25, 50) leggeva <c>#ffffff</c> invece di <c>#334155</c>. Le sole parti non piene ammesse
    /// sopra il corpo sono quelle di sole linee, come gli scaffali di <c>storage</c>, dove nessun
    /// sotto-tracciato racchiude niente: la soglia e' mezza unita' quadrata, cioe' cinque
    /// centomillesimi del riquadro unitario - qualunque area vera sta ordini di grandezza sopra
    /// (la freccia piu' piccola del registro ne misura circa quattrocento).
    /// </para>
    /// </summary>
    [Fact]
    public void TheBodyIsTheFirstPartAndEveryPartAboveItEnclosesNoAreaOrIsFilled()
    {
        // 4,5 = i 4 a cui il riquadro sta dal bordo (il contorno e' disegnato a cavallo del
        // tracciato, e a 0 meta' del tratto cadrebbe fuori dalla cella) piu' la mezza unita' di
        // tolleranza gia' usata altrove in questo file per l'approssimazione poligonale degli archi.
        const double margin = 4.5;
        foreach (var type in DeviceTypeRegistry.All)
        {
            Assert.NotEmpty(type.Shape);

            var body = type.Shape[0];
            Assert.False(body.Filled, $"{type.Code}: il corpo e' diventato pieno - in draw.io riceve il bianco del foglio, e un corpo pieno lo perderebbe.");
            var (minX, minY, maxX, maxY) = SvgPathFill.BoundingBox(SvgPathFill.ParseToPolygons(body.Path));
            Assert.True(minX <= margin && minY <= margin && maxX >= 100 - margin && maxY >= 100 - margin,
                $"{type.Code}: la prima parte, che e' il corpo, non copre l'impronta " +
                $"([{minX:0.##},{minY:0.##},{maxX:0.##},{maxY:0.##}]): il bianco che riceve in draw.io " +
                "vale solo perche' e' lei il foglio su cui stanno le altre parti.");

            foreach (var (part, index) in type.Shape.Select((p, i) => (p, i)).Skip(1))
            {
                if (part.Filled) continue;
                var area = SvgPathFill.LargestEnclosedArea(SvgPathFill.ParseToPolygons(part.Path));
                Assert.True(area < 0.5,
                    $"{type.Code}, parte {index}: racchiude un'area di {area:0.##} unita' quadrate e non e' piena. " +
                    "Sopra il corpo una parte cosi' non decora, cancella: in draw.io riceverebbe un riempimento " +
                    "e sulla tela nessuno, e le due superfici direbbero cose diverse. O la si dichiara piena, o " +
                    "la si scrive come sole linee.");
            }
        }
    }

    /// <summary>
    /// Il difetto trovato su <c>circular-distributor</c>: l'arco esterno e quello interno avevano
    /// bandiera di verso (<c>sweep</c>, il penultimo numero di ogni comando <c>A</c>) opposta - con
    /// la regola di riempimento predefinita due sotto-tracciati di verso opposto si annullano invece
    /// di sommarsi, e il cerchio interno diventava un buco invece di un disco pieno. Misurato in un
    /// browser vero prima di questa correzione (frazione riempita 0.416, l'area dell'anello, contro
    /// 0.785 del disco).
    /// <para>
    /// Una prima versione di questo test confrontava solo le bandiere di verso dei comandi "A" - una
    /// condizione sintattica, non geometrica (correzione alla revisione, seconda passata). Tre
    /// controesempi misurati l'hanno bocciata: la variante "solo la bandiera dell'arco interno
    /// girata" passava pur spostando il riquadro della sagoma fuori dal quadrato unitario; due
    /// poligoni di verso opposto (nessun comando "A" affatto) bucavano il centro senza che la guardia
    /// se ne accorgesse, perche' non c'era nessuna bandiera da leggere; e lo stesso difetto scritto
    /// con le virgole al posto degli spazi (SVG del tutto legale, e la tappa 5 apre il registro a un
    /// assembly esterno che non ha nessuna ragione di scrivere gli archi con gli spazi) sfuggiva alla
    /// espressione regolare. Oggi solo <c>circular-distributor</c> porta un comando "A": sei tipi su
    /// sette passavano senza che nulla li controllasse.
    /// </para>
    /// <para>
    /// Questo test prova la condizione onesta invece: parsa il tracciato con <see cref="SvgPathFill"/>
    /// (un parser minimo di test, non di produzione - <c>M</c>/<c>H</c>/<c>V</c>/<c>A</c>/<c>Z</c>,
    /// gli unici comandi che il registro usa oggi) e verifica, con la stessa regola di riempimento
    /// non-zero di un browser: che il riquadro che il tracciato disegna resta dentro il riquadro
    /// unitario 0..100 che <see cref="DeviceTypeDescriptor.Shape"/> dichiara; che i due punti
    /// dichiarati (<see cref="DeviceTypeDescriptor.LoadUnitSlot"/> e
    /// <see cref="DeviceTypeDescriptor.StatusSlot"/>) sono davvero dentro il riempimento; e che ogni
    /// porta e' davvero sul contorno del riempimento - non dentro, non fuori, entro mezza unita' (la
    /// stessa tolleranza gia' usata per gli archi, che una poligonale a 64 lati approssima). La prima
    /// condizione prende i due controesempi senza archi o con le virgole (il centro diventa un buco,
    /// qualunque sia la sintassi usata per scriverlo); la seconda prende quello con la sola bandiera
    /// girata (il centro resta pieno, ma il riquadro esce dal quadrato dichiarato - misurato:
    /// [8,-34,84,126] contro [8,8,84,84] del tracciato vero).
    /// </para>
    /// <para>
    /// I punti delle porte e i due <see cref="ShapePoint"/> erano prima provati da due test separati,
    /// a regex, che riconoscevano solo un rettangolo semplice ("M x1 y1 H x2 V y2 H x1 Z") o un
    /// cerchio: bastava una tacca o un fondo arrotondato - le sagome scelte alla tappa B3-ter per
    /// loading, buffer, loading-and-buffer - a far fallire il riconoscimento del tracciato invece che
    /// il controllo geometrico vero. Qui sotto la stessa poligonale gia' parsata per il centro serve
    /// anche per questi due controlli, qualunque sia la forma del tracciato: e' la stessa porta da cui
    /// alla tappa 5 entreranno le sagome dell'integratore.
    /// </para>
    /// <para>
    /// <b>Con piu' parti le tre condizioni non si applicano tutte allo stesso modo</b>, e distinguerle
    /// e' il punto. Il riquadro vale <b>parte per parte</b>: una parte che esce dal quadrato unitario
    /// esce, che le altre stiano dentro non la riporta dentro, e il verso sbagliato di un
    /// sotto-tracciato e' un difetto della sua parte. Le altre due valgono invece sull'<b>unione</b>:
    /// ogni parte e' un <c>&lt;path&gt;</c> a se', e fra <c>&lt;path&gt;</c> diversi non c'e' nessun
    /// annullamento - il punto dove si mostra l'unita' di carico basta che sia dentro il riempimento
    /// di <b>una</b> parte perche' la presa della tela lo raccolga, e una porta basta che stia sul
    /// contorno di <b>una</b> parte perche' sia sul bordo della sagoma. Chiedere "ogni parte contiene
    /// il centro" sarebbe una condizione che le sagome composte non possono soddisfare: una freccia
    /// disegnata sopra un ingranaggio non contiene il centro di niente.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryShapeKeepsItsCentreFilledAndItsOutlineInsideTheUnitSquare()
    {
        foreach (var type in DeviceTypeRegistry.All)
        {
            var parts = type.Shape.Select(p => SvgPathFill.ParseToPolygons(p.Path)).ToList();

            foreach (var (part, index) in parts.Select((p, i) => (p, i)))
            {
                var (minX, minY, maxX, maxY) = SvgPathFill.BoundingBox(part);
                // Mezza unita' di margine: le sagome vere toccano il bordo (per esempio X=4 o X=96), non
                // serve altro spazio, ma un confronto esatto con 0/100 romperebbe su un arrotondamento
                // dell'ultimo segmento della poligonale che approssima un arco.
                Assert.True(minX >= -0.5 && minY >= -0.5 && maxX <= 100.5 && maxY <= 100.5,
                    $"{type.Code}, parte {index}: il riquadro disegnato [{minX:0.##},{minY:0.##},{maxX:0.##},{maxY:0.##}] esce dal " +
                    "riquadro unitario 0..100 dichiarato per le parti - un sotto-tracciato ha preso il verso sbagliato.");
            }

            AssertFilled(parts, type.LoadUnitSlot.X, type.LoadUnitSlot.Y, type.Code, nameof(type.LoadUnitSlot));
            AssertFilled(parts, type.StatusSlot.X, type.StatusSlot.Y, type.Code, nameof(type.StatusSlot));

            foreach (var port in type.Ports)
            {
                var distance = parts.Min(part => SvgPathFill.DistanceToNearestEdge(part, port.X, port.Y));
                Assert.True(distance < 0.5,
                    $"{type.Code}: porta {port.Name} ({port.X},{port.Y}) a distanza {distance:0.##} dal contorno " +
                    "del riempimento - una porta sta esattamente sul bordo della propria sagoma, non staccata da " +
                    "una tacca o da un arrotondamento.");
            }
        }
    }

    /// <summary>
    /// Il foro dell'ingranaggio e' un buco vero, e serve a qualcosa: e' li' che il quadro dal vivo
    /// mostra l'unita' di carico (<see cref="DeviceTypeDescriptor.LoadUnitSlot"/>, che per tutti e
    /// sette e' il centro). Dove va l'unita' di carico la STRUTTURA della sagoma - cio' che porta il
    /// colore del tipo: il riquadro, l'ingranaggio, la freccia curva del distributore - non deve
    /// mettere una macchia piena del proprio colore.
    /// <para>
    /// Le frecce del proprietario fanno eccezione ed e' voluto: la punta della freccia verde arriva
    /// dentro il foro, e' cosi' che lui l'ha disegnata, e un'unita' di carico sopra una freccia si
    /// legge - sopra un disco pieno del colore del tipo no.
    /// </para>
    /// <para>
    /// Questa e' la prova che il foro c'e' <b>davvero</b>: e' un annullamento fra due versi opposti
    /// dentro lo stesso tracciato, e basta girare una bandiera di verso perche' l'ingranaggio
    /// diventi un disco pieno senza che nessuna condizione geometrica di sopra se ne accorga - il
    /// centro resta coperto dal riquadro, il riquadro resta dentro l'unita', le porte restano sul
    /// bordo. Senza questa riga il foro sarebbe sorvegliato soltanto dal confronto carattere per
    /// carattere del riscontro, che dice che il tracciato e' cambiato ma non che cosa e' cambiato.
    /// </para>
    /// </summary>
    [Fact]
    public void TheStructureLeavesTheCentreOfTheShapeHollow()
    {
        foreach (var type in DeviceTypeRegistry.All)
        {
            var structureColour = type.Shape[0].Color;
            foreach (var (part, index) in type.Shape.Select((p, i) => (p, i)).Skip(1))
            {
                if (!part.Filled || !string.Equals(part.Color, structureColour, StringComparison.OrdinalIgnoreCase)) continue;
                var polygons = SvgPathFill.ParseToPolygons(part.Path);
                Assert.False(SvgPathFill.IsPointFilled(polygons, type.LoadUnitSlot.X, type.LoadUnitSlot.Y),
                    $"{type.Code}, parte {index}: e' piena del colore del tipo e copre il punto dell'unita' di " +
                    $"carico ({type.LoadUnitSlot.X},{type.LoadUnitSlot.Y}). L'ingranaggio deve restare un anello: " +
                    "se il foro si richiude - basta la bandiera di verso del cerchio interno girata - l'unita' di " +
                    "carico finisce sopra una macchia piena e non si legge piu'.");
            }
        }
    }

    /// <summary>
    /// Dove il quadro dal vivo mostrera' lo STATO (<see cref="DeviceTypeDescriptor.StatusSlot"/>,
    /// (88, 60) per tutti e sette) non deve esserci nessuna parte piena: uno stato dipinto sopra
    /// una macchia del colore del tipo, o sopra una freccia, non si legge. Il commento del registro
    /// lo prometteva gia', ma con la formula "controllato tipo per tipo", cioe' da una persona: la
    /// stessa sorveglianza che questo lotto ha dichiarato insufficiente altrove.
    /// <see cref="DeviceTypeDescriptor.LoadUnitSlot"/> ha la propria prova
    /// (<see cref="TheStructureLeavesTheCentreOfTheShapeHollow"/>), questa e' la sua.
    /// <para>
    /// <b>E non lo chiede solo sulla sagoma com'e' scritta.</b> Da questo lotto una parte rigida
    /// (<see cref="ShapePart.Rigid"/>) non si stira con l'ingombro: su un dispositivo alto e
    /// stretto, nel riquadro unitario, e' come se venisse RIMPICCIOLITA verso il centro (50, 50) -
    /// su uno largo, verso il centro in orizzontale - mentre il punto dello stato resta dov'e'. Un
    /// punto che oggi cade fuori da un riempimento puo' quindi trovarcisi dentro domani: e' facile
    /// vederlo per un disco pieno, ma l'ingranaggio e' un ANELLO, e un anello che si restringe
    /// scopre da una parte e copre dall'altra. Per questo la prova ripete la domanda su piu'
    /// rapporti d'ingombro, compreso 122x191 - i 43 dispositivi su 51 dell'impianto vero.
    /// </para>
    /// </summary>
    [Fact]
    public void TheStatusPointStaysOffEveryFilledPartAtEveryShapeOfTheFootprint()
    {
        // (kx, ky) come li calcola RigidPartFit: il lato minore diviso ciascuno dei due. Il primo e'
        // la sagoma com'e' scritta, gli altri quattro sono ingombri alti/stretti e larghi/bassi -
        // 122x191 e 191x122 sono quelli veri, gli ultimi due un caso estremo per parte.
        (double Kx, double Ky)[] fits =
        [
            (1, 1),
            (1, 122 / 191.0), (122 / 191.0, 1),
            (1, 0.2), (0.2, 1),
        ];

        // Una parte elastica si stira con l'ingombro, quindi resta al proprio posto nel riquadro
        // unitario: per lei la sagoma com'e' scritta e' l'unico caso che esista.
        (double Kx, double Ky)[] asWritten = [(1, 1)];

        foreach (var type in DeviceTypeRegistry.All)
        {
            foreach (var (part, index) in type.Shape.Select((p, i) => (p, i)))
            {
                if (!part.Filled) continue;
                var polygons = SvgPathFill.ParseToPolygons(part.Path);

                foreach (var (kx, ky) in part.Rigid ? fits : asWritten)
                {
                    var fitted = polygons
                        .Select(sub => sub.Select(p => (X: 50 + (p.X - 50) * kx, Y: 50 + (p.Y - 50) * ky)).ToList())
                        .ToList();

                    Assert.False(SvgPathFill.IsPointFilled(fitted, type.StatusSlot.X, type.StatusSlot.Y),
                        $"{type.Code}, parte {index}: e' piena e copre il punto dello stato " +
                        $"({type.StatusSlot.X},{type.StatusSlot.Y}) su un ingombro che la riduce di " +
                        $"({kx:0.###}, {ky:0.###}). Lo stato va dipinto su bianco: sopra una parte piena " +
                        "non si legge, e su un ingombro rettangolare le parti rigide si spostano rispetto " +
                        "al punto, che invece resta dov'e'.");
                }
            }
        }
    }

    /// <summary>Dentro il riempimento di <b>almeno una</b> parte: si veda il commento della prova che chiama.</summary>
    private static void AssertFilled(List<List<List<(double X, double Y)>>> parts, double x, double y, string code, string pointName)
    {
        var filled = parts.Any(part => SvgPathFill.IsPointFilled(part, x, y));
        Assert.True(filled,
            $"{code}: {pointName} ({x},{y}) non e' dentro il riempimento di nessuna parte - o due sotto-tracciati di " +
            "verso opposto si annullano invece di sommarsi (lo stesso buco trovato su circular-distributor), oppure " +
            "il punto cade fuori dalla sagoma vera (lo stesso scarto trovato sullo StatusSlot di buffer, 0,13 unita' " +
            "fuori dal fondo arrotondato).");
    }

    /// <summary>
    /// L'invariante che il compito "le sagome riempiono l'impronta" introduce: il tracciato di
    /// <c>automation</c> prima di questo compito riempiva solo il 32% dell'altezza della propria
    /// impronta (misurato sui dispositivi veri dell'impianto) pur avendo il centro pieno e il riquadro
    /// dentro 0..100 - le due prove sopra non se ne accorgevano, perche' nessuna delle due controlla
    /// quanto del riquadro unitario il contorno raggiunge davvero. Qui si prova che ogni tracciato
    /// tocca tutti e quattro i lati del riquadro 0..100 entro un margine.
    /// <para>
    /// Il margine e' scelto sui numeri veri dei tracciati, non indovinato. La giustificazione di
    /// prima - "il piu' lontano e' storage, che parte a 14 unita' (M 8 14 H 92 V 86)" - **non vale
    /// piu'**: da quando le sagome sono composte, tutti e sette portano lo stesso corpo
    /// "M 4 4 H 96 V 96 H 4 Z", che parte a 4. Margine 4,5 = 4 piu' la stessa mezza unita' di
    /// tolleranza gia' usata sopra per l'approssimazione poligonale degli archi (rilevante per
    /// buffer, loading-and-buffer e circular-distributor, il cui apice non cade sempre esattamente
    /// su uno dei 64 segmenti campionati). Tenere 14,5 avrebbe lasciato passare una sagoma che
    /// scopre il 14% per lato, tre volte piu' di qualunque sagoma vera: un margine che nessun caso
    /// reale sfiora non sorveglia niente.
    /// </para>
    /// <para>
    /// L'impronta e' quella della sagoma <b>intera</b>, cioe' l'unione delle parti: sono le maniglie
    /// di modifica ai quattro angoli dell'impronta a dover trovare il disegno accanto a se', e a chi
    /// guarda non importa quale parte arrivi fin la'. Una parte piccola in mezzo a una grande e'
    /// legittima; una sagoma che tutta insieme lascia un lato scoperto no.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryShapeTouchesAllFourSidesOfItsFootprint()
    {
        const double margin = 4.5;
        foreach (var type in DeviceTypeRegistry.All)
        {
            var subpaths = type.Shape.SelectMany(p => SvgPathFill.ParseToPolygons(p.Path)).ToList();
            var (minX, minY, maxX, maxY) = SvgPathFill.BoundingBox(subpaths);

            Assert.True(minX <= margin,
                $"{type.Code}: il lato sinistro dell'impronta non e' toccato (minX={minX:0.##}, margine {margin}).");
            Assert.True(minY <= margin,
                $"{type.Code}: il lato superiore dell'impronta non e' toccato (minY={minY:0.##}, margine {margin}).");
            Assert.True(maxX >= 100 - margin,
                $"{type.Code}: il lato destro dell'impronta non e' toccato (maxX={maxX:0.##}, margine {margin}).");
            Assert.True(maxY >= 100 - margin,
                $"{type.Code}: il lato inferiore dell'impronta non e' toccato (maxY={maxY:0.##}, margine {margin}).");
        }
    }
}

/// <summary>
/// Un parser e un riempitore SVG ridotti all'osso, solo per <see cref="DeviceTypeRegistryTest"/>: non
/// e' pensato per disegnare, solo per rispondere a tre domande su un tracciato del registro - il suo
/// riquadro, se un punto dato cade dentro il riempimento con la regola "non-zero" che un browser
/// userebbe, e quanto un punto dato e' lontano dal contorno del riempimento. Comandi gestiti:
/// <c>M</c>, <c>H</c>, <c>V</c>, <c>L</c>, <c>A</c>, <c>Z</c> - i cinque che
/// <see cref="DeviceTypeRegistry"/> scrive oggi (nessuno usa <c>L</c>) piu' <c>L</c> stesso, tenuto
/// per parita' con la copia di questo file in <c>Wcs.WebEditor.Test</c>, che ne ha bisogno per la X
/// del tipo sconosciuto; un comando diverso solleva, cosi' un tracciato futuro che ne usasse uno non
/// gestito fallisce rumorosamente invece di essere ignorato in silenzio.
/// </summary>
internal static class SvgPathFill
{
    public static List<List<(double X, double Y)>> ParseToPolygons(string d)
    {
        var tokens = Tokenize(d);
        var subpaths = new List<List<(double X, double Y)>>();
        List<(double X, double Y)>? current = null;
        double x = 0, y = 0;
        var i = 0;

        while (i < tokens.Count)
        {
            var cmd = tokens[i++];
            switch (cmd)
            {
                case "M":
                    x = ReadNumber(tokens, ref i);
                    y = ReadNumber(tokens, ref i);
                    current = [(x, y)];
                    subpaths.Add(current);
                    break;
                case "H":
                    x = ReadNumber(tokens, ref i);
                    Current(current).Add((x, y));
                    break;
                case "V":
                    y = ReadNumber(tokens, ref i);
                    Current(current).Add((x, y));
                    break;
                case "L":
                    x = ReadNumber(tokens, ref i);
                    y = ReadNumber(tokens, ref i);
                    Current(current).Add((x, y));
                    break;
                case "A":
                    var rx = ReadNumber(tokens, ref i);
                    var ry = ReadNumber(tokens, ref i);
                    _ = ReadNumber(tokens, ref i); // x-axis-rotation: sempre 0 nel registro di oggi, si veda AppendArc
                    var largeArc = ReadNumber(tokens, ref i) != 0;
                    var sweep = ReadNumber(tokens, ref i) != 0;
                    var endX = ReadNumber(tokens, ref i);
                    var endY = ReadNumber(tokens, ref i);
                    AppendArc(Current(current), x, y, rx, ry, largeArc, sweep, endX, endY);
                    x = endX;
                    y = endY;
                    break;
                case "Z":
                    // Chiusura implicita: WindingNumber collega gia' l'ultimo punto della poligonale
                    // al primo, non serve aggiungere un punto di chiusura esplicito.
                    break;
                default:
                    throw new InvalidOperationException(
                        $"SvgPathFill non gestisce il comando '{cmd}' (tracciato: \"{d}\") - e' un parser di test, non di produzione.");
            }
        }

        return subpaths;
    }

    private static List<(double X, double Y)> Current(List<(double X, double Y)>? current) =>
        current ?? throw new InvalidOperationException("comando di disegno prima di un M: nessun sotto-tracciato aperto");

    public static (double MinX, double MinY, double MaxX, double MaxY) BoundingBox(List<List<(double X, double Y)>> subpaths)
    {
        var points = subpaths.SelectMany(p => p).ToList();
        return (points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
    }

    /// <summary>Regola di riempimento "non-zero", la stessa che SVG usa quando <c>fill-rule</c> non e' dichiarato (nessuna sagoma del registro lo dichiara).</summary>
    public static bool IsPointFilled(List<List<(double X, double Y)>> subpaths, double px, double py)
    {
        var winding = 0;
        foreach (var polygon in subpaths)
        {
            for (var i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Count];
                if (a.Y <= py)
                {
                    if (b.Y > py && IsLeft(a, b, px, py) > 0) winding++;
                }
                else
                {
                    if (b.Y <= py && IsLeft(a, b, px, py) < 0) winding--;
                }
            }
        }

        return winding != 0;
    }

    /// <summary>
    /// L'area con segno del piu' grande sotto-tracciato, in valore assoluto: quanta superficie il
    /// tracciato racchiude davvero. Serve a distinguere una parte che DIPINGE un'area - un
    /// ingranaggio, una freccia - da una fatta di sole linee, come gli scaffali di <c>storage</c>,
    /// dove ogni sotto-tracciato e' un segmento e l'area e' zero. La differenza non e' estetica: una
    /// parte che racchiude un'area e non e' piena verrebbe dipinta di bianco in draw.io e
    /// cancellerebbe cio' che le sta sotto (misurato: si veda <c>DrawioShape.DecorationFill</c>).
    /// </summary>
    public static double LargestEnclosedArea(List<List<(double X, double Y)>> subpaths)
    {
        var largest = 0.0;
        foreach (var polygon in subpaths)
        {
            var twice = 0.0;
            for (var i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Count];
                twice += a.X * b.Y - b.X * a.Y;
            }

            largest = Math.Max(largest, Math.Abs(twice) / 2);
        }

        return largest;
    }

    private static double IsLeft((double X, double Y) a, (double X, double Y) b, double px, double py) =>
        (b.X - a.X) * (py - a.Y) - (px - a.X) * (b.Y - a.Y);

    /// <summary>
    /// Distanza minima di un punto dal contorno del riempimento: il piu' corto fra i segmenti di ogni
    /// sotto-tracciato, poligonale compresa (chiusura implicita fra l'ultimo punto e il primo, come in
    /// <see cref="IsPointFilled"/>). Non distingue un sotto-tracciato che delimita l'area (il
    /// rettangolo esterno) da uno che e' solo una linea interna (per esempio il divisorio di buffer):
    /// una porta vicina a una linea interna quanto al bordo vero darebbe un falso verde, ma nel
    /// registro di oggi le porte stanno sempre lontane dalle linee interne - sono agli estremi della
    /// sagoma, i divisori a meta'.
    /// </summary>
    public static double DistanceToNearestEdge(List<List<(double X, double Y)>> subpaths, double px, double py)
    {
        var nearest = double.MaxValue;
        foreach (var polygon in subpaths)
        {
            for (var i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Count];
                var distance = DistanceToSegment(a, b, px, py);
                if (distance < nearest) nearest = distance;
            }
        }

        return nearest;
    }

    private static double DistanceToSegment((double X, double Y) a, (double X, double Y) b, double px, double py)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        if (dx == 0 && dy == 0) return Math.Sqrt(Math.Pow(px - a.X, 2) + Math.Pow(py - a.Y, 2));

        var t = ((px - a.X) * dx + (py - a.Y) * dy) / (dx * dx + dy * dy);
        t = Math.Clamp(t, 0, 1);
        var closestX = a.X + t * dx;
        var closestY = a.Y + t * dy;
        return Math.Sqrt(Math.Pow(px - closestX, 2) + Math.Pow(py - closestY, 2));
    }

    private static List<string> Tokenize(string d)
    {
        // Le virgole sono un separatore legale in SVG quanto lo spazio (si veda il controesempio
        // della revisione, "A 42,42 0 1,1 ..."): normalizzarle a spazi prima di leggere i numeri
        // evita di doverle riconoscere in ogni punto del parser.
        var normalized = d.Replace(',', ' ');
        return Regex.Matches(normalized, @"[MHVLAZmhvlaz]|-?\d*\.?\d+(?:[eE][-+]?\d+)?")
            .Select(m => m.Value)
            .ToList();
    }

    private static double ReadNumber(List<string> tokens, ref int i) =>
        double.Parse(tokens[i++], CultureInfo.InvariantCulture);

    /// <summary>
    /// Converte un comando <c>A</c> (arco ellittico, parametrizzato dagli estremi) in una poligonale,
    /// con la conversione da estremi a centro della specifica SVG 1.1 (appendice F.6.5) -
    /// semplificata per <c>x-axis-rotation = 0</c>: nessun tracciato del registro di oggi ruota
    /// l'ellisse stessa (la rotazione del dispositivo ruota il &lt;g&gt; del nodo, non l'arco dentro
    /// il suo riquadro unitario), quindi il termine che quella rotazione introdurrebbe sarebbe sempre
    /// l'identita' - ometterlo tiene il parser piu' semplice senza perdere nessun caso che questo
    /// registro possa scrivere oggi.
    /// </summary>
    private static void AppendArc(List<(double X, double Y)> points, double x1, double y1,
        double rx, double ry, bool largeArc, bool sweep, double x2, double y2)
    {
        rx = Math.Abs(rx);
        ry = Math.Abs(ry);
        if (rx == 0 || ry == 0)
        {
            points.Add((x2, y2));
            return;
        }

        var x1P = (x1 - x2) / 2;
        var y1P = (y1 - y2) / 2;

        var lambda = x1P * x1P / (rx * rx) + y1P * y1P / (ry * ry);
        if (lambda > 1)
        {
            var scale = Math.Sqrt(lambda);
            rx *= scale;
            ry *= scale;
        }

        var sign = largeArc != sweep ? 1 : -1;
        var numerator = rx * rx * ry * ry - rx * rx * y1P * y1P - ry * ry * x1P * x1P;
        var denominator = rx * rx * y1P * y1P + ry * ry * x1P * x1P;
        var co = sign * Math.Sqrt(Math.Max(0, numerator) / denominator);
        var cxP = co * rx * y1P / ry;
        var cyP = -co * ry * x1P / rx;

        var cx = cxP + (x1 + x2) / 2;
        var cy = cyP + (y1 + y2) / 2;

        var theta1 = AngleBetween(1, 0, (x1P - cxP) / rx, (y1P - cyP) / ry);
        var deltaTheta = AngleBetween((x1P - cxP) / rx, (y1P - cyP) / ry, (-x1P - cxP) / rx, (-y1P - cyP) / ry);
        if (!sweep && deltaTheta > 0) deltaTheta -= 2 * Math.PI;
        if (sweep && deltaTheta < 0) deltaTheta += 2 * Math.PI;

        const int segments = 64; // basta a restare fedele a un cerchio per questa prova su un punto
        for (var s = 1; s <= segments; s++)
        {
            var theta = theta1 + deltaTheta * s / segments;
            points.Add((cx + rx * Math.Cos(theta), cy + ry * Math.Sin(theta)));
        }
    }

    private static double AngleBetween(double ux, double uy, double vx, double vy)
    {
        var dot = ux * vx + uy * vy;
        var length = Math.Sqrt(ux * ux + uy * uy) * Math.Sqrt(vx * vx + vy * vy);
        var angle = Math.Acos(Math.Clamp(dot / length, -1, 1));
        return ux * vy - uy * vx < 0 ? -angle : angle;
    }
}
