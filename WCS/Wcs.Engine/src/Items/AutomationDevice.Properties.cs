using System.ComponentModel;
using System.Drawing;
using System.Runtime.CompilerServices;
using Helper.Converters;
using Wcs.Engine.Collections;
using Wcs.Common.Events;
using Wcs.Engine.Converters;
using Wcs.Connection.Interface;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Items;

public sealed partial class AutomationDevice
{

    #region Internal Fields

    internal int LastLevelLoaded;

    #endregion Internal Fields

    #region Private Fields

    private readonly SemaphoreSlim _executionSemaphore = new(1, 1);
    private bool _acceptLuIfDestinationIsAvailable;

    private bool _addStorageLengthLuDepth;

    private string _asi = "0000";

    private bool _autoExecuteStep = true;

    private bool _autoUnloadLu;

    private int _belongingGroupId;

    private ConnectionTypes _connectionType;

    private int _delayBlind = 500;

    private Guid _destinationItemId = Guid.Empty;

    private Guid _destinationItemId2 = Guid.Empty;

    private decimal _deviceConnectionId;

    private DeviceTypes _deviceType = DeviceTypes.None;

    private bool _distributeOnBuffers;

    private bool _enableDefragmentation;

    private bool _extractionCheckPoint;

    private bool _getLuInfo;

    private bool _groupedMoveLuTo;

    private bool _groupExecuteStepRunning;

    private int _groupItemId;

    private int _groupLevelId;

    private bool _groupLoadingActive;

    private int _groupRoutePathId;

    private bool _groupTransferringActive;

    private decimal _idCellToDefragment;
    private decimal _linkedCellId;

    private LookWhereAllocateLuTypes _lookWhereAllocateLu = LookWhereAllocateLuTypes.No;

    private SizeF _luImageSize;

    private int _maxUnloadWeight;

    private MotionTypes _movementType;

    private string? _name;

    private bool _onLoadLuActiveFilter;

    private int _orderNumberGroup;

    private string _partition = "0001";

    private int _pickUpUnitStep;

    private int _priorityInRoutePath;

    private bool _recordLuDataOnGetLuInfo;

    private RecordStorageTypes _recordStorageLength;

    private Guid _rejectDestinationUserRequest = Guid.Empty;

    private Guid _rejectItemDestinationAllocateLu = Guid.Empty;

    private Guid _rejectItemDestinationIdGetLuInfo = Guid.Empty;

    private Guid _relocateItemId = Guid.Empty;

    private bool _respectQueue;

    private Guid _runningStepId = Guid.Empty;

    private bool _showInfoMessageAssignedLu;

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

    private int _waitingTimeForcedLoading;

    private int _waitLookingLevels;

    #endregion Private Fields

    #region Public Events

    public event PropertyChangedEventHandler? PropertyChanged;

