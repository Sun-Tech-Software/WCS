using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Io;
using Xunit;

namespace Wcs.WebEditor.Test;

/// <summary>
/// Il confronto che il compito 12 mostra prima di sostituire l'impianto ("nel testo si dice che
/// cosa va perso. Contalo davvero"): quattro numeri, ciascuno provato per conto suo.
/// </summary>
public sealed class PlantGraphDiffTest
{
    private static PlantItemConfigDto Item(Guid id) => new() { ItemId = id };
    private static PlantItemGeometryDto Placed(Guid id, double x = 1, double y = 2) => new(id, x, y, 10, 10, 0);
    private static PlantItemGeometryDto Unplaced(Guid id) => new(id, null, null, null, null, null);

    private static PlantGraphDto Empty() => new([], [], [], []);

    [Fact]
    public void TwoIdenticalGraphsLoseNothing()
    {
        var a = Guid.NewGuid();
        var graph = new PlantGraphDto([Item(a)], [Placed(a)],
            [new JunctionDto(Guid.NewGuid(), a, a, 1)],
            [new DoorMappingDto(1, a, "P1", 10, "A", 0, false)]);

        var diff = PlantGraphDiff.Compare(graph, graph);

        Assert.Equal(new PlantGraphDiff(0, 0, 0, 0), diff);
        Assert.False(diff.IsDestructive);
    }

    [Fact]
    public void ADeviceMissingFromTheProposedGraphIsCountedAsLost()
    {
        var kept = Guid.NewGuid();
        var removed = Guid.NewGuid();
        var current = new PlantGraphDto([Item(kept), Item(removed)], [], [], []);
        var proposed = new PlantGraphDto([Item(kept)], [], [], []);

        var diff = PlantGraphDiff.Compare(current, proposed);

        Assert.Equal(1, diff.LostItems);
        Assert.True(diff.IsDestructive);
    }

    /// <summary>Le giunzioni si confrontano per la coppia origine/destinazione, non per JunctionId: un'importazione ricrea ogni giunzione con un Guid nuovo, e quella non e' una perdita.</summary>
    [Fact]
    public void ASurvivingJunctionWithADifferentJunctionIdIsNotCountedAsLost()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var current = new PlantGraphDto([], [], [new JunctionDto(Guid.NewGuid(), a, b, 1)], []);
        var proposed = new PlantGraphDto([], [], [new JunctionDto(Guid.NewGuid(), a, b, 9)], []);

        var diff = PlantGraphDiff.Compare(current, proposed);

