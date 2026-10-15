using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// La coda delle rotte da costruire perde una missione per id (<see cref="RoutesToBuild.Remove"/>):
/// la toglie lo spostamento che le ha dato la rotta subito (<c>MissionGateway.Move</c>), perche' la
/// richiesta rimasta in coda il giro del navigatore la ripescherebbe piu' avanti, da un altro
/// dispositivo. La missione puo' esserci piu' volte (chi accoda non sempre guarda se c'e' gia'), e
/// le altre restano nel loro ordine.
/// </summary>
public sealed class RoutesToBuildRemoveTest : IDisposable
{
    private readonly PathMission _first = new(new Guid("7d3c1a20-0000-0000-0000-000000000001"));
    private readonly PathMission _moved = new(new Guid("7d3c1a20-0000-0000-0000-000000000002"));
    private readonly PathMission _last = new(new Guid("7d3c1a20-0000-0000-0000-000000000003"));

    public RoutesToBuildRemoveTest() => RoutesToBuild.Instance.Items = new();

    public void Dispose() => RoutesToBuild.Instance.Items = new();

    [Fact]
    public void EveryEntryOfTheMissionGoesAndTheOthersKeepTheirOrder()
    {
        RoutesToBuild.Instance.Enqueue(_moved);
        RoutesToBuild.Instance.Enqueue(_first);
        RoutesToBuild.Instance.Enqueue(_moved);
        RoutesToBuild.Instance.Enqueue(_last);

        RoutesToBuild.Instance.Remove(_moved.MissionId);

        Assert.Null(RoutesToBuild.Instance[_moved.MissionId]);
        Assert.Same(_first, RoutesToBuild.Instance[_first.MissionId]);
        Assert.Same(_last, RoutesToBuild.Instance[_last.MissionId]);
        Assert.Same(_first, RoutesToBuild.Instance.Dequeue());
        Assert.Same(_last, RoutesToBuild.Instance.Dequeue());
        Assert.Empty(RoutesToBuild.Instance.Items);
    }

    /// <summary>Tolta, la missione si puo' rimettere in coda come prima, in fondo.</summary>
    [Fact]
    public void ARemovedMissionCanBeQueuedAgain()
    {
        RoutesToBuild.Instance.Enqueue(_moved);
        RoutesToBuild.Instance.Enqueue(_first);
        RoutesToBuild.Instance.Remove(_moved.MissionId);

        RoutesToBuild.Instance.Enqueue(_moved);

        Assert.Same(_moved, RoutesToBuild.Instance[_moved.MissionId]);
        Assert.Same(_first, RoutesToBuild.Instance.Dequeue());
        Assert.Same(_moved, RoutesToBuild.Instance.Dequeue());
        Assert.Empty(RoutesToBuild.Instance.Items);
    }
}
