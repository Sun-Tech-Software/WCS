using System.Reflection;
using Wcs.Engine.Items;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Voce D49: <c>await x?.M()</c> si legge come una protezione e non lo e'. Quando <c>x</c> e'
/// nullo la catena si interrompe, l'espressione vale un <c>Task</c> nullo, e l'<c>await</c> su un
/// <c>Task</c> nullo solleva <c>NullReferenceException</c>: proprio l'eccezione che la forma
/// sembra evitare.
/// </summary>
/// <remarks>
/// L'ultimo punto con quella forma raggiungibile con un valore nullo e'
/// <c>Navigator.BuildRouteToFixedDestination</c>, che accetta una <c>PathMission?</c>: i due
/// chiamanti di oggi la guardano prima, quindi qui la si chiama per riflessione, con il
/// dispositivo senza destinazioni fisse cosi' che i rami che gia' guardano <c>value</c> si
/// saltino e si arrivi all'<c>await</c>. Gli altri due punti, in <c>ExecutableCommands.ItemStepDone</c>,
/// iterano <c>BelongedItems</c>, che non contiene mai un nullo: la stessa forma li' non e'
/// raggiungibile, e viene riscritta per non mentire.
/// </remarks>
public sealed class AwaitOnNullTargetTest
{
    [Fact]
    public async Task BuildingTheRouteToAFixedDestinationWithoutAMissionDoesNothing()
    {
        var method = typeof(Navigator).GetMethod("BuildRouteToFixedDestination",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        // Disposto alla fine: il costruttore iscrive il dispositivo a Navigator.StatusChanged e
        // solo Dispose lo disiscrive; senza, resterebbe un iscritto zombie nella suite (D144).
        using var device = new AutomationDevice(Guid.NewGuid()) { DeviceType = DeviceTypes.Automation };

        var task = (Task?)method!.Invoke(null, [null, device]);
        Assert.NotNull(task);

        // Senza missione non c'e' niente da costruire: si esce, non si solleva.
        await task!;
    }
}
