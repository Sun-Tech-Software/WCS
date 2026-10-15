namespace Wcs.Engine.Persistence.Entities;

/// <summary>
/// La riga di un dispositivo del diagramma nel database del WCS: configurazione e ultimo stato
/// di un <c>AutomationDevice</c>. Sostituisce <c>NAVIGATOR_ITEMS</c> su SQL Server.
/// </summary>
/// <remarks>
/// Lo schema porta solo le colonne che il motore leggeva o scriveva: quelle che <c>NAVIGATOR_ITEMS</c>
/// aveva e nessuno usava piu' (<c>LOCK_ON_STEP</c>, <c>ID_RUSH_MOVER_GROUP</c>,
/// <c>ASI_COMPACTING</c>, <c>PARTITION_WINDOW</c>, <c>REVERSE_SIDE</c> e simili) non ci sono. La
/// corrispondenza con le vecchie colonne e' documentata in <c>PlantStore</c>. Gli identificatori
/// vuoti diventano nulli nella riga e tornano vuoti in memoria.
/// </remarks>
public sealed class PlantItem
{
    /// <summary>L'identita' del dispositivo, la stessa dell'oggetto in memoria e del diagramma.</summary>
    public Guid ItemId { get; set; }

    public string? Name { get; set; }
    public short DeviceType { get; set; }
    public short Status { get; set; }
    public short LoadUnitStatus { get; set; }
    public string? Asi { get; set; }
    public string? Partition { get; set; }
    public string? Subitem { get; set; }
    public int DelayBlind { get; set; }
    public int TimeOutPlcMoving { get; set; }
    public int LoadUnitImageHeight { get; set; }
    public int LoadUnitImageWidth { get; set; }
    public bool AutoExecuteStep { get; set; }
    public bool AutoUnloadLoadUnit { get; set; }
    public Guid? BuildDestinationItemId { get; set; }
    public Guid? BuildDestinationItemId2 { get; set; }
    public short ConnectionType { get; set; }
    public short MotionType { get; set; }
    public bool GetLoadUnitInfo { get; set; }
    public short LookWhereAllocateLoadUnit { get; set; }
    public Guid? RejectItemWhenNoAllocation { get; set; }
    public Guid? RelocateItemId { get; set; }
    public bool RecordLoadUnitInfo { get; set; }
    public Guid? RejectItemWhenInfoError { get; set; }
    public decimal LinkedCellId { get; set; }
    public int StorageOffset { get; set; }
    public short RecordStorageLength { get; set; }
    public decimal DeviceConnectionId { get; set; }
    public bool ShowInfoMessageAssignedLoadUnit { get; set; }
    public int StoreUnitStep { get; set; }
    public int PickUpUnitStep { get; set; }
    public int OrderNumberInGroup { get; set; }
    public int GroupItemId { get; set; }
    public int GroupRoutePathId { get; set; }
    public int PriorityInRoutePath { get; set; }
    public int TimeLapseToExecuteStep { get; set; }
    public int BelongingGroupId { get; set; }
    public int GroupLevelId { get; set; }
    public int WaitingTimeForcedLoading { get; set; }
    public bool GroupLoadingActive { get; set; }
    public bool GroupTransferringActive { get; set; }
    public bool GroupedMoveLoadUnitTo { get; set; }
    public bool RespectQueue { get; set; }
    public bool WaitBelongedItemsEmptyBeforeLoading { get; set; }
    public int StorageOffsetReversed { get; set; }
    public string? StoragePartition { get; set; }
    public string? StoragePartitionReversed { get; set; }
    public Guid? TriggerItemGetLoadUnitInfo { get; set; }
    public Guid? RunningStepId { get; set; }
    public Guid? TriggerItemToExtractPoint1 { get; set; }
    public Guid? TriggerItemToExtractPoint2 { get; set; }
    public bool DistributeOnBuffers { get; set; }
    public bool AddStorageLengthLoadUnitDepth { get; set; }
    public bool EnableDefragmentation { get; set; }
    public decimal CellToDefragmentId { get; set; }
    public bool AcceptLoadUnitIfDestinationIsAvailable { get; set; }
    public int MaxUnloadWeight { get; set; }
    public bool ExtractionCheckPoint { get; set; }
    public Guid? RejectDestinationUserRequest { get; set; }
    public int TimeOutGetLoadUnitInfo { get; set; }
    public bool ToBeFiltered { get; set; }
    public bool OnLoadLoadUnitActiveFilter { get; set; }
    public int WaitLookingLevels { get; set; }

    /// <summary>
    /// Dove sta il dispositivo nel disegno: ascissa dell'angolo in alto a sinistra, nelle unita'
    /// del disegno. Nulla significa <b>non collocato</b>, e non l'origine: un dispositivo a (0, 0)
    /// sta nell'angolo, uno con la geometria nulla non sta da nessuna parte, e chi disegna deve
    /// poterlo distinguere. Il motore non legge nessuna delle cinque colonne che seguono: sono
    /// dati di chi disegna, custoditi qui come prima stavano nel blob del diagramma.
    /// </summary>
    public double? X { get; set; }

    /// <summary>Ordinata dell'angolo in alto a sinistra. Nulla: non collocato.</summary>
    public double? Y { get; set; }

    /// <summary>
    /// Larghezza del **riquadro che contiene il dispositivo gia' ruotato**, non la sua larghezza
    /// propria: e' l'AABB che Syncfusion serializza in <c>BoundingRect</c>, e a 90 gradi larghezza e
    /// altezza risultano scambiate. Chi disegna ricava la dimensione propria invertendo l'inviluppo
    /// (si veda <c>Wcs.WebEditor.Drawing.NodePlacement</c>). Nulla: non collocato.
    /// </summary>
    public double? Width { get; set; }

    /// <summary>
    /// Altezza del riquadro che contiene il dispositivo gia' ruotato, con la stessa avvertenza di
    /// <see cref="Width"/>: non e' l'altezza propria. Nulla: non collocato.
    /// </summary>
    public double? Height { get; set; }

    /// <summary>
    /// Angolo in gradi, positivo in senso orario, e non un quarto di giro: misurato sui 51
    /// dispositivi del disegno dimostrativo, diciotto portano un angolo diverso da zero e i valori
    /// sono -180, -90, 0, 35 e 90. Uno solo non e' multiplo di 90, e basta quello: un enumeratore a
    /// quarti di giro perderebbe il disegno. Nulla: non collocato.
    /// </summary>
    public double? Rotation { get; set; }

    /// <summary>
    /// Token opaco della geometria corrente. Cambia insieme ai cinque valori geometrici e permette
    /// di rifiutare una scrittura costruita su una geometria ormai superata.
    /// </summary>
    public Guid GeometryVersion { get; set; }
}
