namespace Wcs.Wms.Contracts;

/// <summary>
/// La domanda «di questi, quali sono in pausa?» (compiti 5 e 6): un elenco di identificativi che il
/// motore ha nella propria coda di estrazione, e di cui vuole sapere quali appartengono a una
/// lavorazione che non e' piu' in corso.
/// </summary>
/// <remarks>
/// <b>Perche' un tipo solo per due domande.</b> Le righe di estrazione e le assegnazioni di nota
/// sono due famiglie diverse - due tabelle, due giunzioni, due rotte - ma la <b>richiesta</b> ha la
/// stessa forma: una lista di identificativi. Il tipo e' quindi condiviso, e a distinguere le due
/// domande e' la rotta: <see cref="WarehouseRoutes.PausedExtractionRows"/> per le righe di
/// estrazione, <see cref="WarehouseRoutes.PausedNoteAssignments"/> per le assegnazioni di nota
/// (compito 6). Il criterio della casa - stessa operazione solo
/// se chiave, forma e semantica coincidono - qui tiene separate le due <b>operazioni</b>, perche' la
/// chiave non e' la stessa; il tipo del corpo, che chiave non e', si condivide.
/// <para>
/// <b>Perche' viaggia in un corpo POST e non nella query string.</b> Sull'impianto di prova la coda
/// arriva a centinaia di righe, e il gestionale spezza l'elenco in blocchi da mille parametri: una
/// lista cosi' in un URL supererebbe il limite di lunghezza dei server e dei proxy. La rotta e'
/// quindi POST pur essendo una <b>lettura</b> - non scrive niente di la' dal confine.
/// </para>
/// <para>
/// Un elenco <b>vuoto</b> e' legittimo e non e' un giro sprecato: la risposta e' una lista vuota, e
/// tutt'e due i lati la danno senza toccare il database.
/// </para>
/// </remarks>
/// <param name="Ids">Gli identificativi da esaminare, nell'ordine in cui il motore li ha.</param>
public sealed record PausedAmongRequest(IReadOnlyList<decimal> Ids);
