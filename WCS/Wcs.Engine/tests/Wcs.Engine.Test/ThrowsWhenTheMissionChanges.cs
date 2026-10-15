using Wcs.Engine.Api;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence.Entities;

namespace Wcs.Engine.Test;

/// <summary>
/// Il gestore degli eventi del motore che solleva quando la missione <c>failing</c> cambia porta o rotta
/// (<see cref="EngineMissionChange.StepChanged"/>, che <c>PathMission.LeaveTheDoor</c> annuncia): e' la
/// scrittura del cambio di quella missione che cade, a meta' di chi le fa lasciare la porta. Le altre
/// notifiche passano senza fare niente.
/// </summary>
internal sealed class ThrowsWhenTheMissionChanges(PathMission failing) : IEngineEventSink
{
    public void MissionChanged(PathMission mission, EngineMissionChange change)
    {
        if (ReferenceEquals(mission, failing) && change == EngineMissionChange.StepChanged)
            throw new InvalidOperationException($"la scrittura del cambio della missione {mission.MissionId} e' caduta");
    }

    public void CommandChanged(CommandRecord command) { }
    public void ItemChanged(AutomationDevice device) { }
    public void NavigatorStatusChanged() { }
    public void ExtractionQueueChanged(ExtractionQueueItem item, EngineQueueChange change) { }
}
