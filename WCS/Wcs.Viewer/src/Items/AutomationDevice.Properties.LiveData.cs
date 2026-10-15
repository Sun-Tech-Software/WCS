using System.ComponentModel;
using Helper.Converters;
using Wcs.Common;

namespace Wcs.Viewer.Items;

public partial class AutomationDevice
{

    #region Methods

    /// <summary>
    ///     Returns the list of current picking missions
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the list of current picking missions")]
    [DisplayName("ActivePickingMission")]
    [Category("Live Data")]
    public List<PathMission?> ActivePickingMission { get; internal set; } = [];

    /// <summary>
    ///     Returns the list of ongoing depot missions
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the list of ongoing depot missions")]
    [DisplayName("ActiveStoringMission")]
    [Category("Live Data")]
    public List<PathMission?> ActiveStoringMission { get; internal set; } = [];

    /// <summary>
    ///     Returns the PLC code of the last deposit refused in a row (67, 85 or 133), 0 if none
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the PLC code of the last deposit refused in a row (67, 85 or 133), 0 if none")]
    [DisplayName("RefusedDepositErrorCode")]
    [Category("Live Data")]
    public int RefusedDepositErrorCode { get; set; }

    /// <summary>
    ///     Returns how many times in a row the PLC refused a deposit with RefusedDepositErrorCode
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns how many times in a row the PLC refused a deposit with RefusedDepositErrorCode")]
    [DisplayName("RefusedDepositsInARow")]
    [Category("Live Data")]
    public int RefusedDepositsInARow { get; set; }

    /// <summary>
    ///     Returns or Sets the queue of AutomationDevices that are waiting to transfer an ActiveMissions
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the queue of AutomationDevices that are waiting to transfer an ActiveMissions")]
    [DisplayName("DevicesReadyToTransferLu")]
    [Category("Live Data")]
    public List<AutomationDevice> DevicesReadyToTransferLu { get; set; } = [];

    /// <summary>
    ///     Returns whether a MissionStep is executing
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns whether a MissionStep is executing")]
    [DisplayName("ExecuteStepIsRunning")]
    [Category("Live Data")]
    public bool ExecuteStepIsRunning { get; set; }

    /// <summary>
    ///     Returns the next group of PathMissions to fetch based on the GroupedMovementId grouping
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the next group of PathMissions to fetch based on the GroupedMovementId grouping")]
    [DisplayName("GroupedLuToPickUp")]
    [Category("Live Data")]
    [ReadOnly(true)]
    public List<PathMission> GroupedLuToPickUp { get; internal set; } = [];

    /// <summary>
    ///     Identifies whether an ExecuteStep procedure is in progress for a GroupItem (Note: This function concerns
    ///     GroupedItems of type GroupOfAutomationDevices)
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Identifies whether an ExecuteStep procedure is in progress for a GroupItem (Note: This function concerns GroupedItems of type GroupOfAutomationDevices)")]
    [DisplayName("GroupExecuteStepRunning")]
    [Category("Live Data")]
    public bool GroupExecuteStepRunning { get; set; }

    /// <summary>
    ///     Returns the Availability to execute a MissionStep
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the Availability to execute a MissionStep")]
    [DisplayName("IsReadyToExecuteStep")]
    [Category("Live Data")]
    public bool IsReadyToExecuteStep { get; internal set; }

    /// <summary>
    ///     Returns the availability to receive the loaded ActiveMissions from another AutomationDeviceBase
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the availability to receive the loaded ActiveMissions from another AutomationDevice")]
    [DisplayName("IsReadyToReceiveLu")]
    [Category("Live Data")]
    public bool IsReadyToReceiveLu { get; internal set; }

    /// <summary>
    ///     Returns the Availability to move the loaded PathMission to another AutomationDeviceBase
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the Availability to move the loaded PathMission to another AutomationDevice")]
    [DisplayName("IsReadyToTransferLu")]
    [Category("Live Data")]
    public bool IsReadyToTransferLu { get; internal set; }

