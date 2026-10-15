// The floating windows of the draw.io page (spec 2026-09-29): moving by the title bar, resizing by
// the grip, bringing the one touched last to the front, shutting the one that has the focus with
// Esc, keeping every window inside the viewport and below the page's bar and the lines under it
// (the engine's refusal, the row of outcomes), and remembering where each one was and in which order they were stacked - in
// THIS browser only (localStorage), a per-viewer convenience: it never reaches the host and it is
// not shared.
//
// Both gestures use pointer capture: without it the pointer, crossing draw.io's iframe, would be
// the iframe's, and the window would stop half-way with the button still down.
const placePrefix = 'wcs.drawio.window.';
const openKey = 'wcs.drawio.windows.open';
const minWidth = 280;   // the title bar, a filter and two rows of a table
const minHeight = 160;
// The strip of section buttons the windows belong to: the column that holds it is what is watched
// for the floor to move (watchStrip).
const stripSelector = '.drawio-sections';
// The row of outcomes under the strip holds the windows of its reports (their "Details"): they are
// windows of the same column, with the same floor.
const rowSelector = '.drawio-notices';
// The floor of clamp(): no window ever goes above the top of the drawing's area, the element of the
// column right after the page's controls - the strip with its buttons and the Rulers switch, the
// engine's refusal and the row of outcomes under it - so no window ever covers one of them (owner's
// decision, 29/9). 0 where there is no such element.
const floorSelector = ':scope > .drawio-body';
const attached = new Map(); // element -> { id, off }
// The windows stack from 100 up. The overlays of the page (.plant-context-menu, .plant-import-dialog)
// sit at fixed z-index 900-951 and must stay above every window, so the counter never gets near
// them: at frontLimit the windows are renumbered from 100, in their current order. (The import's
// confirmation is inside the Plant file window: the stylesheet lifts that window to 950 !important
// while it holds one, over this inline value - see .drawio-window:has(.plant-import-dialog-overlay).
// The order below is the module's own, the inline values: that lift is not part of it.)
const frontBase = 100;
const frontLimit = 800;
let front = frontBase;
let frontElement = null;
// While the page opens again the windows that were open at the end of the last visit: their ids in
// the order they were stacked then, the one in front last, and those shown so far. show() stacks
// them in that order instead of in the order they happen to be shown (see openedLastTime).
let reopening = null; // { order: [id], shown: Set<id> }

function read(key) {
  try {
    const raw = localStorage.getItem(key);
    return raw === null ? null : JSON.parse(raw);
  } catch {
    return null; // blocked storage or a value this version cannot read: as if nothing were stored
  }
}

function write(key, value) {
  try { localStorage.setItem(key, JSON.stringify(value)); } catch { /* a convenience only */ }
}

function isPlace(p) {
  return p !== null && typeof p === 'object'
    && ['left', 'top', 'width', 'height'].every((k) => Number.isFinite(p[k]));
}

function apply(element, p) {
  element.style.left = `${p.left}px`;
  element.style.top = `${p.top}px`;
  element.style.width = `${p.width}px`;
  element.style.height = `${p.height}px`;
}

function numberOr(value, fallback) {
  return Number.isFinite(value) && value > 0 ? value : fallback;
}

function current(element) {
  const r = element.getBoundingClientRect();
  return { left: r.left, top: r.top, width: r.width, height: r.height };
}

// Where a window's top may go no higher: the top of the drawing's area, which moves down whenever
// something above it grows (the strip wrapping to a second line, the engine's refusal, a line of the
// row of outcomes) and up and down with the page when the column is taller than the browser and the
// page scrolls.
function floorOf(element) {
  const column = element.closest(`${stripSelector}, ${rowSelector}`)?.parentElement;
  const drawing = column?.querySelector(floorSelector);
  return drawing ? Math.max(0, drawing.getBoundingClientRect().top) : 0;
}

// A content-heavy window can declare a larger useful minimum in CSS. Reading it here keeps pointer
// resizing, stored places and viewport clamping in agreement with what the browser actually draws.
// "none" and "auto" parse as NaN and therefore keep the page-wide fallback.
function minimumOf(element) {
  const style = getComputedStyle(element);
  return {
    width: Number.parseFloat(style.minWidth) || minWidth,
    height: Number.parseFloat(style.minHeight) || minHeight,
  };
}

