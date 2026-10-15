using System.ComponentModel;
using Helper.Converters;
using Wcs.Common;

namespace Wcs.Viewer.Items;

public partial class AutomationDevice
{

    #region Methods

    /// <summary>
    ///     Returns or Sets whether to accept a UDC if the target is available
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets whether to accept a UDC if the target is available")]
    [DisplayName("AcceptLuIfDestinationIsAvailable")]
    [Category("Path Option")]
    public bool AcceptLuIfDestinationIsAvailable
    {
        get => _acceptLuIfDestinationIsAvailable;
        set
        {
            if (_acceptLuIfDestinationIsAvailable == value) return;
            _acceptLuIfDestinationIsAvailable = value;
            //ORM.AutomationDevice.Update(this);
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
    [DisplayName("DestinationItem")]
    [Category("Path Option")]
    public AutomationDevice? DestinationItem
    {
        get { return Navigator.PathDevices.FirstOrDefault(x => x != null && x.DestinationItemId == _destinationItemId); }
        //set
        //{
        //    if (value != null) DestinationItemId = value.ItemGuid;
        //}
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
        get { return Navigator.PathDevices.FirstOrDefault(x => x != null && x.DestinationItemId == _destinationItemId2); }
        //set
        //{
        //    if (value != null) DestinationItemId2 = value.ItemGuid;
        //}
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
        set
        {
            if (_destinationItemId == value) return;
            _destinationItemId = value;
            //ORM.AutomationDevice.Update(this);
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
    [DisplayName("IdDestinationItem2")]
    [Category("Path Option")]
    public Guid DestinationItemId2
    {
        get => _destinationItemId2;
        set
        {
            if (_destinationItemId2 == value) return;
            _destinationItemId2 = value;
            //ORM.AutomationDevice.Update(this);
        }
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
            return Navigator.PathJunctions.Where(x =>
                    x is { SourceItem: not null, DestinationItem: not null } && x.SourceId == ItemGuid)
                .OrderBy(x => x?.DestinationItem?.PriorityInRoutePath).Select(x => x?.DestinationItem).ToList();
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
        set
        {
            if (_distributeOnBuffers == value) return;
            _distributeOnBuffers = value;
            //ORM.AutomationDevice.Update(this);
        }
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
        set
        {
            if (_extractionCheckPoint == value) return;
            _extractionCheckPoint = value;
            //ORM.AutomationDevice.Update(this);
        }
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
        get { return DestinationItemsLinked.Any(x => x != null && x.DeviceType == Enums.DeviceTypes.Storage); }
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
    public Enums.LookWhereAllocateLuTypes LookWhereAllocateLu
    {
        get => _lookWhereAllocateLu;
        set
        {
            if (_lookWhereAllocateLu == value) return;
            _lookWhereAllocateLu = value;
            //ORM.AutomationDevice.Update(this);
        }
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
        set
        {
            if (_onLoadLuActiveFilter == value) return;
            _onLoadLuActiveFilter = value;
            //ORM.AutomationDevice.Update(this);
        }
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
        set
        {
            if (_orderNumberGroup == value) return;
            _orderNumberGroup = value;
            //ORM.AutomationDevice.Update(this);
        }
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
        set
        {
            if (_priorityInRoutePath == value) return;
            _priorityInRoutePath = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns the list of Buffers that can be reached from this item in a maximum of 50 steps
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the list of Buffers that can be reached from this item in a maximum of 50 steps")]
    [DisplayName("ReachableBuffers")]
    [Category("Path Option")]
    public List<AutomationDevice> ReachableBuffers { get; internal set; } = [];

    /// <summary>
    ///     Returns the list of Stores that can be reached from this item in a maximum of 50 steps
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the list of Stores that can be reached from this item in a maximum of 50 steps")]
    [DisplayName("ReachableStores")]
    [Category("Path Option")]
    public List<AutomationDevice> ReachableStoreOperators { get; internal set; } = [];

    /// <summary>
    ///     Returns or Sets the rejection point on user request
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the rejection point on user request")]
    [DisplayName("RejectDestinationUserRequest")]
    [Category("Path Option")]
    public Guid RejectDestinationUserRequest
    {
        get => _rejectDestinationUserRequest;
        set
        {
            if (_rejectDestinationUserRequest == value) return;
            _rejectDestinationUserRequest = value;
            //ORM.AutomationDevice.Update(this);
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
    [DisplayName("RejectItemDestination_GetLUInfo")]
    [Category("Path Option")]
    public AutomationDevice? RejectItemDestinationGetLuInfo
    {
        get { return Navigator.PathDevices.FirstOrDefault(x => x != null && x.ItemGuid == _rejectItemDestinationIdGetLuInfo); }
        //set
        //{
        //    if (value != null) RejectItemDestinationId_GetLUInfo = value.ItemGuid;
        //}
    }

    /// <summary>
    ///     Returns or Sets where the PathMission will be rejected if there are no cells available for storage
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets where the PathMission will be rejected if there are no cells available for storage")]
    [DisplayName("RejectItemDestinationAllocateLu")]
    [Category("Path Option")]
    public Guid RejectItemDestinationAllocateLu
    {
        get => _rejectItemDestinationAllocateLu;
        set
        {
            if (_rejectItemDestinationAllocateLu == value) return;
            _rejectItemDestinationAllocateLu = value;
            //ORM.AutomationDevice.Update(this);
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
    public Guid RejectItemDestinationIdGetLuInfo
    {
        get => _rejectItemDestinationIdGetLuInfo;
        set
        {
            if (_rejectItemDestinationIdGetLuInfo == value) return;
            _rejectItemDestinationIdGetLuInfo = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    /// <summary>
    ///     Returns the Automation Devices that are the movement sources of the Automation Device in order of
    ///     PriorityInRoutePath")
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
            return Navigator.PathJunctions
                .Where(x => x is { SourceItem: not null, DestinationItem: not null } && x.DestinationId == ItemGuid)
                .OrderBy(x => x?.SourceItem?.PriorityInRoutePath).Select(x => x?.SourceItem).ToList();
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
            return Navigator.PathJunctions.Where(x =>
                x?.SourceItem != null &&
                x.DestinationItem != null &&
                x.SourceId == ItemGuid &&
                x.DestinationItem.DeviceType == Enums.DeviceTypes.Storage).Select(x => x?.DestinationItem).ToList();
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
        set
        {
            if (_toBeFiltered == value) return;
            _toBeFiltered = value;
            //ORM.AutomationDevice.Update(this);
        }
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
        set
        {
            if (_waitLookingLevels == value) return;
            _waitLookingLevels = value;
            //ORM.AutomationDevice.Update(this);
        }
    }

    #endregion Methods

}
