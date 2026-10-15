namespace Wcs.Engine.Persistence.Entities;

/// <summary>Il tipo di mutazione fisica che il WCS deve consegnare al WMS.</summary>
public enum WarehouseTransitionKind : byte
{
    Allocate = 1,
    Release = 2
}

/// <summary>
/// Una transizione fisica già decisa dal WCS e non ancora completata dal WMS. La riga resta
/// presente durante tutti i tentativi, conservando lo stesso <see cref="OperationId"/>.
/// </summary>
public sealed class PendingWarehouseTransition
{
    public long Id { get; set; }
    public Guid OperationId { get; set; }
    public WarehouseTransitionKind Kind { get; set; }
    public Guid MissionId { get; set; }
    public decimal LoadUnitId { get; set; }
    public string PayloadJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset NextAttemptAtUtc { get; set; }
}
