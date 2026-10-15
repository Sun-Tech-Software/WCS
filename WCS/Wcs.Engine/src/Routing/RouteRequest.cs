namespace Wcs.Engine.Routing;

/// <summary>
/// La domanda alla ricerca: da dove a dove, con quale gateway (Guid.Empty se nessuno), se ignorare
/// lo stato dei dispositivi (solo <c>PickUpAndStoreAgain</c>), se accettare i dispositivi fuori
/// linea o in errore (la seconda passata del chiamante), e quali dispositivi scartare (gli elementi
/// filtrati da un deposito in arrivo).
/// </summary>
/// <param name="GatewayId">Il dispositivo da cui la rotta deve passare; una rotta che non lo attraversa non vale.</param>
public sealed record RouteRequest(
    Guid SourceId,
    Guid DestinationId,
    Guid GatewayId,
    bool IgnoreItemStatus,
    bool ConsiderOffLineAvailable,
    IReadOnlySet<Guid> ItemsToDiscard);

/// <summary>Un passo della rotta trovata; <see cref="FromGroupRoutePath"/> e' il livello del dispositivo di partenza, come lo scrive <c>MissionStep</c>.</summary>
public readonly record struct RouteHop(Guid FromId, Guid ToId, int FromGroupRoutePath);
