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
    ///     Returns or Sets whether to accept a LU if the target is available
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets whether to accept a LU if the target is available")]
    [DisplayName("AcceptLUIfDestinationIsAvailable")]
    [Category("Path Option")]
    public bool AcceptLuIfDestinationIsAvailable
    {
        get => _acceptLuIfDestinationIsAvailable;
        set => SetProperty(ref _acceptLuIfDestinationIsAvailable, value, true);
    }

    /// <summary>
    ///     Returns or Sets on which element to move the loaded PathMission (This function is valid only if there are no
    ///     destinations previously assigned to the PathMission)
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or Sets on which element to move the loaded PathMission (This function is valid only if there are no destinations previously assigned to the PathMission)")]
    [DisplayName("DestinationItem")]
    [Category("Path Option")]
    public AutomationDevice? DestinationItem
    {
        get => PathDevices.Instance[DestinationItemId];
        set
        {
            if (value != null) DestinationItemId = value.ItemGuid;
        }
    }

    /// <summary>
    ///     Returns or Sets on which element to move the loaded PathMission (This function is valid only if there are no
    ///     destinations previously assigned to the PathMission)
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or Sets on which element to move the loaded PathMission (This function is valid only if there are no destinations previously assigned to the PathMission)")]
    [DisplayName("DestinationItem2")]
    [Category("Path Option")]
    public AutomationDevice? DestinationItem2
    {
        get => PathDevices.Instance[DestinationItemId2];
        set
        {
            if (value != null) DestinationItemId2 = value.ItemGuid;
        }
    }

    /// <summary>
    ///     Returns or Sets on which element to move the loaded PathMission (This function is valid only if there are no
    ///     destinations previously assigned to the PathMission)
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or Sets on which element to move the loaded PathMission (This function is valid only if there are no destinations previously assigned to the PathMission)")]
    [DisplayName("IdDestinationItem")]
    [Category("Path Option")]
    public Guid DestinationItemId
    {
        get => _destinationItemId;
        set => SetProperty(ref _destinationItemId, value, true);
    }

    /// <summary>
    ///     Returns or Sets on which element to move the loaded PathMission (This function is valid only if there are no
    ///     destinations previously assigned to the PathMission)
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or Sets on which element to move the loaded PathMission (This function is valid only if there are no destinations previously assigned to the PathMission)")]
    [DisplayName("IdDestinationItem2")]
    [Category("Path Option")]
    public Guid DestinationItemId2
    {
        get => _destinationItemId2;
        set => SetProperty(ref _destinationItemId2, value, true);
    }

    /// <summary>
    ///     Returns the Automation Devices that are the movement destinations of the Automation Device in order of
    ///     PriorityInRoutePath
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns the Automation Devices that are the movement destinations of the Automation Device in order of PriorityInRoutePath")]
    [DisplayName("DestinationItemsLinked")]
    [Category("Path Option")]
    [ReadOnly(true)]
    public List<AutomationDevice?> DestinationItemsLinked
    {
        get
        {
            return PathJunctions.Instance.Where(x =>
                    x is { SourceItem: not null, DestinationItem: not null } && x.SourceId == ItemGuid)
                .OrderBy(x => x.DestinationItem?.PriorityInRoutePath).Select(x => x.DestinationItem).ToList();
        }
    }

    /// <summary>
    ///     Returns or Sets if when an extraction PathMission passes on this Item the system will balance the output on the
    ///     buffers reachable from it using ReachableBuffers property
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or Sets if when an extraction PathMission passes on this Item the system will balance the output on the buffers reachable from it using ReachableBuffers property")]
    [DisplayName("DistributeOnBuffers")]
    [Category("Path Option")]
    public bool DistributeOnBuffers
    {
        get => _distributeOnBuffers;
        set => SetProperty(ref _distributeOnBuffers, value, true);
    }

    /// <summary>
    ///     Returns or Sets the point at which to consider the extracted UDCs for inventory notes
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the point at which to consider the extracted UDCs for inventory notes")]
    [DisplayName("ExtractionCheckPoint")]
    [Category("Path Option")]
    public bool ExtractionCheckPoint
    {
        get => _extractionCheckPoint;
        set => SetProperty(ref _extractionCheckPoint, value, true);
    }

    /// <summary>
    ///     Returns whether the AutomationDeviceBase is connected to an AutomationDeviceBase of type Storage
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns whether the AutomationDevice is connected to an AutomationDevice of type Storage")]
    [DisplayName("IsLinkedToStorage")]
    [Category("Path Option")]
    public bool IsLinkedToStorage
    {
        get { return DestinationItemsLinked.Any(x => x is { DeviceType: DeviceTypes.Storage }); }
    }

    /// <summary>
    ///     Returns or Sets where to store the PathMission and if so, builds the movement mission
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets where to store the PathMission and if so, builds the movement mission")]
    [DisplayName("LookWhereAllocateLu")]
    [Category("Path Option")]
    [TypeConverter(typeof(PropertyTypeConverter))]
    public LookWhereAllocateLuTypes LookWhereAllocateLu
    {
        get => _lookWhereAllocateLu;
        set => SetProperty(ref _lookWhereAllocateLu, value, true);
    }

    /// <summary>
    ///     Returns or Sets whether when a PathMission is loaded it sets its ActiveFilter property to true
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets whether when a PathMission is loaded it sets its ActiveFilter property to true")]
    [DisplayName("OnLoadLuActiveFilter")]
    [Category("Path Option")]
    public bool OnLoadLuActiveFilter
    {
        get => _onLoadLuActiveFilter;
        set => SetProperty(ref _onLoadLuActiveFilter, value, true);
    }

    /// <summary>
    ///     Returns or Sets the Cartesian position number starting from top to bottom or right to left plus (top = 0, right =
    ///     0)
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or Sets the Cartesian position number starting from top to bottom or right to left plus (top = 0, right = 0)")]
    [DisplayName("OrderNumberGroup")]
    [Category("Path Option")]
    public int OrderNumberGroup
    {
        get => _orderNumberGroup;
        set => SetProperty(ref _orderNumberGroup, value, true);
    }

    /// <summary>
    ///     Returns or sets the number of movement priorities in picking and depositing when multiple elements are connected to
    ///     the same element, for example the CircularDistributor or the shuttle with multiple load units, in this way a
    ///     picking or depositing priority can be set
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or sets the number of movement priorities in picking and depositing when multiple elements are connected to the same element, for example the CircularDistributor or the shuttle with multiple load units, in this way a picking or depositing priority can be set")]
    [DisplayName("PriorityInRoutePath")]
    [Category("Path Option")]
    public int PriorityInRoutePath
    {
        get => _priorityInRoutePath;
        set => SetProperty(ref _priorityInRoutePath, value, true);
    }

    /// <summary>
    ///     Returns the list of Buffers that can be reached from this item in a maximum of 50 steps
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the list of Buffers that can be reached from this item in a maximum of 50 steps")]
    [DisplayName("ReachableBuffers")]
    [Category("Path Option")]
    public List<AutomationDevice> ReachableBuffers
    {
        get
        {
            var argStompedItems = new List<Guid>();
            var argStompedGroupRoutePath = new List<int>();
            return SearchReachableBuffers(this, 0, argStompedItems, argStompedGroupRoutePath);
        }
    }

    /// <summary>
    ///     Returns the list of Stores that can be reached from this item in a maximum of 50 steps
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the list of Stores that can be reached from this item in a maximum of 50 steps")]
    [DisplayName("ReachableStores")]
    [Category("Path Option")]
    public List<AutomationDevice> ReachableStoreOperators
    {
        get
        {
            var argStompedItems = new List<Guid>();
            var argStompedGroupRoutePath = new List<int>();
            return SearchReachableStoreOperators(this, 0, argStompedItems, argStompedGroupRoutePath);
        }
    }

    /// <summary>
    ///     Returns or Sets the rejection point on user request
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the rejection point on user request")]
    [DisplayName("RejectDestinationUserRequest")]
    [Category("Path Option")]
    [TypeConverter(typeof(AutomationDeviceType))]
    public Guid RejectDestinationUserRequest
    {
        get => _rejectDestinationUserRequest;
        set => SetProperty(ref _rejectDestinationUserRequest, value, true);
    }

    /// <summary>
    ///     Returns or Sets where the PathMission will be rejected if there are no cells available for storage
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets where the PathMission will be rejected if there are no cells available for storage")]
    [DisplayName("RejectItemDestinationAllocateLu")]
    [Category("Path Option")]
    [TypeConverter(typeof(AutomationDeviceType))]
    public Guid RejectItemDestinationAllocateLu
    {
        get => _rejectItemDestinationAllocateLu;
        set => SetProperty(ref _rejectItemDestinationAllocateLu, value, true);
    }

    /// <summary>
    ///     Returns or sets where the PathMission will be rejected in case of error in reading the dimensional and
    ///     identification information from the Connection
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or sets where the PathMission will be rejected in case of error in reading the dimensional and identification information from the PLC")]
    [DisplayName("RejectItemDestinationGetLuInfo")]
    [Category("Path Option")]
    [TypeConverter(typeof(AutomationDeviceType))]
    public AutomationDevice? RejectItemDestinationGetLuInfo
    {
        get => PathDevices.Instance[RejectItemDestinationIdGetLuInfo];
        set
        {
            if (value != null) RejectItemDestinationIdGetLuInfo = value.ItemGuid;
        }
    }

    /// <summary>
    ///     Returns or sets where the PathMission will be rejected in case of error in reading the dimensional and
    ///     identification information from the Connection
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or sets where the PathMission will be rejected in case of error in reading the dimensional and identification information from the PLC")]
    [DisplayName("RejectItemDestinationIdGetLuInfo")]
    [Category("Path Option")]
    [TypeConverter(typeof(AutomationDeviceType))]
    public Guid RejectItemDestinationIdGetLuInfo
    {
        get => _rejectItemDestinationIdGetLuInfo;
        set => SetProperty(ref _rejectItemDestinationIdGetLuInfo, value, true);
    }

    /// <summary>
    ///     Returns the Automation Devices that are the movement sources of the Automation Device in order of
    ///     PriorityInRoutePath
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns the Automation Devices that are the movement sources of the Automation Device in order of PriorityInRoutePath")]
    [DisplayName("SourceItemsLinked")]
    [Category("Path Option")]
    [ReadOnly(true)]
    public List<AutomationDevice?> SourceItemsLinked
    {
        get
        {
            return PathJunctions.Instance
                .Where(x => x.SourceItem != null && x.DestinationItem != null && x.DestinationId == ItemGuid)
                .OrderBy(x => x.SourceItem?.PriorityInRoutePath).Select(x => x.SourceItem).ToList();
        }
    }

    /// <summary>
    ///     Returns the Storage that are the movement destinations of the Automation Device
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the Storage that are the movement destinations of the Automation Device")]
    [DisplayName("StorageItemsLinked")]
    [Category("Path Option")]
    [ReadOnly(true)]
    public List<AutomationDevice?> StorageItemsLinked
    {
        get
        {
            return PathJunctions.Instance.Where(x =>
                x is { SourceItem: not null, DestinationItem: not null } &&
                x.SourceId == ItemGuid &&
                x.DestinationItem.DeviceType == DeviceTypes.Storage).Select(x => x.DestinationItem).ToList();
        }
    }

    /// <summary>
    ///     Returns or Sets whether the element should be considered filterable by PathMission with ActiveFilter set to true
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or Sets whether the element should be considered filterable by PathMission with ActiveFilter set to true")]
    [DisplayName("ToBeFiltered")]
    [Category("Path Option")]
    public bool ToBeFiltered
    {
        get => _toBeFiltered;
        set => SetProperty(ref _toBeFiltered, value, true);
    }

    /// <summary>
    ///     Returns or Sets the number of items to check to see if PathMission is arriving for loading
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the number of items to check to see if PathMission is arriving for loading")]
    [DisplayName("WaitLookingLevels")]
    [Category("Path Option")]
    public int WaitLookingLevels
    {
        get => _waitLookingLevels;
        set => SetProperty(ref _waitLookingLevels, value, true);
    }

    #endregion Methods

}
