using Wcs.Engine.Api;
using Wcs.Host.Mapping;
using Wcs.WebEditor.Drawing;
using Wcs.WebEditor.Drawio;
using Xunit;

namespace Wcs.Host.Test;

/// <summary>
/// Il riscontro delle sette sagome: che cosa il registro dichiara, e che cosa ne esce sull'immagine
/// draw.io. Ogni riga del file <c>DeviceShapes.golden.txt</c> e' un fatto
/// verificabile a occhio in un diff, e il confronto e' testuale - non "simile", <b>uguale</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Perche' sta qui e non altrove.</b> Serve vedere insieme i due capi: il registro, che vive in
/// <c>Wcs.Engine</c>, e chi disegna, che vive in <c>Wcs.WebEditor</c>. <c>Wcs.WebEditor.Test</c> non
/// puo' referenziare il motore - <c>BoundaryTest</c> tiene chiusa quella porta apposta - e
/// <c>Wcs.Engine.Test</c> non conosce chi disegna. <c>Wcs.Host</c> referenzia tutti e due perche' e'
/// lui a servire l'uno all'altro, e questo e' l'unico posto dove la catena intera
/// (<c>DeviceTypeRegistry</c> -&gt; <c>DtoMapping.ToDto</c> -&gt; <c>DrawioShape.ImageStyle</c>) si
/// puo' percorrere per intero senza ricopiare niente a mano.
/// </para>
/// <para>
/// <b>Che cosa prende.</b> Prende ogni cambiamento del tracciato, del colore, del riempimento, del
/// numero e dell'ordine delle parti, e ogni cambiamento dell'immagine di draw.io, compresi quelli
/// che non vengono dal registro - il riempimento bianco, lo spessore del contorno e la giuntura.
/// La sola superficie verificata qui e' quella effettivamente renderizzata da draw.io; le regole
/// di resa restano provate dove stanno, in <c>DrawioShapePartsTest</c>, su tipi inventati apposta.
/// </para>
/// <para>
/// <b>Due ingombri, non uno.</b> Fino al lotto delle parti rigide ogni riga nasceva da un
/// dispositivo quadrato (100x100), e su un quadrato una parte rigida e una elastica si disegnano
/// allo stesso modo: un riscontro tenuto li' non avrebbe visto niente proprio del cambiamento piu'
/// visibile a video. Ogni tipo compare quindi due volte sull'immagine - sul quadrato
/// e su 122x191, l'ingombro dei 43 dispositivi su 51 dell'impianto vero - e le parti portano una
/// riga in piu', la bandiera che dichiarano. Le righe del quadrato sono rimaste quelle che erano,
/// meno il campo <c>fit</c> aggiunto in coda a ognuna: che <b>dicano</b> "nessuno" e' il punto, e
/// che <b>siano</b> le stesse e' quanto il lotto prometteva.
/// </para>
/// <para>
/// <b>Il giorno che le sagome cambiano davvero</b> questa prova diventa rossa, ed e' il suo
/// mestiere: il file si riscrive di proposito, guardando riga per riga che cambi solo quello che
/// deve. Non c'e' nessun interruttore che lo rigeneri da solo, e non deve essercene uno - un
/// riscontro che si riallinea da se' al primo rosso non e' un riscontro.
/// </para>
/// </remarks>
public sealed class DeviceShapeGoldenTest
{
    private const string GoldenFile = "DeviceShapes.golden.txt";

    /// <summary>
    /// L'ingombro PROPRIO dei dispositivi veri dell'impianto del proprietario: 43 su 51 misurano
    /// cosi'. E' l'ingombro su cui il riscontro chiede le due superfici una seconda volta, perche'
    /// su un quadrato - l'unico che teneva prima di questo lotto - una parte rigida e una elastica
    /// si disegnano allo stesso modo, e una sagoma che non si adatta piu' passerebbe inosservata.
    /// </summary>
    private const double RealWidth = 122;
    private const double RealHeight = 191;

    /// <summary>L'ingombro di sempre: quello su cui le righe del riscontro non devono cambiare.</summary>
    private const double SquareSide = 100;

