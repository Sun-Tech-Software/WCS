namespace Wcs.Api.Contracts.Dtos;

/// <summary>
/// Il campo che dice quale dei due <c>503</c> dell'host e' arrivato (voce 34, decisione del
/// proprietario dell'1/10): il motore non caricato o il gestionale non raggiungibile. Le pagine
/// leggono questo, non il titolo: il titolo e' prosa italiana dell'host, e lo stesso <c>503</c> arriva
/// su una rotta anche con altri titoli. I titoli e i codici restano quelli di prima.
/// </summary>
/// <remarks>
/// C'e' solo su questi due <c>503</c>. Gli altri - il database del motore che non c'e', il motore in
/// Recover di uno spostamento (<see cref="MissionMoveProblems.EngineRecoveringTitle"/>), il comando
/// che richiede Run - non lo portano.
/// </remarks>
public static class ServiceUnavailableProblems
{
    /// <summary>Il nome del campo; il valore e' <see cref="EngineNotLoaded"/> o <see cref="WarehouseUnreachable"/>.</summary>
    public const string ReasonExtension = "unavailable";

    /// <summary>
    /// Il motore non e' caricato. Lo dice il filtro delle rotte del motore (<c>Program.cs</c>, voce
    /// D145), che rifiuta la richiesta prima che tocchi il motore - e allora non e' stato scritto
    /// niente -, ma anche la sostituzione dell'impianto (<c>PlantQueries</c>, il <c>PUT</c> del grafo)
    /// quando il grafo e' gia' stato scritto e poi non si ricarica: li' qualcosa e' stato scritto. Il
    /// campo dice quale dei due <c>503</c> e', non se la richiesta abbia scritto: lo dice la rotta. Il
    /// motivo del caricamento fallito e' nel dettaglio e in <c>/status</c>.
    /// </summary>
    public const string EngineNotLoaded = "engineNotLoaded";

    /// <summary>
    /// Il gestionale non risponde, o il confine verso di lui non e' configurato: il <c>503</c> con il
    /// titolo <see cref="MissionDestinationProblems.WarehouseUnavailableTitle"/>.
    /// </summary>
    public const string WarehouseUnreachable = "warehouseUnreachable";
}
