namespace Wcs.Connection.Interface.CommandData;

public class MovementLuDataCommand
{
    #region Public Properties

    public string? DestAsi { get; set; } = string.Empty;
    public string? DestPartition { get; set; } = string.Empty;
    public string? DestSubitem { get; set; } = string.Empty;
    public int HandlingMode { get; set; }
    public int Id { get; set; }
    public string? SourceAsi { get; set; } = string.Empty;
    public string? SourcePartition { get; set; } = string.Empty;
    public string? SourceSubitem { get; set; } = string.Empty;

    #endregion Public Properties
}