using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Checks;
using Wcs.WebEditor.Model;
using Wcs.WebEditor.Session;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// Il cablaggio del compito 10 dentro <c>ChecksPanel</c>: due pulsanti, due verifiche indipendenti.
/// "Check Junctions" e' locale (<c>JunctionChecksTest</c> prova la logica pura); "Check Configuration"
/// parla con l'host, come <c>btnCheckConfig_ItemClick</c> nell'Editor WinForms.
/// </summary>
public sealed class ChecksTest : PlantCanvasBunitContext
{
    private static PlantItemDto Item(Guid id, string? name = null, double? x = 0, double? y = 0) => new(
        id, name, DeviceTypes.Automation, null, null, null, default, default, 0, null, 0, 0, 0, 0,
        default!, X: x, Y: y, Width: 100, Height: 100, Rotation: 0);

    private (IRenderedComponent<ChecksPanel> Cut, WmsConfigurationCheckHandler Handler) RenderPanel(PlantGraph graph)
    {
        var handler = new WmsConfigurationCheckHandler();
        var state = new EditorSessionState(new Uri("http://localhost:5580"), handler);
        state.UseKey("chiave-di-prova");
        Services.AddSingleton(state);

        var cut = Render<ChecksPanel>(parameters => parameters.Add(p => p.Graph, graph));
        return (cut, handler);
    }

    [Fact]
    public void NothingIsShownBeforeEitherButtonIsPressed()
    {
        var (cut, _) = RenderPanel(new PlantGraph([], [], []));

        // Nessuno zero che finga un dato: prima di premere, non c'e' ne' "nessun problema" ne' una
        // tabella vuota - non c'e' proprio nessun risultato, perche' nessuna verifica e' ancora stata
        // eseguita.
        Assert.Empty(cut.FindAll("#junction-check-result"));
        Assert.Empty(cut.FindAll("#config-check-result"));
        Assert.Empty(cut.FindAll("#config-check-error"));
    }

    [Fact]
    public void CheckJunctionsShowsTheWarningsComputedFromTheGraph()
    {
        var a = Item(Guid.NewGuid());
        var missingDestination = Guid.NewGuid();
        var graph = new PlantGraph([a], [new JunctionDto(Guid.NewGuid(), a.ItemId, missingDestination, 0)], []);
        var (cut, _) = RenderPanel(graph);

        cut.Find("#check-junctions-button").Click();

        var items = cut.FindAll("#junction-check-result li");
        Assert.Single(items);
        Assert.Contains(missingDestination.ToString(), items[0].TextContent);
    }

    [Fact]
    public void CheckJunctionsOnAWellFormedGraphShowsNoProblemsNotAnEmptyPanel()
    {
        var a = Item(Guid.NewGuid());
        var b = Item(Guid.NewGuid());
        var graph = new PlantGraph([a, b], [new JunctionDto(Guid.NewGuid(), a.ItemId, b.ItemId, 0)], []);
        var (cut, _) = RenderPanel(graph);

        cut.Find("#check-junctions-button").Click();

        // Zero righe, ma il risultato c'e' - un contenitore vuoto in silenzio sarebbe indistinguibile
        // da "non ancora premuto", esattamente lo zero che finge un dato che la regola vieta.
        var result = cut.Find("#junction-check-result");
        Assert.Empty(result.QuerySelectorAll("li"));
        Assert.Contains("No problem found", result.TextContent);
    }

    [Fact]
    public async Task CheckConfigurationOnSuccessWithNoWarningsShowsTheSuccessMessage()
    {
        var (cut, handler) = RenderPanel(new PlantGraph([], [], []));
        handler.Warnings = [];

        await cut.Find("#check-config-button").ClickAsync(new MouseEventArgs());

        Assert.Equal(1, handler.Requests);
        Assert.Contains("checked successfully", cut.Find("#config-check-result").TextContent);
    }

