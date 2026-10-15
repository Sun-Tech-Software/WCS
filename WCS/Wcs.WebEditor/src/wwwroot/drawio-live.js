// The live status of the plant, drawn OVER the canvas and never INTO the drawing.
//
// Everything here is a shape of mxGraph's view, in the view's overlay pane: the load unit on board of
// a device, its status dot and the motion and route arrows. None of it is a cell of the model, so the
// save, draw.io's undo and the page's reading of the drawing never see any of it - which is the one
// rule this module exists to keep (spec 2026-09-24, decision 1). Queue item 21 took the last cell
// overlays (mxCellOverlay) away: an overlay lives ON the cell, mxCell.clone() copies it to a Ctrl+D
// copy, and mxCellRenderer.redrawCellOverlays turns its centre by mod(rotation, 90) only - a device
// at rotation=-180 (the real plant has them) had its marks drawn at the wrong corner. A shape of our
// own in the overlay pane has none of those problems: it is keyed by the cell id, placed where the
// cell's shape is painted at every redraw - the preview of a drag or a resize included (queue item
// 22) - and turned by the cell's true rotation.
import { liveTarget } from './drawio-bridge.js';

// The status dot: a fixed size on screen at any zoom (queue item 20), inside the device's top right
// corner (queue item 21, owner's decision 4). The inset keeps it off the device's own outline.
const statusSize = 14;
const statusInset = 1;
const statusStroke = '#1f2937';
// The load unit: a box of drawing units centred in the device, as the WinForms' LU node was (queue
// item 21, owner's decision 1). Half see-through, so that the device and its label stay readable
// under it.
const loadFill = '#b45309';
const loadFillOpacity = 55;
const loadStroke = '#1f2937';
const motionColor = '#ff0000';
const nextColor = '#ffd700';
const finalColor = '#ff8c00';
const hideDelay = 300;

let graph = null;          // the graph our objects live on
let win = null;            // its window: mxGraph's classes are the iframe's
let listeners = null;      // what adopt() installed, to take it off again
const marksByCell = new Map();     // cellId -> { device, status, load }: the frame's device and its two shapes
const motionByCell = new Map();    // from cellId -> { shape, to }
let motions = [];
let routeShapes = [];
let route = null;          // { from, next, final }
let hoverEnabled = false;
let host = null;
let hoveredCell = null;
let hideTimer = null;
// True from the moment the mouse leaves hoveredCell (an empty spot of canvas, or the container
// itself) until the grace timer either fires or the SAME device is entered again. A route that
// arrives from the page while this is true is for a device the operator has already left, and
// showRoute refuses it (fix round 1, finding 3) - hoveredCell alone cannot tell the difference,
// because it is not cleared until the grace timer actually fires.
let leaving = false;
let redrawQueued = false;

export function render(frameJson) {
  const target = liveTarget();
  if (!target) return 'no-graph';
  try {
    if (graph !== target.graph) adopt(target);
    const frame = JSON.parse(frameJson);
    drawDevices(frame.devices || []);
    motions = frame.motions || [];
    drawArrows();
    return 'drawn';
  } catch (err) {
    console.error('drawio-live: the live status could not be drawn', err);
    return 'failed';
  }
}

export function setHover(enabled, dotnet) {
  try {
    hoverEnabled = !!enabled;
    host = dotnet || host;
    if (!hoverEnabled) {
      clearTimeout(hideTimer);
      hideTimer = null;
      leaving = false;
      hoveredCell = null;
      route = null;
      drawArrows();
    }
  } catch (err) {
    console.error('drawio-live: the hover could not be toggled', err);
  }
}

// The route arrives from the page after two reads on the host: it is drawn only if the mouse is
// still on the device it was asked for. The page has its own version guard; this is the last one.
//
// Both sides of the identity check are strings (fix round 1, finding 5): hoveredCell.id is always
// one already, but fromCellId's type is only ever a promise of the caller's, not of this module's,
// and a bare "!==" would silently start comparing a string to whatever that caller actually sent.
export function showRoute(fromCellId, nextCellId, finalCellId) {
  try {
    if (!hoverEnabled || leaving || !hoveredCell || String(hoveredCell.id) !== String(fromCellId)) return;
    route = { from: fromCellId, next: nextCellId, final: finalCellId };
    drawArrows();
  } catch (err) {
    console.error('drawio-live: the route could not be drawn', err);
  }
}

