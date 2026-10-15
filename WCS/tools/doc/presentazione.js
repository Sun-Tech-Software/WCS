// La presentazione del WCS per chi non programma: la fila di persone che si passano oggetti.
// Uso: npm install pptxgenjs && node presentazione.js  (scrive Il-WCS-come-ragiona-una-fila-di-macchine.pptx)
// Tutti i diagrammi sono forme native (rettangoli, frecce, testo): niente immagini.
const pptxgen = require('pptxgenjs');
const pres = new pptxgen();
pres.layout = 'LAYOUT_16x9'; // 10" x 5.625"
pres.title = 'Il WCS: come ragiona una fila di macchine';

// Palette: acciaio e ambra, come un impianto.
const C = {
  ink: '1F2A37',      // fondo scuro e testo
  steel: '4A6FA5',    // le macchine
  steelDark: '2F4B7A',
  amber: 'F2A900',    // l'oggetto, l'accento
  light: 'F4F6F8',    // fondo chiaro
  mist: 'D9E0E8',     // riquadri tenui
  white: 'FFFFFF',
  wcs: '2E7D6B',      // il WCS
  wms: '8A5A2B',      // il gestionale
  grey: '6B7785',
  red: 'B23A3A',
};
const HEAD = 'Cambria';
const BODY = 'Calibri';

function bg(slide, color) { slide.background = { color }; }
function title(slide, text, opts = {}) {
  slide.addText(text, { x: 0.5, y: 0.35, w: 9, h: 0.7, fontFace: HEAD, fontSize: 30, bold: true,
    color: opts.color || C.ink, isTextBox: true, margin: 0 });
}
function note(slide, text) { slide.addNotes(text); }
function body(slide, text, x, y, w, h, opts = {}) {
  slide.addText(text, { x, y, w, h, fontFace: BODY, fontSize: opts.size || 14, color: opts.color || C.ink,
    isTextBox: true, margin: 0, valign: opts.valign || 'top', align: opts.align || 'left', bold: !!opts.bold,
    italic: !!opts.italic, lineSpacingMultiple: 1.1 });
}
function box(slide, x, y, w, h, fill, text, opts = {}) {
  slide.addShape(pres.ShapeType.roundRect, { x, y, w, h, fill: { color: fill }, line: { color: fill, width: 0 },
    rectRadius: 0.08, shadow: opts.shadow ? { type: 'outer', color: '000000', blur: 4, offset: 2, angle: 90, opacity: 0.18 } : undefined });
  if (text) slide.addText(text, { x, y, w, h, fontFace: BODY, fontSize: opts.size || 12, color: opts.color || C.white,
    bold: opts.bold !== false, align: 'center', valign: 'middle', isTextBox: true, margin: 0.05 });
}
function arrow(slide, x1, y1, x2, y2, color = C.grey, width = 2) {
  const x = Math.min(x1, x2), y = Math.min(y1, y2);
  const w = Math.abs(x2 - x1), h = Math.abs(y2 - y1);
  slide.addShape(pres.ShapeType.line, { x, y, w: w || 0.01, h: h || 0.01, line: { color, width, endArrowType: 'triangle' },
    flipH: x2 < x1, flipV: y2 < y1 });
}
function object(slide, x, y, size = 0.28) {
  slide.addShape(pres.ShapeType.rect, { x, y, w: size, h: size, fill: { color: C.amber }, line: { color: 'B47C00', width: 1 } });
}
// Una "persona" della fila: un rettangolo con il nome sotto.
function person(slide, x, y, label, opts = {}) {
  const w = opts.w || 1.15, h = opts.h || 0.75;
  box(slide, x, y, w, h, opts.fill || C.steel, opts.inner || '', { size: 11 });
  slide.addText(label, { x: x - 0.1, y: y + h + 0.05, w: w + 0.2, h: 0.3, fontFace: BODY, fontSize: 11,
    color: C.ink, align: 'center', isTextBox: true, margin: 0 });
}
function row(slide, y, labels, opts = {}) {
  const w = 1.15, gap = 0.45, x0 = opts.x0 || 0.7;
  labels.forEach((l, i) => {
    const x = x0 + i * (w + gap);
    person(slide, x, y, l, { fill: opts.fills ? opts.fills[i] : C.steel, inner: opts.inner ? opts.inner[i] : '' });
    if (i < labels.length - 1) arrow(slide, x + w + 0.05, y + 0.375, x + w + gap - 0.05, y + 0.375, C.grey, 2.5);
  });
  return { w, gap, x0 };
}

