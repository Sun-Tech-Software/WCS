using System.ComponentModel;
using Helper.Converters;
using Wcs.Api.Contracts.Dtos;
using Wcs.Common;

namespace Wcs.Viewer.Items;

public class PathMission
{
    #region Private Fields

    private bool _activeFilter;
    private DateTime _date = DateTime.Now;
    private string? _destinationAsi = string.Empty;
    private string? _destinationPartition = string.Empty;
    private string? _destinationSubitem = string.Empty;
    private Guid? _destinationItemId = Guid.Empty;
    private decimal _extractionQueueId;
    private Guid _gateway = Guid.Empty;
    private Guid _groupedMovementId = Guid.Empty;
    private Guid _guidAssignmentLu;
    private int _handlingMode;
    private decimal _idLuType;
    private string? _luCode = string.Empty;
    private decimal _luId;
    private decimal _luNoteAssignmentId;
    private Guid _missionId = Guid.Empty;
    private Enums.MissionTypes _missionType = Enums.MissionTypes.None;
    private string? _movingDescription = string.Empty;
    private decimal _noteProcessingId;
    private Enums.OperationTypes _operationType = Enums.OperationTypes.None;
    private string? _pickUpDoorCode = string.Empty;
    private Guid _positionItemId = Guid.Empty;
    private bool _reallocatedDestination;
    private Guid _relocateItemId = Guid.Empty;
    private string? _sourceAsi = string.Empty;
    private string? _sourcePartition = string.Empty;
    private string? _sourceSubitem = string.Empty;
    private Guid _sourceItemId = Guid.Empty;
    private Enums.MissionStatusTypes _status = Enums.MissionStatusTypes.Stop;
    private string? _statusDescription = string.Empty;
    private string? _storageDoorCode = string.Empty;
    private decimal _tare;

    #endregion Private Fields

    #region Public Constructors

    public PathMission()
    {
    }

    #endregion Public Constructors

    #region Public Methods

    /// <summary>Crea la missione a partire dal DTO del piano.</summary>
    public static PathMission From(MissionDto dto) => new()
    {
        MissionId = dto.MissionId,
        LuId = dto.LoadUnitId,
        LuCode = dto.LoadUnitCode,
        PositionItemId = dto.PositionItemId ?? Guid.Empty,
        SourceItemId = dto.SourceItemId ?? Guid.Empty,
        DestinationItemId = dto.DestinationItemId,
        Date = dto.Date,
        StatusDescription = dto.StatusDescription,
        StorageDoorCode = dto.StorageDoorCode,
        PickUpDoorCode = dto.PickUpDoorCode,
        Status = dto.Status,
        IdCommandToDo = dto.CommandId ?? 0,
        RelocateItemId = dto.RelocateItemId ?? Guid.Empty,
        MissionType = dto.MissionType,
        MovingDescription = dto.MovingDescription,
        OperationType = dto.OperationType,
        BufferCellId = dto.BufferCellId,
        ExtractionQueueId = dto.ExtractionQueueId,
        IdLuType = dto.LoadUnitTypeId,
        GroupedMovementId = dto.GroupedMovementId ?? Guid.Empty,
        HandlingMode = dto.HandlingMode,
        Gateway = dto.GatewayItemId ?? Guid.Empty,
        SourceAsi = dto.SourceAsi,
        SourceSubitem = dto.SourceSubitem,
        SourcePartition = dto.SourcePartition,
        DestinationAsi = dto.DestinationAsi,
        DestinationSubitem = dto.DestinationSubitem,
        DestinationPartition = dto.DestinationPartition,
        LuNoteAssignmentId = dto.NoteAssignmentId,
        GuidAssignmentLu = dto.AssignmentGuid ?? Guid.Empty,
        NoteProcessingId = dto.NoteProcessingId,
        ActiveFilter = dto.ActiveFilter,
        Tare = dto.Tare,
        ReallocatedDestination = dto.ReallocatedDestination,
    };

    #endregion Public Methods

    #region Public Properties

    /// <summary>
    ///     Returns or Sets whether the mission requires filtering of specific route elements
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets whether the mission requires filtering of specific route elements")]
    [DisplayName("ActiveFilter")]
    [Category("PathMission")]
    internal bool ActiveFilter
    {
        get => _activeFilter;
        set
        {
            if (_activeFilter == value) return;
            _activeFilter = value;
            //StartUpdate();
        }
    }