// The drawing is about to be replaced (the page seeds it again): the marks were for cells that are
// going away, and the map would say they are still there.
export function forget() {
  try {
    // The hover state goes with it (fix round 1, finding 2): a pending grace timer belongs to a
    // device that is about to stop existing, and letting it fire later would call back on a cell
    // nobody remembers any more.
    clearTimeout(hideTimer);
    hideTimer = null;
    leaving = false;
    hoveredCell = null;
    route = null;
    destroyAllMarks('before forgetting them');
  } catch (err) {
    console.error('drawio-live: the live status could not be forgotten', err);
  }
}

export function clear() {
  try {
    forget();
    // Hover MODE itself goes with it (fix round 1, finding 2), not only the state forget() already
    // clears: without this, hoverEnabled stayed true and host kept pointing at whatever .NET
    // reference was last given - possibly one already disposed - and the very next mouse move
    // after clear() would call back on it.
    hoverEnabled = false;
    host = null;
    motions = [];
    destroyRoute();
    try {
      for (const entry of motionByCell.values()) {
        try { entry.shape.destroy(); } catch (err) {
          console.error('drawio-live: a motion arrow could not be destroyed while clearing', err);
        }
      }
    } finally {
      // Fix round 2, finding 2: cleared and released no matter what a shape's own destroy() did -
      // the module has to actually let go of the graph it was told to leave, not stay attached to
      // it because one shape misbehaved.
      motionByCell.clear();
      release();
    }
  } catch (err) {
    console.error('drawio-live: the live status could not be cleared', err);
  }
}

// Wipes every live object anchored to the graph about to be abandoned (fix round 1, finding 1).
// adopt() cannot tell a first attach from a document that has just replaced the one it was drawing
// on by looking at the target alone, so this runs whenever the graph really is changing. Hover MODE
// (hoverEnabled/host) is left alone on purpose - only the state that points at the now-gone cells
// and shapes of the OLD graph is torn down; a document reload should not have to re-arm hover.
function abandonGraph() {
  clearTimeout(hideTimer);
  hideTimer = null;
  leaving = false;
  hoveredCell = null;
  route = null;
  destroyRoute();
  try {
    // The old graph may already be gone (the iframe reloaded, or draw.io tore the document down):
    // best effort only, and it must not stop the new document from being adopted.
    destroyAllMarks('while abandoning the old graph');
    for (const entry of motionByCell.values()) {
      try { entry.shape.destroy(); } catch (err) {
        console.error('drawio-live: an old motion arrow could not be destroyed', err);
      }
    }
  } finally {
    // Fix round 2, finding 2: the maps are forgotten no matter what happened above. render() calls
    // adopt() -> abandonGraph() again on every retry while graph !== target.graph, so a map left
    // holding a poisoned entry would fail the very same way forever instead of just this once.
    marksByCell.clear();
    motionByCell.clear();
  }
}

