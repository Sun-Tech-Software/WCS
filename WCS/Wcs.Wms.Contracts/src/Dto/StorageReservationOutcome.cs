namespace Wcs.Wms.Contracts;

/// <summary>
/// L'esito di una prenotazione di deposito (<see cref="StorageReservation"/>, compito 12). Gemello
/// lato confine di <c>WMS.Services.Interface.Enumeratori.TipiRisultato</c>, che ne ha dodici valori:
/// il motore, prima del compito 12, ne distingueva solo tre alla fonte (cella trovata, unità di
/// carico senza referenze, tutto il resto), ma l'esito intero viaggia nel contratto - non solo i tre
/// che oggi hanno un lettore - perché tagliare gli altri nove avrebbe ripetuto lo stesso difetto già
/// segnalato per <see cref="StorageReservation"/>: un tipo che non regge alla prova del codice che
/// lo produce.
/// </summary>
public enum StorageReservationOutcome : byte
{
    CellFound = 0,
    LoadUnitCodeRequired = 1,
    LoadUnitIdRequired = 2,
    WarehouseRequired = 3,
    LoadUnitCodeNotFound = 4,
    LoadUnitHasNoReferences = 5,
    NoFreeCells = 6,
    NoDoorSelected = 7,
    NoCellSelected = 8,
    SelectedDoorNotFound = 9,
    LoadUnitPositioned = 10,
    PositionUpdateFailed = 11
}
