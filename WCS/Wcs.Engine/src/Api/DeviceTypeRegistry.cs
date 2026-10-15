using static Wcs.Common.Enums;

namespace Wcs.Engine.Api;

/// <summary>Dove entra o esce la merce, nel riquadro unitario del tipo.</summary>
public sealed record DevicePort(string Name, double X, double Y, string Direction);

/// <summary>Un punto nel riquadro unitario 0..100 del tipo.</summary>
public sealed record ShapePoint(double X, double Y);

/// <summary>
/// Una parte della sagoma di un tipo: il proprio tracciato SVG dentro il riquadro unitario
/// 0 0 100 100, il proprio colore (<c>#RRGGBB</c>), se e' piena o solo contornata, e se sta rigida
/// o elastica sull'ingombro del dispositivo.
/// </summary>
/// <remarks>
/// Il gemello nel motore di <c>Wcs.Api.Contracts.Dtos.ShapePartDto</c>, dove sta la spiegazione per
/// esteso: piu' parti perche' gli elementi forniti dal proprietario sono composti (un riquadro, un
/// ingranaggio pieno, frecce colorate sopra), e l'ordine dell'elenco e' l'ordine di disegno -
/// l'ultima parte sta sopra le precedenti.
/// <para>
/// Il colore e' per parte e non per tipo: alla tappa 4b ce n'era uno solo, scelto perche' alla
/// scala vera dell'impianto il contorno di una sagoma misura meno di un pixel - a quella scala si
/// legge solo la silhouette, e senza un colore diverso per tipo cinque sagome su sette si
/// confondono in un rettangolo uguale. Quella ragione non cambia, ma da quando le sagome sono
/// composte un tipo porta <b>due famiglie</b> di colore: la STRUTTURA (riquadro, ingranaggio,
/// scaffali, freccia curva) porta il colore del proprio tipo, le FRECCE portano i due colori del
/// proprietario - uguali su ogni tipo che le porta, perche' dicono un verso e non un'identita'.
/// Si veda il <c>remarks</c> di <see cref="DeviceTypeRegistry"/>, e la regola e' sorvegliata da
/// <c>TheStructureCarriesTheTypeColourAndTheArrowsTheOwnersTwo</c>.
/// </para>
/// <para>
/// <see cref="Rigid"/> dice come la parte incontra l'ingombro del dispositivo, che quadrato non e'
/// (sull'impianto vero 122x191): elastica (<c>false</c>) si stira su tutto l'ingombro - e' il
/// riquadro, che deve arrivare ai quattro lati; rigida (<c>true</c>) resta in proporzione e
/// centrata - e' l'ingranaggio, che stirato diventa un ovale, e con lui le frecce e gli scaffali.
/// Su un ingombro quadrato le due rese coincidono esattamente. E' una bandiera dichiarata e non
/// "tutte tranne la prima": essere la prima parte vuol gia' dire un'altra cosa (il corpo, quello
/// che in draw.io riceve il bianco del foglio), e due significati sullo stesso indice non si
/// potrebbero piu' separare il giorno che un tipo ne volesse uno senza l'altro.
/// </para>
/// </remarks>
public sealed record ShapePart(string Path, string Color, bool Filled, bool Rigid);

/// <summary>
/// Che cosa il motore dichiara di un tipo di dispositivo a chi lo deve disegnare.
/// </summary>
/// <remarks>
/// <see cref="Shape"/> e' l'elenco ordinato delle parti (<see cref="ShapePart"/>), ognuna un
/// tracciato SVG dentro un riquadro unitario 0 0 100 100: chi disegna le colloca applicando la
/// geometria del dispositivo (X, Y, Width, Height, Rotation). Non e' mai vuoto.
/// <see cref="LoadUnitSlot"/> e <see cref="StatusSlot"/> sono l'erede dichiarato dei due nodes
/// nominati <c>LU</c> e <c>STATUS</c> che ogni forma della palette WinForms si porta dentro come
/// bitmap: senza, il quadro dal vivo non saprebbe dove mettere l'unita' di carico e lo stato.
/// </remarks>
public sealed record DeviceTypeDescriptor(
    string Code,
    short NumericCode,
    string Label,
    IReadOnlyList<ShapePart> Shape,
    IReadOnlyList<DevicePort> Ports,
    ShapePoint LoadUnitSlot,
    ShapePoint StatusSlot);