function adopt(target) {
  if (graph && graph !== target.graph) abandonGraph();
  release();
  const nextGraph = target.graph;
  const nextWin = target.window;
  // The marks are placed at once, not on the next animation frame like the arrows: the view has
  // already revalidated every state when it fires SCALE/TRANSLATE (mxGraphView.setScale ->
  // viewStateChanged) and when the model fires CHANGE (mxGraph's own listener, added first by its
  // constructor, validates the view before ours runs), so the states read here are the new ones -
  // and a mark a frame behind its device is a mark visibly off it while the operator zooms.
  // Guarded here too: this runs inside draw.io's own model CHANGE event, and a throw out of it would
  // cut off every listener mxEventSource has not called yet.
  const redraw = () => {
    try { placeMarks(); } catch (err) { console.error('drawio-live: the live marks could not be placed', err); }
    queueRedraw();
  };
  const view = nextGraph.getView();
  const model = nextGraph.getModel();
  const mouse = { mouseDown() {}, mouseMove(sender, me) { onMouseMove(me); }, mouseUp(sender, me) { onMouseUp(me); } };
  let restoreTooltip = null;
  let restoreRenderer = null;
  try {
    view.addListener(nextWin.mxEvent.SCALE, redraw);
    view.addListener(nextWin.mxEvent.TRANSLATE, redraw);
    view.addListener(nextWin.mxEvent.SCALE_AND_TRANSLATE, redraw);
    model.addListener(nextWin.mxEvent.CHANGE, redraw);
    restoreTooltip = hookTooltip(nextGraph, nextWin);
    restoreRenderer = hookRenderer(nextGraph);
    nextGraph.addMouseListener(mouse);
    // mxGraph only fires mouseMove while the pointer is inside graph.container (fix round 1,
    // finding 4): leaving the canvas straight off a device, with no gap of empty canvas first,
    // never fires it again, and without this the hover - and the arrows it drew - would never end.
    // The DOM's own mouseleave covers exactly that gap, through the SAME grace timer as an
    // ordinary move onto empty canvas.
    nextGraph.container.addEventListener('mouseleave', onMouseLeaveContainer);
  } catch (err) {
    // Fix round 2, finding 3: undo whatever DID get installed before the throw. Every removal
    // below is a safe no-op for a listener that was never added (mxEventSource.removeListener,
    // mxGraph.removeMouseListener and Element.removeEventListener all just find nothing and do
    // nothing), so it is simpler and just as correct to undo the lot than to track exactly how far
    // the try got. Without this, a retry's adopt() would install every listener a SECOND time on
    // top of what the partial failure left behind: each mouse move would call OnLiveHover twice,
    // each pan or zoom would redraw twice - forever, since nothing here ever notices the
    // duplication. The tooltip hook is undone too (queue item 21), and the renderer's (queue item
    // 22): a graph this module did not adopt must not keep a wrapper of this module's on it.
    view.removeListener(redraw);
    model.removeListener(redraw);
    if (restoreTooltip) restoreTooltip();
    if (restoreRenderer) restoreRenderer();
    nextGraph.removeMouseListener(mouse);
    nextGraph.container.removeEventListener('mouseleave', onMouseLeaveContainer);
    throw err;
  }
  // graph/win/listeners are assigned only now that every listener really is installed (fix round 1,
  // finding 6): a throw above must leave the module exactly as it was, so the next render() still
  // sees graph !== target.graph and retries adopt() from scratch instead of trusting a
  // half-installed graph as the one it is already drawing on.
  graph = nextGraph;
  win = nextWin;
  listeners = { view, model, redraw, mouse, restoreTooltip, restoreRenderer, container: nextGraph.container };
}

// Never throws (queue item 21): it is called from clear()'s finally and at the top of adopt(), and a
// listener, a tooltip or a renderer hook that could not be taken off must not keep the module
// attached to a graph it was told to leave. graph/win/listeners are let go whatever happened.
function release() {
  const restoreTooltip = listeners ? listeners.restoreTooltip : null;
  const restoreRenderer = listeners ? listeners.restoreRenderer : null;
  try {
    if (listeners && graph) {
      listeners.view.removeListener(listeners.redraw);
      listeners.model.removeListener(listeners.redraw);
      graph.removeMouseListener(listeners.mouse);
      listeners.container.removeEventListener('mouseleave', onMouseLeaveContainer);
    }
  } catch (err) {
    console.error('drawio-live: the listeners could not be taken off the graph', err);
  } finally {
    listeners = null;
    graph = null;
    win = null;
    if (restoreTooltip) restoreTooltip();
    if (restoreRenderer) restoreRenderer();
  }
}

// Queue item 21, owner's decisions 3 and 4: the marks never take the mouse (see makeMark), so what
// they have to say goes in the device's own tooltip - "Load unit UC256355 (867294)", "Status: In
// Error" - above whatever draw.io would show for the device anyway. mxGraph asks
// graph.getTooltipForCell(state.cell) for it (mxGraph.prototype.getTooltip, once the overlays and
// the selection handles have had their say; draw.io's Graph.prototype.getTooltipForCell builds the
// cell's own lines). Wrapped on THIS graph instance only - no prototype is touched, so draw.io's
// other graphs (the sidebar's, the outline's) are left alone - and put back by release().
//
// The wrapper adds something only while its graph is the one this module draws on: if something
// else wrapped getTooltipForCell after us, putting ours back would cut that wrapper out, so in that
// case the restore leaves ours in place and it simply stops adding anything. Never throws: a
// failure of ours leaves draw.io's own tooltip as it was.
function hookTooltip(target, targetWin) {
  const hadOwn = Object.prototype.hasOwnProperty.call(target, 'getTooltipForCell');
  const original = target.getTooltipForCell;
  const wrapped = function (cell) {
    const base = original.apply(this, arguments);
    let ours = null;
    try {
      ours = graph === target ? liveTooltipOf(cell, targetWin) : null;
    } catch (err) {
      console.error('drawio-live: the live status could not be put in the tooltip', err);
    }
    if (!ours) return base;
    return base ? ours + '<br>' + base : ours;
  };
  target.getTooltipForCell = wrapped;
  return () => {
    try {
      if (target.getTooltipForCell !== wrapped) return;
      if (hadOwn) target.getTooltipForCell = original;
      else delete target.getTooltipForCell;
    } catch (err) {
      console.error('drawio-live: the tooltip could not be put back', err);
    }
  };
}

