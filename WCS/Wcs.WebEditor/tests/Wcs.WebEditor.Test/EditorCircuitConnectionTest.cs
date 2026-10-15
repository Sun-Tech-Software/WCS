using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wcs.WebEditor.Session;
using Xunit;

namespace Wcs.WebEditor.Test;

/// <summary>
/// Queue item 14: the circuit's handler that tells the session its connection came back. What Blazor
/// resolves from the circuit's scope is what the application registers (<see cref="WebEditorExtensions.AddWebEditor"/>).
/// </summary>
public sealed class EditorCircuitConnectionTest
{
    private static IServiceProvider Services()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Kestrel:Endpoints:Http:Url"] = "http://0.0.0.0:5580" })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWebEditor(configuration);
        return services.BuildServiceProvider();
    }

    /// <summary>Each circuit's handler speaks to the session of the same circuit, and to no other.</summary>
    [Fact]
    public async Task TheCircuitsHandlerTellsItsOwnSessionThatTheConnectionCameBack()
    {
        var provider = Services();
        using var circuit = provider.CreateScope();
        using var otherCircuit = provider.CreateScope();
        var session = circuit.ServiceProvider.GetRequiredService<EditorSessionState>();
        var otherSession = otherCircuit.ServiceProvider.GetRequiredService<EditorSessionState>();
        var cameBack = 0;
        var otherCameBack = 0;
        session.ConnectionCameBack += () => cameBack++;
        otherSession.ConnectionCameBack += () => otherCameBack++;

        var handler = Assert.Single(circuit.ServiceProvider.GetServices<CircuitHandler>().OfType<EditorCircuitConnection>());
        await handler.OnConnectionDownAsync(null!, CancellationToken.None);
        await handler.OnConnectionUpAsync(null!, CancellationToken.None);

        Assert.Equal(1, cameBack);
        Assert.Equal(0, otherCameBack);
    }

    /// <summary>Blazor calls up at the circuit's first connection too: only an up after a down is a connection that came back, once.</summary>
    [Fact]
    public async Task OnlyAnUpAfterADownIsAConnectionThatCameBack()
    {
        var session = new EditorSessionState(new Uri("http://localhost:5580"));
        var cameBack = 0;
        session.ConnectionCameBack += () => cameBack++;
        var handler = new EditorCircuitConnection(session);

        await handler.OnConnectionUpAsync(null!, CancellationToken.None);
        Assert.Equal(0, cameBack);

        await handler.OnConnectionDownAsync(null!, CancellationToken.None);
        await handler.OnConnectionUpAsync(null!, CancellationToken.None);
        await handler.OnConnectionUpAsync(null!, CancellationToken.None);
        Assert.Equal(1, cameBack);
    }
}
