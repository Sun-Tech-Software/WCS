namespace Wcs.Engine.Persistence.Entities;

/// <summary>
/// Quali dati del PLC registrare quando arriva una UDC, e con quali valori creare una UDC nuova.
/// Una riga sola. Sostituisce <c>RECORD_DATA_FROM_ASI</c> su SQL Server.
/// </summary>
/// <remarks>
/// Corrispondenza con le vecchie colonne: <c>RECORD_LU_CONTAINER_TYPE</c>→<c>RecordContainerType</c>,
/// <c>RECORD_LU_CONTAINER_SIZE_HEIGTH/LENGTH/WIDTH</c>→<c>RecordContainerHeight/Length/Width</c>,
/// <c>RECORD_LU_HEIGHT/LENGTH/WIDTH/WEIGTH</c>→<c>RecordHeight/Length/Width/Weight</c>,
/// <c>RECORD_LU_TYPE</c>→<c>RecordLoadUnitType</c>, <c>RECORD_LU_SIDE1SURPLUS/SIDE2SURPLUS</c>→<c>RecordSide1Surplus/Side2Surplus</c>,
/// <c>DECODE_LU_LABEL_CODES</c>→<c>DecodeLabelCodes</c>, <c>NEWLU_ID_LU_TYPE</c>→<c>NewLoadUnitTypeId</c>,
/// <c>NEWLU_ID_REFERENCE</c>→<c>NewLoadUnitReference</c>, <c>NEWLU_QT</c>→<c>NewLoadUnitQuantity</c>,
/// <c>NEWLU_ID_QT</c>→<c>NewLoadUnitQuantityId</c>. Le colonne che il motore non leggeva ne' scriveva non ci sono.
/// </remarks>
public sealed class RecordDataSettings
{
    public const long DefaultId = 1;

    public long Id { get; set; }
    public bool RecordContainerType { get; set; }
    public bool RecordContainerHeight { get; set; }
    public bool RecordContainerLength { get; set; }
    public bool RecordContainerWidth { get; set; }
    public bool RecordHeight { get; set; }
    public bool RecordLength { get; set; }
    public bool RecordWidth { get; set; }
    public bool RecordWeight { get; set; }
    public bool RecordLoadUnitType { get; set; }
    public bool RecordSide1Surplus { get; set; }
    public bool RecordSide2Surplus { get; set; }
    public bool DecodeLabelCodes { get; set; }
    public decimal NewLoadUnitTypeId { get; set; }
    public string? NewLoadUnitReference { get; set; }
    public decimal NewLoadUnitQuantity { get; set; }
    public string? NewLoadUnitQuantityId { get; set; }
}
