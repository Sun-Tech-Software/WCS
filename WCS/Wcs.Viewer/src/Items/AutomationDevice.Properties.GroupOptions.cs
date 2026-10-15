using System.ComponentModel;
using Helper.Converters;
using Wcs.Common;

namespace Wcs.Viewer.Items;

public partial class AutomationDevice
{

    #region Methods

    /// <summary>
    ///     Returns the GroupItem to which the AutomationDeviceBase belongs
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the GroupItem to which the AutomationDevice belongs")]
    [DisplayName("BelongedGroup")]
    [Category("Group Option")]
    [ReadOnly(true)]
    public AutomationDevice? BelongedGroup
    {
        get
        {
            return BelongingGroupId == 0 ? null : Navigator.PathDevices.FirstOrDefault(x => x != null && x.GroupItemId == BelongingGroupId);
        }
    }

    /// <summary>
    ///     Returns the AutomationDevices in order of OrderNumberGroup that have Belonging Group DestinationId equal to
    ///     GroupItemId
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns the AutomationDevices in order of OrderNumberGroup that have Belonging Group Id equal to GroupItemId")]
    [DisplayName("BelongedItems")]
    [Category("Group Option")]
    [ReadOnly(true)]
    public List<AutomationDevice> BelongedItems { get; internal set; } = [];

    /// <summary>
    ///     Returns all MissionSteps of belonging items that have Belonging Group DestinationId equal to GroupItemId
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns all MissionSteps of belonging items that have Belonging Group Id equal to GroupItemId")]
    [DisplayName("BelongedItemsMissionSteps")]
    [Category("Group Option")]
    [ReadOnly(true)]
    public List<MissionStep> BelongedItemsMissionSteps { get; internal set; } = [];

    /// <summary>
    ///     Returns a list of linked GroupItemIds that are possible bidirectional navigation targets
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns a list of linked GroupItemIds that are possible bidirectional navigation targets")]
    [DisplayName("BidirectionalGroupsLinked")]
    [Category("Group Option")]
    [ReadOnly(true)]
    public List<int> BidirectionalGroupsLinked { get; internal set; } = [];

    /// <summary>
    ///     Returns the first BufferGroup available for extracting UDCs from UDC groups
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the first BufferGroup available for extracting UDCs from UDC groups")]
    [DisplayName("BufferGroup")]
    [Category("Group Option")]
    [ReadOnly(true)]
    public AutomationDevice? BufferGroup { get; internal set; }

    /// <summary>
    ///     Returns a list of linked GroupLevelIds which are the possible buffer zones for extracting UDCs from groups of UDCs
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns a list of linked GroupLevelIds which are the possible buffer zones for extracting UDCs from groups of UDCs")]
    [DisplayName("BufferGroupLevelsLinked")]
    [Category("Group Option")]
    [ReadOnly(true)]
    public List<int> BufferGroupLevelsLinked { get; internal set; } = [];

    /// <summary>
    ///     Returns a list of linked GroupItemIds that are possible move targets
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns a list of linked GroupItemIds that are possible move targets")]
    [DisplayName("DestinationGroupsLinked")]
    [Category("Group Option")]
    [ReadOnly(true)]
    public List<int> DestinationGroupsLinked { get; internal set; } = [];

    /// <summary>
    ///     Returns or Sets whether the MoveLuFromAsiToAsi command is grouped for all the PathMissions contained in the
    ///     AutomationDevices of the GroupItem
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or Sets whether the MOVE_LU_TO command is grouped for all the PathMissions contained in the AutomationDevices of the GroupItem")]
    [DisplayName("GroupedMoveLuTo")]
    [Category("Group Option")]
    public bool GroupedMoveLuTo
    {
        get => _groupedMoveLuTo;
        set
        {
            if (_groupedMoveLuTo == value)
                return;
            _groupedMoveLuTo = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or Sets the source level from which the item group will be accessed
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the source level from which the item group will be accessed")]
    [DisplayName("GroupLevelId")]
    [Category("Group Option")]
    public int GroupLevelId
    {
        get => _groupLevelId;
        set
        {
            if (_groupLevelId == value) return;
            _groupLevelId = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or Sets whether the group can pick up PathMission from the connected AutomationDevices Note: The option is
    ///     active only for ItemType GroupOfAutomationDevices
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or Sets whether the group can pick up PathMission from the connected AutomationDevices Note: The option is active only for ItemType GroupOfAutomationDevices")]
    [DisplayName("GroupLoadingActive")]
    [Category("Group Option")]
    public bool GroupLoadingActive
    {
        get => _groupLoadingActive;
        set
        {
            if (_groupLoadingActive == value) return;
            _groupLoadingActive = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or sets the level identifier of a group of AutomationDevices, this property is used during path processing
    ///     to avoid returning to the same level of origin
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or sets the level identifier of a group of AutomationDevices, this property is used during path processing to avoid returning to the same level of origin")]
    [DisplayName("GroupRoutePathId")]
    [Category("Group Option")]
    public int GroupRoutePathId
    {
        get => _groupRoutePathId;
        set
        {
            if (_groupRoutePathId == value) return;
            _groupRoutePathId = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or Sets whether the group can pick up PathMission in the connected AutomationDevices Note: The Option is
    ///     active only for ItemType GroupOfAutomationDevices and only for those with Items not connected to Storage
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or Sets whether the group can pick up PathMission in the connected AutomationDevices Note: The Option is active only for ItemType GroupOfAutomationDevices and only for those with Items not connected to Storage")]
    [DisplayName("GroupTransferringActive")]
    [Category("Group Option")]
    public bool GroupTransferingActive
    {
        get => _groupTransferringActive;
        set
        {
            if (_groupTransferringActive == value) return;
            _groupTransferringActive = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns a list of linked GroupItemIds that are the possible navigation sources
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns a list of linked GroupItemIds that are the possible navigation sources")]
    [DisplayName("SourceGroupsLinked")]
    [Category("Group Option")]
    [ReadOnly(true)]
    public List<int> SourceGroupsLinked { get; internal set; } = [];

    /// <summary>
    ///     Returns or Sets the test execution time interval of the ExecuteStep command of a GroupItem
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the test execution time interval of the ExecuteStep command of a GroupItem")]
    [DisplayName("TimeLapseToExecuteStep")]
    [Category("Group Option")]
    public int TimeLapseToExecuteStep
    {
        get => _timeLapseToExecuteStep;
        set
        {
            if (_timeLapseToExecuteStep == value) return;
            _timeLapseToExecuteStep = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or Sets whether to wait until all elements of the group are empty before loading the PathMissions
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or Sets whether to wait until all elements of the group are empty before loading the PathMissions")]
    [DisplayName("WaitBelongedItemsEmptyBeforeLoading")]
    [Category("Group Option")]
    public bool WaitBelongedItemsEmptyBeforeLoading
    {
        get => _waitBelongedItemsEmptyBeforeLoading;
        set
        {
            if (_waitBelongedItemsEmptyBeforeLoading == value) return;
            _waitBelongedItemsEmptyBeforeLoading = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns or Sets the maximum waiting time for a GroupItem to load all the elements belonging to it
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the maximum waiting time for a GroupItem to load all the elements belonging to it")]
    [DisplayName("WaitingTimeForcedLoading")]
    [Category("Group Option")]
    public int WaitingTimeForcedLoading
    {
        get => _waitingTimeForcedLoading;
        set
        {
            if (_waitingTimeForcedLoading == value) return;
            _waitingTimeForcedLoading = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    #endregion Methods

}
