using static Wcs.Common.Enums;

namespace Wcs.Api.Contracts.Dtos;

/// <summary>La modalità di connessione da applicare a tutti i dispositivi: Test (simulatore) o Connected (PLC).</summary>
public sealed record ConnectionTypeUpdate(ConnectionTypes ConnectionType);
