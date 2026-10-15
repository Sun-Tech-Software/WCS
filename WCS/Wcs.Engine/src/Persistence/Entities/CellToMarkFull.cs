namespace Wcs.Engine.Persistence.Entities;

/// <summary>
/// Una cella che il PLC ha rifiutato con l'errore 67 («cella piena») e che il gestionale, muto in quel
/// momento, non ha ancora segnato piena (<c>CellsToMarkFull</c>): si ritenta a ogni giro del freno del
/// navigatore finche' il gestionale risponde, anche dopo un riavvio (risposta 3 del proprietario del
/// 9/10). Riuscito il segno, la riga si cancella.
/// </summary>
public sealed class CellToMarkFull
{
    /// <summary>Chiave tecnica, crescente: da' l'ordine dei ritentativi.</summary>
    public long Id { get; set; }

    /// <summary>Il codice porta della cella, la chiave con cui il gestionale la blocca. Unico.</summary>
    public string DoorCode { get; set; } = string.Empty;
}