    /// <summary>
    ///     Returns or Sets the identifier of the Buffer cell where to bring the ActiveMissions during the extraction phase
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or Sets the identifier of the Buffer cell where to bring the ActiveMissions during the extraction phase")]
    [DisplayName("BufferCellId")]
    [Category("PathMission")]
    public decimal BufferCellId
    {
        get;
        set;
        //StartUpdate();
    } = 0;

    /// <summary>
    ///     Returns or Sets the date/time of the last PathMission operation
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the date/time of the last PathMission operation")]
    [DisplayName("DateTime")]
    [Category("MissionStep")]
    public DateTime Date
    {
        get => _date;
        set
        {
            if (_date == value) return;
            _date = value;
            //StartUpdate();
        }
    }

    /// <summary>
    ///     Returns or Sets the destination Asi
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the destination ASI")]
    [DisplayName("DestinationAsi")]
    [Category("PathMission")]
    public string? DestinationAsi
    {
        get => _destinationAsi;
        set
        {
            if (_destinationAsi == value) return;
            _destinationAsi = value;
            //StartUpdate();
        }
    }

    /// <summary>
    ///     Returns or Sets the destination Partition
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the destination PARTITION")]
    [DisplayName("DestinationPartition")]
    [Category("PathMission")]
    public string? DestinationPartition
    {
        get => _destinationPartition;
        set
        {
            if (_destinationPartition == value) return;
            _destinationPartition = value;
            //StartUpdate();
        }
    }

    /// <summary>
    ///     Returns or Sets the destination Subitem
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the destination SUBITEM")]
    [DisplayName("DestinationSubitem")]
    [Category("PathMission")]
    public string? DestinationSubitem
    {
        get => _destinationSubitem;
        set
        {
            if (_destinationSubitem == value) return;
            _destinationSubitem = value;
            //StartUpdate();
        }
    }

    /// <summary>
    ///     Returns or Sets the destination AutomationDeviceBase of the PathMission
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the destination AutomationDevice of the PathMission")]
    [DisplayName("DestinationItem")]
    [Category("PathMission")]
    public AutomationDevice? DestinationItem
    {
        get { return Navigator.PathDevices.FirstOrDefault(x => x is not null && x.ItemGuid == DestinationItemId); }
        //set
        //{
        //    if (value == null) return;
        //    DestinationItemId = value.ItemGuid;
        //}
    }

    /// <summary>
    ///     Returns or Sets the identifier of the AutomationDeviceBase destination of the PathMission
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the identifier of the AutomationDevice destination of the PathMission")]
    [DisplayName("Destination")]
    [Category("PathMission")]
    public Guid? DestinationItemId
    {
        get => _destinationItemId;
        set
        {
            if (_destinationItemId == value) return;
            _destinationItemId = value;
            //StartUpdate();
        }
    }

    /// <summary>
    ///     Returns or Sets the unique identifier to the extraction queue UDC_EXTRATION_ID of the AUTOMATIC_UDC_EXTRATIONS
    ///     table
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or Sets the unique identifier to the extraction queue UDC_EXTRATION_ID of the AUTOMATIC_UDC_EXTRATIONS table")]
    [DisplayName("IdExtractionQueue")]
    [Category("PathMission")]
    public decimal ExtractionQueueId
    {
        get => _extractionQueueId;
        set
        {
            if (_extractionQueueId == value) return;
            _extractionQueueId = value;
            //StartUpdate();
        }
    }

    /// <summary>
    ///     Returns or Sets the identifier of the AutomationDeviceBase through which the PathMission must pass
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the identifier of the AutomationDevice through which the PathMission must pass")]
    [DisplayName("Gateway")]
    [Category("PathMission")]
    public Guid Gateway
    {
        get => _gateway;
        set
        {
            if (_gateway == value) return;
            _gateway = value;
            //StartUpdate();
        }
    }

    /// <summary>
    ///     Returns or Sets the AutomationDeviceBase the PathMission should pass through
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the AutomationDevice the PathMission should pass through")]
    [DisplayName("GatewayItem")]
    [Category("PathMission")]
    public AutomationDevice? GatewayItem
    {
        get { return Navigator.PathDevices.FirstOrDefault(x => x is not null && x.ItemGuid == Gateway); }
        //set
        //{
        //    if (value == null) return;
        //    Gateway = value.ItemGuid;
        //}
    }