    #endregion Public Events

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
        set => SetProperty(ref _addStorageLengthLuDepth, value, true);
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
        set => SetProperty(ref _asi, value, true);
    }

    /// <summary>
    ///     Returns or Sets whether to automatically execute the movement micro mission relating to the loaded PathMission)
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or Sets whether to automatically execute the movement micromission relating to the loaded PathMission")]
    [DisplayName("AutoExecuteStep")]
    [Category("Basic Option")]
    public bool AutoExecuteStep
    {
        get
        {
            if (IsLinkedToStorage || DeviceType == DeviceTypes.CircularDistributor || DeviceType == DeviceTypes.Storage)
                return false;
            return _autoExecuteStep;
        }
        set => SetProperty(ref _autoExecuteStep, value, true);
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
        set => SetProperty(ref _autoUnloadLu, value, true);
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
        set => SetProperty(ref _belongingGroupId, value, true);
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
    public ConnectionTypes ConnectionType
    {
        get => _connectionType;
        set => SetProperty(ref _connectionType, value, true);
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
        set => SetProperty(ref _delayBlind, value, true);
    }

    /// <summary>
    ///     Returns or Sets the active connection to the DeviceConnections
    /// </summary>
    /// <returns></returns>
    [Browsable(false)]
    public IConnection? DeviceConnection { get; set; }

    /// <summary>
    ///     Returns or Sets the PCL PCM identifier
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the PCL device identifier")]
    [DisplayName("DeviceConnectionId")]
    [Category("Device Option")]
    public decimal DeviceConnectionId
    {
        get => _deviceConnectionId;
        set => SetProperty(ref _deviceConnectionId, value, true);
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
    public DeviceTypes DeviceType
    {
        get => _deviceType;
        set
        {
            // StartActiveProcesses() non si chiama piu' da qui: leggere una riga di
            // NAVIGATOR_ITEMS assegna le colonne una per una, e avviare i cicli sul setter
            // significava farlo su un dispositivo non ancora configurato del tutto, tante
            // volte quante le colonne rilevanti. L'avvio e' ora in LoadAndStart, una volta
            // sola e a configurazione completa. Si veda la voce D63 del registro.
            SetProperty(ref _deviceType, value, true);
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
        set => SetProperty(ref _enableDefragmentation, value, true);
    }

    /// <summary>
    ///     Returns or sets whether to request dimensional data and identification codes from the Connection when a PathMission
    ///     is located on the AutomationDeviceBase
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or sets whether to request dimensional data and identification codes from the PLC when a PathMission is located on the AutomationDevice")]
    [DisplayName("GetLuInfo")]
    [Category("Basic Option")]
    public bool GetLuInfo
    {
        get => _getLuInfo;
        set => SetProperty(ref _getLuInfo, value, true);
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
            // StartActiveProcesses() non si chiama piu' da qui: leggere una riga di
            // NAVIGATOR_ITEMS assegna le colonne una per una, e avviare i cicli sul setter
            // significava farlo su un dispositivo non ancora configurato del tutto, tante
            // volte quante le colonne rilevanti. L'avvio e' ora in LoadAndStart, una volta
            // sola e a configurazione completa. Si veda la voce D63 del registro.
            SetProperty(ref _groupItemId, value, true);
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
            // La riga la scrive SetProperty, se il valore cambia e non si sta caricando. Qui c'era
            // una seconda scrittura incondizionata, fuori dalla guardia: a ogni ricaricamento la
            // riga si riscriveva mentre la si leggeva (coda, voce 42).
            SetProperty(ref _idCellToDefragment, value, true);

            if (value == 0)
            {
                RemoveLuToPickUpWhere(x => x is { MissionType: MissionTypes.DefragmentCell });
                LUsToPick.RemoveAll(x => x is { MissionType: MissionTypes.DefragmentCell });
            }
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
    public Guid ItemGuid { get; } = Guid.NewGuid();

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
        set => SetProperty(ref _linkedCellId, value, true);
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
    /// <remarks>
    /// <para>
    /// Qui c'era un <c>FirstOrDefault</c> su <b>tutte</b> le missioni attive, rifatto a ogni
    /// lettura, in un percorso dove questa proprieta' si legge di continuo — spesso due volte nella
    /// stessa istruzione, nella forma <c>if (device.LoadedLu != null) device.LoadedLu.X = ...</c>,
    /// che erano due ricerche e potevano dare due risposte diverse. E se due missioni dichiaravano
    /// la stessa posizione, quale delle due tornasse era **arbitrario**: l'ordine di enumerazione
    /// di una <c>ConcurrentDictionary</c> non e' definito. Voce <c>D74</c> del registro.
    /// </para>
    /// <para>
    /// Ora e' una lettura indicizzata. La verita' resta <c>PathMission.PositionItemId</c>: l'indice
    /// lo segue, e chi lo interroga riceve una voce ricontrollata. E' sparito anche il
    /// <c>catch</c> che restituiva <c>null</c> su qualunque errore — faceva risultare
    /// <b>scarico</b> un dispositivo che non lo era, che e' il modo peggiore di sbagliare qui.
    /// </para>
    /// </remarks>
    public PathMission? LoadedLu => ActiveMissions.Instance.MissionAtPosition(ItemGuid);

    /// <summary>
    ///     Returns or Sets the graphic size of the PathMission image (0;0 = default)
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the graphic size of the PathMission image (0;0 = default)")]
    [DisplayName("LuImageSize")]
    [Category("Basic Option")]
    public SizeF LuImageSize
    {
        get => _luImageSize;
        set => SetProperty(ref _luImageSize, value, true);
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
        set => SetProperty(ref _maxUnloadWeight, value, true);
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
    public MotionTypes MotionType
    {
        get => _movementType;
        set => SetProperty(ref _movementType, value, true);
    }

    /// <summary>
    ///     Returns or Sets the result type of the move-completed report
    /// </summary>
    /// <returns></returns>
    [DisplayName("MoveToResult")]
    [Description("Returns or Sets the result type of the move-completed report")]
    [Category("Test Options")]
    public ResultCodeTypes MoveToResult { get; set; } = ResultCodeTypes.Success;

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
        set => SetProperty(ref _name, value, true);
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
        set => SetProperty(ref _partition, value, true);
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
        set => SetProperty(ref _pickUpUnitStep, value, true);
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
        set => SetProperty(ref _recordLuDataOnGetLuInfo, value, true);
    }

    /// <summary>
    ///     Returns or sets the Storage Quota Posting MissionType
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or sets the Storage Quota Posting Type")]
    [DisplayName("RecordStorageLength" +
                 "")]
    [Category("Basic Option")]
    public RecordStorageTypes RecordStorageLength
    {
        get => _recordStorageLength;
        set => SetProperty(ref _recordStorageLength, value, true);
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
    [TypeConverter(typeof(AutomationDeviceType))]
    public AutomationDevice? RelocateItem
    {
        get => PathDevices.Instance[RelocateItemId];
        set
        {
            if (value != null) RelocateItemId = value.ItemGuid;
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
    [DisplayName("RelocateItemId")]
    [Category("Basic Option")]
    public Guid RelocateItemId
    {
        get => _relocateItemId;
        set => SetProperty(ref _relocateItemId, value, true);
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
        set => SetProperty(ref _respectQueue, value, true);
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
        set => SetProperty(ref _showInfoMessageAssignedLu, value, true);
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
    public DeviceStatusTypes Status
    {
        get => _stateMachine?.CurrentState ?? DeviceStatusTypes.OffLine;

        // Il cambio e' visibile al ritorno dall'assegnazione: la sezione critica e' sincrona e non
        // fa I/O, mentre la pubblicazione prosegue in sottofondo. Prima qui c'era un Task.Run, e il
        // motore proseguiva come se lo stato fosse gia' cambiato. Si veda la voce D43 del registro.
        set => _stateMachine?.TryChangeStateNow(value);
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
        set => SetProperty(ref _storageOffset, value, true);
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
        // Come IdCellToDefragment: scrive solo SetProperty, sotto la guardia del caricamento.
        set => SetProperty(ref _storageOffsetReversed, value, true);
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
        set => SetProperty(ref _storagePartition, value, true);
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
        set => SetProperty(ref _storagePartitionReversed, value, true);
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
        set => SetProperty(ref _storeUnitStep, value, true);
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
        set => SetProperty(ref _subitem, value, true);
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
        set => SetProperty(ref _timeOutGetLuInfo, value, true);
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
        set => SetProperty(ref _timeOutPlcMoving, value, true);
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
        set => SetProperty(ref _triggerItemToGetLuInfo, value, true);
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
        set => SetProperty(ref _triggerItemToExtractPoint1, value, true);
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
        get => PathDevices.Instance[TriggerItemToExtractPoint1];
        set => TriggerItemToExtractPoint1 = value?.ItemGuid ?? Guid.Empty;
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
        set => SetProperty(ref _triggerItemToExtractPoint2, value, true);
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
        get => PathDevices.Instance[TriggerItemToExtractPoint2];
        set => TriggerItemToExtractPoint2 = value?.ItemGuid ?? Guid.Empty;
    }

    #endregion Public Properties

    #region Internal Properties

    /// <summary>
    ///     Check if LU can be extracted
    /// </summary>
    /// <returns></returns>
    internal bool CanExtractLUs
    {
        get
        {
            if (TriggerItemToExtractPoint1 != Guid.Empty)
            {
                var triggerItem = PathDevices.Instance[TriggerItemToExtractPoint1];

                if (triggerItem != null &&
                    (triggerItem.LUsLoaded.Count > 0 || triggerItem.Status == DeviceStatusTypes.OffLine))
                    return false;
            }

            if (TriggerItemToExtractPoint2 != Guid.Empty)
            {
                var triggerItem = PathDevices.Instance[TriggerItemToExtractPoint2];

                if (triggerItem != null &&
                    (triggerItem.LUsLoaded.Count > 0 || triggerItem.Status == DeviceStatusTypes.OffLine))
                    return false;
            }

            return true;
        }
    }

    /// <summary>
    ///     Returns the number of picking missions performed consecutively
    /// </summary>
    /// <returns></returns>
    internal int CurrentPickUpStep { get; set; }

    /// <summary>
    ///     Returns the number of depot missions executed consecutively
    /// </summary>
    /// <returns></returns>
    internal int CurrentStoreStep { get; set; }

    internal string? EmptyCellDestinationDoor { get; set; } = string.Empty;

    internal decimal EmptyCellIdLuType { get; set; }

    internal string? EmptyCellIdWorkstation { get; set; } = string.Empty;

    internal string? EmptyCellSourceDoor { get; set; } = string.Empty;

    internal EmptyCellStatusTypes EmptyCellStatus { get; set; } = EmptyCellStatusTypes.None;

    internal bool IsExecutingGroupExtractionLu { get; set; }

    internal bool IsLoadingLu { get; set; }

    /// <summary>
    ///     Returns or Sets the last type of movement made by the GroupItem
    /// </summary>
    /// <returns></returns>
    internal GroupMovementType LastGroupMovement { get; set; }

    /// <summary>
    ///     Returns if there are PathMissions with no deposit destination
    /// </summary>
    /// <returns></returns>
    internal bool LUsWithNoStorage
    {
        get { return LUsLoaded.Any(x => string.IsNullOrEmpty(x?.StorageDoorCode)); }
    }

    #endregion Internal Properties

}