// The live lines for the device a cell belongs to (the cell itself, or its nearest ancestor this
// module has marks for), HTML-escaped: mxTooltipHandler.show puts the string in with innerHTML.
function liveTooltipOf(cell, targetWin) {
  const model = graph.getModel();
  for (let c = cell; c; c = model.getParent(c)) {
    const entry = marksByCell.get(String(c.id));
    if (!entry) continue;
    const d = entry.device;
    const lines = [];
    if (d.color) lines.push(`Status: ${d.statusName}`);
    if (d.loadUnit) lines.push(`Load unit ${d.loadUnit}`);
    return lines.length ? lines.map((line) => targetWin.mxUtils.htmlEntities(line)).join('<br>') : null;
  }
  return null;
}

// Queue item 22: the marks follow a device while mxGraph paints a PREVIEW of it. A drag
// (mxGraphHandler.updateLivePreview) and a resize (mxVertexHandler.updateLivePreview - draw.io turns
// livePreview on) both move the cell's view state to the preview, have the cell renderer paint the
// cell there and put the state back; the model changes only on the drop. No CHANGE, SCALE or
// TRANSLATE is fired in between, so marks placed on those stayed at the old place until the drop
// (measured: 188 px behind a drag; 31 px off centre and 53 px too narrow in a resize). The cell
// overlays of before queue item 21 followed because mxCellRenderer.redraw repaints them from the
// state it is painting (redrawCellOverlays); our shapes follow the same way, placed right after
// every paint of their device's shape. A drag given up with Esc comes through here too:
// mxGraphHandler.reset repaints the cells from their states (resetLivePreview, then
// view.validate), and mxVertexHandler's escape handler repaints its cell itself (both measured,
// DrawioLiveOverlayTest).
//
// EVERY paint by the renderer, not the previews alone: draw.io's animated layouts
// (EditorUi.executeLayout, mxMorphing through mxCellStatePreview.revalidateState) and
// mxEffects.animateChanges paint the devices on their way, and the marks now travel with them where
// they used to jump to the end on the CHANGE - which is what "part of the device" means. The one
// paint that does not come through here is draw.io's wipe and pop of a cell
// (Graph.createVertexWipeAnimation, createVertexPopAnimation), which set the shape's bounds and call
// shape.redraw() straight: the marks stay put through those and are placed again by the CHANGE.
//
// Wrapped on THIS graph's renderer only - every mxGraph has its own, so the outline's and the
// sidebar's are left alone - under the rules of hookTooltip: it adds something only while its graph
// is the one this module draws on, release() puts the original back unless something wrapped it
// after us, and a failure of ours never reaches mxGraph's paint. draw.io swaps renderer.redraw
// itself for the length of a print (mxPrintPreview.addGraphFragment) and of a file patch
// (DrawioFile.patch), and puts back what it found - ours.
function hookRenderer(target) {
  const renderer = target.cellRenderer;
  const hadOwn = Object.prototype.hasOwnProperty.call(renderer, 'redraw');
  const original = renderer.redraw;
  const wrapped = function (state, force, rendering) {
    const result = original.apply(this, arguments);
    try {
      // mxCellRenderer.redraw's own test: rendering=false updates the shape's bounds without
      // painting it.
      if (graph === target && (rendering == null || rendering)) followPaint(state);
    } catch (err) {
      console.error('drawio-live: the live marks could not follow their device', err);
    }
    return result;
  };
  renderer.redraw = wrapped;
  return () => {
    try {
      if (renderer.redraw !== wrapped) return;
      if (hadOwn) renderer.redraw = original;
      else delete renderer.redraw;
    } catch (err) {
      console.error('drawio-live: the cell renderer could not be put back', err);
    }
  };
}

