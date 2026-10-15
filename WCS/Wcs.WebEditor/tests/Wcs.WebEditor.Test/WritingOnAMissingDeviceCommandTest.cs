using System.Net;
using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Editing;
using Wcs.WebEditor.Model;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// Ogni comando che scrive su un dispositivo, messo davanti a un dispositivo che <b>non c'e' piu'</b>:
/// deve fallire, e non deve lasciare niente a meta'.
/// </summary>
/// <remarks>
/// Fino al lotto del 404 questi comandi si comportavano in tre modi diversi davanti alla stessa
/// situazione, perche' tre modi diversi li offriva l'host: la modifica della configurazione
/// <b>ricreava</b> il dispositivo (la rotta era un upsert), il collegamento della cella lo ricreava a
/// sua volta - scrive la configurazione per prima - e ci appendeva una riga di mappa porte, e lo
/// scioglimento rispondeva <c>204</c> su un dispositivo inesistente. Qui si prova la regola che li
/// ha sostituiti, comando per comando, contro il gestore finto che imita il contratto nuovo.
/// <para>
/// Ogni prova guarda anche <c>handler.Configs</c> e <c>handler.DoorMappings</c>: un'eccezione
/// sollevata da un comando che avesse comunque scritto sarebbe il difetto di prima con una faccia
/// nuova, e un'assert sul solo tipo dell'eccezione non lo vedrebbe.
/// </para>
/// </remarks>
public sealed class WritingOnAMissingDeviceCommandTest
{
    private static WcsClient NewClient(PlantMutationHandler handler) =>
        new(new WcsClientOptions { BaseAddress = new Uri("http://localhost:5580") }, handler);

    private static PlantItemConfigDto Config(Guid id, decimal linkedCellId = 0m, string name = "sparito") => new()
    {
        ItemId = id,
        Name = name,
        DeviceType = (short)DeviceTypes.Automation,
        LinkedCellId = linkedCellId,
    };

    private static async Task<HttpStatusCode> StatusOfAsync(Func<Task> gesture)
    {
        var refused = await Assert.ThrowsAsync<WcsApiException>(gesture);
        return refused.StatusCode;
    }

