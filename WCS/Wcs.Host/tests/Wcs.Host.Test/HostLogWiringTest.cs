using Wcs.Engine;
using Wcs.Engine.Persistence;
using Xunit;

namespace Wcs.Host.Test;

/// <summary>
/// I registri che l'host collega al motore all'avvio: gli errori che devono lasciare un segno
/// anche con la diagnostica spenta (<c>Navigator.HostErrorLog</c>) e le due ritenzioni per file,
/// quella dello storico (<c>WcsHistoryDatabase.Notice</c>) e quella del registro dei messaggi dei
/// PLC (<c>WcsPlcLogDatabase.Notice</c>, voce <c>D183</c>): la cancellazione di un file mensile e'
/// un dato che se ne va.
/// Senza gancio finirebbero sull'errore standard di un servizio Windows, cioe' da nessuna parte.
/// </summary>
/// <remarks>
/// Le prime due asserzioni vengono da <c>InProcessAutomationTest</c>, cancellato con il taglio dei
/// riferimenti del motore ai progetti del gestionale: provavano il cablaggio dell'host, non l'area
/// in processo, e valgono anche senza quella classe; la terza e' del gemello nato con <c>D183</c>.
/// <c>EngineHostedService</c> collega questi registri prima della guardia su <c>StartEngine</c>,
/// quindi si vedono anche qui, dove l'host in memoria gira con il motore spento
/// (<c>Wcs:StartEngine</c> falso).
/// </remarks>
[Collection("host")]
public sealed class HostLogWiringTest(HostFixture host)
{
    [Fact]
    public void TheHostConnectsItsLogsToTheEngineAtStart()
    {
        // L'host in memoria parte al primo client.
        host.CreateAuthenticatedClient();

        Assert.NotNull(Navigator.HostErrorLog);
        Assert.NotNull(WcsHistoryDatabase.Notice);
        Assert.NotNull(WcsPlcLogDatabase.Notice);
    }
}
