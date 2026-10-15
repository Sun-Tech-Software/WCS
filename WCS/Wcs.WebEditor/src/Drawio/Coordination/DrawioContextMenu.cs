using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Drawio.Coordination;

/// <summary>
/// Compito 5, row 63: the entries this Editor adds to draw.io's own right-click menu on a device
/// (drawio-bridge.js, hookThePopupMenu). Properties and Duplicate Device act on the properties
/// panel; Linked Storage Cells and Mission Steps open dedicated non-modal windows; the remaining
/// entries are the panel's device commands (<see cref="Components.DrawioDeviceCommands"/>), offered,
/// like them, only with the engine running.
/// </summary>
public enum DrawioMenuEntry
{
    Properties,
    LinkedStorageCells,
    MissionSteps,

    /// <summary>
    /// Fix round 2: the panel's own "Duplicate device" (DrawioDuplicateDevice), which the entry
    /// arms - the question with its two answers - and brings into view; the answer is the operator's,
    /// in the panel, as for the button.
    /// </summary>
    DuplicateDevice,
    ExecuteStep,
    UnloadLu,
    SetOffLine,
    SetReady,
    StepDone,
}

/// <summary>The four device commands of the panel (row 54), the "Set Off Line"/"Set Ready" button counted as the two commands it sends.</summary>
public enum DrawioDeviceCommand
{
    ExecuteStep,
    UnloadLu,
    SetOffLine,
    SetReady,
    StepDone,
}

public static class DrawioContextMenu
{
    /// <summary>
    /// The entry the bridge names. The names are LITERALS on the other side too (drawio-bridge.js,
    /// addDeviceEntries): renaming one without the other makes that entry answer "not known here",
    /// which the page says rather than doing something else.
    /// </summary>
    public static DrawioMenuEntry? Parse(string? name) => name switch
    {
        "properties" => DrawioMenuEntry.Properties,
        "linked-storage-cells" => DrawioMenuEntry.LinkedStorageCells,
        "mission-steps" => DrawioMenuEntry.MissionSteps,
        "duplicate-device" => DrawioMenuEntry.DuplicateDevice,
        "execute-step" => DrawioMenuEntry.ExecuteStep,
        "unload-lu" => DrawioMenuEntry.UnloadLu,
        "set-off-line" => DrawioMenuEntry.SetOffLine,
        "set-ready" => DrawioMenuEntry.SetReady,
        "step-done" => DrawioMenuEntry.StepDone,
        _ => null,
    };

    /// <summary>The panel's command an entry stands for, or null for an entry that only shows a part of the panel.</summary>
    public static DrawioDeviceCommand? CommandOf(DrawioMenuEntry entry) => entry switch
    {
        DrawioMenuEntry.ExecuteStep => DrawioDeviceCommand.ExecuteStep,
        DrawioMenuEntry.UnloadLu => DrawioDeviceCommand.UnloadLu,
        DrawioMenuEntry.SetOffLine => DrawioDeviceCommand.SetOffLine,
        DrawioMenuEntry.SetReady => DrawioDeviceCommand.SetReady,
        DrawioMenuEntry.StepDone => DrawioDeviceCommand.StepDone,
        _ => null,
    };

    /// <summary>
    /// The elements of the properties panel an entry brings into view, the first one found winning.
    /// The two contextual information entries own windows and therefore have no panel target. Null
    /// for them and for commands.
    /// </summary>
    public static string[]? TargetsOf(DrawioMenuEntry entry) => entry switch
    {
        DrawioMenuEntry.Properties => ["drawio-property-panel"],
        DrawioMenuEntry.DuplicateDevice => ["drawio-duplicate-confirmation"],
        _ => null,
    };

    /// <summary>What an entry that shows a part of the panel is called in the page's own sentences.</summary>
    public static string WhatOf(DrawioMenuEntry entry) => entry switch
    {
        DrawioMenuEntry.Properties => "The properties",
        DrawioMenuEntry.LinkedStorageCells => "The linked storage cells",
        DrawioMenuEntry.MissionSteps => "The mission steps",
        DrawioMenuEntry.DuplicateDevice => "The duplication question",
        _ => entry.ToString(),
    };

    /// <summary>The command the host is sent, and the words the panel uses for it - the button's own label.</summary>
    public static (CommandTypes Type, string Label) Of(DrawioDeviceCommand command) => command switch
    {
        DrawioDeviceCommand.ExecuteStep => (CommandTypes.ItemExecuteStep, "Execute Step"),
        DrawioDeviceCommand.UnloadLu => (CommandTypes.ItemUnloadLu, "Unload LU"),
        DrawioDeviceCommand.SetOffLine => (CommandTypes.ItemSetOffLine, "Set Off Line"),
        DrawioDeviceCommand.SetReady => (CommandTypes.ItemSetOnLine, "Set Ready"),
        DrawioDeviceCommand.StepDone => (CommandTypes.ItemStepDone, "Step Done"),
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
    };
}

/// <summary>
/// A device command chosen from the right-click menu, handed to the panel's commands component for
/// that device (<see cref="Components.DrawioDeviceCommands.MenuRequest"/>), which sends it exactly as
/// its own button would. Taken ONCE: the page keeps the object until something else happens, and a
/// later render - or a new commands component for the same device - must not send it a second time.
/// </summary>
public sealed class DrawioMenuCommand(Guid itemId, DrawioDeviceCommand command)
{
    public Guid ItemId { get; } = itemId;

    public DrawioDeviceCommand Command { get; } = command;

    public bool Taken { get; private set; }

    /// <summary>True the first time only: the caller that gets true is the one that sends it.</summary>
    public bool Take()
    {
        if (Taken) return false;
        Taken = true;
        return true;
    }
}
