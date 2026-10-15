using System.Net;
using System.Net.Http.Json;
using Wcs.Api.Contracts;
using Wcs.Api.Contracts.Dtos;
using Wcs.Engine;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;

namespace Wcs.Host.Test;

/// <summary>
/// Le due letture dell'Editor sotto <c>/wms</c> - le celle da collegare a porte e polmoni, e il
/// controllo di configurazione - dopo il compito 7 non sono piu' due query in T-SQL diretto sulla
/// connessione al WMS del motore (<c>WmsQueries</c>, cancellata): sono due domande al confine
/// (<see cref="IWarehouse.GetCellsForEditorAsync"/> e
/// <see cref="IWarehouse.GetConfigurationWarningsAsync"/>), e l'host le gira e le riveste nella
/// forma che l'Editor gia' legge.
/// </summary>
/// <remarks>
/// <para>
/// <b>L'Editor non si tocca, ed e' quello che queste prove sorvegliano.</b> Il corpo della risposta
/// si legge due volte: una volta nei tipi che l'Editor usa (<see cref="WmsCell"/>,
/// <see cref="WmsConfigurationWarning"/>), e una volta come testo. La seconda non e' un doppione:
/// <c>ReadFromJsonAsync</c> e' insensibile alle maiuscole e accetterebbe nomi di campo diversi da
/// quelli che l'Editor vede arrivare, mentre il testo fissa proprio quelli.
/// </para>
/// <para>
/// Sono letture <b>guidate dall'Editor</b>, non del ciclo periodico del motore: partono quando si
/// apre la finestra dei collegamenti o quella del controllo, e non entrano ne' nel giro al secondo
/// di <c>MasterProcess</c> ne' nel ciclo a 10 ms.
/// </para>
/// </remarks>
[Collection("host")]
public sealed class WmsEndpointsTest : IDisposable
{
    private const string CellsUrl = ApiRoutes.Prefix + ApiRoutes.WmsCells;
    private const string CheckUrl = ApiRoutes.Prefix + ApiRoutes.WmsConfigurationCheck;

    private readonly HostFixture _host;
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly FakeWarehouse _warehouse = new();

    public WmsEndpointsTest(HostFixture host)
    {
        _host = host;
        Navigator.Warehouse = _warehouse;
    }

    public void Dispose() => Navigator.Warehouse = _previousWarehouse;

