using Wcs.Api.Client;
using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Model;

namespace Wcs.WebEditor.Editing;

/// <summary>
/// Unico ingresso delle scritture geometriche dell'Editor: prende la versione dal modello locale,
/// attende la conferma dell'host e solo allora fa avanzare valori e versione insieme.
/// </summary>
public static class GeometryWriter
{
    public static async Task WriteAsync(
        WcsClient client,
        PlantGraph graph,
        IReadOnlyCollection<PlantItemGeometryDto> geometries,
        CancellationToken ct = default)
    {
        var duplicateIds = geometries
            .GroupBy(geometry => geometry.ItemId)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        if (duplicateIds.Count > 0)
            throw new ArgumentException("The same geometry appears more than once.", nameof(geometries));

        var items = graph.Items.ToDictionary(item => item.ItemId);
        var missingIds = geometries
            .Where(geometry => !items.ContainsKey(geometry.ItemId))
            .Select(geometry => geometry.ItemId)
            .ToList();
        if (missingIds.Count > 0)
            throw new InvalidOperationException(
                $"The geometry cannot be written: {string.Join(", ", missingIds)} is not in the plant this page holds.");

        var updates = geometries
            .Select(geometry => ToUpdate(geometry, items[geometry.ItemId].GeometryVersion))
            .ToList();

        IReadOnlyList<PlantItemGeometryVersionDto> confirmations = updates.Count == 1
            ? [await client.SetItemGeometryAsync(updates[0], ct)]
            : await client.SetItemGeometriesAsync(updates, ct);

        ValidateConfirmations(updates, confirmations);
        graph.ApplyGeometry(geometries, confirmations);
    }

    public static PlantItemGeometryUpdateDto ToUpdate(PlantItemGeometryDto geometry, Guid expectedVersion) =>
        new(
            geometry.ItemId,
            geometry.X,
            geometry.Y,
            geometry.Width,
            geometry.Height,
            geometry.Rotation,
            expectedVersion);

    public static PlantItemDto ApplyConfirmation(
        PlantItemDto item,
        PlantItemGeometryDto geometry,
        PlantItemGeometryVersionDto confirmation)
    {
        if (item.ItemId != geometry.ItemId || item.ItemId != confirmation.ItemId)
            throw new InvalidOperationException("The geometry confirmation is not about the device that was written.");
        if (confirmation.GeometryVersion == Guid.Empty)
            throw new InvalidOperationException("The host confirmed an empty geometry version.");

        return item with
        {
            X = geometry.X,
            Y = geometry.Y,
            Width = geometry.Width,
            Height = geometry.Height,
            Rotation = geometry.Rotation,
            GeometryVersion = confirmation.GeometryVersion
        };
    }

    private static void ValidateConfirmations(
        IReadOnlyCollection<PlantItemGeometryUpdateDto> updates,
        IReadOnlyList<PlantItemGeometryVersionDto> confirmations)
    {
        var requestedIds = updates.Select(update => update.ItemId).ToHashSet();
        var confirmedIds = confirmations.Select(confirmation => confirmation.ItemId).ToList();
        if (confirmedIds.Count != requestedIds.Count ||
            confirmedIds.Distinct().Count() != confirmedIds.Count ||
            !requestedIds.SetEquals(confirmedIds) ||
            confirmations.Any(confirmation => confirmation.GeometryVersion == Guid.Empty))
        {
            throw new InvalidOperationException(
                "The host's answer does not confirm exactly all the geometries that were asked for.");
        }
    }
}
