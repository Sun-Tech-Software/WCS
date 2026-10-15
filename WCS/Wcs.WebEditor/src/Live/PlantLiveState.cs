using System.Globalization;
using Wcs.Api.Contracts.Dtos;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Live;

/// <summary>What the canvas shows of one device, live: its status colour (none in Ready) and the load unit on board.</summary>
public sealed record LiveDeviceView(Guid ItemId, string? StatusColor, string StatusName, string? LoadUnit);

/// <summary>A movement in progress: the device running a step, and the device waiting for that step.</summary>
public sealed record LiveMotion(Guid From, Guid To);

/// <summary>
/// The live status of the plant as the host's events tell it, and what the canvas draws of it. No
/// UI and no I/O: <see cref="PlantLiveFeed"/> feeds it, the page reads it.
/// </summary>
/// <remarks>
/// The colours and the movement rule are the WinForms Editor's (<c>DiagramHandler.StatusColors</c>
/// and <c>HandleRunningStepCreation</c>). One difference, the owner's decision of 2026-09-24: the
/// WinForms had no colour for Recovering, which therefore drew nothing; here it has one (brown), so
/// that a device in that state is not mistaken for a Ready one.
/// </remarks>
public sealed class PlantLiveState(Func<Guid, DeviceTypes?> deviceTypeOf)
{
    private readonly Dictionary<Guid, ItemStatus> _byItem = [];

    /// <summary>Replaces everything: a snapshot is the truth, the events before it are not.</summary>
    public void Reset(IEnumerable<ItemStatus> snapshot)
    {
        _byItem.Clear();
        foreach (var status in snapshot) _byItem[status.ItemId] = status;
    }

    public void Apply(ItemStatus status) => _byItem[status.ItemId] = status;

    public ItemStatus? StatusOf(Guid itemId) => _byItem.GetValueOrDefault(itemId);

    /// <summary>The devices that show something: a status other than Ready, or a load unit on board.</summary>
    public IReadOnlyList<LiveDeviceView> Devices() =>
        [.. _byItem.Values.Select(ViewOf).Where(view => view.StatusColor is not null || view.LoadUnit is not null)];

    public IReadOnlyList<LiveMotion> Motions()
    {
        var waitingFor = new Dictionary<Guid, Guid>();
        foreach (var status in _byItem.Values)
            if (status.WaitingStepId != Guid.Empty) waitingFor.TryAdd(status.WaitingStepId, status.ItemId);

        var motions = new List<LiveMotion>();
        foreach (var status in _byItem.Values)
        {
            if (status.RunningStepId == Guid.Empty) continue;
            if (!waitingFor.TryGetValue(status.RunningStepId, out var to) || to == status.ItemId) continue;
            if (deviceTypeOf(status.ItemId) == DeviceTypes.Storage || deviceTypeOf(to) == DeviceTypes.Storage) continue;
            motions.Add(new LiveMotion(status.ItemId, to));
        }

        return motions;
    }

    public static LiveDeviceView ViewOf(ItemStatus status) =>
        new(status.ItemId, ColorOf(status.Status), NameOf(status.Status),
            status.LuLoadStatus == LuLoadStatusTypes.Loaded ? LoadUnitOf(status) : null);

    public static string? ColorOf(DeviceStatusTypes status) => status switch
    {
        DeviceStatusTypes.Error => "#ff0000",
        DeviceStatusTypes.Moving => "#ffa500",
        DeviceStatusTypes.Loading => "#0000ff",
        DeviceStatusTypes.Unloading => "#00ffff",
        DeviceStatusTypes.OffLine => "#ee82ee",
        DeviceStatusTypes.Locked => "#808080",
        DeviceStatusTypes.Warning => "#ffff00",
        DeviceStatusTypes.Busy => "#ffffff",
        DeviceStatusTypes.Recovering => "#8b4513",
        _ => null,
    };

    public static string NameOf(DeviceStatusTypes status) => status switch
    {
        DeviceStatusTypes.Ready => "Ready",
        DeviceStatusTypes.Moving => "Moving",
        DeviceStatusTypes.Loading => "Loading",
        DeviceStatusTypes.Unloading => "Unloading",
        DeviceStatusTypes.Busy => "Busy",
        DeviceStatusTypes.Recovering => "Recovering",
        DeviceStatusTypes.OffLine => "Offline",
        DeviceStatusTypes.Locked => "Locked",
        DeviceStatusTypes.Warning => "Warning",
        DeviceStatusTypes.Error => "In Error",
        _ => $"Status {(byte)status}",
    };

    private static string LoadUnitOf(ItemStatus status)
    {
        var id = status.LoadedLuId.ToString(CultureInfo.InvariantCulture);
        return status.LoadedLuCode is { Length: > 0 } code ? $"{code} ({id})" : id;
    }
}
