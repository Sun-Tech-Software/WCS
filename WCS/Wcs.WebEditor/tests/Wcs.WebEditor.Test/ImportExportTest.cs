using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Io;
using Wcs.WebEditor.Model;
using Wcs.WebEditor.Session;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// <c>ImportExport.razor</c> da solo (compito 12): il cablaggio dei bottoni contro il permesso di
/// modifica, e il giro completo di un'importazione JSON nativa - lettura, riquadro di conferma coi
/// contatori veri, e la scrittura solo dopo il clic su "Confirm".
/// </summary>
public sealed class ImportExportTest : PlantCanvasBunitContext
{
    private static PlantItemConfigDto Item(Guid id) => new() { ItemId = id };

    private (IRenderedComponent<ImportExport> Cut, GraphHandler Handler) RenderComponent(PlantGraph? graph, bool canEdit,
        NavigatorStatusTypes? engineStatus = NavigatorStatusTypes.Stop)
    {
        var handler = new GraphHandler();
        var state = new EditorSessionState(new Uri("http://localhost:5580"), handler);
        state.UseKey("chiave-di-prova");
        Services.AddSingleton(state);

        var cut = Render<ImportExport>(p => p.Add(x => x.Graph, graph).Add(x => x.CanEdit, canEdit).Add(x => x.EngineStatus, engineStatus));
        return (cut, handler);
    }

    private static readonly PlcConnectionDto Plc1 = new(1, "Line 1", "10.0.0.1", 2011, "10.0.0.2", 2012);

