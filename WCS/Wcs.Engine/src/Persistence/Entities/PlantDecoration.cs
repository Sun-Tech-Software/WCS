namespace Wcs.Engine.Persistence.Entities;

/// <summary>
/// La decorazione del disegno: scritte, riquadri di zona, note dell'operatore. Vive nel wcs.db
/// accanto all'impianto (decisione 3.6 del progetto) cosi' lo segue quando lo si esporta o lo si
/// copia altrove. Non la interpretiamo: e' il pezzo di XML mxGraph che non porta il nostro
/// marchio. Una riga sola, come <see cref="PlantDiagram"/>.
/// </summary>
public sealed class PlantDecoration
{
    /// <summary>L'unica riga ha <see cref="DefaultId"/>.</summary>
    public const long DefaultId = 1;

    public long Id { get; set; }
    public string Xml { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; }
}
