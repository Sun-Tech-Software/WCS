using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Wcs.Wms.Contracts;

namespace Wcs.SampleWms;

/// <summary>The sample's settings, section <c>SampleWms</c> of the configuration.</summary>
public sealed class SampleWmsOptions
{
    public const string Section = "SampleWms";

    /// <summary>The key the WCS must send in <c>X-Api-Key</c> (the WCS host's <c>Wms:ApiKey</c>).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>How long a storage reservation lives without being asked again.</summary>
    public int ReservationLifetimeMinutes { get; set; } = 10;
}

/// <summary>
/// Every route of the contract (and of the sample's admin routes) requires the shared key in the
/// <see cref="WarehouseRoutes.ApiKeyHeader"/> header; anything else is 401.
/// </summary>
/// <remarks>
/// <para>
/// <b>A middleware, not an endpoint filter.</b> An endpoint filter runs after the body is bound, so a
/// request without the key and with a body that does not read would get 400 instead of 401. Here the
/// key is checked before the request reaches its endpoint: no key, no binding, always 401.
/// </para>
/// <para>
/// <b>The comparison does not reveal the key.</b> Both keys are hashed with SHA-256 and the two hashes
/// are compared with <see cref="CryptographicOperations.FixedTimeEquals"/>: the hashes always have the
/// same length, so the time taken does not depend on how many characters of the key sent are right,
/// nor on its length (comparing the keys themselves would return at once when the lengths differ).
/// </para>
/// </remarks>
public sealed class ApiKeyFilter(IOptions<SampleWmsOptions> options) : IMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var sent = context.Request.Headers[WarehouseRoutes.ApiKeyHeader].ToString();
        var expected = options.Value.ApiKey;

        if (expected.Length == 0 ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(sent)), SHA256.HashData(Encoding.UTF8.GetBytes(expected))))
        {
            await Results.Problem($"Missing or wrong {WarehouseRoutes.ApiKeyHeader} header.", statusCode: StatusCodes.Status401Unauthorized)
                .ExecuteAsync(context);
            return;
        }

        await next(context);
    }
}

public static class ApiKeyFilterExtensions
{
    /// <summary>
    /// The key check on every request under <see cref="WarehouseRoutes.Prefix"/> and
    /// <see cref="AdminEndpoints.Prefix"/>, before routing reaches the endpoint. Other paths (the page
    /// at <c>/</c>) need no key.
    /// </summary>
    public static IApplicationBuilder UseApiKey(this IApplicationBuilder app) =>
        app.UseWhen(
            context => context.Request.Path.StartsWithSegments(WarehouseRoutes.Prefix) ||
                       context.Request.Path.StartsWithSegments(AdminEndpoints.Prefix),
            branch => branch.UseMiddleware<ApiKeyFilter>());
}
