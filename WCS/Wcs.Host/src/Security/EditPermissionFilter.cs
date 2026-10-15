using Wcs.Api.Contracts;
using Wcs.Engine.Api;

namespace Wcs.Host.Security;

/// <summary>
/// Marca una rotta come rotta che cambia la forma dell'impianto e quindi esige il permesso di
/// modifica. Non fa niente da sola: la usano <see cref="EditPermissionEndpointExtensions.RequiresEditPermission"/>,
/// che la mette nei metadati insieme al filtro vero, e il cancello di test
/// (<c>EditPermissionGateTest</c> nell'host) che verifica che nessuna rotta di scrittura di
/// <c>/plant/*</c> ne sia priva - e' un metadato apposta perche' un test puo' enumerarlo, mentre il
/// filtro da solo (che agisce sulla pipeline, non sui metadati) non si presterebbe alla stessa
/// verifica senza eseguire davvero ogni richiesta.
/// </summary>
internal sealed class RequiresEditPermissionMarker
{
    public static readonly RequiresEditPermissionMarker Instance = new();
    private RequiresEditPermissionMarker() { }
}

/// <summary>
/// Il cancello delle scritture che cambiano la forma dell'impianto (compito 4b, estensione del
/// permesso oltre le rotte del permesso stesso, che si fermavano a se' stesse): esige che chi
/// scrive porti <see cref="ApiRoutes.EditHolderHeader"/> con l'identita' di chi tiene davvero
/// <see cref="EditLock"/> in quel momento.
/// </summary>
/// <remarks>
/// Non e' un'autenticazione - quella resta <c>X-Api-Key</c>, controllata da
/// <c>ApiKeyMiddleware</c> prima che una richiesta arrivi qui - e' la prova che chi scrive e'
/// proprio chi tiene il permesso adesso, non un'altra scheda del browser ne' l'Editor WinForms, che
/// non lo chiede e non manda mai questa intestazione: da qui in poi ogni sua scrittura su
/// <c>/plant/*</c> trova questo cancello chiuso, per costruzione - e' la conseguenza voluta di
/// questo compito, non un guasto (si veda il commento sopra <c>PlantEndpoints.MapPlant</c>).
/// <para>
/// Risponde <b>423 Locked</b> (RFC 4918/WebDAV) e non 409: in questo host 409 e' gia' preso da «il
/// motore e' in Run» (<see cref="EngineBusyException"/>, tradotto da <c>EngineExceptionHandler</c>)
/// su molte di queste stesse rotte. Due motivi di rifiuto diversi con lo stesso codice sarebbero
/// indistinguibili per chi legge solo lo stato HTTP; 423 e' il codice che il web gia' usa per «la
/// risorsa e' bloccata da qualcun altro», che e' esattamente il caso qui.
/// </para>
/// <para>
/// <b>Che cosa garantisce, e fino a dove (ultima passata del compito 4b).</b> Il controllo
/// (<see cref="EditLock.VerifyAndRenew"/>) corre una volta sola, all'ingresso della richiesta: non
/// resta acceso per la durata della scrittura, che tocca il disco e quindi impiega tempo vero -
/// millisecondi, non gli istanti fra due <c>lock</c>. Il rimedio non e' tenere il permesso bloccato
/// fino alla fine della scrittura (serializzerebbe ogni scrittura dell'impianto dietro un solo
/// lucchetto, un costo pagato sempre per un caso che quasi non capita): e' che <b>ogni scrittura
/// autorizzata rinnova il permesso nello stesso passo in cui lo verifica</b>, sotto lo stesso
/// <c>lock</c> di <see cref="EditLock"/> - la prova piu' forte che esista che il titolare sta
/// lavorando davvero. Un editor attivo non perde mai il permesso mentre scrive: la scadenza si
/// sposta avanti a ogni sua scrittura, prima ancora che la scrittura stessa cominci.
/// <b>Il residuo, dichiarato invece che nascosto:</b> se una singola scrittura autorizzata dura piu'
/// a lungo dell'intera durata del permesso (<c>EditLockEndpoints.Lease</c>, oggi 60 secondi) - un
/// disco molto lento, non una gara fra due richieste - il permesso puo' scadere mentre quella
/// scrittura e' ancora in volo; se in quella stessa finestra un secondo titolare lo ottiene e scrive
/// a sua volta, le due scritture si sovrappongono senza che nessuno dei due cancelli se ne accorga.
/// Oltre al disco molto lento, la stessa finestra si apre anche piu' ordinariamente: se chi scrive
/// chiude la scheda mentre il salvataggio e' ancora in volo, il circuito di chiusura chiama
/// EditLock.Release senza che questo verifichi che la scrittura sia terminata.
/// E' un guasto di tempo di I/O, non piu' una corsa fra il controllo e l'azione: la finestra che
/// prima si apriva a ogni scrittura adesso si apre per un disco anormalmente lento oppure per una
/// chiusura della scheda ravvicinata al salvataggio.
/// </para>
/// </remarks>
internal sealed class EditPermissionFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var provided = context.HttpContext.Request.Headers[ApiRoutes.EditHolderHeader].ToString();

        if (string.IsNullOrEmpty(provided))
            return ValueTask.FromResult<object?>(Deny(EditLock.Current));

        var (valid, state) = EditLock.VerifyAndRenew(provided);
        return valid ? next(context) : ValueTask.FromResult<object?>(Deny(state));
    }

    /// <summary>
    /// Il corpo dice chi ha il permesso e da quando: chi lo legge e' un operatore che vuole sapere
    /// a chi chiedere, non uno sviluppatore. Quando nessuno tiene il permesso non c'e' un "chi" da
    /// dire - il messaggio lo dice esplicitamente invece di lasciare un campo vuoto senza spiegarlo.
    /// </summary>
    private static IResult Deny(EditLockState? current) => current is null
        ? Results.Problem(
            title: "Nessuno tiene il permesso di modifica",
            detail: "Questa scrittura cambia la forma dell'impianto ed esige il permesso di modifica, " +
                     "che al momento non tiene nessuno. Chiedilo con POST /plant/edit-lock prima di riprovare.",
            statusCode: StatusCodes.Status423Locked)
        : Results.Problem(
            title: "Permesso di modifica di qualcun altro",
            detail: $"Questa scrittura cambia la forma dell'impianto ed esige il permesso di modifica: " +
                     $"lo tiene {current.Holder} dalle {current.SinceLocal:HH:mm:ss}. Aspetta che scada, " +
                     "o chiedilo con POST /plant/edit-lock.",
            statusCode: StatusCodes.Status423Locked);
}

