namespace Wcs.Connection.Interface;

public class DataAsi(string? asi, string? subitem, string? partition)
{
    public string Asi { get; set; } = asi ?? string.Empty;
    public string Partition { get; set; } = partition ?? string.Empty;
    public string Subitem { get; set; } = subitem ?? string.Empty;
}