// ---------- 1. Titolo ----------
{
  const s = pres.addSlide(); bg(s, C.ink);
  s.addText('Il WCS', { x: 0.7, y: 1.2, w: 8.6, h: 1.0, fontFace: HEAD, fontSize: 54, bold: true, color: C.white, isTextBox: true, margin: 0 });
  s.addText('Come ragiona una fila di macchine che si passano oggetti', { x: 0.7, y: 2.2, w: 8.6, h: 0.7, fontFace: BODY, fontSize: 22, color: C.mist, isTextBox: true, margin: 0 });
  // la fila in miniatura
  const y = 3.5; const labels = ['Ingresso', 'Nastro', 'Smistatore', 'Nastro', 'Magazzino', 'Uscita'];
  labels.forEach((l, i) => {
    const x = 0.7 + i * 1.45;
    box(s, x, y, 1.0, 0.55, i === 4 ? C.wms : C.steel, '', {});
    if (i < labels.length - 1) arrow(s, x + 1.05, y + 0.275, x + 1.4, y + 0.275, C.mist, 2);
  });
  object(s, 0.7 + 2 * 1.45 + 0.36, y + 0.13);
  s.addText('Suntech · settembre 2026', { x: 0.7, y: 4.8, w: 8.6, h: 0.4, fontFace: BODY, fontSize: 12, color: C.grey, isTextBox: true, margin: 0 });
  note(s, 'Apertura. Una frase: il WCS è il livello che sta fra il magazzino e le macchine, e ragiona di percorso.');
}

// ---------- 2. L'idea ----------
{
  const s = pres.addSlide(); bg(s, C.light);
  title(s, 'L’idea: una fila che si passa oggetti');
  const r = row(s, 1.5, ['Ingresso', 'Nastro 1', 'Nastro 2', 'Smistatore', 'Nastro 3', 'Uscita']);
  object(s, r.x0 + 2 * (r.w + r.gap) + 0.43, 1.73);
  body(s, 'Ognuno riceve un oggetto da chi gli sta prima, fa la sua parte, e lo passa a chi gli sta dopo.', 0.7, 2.95, 8.6, 0.5, { size: 16 });
  body(s, [
    { text: 'Nessuno vede tutta la fila: ', options: { bold: true } },
    { text: 'ognuno sa solo che cosa ha in mano e a chi passarlo.' },
  ], 0.7, 3.5, 8.6, 0.5, { size: 14 });
  body(s, [
    { text: 'Il WCS è chi coordina la fila: ', options: { bold: true } },
    { text: 'parla con ciascuno, uno alla volta, e decide il passo successivo dell’oggetto.' },
  ], 0.7, 3.95, 8.6, 0.5, { size: 14 });
  box(s, 0.7, 4.65, 8.6, 0.55, C.mist, 'L’oggetto è un’unità di carico: un bancale, un contenitore, una cassa. Nel seguito lo chiamiamo «oggetto».', { color: C.ink, bold: false, size: 12 });
  note(s, 'La metafora regge tutto il resto: persone = dispositivi (nastri, smistatori, trasloelevatori), oggetto = unità di carico, il WCS = chi coordina.');
}

