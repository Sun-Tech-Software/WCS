// The bridge between Blazor and draw.io's iframe. Embedding protocol: JSON messages over
// postMessage. The events this bridge listens to are five: 'configure' (draw.io asks for its
// configuration and does NOT start until it has it), 'init' (draw.io is ready to receive), 'load'
// (draw.io has put on the canvas the document load() sent it), 'save' (the operator saved) and
// 'export' (we asked for the drawing ourselves).
let listener = null;

// La configurazione che draw.io aspetta, promessa PRIMA di essere nota. E' una promessa e non un
// valore perche' le due cose arrivano in ordine libero: la sezione dei dispositivi si genera dal
// registro che il motore dichiara, cioe' da una lettura HTTP che puo' finire dopo che draw.io ha
// gia' chiesto. draw.io, dal canto suo, aspetta la risposta all'infinito - il suo ascoltatore resta
// registrato finche' non arriva - quindi rispondere tardi va bene, e rispondere MAI no.
let resolveConfig = null;
let configReady = null;

// The selection, which the embedding protocol does NOT carry: not one of the nineteen JSON events
// draw.io posts to its parent says what is selected. The way round is not a trick - the iframe is
// served by our own host, so it is same origin, and draw.io hands its own EditorUi to whoever asks
// through the entry point it opens for plugins: window.Draw.loadPlugin(fn) calls fn(ui) at once if
// the application is already up, otherwise as soon as it is. Measured in a real browser, see
// DrawioSelectionReachTest.
//
// What goes up to the page is the cells' own IDENTIFIERS and nothing else: for a device drawn by
// DrawioDocument the cell's id IS the plant item's guid (it is written as the <object id>), and the
// page is the only side that knows the plant, so it is the page - not this file - that says whether
// an identifier is a device and what it is called. A name read off the cell here would be the label
// draw.io draws, which whoever is drawing can edit.
//
// The five variables are the whole state of the subscription, and detach() puts all five back the
// way they were before the first attach.
let selectionWindow = null;   // the iframe window the subscription lives in: a reload does NOT bring a new one
let selectionDocument = null; // the iframe document the subscription lives in: a reload DOES - see watchSelection
let selectionModel = null;    // the mxGraphSelectionModel we subscribed to, kept in order to undo it
let selectionListener = null; // our listener on it, kept for the same reason
let selectionTimer = null;    // the guard against a loadPlugin that never calls back
// The graph itself, kept for renameCell (batch C) - the only place this bridge reaches INTO
// draw.io's model and changes something there, and only after the plant has accepted the new name.
// Not the only thing the page changes inside draw.io: the drawing goes in whole through load(),
// which hands a document to draw.io by its own message and never touches the graph. It is taken
// from the same ui the subscription comes from and dropped with it, because a graph of a window
// that has reloaded is a graph nobody is drawing on any more.
let selectionGraph = null;
// draw.io's own EditorUi, for the drawing as the save would read it: see currentDrawing() below.
let drawioUi = null;
// The subscription to the ADDITIONS, which is a different object from the selection's: the graph's
// own model, not its selection model. Same window, same loadPlugin callback, same detach - see
// watchAdditions for what it reports and, above all, for what it deliberately does NOT decide.
let additionsModel = null;
let additionsListener = null;
// draw.io's own undo stack - the one Ctrl+Z walks - taken from the same ui and dropped with it.
// Read ONLY, and only by theLabelLandsOnItsOwnGesture: nothing here ever pushes, pops or reorders
// it. A draw.io that did not hand one over costs the operator a second press and breaks nothing.
let undoHistory = null;
// Which undoable edit each just-born cell came in with, remembered at the moment the birth is
// reported to the page and read again when the label for that cell comes back from the plant. See
// theLabelLandsOnItsOwnGesture for what the two are compared for.
const birthEditOfCell = new Map();
// How many births are remembered at a time. The page asks for a label for every cell it manages to
// create, and each ask forgets its own entry, so in an Editor that keeps up this map holds one
// gesture's worth. What it guards against is the cells that never get a label - a creation the host
// refused, a shape the operator drew themselves - which would otherwise pile up for as long as the
// page is open. The oldest go first, and losing one only costs that label a second press.
const birthsRemembered = 200;

// THE MARK "THE DRAWING HAS CHANGED SINCE IT WAS LAST ON THE PLANT" (row 46 of the parity
// inventory), and the one thing it is for: the browser's own "Leave site?" when the tab is closed or
// reloaded with work that would be lost. It is NOT the engine switch's check on unsaved work
// (DrawioUnsavedWork), which reads the drawing itself and stays what it is.
//
// It is two counters and not a boolean because a save is a ROUND and the operator can go on drawing
// while it runs: the save writes the drawing as draw.io handed it over, and a change made after that
// is not on the plant however well the round ends. So every model edit counts one, the count is
// taken when the drawing is handed over, and a round that wrote everything moves the "on the plant"
// counter up to that count - never to the count of the moment the answer arrives.
//
// In doubt it stays on - a question too many is bearable, lost work is not:
//   - an undo back to the saved state still counts as a change, and asks;
//   - a draw.io whose model this bridge could not subscribe to asks at every close
//     (changesUnknowable), because then nothing here can tell.
let changesSeen = 0;          // model edits since attach that were not the page's own echo
let changesOnThePlant = 0;    // how many of them the plant is known to hold
let changesUnknowable = false;
let changesModel = null;      // the mxGraphModel the mark listens to, kept in order to undo it
let changesListener = null;
// Up while renameCell writes a name the plant ALREADY has, or removeDeletedDeviceCells takes off a
// device it no longer has: an echo of the plant, not work of the operator's - it must not light the mark.
let echoingThePlant = false;
// draw.io's own question, taken over while this bridge is attached: see takeOverTheLeavingQuestion.
let questionUi = null;
let questionHadItsOwn = false;
let questionAsDrawioWroteIt = null;
// The page window's beforeunload handler, installed at attach and taken off at detach.
let leavingGuard = null;
// Which attachment the counters above belong to. attach and detach put the counters back to zero,
// and a save answer still in flight from before would otherwise apply ITS count to the new ones -
// "on the plant" moved up to a number that means nothing any more, and unsaved work taken for
// saved. Every save captures the generation it was handed over in, and its answer is dropped when
// the generation has moved on.
let countersGeneration = 0;

// QUEUE ITEM 13: THE DRAWING DOES NOT TRAVEL TO THE PAGE IN ONE MESSAGE. The page's circuit refuses a
// message from the browser over its ceiling, and the drawing of a real plant is far over it (measured:
// 418 KB for 152 devices, against SignalR's 32 KB by default) - the circuit closed and the save round
// never ended. So a save hands the page only a NUMBER, the drawing stays here under it, and the page
// asks for it back (handedOverDrawing) as bytes, which Blazor streams across the circuit in pieces.
// The same for currentDrawing, the engine switch's read of the drawing. Held only while the page's
// answer is pending: taken out when it comes, when the page lets go of it (letGoOfSaves), and all of
// them at detach.
let drawingsHandedOver = 0;
const handedOverDrawings = new Map();
const drawingEncoder = new TextEncoder();

// The bytes Blazor streams: a Uint8Array, never null - a null answer to a stream is refused by
// Blazor itself before the page sees it. An absent drawing is therefore NO bytes, which the page
// reads as absent.
function drawingBytes(xml) {
  return drawingEncoder.encode(typeof xml === 'string' ? xml : '');
}

// QUEUE ITEM 15: THE TWO MESSAGES THAT GROW WITH A GESTURE HAVE A CEILING OF THEIR OWN. The drawing
// is streamed (item 13), but the cells a gesture adds (OnDrawioCellsAdded) and the cells selected
// (OnDrawioSelectionChanged) still cross in one message each, and a message over the circuit's
// ceiling (WebEditorExtensions.CircuitMessageCeiling, 320 KB) closes the connection: the call is
// lost and the circuit comes back without it. Measured on 446 cells: 66,331 and 17,461 bytes; a
// paste of 14,000 small shapes and the selection of them crossed together in 1,968,421 bytes
// (DrawioTooManyCellsTest). Over this ceiling the bridge sends the page a short warning with the
// number of cells instead - "too many cells to follow (N)" - and the page says it.
// 300 KB and not 320: what is measured here is the arguments' JSON, and the message around them
// (the method's name, the call's number, the reference to the page) adds bytes of its own - the
// 20 KB left are room for them, not a measured need.
const largestGestureMessage = 300 * 1024;
const gestureEncoder = new TextEncoder();

// Calls the page with method(payload) when the message fits under largestGestureMessage, otherwise
// with warning(count). Measured as Blazor serializes the call - JSON.stringify of the arguments,
// whose UTF-8 is what goes on the wire - and not estimated from the count: a cell's description
// grows with its id and its numbers.
function tellOrWarn(dotnet, method, payload, warning, count) {
  const bytes = gestureEncoder.encode(JSON.stringify([payload])).length;
  if (bytes <= largestGestureMessage) return dotnet.invokeMethodAsync(method, payload);
  console.warn(`drawio-bridge: too many cells to follow (${count}): ${method} would carry ${bytes} bytes, ` +
    `over the ceiling of ${largestGestureMessage}`);
  return dotnet.invokeMethodAsync(warning, count);
}

function holdsUnsavedWork() {
  return changesUnknowable || changesSeen !== changesOnThePlant;
}

// The two attribute names a dropped cell carries. They are LITERALS here and constants over there
// (DrawioLibrary.TypeAttribute, and the wcs= mark DrawioDocument and DrawioLibrary both write):
// this file is JavaScript running inside draw.io and cannot see the C# side. Renaming one without
// the other would stop every drop from becoming a device, and no unit test could see it - the C#
// ones never run this file. What sees it is the browser test that really drags an entry and reads
// the device back off the host (DrawioShapesPanelTest): that one goes red, which is why it exists.
const markAttribute = 'wcs';
const typeAttribute = 'wcsType';

// Row 37's downstream guard (fix round 2, widened fix round 3). The guard's own state, mirroring
// questionUi/questionHadItsOwn/questionAsDrawioWroteIt above exactly - same save-and-restore shape,
// same reason (on the INSTANCE, restored at the three real lifecycle points - see
// teardownTheDefencesAgainstHiding, fix round 3).
let visibilityGraph = null;
let visibilityHadItsOwn = false;
let visibilityAsDrawioWroteIt = null;

// FIX ROUND 3: the set of cell ids that have at least one MARKED DESCENDANT anywhere under them -
// not only immediate children, the whole subtree - recomputed on every model edit. See
// guardMarkedCellsVisible for why a set and not a single literal (row 37's own default layer used to
// be the one hardcoded case; it is now just the commonest member of this set, not a case of its own).
let visibilityAncestorIds = new Set();
let visibilityModel = null;
let visibilityChangeListener = null;

// The second belt's own save-and-restore state (fix round 3: it used to have none, and was never
// put back - see restoreCustomActionLinks).
let customLinkGraph = null;
let customLinkHadItsOwn = false;
let customLinkAsDrawioWroteIt = null;

// ROW 33 OF THE PARITY INVENTORY (rulers). Two things that do NOT belong together in one function:
// the browser's OWN memory of the switch (loadRulerPreference/saveRulerPreference, this page's own
// localStorage key - nothing to do with draw.io) and the one call that actually moves the rulers on
// draw.io's canvas (setRulerVisible, which needs drawioUi and so lives here rather than in a module
// of its own). DrawioEditor.razor is what ties the two together: it reads the memory as soon as this
// bridge exists, applies it once draw.io answers 'init' (OnDrawioReady), and on every click of the
// switch calls both - independently, so a canvas that could not be reached still gets remembered for
// next time.
//
// MEASURED in app.min.js: EditorUi.prototype.setRulerVisible calls mxSettings.setRulerOn(c) and
// mxSettings.save(), which is draw.io's OWN shared setting (the one every diagram this browser opens
// would read, same family as the tabContainer trap keepOnePageOnly's own comment documents) - unlike
// setTabContainerVisible, setRulerVisible takes no second argument to skip that save. It is not
// worked around here: this bundle is hosted for this one Editor alone, nothing else in this browser
// profile ever opens it, and OUR OWN memory below is what DrawioEditor.razor actually reads back -
// draw.io's shared copy is never consulted. isRulerVisible() is asked first so that a click matching
// what is already on screen costs no needless scroll adjustment (setRulerVisible moves the diagram
// container's scroll to keep the same point under the ruler's corner, MEASURED in the same source).
const rulerStorageKey = 'wcs.webeditor.drawio-rulers';

/**
 * Row 33: the switch's remembered value, or false when there is none - a browser never opened before,
 * a private window, or one whose site data is blocked (localStorage access THROWS there, it does not
 * come back empty). False is the parity inventory's own default, so "could not read" and "never
 * saved" answer the same way.
 */
export function loadRulerPreference() {
  try {
    return window.localStorage.getItem(rulerStorageKey) === '1';
  } catch (err) {
    console.error('drawio-bridge: the rulers\' memory could not be read', err);
    return false;
  }
}

/** Row 33: remembers the switch's value for next time. Nothing breaks if it cannot: only the memory is lost, not the session in front of the operator. */
export function saveRulerPreference(enabled) {
  try {
    window.localStorage.setItem(rulerStorageKey, enabled ? '1' : '0');
  } catch (err) {
    console.error('drawio-bridge: the rulers\' memory could not be saved', err);
  }
}

/**
 * Row 33: turns draw.io's own rulers on or off. False when there is no draw.io to ask (the bridge
 * never attached, or the subscription that hands over drawioUi failed) - the page has nothing more
 * to do about it, the switch still shows what the operator asked for.
 */
export function setRulerVisible(enabled) {
  if (!drawioUi || typeof drawioUi.setRulerVisible !== 'function') return false;
  try {
    if (typeof drawioUi.isRulerVisible === 'function' && drawioUi.isRulerVisible() === !!enabled) return true;
    drawioUi.setRulerVisible(!!enabled);
    return true;
  } catch (err) {
    console.error('drawio-bridge: the rulers could not be toggled', err);
    return false;
  }
}

// ROW 35 OF THE PARITY INVENTORY (pointer position). The element THIS PAGE renders once, with this
// stable id and no content of its own - see DrawioEditor.razor - so that writing into it here never
// collides with a Blazor render: nothing in the page's markup binds that element's text, so Blazor
// never touches it again once it is on the DOM.
const pointerElementId = 'drawio-pointer-position';

// WHETHER THE MODEL'S UNITS ARE THE PLANT'S OWN, verified in DrawioDocument.Build (C#) rather than
// assumed here: every device's box is NodePlacement.OwnBox(item.X, item.Y, item.Width, item.Height,
// rotation) - the device's OWN (unrotated) box, centred on NodePlacement.WorldCenter, which is
// (item.X + item.Width/2, item.Y + item.Height/2) computed directly from the plant's own numbers,
// with no scale and no offset applied anywhere in Build. The layer the geometry hangs from
// (DrawioDocument.DefaultLayerId, mxCell id="1") carries no geometry of its own either - parent="0",
// no <mxGeometry> - so it applies no translation of its own. A device's rotation moves its BOX around
// that same centre, but the centre itself - and so the coordinate FRAME the box lives in - never
// moves: a model point and the plant point at the same place are the same two numbers, no conversion
// needed. What DOES need converting is screen pixels to that model frame, which is the one thing
// draw.io itself knows (its own scale and scroll/pan) and this bridge does not try to recompute by
// reading the DOM.
//
// FIX ROUND 1: NOT graph.getPointForEvent, and the reason is measured rather than a style choice.
// graph.getPointForEvent(evt,false) - what the first draft of this file called - snaps its answer to
// the grid whenever the graph has one (mxGraph.prototype.gridEnabled=true by default, and this
// document's own grid is on: DrawioDocument.Build writes grid="1" gridSize="10", MEASURED in
// app.min.js's own reader for that attribute). Up to five units off the real pointer for that reason
// alone (half of gridSize 10) - the WinForms Editor this page is closing parity with shows the EXACT
// position, not one rounded to the nearest ten. So the conversion is redone here without the snap,
// using the SAME two calls getPointForEvent itself is built from (MEASURED, same source): the raw
// client point is put in the container's own frame with mxUtils.convertPoint(graph.container,
// clientX, clientY) - which is what every one of draw.io's own handles and guides is drawn against -
// and THAT point, not draw.io's rounded one, is divided by graph.view.scale and has
// graph.view.translate subtracted. See watchPointer below for where this runs.
let pointerGraph = null;         // the graph the mouse listener below is installed on
let pointerMouseListener = null; // the { mouseDown, mouseMove, mouseUp } object addMouseListener was given
let pointerLeaveListener = null; // the container's own 'mouseleave', for the same reason drawio-live.js needs one (mxGraph only fires mouseMove while the pointer is inside the container)
let pointerRafScheduled = false;
let pointerPendingLine = '';

function N1(v) {
  return (Math.round(v * 10) / 10).toFixed(1);
}

function pointerElement() {
  try {
    return document.getElementById(pointerElementId);
  } catch (err) {
    // document itself is not expected to throw, but this runs on every animation frame while the
    // mouse moves: a page in the middle of tearing down must not turn that into an unhandled error.
    return null;
  }
}

function flushPointer() {
  pointerRafScheduled = false;
  const el = pointerElement();
  if (el) el.textContent = pointerPendingLine;
}

// Throttled to one DOM write per animation frame (spec: "no round trip on every mouse move" - this
// is the JS-side half of that rule, the browser's own paint cadence instead of one write per event).
function schedulePointer(line) {
  pointerPendingLine = line;
  if (pointerRafScheduled) return;
  pointerRafScheduled = true;
  requestAnimationFrame(flushPointer);
}

function watchPointer(ui, w) {
  try {
    const graph = ui.editor.graph;
    const mouse = {
      mouseDown() {},
      mouseMove(sender, me) {
        try {
          // Fix round 1: the RAW model point, deliberately not graph.getPointForEvent - see the
          // block comment above pointerGraph for why (that function snaps to the grid, and this
          // indicator must not).
          const evt = me.getEvent();
          const raw = w.mxUtils.convertPoint(graph.container, w.mxEvent.getClientX(evt), w.mxEvent.getClientY(evt));
          const scale = graph.view.scale;
          const translate = graph.view.translate;
          const x = raw.x / scale - translate.x;
          const y = raw.y / scale - translate.y;
          schedulePointer(`X: ${N1(x)}  Y: ${N1(y)}`);
        } catch (err) {
          console.error('drawio-bridge: the pointer position could not be read', err);
        }
      },
      mouseUp() {},
    };
    const onLeave = () => schedulePointer('');
    graph.addMouseListener(mouse);
    graph.container.addEventListener('mouseleave', onLeave);
    pointerGraph = graph;
    pointerMouseListener = mouse;
    pointerLeaveListener = onLeave;
    // Empty until the first real move: a stale reading from a previous drawing must not sit there
    // looking current.
    schedulePointer('');
  } catch (err) {
    console.error('drawio-bridge: the pointer position could not be tracked', err);
  }
}

function unwatchPointer() {
  try {
    if (pointerGraph && pointerMouseListener) pointerGraph.removeMouseListener(pointerMouseListener);
    if (pointerGraph && pointerLeaveListener) pointerGraph.container.removeEventListener('mouseleave', pointerLeaveListener);
  } catch (err) {
    console.error('drawio-bridge: the pointer subscription could not be taken off', err);
  }
  pointerGraph = null;
  pointerMouseListener = null;
  pointerLeaveListener = null;
  schedulePointer('');
}

// How long the callback of Draw.loadPlugin is waited for before the page is told that the selection
// is not readable. Measured: at the 'init' event the application is already up and the callback runs
// at once (it had already run at 1043 ms, against 1066 ms for 'init'), so this ceiling is not a
// delay anybody waits - it is the point beyond which a draw.io that stopped calling back is SAID
// instead of leaving the page mutely without a selection forever.
const selectionHookCeiling = 10000;

// What draw.io says to whoever reaches a road to a second page anyway. It travels as an Error
// message because that is the only thing draw.io's own callers of these functions do with a
// failure: they hand it to ui.handleError, which puts it in front of the operator.
const onePageOnlyMessage =
  'This Editor draws the plant on a single page: another page cannot be added, and the one that is '
  + 'here cannot be deleted. Nothing drawn outside this page would ever reach the plant.';

// Un guasto dentro il gestore dei messaggi non resta dentro la promessa. Da quando il gestore e'
// async, cio' che solleva li' dentro - per esempio un iframe.contentWindow che non c'e' piu' -
// diventa una promessa rifiutata che NESSUNO guarda: la pagina continua come se niente fosse e
// draw.io resta fermo senza che una riga lo dica. Qui lo si scrive nella console e lo si rilancia
// fuori dalla promessa, dove il browser lo mostra come un errore vero (ed e' dove le prove nel
// browser lo possono leggere).
function report(err) {
  console.error('drawio-bridge: guasto nel gestore dei messaggi di draw.io', err);
  setTimeout(() => { throw err; });
}

// The document a window holds right now, or null when there is no window or it cannot be read. It
// is read OUTSIDE watchSelection's try, where a throw would take 'init' down with it: the iframe is
// served by our own host so it never should, and a null here only means "subscribe again".
function documentOf(w) {
  try {
    return w ? w.document : null;
  } catch {
    return null;
  }
}

