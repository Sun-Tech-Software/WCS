using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Engine.Api;
using Wcs.Wms.Contracts;
using Xunit;

namespace Wcs.Host.Test;

/// <summary>
/// Il gestore traduce anche il guasto del confine HTTP.
/// </summary>
/// <remarks>
/// <c>WarehouseUnavailableException</c> e' il guasto della chiamata al gestionale, e senza la sua
/// riga nel gestore finirebbe in quello predefinito con un <c>500</c>. Quando l'ondata 2b la
/// scrisse era latente - nessun endpoint poteva sollevarla - e il gestore si esercitava
/// direttamente proprio per questo; dal compito 7 le due rotte dell'Editor sotto <c>/wms</c> la
/// sollevano davvero, e <see cref="WmsEndpointsTest"/> le prova dal giro HTTP. Le prove qui
/// restano perche' guardano il gestore, non una rotta: coprono anche le eccezioni che nessuna
/// rotta produce oggi.
/// </remarks>
[Collection("host")]
public sealed class EngineExceptionHandlerTest(HostFixture host)
{
    [Fact]
    public async Task TheBoundaryFailureIs503()
    {
        var (handled, status, body) = await Handle(new WarehouseUnavailableException("LocateLoadUnitAsync"));

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status);
        // Voce 34: il campo dice quale dei due 503 e'; il titolo resta quello di prima.
        Assert.Equal("warehouseUnreachable", ReasonOf(body));
        Assert.Equal("Il gestionale non è raggiungibile dal motore", TitleOf(body));
    }

    /// <summary>
    /// L'altro dei due 503 della voce 34: il motore non caricato porta il suo valore nello stesso
    /// campo, e il titolo di prima.
    /// </summary>
    [Fact]
    public async Task TheEngineNotLoadedIs503WithItsReason()
    {
        var (handled, status, body) = await Handle(new EngineNotLoadedException("il grafo non si carica"));

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status);
        Assert.Equal("engineNotLoaded", ReasonOf(body));
        Assert.Equal("Il motore non è caricato", TitleOf(body));
    }

    /// <summary>
    /// La riga accanto, che non deve cambiare: il motore senza store resta un 503. Prima al suo
    /// posto c'era <c>WmsUnavailableException</c>, il guasto della lettura diretta su SQL Server,
    /// che con il compito 7 e' rimasta senza usi ed e' stata cancellata.
    /// </summary>
    [Fact]
    public async Task TheEngineWithoutItsStoreIsStill503()
    {
        var (handled, status, body) = await Handle(new EngineStoreUnavailableException("lo store non c'e'"));

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status);
        // Non e' nessuno dei due 503 della voce 34: il campo non c'e'.
        Assert.Null(ReasonOf(body));
    }

    /// <summary>
    /// E un'eccezione qualunque resta al gestore predefinito: senza questa asserzione le due
    /// precedenti sarebbero verdi anche con un gestore che dice sempre di si'.
    /// </summary>
    [Fact]
    public async Task AnyOtherExceptionIsNotHandledHere()
    {
        var (handled, _, _) = await Handle(new InvalidOperationException("qualcosa d'altro"));

        Assert.False(handled);
    }

    private static string? ReasonOf(string body)
    {
        using var problem = JsonDocument.Parse(body);
        return problem.RootElement.TryGetProperty("unavailable", out var reason) ? reason.GetString() : null;
    }

    private static string? TitleOf(string body)
    {
        using var problem = JsonDocument.Parse(body);
        return problem.RootElement.GetProperty("title").GetString();
    }

    private async Task<(bool Handled, int Status, string Body)> Handle(Exception exception)
    {
        var handler = new EngineExceptionHandler(host.Services.GetRequiredService<IProblemDetailsService>());
        var context = new DefaultHttpContext
        {
            RequestServices = host.Services,
            Response = { Body = new MemoryStream() }
        };

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);
        context.Response.Body.Position = 0;
        return (handled, context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }
}
