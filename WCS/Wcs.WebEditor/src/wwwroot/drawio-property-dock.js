const key = 'wcs.drawio.propertyDock.width.v1';
const minimum = 320;
const maximum = 560;
const canvasMinimum = 480;
const keyboardStep = 16;
const attached = new WeakMap();

const absoluteWidth = value => Math.max(minimum, Math.min(maximum, value));

function allowedMaximum(dock) {
    const bodyWidth = dock.parentElement?.clientWidth ?? minimum + canvasMinimum;
    return Math.max(minimum, Math.min(maximum, bodyWidth - canvasMinimum));
}

function readDesiredWidth() {
    try {
        const raw = localStorage.getItem(key);
        if (raw === null || raw.trim() === '') return minimum;
        const value = Number(raw);
        return Number.isFinite(value) ? absoluteWidth(value) : minimum;
    } catch {
        return minimum;
    }
}

function remember(width) {
    try { localStorage.setItem(key, String(width)); } catch { }
}

function apply(dock, resizer, state) {
    const limit = allowedMaximum(dock);
    const width = Math.min(state.desiredWidth, limit);
    dock.style.setProperty('--drawio-property-dock-width', `${width}px`);
    resizer.setAttribute('aria-valuenow', String(width));
    resizer.setAttribute('aria-valuemax', String(limit));
}

export function attach(dock) {
    if (attached.has(dock)) return;
    const resizer = dock.querySelector('.drawio-property-dock-resizer');
    if (!resizer) return;
    const state = { desiredWidth: readDesiredWidth(), pointerId: null, right: 0 };

    const pointerdown = event => {
        if (event.button !== 0) return;
        state.pointerId = event.pointerId;
        // Keep the grab point: the handle is inside the dock's clipped border, not over the iframe.
        state.right = event.clientX + dock.getBoundingClientRect().width;
        resizer.setPointerCapture(event.pointerId);
        event.preventDefault();
    };
    const pointermove = event => {
        if (event.pointerId !== state.pointerId) return;
        state.desiredWidth = absoluteWidth(state.right - event.clientX);
        apply(dock, resizer, state);
    };
    const pointerup = event => {
        if (event.pointerId !== state.pointerId) return;
        state.pointerId = null;
        remember(state.desiredWidth);
    };
    const keydown = event => {
        const visibleWidth = dock.getBoundingClientRect().width;
        const wanted = event.key === 'ArrowLeft' ? visibleWidth + keyboardStep
            : event.key === 'ArrowRight' ? visibleWidth - keyboardStep
            : event.key === 'Home' ? minimum
            : event.key === 'End' ? maximum
            : null;
        if (wanted === null) return;
        event.preventDefault();
        state.desiredWidth = absoluteWidth(wanted);
        apply(dock, resizer, state);
        remember(state.desiredWidth);
    };
    const reset = () => {
        state.desiredWidth = minimum;
        apply(dock, resizer, state);
        remember(state.desiredWidth);
    };
    const observer = new ResizeObserver(() => apply(dock, resizer, state));

    resizer.addEventListener('pointerdown', pointerdown);
    resizer.addEventListener('pointermove', pointermove);
    resizer.addEventListener('pointerup', pointerup);
    resizer.addEventListener('pointercancel', pointerup);
    resizer.addEventListener('keydown', keydown);
    resizer.addEventListener('dblclick', reset);
    if (dock.parentElement) observer.observe(dock.parentElement);
    attached.set(dock, () => {
        resizer.removeEventListener('pointerdown', pointerdown);
        resizer.removeEventListener('pointermove', pointermove);
        resizer.removeEventListener('pointerup', pointerup);
        resizer.removeEventListener('pointercancel', pointerup);
        resizer.removeEventListener('keydown', keydown);
        resizer.removeEventListener('dblclick', reset);
        observer.disconnect();
        delete dock.dataset.resizerAttached;
    });
    apply(dock, resizer, state);
    // Says the listeners are on: until then a drag on the separator does nothing, and the browser
    // tests wait for this before they drag or press keys on it.
    dock.dataset.resizerAttached = 'true';
}

export function detach(dock) {
    attached.get(dock)?.();
    attached.delete(dock);
}