// The marks of a device whose shape has just been painted, placed from that paint - only a paint
// onto the pane the marks are in. A print (mxPrintPreview, draw.io's Ctrl+P) and mxGraph's export of
// the view (mxUtils.getViewXml) paint temporary states of the same cells, at a scale and a place of
// their own, and those must not move the marks on the canvas (measured, DrawioLiveOverlayTest). The
// pane is what tells them apart: both give the view panes of their own for the length of the paint
// (addGraphFragment through view.createSvg, getViewXml by hand). Nothing about the state does -
// mxTemporaryCellStates swaps the view's whole dictionary of states (view.setStates), so for the
// length of the paint the temporary state IS the one the view holds for the cell, and its cell is
// the model's.
//
// Queue item 32: the arrows that start or end on that device follow the same paints, on the next
// animation frame like every other redraw of theirs (queueRedraw). drawArrows reads where the device
// is painted (centreOf), so in the middle of a drag they start from the preview, and the repaint of
// an Esc or the CHANGE of a drop puts them back where the model says. The same pane test as the
// marks', on the pane the arrows are in.
function followPaint(state) {
  if (!state || !state.cell || !state.shape) return;
  const cellId = String(state.cell.id);
  const pane = state.view.getOverlayPane();
  const entry = marksByCell.get(cellId);
  const mark = entry ? entry.load || entry.status : null;
  if (mark && mark.node && pane === mark.node.parentNode) placeMarksOn(entry, state);
  if (isArrowEnd(cellId) && pane === arrowPane()) queueRedraw();
}

// Whether a motion arrow or the route on hover starts or ends on this cell.
function isArrowEnd(cellId) {
  if (route && [route.from, route.next, route.final].some((id) => id != null && String(id) === cellId)) return true;
  return motions.some((m) => String(m.from) === cellId || String(m.to) === cellId);
}

// The pane the arrows are drawn in, read off an arrow that is there: null when there is none, and
// then there is nothing to follow either.
function arrowPane() {
  for (const entry of motionByCell.values()) {
    if (entry.shape.node && entry.shape.node.parentNode) return entry.shape.node.parentNode;
  }
  for (const shape of routeShapes) {
    if (shape.node && shape.node.parentNode) return shape.node.parentNode;
  }
  return null;
}

function queueRedraw() {
  if (redrawQueued) return;
  redrawQueued = true;
  requestAnimationFrame(() => {
    redrawQueued = false;
    try { drawArrows(); } catch (err) { console.error('drawio-live: the arrows could not be redrawn', err); }
  });
}

// The marks of every device the frame speaks of: made, recoloured or taken away, then placed. Keyed
// by the frame's cell id and by nothing the cell carries, so a copy of a device (Ctrl+D, a
// Ctrl+drag) is a cell this module has no entry for and shows nothing, and a cell replaced under the
// same id is found again by placeMarks at the next redraw.
function drawDevices(devices) {
  const wanted = new Map(devices.map((d) => [String(d.cellId), d]));
  for (const [cellId, entry] of marksByCell) {
    if (!wanted.has(cellId)) {
      destroyMarks(entry);
      marksByCell.delete(cellId);
    }
  }
  for (const [cellId, d] of wanted) {
    let entry = marksByCell.get(cellId);
    if (!entry) {
      entry = { device: d, status: null, load: null };
      marksByCell.set(cellId, entry);
    }
    entry.device = d;
    if (d.color) {
      if (!entry.status) {
        entry.status = makeMark(new win.mxEllipse(new win.mxRectangle(0, 0, statusSize, statusSize), d.color, statusStroke, 1.5), 'status', cellId);
      }
      entry.status.fill = d.color;
    } else if (entry.status) {
      destroyShape(entry.status);
      entry.status = null;
    }
    const sized = Number(d.loadUnitWidth) > 0 && Number(d.loadUnitHeight) > 0;
    if (d.loadUnit && !sized) {
      // The page always sends a size (LiveFrameJson, from LoadUnitSize's rule): a frame without one
      // is a broken contract, said here rather than covered with a size this module would invent.
      console.error(`drawio-live: the load unit of cell ${cellId} came without a size, and is not drawn`, d);
    }
    if (d.loadUnit && sized) {
      if (!entry.load) {
        const load = makeMark(new win.mxRectangleShape(new win.mxRectangle(0, 0, 1, 1), loadFill, loadStroke, 1.5), 'load', cellId);
        load.fillOpacity = loadFillOpacity;
        // At the bottom of the pane: the status dots and the arrows are drawn over it, never under.
        const pane = load.node.parentNode;
        pane.insertBefore(load.node, pane.firstChild);
        entry.load = load;
      }
    } else if (entry.load) {
      destroyShape(entry.load);
      entry.load = null;
    }
  }
  placeMarks();
}

