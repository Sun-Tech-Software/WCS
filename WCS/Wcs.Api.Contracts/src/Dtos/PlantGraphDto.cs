namespace Wcs.Api.Contracts.Dtos;

/// <summary>
/// L'impianto intero, come lo vedono l'esportazione e l'importazione (compito 12): la
/// configurazione di ogni dispositivo, la sua geometria, le giunzioni, le mappature porte, il
/// disegno libero e la configurazione delle connessioni PLC. Riusa i contratti già esposti dalle
/// rotte specifiche e li compone in un solo corpo: chi importa deve poter sostituire il backup in
/// un colpo solo, non con scritture separate che possono riuscire a metà.
/// </summary>
/// <remarks>
/// Non porta lo stato dal vivo dei dispositivi (<see cref="Wcs.Api.Contracts.Dtos.ItemStatus"/>) né
/// quello delle connessioni PLC: un'esportazione è un backup della configurazione dell'impianto,
/// non una fotografia di missioni, avanzamenti o comandi in corso. Non porta nemmeno il blob del disegno Syncfusion
/// (<see cref="DiagramDto"/>): quello è congelato dalla tappa 4a, pesa decine di megabyte
/// sull'impianto di prova e nessuno lo legge più — l'unica cosa che ne usciva ancora, la geometria,
/// viene già dall'importazione dei vecchi <c>.edd</c>/<c>.xml</c> tramite
/// <c>DiagramGeometryReader</c>, non da questo DTO.
/// <para>
/// La decorazione e la configurazione delle connessioni PLC, invece, <b>viaggiano</b>: si vedano
/// <see cref="Decoration"/> e <see cref="PlcConnections"/> per la compatibilità con i file creati
/// prima che questi campi esistessero.
/// </para>
/// </remarks>
/// <param name="Decoration">
/// Il disegno libero dell'operatore (<see cref="PlantDecorationDto.Xml"/>), oppure <b>nullo</b>.
/// <para>
/// I due casi vogliono dire cose diverse, e non sono intercambiabili. <b>Non nullo</b> — anche se
/// è la stringa vuota — vuol dire «questo file parla del disegno»: <c>PUT /plant/graph</c> lo
/// <b>sostituisce</b> con quello del file, ed è un ripristino (la stringa vuota ripristina
/// l'assenza di decorazione, che è uno stato legittimo). <b>Nullo</b> vuol dire «questo file non
/// parla del disegno»: la decorazione dell'impianto <b>resta quella che c'è</b>. Il nullo è il caso
/// delle esportazioni fatte prima che questo campo esistesse, che devono continuare a importarsi
/// senza cancellare il disegno di chi le importa — e il valore predefinito è nullo proprio perché
/// un file che tace non deve poter parlare per sbaglio.
/// </para>
/// </param>
/// <param name="PlcConnections">
/// Le sole impostazioni configurabili delle connessioni PLC. Non comprende stato operativo,
/// avanzamento o ultimo comando. <b>Nullo</b> identifica un vecchio file che non parla delle
/// connessioni: durante l'importazione quelle già presenti devono essere conservate. Una raccolta
/// non nulla, anche vuota, le sostituisce invece con il contenuto del backup.
/// </param>
public sealed record PlantGraphDto(
    IReadOnlyList<PlantItemConfigDto> Items,
    IReadOnlyList<PlantItemGeometryDto> Geometries,
    IReadOnlyList<JunctionDto> Junctions,
    IReadOnlyList<DoorMappingDto> DoorMappings,
    string? Decoration = null,
    IReadOnlyList<PlcConnectionDto>? PlcConnections = null);

/// <summary>
/// Quello che <c>DiagramGeometryReader</c> ha letto da un vecchio disegno <c>.edd</c>/<c>.xml</c>
/// caricato per l'importazione (compito 12): le stesse geometrie e gli stessi quattro contatori del
/// verbale di migrazione (<see cref="Wcs.Engine.Persistence.GeometryMigrationReport"/> — non
/// referenziato qui perché quel tipo vive nel motore e questo contratto no), mostrati con la stessa
/// avvertenza: sono contati <b>dopo</b> il filtro sul tipo del nodo (solo i nodi il cui
/// <c>PropertyBag</c> dichiara <c>OrchestraType</c>/<c>DiagramType</c> uguale a <c>ITEM</c> sono
/// considerati dispositivi), quindi uno zero dice «niente di ciò che il lettore guarda mancava», non
/// «il disegno non conteneva altro».
/// </summary>
/// <param name="Geometries">Solo i dispositivi il cui Guid porta almeno una geometria leggibile — gli stessi che finiscono in <c>DiagramGeometryResult.Geometries</c>.</param>
/// <param name="ElementsWithoutGuid">Nodi dispositivo del disegno il cui <c>Tag</c> non è un Guid leggibile.</param>
/// <param name="GuidsWithoutGeometry">Guid di dispositivo trovati nel disegno ma senza nessun riquadro leggibile in nessuna occorrenza.</param>
/// <param name="PartiallyPlaced">Quante delle <see cref="Geometries"/> portano un riquadro letto solo in parte (uno, due o tre numeri su quattro, mai tutti).</param>
public sealed record LegacyGeometryImportDto(
    IReadOnlyList<PlantItemGeometryDto> Geometries,
    int ElementsWithoutGuid,
    int GuidsWithoutGeometry,
    int PartiallyPlaced);
