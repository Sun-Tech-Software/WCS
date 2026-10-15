using static Wcs.Common.Enums;

namespace Wcs.Api.Contracts.Dtos;

/// <summary>
/// Lo stato del motore; <c>HistoryFrom</c> e' il primo mese di storico in linea (nullo se lo
/// storico non e' aperto). <c>EngineLoadError</c> e' il motivo dell'ultimo caricamento fallito
/// (voce D145): l'host e' vivo ma il motore non e' caricato, e lo ritenta; nullo dopo un
/// caricamento riuscito.
/// </summary>
public sealed record EngineStatus(NavigatorStatusTypes NavStatus, DefragmentationTypes Defragmentation,
    string Version, DateTime UtcNow, DateOnly? HistoryFrom = null, string? EngineLoadError = null);
