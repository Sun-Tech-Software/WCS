using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Model;

namespace Wcs.WebEditor.Checks;

/// <summary>
/// I tre motivi per cui una riga di <see cref="JunctionCheckWarning"/> puo' comparire (compito 10).
/// <see cref="DanglingJunction"/> e' quello che il "Check Junctions" di oggi verifica davvero
/// (<c>WCS/Wcs.Editor/src/FrmEDITOR.Private.cs:60-98</c>): per ogni giunzione del diagramma, che
/// l'estremo di origine e quello di destinazione siano entrambi dispositivi veri. <see cref="IsolatedItem"/>
/// e <see cref="UnplacedItem"/> sono proposti dal brief di questo compito come aggiunte - il codice
/// di oggi non li verifica, e non vanno letti come una riproduzione fedele.
/// </summary>
public enum JunctionCheckWarningKind
{
    DanglingJunction,
    IsolatedItem,
    UnplacedItem,
}

/// <summary>
/// <paramref name="TargetId"/> (compito B2b): l'identificativo dell'elemento incriminato, non
/// ricavato analizzando <paramref name="Message"/> - un identificativo di giunzione per
/// <see cref="JunctionCheckWarningKind.DanglingJunction"/>, di dispositivo per le altre due. Nullo
/// non e' previsto oggi (ogni ramo di <see cref="JunctionChecks.ComputeWarnings"/> lo valorizza), ma
/// resta annullabile: un chiamante che costruisse un avviso senza un elemento preciso non deve
/// rompere la compilazione.
/// </summary>
public sealed record JunctionCheckWarning(JunctionCheckWarningKind Kind, string Message, Guid? TargetId = null);

/// <summary>
/// Il "Check Junctions" dell'Editor web (compito 10): a differenza di quello di oggi
/// (<c>btnCheckJunctions_ItemClick</c>) non chiama l'host - legge il grafo gia' caricato nella
/// pagina (<see cref="PlantGraph"/>), la stessa fonte che disegna la tela.
/// </summary>
public static class JunctionChecks
{
    /// <summary>
    /// Calcola le righe del controllo. Tre verifiche, non una sola - si veda il commento di
    /// <see cref="JunctionCheckWarningKind"/> per quale delle tre riproduce il controllo di oggi.
    /// </summary>
    /// <remarks>
    /// Il controllo di oggi scandisce <c>RouteEditorManager.View.Model.Nodes</c> con
    /// <c>for (i = 0; i &lt; Nodes.Count - 1; i++)</c>: l'ultimo nodo del modello non viene mai
    /// esaminato, quindi una giunzione scucita che capita di essere l'ultima non viene segnalata.
    /// Qui non c'e' un elenco unico "nodi" da cui l'ultimo possa restare fuori per un confine
    /// sbagliato: si scandiscono per intero <see cref="PlantGraph.Junctions"/> (verifica 1) e
    /// <see cref="PlantGraph.Items"/> (verifiche 2 e 3), senza nessun <c>-1</c> sul limite del ciclo -
    /// il difetto del ciclo di oggi non ha un punto equivalente da riprodurre qui.
    /// </remarks>
    public static IReadOnlyList<JunctionCheckWarning> ComputeWarnings(PlantGraph graph)
    {
        var warnings = new List<JunctionCheckWarning>();
        var itemIds = graph.Items.Select(i => i.ItemId).ToHashSet();

        // 1. Giunzioni scucite: riproduce il controllo di oggi. "Tag non nullo" nell'Editor WinForms
        // significa "il nodo Syncfusion porta un dispositivo vero" - qui l'equivalente e' che
        // l'identificativo di origine/destinazione della giunzione sia fra i dispositivi
        // dell'impianto. Una giunzione i cui estremi esistono ma non sono collocati NON e' qui dentro
        // (l'identificativo c'e' davvero, solo la geometria manca): resta comunque invisibile sulla
        // tela (PlantCanvas.TryBuildEdge scarta un estremo non collocato), ma per una ragione diversa
        // da una giunzione scucita - si veda il commento di ChecksPanel.OnHighlightedItemIdsChanged
        // per l'esclusiva dichiarata.
        foreach (var junction in graph.Junctions)
        {
            if (!itemIds.Contains(junction.SourceItemId))
                warnings.Add(new JunctionCheckWarning(JunctionCheckWarningKind.DanglingJunction,
                    $"Junction {junction.JunctionId}: its source device {junction.SourceItemId} is not (or no longer) in the plant.",
                    junction.JunctionId));

            if (!itemIds.Contains(junction.DestinationItemId))
                warnings.Add(new JunctionCheckWarning(JunctionCheckWarningKind.DanglingJunction,
                    $"Junction {junction.JunctionId}: its destination device {junction.DestinationItemId} is not (or no longer) in the plant.",
                    junction.JunctionId));
        }

        // 2. NUOVO rispetto al controllo di oggi (proposto dal brief): dispositivi che non compaiono
        // ne' come origine ne' come destinazione di nessuna giunzione.
        var connectedItemIds = graph.Junctions
            .SelectMany(j => new[] { j.SourceItemId, j.DestinationItemId })
            .ToHashSet();
        foreach (var item in graph.Items.Where(i => !connectedItemIds.Contains(i.ItemId)))
            warnings.Add(new JunctionCheckWarning(JunctionCheckWarningKind.IsolatedItem,
                $"Device {DisplayName(item)}: no junction goes in or out.", item.ItemId));

        // 3. NUOVO rispetto al controllo di oggi (proposto dal brief): dispositivi non collocati -
        // la stessa regola che PlantCanvas.UnplacedItems mostra gia' a margine della tela
        // (PlantGraph.IsPlaced), non una seconda definizione di "collocato".
        foreach (var item in graph.Items.Where(i => !PlantGraph.IsPlaced(i)))
            warnings.Add(new JunctionCheckWarning(JunctionCheckWarningKind.UnplacedItem,
                $"Device {DisplayName(item)}: not placed.", item.ItemId));

        return warnings;
    }

    private static string DisplayName(PlantItemDto item) => item.Name ?? item.ItemId.ToString();
}
