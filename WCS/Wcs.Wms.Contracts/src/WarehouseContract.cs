namespace Wcs.Wms.Contracts;

/// <summary>
/// The version of the WCS -> WMS contract: the routes of <see cref="WarehouseRoutes"/>, the bodies of the
/// <c>Dto</c> folder and the way <c>HttpWarehouse</c> puts them on the wire. It is not sent on the wire:
/// it is the <c>info.version</c> of the machine-readable definition, <c>guide/contracts/wms-contract.openapi.json</c>,
/// which is generated from this code and checked by <c>tools/Wcs.ContractCheck</c>.
/// </summary>
/// <remarks>
/// Semantic versioning, seen from the WMS that implements the contract: a change an existing WMS
/// would not survive (a new route it must serve, a request field removed, a response field the WCS now
/// requires, a different casing...) raises the major part, and the check fails until it does; anything
/// else raises the minor part.
/// </remarks>
public static class WarehouseContract
{
    public const string Version = "1.0.0";
}