// One of our shapes in the overlay pane. The mouse goes through it (owner's decision 2): mxShape's
// own pointerEvents=false makes mxSvgCanvas2D put pointer-events="none" on every element it paints,
// and the group gets it too - so a click, a drag, a right-click or a hover on a loaded device reaches
// the device's own shape underneath, exactly as on an empty one. The two data attributes are the
// module's own mark on its shapes, for whoever has to tell them from mxGraph's.
function makeMark(shape, kind, cellId) {
  shape.dialect = graph.dialect;
  shape.pointerEvents = false;
  shape.visible = false;
  shape.init(graph.getView().getOverlayPane());
  shape.node.style.pointerEvents = 'none';
  shape.node.setAttribute('data-wcs-live', kind);
  shape.node.setAttribute('data-wcs-cell', cellId);
  return shape;
}

// Where every mark goes, from where its cell's shape is painted NOW (paintedBox: the shape's last
// painted box, the view state's only for a shape that has none - the two differ by the whole drag in
// the middle of one, queue item 22): the cell and its state are looked up by id at every call, so
// the marks follow a move, a resize, a zoom, an undo and a cell replaced under the same id alike. A
// cell with no state (not in the drawing, hidden, on a layer turned off) has its marks hidden, not
// destroyed: the frame still speaks of it, and it may come back.
function placeMarks() {
  if (!graph) return;
  const view = graph.getView();
  const model = graph.getModel();
  for (const [cellId, entry] of marksByCell) {
    try {
      const cell = model.getCell(cellId);
      placeMarksOn(entry, cell ? view.getState(cell) : null);
    } catch (err) {
      console.error(`drawio-live: the marks of cell ${cellId} could not be placed`, err);
    }
  }
}

// One device's marks, on the box its shape is painted in (see paintedBox) and turned by its style.
function placeMarksOn(entry, state) {
  const box = state ? paintedBox(state) : null;
  if (!box || !(box.width > 0) || !(box.height > 0)) {
    hideShape(entry.load);
    hideShape(entry.status);
    return;
  }
  // The style's own rotation, whole: mxGraph turns the device's shape by exactly this
  // (mxShape.updateTransform), and so do we - not by the mod(rotation, 90) its overlays get. A drag
  // or a resize leaves the style alone, and draw.io's rotation handle turns only its own outline
  // until the drop.
  const rotation = Number(win.mxUtils.getValue(state.style, win.mxConstants.STYLE_ROTATION, 0)) || 0;
  const cx = box.x + box.width / 2;
  const cy = box.y + box.height / 2;
  if (entry.load) placeLoad(entry.load, entry.device, box, state.view.scale, rotation, cx, cy);
  if (entry.status) placeStatus(entry.status, box, rotation, cx, cy);
}

// Queue item 22: the box the device's shape was last PAINTED in - mxCellRenderer.redrawShape sets
// shape.bounds from the state at every paint - which is the state's own box except in the middle of
// a drag or a resize, when the shape is at the preview and the state back at the old place. Read
// here and not only in followPaint, because the marks are placed from other events too: a live
// frame that arrives mid-gesture (every live event of a running plant sends one, OnLiveFrameReady)
// would otherwise put them back at the old place until the next move of the mouse. The state's box
// only while the shape has none (resetLivePreview clears it just before the repaint).
function paintedBox(state) {
  const bounds = state.shape ? state.shape.bounds : null;
  return bounds || state;
}

// Queue item 21, owner's decision 1: a box of the device's load unit size in DRAWING units (so
// times the view's scale on screen), centred in the device and turned with it around that same
// centre. A device smaller than the box gets it scaled down uniformly until it fits inside the
// device's own unrotated box - the WinForms node could not stick out of its device either, being
// part of it. box.width/height are the device's box already on screen, as painted (queue item 22).
function placeLoad(shape, device, box, scale, rotation, cx, cy) {
  const width = Number(device.loadUnitWidth) * scale;
  const height = Number(device.loadUnitHeight) * scale;
  const fit = Math.min(1, box.width / width, box.height / height);
  showShape(shape, new win.mxRectangle(cx - width * fit / 2, cy - height * fit / 2, width * fit, height * fit), rotation);
}

