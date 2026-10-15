using Wcs.Wms.Contracts;

namespace Wcs.Engine.Persistence.Entities;

/// <summary>
/// Un esito di estrazione che il WCS ha registrato localmente e deve ancora consegnare al WMS.
/// </summary>
public sealed class PendingExtractionOutcome
{
    public long Id { get; set; }
    public decimal ExtractionRowId { get; set; }
    public ExtractionRowStatus Status { get; set; }
    public string WorkstationId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}