    /// <summary>La modifica della configurazione: <c>404</c>, e il dispositivo <b>non</b> rinasce.</summary>
    [Fact]
    public async Task EditingTheConfigurationOfAMissingDeviceIsRefusedAndDoesNotBringItBack()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var id = Guid.NewGuid();
        var graph = new PlantGraph();
        var command = new EditItemConfigCommand(Config(id), Config(id, name: "cambiato"));

        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(() => command.ApplyAsync(client, graph)));

        Assert.Empty(handler.Configs);
        Assert.Empty(graph.Items);
    }

    /// <summary>
    /// Il collegamento della cella: <c>404</c> sulla <b>prima</b> scrittura, quindi nessuna riga di
    /// mappa porte - che era il difetto misurato che ha aperto il lotto.
    /// </summary>
    /// <remarks>
    /// L'ordine dei comandi delle celle (configurazione, poi mappa porte) non e' stato scelto per
    /// questo, ma e' quello che qui paga: il rifiuto arriva a mani vuote. La prova lo verifica
    /// contando le due rotte separatamente, non solo guardando la tabella finale - una mappa porte
    /// scritta e poi ripulita da qualcosa lascerebbe la tabella vuota lo stesso.
    /// </remarks>
    [Fact]
    public async Task LinkingACellToAMissingDeviceIsRefusedAndLeavesNoOrphanRow()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var id = Guid.NewGuid();
        var graph = new PlantGraph();
        var command = new LinkBufferCellCommand(Config(id), Config(id, 77m), [], new DoorMappingLink(id, "PORTA-A", 77m));

        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(() => command.ApplyAsync(client, graph)));

        Assert.Equal(1, handler.ConfigWrites);      // la PUT e' partita
        Assert.Equal(0, handler.DoorMappingLinks);  // e la POST della cella no: il rifiuto e' arrivato prima
        Assert.Empty(handler.DoorMappings);
        Assert.Empty(handler.Configs);
    }

    /// <summary>Lo scioglimento: <c>404</c> anche lui, invece del <c>204</c> di prima su un dispositivo che non c'era.</summary>
    [Fact]
    public async Task UnlinkingACellFromAMissingDeviceIsRefused()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var id = Guid.NewGuid();
        var graph = new PlantGraph();
        var command = new UnlinkBufferCellCommand(Config(id, 77m), Config(id), []);

        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(() => command.ApplyAsync(client, graph)));

        Assert.Equal(0, handler.DoorMappingDeletes);
        Assert.Empty(handler.Configs);
    }

    /// <summary>
    /// L'<b>annulla</b> dei comandi delle celle riscrive la configurazione di prima: se il dispositivo
    /// e' sparito nel frattempo, fallisce anche lui. Dichiarato, non nascosto.
    /// </summary>
    /// <remarks>
    /// E' la conseguenza che il brief del lotto chiedeva di non tacere. Lo scenario e' vero: si
    /// collega una cella, qualcun altro cancella il dispositivo, si preme annulla. Il rimedio non e'
    /// lasciar passare l'annulla - riscrivere la configurazione ricreerebbe il dispositivo, che e'
    /// esattamente cio' che il lotto toglie - ma sapere che non passa.
    /// </remarks>
    [Fact]
    public async Task UndoingACellGestureOnADeviceDeletedMeanwhileIsRefusedToo()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var id = Guid.NewGuid();
        handler.SeedItems(id);
        var graph = new PlantGraph();
        var command = new LinkBufferCellCommand(Config(id), Config(id, 77m), [], new DoorMappingLink(id, "PORTA-A", 77m));
        await command.ApplyAsync(client, graph);
        Assert.Single(handler.DoorMappings);

        // Qualcun altro cancella il dispositivo fra il collegamento e l'annulla.
        handler.Configs.Remove(id);

        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(() => command.UndoAsync(client, graph)));

        // La riga della mappa porte resta quella scritta dal collegamento: l'annulla non ne ha
        // toccata nessuna, ne' ha ricreato il dispositivo.
        Assert.Single(handler.DoorMappings);
        Assert.Empty(handler.Configs);
    }

    /// <summary>
    /// L'<b>annulla di una cancellazione</b> quando qualcun altro ha rimesso quel dispositivo: la
    /// creazione risponde <c>409</c>, e l'annulla si ferma senza sovrascrivere la configurazione che
    /// c'e' adesso ne' rimettere le giunzioni di prima.
    /// </summary>
    /// <remarks>
    /// Le tre cose che questa prova tiene ferme insieme, e nessuna delle tre regge da sola: il
    /// messaggio (che l'operatore legge), la configurazione dell'host <b>intatta</b> (sovrascriverla
    /// sarebbe la perdita silenziosa che il 404 e' venuto a chiudere), e nessuna giunzione ricreata
    /// (rimetterne di vecchie su un impianto che nel frattempo e' cambiato sarebbe peggio del non
    /// fare niente).
    /// </remarks>
    [Fact]
    public async Task UndoingADeletionWhenTheDeviceCameBackStopsWithoutOverwritingIt()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();
        var mine = Config(id, name: "il mio");
        var geometry = new PlantItemGeometryDto(id, 10, 20, 30, 40, 0);
        var junction = new JunctionDto(Guid.NewGuid(), id, other, 1);
        var command = new DeleteItemCommand(mine, geometry, [junction]);

        // Qualcun altro ha rimesso QUEL dispositivo (per esempio un'importazione dell'impianto
        // intero), con una configurazione sua.
        handler.Configs[id] = Config(id, name: "quello di un altro");
        var graph = new PlantGraph();

        var stopped = await Assert.ThrowsAsync<InvalidOperationException>(() => command.UndoAsync(client, graph));

        Assert.Contains(id.ToString(), stopped.Message, StringComparison.Ordinal);
        Assert.Contains("e' tornato sull'impianto", stopped.Message, StringComparison.Ordinal);
        Assert.Contains("1 giunzione", stopped.Message, StringComparison.Ordinal);
        // E non l'altra causa: il motore e' fermo, dirlo in Run manderebbe a fermare quello che e'
        // gia' fermo invece che a guardare l'impianto.
        Assert.DoesNotContain("motore", stopped.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Conflict, Assert.IsType<WcsApiException>(stopped.InnerException).StatusCode);

        Assert.Equal("quello di un altro", handler.Configs[id].Name); // non sovrascritta
        Assert.Equal(0, handler.ConfigWrites);                        // nemmeno tentata con la PUT
        Assert.Equal(0, handler.GeometryWrites);
        Assert.Equal(0, handler.JunctionAdds);
        Assert.Empty(graph.Junctions);
    }

    /// <summary>
    /// Lo stesso <c>409</c>, l'<b>altra</b> causa: il motore e' stato avviato fra la cancellazione e
    /// l'annulla, e il dispositivo non e' tornato per niente. Il messaggio deve dire di fermare il
    /// motore, non che il dispositivo e' tornato e che restano giunzioni da rimettere a mano.
    /// </summary>
    /// <remarks>
    /// <c>POST /plant/items</c> risponde <c>409</c> anche a motore in marcia
    /// (<c>PlantQueries.RequireNotRunning</c> prima del controllo di esistenza; lo prova
    /// <c>Wcs.Host.Test/WritingOnAMissingDeviceTest.ARunningEngineIsStill409AndNot404</c>). Trattare
    /// ogni <c>409</c> come «il dispositivo e' tornato» e' la frase che manda l'operatore a rifare a
    /// mano un lavoro che nessuno ha perso: qui il dispositivo <b>non c'e'</b> sull'host finto, e la
    /// prova lo verifica invece di fidarsi del solo testo.
    /// <para>
    /// Le due leve sono separate apposta: <see cref="PlantMutationHandler.FailNext"/> fa arrivare il
    /// <c>409</c>, <see cref="PlantMutationHandler.NavStatus"/> dice che cosa risponde
    /// <c>GET /status</c>. Chi imita qui dentro anche la guardia del Run proverebbe l'host finto, non
    /// il comando.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task UndoingADeletionWithTheEngineRunningSaysToStopItAndNotThatTheDeviceCameBack()
    {
        var handler = new PlantMutationHandler();
        var client = NewClient(handler);
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();
        var junction = new JunctionDto(Guid.NewGuid(), id, other, 1);
        var command = new DeleteItemCommand(Config(id, name: "il mio"), new PlantItemGeometryDto(id, 10, 20, 30, 40, 0), [junction]);

        handler.NavStatus = NavigatorStatusTypes.Run;
        handler.FailNext(PlantMutationHandler.Route.ItemPost, HttpStatusCode.Conflict);
        var graph = new PlantGraph();

        var stopped = await Assert.ThrowsAsync<InvalidOperationException>(() => command.UndoAsync(client, graph));

        Assert.Contains("Il motore e' in Run", stopped.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("tornato sull'impianto", stopped.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("a mano", stopped.Message, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, Assert.IsType<WcsApiException>(stopped.InnerException).StatusCode);

        // Il dispositivo non e' tornato per davvero: la premessa della frase scartata e' falsa, e
        // questa riga e' quello che lo dice - non il testo del messaggio.
        Assert.Empty(handler.Configs);
        Assert.Equal(1, handler.StatusGets);
        Assert.Equal(0, handler.ConfigWrites);
        Assert.Equal(0, handler.GeometryWrites);
        Assert.Equal(0, handler.JunctionAdds);
        Assert.Empty(graph.Junctions);
    }
}
