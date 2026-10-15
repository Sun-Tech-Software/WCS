using System.ComponentModel;
using static Wcs.Common.Enums;

namespace Wcs.Api.Contracts.Dtos;

/// <summary>
/// La configurazione di un dispositivo dell'impianto: le colonne di <c>PlantItems</c> che
/// l'Editor governa. Non porta lo stato dal vivo (<c>Status</c>, <c>LoadUnitStatus</c>,
/// <c>RunningStepId</c>): quello sta in <see cref="ItemStatus"/>. È una classe con i setter
/// perché la griglia delle proprietà dell'Editor scrive direttamente qui.
/// </summary>
/// <remarks>
/// <para>
/// Gli attributi di <c>System.ComponentModel</c> sono quelli di <c>AutomationDevice</c> nel
/// motore, categoria per categoria e testo per testo: la griglia dell'Editor mostra il DTO e non
/// più il dispositivo vivo, e senza di essi era diventata un elenco alfabetico senza gruppi. La
/// corrispondenza fra i nomi del DTO e quelli del motore è quella di <c>PlantStore.Snapshot</c>.
/// <c>[Browsable(true)]</c> non è ricopiato perché è il valore predefinito: nessuna delle
/// proprietà che il motore nasconde con <c>[Browsable(false)]</c> arriva fin qui.
/// </para>
/// <para>
/// I cinque <c>short</c> che portano un enum (<c>DeviceType</c>, <c>ConnectionType</c>,
/// <c>MotionType</c>, <c>LookWhereAllocateLoadUnit</c>, <c>RecordStorageLength</c>) hanno il
/// convertitore <see cref="EnumCodeConverter{TEnum}"/>: nel motore erano tipizzati con l'enum e la
/// griglia ne mostrava il testo, qui sono numeri e senza convertitore si vedrebbe la cifra. Gli
/// elenchi di dispositivi (<c>RejectItemWhenNoAllocation</c>, <c>RejectItemWhenInfoError</c>,
/// <c>RejectDestinationUserRequest</c>, <c>RelocateItemId</c>) hanno invece bisogno dell'impianto
/// per esistere e restano fuori dai contratti: li aggiunge l'Editor con un
/// <c>TypeDescriptionProvider</c>.
/// </para>
/// </remarks>
public sealed class PlantItemConfigDto
{
    [Category("Device Option"), DisplayName("ItemGuid"), Description("Returns or Sets the unique identifier of the item"), ReadOnly(true)]
    public Guid ItemId { get; set; }

    [Category("Basic Option"), DisplayName("Name"), Description("Returns or Sets the name of the AutomationDevice")]
    public string? Name { get; set; }

    [Category("Basic Option"), DisplayName("DeviceType"), Description("Element Type"), TypeConverter(typeof(EnumCodeConverter<DeviceTypes>))]
    public short DeviceType { get; set; }

    [Category("Device Option"), DisplayName("ASI"), Description("Returns or Sets the ASI of the element")]
    public string? Asi { get; set; }

    [Category("Device Option"), DisplayName("PARTITION"), Description("Returns or Sets the PARTITION of the element")]
    public string? Partition { get; set; }

    [Category("Device Option"), DisplayName("SUBITEM"), Description("Returns or Sets the SUBITEM of the element")]
    public string? Subitem { get; set; }

    [Category("Test Options"), DisplayName("DelayBlind"), Description("Returns or Sets the waiting time in milliseconds for the move-completed report that follows a move command, this function is valid only for ConnectionType=Test")]
    public int DelayBlind { get; set; }

    [Category("Device Option"), DisplayName("TimeOutPlcMoving"), Description("Returns or Sets the waiting time for the move-completed report in milliseconds before reporting the move TimeOut")]
    public int TimeOutPlcMoving { get; set; }

    [Category("Basic Option"), DisplayName("LuImageHeight"), Description("Returns or Sets the graphic height of the load unit image (0 = default)")]
    public int LoadUnitImageHeight { get; set; }

