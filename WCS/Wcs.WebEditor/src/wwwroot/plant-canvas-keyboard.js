// Il modulo JS delle scorciatoie da tastiera della tela (compito B4): OnKeyDown (PlantCanvas.razor)
// porta @onkeydown sul contenitore div.plant-canvas, che avvolge anche la barra, gli elenchi e il
// pannello delle proprieta' - keydown risale il DOM da qualunque campo dentro quel contenitore fino
// a li'. KeyboardEventArgs di Blazor non porta il bersaglio dell'evento, quindi OnKeyDown da solo
// non puo' sapere se il tasto premuto viene dalla tela o da un campo che lo sta gia' usando per se'
// (scrivere un nome, la casella dello zoom): document.activeElement e' l'unica informazione
// affidabile, e solo un browser vero (o qui, un mock di prova) puo' darla. OnKeyDown chiede a questa
// funzione una volta per pressione, prima di muovere la selezione con le frecce o di
// annullare/rifare.
//
// Generico apposta, non un elenco di id da tenere aggiornato a mano: un campo di testo o numero, un
// elenco a discesa o un contenteditable viene riconosciuto senza che ogni campo nuovo debba
// difendersi da solo aggiungendo uno stopPropagation.
const TYPING_TAGS = new Set(['TEXTAREA', 'SELECT']);

// Dentro <input> non ogni "type" intercetta le frecce per se': checkbox e radio no - il loro gesto
// nativo e' la barra spaziatrice (checkbox) o le frecce fra i bottoni dello STESSO gruppo (radio, un
// caso che questo Editor non usa oggi), non le frecce della tela. La checkbox delle proprieta'
// booleane (PropertyPanel.razor) e' l'unico caso presente oggi: senza questa eccezione il fuoco li'
// spegnerebbe le frecce senza nessun conflitto da proteggere, perche' la checkbox non le usa per se'.
// Ogni altro "type" (text, number, e ogni tipo futuro non elencato qui) intercetta.
const NON_TYPING_INPUT_TYPES = new Set(['checkbox', 'radio']);

export function isTypingTarget() {
    const active = document.activeElement;
    if (!active) return false;
    if (active.tagName === 'INPUT') return !NON_TYPING_INPUT_TYPES.has(active.type);
    if (TYPING_TAGS.has(active.tagName)) return true;
    return active.isContentEditable === true;
}
