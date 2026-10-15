namespace Wcs.Api.Contracts.Dtos;

/// <summary>
/// Il modello disegnato dell'impianto. Nella <c>PUT</c> e' obbligatorio solo <c>DiagramXml</c>:
/// <c>Diagram</c> e' il binario di <c>SaveBinary</c> di Syncfusion, che arriva vuoto e che nessuno
/// legge; la vista e i contatori li scrive il motore.
/// </summary>
public sealed record DiagramDto(byte[]? Diagram, byte[]? DiagramXml, byte[]? View, int CheckCounter,
    int StatusCounter, short CompactingStatus);
