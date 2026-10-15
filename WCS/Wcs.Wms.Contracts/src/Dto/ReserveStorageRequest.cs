namespace Wcs.Wms.Contracts;

/// <summary>
/// Quello che serve al gestionale per scegliere - e scrivere - dove depositare (compito 12, ex
/// <c>Navigator.WmsBusinessArea.CelleMagazzino.CercaDestinazioneUdc</c> con
/// <c>WMS.Services.Interface.Models.OpzioneRicercaCelle</c>). Non è una ricerca: il motore la chiama
/// sempre per prenotare una posizione, e quando <see cref="AssignPosition"/> è vero l'operazione
/// scrive - è l'unica del percorso caldo che lo fa. I sei campi della versione precedente non
/// reggevano alla prova dei chiamanti veri: tre di essi (l'area preferita, la profondità, il tipo
/// unità di carico) non corrispondevano a niente che <c>PathMission.FindRouteAllocateLu</c> o
/// <c>AutomationDeviceStorageManager.MoveObstacleLu</c> impostassero, mentre mancavano gli elenchi
/// che quei due chiamanti costruiscono davvero - aree di ricerca, celle da escludere, dispositivi
/// raggiungibili - che sono identificativi del WCS (aree note al motore, <c>ItemGuid</c> dei
/// dispositivi), non del gestionale.
/// </summary>
/// <param name="MissionId">La missione per cui si prenota - <see cref="StorageReservation.MissionId"/> la riporta indietro.</param>
/// <param name="LoadUnitId">L'unità di carico da posizionare.</param>
/// <param name="WarehouseId">Il magazzino in cui cercare.</param>
/// <param name="SearchAreaIds">Le aree ammesse alla ricerca, identificativi noti al WCS (ex <c>DbInfo.GetAutomaticAreasByWarehouseId</c>).</param>
/// <param name="ExcludedCellIds">Le celle da escludere dalla ricerca (ad esempio quella di provenienza, per non depositare dove si è appena prelevato).</param>
/// <param name="ReachableDeviceIds">I dispositivi di automazione raggiungibili che possono operare il deposito, per <see cref="StorageCellSearchMode.FreeAutomaticOperator"/> e affini.</param>
/// <param name="AreaSelectionMode">Se esplorare tutte le aree ammesse o fermarsi alla prima.</param>
/// <param name="CellSearchMode">Quali celle ammette la ricerca.</param>
/// <param name="CellFullnessPreference">Fra le celle candidate, se preferire le più piene o le più vuote.</param>
/// <param name="SearchOperatorType">Chi effettua la ricerca, manuale o automatico.</param>
/// <param name="AssignPosition">Se vero, la posizione trovata viene scritta (prenotata); se falso, è una sola verifica di disponibilità.</param>
/// <param name="VerifyPosition">Se il gestionale deve verificare la posizione prima di rispondere.</param>
/// <param name="RequestedByWorkstationId">La postazione che sta chiedendo, per la tracciabilità della scrittura.</param>
/// <param name="RequestedByUserId">L'utente che sta chiedendo, per la tracciabilità della scrittura.</param>
public sealed record ReserveStorageRequest(
    Guid MissionId,
    decimal LoadUnitId,
    decimal WarehouseId,
    IReadOnlyList<decimal> SearchAreaIds,
    IReadOnlyList<decimal> ExcludedCellIds,
    IReadOnlyList<Guid> ReachableDeviceIds,
    StorageAreaSelectionMode AreaSelectionMode,
    StorageCellSearchMode CellSearchMode,
    CellFullnessPreference CellFullnessPreference,
    StorageSearchOperatorType SearchOperatorType,
    bool AssignPosition,
    bool VerifyPosition,
    string RequestedByWorkstationId,
    string RequestedByUserId);
