namespace Wcs.Wms.Contracts;

/// <summary>
/// Come è finita la ricerca di una referenza (<see cref="ReferenceSearchResult"/>, compito 20). Non
/// è un booleano perché i due modi di non trovare nulla non sono la stessa cosa per chi guarda
/// l'impianto: se il polmone non serve nessuna area di prelievo il magazzino è configurato male, e
/// nessuna referenza potrà mai arrivare lì; se invece nessuna unità di carico ha quella referenza
/// (o quelle che ce l'hanno sono bloccate) la configurazione è a posto e manca la merce. Il motore
/// lascia all'operatore due messaggi diversi, e li lasciava anche prima del compito 20: uno veniva
/// dalla sua lettura delle aree, l'altro dalla ricerca.
/// </summary>
public enum ReferenceSearchOutcome : byte
{
    /// <summary>Un'unità di carico con quella referenza, e la cella in cui sta.</summary>
    Found = 0,

    /// <summary>Il polmone non serve nessuna area di prelievo: non c'è niente da cercare.</summary>
    NoPickingAreas = 1,

    /// <summary>Nessuna unità di carico raggiungibile ha quella referenza.</summary>
    NotFound = 2
}