// ---------- 3. Ognuno ha la sua logica ----------
{
  const s = pres.addSlide(); bg(s, C.light);
  title(s, 'Ognuno ha la sua logica: la macchina e il suo software');
  // zoom su una persona
  box(s, 0.7, 1.45, 3.2, 3.6, C.steel, '', { shadow: true });
  s.addText('Una persona della fila', { x: 0.85, y: 1.55, w: 2.9, h: 0.4, fontFace: BODY, fontSize: 14, bold: true, color: C.white, isTextBox: true, margin: 0 });
  box(s, 0.9, 2.05, 2.8, 0.75, C.steelDark, 'La macchina\n(motori, sensori, PLC)', { size: 12 });
  box(s, 0.9, 2.9, 2.8, 0.75, C.steelDark, 'Il suo software\n(che cosa sa fare, e come)', { size: 12 });
  box(s, 0.9, 3.75, 2.8, 1.15, C.white, 'Quello che sa fare:\naccettare un oggetto · passarlo avanti\nleggere l’etichetta · fermarsi', { color: C.ink, bold: false, size: 11 });
  arrow(s, 4.1, 3.2, 4.6, 3.2, C.grey, 2.5);
  body(s, 'Il WCS non muove i motori: chiede alla macchina di fare una cosa che lei sa fare, e aspetta che lei dica com’è andata.', 4.8, 1.6, 4.5, 1.1, { size: 15 });
  body(s, [
    { text: 'Capacità diverse, stesso dialogo. ', options: { bold: true } },
    { text: 'Un nastro sa solo passare avanti; uno smistatore sceglie fra due uscite; un trasloelevatore riposa e preleva. Il WCS parla con tutti nello stesso modo, e ognuno risponde con quello che sa fare.' },
  ], 4.8, 2.85, 4.5, 1.5, { size: 13 });
  body(s, [
    { text: 'Stato, non comando. ', options: { bold: true } },
    { text: 'Ogni persona è «pronta», «in movimento», «occupata» o «fuori linea»: il WCS legge lo stato prima di chiedere.' },
  ], 4.8, 4.35, 4.5, 0.9, { size: 13 });
  note(s, 'Il punto da far passare: il WCS non è il software della macchina. È il coordinatore che le parla.');
}

// ---------- 4. Il dialogo ----------
{
  const s = pres.addSlide(); bg(s, C.light);
  title(s, 'Il dialogo: cinque richieste, sei risposte');
  box(s, 0.7, 1.4, 2.0, 3.75, C.wcs, 'WCS', { size: 20 });
  box(s, 7.3, 1.4, 2.0, 3.75, C.steel, 'La macchina', { size: 18 });
  // etichetta sopra, freccia sotto: non si toccano
  const req = ['Prendi questo oggetto', 'Scarica l’oggetto', 'Porta l’oggetto da A a B', 'Leggi i dati dell’oggetto', 'Mostra un messaggio'];
  req.forEach((t, i) => {
    const y = 1.5 + i * 0.35;
    s.addText(t, { x: 2.9, y, w: 4.2, h: 0.24, fontFace: BODY, fontSize: 10.5, color: C.wcs, isTextBox: true, margin: 0, valign: 'bottom' });
    arrow(s, 2.8, y + 0.27, 7.2, y + 0.27, C.wcs, 1.75);
  });
  const ans = ['È successo qualcosa (un evento)', 'Movimento completato', 'Ecco i dati dell’oggetto', 'C’è un oggetto nuovo su di me', 'L’oggetto è uscito', 'Mi servono i dati dell’oggetto'];
  ans.forEach((t, i) => {
    const y = 3.38 + i * 0.29;
    s.addText(t, { x: 2.9, y, w: 4.2, h: 0.22, fontFace: BODY, fontSize: 10, color: C.steelDark, isTextBox: true, margin: 0, align: 'right', valign: 'bottom' });
    arrow(s, 7.2, y + 0.24, 2.8, y + 0.24, C.steel, 1.75);
  });
  body(s, 'Sono i canali dell’interfaccia. Qualunque macchina che sappia parlare così può stare nella fila.', 0.7, 5.25, 8.6, 0.3, { size: 12, italic: true, color: C.grey });
  note(s, 'Sopra le richieste (verdi, dal WCS), sotto le risposte (blu, dalla macchina). Sono davvero i canali del codice: 5 metodi e 6 eventi dell’interfaccia di connessione.');
}