    /// <summary>
    ///     Returns or sets the unique identifier for group movements generated by AutomationDeviceBase of type
    ///     GroupOfAutomationDevices
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or sets the unique identifier for group movements generated by AutomationDevice of type GroupOfAutomationDevices")]
    [DisplayName("GroupedMovementId")]
    [Category("PathMission")]
    public Guid GroupedMovementId
    {
        get => _groupedMovementId;
        set
        {
            if (_groupedMovementId == value) return;
            _groupedMovementId = value;
            //StartUpdate();
        }
    }

    /// <summary>
    ///     Returns or Sets the unique identifier to the extraction queue UDC_ASSIGNMENT_GUID of the NOTE_ASSIGNMENT table
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or Sets the unique identifier to the extraction queue UDC_ASSIGNMENT_GUID of the NOTE_ASSIGNMENT table")]
    [DisplayName("GuidAssignmentLu")]
    [Category("PathMission")]
    public Guid GuidAssignmentLu
    {
        get => _guidAssignmentLu;
        set
        {
            if (_guidAssignmentLu == value) return;
            _guidAssignmentLu = value;
            //StartUpdate();
        }
    }

    /// <summary>
    ///     Returns or Sets the type of Handling Mode necessary for the move command relating to the type of LU
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or Sets the type of Handling Mode necessary for the move command relating to the type of LU")]
    [DisplayName("HandlingMode")]
    [Category("PathMission")]
    public int HandlingMode
    {
        get => _handlingMode;
        set
        {
            if (_handlingMode == value) return;
            _handlingMode = value;
            //StartUpdate();
        }
    }

    /// <summary>
    ///     Returns or Sets the unique identifier to the ID_COMMAND_TODO command queue of the COMMAND_TODO table
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or Sets the unique identifier to the ID_COMMAND_TODO command queue of the COMMAND_TODO table")]
    [DisplayName("IdCommandToDo")]
    [Category("PathMission")]
    public decimal IdCommandToDo { get; set; }

    /// <summary>
    ///     Returns or Sets the Load Unit type
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the Load Unit type")]
    [DisplayName("IdLUType")]
    [Category("PathMission")]
    public decimal IdLuType
    {
        get => _idLuType;
        set
        {
            if (_idLuType == value) return;
            _idLuType = value;
            //HandlingMode = (int)Math.Round(DBInfo.GetHandlingMode(value));
            //StartUpdate();
        }
    }

    /// <summary>
    ///     Returns or Sets the unique identifier COD_UDC of the UDC table
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the unique identifier COD_UDC of the UDC table")]
    [DisplayName("LuCode")]
    [Category("PathMission")]
    public string? LuCode
    {
        get => _luCode;
        set
        {
            if (_luCode == value) return;
            _luCode = value;
            //StartUpdate();
        }
    }

    /// <summary>
    ///     Returns or Sets the LuId unique identifier of the UDC table
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the LuId unique identifier of the UDC table")]
    [DisplayName("LuId")]
    [Category("PathMission")]
    public decimal LuId
    {
        get => _luId;
        set
        {
            if (_luId == value) return;
            _luId = value;
            //StartUpdate();
        }
    }

    /// <summary>
    ///     Returns or Sets the unique identifier to the extraction queue NOTE_ASSIGNMENT_ID of the NOTE_ASSIGNMENT table
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or Sets the unique identifier to the extraction queue NOTE_ASSIGNMENT_ID of the NOTE_ASSIGNMENT table")]
    [DisplayName("LuNoteAssignmentId")]
    [Category("PathMission")]
    public decimal LuNoteAssignmentId
    {
        get => _luNoteAssignmentId;
        set
        {
            if (_luNoteAssignmentId == value) return;
            _luNoteAssignmentId = value;
            //StartUpdate();
        }
    }

    /// <summary>
    ///     Returns or Sets the unique identifier of PathMission
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the unique identifier of PathMission")]
    [DisplayName("MissionId")]
    [Category("PathMission")]
    public Guid MissionId
    {
        get => _missionId;
        private set
        {
            if (_missionId == value) return;
            _missionId = value;
            //StartUpdate();
        }
    }

