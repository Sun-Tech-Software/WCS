using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Checks;
using Wcs.WebEditor.Model;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// La logica pura del "Check Junctions" web (compito 10): separata da <c>ChecksPanel</c> (provato a
/// parte in <c>ChecksTest</c>) come <c>NodePlacement</c> lo e' da <c>PlantCanvas</c> - cosi' ogni
/// caso si prova sul grafo, senza montare un componente.
/// </summary>
public sealed class JunctionChecksTest
{
    private static PlantItemDto Item(Guid id, string? name = null, double? x = 0, double? y = 0) => new(
        id, name, DeviceTypes.Automation, null, null, null, default, default, 0, null, 0, 0, 0, 0,
        default!, X: x, Y: y, Width: 100, Height: 100, Rotation: 0);

    [Fact]
    public void AWellFormedGraphHasNoWarnings()
    {
        var a = Item(Guid.NewGuid());
        var b = Item(Guid.NewGuid());
        var graph = new PlantGraph([a, b], [new JunctionDto(Guid.NewGuid(), a.ItemId, b.ItemId, 0)], []);

        var warnings = JunctionChecks.ComputeWarnings(graph);

        // Zero non e' una prova in se': qui lo e' solo perche' il fixture e' costruito per non avere
        // niente da segnalare su nessuna delle tre verifiche (due dispositivi, collegati fra loro,
        // entrambi collocati) - non perche' il metodo torni sempre vuoto.
        Assert.Empty(warnings);
    }

    /// <summary>
    /// Riproduce il controllo di oggi (<c>FrmEDITOR.Private.cs:60-98</c>): una giunzione il cui
    /// estremo di origine non e' (piu') un dispositivo dell'impianto e' segnalata.
    /// </summary>
    [Fact]
    public void ADanglingSourceIsReported()
    {
        var destination = Item(Guid.NewGuid());
        var missingSource = Guid.NewGuid();
        var graph = new PlantGraph([destination], [new JunctionDto(Guid.NewGuid(), missingSource, destination.ItemId, 0)], []);

        var warnings = JunctionChecks.ComputeWarnings(graph);

        var warning = Assert.Single(warnings, w => w.Kind == JunctionCheckWarningKind.DanglingJunction);
        Assert.Contains(missingSource.ToString(), warning.Message);
    }

    [Fact]
    public void ADanglingDestinationIsReported()
    {
        var source = Item(Guid.NewGuid());
        var missingDestination = Guid.NewGuid();
        var graph = new PlantGraph([source], [new JunctionDto(Guid.NewGuid(), source.ItemId, missingDestination, 0)], []);

        var warnings = JunctionChecks.ComputeWarnings(graph);

        var warning = Assert.Single(warnings, w => w.Kind == JunctionCheckWarningKind.DanglingJunction);
        Assert.Contains(missingDestination.ToString(), warning.Message);
    }

    /// <summary>
    /// Una giunzione scucita su entrambi gli estremi produce due righe distinte, non una sola: chi
    /// legge deve sapere che mancano sia l'origine sia la destinazione, non solo che "qualcosa" non va.
    /// </summary>
    [Fact]
    public void ADanglingJunctionOnBothEndsReportsBothMessages()
    {
        var graph = new PlantGraph([], [new JunctionDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0)], []);

        var warnings = JunctionChecks.ComputeWarnings(graph);

