using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Editing;
using Wcs.WebEditor.Model;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// L'annulla (compito 6): <see cref="CommandStack"/> da sola (pila generica, senza toccare
/// l'host - <see cref="FakeCommand"/> sotto), e <see cref="MoveItemsCommand"/>, il primo
/// <see cref="EditCommand"/>, contro un <see cref="WcsClient"/> vero appoggiato su un gestore HTTP
/// finto condiviso con <c>DragTest</c> e <c>UndoTest</c> (<c>GeometryCountingHandler.cs</c>):
/// non bUnit, perche' niente qui tocca <c>PlantCanvas</c> o il DOM - la parte di interfaccia
/// (pulsante, Ctrl+Z, permesso) e' provata a parte in <c>UndoTest</c>.
/// </summary>
public sealed class CommandStackTest
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static PlantItemDto Item(Guid id, double x, double y, Guid geometryVersion = default) => new(
        id, "x", DeviceTypes.Automation, null, null, null, default, default, 0, null, 0, 0, 0, 0,
        default!, X: x, Y: y, Width: 100, Height: 100, Rotation: 0)
    {
        GeometryVersion = geometryVersion
    };

    private static WcsClient NewClient(GeometryCountingHandler handler) =>
        new(new WcsClientOptions { BaseAddress = new Uri("http://localhost:5580") }, handler);

    /// <summary>Un comando finto per provare <see cref="CommandStack"/> senza scrivere sull'host: registra in <see cref="Log"/> ogni rifacimento/annullamento riuscito, e puo' fallire l'uno o l'altro a comando.</summary>
    private sealed record FakeCommand(int Id, List<string> Log, bool FailApply = false, bool FailUndo = false) : EditCommand
    {
        public override string Description => $"Fake {Id}";

        public override Task ApplyAsync(WcsClient client, PlantGraph graph)
        {
            if (FailApply) throw new WcsApiException(HttpStatusCode.Locked, "il permesso e' di qualcun altro");
            Log.Add($"apply {Id}");
            return Task.CompletedTask;
        }

        public override Task UndoAsync(WcsClient client, PlantGraph graph)
        {
            if (FailUndo) throw new WcsApiException(HttpStatusCode.Locked, "il permesso e' di qualcun altro");
            Log.Add($"undo {Id}");
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Come <see cref="FakeCommand"/>, ma <see cref="UndoAsync"/> richiama una volta sola (la guardia
    /// <c>_reentered</c> impedisce una ricorsione infinita) <see cref="Reentrant"/> **prima** di
    /// registrare il proprio annullamento - riproduce, senza vero multithreading, due annulla
    /// sovrapposti che leggono lo stesso comando in cima prima che il primo l'abbia tolto dalla pila.
    /// Un vero data race con <c>TaskCompletionSource</c> e due <c>Task</c> non attesi e' stato
    /// scartato: farebbe girare le due continuazioni su thread del pool diversi, corrompendo la
    /// <c>List&lt;T&gt;</c> interna di <c>CommandStack</c> (non pensata per essere condivisa fra
    /// thread) in un modo che non e' lo scenario descritto dalla revisione - il circuito Blazor e' a
    /// thread singolo, non davvero parallelo. La rientranza sincrona riproduce la stessa cosa
    /// (l'ordine di lettura prima della rimozione) in modo deterministico.
    /// </summary>
    private sealed record ReentrantFakeCommand(int Id, List<string> Log, Func<Task> Reentrant) : EditCommand
    {
        private bool _reentered;

        public override string Description => $"Fake {Id}";

        public override Task ApplyAsync(WcsClient client, PlantGraph graph)
        {
            Log.Add($"apply {Id}");
            return Task.CompletedTask;
        }

        public override async Task UndoAsync(WcsClient client, PlantGraph graph)
        {
            if (!_reentered)
            {
                _reentered = true;
                await Reentrant();
            }

            Log.Add($"undo {Id}");
        }
    }

    /// <summary>
    /// Come <see cref="ReentrantFakeCommand"/>, ma la rientranza e' in <see cref="ApplyAsync"/>, non
    /// in <see cref="UndoAsync"/> - per riprodurre due rifai sovrapposti sullo STESSO comando in cima
    /// a <c>_redoStack</c>, non due annulla. <see cref="ApplyAsync"/> gira anche alla prima
    /// <see cref="CommandStack.DoAsync"/> (quella che mette il comando in pila la prima volta): non
    /// deve rientrare li', o il rientro accadrebbe prima ancora che il test annulli e rifaccia
    /// qualcosa. Solo dalla SECONDA <see cref="ApplyAsync"/> (quella di
    /// <see cref="CommandStack.RedoAsync"/>, dopo un annulla) scatta il rientro.
    /// </summary>
    private sealed record ReentrantOnRedoFakeCommand(int Id, List<string> Log, Func<Task> Reentrant) : EditCommand
    {
        private int _applyCount;
        private bool _reentered;

        public override string Description => $"Fake {Id}";

        public override async Task ApplyAsync(WcsClient client, PlantGraph graph)
        {
            _applyCount++;
            if (_applyCount == 2 && !_reentered)
            {
                _reentered = true;
                await Reentrant();
            }

            Log.Add($"apply {Id}");
        }

        public override Task UndoAsync(WcsClient client, PlantGraph graph)
        {
            Log.Add($"undo {Id}");
            return Task.CompletedTask;
        }
    }

    private static readonly PlantGraph DummyGraph = new();
    private static readonly WcsClient DummyClient = new(new WcsClientOptions());

    [Fact]
    public async Task UndoOnAnEmptyStackDoesNothingAndDoesNotThrow()
    {
        var stack = new CommandStack();

        await stack.UndoAsync(DummyClient, DummyGraph);

        Assert.False(stack.CanUndo);
        Assert.Equal(0, stack.Count);
    }

    [Fact]
    public async Task ACommandEntersTheStackOnlyAfterApplySucceeds()
    {
        var stack = new CommandStack();
        var log = new List<string>();
        var failing = new FakeCommand(1, log, FailApply: true);

        await Assert.ThrowsAsync<WcsApiException>(() => stack.DoAsync(failing, DummyClient, DummyGraph));

        // La scrittura e' fallita: non e' entrato in pila, non c'e' niente da annullare.
        Assert.Equal(0, stack.Count);
        Assert.False(stack.CanUndo);
    }

    [Fact]
    public async Task ACommandLeavesTheStackOnlyAfterUndoSucceeds()
    {
        var stack = new CommandStack();
        var log = new List<string>();
        var failsOnUndo = new FakeCommand(1, log, FailUndo: true);
        await stack.DoAsync(failsOnUndo, DummyClient, DummyGraph);
        Assert.Equal(1, stack.Count);

        await Assert.ThrowsAsync<WcsApiException>(() => stack.UndoAsync(DummyClient, DummyGraph));

        // L'annulla e' fallito: il comando resta al suo posto, un tentativo successivo puo' riprovarlo.
        Assert.Equal(1, stack.Count);
        Assert.True(stack.CanUndo);
    }

    /// <summary>
    /// <c>CommandStack.UndoAsync</c> leggeva l'ultimo comando,
    /// aspettava l'host, e SOLO DOPO lo toglieva per indice (<c>RemoveAt(Count-1)</c>). Due annulla
    /// che si sovrappongono - qui riprodotti con <see cref="ReentrantFakeCommand"/>, che richiama un
    /// secondo <c>stack.UndoAsync</c> da dentro il proprio, prima di completare - leggono entrambi lo
    /// stesso comando in cima prima che il primo l'abbia tolto: con la rimozione per indice il secondo
    /// <c>RemoveAt</c> toglierebbe chi si trova in cima allora, cioe' il comando sotto ("bottom") -
    /// mai annullato (il suo <see cref="EditCommand.UndoAsync"/> non viene mai invocato) ma sparito
    /// dalla pila comunque. Con la rimozione per identita' il secondo tentativo di togliere lo stesso
    /// oggetto non trova piu' niente e non fa nulla: "bottom" resta al suo posto.
    /// </summary>
    [Fact]
    public async Task OverlappingUndoesRemoveTheCommandByIdentityNotByIndexSoTheOlderCommandSurvives()
    {
        var stack = new CommandStack();
        var log = new List<string>();
        var bottom = new FakeCommand(1, log);
        await stack.DoAsync(bottom, DummyClient, DummyGraph);

        var top = new ReentrantFakeCommand(2, log, () => stack.UndoAsync(DummyClient, DummyGraph));
        await stack.DoAsync(top, DummyClient, DummyGraph);

        // Un solo annulla dall'esterno: "top" ne richiama internamente un secondo, sullo stesso
        // thread, prima di completare il proprio - la stessa sovrapposizione di lettura che due
        // Ctrl+Z ravvicinati produrrebbero, senza un vero secondo thread.
        await stack.UndoAsync(DummyClient, DummyGraph);

        // "bottom" non e' mai stato annullato (nel log compare solo "undo 2", due volte - una per
        // ognuna delle due chiamate annidate a top.UndoAsync - mai "undo 1") ma con la rimozione per
        // indice sparirebbe comunque dalla pila. Con la rimozione per identita' resta al suo posto,
        // ancora annullabile.
        Assert.DoesNotContain("undo 1", log);
        Assert.Equal(1, stack.Count);
        Assert.True(stack.CanUndo);
    }

    /// <summary>
    /// Il gemello, sul verso del rifai, del test sopra: <c>CommandStack.RedoAsync</c> aveva
    /// <c>_undoStack.Add(command)</c> fuori dalla guardia <c>if (index >= 0)</c> - la stessa riga
    /// gemella che il test sopra prova per <c>UndoAsync</c>. Due rifai che si sovrappongono sullo
    /// STESSO comando in cima a <c>_redoStack</c> - qui riprodotti con
    /// <see cref="ReentrantOnRedoFakeCommand"/>, che richiama un secondo <c>stack.RedoAsync</c> da
    /// dentro il proprio <c>ApplyAsync</c>, prima che il primo abbia tolto il comando da
    /// <c>_redoStack</c> - leggono entrambi lo stesso comando in cima. Il rientrante lo trova ancora
    /// li', lo toglie e lo rimette in <c>_undoStack</c>; il primo, quando riprende, non lo trova piu'
    /// (<c>index &lt; 0</c>). Senza la guardia sull'<c>Add</c>, il primo lo rimetterebbe comunque in
    /// <c>_undoStack</c> una seconda volta - lo stesso oggetto due volte in pila, un annulla
    /// successivo lo annullerebbe due volte.
    /// </summary>
    [Fact]
    public async Task OverlappingRedoesAddTheCommandToUndoOnlyOnceEvenWhenTheyRaceOnTheSameTopOfRedo()
    {
        var stack = new CommandStack();
        var log = new List<string>();
        var solo = new ReentrantOnRedoFakeCommand(1, log, () => stack.RedoAsync(DummyClient, DummyGraph));

        await stack.DoAsync(solo, DummyClient, DummyGraph); // ApplyAsync #1: nessun rientro
        await stack.UndoAsync(DummyClient, DummyGraph); // ora in _redoStack, non piu' in _undoStack

        // Un solo rifai dall'esterno: "solo" ne richiama internamente un secondo, sullo stesso
        // thread, prima di completare il proprio - la stessa sovrapposizione di lettura che due
        // Ctrl+Y ravvicinati produrrebbero, senza un vero secondo thread.
        await stack.RedoAsync(DummyClient, DummyGraph); // ApplyAsync #2: rientra qui

        // "solo" e' stato applicato TRE volte in tutto: una per il DoAsync iniziale, due per la
        // corsa (la chiamata rientrante che completa per prima, e quella esterna che riprende dopo
        // di lei) - il rifai duplicato sull'host e' un difetto diverso, gia' dichiarato non
        // dimostrato raggiungibile oggi (i default di SignalR serializzano le invocazioni). Quello
        // che questo test prova e' l'invariante della pila, non quanto l'host viene chiamato: con la
        // guardia, "solo" finisce UNA volta sola in _undoStack, non due.
        Assert.Equal(3, log.Count(l => l == "apply 1"));
        Assert.Equal(1, stack.Count);
        Assert.Single(UndoStackOf(stack));
        Assert.False(stack.CanRedo);
    }

    /// <summary>Legge il campo privato <c>_undoStack</c> per verificare per riferimento (non per valore, non tramite l'API pubblica) che cosa resta davvero in pila - serve esattamente questo, perche' due comandi uguali per valore rendono <see cref="CommandStack.UndoDescription"/> e <see cref="CommandStack.Count"/> ciechi a quale dei due sia sopravvissuto.</summary>
    private static IReadOnlyList<EditCommand> UndoStackOf(CommandStack stack) =>
        (IReadOnlyList<EditCommand>)typeof(CommandStack)
            .GetField("_undoStack", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(stack)!;

    /// <summary>
    /// Il commento di <c>CommandStack.UndoAsync</c> promette una rimozione "per
    /// identita'", ma <c>EditCommand</c> e' un record - <c>List&lt;T&gt;.Remove(command)</c> usa
    /// l'Equals sintetizzato, che confronta i campi per VALORE. Oggi coincide con l'identita' solo
    /// perche' i due campi di <see cref="MoveItemsCommand"/> sono <c>List&lt;T&gt;</c> allocate nuove
    /// a ogni gesto (non implementano <c>IEquatable&lt;T&gt;</c>, quindi il confronto degenera nel
    /// riferimento) - una proprieta' accidentale che nessun commento cita. Qui la coincidenza viene
    /// rotta di proposito: due <see cref="FakeCommand"/> con lo stesso <c>Id</c> e lo stesso
    /// riferimento a <c>log</c> sono uguali per valore (<c>Assert.Equal</c> sotto lo prova) ma sono
    /// due istanze distinte, messe in pila una dopo l'altra. Un solo annulla toglie quella in cima
    /// (<c>top</c>): con la rimozione per riferimento resta <c>bottom</c>, con
    /// <c>List&lt;T&gt;.Remove(command)</c> (uguaglianza per valore) sarebbe stata tolta <c>bottom</c>
    /// - la voce piu' vecchia fra le due uguali, mai annullata - lasciando <c>top</c> in pila al suo
    /// posto: esattamente il difetto (annullare il comando sbagliato) che questo test impedisce si
    /// riapra.
    /// </summary>
    [Fact]
    public async Task UndoRemovesTheExactInstanceJustUndoneEvenWhenAnEqualByValueCommandSitsBelowIt()
    {
        var stack = new CommandStack();
        var log = new List<string>();
        var bottom = new FakeCommand(1, log);
        var top = new FakeCommand(1, log);

        // Precondizione del test: uguali per valore, ma non lo stesso oggetto - altrimenti non
        // proverebbe niente di diverso dal caso di oggi (MoveItemsCommand, dove i campi differiscono
        // sempre per riferimento).
        Assert.Equal(bottom, top);
        Assert.False(ReferenceEquals(bottom, top));

        await stack.DoAsync(bottom, DummyClient, DummyGraph);
        await stack.DoAsync(top, DummyClient, DummyGraph);

        await stack.UndoAsync(DummyClient, DummyGraph); // annulla l'ultimo messo in pila: "top"

        var remaining = Assert.Single(UndoStackOf(stack));
        Assert.Same(bottom, remaining);
        Assert.Equal(1, stack.Count);
        Assert.True(stack.CanUndo);
    }

    /// <summary>
    /// Il tetto della pila (compito 6, dichiarato in <c>CommandStack.MaxDepth</c> = 50): oltre quello
    /// si scarta il comando piu' vecchio, non un altro qualsiasi. Il numero e' ripetuto qui perche'
    /// il campo e' privato - se <c>MaxDepth</c> cambia in futuro senza aggiornare questo test, il
    /// test smette di provare il tetto vero (fallisce, non finge di passare): e' il prezzo
    /// dell'accoppiamento a una costante non esposta, preferito a inventare un modo di leggerla da
    /// fuori solo per questo test.
    /// </summary>
    [Fact]
    public async Task PastTheCapTheOldestCommandIsDiscarded()
    {
        const int cap = 50;
        var stack = new CommandStack();
        var log = new List<string>();

        for (var i = 0; i < cap + 1; i++)
            await stack.DoAsync(new FakeCommand(i, log), DummyClient, DummyGraph);

        // cap+1 rifacimenti, ma solo cap restano annullabili: il primo (id 0) e' stato scartato.
        Assert.Equal(cap, stack.Count);

        for (var i = 0; i < cap; i++)
            await stack.UndoAsync(DummyClient, DummyGraph);

        Assert.False(stack.CanUndo);

        // L'ordine di annullamento e' LIFO da cap fino a 1 - id 0 non compare mai: se il tetto avesse
        // scartato un comando diverso dal piu' vecchio, id 0 sarebbe qui dentro.
        var undone = log.Where(entry => entry.StartsWith("undo", StringComparison.Ordinal)).ToList();
        var expected = Enumerable.Range(1, cap).Reverse().Select(i => $"undo {i}").ToList();
        Assert.Equal(expected, undone);
    }

    [Fact]
    public async Task UndoingAMoveRestoresTheGeometryFromBeforeNotTheOriginNorNull()
    {
        var id = Guid.NewGuid();
        var originalVersion = Guid.NewGuid();
        var handler = new GeometryCountingHandler();
        var client = NewClient(handler);
        var graph = new PlantGraph([Item(id, x: 130, y: 70, originalVersion)], [], []);

        // "Prima" e' un punto arbitrario, diverso sia dall'origine (0,0) sia da un valore che un
        // annulla scritto male (per esempio "rimetti zero", o "non toccare niente") produrrebbe.
        var before = new PlantItemGeometryDto(id, X: 130, Y: 70, Width: 100, Height: 100, Rotation: 0);
        var after = new PlantItemGeometryDto(id, X: 460, Y: 260, Width: 100, Height: 100, Rotation: 0);
        var stack = new CommandStack();

        await stack.DoAsync(new MoveItemsCommand([before], [after]), client, graph);
        Assert.Equal(460, graph.Items.Single(i => i.ItemId == id).X);
        var appliedVersion = graph.Items.Single(i => i.ItemId == id).GeometryVersion;
        Assert.NotEqual(originalVersion, appliedVersion);

        await stack.UndoAsync(client, graph);

        var restored = graph.Items.Single(i => i.ItemId == id);
        Assert.Equal(130, restored.X);
        Assert.Equal(70, restored.Y);
        Assert.False(stack.CanUndo);

        Assert.Equal(2, handler.SingleGeometryRequests);
        Assert.Equal(0, handler.BulkGeometryRequests);

        var applyBody = JsonSerializer.Deserialize<PlantItemGeometryUpdateDto>(handler.SingleBodies[0], JsonOptions)!;
        Assert.Equal(originalVersion, applyBody.ExpectedGeometryVersion);
        var undoBody = JsonSerializer.Deserialize<PlantItemGeometryUpdateDto>(handler.SingleBodies[1], JsonOptions)!;
        Assert.Equal(130, undoBody.X);
        Assert.Equal(70, undoBody.Y);
        Assert.Equal(appliedVersion, undoBody.ExpectedGeometryVersion);
        Assert.Equal(handler.ConfirmedVersions[id], restored.GeometryVersion);
    }

    /// <summary>Come il compito 5 per la scrittura originale: annullare uno spostamento di piu' d'un dispositivo e' una PUT sola, non una per dispositivo.</summary>
    [Fact]
    public async Task UndoingAGroupMoveSendsOneBulkPutNotOnePerDevice()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var handler = new GeometryCountingHandler();
        var client = NewClient(handler);
        var graph = new PlantGraph([Item(idA, x: 0, y: 0), Item(idB, x: 300, y: 0)], [], []);

        var before = new List<PlantItemGeometryDto>
        {
            new(idA, 0, 0, 100, 100, 0),
            new(idB, 300, 0, 100, 100, 0),
        };
        var after = new List<PlantItemGeometryDto>
        {
            new(idA, 60, -40, 100, 100, 0),
            new(idB, 360, -40, 100, 100, 0),
        };
        var stack = new CommandStack();
        await stack.DoAsync(new MoveItemsCommand(before, after), client, graph);
        Assert.Equal(1, handler.BulkGeometryRequests); // il rifacimento

        await stack.UndoAsync(client, graph);

        // Il rifacimento e l'annulla, entrambi di gruppo - nessuna PUT singola.
        Assert.Equal(2, handler.BulkGeometryRequests);
        Assert.Equal(0, handler.SingleGeometryRequests);

        var sent = JsonSerializer.Deserialize<List<PlantItemGeometryUpdateDto>>(handler.BulkBodies[1], JsonOptions)!;
        Assert.Equal(2, sent.Count);
        Assert.Contains(sent, g => g.ItemId == idA && g.X == 0 && g.Y == 0);
        Assert.Contains(sent, g => g.ItemId == idB && g.X == 300 && g.Y == 0);

        var restoredA = graph.Items.Single(i => i.ItemId == idA);
        var restoredB = graph.Items.Single(i => i.ItemId == idB);
        Assert.Equal(0, restoredA.X);
        Assert.Equal(300, restoredB.X);
    }

    /// <summary>
    /// Il commento della classe dichiarava che <c>Before.Count == After.Count</c>
    /// era garantito dal chiamante (<c>PlantCanvas.OnMouseUp</c> costruisce le due liste dalla stessa
    /// selezione), ma il costruttore pubblico non lo imponeva - un chiamante futuro con liste di
    /// lunghezza diversa sarebbe passato inosservato, mandando l'annulla sulla rotta sbagliata (la
    /// scelta singola/di gruppo guarda una sola delle due liste alla volta).
    /// </summary>
    [Fact]
    public void ConstructingWithMismatchedBeforeAndAfterCountsThrows()
    {
        var before = new List<PlantItemGeometryDto> { new(Guid.NewGuid(), 0, 0, 100, 100, 0) };
        var after = new List<PlantItemGeometryDto>
        {
            new(Guid.NewGuid(), 0, 0, 100, 100, 0),
            new(Guid.NewGuid(), 300, 0, 100, 100, 0),
        };

        Assert.Throws<ArgumentException>(() => new MoveItemsCommand(before, after));
    }

    /// <summary>
    /// La stessa regola del compito 5, ora sull'annulla: se l'host rifiuta
    /// la scrittura del ripristino, il modello locale non cambia - <c>MoveItemsCommand</c> chiama
    /// <c>PlantGraph.ApplyGeometry</c> solo dopo la scrittura, mai prima - e il comando resta in pila
    /// (provato gia' in <see cref="ACommandLeavesTheStackOnlyAfterUndoSucceeds"/> con un comando
    /// finto; qui con il comando vero, contro il client vero).
    /// </summary>
    [Fact]
    public async Task WhenTheHostRejectsTheUndoTheModelDoesNotChangeAndTheCommandStaysInTheStack()
    {
        var id = Guid.NewGuid();
        var handler = new GeometryCountingHandler();
        var client = NewClient(handler);
        var graph = new PlantGraph([Item(id, x: 130, y: 70)], [], []);

        var before = new PlantItemGeometryDto(id, X: 130, Y: 70, Width: 100, Height: 100, Rotation: 0);
        var after = new PlantItemGeometryDto(id, X: 460, Y: 260, Width: 100, Height: 100, Rotation: 0);
        var stack = new CommandStack();
        await stack.DoAsync(new MoveItemsCommand([before], [after]), client, graph);

        handler.ResponseStatus = HttpStatusCode.Locked; // 423: il permesso perso nel frattempo

        await Assert.ThrowsAsync<WcsApiException>(() => stack.UndoAsync(client, graph));

        var unchanged = graph.Items.Single(i => i.ItemId == id);
        Assert.Equal(460, unchanged.X); // ancora la posizione "dopo": l'annulla non e' passato
        Assert.Equal(260, unchanged.Y);
        Assert.True(stack.CanUndo);
        Assert.Equal(1, stack.Count);
    }

    [Fact]
    public async Task AGroupMoveSendsEachItemsCurrentVersionAndStoresEachConfirmation()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var versionA = Guid.NewGuid();
        var versionB = Guid.NewGuid();
        var handler = new GeometryCountingHandler();
        var graph = new PlantGraph(
            [Item(idA, 0, 0, versionA), Item(idB, 300, 0, versionB)], [], []);
        var command = new MoveItemsCommand(
            [new(idA, 0, 0, 100, 100, 0), new(idB, 300, 0, 100, 100, 0)],
            [new(idA, 10, 20, 100, 100, 0), new(idB, 310, 20, 100, 100, 0)]);

        await command.ApplyAsync(NewClient(handler), graph);

        var sent = JsonSerializer.Deserialize<List<PlantItemGeometryUpdateDto>>(
            Assert.Single(handler.BulkBodies), JsonOptions)!;
        Assert.Contains(sent, update => update.ItemId == idA && update.ExpectedGeometryVersion == versionA);
        Assert.Contains(sent, update => update.ItemId == idB && update.ExpectedGeometryVersion == versionB);
        Assert.Equal(handler.ConfirmedVersions[idA], graph.Items.Single(item => item.ItemId == idA).GeometryVersion);
        Assert.Equal(handler.ConfirmedVersions[idB], graph.Items.Single(item => item.ItemId == idB).GeometryVersion);
    }

    [Fact]
    public async Task AGeometryConflictLeavesTheGraphAndCommandStackUnchanged()
    {
        var id = Guid.NewGuid();
        var version = Guid.NewGuid();
        var handler = new GeometryCountingHandler
        {
            ResponseStatus = HttpStatusCode.Conflict,
            ConflictItemIds = [id]
        };
        var graph = new PlantGraph([Item(id, 12, 34, version)], [], []);
        var stack = new CommandStack();
        var command = new MoveItemsCommand(
            [new(id, 12, 34, 100, 100, 0)],
            [new(id, 90, 91, 100, 100, 0)]);

        var conflict = await Assert.ThrowsAsync<GeometryConflictException>(() =>
            stack.DoAsync(command, NewClient(handler), graph));

        Assert.Equal([id], conflict.ItemIds);
        var unchanged = Assert.Single(graph.Items);
        Assert.Equal(12, unchanged.X);
        Assert.Equal(version, unchanged.GeometryVersion);
        Assert.False(stack.CanUndo);
        Assert.Equal(0, stack.Count);
    }

    [Fact]
    public async Task WritingGeometryForAnItemMissingFromTheLocalGraphFailsBeforeSendingAnything()
    {
        var missingId = Guid.NewGuid();
        var handler = new GeometryCountingHandler();
        var graph = new PlantGraph();
        var command = new MoveItemsCommand(
            [new(missingId, 0, 0, 100, 100, 0)],
            [new(missingId, 10, 10, 100, 100, 0)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            command.ApplyAsync(NewClient(handler), graph));

        Assert.Equal(0, handler.SingleGeometryRequests);
        Assert.Equal(0, handler.BulkGeometryRequests);
    }

    // --- Il rifai (compito B4) --------------------------------------------------------------

    [Fact]
    public async Task RedoOnAnEmptyStackDoesNothingAndDoesNotThrow()
    {
        var stack = new CommandStack();

        await stack.RedoAsync(DummyClient, DummyGraph);

        Assert.False(stack.CanRedo);
        Assert.Null(stack.RedoDescription);
    }

    [Fact]
    public async Task UndoingACommandMakesItAvailableToRedoAndRedoingItReappliesIt()
    {
        var log = new List<string>();
        var stack = new CommandStack();
        var command = new FakeCommand(1, log);

        await stack.DoAsync(command, DummyClient, DummyGraph);
        Assert.False(stack.CanRedo);

        await stack.UndoAsync(DummyClient, DummyGraph);
        Assert.True(stack.CanRedo);
        Assert.Equal("Fake 1", stack.RedoDescription);
        Assert.False(stack.CanUndo);

        await stack.RedoAsync(DummyClient, DummyGraph);
        Assert.Equal(["apply 1", "undo 1", "apply 1"], log);
        Assert.True(stack.CanUndo);
        Assert.False(stack.CanRedo);
    }

    /// <summary>
    /// La regola di ogni editor con annulla/rifai: un comando nuovo dopo un annulla apre un ramo di
    /// storia diverso, e il rifai lasciato in sospeso non deve restare raggiungibile - rimetterebbe
    /// uno stato che il comando nuovo ha gia' superato.
    /// </summary>
    [Fact]
    public async Task ANewCommandAfterAnUndoClearsTheRedoStack()
    {
        var log = new List<string>();
        var stack = new CommandStack();

        await stack.DoAsync(new FakeCommand(1, log), DummyClient, DummyGraph);
        await stack.UndoAsync(DummyClient, DummyGraph);
        Assert.True(stack.CanRedo);

        await stack.DoAsync(new FakeCommand(2, log), DummyClient, DummyGraph);

        Assert.False(stack.CanRedo);
        Assert.Null(stack.RedoDescription);
    }

    /// <summary>
    /// Un comando che riesce la prima volta (per finire in pila e poi nel rifai tramite un annulla)
    /// e fallisce dalla seconda in poi: serve a provare che un rifai rifiutato dall'host lascia il
    /// comando ancora disponibile per un secondo tentativo, la stessa regola gia' provata per
    /// l'annulla in <see cref="WhenTheHostRejectsTheUndoTheModelDoesNotChangeAndTheCommandStaysInTheStack"/>,
    /// qui sul verso opposto del gesto.
    /// </summary>
    private sealed record FlakyOnSecondApplyCommand(List<string> Log) : EditCommand
    {
        private int _applyCount;

        public override string Description => "Flaky";

        public override Task ApplyAsync(WcsClient client, PlantGraph graph)
        {
            _applyCount++;
            if (_applyCount > 1) throw new WcsApiException(HttpStatusCode.Locked, "il permesso e' di qualcun altro");
            Log.Add("apply");
            return Task.CompletedTask;
        }

        public override Task UndoAsync(WcsClient client, PlantGraph graph)
        {
            Log.Add("undo");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task WhenTheHostRejectsARedoTheCommandStaysAvailableToRedoAgain()
    {
        var log = new List<string>();
        var stack = new CommandStack();
        var command = new FlakyOnSecondApplyCommand(log);

        await stack.DoAsync(command, DummyClient, DummyGraph);
        await stack.UndoAsync(DummyClient, DummyGraph);
        Assert.True(stack.CanRedo);

        await Assert.ThrowsAsync<WcsApiException>(() => stack.RedoAsync(DummyClient, DummyGraph));

        Assert.True(stack.CanRedo);
        Assert.False(stack.CanUndo);
        Assert.Equal(["apply", "undo"], log);
    }
}