    /// <summary>
    ///     Returns or sets the list of all mission steps
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or sets the list of all mission steps")]
    [DisplayName("MissionSteps")]
    [Category("PathMission")]
    public List<MissionStep> MissionSteps
    {
        get { return Navigator.MissionSteps.OfType<MissionStep>().Where(x => x.MissionId == MissionId).ToList(); }
    }

    /// <summary>
    ///     Returns or Sets the Mission type for the PathMission
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the Mission type for the PathMission")]
    [DisplayName("MissionType")]
    [Category("PathMission")]
    [TypeConverter(typeof(PropertyTypeConverter))]
    public Enums.MissionTypes MissionType
    {
        get => _missionType;
        set
        {
            if (_missionType == value) return;
            _missionType = value;
            //StartUpdate();
        }
    }

    /// <summary>
    ///     Returns or Sets the current movement description of the PathMission
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the current movement description of the PathMission")]
    [DisplayName("MovingDescription")]
    [Category("PathMission")]
    public string? MovingDescription
    {
        get => _movingDescription;
        set
        {
            if (_movingDescription == value) return;
            _movingDescription = value;
            //StartUpdate();
        }
    }

    /// <summary>
    ///     Returns or sets the unique identifier to the JOB_ID extraction queue of the NOTE_JOBS table
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or sets the unique identifier to the JOB_ID extraction queue of the NOTE_JOBS table")]
    [DisplayName("NoteProcessingId")]
    [Category("PathMission")]
    public decimal NoteProcessingId
    {
        get => _noteProcessingId;
        set
        {
            if (_noteProcessingId == value) return;
            _noteProcessingId = value;
            //StartUpdate();
        }
    }

    /// <summary>
    ///     Returns or Sets the type of operation for the PathMission (Inbound/Outbound)
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the type of operation for the PathMission (Inbound/Outbound)")]
    [DisplayName("OperationType")]
    [TypeConverter(typeof(PropertyTypeConverter))]
    [Category("PathMission")]
    public Enums.OperationTypes OperationType
    {
        get => _operationType;
        set
        {
            if (_operationType == value) return;
            _operationType = value;
            //StartUpdate();
        }
    }

    /// <summary>
    ///     Returns or Sets the door code of the warehouse picking cell
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the door code of the warehouse picking cell")]
    [DisplayName("PickUpDoorCode")]
    [Category("PathMission")]
    public string? PickUpDoorCode
    {
        get => _pickUpDoorCode;
        set
        {
            if (_pickUpDoorCode == value) return;
            _pickUpDoorCode = value;
            //StartUpdate();
        }
    }

    /// <summary>
    ///     Returns or Sets the AutomationDeviceBase where the PathMission is positioned
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the AutomationDevice where the PathMission is positioned")]
    [DisplayName("PositionItemId")]
    [Category("PathMission")]
    public AutomationDevice? PositionItem
    {
        get { return Navigator.PathDevices.FirstOrDefault(x => x is not null && x.ItemGuid == PositionItemId); }
        //set
        //{
        //    if (value == null) return;
        //    PositionItemId = value.ItemGuid;
        //    //StartUpdate();
        //}
    }

    /// <summary>
    ///     Returns or Sets the identifier of the AutomationDeviceBase where the PathMission is positioned
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the identifier of the AutomationDevice where the PathMission is positioned")]
    [DisplayName("PositionItemId")]
    [Category("PathMission")]
    public Guid PositionItemId
    {
        get => _positionItemId;
        set
        {
            if (_positionItemId == value) return;
            _positionItemId = value;
            //StartUpdate();
        }
    }

    /// <summary>
    ///     Returns or Sets if the destination has been recalculated, for example in case of distribution on the destination
    ///     buffers
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or Sets if the destination has been recalculated, for example in case of distribution on the destination buffers")]
    [DisplayName("ReallocatedDestination")]
    [Category("PathMission")]
    public bool ReallocatedDestination
    {
        get => _reallocatedDestination;
        set
        {
            if (_reallocatedDestination == value) return;
            _reallocatedDestination = value;
            //StartUpdate();
        }
    }

    /// <summary>
    ///     Returns or sets the identifier of the AutomationDeviceBase which is responsible for moving the PathMission within
    ///     the warehouse cells
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or sets the identifier of the AutomationDevice which is responsible for moving the PathMission within the warehouse cells")]
    [DisplayName("RelocateItemId")]
    [Category("PathMission")]
    public Guid RelocateItemId
    {
        get => _relocateItemId;
        set
        {
            if (_relocateItemId == value) return;
            _relocateItemId = value;
            //StartUpdate();
        }
    }