        Assert.Equal(2, warnings.Count(w => w.Kind == JunctionCheckWarningKind.DanglingJunction));
    }

    /// <summary>
    /// Il difetto del controllo di oggi (<c>for (i = 0; i &lt; Nodes.Count - 1; i++)</c>) salta
    /// l'ultimo nodo del modello: una giunzione scucita che capita di essere l'ultima non viene
    /// segnalata. Qui la giunzione scucita e' l'ultima della lista - prova diretta che
    /// <see cref="JunctionChecks.ComputeWarnings"/> non riproduce quel limite: scandisce
    /// <see cref="PlantGraph.Junctions"/> per intero, non fino al penultimo elemento.
    /// </summary>
    [Fact]
    public void ADanglingJunctionThatIsLastInTheListIsStillReported()
    {
        var a = Item(Guid.NewGuid());
        var b = Item(Guid.NewGuid());
        var wellFormed = new JunctionDto(Guid.NewGuid(), a.ItemId, b.ItemId, 0);
        var danglingLast = new JunctionDto(Guid.NewGuid(), a.ItemId, Guid.NewGuid(), 0);
        // L'ordine conta per questo test: la giunzione scucita e' l'ultimo elemento della lista,
        // esattamente il caso che il ciclo "Count - 1" del controllo WinForms lascerebbe fuori.
        var graph = new PlantGraph([a, b], [wellFormed, danglingLast], []);

        var warnings = JunctionChecks.ComputeWarnings(graph);

        Assert.Contains(warnings, w => w.Kind == JunctionCheckWarningKind.DanglingJunction && w.Message.Contains(danglingLast.DestinationItemId.ToString()));

        // La riproduzione fedele del ciclo di oggi (per contrasto, non il codice sotto prova):
        // "for (i = 0; i < list.Count - 1; i++)" applicata alla stessa lista di giunzioni salterebbe
        // proprio l'ultimo elemento, e quindi non troverebbe questo caso - e' la dimostrazione che il
        // limite del ciclo di oggi, se copiato qui, spegnerebbe questo assert.
        var junctions = graph.Junctions;
        var itemIds = graph.Items.Select(i => i.ItemId).ToHashSet();
        var foundWithTodaysOffByOne = false;
        for (var i = 0; i < junctions.Count - 1; i++)
        {
            var j = junctions[i];
            if (!itemIds.Contains(j.SourceItemId) || !itemIds.Contains(j.DestinationItemId))
                foundWithTodaysOffByOne = true;
        }
        Assert.False(foundWithTodaysOffByOne, "il fixture e' costruito apposta perche' il ciclo di oggi non trovi niente - la riga sopra prova che il metodo vero, senza quel limite, la trova comunque");
    }

    /// <summary>Nuovo rispetto al controllo di oggi (proposto dal brief): un dispositivo senza nessuna giunzione, ne' entrante ne' uscente.</summary>
    [Fact]
    public void AnItemWithNoJunctionsAtAllIsReportedAsIsolated()
    {
        var isolated = Item(Guid.NewGuid());
        var a = Item(Guid.NewGuid());
        var b = Item(Guid.NewGuid());
        var graph = new PlantGraph([isolated, a, b], [new JunctionDto(Guid.NewGuid(), a.ItemId, b.ItemId, 0)], []);

        var warnings = JunctionChecks.ComputeWarnings(graph);

        var warning = Assert.Single(warnings, w => w.Kind == JunctionCheckWarningKind.IsolatedItem);
        Assert.Contains(isolated.ItemId.ToString(), warning.Message);
    }

    /// <summary>Un dispositivo con solo una giunzione uscente (mai come destinazione) non e' isolato: basta una direzione sola.</summary>
    [Fact]
    public void AnItemWithOnlyAnOutgoingJunctionIsNotIsolated()
    {
        var a = Item(Guid.NewGuid());
        var b = Item(Guid.NewGuid());
        var graph = new PlantGraph([a, b], [new JunctionDto(Guid.NewGuid(), a.ItemId, b.ItemId, 0)], []);

        var warnings = JunctionChecks.ComputeWarnings(graph);

        Assert.DoesNotContain(warnings, w => w.Kind == JunctionCheckWarningKind.IsolatedItem);
    }

    /// <summary>Nuovo rispetto al controllo di oggi (proposto dal brief): un dispositivo senza posizione (X o Y nulli, come PlantGraph.IsPlaced li riconosce).</summary>
    [Fact]
    public void AnUnplacedItemIsReported()
    {
        var unplaced = Item(Guid.NewGuid(), x: null, y: null);
        var other = Item(Guid.NewGuid());
        // Collegato (non isolato), ma non collocato: il fixture prova le due verifiche separate -
        // senza una giunzione, un dispositivo non collocato sarebbe anche isolato per la verifica 2,
        // e l'assert sotto non distinguerebbe piu' quale delle due segnalazioni prova davvero.
        var graph = new PlantGraph([unplaced, other], [new JunctionDto(Guid.NewGuid(), unplaced.ItemId, other.ItemId, 0)], []);

        var warnings = JunctionChecks.ComputeWarnings(graph);

        var warning = Assert.Single(warnings, w => w.Kind == JunctionCheckWarningKind.UnplacedItem);
        Assert.Contains(unplaced.ItemId.ToString(), warning.Message);

        // La verifica 2 (isolamento) guarda solo le giunzioni, non se il dispositivo e' disegnabile:
        // collegato com'e', non e' isolato anche se non e' collocato - le due segnalazioni sono
        // indipendenti, non l'una conseguenza dell'altra.
        Assert.DoesNotContain(warnings, w => w.Kind == JunctionCheckWarningKind.IsolatedItem);
    }

    [Fact]
    public void AnItemWithoutANameIsReportedByItsRawId()
    {
        var unplaced = Item(Guid.NewGuid(), name: null, x: null, y: null);
        var graph = new PlantGraph([unplaced], [], []);

        var warnings = JunctionChecks.ComputeWarnings(graph);

        Assert.Contains(warnings, w => w.Message.Contains(unplaced.ItemId.ToString()));
    }
}