    /// <summary>
    ///     Returns whether a PathMission is being received from an element
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns whether a PathMission is being received from an element")]
    [DisplayName("IsReceivingLu")]
    [Category("Live Data")]
    public bool IsReceivingLu { get; internal set; }

    /// <summary>
    ///     Returns the last operation performed
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the last operation performed")]
    [DisplayName("LastExecutedStepType")]
    [Category("Live Data")]
    public Enums.ExecutedStepTypes LastExecutedStepType { get; internal set; }

    /// <summary>
    ///     Returns the loading status of the PathMission
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the loading status of the PathMission")]
    [DisplayName("LuLoadStatus")]
    [TypeConverter(typeof(PropertyTypeConverter))]
    [Category("Live Data")]
    [ReadOnly(true)]
    public Enums.LuLoadStatusTypes LuLoadStatus => LUsLoaded.Count == 0 ? Enums.LuLoadStatusTypes.None : Enums.LuLoadStatusTypes.Loaded;

    /// <summary>
    ///     Returns the list of PathMissions on board
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the list of PathMissions on board")]
    [DisplayName("LUsLoaded")]
    [Category("Live Data")]
    [ReadOnly(true)]
    public List<PathMission> LUsLoaded { get; internal set; } = [];

    /// <summary>
    ///     Returns the list of PathMissions ready to be loaded from the first GroupLevelId
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the list of PathMissions ready to be loaded from the first GroupLevelId")]
    [DisplayName("LUsReadyToLoad")]
    [Category("Live Data")]
    [ReadOnly(true)]
    public List<PathMission?> LUsReadyToLoad { get; internal set; } = [];

    /// <summary>
    ///     Returns the list of MissionSteps related to the list of LUsReadyToLoad
    /// </summary>
    /// <returns></returns>
    [Description("Returns the list of MissionSteps related to the list of LUsReadyToLoad")]
    [DisplayName("LUsReadyToLoadSteps")]
    [Category("Live Data")]
    [ReadOnly(true)]
    public List<MissionStep> LUsReadyToLoadSteps { get; internal set; } = [];

    /// <summary>
    ///     Returns the list of PathMissions ready to be transferred to the first GroupLevelId
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the list of PathMissions ready to be transferred to the first GroupLevelId")]
    [DisplayName("LUsReadyToTransfer")]
    [Category("Live Data")]
    [ReadOnly(true)]
    public List<PathMission> LUsReadyToTransfer { get; internal set; } = [];

    /// <summary>
    ///     Returns the list of MissionSteps related to the list of LUsReadyToTransfer
    /// </summary>
    /// <returns></returns>
    [Description("Returns the list of MissionSteps related to the list of LUsReadyToTransfer")]
    [DisplayName("LUsReadyToTransferSteps")]
    [Category("Live Data")]
    public List<MissionStep> LUsReadyToTransferSteps { get; internal set; } = [];

    /// <summary>
    ///     Returns or Sets the PathMission that must be taken from the Warehouse
    /// </summary>
    /// <returns></returns>
    [Description("Returns or Sets the PathMission that must be taken from the Warehouse")]
    [DisplayName("LUsToPick")]
    [Category("Live Data")]
    public List<PathMission> LUsToPick { get; internal set; } = [];

    /// <summary>
    ///     Returns the list of MissionSteps related to the list of LUsToPick
    /// </summary>
    /// <returns></returns>
    [Description("Returns the list of MissionSteps related to the list of LUsToPick")]
    [DisplayName("LUsToPickSteps")]
    [Category("Live Data")]
    public List<MissionStep> LUsToPickSteps { get; internal set; } = [];

    /// <summary>
    ///     Returns the list of UDCs that are awaiting transfer
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the list of UDCs that are awaiting transfer")]
    [DisplayName("LUsToReceive")]
    [Category("Live Data")]
    public List<PathMission> LUsToReceive { get; internal set; } = [];