    /// <summary>
    /// La riga che il confine risponde arriva all'Editor con gli stessi otto campi di prima, e i
    /// due filtri della finestra - tipo d'area e «solo automatiche» - arrivano al confine. La
    /// seconda asserzione non e' un contorno: una rotta che perdesse i filtri risponderebbe
    /// comunque una lista plausibile, e la finestra dei collegamenti mostrerebbe celle di aree che
    /// non c'entrano.
    /// </summary>
    [Fact]
    public async Task TheCellsComeFromTheBoundaryWithTheShapeTheEditorReads()
    {
        _warehouse.EditorCells.Add(new EditorCellRow(300, "Magazzino 1", "Area A", 4, 2, "P01A", "P01B", "Cella 4-2"));

        var response = await _host.CreateAuthenticatedClient().GetAsync($"{CellsUrl}?areaType=2&automaticOnly=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cell = Assert.Single((await response.Content.ReadFromJsonAsync<List<WmsCell>>())!);
        Assert.Equal(new WmsCell(300, "Magazzino 1", "Area A", 4, 2, "P01A", "P01B", "Cella 4-2"), cell);
        Assert.Equal(((short?)2, true), Assert.Single(_warehouse.EditorCellQueries));
    }

    /// <summary>
    /// Senza filtri la domanda parte lo stesso, con «nessun tipo d'area» e «non solo automatiche»:
    /// e' la finestra dei collegamenti di stoccaggio, che chiede le aree 0 e 1 tutte quante. Il
    /// valore predefinito di <c>automaticOnly</c> sta nell'endpoint, e senza questa prova un
    /// cambiamento di quel valore passerebbe inosservato.
    /// </summary>
    [Fact]
    public async Task WithoutFiltersTheQuestionIsAskedForEveryAreaAndNotOnlyTheAutomaticOnes()
    {
        Assert.Equal(HttpStatusCode.OK, (await _host.CreateAuthenticatedClient().GetAsync(CellsUrl)).StatusCode);

        Assert.Equal(((short?)null, false), Assert.Single(_warehouse.EditorCellQueries));
    }

    /// <summary>
    /// Il controllo di configurazione: le colonne le decide la procedura del gestionale, quindi la
    /// riga viaggia come un dizionario e l'Editor ne fa le colonne della griglia. La modalita'
    /// arriva dalla query string.
    /// </summary>
    [Fact]
    public async Task TheConfigurationCheckComesFromTheBoundaryWithTheColumnsTheProcedureDecides()
    {
        _warehouse.ConfigurationWarnings.Add(new ConfigurationWarning(new Dictionary<string, string?>
        {
            ["priority"] = "3",
            ["message"] = "La cella C1 del magazzino Magazzino 1 ha una errata configurazione"
        }));

        var response = await _host.CreateAuthenticatedClient().GetAsync($"{CheckUrl}?mode=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var warning = Assert.Single((await response.Content.ReadFromJsonAsync<List<WmsConfigurationWarning>>())!);
        Assert.Equal("3", warning.Values["priority"]);
        Assert.Equal("La cella C1 del magazzino Magazzino 1 ha una errata configurazione", warning.Values["message"]);
        Assert.Equal(2, Assert.Single(_warehouse.ConfigurationChecks));
    }

    /// <summary>
    /// I nomi dei campi sul filo, che sono la promessa vera del compito: l'Editor legge questo
    /// testo e non si tocca. <c>ReadFromJsonAsync</c> delle prove qui sopra non li vedrebbe
    /// cambiare, perche' e' insensibile alle maiuscole e legge per tipo.
    /// </summary>
    [Fact]
    public async Task TheFieldNamesOnTheWireAreTheOnesTheEditorAlreadyReads()
    {
        _warehouse.EditorCells.Add(new EditorCellRow(300, "Magazzino 1", "Area A", 4, 2, "P01A", "P01B", "Cella 4-2"));
        _warehouse.ConfigurationWarnings.Add(new ConfigurationWarning(new Dictionary<string, string?> { ["message"] = "Magazzino non creato" }));
        var client = _host.CreateAuthenticatedClient();

        var cells = await client.GetStringAsync(CellsUrl);
        var check = await client.GetStringAsync(CheckUrl);

        foreach (var field in new[] { "cellId", "warehouse", "area", "column", "floor", "doorA", "doorB", "description" })
            Assert.Contains($"\"{field}\":", cells);
        // Le chiavi del dizionario sono un dato - le decide la procedura - e non si camelizzano:
        // la griglia dell'Editor ne fa i nomi delle colonne.
        Assert.Contains("\"values\":", check);
        Assert.Contains("\"message\":", check);
    }

    /// <summary>
    /// Senza confine impostato le due letture rispondono <c>503</c>, non una lista vuota che
    /// l'Editor mostrerebbe come «nessuna cella» o «configurazione a posto». E' la stessa risposta
    /// di prima - allora la dava <c>WmsUnavailableException</c> sulla connessione al WMS mancante,
    /// adesso <see cref="WarehouseUnavailableException"/> sul confine mancante - e la regola del
    /// guasto del confine dice che dev'essere questa.
    /// </summary>
    [Fact]
    public async Task WithoutTheBoundaryBothReadsAre503AsProblemDetails()
    {
        Navigator.Warehouse = null;
        var client = _host.CreateAuthenticatedClient();

        var cells = await client.GetAsync($"{CellsUrl}?areaType=2&automaticOnly=true");
        var check = await client.GetAsync($"{CheckUrl}?mode=0");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, cells.StatusCode);
        Assert.Equal("application/problem+json", cells.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, check.StatusCode);
        Assert.Equal("application/problem+json", check.Content.Headers.ContentType!.MediaType);
        // Voce 34: il campo dice che e' il gestionale, non il motore non caricato.
        Assert.Equal("warehouseUnreachable", await UnavailableReasonOf(cells));
        Assert.Equal("warehouseUnreachable", await UnavailableReasonOf(check));
    }

    /// <summary>
    /// Il gestionale che non risponde e' anche lui un <c>503</c>: e' l'altra meta' della regola -
    /// il confine c'e' ma la domanda non riceve risposta - e senza di lei la prova qui sopra
    /// starebbe in piedi anche con un endpoint che non chiede niente a nessuno.
    /// </summary>
    [Fact]
    public async Task AWarehouseThatDoesNotAnswerIs503Too()
    {
        _warehouse.Unavailable = true;
        var client = _host.CreateAuthenticatedClient();

        var cells = await client.GetAsync(CellsUrl);
        var check = await client.GetAsync(CheckUrl);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, cells.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, check.StatusCode);
        Assert.Equal("warehouseUnreachable", await UnavailableReasonOf(cells));
        Assert.Equal("warehouseUnreachable", await UnavailableReasonOf(check));
    }

    /// <summary>Il campo del problem document che dice quale dei due 503 e' (voce 34); null se manca.</summary>
    private static async Task<string?> UnavailableReasonOf(HttpResponseMessage response)
    {
        using var problem = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.TryGetProperty("unavailable", out var reason) ? reason.GetString() : null;
    }
}
