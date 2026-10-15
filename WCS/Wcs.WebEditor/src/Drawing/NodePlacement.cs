namespace Wcs.WebEditor.Drawing;

/// <summary>
/// Come un dispositivo collocato si mette in scena: il centro del mondo attorno a cui ruota, le sue
/// dimensioni proprie (quelle da dare in pasto alla scala della sagoma, prima di ruotarla) e dove
/// finisce, nel mondo, un punto del riquadro unitario 0..100 del tipo - per esempio una porta.
/// </summary>
/// <remarks>
/// Il riquadro migrato da Syncfusion (<c>X</c>, <c>Y</c>, <c>Width</c>, <c>Height</c> di
/// <c>PlantItemDto</c>) non e' il riquadro del nodo prima della rotazione: e' il <c>BoundingRect</c>
/// che Syncfusion stessa serializza, e quello e' l'AABB del nodo **gia' ruotato** - non le sue
/// dimensioni proprie. Verificato costruendo davvero un nodo con
/// <c>Syncfusion.Diagram.Windows 34.2.7</c> (la stessa versione di <c>Wcs.Editor.csproj</c>):
/// <c>new Rectangle(new RectangleF(100, 50, 80, 20))</c> senza rotazione porta
/// <c>PinPoint = (140, 60)</c> (il centro del riquadro: la documentazione XML del pacchetto lo
/// conferma - "PinPoint value is considered as the node's center point"). Impostando
/// <c>RotationAngle</c> a 90, -90, 180 o 35 il <c>PinPoint</c> **non cambia**, ma il
/// <c>BoundingRect</c> serializzato si sposta seguendo esattamente l'AABB di un rettangolo 80x20
/// ruotato attorno al proprio centro (a 90/-90 diventa <c>(130, 20, 20, 80)</c> - larghezza e
/// altezza scambiate; a 180 resta <c>(100, 50, 80, 20)</c>; a 35 diventa
/// <c>(101.5, 28.9, 77.0, 62.3)</c>, l'inviluppo del rettangolo inclinato). Questa classe inverte
/// quella stessa trasformazione per ricavare le dimensioni proprie e ricostruire la sagoma com'era
/// prima che Syncfusion la ruotasse.
/// <para>
/// Verifica sui dati veri, non solo sul rettangolo costruito apposta sopra: nel disegno migrato
/// (51 dispositivi) i riquadri a rotazione -90 sono lo scambio esatto, al decimo di unita', di
/// quelli a rotazione 0 dello stesso tipo - 108.8x178.2 contro 178.2x108.8; 113.5x182.6 contro
/// 182.6x113.5; 122.1x191.0 contro 191.0x122.1. A -180 il riquadro non si scambia (stesso AABB di
/// 0, coerente con un giro di mezzo attorno al centro, che lascia l'inviluppo invariato). L'unico
/// dispositivo a 35 gradi ha un riquadro 183.5x174.5: un valore che nessuna somma di proiezioni di
/// una dimensione propria "normale" produrrebbe, coerente con l'inviluppo di un rettangolo
/// inclinato e non con un errore di lettura. E' su questa verifica, non solo sulla costruzione
/// Syncfusion sopra, che poggia l'inversione dell'AABB fatta da <see cref="LocalSize"/>.
/// </para>
/// </remarks>
public static class NodePlacement
{
    /// <summary>
    /// Il centro del dispositivo nel mondo: il centro del riquadro migrato, che resta lo stesso
    /// qualunque sia la rotazione (e' il <c>PinPoint</c> di Syncfusion, non l'angolo in alto a
    /// sinistra). E' il punto attorno a cui la sagoma ruota.
    /// </summary>
    public static (double X, double Y) WorldCenter(double x, double y, double width, double height) =>
        (x + width / 2, y + height / 2);

    /// <summary>
    /// Vero quando <see cref="LocalSize"/>, per questa rotazione, non puo' invertire l'AABB e
    /// ripiega sul riquadro cosi' com'e': il determinante (<c>cos(2*theta)</c>) e' troppo vicino a
    /// zero, a 45 gradi e suoi multipli. E' il segnale che <see cref="LocalSize"/> stessa usa per
    /// decidere se ripiegare, esposto perche' chi disegna possa dichiarare il ripiego invece di
    /// lasciarlo silenzioso - il difetto che questo stesso file correggeva per la rotazione attorno
    /// all'angolo non va reintrodotto qui in un'altra forma.
    /// </summary>
    public static bool IsDegenerateRotation(double rotationDegrees) => Math.Abs(Determinant(rotationDegrees)) < 1e-6;

    private static double Determinant(double rotationDegrees)
    {
        var radians = rotationDegrees * Math.PI / 180;
        var cos = Math.Abs(Math.Cos(radians));
        var sin = Math.Abs(Math.Sin(radians));
        return cos * cos - sin * sin;
    }

