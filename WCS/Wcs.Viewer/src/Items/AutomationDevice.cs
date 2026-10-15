using System.ComponentModel;
using Helper.Converters;
using Wcs.Api.Contracts.Dtos;
using Wcs.Common;

namespace Wcs.Viewer.Items;

public partial class AutomationDevice
{
    #region Private Fields

    private bool _acceptLuIfDestinationIsAvailable;
    private bool _addStorageLengthLuDepth;
    private string _asi = "0000";
    private bool _autoExecuteStep = true;
    private bool _autoUnloadLu;
    private int _belongingGroupId;
    private Enums.ConnectionTypes _connectionType;
    private int _delayBlind = 500;
    private Guid _destinationItemId = Guid.Empty;
    private Guid _destinationItemId2 = Guid.Empty;
    private int _deviceConnectionId;
    private Enums.DeviceTypes _deviceType = Enums.DeviceTypes.None;
    private bool _distributeOnBuffers;
    private bool _enableDefragmentation;
    private bool _extractionCheckPoint;
    private bool _getLuInfo;
    private bool _groupedMoveLuTo;
    private int _groupItemId;
    private int _groupLevelId;
    private bool _groupLoadingActive;
    private int _groupRoutePathId;
    private bool _groupTransferringActive;
    private decimal _idCellToDefragment;
    private decimal _linkedCellId;
    private Enums.LookWhereAllocateLuTypes _lookWhereAllocateLu = Enums.LookWhereAllocateLuTypes.No;
    private int _maxUnloadWeight;
    private Enums.MotionTypes _movementType;
    private string? _name;
    private bool _onLoadLuActiveFilter;
    private int _orderNumberGroup;
    private string _partition = "0001";
    private int _pickUpUnitStep;
    private int _priorityInRoutePath;
    private bool _recordLuDataOnGetLuInfo;
    private Enums.RecordStorageTypes _recordStorageLength;
    private Guid _rejectDestinationUserRequest = Guid.Empty;
    private Guid _rejectItemDestinationAllocateLu = Guid.Empty;
    private Guid _rejectItemDestinationIdGetLuInfo = Guid.Empty;
    private Guid _relocateItemId = Guid.Empty;
    private bool _respectQueue;
    private Guid _runningStepId = Guid.Empty;
    private bool _showInfoMessageAssignedLu;
    private Enums.DeviceStatusTypes _status = Enums.DeviceStatusTypes.Ready;
    private int _storageOffset;
    private int _storageOffsetReversed;
    private string? _storagePartition = string.Empty;
    private string? _storagePartitionReversed = string.Empty;
    private int _storeUnitStep;
    private string _subitem = "0001";
    private int _timeLapseToExecuteStep = 1000;
    private int _timeOutGetLuInfo = 2000;
    private int _timeOutPlcMoving = 300000;
    private bool _toBeFiltered;
    private Guid _triggerItemToExtractPoint1 = Guid.Empty;
    private Guid _triggerItemToExtractPoint2 = Guid.Empty;
    private Guid _triggerItemToGetLuInfo = Guid.Empty;
    private bool _waitBelongedItemsEmptyBeforeLoading;
    private Guid _waitingStepId;
    private int _waitingTimeForcedLoading;
    private int _waitLookingLevels;

    #endregion Private Fields

    #region Public Constructors

    public AutomationDevice()
    {

    }

    #endregion Public Constructors

    #region Public Methods

    /// <summary>
    ///     Crea il dispositivo a partire dal DTO del piano: identita', configurazione principale e stato dal vivo.
    ///     La configurazione completa (tutte le colonne di PlantItems) arriva con il piano 3a-2; qui i campi che il
    ///     DTO non porta restano ai valori predefiniti.
    /// </summary>
    public static AutomationDevice From(PlantItemDto dto) => new()
    {
        ItemGuid = dto.ItemId,
        Name = dto.Name,
        DeviceType = dto.DeviceType,
        ASI = dto.Asi ?? "0000",
        PARTITION = dto.Partition ?? "0001",
        SUBITEM = dto.Subitem ?? "0001",
        ConnectionType = dto.ConnectionType,
        MotionType = dto.MotionType,
        LinkedCellId = dto.LinkedCellId,
        RelocateItemId = dto.RelocateItemId ?? Guid.Empty,
        DeviceConnectionId = (int)dto.DeviceConnectionId,
        GroupItemId = dto.GroupItemId,
        BelongingGroupId = dto.BelongingGroupId,
        GroupLevelId = dto.GroupLevelId,
        Status = dto.Live.Status,
        RunningStepId = dto.Live.RunningStepId,
        WaitingStepId = dto.Live.WaitingStepId,
    };