/// <summary>
/// I tipi di dispositivo che il motore conosce. Nella tappa 4a l'insieme e' chiuso e lo popola
/// questo file: e' l'API a essere aperta, non l'insieme. Ad aprirlo - un assembly che l'host carica
/// e che registra tipi nuovi - ci pensa la tappa 5, che trova il posto gia' pronto.
/// </summary>
/// <remarks>
/// <b>Le sagome sono quelle disegnate dal proprietario</b> (elementi <c>Automazione_*_V4</c>, set
/// approvato del 15/09): un riquadro, un ingranaggio pieno al centro, e sopra le frecce colorate
/// che dicono da che parte la merce entra o esce. Quattro elementi forniti, sette tipi: la
/// mappatura e' sua - base = <c>automation</c>, freccia verde che entra = <c>loading</c>, freccia
/// arancione che esce = <c>buffer</c>, verde piu' arancione = <c>loading-and-buffer</c>, solo
/// scaffale senza ingranaggio = <c>storage</c> - e <c>circular-distributor</c> e
/// <c>group-automation</c> sono derivati dallo stesso ingranaggio, come ha chiesto ("li derivo io
/// dall'ingranaggio").
/// <para>
/// <b>L'ingranaggio e le frecce non sono ridisegnati: sono i suoi punti</b>, traslati sul centro
/// (50, 50) e scalati di 31,5/34,8 - il rapporto che tiene il diametro di punta al 68,5%
/// dell'altezza utile del riquadro (63 su 92), come nel suo disegno. L'unica cosa che e' stata
/// rifatta e' la lunghezza dell'asta delle frecce, perche' il suo riquadro e' 160x100 e questo e'
/// quadrato: la testa e la punta conservano le proporzioni sue, e l'asta si ferma a 8, quattro
/// unita' dentro il bordo del riquadro.
/// </para>
/// <para>
/// <b>Il foro dell'ingranaggio non e' un disco bianco sopra i denti: e' un buco vero.</b> Nel suo
/// elemento il foro e' un <c>&lt;circle fill="white"/&gt;</c> disegnato sopra; qui il cerchio sta
/// nello stesso tracciato dei denti ma percorso al CONTRARIO (bandiera di verso 0 contro il verso
/// orario del poligono), e con la regola di riempimento non-zero i due versi si annullano - resta
/// un anello. Misurato in un browser vero: il pixel al centro legge il bianco del foglio sotto,
/// non un bianco dipinto sopra. E' l'inverso esatto della correzione fatta su
/// <c>circular-distributor</c> alla tappa B2a, dove due versi opposti bucavano un disco che doveva
/// essere pieno: la stessa regola, usata di proposito invece che per sbaglio. Il bianco dipinto
/// sopra sarebbe stato il primo caso del pericolo descritto in <c>DrawioShape.DecorationFill</c>.
/// </para>
/// <para>
/// <b>I colori.</b> Il riquadro, l'ingranaggio e gli scaffali portano il colore del tipo - i sette
/// colori approvati alla tappa B3-ter, che restano quelli: a impianto intero il contorno misura
/// meno di un pixel, si legge solo la silhouette, e senza un colore per tipo cinque sagome su sette
/// tornerebbero a confondersi in uno stesso riquadro. Le frecce portano invece i due colori del
/// proprietario, verde <c>#15956b</c> per l'ingresso e arancione <c>#e57924</c> per l'uscita: sono
/// il suo segno distintivo, e sono gli stessi su ogni tipo che li porta.
/// </para>
/// <para>
/// <b>Le parti sono in ordine di disegno</b>, l'ultima sopra le precedenti: prima il riquadro
/// (contornato, e l'unico che in draw.io riceve il bianco del foglio - si veda
/// <c>DrawioShape.OutlineFill</c>), poi l'ingranaggio pieno, poi le frecce piene. Nessuna parte
/// sopra il riquadro e' contornata tranne gli scaffali di <c>storage</c>, che non racchiudono
/// nessuna area: la regola sta in <c>DeviceTypeRegistryTest.TheBodyIsTheFirstPartAndEveryPartAboveItEnclosesNoAreaOrIsFilled</c>.
/// </para>
/// <para>
/// <b>Il riquadro si stira, tutto il resto no.</b> Le sagome sono scritte in un riquadro unitario
/// QUADRATO, e l'ingombro di un dispositivo vero non lo e' - sul suo impianto 43 dispositivi su 51
/// misurano 122x191. Il riquadro deve arrivare ai quattro lati e quindi si stira (elastico); un
/// ingranaggio stirato diventa un ovale, ed e' esattamente quello che il proprietario ha visto
/// guardando l'Editor sull'impianto vero. Percio' ogni parte dichiara la propria
/// (<see cref="ShapePart.Rigid"/>): qui elastico e' solo il corpo, rigidi l'ingranaggio, le
/// frecce, la freccia curva e gli scaffali - in proporzione e centrati sull'ingombro, com'e' fatto
/// il suo elemento (riquadro 160x100, ingranaggio tondo dentro). Che oggi "rigida" e "non e' la
/// prima parte" diano lo stesso elenco e' un fatto di questi sette tipi, non una regola: si veda
/// il <c>remarks</c> di <see cref="ShapePart"/>.
/// </para>
/// </remarks>
public static class DeviceTypeRegistry
{
    // Il riquadro, identico per tutti e sette: e' il corpo della sagoma, quello che riempie
    // l'impronta e su cui tutto il resto e' appoggiato. Da 4 a 96 e non da 0 a 100 perche' il
    // contorno viene disegnato a cavallo del tracciato, e a 0 meta' del tratto cadrebbe fuori dalla
    // cella.
    private const string Body = "M 4 4 H 96 V 96 H 4 Z";

