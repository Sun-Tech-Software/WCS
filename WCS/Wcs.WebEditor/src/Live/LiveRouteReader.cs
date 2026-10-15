using System.Text.Json;
using Wcs.Api.Client;
using Wcs.WebEditor.Model;

namespace Wcs.WebEditor.Live;

/// <summary>
/// The two reads behind the hover route, as the WinForms Editor made them: at every hover, because
/// the route can change. An answer that arrives after the mouse has moved on is thrown away.
/// </summary>
public sealed class LiveRouteReader(Func<WcsClient> client, Func<PlantGraph?> graph)
{
    private long _version;

    public async Task<LiveRouteView?> ReadAsync(Guid itemId, bool loaded)
    {
        var version = Interlocked.Increment(ref _version);
        if (!loaded) return null;

        try
        {
            var mission = (await client().GetMissionsAsync(itemId: itemId)).FirstOrDefault();
            if (mission is null || version != Volatile.Read(ref _version)) return null;
            var steps = await client().GetMissionStepsAsync(mission.MissionId);
            if (version != Volatile.Read(ref _version)) return null;

            var items = graph()?.Items ?? [];
            return LiveRoute.Of(itemId, mission, steps, id => items.FirstOrDefault(item => item.ItemId == id));
        }
        catch (Exception ex) when (ex is HttpRequestException or WcsApiException or TaskCanceledException or JsonException
                                       or ObjectDisposedException)
        {
            // A failed read draws nothing and bothers nobody: the next hover asks again. The
            // ObjectDisposedException is the WcsClient disposed with its session while a read was on
            // its way (the same finding as in PlantLiveFeed's snapshot).
            return null;
        }
    }

    /// <summary>The mouse left: whatever is still on its way is for a hover that is over.</summary>
    public void Forget() => Interlocked.Increment(ref _version);
}
