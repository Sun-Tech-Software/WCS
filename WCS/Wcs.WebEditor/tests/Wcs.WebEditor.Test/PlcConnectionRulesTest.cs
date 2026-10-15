using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Io;
using Xunit;

namespace Wcs.WebEditor.Test;

/// <summary>
/// Compito 4, row 62: the ONE check of a list of PLC connections, shared by the grid of the draw.io
/// page and by the import (ImportExport) - the WinForms checks (FrmPCM_PLCS_LIST_FormClosing: an Id
/// greater than zero, and not repeated) and the host's own graph check (GraphEndpoints.Validate: the
/// same two, plus a device's connection reference) - and the comparison the two confirmations count.
/// </summary>
public sealed class PlcConnectionRulesTest
{
    private static PlcConnectionDto Connection(long id, string? description = null) =>
        new(id, description, "10.0.0.1", 2011, "10.0.0.2", 2012);

    [Fact]
    public void AListWithPositiveUniqueIdsHasNoProblem()
    {
        Assert.Empty(PlcConnectionRules.Problems([Connection(1), Connection(2), Connection(30)]));
    }

    /// <summary>
    /// Every offending row is named, not only the first: the grid marks them all. The WinForms order is
    /// kept - FrmPCM_PLCS_LIST checks the Ids below one BEFORE the repeated ones - so the first problem,
    /// the one the import words, is the one the WinForms would have shown.
    /// </summary>
    [Fact]
    public void EveryRowWithAnIdBelowOneAndEveryRepeatedRowIsNamedInTheWinFormsOrder()
    {
        var problems = PlcConnectionRules.Problems([Connection(7), Connection(0), Connection(7), Connection(-1), Connection(3)]);

        Assert.Equal(
        [
            new PlcConnectionProblem(1, 0, PlcConnectionProblemKind.IdNotPositive),
            new PlcConnectionProblem(3, -1, PlcConnectionProblemKind.IdNotPositive),
            new PlcConnectionProblem(0, 7, PlcConnectionProblemKind.IdRepeated),
            new PlcConnectionProblem(2, 7, PlcConnectionProblemKind.IdRepeated),
        ], problems);
    }

    /// <summary>Two rows below one are both "not positive", and never also "repeated": the WinForms stops at the first check.</summary>
    [Fact]
    public void TwoRowsWithTheSameIdBelowOneAreNotAlsoRepeated()
    {
        var problems = PlcConnectionRules.Problems([Connection(-1), Connection(-1)]);

        Assert.All(problems, problem => Assert.Equal(PlcConnectionProblemKind.IdNotPositive, problem.Kind));
        Assert.Equal(2, problems.Count);
    }

    [Theory]
    [InlineData(0, true, 0)]
    [InlineData(3, true, 3)]
    [InlineData(-1, false, 0)]
    [InlineData(2.5, false, 0)]
    public void ADevicesConnectionReferenceIsAWholeNumberNotBelowZero(double reference, bool valid, long connectionId)
    {
        Assert.Equal(valid, PlcConnectionRules.TryReadReference((decimal)reference, out var read));
        if (valid) Assert.Equal(connectionId, read);
    }

    [Fact]
    public void AReferenceBeyondTheLargestIdIsNotValid()
    {
        Assert.False(PlcConnectionRules.TryReadReference((decimal)long.MaxValue + 1, out _));
    }

    /// <summary>By Id, as the host replaces them (PlcSettingsStore.ReplaceConnectionsAsync): an Id changed is one removed and one added.</summary>
    [Fact]
    public void TheChangesAreNamedByIdentifier()
    {
        var current = new[] { Connection(1, "same"), Connection(2, "before"), Connection(3, "removed"), Connection(5, "renumbered") };
        var proposed = new[] { Connection(6, "renumbered"), Connection(1, "same"), Connection(2, "after"), Connection(4, "added") };

        var changes = PlcConnectionRules.Compare(current, proposed);

        Assert.Equal([4L, 6L], changes.Added);
        Assert.Equal([3L, 5L], changes.Removed);
        Assert.Equal([2L], changes.Modified);
        Assert.False(changes.None);
        Assert.True(PlcConnectionRules.Compare(current, current).None);
    }
}