// Inside the viewport and below the floor, title bar included: a window bigger than the room shrinks
// to it, one partly outside comes back in. The window's minimum size wins over the room: below that
// size it overflows the viewport (and its X may be out of reach - Esc still closes it) rather than
// get unusable - but never goes above the floor.
function clamp(element, p, floor) {
  const minimum = minimumOf(element);
  const width = Math.max(minimum.width, Math.min(p.width, window.innerWidth));
  const height = Math.max(minimum.height, Math.min(p.height, window.innerHeight - floor));
  const left = Math.min(Math.max(0, p.left), Math.max(0, window.innerWidth - width));
  const top = Math.min(Math.max(floor, p.top), Math.max(floor, window.innerHeight - height));
  return { left, top, width, height };
}

function bringBackInside() {
  forgetDisconnected();
  for (const [element, entry] of attached) {
    if (element.hidden || entry.popup) continue;
    if (entry.options.autoSize) sizeToContent(element, entry.profile);
    else apply(element, clamp(element, current(element), floorOf(element)));
  }
}

// The floor moves without the viewport changing, and no event says so. A ResizeObserver on the
// drawing's area alone would miss it (it MOVES when something above it grows, and shrinks only while
// it has height to give), and one on the column misses it too (.drawio-shell is 100vh: what grows in
// it takes its height from the drawing, not from the column). So every element of the column is
// observed - whatever grows, shrinks or wraps there changes the size of one of them.
//
// The MutationObserver on the column adds the elements the page puts in later (a line rendered only
// when there is something to say: the engine's refusal, "Loading the plant...") to the observed ones,
// and re-checks at once when one comes or goes. No test turns red without it, and that is expected: a
// line inserted above the drawing also shrinks .drawio-body, whose own entry already reports the
// move. It matters in two cases the ResizeObserver cannot see: a line inserted while .drawio-body has
// no height left to give (the column taller than the browser - nothing observed changes size, the
// floor just moves down), and the later growth of an element that was not there when the observers
// started (without observeAll() it would be observed by nobody).
//
// The page itself scrolls when the column is taller than the browser (.drawio-shell has no overflow
// of its own, so the document is what scrolls): that moves the floor with no size changing either,
// and the passive 'scroll' listener on the window, below, covers it.
let watched = null; // { strip, sizes: ResizeObserver, children: MutationObserver }

function watchStrip(strip) {
  if (!strip || !strip.parentElement || watched?.strip === strip) return;
  unwatchStrip();
  const column = strip.parentElement;
  const sizes = new ResizeObserver(bringBackInside);
  const observeAll = () => {
    sizes.disconnect(); // an element the page removed must not stay observed
    for (const child of column.children) sizes.observe(child);
  };
  const children = new MutationObserver(() => { observeAll(); bringBackInside(); });
  observeAll();
  children.observe(column, { childList: true });
  watched = { strip, sizes, children };
}

function unwatchStrip() {
  if (!watched) return;
  watched.sizes.disconnect();
  watched.children.disconnect();
  watched = null;
}

function zOf(element) {
  return Number.parseInt(element.style.zIndex, 10) || 0;
}

function renumber() {
  const order = [...attached.keys()].sort((a, b) => zOf(a) - zOf(b));
  order.forEach((element, i) => { element.style.zIndex = String(frontBase + i); });
  front = frontBase + order.length - 1;
}

// force: raise it even if it is the one raised last - the reopening restacks windows in an order
// of its own, and the one raised last may no longer be on top of them.
function toFront(element, force = false) {
  if (element === frontElement && !force) return; // touching it again must not raise the counter
  if (front + 1 >= frontLimit) renumber();
  front += 1;
  frontElement = element;
  element.style.zIndex = String(front);
  rememberOrder();
}

function elementOf(id) {
  for (const [element, entry] of attached) {
    if (entry.id === id && element.isConnected) return element;
  }
  return null;
}

// The open window in front of the other open ones, by the module's own order (the inline z-index);
// of two with the same value, the later in the page, as the browser stacks them.
function frontOpen() {
  let best = null;
  for (const element of attached.keys()) {
    if (element.hidden || !element.isConnected) continue;
    const later = best && (best.compareDocumentPosition(element) & Node.DOCUMENT_POSITION_FOLLOWING);
    if (!best || zOf(element) > zOf(best) || (zOf(element) === zOf(best) && later)) best = element;
  }
  return best;
}

// The memory of the open windows, in the order they are stacked now, the one in front last, so that
// the next visit stacks them the same way (openedLastTime). An id whose window has no place in the
// stack yet (being opened: the page writes the memory before the window is shown) goes last - it is
// about to be put in front. Not while the reopening is under way: the windows not shown yet would
// sort as if they were behind.
function stackOrder(ids) {
  const z = (id) => {
    const element = elementOf(id);
    return element && element.style.zIndex !== '' ? zOf(element) : Number.POSITIVE_INFINITY;
  };
  return ids.map((id, i) => ({ id, i, z: z(id) }))
    .sort((a, b) => (a.z === b.z ? a.i - b.i : a.z - b.z))
    .map((entry) => entry.id);
}