/// <summary>
/// La dichiarazione opposta a <see cref="RequiresEditPermissionMarker"/>: questa rotta di scrittura
/// non cambia la configurazione dell'impianto e NON esige il permesso, per il motivo scritto in
/// <see cref="Reason"/>. Anche questo e' solo un metadato: il cancello di test
/// (<c>EditPermissionGateTest</c>) pretende che ogni rotta di scrittura dell'API porti l'una o
/// l'altra dichiarazione, cosi' una rotta nuova non puo' nascere senza che qualcuno abbia deciso -
/// e scritto accanto alla sua mappatura - se il permesso le serve (compito 4b della console).
/// </summary>
internal sealed class EditPermissionNotRequiredMarker(string reason)
{
    public string Reason { get; } = reason;
}

internal static class EditPermissionEndpointExtensions
{
    /// <summary>
    /// Aggancia il cancello del permesso a una rotta: da chiamare su ogni rotta di scrittura che
    /// cambia la configurazione dell'impianto - in <c>/plant/*</c> la geometria, la configurazione di un
    /// dispositivo, la cancellazione, lo svuotamento, «tieni solo questi», le giunzioni, le
    /// mappature porte, il collegamento delle celle; fuori, la sostituzione delle connessioni PLC
    /// (<c>PUT /plc/connections</c>, compito 4b della console). Non sulle letture, che restano
    /// libere, e non sulle due rotte del permesso stesso (<c>POST</c>/<c>DELETE /plant/edit-lock</c>),
    /// che non possono esigere se stesse.
    /// </summary>
    public static RouteHandlerBuilder RequiresEditPermission(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter<EditPermissionFilter>().WithMetadata(RequiresEditPermissionMarker.Instance);

    /// <summary>
    /// Dichiara che una rotta di scrittura non esige il permesso, e perche': da chiamare sulle
    /// scritture che non cambiano la configurazione dell'impianto (comandi e code del motore, il
    /// registro, il permesso stesso). Non aggancia nessun filtro - la rotta resta com'era - ma senza
    /// questa dichiarazione o <see cref="RequiresEditPermission"/> il cancello di test fallisce.
    /// </summary>
    public static RouteHandlerBuilder DoesNotRequireEditPermission(this RouteHandlerBuilder builder, string reason) =>
        builder.WithMetadata(new EditPermissionNotRequiredMarker(reason));
}
