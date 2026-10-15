namespace Wcs.Engine.Persistence.Entities;

/// <summary>
/// La prenotazione di deposito di una missione che la ricarica ha chiuso, da restituire al gestionale
/// al passaggio a Run (<c>ReservationsOfMissionsClosedOnReload</c>). Sta nel database del WCS perche'
/// resti da restituire anche se l'host si ferma prima del Run (risposta 2 del proprietario del 9/10);
/// restituita, o scartata con la riga di avviso, la riga si cancella.
/// </summary>
public sealed class ReservationToGiveBack
{
    /// <summary>Chiave tecnica, crescente.</summary>
    public long Id { get; set; }

    /// <summary>La missione chiusa, la cui prenotazione si restituisce. Unica.</summary>
    public Guid MissionId { get; set; }
}