// Subscribes to draw.io's selection and tells the page, every time it changes, WHICH cells are
// selected. Called from the 'init' event.
//
// Why at 'init' and not earlier: window.Draw is there much sooner (266 ms, against 1066 ms for
// 'init') and asking that early works - the callback is queued and runs by itself - but it takes
// polling, because there is no event for "the iframe's window now has Draw". At 'init' the
// application is up by definition, one message we already listen to, no polling and no second guess.
//
// Why once per DOCUMENT and not once per seeding: the subscription survives a 'load' - after a
// reseeding graph is the same object - and subscribing again would report every change twice. An
// iframe that reloads, on the other hand, has a new draw.io in it, and that really is a new
// subscription: hence the comparison on the DOCUMENT rather than a plain boolean. Not on the window:
// MEASURED on 2026-09-24 (DrawioSameFrameReloadTest), iframe.contentWindow is the very same
// WindowProxy object before and after a reload of the same <iframe> element - draw.io builds a new
// EditorUi and a new graph behind it, and a comparison on the window took the new draw.io's 'init'
// for the old one's and stayed on a torn-down graph for good. The document is what a navigation
// renews, whatever the element.
function watchSelection(iframe, dotnet) {
  const w = iframe.contentWindow;
  const doc = documentOf(w);
  if (doc && selectionWindow === w && selectionDocument === doc) return;
  unwatchSelection();
  // The mark's subscription too: it lives on the model of the draw.io that is going away.
  unwatchChanges();
  // And the guard/second belt, on the same draw.io that is going away - a real lifecycle point,
  // fix round 3's own three (see teardownTheDefencesAgainstHiding).
  teardownTheDefencesAgainstHiding();
  // And queue item 8's hold on draw.io's windows, at the same three points and for the same reason.
  unkeepTheSaveButtonClear();

  // Nothing here throws towards the message handler: a draw.io that stopped opening the plugin
  // entry point must not take 'init' - and with it the whole seeding of the plant - down with it.
  // It is declared to the page instead, which is the one difference from the other failures of this
  // bridge: without the selection the Editor still draws and still saves, only the panel next to it
  // stays empty, so the page says THAT rather than covering the drawing with an error.
  try {
    if (!w || !w.Draw || typeof w.Draw.loadPlugin !== 'function' || !w.mxEvent) {
      console.error('drawio-bridge: draw.io does not expose window.Draw.loadPlugin: neither the selection nor the additions can be read');
      changesUnknowable = true;
      dotnet.invokeMethodAsync('OnDrawioSelectionUnavailable');
      dotnet.invokeMethodAsync('OnDrawioCreationUnavailable');
      return;
    }

    selectionWindow = w;
    selectionDocument = doc;
    selectionTimer = setTimeout(() => {
      selectionTimer = null;
      selectionWindow = null;
      selectionDocument = null;
      console.error('drawio-bridge: Draw.loadPlugin never called back: neither the selection nor the additions can be read');
      changesUnknowable = true;
      dotnet.invokeMethodAsync('OnDrawioSelectionUnavailable');
      dotnet.invokeMethodAsync('OnDrawioCreationUnavailable');
    }, selectionHookCeiling);

    w.Draw.loadPlugin((ui) => {
      // Detached - or the iframe reloaded - while this callback sat in draw.io's queue: the
      // subscription would install itself in a window nobody is listening to any more, and nothing
      // would ever take it off again. The document too, for the same reason as the guard above.
      if (selectionWindow !== w || selectionDocument !== doc) return;
      clearTimeout(selectionTimer);
      selectionTimer = null;

      // FIRST of the three, and in a try of its own inside itself: it is the only one of them that
      // guards against a LOSS, and it must not depend on the selection - whose failure returns from
      // this callback before the additions are even reached. It is also installed before the plant
      // is sent (the page seeds the drawing only after OnDrawioReady, which is said below), so
      // there is no window in which the operator has a drawing and no guard.
      keepOnePageOnly(ui);

      // ALONGSIDE it, at the same point and for the same two reasons (owner's decision of
      // 2026-09-25, parity inventory row 37): it guards against the same KIND of loss - draw.io's own
      // Layers window can switch the default layer off, taking every device and every junction off
      // the canvas for the rest of the session with nothing said about it - and it must not depend
      // on the selection either. See closeTheLayersWindow for the routes measured in the bundle and
      // why the fix is a no-op rather than a throw. (Fix round 3: the window's own lock icon is a
      // different feature - it stops editing, not rendering - and does not belong in this sentence.)
      closeTheLayersWindow(ui);

      // AND the Tags window, closed the same day for the same reason (a marked cell whose every tag
      // is hidden through it is not rendered either - see closeTheTagsWindow's own comment).
      closeTheTagsWindow(ui);

      // Queue item 8: draw.io's own windows kept off its Save button. Here, beside the two windows
      // above and before the selection, because it must not depend on the selection either - a page
      // whose selection could not be read still saves, and needs the button. Its own try is inside.
      keepTheSaveButtonClear(ui, w);

      // AND THE GUARD ITSELF (fix round 2), downstream of both windows and of whatever route comes
      // next: see guardMarkedCellsVisible for what it does and why closing windows one at a time
      // stopped being enough. Installed here, at the same early point, for the same reason - before
      // the plant is sent, so there is no window in which a device could be drawn without it.
      guardMarkedCellsVisible(ui, w);

      // The second belt on the one route found to reach the guard's same two levers by hand (Edit
      // Link): see ignoreCustomActionLinks.
      ignoreCustomActionLinks(ui);

      // SECOND, and for the same two reasons: it too guards against a loss - the close of a tab
      // with work in it - and it must not depend on the selection. Its own try is inside it.
      watchChanges(ui, w);

      try {
        const graph = ui.editor.graph;
        // Queue item 15: a selection too large for one message of the circuit is said as its count.
        const tell = () => {
          const ids = graph.getSelectionCells().map((cell) => (cell.id == null ? '' : String(cell.id)));
          return tellOrWarn(dotnet, 'OnDrawioSelectionChanged', ids, 'OnDrawioTooManyCellsSelected', ids.length);
        };
        selectionModel = graph.getSelectionModel();
        selectionListener = tell;
        selectionGraph = graph;
        drawioUi = ui;
        // The undo stack, from the same ui: see theLabelLandsOnItsOwnGesture. Taken defensively -
        // a draw.io without an undoManager would make every creation's label cost two presses, and
        // that is a cost, not a failure, so it must not take the selection down with it.
        undoHistory = (ui.editor && ui.editor.undoManager) || null;
        selectionModel.addListener(w.mxEvent.CHANGE, selectionListener);
        // Said once right away, and READ rather than assumed empty: it is what turns "the page has
        // never been told" into "the page knows, and this is what is selected". Without it the page
        // could not tell a selection that is empty from a subscription that never happened, and it
        // would have to choose between keeping quiet and claiming something nobody measured.
        tell();
      } catch (err) {
        // Back to the state before the attempt, listener included: addListener can throw AFTER the
        // two variables have been set, and leaving them behind would mean a subscription believed
        // to exist that nobody would ever take off.
        unwatchSelection();
        console.error('drawio-bridge: draw.io did not hand over a graph to subscribe to', err);
        dotnet.invokeMethodAsync('OnDrawioSelectionUnavailable');
        dotnet.invokeMethodAsync('OnDrawioCreationUnavailable');
        return;
      }

      // The additions, in a try of their OWN and after the selection: the two subscriptions fail
      // separately and cost different things, so one must not take the other down. A page with the
      // selection and without the additions still draws, still saves and still shows the properties
      // panel - only a dropped shape stays a drawing - and the page says exactly that.
      watchAdditions(w, dotnet);

      // Row 35's pointer position, for the same reason it sits beside the additions rather than
      // inside their own try: it is a THIRD, independent thing that can fail on its own (its own try
      // is inside watchPointer) without taking either of the two above down with it - the indicator
      // going blank costs nobody the drawing or the properties panel.
      watchPointer(ui, w);

      // Row 63's entries in the right-click menu, a FOURTH independent thing (its own try is inside
      // hookThePopupMenu): a menu without them is draw.io's own, and costs nothing else. After the
      // selection, because every entry works through it - the panel is about the selected device.
      hookThePopupMenu(ui, dotnet);

      // Row 8, the owner's decision of 27/9: no cut of a device. A FIFTH independent thing, with its
      // own try inside guardTheCut.
      guardTheCut(ui, dotnet);
    });
  } catch (err) {
    unwatchSelection();
    // Nothing is watching the drawing's changes unless the callback got as far as that: then the
    // close has to ask, because nothing here can tell whether there is work to lose.
    if (!changesModel) changesUnknowable = true;
    console.error('drawio-bridge: the subscription to draw.io\'s selection could not be installed', err);
    dotnet.invokeMethodAsync('OnDrawioSelectionUnavailable');
    dotnet.invokeMethodAsync('OnDrawioCreationUnavailable');
  }
}

// Takes away every road to a second page (owner's decision of 2026-09-17), because a second page is
// a LOSS and not a preference: the drawing that comes back then carries two <diagram>, the plant is
// read off the first one, and the decoration is rewritten from what was read - so everything the
// operator drew on the second page is thrown away at the next opening, and nothing says a word.
//
// MEASURED in a real browser on 2026-09-17, on this very bundle, before any of this was written:
//   - the tab bar is there and visible (ui.tabContainer, display flex, 32 px high, seven children,
//     the first with title "Insert Page"), and a REAL click on that "+" takes ui.pages from 1 to 2;
//   - ui.actions carries insertPage, removePage, renamePage, duplicatePage, sortPages, pageTabs,
//     previousPage, nextPage; the insertPage action, the duplicatePage action, ui.insertPage() and
//     ui.duplicatePage() each added one, and from five pages ui.removePage() took one away;
//   - the address parameters are not a lever: urlParams.pages is undefined and so is
//     Editor.pagesVisible - the first decides how a file is WRITTEN, the second does not exist here;
//   - ui.editor.setGraphXml with an <mxfile> carrying two <diagram> does NOT add pages, so Extras >
//     Edit Diagram - whose Apply ends there - is not a road either.
//
// ui.insertPage IS NOT THE ONE CHOKE POINT, and the comment that used to say so here - "every one
// of them ends at ui.insertPage, so replacing that one function closes the roads we found AND the
// ones we did not" - is what made the road below invisible for a whole batch. It was an exclusive
// claim nobody had measured, and it was false about the very bundle we host.
//
// WHERE A PAGE IS REALLY BORN, and counting "new ChangePage(" does not answer it. The seven sites
// in the bundle we host, with the offsets they were read at on 2026-09-18: FOUR of them
// (8477565, 8477842, 8478020, 8478129) are inside EditorUi.replaceFileData, which begins at
// 8476806; one is in EditorUi.importXml, which makes a page per <diagram> past the first and one
// more when its addNewPage argument is true; one is in EditorUi.insertPage; and one is in
// EditorUi.removePage, which takes a page AWAY. The line that used to stand here gave those first
// four to EditorUi.setFileData and called it "the load, which is how our own document arrives":
// that was wrong, and EditorUi.setFileData begins at 8490377, past every one of them.
//
// It was wrong about the mechanism as well, and that is the part worth keeping: EditorUi.setFileData
// makes its pages with no ChangePage at all. It empties this.pages and fills it again by hand -
// "this.pages=[];for(var t=0;t<c.length;t++){ ... this.pages.push(f)" over the <diagram> of the
// document it is given (8490377). So a search for ChangePage cannot enumerate the ways a page is
// born, which is why setFileData is wrapped below as a road of its own and not left to follow from
// somebody else's.
//
// WHAT REACHES importXml, and this is a list of EXAMPLES rather than a set: Ctrl+V
// (EditorUi.pasteXml), a file dropped on the canvas (EditorUi.importFile, under the
// graph.container "drop" listener), EditorUi.insertTextAt, EditorUi.pasteCells,
// EditorUi.replaceMermaidCell, the image dialog's filePicked and App.openTemplateDialog. Not one of
// them goes anywhere near insertPage. What is MEASURED about all of them at once, instead of
// listed: every call of importXml in the bundle resolves on an instance - fourteen "this.importXml("
// and two "b.importXml(", sixteen in all - and the bundle carries no importXml.call and no
// importXml.apply anywhere. That, and not the list, is why a wrapper put on the instance covers the
// callers nobody has named yet.
//
// MEASURED on 2026-09-18 in a real browser, with the operator's own gestures, BEFORE this half was
// written (the block of 2026-09-17 was in place the whole time, and stopped none of it):
//   - Ctrl+V of the TEXT of a two-page .drawio file:   pages 1 -> 3, and draw.io moved the canvas
//     to page 2 (current=1). With the tab bar hidden, nothing on screen said where the operator was;
//   - dropping that same file on the canvas:           pages 1 -> 3, current=1;
//   - dropping a ONE-page .drawio file with Ctrl held: pages 1 -> 2, current=1. Nobody had this one
//     on any list: importFile hands mxEvent.isControlDown(evt) to importXml as addNewPage, and with
//     addNewPage true even a single <diagram> becomes a page of its own;
//   - dropping that same one-page file WITHOUT Ctrl:   pages 1 -> 1, and its shape did arrive on the
//     canvas (2 -> 3 cells). That is the gesture that must go on working, and it does;
//   - the functions by hand: ui.importXml(two <diagram>) 1 -> 3, ui.importXml(one <diagram>,
//     addNewPage=true) 1 -> 2, ui.pasteXml(two <diagram>) 1 -> 3. None of the three threw.
//
// AND ONE MORE, measured later on the same day and with the wrapper above already installed: the
// very message this bridge's own load() posts ({action:'load', xml, autosave:0}) carrying a
// document of two <diagram> took pages 1 -> 2. The operator was left on page one - only its label
// came up on the canvas - and no dialog said that a second page had been made. It was never a
// defect: nothing the operator does posts that message, we are its only caller, and the document we
// send carries a single <diagram>. It was a road, open behind a block that claimed to have shut
// them all, and the day a seeded plant carried two <diagram> it would have hidden the second page
// exactly as Ctrl+V did. It is shut below.
//
// WHAT THE IMPORT WRAPPER DOES, and the shape of it is a decision. It hands draw.io the SAME document with
// only the first <diagram> left in it, and forces addNewPage to false: what comes in is then exactly
// what draw.io itself does with a one-page file - its own code, its own branch - and what was left
// out is SAID, in draw.io's own error dialog. Refusing the paste whole was the other option and it
// was not taken: the first page is the part this Editor can actually keep (cells without our mark
// come back as decoration and are written again at the next opening, see DrawioReader), so a refusal
// would throw that away too and leave the operator no way forward from inside the Editor. The
// invariant it buys is the one that matters: through this function the number of pages never grows.
// The ordinary paste of copied cells is not touched by any of it - what Ctrl+C puts on the clipboard
// is an <mxGraphModel> and not an <mxfile>, so firstPageOf hands it back exactly as it came and
// nothing is said to anybody. Measured after the wrapper: copying a device and pasting it still
// makes a second one, and no dialog opens.
//
// It TELLS instead of throwing, and that is the difference from insertPage below: there the gesture
// did nothing at all, here the gesture did something and something else was left out. A throw would
// have taken the whole paste down with it.
//
// It THROWS instead of doing nothing for insertPage, and that is the point there: draw.io's own
// callers of those functions hand a failure to ui.handleError, which puts it in front of the
// operator. A silent no-op would be a "+" that does nothing, which is the same silence in another
// costume.
//
// Deleting the only page is closed by the same move, and it is not a side effect - it is the other
// half of "one page". EditorUi.prototype.removePage, removing the last page, calls this.insertPage()
// to put a fresh one in its place; with insertPage throwing, removePage's own catch takes it to
// handleError and the ChangePage that would have removed the page never runs. The page stays.
//
// WHAT THIS IS NOT: a guarantee, and this batch is the proof of why. Every road listed above was
// measured shut afterwards, and the ordinary gestures measured intact - but the list is a list of
// what somebody thought to try, and the one that was missing was reachable with Ctrl+V in this very
// bundle, in front of everybody. This is our code running inside somebody else's
// application: it can fail to install, and all it does then is write a line in the console; and a
// draw.io upgrade can open a road nobody has measured yet. What guarantees that a second page cannot
// pass in SILENCE is elsewhere and does not depend on any of this: the save counts the <diagram> of
// the drawing that comes back and stops (DrawioContents.Diagrams, and OnDrawioSaved). That defence
// held against the road above too - measured, with three and with five pages, nothing was written.
// This half only takes the doors away.
function keepOnePageOnly(ui) {
  try {
    // THE ROAD Ctrl+V TAKES, and it is first here because it is the commonest gesture there is.
    // Wrapped on the instance, not on the prototype, for the same reason as insertPage below.
    const importXmlAsDrawioWroteIt = ui.importXml;
    if (typeof importXmlAsDrawioWroteIt === 'function') {
      ui.importXml = function (xml, dx, dy, crop, noErrorHandling, addNewPage, applyDefaultStyles) {
        const cut = firstPageOf(ui, xml);
        // addNewPage is forced false and not merely ignored: it is the argument a Ctrl+drop sets,
        // and with it a file of a single page still became a page of its own.
        const cells = importXmlAsDrawioWroteIt.call(
          this, cut.xml, dx, dy, crop, noErrorHandling, false, applyDefaultStyles);
        sayWhatDidNotComeIn(ui, cut.pages, addNewPage);
        return cells;
      };
    }

    // THE ROAD OUR OWN DOCUMENT ARRIVES BY, and the reason it is here is that it was found open
    // AFTER the wrapper above was written and measured. setFileData is what the embedded draw.io
    // calls when the 'load' message this bridge posts reaches it, and it builds this.pages straight
    // from the <diagram> it is handed, so a document of two would have made two pages - measured,
    // see above.
    //
    // It is cut by the same firstPageOf as the import, which is what makes it safe for the
    // seeding: a naked <mxGraphModel> and an <mxfile> of one <diagram> come back untouched and
    // never reach the cutting at all, and those are the only two shapes this Editor ever sends
    // (measured at the save, and the whole browser suite seeds through this function).
    //
    // The second argument is passed on rather than dropped: this bundle's setFileData overwrites it
    // on its first line and never reads it, so it is not ours to decide about.
    const setFileDataAsDrawioWroteIt = ui.setFileData;
    if (typeof setFileDataAsDrawioWroteIt === 'function') {
      ui.setFileData = function (xml, second) {
        const cut = firstPageOf(ui, xml);
        const done = setFileDataAsDrawioWroteIt.call(this, cut.xml, second);
        sayTheDrawingWasCutToItsFirstPage(ui, cut.pages);
        return done;
      };
    }

    // The one function the roads of the INTERFACE end at. Replaced on the instance, not on the
    // prototype: the browser test needs the original to be able to MAKE a second page and prove that
    // the save refuses it, and a plant drawn by a draw.io whose prototype we had rewritten would not
    // be the draw.io the operator uses.
    ui.insertPage = function () { throw new Error(onePageOnlyMessage); };

    // The two entries that offer it. Blocking insertPage alone would already stop them, but an
    // interface that offers a gesture and then shows an error is an interface that lies about what
    // it can do: isEnabled greys them out where a menu still draws them.
    for (const name of ['insertPage', 'duplicatePage']) {
      const action = ui.actions ? ui.actions.get(name) : null;
      if (!action) continue;
      action.funct = function () { throw new Error(onePageOnlyMessage); };
      action.isEnabled = function () { return false; };
    }

    // And the bar itself, with its "+" and its per-tab context menu - through draw.io's own switch
    // for it, which is what its View > Page Tabs entry flips (the "pageTabs" action calls exactly
    // this). Writing tabContainer.style.display by hand was the first attempt and it does NOT hold:
    // measured, the bar was back to "flex" and 32 pixels high by the time the plant was drawn,
    // because EditorUi.updateTabContainer - which draw.io calls again at every page change - puts
    // the display back to "" whenever isTabContainerVisible() says yes. The predicate is pinned
    // shut first, so every later call of draw.io's own updater keeps the bar away instead of
    // undoing us; setTabContainerVisible then applies it once, right now.
    //
    // The second argument is left out on purpose: with it, draw.io would write "pages: false" into
    // the browser's stored draw.io settings, which are shared with every other diagram that browser
    // opens. This Editor's rule is this Editor's, not the browser's.
    if (typeof ui.setTabContainerVisible === 'function') {
      ui.isTabContainerVisible = function () { return false; };
      ui.setTabContainerVisible(false);
    }
  } catch (err) {
    // Not declared to the page, and deliberately: this half only takes the doors away, and the
    // guarantee that a second page cannot pass unseen is the save's, which does not go through
    // here. A page told "the doors are still open" could do nothing with it that the save does not
    // already do.
    console.error('drawio-bridge: the roads to a second page could not be closed', err);
  }
}

