namespace Wcs.Api.Contracts.Dtos;

/// <summary>Una cella del WMS come la vede l'Editor per collegare porte e polmoni: magazzino, area, colonna, piano, porte A e B.</summary>
public sealed record WmsCell(decimal CellId, string Warehouse, string Area, int Column, int? Floor, string? DoorA, string? DoorB, string? Description);