    /// <summary>
    /// Le dimensioni proprie del dispositivo, prima della rotazione: quelle da usare per la scala
    /// della sagoma. Si ricavano invertendo la formula con cui Syncfusion calcola l'AABB di un
    /// rettangolo <c>w0 x h0</c> ruotato di <c>theta</c> attorno al proprio centro -
    /// <c>Width = w0*|cos(theta)| + h0*|sin(theta)|</c>,
    /// <c>Height = w0*|sin(theta)| + h0*|cos(theta)|</c> - un sistema lineare 2x2 sempre risolvibile
    /// tranne quando il determinante si annulla (<see cref="IsDegenerateRotation"/>), a 45 gradi e
    /// suoi multipli: li' l'AABB non porta piu' abbastanza informazione per risalire alle due
    /// dimensioni separate. Nessuno dei 51 dispositivi del disegno reale cade su quell'angolo (i
    /// valori visti sono -180, -90, 0, 35, 90): quando capitasse, si ripiega sul riquadro cosi'
    /// com'e' piuttosto che dividere per (quasi) zero - il chiamante lo scopre con
    /// <see cref="IsDegenerateRotation"/>, non restando muto.
    /// <para>
    /// Anche fuori dal caso degenere il risultato puo' avere una dimensione negativa: un riquadro
    /// con <c>width</c> o <c>height</c> a zero (una geometria parziale, che
    /// <c>DiagramGeometryReader</c> ammette esplicitamente) produce una sagoma specchiata, non
    /// invisibile - <c>Transform</c> in <c>PlantCanvas</c> passa questi numeri a <c>scale()</c>
    /// cosi' come sono. Anche qui il chiamante lo scopre con <see cref="IsMirroredSize"/>, non
    /// restando muto.
    /// </para>
    /// </summary>
    public static (double Width, double Height) LocalSize(double width, double height, double rotationDegrees)
    {
        var determinant = Determinant(rotationDegrees);
        if (Math.Abs(determinant) < 1e-6) return (width, height);

        var radians = rotationDegrees * Math.PI / 180;
        var cos = Math.Abs(Math.Cos(radians));
        var sin = Math.Abs(Math.Sin(radians));

        return (
            (width * cos - height * sin) / determinant,
            (height * cos - width * sin) / determinant);
    }

    /// <summary>
    /// Vero quando <see cref="LocalSize"/>, per queste dimensioni e questa rotazione (non
    /// degenere), produce una dimensione propria negativa: un riquadro con <c>width</c> o
    /// <c>height</c> a zero - una geometria parziale, che <c>DiagramGeometryReader</c> ammette
    /// esplicitamente - puo' rendere il sistema lineare invertito con un risultato negativo, cioe'
    /// una scala negativa: la sagoma finisce specchiata, non invisibile. Come
    /// <see cref="IsDegenerateRotation"/>, esposto perche' chi disegna possa dichiarare il ripiego
    /// invece di lasciarlo silenzioso.
    /// </summary>
    public static bool IsMirroredSize(double width, double height, double rotationDegrees)
    {
        var (width0, height0) = LocalSize(width, height, rotationDegrees);
        return width0 < 0 || height0 < 0;
    }

    /// <summary>
    /// Dove finisce, nel mondo, un punto del riquadro unitario 0..100 del tipo (per esempio una
    /// porta): si sposta dal centro della sagoma (50,50), si scala sulle dimensioni proprie del
    /// dispositivo, si ruota attorno al centro - non attorno all'angolo - e si trasla al centro del
    /// dispositivo nel mondo. La stessa composizione, nello stesso ordine, con cui si colloca la
    /// sagoma intera: una porta e' solo un punto della sagoma.
    /// </summary>
    public static (double X, double Y) WorldPoint(
        double localX, double localY, double x, double y, double width, double height, double rotationDegrees)
    {
        var (width0, height0) = LocalSize(width, height, rotationDegrees);
        var (centerX, centerY) = WorldCenter(x, y, width, height);

        var dx = (localX - 50) * width0 / 100;
        var dy = (localY - 50) * height0 / 100;
        var (rotatedX, rotatedY) = Rotate(dx, dy, rotationDegrees);

        return (centerX + rotatedX, centerY + rotatedY);
    }

    /// <summary>
    /// Ruota un vettore (non un punto: nessuna traslazione) di <paramref name="rotationDegrees"/>
    /// gradi, la stessa matrice usata da <see cref="WorldPoint"/> per portare un punto del riquadro
    /// unitario nel mondo. Esposto a parte (compito B4) perche' i gesti di ridimensionamento e
    /// rotazione hanno bisogno della stessa rotazione applicata a vettori che non nascono da
    /// <see cref="WorldPoint"/> - lo scostamento di una maniglia dal bordo della sagoma, o la
    /// direzione di un angolo trascinato - senza duplicare qui la coppia cos/sin.
    /// </summary>
    public static (double X, double Y) Rotate(double x, double y, double rotationDegrees)
    {
        var radians = rotationDegrees * Math.PI / 180;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        return (x * cos - y * sin, x * sin + y * cos);
    }