// Gives back the document that is about to be imported with ONLY its first <diagram> in it, and how
// many it carried. Nothing to cut - a naked <mxGraphModel>, which is what an ordinary copy-and-paste
// of cells produces, or an <mxfile> with a single <diagram> - comes back untouched, so that gesture
// runs through draw.io's own code exactly as it did before.
//
// It reads the document the way draw.io is about to read it and not in a way of its own: the same
// mxUtils.parseXml and the same Editor.extractGraphModel, called with the same second argument
// importXml uses (null != this.pages). Otherwise a document that draw.io digs an <mxfile> out of -
// it unwraps a compressed diagram, an <svg> with its content attribute, an embedded div - would be
// "not an mxfile" here and a two-page file over there. Editor.extractGraphModel reads and returns,
// it changes nothing of the editor, which is why calling it once more costs nothing.
//
// A failure to read leaves the document ALONE rather than guessing, and says so in the console:
// draw.io is about to fail on the same text, and a document this cannot parse is not one it can
// safely rewrite either.
function firstPageOf(ui, xml) {
  const untouched = { xml: xml, pages: 0 };
  try {
    if (typeof xml !== 'string' || xml.length === 0) return untouched;
    const w = ui.editor.graph.container.ownerDocument.defaultView;
    const parsed = w.mxUtils.parseXml(xml);
    const file = ui.editor.extractGraphModel(parsed.documentElement, ui.pages != null);
    if (file == null || file.nodeName !== 'mxfile') return untouched;

    const diagrams = file.getElementsByTagName('diagram');
    if (diagrams.length <= 1) return { xml: xml, pages: diagrams.length };

    const pages = diagrams.length;
    // Backwards, because getElementsByTagName hands back a LIVE collection: removing from the front
    // would renumber what is left under our feet and leave every other page in.
    //
    // A document of exactly TWO pages cannot tell the two directions apart - forwards, the one
    // removal it does is the right one - so the test that holds this line is the one with THREE
    // <diagram> in it (DrawioOnePageTest.ARealCtrlVOfAThreePageFileBringsOnlyItsFirstPage).
    // Measured on 2026-09-18 with the loop turned forwards: the second removal reads past the end
    // of the collection that has just shrunk, the catch below hands the document back UNCUT, and a
    // real Ctrl+V took the drawing from one page to FOUR. The three-page test failed on exactly
    // that count and the other four tests of its file stayed green - which is what "a suite that
    // cannot tell the right cut from a broken one" looks like from the inside.
    for (let i = pages - 1; i > 0; i--) diagrams[i].parentNode.removeChild(diagrams[i]);
    // The attribute would go on saying "2" over a document that now carries one <diagram>. Nothing
    // in importXml reads it, and it is corrected all the same: a count that lies is exactly what
    // this batch was called in to repair.
    if (file.getAttribute('pages') != null) file.setAttribute('pages', '1');
    return { xml: new w.XMLSerializer().serializeToString(file), pages: pages };
  } catch (err) {
    console.error('drawio-bridge: what was being imported could not be read as a draw.io document', err);
    return untouched;
  }
}

// Says what did not come in. It is the whole point of the wrapper above: taking the pages away and
// keeping quiet about it would be the same silent loss in a smaller costume.
//
// The channel is ui.handleError, draw.io's own error dialog over the canvas, and it is the same one
// the block uses for insertPage. It is not our page's report: this runs inside draw.io, at the
// moment of the gesture, and the page has nothing to do with it - the save's refusal, which is the
// guarantee, has its own words over there.
//
// Nothing to say means nothing shown: an ordinary paste must not open a dialog.
function sayWhatDidNotComeIn(ui, pagesInWhatWasImported, aNewPageWasAsked) {
  try {
    const left = pagesInWhatWasImported > 1 ? pagesInWhatWasImported - 1 : 0;
    if (left === 0 && !aNewPageWasAsked) return;

    let said = 'This Editor draws the plant on a single page. ';
    if (left > 0) {
      said += 'What you brought in carries ' + pagesInWhatWasImported + ' pages: only the first one '
        + 'came in, and ' + (left === 1 ? 'the other one was' : 'the other ' + left + ' were')
        + ' left out - nothing of them is on the canvas and nothing of them was ever on the plant. '
        + 'To bring them in too, open that file in another draw.io and copy the shapes of one page '
        + 'at a time. ';
    }
    if (aNewPageWasAsked) {
      said += 'It was put on THIS page: holding Ctrl while dropping a file asks draw.io for a page of '
        + 'its own, and there is only one here. ';
    }
    said += 'Nothing that was already drawn has been touched.';
    ui.handleError(new Error(said));
  } catch (err) {
    // The import itself went through, and the operator must not lose it because we could not put a
    // dialog on the screen. The console line is what is left, and it is better than a broken paste.
    console.error('drawio-bridge: what was left out of the import could not be shown', err);
  }
}

// Says that a drawing which arrived to be OPENED carried more than one page. A sentence of its own
// rather than the import's, and that is the point: a load REPLACES what is on the canvas, so "nothing
// that was already drawn has been touched" - true of a paste - would be a lie here, and there is no
// "bring the others in by copying them" either, because the operator did not bring anything.
//
// Nothing to say means nothing shown, and that is the ordinary case: every seeding this Editor does
// sends one page.
function sayTheDrawingWasCutToItsFirstPage(ui, pagesInWhatArrived) {
  try {
    if (!(pagesInWhatArrived > 1)) return;
    ui.handleError(new Error(
      'This Editor draws the plant on a single page. The drawing that has just been opened carried '
      + pagesInWhatArrived + ' pages: only the first one is on the canvas. Nothing of the others was '
      + 'drawn, and nothing of them can be saved from here.'));
  } catch (err) {
    // The drawing itself arrived, and it is on the canvas: a dialog that could not be opened must
    // not take it away again.
    console.error('drawio-bridge: what was left out of the drawing that arrived could not be shown', err);
  }
}

// Takes away the road to draw.io's own Layers window (owner's decision of 2026-09-25, parity
// inventory row 37, "Cose che non tornano" voce e), because opening it is how the default layer
// (mxCell id="1", DrawioDocument.DefaultLayerId - the one layer every device and every junction
// hangs from, see the comment on watchAdditions' inModel for the same cell) can be switched off from
// the canvas. A layer turned invisible from the window's own eye icon takes the whole plant off the
// screen for the rest of the session, with NOTHING said about it anywhere on this page - the save
// does not even notice, because DrawioReader never reads a layer's visibility, and a reload puts it
// back only because DrawioDocument.Build rewrites layer "1" from scratch every time. (The window's
// own lock icon is a different feature entirely - fix round 3 - it stops editing a layer's cells,
// not their rendering, and never belonged in this sentence.)
//
// MEASURED in the bundle this Editor hosts (app.min.js), before any of this was written:
//   - Ctrl+Shift+L is bound through the graph's own key handler - its bindAction, not
//     Actions.prototype's (fix round 1: the first version of this comment named the wrong one) -
//     to the "layers" action (bindAction(76,!0,"layers",!0), 76 being 'L'), and that bindAction's
//     own wrapper only calls the action's funct AFTER asking isEnabled() - "H.isEnabled()&&H.funct.
//     apply(this,arguments)" - so disabling the action closes the shortcut without touching the key
//     binding at all;
//   - the SAME action is what a menu entry named "layers" ends at (the "View > Panels" submenu, and
//     the floating menu this theme shows in place of a menu bar both call addMenuItems with
//     "layers" in the list, which reads the one action back off ui.actions - measured, no second
//     "layers" menu item anywhere in the bundle);
//   - ui=min runs the embedded draw.io CHROMELESS: window.uiTheme is set straight from
//     urlParams.ui ("min" here), and the bundle's own bootstrap builds the Editor with
//     "0"==urlParams.chrome||"min"==uiTheme as its chromeless flag - so EditorUi's own menubar
//     (built only when NOT chromeless) is never created at all. One fewer road, not a road of its
//     own, and the same fact DrawioOnePageTest's own comment already measured for insertPage;
//   - searching the bundle for every addAction whose name contains "Layer" turns up exactly one:
//     "layers" itself. There is no standalone "addLayer"/"removeLayer"/"lockLayer" action - the
//     only places a layer is born, renamed, hidden or locked are inside LayersWindow's own DOM (its
//     "+" button, the eye and lock icons on each row, its rows' context menu), which never run
//     while the window is never created;
//   - EditorUi.prototype.showLayersDialog is a SECOND caller of the same action, reached at the tail
//     of every file load and not only from the operator's own gesture: "1<getChildCount(root)&&
//     (null==actions.layersWindow?actions.get('layers').funct():...)" - it opens the window BY
//     ITSELF, without asking isEnabled() first, whenever the document that has just loaded carries
//     more than one layer on its current page. This plant is always seeded with exactly one
//     (DrawioDocument.Build writes a single <mxCell id="1">), but a file pasted or dropped from
//     outside this Editor could carry more - a layer is a document property orthogonal to the
//     one-page rule keepOnePageOnly enforces, so a single <diagram> can still hold two layers;
//   - FIX ROUND 1, THE ONE THAT MATTERS MOST: EditorUi.prototype.init - the SYNCHRONOUS setup that
//     runs while EditorUi is being built, measured well before draw.io's own 'init' message (the
//     event this whole bridge waits for before installing anything) ever fires - calls
//     this.restoreVisibleWindows() itself: "if(Editor.isSettingsEnabled()){var c={layers:"layers",
//     outline:"outline",tags:"tags"};for(f in c){var l=mxSettings.getWindowState(f);null!=l&&
//     l.visible&&(l=this.actions.get(c[f]),null!=l&&l.funct())}}". mxSettings.getWindowState reads
//     mxSettings.settings.windowStates[name], which lives in the SAME shared ".drawio-config"
//     localStorage entry setRulerVisible's own comment already documents (every draw.io tab this
//     browser opens reads and writes it) - and EditorUi.prototype.installWindowPersistence writes
//     visible:true into it the moment the window is shown. So a session that once left the Layers
//     window open - before this fix existed, or from any other tool sharing the origin - reopens it
//     at the START of every later session, calling the ORIGINAL funct BEFORE this bridge's own
//     watchSelection ever gets a chance to disable it. Ctrl+Shift+L, now disabled, could not even
//     close it again. MEASURED reproducing it live: seeding windowStates.layers.visible=true before
//     the page opens left the window visible after the page finished loading, with this bridge's
//     'layers' action already disabled.
//
// THE SHAPE OF THE FIX, and why it is a NO-OP rather than a throw - the one respect in which this
// differs from keepOnePageOnly's insertPage/duplicatePage. Those two THROW, because every caller
// draw.io itself has for them hands the failure to ui.handleError, which puts it in front of the
// operator and leaves the rest of the Editor working. showLayersDialog has no such caller: its own
// call of actions.get("layers").funct() sits among plain statements with no try of its own around
// any of them - "this.showLayersDialog();this.chromelessResize&&this.chromelessResize();this.editor.
// undoManager.clear();this.editor.modified=...;this.updateUi();...;window.focus();...;this.format.
// refresh()" (measured at the tail of the file-open flow; a second, genuinely comma-separated site
// exists too - "this.editor.graph.selectUnlockedLayer(),this.showLayersDialog(),...,this.
// restoreLibraries(),window...focus()" - and neither wraps the call in anything that would stop a
// throw from taking the rest down with it) - so a throw there would take every one of those down
// too, on any file this Editor did not itself seed. funct is turned into a plain no-op instead: the
// window is never created, from ANY caller, and nothing downstream of it ever sees an exception.
// isEnabled is set to false alongside it, for the reason keepOnePageOnly's own comment gives for
// insertPage - an entry that stays lit and does nothing when pressed is a lie about what the
// interface can do - and it is what keeps the keyboard shortcut and an ordinary menu click from even
// reaching the no-op. And where restoreVisibleWindows has ALREADY called the original funct before
// this ever runs - the window already exists and is already showing - the fix hides it: see
// hideIfAlreadyOpenAtStartup below for why hiding (not destroying) is what makes that state stick.
//
// WHAT THIS IS NOT: a guarantee that the default layer can never be hidden by some road nobody has
// measured, for the same reason keepOnePageOnly's own comment gives for its block - this is our code
// running inside somebody else's application, and a draw.io upgrade can open a road nobody has
// measured yet. Unlike the second-page loss, there is no defence downstream of this one either: a
// hidden layer does not survive a reload (DrawioDocument.Build always writes it visible), so nothing
// analogous to the save's own page count is needed or possible here - the whole guarantee is this
// one road being shut. draw.io's Tags window is a DIFFERENT road to the same kind of loss (a tag can
// be marked hidden, and Graph.prototype.isCellVisible was overridden to also ask
// isAllTagsHidden - a cell whose every tag is hidden is not rendered, exactly like a hidden layer):
// closed by closeTheTagsWindow below, same decision, same day.
function closeTheLayersWindow(ui) {
  try {
    const action = ui.actions ? ui.actions.get('layers') : null;
    if (!action) return;
    action.funct = function () {};
    action.isEnabled = function () { return false; };
    // ui.actions.layersWindow, not ui.layersWindow: MEASURED in the bundle - the action's own
    // funct sets "this.layersWindow" where "this" is ui.actions (the callback is built inside
    // Actions' own init, bound to that object), and EditorUi.prototype.getWindows/showLayersDialog
    // both read it back the same way ("this.actions.layersWindow").
    hideIfAlreadyOpenAtStartup(ui.actions && ui.actions.layersWindow);
  } catch (err) {
    // Not declared to the page, for the same reason keepOnePageOnly's own catch is not: this half
    // only takes the road away, and there is nothing downstream of it that depends on it - the
    // plant's own data never carried a visibility flag to begin with (DrawioDocument/DrawioReader),
    // so the drawing and the save are unaffected either way.
    console.error('drawio-bridge: the road to the Layers window could not be closed', err);
  }
}

// Takes away the road to draw.io's own Tags window (owner's decision of 2026-09-25, same day and
// same reasoning as closeTheLayersWindow, taken right after it: a marked cell whose every tag is
// hidden through this window is not rendered either - Graph.prototype.isCellVisible is overridden
// to also ask "!this.isAllTagsHidden(this.getTagsForCell(k))", and Graph.prototype.setHiddenTags/
// toggleHiddenTag are the two functions that change what counts as hidden. Nothing in this plant's
// own data ever carries a tag (DrawioDocument.Build writes none), so this can only ever be tags the
// operator added by hand through this same window - but the window is what lets an accidental click
// take a device off the canvas with no way back, exactly the risk row 37 already names for layers.
//
// MEASURED in the bundle, mirroring the layers search above:
//   - Ctrl+K is bound through the graph's own key handler to the "tags" action
//     (this.keyHandler.bindAction(75,!0,"tags"), 75 being 'K'), gated on
//     "if(!this.editor.chromeless||this.editor.editable)" - chromeless is true under ui=min, so
//     this reduces to this.editor.editable, which this Editor leaves true (the graph is meant to be
//     drawn on); the shortcut is bound and reachable;
//   - the "tags" action is registered the same way as "layers" - d.actions.addAction("tags", ...) -
//     so ui.actions.get('tags') is the one place to disable it from, same as layers;
//   - UNLIKE layers, the window this action opens is NOT kept on ui.actions: the action's callback
//     is bound to the Menus instance (it is added from inside Menus' own init, with "d" standing for
//     the captured ui), so it sets "this.tagsWindow" where "this" is ui.menus - MEASURED against
//     EditorUi.prototype.getWindows, whose own list has "this.actions.layersWindow" alongside
//     "this.menus.tagsWindow" in two DIFFERENT concat calls. Reading ui.actions.tagsWindow here
//     would silently find nothing;
//   - restoreVisibleWindows' own map already lists "tags" beside "layers" and "outline" (the same
//     one quoted in closeTheLayersWindow's comment) - the SAME startup race applies, and the SAME
//     fix (hide it if it is already open) closes it;
//   - the hidden-tags mechanism itself (setHiddenTags/toggleHiddenTag/hiddenTags) is called from
//     inside Graph.prototype.createTagsDialog's own row-building code (the eye icon next to each tag
//     in the window, and its shift-click "show only this one" variant) - MEASURED, closing THIS
//     window closes THAT door - but FIX ROUND 2 found this was not the only one: guardMarkedCellsVisible's
//     own comment has the other, reached through Edit Link's custom actions
//     ({"tags":{"hidden":[...]}}, run through Graph.prototype.executeCustomActions, which calls
//     hiddenTags-setting code that has nothing to do with this window at all). The guard is what
//     closes that one; this window closes only its own front door;
//   - hidden tags are NOT restored from the shared ".drawio-config" the way a window's visible flag
//     is - REVISED, fix round 2: the first version of this comment said they come from a FILE's own
//     saved extras, and that was measured against the wrong function. The real restoring code is
//     EditorUi.prototype.addChromelessToolbarItems: "if(null!=urlParams.tags){...f=JSON.parse(l)
//     [decodeURIComponent of urlParams.tags];...n.hiddenTags=...f[pageId]...}" on the graph's own
//     'fileLoaded' event - a URL PARAMETER named "tags", not a file's own saved data. This Editor's
//     FrameUrl (DrawioEditor.razor) never sets one, so that branch never runs here and a fresh seed
//     always starts with hiddenTags=[] regardless of what an earlier session hid - the conclusion
//     ("nothing to reconcile here, unlike the window's own visibility") still holds, only the
//     mechanism named was wrong.
//
// The Tags window is left OUT OF SCOPE by this same decision for now, on the owner's own word: it
// also lets an operator rename or delete a tag across the whole plant, and whether THAT deserves
// its own treatment is still open. What is closed here is only the ROAD TO HIDING A CELL - the
// funct/isEnabled pair, exactly mirroring closeTheLayersWindow, with nothing else touched.
function closeTheTagsWindow(ui) {
  try {
    const action = ui.actions ? ui.actions.get('tags') : null;
    if (!action) return;
    action.funct = function () {};
    action.isEnabled = function () { return false; };
    hideIfAlreadyOpenAtStartup(ui.menus && ui.menus.tagsWindow);
  } catch (err) {
    console.error('drawio-bridge: the road to the Tags window could not be closed', err);
  }
}

// Shared by closeTheLayersWindow and closeTheTagsWindow: hides a window that
// EditorUi.prototype.restoreVisibleWindows may already have opened, synchronously, during
// EditorUi's own construction - well before this bridge's watchSelection ever gets to install the
// funct/isEnabled patch above. Hiding rather than leaving it: EditorUi.prototype.
// installWindowPersistence binds a listener on the window's own SHOW/HIDE events that calls
// EditorUi.prototype.saveWindowState, which writes {..., visible: f.isVisible()} straight back into
// the SAME shared ".drawio-config" entry restoreVisibleWindows reads from and calls mxSettings.
// save() - so setVisible(false) here does not just hide the window for this session, it corrects
// the shared memory that would otherwise keep reopening it on every later page this browser opens,
// this Editor or any other application sharing the same origin. isVisible() is asked first so a
// window that never opened (the ordinary case) costs nothing.
function hideIfAlreadyOpenAtStartup(win) {
  if (win && win.window && typeof win.window.isVisible === 'function' && win.window.isVisible()) {
    win.window.setVisible(false);
  }
}

