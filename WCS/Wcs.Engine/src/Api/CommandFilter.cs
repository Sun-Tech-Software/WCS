namespace Wcs.Engine.Api;

/// <summary>
/// I filtri di <c>GET /commands</c>: tutti facoltativi, in AND. <c>MinType</c>/<c>MaxType</c>
/// delimitano la fascia del tipo (i comandi utente del Client sono sotto 3000);
/// <c>ExcludeUserId</c> toglie un utente (il motore firma i propri con ORCHESTRA_ROUTE) — come nel Client
/// di oggi, in SQL esclude anche le righe con <c>UserId</c> nullo, non solo quelle di un altro utente;
/// <c>ShowStatus</c> e' lo stato di visualizzazione della vista dei comandi (0 = da mostrare).
/// </summary>
public sealed record CommandFilter(short? Status = null, string? WorkstationId = null, string? UserId = null,
    DateTime? From = null, DateTime? To = null, Guid? ItemId = null, short? ShowStatus = null,
    int? MinType = null, int? MaxType = null, string? ExcludeUserId = null, int Take = 200);