    private static void Choose(IRenderedComponent<ImportExport> cut, PlantGraphDto file) =>
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(JsonSerializer.SerializeToUtf8Bytes(file), "impianto.json"));

    /// <summary>
    /// Owner's decision of 27/9 on Pause: a plant is imported only with the engine STOPPED, whatever the
    /// file carries - with or without PLC connections, the host's own or not (it reverses Task 4c's
    /// Pause relaxation). The dialog says so before the confirmation, with the engine's state and, in
    /// Pause, why; it holds the button, and a click anyway sends nothing.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnyFileIsHeldWithTheEnginePaused(bool withTheHostsOwnPlcConnections)
    {
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true, engineStatus: NavigatorStatusTypes.Pause);
        handler.NavStatus = NavigatorStatusTypes.Pause;
        handler.Current = new PlantGraphDto([], [], [], [], null, [Plc1]);

        Choose(cut, new PlantGraphDto([], [], [], [], null, withTheHostsOwnPlcConnections ? [Plc1] : null));

        Assert.Equal(
            "Cannot be imported now, and nothing will be touched: the host imports a plant only with the engine stopped (Stop) - the engine is in Pause, and an import rebuilds every device without its connection, which resuming from Pause does not open again. Stop the engine, then confirm: the file you chose stays here.",
            cut.Find("#plant-import-engine").TextContent.Trim());
        Assert.True(cut.Find("#plant-import-confirm-button").HasAttribute("disabled"));

        cut.Find("#plant-import-confirm-button").Click();

        Assert.Equal(0, handler.GraphPuts);
        Assert.Contains("Nothing was sent", cut.Find("#plant-io-error").TextContent);
        Assert.NotNull(cut.Find("#plant-import-confirm"));
    }

    /// <summary>In Stop nothing is held and the host takes the file.</summary>
    [Fact]
    public void AFileGoesThroughWithTheEngineStopped()
    {
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true, engineStatus: NavigatorStatusTypes.Stop);
        handler.Current = new PlantGraphDto([], [], [], [], null, [Plc1]);

        Choose(cut, new PlantGraphDto([], [], [], [], null, [Plc1 with { LocalPort = 3012 }]));

        Assert.Empty(cut.FindAll("#plant-import-engine"));
        cut.Find("#plant-import-confirm-button").Click();

        Assert.Equal(1, handler.GraphPuts);
        Assert.Equal(HttpStatusCode.NoContent, handler.LastPutAnswer);
    }

    /// <summary>Recover and Dispose hold every file too, and say the state without the Pause reason.</summary>
    [Theory]
    [InlineData(NavigatorStatusTypes.Recover)]
    [InlineData(NavigatorStatusTypes.Dispose)]
    public void AnyFileIsHeldInRecoverAndDispose(NavigatorStatusTypes state)
    {
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true, engineStatus: state);
        handler.NavStatus = state;

        Choose(cut, new PlantGraphDto([], [], [], [], null, null));

        var said = cut.Find("#plant-import-engine").TextContent;
        Assert.Contains("Cannot be imported now", said);
        Assert.Contains($"the host imports a plant only with the engine stopped (Stop) - the engine is in {state}.", said);
        Assert.DoesNotContain("resuming from Pause", said);
        Assert.True(cut.Find("#plant-import-confirm-button").HasAttribute("disabled"));
    }

    /// <summary>
    /// The engine's state unknown: the dialog says what the host accepts - Stop only, for any file -
    /// and leaves the decision to it.
    /// </summary>
    [Fact]
    public void WithTheEngineStateUnknownTheDialogSaysWhatTheHostAccepts()
    {
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true, engineStatus: null);
        handler.Current = new PlantGraphDto([], [], [], [], null, [Plc1]);

        Choose(cut, new PlantGraphDto([], [], [], [], null, null));

        Assert.Equal(
            "The host imports a plant only with the engine stopped (Stop). This page does not know the engine's state right now: if it is not stopped, the host refuses the import and nothing is touched.",
            cut.Find("#plant-import-engine").TextContent.Trim());
        Assert.False(cut.Find("#plant-import-confirm-button").HasAttribute("disabled"));
    }

    /// <summary>
    /// With the engine's state not known the dialog says what the host will decide and does not hold
    /// the button; the host - in Pause - refuses the changed list, and that 409 reads as what it is: a
    /// refusal, nothing touched, with the host's own words behind the line's "?" (row 37 of the queue,
    /// extended to the whole page on 10/10).
    /// </summary>
    [Fact]
    public void WithTheEngineStateUnknownTheHostsRefusalOfAChangedListReadsAsARefusal()
    {
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true, engineStatus: null);
        handler.NavStatus = NavigatorStatusTypes.Pause;
        handler.Current = new PlantGraphDto([], [], [], [], null, [Plc1]);

        Choose(cut, new PlantGraphDto([], [], [], [], null, [Plc1, Plc1 with { Id = 2 }]));

        Assert.Contains("does not know the engine's state", cut.Find("#plant-import-engine").TextContent);
        Assert.False(cut.Find("#plant-import-confirm-button").HasAttribute("disabled"));

        cut.Find("#plant-import-confirm-button").Click();

        var error = cut.Find("#plant-io-error").TextContent;
        Assert.Equal("Import refused, and the plant was not touched: the host refused it - the plant's state does not allow it now.", error);
        var answer = DrawioFailureLineAssert.BehindTheQuestionMark(cut, "plant-io-error");
        Assert.StartsWith("the host answered 409: {", answer);
        Assert.Contains("sostituzione dell", answer);
        Assert.Equal(HttpStatusCode.Conflict, handler.LastPutAnswer);
        Assert.NotNull(cut.Find("#plant-import-confirm"));
    }

    /// <summary>
    /// Export and Import say what they do in words, with the page's icons (DrawioIcon) - since the final
    /// fix round of the passata grafica: until then two bare icons from the old dock, named by a title
    /// only, were the whole content of the Plant file window. The words are the input's name too: the
    /// label that is drawn as the Import button is the file input's label.
    /// </summary>
    [Fact]
    public void ExportAndImportSayWhatTheyDoWithThePagesIcons()
    {
        var (cut, _) = RenderComponent(graph: new PlantGraph(), canEdit: true);

        var export = cut.Find("#plant-export-button");
        Assert.Equal("Export", export.TextContent.Trim());
        Assert.Equal("export", Assert.Single(export.QuerySelectorAll("svg.drawio-icon")).GetAttribute("data-icon"));
        var import = cut.Find("label#plant-import-button");
        Assert.Equal("Choose file", import.TextContent.Trim());
        Assert.Equal("import", Assert.Single(import.QuerySelectorAll("svg.drawio-icon")).GetAttribute("data-icon"));
        Assert.Equal("plant-import-input", import.GetAttribute("for"));
    }

    [Fact]
    public void TheExportButtonIsDisabledWithoutAGraphYet()
    {
        var (cut, _) = RenderComponent(graph: null, canEdit: true);

        Assert.True(cut.Find("#plant-export-button").HasAttribute("disabled"));
    }

    [Fact]
    public void TheImportInputIsDisabledWithoutTheEditPermission()
    {
        var (cut, _) = RenderComponent(graph: new PlantGraph(), canEdit: false);

        Assert.True(cut.Find("#plant-import-input").HasAttribute("disabled"));
    }

    [Fact]
    public void TheImportInputIsEnabledWithTheEditPermission()
    {
        var (cut, _) = RenderComponent(graph: new PlantGraph(), canEdit: true);

        Assert.False(cut.Find("#plant-import-input").HasAttribute("disabled"));
    }

    [Fact]
    public void ClickingExportRereadsTheGraphFromTheHost()
    {
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true);

        cut.Find("#plant-export-button").Click();

        Assert.Equal(1, handler.GraphGets);
    }

    /// <summary>
    /// Il giro intero di un'importazione JSON (compito 12, punti 2 e 5): il file caricato sostituisce
    /// un dispositivo con un altro, il riquadro di conferma mostra un dispositivo perso e nessun
    /// altro, e la scrittura vera (PUT) parte solo dopo "Confirm" - non alla sola selezione del file.
    /// </summary>
    [Fact]
    public void SelectingAJsonFileShowsTheRealDiffAndWritesOnlyAfterConfirmation()
    {
        var kept = Guid.NewGuid();
        var discarded = Guid.NewGuid();
        var incoming = Guid.NewGuid();
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true);
        handler.Current = new PlantGraphDto([Item(kept), Item(discarded)], [], [], []);

        var proposed = new PlantGraphDto([Item(kept), Item(incoming)], [], [], []);
        var json = JsonSerializer.SerializeToUtf8Bytes(proposed);
        var file = InputFileContent.CreateFromBinary(json, "impianto.json");

        cut.FindComponent<InputFile>().UploadFiles(file);

        Assert.Equal(0, handler.GraphPuts); // ancora nessuna scrittura, solo la lettura
        // Correzione minore M10 della revisione: il plurale era fisso ("1 devices"), ora concorda al singolare.
        Assert.Equal("1 device", cut.Find("#plant-import-lost-items").TextContent);
        Assert.Empty(cut.FindAll("#plant-import-legacy-report")); // JSON nativo: nessun verbale di lettura del vecchio disegno

        cut.Find("#plant-import-confirm-button").Click();

        Assert.Equal(1, handler.GraphPuts);
        Assert.NotNull(handler.LastPut);
        Assert.Contains(handler.LastPut!.Items, i => i.ItemId == incoming);
        Assert.DoesNotContain(handler.LastPut.Items, i => i.ItemId == discarded);
    }

    [Fact]
    public void AJsonBackupReportsEveryPlcConnectionChangeBeforeConfirmation()
    {
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true);
        handler.Current = new PlantGraphDto([], [], [], [], null,
        [
            new PlcConnectionDto(1, "unchanged", null, null, null, null),
            new PlcConnectionDto(2, "before", null, null, null, null),
            new PlcConnectionDto(3, "removed", null, null, null, null)
        ]);
        var proposed = new PlantGraphDto([], [], [], [], null,
        [
            new PlcConnectionDto(1, "unchanged", null, null, null, null),
            new PlcConnectionDto(2, "after", null, null, null, null),
            new PlcConnectionDto(4, "added", null, null, null, null)
        ]);

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(
            JsonSerializer.SerializeToUtf8Bytes(proposed), "impianto.json"));

        var summary = cut.Find("#plant-import-plc-summary").TextContent;
        Assert.Contains("1 added", summary);
        Assert.Contains("1 removed", summary);
        Assert.Contains("1 changed", summary);
        Assert.Equal(0, handler.GraphPuts);
    }

    [Fact]
    public void AnOldJsonBackupSaysThatCurrentPlcConnectionsWillBePreserved()
    {
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true);
        handler.Current = new PlantGraphDto([], [], [], [], null,
            [new PlcConnectionDto(1, "current", null, null, null, null)]);
        var oldBackup = new PlantGraphDto([], [], [], []);

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(
            JsonSerializer.SerializeToUtf8Bytes(oldBackup), "vecchio.json"));

        var notice = cut.Find("#plant-import-plc-preserved").TextContent;
        Assert.Contains("says nothing", notice);
        Assert.Contains("stay unchanged", notice);
    }

    [Fact]
    public void AJsonBackupWithDuplicatePlcIdsShowsAFileErrorInsteadOfOpeningTheConfirmation()
    {
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true);
        var invalid = new PlantGraphDto([], [], [], [], null,
        [
            new PlcConnectionDto(7, "first", null, null, null, null),
            new PlcConnectionDto(7, "duplicate", null, null, null, null)
        ]);

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(
            JsonSerializer.SerializeToUtf8Bytes(invalid), "duplicato.json"));

        Assert.Contains("PLC connection 7", cut.Find("#plant-io-error").TextContent);
        Assert.Contains("more than once", cut.Find("#plant-io-error").TextContent);
        Assert.Empty(cut.FindAll("#plant-import-confirm"));
        Assert.Equal(0, handler.GraphPuts);
    }

    /// <summary>
    /// Compito 4 (riga 62), giro di correzione 1: un file con un Id ripetuto E un Id minore di uno
    /// nomina il SECONDO, come il WinForms (FrmPCM_PLCS_LIST_FormClosing controlla prima gli Id minori
    /// di uno, poi i ripetuti) e come la griglia delle connessioni, con cui l'importazione condivide
    /// PlcConnectionRules. Prima di quella condivisione l'importazione percorreva le righe una volta
    /// sola e nominava il primo problema incontrato: qui il ripetuto.
    /// </summary>
    [Fact]
    public void AJsonBackupWithARepeatedAndANonPositivePlcIdNamesTheIdBelowOneFirst()
    {
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true);
        var invalid = new PlantGraphDto([], [], [], [], null,
        [
            new PlcConnectionDto(5, "first", null, null, null, null),
            new PlcConnectionDto(5, "duplicate", null, null, null, null),
            new PlcConnectionDto(0, "zero", null, null, null, null)
        ]);

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(
            JsonSerializer.SerializeToUtf8Bytes(invalid), "misto.json"));

        Assert.Contains("greater than zero", cut.Find("#plant-io-error").TextContent);
        Assert.DoesNotContain("more than once", cut.Find("#plant-io-error").TextContent);
        Assert.Empty(cut.FindAll("#plant-import-confirm"));
        Assert.Equal(0, handler.GraphPuts);
    }

    /// <summary>
    /// Correzione Critico 2 della revisione: un'importazione cancella anche missioni e coda di
    /// estrazione (PlantQueries.RemoveAllItemsAsync), sempre - non dipende dal formato importato -
    /// e nessun file esportato le rimette. La conferma deve nominarle e contarle davvero, non solo i
    /// quattro numeri di PlantGraphDiff che non le vedono nemmeno.
    /// </summary>
    [Fact]
    public void TheConfirmationDialogCountsMissionsAndExtractionQueueRegardlessOfFormat()
    {
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true);
        handler.Missions = [Mission(), Mission()];
        handler.ExtractionQueue = [ExtractionQueueRow(), ExtractionQueueRow(), ExtractionQueueRow()];

        var file = InputFileContent.CreateFromBinary(
            JsonSerializer.SerializeToUtf8Bytes(new PlantGraphDto([], [], [], [])), "impianto.json");
        cut.FindComponent<InputFile>().UploadFiles(file);

        // I DUE CONTEGGI CI SONO ANCORA, E DICONO UN'ALTRA COSA. Stavano nell'elenco di cio' che
        // l'importazione porta via, ed era vero finche' ReplaceGraphAsync passava da
        // RemoveAllItemsAsync, che cancella anche Missions ed ExtractionQueue. Da quando la
        // sostituzione e' atomica l'host la RIFIUTA con lavoro in corso: la stessa frase sarebbe
        // diventata la promessa di una distruzione che non avviene.
        Assert.Equal("2 missions", cut.Find("#plant-import-lost-missions").TextContent);
        Assert.Equal("3 rows of the extraction queue", cut.Find("#plant-import-lost-extraction-queue").TextContent);
        Assert.Contains("Cannot be imported now", cut.Find("#plant-import-blocked").TextContent);

        // E IL PULSANTE NON SI PREME: il rifiuto e' l'unico prevedibile prima del clic, e farlo
        // scoprire dopo sarebbe un giro a vuoto.
        Assert.True(cut.Find("#plant-import-confirm-button").HasAttribute("disabled"));
    }

    /// <summary>
    /// <b>Un rifiuto dell'host non e' un guasto a meta'</b>, e fino al 21/9/2026 finiva nello stesso
    /// posto: 409 e 400 si prendevano l'avviso bloccante «può essere rimasta a metà», il rilascio
    /// del permesso e il ricaricamento pieno della pagina - sull'esito piu' sicuro che questa rotta
    /// sappia dare, il messaggio piu' allarmante che questa pagina sappia dire.
    /// </summary>
    /// <remarks>
    /// La strada esiste da quando la sostituzione e' atomica e rifiuta il lavoro in corso, e
    /// <b>non la copriva nessuna prova</b>: il conteggio del lavoro in corso e' letto prima e puo'
    /// essere vecchio di qualche istante - il controllo vero l'host lo fa nella stessa unita'
    /// serializzata della sostituzione - quindi il 409 arriva anche con il pulsante premibile.
    /// </remarks>
    [Fact]
    public void AnImportTheHostRefusesSaysSoAndLeavesTheFileWhereItIs()
    {
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true);
        handler.PutStatus = HttpStatusCode.Conflict;
        handler.PutBody = """{"title":"Impossibile sostituire il grafo con lavoro pendente","status":409,"detail":"1 missione in corso"}""";

        var file = InputFileContent.CreateFromBinary(
            JsonSerializer.SerializeToUtf8Bytes(new PlantGraphDto([], [], [], [])), "impianto.json");
        cut.FindComponent<InputFile>().UploadFiles(file);
        cut.Find("#plant-import-confirm-button").Click();

        var error = cut.Find("#plant-io-error").TextContent;
        Assert.Equal("Import refused, and the plant was not touched: the host refused it - the plant's state does not allow it now.", error);
        // Il motivo dell'host arriva intero, titolo e dettaglio, dietro la «?» della riga e non nella
        // frase (voce 37 della coda, estesa a tutta la pagina il 10/10).
        Assert.Equal("the host answered 409: {\"title\":\"Impossibile sostituire il grafo con lavoro pendente\",\"status\":409,\"detail\":\"1 missione in corso\"}",
            DrawioFailureLineAssert.BehindTheQuestionMark(cut, "plant-io-error"));

        // E la finestra resta aperta con il file scelto: il secondo tentativo e' un clic, non un
        // ricaricamento e una chiave da ridare.
        Assert.NotNull(cut.Find("#plant-import-confirm"));
    }

    /// <summary>
    /// Correzione Importante 7 della revisione, punto 1: il ramo legacy del componente non era mai
    /// reso da nessun test - #plant-import-legacy-report, il paragrafo di UnknownGuids e
    /// l'avvertenza sui contatori (il requisito 4 del compito) non comparivano in nessuna prova.
    /// </summary>
    [Fact]
    public void SelectingALegacyDrawingShowsTheMigrationReportAndTheCaveat()
    {
        var known = Guid.NewGuid();
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true);
        handler.Current = new PlantGraphDto([Item(known)], [], [], []);
        handler.Legacy = new LegacyGeometryImportDto(
            [new PlantItemGeometryDto(known, 10, 20, 30, 40, 0), new PlantItemGeometryDto(Guid.NewGuid(), 1, 1, 1, 1, 0)],
            ElementsWithoutGuid: 2, GuidsWithoutGeometry: 1, PartiallyPlaced: 1);

        var file = InputFileContent.CreateFromBinary([1, 2, 3], "vecchio-disegno.edd");
        cut.FindComponent<InputFile>().UploadFiles(file);

        var report = cut.Find("#plant-import-legacy-report");
        Assert.Contains("2", report.TextContent); // dispositivi collocati (letti dal disegno)
        Assert.Contains("1", report.TextContent); // Guid del disegno non noti a questo impianto (UnknownGuids)
        Assert.Contains("0", cut.Find("#plant-import-items-not-mentioned").TextContent); // known e' nominato dal disegno
        Assert.Contains("does not name at all", cut.Find("#plant-import-items-not-mentioned").TextContent);
        Assert.Contains("the numbers are not a partition", cut.Find("#plant-import-legacy-report .plant-import-caveat").TextContent);
    }

    /// <summary>
    /// Every count of the old drawing's report agrees with its number (review of 27/9): "1 device
    /// placed", "1 node", "1 Guid", and the sentences about them in the singular too.
    /// </summary>
    [Fact]
    public void TheOldDrawingsReportSpeaksInTheSingularForOne()
    {
        var known = Guid.NewGuid();
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true);
        handler.Current = new PlantGraphDto([Item(known), Item(Guid.NewGuid())], [], [], []);
        handler.Legacy = new LegacyGeometryImportDto(
            [new PlantItemGeometryDto(known, 10, 20, 30, 40, 0)],
            ElementsWithoutGuid: 1, GuidsWithoutGeometry: 1, PartiallyPlaced: 1);

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3], "vecchio-disegno.edd"));

        var report = Plain(cut.Find("#plant-import-legacy-report").TextContent);
        Assert.Contains("Read from the old drawing: 1 device placed (1 of them with a box read only in part).", report);
        Assert.Contains("Not recognized in the drawing: 1 node without a readable Guid, 1 Guid without a readable box in any of its occurrences.", report);
        Assert.Equal(
            "1 device of this plant that the drawing does not name at all: it keeps the position it already had - the drawing simply says nothing about it.",
            Plain(cut.Find("#plant-import-items-not-mentioned").TextContent));
    }

    /// <summary>The twin of the above, with one Guid of the drawing unknown to the plant and the plural everywhere else.</summary>
    [Fact]
    public void TheOldDrawingsReportSpeaksInThePluralForMoreThanOne()
    {
        var known = Guid.NewGuid();
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true);
        handler.Current = new PlantGraphDto([Item(known), Item(Guid.NewGuid()), Item(Guid.NewGuid())], [], [], []);
        handler.Legacy = new LegacyGeometryImportDto(
            [new PlantItemGeometryDto(known, 10, 20, 30, 40, 0), new PlantItemGeometryDto(Guid.NewGuid(), 1, 1, 1, 1, 0)],
            ElementsWithoutGuid: 2, GuidsWithoutGeometry: 3, PartiallyPlaced: 0);

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3], "vecchio-disegno.edd"));

        var report = Plain(cut.Find("#plant-import-legacy-report").TextContent);
        Assert.Contains("Read from the old drawing: 2 devices placed (0 of them with a box read only in part).", report);
        Assert.Contains("Not recognized in the drawing: 2 nodes without a readable Guid, 3 Guids without a readable box in any of their occurrences.", report);
        Assert.Contains("1 Guid of the drawing matches no device of this plant: it is ignored, and its position goes nowhere.", report);
        Assert.Contains("2 devices of this plant that the drawing does not name at all: they keep the position they already had - the drawing simply says nothing about them.", report);
    }

    /// <summary>
    /// A refusal whose body is JSON but not a problem document - a list, a number, a title or a
    /// detail that is not a string - is still read as a refusal, with the body as it came behind the
    /// line's "?". Reading such a body used to throw inside the refusal's own catch, which would have
    /// ended the circuit; since row 37 (extended on 10/10) nothing reads it at all.
    /// </summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("""{"title":1}""")]
    [InlineData("""{"title":"Il motore non è fermo","detail":{"a":1}}""")]
    public void ARefusalWithABodyThatIsNotAProblemDocumentIsQuotedAsItCame(string body)
    {
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true);
        handler.PutStatus = HttpStatusCode.Conflict;
        handler.PutBody = body;
        Choose(cut, new PlantGraphDto([], [], [], []));

        cut.Find("#plant-import-confirm-button").Click();

        Assert.Equal("Import refused, and the plant was not touched: the host refused it - the plant's state does not allow it now.",
            cut.Find("#plant-io-error").TextContent);
        Assert.Equal($"the host answered 409: {body}", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "plant-io-error"));
        Assert.NotNull(cut.Find("#plant-import-confirm"));
    }

    /// <summary>
    /// A 400 of the replacement is a plant the host does not take, and its words - here a detail with
    /// no title - are behind the line's "?", not in it.
    /// </summary>
    [Fact]
    public void ARefusedPlantKeepsTheHostsWordsBehindTheQuestionMark()
    {
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true);
        handler.PutStatus = HttpStatusCode.BadRequest;
        handler.PutBody = """{"detail":"un dispositivo senza tipo"}""";
        Choose(cut, new PlantGraphDto([], [], [], []));

        cut.Find("#plant-import-confirm-button").Click();

        Assert.Equal("Import refused, and the plant was not touched: the host refused it - the plant in the file is not valid for the host.",
            cut.Find("#plant-io-error").TextContent);
        Assert.Equal("the host answered 400: {\"detail\":\"un dispositivo senza tipo\"}",
            DrawioFailureLineAssert.BehindTheQuestionMark(cut, "plant-io-error"));
    }

    /// <summary>
    /// The client's own framing of a host failure ("L'API del motore ha risposto ...") never reaches
    /// the page: the export says why in the page's words, and the host's answer as it came is behind
    /// the line's "?" (row 37 of the queue, extended to the whole page on 10/10).
    /// </summary>
    [Fact]
    public void AnExportTheHostFailsSaysWhatTheHostAnswered()
    {
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true);
        handler.GetStatus = HttpStatusCode.InternalServerError;
        handler.GetBody = "guasto";

        cut.Find("#plant-export-button").Click();

        Assert.Equal("Export failed because the host answered with an error.", cut.Find("#plant-io-error").TextContent);
        Assert.Equal("the host answered 500: guasto", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "plant-io-error"));
    }

    /// <summary>
    /// A pick whose host reads fail is not "the file could not be read": the file was never looked
    /// at. The line says the host did not give the plant, and what it answered.
    /// </summary>
    [Fact]
    public void APickWhoseHostReadFailsBlamesTheHostNotTheFile()
    {
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true);
        handler.GetStatus = HttpStatusCode.ServiceUnavailable;
        handler.GetBody = """{"title":"Memoria non disponibile"}""";

        Choose(cut, new PlantGraphDto([], [], [], []));

        Assert.Equal(
            "The plant as it is now could not be read from the host because the host answered with an error, so the file was not compared with it.",
            cut.Find("#plant-io-error").TextContent);
        Assert.Equal("the host answered 503: {\"title\":\"Memoria non disponibile\"}", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "plant-io-error"));
        Assert.Empty(cut.FindAll("#plant-import-confirm"));
    }

    /// <summary>The same for an old drawing the host could not read: it is the host's answer that is behind the "?".</summary>
    [Fact]
    public void AnOldDrawingTheHostCannotReadSaysWhatTheHostAnswered()
    {
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true);
        handler.LegacyStatus = HttpStatusCode.InternalServerError;

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3], "vecchio-disegno.edd"));

        Assert.Equal("The host could not read the old drawing, because the host answered with an error.",
            cut.Find("#plant-io-error").TextContent);
        Assert.Equal("the host answered 500 with an empty body", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "plant-io-error"));
        Assert.Empty(cut.FindAll("#plant-import-confirm"));
    }

    /// <summary>
    /// An outcome the page cannot know (a 5xx after the confirmation) is told in the blocking alert,
    /// with the host's answer and without the client's Italian framing.
    /// </summary>
    [Fact]
    public void TheAlertOfAnUnknownOutcomeSaysWhatTheHostAnswered()
    {
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true);
        handler.PutStatus = HttpStatusCode.InternalServerError;
        handler.PutBody = "guasto";
        Choose(cut, new PlantGraphDto([], [], [], []));

        cut.Find("#plant-import-confirm-button").Click();

        var alert = Assert.Single(JSInterop.Invocations, call => call.Identifier == "alert");
        var said = Assert.IsType<string>(alert.Arguments[0]);
        Assert.EndsWith("Detail: the host answered 500: guasto", said);
        Assert.DoesNotContain("L'API del motore", said);
    }

    /// <summary>The text of an element with its source line breaks and indentation folded into single spaces.</summary>
    private static string Plain(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

    private static MissionDto Mission() => new(
        MissionId: Guid.NewGuid(), LoadUnitId: 0, LoadUnitCode: null, PositionItemId: null,
        SourceItemId: null, DestinationItemId: null, Date: default, StatusDescription: null,
        StorageDoorCode: null, PickUpDoorCode: null, Status: default, CommandId: null,
        RelocateItemId: null, MissionType: default, MovingDescription: null, OperationType: default,
        BufferCellId: 0, ExtractionQueueId: 0, LoadUnitTypeId: 0, GroupedMovementId: null,
        HandlingMode: 0, GatewayItemId: null, SourceAsi: null, SourceSubitem: null, SourcePartition: null,
        DestinationAsi: null, DestinationSubitem: null, DestinationPartition: null, NoteAssignmentId: 0,
        AssignmentGuid: null, NoteProcessingId: 0, ActiveFilter: false, Tare: 0, ReallocatedDestination: false,
        OperatorDestination: false);

    private static ExtractionQueueItemDto ExtractionQueueRow() => new(
        Id: 0, AssignmentId: null, LoadUnitId: 0, CellId: null, BufferCellId: null,
        SourceItemId: Guid.NewGuid(), DestinationItemId: null, Extract: true, WorkstationId: null,
        Processed: false, RelocatorItemId: null, DoorCode: null, SourceAssignmentId: null,
        ExtractionRowId: null, DestinationCellId: null, DestinationDoorCode: null, Priority: 0,
        Compacting: false, AssignmentGuid: null, ApiCommandId: null);

    // Row 37 of the queue, extended to the whole page on 10/10: the failures of the file itself say what
    // they are in the page's words, and what was thrown is behind the line's "?".

    /// <summary>A .json that is not JSON at all: the page's sentence, the parser's words behind the "?".</summary>
    [Fact]
    public void AJsonFileThatIsNotJsonKeepsTheParsersWordsBehindTheQuestionMark()
    {
        var (cut, _) = RenderComponent(graph: new PlantGraph(), canEdit: true);

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(Encoding.UTF8.GetBytes("{ non e' JSON"), "impianto.json"));

        Assert.Equal("The file is not a plant file: a .json file has to be an export of this Editor.", cut.Find("#plant-io-error").TextContent);
        Assert.StartsWith("JsonException: ", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "plant-io-error"));
    }

    /// <summary>A file over what the page takes: said in the page's words, the browser's own behind the "?".</summary>
    [Fact]
    public void AFileTooLargeToReadIsSaidInThePagesWords()
    {
        var (cut, _) = RenderComponent(graph: new PlantGraph(), canEdit: true);

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(new byte[64 * 1024 * 1024 + 1], "impianto.json"));

        Assert.Equal("The file could not be read: it is larger than the 64 MB this page takes, or the browser did not hand it over.",
            cut.Find("#plant-io-error").TextContent);
        Assert.StartsWith("IOException: ", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "plant-io-error"));
    }

    /// <summary>
    /// The plant that cannot be read again at "Confirm", to check it is still the one compared: nothing
    /// was written, the why in the page's words and the host's answer behind the "?".
    /// </summary>
    [Fact]
    public void APlantThatCannotBeReadAtTheConfirmationKeepsTheHostsAnswerBehindTheQuestionMark()
    {
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true);
        Choose(cut, new PlantGraphDto([], [], [], []));
        handler.GetStatus = HttpStatusCode.InternalServerError;
        handler.GetBody = "guasto";

        cut.Find("#plant-import-confirm-button").Click();

        Assert.Equal("Nothing was written: the plant could not be read again just before writing, to check that it is still the one this confirmation compares the file with, " +
                     "because the host answered with an error. Confirm again in a moment.", cut.Find("#plant-io-error").TextContent);
        Assert.Equal("the host answered 500: guasto", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "plant-io-error"));
        Assert.Equal(0, handler.GraphPuts);
    }

    /// <summary>Un file con un'estensione non riconosciuta non arriva nemmeno a interrogare l'host.</summary>
    [Fact]
    public void AnUnrecognizedExtensionIsRefusedWithoutCallingTheHost()
    {
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true);
        var file = InputFileContent.CreateFromBinary([1, 2, 3], "impianto.txt");

        cut.FindComponent<InputFile>().UploadFiles(file);

        Assert.Equal(0, handler.GraphGets);
        Assert.Contains("not recognized", cut.Find("#plant-io-error").TextContent);
    }

    /// <summary>
    /// A .json file that is valid JSON but not a plant: <c>null</c> used to throw an exception the
    /// file handler did not catch - on Blazor Server that ends the circuit - and <c>{}</c> or a list
    /// holding a null reached the comparison and threw there. Each one is now said in the dialog's
    /// error line, nothing is proposed and nothing is written.
    /// </summary>
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"a plant\"")]
    [InlineData("{}")]
    [InlineData("""{"Items":null,"Geometries":[],"Junctions":[],"DoorMappings":[]}""")]
    [InlineData("""{"Items":[null],"Geometries":[],"Junctions":[],"DoorMappings":[]}""")]
    [InlineData("""{"Items":[],"Geometries":[null],"Junctions":[],"DoorMappings":[]}""")]
    [InlineData("""{"Items":[],"Geometries":[],"Junctions":[null],"DoorMappings":[]}""")]
    [InlineData("""{"Items":[],"Geometries":[],"Junctions":[],"DoorMappings":[null]}""")]
    [InlineData("""{"Items":[],"Geometries":[],"Junctions":[],"DoorMappings":[],"PlcConnections":[null]}""")]
    public void AJsonFileThatIsNotAPlantIsSaidAndNothingIsProposed(string content)
    {
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true);
        handler.Current = new PlantGraphDto([Item(Guid.NewGuid())], [], [], []);

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(Encoding.UTF8.GetBytes(content), "impianto.json"));

        Assert.Equal("The file is not a plant file: a .json file has to be an export of this Editor.", cut.Find("#plant-io-error").TextContent);
        Assert.Empty(cut.FindAll("#plant-import-confirm"));
        Assert.Equal(0, handler.GraphPuts);
        // And the component is still alive and usable: the next file is read as usual.
        Choose(cut, new PlantGraphDto([], [], [], []));
        Assert.NotNull(cut.Find("#plant-import-confirm"));
    }

    /// <summary>
    /// The same kind of file one step further: a plant, but with one device placed twice. The
    /// comparison indexes the positions by device and used to throw on it, outside the handler's
    /// catch; it is said instead, with the device.
    /// </summary>
    [Fact]
    public void AJsonFileWithADevicePlacedTwiceIsSaidAndNothingIsProposed()
    {
        var device = Guid.NewGuid();
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true);

        Choose(cut, new PlantGraphDto([Item(device)],
            [new PlantItemGeometryDto(device, 1, 2, 3, 4, 0), new PlantItemGeometryDto(device, 5, 6, 7, 8, 0)], [], []));

        Assert.Contains($"Device {device} has more than one position", cut.Find("#plant-io-error").TextContent);
        Assert.Empty(cut.FindAll("#plant-import-confirm"));
        Assert.Equal(0, handler.GraphPuts);
    }

    /// <summary>
    /// Queue item 17 (owner's decision of 28/9): two junctions joining the same two devices the same
    /// way round are refused, as both editors refuse an arrow that doubles one and as the host now
    /// refuses the file. Said before the confirmation, naming the two devices, and nothing proposed;
    /// without an identifier too, as in old archives.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AJsonFileThatJoinsTwoDevicesTwiceTheSameWayIsSaidByNameAndNothingIsProposed(bool withoutIdentifiers)
    {
        var from = Guid.NewGuid();
        var to = Guid.NewGuid();
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true);

        Choose(cut, new PlantGraphDto(
            [new PlantItemConfigDto { ItemId = from, Name = "Conveyor 1" }, new PlantItemConfigDto { ItemId = to, Name = "Shuttle 2" }],
            [],
            [
                new JunctionDto(withoutIdentifiers ? Guid.Empty : Guid.NewGuid(), from, to, 1),
                new JunctionDto(withoutIdentifiers ? Guid.Empty : Guid.NewGuid(), from, to, 99),
            ],
            []));

        var error = cut.Find("#plant-io-error").TextContent;
        Assert.Contains("Conveyor 1", error);
        Assert.Contains("Shuttle 2", error);
        Assert.Contains("twice", error);
        Assert.Empty(cut.FindAll("#plant-import-confirm"));
        Assert.Equal(0, handler.GraphPuts);
    }

    /// <summary>
    /// Queue item 17, fix round: an old .edd/.xml carries only positions, and its proposal takes the
    /// junctions from the plant as it is now (<c>LegacyGeometryMerge.Apply</c>). A plant that already
    /// holds two junctions between the same two devices the same way round - a migration from the old
    /// table could leave one - would have been refused by the host only after "Confirm", in Italian.
    /// Said before, in the dialog, as what it is: the double is in the plant, not in the file. The
    /// button is held.
    /// </summary>
    [Fact]
    public void AnOldDrawingOnAPlantThatAlreadyJoinsTwoDevicesTwiceSaysSoBeforeTheConfirmation()
    {
        var from = Guid.NewGuid();
        var to = Guid.NewGuid();
        var (cut, handler) = RenderComponent(graph: new PlantGraph([], [], [AutomationType()]), canEdit: true);
        handler.Current = new PlantGraphDto(
            [new PlantItemConfigDto { ItemId = from, Name = "Conveyor 1" }, new PlantItemConfigDto { ItemId = to, Name = "Shuttle 2" }],
            [new PlantItemGeometryDto(from, 10, 20, 120, 60, 0), new PlantItemGeometryDto(to, 400, 20, 120, 60, 0)],
            [new JunctionDto(Guid.NewGuid(), from, to, 1), new JunctionDto(Guid.NewGuid(), from, to, 99)],
            []);
        handler.Legacy = new LegacyGeometryImportDto([new PlantItemGeometryDto(from, 10, 20, 30, 40, 0)], 0, 0, 0);

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3], "vecchio-disegno.edd"));

        var said = cut.Find("#plant-import-doubled-junction").TextContent;
        Assert.Contains($"The plant already holds two junctions from Conveyor 1 ({from}) to Shuttle 2 ({to}) (drawn one on top of the other)", said);
        Assert.Contains("delete one of the two arrows, save, then import again", said);
        Assert.True(cut.Find("#plant-import-confirm-button").HasAttribute("disabled"));
        Assert.Equal(0, handler.GraphPuts);
    }

    /// <summary>
    /// Last fix round of queue item 17: when an end of the doubled pair is not drawn, neither of the
    /// two arrows is on the drawing (<c>DrawioDocument.WhyNotDrawn</c>) - and a plant with undrawn
    /// devices is exactly the one that takes its positions from an old drawing. "Delete one of the two
    /// arrows" was out of reach: the line names the two junctions and says what can be done instead.
    /// </summary>
    [Fact]
    public void AnOldDrawingOnAPlantWhoseDoubledJunctionIsNotDrawnNamesTheTwoJunctions()
    {
        var from = Guid.NewGuid();
        var to = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var (cut, handler) = RenderComponent(graph: new PlantGraph([], [], [AutomationType()]), canEdit: true);
        handler.Current = new PlantGraphDto(
            [new PlantItemConfigDto { ItemId = from, Name = "Conveyor 1" }, new PlantItemConfigDto { ItemId = to, Name = "Shuttle 2" }],
            [new PlantItemGeometryDto(from, 10, 20, 120, 60, 0)],
            [new JunctionDto(first, from, to, 1), new JunctionDto(second, from, to, 99)],
            []);
        handler.Legacy = new LegacyGeometryImportDto([new PlantItemGeometryDto(to, 400, 20, 120, 60, 0)], 0, 0, 0);

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3], "vecchio-disegno.edd"));

        var said = cut.Find("#plant-import-doubled-junction").TextContent;
        Assert.Contains($"The plant already holds two junctions from Conveyor 1 ({from}) to Shuttle 2 ({to}), {first} and {second}", said);
        Assert.Contains($"their arrows are not on the drawing, because Shuttle 2 ({to}) is not drawn", said);
        Assert.Contains("No gesture of this page deletes a junction that is not drawn", said);
        Assert.DoesNotContain("delete one of the two arrows", said);
        Assert.True(cut.Find("#plant-import-confirm-button").HasAttribute("disabled"));
    }

    /// <summary>
    /// The dialog and the drawing answer "is it drawn?" with ONE rule
    /// (<c>DrawioDocument.WhatKeepsItOffTheDrawing</c>), and this is where two copies would part: a
    /// device WITH a box whose type the registry does not declare - or a registry the page holds
    /// empty - is not drawn, so its arrows are not on the drawing. Asked of both, side by side.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADeviceWithABoxButNoDeclaredTypeIsNotDrawnForTheDialogAsForTheDrawing(bool registryDeclaresOtherTypes)
    {
        var from = Guid.NewGuid();
        var to = Guid.NewGuid();
        const short undeclared = 7;
        DeviceTypeDto[] types = registryDeclaresOtherTypes ? [AutomationType()] : [];
        var page = new PlantGraph([], [], types);
        var (cut, handler) = RenderComponent(graph: page, canEdit: true);
        handler.Current = new PlantGraphDto(
            [new PlantItemConfigDto { ItemId = from, Name = "Conveyor 1" }, new PlantItemConfigDto { ItemId = to, Name = "Shuttle 2", DeviceType = undeclared }],
            [new PlantItemGeometryDto(from, 10, 20, 120, 60, 0), new PlantItemGeometryDto(to, 400, 20, 120, 60, 0)],
            [new JunctionDto(Guid.NewGuid(), from, to, 1), new JunctionDto(Guid.NewGuid(), from, to, 99)],
            []);
        handler.Legacy = new LegacyGeometryImportDto([new PlantItemGeometryDto(from, 10, 20, 120, 60, 0)], 0, 0, 0);

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3], "vecchio-disegno.edd"));

        // The drawing's own answer for the same device, box and all.
        var shuttle = new PlantItemDto(to, "Shuttle 2", (DeviceTypes)undeclared, null, null, null, default, default, 0, null,
            0, 0, 0, 0, default!, 400, 20, 120, 60, 0);
        Assert.NotNull(Wcs.WebEditor.Drawio.DrawioDocument.WhyNotDrawn(page, shuttle));
        // With an empty registry Conveyor 1 is not drawn either: the rule says so of both.
        var said = cut.Find("#plant-import-doubled-junction").TextContent;
        Assert.Contains("their arrows are not on the drawing, because", said);
        Assert.Contains(registryDeclaresOtherTypes
            ? $"because Shuttle 2 ({to}) is not drawn"
            : $"because Conveyor 1 ({from}) and Shuttle 2 ({to}) are not drawn", said);
    }

    private static DeviceTypeDto AutomationType() => new("automation", 0, "Automation",
        [new ShapePartDto("M 4 4 H 96 V 96 H 4 Z", "#334155", Filled: false, Rigid: false)],
        [new DevicePortDto("in", 4, 50, "in"), new DevicePortDto("out", 96, 50, "out")],
        new ShapePointDto(50, 50), new ShapePointDto(88, 60));

    /// <summary>The same old drawing on a plant with one junction each way: nothing to say, and "Confirm" can be pressed.</summary>
    [Fact]
    public void AnOldDrawingOnAPlantThatJoinsTwoDevicesOnceEachWayCanBeConfirmed()
    {
        var from = Guid.NewGuid();
        var to = Guid.NewGuid();
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true);
        handler.Current = new PlantGraphDto([Item(from), Item(to)], [],
            [new JunctionDto(Guid.NewGuid(), from, to, 1), new JunctionDto(Guid.NewGuid(), to, from, 1)], []);
        handler.Legacy = new LegacyGeometryImportDto([new PlantItemGeometryDto(from, 10, 20, 30, 40, 0)], 0, 0, 0);

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3], "vecchio-disegno.edd"));

        Assert.Empty(cut.FindAll("#plant-import-doubled-junction"));
        Assert.False(cut.Find("#plant-import-confirm-button").HasAttribute("disabled"));
    }

    /// <summary>One junction each way between the same two devices is two connections, not a double: proposed as usual.</summary>
    [Fact]
    public void AJsonFileThatJoinsTwoDevicesOnceEachWayIsProposed()
    {
        var from = Guid.NewGuid();
        var to = Guid.NewGuid();
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true);

        Choose(cut, new PlantGraphDto([Item(from), Item(to)], [],
            [new JunctionDto(Guid.NewGuid(), from, to, 1), new JunctionDto(Guid.NewGuid(), to, from, 1)], []));

        Assert.Empty(cut.FindAll("#plant-io-error"));
        Assert.NotNull(cut.Find("#plant-import-confirm"));
        Assert.Equal(0, handler.GraphPuts);
    }

    /// <summary>
    /// Il gestore HTTP finto delle rotte di /plant/graph, /plant/graph/legacy-geometry, /missions e
    /// /extraction-queue - le ultime due perche' il riquadro di conferma le conta sempre (Critico 2
    /// della revisione: un'importazione le cancella comunque, e la conferma deve nominarle).
    /// </summary>
    // ---- Fix round of 27/9: a confirmation can go stale while it waits ------------------------

    /// <summary>
    /// A confirmation can wait - hidden in its shut section, since 27/9 - while the plant changes
    /// under it. The comparison it shows, and for an old drawing the whole proposal (the plant as it
    /// was, merged with the drawing's positions), were worked out when the file was chosen: sending
    /// that proposal now would put back what was changed meanwhile, under a dialog that says nothing
    /// is lost. So "Confirm" compares again just before writing, and a plant that moved gets the new
    /// comparison on screen and NOTHING written; the next "Confirm" writes the new proposal.
    /// </summary>
    [Fact]
    public void AConfirmOnAPlantChangedUnderneathWritesNothingAndTheNextConfirmWritesTheNewProposal()
    {
        var known = Guid.NewGuid();
        var added = Guid.NewGuid();
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true);
        handler.Current = new PlantGraphDto([new PlantItemConfigDto { ItemId = known, Name = "before" }], [], [], []);
        handler.Legacy = new LegacyGeometryImportDto([new PlantItemGeometryDto(known, 10, 20, 30, 40, 0)], 0, 0, 0);
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3], "vecchio-disegno.edd"));
        Assert.StartsWith("0 devices", cut.Find("#plant-import-items-not-mentioned").TextContent.Trim());
        Assert.Empty(cut.FindAll("#plant-import-plant-changed"));

        // Somebody changes the plant while the confirmation waits: a device renamed, one added.
        handler.Current = new PlantGraphDto(
            [new PlantItemConfigDto { ItemId = known, Name = "after" }, new PlantItemConfigDto { ItemId = added, Name = "new" }], [], [], []);

        cut.Find("#plant-import-confirm-button").Click();

        Assert.Equal(0, handler.GraphPuts);
        Assert.Single(cut.FindAll("#plant-import-confirm"));
        Assert.Contains("Nothing was written", cut.Find("#plant-import-plant-changed").TextContent);
        Assert.StartsWith("1 device", cut.Find("#plant-import-items-not-mentioned").TextContent.Trim());

        cut.Find("#plant-import-confirm-button").Click();

        Assert.Equal(1, handler.GraphPuts);
        var sent = handler.LastPut!;
        Assert.Equal("after", sent.Items.Single(item => item.ItemId == known).Name);
        Assert.Contains(sent.Items, item => item.ItemId == added);
    }

    /// <summary>The same check for a native file: the plant it replaces gained a device, so what is lost is no longer what the dialog said.</summary>
    [Fact]
    public void ANativeFileConfirmedOnAPlantThatGainedADeviceSaysSoBeforeWriting()
    {
        var kept = Guid.NewGuid();
        var added = Guid.NewGuid();
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true);
        handler.Current = new PlantGraphDto([Item(kept)], [], [], []);
        Choose(cut, new PlantGraphDto([Item(kept)], [], [], []));
        Assert.StartsWith("0 devices", cut.Find("#plant-import-lost-items").TextContent.Trim());

        handler.Current = new PlantGraphDto([Item(kept), Item(added)], [], [], []);
        cut.Find("#plant-import-confirm-button").Click();

        Assert.Equal(0, handler.GraphPuts);
        Assert.StartsWith("1 device", cut.Find("#plant-import-lost-items").TextContent.Trim());
        Assert.Single(cut.FindAll("#plant-import-plant-changed"));
    }

    // ---- Second fix round of 27/9 --------------------------------------------------------------

    private (IRenderedComponent<ImportExport> Cut, GraphHandler Handler, Func<PlantImportStatus?> Told) RenderTelling()
    {
        var handler = new GraphHandler();
        var state = new EditorSessionState(new Uri("http://localhost:5580"), handler);
        state.UseKey("chiave-di-prova");
        Services.AddSingleton(state);
        PlantImportStatus? told = null;
        var cut = Render<ImportExport>(p => p
            .Add(x => x.Graph, new PlantGraph())
            .Add(x => x.CanEdit, true)
            .Add(x => x.EngineStatus, NavigatorStatusTypes.Stop)
            .Add(x => x.OnPendingImportChanged, (PlantImportStatus? status) => told = status));
        return (cut, handler, () => told);
    }

    /// <summary>
    /// A: the host lists the same plant in another order (items by ASI and name, the other rows as
    /// the database gives them): that is not a change, and "Confirm" writes at once.
    /// </summary>
    [Fact]
    public void AConfirmOnThePlantReadBackInAnotherOrderWritesAtOnce()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true);
        var plc2 = new PlcConnectionDto(2, "Line 2", "10.0.0.3", 2011, "10.0.0.4", 2012);
        handler.Current = new PlantGraphDto(
            [Item(first), Item(second)],
            [new PlantItemGeometryDto(first, 1, 2, 3, 4, 0), new PlantItemGeometryDto(second, 5, 6, 7, 8, 0)],
            [new JunctionDto(Guid.NewGuid(), first, second, 0), new JunctionDto(Guid.NewGuid(), second, first, 99)],
            [], null, [Plc1, plc2]);
        Choose(cut, new PlantGraphDto([Item(first)], [], [], []));

        var read = handler.Current;
        handler.Current = new PlantGraphDto(
            [.. read.Items.Reverse()], [.. read.Geometries.Reverse()], [.. read.Junctions.Reverse()],
            [], null, [.. read.PlcConnections!.Reverse()]);
        cut.Find("#plant-import-confirm-button").Click();

        Assert.Equal(1, handler.GraphPuts);
        Assert.Empty(cut.FindAll("#plant-import-plant-changed"));
    }

    /// <summary>
    /// B: once the operator confirms again, the "plant changed" notice has done its job and goes; a
    /// second "Confirm" that is refused is then said as a refusal, on the dialog and to the host
    /// component - not still as "the plant changed".
    /// </summary>
    [Fact]
    public void ASecondConfirmThatIsRefusedDropsTheChangedNoticeAndSaysNotWritten()
    {
        var kept = Guid.NewGuid();
        var (cut, handler, told) = RenderTelling();
        handler.Current = new PlantGraphDto([Item(kept)], [], [], []);
        Choose(cut, new PlantGraphDto([Item(kept)], [], [], []));
        handler.Current = new PlantGraphDto([Item(kept), Item(Guid.NewGuid())], [], [], []);
        cut.Find("#plant-import-confirm-button").Click();
        Assert.Single(cut.FindAll("#plant-import-plant-changed"));
        Assert.Equal(PlantImportPhase.PlantChanged, told()?.Phase);

        handler.PutStatus = HttpStatusCode.Conflict;
        cut.Find("#plant-import-confirm-button").Click();

        Assert.Equal(1, handler.GraphPuts);
        Assert.Empty(cut.FindAll("#plant-import-plant-changed"));
        Assert.Contains("Import refused", cut.Find("#plant-io-error").TextContent);
        Assert.Equal(PlantImportPhase.NotWritten, told()?.Phase);
    }

    /// <summary>
    /// C: a comparison made again in the background that cannot read the plant is its own state - no
    /// "Confirm" was pressed, so nothing was "not written" - and the next comparison that succeeds
    /// clears it, whether or not it finds a change.
    /// </summary>
    [Fact]
    public void ABackgroundCheckThatCannotReadThePlantSaysSoAndASuccessfulOneClearsIt()
    {
        var kept = Guid.NewGuid();
        var (cut, handler, told) = RenderTelling();
        handler.Current = new PlantGraphDto([Item(kept)], [], [], []);
        Choose(cut, new PlantGraphDto([Item(kept)], [], [], []));
        cut.Render(p => p.Add(x => x.Shown, false));

        handler.GetStatus = HttpStatusCode.InternalServerError;
        cut.Render(p => p.Add(x => x.Shown, true));

        Assert.Equal(PlantImportPhase.NotCompared, told()?.Phase);
        Assert.Equal("The plant could not be read again to check this file against it because the host answered with an error: the comparison below may be old. \"Confirm\" reads it again before writing anything.",
            cut.Find("#plant-io-error").TextContent);
        Assert.StartsWith("the host answered 500", DrawioFailureLineAssert.BehindTheQuestionMark(cut, "plant-io-error"));
        Assert.Equal(0, handler.GraphPuts);

        handler.GetStatus = null;
        cut.Render(p => p.Add(x => x.Shown, false));
        cut.Render(p => p.Add(x => x.Shown, true));

        Assert.Equal(PlantImportPhase.Waiting, told()?.Phase);
        Assert.Empty(cut.FindAll("#plant-io-error"));
        Assert.Empty(cut.FindAll("#plant-import-plant-changed"));
    }

    /// <summary>
    /// D: a comparison asked for while another operation holds the component (an export on its way
    /// here) is not dropped: it runs as soon as that operation ends.
    /// </summary>
    [Fact]
    public async Task ACheckAskedForDuringAnExportRunsWhenTheExportEnds()
    {
        var kept = Guid.NewGuid();
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true);
        handler.Current = new PlantGraphDto([Item(kept)], [], [], []);
        Choose(cut, new PlantGraphDto([Item(kept)], [], [], []));

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.GraphGetGate = gate;
        var exporting = cut.InvokeAsync(() => cut.Find("#plant-export-button").Click());
        await handler.GraphGetGateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        handler.Current = new PlantGraphDto([Item(kept), Item(Guid.NewGuid())], [], [], []);
        cut.Render(p => p.Add(x => x.Shown, false));
        cut.Render(p => p.Add(x => x.Shown, true));
        Assert.Empty(cut.FindAll("#plant-import-plant-changed"));

        gate.SetResult();
        await exporting;

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#plant-import-plant-changed")));
        Assert.Equal(0, handler.GraphPuts);
    }

    /// <summary>A plant that did NOT change is written at the first "Confirm", with no extra question.</summary>
    [Fact]
    public void AConfirmOnAPlantThatDidNotChangeWritesAtOnce()
    {
        var kept = Guid.NewGuid();
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true);
        handler.Current = new PlantGraphDto([Item(kept)], [new PlantItemGeometryDto(kept, 1, 2, 3, 4, 0)], [], [], "<d/>", [Plc1]);
        Choose(cut, new PlantGraphDto([Item(kept)], [], [], []));

        cut.Find("#plant-import-confirm-button").Click();

        Assert.Equal(1, handler.GraphPuts);
        Assert.Empty(cut.FindAll("#plant-import-plant-changed"));
    }

    /// <summary>
    /// The plant cannot be read again just before writing: nothing is written - it is not known what
    /// the file would replace - and it is said as a refusal, with the dialog still there, not as the
    /// "outcome unknown" warning and reload that a failure DURING the write earns.
    /// </summary>
    [Fact]
    public void APlantThatCannotBeReadAgainAtConfirmWritesNothingAndDoesNotReload()
    {
        var kept = Guid.NewGuid();
        var (cut, handler) = RenderComponent(graph: new PlantGraph(), canEdit: true);
        handler.Current = new PlantGraphDto([Item(kept)], [], [], []);
        Choose(cut, new PlantGraphDto([Item(kept)], [], [], []));
        var navigation = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var reloaded = false;
        navigation.LocationChanged += (_, _) => reloaded = true;

        handler.GetStatus = HttpStatusCode.InternalServerError;
        cut.Find("#plant-import-confirm-button").Click();

        Assert.Equal(0, handler.GraphPuts);
        Assert.False(reloaded);
        Assert.Single(cut.FindAll("#plant-import-confirm"));
        Assert.Contains("Nothing was written", cut.Find("#plant-io-error").TextContent);
    }

    private sealed class GraphHandler : HttpMessageHandler
    {
        public PlantGraphDto Current { get; set; } = new([], [], [], []);

        /// <summary>
        /// The engine's state as the real host's PUT sees it (Task 4c, PlantQueries.ReplaceGraphCoreAsync):
        /// refused in Run for every file; for a file carrying PLC connections, in Recover and Dispose
        /// whatever they are and in Pause when they differ from <see cref="Current"/>'s. Stop by default:
        /// every older test here imports with it stopped.
        /// </summary>
        public NavigatorStatusTypes NavStatus { get; set; } = NavigatorStatusTypes.Stop;

        /// <summary>What the last PUT was answered.</summary>
        public HttpStatusCode? LastPutAnswer { get; private set; }
        public LegacyGeometryImportDto Legacy { get; set; } = new([], 0, 0, 0);
        public List<MissionDto> Missions { get; set; } = [];
        public List<ExtractionQueueItemDto> ExtractionQueue { get; set; } = [];
        /// <summary>Come risponde la PUT del grafo: <c>NoContent</c> e' la riuscita, il resto e' il rifiuto che si vuole provare.</summary>
        public HttpStatusCode PutStatus { get; set; } = HttpStatusCode.NoContent;

        /// <summary>Il corpo del rifiuto: un <c>ProblemDetails</c>, come quello che manda l'host davvero.</summary>
        public string PutBody { get; set; } = string.Empty;

        /// <summary>When set, <c>GET /plant/graph</c> answers this status with <see cref="GetBody"/>: the host failing to give the plant.</summary>
        public HttpStatusCode? GetStatus { get; set; }
        public string GetBody { get; set; } = string.Empty;

        /// <summary>When set, the legacy route answers this status with <see cref="LegacyBody"/>: the host failing to read an old drawing.</summary>
        public HttpStatusCode? LegacyStatus { get; set; }
        public string LegacyBody { get; set; } = string.Empty;

        public int GraphGets { get; private set; }

        /// <summary>Holds the NEXT <c>GET /plant/graph</c> until the test opens it, then disarms itself: the way to keep an export on its way.</summary>
        public TaskCompletionSource? GraphGetGate { get; set; }

        /// <summary>Completed when a read really enters <see cref="GraphGetGate"/>.</summary>
        public TaskCompletionSource GraphGetGateReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int GraphPuts { get; private set; }
        public PlantGraphDto? LastPut { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (request.Method == HttpMethod.Get && path.EndsWith("/plant/graph", StringComparison.Ordinal))
            {
                GraphGets++;
                if (GraphGetGate is { } gate)
                {
                    GraphGetGate = null;
                    GraphGetGateReached.TrySetResult();
                    await gate.Task;
                }
                if (GetStatus is { } failed)
                    return new HttpResponseMessage(failed) { Content = new StringContent(GetBody) };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Current) };
            }

            if (request.Method == HttpMethod.Put && path.EndsWith("/plant/graph", StringComparison.Ordinal))
            {
                GraphPuts++;
                LastPut = await request.Content!.ReadFromJsonAsync<PlantGraphDto>(ct);
                var answer = Answer(LastPut!);
                LastPutAnswer = answer.StatusCode;
                return answer;
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/legacy-geometry", StringComparison.Ordinal))
                return LegacyStatus is { } legacyFailed
                    ? new HttpResponseMessage(legacyFailed) { Content = new StringContent(LegacyBody) }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Legacy) };

            if (request.Method == HttpMethod.Get && path.EndsWith("/missions", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Missions) };

            if (request.Method == HttpMethod.Get && path.EndsWith("/extraction-queue", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(ExtractionQueue) };

            if (request.Method == HttpMethod.Delete && path.EndsWith("/edit-lock", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NoContent);

            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent(string.Empty) };
        }

        private HttpResponseMessage Answer(PlantGraphDto imported)
        {
            // As the real host (owner's decision of 27/9 on Pause): any import only with the engine stopped.
            if (NavStatus != NavigatorStatusTypes.Stop)
                return Refused($"sostituzione dell'impianto: il motore è in {NavStatus}, e un impianto si importa solo a motore fermo (Stop)");
            if (PutStatus != HttpStatusCode.NoContent)
                return new HttpResponseMessage(PutStatus) { Content = new StringContent(PutBody) };
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        private static HttpResponseMessage Refused(string detail) =>
            new(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { title = "Il motore non è fermo", status = 409, detail }) };
    }
}