// THE PRIMARY DEFENCE (fix round 2, owner's rule restated: nothing in the plant Editor may make a
// device vanish from view; WIDENED fix round 3). Closing routes to hiding a cell one at a time is
// whack-a-mole - after Layers and Tags, a THIRD was found: Edit Link (Alt+Shift+L, or the
// right-click menu's "Edit Link...", reachable under ui=min - EditorUi.prototype.altShiftActions
// binds 76='L' to "editLink", and mxKeyHandler.prototype.getFunction reads that map gated only on
// the graph's own isEnabled(), not on chromeless/editable) lets an operator carry ANY string onto
// ANY cell as its link, including draw.io's own custom-action mini-language: a link of
// "data:action/json,{...}" is read by Graph.prototype.handleCustomLink and run through
// Graph.prototype.executeCustomActions when the link is clicked. MEASURED reaching the SAME two
// levers the two closed windows already close from their own UI: an action of
// {"hide":{"cells":["<id>"]}} calls setCellsVisible (undoable, Ctrl+Z puts it back), and one of
// {"tags":{"hidden":["<tag>"]}} sets Graph.prototype.hiddenTags DIRECTLY - NOT through the Tags
// window at all (so its own Reset, and this bridge's closure of the window itself, are both beside
// the point), and NOT undoable (executeCustomActions is not run inside the model's own update/undo
// scope for a plain property assignment like this one).
//
// SO THE FIX MOVES DOWNSTREAM OF EVERY ROUTE THAT HIDES THROUGH VISIBILITY, closed or not yet found
// (fix round 4: ONE route hides through something else entirely and is not, and must not be, covered
// by this - see the collapse exception below, at the end of THE WIDER RULE). mxGraphView.prototype.
// validateCell is what turns model state into what is actually drawn, and MEASURED in the bundle it
// is built on ONE question asked of the graph, recursively down the parent chain: "b=(null!=b?b:!0)
// &&this.graph.isCellVisible(a);...for(...)this.validateCell(c.getChildAt(a,e),b&&(...))" - a cell's
// own answer is ANDed with what its PARENT already answered, all the way up to the layer. And
// this.graph.isCellVisible is ALREADY where draw.io itself combines the two things this batch has
// found so far - MEASURED, "Graph.prototype.isCellVisible=function(k){return T.apply(this,arguments)
// &&!this.isAllTagsHidden(this.getTagsForCell(k))}" wraps mxGraph.prototype's own "return
// this.model.isVisible(a)".
//
// FIX ROUND 3, THE PART THAT WAS WRONG: overriding isCellVisible for "the default layer and every
// marked cell" (round 2's own words) is NOT enough, and the bug is in the ONE line quoted above -
// "b=(null!=b?b:!0)&&this.graph.isCellVisible(a)" is a JavaScript "&&": when the PARENT's own answer
// (the "b" argument passed in) is already false, the right-hand side is NEVER EVALUATED - isCellVisible
// is not even CALLED for the child. MEASURED LIVE reproducing it: a device grouped with another
// device (Ctrl+G, an UNMARKED cell draw.io itself creates as their new parent) vanished when that
// GROUP was hidden or tagged hidden, because validateCell never got as far as asking the guard about
// the device at all - it had already given up one level higher, on a parent the guard did not know
// to answer for. The same is true of any layer other than the default one, for the same reason: the
// guard's old "the default layer" case was one hardcoded id, not a rule.
//
// THE WIDER RULE: isCellVisible must answer true not only for a marked cell, but for every cell that
// HAS a marked descendant anywhere under it - the layer, a group, a group inside a group, whatever
// draw.io lets an operator nest a device inside. visibilityAncestorIds is that set, walked UP from
// every marked cell to the root and kept current on every model edit (see recomputeMarkedAncestors) -
// NOT walked down from each cell on every isCellVisible call, which validateCell makes for every
// cell on every repaint: an O(descendants) check there would run at repaint frequency on whatever is
// on screen, while a full recompute of the set runs once per COMMITTED edit and touches every cell
// once regardless of nesting. ESTIMATED cheap enough to say so, not MEASURED with a real timer: one
// enumeration of a plain JS object (model.cells) plus, per marked cell, a walk of a handful of parent
// pointers - no DOM access, no layout, well under a millisecond for the few hundred devices and
// junctions this Editor's own plants run to (the seed this whole test batch uses is two or three) -
// the number this had to stay off entirely is isCellVisible's own call frequency (once per cell per
// repaint), not the set's (once per committed edit).
//
// THE ONE EXCEPTION, DOCUMENTED RATHER THAN CLOSED (fix round 4): draw.io's own COLLAPSE - the "+"/"-"
// on a real CONTAINER cell (style container=1, e.g. a swimlane or an explicit Container shape; a
// plain Ctrl+G group is NOT foldable, MEASURED - it carries no such control) - still hides whatever
// is inside, marked or not, because validateCell ANDs the collapse check in AFTER visibility and
// INDEPENDENTLY of it: "b&&(!this.isCellCollapsed(a)||a==this.currentRoot)" is what is handed to a
// cell's CHILDREN, and this guard has no way into that - it only answers isCellVisible, and a
// container the guard correctly calls "visible" can still fold its own children away regardless of
// what isCellVisible says about any of them. Left alone, not covered, for three reasons together:
// the container itself stays on screen with its fold control, so nothing vanishes WITHOUT a visible
// trace of where it went and how to bring it back; the fold is the operator's own reversible click,
// the same kind of choice hiding a note is; and a device reparented into ANY container - collapsed or
// not - is already refused at the save, by name, the same way row 12's own grouped device is:
// DrawioDelta.OnTheCanvas (`WE/Drawio/DrawioDelta.cs:1620`, checked at `:616`) refuses a device whose
// PARENT is not DrawioDocument.DefaultLayerId, and a container is exactly such a parent. So this is
// not a route to losing a device from the plant - only a route to a session where it is not on
// screen for a moment, with a visible way back and a save that already says no.
//
// LEFT OUTSIDE THE GUARD, ON PURPOSE: every cell WITHOUT a marked descendant and without the mark
// itself - the decoration the operator draws by hand, which this Editor's own save leaves entirely
// to draw.io's own rules (see DrawioDocument.Build/DrawioReader, neither reads nor writes a
// visibility flag for it either) - governed exactly as draw.io itself would govern it: a hidden
// layer, a hidden tag, or a cell hidden directly all still hide it, MEASURED as still true after this
// round's widening (fix round 3 also corrects the earlier version of this comment, which claimed a
// note left on the hidden DEFAULT layer specifically would stay hidden "governed as draw.io would" -
// false now that the layer itself is a MEMBER of visibilityAncestorIds whenever it has a marked
// descendant, which this plant's own default layer always does: a note on that same layer is carried
// back to VISIBLE along with the layer, not because of anything special about notes, but because the
// layer's own answer is no longer "hidden" for anyone under it). A LOCKED cell is a different
// question entirely and never belonged in this list: draw.io's own lock only stops a cell from being
// moved, resized or deleted - it has nothing to do with mxGraphView.isCellVisible or any rendering
// decision, measured by its absence from every function this comment cites. Hiding a note the
// operator drew by hand is that operator's own choice to make and undo; hiding a device is a loss
// nobody chose, and the two must not be confused with one guard that goes too far.
//
// On the instance and not on the prototype, like everything else this bridge changes in draw.io but
// one (keepTheSaveButtonClear, queue item 8, which says why it wraps mxWindow.prototype.fit), and
// put back at the three real lifecycle points by teardownTheDefencesAgainstHiding (fix round 3: NOT
// from unwatchChanges' own failure path any more - see that function's own comment for why).
function guardMarkedCellsVisible(ui, w) {
  try {
    const graph = ui.editor.graph;
    const model = graph.getModel();
    visibilityGraph = graph;
    visibilityHadItsOwn = Object.prototype.hasOwnProperty.call(graph, 'isCellVisible');
    visibilityAsDrawioWroteIt = graph.isCellVisible;
    graph.isCellVisible = function (cell) {
      const mark = cell && cell.value && typeof cell.value.getAttribute === 'function'
        ? cell.value.getAttribute(markAttribute)
        : null;
      if (mark === 'device' || mark === 'junction') return true;
      if (cell && cell.id != null && visibilityAncestorIds.has(String(cell.id))) return true;
      return visibilityAsDrawioWroteIt.apply(this, arguments);
    };

    // The ancestor set: built now, for whatever the plant was already seeded with before this guard
    // ever ran (the seeding itself is one edit this listener is not yet subscribed to receive), and
    // kept current on every edit after - fix round 4, THE REDRAW: recomputing the set is not enough
    // on its own. mxGraphModel fires mxEvent.CHANGE to every listener in the order they were added,
    // and mxGraph's OWN listener - installed inside the graph's constructor, long before this bridge
    // ever attaches - is registered FIRST: by the time this one runs, the graph has ALREADY validated
    // the view against the STALE set. MEASURED LIVE reproducing it: group two devices, hide the
    // group, ungroup them, Ctrl+Z - the devices stayed off-screen until something else forced a
    // redraw (graph.refresh()), because the set changed back but nothing repainted with it; the same
    // for a single edit that adds a hidden group already containing a device. undo/redo go through
    // this SAME event (mxUndoableEdit.undo ends in the same model.endUpdate/CHANGE as any edit), so
    // fixing the listener fixes both at once - a separate BEFORE_UNDO hook was tried and dropped: it
    // would help the visible-immediately case no more than this does, and CHANGE alone already
    // covers undo/redo, which is the case that was actually broken. The fix: compare the set BEFORE
    // and AFTER recomputing, and when it changed, force exactly the two calls validateCell is built
    // from (invalidate every cell's state, then validate from the root again) - not graph.refresh(),
    // which also clears every state and fires its own REFRESH event that other parts of this bridge
    // (drawio-live.js) have no reason to hear about. Skipped when the set did NOT change - the
    // ordinary case, most edits (a drag, a rename, a color) touch no ancestor membership at all - so
    // this costs nothing beyond the recompute itself on the edits that do not need it.
    visibilityModel = model;
    recomputeMarkedAncestors(model);
    visibilityChangeListener = () => {
      const before = visibilityAncestorIds;
      recomputeMarkedAncestors(model);
      if (!sameIdSet(before, visibilityAncestorIds)) {
        graph.view.invalidate();
        graph.view.validate();
      }
    };
    model.addListener(w.mxEvent.CHANGE, visibilityChangeListener);
  } catch (err) {
    // Not declared to the page, for the same reason every other bridge closure's catch is not: a
    // guard that could not be installed leaves the plant exactly as visible as draw.io's own rules
    // would leave it - no worse than before this batch, and the console line says why.
    console.error('drawio-bridge: the guard on marked cells could not be installed', err);
  }
}

// Rebuilds visibilityAncestorIds from scratch: every cell currently in the model carrying our mark,
// walked UP to the root, every id on the way added to the set. A full recompute and not an
// incremental one - see guardMarkedCellsVisible's own comment for why that is cheap enough here, and
// simpler than trying to patch the set for whatever an edit's own changes were (a group formed, a
// device moved between groups, a device deleted taking its own membership with it - each would need
// its own logic, for a set this can just rebuild instead).
function recomputeMarkedAncestors(model) {
  const ancestors = new Set();
  const cells = model.cells || {};
  for (const id in cells) {
    if (!Object.prototype.hasOwnProperty.call(cells, id)) continue;
    const cell = cells[id];
    const mark = cell && cell.value && typeof cell.value.getAttribute === 'function'
      ? cell.value.getAttribute(markAttribute)
      : null;
    if (mark !== 'device' && mark !== 'junction') continue;
    for (let parent = cell.parent; parent != null; parent = parent.parent) {
      if (parent.id != null) ancestors.add(String(parent.id));
    }
  }
  visibilityAncestorIds = ancestors;
}

// Whether two sets of ids hold the same members - not whether they are the same OBJECT, which they
// never are (recomputeMarkedAncestors always builds a fresh one). Used to decide whether the redraw
// in guardMarkedCellsVisible's own change listener is owed at all: most edits change nothing an
// ancestor set cares about, and size first is the cheap rejection for the common case.
function sameIdSet(a, b) {
  if (a.size !== b.size) return false;
  for (const id of a) if (!b.has(id)) return false;
  return true;
}

// The other half of guardMarkedCellsVisible, called by teardownTheDefencesAgainstHiding at the three
// real lifecycle points. hasOwnProperty decides how, not whether: a graph that never had its own
// isCellVisible (the ordinary case - it comes from the prototype chain) gets the instance property
// deleted outright, falling back to whatever the prototype answers; one that somehow already had its
// own (not measured to happen, but the same defensive shape as the leaving question's own restore)
// gets that exact value back.
function unguardMarkedCellsVisible() {
  try {
    if (visibilityGraph) {
      if (visibilityHadItsOwn) visibilityGraph.isCellVisible = visibilityAsDrawioWroteIt;
      else delete visibilityGraph.isCellVisible;
    }
    if (visibilityModel && visibilityChangeListener) visibilityModel.removeListener(visibilityChangeListener);
  } catch (err) {
    console.error('drawio-bridge: the guard on marked cells could not be taken off', err);
  }
  visibilityGraph = null;
  visibilityHadItsOwn = false;
  visibilityAsDrawioWroteIt = null;
  visibilityModel = null;
  visibilityChangeListener = null;
  visibilityAncestorIds = new Set();
}

// THE SECOND BELT (fix round 2): not the primary defence - guardMarkedCellsVisible is, and holds
// even if some route nobody has measured reaches the same two levers some other way - but the front
// door Edit Link opens is worth closing too, and MEASURED to cost nothing else. Graph.prototype.
// handleCustomLink is a single, narrow function that does something for exactly ONE shape of link:
// "data:action/json,...". Every ordinary http/https link never reaches it at all - Graph.prototype.
// isCustomLink only calls a link "custom" when it starts with "data:" (openLink handles everything
// else), and draw.io's own page-navigation links ("data:page/id,...") are answered by EditorUi.
// prototype.handleCustomLink BEFORE this one is ever called - MEASURED, that wrapper checks
// Graph.isPageLink(c) first and only falls through to this.editor.graph.handleCustomLink(c,f) when
// it is false. So turning this ONE function into a no-op closes the JSON mini-language specifically
// and touches nothing else a real link on this plant would ever do - this Editor's own cells never
// carry a data: link to begin with (DrawioDocument.Build writes plain http links or none), so a
// click that used to run custom actions now does nothing instead, the same choice
// closeTheLayersWindow/closeTheTagsWindow already made for their own actions.
//
// On the instance, same shape as the guard above - fix round 3: the first version of this function
// had no restore at all, unlike every other override this bridge makes. See restoreCustomActionLinks.
function ignoreCustomActionLinks(ui) {
  try {
    const graph = ui.editor.graph;
    if (typeof graph.handleCustomLink !== 'function') return;
    customLinkGraph = graph;
    customLinkHadItsOwn = Object.prototype.hasOwnProperty.call(graph, 'handleCustomLink');
    customLinkAsDrawioWroteIt = graph.handleCustomLink;
    graph.handleCustomLink = function () {};
  } catch (err) {
    console.error('drawio-bridge: custom-action links could not be closed', err);
  }
}

// The other half of ignoreCustomActionLinks, called by teardownTheDefencesAgainstHiding at the same
// three points as unguardMarkedCellsVisible - same hasOwnProperty shape, same reason.
function restoreCustomActionLinks() {
  try {
    if (customLinkGraph) {
      if (customLinkHadItsOwn) customLinkGraph.handleCustomLink = customLinkAsDrawioWroteIt;
      else delete customLinkGraph.handleCustomLink;
    }
  } catch (err) {
    console.error('drawio-bridge: custom-action links could not be restored', err);
  }
  customLinkGraph = null;
  customLinkHadItsOwn = false;
  customLinkAsDrawioWroteIt = null;
}

// FIX ROUND 3 (reviewer finding 3): the two defences above used to be torn down as a side effect of
// unwatchChanges(), which is ALSO called from watchChanges' own catch block - a failure to subscribe
// to the model for the LEAVING QUESTION's counter, which has nothing to do with whether the guard or
// the second belt should still hold. Both were being undone by a failure that was not theirs. Called
// instead directly at the three points that are genuinely "a new draw.io is about to exist, or this
// one is going away": the start of a new watchSelection, attach(), detach() - see those three call
// sites.
function teardownTheDefencesAgainstHiding() {
  unguardMarkedCellsVisible();
  restoreCustomActionLinks();
}

// Tells the page which cells have just ENTERED draw.io's model, so that a shape dragged out of the
// device section can become a real device on the plant (owner's decision of 2026-09-17).
//
// It subscribes to the graph's own model, not to the selection model, and it reads the changes of
// the edit that has just been committed: mxGraphModel fires mxEvent.CHANGE once per atomic edit,
// with every change of that edit inside it. A paste of twenty cells is therefore ONE call carrying
// twenty cells, not twenty calls - which is what lets the page take the permit once for the whole
// gesture instead of twenty times. Measured in a real browser, see DrawioShapesPanelTest.
//
// WHICH change means "a cell was born": an mxChildChange whose CURRENT parent is in this model and
// whose PREVIOUS parent is not. The two fields are swapped by execute() - after it, 'parent' is
// where the cell is NOW and 'previous' where it was - and "is it in this model" is answered by
// walking up the parents to the model's own root, not by looking at an id.
//
// It used to read "previous is null and parent is not", and that cost the worst defect of the
// previous batch: a native Ctrl+V DOES arrive with a previous parent - the temporary parent of the
// decoded model draw.io builds to read the clipboard - so the pasted cell was skipped, no device
// was born, and a cut-and-paste DESTROYED the device (the cut's deletion went through at the next
// save, the paste created nothing: the plant went from 3 devices to 2).
//
// THE THREE SITUATIONS, measured in a real browser on 2026-09-17 with a probe reading the raw
// changes off this very model, because widening this test at a guess is what would let the
// fifty-one duplicates of the seeding back in:
//
//   SEEDING (load of a plant with 2 devices, probe subscribed while the model still held only its
//     root and layer 1): ONE edit, two changes, BOTH mxRootChange and not one mxChildChange. The
//     loop below sees nothing at all - neither the old test nor the new one is even reached.
//   BIRTH (a shape dragged out of the panel): one mxChildChange, previous = null,
//     parent = layer "1" (in the model). Old test: yes. New test: yes.
//   PASTE (Ctrl+C then Ctrl+V with the cell really selected - the selection was the empty half of
//     the previous batch's failed attempt): one mxChildChange, previous = an object whose id is
//     null and which is NOT reachable from this model's root, parent = layer "1".
//     Old test: NO - this is the defect. New test: yes.
//   MOVE BETWEEN PARENTS (Ctrl+G over two seeded devices): the group cell is born
//     (previous = null, and it carries no mark, so the page ignores it) and the two devices move
//     with previous = layer "1", WHICH IS IN THE MODEL, parent = the group.
//     Old test: no. New test: NO - a move is still not a birth, which is the whole point.
//   REMOVAL (the undo of a drop) keeps being skipped by the current parent: parent = null.
//
// So the new test is a superset of the old one by exactly one case - a previous parent that does
// not belong to this model - and that case cannot be the seeding, because the seeding produces no
// child change at all.
//
// What this does NOT decide, and that is the whole point: whether any of these cells should become
// a device. It reports the mark, the declared type, the parent and the box, and the PAGE - the only
// side that knows the plant - decides. The division is not tidiness: the plant is seeded by sending
// a whole document through load(), and a listener that decided here would have no way of telling
// the operator's drop from the fifty-one devices of the plant arriving at opening.
function watchAdditions(w, dotnet) {
  try {
    const model = selectionGraph.getModel();
    // Whether a cell belongs to THIS model, walked rather than looked up: the temporary parent a
    // paste arrives from has no id at all (measured: id null), so model.getCell() could not answer,
    // and an id-based test would also say "in the model" for a cell of a document that has since
    // been replaced. Walking up the parents to the root answers the only question that matters -
    // is this a parent the operator can see. Null is not in the model, which is what makes a plain
    // drop (previous null) and a removal (parent null) come out right without a special case.
    const inModel = (cell) => {
      const root = model.getRoot();
      for (let node = cell; node != null; node = node.parent) if (node === root) return true;
      return false;
    };
    const attributeOf = (cell, name) => (cell.value && typeof cell.value.getAttribute === 'function')
      ? cell.value.getAttribute(name)
      : null;
    const describe = (cell) => {
      // Read at CHANGE time, that is at the end of the edit: by now the drop has put the cell where
      // the operator let go of it, and the box is the one the drawing will keep.
      const geometry = cell.geometry;
      return {
        id: cell.id == null ? '' : String(cell.id),
        mark: attributeOf(cell, markAttribute),
        type: attributeOf(cell, typeAttribute),
        // The cell it hangs from: the page refuses anything that is not layer 1 - inside a group
        // the box below is relative to the group and not to the canvas (the same fact for which
        // DrawioDelta refuses to write a grouped cell's position), and on a second layer the box IS
        // absolute but the save holds the same single-layer rule. Reported and not judged here: this
        // file has no idea which layer the plant is drawn on.
        parent: (cell.parent && cell.parent.id != null) ? String(cell.parent.id) : null,
        x: geometry ? geometry.x : 0,
        y: geometry ? geometry.y : 0,
        width: geometry ? geometry.width : 0,
        height: geometry ? geometry.height : 0,
      };
    };

    additionsListener = (sender, evt) => {
      try {
        const edit = evt.getProperty('edit');
        if (!edit || !edit.changes) return;
        const born = [];
        for (const change of edit.changes) {
          if (!change || !change.child) continue;
          // Where it is now must be somewhere the operator can see: a removal leaves parent null.
          if (!inModel(change.parent)) continue;
          // Where it was must NOT be: a move inside this drawing is not a birth.
          if (inModel(change.previous)) continue;
          born.push(describe(change.child));
        }
        if (born.length > 0) {
          // WHICH edit these cells came in with, before the page is told anything: it is the only
          // moment at which that is known, and renameCell needs it much later. A paste of twenty is
          // one edit for twenty cells, so the twenty entries share it.
          rememberTheBirth(born, edit);
          // Queue item 15: a gesture that brings too many cells for one message of the circuit is
          // said as their count, and none of them can become a device.
          tellOrWarn(dotnet, 'OnDrawioCellsAdded', born, 'OnDrawioTooManyCellsAdded', born.length);
        }
      } catch (err) {
        // Inside a listener draw.io calls: an exception from here would climb into mxGraphModel's
        // endUpdate, where nobody is waiting for it. It is written down and declared instead - a
        // drop that quietly fails to become a device is the exact loss this batch exists to close.
        console.error('drawio-bridge: the cells just added could not be read', err);
        dotnet.invokeMethodAsync('OnDrawioCreationUnavailable');
      }
    };
    additionsModel = model;
    additionsModel.addListener(w.mxEvent.CHANGE, additionsListener);
  } catch (err) {
    // Back to no subscription at all, listener included: addListener can throw AFTER the two
    // variables are set, and a subscription believed to exist is one nobody would ever take off.
    unwatchAdditions();
    console.error('drawio-bridge: the subscription to draw.io\'s additions could not be installed', err);
    dotnet.invokeMethodAsync('OnDrawioCreationUnavailable');
  }
}

// Takes the subscription off INSIDE draw.io, and not only here: the listener lives on an object of
// the iframe's, and forgetting it would leave a call towards a .NET reference that is gone - the
// same reason the page detaches this bridge in DisposeAsync.
function unwatchSelection() {
  if (selectionTimer) clearTimeout(selectionTimer);
  if (selectionModel && selectionListener) selectionModel.removeListener(selectionListener);
  selectionTimer = null;
  selectionModel = null;
  selectionListener = null;
  selectionWindow = null;
  selectionDocument = null;
  selectionGraph = null;
  drawioUi = null;
  // The additions go with it, in the same breath and for the same reason: their listener lives on
  // an object of the iframe's too, and it calls the same .NET reference.
  unwatchAdditions();
  // And the pointer's own listener, on the same graph and container that are going away - it calls
  // no .NET reference, but leaving it on would mean a listener nobody would ever take off, and text
  // left over from a drawing that no longer exists.
  unwatchPointer();
  // And row 63's entries in the right-click menu: installed from the same callback, they call the
  // same .NET reference.
  unhookThePopupMenu();
  // And row 8's guard on the cut, installed from the same callback too.
  unguardTheCut();
}