// ---------- 5. Aspetta, poi decide ----------
{
  const s = pres.addSlide(); bg(s, C.light);
  title(s, 'Chiede, aspetta la risposta, poi decide');
  // ciclo: 4 tappe
  const steps = [['1', 'Legge lo stato', 'della persona\n(pronta? occupata?)'], ['2', 'Chiede', '«porta l’oggetto\nda A a B»'], ['3', 'Aspetta', 'la risposta della\nmacchina'], ['4', 'Decide', 'il passo successivo,\no aspetta ancora']];
  steps.forEach(([n, h, t], i) => {
    const x = 0.7 + i * 2.25;
    slideCard(s, x, 1.5, 1.95, 2.1, n, h, t);
    if (i < 3) arrow(s, x + 2.0, 2.55, x + 2.2, 2.55, C.grey, 2.5);
  });
  arrow(s, 8.4, 3.75, 1.5, 3.75, C.amber, 2.5);
  body(s, 'e ricomincia per il passo dopo', 3.6, 3.85, 3.0, 0.3, { size: 11, italic: true, color: C.grey, align: 'center' });
  body(s, [
    { text: 'Nessuna risposta non è un silenzio: ', options: { bold: true } },
    { text: 'è un fatto. La persona resta «occupata» finché non parla, l’oggetto aspetta, e il WCS lo scrive nel suo registro. Il resto della fila continua.' },
  ], 0.7, 4.3, 8.6, 0.9, { size: 13 });
  note(s, 'Il ciclo vale per ogni passo di ogni oggetto. Il WCS non prevede: osserva e reagisce.');
}
function slideCard(s, x, y, w, h, n, head, text) {
  box(s, x, y, w, h, C.white, '', { shadow: true });
  s.addShape(pres.ShapeType.ellipse, { x: x + 0.15, y: y + 0.15, w: 0.5, h: 0.5, fill: { color: C.amber }, line: { color: C.amber, width: 0 } });
  s.addText(n, { x: x + 0.15, y: y + 0.15, w: 0.5, h: 0.5, fontFace: HEAD, fontSize: 18, bold: true, color: C.ink, align: 'center', valign: 'middle', isTextBox: true, margin: 0 });
  s.addText(head, { x: x + 0.15, y: y + 0.75, w: w - 0.3, h: 0.4, fontFace: BODY, fontSize: 15, bold: true, color: C.ink, isTextBox: true, margin: 0 });
  s.addText(text, { x: x + 0.15, y: y + 1.15, w: w - 0.3, h: 0.9, fontFace: BODY, fontSize: 11.5, color: C.grey, isTextBox: true, margin: 0 });
}