// Queue item 21, owner's decision 4: the dot keeps its size on screen and sits inside the device's
// top right corner - worked out in the device's own unturned frame (right of the centre, above it),
// then turned by the device's rotation around its centre. A device too small on screen for the dot
// gets it centred on that side instead of sticking out (at DEMOPLANT's opening scale of 0.25 a
// 60-unit side is 15 px, one more than the dot).
function placeStatus(shape, box, rotation, cx, cy) {
  const half = statusSize / 2;
  const dx = Math.max(0, box.width / 2 - statusInset - half);
  const dy = -Math.max(0, box.height / 2 - statusInset - half);
  const angle = rotation * Math.PI / 180;
  const x = cx + dx * Math.cos(angle) - dy * Math.sin(angle);
  const y = cy + dx * Math.sin(angle) + dy * Math.cos(angle);
  showShape(shape, new win.mxRectangle(x - half, y - half, statusSize, statusSize), rotation);
}

function showShape(shape, bounds, rotation) {
  shape.bounds = bounds;
  shape.rotation = rotation;
  shape.visible = true;
  shape.redraw();
}

function hideShape(shape) {
  if (!shape) return;
  shape.visible = false;
  shape.redraw();
}

// A mark whose own destroy() fails still leaves the canvas: its node is taken out of the pane by
// hand, so that nothing of a device the frame no longer speaks of stays on screen.
function destroyShape(shape) {
  try { shape.destroy(); } catch (err) {
    console.error('drawio-live: a live mark could not be destroyed', err);
    try {
      if (shape.node && shape.node.parentNode) shape.node.parentNode.removeChild(shape.node);
    } catch (inner) {
      console.error('drawio-live: a live mark could not be taken off the canvas either', inner);
    }
  }
}

function destroyMarks(entry) {
  const { status, load } = entry;
  entry.status = null;
  entry.load = null;
  if (status) destroyShape(status);
  if (load) destroyShape(load);
}

// Every mark goes, each shape guarded on its own and the map emptied regardless (fix round 2,
// finding 2, the same rule as destroyRoute): one shape whose destroy() throws must neither leave the
// others on the canvas nor leave the map believing a dead shape is still there.
function destroyAllMarks(when) {
  try {
    for (const entry of marksByCell.values()) {
      try { destroyMarks(entry); } catch (err) {
        console.error(`drawio-live: the live marks could not be taken off ${when}`, err);
      }
    }
  } finally {
    marksByCell.clear();
  }
}

// Queue item 32: the centre of the box the cell's shape was last painted in (paintedBox), not the
// view state's - the two differ by the whole drag in the middle of one, and the arrows follow the
// preview as the marks do (owner's decision of 1/10). A rotation leaves the centre where it is.
function centreOf(cellId) {
  if (!cellId) return null;
  const cell = graph.getModel().getCell(cellId);
  const state = cell && graph.getView().getState(cell);
  if (!state) return null;
  const box = paintedBox(state);
  return new win.mxPoint(box.x + box.width / 2, box.y + box.height / 2);
}

function arrow(points, color, width) {
  const shape = new win.mxConnector(points, color, width);
  shape.dialect = graph.dialect;
  shape.style = { endArrow: 'block', endSize: 6, endFill: 1 };
  shape.isDashed = true;
  shape.pointerEvents = false;
  shape.init(graph.getView().getOverlayPane());
  shape.redraw();
  return shape;
}

function drawArrows() {
  if (!graph) return;
  // One arrow per device that runs a step, reused from step to step (the WinForms measured
  // 100-190 ms per step to create and destroy them; here it is only tidier): re-pointed when the
  // destination changes, hidden when the step ends.
  const wanted = new Map(motions.map((m) => [m.from, m.to]));
  for (const [from, entry] of motionByCell) {
    if (!wanted.has(from)) entry.shape.node.style.visibility = 'hidden';
  }
  for (const [from, to] of wanted) {
    const a = centreOf(from);
    const b = centreOf(to);
    let entry = motionByCell.get(from);
    if (!a || !b) {
      if (entry) entry.shape.node.style.visibility = 'hidden';
      continue;
    }
    if (!entry) {
      entry = { shape: arrow([a, b], motionColor, 3), to };
      motionByCell.set(from, entry);
    } else {
      entry.shape.points = [a, b];
      entry.to = to;
      entry.shape.redraw();
    }
    entry.shape.node.style.visibility = 'visible';
  }

  destroyRoute();
  if (route) {
    const from = centreOf(route.from);
    const final = centreOf(route.final);
    const next = centreOf(route.next);
    // The orange first and the yellow after: the last one added is in front, as in the WinForms.
    if (from && final) routeShapes.push(arrow([from, final], finalColor, 4));
    if (from && next) routeShapes.push(arrow([from, next], nextColor, 4));
  }
}

