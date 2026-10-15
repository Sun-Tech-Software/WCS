using Wcs.Api.Client;
using Wcs.WebEditor.Model;

namespace Wcs.WebEditor.Editing;

/// <summary>
/// La pila dell'annulla e del rifai (compito 6, esteso dal compito B4): con il salvataggio
/// immediato del compito 5 un gesto sbagliato e' gia' scritto sull'host - senza questa pila l'unico
/// rimedio sarebbe rifare il gesto a occhio.
/// </summary>
/// <remarks>
/// Il compito 6 dichiarava "solo annulla, nessun rifai" perche' il brief della 4b si fermava li'; il
/// piano del compito B4 lo chiede esplicitamente ("Rifai accanto all'annulla"), e la richiesta del
/// proprietario per i gesti nuovi (ridimensionare, ruotare, allineare) rende un rifai piu' utile che
/// nel solo trascinamento - un ridimensionamento sbagliato due volte di seguito, con un annulla in
/// mezzo per errore, non deve costringere a rifare il gesto a mano. <see cref="_redoStack"/> tiene i
/// comandi appena annullati, nello stesso ordine con cui <see cref="UndoAsync"/> li ha tolti da
/// <see cref="_undoStack"/>; <see cref="DoAsync"/> lo svuota a ogni nuovo comando - la stessa regola
/// di ogni editor con annulla/rifai: un gesto nuovo dopo un annulla rende irraggiungibile il ramo di
/// storia appena lasciato, e tenerlo in giro lo confonderebbe con quello nuovo.
/// </remarks>
/// <remarks>
/// Un comando entra in pila solo dopo che <see cref="EditCommand.ApplyAsync"/> e' andato a buon
/// fine (mai prima): se la scrittura fallisce l'eccezione risale a chi ha chiamato
/// <see cref="DoAsync"/> e la pila resta come prima. Lo stesso per <see cref="UndoAsync"/>: il
/// comando esce dalla pila solo dopo che <see cref="EditCommand.UndoAsync"/> e' riuscito - un
/// annulla fallito (per esempio il permesso perso nel frattempo) non svuota il suo posto, cosi' un
/// tentativo successivo puo' riprovare lo stesso comando.
/// </remarks>
public sealed class CommandStack
{
    /// <summary>
    /// Il tetto della pila: scelta arbitraria di questo compito, non una misura - copre una sessione
    /// di lavoro tipica (decine di trascinamenti) senza lasciar crescere la memoria del circuito
    /// Blazor senza limite in una sessione lunga. Oltre il tetto si scarta il comando piu' vecchio.
    /// </summary>
    private const int MaxDepth = 50;

    private readonly List<EditCommand> _undoStack = [];

    /// <summary>I comandi appena annullati, pronti per <see cref="RedoAsync"/> - svuotata da <see cref="DoAsync"/> a ogni comando nuovo (si veda il remarks della classe).</summary>
    private readonly List<EditCommand> _redoStack = [];

    /// <summary>Quanti comandi sono annullabili in questo momento.</summary>
    public int Count => _undoStack.Count;