    /// <summary>
    /// Le righe che descrivono le sette sagome, nell'ordine del registro. Il numero delle parti sta
    /// su una riga sua: senza, una parte sparita farebbe sparire anche le proprie righe e il
    /// confronto direbbe solo "meno righe del previsto".
    /// </summary>
    private List<string> Fingerprint()
    {
        var lines = new List<string>();
        foreach (var type in DeviceTypeRegistry.All.Select(DtoMapping.ToDto))
        {
            lines.Add($"{type.Code} | parts | {type.Shape.Count}");
            for (var i = 0; i < type.Shape.Count; i++)
            {
                var part = type.Shape[i];
                lines.Add($"{type.Code} | part {i} path | {part.Path}");
                lines.Add($"{type.Code} | part {i} colour | {part.Color}");
                lines.Add($"{type.Code} | part {i} filled | {(part.Filled ? "true" : "false")}");
                lines.Add($"{type.Code} | part {i} rigid | {(part.Rigid ? "true" : "false")}");
            }

            lines.Add($"{type.Code} | image | {DrawioShape.ImageStyle(type, SquareSide, SquareSide)}");
            lines.Add($"{type.Code} | image {RealWidth:0}x{RealHeight:0} | {DrawioShape.ImageStyle(type, RealWidth, RealHeight)}");
        }

        return lines;
    }

    [Fact]
    public void TheSevenShapesAndTheirDrawioImagesAreExactlyTheGoldenFile()
    {
        // Righe e non testo intero: cosi' una fine riga diversa fra il file e la macchina che lo
        // legge non fa fallire un confronto che non parla di fini riga. Le righe che cominciano per
        // '#' sono l'intestazione del file, per chi lo apre: nessuna riga del riscontro comincia
        // cosi', tutte cominciano col codice del tipo.
        var expected = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, GoldenFile))
            .Where(line => !line.StartsWith('#'))
            .ToList();
        var actual = Fingerprint();

        // Prima le righe in comune e poi il conto: cosi' il rosso nomina la prima riga diversa
        // invece di dire soltanto "numero di righe sbagliato".
        for (var i = 0; i < Math.Min(expected.Count, actual.Count); i++)
            Assert.Equal(expected[i], actual[i]);
        Assert.Equal(expected.Count, actual.Count);
    }

    /// <summary>
    /// <b>La verifica che rende sicuro il lotto delle parti rigide.</b> Su un ingombro QUADRATO
    /// "rigida" e "elastica" sono la stessa cosa - il lato minore e' tutti e due i lati - quindi le
    /// sette sagome vere devono uscire da tutte e due le superfici esattamente come uscivano quando
    /// ogni parte era elastica, che e' il comportamento di prima di questo lotto. Il confronto non
    /// e' con una stringa ricopiata: e' con le stesse sette sagome a cui si e' tolta la bandiera,
    /// cioe' con il codice di prima esercitato adesso.
    /// <para>
    /// Il riscontro qui sopra dice <b>quanto</b> cambia; questa dice <b>dove non deve cambiare</b>.
    /// Le due non si sostituiscono: il riscontro fissa i fatti di oggi e diventerebbe rosso anche
    /// per un ingombro quadrato riscritto di proposito, questa fissa la regola e resta vera il
    /// giorno che le sagome cambiano.
    /// </para>
    /// </summary>
    [Fact]
    public void OnASquareFootprintTheSevenShapesAreExactlyWhatTheyWereWhenEveryPartWasElastic()
    {
        var types = DeviceTypeRegistry.All.Select(DtoMapping.ToDto).ToList();

        // Almeno un tipo deve avere una parte rigida, altrimenti tutto quello che segue passerebbe
        // a vuoto: zero non e' una prova. Vale sull'INSIEME, non tipo per tipo - da quando gli
        // scaffali di storage sono elastici (scelta del proprietario, sul disegno: un montante
        // rigido si stacca dal telaio su un ingombro alto e stretto) esiste un tipo senza nessuna
        // parte rigida, ed e' legittimo.
        Assert.Contains(types, t => t.Shape.Any(p => p.Rigid));

        foreach (var type in types)
        {
            var elastic = type with { Shape = type.Shape.Select(p => p with { Rigid = false }).ToList() };

            foreach (var side in new[] { SquareSide, RealHeight })
            {
                Assert.Equal(DrawioShape.ImageStyle(elastic, side, side), DrawioShape.ImageStyle(type, side, side));
            }

            // Sull'ingombro vero i due devono invece DIVERGERE, se il tipo ha una parte rigida:
            // senza questa riga le asserzioni sopra sarebbero soddisfatte anche da una bandiera che
            // non fa piu' niente. E un tipo tutto elastico deve fare il contrario - restare uguale a
            // se stesso spento - che e' l'altra faccia della stessa regola: saltarlo e basta
            // lascerebbe scoperto il caso in cui una bandiera si accende dove non deve.
            var rigid = type.Shape.Any(p => p.Rigid);
            var onTheRealFootprint = DrawioShape.ImageStyle(type, RealWidth, RealHeight);
            var elasticOnTheRealFootprint = DrawioShape.ImageStyle(elastic, RealWidth, RealHeight);

            if (rigid) Assert.NotEqual(elasticOnTheRealFootprint, onTheRealFootprint);
            else Assert.Equal(elasticOnTheRealFootprint, onTheRealFootprint);
        }
    }
}