// The additions half of unwatchSelection, in a function of its own because watchAdditions has to be
// able to undo ITSELF - half an installed subscription - without taking the selection off with it.
function unwatchAdditions() {
  if (additionsModel && additionsListener) additionsModel.removeListener(additionsListener);
  additionsModel = null;
  additionsListener = null;
  // The births remembered go with the subscription that recorded them: they point at edits of a
  // model nobody is drawing on any more, and a cell id of the old drawing could collide with one of
  // the new (draw.io numbers them per document). A label arriving afterwards finds nothing, which
  // is the safe answer.
  birthEditOfCell.clear();
  undoHistory = null;
}

// Counts the model edits that light the mark "changed since it was last on the plant" - see
// changesSeen at the top of this file.
//
// It listens to the same event as watchAdditions, the model's mxEvent.CHANGE, fired once per
// committed edit. What it does NOT count, and why each one stays out:
//   - THE SEEDING. It does fire CHANGE (an mxRootChange) and it is counted like anything else; what
//     puts the mark back down is draw.io's own 'load' event, which the embedded draw.io posts right
//     after it has put the document in (see the message handler in attach).
//   - THE PAGE'S ECHOES OF THE PLANT: renameCell (a name it already has) and removeDeletedDeviceCells
//     (a device it no longer has) raise echoingThePlant around their own edit, which is skipped.
//   - THE LIVE STATUS. drawio-live.js draws shapes of the view (queue item 21), never cells of the
//     model, so it never reaches this listener (DrawioLiveOverlayTest reads the model before and
//     after a frame and finds it identical).
//   - THE SELECTION and "go to": mxGraphSelectionModel fires on itself, not on the model.
// An edit with no changes at all is not counted either: it changed nothing that could be lost.
//
// A failure to subscribe is not silence: the mark is then declared unknowable and every close asks.
function watchChanges(ui, w) {
  try {
    const model = ui.editor.graph.getModel();
    const listener = (sender, evt) => {
      if (echoingThePlant) return;
      try {
        const edit = evt.getProperty('edit');
        if (edit && Array.isArray(edit.changes) && edit.changes.length === 0) return;
      } catch {
        // An edit this cannot read is counted: in doubt, the mark goes on.
      }
      changesSeen++;
    };
    model.addListener(w.mxEvent.CHANGE, listener);
    changesModel = model;
    changesListener = listener;
    changesUnknowable = false;
    takeOverTheLeavingQuestion(ui);
  } catch (err) {
    unwatchChanges();
    changesUnknowable = true;
    console.error('drawio-bridge: the drawing\'s changes cannot be followed: closing the tab will always ask', err);
  }
}

// draw.io asks a question of its own when the tab closes: its EditorUi puts window.onbeforeunload
// on the IFRAME's window, and App.prototype.onBeforeUnload answers "allChangesLost" whenever
// "1"==urlParams.embed and editor.modified (read out of drawio/js/app.min.js, the bundle we host).
// Chromium runs a subframe's beforeunload too, so that question does reach the operator - MEASURED
// on 2026-09-24 against a host without this bridge's mark: a drag asked, a rename from the panel
// ASKED (editor.modified knows nothing of the plant), and a save whose report refused part of the
// drawing did NOT ask (draw.io's Save button puts modified back to false, whatever the plant then
// said). Two answers out of six wrong, so it is not ours to keep: while this bridge is attached
// draw.io's question says nothing, and the one on the page's window - askBeforeLeaving - decides
// alone.
//
// On the instance and not on the prototype, like everything else this bridge changes in draw.io but
// one (keepTheSaveButtonClear, queue item 8, which says why it wraps mxWindow.prototype.fit), and
// put back as it was by unwatchChanges.
function takeOverTheLeavingQuestion(ui) {
  questionUi = ui;
  questionHadItsOwn = Object.prototype.hasOwnProperty.call(ui, 'onBeforeUnload');
  questionAsDrawioWroteIt = ui.onBeforeUnload;
  ui.onBeforeUnload = function () { return undefined; };
}

function unwatchChanges() {
  try {
    if (changesModel && changesListener) changesModel.removeListener(changesListener);
    if (questionUi) {
      if (questionHadItsOwn) questionUi.onBeforeUnload = questionAsDrawioWroteIt;
      else delete questionUi.onBeforeUnload;
    }
  } catch (err) {
    console.error('drawio-bridge: the subscription to the drawing\'s changes could not be taken off', err);
  }
  changesModel = null;
  changesListener = null;
  questionUi = null;
  questionHadItsOwn = false;
  questionAsDrawioWroteIt = null;
  // Fix round 3: the guard on marked cells and the second belt used to be put back here too, and
  // that was wrong - this function also runs from watchChanges' own failure path, which has nothing
  // to do with either of them. See teardownTheDefencesAgainstHiding and its own three call sites.
}

// The page window's beforeunload handler. The browser shows its own words - no page may put its own
// there any more - and preventDefault plus a returnValue is what current browsers take as "ask".
// Nothing in it can throw: it reads three variables.
function askBeforeLeaving(e) {
  if (!holdsUnsavedWork()) return undefined;
  e.preventDefault();
  e.returnValue = '';
  return '';
}

// Back to "nothing drawn, nothing to lose", unknowable included: at attach, where a new draw.io is
// about to start with an empty canvas, and at detach, which puts this module back the way it was.
function forgetTheChanges() {
  countersGeneration++;
  changesSeen = 0;
  changesOnThePlant = 0;
  changesUnknowable = false;
}

// Remembers, for each cell just born, the undoable edit it arrived with - the map is keyed by cell
// because that is what renameCell is asked about. Re-inserted rather than overwritten so that the
// Map's insertion order stays an age order, which is what the ceiling below trims by.
function rememberTheBirth(born, edit) {
  for (const cell of born) {
    if (!cell.id) continue;
    birthEditOfCell.delete(cell.id);
    birthEditOfCell.set(cell.id, edit);
  }
  while (birthEditOfCell.size > birthsRemembered) {
    const oldest = birthEditOfCell.keys().next();
    if (oldest.done) break;
    birthEditOfCell.delete(oldest.value);
  }
}

// WHETHER THE LABEL ABOUT TO BE WRITTEN WOULD LAND DIRECTLY ON TOP OF THE GESTURE IT BELONGS TO,
// which is the one condition under which making it insignificant is safe. See renameCell for why.
//
// The walk goes down from the top of the undo stack - from indexOfNextAdd, which is where our own
// label is about to be pushed - and it is allowed to step over nothing except labels this bridge
// itself made insignificant. The first entry that is anybody else's stops it. Reaching the birth
// edit means: between that gesture and this label there is nothing an undo would refuse to walk
// through, so an undo that walks through this label lands on the birth it belongs to and stops.
//
// WHY THE EDIT REMEMBERED AT THE BIRTH IS THE VERY OBJECT THAT ENDS UP IN THE STACK, which is what
// the identity comparison below rests on. Out of the bundle we host, as it stands there:
//   mxGraphModel.prototype.endUpdate: "var a=this.currentEdit;this.currentEdit=
//     this.createUndoableEdit();a.notify();this.fireEvent(new mxEventObject(mxEvent.UNDO,..."
//   Editor.prototype.createUndoManager: "var b=new mxUndoManager;this.undoListener=function(e,g){
//     b.undoableEditHappened(g.getProperty(\"edit\"))}"
// - notify() is what fires CHANGE, the event watchAdditions hears the birth on, and the UNDO event
// fired straight after carries THE SAME a into the manager's history. So the object in hand at the
// birth and the entry in the stack are one and the same. It is not only read: if it were not so the
// walk would never find a birth, every label would stay significant, and the two tests that measure
// one press after a drop and after a paste would be red. They are green.
//
// MEASURED in a real browser on 2026-09-18, with the stack read off ui.editor.undoManager:
//   - ONE DROP, nothing in the window: the stack went from "indexOfNextAdd=0 of 0" to
//     "indexOfNextAdd=2 of 2 | 0:SIG/3[mxGeometryChange,mxGeometryChange,mxChildChange] |
//     1:insig/1[mxValueChange]" - the birth and our label - and one Ctrl+Z took indexOfNextAdd from
//     2 to 0, that is both. Unchanged by this guard, which is the point.
//   - A PASTE OF TWENTY, on a drawing that already held 30 marked cells: from
//     "indexOfNextAdd=37 of 37" to "58 of 58", and what went on top was ONE significant entry,
//     "37:SIG/60[mxGeometryChange,mxChildChange, …]" - the twenty cells arriving together - and then
//     exactly TWENTY "insig/1[mxValueChange]", our labels. All twenty came out insignificant, which
//     is the walk below stepping over up to nineteen of its own and still reaching the birth. One
//     Ctrl+Z took indexOfNextAdd from 58 back to 37 and the marked cells from 50 to 30.
//   - THE SAME DROP WITH SOMEBODY ELSE'S GESTURE IN THE WINDOW: "0:SIG/3[the birth] |
//     1:SIG/1[mxGeometryChange, a shape the operator moved] | 2:SIG/1[mxValueChange, our label]".
//     The walk stops on entry 1 and the label stays SIGNIFICANT, so one Ctrl+Z undoes the label and
//     stops there, with the move still where the operator put it (x=685.54), and a second press is
//     what undoes the move (back to x=625.54). WITHOUT this guard that same single press undid the
//     move as well - measured before the repair: x went back from 685.54 to 625.54 on ONE press,
//     with the operator's gesture gone along with a label they never asked for.
//   - A PASTE OF THREE WITH A GESTURE IN THE WINDOW, and this is what the guard really costs.
//     The move was armed to land right after the FIRST label. The stack that came out:
//       6:SIG/9[the three cells arriving together]   the birth
//       7:insig/1[mxValueChange]                     label 1 - the only one that got the concession
//       8:SIG/1[mxGeometryChange]                    the operator's move
//       9:SIG/1[mxValueChange]                       label 2, significant: entry 8 is not ours
//      10:SIG/1[mxValueChange]                       label 3, significant for the same reason
//     and the presses: 1 and 2 took the two labels back, 3 undid the move, and only 4 took the three
//     pasted shapes away. FOUR presses for three cells, and a paste of twenty disturbed before its
//     first label would be TWENTY-TWO. The guard does not cost "a second press" there: it costs one
//     press per label that landed after the intruder. What it buys is the same in all of them - the
//     operator's own gesture is still on the canvas afterwards - and that is the trade that was
//     taken, but the number is this one.
//
// THE MARK IS THE WHOLE TEST, and the second half that used to sit beside it - "and the entry is
// insignificant" - has been taken out because it could not change an outcome. wcsLabelOfACreation
// is written in exactly one place, on the line after significant=false and inside the same branch,
// so an entry carrying the mark is an insignificant entry by construction; a label of ours that
// stayed significant was written the ordinary way and carries no mark at all, and the walk stops on
// it for that reason. The comment that used to stand here called the second half "not
// belt-and-braces", and that was a third exclusive claim nobody had measured: with the clause
// removed the whole of DrawioShapesPanelTest is green, which is what a dead clause looks like.
//
// It was not resurrected either, and that was measured rather than argued. The way to make it live
// is to mark EVERY label of a creation, significant or not, and let the walk step over our own
// significant ones; it was built that way on 2026-09-18 and the paste of three below cost the same
// FOUR presses, with an identical stack. The reason is in that stack: what makes a label significant
// is a foreign entry between it and the birth, and that entry is still there for every label after
// it, so the birth is unreachable whatever we step over. A complication that removes no press is
// worse than the dead clause it replaces.
//
// Anything unknown answers false, and that is the whole safety of this function: no birth
// remembered, no undoManager, a stack that has been trimmed past the birth (a Ctrl+Z already
// pressed, or the hundred-entry ceiling of mxUndoManager). False costs the operator one extra
// press; a wrong true costs somebody else's gesture.
function theLabelLandsOnItsOwnGesture(cellId) {
  const birth = birthEditOfCell.get(cellId);
  if (!birth || !undoHistory || !Array.isArray(undoHistory.history)) return false;
  const history = undoHistory.history;
  let i = undoHistory.indexOfNextAdd;
  if (!(i > 0) || i > history.length) return false;
  while (i-- > 0) {
    const entry = history[i];
    if (entry === birth) return true;
    if (!entry || entry.wcsLabelOfACreation !== true) return false;
  }
  return false;
}

// Renames a cell on the canvas, AFTER the plant has accepted the new name (batch C). It is the only
// place this bridge changes anything in draw.io's own model - the seeding sends a whole document
// through load() instead - and the order is the whole point: rename the canvas before the write and
// the drawing would show a name the plant does not have, which is the very lie this panel exists to
// close.
//
// It is called through the same graph the selection subscription came from, so it works exactly when
// the selection works: if the subscription failed, this returns false and the page says the canvas
// did not follow, instead of quietly leaving the old label there.
//
// It used to open a beginUpdate/endUpdate pair around the call, and the comment justifying it said
// that without the pair the change would not be undoable and draw.io would not repaint the label in
// one go. Both halves were false, and the bundle we host says so in its own source: cellLabelChanged
// wraps ITSELF. Read out of drawio/js/app.min.js, as it stands there -
//   Graph.prototype.cellLabelChanged=function(a,b,c){this.model.beginUpdate();
//   try{this.model.setValue(a,b),c&&this.cellSizeUpdated(a,!1)}finally{this.model.endUpdate()}}
// - and mxGraph.prototype.cellLabelChanged (mxgraph/mxClient.js) is built the same way. The undoable
// edit is born either way, the repaint with it, and a second pair around one model change adds
// nothing. Taken out together with the comment that argued for it. Of the two halves it claimed,
// the repaint is measured - the browser test waits for the new label to appear on the canvas - and
// the undo is not measured by anything here: it rests on the source quoted above.
//
// THE THIRD ARGUMENT, partOfTheGestureBefore, IS ABOUT CTRL+Z AND NOTHING ELSE. It is true only for
// the label a CREATION puts on the shape that was just dropped, and it ASKS for that label's
// undoable edit to be NOT SIGNIFICANT, so that one Ctrl+Z takes the label and the shape away
// together instead of costing the operator two presses for one gesture. It is an ask and not an
// order: the answer is no whenever the label would not land directly on top of the birth it belongs
// to - see theLabelLandsOnItsOwnGesture - and then the label is written the ordinary way. What that
// falling back costs is ONE PRESS PER LABEL that landed after the intruder, plus the press for the
// intruder itself and the press for the birth: two for a single drop, and FOUR for a paste of three
// disturbed after its first label (measured on 2026-09-18, the stack is written out over
// theLabelLandsOnItsOwnGesture). "Two presses" is the cost of the single drop and of nothing else,
// and it was written here as though it were the cost of the fallback. How it works, measured in a
// real browser on 2026-09-17 and not only read:
//
//   mxUndoManager.prototype.undo=function(){for(;0<this.indexOfNextAdd;){
//     var a=this.history[--this.indexOfNextAdd];a.undo();
//     if(a.isSignificant()){this.fireEvent(new mxEventObject(mxEvent.UNDO,"edit",a));break}}};
//
// - the edit is undone BEFORE the flag is looked at, and an insignificant one does not stop the
// loop, so the walk carries on into the edit underneath. Measured: after a drop the history held
// "0:SIG/3[mxGeometryChange,mxGeometryChange,mxChildChange] | 1:SIG/1[mxValueChange]" - the drop and
// our label, both significant - and that second SIG is what this argument turns off.
//
// The flag lives on model.currentEdit, which endUpdate hands to the undo manager when updateLevel
// comes back to zero. cellLabelChanged opens a pair of its own, so the pair opened here keeps the
// level at 1 while it runs and the edit is still ours when it ends.
//
// WHAT THE FLAG ALONE COST, and the reason theLabelLandsOnItsOwnGesture exists (owner's decision of
// 2026-09-18). An insignificant edit does not stop the walk, so it hands to that same Ctrl+Z
// whatever sits UNDER it - and what sits under it is only the birth of the shape as long as nothing
// else got in between. The creation's round trip to the plant is about 137 ms a cell and nearly
// three seconds on a paste of twenty (measured in batch 0ccfd67), and a gesture the operator makes
// inside that window lands between the two. Measured on 2026-09-18, a drop with a shape moved in
// the window while the creation was in flight: the stack held
// "0:SIG/3[the birth] | 1:SIG/1[the move] | 2:insig/1[our label]", and ONE Ctrl+Z walked through the
// label, undid the move and stopped - the operator's own move gone with a label they never asked
// for. The guard closes that: the label stays significant whenever it does not land straight on its
// own birth, and the press stops on it. What that is worth and what it costs are both measured, and
// the cost is not the one this file used to claim: on a paste of three disturbed after the first
// label the operator pays FOUR presses, not two, because every label that lands after the intruder
// stays significant in its turn. The reserve says so out loud, and nothing was added to bring that
// number down: the attempt was built and measured on 2026-09-18 and removed no press at all - see
// theLabelLandsOnItsOwnGesture.
//
// IT IS NOT FOR THE PANEL'S RENAME, and that is a decision rather than an omission: there the
// operator typed in OUR panel and there is no draw.io gesture underneath, so an insignificant edit
// would make Ctrl+Z put the old label back AND carry on undoing whatever unrelated thing came
// before. That would be worse than the two presses. The panel calls this without the argument.
//
// THE GUARD ON updateLevel: when it is not zero somebody else is already changing the model and
// currentEdit is THEIRS - marking it insignificant would hand their gesture to the same walk. Two
// presses are better than somebody else's gesture vanishing, so in that case the label is written
// the ordinary way.
//
// WHAT IS MEASURED ABOUT IT, because this used to call itself "not decoration" and could not show
// it. Nobody has reached this branch in a real Editor, and the two things that were measured say
// why: with the model open no event is fired, so watchAdditions hears nothing and the page asks for
// no label; and this function is one synchronous JS task, with no suspension point between the test
// and the writing of the flag, so there is no window to interleave in. It is therefore defensive
// code that is correct and, on everything measured so far, unreachable. It stays, because the day
// something else in this bridge does open the model the cost of not having it is somebody else's
// gesture disappearing - but nothing here proves it runs, and the reserve says so out loud.
export function renameCell(cellId, label, partOfTheGestureBefore, alreadyOnThePlant = true) {
  if (!selectionGraph) return false;
  // MOST CALLERS call this only after a write went through - the birth naming after the create
  // request answered, the config panel's rename after the plant took the new name - so by default
  // the model edit below is an echo and not work that a closed tab would lose: it must not light the
  // mark (see watchChanges). cellLabelChanged is synchronous and fires CHANGE inside its own
  // endUpdate, so the flag is up exactly for this edit and for nothing after it.
  //
  // THE RE-POINTING GESTURE (row 15) IS THE EXCEPTION: it writes the shape/device pair into this
  // page's own map and calls this with alreadyOnThePlant=false - nothing has reached the plant yet,
  // the pair only goes there at the next save - so that label change is real unsaved work and must
  // light the mark like any other edit.
  echoingThePlant = !!alreadyOnThePlant;
  try {
    return renameCellOnTheCanvas(cellId, label, partOfTheGestureBefore);
  } finally {
    echoingThePlant = false;
  }
}

function renameCellOnTheCanvas(cellId, label, partOfTheGestureBefore) {
  try {
    const model = selectionGraph.getModel();
    const cell = model.getCell(cellId);
    // The cell can really be missing: the drawing is the operator's, and the device whose
    // configuration the panel is showing may have been deleted from the canvas while the write was
    // on its way. The plant has the new name all the same - that write went through - so this is
    // reported, not thrown.
    if (!cell) return false;
    if (partOfTheGestureBefore) {
      // Asked BEFORE anything is written, because writing is what moves the top of the stack, and
      // forgotten straight away: this cell's birth has had its one answer.
      const onItsOwnGesture = theLabelLandsOnItsOwnGesture(cellId);
      birthEditOfCell.delete(cellId);
      if (onItsOwnGesture && model.updateLevel === 0) {
        model.beginUpdate();
        try {
          // Marked at once, before the change: at updateLevel 0 currentEdit is the fresh empty one
          // endUpdate left behind, so there is nobody else's work in it to mark. The second mark is
          // what lets the NEXT label of the same gesture walk over this one - see
          // theLabelLandsOnItsOwnGesture - and it is set only on an edit that really is ours.
          //
          // THESE TWO LINES BELONG TOGETHER, and the walk now depends on it: the mark is the walk's
          // whole test, so an edit that carried it while staying significant would be walked over
          // and the undo would stop on it anyway, promising a press that does nothing. This is the
          // only place either of them is written, and neither is ever written without the other.
          if (model.currentEdit) {
            model.currentEdit.significant = false;
            model.currentEdit.wcsLabelOfACreation = true;
          }
          selectionGraph.cellLabelChanged(cell, label);
        } finally {
          model.endUpdate();
        }
        return true;
      }
    }
    selectionGraph.cellLabelChanged(cell, label);
    return true;
  } catch (err) {
    console.error('drawio-bridge: the cell could not be renamed on the canvas', err);
    return false;
  }
}

