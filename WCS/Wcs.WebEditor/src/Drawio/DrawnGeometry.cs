using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawing;

namespace Wcs.WebEditor.Drawio;

/// <summary>Why a device's drawn box cannot be written back as the plant's geometry (see <see cref="DrawnGeometry.Judge"/>).</summary>
public enum DrawnGeometryRefusal
{
    /// <summary>Resized while at a rotation of 45 degrees (+ k*90): the drawing does not carry its own size there.</summary>
    ResizedAtDegenerateRotation,

    /// <summary>Turned away from a rotation of 45 degrees (+ k*90), for the same reason.</summary>
    RotatedAwayFromDegenerateRotation,

    /// <summary>Turned TO a rotation of 45 degrees (+ k*90): the plant could not represent it.</summary>
    TurnedToDegenerateRotation,
}

/// <summary>
/// What a device's drawn box means for the plant: nothing (<see cref="Unchanged"/>, and then
/// <see cref="Geometry"/> is the plant's own, exactly), a refusal, or the geometry to write.
/// </summary>
public readonly record struct DrawnGeometryVerdict(bool Unchanged, DrawnGeometryRefusal? Refusal, PlantItemGeometryDto? Geometry);

/// <summary>
/// THE rule that turns a device's box as the drawing holds it - its OWN box and the rotation of its
/// style - into the plant's geometry, and says when it cannot. One rule, two users: the save
/// (<see cref="DrawioDelta"/>, the cells the drawing hands back) and the duplication
/// (<c>DrawioDuplicationCoordinator</c>, the source's cell as drawn now, compito 5 fix round 4). It
/// used to live inside the save alone, and the duplication, with a bare
/// <see cref="NodePlacement.EnvelopeBox"/> of its own, drifted by the drawing's rounding and, at 45
/// degrees, came out a square root of two larger: a second copy of a rule is where it goes wrong.
/// </summary>
public static class DrawnGeometry
{
    /// <summary>
    /// Quanto si tollera confrontando due riquadri nello spazio del disegno. Non e' un margine
    /// estetico, ed e' bene essere precisi su quale sia l'unico errore in gioco: i due riquadri che
    /// si confrontano nascono dallo STESSO record dell'impianto - quello letto dal disegno e quello
    /// che <see cref="DrawioDocument"/> aveva scritto, ricalcolato qui con la stessa
    /// <see cref="NodePlacement.OwnBox"/>. Non c'e' nessun giro di andata e ritorno fra i due spazi
    /// da cui possa uscire un errore amplificato: resta il solo arrotondamento della SCRITTURA, che
    /// <see cref="DrawioDocument"/> fa a due decimali ("0.##") e che sposta ogni numero al piu' di
    /// 0.005. La tolleranza di 0.01 del brief ci starebbe appena sopra, troppo appena per un numero
    /// letto e riparsato; 0.05 e' dieci volte l'errore possibile e resta ben sotto qualunque gesto
    /// dell'operatore, che in draw.io si muove su una griglia di 10.
    /// </summary>
    internal const double DrawingTolerance = 0.05;

    /// <summary>
    /// Judges the drawn box (<paramref name="x"/>, <paramref name="y"/>, <paramref name="width"/>,
    /// <paramref name="height"/>: the device's OWN box on the drawing; <paramref name="rotation"/>: its
    /// style's) against the plant's record <paramref name="before"/>. The save asks the host about
    /// the device between <see cref="DrawnGeometryVerdict.Unchanged"/> and the rest - the order it
    /// always had - so the verdict carries all three answers at once.
    /// </summary>
    public static DrawnGeometryVerdict Judge(PlantItemDto before, double x, double y, double width, double height, double rotation)
    {
        var plantRotation = before.Rotation ?? 0;
        var written = NodePlacement.OwnBox(before.X ?? 0, before.Y ?? 0, before.Width ?? 0, before.Height ?? 0, plantRotation);
        // Le due meta' stanno separate perche' i rifiuti qui sotto devono nominare il gesto
        // dell'operatore: un ridimensionamento e una rotazione non sono la stessa cosa per chi
        // legge, anche quando il motivo tecnico per cui li rifiutiamo e' lo stesso.
        var ownSizeKept = Near(width, written.Width) && Near(height, written.Height);
        var rotationKept = Near(rotation, plantRotation);
        var sizeKept = ownSizeKept && rotationKept;
        if (sizeKept && Near(x, written.X) && Near(y, written.Y))
            return new DrawnGeometryVerdict(true, null,
                new PlantItemGeometryDto(before.ItemId, before.X, before.Y, before.Width, before.Height, before.Rotation));

        // La rotazione degenere DI PARTENZA: a 45 gradi e multipli NodePlacement.OwnBox non sa
        // invertire l'inviluppo e ripiega sul riquadro cosi' com'e', percio' il riquadro del
        // disegno NON e' la dimensione propria e il giro di ritorno restituirebbe un
        // dispositivo piu' grande di quello di partenza. Uno spostamento si scrive lo stesso -
        // il centro si sposta e basta - ma un ridimensionamento o una rotazione li' non sono
        // ricostruibili. I due gesti hanno due motivi distinti perche' l'operatore deve
        // riconoscere il proprio: chi raddrizza un dispositivo non l'ha ridimensionato, e un
        // rifiuto che parla di un gesto che non hai fatto lo si prende per un guasto nostro.
        if (NodePlacement.IsDegenerateRotation(plantRotation) && !sizeKept)
            return new DrawnGeometryVerdict(false, !ownSizeKept
                ? DrawnGeometryRefusal.ResizedAtDegenerateRotation
                : DrawnGeometryRefusal.RotatedAwayFromDegenerateRotation, null);

        // L'altro verso dello stesso angolo, e il piu' insidioso perche' parte da una rotazione
        // qualunque, che di suo non ha niente di degenere: PORTARE un dispositivo a 45 gradi.
        // Scriverlo si potrebbe - l'inviluppo si calcola - ma l'impianto memorizza SOLO
        // l'inviluppo, e a 45 gradi l'inviluppo non porta piu' abbastanza informazione per
        // risalire alle due dimensioni proprie separate (si veda NodePlacement.LocalSize): alla
        // riapertura il dispositivo tornerebbe come un quadrato gonfiato di radice di due.
        // E' un limite del formato, non un errore di calcolo, ma resta una perdita silenziosa:
        // si rifiuta dicendo perche', invece di scrivere e lasciarla scoprire al disegno dopo.
        //
        // Vale ANCHE quando la dimensione propria di partenza e' gia' quadrata, e distinguere
        // quel caso introdurrebbe il difetto invece di toglierlo: un dispositivo 100x100 a 45
        // gradi ha inviluppo 141.42x141.42 (100 * (|cos 45| + |sin 45|), misurato con
        // NodePlacement.Envelope), e alla riapertura NodePlacement.LocalSize(141.42, 141.42, 45)
        // e' ANCH'ESSA degenere - ripiega sul riquadro cosi' com'e' e restituisce 141.42 tale e
        // quale, non 100. Il quadrato cresce di radice di due A OGNI GIRO, senza fondo, esatto
        // come un dispositivo non quadrato: non scrivere e' l'unica scelta che non ripete la
        // stessa perdita silenziosa un giro dopo.
        if (!rotationKept && NodePlacement.IsDegenerateRotation(rotation))
            return new DrawnGeometryVerdict(false, DrawnGeometryRefusal.TurnedToDegenerateRotation, null);

        return new DrawnGeometryVerdict(false, null, Geometry(before, x, y, width, height, rotation, sizeKept));
    }