// ---------- 6. La rotta ----------
{
  const s = pres.addSlide(); bg(s, C.light);
  title(s, 'L’oggetto chiede la strada');
  // grafo: nodi e collegamenti
  const nodes = { A: [0.9, 2.4], B: [2.4, 1.6], C: [2.4, 3.2], D: [3.9, 2.4], E: [5.4, 1.6], F: [5.4, 3.2], G: [6.9, 2.4] };
  const edges = [['A', 'B'], ['A', 'C'], ['B', 'D'], ['C', 'D'], ['D', 'E'], ['D', 'F'], ['E', 'G'], ['F', 'G']];
  const path = new Set(['A-C', 'C-D', 'D-F', 'F-G']);
  edges.forEach(([a, b]) => {
    const [x1, y1] = nodes[a], [x2, y2] = nodes[b];
    const on = path.has(a + '-' + b);
    arrow(s, x1 + 0.45, y1 + 0.2, x2 - 0.05, y2 + 0.2, on ? C.amber : C.mist, on ? 3 : 2);
  });
  const names = { A: 'Ingresso', B: 'Nastro 1', C: 'Nastro 2', D: 'Smistatore', E: 'Nastro 3', F: 'Nastro 4', G: 'Magazzino' };
  Object.entries(nodes).forEach(([k, [x, y]]) => {
    const blocked = k === 'B';
    box(s, x, y, 0.5, 0.4, blocked ? C.red : (k === 'G' ? C.wms : C.steel), '', {});
    s.addText(names[k], { x: x - 0.35, y: y + 0.42, w: 1.2, h: 0.25, fontFace: BODY, fontSize: 9.5, color: C.ink, align: 'center', isTextBox: true, margin: 0 });
  });
  s.addText('(fuori linea)', { x: 2.15, y: 2.27, w: 1.0, h: 0.22, fontFace: BODY, fontSize: 9, color: C.red, align: 'center', isTextBox: true, margin: 0 });
  object(s, 1.01, 2.46, 0.26);
  box(s, 7.6, 1.1, 1.75, 1.1, C.wcs, 'Il gestore\ndelle rotte', { size: 13 });
  // la domanda sale dall'oggetto e corre sopra il grafo, senza attraversarlo
  s.addShape(pres.ShapeType.line, { x: 1.14, y: 1.2, w: 0.01, h: 1.2, line: { color: C.wcs, width: 1.5 } });
  arrow(s, 1.14, 1.2, 7.5, 1.2, C.wcs, 1.5);
  s.addText('«dove vado?»', { x: 3.6, y: 0.93, w: 1.5, h: 0.25, fontFace: BODY, fontSize: 10, italic: true, color: C.wcs, isTextBox: true, margin: 0 });
  body(s, [
    { text: 'L’impianto è una mappa, non un programma. ', options: { bold: true } },
    { text: 'I punti e i collegamenti sono dati disegnati nell’Editor; la strada la calcola il gestore delle rotte ' },
    { text: 'a ogni salto', options: { bold: true } },
    { text: ', guardando la situazione di quel momento: se una persona è fuori linea o occupata, l’oggetto passa dall’altra parte.' },
  ], 0.7, 3.95, 8.6, 1.3, { size: 13 });
  note(s, 'Il grafo si disegna, non si programma. Una macchina in più = un nodo in più. La rotta non è fissata alla partenza: viene riconsiderata a ogni passo.');
}

// ---------- 7. Il magazzino e il WCS ----------
{
  const s = pres.addSlide(); bg(s, C.light);
  title(s, 'Due mestieri: il magazzino ragiona di merce, il WCS di percorso');
  box(s, 0.7, 1.5, 3.6, 3.0, C.wms, '', { shadow: true });
  s.addText('Il gestionale (WMS)', { x: 0.9, y: 1.6, w: 3.2, h: 0.4, fontFace: BODY, fontSize: 16, bold: true, color: C.white, isTextBox: true, margin: 0 });
  s.addText([
    { text: 'sa che cosa c’è e dove', options: { bullet: true, breakLine: true } },
    { text: 'decide in quale cella riporre', options: { bullet: true, breakLine: true } },
    { text: 'tiene le note e gli ordini', options: { bullet: true, breakLine: true } },
    { text: 'chiude le lavorazioni', options: { bullet: true } },
  ], { x: 0.9, y: 2.1, w: 3.2, h: 2.2, fontFace: BODY, fontSize: 13, color: C.white, isTextBox: true, margin: 0, paraSpaceAfter: 6 });
  box(s, 5.7, 1.5, 3.6, 3.0, C.wcs, '', { shadow: true });
  s.addText('Il WCS', { x: 5.9, y: 1.6, w: 3.2, h: 0.4, fontFace: BODY, fontSize: 16, bold: true, color: C.white, isTextBox: true, margin: 0 });
  s.addText([
    { text: 'sa com’è fatto l’impianto', options: { bullet: true, breakLine: true } },
    { text: 'porta l’oggetto da un punto all’altro', options: { bullet: true, breakLine: true } },
    { text: 'chiede: «dove la metto?»', options: { bullet: true, breakLine: true } },
    { text: 'racconta: «è arrivata», «è uscita»', options: { bullet: true } },
  ], { x: 5.9, y: 2.1, w: 3.2, h: 2.2, fontFace: BODY, fontSize: 13, color: C.white, isTextBox: true, margin: 0, paraSpaceAfter: 6 });
  arrow(s, 4.4, 2.5, 5.6, 2.5, C.wms, 2.5); s.addText('«mettila nella cella 12»', { x: 4.35, y: 2.15, w: 1.35, h: 0.3, fontFace: BODY, fontSize: 9.5, color: C.wms, align: 'center', isTextBox: true, margin: 0 });
  arrow(s, 5.6, 3.4, 4.4, 3.4, C.wcs, 2.5); s.addText('«fatto» · «dove la metto?»', { x: 4.35, y: 3.5, w: 1.35, h: 0.45, fontFace: BODY, fontSize: 9.5, color: C.wcs, align: 'center', isTextBox: true, margin: 0 });
  body(s, [
    { text: 'Il WCS non scrive nei libri del magazzino: ', options: { bold: true } },
    { text: 'racconta i fatti, e il magazzino ne trae le conseguenze. I due parlano da programma a programma, attraverso un confine dichiarato, e possono vivere su computer diversi.' },
  ], 0.7, 4.7, 8.6, 0.75, { size: 13 });
  note(s, 'È la regola di casa decisa a settembre: il WCS segnala, il gestionale decide. Il confine è HTTP con una chiave; se il gestionale non risponde, l’oggetto aspetta e il WCS riprova.');
}

