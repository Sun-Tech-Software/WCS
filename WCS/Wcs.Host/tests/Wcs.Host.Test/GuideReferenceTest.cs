using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api.Contracts;
using Wcs.Engine.Items;
using Wcs.Host.Security;
using Wcs.Wms.Contracts;
using Xunit;

namespace Wcs.Host.Test;

/// <summary>
/// The reference pages of the English guides (<c>guide/reference</c> in this repository, <c>docs/reference</c>
/// in the public copy) list every route of the host, every route of the WMS contract and every command
/// code. These tests read the pages and compare them with what the code really registers, in both
/// directions: a route added or removed without updating the page fails here, and so does a row of the
/// page that no longer exists in the code.
/// </summary>
[Collection("host")]
public sealed partial class GuideReferenceTest(HostFixture fixture)
{
    [Fact]
    public void TheHostApiReferenceListsEveryRouteAndItsEditLease()
    {
        var registered = fixture.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => (e.RoutePattern.RawText ?? "").StartsWith(ApiRoutes.Prefix + "/", StringComparison.Ordinal))
            .SelectMany(e => e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods
                .Select(method => (Key: $"{method} {Normalise(e.RoutePattern.RawText!)}",
                    Lease: e.Metadata.GetMetadata<RequiresEditPermissionMarker>() != null)))
            .ToDictionary(r => r.Key, r => r.Lease);

        var documented = HostRow().Matches(ReadReference("host-api.md"))
            .ToDictionary(m => $"{m.Groups["method"].Value} {m.Groups["path"].Value}", m => m.Groups["lease"].Value.Trim() == "yes");

        Assert.Equal(66, registered.Count);
        Assert.True(registered.Keys.Order().SequenceEqual(documented.Keys.Order()),
            "guide/reference/host-api.md and the host disagree. Not documented: " +
            string.Join(", ", registered.Keys.Except(documented.Keys)) + ". Documented but not registered: " +
            string.Join(", ", documented.Keys.Except(registered.Keys)));
        var wrongLease = registered.Where(r => documented[r.Key] != r.Value).Select(r => r.Key).ToList();
        Assert.True(wrongLease.Count == 0, "Wrong \"Lease\" column in guide/reference/host-api.md: " + string.Join(", ", wrongLease));
    }

    [Fact]
    public void TheWmsRoutesReferenceListsEveryRouteWithItsMethod()
    {
        var routes = typeof(WarehouseRoutes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.Name is not (nameof(WarehouseRoutes.Prefix) or nameof(WarehouseRoutes.ApiKeyHeader)))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet();
        var methods = typeof(IWarehouse).GetMethods().Select(m => m.Name).ToHashSet();

        var rows = WmsRow().Matches(ReadReference("wms-routes.md")).ToList();

        Assert.Equal(43, routes.Count);
        Assert.Equal(43, methods.Count);
        Assert.True(routes.SetEquals(rows.Select(r => r.Groups["path"].Value)),
            "guide/reference/wms-routes.md and WarehouseRoutes disagree. Not documented: " +
            string.Join(", ", routes.Except(rows.Select(r => r.Groups["path"].Value))));
        Assert.True(methods.SetEquals(rows.Select(r => r.Groups["member"].Value)),
            "guide/reference/wms-routes.md and IWarehouse disagree. Not documented: " +
            string.Join(", ", methods.Except(rows.Select(r => r.Groups["member"].Value))));
        Assert.Equal(43, rows.Select(r => r.Groups["number"].Value).Distinct().Count());
    }

    [Fact]
    public void TheCommandsReferenceListsEveryCode()
    {
        var codes = Enum.GetValues<Command.CommandTypes>().Select(c => $"{(int)c} {c}").ToHashSet();

        var documented = CommandRow().Matches(ReadReference("commands.md"))
            .Select(m => $"{m.Groups["code"].Value} {m.Groups["name"].Value}")
            .ToHashSet();

        Assert.True(codes.SetEquals(documented),
            "guide/reference/commands.md and Command.CommandTypes disagree. Not documented: " +
            string.Join(", ", codes.Except(documented)) + ". Documented but not in the code: " +
            string.Join(", ", documented.Except(codes)));
    }

    /// <summary><c>/api/v1/commands/</c> and <c>/api/v1/commands/{id:long}</c> as the page writes them.</summary>
    private static string Normalise(string pattern) =>
        RouteConstraint().Replace(pattern, "}").TrimEnd('/');

    /// <summary>
    /// The page, from <c>guide/reference</c> (this repository) or <c>docs/reference</c> (the public copy, where
    /// the export moves <c>guide</c> to <c>docs</c>), found from the folder of <c>Wcs.slnx</c>.
    /// </summary>
    private static string ReadReference(string page)
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder != null && !File.Exists(Path.Combine(folder.FullName, "Wcs.slnx")))
            folder = folder.Parent;
        Assert.NotNull(folder);

        foreach (var docs in new[] { "guide", "docs" })
        {
            var path = Path.Combine(folder.FullName, docs, "reference", page);
            if (File.Exists(path)) return File.ReadAllText(path);
        }

        Assert.Fail($"reference/{page} not found under guide/ or docs/ in {folder.FullName}");
        return string.Empty;
    }

    [GeneratedRegex(@"^\| (?<method>GET|POST|PUT|DELETE|PATCH) \| `(?<path>/api/v1/[^`]*)` \|(?<lease>[^|]*)\|", RegexOptions.Multiline)]
    private static partial Regex HostRow();

    [GeneratedRegex(@"^\| (?<number>\d+) \| (?:GET|POST) \| `(?<path>[^`]+)` \| `(?<member>\w+)` \|", RegexOptions.Multiline)]
    private static partial Regex WmsRow();

    [GeneratedRegex(@"^\| (?<code>\d{4}) \| `(?<name>\w+)`", RegexOptions.Multiline)]
    private static partial Regex CommandRow();

    [GeneratedRegex(@":[^}]+}")]
    private static partial Regex RouteConstraint();
}
