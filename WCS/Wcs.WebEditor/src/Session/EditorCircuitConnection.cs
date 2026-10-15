using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Wcs.WebEditor.Session;

/// <summary>
/// Queue item 14: hears the connection of a session's circuit come back after a drop, and says so on
/// the session (<see cref="EditorSessionState.ConnectionCameBack"/>). One per circuit: it is
/// registered Scoped, as the session it speaks to (<see cref="WebEditorExtensions.AddWebEditor"/>).
/// </summary>
/// <remarks>
/// Blazor calls <see cref="OnConnectionUpAsync"/> at the circuit's first connection too: only an up
/// after a down is a connection that came back.
/// </remarks>
public sealed class EditorCircuitConnection(EditorSessionState session) : CircuitHandler
{
    private int _down;

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _down, 1);
        return Task.CompletedTask;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _down, 0) == 1) session.SayTheConnectionCameBack();
        return Task.CompletedTask;
    }
}
