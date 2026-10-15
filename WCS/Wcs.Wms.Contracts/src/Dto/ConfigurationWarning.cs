namespace Wcs.Wms.Contracts;

/// <summary>
/// Una riga del controllo di configurazione del gestionale (compito 7): oggi una priorita' e un
/// messaggio, domani quello che la procedura decidera' di rispondere.
/// </summary>
/// <remarks>
/// <para>
/// <b>Perche' un dizionario e non un tipo con i campi.</b> Le righe arrivano da
/// <c>dbo.sp_check_configurazione</c>, che le raccoglie in una tabella temporanea e la risponde
/// con un <c>SELECT *</c>: le colonne le decide la procedura, che e' del gestionale e cambia con
/// lui. L'Editor lo sa e non le conosce - ne fa le colonne della griglia cosi' come arrivano - e
/// il confine qui non deve promettere piu' di quello che sa: dichiarare due campi significherebbe
/// che una colonna in piu' sparisce in silenzio nel passaggio.
/// </para>
/// <para>
/// I valori sono testo, e possono essere nulli: una colonna che nel gestionale e' <c>NULL</c>
/// resta nulla, e non diventa la stringa vuota - l'Editor la mostra come cella vuota, che e' cio'
/// che mostrava prima. Il nullo arriva fin qui anche se il serializzatore del gestionale ha
/// <c>NullValueHandling.Ignore</c>: quell'opzione non tocca le voci di un dizionario, e una prova
/// dal lato di la' lo fissa - se sparisse, l'Editor perderebbe la colonna, perche' i nomi delle
/// colonne li prende dalla prima riga.
/// </para>
/// <para>
/// <b>Le chiavi passano dal camelCase del gestionale</b>, il cui risolutore tratta anche quelle
/// dei dizionari: <c>priority</c> e <c>message</c>, le due di oggi, sono gia' minuscole e non
/// cambiano, ma una colonna aggiunta domani con l'iniziale maiuscola arriverebbe con l'iniziale
/// minuscola - e quello che l'Editor scrive in testa alla colonna e' questa chiave.
/// </para>
/// </remarks>
public sealed record ConfigurationWarning(IReadOnlyDictionary<string, string?> Columns);