function rememberOrder() {
  if (reopening) return;
  const ids = read(openKey);
  if (Array.isArray(ids)) write(openKey, stackOrder(ids.filter((id) => typeof id === 'string')));
}

// An element the page removed without calling detach (or before it could) must not stay in the map.
function forget(element) {
  const entry = attached.get(element);
  if (!entry) return;
  if (entry.popup) returnToEditor(element, true, false);
  entry.off();
  attached.delete(element);
  if (frontElement === element) frontElement = null;
  if (attached.size === 0) unwatchStrip();
}

function forgetDisconnected() {
  for (const [element, entry] of [...attached]) {
    if (element.isConnected) continue;
    // Not a repeated test: returnToEditor() puts a detached element back at its placeholder, and
    // one whose placeholder is still in the page is connected again - it stays known.
    if (entry.popup) returnToEditor(element, false, false);
    if (!element.isConnected) forget(element);
  }
}

function remember(element) {
  const entry = attached.get(element);
  if (entry && !entry.popup) write(placePrefix + entry.id, current(element));
}

// A field that has its own use for Esc: a text box clears itself, a list closes, an editor gives up.
function isEditable(target) {
  return target instanceof Element
    && (target.matches('input, textarea, select') || target.isContentEditable);
}

function drag(element, handle, move) {
  if (!handle) return;
  handle.addEventListener('pointerdown', (down) => {
    if (down.button !== 0 || down.target.closest('button') || attached.get(element)?.popup) return;
    down.preventDefault();
    handle.setPointerCapture(down.pointerId);
    const start = { x: down.clientX, y: down.clientY, place: current(element) };
    const onMove = (e) => apply(element, clamp(element, move(start.place, e.clientX - start.x, e.clientY - start.y), floorOf(element)));
    let ended = false;
    const onEnd = () => {
      if (ended) return; // pointerup and the lostpointercapture that follows it both arrive here
      ended = true;
      handle.removeEventListener('pointermove', onMove);
      handle.removeEventListener('pointerup', onEnd);
      handle.removeEventListener('pointercancel', onEnd);
      handle.removeEventListener('lostpointercapture', onEnd);
      remember(element);
    };
    handle.addEventListener('pointermove', onMove);
    handle.addEventListener('pointerup', onEnd);
    handle.addEventListener('pointercancel', onEnd);
    // A capture lost with no pointerup (the element removed, the browser taking the pointer back)
    // must not leave onMove attached.
    handle.addEventListener('lostpointercapture', onEnd);
  });
}

// Blazor hears every event of the page on the editor's document (one listener per event name, on
// document): an event of a detached window happens in the separate window's document and never gets
// there. The bridge hands Blazor a copy of it, dispatched on the editor's document, that names the
// control in the separate window as its target and the path up to the window as its composed path -
// what Blazor reads to find its handlers. Nothing is moved and nothing of the original is cancelled:
// the browser does in the separate window what it always does (typing, Backspace, paste, Tab, Enter
// and Space on a button, the arrows in a select), the scroll offsets and the open pickers stay as they
// are, and what Blazor asks for (preventDefault) is applied to the original. The names are the events
// the windows' components listen to (@onclick, @ondblclick, @oninput, @onchange, @onkeydown,
// @onfocusin, InputFile's change...), on the bubbling side, after the window's own listeners.
const bridgedEventNames = ['click', 'dblclick', 'input', 'change', 'keydown', 'keyup', 'focusin', 'focusout', 'submit'];

// While an import waits for its confirmation the editor covers itself with the confirmation's
// backdrop (ImportExport.razor, .plant-import-dialog-overlay), and nothing else of the page may be
// used. A detached window is not under that backdrop: these are the events of the separate window
// that are taken away from it meanwhile - every press and every key - and a cover of its own says why.
const blockedEventNames = ['pointerdown', 'mousedown', 'mouseup', 'click', 'dblclick', 'auxclick',
  'contextmenu', 'keydown', 'keypress', 'keyup', 'beforeinput', 'paste', 'drop'];
const blockerText = 'An import is waiting for confirmation in the editor window. Confirm or cancel it there first.';

function popupStatus(element, text) {
  const status = element.querySelector('.drawio-window-popout-status');
  if (status) status.textContent = text;
}

