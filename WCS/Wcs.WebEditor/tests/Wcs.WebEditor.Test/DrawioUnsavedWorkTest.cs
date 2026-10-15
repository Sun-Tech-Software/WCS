using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawio;
using Xunit;

namespace Wcs.WebEditor.Test;

public sealed class DrawioUnsavedWorkTest
{
    private static DrawioDeltaResult Delta(
        int moves = 0, int deleted = 0, int newDevices = 0, int newJunctions = 0, int deletedJunctions = 0, int priorities = 0,
        int refusals = 0, int notices = 0) =>
        new(
            [.. Enumerable.Range(0, moves).Select(_ => new PlantItemGeometryDto(Guid.NewGuid(), 0, 0, 1, 1, 0))],
            [.. Enumerable.Range(0, deleted).Select(_ => Guid.NewGuid())],
            [.. Enumerable.Range(0, newDevices).Select(i => new DrawioCell($"c{i}", null, null, "", null, 0, 0, 1, 1, 0, false, false, null, null, ""))],
            [.. Enumerable.Range(0, newJunctions).Select(i => new DrawioNewJunction($"e{i}", Guid.NewGuid(), Guid.NewGuid(), 0))],
            [.. Enumerable.Range(0, deletedJunctions).Select(_ => Guid.NewGuid())],
            [.. Enumerable.Range(0, priorities).Select(_ => new DrawioJunctionPriority(Guid.NewGuid(), 1))],
            [.. Enumerable.Range(0, refusals).Select(i => new DrawioRefusal($"r{i}", "no"))],
            [.. Enumerable.Range(0, notices).Select(i => new DrawioNotice($"n{i}", "note"))],
            "<decoration/>");

    [Fact]
    public void NothingToWriteIsNothingUnsaved() => Assert.Null(DrawioUnsavedWork.Describe(Delta()));

    [Fact]
    public void RefusalsAndNoticesDoNotHoldTheEngineBack() =>
        Assert.Null(DrawioUnsavedWork.Describe(Delta(refusals: 2, notices: 3)));

    [Fact]
    public void EveryKindOfWriteIsNamed()
    {
        var said = DrawioUnsavedWork.Describe(Delta(moves: 2, deleted: 1, newDevices: 1, newJunctions: 1, deletedJunctions: 1, priorities: 1));

        Assert.NotNull(said);
        Assert.Contains("2 moved or resized device(s)", said);
        Assert.Contains("1 deleted device(s)", said);
        Assert.Contains("1 new device(s)", said);
        Assert.Contains("1 new junction(s)", said);
        Assert.Contains("1 deleted junction(s)", said);
        Assert.Contains("1 junction priority change(s)", said);
        Assert.Contains("the engine was NOT started", said);
    }
}
