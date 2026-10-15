using Wcs.Api.Contracts.Dtos;

namespace Wcs.Api.Contracts;

public enum MissionChange { Started, StepChanged, Completed, Removed, PositionChanged }
public enum QueueChange { Added, Updated, Removed }

/// <summary>
/// Gli eventi che l'host spinge ai client. Regola per chi ascolta: prima lo stato completo da
/// <c>GET</c>, poi le differenze da qui; a ogni riconnessione si ripete.
/// </summary>
public interface IPlantEvents
{
    Task ItemStatusChanged(ItemStatus item);

    /// <summary>
    /// Lo stato del motore è cambiato — <b>oppure</b> la sequenza delle differenze ha un buco.
    /// </summary>
    /// <remarks>
    /// L'host manda questo messaggio anche quando la coda degli eventi si è riempita e ne ha
    /// scartati: vale allora come «ricarica lo stato completo», cioè rifai le <c>GET</c> come alla
    /// prima connessione. Chi ascolta non può distinguere i due casi e non deve provarci: trattare
    /// sempre questo messaggio come un invito a riallinearsi è corretto in entrambi.
    /// </remarks>
    Task NavigatorStatusChanged(EngineStatus status);
    Task MissionChanged(MissionDto mission, MissionChange change);
    Task ExtractionQueueChanged(ExtractionQueueItemDto item, QueueChange change);
    Task CommandChanged(CommandDto command);
    Task Watchdog(DateTime utcNow);
}
