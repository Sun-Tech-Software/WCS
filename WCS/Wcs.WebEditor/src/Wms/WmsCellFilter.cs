using System.Globalization;
using Wcs.Api.Contracts.Dtos;

namespace Wcs.WebEditor.Wms;

/// <summary>The searchable values shared by the two warehouse-cell grids.</summary>
internal static class WmsCellFilter
{
    public static bool Matches(WmsCell cell, string filter)
    {
        var wanted = filter.Trim();
        if (wanted.Length == 0) return true;

        string?[] values =
        [
            cell.CellId.ToString(CultureInfo.InvariantCulture), cell.Warehouse, cell.Area,
            cell.Column.ToString(CultureInfo.InvariantCulture),
            Convert.ToString(cell.Floor, CultureInfo.InvariantCulture), cell.DoorA, cell.DoorB,
            cell.Description,
        ];
        return values.Any(value => value?.Contains(wanted, StringComparison.OrdinalIgnoreCase) == true);
    }
}
