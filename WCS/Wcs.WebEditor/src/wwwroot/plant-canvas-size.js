// Il modulo JS della tela elastica (compito B3a): PlantCanvas.razor lo importa come modulo isolato
// (stessa convenzione di wcs-webeditor.css, si veda il commento di SizeModulePath in quel file) e lo
// usa per sapere quanto e' grande l'<svg> per davvero nel browser - la sola cosa che C# non puo'
// leggere da solo, perche' dipende dal layout CSS (flex) applicato dal foglio di stile della
// libreria.
//
// Un solo ResizeObserver per elemento osservato (non uno globale sulla finestra): osserva la tela
// stessa, non un antenato, quindi segue ogni causa che cambia lo spazio disponibile - il
// ridimensionamento della finestra, la barra che va a capo, il pannello delle proprieta' che si apre
// o si chiude - non solo il resize del browser in senso stretto (si veda il commento di
// OnAfterRenderAsync in PlantCanvas.razor per la ragione).

const observersByElement = new Map();

/**
 * Comincia a osservare "element": ogni volta che il suo riquadro cambia (compresa la prima misura,
 * che ResizeObserver richiama da solo appena l'osservazione parte - comportamento di specifica, non
 * un espediente di questo file) chiama dotNetRef.invokeMethodAsync con la larghezza e l'altezza in
 * pixel CSS. contentRect, non offsetWidth/offsetHeight: e' la dimensione del contenuto (senza
 * bordo/padding), coerente con quello che l'attributo width/height dell'<svg> deve ricevere.
 */
export function observeSize(element, dotNetRef) {
    if (!element) return;

    // Un secondo observeSize sullo stesso elemento (per esempio un doppio OnAfterRenderAsync(firstRender:
    // true), che in teoria non dovrebbe accadere ma costerebbe una fuga silenziosa se accadesse)
    // sostituisce l'osservatore vecchio invece di sommarsi a lui: mai due ResizeObserver che invocano lo
    // stesso dotNetRef per lo stesso elemento.
    unobserveSize(element);

    const observer = new ResizeObserver(entries => {
        for (const entry of entries) {
            const box = entry.contentRect;
            dotNetRef.invokeMethodAsync('OnCanvasSizeChanged', box.width, box.height);
        }
    });

    observer.observe(element);
    observersByElement.set(element, observer);
}

/** Ferma l'osservazione e libera l'observer - chiamato da PlantCanvas.DisposeAsync. */
export function unobserveSize(element) {
    const observer = observersByElement.get(element);
    if (!observer) return;

    observer.disconnect();
    observersByElement.delete(element);
}