    public bool CanUndo => _undoStack.Count > 0;

    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>La descrizione del comando che un annulla toglierebbe ora, o nullo a pila vuota (il brief dichiara <see cref="EditCommand.Description"/> nell'interfaccia, ma prima di questa correzione nessun punto la mostrava).</summary>
    public string? UndoDescription => _undoStack.Count > 0 ? _undoStack[^1].Description : null;

    /// <summary>La descrizione del comando che un rifai rimetterebbe ora, o nullo se non c'e' niente da rifare (compito B4).</summary>
    public string? RedoDescription => _redoStack.Count > 0 ? _redoStack[^1].Description : null;

    /// <summary>
    /// Esegue <paramref name="command"/> e, solo se la scrittura riesce, lo mette in pila. Svuota il
    /// rifai (compito B4): un gesto nuovo dopo un annulla apre un ramo di storia diverso da quello
    /// che <see cref="_redoStack"/> stava conservando, e tenerlo in giro lascerebbe un "rifai" che
    /// rimetterebbe uno stato che il gesto appena fatto ha gia' superato.
    /// </summary>
    public async Task DoAsync(EditCommand command, WcsClient client, PlantGraph graph)
    {
        await command.ApplyAsync(client, graph);

        _undoStack.Add(command);
        if (_undoStack.Count > MaxDepth) _undoStack.RemoveAt(0);
        _redoStack.Clear();
    }

    /// <summary>
    /// Annulla l'ultimo comando. A pila vuota non fa niente e non solleva - un annulla senza niente
    /// da annullare non e' un errore.
    /// </summary>
    public async Task UndoAsync(WcsClient client, PlantGraph graph)
    {
        if (_undoStack.Count == 0) return;

        var command = _undoStack[^1];
        await command.UndoAsync(client, graph);

        // Per identita', non per indice: la guardia sopra e' valutata prima dell'await, ma
        // questa riga gira dopo - se due annulla si sovrappongono (per esempio Ctrl+Z tenuto premuto
        // e la ripetizione automatica manda un secondo keydown mentre la PUT del primo e' ancora in
        // volo) entrambi leggono lo stesso comando in cima prima che il primo l'abbia tolto. Con
        // RemoveAt(Count-1) la seconda rimozione toglierebbe chi si trova in cima *allora* - con due
        // comandi in pila, quello sotto, mai annullato - o solleverebbe ArgumentOutOfRangeException
        // con un comando solo, un'eccezione che non rientra nel filtro catch di
        // PlantCanvas.UndoAsync e abbatterebbe il circuito. Non ho dimostrato che la sovrapposizione
        // sia raggiungibile oggi (i default di SignalR serializzano le invocazioni), ma nessun
        // commento qui lo dichiara: cercare per riferimento trasforma l'eventuale corsa in un no-op
        // benigno (il secondo giro non trova piu' l'oggetto e non fa niente) invece di un dato perso
        // o di un'eccezione non gestita.
        //
        // Per RIFERIMENTO, non List<T>.Remove(command): EditCommand e' un record, quindi
        // Remove userebbe l'Equals sintetizzato - un confronto per VALORE, campo per campo. Oggi
        // coincide con l'identita' solo per un motivo accidentale che nessun commento cita: i campi
        // di MoveItemsCommand sono due List<T> allocate nuove a ogni gesto, e List<T> non implementa
        // IEquatable<T>, quindi il confronto degenera nel riferimento. Il primo EditCommand futuro
        // con campi tutti confrontabili per valore (compiti 7-12) rompe questa coincidenza: due voci
        // uguali per valore in pila sono allora possibili (basta che lo stato torni due volte allo
        // stesso punto), e Remove(command) toglierebbe la voce piu' vecchia fra le uguali - il
        // comando mai annullato, esattamente il difetto che la ricerca per riferimento, sopra, evita di riaprire.
        // FindLastIndex con ReferenceEquals cerca l'oggetto appena annullato e nessun altro che gli
        // somigli, qualunque sia la forma dei suoi campi.
        var index = _undoStack.FindLastIndex(c => ReferenceEquals(c, command));
        // Rifai (compito B4): il comando appena annullato torna disponibile a RedoAsync - non tolto
        // per riferimento come sopra, perche' qui la pila di destinazione e' vuota di lui: viene
        // semplicemente aggiunto in coda, l'ultimo annullato in cima al rifai (LIFO, come l'annulla).
        // DENTRO lo stesso "if" della RemoveAt, non dopo: e' lo stesso guardiano che rende
        // "no-op benigno" la corsa descritta sopra, e deve coprire tutto cio' che questo giro fa. Se
        // il comando non e' piu' in _undoStack (il secondo giro di una sovrapposizione, che non lo
        // trova piu'), aggiungerlo comunque al rifai lo rimetterebbe in pila una seconda volta - due
        // ApplyAsync dello stesso comando su due rifai, e l'invariante "annulla + rifai <= MaxDepth"
        // (la ragione per cui RedoAsync, sotto, non ha un proprio tetto) salterebbe.
        if (index >= 0)
        {
            _undoStack.RemoveAt(index);
            _redoStack.Add(command);
        }
    }

    /// <summary>
    /// Rifa' l'ultimo comando annullato (compito B4). A pila vuota non fa niente e non solleva - la
    /// stessa scelta di <see cref="UndoAsync"/>, per la stessa ragione: un rifai senza niente da
    /// rifare non e' un errore.
    /// </summary>
    public async Task RedoAsync(WcsClient client, PlantGraph graph)
    {
        if (_redoStack.Count == 0) return;

        var command = _redoStack[^1];
        await command.ApplyAsync(client, graph);

        // Per riferimento, la stessa ragione di UndoAsync sopra: un rifai che si sovrappone a se
        // stesso non deve togliere il comando sbagliato ne' sollevare su un indice fuori pila.
        var index = _redoStack.FindLastIndex(c => ReferenceEquals(c, command));
        // DENTRO lo stesso "if" della RemoveAt, non dopo - lo stesso guardiano usato in UndoAsync
        // per la stessa corsa speculare: se due rifai si sovrappongono, il secondo giro non trova
        // piu' il comando in _redoStack (index < 0) e non deve comunque aggiungerlo a _undoStack,
        // altrimenti lo stesso oggetto comparirebbe due volte in pila - due annulla successivi
        // eseguirebbero due volte l'UndoAsync dello stesso comando - e l'invariante
        // "annulla + rifai <= MaxDepth" salterebbe.
        if (index >= 0)
        {
            _redoStack.RemoveAt(index);

            // Il comando rifatto torna nell'annulla, come se fosse stato eseguito ora - non passa
            // da DoAsync (che svuoterebbe di nuovo il rifai: un rifai non e' un gesto nuovo, e'
            // lo stesso gesto che era gia' stato fatto e poi disfatto). Nessun tetto qui: se il
            // comando c'era gia' in pila prima dell'annulla che lo ha tolto, MaxDepth non lo aveva
            // ancora scartato.
            _undoStack.Add(command);
        }
    }
}