// Selects one cell on the canvas and brings it into view: how the plant checks take whoever reads
// them to the device a warning is about.
//
// IT DOES NOT TOUCH THE MODEL, and that is the shape of the whole batch rather than a detail.
// Colouring the offending cells - which is what the SVG Editor does on its own canvas - would be a
// change of the MODEL, and draw.io's undo manager listens to the model and to the view:
//
//   Editor.prototype.createUndoManager=function(){var a=this.graph,b=new mxUndoManager;
//     this.undoListener=function(e,g){b.undoableEditHappened(g.getProperty("edit"))}; ...
//     a.getModel().addListener(mxEvent.UNDO,d);a.getView().addListener(mxEvent.UNDO,d); ...
//
// (read out of drawio/js/app.min.js, the bundle we host). A highlight would land in the history
// Ctrl+Z walks, and this branch has already spent three batches on what may and may not go in there.
// The selection does not: mxGraphSelectionModel.changeSelection builds its undoable edit with
// "new mxUndoableEdit(this,!1)" and fires mxEvent.UNDO on ITSELF, and the two addListener calls
// above are on the model and on the view, not on the selection model. That is what the source says;
// what the product rests on is the measurement, taken in a real browser through this very function
// before anything was built on it - see DrawioChecksTest, which reads the undo stack before and
// after a selection and then presses Ctrl+Z for real.
//
// MEASURED ON 2026-09-18, on a freshly opened plant, both sides of the choice:
//   - graph.setCellStyles('strokeColor', ..., [cell]) - the colouring - took the undo manager from
//     [indexOfNextAdd, history.length] = [0, 0] to [1, 1], one SIGNIFICANT entry;
//   - one drag by the operator took it from [0, 0] to [1, 1] as well, and a selection through this
//     function left it at [1, 1]: nothing added, and one Ctrl+Z put the drag back.
//
// It goes through the same graph the selection subscription came from, so it works exactly when the
// selection works - and a page whose subscription failed is told THAT, rather than being told the
// cell is not there.
//
// The answer is a code and not a boolean because the failures are different facts for whoever reads
// the page: no subscription at all, a cell that is not on the drawing (a device with no position is
// not drawn - PlantGraph.IsPlaced - and neither is one whose shape the operator deleted), and a
// draw.io that threw. The page turns each one into its own sentence and never keeps quiet about any
// of them.
export function selectCell(cellId) {
  if (!selectionGraph) return 'no-graph';
  try {
    const cell = selectionGraph.getModel().getCell(cellId);
    if (!cell) return 'no-cell';
    selectionGraph.setSelectionCell(cell);
    // Centred, not merely scrolled into view: on a plant of fifty devices a cell brought just inside
    // the edge arrives surrounded by the ones the warning is NOT about. Centred in the part of the
    // canvas the page's windows and draw.io's panels leave free (owner's decision D2, 29/9; queue
    // item 36): see centreCellInTheFreeCanvas. Always, even when the cell is already in sight.
    centreCellInTheFreeCanvas(selectionGraph, cell);
    return 'ok';
  } catch (err) {
    console.error('drawio-bridge: the cell could not be selected on the canvas', err);
    return 'failed';
  }
}

// The floating windows of the page (drawio-windows.js, spec 2026-09-29) float over the canvas, and
// "go to" from a row of the device list would otherwise centre the device right under the window
// the row is in (owner's decision D2, 29/9). So the cell is centred in the largest rectangle of the
// canvas that no open window covers AMONG THOSE THAT HOLD THE CELL (queue item 25). The copy
// Duplicate draws (drawDeviceCell) is brought into view by this same function (queue item 24), so
// the two can never place a cell differently - but only when the operator could not see it where it
// was born (queue item 33, owner's decision 1/10): see isSeenWhole. With nothing over the canvas it
// is centred as it always was (mxGraph's own scrollCellToVisible(cell, true)), and so it is when no
// free rectangle holds the cell: a cell that fits nowhere is better centred than squeezed into a
// corner.
//
// What covers the canvas is the page's windows AND draw.io's own panels over it (queue item 36,
// owner's decision 1/10). MEASURED in DrawioWindowsBrowserTest, the canvas at -10,89 1290x885 on the
// page: draw.io's shapes panel at 10,97 214x646 and its format panel at 1038,101 242x602 are
// mxWindows of draw.io's document floating over mxGraph's container, and "go to" with the Devices
// window over the left of the canvas centred the device under the format panel. draw.io's toolbar
// is above the container, not over it.
//
// Until queue item 25 the largest free rectangle of all was chosen first and only then held up to
// the cell, so a band too thin for it beat a smaller corner that would have held it. MEASURED in
// DrawioWindowsBrowserTest, the canvas at -10,89 1290x885: the Devices window over all of it but a
// band 40 px high across the top, the Missions window leaving a corner 240 x 150 at the bottom left.
// The band won (1290 x 40 against 240 x 150), was too low for the 120 x 60 device, and "go to"
// centred the device on the plain centre of the canvas (its box at 576.96,503.98) - under the
// Devices window.
//
// "The canvas" is the part of draw.io's iframe the drawing scrolls in that can be SEEN - mxGraph's
// container, its client box (scrollbars out), in the page's coordinates (the container's box inside
// the iframe's document, moved by where the iframe's content starts on the page), cut to the
// iframe's content box and to the page's viewport. The container sticks out of the iframe: MEASURED,
// it starts at x -10 with the iframe at 0, and those 10 px, which no window can cover either, counted
// as free canvas - so a free area at the left edge could "hold" the cell only through pixels nobody
// sees, and a cell centred there sat 5 px left of the middle of what can be seen (final review of
// rows 22-29: since queue item 25 any area that holds the cell can win, not only the largest).
//
// "The cell" is the box its shape covers on the screen: the state's box, zoom included, turned by
// the cell's rotation (mxUtils.getBoundingBox - mxGraph paints a turned device turned). A device at
// 90 degrees is painted tall: held up by its unturned box, it was refused by a column that holds its
// shape, and sent back under a window; the seed's device at 30 degrees covers 134 x 112, not its own
// 120 x 60.
//
// The windows are the page's .drawio-window elements that are not hidden, whatever their order; the
// panels draw.io's .mxWindow elements that it shows (a closed one is display none).
//
// The scroll itself is scrollCellToVisible's, read out of the bundle we host (js/app.min.js, the
// same code as mxgraph/src/view/mxGraph.js) with ONE change - the point the cell's centre is brought
// to, the centre of the free rectangle instead of the container's:
//
//   scrollCellToVisible=function(a,b){var c=-this.view.translate.x,d=-this.view.translate.y;
//     a=this.view.getState(a);null!=a&&(c=new mxRectangle(c+a.x,d+a.y,a.width,a.height),
//     b&&null!=this.container&&(b=this.container.clientWidth,d=this.container.clientHeight,
//     c.x=c.getCenterX()-b/2,c.width=b,c.y=c.getCenterY()-d/2,c.height=d), ...
//
// A rectangle as big as the container whose left edge is the cell's centre less the anchor: the
// scroll brings that rectangle's edge to the container's edge (to within mxGraph's own 2 px), and so
// the cell's centre to the anchor - with scrollbars (draw.io's default) as without, where
// scrollRectToVisible moves the view's translate instead, and the view is told as mxGraph tells it.
// draw.io pads its page by about the container's size on every side (Graph.getPagePadding), so there
// is room to scroll a cell of the drawing to any point of the container.
const pageWindowSelector = '.drawio-window';
const drawioPanelSelector = '.mxWindow';

function centreCellInTheFreeCanvas(graph, cell) {
  const view = graph.view;
  const state = view.getState(cell);
  const anchor = state ? freeCentreOfTheCanvas(graph, state) : null;
  if (!anchor) {
    graph.scrollCellToVisible(cell, true);
    return;
  }

  const w = graph.container.ownerDocument.defaultView;
  const before = new w.mxPoint(view.translate.x, view.translate.y);
  const bounds = new w.mxRectangle(
    -view.translate.x + state.x + state.width / 2 - anchor.x,
    -view.translate.y + state.y + state.height / 2 - anchor.y,
    graph.container.clientWidth,
    graph.container.clientHeight);
  if (graph.scrollRectToVisible(bounds)) {
    const after = new w.mxPoint(view.translate.x, view.translate.y);
    view.translate.x = before.x;
    view.translate.y = before.y;
    view.setTranslate(after.x, after.y);
  }
}

// Where the canvas is on the page and what lies over it, as { left, top, canvas, covers }: left and
// top where the container's client box starts on the page (the anchor is measured from there), the
// canvas and the covers those of the comment above, every cover cut to the canvas. Null when draw.io
// is not in a frame or no part of the canvas can be seen.
function theCanvasAndWhatCoversIt(graph) {
  const container = graph.container;
  const w = container.ownerDocument.defaultView;
  const frame = w.frameElement;
  if (!frame) return null;
  const f = frame.getBoundingClientRect();
  const c = container.getBoundingClientRect();
  // Where the container's client box starts on the page: the anchor is measured from here.
  const left = f.left + frame.clientLeft + c.left + container.clientLeft;
  const top = f.top + frame.clientTop + c.top + container.clientTop;
  const page = document.documentElement;
  const canvas = {
    left: Math.max(left, f.left + frame.clientLeft, 0),
    top: Math.max(top, f.top + frame.clientTop, 0),
    right: Math.min(left + container.clientWidth, f.left + frame.clientLeft + frame.clientWidth, page.clientWidth),
    bottom: Math.min(top + container.clientHeight, f.top + frame.clientTop + frame.clientHeight, page.clientHeight),
  };
  if (canvas.right <= canvas.left || canvas.bottom <= canvas.top) return null;

  const covers = [];
  const cover = (r, dx, dy) => {
    const clipped = {
      left: Math.max(r.left + dx, canvas.left), top: Math.max(r.top + dy, canvas.top),
      right: Math.min(r.right + dx, canvas.right), bottom: Math.min(r.bottom + dy, canvas.bottom),
    };
    if (clipped.right > clipped.left && clipped.bottom > clipped.top) covers.push(clipped);
  };
  for (const element of document.querySelectorAll(pageWindowSelector)) {
    if (element.hidden) continue;
    cover(element.getBoundingClientRect(), 0, 0);
  }
  // draw.io's panels are in its own document: their boxes are moved by where the frame's content
  // starts on the page. One hidden by draw.io (display none, here or on a parent) has no box at all.
  for (const element of container.ownerDocument.querySelectorAll(drawioPanelSelector)) {
    if (element.getClientRects().length === 0 || w.getComputedStyle(element).visibility === 'hidden') continue;
    cover(element.getBoundingClientRect(), f.left + frame.clientLeft, f.top + frame.clientTop);
  }
  return { left, top, canvas, covers };
}

// The box the cell's shape covers in the container's coordinates: the state's box, zoom included,
// turned by the cell's rotation (the comment above).
function paintedBoxOf(graph, state) {
  const w = graph.container.ownerDocument.defaultView;
  const unturned = new w.mxRectangle(state.x, state.y, state.width, state.height);
  const rotation = Number(w.mxUtils.getValue(state.style, w.mxConstants.STYLE_ROTATION, 0)) || 0;
  // getBoundingBox answers null for no rotation: the box is then the state's own.
  return w.mxUtils.getBoundingBox(unturned, rotation) || unturned;
}

// The centre of the largest rectangle of the canvas nothing covers and the cell fits in, in the
// container's client coordinates; null when nothing covers the canvas at all, or when no free
// rectangle is at least as big as the cell.
function freeCentreOfTheCanvas(graph, state) {
  const place = theCanvasAndWhatCoversIt(graph);
  if (!place || place.covers.length === 0) return null;
  const shape = paintedBoxOf(graph, state);
  const free = largestFreeRectangle(place.canvas, place.covers, { width: shape.width, height: shape.height });
  if (!free) return null;
  return { x: (free.left + free.right) / 2 - place.left, y: (free.top + free.bottom) / 2 - place.top };
}

// Whether the operator sees the whole cell where it is: its shape inside the canvas that can be seen,
// and no part of it under a window of the page or a panel of draw.io (queue item 33). False when the
// canvas cannot be measured: the copy is then brought into view, as it always was.
function isSeenWhole(graph, state) {
  const place = theCanvasAndWhatCoversIt(graph);
  if (!place) return false;
  const shape = paintedBoxOf(graph, state);
  const box = {
    left: place.left + shape.x - graph.container.scrollLeft,
    top: place.top + shape.y - graph.container.scrollTop,
  };
  box.right = box.left + shape.width;
  box.bottom = box.top + shape.height;
  const { canvas } = place;
  if (box.left < canvas.left || box.top < canvas.top || box.right > canvas.right || box.bottom > canvas.bottom) return false;
  return !place.covers.some((c) =>
    c.left < box.right && c.right > box.left && c.top < box.bottom && c.bottom > box.top);
}

// The largest rectangle inside the canvas that overlaps none of the covers and is at least
// least.width x least.height - the cell's box - or null when there is none. Its edges can only be the
// canvas's own edges or the covers' edges, so every pair of those, across and down, is a candidate:
// a handful of windows makes a few thousand candidates, each checked against a handful of covers.
// For a given top, bottom and left edge, a candidate that overlaps a cover still overlaps it when it
// is made wider, so the widening stops there; one too small for the cell is only passed over, since
// made wider it may hold it. Nothing is lost by looking at these candidates alone: a free rectangle
// that holds the cell, pushed out on each side until it meets a cover or the canvas's edge, is still
// free, still holds it, is no smaller, and is a candidate. Of two as large, the first found - the
// upper, then the left one - so that the answer is the same every time for the same windows.
//
// Exported for the browser tests alone, which call it on boxes of their own
// (DrawioWindowsBrowserTest): it reads nothing but its arguments.
export function largestFreeRectangle(canvas, covers, least) {
  const edges = (a, b, key1, key2) => [...new Set([a, b, ...covers.flatMap((r) => [r[key1], r[key2]])])]
    .sort((x, y) => x - y);
  const xs = edges(canvas.left, canvas.right, 'left', 'right');
  const ys = edges(canvas.top, canvas.bottom, 'top', 'bottom');
  let best = null;
  let bestArea = 0;
  for (let t = 0; t < ys.length; t++) {
    for (let b = t + 1; b < ys.length; b++) {
      for (let l = 0; l < xs.length; l++) {
        for (let r = l + 1; r < xs.length; r++) {
          const candidate = { left: xs[l], top: ys[t], right: xs[r], bottom: ys[b] };
          if (covers.some((c) =>
            c.left < candidate.right && c.right > candidate.left && c.top < candidate.bottom && c.bottom > candidate.top)) {
            break;
          }
          const width = candidate.right - candidate.left;
          const height = candidate.bottom - candidate.top;
          if (width < least.width || height < least.height) continue;
          const area = width * height;
          if (area > bestArea) {
            best = candidate;
            bestArea = area;
          }
        }
      }
    }
  }
  return best;
}

// Every cell of the canvas that carries a mark of ours, as {id, mark}. It is what the page needs to
// answer two questions it cannot answer on its own, and it answers them about the canvas as it is
// NOW rather than as it was at the seeding:
//   - is the selected cell one the drawing marks as a device? The page hears only identifiers from
//     watchSelection, and a note and an orphan device cell look exactly alike to it;
//   - is there already a cell standing for a given device? The seeding writes the device's guid as
//     the cell's id, so the answer is in these identifiers - but only for a canvas the operator has
//     not deleted that cell from, which is why it is asked and not deduced.
// Both are preconditions of the reassociation (see DrawioEditor.ReassociateAsync), and both have to
// be true at the moment of the gesture: a page that decided from what it remembered would refuse,
// or worse allow, on a canvas that has moved on.
//
// IT DOES NOT TOUCH THE MODEL - it reads model.cells and nothing else - so, like selectCell above,
// it puts nothing into the history Ctrl+Z walks. That is the whole reason the reassociation is
// built on a read: the branch has spent three batches on what may go into that history.
//
// The walk is over model.cells and not over the children of the default parent, for the same reason
// the browser tests' own MarkedCellIdsAsync walks it: a cell inside a group is still in the drawing
// and still comes back at the save, and a walk that missed it would let the page say "no cell stands
// for this device" about a device that has one.
export function markedCells() {
  if (!selectionGraph) return null;
  try {
    const model = selectionGraph.getModel();
    const found = [];
    for (const key of Object.keys(model.cells || {})) {
      const cell = model.cells[key];
      const value = cell ? cell.value : null;
      const mark = value && value.getAttribute ? value.getAttribute('wcs') : null;
      if (mark) found.push({ id: String(cell.id), mark: String(mark) });
    }
    return found;
  } catch (err) {
    console.error('drawio-bridge: the marked cells could not be read off the canvas', err);
    // Null and not an empty list, and the difference is the whole point: empty would tell the page
    // "this canvas has no cell of ours", which is a statement about the drawing, and the page would
    // refuse the gesture with a sentence that is false. Null says "I could not look".
    return null;
  }
}

// ROW 63 OF THE PARITY INVENTORY (compito 5): our entries in draw.io's own right-click menu on a
// device - Properties, Linked Storage Cells, Mission Steps, and with the engine running the four
// device commands of the properties panel. Each entry is an action the PANEL already has: it makes
// the device the only cell selected (so the panel is about it) and tells the page which entry was
// chosen (OnDrawioMenuEntry), and the page does the rest through the panel. Nothing here touches the
// model: a selection is not a model edit (see selectCell), so nothing enters the drawing, the save or
// the "Leave site?" counter.
//
// WHICH HOOK, MEASURED in the bundle we host (app.min.js) before anything was written:
//   - EditorUi's constructor installs the graph's popup factory as
//     "null!=this.menus&&(g.popupMenuHandler.factoryMethod=mxUtils.bind(this,function(M,P,ea){
//     this.menus.createPopupMenu(M,P,ea)}))" - it calls createPopupMenu THROUGH ui.menus at every
//     right-click, so replacing that one function on the ui.menus instance changes every menu from
//     then on, without touching the factory itself. It is the way draw.io's own code extends the
//     menu too: a second site in the same bundle does exactly this
//     ("G=t.menus.createPopupMenu;t.menus.createPopupMenu=function(k,p,A){G.apply(this,arguments);...").
//   - Menus.prototype.createPopupMenu=function(a,b,d){a.smartSeparators=!0;a.hideShortcuts=!0;
//     this.addPopupMenuItems(a,b,d)} - a is the mxPopupMenu, b the cell under the pointer (null on
//     the background), d the event. ui=min does not change it: chromeless drops the menubar, not
//     ui.menus (the right-click menu on a device measured in this very page offers To Front, To Back,
//     Cut, Copy, Duplicate, Delete, Lock/Unlock and Edit - see DrawioDelta.cs's own comment).
//   - mxPopupMenu.prototype.addItem=function(a,b,c,d,e,f,g,k) - title, image, funct, parent,
//     iconCls, enabled, active, noHover: an entry is one call, its funct runs on the click.
//   - mxPopupMenuHandler.prototype.selectOnPopup=!0: a right-click on a cell that is not selected
//     selects it before the menu is built, so the menu is normally already about the device alone.
//     Not always - a right-click on one member of a multi-selection keeps the whole selection - which
//     is why the entry selects the device itself before telling the page.
//
// On the instance and not on the prototype, like everything else this bridge changes in draw.io but
// one (keepTheSaveButtonClear, queue item 8, which says why it wraps mxWindow.prototype.fit), and
// put back by unhookThePopupMenu at the same points as the selection (unwatchSelection: a new
// draw.io, attach, detach).
//
// WHAT THE ENTRIES ARE BUILT FROM is said by the page, because it is the only side that knows the
// plant and draw.io builds a menu at once, with no room for a round trip: which cells are devices
// (so a junction's arrow or a drawing-only shape gets none), whether the engine runs (the commands
// are offered only then, as in the panel), and which devices are offline ("Set Ready" in place of
// "Set Off Line", as the panel's own button says). See setDeviceMenu and DrawioEditor.SyncDeviceMenuAsync.
let deviceMenuRunning = false;
let deviceMenuCells = new Set();
let deviceMenuOffline = new Set();
let popupMenus = null;          // the ui.menus whose createPopupMenu we replaced
let popupHadItsOwn = false;
let popupAsDrawioWroteIt = null;
let popupWrapper = null;        // ours, kept to know whether it is still the one installed

/** Row 63: what the page says the right-click menu may offer. Never throws toward the page. */
export function setDeviceMenu(running, deviceCellIds, offlineCellIds) {
  try {
    deviceMenuRunning = running === true;
    deviceMenuCells = new Set(Array.isArray(deviceCellIds) ? deviceCellIds.map(String) : []);
    deviceMenuOffline = new Set(Array.isArray(offlineCellIds) ? offlineCellIds.map(String) : []);
  } catch (err) {
    console.error('drawio-bridge: what the right-click menu may offer could not be taken', err);
    forgetTheDeviceMenu();
  }
}

/**
 * Row 63: what the NEXT right-click menu will be built from - read only, and a copy. It is what the
 * browser tests wait on before they right-click, instead of a time: the page tells this module after
 * its own render, and a right-click made before that would find a menu the page has not yet caught up
 * with.
 */
export function deviceMenuState() {
  return {
    running: deviceMenuRunning,
    devices: Array.from(deviceMenuCells),
    offline: Array.from(deviceMenuOffline),
    hooked: popupWrapper !== null,
  };
}

// Back to "no cell is a device": at attach, before the page has said anything about the new
// draw.io, and at detach. NOT at unwatchSelection: the page says it once after attach and then only
// what changes, and draw.io's 'init' - which re-subscribes - comes after that first word.
function forgetTheDeviceMenu() {
  deviceMenuRunning = false;
  deviceMenuCells = new Set();
  deviceMenuOffline = new Set();
}

