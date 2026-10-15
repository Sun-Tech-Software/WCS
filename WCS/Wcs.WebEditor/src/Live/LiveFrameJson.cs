using System.Text.Json;
using Wcs.WebEditor.Model;

namespace Wcs.WebEditor.Live;

/// <summary>
/// The frame as <c>drawio-live.js</c> reads it. The plant speaks of items, the drawing of cells: a
/// device drawn in this session carries draw.io's own identifier until the page is reloaded, so the
/// translation is the page's (<c>DrawioSelectionCoordinator.DrawnCellOf</c>), not a Guid's ToString.
/// </summary>
/// <remarks>
/// Queue item 21: every device carries the size its load unit is drawn with, in drawing units
/// (<see cref="LoadUnitSize"/>, from the page's <see cref="PlantGraph.LoadUnitSizeOf"/>). The canvas
/// only draws it: the rule that decides it is the C# one, and there is no second copy in the module.
/// </remarks>
public static class LiveFrameJson
{
    public static string Build(LiveFrame frame, Func<Guid, string> cellOf, Func<Guid, LoadUnitSize> loadUnitSizeOf) =>
        JsonSerializer.Serialize(new
        {
            devices = frame.Devices.Select(device =>
            {
                var size = loadUnitSizeOf(device.ItemId);
                return new
                {
                    cellId = cellOf(device.ItemId),
                    color = device.StatusColor,
                    statusName = device.StatusName,
                    loadUnit = device.LoadUnit,
                    loadUnitWidth = size.Width,
                    loadUnitHeight = size.Height,
                };
            }),
            motions = frame.Motions.Select(motion => new { from = cellOf(motion.From), to = cellOf(motion.To) }),
        });
}