    #endregion Public Methods

    #region Public Properties

    /// <summary>
    ///     Returns or Sets whether to add the UDC depth to the storage quota
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets whether to add the UDC depth to the storage quota")]
    [DisplayName("AddStorageLengthLuDepth")]
    [Category("Storing Option")]
    public bool AddStorageLengthLuDepth
    {
        get => _addStorageLengthLuDepth;
        set
        {
            if (_addStorageLengthLuDepth == value) return;
            _addStorageLengthLuDepth = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or Sets the Asi of the element
    /// </summary>
    /// <returns></returns>
    [DisplayName("ASI")]
    [Description("Returns or Sets the ASI of the element")]
    [Category("Device Option")]
    public string ASI
    {
        get => _asi;
        set
        {
            if (_asi == "") return;
            _asi = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or Sets whether to automatically execute the movement micromission relating to the loaded PathMission)
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or Sets whether to automatically execute the movement micromission relating to the loaded PathMission")]
    [DisplayName("AutoExecuteStep")]
    [Category("Basic Option")]
    public bool AutoExecuteStep
    {
        get => _autoExecuteStep;
        set
        {
            if (_autoExecuteStep == value) return;
            _autoExecuteStep = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or Sets whether the loaded PathMission will be immediately logically downloaded
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets whether the loaded PathMission will be immediately logically downloaded")]
    [DisplayName("AutoUnloadLU")]
    [Category("Basic Option")]
    public bool AutoUnloadLu
    {
        get => _autoUnloadLu;
        set
        {
            if (_autoUnloadLu == value) return;
            _autoUnloadLu = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or Sets the belonging AutomationDeviceBase
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the belonging AutomationDevice")]
    [DisplayName("BelongingGroupId")]
    [Category("Basic Option")]
    public int BelongingGroupId
    {
        get => _belongingGroupId;
        set
        {
            if (_belongingGroupId == value)
                return;
            _belongingGroupId = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or Sets the connection type to use
    /// </summary>
    /// <returns></returns>
    [DisplayName("ConnectionType")]
    [Browsable(true)]
    [Description("Returns or Sets the connection type to use")]
    [Category("Device Option")]
    [TypeConverter(typeof(PropertyTypeConverter))]
    public Enums.ConnectionTypes ConnectionType
    {
        get => _connectionType;
        set
        {
            if (_connectionType == value)
                return;
            _connectionType = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or Sets the waiting time in milliseconds for the move-completed report that follows a move command, this
    ///     function is valid only for ConnectionType=Test
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or Sets the waiting time in milliseconds for the move-completed report that follows a move command, this function is valid only for ConnectionType=Test")]
    [DisplayName("DelayBlind")]
    [Category("Test Options")]
    public int DelayBlind
    {
        get => _delayBlind;
        set
        {
            if (_delayBlind == value) return;
            _delayBlind = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or Sets the PCL PCM identifier
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the PCL device identifier")]
    [DisplayName("DeviceConnectionId")]
    [Category("Device Option")]
    public int DeviceConnectionId
    {
        get => _deviceConnectionId;
        set
        {
            if (_deviceConnectionId == value) return;
            _deviceConnectionId = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Element MissionType
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Element Type")]
    [DisplayName("DeviceType")]
    [Category("Basic Option")]
    [TypeConverter(typeof(PropertyTypeConverter))]
    public Enums.DeviceTypes DeviceType
    {
        get => _deviceType;
        set
        {
            if (_deviceType == value) return;
            _deviceType = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or Sets whether compaction is enabled during loading of elements that are GroupItems
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets whether compaction is enabled during loading of elements that are GroupItems")]
    [DisplayName("EnableDefragmentation")]
    [Category("Defragment Option")]
    public bool EnableDefragmentation
    {
        get => _enableDefragmentation;
        set
        {
            if (_enableDefragmentation == value) return;
            _enableDefragmentation = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or Sets the error number of the move-completed report in case of ConnectionType=Test
    /// </summary>
    /// <returns></returns>
    [DisplayName("ErrorCodeResult")]
    [Description("Returns or Sets the error number of the move-completed report in case of ConnectionType=Test")]
    [Category("Test Options")]
    public int ErrorCodeResult { get; set; }

    /// <summary>
    ///     Returns or sets whether to request dimensional data and identification codes from the Connection when a PathMission
    ///     is located on the AutomationDeviceBase
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or sets whether to request dimensional data and identification codes from the PLC when a PathMission is located on the AutomationDevice")]
    [DisplayName("GetLUInfo")]
    [Category("Basic Option")]
    public bool GetLuInfo
    {
        get => _getLuInfo;
        set
        {
            if (_getLuInfo == value) return;
            _getLuInfo = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or sets the grouping number that allows the movement of multiple load units at the same time
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or sets the grouping number that allows the movement of multiple load units at the same time")]
    [DisplayName("GroupItemId")]
    [Category("Basic Option")]
    public int GroupItemId
    {
        get => _groupItemId;
        set
        {
            if (_groupItemId == value) return;
            _groupItemId = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or Sets the id of the cell to compact
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the id of the cell to compact")]
    [DisplayName("IdCellToDefragment")]
    [Category("Defragment Option")]
    public decimal IdCellToDefragment
    {
        get => _idCellToDefragment;
        set
        {
            if (_idCellToDefragment == value) return;
            _idCellToDefragment = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or Sets the unique identifier of the item
    /// </summary>
    /// <returns></returns>
    [DisplayName("ItemGuid")]
    [Browsable(true)]
    [Description("Returns or Sets the unique identifier of the item")]
    [Category("Device Option")]
    public Guid ItemGuid { get; internal set; } = Guid.NewGuid();

    /// <summary>
    ///     Returns or sets the warehouse cell connected to the element, by valuing this property the system will track the
    ///     PathMission that passes over it
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or sets the warehouse cell connected to the element, by valuing this property the system will track the PathMission that passes over it")]
    [DisplayName("CellId")]
    [Category("Basic Option")]
    public decimal LinkedCellId
    {
        get => _linkedCellId;
        set
        {
            if (_linkedCellId == value) return;
            _linkedCellId = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or Sets the PathMission loaded on the AutomationDeviceBase
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the PathMission loaded on the AutomationDevice")]
    [DisplayName("LoadedLu")]
    [Category("Basic Option")]
    [ReadOnly(true)]
    public PathMission? LoadedLu
    {
        get { return Navigator.ActiveMissions.FirstOrDefault(x => x != null && x.PositionItemId == ItemGuid); }
    }

    /// <summary>
    ///     Returns or Sets the maximum weight that can be loaded on the AutomationDeviceBase
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the maximum weight that can be loaded on the AutomationDevice")]
    [DisplayName("MaxUnloadWeight")]
    [Category("Basic Option")]
    public int MaxUnloadWeight
    {
        get => _maxUnloadWeight;
        set
        {
            if (_maxUnloadWeight == value) return;
            _maxUnloadWeight = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or sets the Motion MissionType
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or sets the Motion Type")]
    [DisplayName("MotionType")]
    [Category("Basic Option")]
    [TypeConverter(typeof(PropertyTypeConverter))]
    public Enums.MotionTypes MotionType
    {
        get => _movementType;
        set
        {
            if (_movementType == value) return;
            _movementType = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or Sets the result type of the move-completed report
    /// </summary>
    /// <returns></returns>
    [DisplayName("MoveToResult")]
    [Description("Returns or Sets the result type of the move-completed report")]
    [Category("Test Options")]
    public Enums.ResultCodeTypes MoveToResult { get; set; } = Enums.ResultCodeTypes.Success;

    /// <summary>
    ///     Returns or Sets the name of the AutomationDeviceBase
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the name of the AutomationDevice")]
    [DisplayName("Name")]
    [Category("Basic Option")]
    public string? Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or Sets the Partition of the element
    /// </summary>
    /// <returns></returns>
    [DisplayName("PARTITION")]
    [Description("Returns or Sets the PARTITION of the element")]
    [Category("Device Option")]
    public string PARTITION
    {
        get => _partition;
        set
        {
            if (_partition == value) return;
            _partition = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or sets the value of the withdrawal for the balancing ratio between deposit and withdrawal
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or sets the value of the withdrawal for the balancing ratio between deposit and withdrawal")]
    [DisplayName("PickUpUnitStep")]
    [Category("Basic Option")]
    public int PickUpUnitStep
    {
        get => _pickUpUnitStep;
        set
        {
            if (_pickUpUnitStep == value) return;
            _pickUpUnitStep = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or sets whether to register a new LU when receiving identification and dimensional data from the Connection
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or sets whether to register a new LU when receiving identification and dimensional data from the PLC")]
    [DisplayName("RecordLuDataOnGetLuInfo")]
    [Category("Basic Option")]
    public bool RecordLuDataOnGetLuInfo
    {
        get => _recordLuDataOnGetLuInfo;
        set
        {
            if (_recordLuDataOnGetLuInfo == value) return;
            _recordLuDataOnGetLuInfo = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or sets the Storage Quota Posting MissionType
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or sets the Storage Quota Posting Type")]
    [DisplayName("RecordStorageLength")]
    [Category("Basic Option")]
    public Enums.RecordStorageTypes RecordStorageLength
    {
        get => _recordStorageLength;
        set
        {
            if (_recordStorageLength == value) return;
            _recordStorageLength = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     It is the connected AutomationDeviceBase responsible for storage (The property is valid only for Storage type
    ///     items)
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "It is the connected AutomationDevice responsible for storage (The property is valid only for Storage type items)")]
    [DisplayName("RelocateItem")]
    [Category("Basic Option")]
    public AutomationDevice? RelocateItem
    {
        get { return Navigator.PathDevices.FirstOrDefault(x => x != null && x.ItemGuid == _relocateItemId); }
        //set
        //{
        //    if (value != null) RelocateItemId = value.ItemGuid;
        //}
    }

    /// <summary>
    ///     It is the connected AutomationDeviceBase responsible for storage (The property is valid only for Storage type
    ///     items)
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "It is the connected AutomationDevice responsible for storage (The property is valid only for Storage type items)")]
    [DisplayName("RelocateItemId")]
    [Category("Basic Option")]
    public Guid RelocateItemId
    {
        get => _relocateItemId;
        set
        {
            if (_relocateItemId == value)
                return;
            _relocateItemId = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or sets whether the AutomationDeviceBase must respect the MissionStep queue
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or sets whether the AutomationDevice must respect the MissionStep queue")]
    [DisplayName("RespectQueue")]
    [Category("Basic Option")]
    public bool RespectQueue
    {
        get => _respectQueue;
        set
        {
            if (_respectQueue == value) return;
            _respectQueue = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or Sets if the PathMission is assigned to a note, shows an information message of the connected Note (This
    ///     function is active only for Buffer type Items)
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or Sets if the PathMission is assigned to a note, shows an information message of the connected Note (This function is active only for Buffer type Items)")]
    [DisplayName("ShowInfoMessageAssignedLu")]
    [Category("Basic Option")]
    public bool ShowInfoMessageAssignedLu
    {
        get => _showInfoMessageAssignedLu;
        set
        {
            if (_showInfoMessageAssignedLu == value) return;
            _showInfoMessageAssignedLu = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or Sets the current state of the AutomationDeviceBase
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the current state of the AutomationDevice")]
    [DisplayName("Status")]
    [TypeConverter(typeof(PropertyTypeConverter))]
    [Category("Basic Option")]
    public Enums.DeviceStatusTypes Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            //FireStatusChanged();
        }
    }

    /// <summary>
    ///     Returns or Sets the storage quota offset
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the storage quota offset")]
    [DisplayName("StorageOffset")]
    [Category("Storing Option")]
    public int StorageOffset
    {
        get => _storageOffset;
        set
        {
            if (_storageOffset == value) return;
            _storageOffset = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or Sets the Offset of the inverted storage quota
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the Offset of the inverted storage quota")]
    [DisplayName("StorageOffsetReversed")]
    [Category("Storing Option")]
    public int StorageOffsetReversed
    {
        get => _storageOffsetReversed;
        set
        {
            if (_storageOffsetReversed == value) return;
            _storageOffsetReversed = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or Sets the storage partition in case of fixed storage
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the storage partition in case of fixed storage")]
    [DisplayName("StoragePartition")]
    [Category("Storing Option")]
    public string? StoragePartition
    {
        get => _storagePartition;
        set
        {
            if (_storagePartition == value) return;
            _storagePartition = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or Sets the storage partition in case of inverted fixed storage
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the storage partition in case of inverted fixed storage")]
    [DisplayName("StoragePartitionReversed")]
    [Category("Storing Option")]
    public string? StoragePartitionReversed
    {
        get => _storagePartitionReversed;
        set
        {
            if (_storagePartitionReversed == value) return;
            _storagePartitionReversed = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or Sets the deposit reference value for the balance ratio between deposit and withdrawal")
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the deposit reference value for the balance ratio between deposit and withdrawal\")")]
    [DisplayName("StoreUnitStep")]
    [Category("Basic Option")]
    public int StoreUnitStep
    {
        get => _storeUnitStep;
        set
        {
            if (_storeUnitStep == value) return;
            _storeUnitStep = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or Sets the Subitem of the element
    /// </summary>
    /// <returns></returns>
    [DisplayName("SUBITEM")]
    [Description("Returns or Sets the SUBITEM of the element")]
    [Category("Device Option")]
    public string SUBITEM
    {
        get => _subitem;
        set
        {
            if (_subitem == value) return;
            _subitem = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or Sets the waiting time for the response from the Connection for GetInfo
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the waiting time for the response from the PLC for GetInfo")]
    [DisplayName("TimeOutGetLuInfo")]
    [Category("Device Option")]
    public int TimeOutGetLuInfo
    {
        get => _timeOutGetLuInfo;
        set
        {
            if (_timeOutGetLuInfo == value) return;
            _timeOutGetLuInfo = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or Sets the waiting time for the move-completed report in milliseconds before reporting the move TimeOut
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or Sets the waiting time for the move-completed report in milliseconds before reporting the move TimeOut")]
    [DisplayName("TimeOutPlcMoving")]
    [Category("Device Option")]
    public int TimeOutPlcMoving
    {
        get => _timeOutPlcMoving;
        set
        {
            if (_timeOutPlcMoving == value) return;
            _timeOutPlcMoving = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or sets the source item for which to request the dimensional data and the identification codes from the
    ///     Connection (The source item can be that of the previous step or the mission source)
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or sets the source item for which to request the dimensional data and the identification codes from the PLC (The source item can be that of the previous step or the mission source)")]
    [DisplayName("TriggerIdGetLuInfo")]
    [Category("Basic Option")]
    public Guid TriggerIdGetLuInfo
    {
        get => _triggerItemToGetLuInfo;
        set
        {
            if (_triggerItemToGetLuInfo == value) return;
            _triggerItemToGetLuInfo = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or Sets the item to check before performing an LU extraction
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the item to check before performing an LU extraction")]
    [DisplayName("TriggerItemToExtractPoint1")]
    [Category("Basic Option")]
    public Guid TriggerItemToExtractPoint1
    {
        get => _triggerItemToExtractPoint1;
        set
        {
            if (_triggerItemToExtractPoint1 == value) return;
            _triggerItemToExtractPoint1 = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or Sets the item to check before performing an LU extraction
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the item to check before performing an LU extraction")]
    [DisplayName("TriggerItemToExtractPoint1Item")]
    [Category("Basic Option")]
    public AutomationDevice? TriggerItemToExtractPoint1Item
    {
        get { return Navigator.PathDevices.FirstOrDefault(x => x != null && x.ItemGuid == _triggerItemToExtractPoint1); }
        //set
        //{
        //    _TriggerItemToExtract_Point1 = value == null ? Guid.Empty : value.ItemGuid;
        //}
    }

    /// <summary>
    ///     Returns or Sets the item to check before performing an LU extraction
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the item to check before performing an LU extraction")]
    [DisplayName("TriggerItemToExtractPoint2")]
    [Category("Basic Option")]
    public Guid TriggerItemToExtractPoint2
    {
        get => _triggerItemToExtractPoint2;
        set
        {
            if (_triggerItemToExtractPoint2 == value) return;
            _triggerItemToExtractPoint2 = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or Sets the item to check before performing an LU extraction
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the item to check before performing an LU extraction")]
    [DisplayName("TriggerItemToExtractPoint2Item")]
    [Category("Basic Option")]
    public AutomationDevice? TriggerItemToExtractPoint2Item
    {
        get { return Navigator.PathDevices.FirstOrDefault(x => x != null && x.ItemGuid == _triggerItemToExtractPoint2); }
        //set
        //{
        //    _TriggerItemToExtract_Point2 = value == null ? Guid.Empty : value.ItemGuid;
        //}
    }

    #endregion Public Properties
}