using System.Globalization;
using Wcs.Api.Contracts.Dtos;

namespace Wcs.WebEditor.Drawio;

/// <summary>
/// The name a device is born with when a shape is dropped on the draw.io canvas: the <b>type's
/// label plus the first free number</b> - <c>Automation 1</c>, <c>Automation 2</c>,
/// <c>Buffer 1</c> - so that a device created by a drag is never mute.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a name at all</b> (owner's decision of 2026-09-17). Until it, a dropped shape created a
/// device with no name, like <c>/editor</c> does. During the session the shape still showed the
/// TYPE's label, so nothing looked wrong; at the next reopening <c>DrawioDocument.Build</c> wrote
/// <c>label=""</c> - because the device really had no name - and the shape came back mute. Whoever
/// dropped five without naming them found five identical anonymous boxes. A suggested name does not
/// break the owner's earlier rule either: he asked that dragging not STOP TO ASK for a name, and an
/// automatic one asks nothing.
/// </para>
/// <para>
/// <b>"Free" means free on the plant AS THIS PAGE LAST READ IT</b>, not free in this session. The
/// caller hands the plant's devices as the page holds them at that instant - <c>PlantGraph.Items</c>,
/// which <c>CreateItemCommand</c> keeps up to date with the host's own reread after every creation -
/// so twenty cells pasted in one gesture take twenty different numbers, and so does a second drop a
/// moment after the first. Handing a snapshot taken when the page opened would give every one of
/// them <c>Automation 1</c>.
/// </para>
/// <para>
/// <b>It is not a reservation, and it cannot be one across two Editors.</b> The edit permit is
/// exclusive on WRITING, not on reading: two Editors taking the permit in turn - a lease lasts 60
/// seconds, <c>EditLockEndpoints.Lease</c> - hold two graphs of their own, and the second can
/// propose a number the first has just written. Nothing on the plant depends on it (see the paragraph on duplicates below), so this is
/// said rather than defended against: a suggestion that has to be looked up before every drop would
/// cost a round trip per cell of a paste to remove a confusion, not a fault.
/// </para>
/// <para>
/// <b>Each type counts on its own</b>, because the prefix is the type's label: a <c>Buffer</c> does
/// not jump to 2 because an <c>Automation 1</c> exists.
/// </para>
/// <para>
/// <b>A collision is unlikely and is looked for all the same.</b> The real names of this plant look
/// like <c>IN01.0001.0001</c>, so nothing of the plant's own ever matches - but from today WE write
/// <c>Automation 1</c>, so a plant written by this Editor will hold exactly the names this rule
/// produces. A duplicate would break nothing (no code in <c>Wcs.Engine</c> or <c>Wcs.Host</c> looks
/// a device up by name, and the upsert does not validate it: the name is there to be shown and
/// sorted by) but it would confuse whoever reads a list, which is the whole reason the name exists.
/// </para>
/// </remarks>
public static class SuggestedDeviceName
{
    /// <summary>
    /// The name for a device of <paramref name="typeLabel"/> about to be created on a plant that
    /// already holds <paramref name="items"/>.
    /// </summary>
    /// <param name="typeLabel">
    /// The registry's own label (<see cref="DeviceTypeDto.Label"/>), the very string the shapes
    /// panel shows for that entry. It is not invented here and it is not translated: a name the
    /// operator cannot tie back to the shape he dragged would be worse than no name.
    /// </param>
    public static string For(string typeLabel, IEnumerable<PlantItemDto> items)
    {
        var taken = new HashSet<int>();
        foreach (var item in items)
            if (NumberOf(typeLabel, item.Name) is { } number)
                taken.Add(number);

        var free = 1;
        while (taken.Contains(free)) free++;
        return $"{typeLabel} {free}";
    }

    /// <summary>
    /// The number in <paramref name="name"/> if it is one of ours for <paramref name="typeLabel"/> -
    /// the label, one space, and digits - and null if it is not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Case is ignored</b> on the label: <c>automation 1</c> and <c>Automation 1</c> are the same
    /// name to whoever reads a list, and suggesting the second next to the first would be suggesting
    /// a duplicate.
    /// </para>
    /// <para>
    /// <b>Digits only, and no leading zero.</b> <c>Automation 1x</c>, <c>Automation +1</c>,
    /// <c>Automation 1 </c> and <c>Automation 007</c> are names somebody else chose that merely
    /// begin like ours: taking them for ours would burn a number for nothing, and <c>007</c> in
    /// particular is a different string from the <c>7</c> we would write, so it does not collide
    /// with anything. A number too big for an <see cref="int"/> is not ours either, for the same
    /// reason: we could never have written it.
    /// </para>
    /// <para>
    /// <b>The digit loop is not made redundant by <c>NumberStyles.None</c>, and that was measured</b>
    /// rather than assumed: <c>int.TryParse("7\0", NumberStyles.None, InvariantCulture)</c> answers
    /// TRUE with 7 on .NET 10 - the parser allows trailing NUL characters. Without the loop,
    /// <c>Automation 7\0</c> would burn the number 7, and a name ending that way is what a
    /// fixed-width CHAR column or a string read back from a PLC hands over. <c>SuggestedDeviceNameTest</c>
    /// holds the case that turns red when the loop goes.
    /// </para>
    /// </remarks>
    private static int? NumberOf(string typeLabel, string? name)
    {
        if (name is null) return null;

        var prefix = typeLabel + " ";
        if (name.Length <= prefix.Length) return null;
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        var digits = name.AsSpan(prefix.Length);
        if (digits[0] == '0') return null;
        foreach (var character in digits)
            if (character is < '0' or > '9')
                return null;

        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