function setPopoutButton(element, detached) {
  const button = element.querySelector('.drawio-window-popout');
  if (!button) return;
  const title = button.dataset.windowTitle || element.getAttribute('aria-label') || 'window';
  const label = detached ? `Return ${title} to the editor` : `Open ${title} in a separate window`;
  button.setAttribute('aria-label', label);
  button.setAttribute('title', label);
  button.setAttribute('aria-pressed', detached ? 'true' : 'false');
}

// Whether an import is waiting for its confirmation on the editor: its backdrop is on screen there.
function importWaiting() {
  const overlay = document.querySelector('.plant-import-dialog-overlay');
  return !!overlay && overlay.checkVisibility();
}

function showBlocker(entry, blocked) {
  const blocker = entry.popupBlocker;
  if (blocker && blocker.hidden === blocked) blocker.hidden = !blocked;
}

// The copy of an event of the separate window for the editor's document, in the editor's realm: the
// same kind of event with the same fields (the original is its own init dictionary), then the target
// and the path, up to the window's element. The target is the first node of the original's path
// that belongs to the editor's realm: Blazor tests it against its own Element, and the nodes it
// created keep the editor's realm after the move, but markup parsed into the separate window - the
// <path> of an icon - is the separate window's (measured: a click on the "?"'s icon was dropped).
// Blazor puts no handler on such markup, so the element that holds it is the target it would find.
function eventForTheEditor(element, target, event) {
  const Kind = window[event.constructor?.name];
  let copy;
  try {
    copy = new (typeof Kind === 'function' && Kind.prototype instanceof Event ? Kind : Event)(event.type, event);
  } catch {
    copy = new Event(event.type, { bubbles: event.bubbles, cancelable: event.cancelable, composed: event.composed });
  }
  const path = [];
  for (let node = target; node; node = node.parentNode) {
    path.push(node);
    if (node === element) break;
  }
  Object.defineProperty(copy, 'target', { value: target });
  Object.defineProperty(copy, 'srcElement', { value: target });
  // Blazor shifts the array it gets: a fresh one at every call.
  Object.defineProperty(copy, 'composedPath', { value: () => path.slice() });
  return copy;
}

// The first node from the event's target up to the window's element that belongs to the editor's
// realm - Element here is deliberately the editor realm's constructor - or null.
function editorTargetOf(element, node) {
  for (let candidate = node; candidate; candidate = candidate.parentNode) {
    if (candidate instanceof Element) return element.contains(candidate) ? candidate : null;
    if (candidate === element) return null;
  }
  return null;
}

function bridgePopupEvent(element, event) {
  const entry = attached.get(element);
  if (!entry?.popup) return;
  const target = editorTargetOf(element, event.target);
  if (!target) return;
  const copy = eventForTheEditor(element, target, event);
  document.dispatchEvent(copy);
  if (copy.defaultPrevented && event.cancelable) event.preventDefault();
}

// Blazor hands an ElementReference to JavaScript as the element of the EDITOR's document that carries
// its capture attribute (document.querySelector('[_bl_...]')). A detached window's elements are in
// the separate window's document, so every call that names one - FocusAsync, the module's own
// sizeToContent/show/shut, a "?"'s watch - would get null, and a FocusAsync would throw. The editor's
// document answers those lookups for the detached windows too: only a selector of that one form, only
// when the editor's own document has no such element.
const captureSelector = /^\[_bl_[^\]]+\]$/;
const nativeQuerySelector = Document.prototype.querySelector;

function findDetached(selector) {
  for (const [element, entry] of attached) {
    if (!entry.popup) continue;
    if (element.matches(selector)) return element;
    const inside = element.querySelector(selector);
    if (inside) return inside;
  }
  return null;
}

document.querySelector = function querySelector(selector) {
  const found = nativeQuerySelector.call(this, selector);
  if (found || typeof selector !== 'string' || !captureSelector.test(selector)) return found;
  return findDetached(selector);
};

// A file chosen in the detached Plant file window: InputFile reads the browser-owned FileList
// asynchronously, and the irreversible confirmation of the import belongs to the editor. The window
// comes home first, then the unchanged input gets its change again there. Heard in the capture phase,
// before InputFile's own listener on the input.
function bringTheFileHome(element, event) {
  const entry = attached.get(element);
  const target = event.target;
  if (!entry?.popup || !(target instanceof Element) || !element.contains(target)
    || !target.matches('input[type="file"]')) return; // InputFile's input is Blazor's: the editor's realm
  event.preventDefault();
  event.stopImmediatePropagation();
  returnToEditor(element, true, true);
  window.queueMicrotask(() => target.dispatchEvent(new Event('change', { bubbles: true, cancelable: true })));
}