        Assert.Equal(0, diff.LostJunctions);
    }

    [Fact]
    public void AJunctionWhoseSourceOrDestinationChangesIsCountedAsLost()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        var current = new PlantGraphDto([], [], [new JunctionDto(Guid.NewGuid(), a, b, 1)], []);
        var proposed = new PlantGraphDto([], [], [new JunctionDto(Guid.NewGuid(), a, c, 1)], []);

        var diff = PlantGraphDiff.Compare(current, proposed);

        Assert.Equal(1, diff.LostJunctions);
    }

    /// <summary>Le mappature si confrontano per (dispositivo, codice porta): lo stesso principio delle giunzioni, l'Id della riga viene dallo store e non porta significato per chi importa.</summary>
    [Fact]
    public void AMappingWithADifferentRowIdButTheSameItemAndDoorCodeIsNotLost()
    {
        var a = Guid.NewGuid();
        var current = new PlantGraphDto([], [], [], [new DoorMappingDto(1, a, "P1", 10, "A", 0, false)]);
        var proposed = new PlantGraphDto([], [], [], [new DoorMappingDto(2, a, "P1", 10, "A", 0, false)]);

        var diff = PlantGraphDiff.Compare(current, proposed);

        Assert.Equal(0, diff.LostMappings);
    }

    /// <summary>Spostare un dispositivo non e' "perderlo": solo un dispositivo collocato che smette di esserlo (o sparisce) conta.</summary>
    [Fact]
    public void MovingAPlacedDeviceIsNotCountedAsALostGeometry()
    {
        var a = Guid.NewGuid();
        var current = new PlantGraphDto([Item(a)], [Placed(a, x: 1)], [], []);
        var proposed = new PlantGraphDto([Item(a)], [Placed(a, x: 999)], [], []);

        var diff = PlantGraphDiff.Compare(current, proposed);

        Assert.Equal(0, diff.LostGeometries);
    }

    [Fact]
    public void ADeviceThatBecomesUnplacedIsCountedAsALostGeometry()
    {
        var a = Guid.NewGuid();
        var current = new PlantGraphDto([Item(a)], [Placed(a)], [], []);
        var proposed = new PlantGraphDto([Item(a)], [Unplaced(a)], [], []);

        var diff = PlantGraphDiff.Compare(current, proposed);

        Assert.Equal(1, diff.LostGeometries);
    }

    /// <summary>Un dispositivo mai collocato che resta mai collocato non e' una perdita: non c'era niente da perdere.</summary>
    [Fact]
    public void AnUnplacedDeviceStayingUnplacedIsNotLost()
    {
        var a = Guid.NewGuid();
        var current = new PlantGraphDto([Item(a)], [Unplaced(a)], [], []);
        var proposed = new PlantGraphDto([Item(a)], [], [], []);

        var diff = PlantGraphDiff.Compare(current, proposed);

        Assert.Equal(0, diff.LostGeometries);
    }

    /// <summary>
    /// Correzione minore collegata all'Importante 8 della revisione: IsPlaced guardava solo X e Y,
    /// quindi una geometria che perdeva larghezza e altezza ma teneva le coordinate non veniva
    /// contata - mentre il riquadro di conferma promette "confrontandoli per intero".
    /// </summary>
    [Fact]
    public void ADeviceThatKeepsItsCoordinatesButLosesWidthAndHeightIsCountedAsALostGeometry()
    {
        var a = Guid.NewGuid();
        var current = new PlantGraphDto([Item(a)], [Placed(a)], [], []);
        var proposed = new PlantGraphDto([Item(a)], [new PlantItemGeometryDto(a, 1, 2, null, null, 0)], [], []);

        var diff = PlantGraphDiff.Compare(current, proposed);

        Assert.Equal(1, diff.LostGeometries);
    }

    [Fact]
    public void EverythingMissingFromAnEmptyProposedGraphIsCountedInEveryColumn()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var current = new PlantGraphDto([Item(a), Item(b)], [Placed(a)],
            [new JunctionDto(Guid.NewGuid(), a, b, 1)],
            [new DoorMappingDto(1, a, "P1", 10, "A", 0, false)]);

        var diff = PlantGraphDiff.Compare(current, Empty());

        Assert.Equal(new PlantGraphDiff(2, 1, 1, 1), diff);
    }

    [Fact]
    public void PlcConnectionsAreCountedAsAddedRemovedOrModifiedByIdentifier()
    {
        var unchanged = new PlcConnectionDto(1, "same", "10.0.0.1", 2001, null, null);
        var current = new PlantGraphDto([], [], [], [], null,
        [
            unchanged,
            new PlcConnectionDto(2, "before", "10.0.0.2", 2002, null, null),
            new PlcConnectionDto(3, "removed", null, null, null, null)
        ]);
        var proposed = new PlantGraphDto([], [], [], [], null,
        [
            unchanged,
            new PlcConnectionDto(2, "after", "10.0.0.2", 2002, null, null),
            new PlcConnectionDto(4, "added", null, null, null, null)
        ]);

        var diff = PlantGraphDiff.Compare(current, proposed);

        Assert.Equal(1, diff.AddedPlcConnections);
        Assert.Equal(1, diff.RemovedPlcConnections);
        Assert.Equal(1, diff.ModifiedPlcConnections);
    }

    [Fact]
    public void AnOldBackupWithoutPlcConnectionsReportsNoPlcChanges()
    {
        var current = new PlantGraphDto([], [], [], [], null,
            [new PlcConnectionDto(1, "current", null, null, null, null)]);
        var oldBackup = new PlantGraphDto([], [], [], []);

        var diff = PlantGraphDiff.Compare(current, oldBackup);

        Assert.Equal(0, diff.AddedPlcConnections);
        Assert.Equal(0, diff.RemovedPlcConnections);
        Assert.Equal(0, diff.ModifiedPlcConnections);
    }
}
