namespace Wcs.Wms.Contracts;

/// <summary>
/// L'unita' di carico e' assegnata a una nota di spedizione (compito 3): compare in
/// <c>ASSEGNAZIONE_NOTE</c>, la tabella del gestionale su cui il compito 21 ha portato i fatti
/// dell'assegnazione. Chi la riceve ne trae un rifiuto - l'unita' di carico e' impegnata da una
/// nota e non si sposta a mano.
/// <para>
/// La domanda ha <b>sempre</b> una risposta, anche su un'unita' di carico che non esiste:
/// <see cref="AssignedToNote"/> e' falso perche' in quella tabella non c'e' nessuna riga con quel
/// <c>ID_UDC</c>, non perche' manchi qualcosa da leggere. Per questo la lettura e' severa e non c'e'
/// nessun 404 da tradurre, al contrario di <see cref="LoadUnitDescription"/>, che descrive una riga
/// di <c>UDC</c> e puo' quindi non trovarla.
/// </para>
/// </summary>
public sealed record LoadUnitAssignmentState(decimal LoadUnitId, bool AssignedToNote);
