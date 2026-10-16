using System.Net;

namespace Wcs.Wms.Contracts;

/// <summary>
/// Il gestionale non ha risposto, o ha risposto un errore che non è una risposta di dominio.
/// Chi la riceve di norma non prende decisioni: lascia la missione in attesa e riprova.
/// </summary>
/// <remarks>
/// <b>Tranne quando il gestionale HA risposto, e ha detto di no.</b> Un 4xx - una riga che non
/// conosce più, un corpo che rifiuta - non guarisce riprovando: è un rifiuto, non un'assenza. Fino
/// al 24/9/2026 le due cose arrivavano identiche, e un outbox durevole che riprova per sempre ciò che
/// il gestionale non accetterà mai si ferma su quella riga e trattiene tutto ciò che viene dopo.
/// <see cref="IsPermanentRejection"/> le separa senza cambiare il tipo: ogni <c>catch</c> esistente
/// continua a vedere la stessa eccezione e a comportarsi come prima, e solo chi ha bisogno di
/// distinguere lo chiede.
/// </remarks>
public sealed class WarehouseUnavailableException(string operation, Exception? inner = null)
    : Exception($"Il gestionale non ha risposto a «{operation}».", inner)
{
    private readonly bool _rejectedByContract;
    private readonly string? _detail;

    /// <summary>
    /// The warehouse answered, but with something the contract does not know (an outcome added by a
    /// newer version): asking again gives the same answer, so it is a permanent rejection like a 4xx,
    /// and <paramref name="detail"/> says what came back.
    /// </summary>
    public WarehouseUnavailableException(string operation, string detail) : this(operation)
    {
        _rejectedByContract = true;
        _detail = detail;
    }

    /// <inheritdoc />
    public override string Message => _detail is null ? base.Message : $"«{Operation}»: {_detail}";

    /// <summary>Il nome dell'operazione che non è riuscita, per il registro.</summary>
    public string Operation { get; } = operation;

    /// <summary>
    /// Il codice con cui il gestionale ha risposto, quando ha risposto: lo porta
    /// <see cref="HttpRequestException"/> che <c>EnsureSuccessStatusCode</c> solleva. Nullo per una
    /// connessione caduta, un timeout o un corpo illeggibile.
    /// </summary>
    public HttpStatusCode? StatusCode { get; } = (inner as HttpRequestException)?.StatusCode;

    /// <summary>
    /// Il gestionale ha risposto e ha rifiutato: un 4xx, esclusi 408 (timeout della richiesta) e 429
    /// (troppe richieste), che sono il suo modo di dire «riprova più tardi»; oppure ha risposto un
    /// valore che il contratto non conosce.
    /// </summary>
    public bool IsPermanentRejection => _rejectedByContract ||
        StatusCode is { } code && (int)code is >= 400 and < 500 &&
        code is not HttpStatusCode.RequestTimeout and not HttpStatusCode.TooManyRequests;
}