function hookThePopupMenu(ui, dotnet) {
  try {
    const menus = ui.menus;
    if (!menus || typeof menus.createPopupMenu !== 'function') {
      console.error('drawio-bridge: draw.io has no right-click menu to add the device entries to');
      return;
    }
    const graph = ui.editor.graph;
    const original = menus.createPopupMenu;
    const wrapper = function (menu, cell) {
      // A wrapper that is no longer the one installed - detached, or another draw.io - adds
      // nothing and only hands on: somebody may have wrapped over it since, and pulling it out of
      // their chain would take their entries away too.
      if (popupWrapper === wrapper) {
        try {
          addDeviceEntries(menu, cell, graph, dotnet, wrapper);
        } catch (err) {
          // draw.io's own entries must still come: our failure costs only ours.
          console.error('drawio-bridge: the device entries could not be added to the right-click menu', err);
        }
      }
      return original.apply(this, arguments);
    };
    popupMenus = menus;
    popupHadItsOwn = Object.prototype.hasOwnProperty.call(menus, 'createPopupMenu');
    popupAsDrawioWroteIt = original;
    popupWrapper = wrapper;
    menus.createPopupMenu = wrapper;
  } catch (err) {
    unhookThePopupMenu();
    console.error('drawio-bridge: the right-click menu could not be extended', err);
  }
}

// Ours come FIRST, above draw.io's own, as the WinForms Editor put its own above Syncfusion's, with
// a separator between the two.
function addDeviceEntries(menu, cell, graph, dotnet, wrapper) {
  if (!cell || cell.id == null) return;
  const id = String(cell.id);
  if (!deviceMenuCells.has(id)) return;

  // The names are LITERALS on the page's side too (DrawioContextMenu.Parse): an entry renamed on
  // one side only is answered "not known" there, not taken for another one.
  const entry = (label, name) => {
    const row = menu.addItem(label, deviceMenuIcon(name), () => chooseDeviceEntry(graph, cell, id, name, dotnet, wrapper));
    const image = row.querySelector('td.mxPopupMenuIcon img');
    if (image) {
      image.alt = '';
      image.setAttribute('aria-hidden', 'true');
      image.setAttribute('role', 'presentation');
    }
    return row;
  };
  entry('Properties', 'properties');
  entry('Linked Storage Cells', 'linked-storage-cells');
  entry('Mission Steps', 'mission-steps');
  // Fix round 2: the panel's own plant-level duplicate, which asks its question in the panel. Not
  // "Duplicate": draw.io's own entry of that name stays in this same menu, and it only copies the
  // shape - on a device loaded from the plant the page refuses that copy (it carries no type).
  entry('Duplicate Device', 'duplicate-device');
  if (deviceMenuRunning) {
    menu.addSeparator();
    entry('Execute Step', 'execute-step');
    entry('Unload LU', 'unload-lu');
    if (deviceMenuOffline.has(id)) entry('Set Ready', 'set-ready');
    else entry('Set Off Line', 'set-off-line');
    entry('Step Done', 'step-done');
  }
  menu.addSeparator();
}

// The page's Razor icons cannot cross into draw.io's iframe. These small outline drawings therefore
// use the same 16 x 16 grammar as DrawioIcon, carried as local data URLs: no package, font, request or
// dependency on draw.io's private icon classes. The command name is the stable key already shared
// with DrawioContextMenu.Parse, so the image cannot drift onto a different action after a relabel.
function deviceMenuIcon(name) {
  const drawings = {
    'properties': '<path d="M3 4h10M3 8h10M3 12h10"/><circle cx="6" cy="4" r="1.5" fill="white"/><circle cx="10" cy="8" r="1.5" fill="white"/><circle cx="7" cy="12" r="1.5" fill="white"/>',
    'linked-storage-cells': '<rect x="1.5" y="2.25" width="5" height="4.5" rx="1"/><rect x="9.5" y="9.25" width="5" height="4.5" rx="1"/><path d="M5.25 9.75 9.75 5.25M4.25 11.25l-1.5 1.5M11.75 4.75l1.5-1.5"/>',
    'mission-steps': '<circle cx="3" cy="12.5" r="1.25"/><circle cx="13" cy="3.5" r="1.25"/><path d="M4.25 12.5h2.5a2 2 0 0 0 2-2v-5a2 2 0 0 1 2-2h1"/><path d="m6 9.75 2.75.75-2.75.75"/>',
    'duplicate-device': '<rect x="5" y="4.5" width="8.5" height="9" rx="1.5"/><path d="M3 11.5h-.5v-9h8.5V3"/>',
    'execute-step': '<path d="M4.5 2.75v10.5L12.75 8z"/>',
    'unload-lu': '<rect x="2" y="2" width="6.5" height="6.5" rx="1"/><path d="M11.5 4.5v8M8.75 9.75l2.75 2.75 2.75-2.75"/>',
    'set-off-line': '<circle cx="8" cy="8" r="6"/><path d="m3.75 12.25 8.5-8.5"/>',
    'set-ready': '<circle cx="8" cy="8" r="6"/><path d="m4.75 8 2.1 2.1 4.4-4.4"/>',
    'step-done': '<rect x="2" y="2" width="12" height="12" rx="2"/><path d="m4.75 8 2.1 2.1 4.4-4.4"/>',
  };
  const drawing = drawings[name];
  if (!drawing) throw new Error(`No device menu icon named '${name}'.`);
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="#4b5563" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">${drawing}</svg>`;
  return `data:image/svg+xml,${encodeURIComponent(svg)}`;
}

function chooseDeviceEntry(graph, cell, id, name, dotnet, wrapper) {
  // The menu was built by a hook that has since been taken off: nobody is listening.
  if (popupWrapper !== wrapper) return;
  try {
    // The device alone, so that the panel is about it - through the selection, which puts nothing
    // into the model or the history Ctrl+Z walks (see selectCell). Not scrolled: it is already
    // under the operator's pointer.
    if (graph.getModel().getCell(id) === cell) graph.setSelectionCell(cell);
  } catch (err) {
    console.error('drawio-bridge: the device could not be selected from the right-click menu', err);
  }
  try {
    const told = dotnet.invokeMethodAsync('OnDrawioMenuEntry', id, name);
    if (told && typeof told.catch === 'function') {
      told.catch((err) => console.error('drawio-bridge: the page did not take the right-click menu entry', err));
    }
  } catch (err) {
    console.error('drawio-bridge: the page could not be told the right-click menu entry', err);
  }
}

function unhookThePopupMenu() {
  try {
    // Put back only if ours is still the one there: somebody who wrapped over it since keeps their
    // wrapper, and ours inside it goes quiet (it checks popupWrapper, cleared below).
    if (popupMenus && popupMenus.createPopupMenu === popupWrapper) {
      if (popupHadItsOwn) popupMenus.createPopupMenu = popupAsDrawioWroteIt;
      else delete popupMenus.createPopupMenu;
    }
  } catch (err) {
    console.error('drawio-bridge: the right-click menu could not be put back', err);
  }
  popupMenus = null;
  popupHadItsOwn = false;
  popupAsDrawioWroteIt = null;
  popupWrapper = null;
}

// ROW 8 OF THE PARITY INVENTORY, THE OWNER'S DECISION OF 27/9: a cut on a selection that holds a
// device of the plant does NOTHING, and the page says so (OnDrawioCutRefused). Measured in the final
// review: a device's cell cut and pasted comes back under a NEW identifier, is not created (a device
// read from the plant carries no type), and the next save deletes the device, its configuration and
// its junctions - the cut lost the device. A device moves by dragging and goes away with Delete.
// A selection with no device in it cuts as draw.io cuts.
//
// WHICH ROADS, MEASURED in the bundle we host (app.min.js) before anything was written - the cut has
// exactly two, and both are closed here:
//   - the ACTION: Actions.init has addAction("cut",function(){...e.copyXml()...c.removeCells(l,!1)
//     ...mxClipboard.cut(c)}); every menu item calls it at the click through the action object
//     (Menus.prototype.addMenuItem: "h.funct(e,k)"), so replacing funct on THAT action object closes
//     the right-click menu's Cut and any other menu's. With this page's ui=min draw.io builds no
//     menubar at all (measured in the page: ui.menubar is null, no visible "Edit" or "Cut"): the
//     right-click menu is the only menu that offers Cut. The keyboard's Ctrl+X reaches the action only
//     when the native clipboard handler is not installed (keyHandler.bindAction(88,!0,"cut")).
//   - the KEYBOARD, when EditorUi.installNativeClipboardHandler is installed (the usual case, and the
//     one this page runs in): it unbinds Ctrl+X from the key handler (bindControlKey(88,null)), puts a
//     hidden contentEditable element under the focus when Ctrl goes down, and the browser's own "cut"
//     event on it calls mxClipboard.copy(graph) and then this.copyCells(element, true) - and copyCells
//     with true is the one that removes the cells (EditorUi.prototype.copyCells:
//     "f?(l.removeCells(n,!1),...)"). Replacing copyCells on the ui INSTANCE closes that road; its
//     other caller, the "copy" event, passes no second argument and is left alone.
// Label editing is not a cut of cells (mxClipboard.cut executes the browser's own cut on the text):
// while a label is being edited everything is handed on.
//
// On the instance, put back by unguardTheCut at the same points as the selection (unwatchSelection),
// never throwing toward the page, and nothing of it enters the model: a refused cut makes no edit.
// A device is what the page says is one (setDeviceMenu - the same set as row 63's menu) OR a cell
// that carries our mark wcs="device" (DrawioDocument.DeviceCellXml writes it on every device cell):
// the page's set is empty until its first word after an attach, and a device just created reaches it
// only at the next render, so the mark closes those windows (confirmation review, M-A). The cells
// themselves, and any cell inside a selected group.
let cutAction = null;           // draw.io's 'cut' action object whose funct we replaced
let cutAsDrawioWroteIt = null;
let cutWrapper = null;
let cutUi = null;               // the ui whose copyCells we replaced
let copyCellsHadItsOwn = false;
let copyCellsAsDrawioWroteIt = null;
let copyCellsWrapper = null;

/** The device cells in the selection, groups included; empty when there is none. */
function devicesInTheSelection(graph) {
  const found = [];
  const model = graph.getModel();
  for (const selected of graph.getSelectionCells() || []) {
    for (const cell of model.getDescendants(selected) || []) {
      const id = cell && cell.id != null ? String(cell.id) : null;
      if (id !== null && (deviceMenuCells.has(id) || carriesTheDeviceMark(cell)) && !found.includes(id)) found.push(id);
    }
  }
  return found;
}

/** The cell's value is our object with wcs="device" - the mark every device cell of this page carries. */
function carriesTheDeviceMark(cell) {
  const value = cell.value;
  return !!value && typeof value === 'object' && typeof value.getAttribute === 'function'
    && value.getAttribute('wcs') === 'device';
}

/**
 * True when this cut must not happen - the page is told why. A failure of this check refuses too:
 * letting a cut through that nobody could judge is the one outcome that can lose a device.
 */
function refusesTheCut(graph, dotnet) {
  try {
    if (graph.isEditing()) return false;
    const devices = devicesInTheSelection(graph);
    if (devices.length === 0) return false;
    try {
      const told = dotnet.invokeMethodAsync('OnDrawioCutRefused', devices);
      if (told && typeof told.catch === 'function') {
        told.catch((err) => console.error('drawio-bridge: the page did not take the refused cut', err));
      }
    } catch (err) {
      console.error('drawio-bridge: the page could not be told of the refused cut', err);
    }
    return true;
  } catch (err) {
    console.error('drawio-bridge: whether the selection holds a device could not be told: the cut is refused', err);
    return true;
  }
}

function guardTheCut(ui, dotnet) {
  try {
    const graph = ui.editor.graph;
    const action = ui.actions && typeof ui.actions.get === 'function' ? ui.actions.get('cut') : null;
    if (action && typeof action.funct === 'function') {
      const original = action.funct;
      const wrapper = function () {
        // A wrapper that is no longer the one installed hands on without judging: see hookThePopupMenu.
        if (cutWrapper === wrapper && refusesTheCut(graph, dotnet)) return undefined;
        return original.apply(this, arguments);
      };
      cutAction = action;
      cutAsDrawioWroteIt = original;
      cutWrapper = wrapper;
      action.funct = wrapper;
    } else {
      console.error('drawio-bridge: draw.io has no cut action to guard');
    }

    if (typeof ui.copyCells === 'function') {
      const originalCopy = ui.copyCells;
      const copyWrapper = function (element, remove) {
        if (remove && copyCellsWrapper === copyWrapper && refusesTheCut(graph, dotnet)) return undefined;
        return originalCopy.apply(this, arguments);
      };
      cutUi = ui;
      copyCellsHadItsOwn = Object.prototype.hasOwnProperty.call(ui, 'copyCells');
      copyCellsAsDrawioWroteIt = originalCopy;
      copyCellsWrapper = copyWrapper;
      ui.copyCells = copyWrapper;
    } else {
      console.error('drawio-bridge: draw.io has no copyCells to guard: the keyboard\'s cut is not guarded');
    }
  } catch (err) {
    unguardTheCut();
    console.error('drawio-bridge: the guard on the cut could not be installed', err);
  }
}

function unguardTheCut() {
  try {
    if (cutAction && cutAction.funct === cutWrapper) cutAction.funct = cutAsDrawioWroteIt;
  } catch (err) {
    console.error('drawio-bridge: the cut action could not be put back', err);
  }
  try {
    if (cutUi && cutUi.copyCells === copyCellsWrapper) {
      if (copyCellsHadItsOwn) cutUi.copyCells = copyCellsAsDrawioWroteIt;
      else delete cutUi.copyCells;
    }
  } catch (err) {
    console.error('drawio-bridge: copyCells could not be put back', err);
  }
  cutAction = null;
  cutAsDrawioWroteIt = null;
  cutWrapper = null;
  cutUi = null;
  copyCellsHadItsOwn = false;
  copyCellsAsDrawioWroteIt = null;
  copyCellsWrapper = null;
}

// Queue item 8: draw.io's own floating windows kept off draw.io's own Save button.
//
// WHAT COVERED IT, MEASURED in the bundle we host (app.min.js) and in a real browser
// (DrawioSaveReachableTest): EditorUi.initMinimalTheme builds the Format window at the top right of
// the frame, "Math.max(10,K.diagramContainer.clientWidth-248),60,240,Math.min(600,D.container.
// clientHeight-10)" - horizontally right under the Save button (button.geEmbedBtn, 9 to 39 px from
// the top) and kept clear of it only by starting 60 px down. Whenever the frame is shorter than the
// window's bottom, draw.io pushes the window UP to stay inside: on every resize of the frame each
// window's own handler (EditorUi.installResizeHandler: setLocation clamps it inside, setSize shrinks
// it), then the theme's own listener calls fit() on the Shapes, Format, Outline, Layers, Chat, Tags,
// Find and Find/Replace windows; the Shapes and Format windows call fit() when they are shown, too.
// Nothing ever moves a window back down. So at 1600x1000 a section that left the frame shorter than
// about 640 px (Operations, or a list at its ceiling: 389-447 px) put the Format window's title bar
// over the button for the rest of the session, and at 1366x768 - a frame of 557-592 px with no
// section open - the button was covered from the start.
//
// WHY HERE AND NOT IN THE PAGE'S LAYOUT, MEASURED TOO: a floor under the drawing (the frame never
// shorter than 662 px, the page scrolling instead) freed the button, but a page that scrolls makes
// the first press on the drawing scroll it - the browser brings the focused frame into view - and
// the device under the hand then moved 44 px further than the mouse at 1366x768. The page keeps its
// layout; draw.io's windows are placed under the button instead.
//
// HOW: mxWindow.prototype.fit in draw.io's window is wrapped - after draw.io's own fit, a window
// that overlaps the button is moved to just under it, and made shorter if the frame has no room for
// it there (never below the window's own minimumSize: then draw.io's placement stands). On a resize
// the theme's fit() comes after the clamp of every window built at startup, so the wrapper sees
// those where draw.io left them; a window built later has its clamp after the wrapper, and the clamp
// finds it already inside the frame and leaves it. The windows draw.io built and placed before
// 'init' are passed through the same rule once, at install. NOT every window is covered: one that is
// missing from the theme's resize list above and is never shown again (draw.io's Freehand window,
// mxLog's, a plugin's) gets at most draw.io's own clamp on a resize, and can end up over the button
// as before. None of them is open in this Editor by default. What is NOT done either: a window the
// operator drags over the button stays there until the next resize - it is theirs - and a window
// made shorter is not made taller again when the frame grows back (draw.io does not do that either;
// the operator resizes it).
//
// On the PROTOTYPE, not on the instances, because windows are born after 'init' too (the Format
// window at a narrow width, Outline, Find...). Put back by unkeepTheSaveButtonClear at the three
// lifecycle points (a new watchSelection, attach, detach), never throwing toward the page, and it
// touches nothing of the model: it moves windows, not cells.
let saveClearPrototype = null;      // the mxWindow.prototype whose fit we replaced
let saveClearFitAsDrawioWroteIt = null;
let saveClearFit = null;

// How far under the button a window is put: its title bar must not touch the button's edge.
const saveButtonClearance = 8;

function keepTheSaveButtonClear(ui, w) {
  // A hold left from an earlier install goes first: two installs must never chain two wrappers.
  unkeepTheSaveButtonClear();
  try {
    const prototype = w.mxWindow && w.mxWindow.prototype;
    if (!prototype || typeof prototype.fit !== 'function') {
      console.error('drawio-bridge: draw.io has no mxWindow.fit to follow: its windows can cover its Save button');
      return;
    }
    const original = prototype.fit;
    const wrapper = function () {
      const result = original.apply(this, arguments);
      // A wrapper that is no longer the one installed hands on without moving anything: see
      // hookThePopupMenu for the same guard.
      if (saveClearFit === wrapper) clearTheSaveButton(this, w);
      return result;
    };
    saveClearPrototype = prototype;
    saveClearFitAsDrawioWroteIt = original;
    saveClearFit = wrapper;
    prototype.fit = wrapper;

    const windows = typeof ui.getWindows === 'function' ? ui.getWindows() : [];
    for (const each of windows || []) {
      if (each && each.window) clearTheSaveButton(each.window, w);
    }
  } catch (err) {
    unkeepTheSaveButtonClear();
    console.error('drawio-bridge: draw.io\'s windows could not be kept off its Save button', err);
  }
}

// Moves one draw.io window from over the Save button to just under it. Never throws: a window that
// cannot be read or moved is left where draw.io put it.
function clearTheSaveButton(win, w) {
  try {
    const div = win && win.div;
    // Not shown, or docked to an edge by draw.io's own dock manager: not ours to move.
    if (!div || div.style.display === 'none' || win.dockState != null) return;
    const button = w.document.querySelector('button.geEmbedBtn');
    if (!button) return;
    const b = button.getBoundingClientRect();
    if (b.width === 0 && b.height === 0) return;
    const r = div.getBoundingClientRect();
    const top = Math.ceil(b.bottom) + saveButtonClearance;
    const overlaps = r.left < b.right && r.right > b.left && r.top < top && r.bottom > b.top;
    if (!overlaps) return;

    // The window's own size as draw.io keeps it (its style); the border is what the box adds.
    const width = parseInt(div.style.width, 10);
    const height = parseInt(div.style.height, 10);
    const border = Math.max(0, Math.round(r.height) - height);
    const room = (w.innerHeight || w.document.documentElement.clientHeight) - top - border;
    const minimum = win.minimumSize ? win.minimumSize.height : 0;
    if (!win.minimized) {
      if (room < minimum) return;
      if (height > room) w.mxWindow.prototype.setSize.call(win, width, room);
    }
    w.mxWindow.prototype.setLocation.call(win, Math.round(r.left), top);
  } catch (err) {
    console.error('drawio-bridge: a draw.io window could not be moved off the Save button', err);
  }
}

function unkeepTheSaveButtonClear() {
  try {
    if (saveClearPrototype && saveClearPrototype.fit === saveClearFit) saveClearPrototype.fit = saveClearFitAsDrawioWroteIt;
  } catch (err) {
    console.error('drawio-bridge: draw.io\'s mxWindow.fit could not be put back', err);
  }
  saveClearPrototype = null;
  saveClearFitAsDrawioWroteIt = null;
  saveClearFit = null;
}

/**
 * Row 63 (compito 5 fix round 3): where a device's cell is drawn NOW - its own box as the model holds
 * it (x, y, width, height: the device's own box, not the plant's rotated envelope) and the rotation
 * its style carries - so that the duplicate goes next to the source the operator sees, moved or not,
 * as the WinForms put it next to the live node's pin point. A READ: nothing of the model is touched.
 * 'grouped' for a cell that is not on the default layer, whose geometry is relative to its group.
 * Never throws toward the page.
 */
export function cellBox(cellId) {
  if (!selectionGraph) return { answer: 'no-graph' };
  try {
    const model = selectionGraph.getModel();
    const cell = model.getCell(String(cellId));
    if (!cell || !cell.geometry) return { answer: 'no-cell' };
    if (!cell.parent || String(cell.parent.id) !== '1') return { answer: 'grouped' };
    const style = selectionGraph.getCellStyle(cell) || {};
    const rotation = parseFloat(style.rotation);
    const g = cell.geometry;
    return { answer: 'ok', x: g.x, y: g.y, width: g.width, height: g.height, rotation: isFinite(rotation) ? rotation : 0 };
  } catch (err) {
    console.error('drawio-bridge: the cell of a device could not be read off the canvas', err);
    return { answer: 'failed' };
  }
}

