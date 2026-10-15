using Microsoft.AspNetCore.Diagnostics;
using Wcs.Api.Contracts.Dtos;
using Wcs.Engine.Api;

namespace Wcs.Host;

/// <summary>
/// Traduce in codici HTTP le eccezioni con cui le facciate del motore dicono «non posso
/// rispondere»: senza database si risponde <c>503</c>, con il motore non caricato <c>503</c>
/// (il motivo nel dettaglio), su un mese che lo storico non apre <c>400</c>, a motore non fermo
/// una scrittura di configurazione <c>409</c>, con il gestionale che non risponde <c>503</c>. Ogni
/// altra eccezione resta al gestore predefinito (<c>500</c>).
/// </summary>
/// <remarks>
/// <b>409 o 400.</b> La convenzione dell'host: <c>400</c> quando la richiesta in se' e' sbagliata -
/// un file che non si puo' importare (<c>GraphEndpoints.Validate</c>), un intervallo fuori dallo
/// storico -, <c>409</c> quando e' ben formata ma lo stato dell'impianto la vieta adesso, e smette di
/// vietarla quando lo stato cambia: il motore non fermo, il lavoro pendente, e dal compito 4c una
/// connessione PLC che dispositivi usano ancora (<see cref="PlcConnectionInUseException"/>: la stessa
/// lista passa appena quei dispositivi sono spostati).
/// </remarks>
internal sealed class EngineExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            EngineStoreUnavailableException => (StatusCodes.Status503ServiceUnavailable, "Il database del motore non è disponibile"),
            EngineNotLoadedException => (StatusCodes.Status503ServiceUnavailable, "Il motore non è caricato"),
            HistoryRangeNotAvailableException => (StatusCodes.Status400BadRequest, "Intervallo fuori dallo storico interrogabile"),
            // «Non è fermo», non «è in Run»: dal compito 4b della console le connessioni PLC sono
            // rifiutate anche in Pause, Recover e Dispose (PlantQueries.RequireStopped), e un titolo
            // che dicesse Run sarebbe falso proprio li'. Per le altre scritture, rifiutate in Run e dal
            // 27/9 anche in Recover e Dispose (PlantQueries.RequireNotRunning), resta vero; lo stato
            // esatto lo dice sempre il dettaglio, che e' il messaggio dell'eccezione.
            EngineBusyException => (StatusCodes.Status409Conflict, "Il motore non è fermo"),
            PlantGraphWorkPendingException => (StatusCodes.Status409Conflict, "Impossibile sostituire il grafo con lavoro pendente"),
            PlcConnectionInUseException => (StatusCodes.Status409Conflict, "Connessione PLC ancora usata da dispositivi"),
            // Il guasto del confine verso il gestionale. Ha preso il posto di
            // WmsUnavailableException, che era la stessa risposta per la lettura diretta su SQL
            // Server: dal compito 7 le due letture dell'Editor passano dal confine, quella classe
            // non ha più usi e non esiste più. La riga era scritta in anticipo dall'ondata 2b,
            // quando nessun endpoint poteva ancora sollevarla; adesso /wms/cells e
            // /wms/configuration-check la sollevano, e senza di lei l'Editor riceverebbe 500.
            Wms.Contracts.WarehouseUnavailableException => (StatusCodes.Status503ServiceUnavailable, MissionDestinationProblems.WarehouseUnavailableTitle),
            // A request the host cannot read (a body that is not JSON, a missing body): the caller's
            // mistake, with the status the framework chose (400, 413, 415...). Minimal APIs answer it
            // themselves in Production, but throw it in Development (RouteHandlerOptions.ThrowOnBadRequest),
            // the environment of the quick start, and without this line it became 500.
            BadHttpRequestException badRequest => (badRequest.StatusCode, "Richiesta non leggibile"),
            _ => (0, string.Empty)
        };
        if (status == 0) return false;

        context.Response.StatusCode = status;
        var problem = new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = { Status = status, Title = title, Detail = exception.Message }
        };
        // Le connessioni e i dispositivi anche come dati, non solo nel dettaglio: la pagina dell'Editor
        // segna le righe senza interpretare le parole.
        if (exception is PlcConnectionInUseException inUse)
            problem.ProblemDetails.Extensions[PlcConnectionProblems.InUseExtension] = inUse.InUse
                .Select(connection => new PlcConnectionInUseDto(connection.ConnectionId,
                    [.. connection.Devices.Select(device => new PlcConnectionUserDto(device.ItemId, device.Name))]))
                .ToList();
        // Quale dei due 503 e' (voce 34): la pagina lo legge da qui, non dal titolo. Il 503 del database
        // del motore assente non lo porta.
        if (UnavailableReason(exception) is { } reason)
            problem.ProblemDetails.Extensions[ServiceUnavailableProblems.ReasonExtension] = reason;
        return await problemDetails.TryWriteAsync(problem);
    }

    private static string? UnavailableReason(Exception exception) => exception switch
    {
        EngineNotLoadedException => ServiceUnavailableProblems.EngineNotLoaded,
        Wms.Contracts.WarehouseUnavailableException => ServiceUnavailableProblems.WarehouseUnreachable,
        _ => null
    };
}
