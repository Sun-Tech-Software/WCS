using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;

namespace Wcs.WebEditor.Live;

/// <summary>
/// The active missions as the missions list (compito 3, row 60) holds them: one read of the host,
/// then the channel's differences on top of it - the rule the host states for every listener
/// (<see cref="IPlantEvents"/>: first the GET, then the events, again at every reconnection).
/// </summary>
/// <remarks>
/// <para>
/// <b>Events that arrive while a read is in flight are held</b> - the last one per mission - and
/// replayed on top of the read once it lands: the answer on its way may predate them, and applying
/// them before it would lose them under it. The same rule <see cref="PlantLiveFeed"/> keeps for the
/// devices' statuses around its snapshot. If the read fails, they go on the rows already held.
/// </para>
/// <para>
/// <b>Before the first read there is nothing to apply them to</b>: a list made of the events alone
/// would show some missions and not others, with nothing to say which. They are dropped, and the
/// first read covers them.
/// </para>
/// <para>
/// Not thread-safe, and it need not be: the list uses it on its renderer's dispatcher only.
/// </para>
/// </remarks>
public sealed class LiveMissionBoard
{
    private Dictionary<Guid, MissionDto> _rows = [];
    private readonly Dictionary<Guid, (MissionDto Mission, MissionChange Change)> _held = [];

    /// <summary>A read has landed at least once: the rows are the host's.</summary>
    public bool HasRead { get; private set; }

    /// <summary>A read is in flight: the events that arrive now are held.</summary>
    public bool Reading { get; private set; }

    public IReadOnlyCollection<MissionDto> Rows => _rows.Values;

    public MissionDto? Find(Guid missionId) => _rows.GetValueOrDefault(missionId);

    public void BeginRead() => Reading = true;

    /// <summary>The read landed: its rows replace the old ones, and the events held meanwhile go on top.</summary>
    public void ReadLanded(IEnumerable<MissionDto> missions)
    {
        var rows = new Dictionary<Guid, MissionDto>();
        foreach (var mission in missions) rows.TryAdd(mission.MissionId, mission);
        _rows = rows;
        HasRead = true;
        Reading = false;
        ReplayHeld();
    }

    /// <summary>The read did not go through: the events held meanwhile go on the rows already held (none before the first read).</summary>
    public void ReadFailed()
    {
        Reading = false;
        if (HasRead) ReplayHeld();
        else _held.Clear();
    }

    /// <summary>
    /// One event of the channel. True when the rows changed - a mission appeared, went, or is not
    /// what it was - so that the list renders only then; false when it was held or dropped.
    /// </summary>
    public bool Apply(MissionDto mission, MissionChange change)
    {
        if (Reading)
        {
            _held[mission.MissionId] = (mission, change);
            return false;
        }

        return HasRead && ApplyNow(mission, change);
    }

    private bool ApplyNow(MissionDto mission, MissionChange change)
    {
        if (change is MissionChange.Completed or MissionChange.Removed)
            return _rows.Remove(mission.MissionId);

        if (_rows.TryGetValue(mission.MissionId, out var before) && before == mission) return false;
        _rows[mission.MissionId] = mission;
        return true;
    }

    private void ReplayHeld()
    {
        foreach (var (mission, change) in _held.Values) ApplyNow(mission, change);
        _held.Clear();
    }
}
