namespace Wcs.Api.Contracts;

/// <summary>
/// The version of the host API contract: the operations under <see cref="ApiRoutes.Prefix"/>, their
/// bodies and their error answers. It is not sent on the wire: it is the <c>info.version</c> of the
/// machine-readable definition, <c>guide/contracts/wcs-host.openapi.json</c>, which is generated from the
/// running host and checked by <c>tools/Wcs.ContractCheck</c>.
/// </summary>
/// <remarks>
/// Semantic versioning, seen from a client of the API: a change an existing client would not survive
/// (a route removed, a required request field added, a response field removed, a type changed...)
/// raises the major part, and the check fails until it does; anything else raises the minor part.
/// The major part is independent of the <c>v1</c> in <see cref="ApiRoutes.Prefix"/>.
/// </remarks>
public static class HostApiContract
{
    public const string Version = "1.0.0";
}