    // L'ingranaggio del proprietario, centro (50, 50), raggio di punta 31,5. I 41 vertici sono i
    // suoi, scalati; il foro (raggio 19,01) e' percorso al contrario dei denti - si veda il remarks
    // qui sopra.
    private const string Gear =
        "M 72.08 40.85 L 73.28 44.62 L 81.02 44.53 L 81.02 55.47 L 73.28 55.38 L 72.08 59.15 " +
        "L 70.27 62.66 L 75.8 68.07 L 68.07 75.8 L 62.66 70.27 L 59.15 72.08 L 55.38 73.28 " +
        "L 55.47 81.02 L 44.53 81.02 L 44.62 73.28 L 40.85 72.08 L 37.34 70.27 L 31.93 75.8 " +
        "L 24.2 68.07 L 29.73 62.66 L 27.92 59.15 L 26.72 55.38 L 18.98 55.47 L 18.98 44.53 " +
        "L 26.72 44.62 L 27.92 40.85 L 29.73 37.34 L 24.2 31.93 L 31.93 24.2 L 37.34 29.73 " +
        "L 40.85 27.92 L 44.62 26.72 L 44.53 18.98 L 55.47 18.98 L 55.38 26.72 L 59.15 27.92 " +
        "L 62.66 29.73 L 68.07 24.2 L 75.8 31.93 L 70.27 37.34 L 72.08 40.85 Z " +
        "M 69.01 50 A 19.01 19.01 0 1 0 30.99 50 A 19.01 19.01 0 1 0 69.01 50 Z";

    // Lo stesso ingranaggio rimpicciolito (raggio di punta 19) e messo due volte affiancato: e' la
    // derivazione che il proprietario ha chiesto per il gruppo, "piu' ingranaggi nello stesso
    // riquadro". I centri stanno a (30, 50) e (70, 50): 40 unita' di distanza contro 38 di diametro,
    // cosi' i denti non si toccano e a occhio restano due, non uno lungo.
    private const string LeftGear =
        "M 43.32 44.48 L 44.04 46.76 L 48.71 46.7 L 48.71 53.3 L 44.04 53.24 L 43.32 55.52 " +
        "L 42.22 57.64 L 45.56 60.9 L 40.9 65.56 L 37.64 62.22 L 35.52 63.32 L 33.24 64.04 " +
        "L 33.3 68.71 L 26.7 68.71 L 26.76 64.04 L 24.48 63.32 L 22.36 62.22 L 19.1 65.56 " +
        "L 14.44 60.9 L 17.78 57.64 L 16.68 55.52 L 15.96 53.24 L 11.29 53.3 L 11.29 46.7 " +
        "L 15.96 46.76 L 16.68 44.48 L 17.78 42.36 L 14.44 39.1 L 19.1 34.44 L 22.36 37.78 " +
        "L 24.48 36.68 L 26.76 35.96 L 26.7 31.29 L 33.3 31.29 L 33.24 35.96 L 35.52 36.68 " +
        "L 37.64 37.78 L 40.9 34.44 L 45.56 39.1 L 42.22 42.36 L 43.32 44.48 Z " +
        "M 41.47 50 A 11.47 11.47 0 1 0 18.53 50 A 11.47 11.47 0 1 0 41.47 50 Z";

