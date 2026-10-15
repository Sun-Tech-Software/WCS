using Wcs.Api.Contracts;
using Wcs.Host.Mapping;

namespace Wcs.Host.Endpoints;

internal static class StatusEndpoints
{
    public static RouteGroupBuilder MapStatus(this RouteGroupBuilder api)
    {
        api.MapGet(ApiRoutes.Status, () => DtoMapping.Status()).WithName("GetStatus");
        return api;
    }
}