// ---------- 8. Qualunque macchina ----------
{
  const s = pres.addSlide(); bg(s, C.light);
  title(s, 'Qualunque macchina, purché parli il dialogo');
  box(s, 0.7, 1.6, 2.2, 1.2, C.wcs, 'WCS', { size: 20 });
  box(s, 3.6, 1.6, 2.6, 1.2, C.ink, 'Il protocollo\n(l’interprete)', { size: 14 });
  arrow(s, 2.95, 2.2, 3.55, 2.2, C.grey, 2.5); arrow(s, 3.55, 2.4, 2.95, 2.4, C.grey, 2.5);
  const machines = [['PLC di marca A', C.steel], ['PLC di marca B', C.steel], ['Robot / navetta', C.steel], ['Simulatore\n(per le prove)', C.steelDark]];
  machines.forEach(([m, col], i) => {
    const y = 1.35 + i * 0.95;
    box(s, 7.0, y, 2.3, 0.75, col, m, { size: 12 });
    arrow(s, 6.25, 2.2, 6.95, y + 0.375, C.grey, 1.5);
  });
  body(s, [
    { text: 'Il WCS conosce i canali, non le macchine. ', options: { bold: true } },
    { text: 'Per collegare una macchina nuova si scrive il suo interprete — il protocollo — che traduce le cinque richieste e le sei risposte nella lingua di quel PLC. Il resto del WCS non cambia.' },
  ], 0.7, 3.1, 5.6, 1.3, { size: 13 });
  body(s, [
    { text: 'Il simulatore è una macchina come le altre: ', options: { bold: true } },
    { text: 'parla il dialogo senza motori. È così che l’impianto si prova in ufficio, prima del cantiere.' },
  ], 0.7, 4.45, 5.6, 0.85, { size: 13 });
  note(s, 'Oggi esiste un protocollo (per i PLC di un fornitore) e la modalità di prova. Il punto: la porta è la stessa per tutti.');
}

// ---------- 9. Che cosa vede chi lavora ----------
{
  const s = pres.addSlide(); bg(s, C.light);
  title(s, 'Che cosa vede chi lavora');
  const cards = [
    ['L’Editor', 'Disegna l’impianto: i punti, i collegamenti, le proprietà di ogni macchina. Guarda gli oggetti muoversi sul disegno.'],
    ['Il Client del magazzino', 'Ordini, estrazioni, note: da qui si chiede «tira fuori questo», e si vede la fila di uscita avanzare.'],
    ['Il motore', 'Gira sempre, senza finestre: coordina la fila, tiene il registro, risponde all’Editor e al Client.'],
  ];
  cards.forEach(([h, t], i) => {
    const x = 0.7 + i * 2.95;
    box(s, x, 1.5, 2.7, 2.9, C.white, '', { shadow: true });
    box(s, x + 0.2, 1.7, 0.6, 0.6, i === 2 ? C.wcs : C.steel, '', {});
    s.addText(h, { x: x + 0.2, y: 2.45, w: 2.3, h: 0.4, fontFace: BODY, fontSize: 15, bold: true, color: C.ink, isTextBox: true, margin: 0 });
    s.addText(t, { x: x + 0.2, y: 2.9, w: 2.3, h: 1.4, fontFace: BODY, fontSize: 11.5, color: C.grey, isTextBox: true, margin: 0 });
  });
  body(s, 'Editor e Client parlano solo con il motore; il motore parla con il magazzino e con le macchine. Nessuno scavalca nessuno.', 0.7, 4.65, 8.6, 0.5, { size: 13, italic: true, color: C.grey });
  note(s, 'Domani: l’Editor con il disegno vero, il Client con un’estrazione, il registro del motore letto a voce.');
}