    /// <summary>
    ///     Returns or Sets the PathMission that must be taken from the Warehouse
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the PathMission that must be taken from the Warehouse")]
    [DisplayName("LUtoPick")]
    [Category("Live Data")]
    public PathMission? LUtoPick { get; internal set; }

    /// <summary>
    ///     Returns or Sets the PathMission that is expected to receive from a different AutomationDeviceBase
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the PathMission that is expected to receive from a different AutomationDevice")]
    [DisplayName("LUtoReceive")]
    [Category("Live Data")]
    public PathMission? LUtoReceive { get; internal set; }

    /// <summary>
    ///     Returns the list of micromissions to execute for the loaded or expected PathMission
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the list of micromissions to execute for the loaded or expected PathMission")]
    [DisplayName("MissionSteps")]
    [Category("Live Data")]
    [ReadOnly(true)]
    public List<MissionStep?> MissionSteps
    {
        get
        {
            return Navigator.MissionSteps
                .Where(x => x is { SourceItem: not null } && x.SourceItem.ItemGuid == ItemGuid).ToList();
        }
        //set
        //{
        //    Navigator.MissionSteps.RemoveAll(x => x != null && x.SourceItem != null && x.SourceItem.ItemGuid == ItemGuid);
        //    Navigator.MissionSteps.AddRange(value);
        //}
    }

    /// <summary>
    ///     Returns or sets the queue of PathMissions to be withdrawn from the warehouse, this property is valid if the Item is
    ///     connected to a Storage-type AutomationDeviceBase
    /// </summary>
    [Browsable(true)]
    [Description(
        "Returns or sets the queue of PathMissions to be withdrawn from the warehouse, this property is valid if the Item is connected to a Storage-type AutomationDevice")]
    [DisplayName("QueuedLUtoPickUp")]
    [Category("Live Data")]
    public List<PathMission> QueuedLUtoPickUp { get; internal set; } = [];

    /// <summary>
    ///     Returns or sets the Executing MissionStep
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or sets the Executing MissionStep")]
    [DisplayName("RunningStep")]
    [Category("Live Data")]
    public MissionStep? RunningStep
    {
        get { return Navigator.MissionSteps.FirstOrDefault(x => x != null && x.StepId == _runningStepId); }
    }

    /// <summary>
    ///     Returns or sets the IdGuid of the Executing MissionStep
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or sets the IdGuid of the Executing MissionStep")]
    [DisplayName("RunningStepId")]
    [Category("Live Data")]
    public Guid RunningStepId
    {
        get => _runningStepId;
        set
        {
            if (_runningStepId == value) return;
            _runningStepId = value;
            //RunningStepChanged?.Invoke(this, new AutomationDeviceRunningStepIdChangedEventArgs(RunningStepId, WaitingStepId));
            //Navigator.BridgeLinker?.ItemChangeStatus(this);

            //ORM.AutomationDevice.UpdateStatus(this);
        }
    }

    /// <summary>
    ///     Returns or sets the Waiting MissionStep
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or sets the Waiting MissionStep")]
    [DisplayName("WaitingStep")]
    [Category("Live Data")]
    public MissionStep? WaitingStep
    {
        get { return Navigator.MissionSteps.FirstOrDefault(x => x != null && x.StepId == _waitingStepId); }
    }

    /// <summary>
    ///     Returns or sets the IdGuid of the Waiting MissionStep
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or sets the IdGuid of the Waiting MissionStep")]
    [DisplayName("WaitingStepId")]
    [Category("Live Data")]
    public Guid WaitingStepId
    {
        get => _waitingStepId;
        set
        {
            if (_waitingStepId == value) return;
            _waitingStepId = value;
            //RunningStepChanged?.Invoke(this, new AutomationDeviceRunningStepIdChangedEventArgs(RunningStepId, WaitingStepId));
            //Navigator.BridgeLinker?.ItemChangeStatus(this);
        }
    }

    #endregion Methods

}
