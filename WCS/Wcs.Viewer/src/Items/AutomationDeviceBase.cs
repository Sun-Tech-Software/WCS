using Wcs.Api.Contracts.Dtos;
using Wcs.Common;
using Wcs.Common.Events;

namespace Wcs.Viewer.Items;

public class AutomationDeviceBase
{
    #region Private Fields

    private Enums.LuLoadStatusTypes _lULoadStatus;
    private Guid _runningStepId = Guid.Empty;
    private Enums.DeviceStatusTypes _status;
    private Guid _waitingStepId = Guid.Empty;

    #endregion Private Fields

    #region Public Constructors

    public AutomationDeviceBase()
    {
    }

    #endregion Public Constructors

    #region Public Methods

    /// <summary>Crea la voce a partire dallo stato dal vivo ricevuto dall'API.</summary>
    public static AutomationDeviceBase From(ItemStatus dto)
    {
        var item = new AutomationDeviceBase { ItemGuid = dto.ItemId };
        item.Apply(dto);
        return item;
    }

    /// <summary>Applica lo stato dal vivo alla voce esistente (evento ItemStatusChanged, o dopo un GetViewItemStatus).</summary>
    public void Apply(ItemStatus dto)
    {
        ASI = dto.Asi;
        PARTITION = dto.Partition;
        SUBITEM = dto.Subitem;
        Name = dto.Name;
        Status = dto.Status;
        LuLoadStatus = dto.LuLoadStatus;
        LuCode = dto.LoadedLuCode;
        LuId = dto.LoadedLuId;
        RunningStepId = dto.RunningStepId;
        WaitingStepId = dto.WaitingStepId;
    }

    #endregion Public Methods

    #region Public Delegates

    public delegate void LuLoadStatusChangedEventHandler(object sender, AutomationDeviceStatusChangedEventArgs e);

    public delegate void RunningStepChangedEventHandler(object sender, AutomationDeviceRunningStepIdChangedEventArgs e);

    public delegate void StatusChangedEventHandler(object sender, AutomationDeviceStatusChangedEventArgs e);

    #endregion Public Delegates

    #region Public Events

    public event LuLoadStatusChangedEventHandler? LuLoadStatusChanged;

    public event RunningStepChangedEventHandler? RunningStepChanged;

    public event StatusChangedEventHandler? StatusChanged;

    #endregion Public Events

    #region Public Properties

    public string ASI { get; set; } = string.Empty;
    public Guid ItemGuid { get; set; } = Guid.Empty;
    public string LuCode { get; set; } = string.Empty;
    public decimal LuId { get; set; }

    public Enums.LuLoadStatusTypes LuLoadStatus
    {
        get => _lULoadStatus;
        set
        {
            if (_lULoadStatus == value) return;
            _lULoadStatus = value;
            LuLoadStatusChanged?.Invoke(this, new AutomationDeviceStatusChangedEventArgs(value, Status));
        }
    }

    public string Name { get; set; } = string.Empty;
    public string PARTITION { get; set; } = string.Empty;

    public Guid RunningStepId
    {
        get => _runningStepId;
        set
        {
            if (_runningStepId == value) return;
            _runningStepId = value;
            RunningStepChanged?.Invoke(this, new AutomationDeviceRunningStepIdChangedEventArgs(value, WaitingStepId));
        }
    }

    public Enums.DeviceStatusTypes Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            StatusChanged?.Invoke(this, new AutomationDeviceStatusChangedEventArgs(LuLoadStatus, value));
        }
    }

    public string SUBITEM { get; set; } = string.Empty;

    public Guid WaitingStepId
    {
        get => _waitingStepId;
        set
        {
            if (_waitingStepId == value) return;
            _waitingStepId = value;
            RunningStepChanged?.Invoke(this, new AutomationDeviceRunningStepIdChangedEventArgs(RunningStepId, value));
        }
    }

    #endregion Public Properties
}