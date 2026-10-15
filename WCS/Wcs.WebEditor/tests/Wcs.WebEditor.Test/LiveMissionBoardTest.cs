using Wcs.Api.Contracts;
using Wcs.WebEditor.Live;
using Xunit;

namespace Wcs.WebEditor.Test;

/// <summary>Compito 3, row 60: the active missions as the list holds them - a read, then the channel's differences.</summary>
public sealed class LiveMissionBoardTest
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");
    private static readonly Guid M1 = Guid.Parse("11111111-0000-4000-8000-000000000001");
    private static readonly Guid M2 = Guid.Parse("22222222-0000-4000-8000-000000000002");

    [Fact]
    public void BeforeTheFirstReadEventsAreDropped()
    {
        var board = new LiveMissionBoard();

        Assert.False(board.Apply(TestMissions.Mission(M1, A), MissionChange.Started));
        board.BeginRead();
        board.ReadLanded([]);

        Assert.Empty(board.Rows);
    }

    [Fact]
    public void AfterTheReadEveryChangeIsApplied()
    {
        var board = new LiveMissionBoard();
        board.BeginRead();
        board.ReadLanded([TestMissions.Mission(M1, A)]);

        Assert.True(board.Apply(TestMissions.Mission(M2, B), MissionChange.Started));
        Assert.True(board.Apply(TestMissions.Mission(M1, B), MissionChange.PositionChanged));
        Assert.False(board.Apply(TestMissions.Mission(M1, B), MissionChange.StepChanged));
        Assert.Equal(B, board.Find(M1)!.PositionItemId);

        Assert.True(board.Apply(TestMissions.Mission(M2, B), MissionChange.Completed));
        Assert.True(board.Apply(TestMissions.Mission(M1, B), MissionChange.Removed));
        Assert.False(board.Apply(TestMissions.Mission(M1, B), MissionChange.Removed));
        Assert.Empty(board.Rows);
    }

    /// <summary>The read in flight may predate what the channel says meanwhile: the channel's word goes on top of it, the last one per mission.</summary>
    [Fact]
    public void EventsDuringAReadAreReplayedOnTopOfIt()
    {
        var board = new LiveMissionBoard();
        board.BeginRead();

        Assert.False(board.Apply(TestMissions.Mission(M1, A), MissionChange.StepChanged));
        Assert.False(board.Apply(TestMissions.Mission(M1, B), MissionChange.PositionChanged));
        Assert.False(board.Apply(TestMissions.Mission(M2, A), MissionChange.PositionChanged));
        Assert.False(board.Apply(TestMissions.Mission(M2, A), MissionChange.Completed));
        board.ReadLanded([TestMissions.Mission(M1, A), TestMissions.Mission(M2, B)]);

        Assert.Equal(B, Assert.Single(board.Rows).PositionItemId);
        Assert.Equal(M1, Assert.Single(board.Rows).MissionId);
        Assert.False(board.Reading);
    }

    [Fact]
    public void EventsDuringAFailedReadGoOnTheRowsAlreadyHeld()
    {
        var board = new LiveMissionBoard();
        board.BeginRead();
        board.ReadLanded([TestMissions.Mission(M1, A)]);

        board.BeginRead();
        board.Apply(TestMissions.Mission(M1, B), MissionChange.PositionChanged);
        board.ReadFailed();

        Assert.Equal(B, board.Find(M1)!.PositionItemId);
        Assert.False(board.Reading);
    }

    /// <summary>A first read that fails leaves nothing to put the held events on: they go, and the next read covers them.</summary>
    [Fact]
    public void EventsDuringAFailedFirstReadAreDropped()
    {
        var board = new LiveMissionBoard();
        board.BeginRead();
        board.Apply(TestMissions.Mission(M1, B), MissionChange.Started);
        board.ReadFailed();

        Assert.False(board.HasRead);
        Assert.Empty(board.Rows);
        board.BeginRead();
        board.ReadLanded([]);
        Assert.Empty(board.Rows);
    }
}