    [Category("Basic Option"), DisplayName("LuImageWidth"), Description("Returns or Sets the graphic width of the load unit image (0 = default)")]
    public int LoadUnitImageWidth { get; set; }

    [Category("Basic Option"), DisplayName("AutoExecuteStep"), Description("Returns or Sets whether to automatically execute the movement micromission relating to the loaded PathMission")]
    public bool AutoExecuteStep { get; set; }

    [Category("Basic Option"), DisplayName("AutoUnloadLU"), Description("Returns or Sets whether the loaded PathMission will be immediately logically downloaded")]
    public bool AutoUnloadLoadUnit { get; set; }

    [Category("Path Option"), DisplayName("IdDestinationItem"), Description("Returns or Sets on which element to move the loaded PathMission (This function is valid only if there are no destinations previously assigned to the PathMission)")]
    public Guid? BuildDestinationItemId { get; set; }

    [Category("Path Option"), DisplayName("IdDestinationItem2"), Description("Returns or Sets on which element to move the loaded PathMission (This function is valid only if there are no destinations previously assigned to the PathMission)")]
    public Guid? BuildDestinationItemId2 { get; set; }

    [Category("Device Option"), DisplayName("ConnectionType"), Description("Returns or Sets the connection type to use"), TypeConverter(typeof(EnumCodeConverter<ConnectionTypes>))]
    public short ConnectionType { get; set; }

    [Category("Basic Option"), DisplayName("MotionType"), Description("Returns or sets the Motion Type"), TypeConverter(typeof(EnumCodeConverter<MotionTypes>))]
    public short MotionType { get; set; }

    [Category("Basic Option"), DisplayName("GetLuInfo"), Description("Returns or sets whether to request dimensional data and identification codes from the PLC when a PathMission is located on the AutomationDevice")]
    public bool GetLoadUnitInfo { get; set; }

    [Category("Path Option"), DisplayName("LookWhereAllocateLu"), Description("Returns or Sets where to store the PathMission and if so, builds the movement mission"), TypeConverter(typeof(EnumCodeConverter<LookWhereAllocateLuTypes>))]
    public short LookWhereAllocateLoadUnit { get; set; }

    [Category("Path Option"), DisplayName("RejectItemDestinationAllocateLu"), Description("Returns or Sets where the PathMission will be rejected if there are no cells available for storage")]
    public Guid? RejectItemWhenNoAllocation { get; set; }

    [Category("Basic Option"), DisplayName("RelocateItemId"), Description("It is the connected AutomationDevice responsible for storage (The property is valid only for Storage type items)")]
    public Guid? RelocateItemId { get; set; }

    [Category("Basic Option"), DisplayName("RecordLuDataOnGetLuInfo"), Description("Returns or sets whether to register a new LU when receiving identification and dimensional data from the PLC")]
    public bool RecordLoadUnitInfo { get; set; }

    [Category("Path Option"), DisplayName("RejectItemDestinationIdGetLuInfo"), Description("Returns or sets where the PathMission will be rejected in case of error in reading the dimensional and identification information from the PLC")]
    public Guid? RejectItemWhenInfoError { get; set; }

    [Category("Basic Option"), DisplayName("CellId"), Description("Returns or sets the warehouse cell connected to the element, by valuing this property the system will track the PathMission that passes over it")]
    public decimal LinkedCellId { get; set; }

    [Category("Storing Option"), DisplayName("StorageOffset"), Description("Returns or Sets the storage quota offset")]
    public int StorageOffset { get; set; }

    [Category("Basic Option"), DisplayName("RecordStorageLength"), Description("Returns or sets the Storage Quota Posting Type"), TypeConverter(typeof(EnumCodeConverter<RecordStorageTypes>))]
    public short RecordStorageLength { get; set; }

    [Category("Device Option"), DisplayName("DeviceConnectionId"), Description("Returns or Sets the PCL device identifier")]
    public decimal DeviceConnectionId { get; set; }

