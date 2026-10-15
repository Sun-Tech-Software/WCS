using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.Engine;
using Wcs.Engine.Api;
using Wcs.Host.Mapping;
using Wcs.Wms.Contracts;

namespace Wcs.Host.Endpoints;

/// <summary>
/// Le rotte sotto <c>/wms</c>: le due letture del gestionale che servono all'Editor (sola lettura,
/// dal confine) e la fotografia per la ricerca celle del WMS, che legge lo store del WCS e i
/// dispositivi in memoria.
/// </summary>
/// <remarks>
/// <para>
/// Le prime due erano due query in T-SQL diretto sulla connessione al WMS del motore
/// (<c>WmsQueries</c>, cancellata dal compito 7). Adesso sono due domande al confine, e l'host non
/// fa che girarle: la forma che arriva all'Editor - <see cref="WmsCell"/> e
/// <see cref="WmsConfigurationWarning"/> - non cambia, perche' l'Editor non si tocca.
/// </para>
/// <para>
/// Sono le <b>prime</b> rotte dell'host che chiedono qualcosa al gestionale. Il guasto del confine
/// arriva qui come <see cref="WarehouseUnavailableException"/> e diventa <c>503</c> in
/// <see cref="EngineExceptionHandler"/>: la riga che lo traduce c'era gia', scritta dall'ondata 2b
/// per questo giorno.
/// </para>
/// </remarks>
internal static class WmsEndpoints
{
    public static RouteGroupBuilder MapWms(this RouteGroupBuilder api)
    {
        api.MapGet(ApiRoutes.WmsCells, async (short? areaType, bool automaticOnly = false) =>
            (await Warehouse(nameof(IWarehouse.GetCellsForEditorAsync))
                .GetCellsForEditorAsync(areaType, automaticOnly)).Select(DtoMapping.ToDto).ToList()).WithName("ListWmsCells");
        api.MapGet(ApiRoutes.WmsConfigurationCheck, async (int mode = 0) =>
            (await Warehouse(nameof(IWarehouse.GetConfigurationWarningsAsync))
                .GetConfigurationWarningsAsync(mode)).Select(w => new WmsConfigurationWarning(w.Columns)).ToList()).WithName("WmsConfigurationCheck");
        api.MapGet(ApiRoutes.WmsCellSearchContext, async () => DtoMapping.ToDto(await CellSearchQueries.ContextAsync())).WithName("GetCellSearchContext");
        return api;
    }

    /// <summary>
    /// Il confine verso il gestionale, o il rifiuto onesto.
    /// </summary>
    /// <remarks>
    /// Non <c>Navigator.RequireWarehouse</c>, che e' il punto comune dei chiamanti <b>dentro</b> il
    /// motore: quello solleva <see cref="InvalidOperationException"/>, perche' per il motore un
    /// confine non impostato e' un errore di cablaggio di chi lo ospita, e l'host lo renderebbe un
    /// <c>500</c>. Per chi apre una finestra dell'Editor, invece, un WCS senza gestionale e un
    /// gestionale che non risponde sono la stessa cosa - il dato non si puo' leggere - e la regola
    /// del guasto del confine dice <c>503</c>. E' la stessa risposta che dava
    /// <c>WmsUnavailableException</c> quando la connessione al WMS mancava.
    /// </remarks>
    private static IWarehouse Warehouse(string operation) =>
        Navigator.Warehouse ?? throw new WarehouseUnavailableException(operation);
}