    /// <summary>
    /// Il gestore finto risponde a qualunque rotta: da solo non basta a provare che "Check
    /// Configurazione" chiami davvero <c>/wms/configuration-check?mode=0</c>, lo stesso <c>mode</c>
    /// che l'Editor di oggi passa (<c>FrmEDITOR.Private.cs:42</c>) - non, per esempio,
    /// <c>/wms/cells</c>.
    /// </summary>
    [Fact]
    public async Task CheckConfigurationCallsTheConfigurationCheckRouteWithModeZero()
    {
        var (cut, handler) = RenderPanel(new PlantGraph([], [], []));

        await cut.Find("#check-config-button").ClickAsync(new MouseEventArgs());

        Assert.Equal("/api/v1/wms/configuration-check?mode=0", handler.LastRequest!.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task CheckConfigurationOnSuccessWithWarningsShowsATableWithTheProcedureColumns()
    {
        var (cut, handler) = RenderPanel(new PlantGraph([], [], []));
        handler.Warnings =
        [
            new WmsConfigurationWarning(new Dictionary<string, string?> { ["DESC_MAGAZZINO"] = "MAG1", ["DESCRIZIONE"] = "cella orfana" }),
        ];

        await cut.Find("#check-config-button").ClickAsync(new MouseEventArgs());

        var table = cut.Find("#config-check-table");
        Assert.Contains("DESC_MAGAZZINO", table.TextContent);
        Assert.Contains("cella orfana", table.TextContent);
        Assert.Empty(cut.FindAll("#config-check-error"));
    }

    /// <summary>
    /// m6 della revisione: se la prima riga avesse il dizionario vuoto, prendere le colonne solo da
    /// lei renderebbe una tabella con righe ma senza celle - segnalazioni presenti, schermo che sembra
    /// dire che non c'e' niente. Le colonne vengono dall'unione delle chiavi di tutte le righe.
    /// </summary>
    [Fact]
    public async Task CheckConfigurationWithAnEmptyFirstRowStillShowsTheColumnsFromLaterRows()
    {
        var (cut, handler) = RenderPanel(new PlantGraph([], [], []));
        handler.Warnings =
        [
            new WmsConfigurationWarning(new Dictionary<string, string?>()),
            new WmsConfigurationWarning(new Dictionary<string, string?> { ["DESCRIZIONE"] = "cella orfana" }),
        ];

        await cut.Find("#check-config-button").ClickAsync(new MouseEventArgs());

        var table = cut.Find("#config-check-table");
        Assert.Contains("DESCRIZIONE", table.TextContent);
        Assert.Contains("cella orfana", table.TextContent);
    }

    /// <summary>
    /// Voce 37 della coda, estesa a tutta la pagina il 10/10: il fallimento della verifica e' una
    /// frase sola con il perche' nelle parole della pagina - qui il 503 del motore non caricato, letto
    /// dal campo del problem document (voce 34) - e la risposta dell'host cosi' come e' arrivata sta
    /// dietro la «?» accanto, non nella frase (prima: «The configuration check failed: L'API del motore
    /// ha risposto 503: {...}»).
    /// </summary>
    [Fact]
    public async Task AFailedConfigurationCheckIsOneSentenceWithTheHostsAnswerBehindTheQuestionMark()
    {
        var (cut, handler) = RenderPanel(new PlantGraph([], [], []));
        handler.FailNext(HttpStatusCode.ServiceUnavailable,
            "{\"title\":\"Il motore non e' caricato\",\"status\":503,\"unavailable\":\"engineNotLoaded\"}");

        await cut.Find("#check-config-button").ClickAsync(new MouseEventArgs());

        Assert.Equal("The configuration check failed because the engine is not ready (it is not loaded on the host).",
            cut.Find("#config-check-error").TextContent);
        Assert.Equal("the host answered 503: {\"title\":\"Il motore non e' caricato\",\"status\":503,\"unavailable\":\"engineNotLoaded\"}",
            DrawioFailureLineAssert.BehindTheQuestionMark(cut, "config-check-error"));
    }

    /// <summary>Un host appeso: il perche' nella frase, il tipo e il messaggio del guasto dietro la «?».</summary>
    [Fact]
    public async Task ATimedOutConfigurationCheckSaysWhyAndKeepsTheFailureBehindTheQuestionMark()
    {
        var (cut, handler) = RenderPanel(new PlantGraph([], [], []));
        handler.ThrowTimeoutNext();

        await cut.Find("#check-config-button").ClickAsync(new MouseEventArgs());

        Assert.Equal("The configuration check failed because the host did not answer in time.", cut.Find("#config-check-error").TextContent);
        Assert.Equal("TaskCanceledException: timeout finto: l'host non ha risposto entro il termine del client",
            DrawioFailureLineAssert.BehindTheQuestionMark(cut, "config-check-error"));
    }

    /// <summary>Un 500/503 non deve abbattere il circuito: si vede nella fascia d'errore, non in silenzio.</summary>
    [Fact]
    public async Task AFailedConfigurationCheckShowsTheErrorInsteadOfCrashingTheCircuit()
    {
        var (cut, handler) = RenderPanel(new PlantGraph([], [], []));
        handler.FailNext(HttpStatusCode.ServiceUnavailable);

        await cut.Find("#check-config-button").ClickAsync(new MouseEventArgs());

        Assert.NotEmpty(cut.FindAll("#config-check-error"));
        Assert.Empty(cut.FindAll("#config-check-result"));
    }

    /// <summary>Un host appeso (timeout, non spento): lo stesso trattamento del 500/503, non un'eccezione che esce dal gestore d'evento.</summary>
    [Fact]
    public async Task ATimedOutConfigurationCheckShowsTheErrorInsteadOfCrashingTheCircuit()
    {
        var (cut, handler) = RenderPanel(new PlantGraph([], [], []));
        handler.ThrowTimeoutNext();

        await cut.Find("#check-config-button").ClickAsync(new MouseEventArgs());

        Assert.NotEmpty(cut.FindAll("#config-check-error"));
    }

    /// <summary>
    /// Zero non e' una prova, sul lato del fallimento: un tentativo riuscito seguito da uno fallito
    /// non deve lasciare in vista il "verificata con successo" del tentativo precedente - sarebbe un
    /// dato non piu' vero mostrato come se lo fosse ancora.
    /// </summary>
    [Fact]
    public async Task AFailedAttemptAfterASuccessfulOneReplacesTheOldResultNotJustAddsToIt()
    {
        var (cut, handler) = RenderPanel(new PlantGraph([], [], []));
        handler.Warnings = [];
        await cut.Find("#check-config-button").ClickAsync(new MouseEventArgs());
        Assert.NotEmpty(cut.FindAll("#config-check-result"));

        handler.FailNext(HttpStatusCode.InternalServerError);
        await cut.Find("#check-config-button").ClickAsync(new MouseEventArgs());

        Assert.Empty(cut.FindAll("#config-check-result"));
        Assert.NotEmpty(cut.FindAll("#config-check-error"));
    }

    /// <summary>Le due verifiche sono indipendenti: eseguire "Check Junctions" non tocca l'esito, ancora non richiesto, di "Check Configuration".</summary>
    [Fact]
    public void RunningTheJunctionCheckDoesNotTouchTheConfigurationCheckState()
    {
        var (cut, _) = RenderPanel(new PlantGraph([], [], []));

        cut.Find("#check-junctions-button").Click();

        Assert.NotEmpty(cut.FindAll("#junction-check-result"));
        Assert.Empty(cut.FindAll("#config-check-result"));
        Assert.Empty(cut.FindAll("#config-check-error"));
    }
}
