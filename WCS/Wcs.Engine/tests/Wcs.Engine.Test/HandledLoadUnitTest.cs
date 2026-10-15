using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Le UDC movimentate nello storico mensile: una riga per missione, che nasce con la missione,
/// cambia a ogni setter e si rilegge dal costruttore.
/// </summary>
/// <remarks>
/// Lo storico e' in memoria e non c'e' scrittore: l'ORM scrive direttamente. Il navigatore deve
/// essere in <c>Run</c>, perche' in <c>Recover</c> l'ORM delle UDC non legge ne' scrive.
/// </remarks>
public sealed class HandledLoadUnitTest : IDisposable
{
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;
    private readonly WcsHistoryDatabase _history = WcsHistoryDatabase.InMemory();

    public HandledLoadUnitTest()
    {
        Navigator.History = _history;
        Navigator.NavStatus = NavigatorStatusTypes.Run;
    }

    public void Dispose()
    {
        Navigator.NavStatus = _previousStatus;
        Navigator.History = null;
        _history.Dispose();
    }

    [Fact]
    public void AHandledLoadUnitIsWrittenBySettersAndReadBackByTheConstructor()
    {
        var missionId = Guid.NewGuid();

        var written = new LoadUnit(missionId)
        {
            Type = LoadUnit.LoadUnitTypes.LuExtractionByAssignment,
            Date = new DateTime(2026, 9, 2, 14, 0, 0),
            CellId = 42,
            LuCode = "SSCC-9",
            LuId = 900,
            ProcessId = 77,
            ResultDescription = "estratta"
        };

        using (var context = _history.CreateContext())
        {
            var row = Assert.Single(context.HandledLoadUnits);
            Assert.Equal(missionId, row.MissionId);
            Assert.Equal((short)LoadUnit.LoadUnitTypes.LuExtractionByAssignment, row.Type);
            Assert.Equal(42m, row.CellId);
            Assert.Equal(77m, row.ProcessId);
            Assert.Null(row.NoteHeaderId);
        }

        var read = new LoadUnit(missionId);
        Assert.Equal(written.Type, read.Type);
        Assert.Equal(written.Date, read.Date);
        Assert.Equal(42m, read.CellId);
        Assert.Equal("SSCC-9", read.LuCode);
        Assert.Equal(900m, read.LuId);
        Assert.Equal(77m, read.ProcessId);
        Assert.Equal("estratta", read.ResultDescription);
        Assert.Equal(0m, read.NoteHeaderId);
    }

    [Fact]
    public void ChangingTheTypeLaterKeepsOneRowPerMission()
    {
        var missionId = Guid.NewGuid();
        var handled = new LoadUnit(missionId) { Type = LoadUnit.LoadUnitTypes.LuLoading, LuCode = "A" };

        handled.Type = LoadUnit.LoadUnitTypes.LuLoadedAndRejected;
        handled.ResultDescription = "rigettata";

        using var context = _history.CreateContext();
        var row = Assert.Single(context.HandledLoadUnits);
        Assert.Equal((short)LoadUnit.LoadUnitTypes.LuLoadedAndRejected, row.Type);
        Assert.Equal("rigettata", row.ResultDescription);
    }

    [Fact]
    public void RemovingTakesTheRowAway()
    {
        var missionId = Guid.NewGuid();
        var handled = new LoadUnit(missionId) { Type = LoadUnit.LoadUnitTypes.LuLoading };

        ORM.LoadUnit.Remove(handled);

        using var context = _history.CreateContext();
        Assert.Empty(context.HandledLoadUnits);
    }
}