    [Category("Basic Option"), DisplayName("ShowInfoMessageAssignedLu"), Description("Returns or Sets if the PathMission is assigned to a note, shows an information message of the connected Note (This function is active only for Buffer type Items)")]
    public bool ShowInfoMessageAssignedLoadUnit { get; set; }

    [Category("Basic Option"), DisplayName("StoreUnitStep"), Description("Returns or sets the value of the deposit for the balancing ratio between deposit and withdrawal")]
    public int StoreUnitStep { get; set; }

    [Category("Basic Option"), DisplayName("PickUpUnitStep"), Description("Returns or sets the value of the withdrawal for the balancing ratio between deposit and withdrawal")]
    public int PickUpUnitStep { get; set; }

    [Category("Path Option"), DisplayName("OrderNumberGroup"), Description("Returns or Sets the Cartesian position number starting from top to bottom or right to left plus (top = 0, right = 0)")]
    public int OrderNumberInGroup { get; set; }

    [Category("Basic Option"), DisplayName("GroupItemId"), Description("Returns or sets the grouping number that allows the movement of multiple load units at the same time")]
    public int GroupItemId { get; set; }

    [Category("Group Option"), DisplayName("GroupRoutePathId"), Description("Returns or sets the level identifier of a group of AutomationDevices, this property is used during path processing to avoid returning to the same level of origin")]
    public int GroupRoutePathId { get; set; }

    [Category("Path Option"), DisplayName("PriorityInRoutePath"), Description("Returns or sets the number of movement priorities in picking and depositing when multiple elements are connected to the same element, for example the CircularDistributor or the shuttle with multiple load units, in this way a picking or depositing priority can be set")]
    public int PriorityInRoutePath { get; set; }

    [Category("Group Option"), DisplayName("TimeLapseToExecuteStep"), Description("Returns or Sets the test execution time interval of the ExecuteStep command of a GroupItem")]
    public int TimeLapseToExecuteStep { get; set; }

    [Category("Basic Option"), DisplayName("BelongingGroupId"), Description("Returns or Sets the belonging AutomationDevice")]
    public int BelongingGroupId { get; set; }

    [Category("Group Option"), DisplayName("GroupLevelId"), Description("Returns or Sets the source level from which the item group will be accessed")]
    public int GroupLevelId { get; set; }

    [Category("Group Option"), DisplayName("WaitingTimeForcedLoading"), Description("Returns or Sets the maximum waiting time for a GroupItem to load all the elements belonging to it")]
    public int WaitingTimeForcedLoading { get; set; }

    [Category("Group Option"), DisplayName("GroupLoadingActive"), Description("Returns or Sets whether the group can pick up PathMission from the connected AutomationDevices Note: The option is active only for ItemType GroupOfAutomationDevices")]
    public bool GroupLoadingActive { get; set; }

    [Category("Group Option"), DisplayName("GroupTransferringActive"), Description("Returns or Sets whether the group can pick up PathMission in the connected AutomationDevices Note: The Option is active only for ItemType GroupOfAutomationDevices and only for those with Items not connected to Storage")]
    public bool GroupTransferringActive { get; set; }

    [Category("Group Option"), DisplayName("GroupedMoveLuTo"), Description("Returns or Sets whether the MOVE_LU_TO command is grouped for all the PathMissions contained in the AutomationDevices of the GroupItem")]
    public bool GroupedMoveLoadUnitTo { get; set; }

    [Category("Basic Option"), DisplayName("RespectQueue"), Description("Returns or sets whether the AutomationDevice must respect the MissionStep queue")]
    public bool RespectQueue { get; set; }

    [Category("Group Option"), DisplayName("WaitBelongedItemsEmptyBeforeLoading"), Description("Returns or Sets whether to wait until all elements of the group are empty before loading the PathMissions")]
    public bool WaitBelongedItemsEmptyBeforeLoading { get; set; }

