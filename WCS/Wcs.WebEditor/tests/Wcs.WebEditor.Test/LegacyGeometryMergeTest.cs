using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Io;
using Xunit;

namespace Wcs.WebEditor.Test;

/// <summary>
/// Quello che un vecchio disegno <c>.edd</c>/<c>.xml</c> diventa quando si versa nello stesso
/// percorso dell'importazione nativa (compito 12, punto 3 del brief).
/// </summary>
public sealed class LegacyGeometryMergeTest
{
    private static PlantItemConfigDto Item(Guid id) => new() { ItemId = id };
    private static PlantItemGeometryDto Unplaced(Guid id) => new(id, null, null, null, null, null);

    [Fact]
    public void AKnownGuidGetsItsNewPosition()
    {
        var a = Guid.NewGuid();
        var current = new PlantGraphDto([Item(a)], [Unplaced(a)], [], []);
        var legacy = new LegacyGeometryImportDto([new PlantItemGeometryDto(a, 10, 20, 30, 40, 0)], 0, 0, 0);

        var (proposed, unknownGuids, itemsNotMentioned) = LegacyGeometryMerge.Apply(current, legacy);

        var geometry = Assert.Single(proposed.Geometries);
        Assert.Equal(10, geometry.X);
        Assert.Equal(0, unknownGuids);
        Assert.Equal(0, itemsNotMentioned);
    }

    /// <summary>
    /// Correzione Importante 6 della revisione: il verbale mostrato all'utente non diceva quanti
    /// dispositivi dell'impianto di ADESSO il disegno non nomina affatto - il numero che questa
    /// importazione merita piu' della migrazione, perche' sono proprio quelli che
    /// <see cref="LegacyGeometryMerge.Apply"/> lascia intatti in silenzio.
    /// </summary>
    [Fact]
    public void ItemsTheDrawingDoesNotMentionAreCounted()
    {
        var mentioned = Guid.NewGuid(); var untouched1 = Guid.NewGuid(); var untouched2 = Guid.NewGuid();
        var current = new PlantGraphDto([Item(mentioned), Item(untouched1), Item(untouched2)],
            [Unplaced(mentioned), Unplaced(untouched1), Unplaced(untouched2)], [], []);
        var legacy = new LegacyGeometryImportDto([new PlantItemGeometryDto(mentioned, 1, 1, 1, 1, 0)], 0, 0, 0);

        var (_, _, itemsNotMentioned) = LegacyGeometryMerge.Apply(current, legacy);

        Assert.Equal(2, itemsNotMentioned);
    }

    /// <summary>
    /// Un Guid del disegno che il disegno non menziona affatto mantiene la posizione che aveva -
    /// il disegno non dice "non e' collocato", semplicemente non ne parla. E' diverso da un Guid che
    /// il disegno menziona ma senza nessun riquadro leggibile (quello e' GuidsWithoutGeometry, gia'
    /// contato da DiagramGeometryReader lato host, non da questa classe).
    /// </summary>
    [Fact]
    public void ADeviceNotMentionedInTheDrawingKeepsItsCurrentPosition()
    {
        var a = Guid.NewGuid(); var untouched = Guid.NewGuid();
        var current = new PlantGraphDto([Item(a), Item(untouched)],
            [new PlantItemGeometryDto(a, 1, 1, 1, 1, 0), new PlantItemGeometryDto(untouched, 77, 88, 1, 1, 0)], [], []);
        var legacy = new LegacyGeometryImportDto([new PlantItemGeometryDto(a, 10, 20, 30, 40, 0)], 0, 0, 0);

        var (proposed, _, _) = LegacyGeometryMerge.Apply(current, legacy);

        var kept = proposed.Geometries.Single(g => g.ItemId == untouched);
        Assert.Equal(77, kept.X);
        Assert.Equal(88, kept.Y);
    }

    /// <summary>Devices, giunzioni e mappature restano quelli di adesso: un vecchio disegno non porta abbastanza per ricrearli.</summary>
    [Fact]
    public void ItemsJunctionsAndDoorMappingsAreUntouched()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var junctions = new[] { new JunctionDto(Guid.NewGuid(), a, b, 1) };
        var mappings = new[] { new DoorMappingDto(1, a, "P1", 10, "A", 0, false) };
        var current = new PlantGraphDto([Item(a), Item(b)], [Unplaced(a), Unplaced(b)], junctions, mappings);
        var legacy = new LegacyGeometryImportDto([], 0, 0, 0);

        var (proposed, _, _) = LegacyGeometryMerge.Apply(current, legacy);

        Assert.Same(current.Items, proposed.Items);
        Assert.Same(current.Junctions, proposed.Junctions);
        Assert.Same(current.DoorMappings, proposed.DoorMappings);
    }

    /// <summary>Un Guid del disegno che non corrisponde a nessun dispositivo di adesso non puo' diventare un dispositivo nuovo (manca il resto della sua configurazione): resta contato, non applicato.</summary>
    [Fact]
    public void AGuidNotKnownToTheCurrentPlantIsCountedButNotApplied()
    {
        var known = Guid.NewGuid(); var unknown = Guid.NewGuid();
        var current = new PlantGraphDto([Item(known)], [Unplaced(known)], [], []);
        var legacy = new LegacyGeometryImportDto(
            [new PlantItemGeometryDto(known, 1, 1, 1, 1, 0), new PlantItemGeometryDto(unknown, 2, 2, 2, 2, 0)], 0, 0, 0);

        var (proposed, unknownGuids, _) = LegacyGeometryMerge.Apply(current, legacy);

        Assert.Equal(1, unknownGuids);
        Assert.Single(proposed.Items); // nessun dispositivo nuovo comparso
        Assert.DoesNotContain(proposed.Geometries, g => g.ItemId == unknown);
    }

    /// <summary>
    /// Un vecchio <c>.edd</c>/<c>.xml</c> non parla del disegno libero, e la proposta che ne esce
    /// non deve parlarne nemmeno lei: <c>Decoration</c> a nullo, non quella dell'impianto di adesso
    /// che il <c>with</c> si porterebbe dietro da <paramref name="current"/>. Scriverla di nuovo
    /// sarebbe innocuo - e' lo stesso valore - ma la finestra di conferma direbbe "il disegno viene
    /// sostituito con quello del file", di un file che un disegno non ce l'ha.
    /// </summary>
    [Fact]
    public void ALegacyDrawingDoesNotSpeakAboutTheFreeDrawing()
    {
        var known = Guid.NewGuid();
        var current = new PlantGraphDto([Item(known)], [Unplaced(known)], [], [], "<mxCell id=\"a-mano\"/>");

        var (proposed, _, _) = LegacyGeometryMerge.Apply(current, new LegacyGeometryImportDto([], 0, 0, 0));

        Assert.Null(proposed.Decoration);
    }

    [Fact]
    public void ALegacyDrawingDoesNotSpeakAboutPlcConnections()
    {
        var known = Guid.NewGuid();
        var current = new PlantGraphDto([Item(known)], [Unplaced(known)], [], [], null,
            [new PlcConnectionDto(1, "current", null, null, null, null)]);

        var (proposed, _, _) = LegacyGeometryMerge.Apply(current,
            new LegacyGeometryImportDto([], 0, 0, 0));

        Assert.Null(proposed.PlcConnections);
    }
}