    private const string RightGear =
        "M 83.32 44.48 L 84.04 46.76 L 88.71 46.7 L 88.71 53.3 L 84.04 53.24 L 83.32 55.52 " +
        "L 82.22 57.64 L 85.56 60.9 L 80.9 65.56 L 77.64 62.22 L 75.52 63.32 L 73.24 64.04 " +
        "L 73.3 68.71 L 66.7 68.71 L 66.76 64.04 L 64.48 63.32 L 62.36 62.22 L 59.1 65.56 " +
        "L 54.44 60.9 L 57.78 57.64 L 56.68 55.52 L 55.96 53.24 L 51.29 53.3 L 51.29 46.7 " +
        "L 55.96 46.76 L 56.68 44.48 L 57.78 42.36 L 54.44 39.1 L 59.1 34.44 L 62.36 37.78 " +
        "L 64.48 36.68 L 66.76 35.96 L 66.7 31.29 L 73.3 31.29 L 73.24 35.96 L 75.52 36.68 " +
        "L 77.64 37.78 L 80.9 34.44 L 85.56 39.1 L 82.22 42.36 L 83.32 44.48 Z " +
        "M 81.47 50 A 11.47 11.47 0 1 0 58.53 50 A 11.47 11.47 0 1 0 81.47 50 Z";

    // Le frecce del proprietario. Quella che entra parte dal bordo interno sinistro e finisce
    // dentro il foro dell'ingranaggio; quella che esce parte dal foro e finisce al bordo interno
    // destro. Nei tipi che le portano tutte e due sono sfalsate di 11 unita' sopra e sotto l'asse,
    // come nel suo quarto elemento, e l'arancione punta a sinistra - cosi' si legge "entra da
    // sinistra, esce da sinistra" senza che le due si sovrappongano.
    private const string ArrowIn =
        "M 8 45.22 L 36.15 45.22 L 36.15 39.14 L 60.86 50 L 36.15 60.86 L 36.15 54.78 L 8 54.78 Z";

    private const string ArrowOut =
        "M 39.14 45.22 L 67.29 45.22 L 67.29 39.14 L 92 50 L 67.29 60.86 L 67.29 54.78 L 39.14 54.78 Z";

    private const string UpperArrowIn =
        "M 8 34.98 L 36.15 34.98 L 36.15 29.91 L 60.86 38.96 L 36.15 48.01 L 36.15 42.94 L 8 42.94 Z";

    private const string LowerArrowOut =
        "M 60.86 57.06 L 32.71 57.06 L 32.71 51.99 L 8 61.04 L 32.71 70.09 L 32.71 65.02 L 60.86 65.02 Z";

    // La freccia curva del distributore: la derivazione chiesta dal proprietario, "una freccia
    // curva attorno all'ingranaggio". Una fascia fra raggio 36 e 41 - la punta dei denti arriva a
    // 31,5 e ognuno dei due contorni e' spesso mezza unita' e mezza per parte, quindi fra la fascia
    // e i denti resta un margine bianco invece di un contatto - da 190 a 35 gradi, con la punta a 6.
    // La barba della punta arriva a 45,5, dentro il bordo interno del riquadro (96). Il verso a
    // video e' orario, come la rotazione che il distributore fa davvero.
    private const string CurvedArrow =
        "M 14.55 56.25 L 9.62 57.12 A 41 41 0 0 1 83.59 26.48 L 87.27 23.9 L 88.29 45.98 " +
        "L 77.03 31.07 L 79.49 29.35 A 36 36 0 0 0 14.55 56.25 Z";

    // Lo scaffale: il solo tipo senza ingranaggio, per scelta del proprietario. Tre ripiani e un
    // montante, tutti sotto-tracciati aperti - nessuno racchiude un'area, quindi nessuno copre il
    // riquadro sotto di se' qualunque riempimento riceva.
    private const string Shelves = "M 4 28 H 96 M 4 52 H 96 M 4 76 H 96 M 50 4 V 96";

