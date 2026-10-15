// Il modulo JS del dock delle barre (compito barre-spostabili): Toolbar.razor lo importa come modulo
// isolato, con la stessa convenzione degli altri di questa libreria (si veda plant-canvas-size.js,
// che la mostra per intero compreso il caso in cui il modulo non si carica affatto).
//
// Fa DUE cose sole, e nessuna delle due decide dove finisce una barra:
//   1. la memoria della disposizione in localStorage (loadLayout/saveLayout) - testo dentro, testo
//      fuori: chi lo interpreta e' ToolbarLayout.Deserialize, in C#, dove i casi rotti sono provati;
//   2. il trascinamento della maniglia: legge il puntatore, calcola SU QUALE riga e in quale
//      posizione cadrebbe la barra, disegna il segno di inserimento e, al rilascio, chiama
//      OnBarDroppedAsync con quei due numeri.
//
// La regola di divisione e' quella del brief, e non e' un vezzo: bUnit non fa hit-testing e non
// simula trascinamenti, quindi tutto cio' che sta in questo file e' codice SENZA rete. Percio' qui
// c'e' solo geometria (quale riga contiene questa y, quante barre stanno a sinistra di questa x) e
// nessuna regola: la riga che si svuota, l'indice agganciato ai limiti, la riga nuova, la barra
// spostata su se stessa li decide ToolbarLayout, in C#, dove una prova li puo' eseguire.

const STORAGE_KEY = 'wcs.webeditor.toolbar-layout';

/**
 * Il testo salvato, o null se non c'e' o se localStorage non e' leggibile (finestra privata,
 * memoria dei siti bloccata: l'accesso SOLLEVA, non torna vuoto). Null vuol dire "non so": chi
 * chiama cade sulla disposizione predefinita con tutte le barre visibili - mai su un dock vuoto.
 */
export function loadLayout() {
    try {
        return window.localStorage.getItem(STORAGE_KEY);
    } catch {
        return null;
    }
}

/** Salva la disposizione. Se localStorage non e' scrivibile non succede niente: si perde la memoria fra una sessione e l'altra, non la disposizione di adesso. */
export function saveLayout(json) {
    try {
        window.localStorage.setItem(STORAGE_KEY, json);
    } catch {
        // Nessun rimedio possibile e nessun danno: la disposizione corrente vive in C#, questa era
        // solo la sua copia per la prossima sessione.
    }
}

// Lo stato di UN trascinamento per volta: un secondo pointerdown mentre uno e' in corso viene
// ignorato (beginDrag esce subito), invece di lasciare due trascinamenti che si contendono lo stesso
// segno di inserimento.
let drag = null;

/**
 * Comincia a trascinare la barra "barId". Gli ascoltatori stanno su document, non sulla maniglia:
 * il puntatore esce di sicuro dalla maniglia (e' larga cinque pixel) e deve continuare a essere
 * seguito fin dove l'utente lo porta, anche fuori dal dock.
 */
export function beginDrag(dock, dotNetRef, barId) {
    if (!dock || !dotNetRef || drag) return;

    const bar = barElement(dock, barId);
    if (!bar) return;

    const marker = document.createElement('div');
    marker.className = 'toolbar-drop-marker';
    dock.appendChild(marker);

    drag = { dock, dotNetRef, barId, marker, target: null };
    bar.classList.add('toolbar-bar-dragging');
    document.body.classList.add('toolbar-dragging');

    document.addEventListener('pointermove', onPointerMove);
    document.addEventListener('pointerup', onPointerUp);
    document.addEventListener('keydown', onKeyDown, true);
}

/** Toglie ascoltatori, segno di inserimento e classi: chiamato al rilascio, all'Esc e da Toolbar.DisposeAsync. */
export function endDrag() {
    if (!drag) return;

    const { dock, barId, marker } = drag;
    document.removeEventListener('pointermove', onPointerMove);
    document.removeEventListener('pointerup', onPointerUp);
    document.removeEventListener('keydown', onKeyDown, true);

    marker.remove();
    barElement(dock, barId)?.classList.remove('toolbar-bar-dragging');
    document.body.classList.remove('toolbar-dragging');
    drag = null;
}

function onPointerMove(event) {
    if (!drag) return;

    drag.target = computeTarget(drag.dock, drag.barId, event.clientX, event.clientY);
    drawMarker(drag.dock, drag.marker, drag.target, drag.barId);
}

function onPointerUp() {
    if (!drag) return;

    // Copiati prima di endDrag, che azzera lo stato.
    const { dotNetRef, barId, target } = drag;
    endDrag();

    // Nessun movimento (un clic secco sulla maniglia) non e' uno spostamento a caso: non si chiama
    // niente. Una chiamata con l'ultima posizione nota sarebbe uno spostamento che l'utente non ha
    // chiesto, e per una barra gia' al suo posto sarebbe comunque un giro a vuoto.
    if (!target) return;

    dotNetRef.invokeMethodAsync('OnBarDroppedAsync', barId, target.row, target.index);
}

