// Riscrive la PROPRIETA' "value" dell'<input id="zoom-input"> (Toolbar.razor), non il solo
// attributo che Blazor gestisce da solo - e' la stessa distinzione spiegata nel commento del
// markup sopra quell'<input>: un browser tiene la proprieta' "value" indipendente da quello che
// Blazor ha appena reso, una volta che l'utente ha digitato qualcosa, e solo una scrittura diretta
// della proprieta' la riporta in sincronia col valore vero applicato da PlantCanvas.SetZoomPercent.
// Chiamato da Toolbar.OnAfterRenderAsync subito dopo un invio nella casella (riuscito, scartato o
// agganciato a un limite gia' in vigore) - non a ogni resa del componente, si veda il commento su
// OnAfterRenderAsync per la ragione (una resa scatenata da altro, mentre l'utente sta ancora
// digitando, cancellerebbe cio' che non ha ancora confermato). E' il caso SCARTATO o AGGANCIATO
// (dove il parametro ZoomPercent non cambia, quindi Blazor non trova nessuna differenza da scrivere
// da solo nell'attributo) quello che questa funzione deve correggere nella proprieta' vera.
export function setValue(element, value) {
    if (element && element.value !== value) {
        element.value = value;
    }
}
