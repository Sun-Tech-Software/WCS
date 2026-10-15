namespace Wcs.Api.Contracts.Dtos;

/// <summary>Dove entra o esce la merce, nel riquadro unitario 0..100 del tipo.</summary>
public sealed record DevicePortDto(string Name, double X, double Y, string Direction);

/// <summary>Un punto nel riquadro unitario 0..100 del tipo.</summary>
public sealed record ShapePointDto(double X, double Y);

/// <summary>
/// Una parte della sagoma di un tipo: il proprio tracciato SVG nel riquadro unitario 0 0 100 100,
/// il proprio colore (<c>#RRGGBB</c>), se e' <b>piena</b> o solo <b>contornata</b>, e se sta
/// <b>rigida</b> o <b>elastica</b> sull'ingombro del dispositivo.
/// </summary>
/// <remarks>
/// <para>
/// Una parte non e' un dettaglio della sagoma: e' il pezzo che si disegna da se'. Gli elementi
/// forniti dal proprietario sono composti - un riquadro contornato, un ingranaggio pieno, e frecce
/// colorate sopra - e tre pezzi cosi' non stanno in un tracciato solo con un colore solo: un
/// tracciato unico non puo' avere due colori, e la regola di riempimento predefinita farebbe
/// annullare fra loro i sotto-tracciati sovrapposti invece di sovrapporli (lo stesso guasto gia'
/// misurato sul cerchio interno di <c>circular-distributor</c>).
/// </para>
/// <para>
/// <b>L'ordine nell'elenco e' l'ordine di disegno</b>, e non e' un dettaglio: l'ultima parte sta
/// sopra le precedenti, come in SVG. Nella prima versione delle sagome fornite la freccia finiva
/// sotto l'ingranaggio e spariva.
/// </para>
/// <para>
/// <b>E la PRIMA parte dell'elenco e' il corpo</b>: e' quella che sta sul foglio e ne porta
/// l'impronta, ed e' quella che chi compone l'immagine di draw.io dipinge del bianco del foglio
/// quando non e' piena (<c>DrawioShape.OutlineFill</c>), mentre ogni parte successiva e' una
/// decorazione appoggiata sopra e non riceve nessun bianco. Non e' una convenzione interna di chi
/// disegna: un tipo che dichiarasse una decorazione per prima riceverebbe il bianco su quella e
/// niente sul corpo - corpo trasparente e macchia bianca sopra le giunzioni - senza che nessuna
/// prova se ne accorga.
/// </para>
/// <para>
/// <see cref="Filled"/> dice se la parte si dipinge con il proprio <see cref="Color"/> o se se ne
/// disegna il solo contorno: negli elementi forniti l'ingranaggio e' pieno, il riquadro no.
/// Nessun valore di scorta: una parte deve dichiararlo, perche' le due rese sono diverse a video e
/// indovinare per lei vorrebbe dire scegliere al posto di chi disegna.
/// </para>
/// <para>
/// <see cref="Rigid"/> dice come la parte incontra l'ingombro del dispositivo, che non e' quadrato
/// come il riquadro unitario in cui la parte e' scritta (sull'impianto vero i dispositivi sono per
/// esempio 122x191). Una parte <b>elastica</b> (<c>false</c>) si stira su tutto l'ingombro: e' il
/// riquadro, che deve arrivare ai quattro lati. Una parte <b>rigida</b> (<c>true</c>) resta in
/// proporzione e centrata sull'ingombro: e' l'ingranaggio, che stirato diventa un ovale, e con lui
/// le frecce e gli scaffali. Su un ingombro quadrato le due rese coincidono esattamente.
/// </para>
/// <para>
/// <b>"In proporzione" vuol dire a scala <c>min(larghezza,altezza)/100</c></b>: il lato PIU'
/// PICCOLO dei due, non il piu' grande. Detto qui e non lasciato a chi disegna per la stessa
/// ragione della prima parte: il registro e' dichiarato apribile a un assembly esterno, e una
/// seconda superficie che scegliesse <c>max</c> avrebbe pur sempre un ingranaggio tondo - solo,
/// piu' largo dell'ingombro, e quindi debordante dal riquadro invece che dentro di esso, che non e'
/// come sono fatti gli elementi del proprietario (riquadro 160x100, ingranaggio tondo dentro). Un
/// difetto senza nessun rosso, perche' su un ingombro quadrato <c>min</c> e <c>max</c> danno la
/// stessa immagine.
/// </para>
/// <para>
/// <b><see cref="Rigid"/> e "essere la prima parte" sono due regole distinte</b>, non due nomi
/// della stessa: oggi, sui sette tipi del registro, il corpo e' insieme la prima parte e l'unica
/// elastica, ma il giorno che un tipo volesse una decorazione elastica - un fondo che riempie
/// l'ingombro dietro al corpo - o un corpo rigido, deve poterlo dire. Nessun valore di scorta, per
/// la stessa ragione di <see cref="Filled"/>.
/// </para>
/// </remarks>
public sealed record ShapePartDto(string Path, string Color, bool Filled, bool Rigid);

/// <summary>
/// Un tipo di dispositivo come lo dichiara il motore a chi lo deve disegnare: il codice stabile in
/// lettere, il valore numerico di oggi per la transizione, l'etichetta, la forma schematica come
/// elenco ordinato di parti, le porte, e i due punti dove si mostrano l'unita' di carico e lo
/// stato.
/// </summary>
/// <remarks>
/// <see cref="Shape"/> non e' mai vuoto: un tipo senza nessuna parte non disegnerebbe niente, e
/// sulla tela un dispositivo invisibile si porta dietro in silenzio le giunzioni che lo toccano.
/// </remarks>
public sealed record DeviceTypeDto(
    string Code, short NumericCode, string Label, IReadOnlyList<ShapePartDto> Shape,
    IReadOnlyList<DevicePortDto> Ports, ShapePointDto LoadUnitSlot, ShapePointDto StatusSlot);
