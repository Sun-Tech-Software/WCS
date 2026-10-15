namespace Wcs.Api.Contracts.Dtos;

/// <summary>Una giunzione da creare (la chiave la assegna il motore) o da aggiornare (solo la priorità).</summary>
public sealed record JunctionRequest(Guid SourceItemId, Guid DestinationItemId, int Priority);
