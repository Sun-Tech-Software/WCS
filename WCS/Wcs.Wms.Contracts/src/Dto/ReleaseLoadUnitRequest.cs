namespace Wcs.Wms.Contracts;

/// <summary>
/// L'uscita di un'unità di carico dall'automazione (compito 13, ex tre chiamate in sequenza di
/// <c>AutomationDeviceLuManager.ProcessLuBusinessLogic</c>: <c>OperazioniUdc.CancellaPosizioneAssegnazioneUdc</c>,
/// <c>CancellaPosizioneFisicaUdc</c>, e - se non risultava già spedita - <c>SpedisciUdc</c>).
/// Il gestionale applica la sequenza come un'unica transazione e usa <see cref="OperationId"/> per
/// riconoscere le ripetizioni della stessa richiesta.
/// <c>SpedisciUdc</c> è già idempotente (non scrive se l'unità di carico risulta già
/// spedita): questa operazione lo chiama sempre, senza il controllo che il motore faceva prima di
/// chiamarlo - lo stesso controllo letto fuori da questa sequenza, quindi soggetto a una finestra
/// di corsa che l'operazione chiude spostandolo dentro la stessa scrittura.
/// </summary>
/// <param name="LoadUnitId">L'unità di carico che esce dall'automazione.</param>
/// <param name="Manual">Vero se l'uscita è manuale, falso se automatica.</param>
/// <param name="RequestedByUserId">L'utente/processo che sta operando, per la tracciabilità della scrittura.</param>
/// <param name="OperationId">
/// Identità stabile della transizione, conservata durante tutti i tentativi. <see cref="Guid.Empty"/>
/// indica un chiamante legacy: il gestionale esegue la richiesta senza registrarne la deduplicazione.
/// </param>
public sealed record ReleaseLoadUnitRequest(
    decimal LoadUnitId,
    bool Manual,
    string RequestedByUserId,
    Guid OperationId = default);
