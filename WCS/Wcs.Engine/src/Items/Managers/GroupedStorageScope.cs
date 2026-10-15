using static Wcs.Common.Enums;

namespace Wcs.Engine.Items.Managers;

/// <summary>
/// Possiede le modifiche temporanee di una ricerca di deposito raggruppata: conserva gli stati
/// originari e rilascia esclusivamente i blocchi porta acquisiti da questo giro.
/// </summary>
internal sealed class GroupedStorageScope
{
    internal const string CleanupFailuresDataKey = "GroupedStorageCleanupFailures";

    private readonly AutomationDevice _device;
    private readonly DeviceStatusTypes _deviceStatus;
    private readonly Action<string, Exception> _recordCleanupFailure;
    private readonly Func<string, Task<bool>> _acquireDoor;
    private readonly Func<string, Task> _unlockDoor;
    private readonly Dictionary<AutomationDevice, DeviceStatusTypes> _positions = [];
    private readonly HashSet<string> _ownedDoors = new(StringComparer.OrdinalIgnoreCase);

    internal GroupedStorageScope(
        AutomationDevice device,
        Action<string, Exception> recordCleanupFailure,
        Func<string, Task<bool>>? acquireDoor = null,
        Func<string, Task>? unlockDoor = null)
    {
        _device = device;
        _deviceStatus = device.Status;
        _recordCleanupFailure = recordCleanupFailure;
        _acquireDoor = acquireDoor ?? ORM.DbInfo.TrySetCellLockedForStoring;
        _unlockDoor = unlockDoor ?? ORM.DbInfo.SetCellUnlockedForStoring;
    }

    internal void TrackPosition(AutomationDevice position) =>
        _positions.TryAdd(position, position.Status);

    internal async Task<bool> TryAcquireDoorAsync(string door)
    {
        if (string.IsNullOrEmpty(door)) return false;
        if (_ownedDoors.Contains(door)) return true;
        if (!await _acquireDoor(door)) return false;

        _ownedDoors.Add(door);
        return true;
    }

    internal async Task RestoreAsync(Exception? primaryFailure)
    {
        var failures = new List<Exception>();

        foreach (var door in _ownedDoors)
            await AttemptAsync($"unlock door {door}", () => _unlockDoor(door), failures);

        foreach (var pair in _positions)
            Attempt($"restore position {pair.Key.ItemGuid}", () => Restore(pair.Key, pair.Value), failures);

        Attempt($"restore device {_device.ItemGuid}", () => Restore(_device, _deviceStatus), failures);

        if (failures.Count == 0) return;

        var aggregate = new AggregateException("Grouped storage cleanup failed.", failures);
        if (primaryFailure == null) throw aggregate;

        primaryFailure.Data[CleanupFailuresDataKey] = aggregate;
    }

    /// <summary>
    /// Rimette lo stato di prima solo se il dispositivo e' ancora nello stato che il giro gli ha dato
    /// (<see cref="DeviceStatusTypes.Recovering"/>). Un errore arrivato intanto - un altro membro che va
    /// in errore e manda il gruppo in <see cref="DeviceStatusTypes.Warning"/> mentre il giro aspetta il
    /// gestionale - resta: prima il ripristino lo copriva con lo stato preso all'inizio, e il gruppo
    /// ripartiva senza che nessuno l'avesse rimesso in marcia.
    /// </summary>
    private static void Restore(AutomationDevice device, DeviceStatusTypes status)
    {
        if (device.Status == DeviceStatusTypes.Recovering)
            device.Status = status;
    }

    private void Attempt(string resource, Action action, ICollection<Exception> failures)
    {
        try
        {
            action();
        }
        catch (Exception failure)
        {
            failures.Add(failure);
            RecordFailure(resource, failure);
        }
    }

    private async Task AttemptAsync(string resource, Func<Task> action, ICollection<Exception> failures)
    {
        try
        {
            await action();
        }
        catch (Exception failure)
        {
            failures.Add(failure);
            RecordFailure(resource, failure);
        }
    }

    private void RecordFailure(string resource, Exception failure)
    {
        try
        {
            _recordCleanupFailure(resource, failure);
        }
        catch
        {
            // Il canale diagnostico non e' una risorsa da ripristinare e non deve interrompere
            // gli sblocchi o i ripristini successivi. Il guasto originale resta nell'aggregato.
        }
    }
}