    /// <summary>
    ///     Returns or Sets the source Asi
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the source ASI")]
    [DisplayName("SourceAsi")]
    [Category("PathMission")]
    public string? SourceAsi
    {
        get => _sourceAsi;
        set
        {
            if (_sourceAsi == value) return;
            _sourceAsi = value;
            //StartUpdate();
        }
    }

    /// <summary>
    ///     Returns or Sets the source Partition
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the source PARTITION")]
    [DisplayName("SourcePartition")]
    [Category("PathMission")]
    public string? SourcePartition
    {
        get => _sourcePartition;
        set
        {
            if (_sourcePartition == value) return;
            _sourcePartition = value;
            //StartUpdate();
        }
    }

    /// <summary>
    ///     Returns or Sets the source Subitem
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the source SUBITEM")]
    [DisplayName("SourceSubitem")]
    [Category("PathMission")]
    public string? SourceSubitem
    {
        get => _sourceSubitem;
        set
        {
            if (_sourceSubitem == value) return;
            _sourceSubitem = value;
            //StartUpdate();
        }
    }

    /// <summary>
    ///     Returns or Sets the source AutomationDeviceBase of the PathMission
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the source AutomationDevice of the PathMission")]
    [DisplayName("SourceItem")]
    [Category("PathMission")]
    public AutomationDevice? SourceItem
    {
        get { return Navigator.PathDevices.FirstOrDefault(x => x is not null && x.ItemGuid == SourceItemId); }
        //set
        //{
        //    if (value == null) return;
        //    SourceItemId = value.ItemGuid;
        //}
    }

    /// <summary>
    ///     Returns or Sets the identifier of the AutomationDeviceBase originating from the PathMission
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the identifier of the AutomationDevice originating from the PathMission")]
    [DisplayName("Source")]
    [Category("PathMission")]
    public Guid SourceItemId
    {
        get => _sourceItemId;
        set
        {
            if (_sourceItemId == value) return;
            _sourceItemId = value;
            //StartUpdate();
        }
    }

    /// <summary>
    ///     Returns or Sets the current state of the PathMission
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the current state of the PathMission")]
    [DisplayName("Status")]
    [TypeConverter(typeof(PropertyTypeConverter))]
    public Enums.MissionStatusTypes Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            //StartUpdate();
        }
    }

    /// <summary>
    ///     Returns or Sets the description of the current state of the PathMission
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the description of the current state of the PathMission")]
    [DisplayName("StatusDescription")]
    [Category("PathMission")]
    public string? StatusDescription
    {
        get => _statusDescription;
        set
        {
            if (_statusDescription == value) return;
            _statusDescription = value;
            //StartUpdate();
        }
    }

    /// <summary>
    ///     Returns or Sets the type of execution of the last move command
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the type of execution of the last move command")]
    [DisplayName("StepOrigin")]
    [Category("PathMission")]
    [TypeConverter(typeof(PropertyTypeConverter))]
    public Enums.StepTypes StepOrigin { get; set; }

    /// <summary>
    ///     Returns the MissionStep to execute
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the MissionStep to execute")]
    [DisplayName("StepToDo")]
    [Category("PathMission")]
    public MissionStep? StepToDo
    {
        get
        {
            if (PositionItem == null) return null;
            return Navigator.MissionSteps.FirstOrDefault(x => x is not null && x.FromNodeId == PositionItemId);
        }
    }

    /// <summary>
    ///     Returns or Sets the door code of the warehouse storage cell
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the door code of the warehouse storage cell")]
    [DisplayName("StorageDoorCode")]
    [Category("PathMission")]
    public string? StorageDoorCode
    {
        get => _storageDoorCode;
        set
        {
            if (_storageDoorCode == value) return;
            _storageDoorCode = value;
            //StartUpdate();
        }
    }

    /// <summary>
    ///     Returns or Sets the Tare of the LU
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the Tare of the LU")]
    [DisplayName("Tare")]
    [Category("PathMission")]
    public decimal Tare
    {
        get => _tare;
        set
        {
            if (_tare == value) return;
            _tare = value;
            //StartUpdate();
        }
    }

    #endregion Public Properties
}