function copyEditorStyles(popup) {
  const doc = popup.document;
  doc.documentElement.lang = document.documentElement.lang || 'en';
  doc.documentElement.className = document.documentElement.className;
  const base = doc.createElement('base');
  base.href = document.baseURI;
  doc.head.appendChild(base);
  for (const source of document.querySelectorAll('link[rel="stylesheet"], style')) {
    const copy = source.cloneNode(true);
    if (copy instanceof popup.HTMLLinkElement) copy.href = source.href;
    doc.head.appendChild(copy);
  }
}

function preferredPopupSize(element, entry) {
  const box = current(element);
  const profile = entry.profile;
  return {
    width: Math.round(numberOr(profile?.width, box.width)),
    height: Math.round(numberOr(profile?.height, box.height)),
  };
}

export function togglePopout(element) {
  const entry = attached.get(element);
  if (!entry) return false;
  if (entry.popup) {
    returnToEditor(element, true, true);
    return true;
  }

  // Under a pending import's backdrop nothing of the editor may be used, and a window taken out of
  // the editor would leave the backdrop's rule behind - the Plant file window would take the
  // confirmation itself away.
  if (importWaiting()) {
    popupStatus(element, 'An import is waiting for confirmation. Confirm or cancel it before opening a separate window.');
    return false;
  }
  popupStatus(element, '');
  const size = preferredPopupSize(element, entry);
  const features = `popup=yes,width=${Math.max(minWidth, size.width)},height=${Math.max(minHeight, size.height)}`;
  let popup = null;
  try { popup = window.open('', `wcs-${entry.id}`, features); } catch { /* browser policy */ }
  if (!popup) {
    popupStatus(element, 'The browser blocked the separate window. Allow pop-ups for this site and try again.');
    return false;
  }

  try {
    popup.document.open();
    popup.document.write('<!doctype html><html><head><meta charset="utf-8"></head><body></body></html>');
    popup.document.close();
    copyEditorStyles(popup);
    popup.document.title = element.getAttribute('aria-label') || 'WCS window';
    popup.document.body.className = 'drawio-popout-body';
    const host = popup.document.createElement('main');
    host.className = 'drawio-popout-host';
    popup.document.body.appendChild(host);
    const blocker = popup.document.createElement('div');
    blocker.className = 'drawio-popout-blocker';
    blocker.setAttribute('role', 'alert');
    blocker.textContent = blockerText;
    blocker.hidden = true;
    popup.document.body.appendChild(blocker);

    entry.popup = popup;
    entry.popupHost = host;
    entry.popupBlocker = blocker;
    // The window was opened at the size of this profile: sizeToContent() resizes it again only for
    // another one (a tab of Operations chosen), never because the page rendered.
    entry.popupSizedFor = JSON.stringify(entry.profile ?? null);
    entry.placeholder = document.createComment(`return ${entry.id}`);
    entry.homeParent = element.parentNode;
    entry.homeParent.insertBefore(entry.placeholder, element);
    entry.savedStyle = element.getAttribute('style');
    entry.savedPlace = current(element);
    element.classList.add('drawio-window-detached');
    host.appendChild(element);
    setPopoutButton(element, true);

    const onEvent = (event) => bridgePopupEvent(element, event);
    const onFile = (event) => bringTheFileHome(element, event);
    // Heard on the separate window itself, in the capture phase: before anything of its document.
    const onBlocked = (event) => {
      const blocked = importWaiting();
      showBlocker(entry, blocked);
      if (!blocked) return;
      event.preventDefault();
      event.stopImmediatePropagation();
    };
    bridgedEventNames.forEach((name) => popup.document.addEventListener(name, onEvent));
    popup.document.addEventListener('change', onFile, true);
    blockedEventNames.forEach((name) => popup.addEventListener(name, onBlocked, true));
    const onPageHide = () => returnToEditor(element, false, false);
    popup.addEventListener('pagehide', onPageHide, { once: true });
    entry.childOff = () => {
      try {
        bridgedEventNames.forEach((name) => popup.document.removeEventListener(name, onEvent));
        popup.document.removeEventListener('change', onFile, true);
      } catch { /* closed */ }
      try { blockedEventNames.forEach((name) => popup.removeEventListener(name, onBlocked, true)); } catch { /* closed */ }
      try { popup.removeEventListener('pagehide', onPageHide); } catch { /* closed */ }
    };
    // The cover follows the editor's backdrop: on when an import starts waiting, off when it is
    // confirmed or cancelled. No event tells this window so; the poll that notices the separate
    // window shut by the browser looks at it too. The events above do not wait for it.
    showBlocker(entry, importWaiting());
    entry.popupPoll = window.setInterval(() => {
      if (entry.popup?.closed) returnToEditor(element, false, false);
      else showBlocker(entry, importWaiting());
    }, 250);
    try { popup.focus(); } catch { /* browser may decline focus */ }
    return true;
  } catch {
    if (entry.popup) returnToEditor(element, false, false);
    try { popup.close(); } catch { /* already unavailable */ }
    popupStatus(element, 'The separate window could not be prepared. Keep working here and try again.');
    return false;
  }
}