function onKeyDown(event) {
    // L'Esc annulla il trascinamento in corso: si lascia la barra dov'era, nessuna chiamata verso C#.
    // In cattura (true) perche' arrivi qui prima di chiunque altro stia ascoltando l'Esc sulla pagina.
    if (event.key !== 'Escape' || !drag) return;

    event.stopPropagation();
    event.preventDefault();
    endDrag();
}

/** La barra nel DOM: la si trova dalla propria maniglia, l'unico elemento del dock che porta il suo id. */
function barElement(dock, barId) {
    const handle = dock.querySelector(`.toolbar-handle[data-toolbar-bar="${barId}"]`);
    return handle ? handle.closest('.toolbar-group') : null;
}

function barIdOf(element) {
    const handle = element.querySelector('.toolbar-handle[data-toolbar-bar]');
    return handle ? handle.dataset.toolbarBar : null;
}

/**
 * Dove cadrebbe la barra se si rilasciasse adesso: { row, index, newRow }.
 *
 * La riga e' quella che contiene la y del puntatore; sotto l'ultima riga (compreso il margine di
 * NEW_ROW_MARGIN px oltre il suo bordo, perche' prendere la riga nuova non richieda di uscire dalla
 * finestra) la riga e' una NUOVA, e vale l'indice fuori limite che ToolbarLayout aggancia da solo.
 *
 * L'indice conta le barre della riga la cui meta' orizzontale sta a sinistra del puntatore, SALTANDO
 * la barra trascinata: e' esattamente la definizione di ToolbarLayout.Move (la posizione contata dopo
 * aver tolto la barra che si sposta). Le due definizioni devono restare la stessa, ed e' per questo
 * che qui c'e' il filtro invece di una correzione dell'indice dopo.
 */
function computeTarget(dock, barId, x, y) {
    const rows = [...dock.querySelectorAll('[data-toolbar-row]')];
    if (rows.length === 0) return null;

    const NEW_ROW_MARGIN = 8;
    const lastRow = rows[rows.length - 1].getBoundingClientRect();
    if (y > lastRow.bottom + NEW_ROW_MARGIN) {
        return { row: rows.length, index: 0, newRow: true };
    }

    let rowIndex = rows.findIndex(row => {
        const box = row.getBoundingClientRect();
        return y >= box.top && y <= box.bottom;
    });

    // Sopra la prima riga, o nel vuoto fra due righe: la piu' vicina invece di nessuna - un
    // trascinamento non deve smettere di rispondere perche' il puntatore e' uscito di due pixel.
    if (rowIndex < 0) {
        rowIndex = y < rows[0].getBoundingClientRect().top ? 0 : rows.length - 1;
    }

    const others = [...rows[rowIndex].querySelectorAll('.toolbar-group')]
        .filter(element => barIdOf(element) !== barId);

    let index = 0;
    for (const element of others) {
        const box = element.getBoundingClientRect();
        if (x > box.left + box.width / 2) index++;
    }

    return { row: rowIndex, index, newRow: false };
}

/**
 * Il segno di inserimento: un filo sottile dove la barra finira', non l'anteprima della barra intera
 * (il brief lo chiede esplicitamente). Verticale fra due barre di una riga, orizzontale sotto
 * l'ultima riga quando il rilascio farebbe nascere una riga nuova. Le coordinate sono relative al
 * dock, che e' position:relative apposta.
 */
function drawMarker(dock, marker, target, barId) {
    if (!target) {
        marker.style.display = 'none';
        return;
    }

    const dockBox = dock.getBoundingClientRect();
    const rows = [...dock.querySelectorAll('[data-toolbar-row]')];
    marker.style.display = 'block';

    if (target.newRow) {
        const lastRow = rows[rows.length - 1].getBoundingClientRect();
        marker.classList.add('toolbar-drop-marker-row');
        marker.style.left = '0px';
        marker.style.top = `${lastRow.bottom - dockBox.top}px`;
        marker.style.width = `${dockBox.width}px`;
        marker.style.height = '';
        return;
    }

    const rowBox = rows[target.row].getBoundingClientRect();
    // Le stesse barre su cui l'indice e' stato contato (senza quella trascinata, si veda
    // computeTarget): il segno cade sul bordo fra la barra target.index-1 e la target.index di
    // QUELL'elenco. Disegnarlo su un elenco diverso da quello che ha prodotto l'indice sposterebbe il
    // filo di una barra rispetto a dove la barra finisce davvero.
    const others = [...rows[target.row].querySelectorAll('.toolbar-group')]
        .filter(element => barIdOf(element) !== barId);
    const edge = others.length === 0
        ? rowBox.left
        : target.index >= others.length
            ? others[others.length - 1].getBoundingClientRect().right
            : others[target.index].getBoundingClientRect().left;

    marker.classList.remove('toolbar-drop-marker-row');
    marker.style.left = `${edge - dockBox.left}px`;
    marker.style.top = `${rowBox.top - dockBox.top}px`;
    marker.style.width = '';
    marker.style.height = `${rowBox.height}px`;
}