/**
 * Row 63 (compito 5 fix round 2): draws the device the page has just duplicated ON THE PLANT, with
 * the cell the page hands over - DrawioDocument.DeviceCellXml, the very <object> the next opening
 * draws for it: its guid as id, wcs="device", its name, its style and box - selects it, and centres
 * it in the part of the canvas the page's windows and draw.io's panels leave free
 * (centreCellInTheFreeCanvas, as "go to") when it was born where the operator cannot see it whole.
 *
 * It is a model edit, and deliberately an ordinary one: the "Leave site?" mark goes on as after a
 * device dropped and created (watchChanges counts it), and Ctrl+Z takes the cell away as it takes a
 * dropped one - the device stays on the plant and the next save takes it away by the road of the
 * deletions, exactly as for a drop. watchAdditions reports the birth like any other; the page's
 * creation lets it be, because its id is already a device of the plant.
 *
 * Decoded with the graph window's own mxCodec, without restoring structures (the <mxCell>'s
 * parent="1" is a reference into a document that is not this model's), then added under the default
 * layer inside one model update. Answers 'ok' (drawn and selected - also when bringing it into view
 * failed, which only the browser console says), 'ok-not-selected' (added, but the selection failed:
 * it IS drawn), 'no-graph', 'exists' (a cell with that id is already there: nothing added) or
 * 'failed' (nothing added); never throws toward the page.
 */
export function drawDeviceCell(xml, cellId) {
  if (!selectionGraph || !selectionWindow) return 'no-graph';
  try {
    const w = selectionWindow;
    const graph = selectionGraph;
    const model = graph.getModel();
    if (model.getCell(String(cellId))) return 'exists';
    const doc = w.mxUtils.parseXml(String(xml));
    const cell = new w.mxCodec(doc).decodeCell(doc.documentElement, false);
    if (!cell || String(cell.id) !== String(cellId)) return 'failed';
    const layer = model.getCell('1') || graph.getDefaultParent();
    model.beginUpdate();
    try {
      graph.addCell(cell, layer);
    } finally {
      model.endUpdate();
    }
  } catch (err) {
    // Final review, M1: addCell may have succeeded and a listener then thrown inside endUpdate - the
    // change is executed before its listeners hear of it. A cell that IS in the model is drawn, and
    // saying 'failed' would make the page treat a drawn device as undrawn.
    try {
      if (selectionGraph.getModel().getCell(String(cellId))) {
        console.error('drawio-bridge: the duplicated device was drawn, but drawing it then failed', err);
        return 'ok-not-selected';
      }
    } catch (ignored) {
      // The model cannot even be asked: nothing says the cell is there.
    }
    console.error('drawio-bridge: the duplicated device could not be drawn on the canvas', err);
    return 'failed';
  }
  // The cell IS on the drawing from here on (fix round 4): whatever happens to the selection or the
  // scroll, the answer must not say 'failed', or the page would treat a drawn device as undrawn.
  let drawn;
  try {
    drawn = selectionGraph.getModel().getCell(String(cellId));
    selectionGraph.setSelectionCell(drawn);
  } catch (err) {
    console.error('drawio-bridge: the duplicated device was drawn but could not be selected', err);
    return 'ok-not-selected';
  }
  // Brought into view as "go to" brings a device (queue item 24): centred in the part of the canvas
  // the page's windows leave free. scrollCellToVisible(cell, false), as it was, did not scroll at all
  // for a copy born inside the canvas - and so left it under the window that covered its birthplace,
  // measured with the Devices window at its default place (DrawioWindowsBrowserTest). A try of its
  // own (final review of rows 22-29): the centring reads the page's windows and boxes and moves the
  // view, and a failure there leaves the copy drawn AND selected - its properties on the panel. Inside
  // the selection's try it was answered 'ok-not-selected', and the page told the operator that draw.io
  // could not select a copy it had selected.
  // Only when the copy is not already in sight (queue item 33, owner's decision 1/10): born whole
  // inside the canvas and under nothing, it stays where it is and the view does not move. Since queue
  // item 24 the view had moved at every Duplicate, the copy centred even when it was already in sight.
  try {
    const state = selectionGraph.view.getState(drawn);
    if (!state || !isSeenWhole(selectionGraph, state)) centreCellInTheFreeCanvas(selectionGraph, drawn);
  } catch (err) {
    console.error('drawio-bridge: the duplicated device was drawn and selected, but could not be brought into view', err);
  }
  return 'ok';
}

/**
 * Row 63: brings a part of the properties panel into view for an entry of the right-click menu -
 * the first of the ids that is on the page: a box (<details>) is opened, the element scrolled to and
 * focused. The panel is this page's own markup, not draw.io's. Answers the id reached,
 * 'no-element' or 'failed'; never throws toward the page.
 */
export function revealInPanel(ids) {
  try {
    for (const id of Array.isArray(ids) ? ids : []) {
      const element = document.getElementById(String(id));
      if (!element) continue;
      if (element.tagName === 'DETAILS') element.open = true;
      element.scrollIntoView({ block: 'start', inline: 'nearest' });
      element.focus({ preventScroll: true });
      return String(id);
    }
    return 'no-element';
  } catch (err) {
    console.error('drawio-bridge: the properties panel could not be brought into view', err);
    return 'failed';
  }
}

export function attach(iframe, dotnet, frameUrl) {
  if (listener) window.removeEventListener('message', listener);
  // A second attach starts a second draw.io: the subscription of the first one points at a window
  // that is about to be replaced, and it is taken off here for the same reason as the listener above.
  unwatchSelection();
  unwatchChanges();
  teardownTheDefencesAgainstHiding();
  unkeepTheSaveButtonClear();
  forgetTheChanges();
  // Row 63: nothing is a device until the page says so about THIS attachment.
  forgetTheDeviceMenu();
  // The question at the close of the tab, on the PAGE's window: see askBeforeLeaving. Taken off
  // first, so that a second attach does not leave two.
  if (leavingGuard) window.removeEventListener('beforeunload', leavingGuard);
  leavingGuard = askBeforeLeaving;
  window.addEventListener('beforeunload', leavingGuard);
  configReady = new Promise((resolve) => { resolveConfig = resolve; });
  const handle = async (e) => {
    if (e.source !== iframe.contentWindow) return;      // solo dal NOSTRO iframe
    if (typeof e.data !== 'string' || !e.data.length) return;
    let m;
    try { m = JSON.parse(e.data); } catch { return; }
    if (m.event === 'configure') {
      iframe.contentWindow.postMessage(JSON.stringify({
        action: 'configure',
        config: { libraries: await configReady },
      }), '*');
    }
    else if (m.event === 'init') {
      // The selection is subscribed HERE and not at the seeding: see watchSelection.
      watchSelection(iframe, dotnet);
      dotnet.invokeMethodAsync('OnDrawioReady');
    }
    else if (m.event === 'load') {
      // draw.io has put a document of OURS on the canvas - it posts 'load' only in answer to the
      // 'load' action, which nothing but this bridge's load() sends - and it posts it right after
      // the document is in, in the same task (read out of app.min.js: the embed handler calls
      // setFileData, then posts createLoadMessage("load")). So the seeding's own CHANGE has already
      // been counted, and what is on the canvas now is exactly what the plant holds.
      changesOnThePlant = changesSeen;
      giveTheFocusBackAfterTheLoad(iframe);
    }
    else if (m.event === 'save' || m.event === 'export') {
      // Counted NOW, when draw.io hands the drawing over, and not when the round ends: whatever the
      // operator draws while the save runs is not in this xml, so it is not on the plant however
      // well the round ends.
      const handedOver = changesSeen;
      const handedOverIn = countersGeneration;
      // The drawing stays here, under its number, and the page takes it (queue item 13).
      const drawing = ++drawingsHandedOver;
      handedOverDrawings.set(drawing, m.xml || '');
      dotnet.invokeMethodAsync('OnDrawioSaved', drawing).then(
        (wroteEverything) => {
          // An answer from before a detach or a re-attach: its count is of counters that no longer
          // exist, and the drawing it saved is not the one on the canvas now.
          if (handedOverIn !== countersGeneration) return;
          // Only a round that wrote EVERYTHING puts the mark down - no error, nothing refused, not
          // written or unconfirmed: the page answers true for that alone. And only ever upwards: an
          // older round ending late, or one from before a new seeding, moves nothing.
          if (wroteEverything === true && handedOver > changesOnThePlant) changesOnThePlant = handedOver;
        },
        (err) => {
          console.error('drawio-bridge: the save round did not answer: the drawing is still taken to hold unsaved work', err);
        }).finally(() => handedOverDrawings.delete(drawing));
    }
  };
  listener = (e) => handle(e).catch(report);
  window.addEventListener('message', listener);
  // LA NAVIGAZIONE PARTE DA QUI, e non da un attributo src nel markup della pagina. MISURATO in un
  // browser vero: draw.io manda 'configure' 82 ms dopo l'inizio del caricamento, e chi ascolta dopo
  // quel momento non lo sente piu' - il messaggio non si ripete, e draw.io resta fermo per sempre
  // senza dire niente. Il tempo che passa fra la resa dell'iframe e questa chiamata non e' nostro
  // (e' un giro sul circuito Blazor piu' l'import del modulo, e con una sessione che ha gia' la
  // chiave e' l'intero avvio del circuito): l'unico ordine che si puo' garantire e' ascoltare
  // prima e far partire dopo.
  iframe.src = frameUrl;
}

// La configurazione, quando la pagina l'ha: si veda configReady qui sopra per il perche' puo'
// arrivare dopo che draw.io l'ha chiesta.
export function configure(librariesJson) {
  if (resolveConfig) resolveConfig(JSON.parse(librariesJson));
}

export function load(iframe, xml) {
  // The births remembered are of the drawing that is being replaced, and draw.io numbers its cells
  // per document: an identifier of the old drawing can come round again in the new one, and the
  // guard in theLabelLandsOnItsOwnGesture would then compare a new cell against somebody else's
  // edit. Nothing measured says that collision has ever happened, and this is the cheap way never
  // to have to find out. What it can cost, at the very worst, is a creation still in flight when a
  // seeding arrives: its label then finds no birth remembered, stays significant, and that one
  // shape costs a second press - the same fallback as everywhere else here.
  birthEditOfCell.clear();
  focusBeforeTheLoad = focusedOnThePage(iframe);
  watchForAPressOnTheCanvas(iframe);
  iframe.contentWindow.postMessage(JSON.stringify({ action: 'load', xml, autosave: 0 }), '*');
}

// draw.io takes the keyboard focus into its own frame when it puts a document on the canvas -
// MEASURED on 2026-09-29 (a page reloaded with two of its windows open again): the page's window
// had the focus from 123 ms, draw.io answered 'init' at 757 ms, the page's window lost the focus to
// the iframe at 785 ms and draw.io posted 'load' at 809 ms. A load is not the operator's gesture:
// the focus goes back, at 'load', to the element of the page that had it when the load was sent - a
// window of the page opened again from the last visit (drawio-windows.js gives the one in front the
// focus), the Plant file window after an import. Only if draw.io took it: a focus the operator has
// moved meanwhile stays where it is, and with nothing of the page focused (the page's body) draw.io
// keeps it, as it always has.
//
// A focus moved INTO draw.io's frame does not show from out here: a press on the canvas between the
// load sent and 'load' (queue item 26, likelier on a large plant, whose load takes longer) leaves the
// page's focus on the iframe exactly as draw.io's own taking does, and was given back to the window
// under the operator's hand. So a press inside the frame is listened for from the load sent to
// 'load', on draw.io's own window - same origin, our own host serves it - in the capture phase,
// before draw.io's handlers can stop it: after one, the focus in the frame is the operator's, and
// stays there.
let focusBeforeTheLoad = null;
let pressedInTheFrame = false;
let pressWatch = null;

function focusedOnThePage(iframe) {
  const active = document.activeElement;
  return active && active !== document.body && active !== iframe ? active : null;
}

function giveTheFocusBackAfterTheLoad(iframe) {
  const before = focusBeforeTheLoad;
  const pressed = pressedInTheFrame;
  focusBeforeTheLoad = null;
  unwatchForAPressOnTheCanvas();
  if (before && before.isConnected && !pressed && document.activeElement === iframe) before.focus({ preventScroll: true });
}

// Only when there is a focus to give back: with none, nothing at 'load' looks at a press.
function watchForAPressOnTheCanvas(iframe) {
  unwatchForAPressOnTheCanvas();
  if (!focusBeforeTheLoad) return;
  const w = iframe.contentWindow;
  if (!w) return;
  const onPress = () => { pressedInTheFrame = true; };
  w.addEventListener('pointerdown', onPress, true);
  pressWatch = { w, onPress };
}

function unwatchForAPressOnTheCanvas() {
  pressedInTheFrame = false;
  if (!pressWatch) return;
  const { w, onPress } = pressWatch;
  pressWatch = null;
  // A frame that has navigated away has dropped its listeners with its document already.
  try { w.removeEventListener('pointerdown', onPress, true); } catch { /* gone with its window */ }
}

// The page is about to replace the whole plant - an import that has just been written - and to
// reload: what the canvas holds is superseded by the operator's own confirmed choice, and asking
// "Leave site?" on that reload would offer to stay on a drawing of a plant that no longer exists.
// The count goes down to "on the plant"; an unknowable mark stays unknowable, because nothing here
// has seen what it cannot see.
export function forgetUnsavedWork() {
  changesOnThePlant = changesSeen;
}

// Queue item 14: the numbers of the saves handed over whose answer has not come. The page asks after
// its circuit's connection came back: a call sent while it was down never reached the page and never
// settles, so its number is still here.
export function savesHandedOver() {
  return Array.from(handedOverDrawings.keys());
}

// Final review, C18: the page lets go of the drawings nobody will ask for again - a save the drop took,
// or one whose round is over and whose answer the drop lost. Their calls never settle, so without this
// each one kept a whole copy of the plant's drawing here until the bridge detached. Never throws.
export function letGoOfSaves(drawings) {
  for (const drawing of Array.isArray(drawings) ? drawings : []) handedOverDrawings.delete(drawing);
}

// Queue item 13: the drawing a save handed over under this number, as bytes for Blazor to stream to
// the page. No bytes when it is not held (the round is over, or the page detached the bridge): the
// page reads an empty drawing, the same as one draw.io handed over empty. Never throws.
export function handedOverDrawing(drawing) {
  try {
    return drawingBytes(handedOverDrawings.get(drawing));
  } catch (err) {
    console.error('drawio-bridge: the drawing handed over could not be given to the page', err);
    return new Uint8Array(0);
  }
}

export function requestSave(iframe) {
  iframe.contentWindow.postMessage(JSON.stringify({ action: 'export', format: 'xml' }), '*');
}

export function detach() {
  if (listener) window.removeEventListener('message', listener);
  listener = null;
  // And the subscription to the selection, which is the other thing of ours that lives outside this
  // module - inside draw.io. See unwatchSelection.
  unwatchSelection();
  // The mark and the question at the close, both: a page that has gone must not ask for a drawing
  // it no longer shows.
  unwatchChanges();
  // The guard and the second belt too: nothing of this bridge is left installed on a graph the page
  // has stopped watching. Nor on draw.io's windows (queue item 8).
  teardownTheDefencesAgainstHiding();
  unkeepTheSaveButtonClear();
  if (leavingGuard) window.removeEventListener('beforeunload', leavingGuard);
  leavingGuard = null;
  forgetTheChanges();
  forgetTheDeviceMenu();
  // The drawings still waiting for the page (queue item 13): a page that has gone takes none.
  handedOverDrawings.clear();
  // Queue item 26: no focus is given back to a page that has gone, and no press is listened for.
  focusBeforeTheLoad = null;
  unwatchForAPressOnTheCanvas();
  // Anche la promessa della configurazione, che e' stato: staccato il ponte, quel resolve non ha
  // piu' nessuno che lo ascolti. Oggi non morderebbe - attach ne crea sempre una nuova prima di
  // ascoltare - ma lasciarla qui vorrebbe dire che una configure() arrivata dopo il distacco
  // risolve una promessa di un aggancio che non c'e' piu', in silenzio. Il modulo torna com'era
  // prima del primo attach.
  resolveConfig = null;
  configReady = null;
}

// The graph and its window, for drawio-live.js: the live status is drawn on the same graph the
// selection is read from, and it lives exactly as long. Null before draw.io's init and after detach.
export function liveTarget() {
  return selectionGraph && selectionWindow ? { graph: selectionGraph, window: selectionWindow } : null;
}

// The drawing as draw.io's own 'export' of format 'xml' returns it (MEASURED in app.min.js: that
// branch answers this.getFileData(!0)), but WITHOUT the message: an 'export' would be taken by this
// bridge as a save. The engine switch reads it to know whether the drawing holds unsaved work.
// As bytes, streamed (queue item 13: see handedOverDrawings); no bytes when there is no drawing.
export function currentDrawing() {
  try {
    return drawingBytes(drawioUi ? drawioUi.getFileData(true) : null);
  } catch (err) {
    console.error('drawio-bridge: the drawing could not be read off draw.io', err);
    return new Uint8Array(0);
  }
}

// CODA DEI LAVORI VOCE 7, THE OWNER'S DECISION OF 27/9: a device somebody else deleted from the plant
// leaves the drawing at the first save that finds it gone. The page hands over the cells the save
// ATTRIBUTES to that device - its own cell, and the arrows of the junctions the host took away with
// it (DrawioDelta, goneCellsOf) - and nothing else is touched: no cell is looked for here, and a cell
// the page did not name stays where it is.
//
// THREE THINGS IT DOES NOT DO, and each is the point of a line below:
//   - IT DOES NOT LIGHT "Leave site?". The canvas is being put back in line with the plant, so this is
//     no work of the operator's that a closed tab would lose: echoingThePlant is raised around the
//     one edit, as renameCell does for a name the plant already has (see watchChanges).
//   - IT PUTS NOTHING INTO DRAW.IO'S UNDO HISTORY. A Ctrl+Z that brought the cell back would bring back
//     a device the plant no longer holds - the very orphan this removes, refused at every save. The
//     edit reaches the history through ONE door, read out of the bundle we host (app.min.js):
//       Editor.prototype.createUndoManager: "this.undoListener=function(e,g){b.undoableEditHappened(
//       g.getProperty("edit"))};var d=mxUtils.bind(this,function(e,g){this.undoListener.apply(this,
//       arguments)});a.getModel().addListener(mxEvent.UNDO,d);a.getView().addListener(mxEvent.UNDO,d)"
//     - the listener on the model looks the editor's undoListener up AT EVERY EDIT, so putting a no-op
//     in its place for this one edit keeps it out, and putting draw.io's own back in the finally leaves
//     nothing installed. Not a history rewritten after the fact: the edit never enters it, and nothing
//     the operator did before or after it is touched. What is NOT held back is what the operator did
//     to those cells BEFORE this save, in the entries of his own gestures - KNOWN RESIDUALS, read from
//     mxGraph's source and not measured: undoing a move of a cell that is no longer in the model changes
//     nothing on the canvas; undoing the DROP that made a device in this session and then redoing it
//     would put that cell back; and undoing an earlier DELETION of a line that hung from the removed
//     device can put that line back with its end still pointing at the removed device - a line whose
//     terminal is a cell no longer in the model. After the redone drop the next save refuses the cell
//     as a device the plant does not know; what it makes of the restored line was not measured. Either
//     way the canvas shows something the plant does not hold until the operator deletes it.
//   - IT DOES NOT TAKE THE OPERATOR'S OWN LINES WITH IT. graph.cellsRemoved and not graph.removeCells:
//     the latter also removes every connected edge the view has no state for, and with includeEdges
//     every connected edge at all. cellsRemoved takes exactly the cells named and DISCONNECTS any other
//     line hanging from them (its end stays where the device was), so the model never points at a cell
//     that is gone.
//
// The answer is read off the MODEL afterwards, not off the call: 'ok' when none of the cells named is
// in it any more - one the operator had already deleted counts as gone - 'failed' when one still is
// (a listener that threw inside endUpdate runs after the change is executed, so a throw alone does not
// mean the cells are there), 'no-graph' before draw.io's init or after detach. Never throws toward
// the page.
export function removeDeletedDeviceCells(cellIds) {
  if (!selectionGraph) return 'no-graph';
  const ids = Array.isArray(cellIds) ? cellIds.map(String) : [];
  const graph = selectionGraph;
  try {
    const model = graph.getModel();
    const cells = [];
    for (const id of ids) {
      const cell = model.getCell(id);
      if (cell && !cells.includes(cell)) cells.push(cell);
    }
    if (cells.length > 0) {
      const editor = drawioUi ? drawioUi.editor : null;
      const recordsTheUndo = editor && typeof editor.undoListener === 'function' ? editor.undoListener : null;
      if (!recordsTheUndo) {
        console.error('drawio-bridge: draw.io has no undoListener to hold back: the removal of a device deleted on the host goes into the undo history');
      }
      echoingThePlant = true;
      if (recordsTheUndo) editor.undoListener = function () {};
      try {
        model.beginUpdate();
        try {
          graph.cellsRemoved(cells);
        } finally {
          model.endUpdate();
        }
      } finally {
        if (recordsTheUndo) editor.undoListener = recordsTheUndo;
        echoingThePlant = false;
      }
    }
  } catch (err) {
    console.error('drawio-bridge: the cells of a device deleted on the host could not be removed from the canvas', err);
  }
  try {
    const model = graph.getModel();
    return ids.every((id) => !model.getCell(id)) ? 'ok' : 'failed';
  } catch (err) {
    console.error('drawio-bridge: whether the cells of a device deleted on the host are gone could not be read', err);
    return 'failed';
  }
}