    // Le porte stanno sul bordo del riquadro, che ora e' lo stesso per tutti e sette: (4, 50) a
    // sinistra, (96, 50) a destra, e per lo scaffale la porta sul lato inferiore. Prima della tappa
    // B3-ter erano condivise fra tipi con sagome diverse e cadevano fuori dal disegno; prima di
    // questo lotto loading e loading-and-buffer avevano la porta d'ingresso in fondo a una tacca
    // (22, 50) che le sagome nuove non hanno piu': ora l'ingresso e' il bordo, e l'asta della
    // freccia verde comincia a 8, quattro unita' piu' in la'.
    //
    // I due punti sono gli stessi per tutti e sette per la stessa ragione: il riquadro e' lo stesso.
    // LoadUnitSlot al centro - che ora e' il foro dell'ingranaggio, l'unico spazio libero al centro
    // della sagoma, ed e' proprio dove un'unita' di carico si legge. StatusSlot a (88, 60): dentro
    // il riquadro e fuori da ogni parte piena - l'ingranaggio grande arriva a 81, quello destro del
    // gruppo ha centro (70, 50) e punta 19 mentre (88, 60) ne dista 20,6, e la freccia curva del
    // distributore a quell'altezza e' gia' finita. Su bianco per tutti e sette, e non piu' perche'
    // qualcuno l'ha guardato tipo per tipo: lo prova
    // DeviceTypeRegistryTest.TheStatusPointStaysOffEveryFilledPartAtEveryShapeOfTheFootprint, che lo
    // chiede anche sulle parti rigide RIMPICCIOLITE - un ingombro alto e stretto le porta verso il
    // centro, e un punto che oggi cade fuori da un riempimento domani potrebbe caderci dentro.
    private static readonly IReadOnlyList<DevicePort> ThroughPorts =
        [new DevicePort("in", 4, 50, "in"), new DevicePort("out", 96, 50, "out")];

    private static readonly ShapePoint Centre = new(50, 50);
    private static readonly ShapePoint Corner = new(88, 60);

    public static IReadOnlyList<DeviceTypeDescriptor> All { get; } =
    [
        new("automation", (short)DeviceTypes.Automation, "Automation",
            [new ShapePart(Body, "#334155", Filled: false, Rigid: false), new ShapePart(Gear, "#334155", Filled: true, Rigid: true)],
            ThroughPorts, Centre, Corner),

        new("loading", (short)DeviceTypes.Loading, "Loading",
            [new ShapePart(Body, "#1d4ed8", Filled: false, Rigid: false), new ShapePart(Gear, "#1d4ed8", Filled: true, Rigid: true),
             new ShapePart(ArrowIn, InboundGreen, Filled: true, Rigid: true)],
            ThroughPorts, Centre, Corner),

        new("buffer", (short)DeviceTypes.Buffer, "Buffer",
            [new ShapePart(Body, "#0f766e", Filled: false, Rigid: false), new ShapePart(Gear, "#0f766e", Filled: true, Rigid: true),
             new ShapePart(ArrowOut, OutboundOrange, Filled: true, Rigid: true)],
            ThroughPorts, Centre, Corner),

        new("storage", (short)DeviceTypes.Storage, "Storage",
            [new ShapePart(Body, "#a16207", Filled: false, Rigid: false), new ShapePart(Shelves, "#a16207", Filled: false, Rigid: false)],
            [new DevicePort("door", 50, 96, "both")], Centre, Corner),

        new("circular-distributor", (short)DeviceTypes.CircularDistributor, "Circular Distributor",
            [new ShapePart(Body, "#4338ca", Filled: false, Rigid: false), new ShapePart(Gear, "#4338ca", Filled: true, Rigid: true),
             new ShapePart(CurvedArrow, "#4338ca", Filled: true, Rigid: true)],
            [new DevicePort("in", 4, 50, "in"), new DevicePort("out", 96, 50, "out"),
             new DevicePort("side", 50, 4, "both")], Centre, Corner),

        new("group-automation", (short)DeviceTypes.GroupOfAutomationDevices, "Group Automation",
            [new ShapePart(Body, "#15803d", Filled: false, Rigid: false), new ShapePart(LeftGear, "#15803d", Filled: true, Rigid: true),
             new ShapePart(RightGear, "#15803d", Filled: true, Rigid: true)],
            ThroughPorts, Centre, Corner),

        new("loading-and-buffer", (short)DeviceTypes.LoadingAndBuffer, "LoadingAndBuffer",
            [new ShapePart(Body, "#0e7490", Filled: false, Rigid: false), new ShapePart(Gear, "#0e7490", Filled: true, Rigid: true),
             new ShapePart(UpperArrowIn, InboundGreen, Filled: true, Rigid: true),
             new ShapePart(LowerArrowOut, OutboundOrange, Filled: true, Rigid: true)],
            ThroughPorts, Centre, Corner)
    ];

    // I due colori del proprietario, presi dai suoi elementi: non sono il colore di un tipo ma il
    // significato di una freccia, e stanno uguali su ogni tipo che la porta.
    private const string InboundGreen = "#15956b";
    private const string OutboundOrange = "#e57924";
}
