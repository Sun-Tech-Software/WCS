namespace Wcs.WebEditor.Drawio.Components;

/// <summary>Paging rules shared by the compact operational grids in the draw.io editor.</summary>
internal static class DataGridPagination
{
    public static int PageCount(int rowCount, int pageSize) =>
        Math.Max(1, (rowCount + pageSize - 1) / pageSize);

    public static string PageLine(int rowCount, int page, int pageSize, string emptyLabel)
    {
        if (rowCount == 0) return $"No {emptyLabel}";
        var first = ((page - 1) * pageSize) + 1;
        var last = Math.Min(page * pageSize, rowCount);
        return first == last ? $"{first} of {rowCount}" : $"{first}–{last} of {rowCount}";
    }

    public static IEnumerable<int> PageChoices(int currentPage, int pageCount)
    {
        var length = Math.Min(5, pageCount);
        var start = Math.Clamp(currentPage - 2, 1, Math.Max(1, pageCount - length + 1));
        return Enumerable.Range(start, length);
    }
}
