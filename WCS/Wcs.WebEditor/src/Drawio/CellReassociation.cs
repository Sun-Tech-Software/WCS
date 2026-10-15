using Wcs.Api.Contracts.Dtos;

namespace Wcs.WebEditor.Drawio;

/// <summary>
/// One cell of the canvas that carries a mark of ours, as the bridge's <c>markedCells</c> reads it
/// off draw.io's model.
/// </summary>
/// <param name="Id">The cell's identifier inside draw.io: the plant's guid for a device the seeding drew, draw.io's own for one born from a drop or a paste.</param>
/// <param name="Mark">The value of the <c>wcs</c> attribute, exactly as the drawing carries it - <c>device</c>, <c>junction</c>, or something we do not recognise.</param>
public sealed record DrawioMarkedCell(string Id, string Mark);

/// <summary>
/// <b>Row 15: pointing a shape at a device by hand.</b> The rules that decide whether that gesture
/// may go through, and the sentence that refuses it when it may not.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the gesture is, here.</b> In the WinForms Editor it wrote a <c>Guid</c> into the node's
/// <c>Tag</c>. Here the cell's identifier is the link (<see cref="DrawioDocument.Build"/> writes the
/// device's guid as the <c>&lt;object id&gt;</c>), and that identifier is NOT rewritten: the page
/// records the pair instead, in the same session map a device born from a drop lives in
/// (<c>DrawioEditor._plantItemByDrawnCell</c>). So a reassociated shape is, from that moment on,
/// exactly the same kind of thing as a dropped one - a cell with draw.io's own identifier that the
/// page knows stands for a device - and it produces <b>no new category of cell</b> in the drawing
/// that comes back at the save. That is what keeps the four generative nets of
/// <c>DrawioDrawingInvariantTest</c> answering about it without being touched, and it is also why
/// nothing goes into the history Ctrl+Z walks: the model is only READ.
/// </para>
/// <para>
/// <b>Writing an identifier by hand is a loaded gun, and this is the safety catch.</b> Every one of
/// the refusals below happens BEFORE anything is recorded, and each one says what was refused and
/// why. The page asks the HOST for the device named by the typed identifier rather than trusting
/// the plant it read at opening, so "the plant knows it" is true at the moment of the gesture and
/// not half an hour earlier.
/// </para>
/// <para>
/// <b>There is no "Reset".</b> The WinForms Editor emptied the box to clear the <c>Tag</c>, and on
/// this branch that half has no meaning left: a cell of ours stripped of its mark is redrawn marked
/// by the plant at the very next save (the cell the operator stripped is taken out of the
/// decoration, so there is no duplicate either), and a cell the page has bound in this session is
/// unbound by deleting the shape. An "empty it" button would therefore promise a detachment that
/// this Editor does not have - which is the one thing this file exists to keep from being said.
/// </para>
/// </remarks>
public static class CellReassociation
{
    /// <summary>The mark a cell has to carry for this gesture to mean anything: the one <see cref="DrawioDocument"/> and <see cref="DrawioLibrary"/> write and <see cref="DrawioReader"/> reads back.</summary>
    public const string DeviceMark = "device";

    /// <summary>
    /// The identifier the operator typed, or <c>null</c> with <paramref name="refusal"/> filled in
    /// when what was typed is not one at all.
    /// </summary>
    /// <remarks>
    /// <b>The empty box is its own refusal</b> and not a silent "nothing happened": in the Editor
    /// this row comes from, an empty box was the gesture that cleared the tag, and whoever has that
    /// in their hands would otherwise press the button, see nothing change and have no way of
    /// telling a clearing that worked from a page that ignored them. See the remarks at the head of
    /// this class for why there is nothing to clear.
    /// </remarks>
    public static Guid? Parse(string? typed, out string? refusal)
    {
        var text = (typed ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            refusal = "Nothing was typed. This box points a shape at a device, and an empty box does not take one away: "
                + "a cell of ours that loses its mark is drawn marked again by the very next save, and a shape this page "
                + "has pointed at a device is unpointed by deleting the shape. Nothing was written.";
            return null;
        }

        if (!Guid.TryParse(text, out var id))
        {
            refusal = $"\"{text}\" is not a device identifier: one looks like "
                + "00000000-0000-0000-0000-000000000000, thirty-two hexadecimal digits in five groups. Nothing was written.";
            return null;
        }

        refusal = null;
        return id;
    }

