namespace Wcs.Engine.Persistence.Entities;

/// <summary>
/// Il diagramma dell'impianto com'e' disegnato nell'Editor, con i contatori e lo stato di
/// compattazione che il motore aggiorna. Una riga sola. Sostituisce <c>NAVIGATOR</c> su SQL Server.
/// </summary>
/// <remarks>
/// <see cref="Diagram"/> e <see cref="DiagramXml"/> sono il modello Syncfusion salvato in binario
/// e in XML: il motore non li interpreta, li custodisce per l'Editor. <see cref="View"/> era la
/// vista salvata e non risulta piu' usata: resta per non perdere dati alla migrazione.
/// </remarks>
public sealed class PlantDiagram
{
    /// <summary>L'unica riga ha <see cref="DefaultId"/>.</summary>
    public const long DefaultId = 1;

    public long Id { get; set; }
    public byte[]? Diagram { get; set; }
    public byte[]? DiagramXml { get; set; }
    public byte[]? View { get; set; }
    public int CheckCounter { get; set; }
    public int StatusCounter { get; set; }
    public short CompactingStatus { get; set; }
    public bool IsDefault { get; set; }

    /// <summary>
    /// Quando la geometria e' stata portata dal blob nelle colonne dei dispositivi. Nulla: non e'
    /// ancora stato fatto. Da quando e' valorizzata la verita' del disegno e' il modello, e
    /// <c>PUT /plant/diagram</c> non accetta piu': si veda la tappa 4a della roadmap.
    /// </summary>
    public DateTime? GeometryMigratedAt { get; set; }
}