function returnToEditor(element, closePopup, focusWindow) {
  const entry = attached.get(element);
  if (!entry?.popup) return;
  const popup = entry.popup;
  entry.popup = null;
  entry.childOff?.();
  entry.childOff = null;
  if (entry.popupPoll) window.clearInterval(entry.popupPoll);
  entry.popupPoll = null;
  element.classList.remove('drawio-window-detached');
  if (entry.savedStyle === null) element.removeAttribute('style');
  else element.setAttribute('style', entry.savedStyle);
  const parent = entry.placeholder?.parentNode || entry.homeParent;
  if (parent) parent.insertBefore(element, entry.placeholder || null);
  entry.placeholder?.remove();
  entry.placeholder = null;
  entry.popupHost = null;
  entry.popupBlocker = null;
  entry.popupSizedFor = null;
  setPopoutButton(element, false);
  if (closePopup && !popup.closed) {
    try { popup.close(); } catch { /* already closing */ }
  }
  if (!element.hidden) {
    if (entry.options.autoSize) sizeToContent(element, entry.profile);
    else apply(element, clamp(element, entry.savedPlace || current(element), floorOf(element)));
    if (focusWindow) element.focus({ preventScroll: true });
  }
}

export function attach(element, id, place, options = {}) {
  forgetDisconnected();
  if (!element || attached.has(element)) return;
  const onFocus = () => { if (!attached.get(element)?.popup) toFront(element); };
  // Esc shuts the window that has the focus, here and not in Blazor: a keydown handler on the
  // window's root would send every keystroke of every window over the circuit. It goes through the
  // window's own X, so it reaches the same close the X does. A field that has its own use for Esc
  // keeps it, and so does a key some content of the window has already handled.
  const onKey = (e) => {
    if (e.key !== 'Escape' || e.defaultPrevented || element.hidden || isEditable(e.target)) return;
    const close = element.querySelector('.drawio-window-close');
    if (!close) return;
    e.preventDefault();
    close.click();
  };
  element.addEventListener('pointerdown', onFocus, true);
  element.addEventListener('focusin', onFocus);
  element.addEventListener('keydown', onKey);
  drag(element, element.querySelector('.drawio-window-title'),
    (p, dx, dy) => ({ ...p, left: p.left + dx, top: p.top + dy }));
  // Resizing never moves the window: the size is capped by the room left to the right and below the
  // window's own corner, so clamp() has nothing to push back (the corner is already below the floor).
  if (options.resizable !== false) {
    drag(element, element.querySelector('.drawio-window-grip'), (p, dx, dy) => {
      const minimum = minimumOf(element);
      return {
        ...p,
        width: Math.max(minimum.width, Math.min(p.width + dx, window.innerWidth - p.left)),
        height: Math.max(minimum.height, Math.min(p.height + dy, window.innerHeight - p.top)),
      };
    });
  }
  const normalizedOptions = { resizable: options.resizable !== false, autoSize: options.autoSize === true };
  const popout = element.querySelector('.drawio-window-popout');
  const close = element.querySelector('.drawio-window-close');
  const onPopout = (event) => {
    event.preventDefault();
    event.stopPropagation();
    togglePopout(element);
  };
  const onDetachedClose = (event) => {
    const entry = attached.get(element);
    if (!entry?.popup) return;
    event.preventDefault();
    event.stopImmediatePropagation();
    returnToEditor(element, true, false);
    // The original event's path still belongs to the child document. A fresh click in the editor
    // reaches Blazor's delegated handler and performs the same close as the ordinary in-page X.
    window.queueMicrotask(() => close.click());
  };
  popout?.addEventListener('click', onPopout);
  close?.addEventListener('click', onDetachedClose);
  attached.set(element, { id, options: normalizedOptions, profile: null, popup: null, off: () => {
    element.removeEventListener('pointerdown', onFocus, true);
    element.removeEventListener('focusin', onFocus);
    element.removeEventListener('keydown', onKey);
    popout?.removeEventListener('click', onPopout);
    close?.removeEventListener('click', onDetachedClose);
  } });
  watchStrip(element.closest(stripSelector));
  const stored = read(placePrefix + id);
  const starting = isPlace(stored)
    ? (normalizedOptions.autoSize ? { ...place, left: stored.left, top: stored.top } : stored)
    : place;
  apply(element, clamp(element, starting, floorOf(element)));
  if (normalizedOptions.autoSize && !element.hidden) sizeToContent(element, null);
}

