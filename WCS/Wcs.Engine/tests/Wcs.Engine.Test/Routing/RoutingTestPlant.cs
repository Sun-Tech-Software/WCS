using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test.Routing;

/// <summary>
/// Un impianto in memoria per i test della ricerca dei percorsi: dispositivi con un nome corto,
/// giunzioni, missioni a bordo e step pianificati, e la pulizia dei singleton che il motore
/// condivide fra tutti i test. Lo strato ORM non scrive perche' <c>Navigator.Store</c> e' nullo
/// (nessun database in prova); il navigatore sta in <c>Recover</c> perche' cosi' <c>PathBuilder</c>
/// non inizializza i filtri e i setter della missione non hanno effetti collaterali.
/// </summary>
internal sealed class RoutingTestPlant : IDisposable
{
    private readonly NavigatorStatusTypes _previousStatus = Navigator.NavStatus;
    private readonly Dictionary<string, AutomationDevice> _byName = new();
    private readonly Dictionary<Guid, string> _byId = new();

    public RoutingTestPlant(NavigatorStatusTypes status = NavigatorStatusTypes.Recover)
    {
        Clear();
        Navigator.NavStatus = status;
    }

    public AutomationDevice this[string name] => _byName[name];

    public IEnumerable<string> DeviceNames => _byName.Keys;

    public string NameOf(Guid? id) => id is { } value && _byId.TryGetValue(value, out var name) ? name : "?";

    public AutomationDevice Device(string name, DeviceTypes type = DeviceTypes.Automation, int level = 0)
    {
        var device = TestDevices.WithRow(Guid.NewGuid(), d =>
        {
            d.DeviceType = type;
            d.GroupRoutePathId = level;
        });
        PathDevices.Instance.Add(device);
        _byName[name] = device;
        _byId[device.ItemGuid] = name;
        return device;
    }

    public AutomationJunction Link(string from, string to, int priority = 99)
    {
        var junction = new AutomationJunction(_byName[from].ItemGuid, _byName[to].ItemGuid) { Priority = priority };
        PathJunctions.Instance.Add(junction);
        return junction;
    }

    /// <summary>Una fila: ogni nome e' collegato al successivo.</summary>
    public void Chain(params string[] names)
    {
        for (var i = 0; i + 1 < names.Length; i++)
            Link(names[i], names[i + 1]);
    }

    /// <summary>Una missione a bordo di <paramref name="position"/> diretta a <paramref name="destination"/>, senza rotta.</summary>
    public PathMission MissionAt(string position, string? destination)
    {
        var mission = new PathMission { PositionItemId = _byName[position].ItemGuid };
        if (destination != null) mission.DestinationItemId = _byName[destination].ItemGuid;
        ActiveMissions.Instance.Add(mission, buildRoute: false);
        return mission;
    }

    /// <summary>Uno step gia' pianificato di <paramref name="mission"/>, come lo lascerebbe BuildPathToDestination.</summary>
    public MissionStep PlannedStep(PathMission mission, string from, string to)
    {
        var step = new MissionStep
        {
            StepId = Guid.NewGuid(),
            MissionId = mission.MissionId,
            Date = DateTime.Now,
            FromNodeId = _byName[from].ItemGuid,
            ToNodeId = _byName[to].ItemGuid,
            FromGroupRoutePath = _byName[from].GroupRoutePathId
        };
        MissionSteps.Instance.Add(step);
        return step;
    }

    /// <summary>La rotta come la costruisce il motore: prima senza, poi con i dispositivi fuori linea.</summary>
    public List<MissionStep> Route(PathMission mission, bool offlinePass = true)
    {
        var steps = new PathBuilder(mission).GetRouteSteps();
        if ((steps == null || steps.Count == 0) && offlinePass)
            steps = new PathBuilder(mission).GetRouteSteps(true);
        return steps ?? [];
    }

    /// <summary>I nomi toccati dalla rotta: la partenza del primo step e l'arrivo di ognuno.</summary>
    public string[] Names(List<MissionStep> steps)
    {
        if (steps.Count == 0) return [];
        var names = new List<string> { NameOf(steps[0].FromNodeId) };
        names.AddRange(steps.Select(s => NameOf(s.ToNodeId)));
        return [.. names];
    }

    private static void Clear()
    {
        // The changes that wait for the end of a step (item 54) are static too: their applications end
        // here, on the plant they were asked on, and none is left for the next test.
        Xunit.Assert.True(Wcs.Engine.Api.PendingMissionChanges.WhenIdleAsync().Wait(TimeSpan.FromSeconds(30)));
        Wcs.Engine.Api.PendingMissionChanges.Reset();

        ActiveMissions.Instance.RemoveAll(_ => true);
        MissionSteps.Instance.RemoveAll(_ => true);
        PathJunctions.Instance.RemoveAll(_ => true);
        PathDevices.Instance.Clear();

        // Le celle da segnare piene sono statiche come le collezioni: una prova che le lascia farebbe
        // chiamare il gestionale finto della prossima al primo giro delle rotte in Run.
        CellsToMarkFull.Forget();
    }

    public void Dispose()
    {
        // Non serve rilasciare i dispositivi qui: PathDevices.Clear() prende l'elenco prima di
        // svuotare e chiama Dispose su ognuno (voce B25 del registro).
        Clear();
        Navigator.NavStatus = _previousStatus;
    }
}
