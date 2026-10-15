namespace Wcs.Wms.Contracts;

/// <summary>
/// L'identificativo risolto da un codice (compito 10, ex <c>DbInfo.GetLuId</c>). Un tipo a parte,
/// non un <c>decimal?</c> nudo, perche' la risposta HTTP deve essere un oggetto: <c>decimal?</c> non
/// soddisfa il vincolo <c>class</c> di <c>HttpWarehouse.GetOrNullAsync</c>.
/// </summary>
public sealed record LoadUnitIdentity(decimal LoadUnitId);
