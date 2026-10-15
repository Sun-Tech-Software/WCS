using Wcs.Engine.Items;
using Wcs.Engine.Persistence.Entities;

namespace Wcs.Engine.Api;

public enum EngineMissionChange { Started, StepChanged, Completed, Removed, PositionChanged }
public enum EngineQueueChange { Added, Updated, Removed }

/// <summary>
/// Chi ospita il motore riceve qui gli eventi da spingere fuori. Sostituisce <c>ZSocket</c>:
/// il motore non sa più che cosa c'è dall'altra parte.
/// </summary>
/// <remarks>
/// Le chiamate arrivano dal thread che produce l'evento — il ciclo del motore, il thread del
/// campo, lo scrittore del database — e non sono avvolte da alcuna guardia: <b>chi implementa
/// non deve né bloccare né sollevare</b>. Un'implementazione che aspetta la rete ferma il motore;
/// una che solleva rompe la scrittura o la decisione da cui l'evento è nato. La forma giusta è
/// mappare e accodare, con un <c>try</c> proprio attorno a tutt'e due.
/// </remarks>
public interface IEngineEventSink
{
    void ItemChanged(AutomationDevice device);
    void NavigatorStatusChanged();
    void MissionChanged(PathMission mission, EngineMissionChange change);
    void ExtractionQueueChanged(ExtractionQueueItem item, EngineQueueChange change);
    void CommandChanged(CommandRecord command);
}