// Measure the live body at a useful width, then keep only as much height as it needs up to the
// profile's ceiling. This deliberately ignores a stored manual size while preserving position.
export function sizeToContent(element, profile) {
  const entry = attached.get(element);
  if (!entry || element.hidden) return;
  entry.profile = profile ?? entry.profile;
  if (entry.popup && !entry.popup.closed) {
    // The separate window's size is the operator's once it is open: resized again for another
    // profile only (a tab of Operations chosen), not at every render of the page - a live frame
    // would otherwise undo a window enlarged by hand.
    const sizedFor = JSON.stringify(entry.profile ?? null);
    if (entry.popupSizedFor === sizedFor) return;
    entry.popupSizedFor = sizedFor;
    const chosen = preferredPopupSize(element, entry);
    try {
      const chromeWidth = Math.max(0, entry.popup.outerWidth - entry.popup.innerWidth);
      const chromeHeight = Math.max(0, entry.popup.outerHeight - entry.popup.innerHeight);
      entry.popup.resizeTo(chosen.width + chromeWidth, chosen.height + chromeHeight);
    } catch { /* browser policy may forbid scripted resizing; the content still scrolls */ }
    return;
  }
  const chosen = entry.profile;
  const before = current(element);
  const style = getComputedStyle(element);
  const cssWidth = Number.parseFloat(style.getPropertyValue('--drawio-window-preferred-width'));
  const cssHeight = Number.parseFloat(style.getPropertyValue('--drawio-window-max-height'));
  const width = numberOr(chosen?.width, numberOr(cssWidth, before.width));
  const heightCeiling = numberOr(chosen?.height, numberOr(cssHeight, window.innerHeight));
  const content = element.querySelector(':scope > .drawio-window-content');
  const oldOverflow = content?.style.overflow;
  element.style.width = `${width}px`;
  element.style.height = 'auto';
  if (content) content.style.overflow = 'visible';
  const naturalHeight = Math.ceil(element.getBoundingClientRect().height);
  if (content) content.style.overflow = oldOverflow ?? '';
  const measured = {
    left: before.left,
    top: before.top,
    width,
    height: Math.min(heightCeiling, Math.max(minimumOf(element).height, naturalHeight)),
  };
  apply(element, clamp(element, measured, floorOf(element)));
}

// The focus goes to the window shown, in front - unless the operator has already put it in that window
// (or, while the page opens again the windows of the last visit, in any of them): the page un-hides a
// window a round trip before show() arrives, and on a slow link a click in its filter can come in
// between. That focus stays where it is, and its window goes in front, as the click that put it there
// did: moved, the focus would leave the field for a window's frame under the operator's hands; left
// behind another window, the field would go on taking the keys under it.
export function show(element) {
  const entry = attached.get(element);
  if (!entry) return;
  if (entry.popup && !entry.popup.closed) {
    try { entry.popup.focus(); } catch { /* browser may decline focus */ }
    return;
  }
  apply(element, clamp(element, current(element), floorOf(element)));
  const active = document.activeElement;
  if (reopening?.order.includes(entry.id)) {
    // Opened again from the memory: every window of the reopening shown so far is stacked in the
    // order of the last visit, and the focus goes to the one in front of them - whatever order
    // their show() calls come in. A window of the reopening the operator is already in, shown yet
    // or not, is then brought in front of them.
    reopening.shown.add(entry.id);
    let top = null;
    for (const id of reopening.order) {
      const shown = reopening.shown.has(id) ? elementOf(id) : null;
      if (shown) { toFront(shown, true); top = shown; }
    }
    const held = reopening.order.map(elementOf).find((w) => w?.contains(active));
    if (reopening.order.every((id) => reopening.shown.has(id))) reopening = null;
    if (!held) (top ?? element).focus({ preventScroll: true });
    else if (held !== top) toFront(held, true);
    return;
  }
  toFront(element);
  if (!element.contains(active)) element.focus({ preventScroll: true });
}

