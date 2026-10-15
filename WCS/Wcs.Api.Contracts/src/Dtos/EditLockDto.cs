namespace Wcs.Api.Contracts.Dtos;

/// <summary>
/// Lo stato del permesso di modifica dell'impianto. <c>IsMine</c> e' relativo a chi ha fatto la
/// richiesta che ha prodotto questa risposta - lo stesso stato del titolare, letto da titolare o
/// da chiunque altro, ha <c>IsMine</c> diverso: non e' un campo del permesso, e' un campo della
/// risposta.
/// </summary>
public sealed record EditLockDto(string Holder, DateTime SinceLocal, DateTime ExpiresLocal, bool IsMine);

/// <summary>
/// Chi chiede, rinnova o rilascia il permesso. <c>Holder</c> e' l'identificativo del circuito
/// Blazor che fa la richiesta - non un'autenticazione, non dice chi e' la persona: serve solo a
/// distinguere una scheda del browser da un'altra.
/// </summary>
public sealed record EditLockRequest(string Holder);