    /// <summary>
    /// Why this shape may <b>not</b> be pointed at this device, or <c>null</c> when it may.
    /// </summary>
    /// <param name="cellId">The cell selected on the canvas, which is the shape being pointed.</param>
    /// <param name="deviceId">The identifier <see cref="Parse"/> read out of the box.</param>
    /// <param name="device">
    /// What the HOST answered about <paramref name="deviceId"/> a moment ago, and <c>null</c> when
    /// it answered that it has no such device. It is the host's answer and not a lookup in the
    /// plant read at opening: somebody else may have deleted the device since, and a shape pointed
    /// at a device that is no longer there is the very fault this row is supposed to repair.
    /// </param>
    /// <param name="marked">
    /// Every marked cell of the canvas as it is now, or <c>null</c> when the drawing could not be
    /// asked. Null is refused rather than read as "no cells": the two questions below are about the
    /// canvas, and answering them from an absence would let the gesture through on a drawing nobody
    /// looked at.
    /// </param>
    /// <param name="boundInThisSession">
    /// The pairs the page already holds - draw.io's identifier for a cell, the plant's for the
    /// device it stands for - which is where a device born from a drop lives, and where this gesture
    /// writes. It is read here so that a device already drawn by a DROPPED shape is refused exactly
    /// like one drawn by a seeded shape.
    /// </param>
    public static string? WhyNot(
        string cellId, Guid deviceId, PlantItemDto? device,
        IReadOnlyList<DrawioMarkedCell>? marked, IReadOnlyDictionary<string, Guid> boundInThisSession)
    {
        if (marked is null)
            return "The drawing could not be asked what it carries, so neither of the two questions this gesture "
                + "depends on - is this shape one of ours, and is that device already drawn somewhere - could be "
                + "answered. Nothing was written.";

        if (!marked.Any(cell => cell.Id == cellId && cell.Mark == DeviceMark))
            return "This shape is not marked as a device of ours, and only a marked one can stand for a device: "
                + "the drawing hands the save its free drawing untouched, so a note or a box pointed at a device "
                + "would go on being a note and the save would never know. Nothing was written.";

        if (device is null)
            return $"The plant has no device {deviceId}. An identifier this Editor does not find on the host is not "
                + "written into a shape: at the next save the shape would still be a cell nobody knows, and every "
                + "junction that used to point at that device would go on pointing at nothing. Nothing was written.";

        if (boundInThisSession.TryGetValue(cellId, out var alreadyHere) && alreadyHere == deviceId)
            return $"This shape already stands for {Named(device)}. Nothing was written, and nothing needed to be.";

        if (AnotherCellStandingFor(cellId, deviceId, marked, boundInThisSession) is { } other)
            return $"{Named(device)} is already drawn on this canvas, by cell {other}: two shapes for one device "
                + "would send two geometries for it to the plant at the next save, and the plant would keep "
                + "whichever arrived last. Nothing was written - delete one of the two shapes first.";

        return null;
    }

    /// <summary>
    /// What the gesture did, in words, once <see cref="WhyNot"/> has let it through: what the shape
    /// is now, and <b>what is still to come</b>, because nothing has reached the plant yet.
    /// </summary>
    /// <remarks>
    /// The half about the type is not a formality: a device whose type the registry does not declare
    /// is undrawn for that reason as well (<see cref="DrawioDocument.WhyNotDrawn"/> asks both), so
    /// writing its geometry at the save repairs one of its two reasons and the next opening still
    /// shows no shape for it. Saying "and the drawing will show it" there would be a promise this
    /// Editor cannot keep.
    /// </remarks>
    public static string WhatItMeans(PlantItemDto device, bool theTypeIsDrawn) =>
        $"This shape now stands for {Named(device)}. Nothing has reached the plant yet: the next save writes this "
        + "shape's position and size to that device, exactly as it does for a device dropped on the canvas. "
        + (theTypeIsDrawn
            ? "After that, the drawing draws it from the plant at every opening."
            : $"Its device type ({(int)device.DeviceType}) is not one the engine's type registry declares, though, so "
            + "even with a box the drawing will still have no shape to draw it with at the next opening: the type "
            + "is what has to change.");