    [Category("Storing Option"), DisplayName("StorageOffsetReversed"), Description("Returns or Sets the Offset of the inverted storage quota")]
    public int StorageOffsetReversed { get; set; }

    [Category("Storing Option"), DisplayName("StoragePartition"), Description("Returns or Sets the storage partition in case of fixed storage")]
    public string? StoragePartition { get; set; }

    [Category("Storing Option"), DisplayName("StoragePartitionReversed"), Description("Returns or Sets the storage partition in case of inverted fixed storage")]
    public string? StoragePartitionReversed { get; set; }

    [Category("Basic Option"), DisplayName("TriggerIdGetLuInfo"), Description("Returns or sets the source item for which to request the dimensional data and the identification codes from the PLC (The source item can be that of the previous step or the mission source)")]
    public Guid? TriggerItemGetLoadUnitInfo { get; set; }

    [Category("Basic Option"), DisplayName("TriggerItemToExtractPoint1"), Description("Returns or Sets the item to check before performing an LU extraction")]
    public Guid? TriggerItemToExtractPoint1 { get; set; }

    [Category("Basic Option"), DisplayName("TriggerItemToExtractPoint2"), Description("Returns or Sets the item to check before performing an LU extraction")]
    public Guid? TriggerItemToExtractPoint2 { get; set; }

    [Category("Path Option"), DisplayName("DistributeOnBuffers"), Description("Returns or Sets if when an extraction PathMission passes on this Item the system will balance the output on the buffers reachable from it using ReachableBuffers property")]
    public bool DistributeOnBuffers { get; set; }

    [Category("Storing Option"), DisplayName("AddStorageLengthLuDepth"), Description("Returns or Sets whether to add the UDC depth to the storage quota")]
    public bool AddStorageLengthLoadUnitDepth { get; set; }

    [Category("Defragment Option"), DisplayName("EnableDefragmentation"), Description("Returns or Sets whether compaction is enabled during loading of elements that are GroupItems")]
    public bool EnableDefragmentation { get; set; }

    [Category("Defragment Option"), DisplayName("IdCellToDefragment"), Description("Returns or Sets the id of the cell to compact")]
    public decimal CellToDefragmentId { get; set; }

    [Category("Path Option"), DisplayName("AcceptLUIfDestinationIsAvailable"), Description("Returns or Sets whether to accept a LU if the target is available")]
    public bool AcceptLoadUnitIfDestinationIsAvailable { get; set; }

    [Category("Basic Option"), DisplayName("MaxUnloadWeight"), Description("Returns or Sets the maximum weight that can be loaded on the AutomationDevice")]
    public int MaxUnloadWeight { get; set; }

    [Category("Path Option"), DisplayName("ExtractionCheckPoint"), Description("Returns or Sets the point at which to consider the extracted UDCs for inventory notes")]
    public bool ExtractionCheckPoint { get; set; }

    [Category("Path Option"), DisplayName("RejectDestinationUserRequest"), Description("Returns or Sets the rejection point on user request")]
    public Guid? RejectDestinationUserRequest { get; set; }

    [Category("Device Option"), DisplayName("TimeOutGetLuInfo"), Description("Returns or Sets the waiting time for the response from the PLC for GetInfo")]
    public int TimeOutGetLoadUnitInfo { get; set; }

    [Category("Path Option"), DisplayName("ToBeFiltered"), Description("Returns or Sets whether the element should be considered filterable by PathMission with ActiveFilter set to true")]
    public bool ToBeFiltered { get; set; }

    [Category("Path Option"), DisplayName("OnLoadLuActiveFilter"), Description("Returns or Sets whether when a PathMission is loaded it sets its ActiveFilter property to true")]
    public bool OnLoadLoadUnitActiveFilter { get; set; }

    [Category("Path Option"), DisplayName("WaitLookingLevels"), Description("Returns or Sets the number of items to check to see if PathMission is arriving for loading")]
    public int WaitLookingLevels { get; set; }
}
