using Microsoft.Extensions.Options;

namespace Wcs.SampleWms;

/// <summary>
/// Builds the sample WMS: configuration, the in-memory warehouse, the contract routes and the admin
/// routes. <c>Program.cs</c> runs it; the tests start it on a free port with <see cref="Create"/>.
/// </summary>
public static class SampleWmsApp
{
    public static WebApplication Create(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddOptions<SampleWmsOptions>()
            .Bind(builder.Configuration.GetSection(SampleWmsOptions.Section))
            .Validate(o => !string.IsNullOrWhiteSpace(o.ApiKey), "SampleWms:ApiKey is empty: the WCS could not authenticate.")
            .Validate(o => o.ReservationLifetimeMinutes > 0, "SampleWms:ReservationLifetimeMinutes must be at least 1.")
            .ValidateOnStart();

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(sp => new SampleWarehouse(
            sp.GetRequiredService<TimeProvider>(),
            TimeSpan.FromMinutes(sp.GetRequiredService<IOptions<SampleWmsOptions>>().Value.ReservationLifetimeMinutes)));
        builder.Services.AddSingleton<Receipts>();
        builder.Services.AddSingleton<ApiKeyFilter>();
        builder.Services.AddProblemDetails();

        var app = builder.Build();

        // Every request of the WCS, with its answer, kept in the state (GET /sample-admin/state,
        // "recentCalls"): the quickest way to see the contract at work. The admin routes are left out.
        app.Use(async (context, next) =>
        {
            await next(context);
            if (context.Request.Path.StartsWithSegments(Wcs.Wms.Contracts.WarehouseRoutes.Prefix))
                context.RequestServices.GetRequiredService<SampleWarehouse>().RecordCall(
                    context.Request.Method, context.Request.Path + context.Request.QueryString, context.Response.StatusCode);
        });

        // The X-Api-Key check, before any endpoint binds its body (see ApiKeyFilter).
        app.UseApiKey();

        app.MapWarehouseContract();
        app.MapSampleAdmin();

        // A page for a person who opens the address in a browser: the contract needs the key.
        app.MapGet("/", () => Results.Text(
            "Wcs.SampleWms is running. The WCS calls /api/wcs/warehouse/... with the X-Api-Key header; " +
            "GET /sample-admin/state (same header) shows what the sample holds."));

        return app;
    }
}