    /// <summary>
    /// The devices the plant holds that <b>no cell of this canvas stands for</b>: the set this
    /// gesture can be aimed at, and the one the box lists so that the identifier to type can be read
    /// somewhere.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is the complement of <see cref="AnotherCellStandingFor"/> asked of the whole plant, and
    /// the two go through the same two roads for that reason: a device drawn by a DROPPED shape is
    /// out of this list exactly as one drawn by a seeded shape is.
    /// </para>
    /// <para>
    /// <b>On a drawing nobody could look at the list is EMPTY</b>, not the whole plant: the question
    /// this answers is about the canvas, and a page that could not read the canvas has no business
    /// naming devices as "without a shape". The gesture is refused for the same reason a moment
    /// later, so the two say the same thing.
    /// </para>
    /// <para>
    /// The order is the plant's, which is the order everything else on the page shows devices in;
    /// an undrawn device is in here like any other - it is precisely the one this gesture repairs -
    /// and what that repair does and does not achieve is said by <see cref="WhatItMeans"/>.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<PlantItemDto> DevicesNoCellStandsFor(
        IReadOnlyList<PlantItemDto> items, IReadOnlyList<DrawioMarkedCell>? marked,
        IReadOnlyDictionary<string, Guid> boundInThisSession)
    {
        if (marked is null) return [];

        var standingFor = new HashSet<Guid>();
        foreach (var cell in marked)
        {
            if (cell.Mark != DeviceMark) continue;
            if (Guid.TryParse(cell.Id, out var seeded)) standingFor.Add(seeded);
            if (boundInThisSession.TryGetValue(cell.Id, out var bound)) standingFor.Add(bound);
        }

        return items.Where(item => !standingFor.Contains(item.ItemId)).ToList();
    }

    /// <summary>The device a line names itself by: its name, and its identifier when it has no name - never a gap.</summary>
    private static string Named(PlantItemDto device) =>
        device.Name is { Length: > 0 } name ? $"{name} ({device.ItemId})" : device.ItemId.ToString();

    /// <summary>
    /// The identifier of a cell OTHER than this one that already stands for this device, or
    /// <c>null</c> when none does.
    /// </summary>
    /// <remarks>
    /// <b>Two roads, the same two the save follows</b> (<c>DrawioDelta.PlantItemOf</c>): the guid
    /// written on the cell by the seeding, and the session map for a cell born from a drop or
    /// pointed by this very gesture. A test that asked only the first would let a device already
    /// drawn by a dropped shape be pointed at a second time, and the two shapes would fight over its
    /// geometry at every save.
    /// <para>
    /// The session map is read THROUGH the marked cells and not on its own: a pair whose cell the
    /// operator has since deleted from the canvas describes a shape that is not there, and refusing
    /// on it would leave the operator with a gesture that can never go through and no way of seeing
    /// why.
    /// </para>
    /// </remarks>
    private static string? AnotherCellStandingFor(
        string cellId, Guid deviceId, IReadOnlyList<DrawioMarkedCell> marked,
        IReadOnlyDictionary<string, Guid> boundInThisSession)
    {
        foreach (var cell in marked)
        {
            if (cell.Id == cellId || cell.Mark != DeviceMark) continue;
            if (Guid.TryParse(cell.Id, out var seeded) && seeded == deviceId) return cell.Id;
            if (boundInThisSession.TryGetValue(cell.Id, out var bound) && bound == deviceId) return cell.Id;
        }

        return null;
    }
}