// ---------- 10. Quando qualcosa va storto ----------
{
  const s = pres.addSlide(); bg(s, C.light);
  title(s, 'Quando qualcosa va storto, la fila non si ferma');
  const cases = [
    ['Il magazzino non risponde', 'Gli oggetti che aspettavano una cella restano dove sono, «in attesa dichiarata». Il WCS lo scrive, riprova, e quando il magazzino torna riprende da solo. Provato: 40 secondi senza gestionale, zero oggetti persi.'],
    ['Una macchina non risponde', 'Quella persona è «fuori linea»: chi doveva passarle un oggetto lo tiene, il gestore delle rotte cerca un’altra strada, il resto della fila continua.'],
    ['Un oggetto senza dati', 'Il WCS chiede alla macchina di leggerli; se non ci sono, l’oggetto va a un punto di rigetto, e chi lavora lo vede nel Client.'],
  ];
  cases.forEach(([h, t], i) => {
    const y = 1.45 + i * 1.25;
    s.addShape(pres.ShapeType.ellipse, { x: 0.7, y: y + 0.1, w: 0.45, h: 0.45, fill: { color: C.amber }, line: { color: C.amber, width: 0 } });
    s.addText(h, { x: 1.35, y, w: 7.9, h: 0.35, fontFace: BODY, fontSize: 15, bold: true, color: C.ink, isTextBox: true, margin: 0 });
    s.addText(t, { x: 1.35, y: y + 0.38, w: 7.9, h: 0.8, fontFace: BODY, fontSize: 12, color: C.grey, isTextBox: true, margin: 0 });
  });
  note(s, 'Il principio: un guasto noto, non uno sconosciuto. Ogni attesa ha un nome e una riga nel registro.');
}

