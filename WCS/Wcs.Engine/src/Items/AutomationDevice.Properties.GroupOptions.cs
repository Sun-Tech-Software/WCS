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
            return BelongingGroupId == 0
                ? null
                : PathDevices.Instance.FirstOrDefault(x => x != null && x.GroupItemId == BelongingGroupId);
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
    public List<AutomationDevice?> BelongedItems
    {
        get
        {
            var belongedItems = new List<AutomationDevice?>();

            if (GroupItemId == 0)
                return belongedItems;

            belongedItems.AddRange(PathDevices.Instance.Where(x =>
                x != null && x.BelongingGroupId == GroupItemId &&
                x.DeviceType is not (DeviceTypes.CircularDistributor or DeviceTypes.GroupOfAutomationDevices)));

            belongedItems.Sort((x, y) => x!.OrderNumberGroup.CompareTo(y!.OrderNumberGroup));

            return belongedItems;
        }
    }

    /// <summary>
    ///     Returns all MissionSteps of belonging items that have Belonging Group DestinationId equal to GroupItemId
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns all MissionSteps of belonging items that have Belonging Group Id equal to GroupItemId")]
    [DisplayName("BelongedItemsMissionSteps")]
    [Category("Group Option")]
    [ReadOnly(true)]
    public List<MissionStep> BelongedItemsMissionSteps
    {
        get
        {
            var result = new List<MissionStep>();

            foreach (var item in BelongedItems)
                if (item?.MissionSteps != null)
                    result.AddRange(item.MissionSteps);

            return [.. result.OrderBy(x => x.MissionId)];
        }
    }

    /// <summary>
    ///     Returns a list of linked GroupItemIds that are possible bidirectional navigation targets
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns a list of linked GroupItemIds that are possible bidirectional navigation targets")]
    [DisplayName("BidirectionalGroupsLinked")]
    [Category("Group Option")]
    [ReadOnly(true)]
    public List<int> BidirectionalGroupsLinked
    {
        get
        {
            var sourceGroups = SourceGroupsLinked;
            var destinationGroups = DestinationGroupsLinked;

            var result = sourceGroups.Where(source => destinationGroups.Contains(source)).ToList();

            result.Sort();

            return result;
        }
    }

    /// <summary>
    ///     Returns the first BufferGroup available for extracting UDCs from UDC groups
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the first BufferGroup available for extracting UDCs from UDC groups")]
    [DisplayName("BufferGroup")]
    [Category("Group Option")]
    [ReadOnly(true)]
    public AutomationDevice? BufferGroup
    {
        get
        {
            AutomationDevice? result = null;

            var bufferGroups = BufferGroupLevelsLinked;

            foreach (var automationDevice in bufferGroups
                         .Select(bufferGroup => PathDevices.GetFromGroupItemId(bufferGroup)).OfType<AutomationDevice>())
            {
                if (automationDevice.LUsLoaded is { Count: 0 })
                    return automationDevice;
                result = automationDevice;
            }

            return result;
        }
    }

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
    public List<int> BufferGroupLevelsLinked
    {
        get
        {
            var result = new List<int>();

            var bidirectionalGroups = BidirectionalGroupsLinked;

            foreach (var bidirectionalGroup in bidirectionalGroups)
            {
                var automationDevice = PathDevices.GetFromGroupItemId(bidirectionalGroup);

                if (automationDevice is not { DeviceType: DeviceTypes.GroupOfAutomationDevices })
                    continue;

                if (!result.Contains(bidirectionalGroup))
                    result.Add(bidirectionalGroup);
            }

            result.Sort();

            return result;
        }
    }

    /// <summary>
    ///     Returns a list of linked GroupItemIds that are possible move targets
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns a list of linked GroupItemIds that are possible move targets")]
    [DisplayName("DestinationGroupsLinked")]
    [Category("Group Option")]
    [ReadOnly(true)]
    public List<int> DestinationGroupsLinked
    {
        get
        {
            var result = new List<int>();

            var belongedItems = BelongedItems;

            if (belongedItems.Count == 0)
                return result;

            foreach (var itemBelonged in belongedItems)
            {
                var destinationItemLinked = itemBelonged?.DestinationItemsLinked;

                if (destinationItemLinked == null) continue;

                foreach (var itemLinked in destinationItemLinked.Where(x => x != null && x.BelongingGroupId != 0))
                {
                    if (itemLinked == null) continue;

                    var automationDevice = PathDevices.GetFromGroupItemId(itemLinked.BelongingGroupId);

                    if (automationDevice is not { DeviceType: DeviceTypes.GroupOfAutomationDevices }) continue;
                    if (!result.Contains(itemLinked.BelongingGroupId))
                        result.Add(itemLinked.BelongingGroupId);
                }
            }

            result.Sort();

            return result;
        }
    }

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
        set => SetProperty(ref _groupedMoveLuTo, value, true);
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
        set => SetProperty(ref _groupLevelId, value, true);
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
        set => SetProperty(ref _groupLoadingActive, value, true);
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
        set => SetProperty(ref _groupRoutePathId, value, true);
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
    public bool GroupTransferringActive
    {
        get => _groupTransferringActive;
        set => SetProperty(ref _groupTransferringActive, value, true);
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
    public List<int> SourceGroupsLinked
    {
        get
        {
            var result = new List<int>();

            var belongedItems = BelongedItems;

            if (belongedItems.Count == 0)
                return result;

            foreach (var itemBelonged in belongedItems)
            {
                var sourceItemLinked = itemBelonged?.SourceItemsLinked;

                if (sourceItemLinked == null) continue;

                foreach (var itemLinked in sourceItemLinked.Where(x => x != null && x.BelongingGroupId != 0))
                {
                    if (itemLinked == null) continue;

                    var automationDevice = PathDevices.GetFromGroupItemId(itemLinked.BelongingGroupId);

                    if (automationDevice is { DeviceType: DeviceTypes.GroupOfAutomationDevices })
                        if (!result.Contains(itemLinked.BelongingGroupId))
                            result.Add(itemLinked.BelongingGroupId);
                }
            }

            result.Sort();

            return result;
        }
    }

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
        set => SetProperty(ref _timeLapseToExecuteStep, value, true);
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
        set => SetProperty(ref _waitBelongedItemsEmptyBeforeLoading, value, true);
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
        set => SetProperty(ref _waitingTimeForcedLoading, value, true);
    }

    #endregion Methods

}
