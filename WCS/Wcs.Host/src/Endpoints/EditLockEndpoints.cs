using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.Engine.Api;
using Wcs.Host.Mapping;
using Wcs.Host.Security;

namespace Wcs.Host.Endpoints;

/// <summary>Le due rotte del permesso di modifica: chiedere/rinnovare, e rilasciare.</summary>
internal static class EditLockEndpoints
{
    /// <summary>
    /// Quanto dura il permesso fra un rinnovo e l'altro. E' l'host a deciderlo, non chi chiede:
    /// chi chiama non manda una durata, la legge dalla risposta (<c>ExpiresLocal - SinceLocal</c>)
    /// e ci si regola per rinnovare a meta' strada.
    /// </summary>
    internal static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);

    public static RouteGroupBuilder MapEditLock(this RouteGroupBuilder api)
    {
        // Concede o rinnova, nella stessa rotta: se il richiedente e' gia' il titolare valido si
        // tratta di un rinnovo, altrimenti di una richiesta nuova - che riesce solo se non c'e' un
        // titolare valido di qualcun altro. Un solo metodo del motore (EditLock.AcquireOrRenew)
        // decide ed emette lo stato risultante sotto lo stesso lock: comporre qui tre chiamate
        // pubbliche separate (Renew, poi TryAcquire, poi leggere Current) apriva una finestra fra
        // l'acquisizione riuscita e la lettura dello stato in cui una DELETE concorrente poteva
        // azzerare il titolare - Current tornava nullo, e la mappatura con "!" degenerava in
        // un'eccezione (500) al posto della risposta prevista.
        api.MapPost(ApiRoutes.EditLock, (EditLockRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Holder))
                return Results.Problem("Holder e' obbligatorio", statusCode: StatusCodes.Status400BadRequest);

            var (granted, state) = EditLock.AcquireOrRenew(request.Holder, Lease);
            return granted
                ? Results.Ok(DtoMapping.ToDto(state, isMine: true))
                : Results.Conflict(DtoMapping.ToDto(state, isMine: false));
        }).DoesNotRequireEditPermission("e' la rotta che concede il permesso: non puo' esigere se stessa")
        .WithName("AcquireOrRenewEditLock");

        // L'identita' passa in query, come DeleteFromExtractionQueueAsync fa gia' per
        // workstationId/userId: una DELETE senza corpo, non una novita' nel client.
        api.MapDelete(ApiRoutes.EditLock, (string holder) =>
        {
            EditLock.Release(holder);
            return Results.NoContent();
        }).DoesNotRequireEditPermission("e' la rotta che restituisce il permesso: non puo' esigere se stessa")
        .WithName("ReleaseEditLock");

        return api;
    }
}
