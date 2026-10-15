namespace Wcs.Wms.Contracts;

/// <summary>
/// L'esito di una riga di estrazione per unità di carico: è quello che un operatore legge quando
/// un'estrazione non parte o quando è arrivata a destinazione, e finisce nella colonna
/// <c>DISTINTA_ESTRAZIONI_UDC.STATO</c>. I valori numerici sono gli stessi di
/// <c>Wcs.Common.Enums.StatusExtractionLu</c>, l'enumerazione di cui il motore si serviva prima del
/// compito 12 e che dopo quel compito non usa più da nessuna parte: questo contratto non potrebbe
/// comunque riferirsi a quella, perché <c>Wcs.Wms.Contracts</c> non ha dipendenze di progetto, come
/// <see cref="CellDoor"/> e <see cref="CellLockState"/> che sono nella stessa condizione.
/// </summary>
/// <remarks>
/// <b>Cinque valori, e due fasi.</b> Fino al compito 11 erano quattro, e il commento di allora
/// spiegava perché: <c>Extracted</c> apparteneva all'altra fase, quella che chiude il movimento, e
/// quella fase la scriveva il motore per conto suo in SQL
/// (<c>DbInfo.UpdateStatusExtractionLu</c>) - ammetterlo qui avrebbe detto che questa operazione
/// poteva scriverlo, e non era vero. Il compito 12 cancella quella scrittura: la fase che chiude il
/// movimento passa <b>dallo stesso fatto</b>, e il valore che le serve deve esistere di qua. Il
/// vocabolario del fatto si allarga; l'operazione resta una, e resta una segnalazione.
/// <para>
/// <c>NotExtracted</c> (-1) invece <b>non</b> c'è, perché nessun punto del motore lo scrive: un
/// valore in un'enumerazione del confine è un permesso, e non se ne danno per un caso che non esiste.
/// </para>
/// <para>
/// <b>La riga dell'esito è una sola, e si aggiorna.</b> Il gestionale scrive questi valori sulla
/// distinta della riga di estrazione con un upsert sulla chiave <c>ID_RIGA_ESTRAZIONE_UDC</c>
/// (<c>MagazzinoPerWcs.ScriviEsitoRigaSql</c>): la prima pronuncia la crea, le successive la
/// cambiano. L'invariante «una riga di estrazione, una distinta» è quella del campo, e senza di
/// essa il residuo della lavorazione non tornerebbe mai a zero.
/// </para>
/// </remarks>
public enum ExtractionRowStatus
{
    /// <summary>
    /// In estrazione. È il valore che il giro scrive quando ha costruito il comando, <b>e anche</b>
    /// quando la riga era già quella in estrazione e non c'era niente da fare: nel motore era lo zero
    /// iniziale della variabile <c>locked</c>, cioè il caso normale della riga già in lavorazione.
    /// Finché una riga vale questo, la lavorazione non si chiude.
    /// </summary>
    InExtraction = 0,

    /// <summary>
    /// L'unità di carico è arrivata alla postazione: la riga è <b>servita</b> (compito 12, ex
    /// <c>StatusExtractionLu.Extracted</c> scritto dal motore alla chiusura del movimento). È
    /// l'esito finale dell'estrazione riuscita, ed è di gran lunga il più frequente: su
    /// <c>DEMOPLANT_WMS</c>, 419.573 delle 436.083 righe di
    /// <c>DISTINTA_ESTRAZIONI_UDC</c>. È da qui che il gestionale trae la chiusura della lavorazione
    /// (decisione 2 della specifica: «la chiusura la decide il gestionale, alla segnalazione *riga
    /// servita*»), perché una riga che vale questo non conta più nel residuo.
    /// </summary>
    Extracted = 1,

    /// <summary>
    /// L'unità di carico non è raggiungibile: non si trova nelle aree servite dal polmone, oppure il
    /// gestionale non ha risposto mentre la si cercava. Il secondo caso è una degradazione voluta -
    /// un'indisponibilità di pochi secondi non deve fermare le righe che dal gestionale non dipendono.
    /// </summary>
    NotReachable = 2,

    /// <summary>L'unità di carico è già in coda per un'altra riga di estrazione.</summary>
    InOtherExtraction = 3,

    /// <summary>L'unità di carico è bloccata (<c>UDC.BLOCCATO</c>).</summary>
    LoadUnitLocked = 4
}