// ---------- 11. La roadmap ----------
{
  const s = pres.addSlide(); bg(s, C.light);
  title(s, 'Dove va il prodotto');
  const steps = [
    ['1', 'Il confine', 'Il WCS parla con il magazzino solo attraverso un contratto dichiarato, via HTTP.', 'fatta'],
    ['2', 'Il repository', 'Il WCS ha il suo repository, con tutta la storia; il gestionale lo consuma.', 'fatta'],
    ['3', 'Senza SQL', 'Le ultime letture dirette del motore sul database del magazzino passano il confine.', 'specificata'],
    ['4', 'Le entità dell’integratore', 'Una persona nuova nella fila, con una logica sua, si scrive rispettando la firma del dispositivo base: il motore non cambia.', 'dichiarata'],
    ['5', 'L’Editor web', 'Il disegno dell’impianto si fa nel browser, con un visualizzatore in Blazor.', 'dichiarata'],
    ['6', 'Pacchetti', 'I pezzi condivisi con il gestionale diventano pacchetti NuGet.', 'dichiarata'],
  ];
  const stateFill = { fatta: C.wcs, specificata: C.amber, dichiarata: C.mist };
  const stateColor = { fatta: C.white, specificata: C.ink, dichiarata: C.ink };
  steps.forEach(([n, h, t, st], i) => {
    const x = 0.7 + (i % 3) * 2.95, y = 1.3 + Math.floor(i / 3) * 1.75;
    box(s, x, y, 2.7, 1.55, C.white, '', { shadow: true });
    s.addShape(pres.ShapeType.ellipse, { x: x + 0.15, y: y + 0.15, w: 0.4, h: 0.4, fill: { color: i === 3 ? C.amber : C.steel }, line: { color: i === 3 ? C.amber : C.steel, width: 0 } });
    s.addText(n, { x: x + 0.15, y: y + 0.15, w: 0.4, h: 0.4, fontFace: HEAD, fontSize: 14, bold: true, color: i === 3 ? C.ink : C.white, align: 'center', valign: 'middle', isTextBox: true, margin: 0 });
    s.addText(h, { x: x + 0.65, y: y + 0.15, w: 1.25, h: 0.4, fontFace: BODY, fontSize: 12.5, bold: true, color: C.ink, valign: 'middle', isTextBox: true, margin: 0 });
    s.addShape(pres.ShapeType.roundRect, { x: x + 1.85, y: y + 0.2, w: 0.75, h: 0.28, fill: { color: stateFill[st] }, line: { color: stateFill[st], width: 0 }, rectRadius: 0.14 });
    s.addText(st, { x: x + 1.85, y: y + 0.2, w: 0.75, h: 0.28, fontFace: BODY, fontSize: 8.5, color: stateColor[st], align: 'center', valign: 'middle', isTextBox: true, margin: 0 });
    s.addText(t, { x: x + 0.15, y: y + 0.65, w: 2.4, h: 0.85, fontFace: BODY, fontSize: 10.5, color: C.grey, isTextBox: true, margin: 0 });
  });
  body(s, 'Le prime due sono fatte; la terza parte dopo questa presentazione. La quarta è il gemello del protocollo: come si può collegare qualunque macchina, si potrà insegnare al WCS qualunque modo di ragionare.', 0.7, 4.8, 8.6, 0.6, { size: 11.5, italic: true, color: C.grey });
  note(s, 'La roadmap dichiarata. La tappa 4 va spiegata con la metafora: oggi i tipi di persona sono sette, fissi nel motore; domani chi integra ne scrive uno nuovo rispettando la firma del dispositivo base, e l’Editor lo vede nella palette.');
}

// ---------- 12. Chiusura ----------
{
  const s = pres.addSlide(); bg(s, C.ink);
  s.addText('In una frase', { x: 0.7, y: 0.8, w: 8.6, h: 0.6, fontFace: HEAD, fontSize: 30, bold: true, color: C.white, isTextBox: true, margin: 0 });
  s.addText('Il WCS è chi coordina una fila di macchine: parla con ciascuna nello stesso modo, aspetta le risposte, decide il passo successivo, e chiede la strada a chi conosce la mappa.', { x: 0.7, y: 1.5, w: 8.6, h: 1.4, fontFace: BODY, fontSize: 20, color: C.mist, isTextBox: true, margin: 0 });
  const pts = [['Mappa, non programma', 'una macchina in più è un nodo in più'], ['Dialogo, non controllo', 'cinque richieste, sei risposte, qualunque hardware'], ['Fatti, non ordini', 'il magazzino decide, il WCS racconta']];
  pts.forEach(([h, t], i) => {
    const x = 0.7 + i * 2.95;
    box(s, x, 3.2, 2.7, 1.5, C.steelDark, '', {});
    s.addText(h, { x: x + 0.2, y: 3.35, w: 2.3, h: 0.45, fontFace: BODY, fontSize: 14, bold: true, color: C.amber, isTextBox: true, margin: 0 });
    s.addText(t, { x: x + 0.2, y: 3.85, w: 2.3, h: 0.7, fontFace: BODY, fontSize: 12, color: C.mist, isTextBox: true, margin: 0 });
  });
  s.addText('Prossimi passi: il motore senza SQL Server, le entità dell’integratore, l’Editor nel browser.', { x: 0.7, y: 4.95, w: 8.6, h: 0.4, fontFace: BODY, fontSize: 12, italic: true, color: C.grey, isTextBox: true, margin: 0 });
  note(s, 'Chiusura: la frase, i tre principi, e il rimando alla roadmap della slide precedente.');
}

pres.writeFile({ fileName: 'Il-WCS-come-ragiona-una-fila-di-macchine.pptx' }).then(f => console.log('scritto', f));
