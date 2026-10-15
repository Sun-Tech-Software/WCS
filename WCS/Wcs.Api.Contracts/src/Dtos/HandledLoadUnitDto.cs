namespace Wcs.Api.Contracts.Dtos;

public sealed record HandledLoadUnitDto(long Id, Guid MissionId, short Type, DateTime Date, decimal CellId,
    string? LoadUnitCode, decimal? ProcessId, string? ResultDescription, decimal? LoadUnitProcessId,
    decimal? NoteHeaderId, decimal? ExtractionHeaderId, decimal? LoadUnitId);
/// <summary>Un aggregato per i KPI: quante UDC per tipo in ogni periodo; <c>Period</c> è il primo giorno del periodo.</summary>
public sealed record HandledLoadUnitSummary(DateOnly Period, short Type, int Count);
