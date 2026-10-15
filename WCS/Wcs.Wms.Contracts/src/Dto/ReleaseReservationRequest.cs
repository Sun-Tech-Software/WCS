namespace Wcs.Wms.Contracts;

/// <summary>
/// La missione che lascia la sua prenotazione di deposito (compito 17). Un corpo per un solo campo
/// perché il rilascio è una scrittura, e le scritture di questo confine viaggiano tutte come POST
/// con corpo (<see cref="WarehouseRoutes.ReleaseReservation"/>), non come segnaposto nel percorso.
/// </summary>
/// <param name="MissionId">La missione, la stessa chiave con cui la prenotazione era stata presa.</param>
public sealed record ReleaseReservationRequest(Guid MissionId);
