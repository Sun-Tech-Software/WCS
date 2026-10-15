namespace Wcs.Engine.Persistence.Entities;

/// <summary>
/// Quali dati del PLC verificare quando arriva una UDC, con quale confronto e contro quale valore.
/// Una riga sola. Sostituisce <c>CHECK_DATA_FROM_ASI</c> su SQL Server.
/// </summary>
/// <remarks>
/// Ogni misura ha due colonne: il tipo di confronto (<c>Compare*</c>, i valori di
/// <c>CheckDataFromAsiInfo.CompareTypes</c>) e il valore atteso. Corrispondenza con le vecchie
/// colonne: <c>COMP_LU_X</c>→<c>CompareX</c>, <c>LU_X</c>→<c>X</c>, con <c>CONTAINER_SIZE_HEIGTH</c>→<c>ContainerHeight</c>,
/// <c>WEIGTH</c>→<c>Weight</c>, <c>TEMP</c>→<c>Temperature</c>, <c>HEIGTH_SURPLUS</c>→<c>HeightSurplus</c>,
/// <c>LENGTH_SIDE_1_SURPLUS</c>→<c>LengthSide1Surplus</c> e simili, <c>CODE</c>→<c>Code</c>.
/// </remarks>
public sealed class CheckDataSettings
{
    public const long DefaultId = 1;

    public long Id { get; set; }

    public short CompareContainerType { get; set; }
    public int ContainerType { get; set; }
    public short CompareContainerHeight { get; set; }
    public int ContainerHeight { get; set; }
    public short CompareContainerLength { get; set; }
    public int ContainerLength { get; set; }
    public short CompareContainerWidth { get; set; }
    public int ContainerWidth { get; set; }
    public short CompareHeight { get; set; }
    public int Height { get; set; }
    public short CompareLength { get; set; }
    public int Length { get; set; }
    public short CompareWidth { get; set; }
    public int Width { get; set; }
    public short CompareWeight { get; set; }
    public int Weight { get; set; }
    public short CompareTemperature { get; set; }
    public int Temperature { get; set; }
    public short CompareLengthSide1Surplus { get; set; }
    public int LengthSide1Surplus { get; set; }
    public short CompareLengthSide2Surplus { get; set; }
    public int LengthSide2Surplus { get; set; }
    public short CompareWidthSide1Surplus { get; set; }
    public int WidthSide1Surplus { get; set; }
    public short CompareWidthSide2Surplus { get; set; }
    public int WidthSide2Surplus { get; set; }
    public short CompareHeightSurplus { get; set; }
    public int HeightSurplus { get; set; }
    public short CompareCode { get; set; }
    public string? Code { get; set; }
}
