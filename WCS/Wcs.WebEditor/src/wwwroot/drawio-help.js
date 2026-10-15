// The "?" of the draw.io page (DrawioHelp.razor): the things about its note that Blazor cannot see.
// The "?" and Esc open and shut the note in the component; this module only watches, from the first
// opening until the component goes. Its listeners are on the note's own element, and on the
// documents only while the note is open: none is left on a document once the note shuts.
//
// A press elsewhere: while the note is open, a press anywhere outside the note's element shuts it -
// on the page, or inside draw.io's frame, whose document our own host serves and this page can read.
// The focus alone does not say so: a press whose default is cancelled moves no focus, and draw.io
// cancels every press on its shapes panel, drawio-windows.js the press a window's drag begins with
// (measured on 30/9: after either one the focus was still on the "?" and the note stayed open). The
// press is heard in the capture phase, before anything on the page or in draw.io can stop it.
//
// Focus leaving: Tab past the note, or a press that does move the focus out of the note's element -
// the component is told to shut it. Blazor's own focusout carries no relatedTarget, which is what
// tells focus going from the "?" into the note from focus leaving both.
//
// Esc inside a window: drawio-windows.js shuts the window that has the focus on an Esc nobody has
// handled (defaultPrevented), and it listens on the window, which the key reaches after the note and
// before Blazor's own listener on the document. So the note marks its Esc as handled here, and the
// window stays open: the first Esc shuts the note, the next one the window. The focus goes back to
// the "?", so the next Esc still reaches the window.
//
// Where the note opens: under the "?" with its right edge on the "?"'s, as the stylesheet hangs it -
// but a window dragged to the browser's left edge, or narrowed, would put its start off the screen,
// and the properties column and a box that scrolls cut what goes past their edges. So while it is
// open the note is placed here, fixed to the viewport (.drawio-help-placed), inside the browser's
// window: moved right or left as far as it has to, under the "?" or over it - whichever has the room
// when it does not fit under it - and no taller than that room (it scrolls inside itself). Placed
// again when the page scrolls, anywhere, or the browser's window changes size, as the "?" moves then.
//
// A window detached into a separate browser window (drawio-windows.js) takes its "?" with it: the
// browser's window is then the separate one - its size places the note, its document is where a press
// elsewhere lands and what scrolls - and the editor's documents are watched as well.
const watched = new Map(); // element -> off

// The room the note leaves between itself and the browser's edges.
const margin = 8;

function place(button, note) {
  if (!button || !note || note.hidden) return;
  const view = button.ownerDocument.defaultView ?? window;
  note.classList.add('drawio-help-placed');
  note.style.maxHeight = '';
  note.style.left = '0px';
  note.style.top = '0px';
  const b = button.getBoundingClientRect();
  const width = Math.min(note.offsetWidth, view.innerWidth - 2 * margin);
  const height = note.offsetHeight;
  const left = Math.max(margin, Math.min(b.right - width, view.innerWidth - margin - width));
  const below = view.innerHeight - margin - (b.bottom + 4);
  const above = b.top - 4 - margin;
  const under = height <= below || below >= above;
  const room = Math.max(0, under ? below : above);
  note.style.left = `${left}px`;
  note.style.top = `${under ? b.bottom + 4 : b.top - 4 - Math.min(height, room)}px`;
  note.style.maxHeight = `${room}px`;
}

// The documents a press can land on: the page's, the document of each of its frames the page can
// read (contentDocument is null for one it cannot), and the separate window's when the "?" is in a
// detached window. Read at each opening: a frame that reloaded has a new document, and a window can
// have been detached or brought back meanwhile.
function documentsOfThePage(element) {
  const documents = [document, ...[...document.querySelectorAll('iframe')].map((f) => f.contentDocument).filter((d) => !!d)];
  if (!documents.includes(element.ownerDocument)) documents.push(element.ownerDocument);
  return documents;
}

export function watchHelp(element, component) {
  if (!element || watched.has(element)) return;
  const button = element.querySelector('.drawio-help');
  const note = element.querySelector('.drawio-help-body');
  const isOpen = () => !!note && !note.hidden;
  const shut = () => component.invokeMethodAsync('Shut').catch(() => { /* the circuit is gone: so is the note */ });

  const onFocusOut = (e) => {
    if (!isOpen() || element.contains(e.relatedTarget)) return;
    shut();
  };
  const onPressElsewhere = (e) => {
    if (isOpen() && !element.contains(e.target)) shut();
  };
  const onKey = (e) => {
    if (e.key !== 'Escape' || !isOpen()) return;
    e.preventDefault();
    button?.focus({ preventScroll: true });
  };
  // A press on the note is the note's: in a window's title bar it would otherwise start dragging the
  // window (drawio-windows.js leaves only buttons out of the drag), and the text could not be selected.
  const onPress = (e) => e.stopPropagation();

  // A scroll of the note's own text is not the "?" moving: placed again, the note would lose it.
  const onMove = (e) => {
    if (e?.target instanceof Node && note?.contains(e.target)) return;
    place(button, note);
  };

  // The documents' listeners follow the note, on when it opens and off when it shuts: the component
  // opens and shuts it by its hidden attribute, and that is what is watched. The observer's word
  // comes before the browser paints the opened note, so it is never seen where it was not placed -
  // the first opening included: the component opens the note only once this module watches it.
  // The documents listened to for a press elsewhere, and the browser windows (the editor's, and the
  // separate one of a detached window) listened to for a scroll or a resize.
  let listening = [];
  let views = [];
  const stopListening = () => {
    for (const d of listening) d.removeEventListener('pointerdown', onPressElsewhere, true);
    for (const v of views) {
      try {
        v.document.removeEventListener('scroll', onMove, true);
        v.removeEventListener('resize', onMove);
      } catch { /* a separate window already closed */ }
    }
    listening = [];
    views = [];
  };
  const follow = () => {
    if (isOpen() && listening.length === 0) {
      listening = documentsOfThePage(element);
      for (const d of listening) d.addEventListener('pointerdown', onPressElsewhere, true);
      views = [...new Set([window, element.ownerDocument.defaultView].filter((v) => !!v))];
      for (const v of views) {
        v.document.addEventListener('scroll', onMove, true);
        v.addEventListener('resize', onMove);
      }
      place(button, note);
    } else if (!isOpen()) {
      stopListening();
    }
  };
  const observer = new MutationObserver(follow);
  if (note) observer.observe(note, { attributes: true, attributeFilter: ['hidden'] });
  follow();

  element.addEventListener('focusout', onFocusOut);
  element.addEventListener('keydown', onKey);
  note?.addEventListener('pointerdown', onPress);
  watched.set(element, () => {
    observer.disconnect();
    stopListening();
    element.removeEventListener('focusout', onFocusOut);
    element.removeEventListener('keydown', onKey);
    note?.removeEventListener('pointerdown', onPress);
  });
}

export function unwatchHelp(element) {
  const off = watched.get(element);
  if (!off) return;
  off();
  watched.delete(element);
}
