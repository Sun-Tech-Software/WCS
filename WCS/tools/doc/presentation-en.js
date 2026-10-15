// The WCS deck for non-programmers, American English edition: a line of people passing objects along.
// Usage: npm install pptxgenjs && node presentation-en.js  (writes The-WCS-How-a-Line-of-Machines-Thinks.pptx)
// Every diagram is made of native shapes (rectangles, arrows, text): no images.
const pptxgen = require('pptxgenjs');
const pres = new pptxgen();
pres.layout = 'LAYOUT_16x9'; // 10" x 5.625"
pres.title = 'The WCS: how a line of machines thinks';

// Palette: steel and amber, like a plant.
const C = {
  ink: '1F2A37',      // dark background and text
  steel: '4A6FA5',    // the machines
  steelDark: '2F4B7A',
  amber: 'F2A900',    // the object, the accent
  light: 'F4F6F8',    // light background
  mist: 'D9E0E8',     // soft panels
  white: 'FFFFFF',
  wcs: '2E7D6B',      // the WCS
  wms: '8A5A2B',      // the WMS
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
// A "person" in the line: a rectangle with its name below.
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

// ---------- 1. Title ----------
{
  const s = pres.addSlide(); bg(s, C.ink);
  s.addText('The WCS', { x: 0.7, y: 1.2, w: 8.6, h: 1.0, fontFace: HEAD, fontSize: 54, bold: true, color: C.white, isTextBox: true, margin: 0 });
  s.addText('How a line of machines passing objects along thinks', { x: 0.7, y: 2.2, w: 8.6, h: 0.7, fontFace: BODY, fontSize: 22, color: C.mist, isTextBox: true, margin: 0 });
  // the line in miniature
  const y = 3.5; const labels = ['Entry', 'Conveyor', 'Sorter', 'Conveyor', 'Warehouse', 'Exit'];
  labels.forEach((l, i) => {
    const x = 0.7 + i * 1.45;
    box(s, x, y, 1.0, 0.55, i === 4 ? C.wms : C.steel, '', {});
    if (i < labels.length - 1) arrow(s, x + 1.05, y + 0.275, x + 1.4, y + 0.275, C.mist, 2);
  });
  object(s, 0.7 + 2 * 1.45 + 0.36, y + 0.13);
  s.addText('Suntech · September 2026', { x: 0.7, y: 4.8, w: 8.6, h: 0.4, fontFace: BODY, fontSize: 12, color: C.grey, isTextBox: true, margin: 0 });
  note(s, 'Opening. One sentence: the WCS is the layer between the warehouse and the machines, and it reasons about routes.');
}

// ---------- 2. The idea ----------
{
  const s = pres.addSlide(); bg(s, C.light);
  title(s, 'The idea: a line passing objects along');
  const r = row(s, 1.5, ['Entry', 'Conveyor 1', 'Conveyor 2', 'Sorter', 'Conveyor 3', 'Exit']);
  object(s, r.x0 + 2 * (r.w + r.gap) + 0.43, 1.73);
  body(s, 'Each one receives an object from the one before, does its part, and passes it to the one after.', 0.7, 2.95, 8.6, 0.5, { size: 16 });
  body(s, [
    { text: 'Nobody sees the whole line: ', options: { bold: true } },
    { text: 'each one only knows what they are holding and who to hand it to.' },
  ], 0.7, 3.5, 8.6, 0.5, { size: 14 });
  body(s, [
    { text: 'The WCS is the one who coordinates the line: ', options: { bold: true } },
    { text: 'it talks to each one, one at a time, and decides the object’s next step.' },
  ], 0.7, 3.95, 8.6, 0.5, { size: 14 });
  box(s, 0.7, 4.65, 8.6, 0.55, C.mist, 'The object is a load unit: a pallet, a container, a tote. From here on we call it “the object”.', { color: C.ink, bold: false, size: 12 });
  note(s, 'The metaphor carries everything else: people = devices (conveyors, sorters, stacker cranes), object = load unit, the WCS = the coordinator.');
}

// ---------- 3. Each one has its own logic ----------
{
  const s = pres.addSlide(); bg(s, C.light);
  title(s, 'Each one has its own logic: the machine and its software');
  // zoom on one person
  box(s, 0.7, 1.45, 3.2, 3.6, C.steel, '', { shadow: true });
  s.addText('One person in the line', { x: 0.85, y: 1.55, w: 2.9, h: 0.4, fontFace: BODY, fontSize: 14, bold: true, color: C.white, isTextBox: true, margin: 0 });
  box(s, 0.9, 2.05, 2.8, 0.75, C.steelDark, 'The machine\n(motors, sensors, PLC)', { size: 12 });
  box(s, 0.9, 2.9, 2.8, 0.75, C.steelDark, 'Its software\n(what it can do, and how)', { size: 12 });
  box(s, 0.9, 3.75, 2.8, 1.15, C.white, 'What it can do:\naccept an object · pass it forward\nread the label · stop', { color: C.ink, bold: false, size: 11 });
  arrow(s, 4.1, 3.2, 4.6, 3.2, C.grey, 2.5);
  body(s, 'The WCS does not drive the motors: it asks the machine to do something it knows how to do, and waits for it to say how it went.', 4.8, 1.6, 4.5, 1.1, { size: 15 });
  body(s, [
    { text: 'Different capabilities, same dialogue. ', options: { bold: true } },
    { text: 'A conveyor can only pass forward; a sorter chooses between two exits; a stacker crane puts away and retrieves. The WCS talks to all of them the same way, and each answers with what it can do.' },
  ], 4.8, 2.85, 4.5, 1.5, { size: 13 });
  body(s, [
    { text: 'Status, not command. ', options: { bold: true } },
    { text: 'Each person is “ready”, “moving”, “busy” or “off line”: the WCS reads the status before asking.' },
  ], 4.8, 4.35, 4.5, 0.9, { size: 13 });
  note(s, 'The point to get across: the WCS is not the machine’s software. It is the coordinator that talks to it.');
}

// ---------- 4. The dialogue ----------
{
  const s = pres.addSlide(); bg(s, C.light);
  title(s, 'The dialogue: five requests, six answers');
  box(s, 0.7, 1.4, 2.0, 3.75, C.wcs, 'WCS', { size: 20 });
  box(s, 7.3, 1.4, 2.0, 3.75, C.steel, 'The machine', { size: 18 });
  // label above, arrow below: they never touch
  const req = ['Take this object', 'Unload the object', 'Carry the object from A to B', 'Read the object’s data', 'Show a message'];
  req.forEach((t, i) => {
    const y = 1.5 + i * 0.35;
    s.addText(t, { x: 2.9, y, w: 4.2, h: 0.24, fontFace: BODY, fontSize: 10.5, color: C.wcs, isTextBox: true, margin: 0, valign: 'bottom' });
    arrow(s, 2.8, y + 0.27, 7.2, y + 0.27, C.wcs, 1.75);
  });
  const ans = ['Something happened (an event)', 'Movement completed', 'Here is the object’s data', 'There is a new object on me', 'The object has left', 'I need the object’s data'];
  ans.forEach((t, i) => {
    const y = 3.38 + i * 0.29;
    s.addText(t, { x: 2.9, y, w: 4.2, h: 0.22, fontFace: BODY, fontSize: 10, color: C.steelDark, isTextBox: true, margin: 0, align: 'right', valign: 'bottom' });
    arrow(s, 7.2, y + 0.24, 2.8, y + 0.24, C.steel, 1.75);
  });
  body(s, 'These are the interface channels. Any machine that can speak this way can stand in the line.', 0.7, 5.25, 8.6, 0.3, { size: 12, italic: true, color: C.grey });
  note(s, 'Above, the requests (green, from the WCS); below, the answers (blue, from the machine). They really are the channels in the code: 5 methods and 6 events of the connection interface.');
}

// ---------- 5. Wait, then decide ----------
{
  const s = pres.addSlide(); bg(s, C.light);
  title(s, 'Ask, wait for the answer, then decide');
  // cycle: 4 stages
  const steps = [['1', 'Reads the status', 'of the person\n(ready? busy?)'], ['2', 'Asks', '“carry the object\nfrom A to B”'], ['3', 'Waits', 'for the machine’s\nanswer'], ['4', 'Decides', 'the next step,\nor keeps waiting']];
  steps.forEach(([n, h, t], i) => {
    const x = 0.7 + i * 2.25;
    slideCard(s, x, 1.5, 1.95, 2.1, n, h, t);
    if (i < 3) arrow(s, x + 2.0, 2.55, x + 2.2, 2.55, C.grey, 2.5);
  });
  arrow(s, 8.4, 3.75, 1.5, 3.75, C.amber, 2.5);
  body(s, 'and starts over for the next step', 3.6, 3.85, 3.0, 0.3, { size: 11, italic: true, color: C.grey, align: 'center' });
  body(s, [
    { text: 'No answer is not silence: ', options: { bold: true } },
    { text: 'it is a fact. The person stays “busy” until it speaks, the object waits, and the WCS writes it in its log. The rest of the line goes on.' },
  ], 0.7, 4.3, 8.6, 0.9, { size: 13 });
  note(s, 'The cycle holds for every step of every object. The WCS does not predict: it observes and reacts.');
}
function slideCard(s, x, y, w, h, n, head, text) {
  box(s, x, y, w, h, C.white, '', { shadow: true });
  s.addShape(pres.ShapeType.ellipse, { x: x + 0.15, y: y + 0.15, w: 0.5, h: 0.5, fill: { color: C.amber }, line: { color: C.amber, width: 0 } });
  s.addText(n, { x: x + 0.15, y: y + 0.15, w: 0.5, h: 0.5, fontFace: HEAD, fontSize: 18, bold: true, color: C.ink, align: 'center', valign: 'middle', isTextBox: true, margin: 0 });
  s.addText(head, { x: x + 0.15, y: y + 0.75, w: w - 0.3, h: 0.4, fontFace: BODY, fontSize: 15, bold: true, color: C.ink, isTextBox: true, margin: 0 });
  s.addText(text, { x: x + 0.15, y: y + 1.15, w: w - 0.3, h: 0.9, fontFace: BODY, fontSize: 11.5, color: C.grey, isTextBox: true, margin: 0 });
}

// ---------- 6. The route ----------
{
  const s = pres.addSlide(); bg(s, C.light);
  title(s, 'The object asks for the route');
  // graph: nodes and links
  const nodes = { A: [0.9, 2.4], B: [2.4, 1.6], C: [2.4, 3.2], D: [3.9, 2.4], E: [5.4, 1.6], F: [5.4, 3.2], G: [6.9, 2.4] };
  const edges = [['A', 'B'], ['A', 'C'], ['B', 'D'], ['C', 'D'], ['D', 'E'], ['D', 'F'], ['E', 'G'], ['F', 'G']];
  const path = new Set(['A-C', 'C-D', 'D-F', 'F-G']);
  edges.forEach(([a, b]) => {
    const [x1, y1] = nodes[a], [x2, y2] = nodes[b];
    const on = path.has(a + '-' + b);
    arrow(s, x1 + 0.45, y1 + 0.2, x2 - 0.05, y2 + 0.2, on ? C.amber : C.mist, on ? 3 : 2);
  });
  const names = { A: 'Entry', B: 'Conveyor 1', C: 'Conveyor 2', D: 'Sorter', E: 'Conveyor 3', F: 'Conveyor 4', G: 'Warehouse' };
  Object.entries(nodes).forEach(([k, [x, y]]) => {
    const blocked = k === 'B';
    box(s, x, y, 0.5, 0.4, blocked ? C.red : (k === 'G' ? C.wms : C.steel), '', {});
    s.addText(names[k], { x: x - 0.35, y: y + 0.42, w: 1.2, h: 0.25, fontFace: BODY, fontSize: 9.5, color: C.ink, align: 'center', isTextBox: true, margin: 0 });
  });
  s.addText('(off line)', { x: 2.15, y: 2.27, w: 1.0, h: 0.22, fontFace: BODY, fontSize: 9, color: C.red, align: 'center', isTextBox: true, margin: 0 });
  object(s, 1.01, 2.46, 0.26);
  box(s, 7.6, 1.1, 1.75, 1.1, C.wcs, 'The route\nmanager', { size: 13 });
  // the question rises from the object and runs above the graph, without crossing it
  s.addShape(pres.ShapeType.line, { x: 1.14, y: 1.2, w: 0.01, h: 1.2, line: { color: C.wcs, width: 1.5 } });
  arrow(s, 1.14, 1.2, 7.5, 1.2, C.wcs, 1.5);
  s.addText('“where do I go?”', { x: 3.6, y: 0.93, w: 1.5, h: 0.25, fontFace: BODY, fontSize: 10, italic: true, color: C.wcs, isTextBox: true, margin: 0 });
  body(s, [
    { text: 'The plant is a map, not a program. ', options: { bold: true } },
    { text: 'Points and links are data drawn in the Editor; the route is computed by the route manager ' },
    { text: 'at every hop', options: { bold: true } },
    { text: ', looking at the situation of that moment: if a person is off line or busy, the object goes the other way.' },
  ], 0.7, 3.95, 8.6, 1.3, { size: 13 });
  note(s, 'The graph is drawn, not programmed. One more machine = one more node. The route is not fixed at departure: it is reconsidered at every step.');
}

// ---------- 7. The warehouse and the WCS ----------
{
  const s = pres.addSlide(); bg(s, C.light);
  title(s, 'Two trades: the warehouse thinks goods, the WCS thinks routes');
  box(s, 0.7, 1.5, 3.6, 3.0, C.wms, '', { shadow: true });
  s.addText('The WMS', { x: 0.9, y: 1.6, w: 3.2, h: 0.4, fontFace: BODY, fontSize: 16, bold: true, color: C.white, isTextBox: true, margin: 0 });
  s.addText([
    { text: 'knows what there is and where', options: { bullet: true, breakLine: true } },
    { text: 'decides which cell to store in', options: { bullet: true, breakLine: true } },
    { text: 'keeps the notes and the orders', options: { bullet: true, breakLine: true } },
    { text: 'closes the jobs', options: { bullet: true } },
  ], { x: 0.9, y: 2.1, w: 3.2, h: 2.2, fontFace: BODY, fontSize: 13, color: C.white, isTextBox: true, margin: 0, paraSpaceAfter: 6 });
  box(s, 5.7, 1.5, 3.6, 3.0, C.wcs, '', { shadow: true });
  s.addText('The WCS', { x: 5.9, y: 1.6, w: 3.2, h: 0.4, fontFace: BODY, fontSize: 16, bold: true, color: C.white, isTextBox: true, margin: 0 });
  s.addText([
    { text: 'knows how the plant is laid out', options: { bullet: true, breakLine: true } },
    { text: 'carries the object from one point to another', options: { bullet: true, breakLine: true } },
    { text: 'asks: “where do I put it?”', options: { bullet: true, breakLine: true } },
    { text: 'reports: “it arrived”, “it left”', options: { bullet: true } },
  ], { x: 5.9, y: 2.1, w: 3.2, h: 2.2, fontFace: BODY, fontSize: 13, color: C.white, isTextBox: true, margin: 0, paraSpaceAfter: 6 });
  arrow(s, 4.4, 2.5, 5.6, 2.5, C.wms, 2.5); s.addText('“put it in cell 12”', { x: 4.35, y: 2.15, w: 1.35, h: 0.3, fontFace: BODY, fontSize: 9.5, color: C.wms, align: 'center', isTextBox: true, margin: 0 });
  arrow(s, 5.6, 3.4, 4.4, 3.4, C.wcs, 2.5); s.addText('“done” · “where do I put it?”', { x: 4.35, y: 3.5, w: 1.35, h: 0.45, fontFace: BODY, fontSize: 9.5, color: C.wcs, align: 'center', isTextBox: true, margin: 0 });
  body(s, [
    { text: 'The WCS does not write in the warehouse’s books: ', options: { bold: true } },
    { text: 'it reports the facts, and the warehouse draws the consequences. The two talk program to program, across a declared boundary, and can live on different computers.' },
  ], 0.7, 4.7, 8.6, 0.75, { size: 13 });
  note(s, 'The house rule decided in September: the WCS reports, the WMS decides. The boundary is HTTP with a key; if the WMS does not answer, the object waits and the WCS retries.');
}

// ---------- 8. Any machine ----------
{
  const s = pres.addSlide(); bg(s, C.light);
  title(s, 'Any machine, as long as it speaks the dialogue');
  box(s, 0.7, 1.6, 2.2, 1.2, C.wcs, 'WCS', { size: 20 });
  box(s, 3.6, 1.6, 2.6, 1.2, C.ink, 'The protocol\n(the interpreter)', { size: 14 });
  arrow(s, 2.95, 2.2, 3.55, 2.2, C.grey, 2.5); arrow(s, 3.55, 2.4, 2.95, 2.4, C.grey, 2.5);
  const machines = [['PLC from vendor A', C.steel], ['PLC from vendor B', C.steel], ['Robot / shuttle', C.steel], ['Simulator\n(for testing)', C.steelDark]];
  machines.forEach(([m, col], i) => {
    const y = 1.35 + i * 0.95;
    box(s, 7.0, y, 2.3, 0.75, col, m, { size: 12 });
    arrow(s, 6.25, 2.2, 6.95, y + 0.375, C.grey, 1.5);
  });
  body(s, [
    { text: 'The WCS knows the channels, not the machines. ', options: { bold: true } },
    { text: 'To connect a new machine you write its interpreter — the protocol — which translates the five requests and the six answers into that PLC’s language. The rest of the WCS does not change.' },
  ], 0.7, 3.1, 5.6, 1.3, { size: 13 });
  body(s, [
    { text: 'The simulator is a machine like the others: ', options: { bold: true } },
    { text: 'it speaks the dialogue without motors. That is how the plant is tested in the office, before the site.' },
  ], 0.7, 4.45, 5.6, 0.85, { size: 13 });
  note(s, 'Today there is one protocol (for one vendor’s PLCs) and the test mode. The point: the door is the same for everyone.');
}

// ---------- 9. What the people at work see ----------
{
  const s = pres.addSlide(); bg(s, C.light);
  title(s, 'What the people at work see');
  const cards = [
    ['The Editor', 'Draws the plant: the points, the links, the properties of each machine. Watches the objects move on the drawing.'],
    ['The warehouse client', 'Orders, extractions, notes: from here you ask “get this out”, and you watch the exit line advance.'],
    ['The engine', 'Always running, no windows: it coordinates the line, keeps the log, answers the Editor and the client.'],
  ];
  cards.forEach(([h, t], i) => {
    const x = 0.7 + i * 2.95;
    box(s, x, 1.5, 2.7, 2.9, C.white, '', { shadow: true });
    box(s, x + 0.2, 1.7, 0.6, 0.6, i === 2 ? C.wcs : C.steel, '', {});
    s.addText(h, { x: x + 0.2, y: 2.45, w: 2.3, h: 0.4, fontFace: BODY, fontSize: 15, bold: true, color: C.ink, isTextBox: true, margin: 0 });
    s.addText(t, { x: x + 0.2, y: 2.9, w: 2.3, h: 1.4, fontFace: BODY, fontSize: 11.5, color: C.grey, isTextBox: true, margin: 0 });
  });
  body(s, 'Editor and client talk only to the engine; the engine talks to the warehouse and to the machines. Nobody goes around anybody.', 0.7, 4.65, 8.6, 0.5, { size: 13, italic: true, color: C.grey });
  note(s, 'Tomorrow: the Editor with the real drawing, the client with an extraction, the engine log read aloud.');
}

// ---------- 10. When something goes wrong ----------
{
  const s = pres.addSlide(); bg(s, C.light);
  title(s, 'When something goes wrong, the line does not stop');
  const cases = [
    ['The warehouse does not answer', 'The objects waiting for a cell stay where they are, in a “declared wait”. The WCS logs it, retries, and when the warehouse comes back it resumes on its own. Tested: 40 seconds without the WMS, zero objects lost.'],
    ['A machine does not answer', 'That person is “off line”: whoever had to hand it an object keeps it, the route manager looks for another way, the rest of the line goes on.'],
    ['An object without data', 'The WCS asks the machine to read it; if there is none, the object goes to a rejection point, and the people at work see it in the client.'],
  ];
  cases.forEach(([h, t], i) => {
    const y = 1.45 + i * 1.25;
    s.addShape(pres.ShapeType.ellipse, { x: 0.7, y: y + 0.1, w: 0.45, h: 0.45, fill: { color: C.amber }, line: { color: C.amber, width: 0 } });
    s.addText(h, { x: 1.35, y, w: 7.9, h: 0.35, fontFace: BODY, fontSize: 15, bold: true, color: C.ink, isTextBox: true, margin: 0 });
    s.addText(t, { x: 1.35, y: y + 0.38, w: 7.9, h: 0.8, fontFace: BODY, fontSize: 12, color: C.grey, isTextBox: true, margin: 0 });
  });
  note(s, 'The principle: a known failure, not an unknown one. Every wait has a name and a line in the log.');
}

// ---------- 11. The roadmap ----------
{
  const s = pres.addSlide(); bg(s, C.light);
  title(s, 'Where the product is going');
  const steps = [
    ['1', 'The boundary', 'The WCS talks to the warehouse only through a declared contract, over HTTP.', 'done'],
    ['2', 'The repository', 'The WCS has its own repository, with all its history; the WMS consumes it.', 'done'],
    ['3', 'Without SQL', 'The engine’s last direct reads on the warehouse database cross the boundary.', 'specified'],
    ['4', 'The integrator’s entities', 'A new person in the line, with its own logic, is written against the signature of the base device: the engine does not change.', 'declared'],
    ['5', 'The web Editor', 'The plant is drawn in the browser, with a viewer in Blazor.', 'declared'],
    ['6', 'Packages', 'The pieces shared with the WMS become NuGet packages.', 'declared'],
  ];
  const stateFill = { done: C.wcs, specified: C.amber, declared: C.mist };
  const stateColor = { done: C.white, specified: C.ink, declared: C.ink };
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
  body(s, 'The first two are done; the third starts after this presentation. The fourth is the protocol’s twin: just as any machine can be connected, the WCS will be able to learn any way of reasoning.', 0.7, 4.8, 8.6, 0.6, { size: 11.5, italic: true, color: C.grey });
  note(s, 'The declared roadmap. Milestone 4 is explained with the metaphor: today the kinds of person are seven, fixed in the engine; tomorrow the integrator writes a new one that respects the base device signature, and the Editor shows it in the palette.');
}

// ---------- 12. Closing ----------
{
  const s = pres.addSlide(); bg(s, C.ink);
  s.addText('In one sentence', { x: 0.7, y: 0.8, w: 8.6, h: 0.6, fontFace: HEAD, fontSize: 30, bold: true, color: C.white, isTextBox: true, margin: 0 });
  s.addText('The WCS is the one who coordinates a line of machines: it talks to each one the same way, waits for the answers, decides the next step, and asks for the route from the one who knows the map.', { x: 0.7, y: 1.5, w: 8.6, h: 1.4, fontFace: BODY, fontSize: 20, color: C.mist, isTextBox: true, margin: 0 });
  const pts = [['Map, not program', 'one more machine is one more node'], ['Dialogue, not control', 'five requests, six answers, any hardware'], ['Facts, not orders', 'the warehouse decides, the WCS reports']];
  pts.forEach(([h, t], i) => {
    const x = 0.7 + i * 2.95;
    box(s, x, 3.2, 2.7, 1.5, C.steelDark, '', {});
    s.addText(h, { x: x + 0.2, y: 3.35, w: 2.3, h: 0.45, fontFace: BODY, fontSize: 14, bold: true, color: C.amber, isTextBox: true, margin: 0 });
    s.addText(t, { x: x + 0.2, y: 3.85, w: 2.3, h: 0.7, fontFace: BODY, fontSize: 12, color: C.mist, isTextBox: true, margin: 0 });
  });
  s.addText('Next steps: the engine without SQL Server, the integrator’s entities, the Editor in the browser.', { x: 0.7, y: 4.95, w: 8.6, h: 0.4, fontFace: BODY, fontSize: 12, italic: true, color: C.grey, isTextBox: true, margin: 0 });
  note(s, 'Closing: the sentence, the three principles, and the pointer to the roadmap on the previous slide.');
}

pres.writeFile({ fileName: 'The-WCS-How-a-Line-of-Machines-Thinks.pptx' }).then(f => console.log('written', f));