    /// <summary>
    /// L'inverso di <see cref="LocalSize"/>: dalle dimensioni proprie di un dispositivo e dalla sua
    /// rotazione, l'inviluppo (AABB) che Syncfusion avrebbe serializzato - la stessa formula che il
    /// commento di <see cref="LocalSize"/> documenta e che quel metodo inverte (<c>Width =
    /// w0*|cos(theta)| + h0*|sin(theta)|</c>, <c>Height = w0*|sin(theta)| + h0*|cos(theta)|</c>).
    /// Serve al ridimensionamento e alla rotazione con maniglia (compito B4): quando la dimensione
    /// propria o la rotazione cambiano, il riquadro migrato (<c>Width</c>/<c>Height</c> di
    /// <c>PlantItemDto</c>) deve essere ricalcolato da capo con questa stessa formula - mai spostato
    /// della stessa quantita' della dimensione propria, che e' esattamente la trappola misurata tre
    /// volte in questa tappa. A differenza di <see cref="LocalSize"/> non esiste un caso degenere da
    /// segnalare: la trasformazione in avanti (dimensione propria -> inviluppo) e' definita per ogni
    /// rotazione, solo l'inversa (inviluppo -> dimensione propria) puo' perdere informazione a 45
    /// gradi.
    /// </summary>
    public static (double Width, double Height) Envelope(double width0, double height0, double rotationDegrees)
    {
        var radians = rotationDegrees * Math.PI / 180;
        var cos = Math.Abs(Math.Cos(radians));
        var sin = Math.Abs(Math.Sin(radians));
        return (width0 * cos + height0 * sin, height0 * cos + width0 * sin);
    }

    /// <summary>
    /// Il riquadro PROPRIO del dispositivo - dimensione propria (<see cref="LocalSize"/>) centrata
    /// sul centro del mondo (<see cref="WorldCenter"/>) - a partire dal riquadro migrato
    /// (<c>X</c>/<c>Y</c>/<c>Width</c>/<c>Height</c> di <c>PlantItemDto</c>, che e' l'AABB della
    /// sagoma GIA' ruotata). E' cio' che un disegnatore che ruota da se' - draw.io con
    /// <c>rotation=</c> nello stile, o la tela SVG con <c>Transform</c> - deve ricevere: passargli
    /// il riquadro migrato cosi' com'e' lo farebbe ruotare una seconda volta.
    /// <para>
    /// Esiste come metodo, e non come tre righe ricopiate in ogni chiamante, perche' il ritorno
    /// (<see cref="EnvelopeBox"/>) deve essere l'inverso ESATTO di questa andata: due copie della
    /// stessa formula che si allontanano fra loro sposterebbero i dispositivi a ogni salvataggio.
    /// </para>
    /// </summary>
    public static (double X, double Y, double Width, double Height) OwnBox(
        double x, double y, double width, double height, double rotationDegrees)
    {
        var (width0, height0) = LocalSize(width, height, rotationDegrees);
        var (centerX, centerY) = WorldCenter(x, y, width, height);
        return (centerX - width0 / 2, centerY - height0 / 2, width0, height0);
    }

    /// <summary>
    /// Il ritorno di <see cref="OwnBox"/>: dal riquadro proprio di un dispositivo al riquadro
    /// migrato che l'impianto memorizza (l'AABB della sagoma ruotata, centrato sullo stesso centro
    /// del mondo). Il centro non si tocca - ruotare non lo sposta - e la dimensione si ricalcola da
    /// capo con <see cref="Envelope"/>, mai spostata della stessa quantita' della dimensione
    /// propria.
    /// <para>
    /// E' l'inverso esatto di <see cref="OwnBox"/> ovunque <see cref="LocalSize"/> sappia invertire,
    /// cioe' ovunque tranne le rotazioni degeneri (45 gradi e multipli): li' <see cref="OwnBox"/>
    /// ripiega sul riquadro cosi' com'e' e il giro di ritorno restituirebbe un riquadro PIU' GRANDE
    /// di quello di partenza. Chi fa il giro completo - <c>DrawioDelta</c> - deve chiedere prima
    /// <see cref="IsDegenerateRotation"/> e dichiarare il caso, non riscrivere in silenzio un
    /// dispositivo ingrassato.
    /// </para>
    /// </summary>
    public static (double X, double Y, double Width, double Height) EnvelopeBox(
        double x, double y, double width0, double height0, double rotationDegrees)
    {
        var (width, height) = Envelope(width0, height0, rotationDegrees);
        var centerX = x + width0 / 2;
        var centerY = y + height0 / 2;
        return (centerX - width / 2, centerY - height / 2, width, height);
    }
}