function destroyRoute() {
  // Each shape guarded on its own, and the array always emptied regardless (fix round 2, finding
  // 2): a shape whose own destroy() throws must not leave the rest of routeShapes undestroyed, and
  // must not leave routeShapes believing a dead shape is still there - drawArrows() calls this on
  // every redraw, so a stuck entry would be retried, and fail the same way, forever.
  for (const shape of routeShapes) {
    try { shape.destroy(); } catch (err) {
      console.error('drawio-live: a route arrow could not be destroyed', err);
    }
  }
  routeShapes = [];
}

function deviceCellOf(cell) {
  const model = graph.getModel();
  for (let c = cell; c; c = model.getParent(c)) {
    const value = c.value;
    if (value && value.getAttribute && value.getAttribute('wcs') === 'device') return c;
  }
  return null;
}

// Starts (or restarts) the grace window in which the operator's mouse may come back to the same
// device before the hover really ends. Shared by onMouseMove's "no cell under the pointer" branch
// and onMouseLeaveContainer, which is the same event with a different DOM source (fix round 1,
// finding 4).
function startLeaving() {
  if (!hoverEnabled || !host || !hoveredCell || draggingTheHovered()) return;
  clearTimeout(hideTimer);
  leaving = true;
  hideTimer = setTimeout(() => {
    hideTimer = null;
    hoveredCell = null;
    leaving = false;
    route = null;
    drawArrows();
    // A disposed .NET reference here is not a failure of this module (fix round 1, finding 2): the
    // page - or its whole circuit - may have gone away while the grace window was running, and
    // there is nobody left to tell.
    host.invokeMethodAsync('OnLiveHover', null).catch(() => {});
  }, hideDelay);
}

// Queue item 32 (owner's answer of 9/10): while mxGraph drags the hovered device the hover stays
// alive, and the route follows the preview for the whole gesture. mxGraph takes the mouse off the
// shape it drags (updateLivePreview sets pointerEvents to false), so from the first move there is
// either nothing under the pointer or whatever the device is dragged over: neither is the operator
// leaving the device, and neither may end or move the hover. The drop or the Esc that ends the drag
// gives the decision back to the next move.
function draggingTheHovered() {
  const handler = graph ? graph.graphHandler : null;
  if (!hoveredCell || !handler || handler.first == null || !handler.cells) return false;
  const model = graph.getModel();
  return handler.cells.some((cell) => model.isAncestor(cell, hoveredCell));
}

function onMouseMove(me) {
  if (!hoverEnabled || !host || draggingTheHovered()) return;
  const cell = deviceCellOf(me.getCell());
  if (cell && cell === hoveredCell) {
    clearTimeout(hideTimer);
    hideTimer = null;
    leaving = false;
    return;
  }
  if (!cell) {
    // The WinForms waited too: going from a device onto its load-unit symbol, or across a gap, the
    // arrows must not blink out exactly where the mouse tends to stop. (Here the load unit never
    // takes the mouse - see makeMark - so only the gap is left, but the wait is the same.)
    if (!hoveredCell) return;
    startLeaving();
    return;
  }
  clearTimeout(hideTimer);
  hideTimer = null;
  leaving = false;
  hoveredCell = cell;
  route = null;
  drawArrows();
  // Same reasoning as the grace timer's own call: a disposed .NET reference here is the page going
  // away mid-gesture, not a defect of this module (fix round 1, finding 2).
  host.invokeMethodAsync('OnLiveHover', String(cell.id)).catch(() => {});
}

// Queue item 32: the container's mouseleave of a drag that took the hovered device off the canvas
// found the hover kept alive (draggingTheHovered), and no other mouseleave comes after it. The
// button let go out there - mxGraph tells its listeners through the document - is the leaving that
// was put off; let go on the canvas, the next move decides as always.
function onMouseUp(me) {
  if (!hoverEnabled || !host || !hoveredCell || draggingTheHovered()) return;
  const evt = me.getEvent();
  const box = graph.container.getBoundingClientRect();
  const x = win.mxEvent.getClientX(evt);
  const y = win.mxEvent.getClientY(evt);
  if (x < box.left || x >= box.right || y < box.top || y >= box.bottom) startLeaving();
}

// mxGraph's own mouse handling never sees a move once the pointer has left graph.container (fix
// round 1, finding 4): without this, leaving the canvas straight off a device - no empty canvas in
// between - would leave hoveredCell set and the arrows on screen forever.
function onMouseLeaveContainer() {
  startLeaving();
}