// The window has just been shut. The focus, if it was in it (the X, Esc) - or already lost with it,
// as a focused element that is hidden loses it - goes back to the button that opens it again (its id
// is the window's followed by -toggle: a button of the strip, or the "Details" of a line of the row of
// outcomes), so that the keyboard does not start over from the top of the page. A "Details" on a line
// hidden behind "+N more" cannot take it: the focus goes to the row then, which holds that line. A
// focus the operator has put elsewhere stays where it is. A contextual window has no permanent
// opener: closing it returns focus to draw.io's frame, the surface its context menu came from.
export function shut(element) {
  const entry = attached.get(element);
  if (!entry) return;
  if (entry.popup) returnToEditor(element, true, false);
  if (!focusIsLost() && !element.contains(document.activeElement)) return;
  const button = document.getElementById(`${entry.id}-toggle`);
  if (button?.checkVisibility()) button.focus({ preventScroll: true });
  else {
    const row = element.closest(rowSelector);
    if (row) row.focus({ preventScroll: true });
    else document.getElementById('drawio-frame')?.focus({ preventScroll: true });
  }
}

// A button of the row of outcomes that its gesture took away - "+N more" and "Show fewer" move to
// another line, the X goes with its line - hands the focus on to the button in its new place, or to the
// row (DrawioNotices). The hand-off lands a round trip after the render, so it takes the focus only if
// the focus was lost meanwhile: on the page's body, where a focused element that is removed or hidden
// leaves it. A focus the operator has put elsewhere before it lands - a field, a window, draw.io's
// frame - stays where it is (queue item 38).
export function focusIfLost(target) {
  if (focusIsLost()) target?.focus({ preventScroll: true });
}

// Whether the keyboard's focus is lost: on the page's body - where a focused element that is removed
// or hidden leaves it - or nowhere at all. shut() and focusIfLost() both ask it.
function focusIsLost() {
  const active = document.activeElement;
  return !active || active === document.body;
}

// The strip's button of an open window (owner's decision D1, 29/9): a window behind another comes to
// the front, with the focus; the one in front is left to be shut. True when it was brought to the
// front - the page then leaves it open - and false when it is already in front of the open windows,
// or is not an open window this module knows: the page shuts it.
export function bringToFrontIfBehind(id) {
  forgetDisconnected();
  const element = elementOf(id);
  if (!element || element.hidden) return false;
  const entry = attached.get(element);
  if (entry?.popup && !entry.popup.closed) {
    try { entry.popup.focus(); } catch { /* browser may decline focus */ }
    return true;
  }
  if (frontOpen() === element) return false;
  toFront(element, true);
  element.focus({ preventScroll: true });
  return true;
}

// A contextual entry is an explicit request to work in its window. Unlike a section button it never
// toggles the window shut, so it also focuses a window that is already in front.
export function activate(id) {
  forgetDisconnected();
  const element = elementOf(id);
  if (!element || element.hidden) return false;
  const entry = attached.get(element);
  if (entry?.popup && !entry.popup.closed) {
    try { entry.popup.focus(); } catch { /* browser may decline focus */ }
    return true;
  }
  if (frontOpen() !== element) toFront(element, true);
  element.focus({ preventScroll: true });
  return true;
}

export function detach(element) {
  if (attached.get(element)?.popup) returnToEditor(element, true, false);
  forget(element);
}

// The windows open at the end of the last visit, the one in front last. The page opens them again
// right after asking, and show() stacks them in this order (see reopening). The reopening ends once
// all of them are shown, or at the page's next rememberOpen: a window that never shows again (an id
// this version of the page no longer has) must not keep it going.
export function openedLastTime() {
  const stored = read(openKey);
  const ids = Array.isArray(stored) ? stored.filter((id) => typeof id === 'string') : [];
  reopening = ids.length > 0 ? { order: ids, shown: new Set() } : null;
  return ids;
}

// The windows open now, as the page says, written in the order they are stacked (stackOrder).
export function rememberOpen(ids) {
  reopening = null;
  write(openKey, stackOrder(Array.isArray(ids) ? ids.filter((id) => typeof id === 'string') : []));
}

// On the page the strip's observers see a browser resize too (the drawing's box changes with it);
// this listener is for windows with no strip, and costs nothing twice: clamp() of a place already
// inside changes nothing. The scroll listener is for the page scrolling (see watchStrip).
window.addEventListener('resize', bringBackInside);
window.addEventListener('scroll', bringBackInside, { passive: true });
window.addEventListener('beforeunload', () => {
  for (const [element, entry] of attached) {
    if (entry.popup) returnToEditor(element, true, false);
  }
});