    /// <summary>
    /// Il ritorno nello spazio dell'impianto: dal riquadro proprio che il disegno porta, al
    /// riquadro migrato che l'impianto memorizza.
    /// <para>
    /// Quando la dimensione propria e la rotazione sono rimaste quelle che avevamo scritto
    /// (<paramref name="sizeKept"/>) il dispositivo e' stato solo spostato: l'inviluppo e' ancora
    /// quello di prima e si ricopia tale e quale, spostandolo col centro. Ricalcolarlo comunque lo
    /// farebbe passare dai due decimali con cui <see cref="DrawioDocument"/> scrive, e il
    /// dispositivo cambierebbe dimensione al primo salvataggio dopo un semplice trascinamento.
    /// </para>
    /// <para>
    /// <b>Che cosa succede davvero senza questo ramo</b> (misurato spegnendolo, non dedotto). Lo
    /// scarto NON si accumula: e' secco, arriva tutto al primo giro e li' resta. Un dispositivo
    /// 130x184 a 35 gradi diventa 129.995508 x 183.995128 al PRIMO giro, e vale gli stessi numeri
    /// al secondo, al quinto e al ventesimo. Dipende dall'angolo, non dal numero di giri: a 30
    /// gradi diventa 130.004266 (cresce, non cala), a 20 gradi 130.003551, a 0, 90 e 180 gradi non
    /// cambia di niente. Il punto fisso ha una ragione precisa: l'errore e' l'arrotondamento a due
    /// decimali della dimensione PROPRIA scritta nel disegno, e
    /// <see cref="NodePlacement.LocalSize"/> e' l'inverso esatto di
    /// <see cref="NodePlacement.Envelope"/> - dal secondo giro in poi la dimensione propria che
    /// torna e' gia' un numero a due decimali, riscriverla lo lascia com'e' e non c'e' piu' niente
    /// da arrotondare.
    /// </para>
    /// <para>
    /// Il ramo resta comunque necessario - una deformazione secca al primo salvataggio dopo un
    /// trascinamento e' una deriva che nessuno ha chiesto - ma va provato per quello che e': una
    /// prova su UN SOLO giro che pretenda <c>Width == 130</c> esatto lo prende in pieno, e
    /// <c>DrawioDeltaTest.FiveOpenAndSaveCyclesDoNotEatTheDevicesSize</c> aggiunge a quella la
    /// dimostrazione che nemmeno ripetendo il giro compare una deriva.
    /// </para>
    /// </summary>
    private static PlantItemGeometryDto Geometry(PlantItemDto before, double x, double y, double width, double height, double rotation, bool sizeKept)
    {
        var id = before.ItemId;
        var centerX = x + width / 2;
        var centerY = y + height / 2;

        if (sizeKept)
        {
            var keptWidth = before.Width ?? 0;
            var keptHeight = before.Height ?? 0;
            return new PlantItemGeometryDto(id, centerX - keptWidth / 2, centerY - keptHeight / 2,
                keptWidth, keptHeight, before.Rotation);
        }

        var (envelopeX, envelopeY, envelopeWidth, envelopeHeight) = NodePlacement.EnvelopeBox(x, y, width, height, rotation);
        return new PlantItemGeometryDto(id, envelopeX, envelopeY, envelopeWidth, envelopeHeight, rotation);
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) <= DrawingTolerance;
}
