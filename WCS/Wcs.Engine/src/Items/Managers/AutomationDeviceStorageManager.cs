using Helper.Converters;
using Wcs.Engine.ORM;
using Log.Interface;
using System.Data;
using Wcs.Connection.Interface;
using Wcs.Engine.Api;
using Wcs.Wms.Contracts;
using static Wcs.Common.Enums;
using static Wcs.Engine.Items.PathMission;

namespace Wcs.Engine.Items.Managers;

/// <summary>
/// Gestisce le operazioni di stoccaggio per i dispositivi di automazione.
/// Fornisce funzionalità per l'estrazione, il deposito e la gestione delle posizioni di stoccaggio delle unità di carico (LU).
/// </summary>
/// <param name="device">Il dispositivo di automazione associato a questo manager</param>
public class AutomationDeviceStorageManager(AutomationDevice device) : IDisposable
{

    #region Internal Fields

    /// <summary>
    /// Token di cancellazione per interrompere le operazioni di estrazione in corso
    /// </summary>
    internal CancellationTokenSource? CancellationExtractionTokenSource;

    /// <summary>
    /// Il freno della ricerca di un'altra cella di deposito, per missione: quando non c'e' posto la
    /// ricerca non si ripete al richiamo ordinario successivo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Magazzino pieno e' una condizione ordinaria, non un guasto: il dispositivo resta pronto con
    /// l'unita' a bordo e il proprio ciclo di sfondo lo ripassa ogni due secondi. Senza freno ogni
    /// passata rifaceva tre letture del confine, il blocco al deposito, il rilascio della
    /// prenotazione, la riprenotazione e lo sblocco, con una riga di avviso, indefinitamente - e fra
    /// un giro e l'altro la missione restava senza prenotazione.
    /// </para>
    /// <para>
    /// Il meccanismo e' quello della voce B70, lo stesso di <c>Navigator.BuildRoutePath</c>: un
    /// passo per tentativo fino a un tetto. Le due misure sono diverse perche' sono diversi i cicli
    /// che frenano - li' un giro ogni 10 ms, qui ogni 2 secondi: il passo e' un giro (2 secondi) e
    /// il tetto quindici (30 secondi), cosi' una cella che si libera viene usata entro mezzo minuto.
    /// </para>
    /// <para>
    /// E' assegnabile perche' i collaudi lo costruiscano con un orologio finto: un freno provato con
    /// attese vere sarebbe lento e, sotto carico, rosso su codice sano.
    /// </para>
    /// </remarks>
    internal RetryBackoff StoreSearchRetries = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));

    /// <summary>
    /// Il giro del ciclo di estrazione: <c>CheckExtraction</c> quando non e' assegnato.
    /// </summary>
    /// <remarks>
    /// E' assegnabile per la stessa ragione di <see cref="StoreSearchRetries"/>: il giro vero
    /// attraversa il confine e vuole un SQL Server, e un collaudo che voglia provare che <b>un giro
    /// fallito non porta via il ciclo</b> deve poter far fallire un giro. Il ciclo che lo esegue e'
    /// <see cref="Navigator.BackgroundLoop"/>, che e' la forma comune.
    /// </remarks>
    internal Func<Task>? ExtractionTurn;

    /// <summary>Ogni quanto si ripete il giro dell'estrazione. Assegnabile per i collaudi.</summary>
    internal int ExtractionEveryMilliseconds = 2000;

    /// <summary>Il ciclo avviato da <see cref="StartProcessExtractionLu"/>, per chi vuole aspettarne la fine.</summary>
    internal Task? ExtractionLoop;

    #endregion Internal Fields

    #region Private Fields

    /// <summary>
    /// Token di cancellazione utilizzato per l'interruzione delle operazioni di estrazione
    /// </summary>
    private CancellationToken _cancelExtractionToken;

    #endregion Private Fields

    #region Public Methods

    /// <summary>
    /// Rilascia le risorse utilizzate dal manager, inclusa la cancellazione delle operazioni di estrazione in corso
    /// </summary>
    public void Dispose()
    {
        CancellationExtractionTokenSource?.Cancel();
        CancellationExtractionTokenSource?.Dispose();
        CancellationExtractionTokenSource = null;
    }

    #endregion Public Methods

    #region Internal Methods

    /// <summary>
    /// Il comando manuale <c>ItemFindStoringLocation</c>: la ricerca delle celle del gruppo, per il
    /// gruppo o per un suo membro.
    /// </summary>
    /// <returns>True se ogni missione del gruppo ha una posizione di deposito, false altrimenti.</returns>
    /// <remarks>
    /// Nessun deposito rifiutato dal PLC: una missione mandata dall'operatore in un magazzino tiene la
    /// sua cella. Un dispositivo singolo che non appartiene a un gruppo non ha nessuno a cui chiedere
    /// la posizione: prima qui si sollevava NullReferenceException - cioe' proprio nel caso normale di
    /// un dispositivo non raggruppato - e ora si risponde "posizione non trovata". Il canale e'
    /// RecordNavigatorLog e non WriteLog perche' un "false" senza spiegazione lascerebbe l'operatore
    /// che ha lanciato il comando senza sapere perche' non e' successo nulla. I depositi rifiutati dal
    /// PLC passano da <see cref="AnotherCellAfterARefusedDepositAsync"/>.
    /// </remarks>
    internal async Task<bool> FindStoringLocation()
    {
        device.IdCellToDefragment = 0;

        if (GroupStorageManager() is { } groupStorageManager)
            return await groupStorageManager.FindNewStoreLocationGrouped([]) == RefusedDepositOutcomes.Found;

        Navigator.RecordNavigatorLog(device,
            "FindStoringLocation: the device does not belong to a group, no storing location can be assigned",
            LogEntryType.Warning);
        return false;
    }

    /// <summary>
    /// Il deposito rifiutato dal PLC con gli errori 67, 85 e 133, per ogni missione (risposta 1 del
    /// proprietario dell'8/10, che sostituisce le risposte 6 e 7 del 2/10): una cella nuova per le
    /// missioni il cui deposito e' stato rifiutato, esclusa quella rifiutata, e senza un'altra cella lo
    /// scarto. Lo chiama il gestore degli errori (<c>AutomationDeviceConnectionManager</c>), che con
    /// l'esito decide se il dispositivo riparte o va in errore e aspetta l'operatore.
    /// </summary>
    /// <param name="refusedDeposits">
    /// Le missioni il cui deposito il PLC ha appena rifiutato: la missione del dispositivo singolo, o
    /// quelle degli step del movimento di gruppo. Solo queste cambiano cella; le altre del gruppo tengono
    /// la loro (compito 5).
    /// </param>
    /// <param name="worker">Chi chiede, per le righe di registro.</param>
    /// <remarks>
    /// <para>
    /// La ricerca e' quella del dispositivo di oggi. Per la missione dell'operatore e' il flusso del
    /// cambio nel magazzino scelto (<c>MissionGateway.AnotherCellInTheChosenStorageAsync</c>); per quella
    /// della configurazione la ricerca delle celle del gruppo, per il gruppo e per un suo membro
    /// (<see cref="FindNewStoreLocationGrouped"/>), e per un dispositivo singolo fuori da ogni gruppo
    /// quella della sua riverifica prima del comando (<see cref="FindNewStoreLocation"/>), che prima su
    /// questa via non c'era: il rifiuto andava allo scarto senza cercare.
    /// </para>
    /// <para>
    /// Le celle rifiutate dal PLC le esclude ogni domanda al gestionale per la missione
    /// (<see cref="PathMission.CellsRefusedByThePlc"/>, che chi chiama ha gia' aggiornato), finche' l'UDC
    /// non deposita: non piu' solo per la durata di questa ricerca.
    /// </para>
    /// <para>
    /// Una ricerca che cade e' «adesso non si sa»: la riga dice perche', e il dispositivo va in errore e
    /// aspetta. Se a cadere e' il gestionale che non risponde, l'esito lo dice
    /// (<see cref="RefusedDepositOutcomes.WarehouseSilent"/>), e chi chiama fa lasciare alla missione la
    /// porta rifiutata (risposta 7 del proprietario dell'8/10 sera). Prima il guasto risaliva al gestore
    /// del completamento, che lo scriveva e basta, e il dispositivo restava com'era, senza errore e senza
    /// deposito.
    /// </para>
    /// </remarks>
    internal async Task<RefusedDepositOutcomes> AnotherCellAfterARefusedDepositAsync(
        IReadOnlyCollection<PathMission> refusedDeposits, string worker)
    {
        device.IdCellToDefragment = 0;

        try
        {
            if (GroupStorageManager() is { } groupStorageManager)
                return await groupStorageManager.FindNewStoreLocationGrouped(refusedDeposits);

            if (device.LoadedLu is { } loaded && refusedDeposits.Contains(loaded))
                return await AnotherCellOnTheDeviceAsync(loaded, worker);

            Navigator.WriteLog($"{worker}: nessuna missione rifiutata a bordo del dispositivo, nessuna cella nuova da cercare",
                LogEntryType.Warning);
            return RefusedDepositOutcomes.NoPlace;
        }
        catch (Exception failure)
        {
            Navigator.WriteLog(failure is WarehouseUnavailableException
                    ? $"{worker}: il gestionale non risponde alla ricerca di una cella nuova dopo il deposito rifiutato ({failure.Message}): il dispositivo va in errore e aspetta l'operatore"
                    : $"{worker}: la ricerca di una cella nuova dopo il deposito rifiutato e' caduta ({failure.GetType().Name}: {failure.Message}): il dispositivo va in errore e aspetta l'operatore",
                LogEntryType.Warning);
            return failure is WarehouseUnavailableException
                ? RefusedDepositOutcomes.WarehouseSilent
                : RefusedDepositOutcomes.NoAnswer;
        }
    }

    /// <summary>
    /// L'esito di <see cref="AnotherCellAfterARefusedDepositAsync"/> e della ricerca delle celle del
    /// gruppo (<see cref="FindNewStoreLocationGrouped"/>).
    /// </summary>
    internal enum RefusedDepositOutcomes
    {
        /// <summary>Ogni missione ha la sua cella e la rotta verso di lei: quelle rifiutate una cella nuova.</summary>
        Found,

        /// <summary>
        /// Una missione senza un'altra cella andava allo scarto, e mentre se ne calcolava la rotta un altro
        /// l'ha mandata verso un'altra destinazione: e' gia' in coda al giro delle rotte con lo stato di chi
        /// ha vinto (<c>PathMission.RejectionOutcome.TakenOver</c>). Non e' un rifiuto: il dispositivo
        /// riparte, come con <see cref="Found"/> (risposta 5 del proprietario dell'11/10).
        /// </summary>
        TakenOver,

        /// <summary>
        /// Come <see cref="Found"/>, ma almeno una missione non aveva un'altra cella ed e' andata allo
        /// scarto per mancanza di celle. Il dispositivo riparte; per il comando manuale e' «nessuna
        /// posizione», come prima.
        /// </summary>
        Rejected,

        /// <summary>
        /// Una missione non ha un'altra cella e lo scarto non si e' fatto: nessun punto di scarto, o
        /// nessuna rotta verso la cella o verso lo scarto. Il dispositivo va in errore e aspetta.
        /// </summary>
        NoPlace,

        /// <summary>
        /// Adesso non si sa: un cambio o un'altra ricerca tiene la missione, l'operatore ne ha scelto la
        /// destinazione mentre si cercava, o la ricerca e' caduta per un guasto che non e' il gestionale
        /// muto. Il dispositivo va in errore e aspetta; nessun nuovo deposito nella cella rifiutata,
        /// nessuno scarto.
        /// </summary>
        NoAnswer,

        /// <summary>
        /// Come <see cref="NoAnswer"/>, ma perche' il gestionale non risponde alla domanda della cella
        /// nuova: in piu' la missione rifiutata lascia la porta rifiutata, come una cella persa, e il
        /// navigatore le cerca un'altra cella, con le celle rifiutate ancora escluse, quando il
        /// gestionale risponde (risposta 7 del proprietario dell'8/10 sera). Lo fa chi chiama, dopo
        /// l'errore del dispositivo.
        /// </summary>
        WarehouseSilent
    }

    /// <summary>
    /// Chi fa la ricerca delle celle del gruppo per questo dispositivo: lui, se e' il gruppo, o il
    /// gruppo a cui appartiene; <c>null</c> per un dispositivo singolo fuori da ogni gruppo.
    /// </summary>
    private AutomationDeviceStorageManager? GroupStorageManager() =>
        device.DeviceType == DeviceTypes.GroupOfAutomationDevices ? this : device.BelongedGroup?.StorageManager;

    /// <summary>
    /// La missione del dispositivo singolo che non appartiene a un gruppo, dopo che il PLC ha
    /// rifiutato il suo deposito (gli errori 67, 85 e 133, <c>AutomationDeviceConnectionManager.HandleStorageErrors</c>):
    /// una cella nuova, esclusa quella rifiutata, e senza un'altra cella lo scarto (risposta 1 del
    /// proprietario dell'8/10). Con il segno dell'operatore nello stesso magazzino, con il flusso del
    /// cambio (<c>MissionGateway.AnotherCellInTheChosenStorageAsync</c>); della configurazione con la
    /// ricerca della riverifica del ricollocatore (<see cref="FindNewStoreLocation"/>).
    /// </summary>
    /// <remarks>
    /// Il deposito verso la cella rifiutata e' finito, perche' il campo ha risposto, ma il gettone e'
    /// ancora sul dispositivo: si toglie qui, prima, se e' quello di uno step della missione. Il flusso
    /// del cambio non tocca una missione con uno step in volo, e il rigetto lo rimanderebbe («rigetto
    /// rimandato: lo step e' in volo»). L'avvio si conta sulla missione come nella ricerca di gruppo:
    /// nessun cambio dell'operatore comincia intanto, e un cambio gia' cominciato tiene fuori la
    /// ricerca, che allora e' «adesso non si sa». Nessun semaforo preso: le chiamate al gestionale
    /// stanno fuori.
    /// </remarks>
    private async Task<RefusedDepositOutcomes> AnotherCellOnTheDeviceAsync(PathMission mission, string worker)
    {
        if (!mission.TryBeginStepStart())
        {
            Navigator.WriteLog(
                $"{worker}: la missione {mission.MissionId} sta cambiando destinazione, adesso nessuna cella nuova per lei",
                LogEntryType.Information);
            return RefusedDepositOutcomes.NoAnswer;
        }

        try
        {
            var refusedStep = device.RunningStepId;
            if (refusedStep != Guid.Empty && mission.MissionSteps.Any(step => step.StepId == refusedStep))
                device.RunningStepId = Guid.Empty;

            switch (await MissionGateway.AnotherCellInTheChosenStorageAsync(mission, worker))
            {
                case RefusedCellOutcome.AnotherCell:
                    return RefusedDepositOutcomes.Found;

                case RefusedCellOutcome.NoOtherCell:
                    return await ToTheRejectionPointAsync(mission, worker);

                case RefusedCellOutcome.NotNow:
                    return RefusedDepositOutcomes.NoAnswer;

                case RefusedCellOutcome.WarehouseSilent:
                    return RefusedDepositOutcomes.WarehouseSilent;
            }

            // Senza il segno: la missione della configurazione. Il gateway resta (risposta 2 del
            // proprietario dell'8/10 sera).
            switch (await FindNewStoreLocation(keepTheGateway: true))
            {
                case NewStoreLocationResults.Found:
                    return RefusedDepositOutcomes.Found;

                case NewStoreLocationResults.NotFound:
                    return await ToTheRejectionPointAsync(mission, worker);

                default:
                    // Il segno si e' alzato fra le due domande: la ricerca non e' partita, e la missione
                    // aspetta come quando un cambio la tiene. Prima finiva in «adesso non si sa» senza
                    // una riga.
                    Navigator.WriteLog(
                        $"{worker}: la destinazione della missione {mission.MissionId} l'ha scelta l'operatore mentre si cercava, adesso nessuna cella nuova per lei",
                        LogEntryType.Information);
                    return RefusedDepositOutcomes.NoAnswer;
            }
        }
        finally
        {
            mission.EndStepStart();
        }
    }

    /// <summary>
    /// Nessun'altra cella per una missione il cui deposito il PLC ha rifiutato: lo scarto per mancanza
    /// di celle, senza riguardo per il segno dell'operatore, che cade con la riga di avviso del setter
    /// della destinazione. Senza un punto di scarto (o senza una rotta verso di lui) il rigetto non si
    /// fa, e il dispositivo va in errore e aspetta l'operatore (risposta 1 del proprietario dell'8/10).
    /// </summary>
    /// <remarks>
    /// Un rigetto che perde la costruzione della sua rotta non e' un rigetto mancato (risposta 5 del
    /// proprietario dell'11/10): contro uno spostamento si completa dalla posizione nuova, ed e' fatto;
    /// contro un'altra destinazione la missione e' gia' in coda al giro delle rotte. In tutti e due i casi
    /// il dispositivo non va in errore per lei. Prima il falso di quel caso era letto come «nessuno
    /// scarto», e il dispositivo andava in errore con la missione gia' in coda.
    /// </remarks>
    private static async Task<RefusedDepositOutcomes> ToTheRejectionPointAsync(PathMission mission, string worker)
    {
        switch (await mission.RejectToAsync(PathMission.RejectionTypes.NoStoreCellAvailable))
        {
            case PathMission.RejectionOutcome.Rejected:
                return RefusedDepositOutcomes.Rejected;
            case PathMission.RejectionOutcome.TakenOver:
                return RefusedDepositOutcomes.TakenOver;
        }

        Navigator.WriteLog(
            $"{worker}: nessun'altra cella per la missione {mission.MissionId} verso {mission.DestinationItemId}, e la missione non e' andata allo scarto (punto di scarto di {mission.PositionItemId} o del suo gruppo: {mission.NoStoreCellRejectionPoint()}): il dispositivo va in errore e aspetta l'operatore",
            LogEntryType.Warning);
        return RefusedDepositOutcomes.NoPlace;
    }

    /// <summary>
    /// Avvia il processo automatico di estrazione delle unità di carico.
    /// Il processo viene eseguito in un task separato e controlla periodicamente le estrazioni disponibili.
    /// </summary>
    /// <remarks>
    /// Restituiva <c>Task</c> con il <c>while</c> nel proprio corpo, quindi diventava un ciclo di
    /// sfondo **solo** perche' i chiamanti lo scartavano con <c>_ =</c>: chi avesse scritto
    /// <c>await StartProcessExtractionLu()</c> — la forma naturale per un metodo <c>async Task</c> —
    /// avrebbe bloccato il proprio chiamante per sempre. Ora e' <c>void</c> e avvia il ciclo al
    /// proprio interno, come gia' facevano gli altri due processi di sfondo del dispositivo. Si veda
    /// la voce <c>D61</c> del registro in <c>WCS/doc/debito-tecnico.md</c>.
    /// <para>
    /// <b>Il <c>try</c> sta dentro il <c>while</c>, non intorno.</b> Fino alla revisione dell'ondata
    /// 1 il <c>catch</c> stava fuori dal ciclo: c'era, quindi il guasto lasciava una riga, ma la
    /// prima eccezione non di annullamento <b>terminava il ciclo per la vita del processo</b> e
    /// l'estrazione di questo magazzino non ripartiva piu'. Avere un <c>catch</c> non e'
    /// sopravvivere al giro, ed e' la stessa forma gia' riparata su <c>LoadNavigatorData</c>,
    /// <c>StartProcessGroupExecuteStep</c> e il distributore circolare: il ciclo e' quello comune,
    /// <see cref="Navigator.BackgroundLoop"/>. Il commento di prima diceva di aver risolto la voce
    /// <c>B20</c>; non era vero, la morte del ciclo l'aveva resa visibile, non impedita.
    /// </para>
    /// <para>
    /// Il nome che compare nel registro porta l'ASI del dispositivo, perche' la riga viene dal ciclo
    /// e non piu' da <c>AutomationDevice.LogError</c>: senza, un guasto dell'estrazione non direbbe
    /// piu' di quale magazzino si parla. Il livello passa da Avviso a Errore, come per gli altri
    /// tre cicli di sfondo.
    /// </para>
    /// </remarks>
    internal void StartProcessExtractionLu()
    {
        if (CancellationExtractionTokenSource != null) return;

        CancellationExtractionTokenSource = new CancellationTokenSource();
        _cancelExtractionToken = CancellationExtractionTokenSource.Token;

        ExtractionLoop = Navigator.BackgroundLoop(
            $"Extraction [ASI {device.ASI}.{device.SUBITEM}.{device.PARTITION}]",
            ExtractionTurn ?? CheckExtraction,
            () => ExtractionEveryMilliseconds,
            _cancelExtractionToken);
    }

    /// <summary>
    /// Avvia il processo di deposito di un'unità di carico caricata sul dispositivo.
    /// Gestisce la verifica delle informazioni di deposito e l'esecuzione del movimento verso la destinazione.
    /// </summary>
    /// <remarks>
    /// <b>Quando la cella scelta non va piu' bene il deposito non parte.</b> La ricerca di un'altra
    /// cella era un <c>Task</c> scartato e questo metodo proseguiva: leggeva la terna di
    /// destinazione mentre l'altro flusso la riscriveva campo per campo e spediva al campo un
    /// comando il cui <c>RunningStepId</c> poteva essere gia' stato azzerato dalla ricerca stessa.
    /// L'unita' di carico si muoveva davvero, il PLC rispondeva, e la chiusura del passo non
    /// trovava piu' nulla a cui riferirsi: dispositivo fermo, missione aperta, nessuna riga di
    /// errore. Ora la ricerca si attende e il deposito riparte al giro successivo di
    /// <c>ExecuteStep</c>, con la destinazione nuova e la rotta ricostruita.
    /// <para>
    /// <b>L'avvio si conta sulla missione</b> (<c>PathMission.TryBeginStepStart</c>), dalla
    /// verifica della cella al gettone: con un cambio di destinazione dell'operatore in corso il
    /// deposito non parte, e mentre parte il cambio non comincia. La verifica puo' riscegliere la
    /// cella e chiede al gestionale: un cambio che intanto prenotasse per la stessa missione si
    /// toglierebbe la cella a vicenda con lei.
    /// </para>
    /// <para>
    /// <b>La cella di una missione dell'operatore, o prenotata dal motore per la missione, si conferma
    /// prima di riverificarla</b> (<c>PathMission.MayStartTheDeposit</c>), e dopo il freno della
    /// riverifica: la riverifica guarda se la cella e' impegnata, la conferma se e' ancora della
    /// missione presso il gestionale, e la seconda domanda viene prima perche' una cella che non e'
    /// piu' sua non va aspettata, va sostituita (lo fa il navigatore).
    /// </para>
    /// </remarks>
    internal async Task StartStoreLu()
    {
        var mission = device.LoadedLu;

        if (mission != null && !mission.TryBeginStepStart())
        {
            Navigator.WriteLog(
                $"StartStoreLu {device.ItemGuid}: la missione {mission.MissionId} sta cambiando destinazione, il deposito non parte",
                LogEntryType.Information);
            return;
        }

        try
        {
            // La conferma della cella che c'e' adesso e' di questo giro, che la usi o no
            // (PathMission.TakeStorageCellConfirmation): una lasciata li' varrebbe per un giro piu'
            // tardi, quando il gestionale puo' aver lasciato la cella.
            await StartStoreLuCoreAsync(mission, mission?.TakeStorageCellConfirmation());
        }
        finally
        {
            mission?.EndStepStart();
        }
    }

    /// <summary>Il corpo di <see cref="StartStoreLu"/>, con l'avvio gia' contato sulla missione.</summary>
    /// <param name="counted">La missione su cui l'avvio si e' contato.</param>
    /// <param name="confirmedOn">La conferma della sua cella che questo giro ha preso, o <c>null</c>.</param>
    private async Task StartStoreLuCoreAsync(PathMission? counted, Guid? confirmedOn)
    {
        if (device.LoadedLu != null)
        {
            var argLoadedLu = device.LoadedLu;

            // Il freno della riverifica (StoreSearchRetries) viene prima di tutto, come in
            // VerifyStoreInfo: chi aspetta non fa niente, nemmeno andare al navigatore.
            if (VerificationPostponed(argLoadedLu)) return;

            // Una missione dell'operatore, o con una cella prenotata per lei, chiede la conferma della
            // sua cella PRIMA della riverifica (PathMission.MayStartTheDeposit): la riverifica guarda
            // se la cella e' impegnata, non se e' ancora della missione. Dopo, una cella che il
            // gestionale ha lasciato scadere e dato a un'altra UDC, che intanto la impegna, faceva
            // rispondere alla riverifica «la aspetta l'operatore» a ogni giro, e la missione non
            // arrivava mai al navigatore che la riprenota: l'UDC restava sul ricollocatore per sempre.
            // Cosi' a ogni scadenza del freno la domanda al gestionale si rifa', e la riverifica parte
            // solo con la cella confermata.
            if (!argLoadedLu.MayStartTheDeposit(ReferenceEquals(argLoadedLu, counted) ? confirmedOn : null,
                    $"StartStoreLu {device.ItemGuid}"))
                return;

            switch (await VerifyStoreInfo(argLoadedLu))
            {
                case StoreVerificationResults.DestinationChanged:
                    Navigator.WriteLog(
                        $"StartStoreLu {device.ItemGuid}: la cella di deposito della missione {argLoadedLu.MissionId} non va piu' bene, il deposito riparte al giro dopo con la destinazione riscelta",
                        LogEntryType.Information);
                    return;

                // Senza riga di registro, e di proposito: e' il freno, che gira a ogni richiamo
                // ordinario. La riga la scrive lui, di rado, quando conta il fallimento.
                case StoreVerificationResults.SearchPostponed:
                    return;

                // La cella dell'operatore non va bene adesso e non se ne cerca un'altra: il deposito
                // non parte verso una cella impegnata. Anche qui la riga la scrive, di rado,
                // ChooseAnotherStoreLocation.
                case StoreVerificationResults.ChosenByOperator:
                    return;
            }
        }

        // La versione della rotta si legge prima dello step e prima dell'indirizzo della cella: il
        // gettone si scrive solo se e' ancora quella (PathMission.TryTakeTheStepToken, corsa 5 della
        // ricognizione della voce 41, compito 6). Una rotta riscritta durante la lettura dal gestionale
        // fa rinunciare il deposito, che riparte al giro dopo con lo step e la porta nuovi.
        var routeMission = device.LoadedLu;
        var seenRouteVersion = routeMission?.RouteVersion ?? 0;

        if (device.LoadedLu?.StepToDo == null) return;

        // L'indirizzo di deposito si legge PRIMA di prendere i gettoni, come nel prelievo: la
        // lettura passa dal gestionale e puo' fallire, e non ha bisogno ne' della prenotazione ne'
        // di RunningStepId. Stava dopo, e le sue due uscite - la lettura che risponde nulla e la
        // lettura che solleva - lasciavano il gettone addosso al dispositivo, che da quel momento
        // rifiutava ogni esecuzione. E' la stessa forma del guasto del 2026-09-09 sul prelievo.
        var toAsiItem = device.RecordStorageLength switch
        {
            RecordStorageTypes.ConvertDepthToPartition => await GetToAsiItemForDepthConversion(),
            RecordStorageTypes.FromPlc or RecordStorageTypes.FixedLength or RecordStorageTypes.CalculatedStorageDepth =>
                await device.LoadedLu.GetAsiCellInfoAsync(device.LoadedLu.StorageDoorCode),
            _ => null
        };

        if (toAsiItem == null)
        {
            Navigator.WriteLog(
                $"Error On ASI {device.ASI}.{device.SUBITEM}.{device.PARTITION} StartStoreLU->MOVE_LU_TO Description: To ASI Destination is Nothing",
                LogEntryType.Warning);
            return;
        }

        // La ricerca della destinazione qui sopra puo' aver cambiato lo step da eseguire, o averlo
        // tolto del tutto: si rilegge invece di fidarsi di quello di prima.
        if (device.LoadedLu?.StepToDo == null) return;

        // Una missione dell'operatore, o con una cella prenotata per lei, deposita solo nella cella
        // che il gestionale le tiene adesso: la domanda la fa il navigatore, fuori da questo semaforo,
        // e il deposito parte con la sua conferma, prima di ogni gettone (PathMission.MayStartTheDeposit,
        // decisioni del proprietario dell'1/10 e risposta 4 del 2/10). Senza, la missione va al
        // navigatore e il deposito riparte al suo risveglio. La domanda c'e' gia' stata prima della
        // riverifica, con la stessa conferma: qui si rifa' per la missione a bordo adesso, che dopo le
        // attese di sopra puo' essere un'altra.
        if (!device.LoadedLu.MayStartTheDeposit(ReferenceEquals(device.LoadedLu, counted) ? confirmedOn : null,
                $"StartStoreLu {device.ItemGuid}"))
            return;

        // Lo step e la destinazione si risolvono UNA volta, qui, prima del posto: da adesso in poi la
        // sequenza lavora su questi, e chi rinuncia sa che cosa restituire senza doverli rileggere.
        // Il posto si prendeva per lo step letto alla prenotazione e si restituiva per quello riletto
        // dopo: una ricostruzione in mezzo cambiava lo step, il gettone rinunciava, e la restituzione
        // andava allo step nuovo lasciando preso il posto del vecchio (revisione finale della voce 41).
        //
        // Il motivo vale per StepToDo, non per DestinationItem, e il commento di prima li metteva
        // insieme (voce m3 della revisione dell'ondata 1): StepToDo dipende da MissionSteps e in
        // errore puo' gia' rispondere nullo, mentre MissionStep.DestinationItem legge
        // PathDevices[ToNodeId] e sopravvive alla sparizione dello step. Risolverla comunque tiene
        // questa e la gemella ExecutePickUpMovement nella stessa forma.
        if (device.LoadedLu.StepToDo is not { } stepToDo) return;
        var destinationItem = stepToDo.DestinationItem;

        // Il posto sulla destinazione si prende, non si scrive: due mittenti potevano superare lo
        // stesso controllo e sovrascriversi il gettone. Voce D73.
        if (destinationItem is { DeviceType: not DeviceTypes.Storage } destinazione &&
            !destinazione.TryReserveForIncomingStep(stepToDo.StepId))
        {
            Navigator.WriteLog(
                $"StartStoreLu {device.ItemGuid}: la destinazione {destinazione.ItemGuid} e' gia' impegnata per lo step {destinazione.WaitingStepId}, il deposito non parte",
                LogEntryType.Information);
            return;
        }

        device.StepStartPauseForTesting?.Invoke();

        var fromAsiItem = Navigator.GetDataAsi(device);

        // Gettone e stato di lavorazione adiacenti, come in ConfigurePickUpStep: fra i due non deve
        // restare un istante in cui il dispositivo e' Ready con un identificativo di passo addosso.
        // Tutti e due sotto il lucchetto della rotta, solo se la rotta e' ancora quella letta prima
        // dello step: fra il posto e il gettone una ricostruzione poteva togliere lo step, e il
        // deposito partiva lo stesso per uno step che non c'era piu' (corsa 5 della ricognizione della
        // voce 41, compito 6). Se la rotta e' cambiata, o a bordo c'e' un'altra missione, il posto si
        // restituisce e il deposito non parte.
        if (!ReferenceEquals(device.LoadedLu, routeMission))
        {
            Navigator.WriteLog(
                $"StartStoreLu {device.ItemGuid}: a bordo non c'e' piu' la missione {routeMission?.MissionId} letta prima dello step, il deposito non parte",
                LogEntryType.Information);
            if (destinationItem is { DeviceType: not DeviceTypes.Storage })
                destinationItem.ReleaseIncomingStep(stepToDo.StepId);

            // La missione a bordo adesso aspetta un'esecuzione: si chiede per quando il dispositivo e'
            // libero, come dopo la rinuncia qui sotto.
            device.ExecuteAgainWhenFree();
            return;
        }

        if (!routeMission.TryTakeTheStepToken(seenRouteVersion, stepToDo, () =>
            {
                device.RunningStepId = stepToDo.StepId;
                device.Status = DeviceStatusTypes.Unloading;
            }))
        {
            if (destinationItem is { DeviceType: not DeviceTypes.Storage })
                destinationItem.ReleaseIncomingStep(stepToDo.StepId);

            // Come il movimento singolo che rinuncia, il dispositivo riprova con la rotta nuova appena e'
            // libero (AutomationDevice.ExecuteAgainWhenFree, risposta 4 del proprietario dell'11/10): chi
            // ha riscritto la rotta lo richiama mentre il deposito e' ancora dentro la sua esecuzione, ed
            // e' rifiutato. Senza, l'UDC aspettava su questo dispositivo il prossimo richiamo.
            device.ExecuteAgainWhenFree();
            return;
        }

        try
        {
            await Task.Delay(200);

            stepToDo.StartStep = DateTime.Now;
            stepToDo.RunningItem = device;

            device.DeviceConnection?.MoveLuFromAsiToAsi(Navigator.GetMovementRunAsi(device), fromAsiItem, fromAsiItem,
                toAsiItem, device.RunningStepId, device.LoadedLu.LuId, device.LoadedLu.HandlingMode);
        }
        catch (Exception ex)
        {
            device.RunningStepId = Guid.Empty;
            device.Status = DeviceStatusTypes.Ready;

            // Anche il posto, esplicitamente. L'azzeramento del gettone qui sopra lo restituirebbe
            // per conto suo - il setter cerca il prenotato in PathDevices - ma solo se lo step e'
            // ancora in MissionSteps: se nel frattempo un altro flusso ha chiamato
            // RemoveMissionSteps sulla stessa missione (ResetLuProperties lo fa), il getter
            // risponde gia' Guid.Empty, il setter esce subito e nessuno restituisce il posto. E'
            // la scelta che HandleGetFromError dichiara sul posto: dirlo non costa e non dipende
            // da quel meccanismo.
            destinationItem?.ReleaseAfterFailedStep(stepToDo.StepId);

            device.LogError(ex);
        }
    }

    /// <summary>
    /// Il deposito e' bloccato quando la cella e' piena o gia' in fase di carico da un'altra
    /// missione (<see cref="CellLockState.Full"/>, <see cref="CellLockState.Loading"/>): una cella
    /// bloccata solo al prelievo (<see cref="CellLockState.Unloading"/>) resta valida per il
    /// deposito. E' proprio la distinzione che <c>CellDescription.Locked</c> booleano non poteva
    /// esprimere (compito 7) - internal, non per farsi chiamare da fuori, ma perche' il test la
    /// raggiunga senza dover costruire l'intero dispositivo.
    /// </summary>
    internal static bool BlocksStoring(CellLockState lockState) =>
        lockState is CellLockState.Full or CellLockState.Loading;

    /// <summary>
    /// L'esito della verifica della cella di deposito, prima che il comando parta verso il campo.
    /// </summary>
    internal enum StoreVerificationResults
    {
        /// <summary>La cella scelta va ancora bene: il deposito puo' partire.</summary>
        DestinationConfirmed,

        /// <summary>
        /// La cella non va piu' bene. La destinazione e' stata riscelta - o si e' provato a
        /// riscegliarla e non c'era posto: in tutt'e due i casi il deposito <b>non parte in questo
        /// giro</b>, perche' la destinazione che la missione ha in mano e' quella che si e' appena
        /// dichiarata inutilizzabile.
        /// </summary>
        DestinationChanged,

        /// <summary>
        /// La verifica non e' stata fatta: la ricerca precedente non aveva trovato posto e il freno
        /// non e' ancora scaduto (<see cref="StoreSearchRetries"/>). Come
        /// <see cref="DestinationChanged"/> il deposito non parte, ma senza riga di registro: chi
        /// aspetta non ha niente da raccontare a ogni giro.
        /// </summary>
        SearchPostponed,

        /// <summary>
        /// La cella non va piu' bene, ma la destinazione l'ha scelta l'operatore
        /// (<see cref="PathMission.OperatorDestination"/>): non se ne cerca un'altra, e la missione
        /// resta com'e' - porta, indirizzo, prenotazione presso il gestionale, destinazione, gateway
        /// e rotta. Il deposito non parte in questo giro; passato il freno (<see cref="StoreSearchRetries"/>)
        /// la missione torna al navigatore, che conferma presso il gestionale che la cella e' ancora
        /// sua (se non lo e' piu', ne prenota un'altra nel magazzino scelto), e la cella si
        /// riverifica: quando torna buona il deposito parte verso di lei. Finche' non torna buona
        /// l'UDC resta sul dispositivo: cambiare una cella che e' ancora della missione e' un nuovo
        /// cambio di destinazione dell'operatore, non una scelta del motore.
        /// </summary>
        ChosenByOperator
    }

    #endregion Internal Methods

    #region Private Methods

    /// <summary>
    /// Registra informazioni di debug per un'unità di carico UDC
    /// </summary>
    /// <param name="row">La riga di dati contenente le informazioni UDC</param>
    /// <param name="message">Il messaggio di debug da registrare</param>
    private static async Task LogUdcDebugInfo(DataRow row, string message)
    {
        var udcId = row.GetValueOrThrow<decimal>("ID_UDC");
        if (udcId > 0)
        {
            Navigator.WriteLog(
                $"DEBUG INFO: {message} UDC: {udcId} - {(await Navigator.RequireWarehouse($"LogUdcDebugInfo sull'unita' di carico {udcId}").DescribeLoadUnitAsync(udcId))?.Code} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                LogEntryType.Information);
        }
    }

    /// <summary>
    /// Resetta le proprietà di un'unità di carico alla configurazione predefinita
    /// </summary>
    /// <param name="lu">L'unità di carico da resettare</param>
    /// <remarks>
    /// Il reset e' per missione. Una missione con la destinazione scelta dall'operatore
    /// (<see cref="PathMission.OperatorDestination"/>) tiene il gateway e l'indirizzo della cella
    /// (risposte 6 e 7 del proprietario del 2/10, decisione 9 delle undici, confermate dalla risposta 1
    /// dell'8/10): il gateway l'ha deciso l'operatore, e l'indirizzo e' quello della cella prenotata,
    /// con la sua profondita', che riletto dalla porta non l'avrebbe. Perde la rotta come le altre: la
    /// ricerca del gruppo gliela rifa', verso un'altra cella del suo magazzino se il PLC ha rifiutato la
    /// sua (<see cref="FindNewStoreLocationGrouped"/>).
    /// </remarks>
    /// <param name="keepTheGateway">
    /// Vero per una missione il cui deposito il PLC ha appena rifiutato: il gateway resta anche senza il
    /// segno, e la rotta verso la cella nuova passa ancora da lui (risposta 2 del proprietario dell'8/10
    /// sera). Il motore non sa chi ha scelto il gateway di una missione senza il segno, e lo tiene
    /// comunque; se la missione va allo scarto, il gateway lo toglie lo scarto (<c>PathMission.RejectTo</c>).
    /// </param>
    private static void ResetLuProperties(PathMission lu, bool keepTheGateway)
    {
        if (!lu.OperatorDestination)
        {
            if (!keepTheGateway) lu.Gateway = Guid.Empty;
            lu.DestinationAsi = string.Empty;
            lu.DestinationSubitem = string.Empty;
            lu.DestinationPartition = string.Empty;
        }

        lu.SourceAsi = string.Empty;
        lu.SourceSubitem = string.Empty;
        lu.SourcePartition = string.Empty;
        lu.RemoveMissionSteps(false);
    }

    /// <summary>
    /// Configura un'unità di carico utilizzando le informazioni di un selettore di posizione
    /// </summary>
    /// <param name="lu">L'unità di carico da configurare</param>
    /// <param name="selector">Il selettore contenente le informazioni di destinazione</param>
    /// <returns>
    /// False se la destinazione l'ha scelta l'operatore: il selettore non si prende, e la missione
    /// resta com'e' (<see cref="PathMission.UnlessChosenByOperator"/>).
    /// </returns>
    private static async Task<bool> SetLuFromSelector(PathMission lu, AutomationDevice.LuSearchLocationByTypeLu selector)
    {
        // Item 54 (answer 8 of the owner of 13/10): a mission bound for a device that is not a storage
        // keeps its destination (PathMission.KeepsItsDestination), like the operator's choice.
        // Item 54, decision 5: nor does a mission on the relocator the operator sent it to, with a choice
        // of what the relocator does with it (PathMission.RelocatorArrivalApplies).
        if (lu.OperatorDestination || lu.KeepsItsDestination || lu.RelocatorArrivalApplies || !lu.UnlessChosenByOperator(() =>
            {
                lu.StorageDoorCode = selector.StorageDoorCode;
                lu.DestinationItemId = selector.DestinationItemId;
                lu.Status = MissionStatusTypes.Run;
            }))
            return false;

        await lu.BuildPathToDestination();
        return true;
    }

    /// <summary>
    /// Controlla se ci sono estrazioni da processare e esegue gli step necessari
    /// </summary>
    /// <returns>Un task che rappresenta l'operazione asincrona di controllo</returns>
    private async Task CheckExtraction()
    {
        if (Navigator.NavStatus != NavigatorStatusTypes.Run || device.Status != DeviceStatusTypes.Ready)
            return;

        if (device.LoadedLu == null && device.LUtoPick == null && device.QueuedLuToPickUpCount == 0 &&
            device.CanExtractLUs)
        {
            // Stessa disciplina degli altri punti di presa: si aspetta con un tetto di tempo, non
            // indefinitamente. Il token resta, cosi' l'arresto del processo continua a essere
            // immediato, ma la sua eccezione ora e' catturata: prima usciva da CheckExtraction,
            // faceva morire il ciclo di StartProcessExtractionLu e l'estrazione di questo
            // magazzino non ripartiva piu'.
            var acquired = false;

            try
            {
                acquired = await Navigator.SyncExtraction.WaitAsync(
                    Navigator.SyncExtractionTimeout, _cancelExtractionToken);

                if (!acquired)
                    Navigator.WriteLog(
                        $"[ASI {device.ASI}] Timeout acquiring extraction lock in CheckExtraction",
                        LogEntryType.Warning);
                else
                    await ProcessExtractionLu();
            }
            catch (OperationCanceledException)
            {
                // Arresto del processo: si esce in silenzio, non e' un errore.
                return;
            }
            catch (Exception ex)
            {
                device.LogError(ex);
            }
            finally
            {
                if (acquired) Navigator.SyncExtraction.Release();
            }
        }

        _ = device.ExecuteStep();
    }

    /// <summary>
    /// Estrae un'unità di carico target e crea il comando di movimento appropriato
    /// </summary>
    /// <param name="bufferCellsNotExtractable">Lista delle celle buffer non estraibili</param>
    /// <param name="doorCodeNotExtractable">Lista dei codici porta non utilizzabili</param>
    /// <param name="row">La riga di dati contenente le informazioni dell'unità di carico</param>
    /// <param name="navigatorCommand">Il comando del navigator da configurare</param>
    private async Task ExtractTargetLu(List<decimal> bufferCellsNotExtractable, List<string?> doorCodeNotExtractable,
        DataRow row, Command navigatorCommand)
    {
        // Compito 16: ex CelleMagazzino.DecodificaCella, di cui si leggeva la sola porta A. Il
        // metodo vecchio rispondeva annullabile e qui si scriveva ?.CODICE_PORTA_A;
        // DescribeCellAsync risponde non annullabile e tratta l'assenza come guasto, quindi la
        // tolleranza per «nessuna cella polmone» diventa una guardia esplicita sullo zero, la
        // stessa di MoveObstacleLu qui sotto. Chiamata diretta: la missione nasce dopo, in
        // device.AddMissionToPickUp.
        var bufferCellId = row.GetValueOrThrow<decimal>("ID_CELLA_POLMONE");
        var bufferDoorCodeA = bufferCellId == 0
            ? null
            : (await Navigator.RequireWarehouse($"DescribeCellAsync sulla cella polmone {bufferCellId}")
                .DescribeCellAsync(bufferCellId)).DoorCodeA;

        navigatorCommand.Date = DateTime.Now;
        navigatorCommand.CommandType = Command.CommandTypes.MoveLuAutomatic;
        navigatorCommand.LuId = row.GetValueOrThrow<decimal>("ID_UDC");
        navigatorCommand.PickUpDoorCode = row.GetValueOrThrow<string>("CODICE_PORTA");
        navigatorCommand.StorageDoorCode = bufferDoorCodeA;
        navigatorCommand.Status = Command.StatusTypes.Done;
        navigatorCommand.WorkstationId = row.GetValueOrThrow<string>("ID_POSTAZIONE");
        navigatorCommand.BufferCellId = row.GetValueOrThrow<decimal>("ID_CELLA_POLMONE");

        if (await device.AddMissionToPickUp(navigatorCommand, row, MissionTypes.Extraction)) return;

        if (navigatorCommand.BufferCellId > 0)
            bufferCellsNotExtractable.Add(navigatorCommand.BufferCellId);
        if (!string.IsNullOrEmpty(navigatorCommand.PickUpDoorCode))
            doorCodeNotExtractable.Add(navigatorCommand.PickUpDoorCode);
    }

    /// <summary>
    /// Trova una nuova posizione di deposito per l'unità di carico attualmente caricata
    /// </summary>
    /// <returns>
    /// <see cref="NewStoreLocationResults.Found"/> se e' stata trovata una nuova posizione,
    /// <see cref="NewStoreLocationResults.NotFound"/> se no, <see cref="NewStoreLocationResults.ChosenByOperator"/>
    /// se la destinazione l'ha scelta l'operatore e la ricerca non e' partita.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Il blocco al deposito della cella vecchia serve solo a non farsi riproporre dalla ricerca la
    /// cella da cui si sta scappando, e finita la ricerca non serve piu'. Prima non veniva tolto ne'
    /// sul percorso di successo ne' su quello di fallimento, e poiche' nessun altro percorso del
    /// motore lo toglie - <c>HandlePickUpAndStoreAgainMission</c> sblocca la propria cella su
    /// un'altra strada, e la ricarica dell'impianto conserva il valore - ogni cambio di destinazione
    /// lasciava dietro di se' una cella che nessuno poteva piu' riempire.
    /// </para>
    /// <para>
    /// <b>Si toglie solo se l'ha messo questa ricerca.</b> Il flag non ha un proprietario: e' un
    /// booleano scritto su tutte le righe della porta (<c>DoorMappingStore.SetStoringLockedAsync</c>),
    /// e c'e' un secondo scrittore con un'intenzione diversa - <c>PathMission.AllocateOnStorage</c>,
    /// sotto l'opzione d'impianto <c>LockCellOnStoringError</c>, mette la cella <b>in quarantena</b>
    /// perche' la quota di deposito rilevata supera la profondita' della cella, ed e' un fatto
    /// fisico, sulla porta della cella che questa stessa missione ha in mano. Uno sblocco
    /// incondizionato rimetteva in servizio, in silenzio, una cella messa fuori servizio. Quindi lo
    /// stato si legge prima di scrivere, e il <c>finally</c> lo rimette com'era.
    /// </para>
    /// <para>
    /// La variante di gruppo (<see cref="FindNewStoreLocationGrouped"/>) fa lo stesso con
    /// <c>GroupedStorageScope</c>, che sblocca solo le porte che ha bloccato lei, in un <c>finally</c>
    /// (voce D123 del registro in <c>WCS/doc/debito-tecnico.md</c>).
    /// </para>
    /// <para>
    /// <b>La cella scelta dall'operatore non si sostituisce</b> (compito 2 del piano delle
    /// missioni). Con il segno (<see cref="PathMission.OperatorDestination"/>) la ricerca non parte:
    /// la cella fa parte della scelta (il cambio verso un magazzino), e cercarne un'altra vorrebbe
    /// dire rifare la scelta al posto dell'operatore. Il segno si guarda prima di ogni effetto,
    /// perche' gli effetti erano proprio il guasto: il gateway azzerato era quello deciso
    /// dall'operatore, la prenotazione lasciata era la sua, e la cella nuova si fermava poi sulla
    /// guardia di scrittura (<c>UnlessChosenByOperator</c>) e tornava al gestionale - la missione
    /// restava senza prenotazione, con la porta della cella vecchia, e ogni giro del freno rifaceva
    /// lo stesso. Se la cella non e' piu' della missione la sostituisce il navigatore, prima di
    /// arrivare qui (<c>StartStoreLu</c>, <c>PathMission.MayStartTheDeposit</c>). Questo vale per la
    /// riverifica prima del comando; dopo che il PLC ha rifiutato il deposito (gli errori 85, 133 e
    /// 67) la cella si sostituisce, nello stesso magazzino scelto
    /// (<see cref="AnotherCellAfterARefusedDepositAsync"/>, <see cref="FindNewStoreLocationGrouped"/>;
    /// risposta 1 del proprietario dell'8/10). Per la missione della configurazione di un dispositivo
    /// fuori da ogni gruppo questa ricerca e' anche quella dopo il deposito rifiutato, con le celle
    /// rifiutate escluse (<see cref="PathMission.CellsRefusedByThePlc"/>).
    /// </para>
    /// <para>
    /// Lo sguardo e' senza lucchetto, e basta: questa ricerca e' contata come l'avvio di uno step
    /// (<c>StartStoreLu</c>, <c>PathMission.TryBeginStepStart</c>), e con un avvio contato il cambio
    /// di destinazione dell'operatore non comincia, quindi il segno non si alza mentre la ricerca
    /// gira; e un cambio cominciato prima la tiene fuori. Lo stesso conto esclude la contesa
    /// dell'unica prenotazione che il gestionale tiene per la missione, fra questa ricerca e la
    /// prenotazione del cambio.
    /// </para>
    /// </remarks>
    /// <param name="keepTheGateway">
    /// Vero dopo un deposito rifiutato dal PLC (<see cref="AnotherCellOnTheDeviceAsync"/>): il gateway
    /// della missione resta, e la rotta verso la cella nuova passa ancora da lui (risposta 2 del
    /// proprietario dell'8/10 sera; il motore non sa chi l'ha scelto, e lo tiene comunque). Falso per la
    /// riverifica prima del comando, che lo toglie come prima.
    /// </param>
    private async Task<NewStoreLocationResults> FindNewStoreLocation(bool keepTheGateway)
    {
        if (device.LoadedLu == null || device.LoadedLu.LuId == 0)
            return NewStoreLocationResults.NotFound;

        if (device.LoadedLu.OperatorDestination)
            return NewStoreLocationResults.ChosenByOperator;

        StoringSelectionTypes selectionType;

        // Il codice porta si prende adesso: la ricerca, se trova posto, riscrive StorageDoorCode con
        // quello della cella nuova, e dopo non ci sarebbe piu' modo di sapere quale sbloccare.
        var doorLockedForSearch = string.Empty;

        if (device.IsLinkedToStorage)
        {
            selectionType = StoringSelectionTypes.StoreInLinkedCell;

            // Bloccata gia' prima: il blocco e' di un altro - la quarantena di AllocateOnStorage - e
            // qui non si tocca niente, ne' mettendolo ne' togliendolo. La ricerca non ha comunque
            // bisogno di metterlo: una porta gia' bloccata non le viene riproposta.
            if (!string.IsNullOrEmpty(device.LoadedLu.StorageDoorCode) &&
                !await DbInfo.GetDoorLockedForStoring(device.LoadedLu.StorageDoorCode))
            {
                doorLockedForSearch = device.LoadedLu.StorageDoorCode;
                await DbInfo.SetCellLockedForStoring(doorLockedForSearch);
            }
        }
        else
        {
            selectionType = StoringSelectionTypes.StoreInReachableCell;
        }

        try
        {
            if (!keepTheGateway) device.LoadedLu.Gateway = Guid.Empty;

            // La prenotazione che la missione aveva presso il gestionale si lascia, non si tiene
            // (compito 17): questo metodo parte perche' quella cella non va piu' bene - occupata da
            // un'estrazione, bloccata, o senza quota - e tenerla vorrebbe dire farsi rispondere la
            // stessa. Cosi' la posizione presso il gestionale torna libera per chi puo' usarla, e la
            // missione non ci resta attaccata nemmeno se qui sotto non si trova niente. Il blocco al
            // deposito della cella - che e' un dato del WCS, un'altra cosa dalla prenotazione - lo
            // toglie il finally qui sotto, se e' stato questo metodo a metterlo.
            if (!await device.LoadedLu.GetStoreToWarehouse(selectionType, keepExistingReservation: false))
                return NewStoreLocationResults.NotFound;

            // L'identificativo del passo si azzera perche' il passo che stava per partire era verso
            // la cella vecchia, e la destinazione adesso e' un'altra: la rotta va rifatta, e un
            // identificativo di un passo che non si eseguira' piu' non deve restare addosso al
            // dispositivo. Non e' innocua «perche' tanto qui non si arriva con il campo valorizzato»:
            // la guardia che lo garantirebbe (AutomationDevice.IsBusyWithCommand) non protegge
            // gli stati Warning ed Error, che dichiara non impegnati anche a RunningStepId
            // valorizzato, e il percorso che arriva fin qui non ha altre guardie di stato -
            // IsReadyToTransferLu ammette Warning per progetto. In quegli stati l'identificativo puo'
            // essere quello che il ritentativo di gruppo si tiene per sapere che cosa ripetere; ma
            // per arrivare qui la ricerca deve avere trovato una cella nuova, e da quel momento
            // quello che c'era da ripetere non c'e' piu'.
            device.RunningStepId = Guid.Empty;
            return NewStoreLocationResults.Found;
        }
        finally
        {
            // Vuoto anche quando la porta c'era ma era gia' bloccata: quel blocco non e' di questa
            // ricerca e non si tocca.
            if (!string.IsNullOrEmpty(doorLockedForSearch))
                await DbInfo.SetCellUnlockedForStoring(doorLockedForSearch);
        }
    }

    /// <summary>
    /// L'esito di <see cref="FindNewStoreLocation"/>.
    /// </summary>
    private enum NewStoreLocationResults
    {
        /// <summary>Trovata un'altra cella: porta, indirizzo e destinazione sono quelli nuovi.</summary>
        Found,

        /// <summary>
        /// Nessuna cella, o nessuna unita' di carico da depositare: la missione tiene la porta di
        /// prima, e la prenotazione solo se la ricerca non e' arrivata al gestionale (che la lascia
        /// prima di cercare).
        /// </summary>
        NotFound,

        /// <summary>
        /// La destinazione l'ha scelta l'operatore: la ricerca non e' partita, e niente e' stato
        /// toccato - ne' il gateway, ne' la prenotazione, ne' il blocco della porta.
        /// </summary>
        ChosenByOperator
    }

    /// <summary>
    /// Trova nuove posizioni di deposito per un gruppo di unità di carico
    /// </summary>
    /// <param name="refusedDeposits">
    /// Le missioni il cui deposito il PLC ha appena rifiutato (<see cref="AnotherCellAfterARefusedDepositAsync"/>);
    /// nessuna per il comando manuale (<see cref="FindStoringLocation"/>).
    /// </param>
    /// <returns>
    /// <see cref="RefusedDepositOutcomes.Found"/> se ogni missione ha la sua posizione;
    /// <see cref="RefusedDepositOutcomes.Rejected"/> se in piu' qualcuna e' andata allo scarto per
    /// mancanza di celle; <see cref="RefusedDepositOutcomes.NoPlace"/> se per una non c'e' posto e lo
    /// scarto non si e' fatto; <see cref="RefusedDepositOutcomes.NoAnswer"/> se per una missione
    /// rifiutata adesso non si sa; <see cref="RefusedDepositOutcomes.WarehouseSilent"/> se il gestionale
    /// non risponde alla domanda della cella nuova di una missione dell'operatore rifiutata. Il gestionale
    /// che non risponde alla ricerca della configurazione solleva, come prima: dopo un rifiuto del PLC lo
    /// raccoglie <see cref="AnotherCellAfterARefusedDepositAsync"/>, che ne fa anche lui
    /// <see cref="RefusedDepositOutcomes.WarehouseSilent"/> (la missione lascia poi la porta rifiutata,
    /// risposta 7 del proprietario dell'8/10 sera); per il comando manuale (<see cref="FindStoringLocation"/>)
    /// arriva a chi esegue il comando.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Il lavoro sulle missioni dei membri - che qui comincia togliendo loro la rotta, e a quelle
    /// senza il segno dell'operatore anche l'indirizzo, e il gateway se il PLC non ha rifiutato il loro
    /// deposito (<see cref="ResetLuProperties"/>) - si conta su ogni missione come l'avvio di uno step (<c>PathMission.TryBeginStepStart</c>, compito 3
    /// del piano delle missioni): una missione
    /// che sta cambiando destinazione per mano dell'operatore resta fuori, e il suo cambio non perde
    /// la rotta appena scritta; mentre il gruppo ci lavora, il cambio non comincia. Se fuori resta una
    /// missione il cui deposito il PLC ha rifiutato, adesso non si sa, e la ricerca non tocca niente:
    /// il gruppo va in errore e aspetta (risposta 1 del proprietario dell'8/10), invece di ripartire
    /// verso la cella rifiutata.
    /// </para>
    /// <para>
    /// <b>La missione mandata dall'operatore in un magazzino</b> (risposta 1 del proprietario
    /// dell'8/10, che sostituisce le risposte 6 e 7 del 2/10): la ricerca parte dopo che il PLC ha
    /// rifiutato il deposito (gli errori 67, 85 e 133 del gruppo o di un membro), e per lei e' un'altra
    /// cella nello stesso magazzino, esclusa quella rifiutata, con il flusso del cambio
    /// (<c>MissionGateway.AnotherCellInTheChosenStorageAsync</c>). Il reset le lascia gateway e
    /// indirizzo; destinazione e segno restano, cambiano porta, indirizzo e rotta. Senza un'altra cella
    /// va allo scarto per mancanza di celle, e il segno cade con la riga di avviso; senza un punto di
    /// scarto la ricerca risponde «nessun posto». Se adesso non si sa (il gestionale non risponde,
    /// nessun percorso verso la porta proposta, un'altra ricerca la tiene) la ricerca risponde «adesso
    /// non si sa», e il gruppo va in errore: le tre regole di prima (tenere la cella, ritentarla, lo
    /// scarto) non ci sono piu'. Se la prenotazione aveva gia' lasciato la cella rifiutata, la missione
    /// la perde (<c>MissionGateway.LoseTheCell</c>), e la cella gliela riprenota il navigatore nel
    /// magazzino scelto. Lo step del deposito rifiutato lo toglie il reset, prima: il flusso del cambio
    /// non tocca una missione con uno step in volo, e per lui lo step del movimento di gruppo lo
    /// sarebbe, con il gettone ancora sul gruppo (lo azzera chi chiama, dopo la ricerca).
    /// </para>
    /// <para>
    /// <b>La missione della configurazione</b> il cui deposito e' stato rifiutato si cerca con il giro
    /// di sempre, con le celle rifiutate escluse dalla domanda (<see cref="PathMission.CellsRefusedByThePlc"/>):
    /// prima la ricerca teneva la prenotazione della cella rifiutata, e la ridava uguale. Lo scarto
    /// quando il gestionale non ha celle lo fa il giro stesso (<c>Navigator.FindWhereStoreLu</c>).
    /// </para>
    /// <para>
    /// <b>Solo la missione il cui deposito e' stato rifiutato</b> (<paramref name="refusedDeposits"/>)
    /// si tocca (risposta 8 del proprietario dell'8/10 sera): le altre a bordo non si contano, non si
    /// azzerano e non si cercano, e tengono cella, rotta e gateway mentre si cerca e anche quando il
    /// gestionale non risponde. Prima il giro azzerava tutte le missioni contate prima di sapere se la
    /// rifiutata aveva un posto, e con il gestionale muto le altre restavano senza rotta (quelle della
    /// configurazione anche senza gateway). Il comando manuale <c>ItemFindStoringLocation</c>
    /// (<see cref="FindStoringLocation"/>), senza missioni rifiutate, le cerca tutte come prima: una
    /// missione dell'operatore tiene la cella, il gateway e l'indirizzo, e la rotta verso di lei si
    /// rifa' (il ramo della scelta dell'operatore di <c>FindWhereStoreLu</c>). Prima del primo giro di
    /// correzioni del compito 5 la ricerca sostituiva la cella a ogni missione con il segno del gruppo,
    /// e senza un'altra cella nel suo magazzino mandava allo scarto una missione la cui cella andava bene.
    /// </para>
    /// <para>
    /// <b>Le copie della porta di una missione rifiutata</b> che ne puo' avere
    /// (<c>MissionGateway.MayHaveCopies</c>: anche con il segno dell'operatore, se la cella e' prenotata per
    /// lei e l'operatore le ha cambiato solo il gateway; non una copia con il segno, che resta una copia),
    /// quando non sono rifiutate anche loro - stanno in un altro movimento - si
    /// fermano prima della ricerca (<c>MissionGateway.StopTheCopiesOfTheRefusedDoors</c>), e se la missione
    /// cambia cella, va allo scarto o perde la cella lasciano la porta rifiutata: le rifa' la ricerca per
    /// tipo del gruppo (<c>MissionGateway.LeaveTheDoorsThatChanged</c>, risposta 8 dell'8/10 e punto 3
    /// dell'8/10 sera). Senza un altro posto per lei, o con «adesso non si sa», tengono la porta come lei.
    /// Una copia che non si puo' fermare fa «adesso non si sa». Prima del terzo giro le contava la ricerca
    /// con tutte le altre; dopo la risposta 8 dell'8/10 sera, fino a questa correzione, restavano verso la
    /// cella rifiutata, che nessuna missione teneva piu', e ci depositavano da sole.
    /// </para>
    /// <para>
    /// Una missione andata allo scarto non ferma piu' il giro: le altre missioni contate si cercano lo
    /// stesso (dopo un rifiuto del PLC sono solo quelle rifiutate; con il comando manuale tutte), e il
    /// gruppo riparte con tutte (prima il giro si fermava li', e le missioni dopo restavano senza
    /// rotta). Senza posto per una missione, o con «adesso non si sa», il giro si ferma come prima.
    /// </para>
    /// <para>
    /// <b>La missione della configurazione che ha perso la cella</b> (<c>PathMission.StorageCellLost</c>)
    /// non si cerca qui, come nel giro del gruppo (<c>AutomationDevice.VerifyWhereAllocateLuGrouped</c>):
    /// non prende il selettore del suo tipo, che le darebbe la porta di un'altra missione - anche quella che
    /// il PLC le ha appena rifiutato, se era una copia - e la cella gliela prenota il navigatore
    /// (<c>MissionGateway.KeepTheStorageCellAsync</c>), con le celle rifiutate escluse. Ci arriva solo il
    /// comando manuale: una missione rifiutata ha una porta, e non ha la cella persa. Prima il comando
    /// manuale le dava il selettore, e il terzo giro di correzioni del compito 13 l'aveva tolto solo dal
    /// giro del gruppo (revisione finale del terzo giro).
    /// </para>
    /// </remarks>
    private async Task<RefusedDepositOutcomes> FindNewStoreLocationGrouped(IReadOnlyCollection<PathMission> refusedDeposits)
    {
        // Dopo un deposito rifiutato si tocca solo la missione rifiutata: le altre a bordo tengono
        // cella, rotta e gateway, mentre si cerca e se il gestionale non risponde (risposta 8 del
        // proprietario dell'8/10 sera), e non si contano nemmeno. Il comando manuale le cerca tutte.
        // Fanno eccezione le copie della porta di una missione rifiutata, anche in un altro movimento:
        // ferme mentre lei cerca, la lasciano se lei cambia cella, e le rifa' la ricerca per tipo del
        // gruppo (MissionGateway.LeaveTheDoorsThatChanged, punto 3 dell'8/10 sera).
        var worker = $"{nameof(FindNewStoreLocationGrouped)} {device.ItemGuid}";
        var copies = refusedDeposits.Count == 0 ? [] : MissionGateway.StopTheCopiesOfTheRefusedDoors(refusedDeposits, worker);
        if (copies == null) return RefusedDepositOutcomes.NoAnswer;

        try
        {
            var started = PathMission.TryBeginStepStarts(
                device.LUsLoaded.Where(lu => refusedDeposits.Count == 0 || refusedDeposits.Contains(lu)), worker);

            try
            {
                if (refusedDeposits.FirstOrDefault(refused => !started.Contains(refused)) is { } heldBack)
                {
                    Navigator.WriteLog(
                        $"{worker}: la missione {heldBack.MissionId}, il cui deposito il PLC ha rifiutato, sta cambiando destinazione: adesso nessuna cella nuova",
                        LogEntryType.Information);
                    return RefusedDepositOutcomes.NoAnswer;
                }

                return await FindNewStoreLocationGroupedAsync(started, refusedDeposits, worker);
            }
            finally
            {
                foreach (var mission in started)
                    mission.EndStepStart();
            }
        }
        finally
        {
            MissionGateway.LeaveTheDoorsThatChanged(copies);
        }
    }

    /// <summary>Il corpo di <see cref="FindNewStoreLocationGrouped"/>, sulle sole missioni contate.</summary>
    /// <param name="started">Le missioni su cui il lavoro si e' contato: le altre restano fuori.</param>
    /// <param name="refusedDeposits">Le missioni il cui deposito il PLC ha appena rifiutato.</param>
    /// <param name="worker">Chi lavora, per le righe di registro: quello di <see cref="FindNewStoreLocationGrouped"/>.</param>
    private async Task<RefusedDepositOutcomes> FindNewStoreLocationGroupedAsync(List<PathMission> started,
        IReadOnlyCollection<PathMission> refusedDeposits, string worker)
    {
        if (device.LUsLoaded is { Count: 0 })
            return RefusedDepositOutcomes.NoPlace;
        var scope = new GroupedStorageScope(device, (resource, failure) =>
            Navigator.WriteLog(
                $"Grouped storage cleanup failed for {resource} on {device.ItemGuid}: {failure}",
                LogEntryType.Error));
        Exception? primaryFailure = null;

        try
        {
            device.Status = DeviceStatusTypes.Recovering;
            var rejected = false;

            foreach (var lu in device.LUsLoaded.OfType<PathMission>().Where(started.Contains))
            {
                ResetLuProperties(lu, keepTheGateway: refusedDeposits.Contains(lu));

                if (device.IsLinkedToStorage && !string.IsNullOrEmpty(lu.StorageDoorCode))
                    await scope.TryAcquireDoorAsync(lu.StorageDoorCode);
            }

            var idTypes = new List<AutomationDevice.LuSearchLocationByTypeLu>();

            foreach (var lu in device.LUsLoaded)
            {
                if (lu?.PositionItem == null || !started.Contains(lu)) continue;

                var currentItemStatus = lu.PositionItem.Status;
                scope.TrackPosition(lu.PositionItem);
                lu.PositionItem.Status = DeviceStatusTypes.Recovering;

                // Il magazzino scelto dall'operatore, se il PLC ha rifiutato proprio il suo deposito:
                // un'altra cella li', esclusa quella rifiutata, e senza un'altra lo scarto (risposta 1
                // del proprietario dell'8/10). Trovata, la rotta la guarda il giro di sempre qui sotto,
                // che per lei va dritto alla destinazione e tiene quella appena fatta verso la cella
                // nuova. Una missione dell'operatore la cui cella nessuno ha rifiutato va solo al giro
                // di sempre.
                if (refusedDeposits.Contains(lu))
                    switch (await MissionGateway.AnotherCellInTheChosenStorageAsync(lu, worker))
                    {
                        case RefusedCellOutcome.NoOtherCell:
                            // Lo scarto, con la regola del dispositivo singolo: senza un punto di scarto
                            // il rigetto non si fa, e il gruppo va in errore e aspetta.
                            lu.PositionItem.Status = currentItemStatus;

                            if (await ToTheRejectionPointAsync(lu, worker) == RefusedDepositOutcomes.NoPlace)
                                return RefusedDepositOutcomes.NoPlace;

                            rejected = true;
                            continue;

                        case RefusedCellOutcome.NotNow:
                            lu.PositionItem.Status = currentItemStatus;
                            return RefusedDepositOutcomes.NoAnswer;

                        case RefusedCellOutcome.WarehouseSilent:
                            lu.PositionItem.Status = currentItemStatus;
                            return RefusedDepositOutcomes.WarehouseSilent;
                    }

                // La missione della configurazione con la cella persa la aspetta dal navigatore, con le
                // celle rifiutate escluse: niente selettore, niente ricerca (vedi le note). Resta senza
                // porta e in coda al navigatore, e il giro va avanti con le altre.
                if (lu is { OperatorDestination: false, StorageCellLost: true } && !refusedDeposits.Contains(lu))
                {
                    lu.PositionItem.Status = currentItemStatus;
                    continue;
                }

                // La destinazione scelta dall'operatore non prende il selettore del suo tipo e non
                // ne lascia uno alle altre, con la regola di AutomationDevice.VerifyWhereAllocateLuGrouped
                // (il commento sta li'). Se verso di lei la rotta non c'e', il gruppo non trova posto
                // come per ogni altra missione.
                var existingSelector = idTypes.FirstOrDefault(x => lu.IdLuType == x.LuTypeId);

                var tookTheSelector = existingSelector != null && await SetLuFromSelector(lu, existingSelector);

                if (!tookTheSelector)
                {
                    var found = await Navigator.FindWhereStoreLu(lu, lu.PositionItem,
                        LookWhereAllocateLuTypes.BuildRouteToStoreByIdLuType);

                    // La cella e' trovata e presa, ma la rotta l'ha cambiata un altro mentre la si
                    // scriveva: la missione e' gia' in coda al giro delle rotte, che la rifa' (voce 41).
                    // Non e' «nessun posto», e il suo stato di adesso non e' quello della ricerca: non
                    // diventa il selettore del suo tipo, e le altre contate si cercano lo stesso.
                    // Item 54: a load unit the operator only brought to its relocator stays, and is not «no
                    // place» either.
                    if (found is AutomationDevice.RouteBuildStatusTypes.TakenOver or AutomationDevice.RouteBuildStatusTypes.Stays)
                    {
                        lu.PositionItem.Status = currentItemStatus;
                        continue;
                    }

                    if (found != AutomationDevice.RouteBuildStatusTypes.Available)
                    {
                        lu.PositionItem.Status = currentItemStatus;

                        // Il giro l'ha mandata allo scarto: se ha la rotta verso di lui, ci va, e le
                        // altre contate si cercano lo stesso (con il comando manuale tutte). Senza rotta lo scarto non si e' fatto (nessun
                        // punto di scarto, nessun percorso), e il gruppo non trova posto, come prima.
                        if (found == AutomationDevice.RouteBuildStatusTypes.Rejected && lu.MissionSteps.Count > 0)
                        {
                            rejected = true;
                            continue;
                        }

                        return RefusedDepositOutcomes.NoPlace;
                    }

                    // Item 54: the destination a mission kept (PathMission.KeepsItsDestination) is not a
                    // cell for its load unit type either.
                    if (!lu.OperatorDestination && !lu.KeepsItsDestination)
                        idTypes.Add(new AutomationDevice.LuSearchLocationByTypeLu
                        {
                            LuTypeId = lu.IdLuType,
                            StorageDoorCode = lu.StorageDoorCode ?? string.Empty,
                            DestinationItemId = lu.DestinationItemId
                        });
                }

                // Mantiene la semantica precedente: la missione successiva vede le posizioni gia'
                // elaborate nel loro stato originario. Lo scope ripete il ripristino nel finally
                // soltanto se un'eccezione interrompe il giro prima di questa riga.
                lu.PositionItem.Status = currentItemStatus;
            }

            return rejected ? RefusedDepositOutcomes.Rejected : RefusedDepositOutcomes.Found;
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            throw;
        }
        finally
        {
            await scope.RestoreAsync(primaryFailure);
        }
    }

    /// <summary>Ingresso ristretto ai collaudi del protocollo di ripristino raggruppato.</summary>
    /// <param name="refusedDeposits">
    /// Le missioni il cui deposito il PLC ha rifiutato; nessuna, come il comando manuale.
    /// </param>
    /// <returns>Vero se ogni missione ha la sua posizione, come il comando manuale.</returns>
    internal async Task<bool> FindNewStoreLocationGroupedForTest(params PathMission[] refusedDeposits) =>
        await FindNewStoreLocationGrouped(refusedDeposits) == RefusedDepositOutcomes.Found;

    /// <summary>
    /// Ottiene le informazioni ASI per il deposito dell'unità di carico
    /// </summary>
    /// <returns>Le informazioni ASI di destinazione o null se non disponibili</returns>
    private async Task<DataAsi?> GetStorageAsiInfo()
    {
        if (device.LoadedLu == null) return null;

        var cellId = await DbInfo.GetCellId(device.LoadedLu.StorageDoorCode);

        // Compito 9: ex Navigator.WmsBusinessArea.CelleMagazzino.QuotaDepositoUdc(cellId, TipiPorta.A,
        // 0, false, device.LoadedLu.LuId), ora IWarehouse.GetCellStoringConstraintsAsync.
        device.LoadedLu.StorageDepth = (await device.LoadedLu.GetCellStoringConstraintsAsync(cellId, CellDoor.A, 0, device.LoadedLu.LuId))
            .TheoreticalStoreDepth;

        DataAsi? toAsiItem = null;
        if (device.LoadedLu.StorageDoorCode != null)
            toAsiItem = await device.LoadedLu.GetAsiCellInfoAsync(device.LoadedLu.StorageDoorCode, device.LoadedLu.StorageDepth);

        if (toAsiItem != null)
        {
            device.LoadedLu.DestinationAsi = toAsiItem.Asi;
            device.LoadedLu.DestinationSubitem = toAsiItem.Subitem;
            device.LoadedLu.DestinationPartition = toAsiItem.Partition;
        }

        return toAsiItem;
    }

    /// <summary>
    /// Ottiene le informazioni ASI di destinazione per la conversione profondità-partizione
    /// </summary>
    /// <returns>Le informazioni ASI di destinazione o null se non valide</returns>
    /// <remarks>
    /// La terna indirizzo/sottoindirizzo/partizione si legge <b>una volta sola</b> e si lavora sulla
    /// copia. Prima erano sei letture di tre proprieta' della missione, che un cambio di
    /// destinazione riscrive una alla volta (<c>SetDataForStoreInLinkedCell</c>): l'indirizzo
    /// spedito al campo poteva essere l'ASI della cella nuova con la partizione della vecchia, cioe'
    /// un posto che nessuno aveva scelto. Con la copia l'indirizzo e' coerente con se stesso: sara'
    /// la cella vecchia o quella nuova, mai un miscuglio.
    /// </remarks>
    private async Task<DataAsi?> GetToAsiItemForDepthConversion()
    {
        if (device.LoadedLu == null) return null;

        var destination = new DataAsi(device.LoadedLu.DestinationAsi, device.LoadedLu.DestinationSubitem,
            device.LoadedLu.DestinationPartition);

        if (string.IsNullOrEmpty(destination.Asi) ||
            string.IsNullOrEmpty(destination.Subitem) ||
            string.IsNullOrEmpty(destination.Partition) ||
            destination.Partition == "0000")
        {
            return await GetStorageAsiInfo();
        }

        var cellId = await DbInfo.GetCellId(device.LoadedLu.StorageDoorCode);
        var storageDepth = await device.LoadedLu.GetStorageDepthByAsiAsync(cellId, destination.Asi,
            destination.Subitem, destination.Partition);

        if (storageDepth < 0)
        {
            return await GetStorageAsiInfo();
        }

        device.LoadedLu.StorageDepth = storageDepth;
        return destination;
    }

    /// <summary>
    /// Muove un'unità di carico che rappresenta un ostacolo verso una nuova posizione
    /// </summary>
    /// <param name="bufferCellsNotExtractable">Lista delle celle buffer non estraibili</param>
    /// <param name="doorCodeNotExtractable">Lista dei codici porta non utilizzabili</param>
    /// <param name="row">La riga di dati contenente le informazioni dell'unità di carico</param>
    /// <param name="navigatorCommand">Il comando del navigator da configurare</param>
    private async Task MoveObstacleLu(List<decimal> bufferCellsNotExtractable, List<string?> doorCodeNotExtractable,
        DataRow row, Command navigatorCommand)
    {
        // Nessuna cella collegata: zero e' un dato legittimo, non un difetto. Il gestionale non
        // configurato e' un'altra cosa - va detto, non aggirato con lo stesso zero (come gia' fa
        // PathMission.DescribeCellAsync, compito 7).
        decimal idMagazzino;
        if (device.LinkedCellId == 0)
            idMagazzino = 0m;
        else if (Navigator.Warehouse is { } warehouseObstacle)
            idMagazzino = (await warehouseObstacle.DescribeCellAsync(device.LinkedCellId)).WarehouseId;
        else
            throw new InvalidOperationException(
                $"MoveObstacleLu su {device.ItemGuid}: Navigator.Warehouse non e' impostato.");

        decimal cellIdDestinazione;
        string? codicePortaDestinazione;

        if (row.GetValueOrThrow<decimal>("ID_CELLA_DESTINAZIONE") == 0 ||
            string.IsNullOrEmpty(row.GetValueOrThrow<string>("CODICE_PORTA_DESTINAZIONE")))
        {
            // Nessuna missione ancora costruita a questo punto (nasce piu' sotto, in
            // device.AddMissionToPickUp): MissionId resta Guid.Empty, come DestinationItemId altrove
            // in questo file quando l'identificativo non esiste ancora.
            //
            // Per questo qui si prenota e basta, senza il ciclo di vita del compito 17
            // (PathMission.ReserveOrKeepStorageAsync): quello poggia sulla missione - e' lei che
            // tiene la prenotazione mentre aspetta, e che la rilascia quando si chiude - e qui la
            // missione non c'e' ancora. Il gestionale, che indicizza il legame per missione, non ne
            // scrive nessuno per una richiesta senza missione: questa prenotazione tiene la cella
            // come faceva prima (l'assegnazione della posizione dell'unita' di carico), e viene
            // consumata subito qui sotto dalla missione che nasce. Non e' nemmeno un tentativo che
            // si ripete: questo giro riparte dalla riga del database, e se la destinazione e' gia'
            // scritta non chiede niente al gestionale.
            var request = new ReserveStorageRequest(
                Guid.Empty,
                row.GetValueOrThrow<decimal>("ID_UDC"),
                idMagazzino,
                await Persistence.LocalQueries.GetAutomaticAreasByWarehouseId(idMagazzino),
                [row.GetValueOrThrow<decimal>("ID_CELLA")],
                [device.ItemGuid],
                StorageAreaSelectionMode.All,
                StorageCellSearchMode.ConnectedAutomaticOperator,
                CellFullnessPreference.Fullest,
                StorageSearchOperatorType.Automatic,
                AssignPosition: true,
                VerifyPosition: false,
                Navigator.WorkstationId,
                Navigator.UserId);

            var reservation = await Navigator.RequireWarehouse($"MoveObstacleLu su {device.ItemGuid}")
                .ReserveStorageAsync(request);

            cellIdDestinazione = reservation.CellId;
            codicePortaDestinazione = reservation.DoorCode;
        }
        else
        {
            cellIdDestinazione = row.GetValueOrThrow<decimal>("ID_CELLA_DESTINAZIONE");
            codicePortaDestinazione = row.GetValueOrThrow<string>("CODICE_PORTA_DESTINAZIONE");
        }

        var missionType = MissionTypes.ChangeStoreLocation;

        navigatorCommand.Date = DateTime.Now;
        navigatorCommand.CommandType = Command.CommandTypes.MoveLuAutomatic;
        navigatorCommand.LuId = row.GetValueOrThrow<decimal>("ID_UDC");
        navigatorCommand.PickUpDoorCode = row.GetValueOrThrow<string>("CODICE_PORTA");

        if (string.IsNullOrEmpty(codicePortaDestinazione))
        {
            missionType = MissionTypes.Extraction;

            // Compito 16: ex CelleMagazzino.DecodificaCella, di cui si leggeva la sola porta A -
            // stessa guardia sullo zero di ExtractTargetLu, e per lo stesso motivo (il metodo
            // vecchio tollerava la cella assente rispondendo nulla, DescribeCellAsync no).
            var bufferCellId = row.GetValueOrThrow<decimal>("ID_CELLA_POLMONE");
            var bufferDoorCodeA = bufferCellId == 0
                ? null
                : (await Navigator.RequireWarehouse($"DescribeCellAsync sulla cella polmone {bufferCellId}")
                    .DescribeCellAsync(bufferCellId)).DoorCodeA;

            row["ID_ITEM_DESTINATION"] = await DbInfo.GetItemId(bufferDoorCodeA);

            navigatorCommand.BufferCellId = bufferCellId;
            navigatorCommand.StorageDoorCode = bufferDoorCodeA;
        }
        else
        {
            await DbInfo.SetOperationLockingCell(cellIdDestinazione, StatoOperazioneCella.Deposito);

            navigatorCommand.BufferCellId = 0;
            navigatorCommand.StorageDoorCode = codicePortaDestinazione;
        }

        navigatorCommand.Status = Command.StatusTypes.Done;
        navigatorCommand.WorkstationId = row.GetValueOrThrow<string>("ID_POSTAZIONE");

        if (await device.AddMissionToPickUp(navigatorCommand, row, missionType)) return;

        if (navigatorCommand.BufferCellId > 0)
            bufferCellsNotExtractable.Add(navigatorCommand.BufferCellId);
        if (!string.IsNullOrEmpty(navigatorCommand.PickUpDoorCode))
            doorCodeNotExtractable.Add(navigatorCommand.PickUpDoorCode);
    }

    /// <summary>
    /// Processa una missione esistente verificando la coerenza dei dati
    /// </summary>
    /// <param name="row">La riga di dati da processare</param>
    /// <param name="missionToCheck">La missione esistente da verificare</param>
    /// <returns>True se la missione deve essere processata, false altrimenti</returns>
    /// <remarks>
    /// <c>internal</c> e non <c>private</c> dal compito 12: e' statica e non tocca il dispositivo,
    /// quindi le prove la chiamano come si chiama una funzione, mentre il suo chiamante di
    /// produzione vive dentro il ciclo di sfondo del magazzino e per entrarci una prova dovrebbe
    /// aspettare a tempo. E' uno dei due punti che segnalano «gia' in un'altra estrazione».
    /// </remarks>
    internal static async Task<bool> ProcessExistingMission(DataRow row, PathMission missionToCheck)
    {
        // Se esiste udc nelle ActiveMissionsLU
        if (missionToCheck.ExtractionQueueId == row.GetValueOrThrow<decimal>("ID_ESTRAZIONE_UDC"))
            return false;

        if (missionToCheck.ExtractionQueueId > 0 &&
            missionToCheck.ExtractionQueueId != row.GetValueOrThrow<decimal>("ID_ESTRAZIONE_UDC"))
        {
            if (row.GetValueOrThrow<decimal>("ID_ASSEGNAZIONE_UDC") > 0)
            {
                await LogUdcDebugInfo(row, "Cancellazione ASSEGNAZIONE_NOTE->MissionToCheck da BuildLUExtraction");

                // Compito 13: il motore scarta l'assegnazione invece di cancellarla da se'. Qui la
                // missione non se ne va - se ne va la riga di coda - quindi il fatto e' «questa
                // assegnazione di nota non la posso servire», con la stessa chiave e la stessa
                // conseguenza del DELETE di prima: la riga sola di ID_ASSEGNAZIONE_UDC.
                await DbInfo.RejectNoteAssignment(row.GetValueOrThrow<decimal>("ID_ASSEGNAZIONE_UDC"),
                    row.GetValueOrThrow<decimal>("ID_UDC"),
                    "l'unita' di carico e' gia' in missione per un'altra riga di estrazione");
            }

            await LogUdcDebugInfo(row, "Cancellazione ESTRAZIONI_UDC_AUTOMATICHE->MissionToCheck da BuildLUExtraction");
            await DbInfo.DeleteExtractionItemQueueById(row.GetValueOrThrow<decimal>("ID_ESTRAZIONE_UDC"));
            return false;
        }

        if (missionToCheck.GuidAssignmentLu != Guid.Empty &&
            missionToCheck.GuidAssignmentLu != row.GetValueOrThrow<Guid>("GUID_ASSEGNAZIONE_UDC"))
        {
            await LogUdcDebugInfo(row, "Cancellazione ESTRAZIONI_UDC_AUTOMATICHE->MissionToCheck da BuildLUExtraction");
            await LogUdcDebugInfo(row, "Cancellazione ASSEGNAZIONE_NOTE->MissionToCheck da BuildLUExtraction");

            // Compito 13: stesso fatto dell'uscita qui sopra, motivo diverso - ed e' il motivo che
            // un operatore legge quando l'assegnazione sparisce.
            await DbInfo.RejectNoteAssignment(row.GetValueOrThrow<decimal>("ID_ASSEGNAZIONE_UDC"),
                row.GetValueOrThrow<decimal>("ID_UDC"),
                "l'unita' di carico e' gia' in missione per un'altra assegnazione");
            await DbInfo.DeleteExtractionItemQueueById(row.GetValueOrThrow<decimal>("ID_ESTRAZIONE_UDC"));
            return false;
        }

        // Compito 12: la segnalazione al posto dell'UPDATE su DISTINTA_ESTRAZIONI_UDC. Il «se la
        // scrittura e' riuscita» di prima non ha piu' un valore falso da leggere: il guasto del
        // confine solleva, il ciclo di sfondo del magazzino lo registra e ripassa, e la riga di coda
        // resta dov'e'. Le due uscite erano comunque tutt'e due false.
        await DbInfo.ReportExtractionRowOutcome(row.GetValueOrThrow<decimal>("ID_ESTRAZIONE_UDC"),
            ExtractionRowStatus.InOtherExtraction);
        await LogUdcDebugInfo(row, "Cancellazione ESTRAZIONI_UDC_AUTOMATICHE da ProcessExtractionLU 1");
        await DbInfo.DeleteExtractionItemQueueById(row.GetValueOrThrow<decimal>("ID_ESTRAZIONE_UDC"));

        return false;
    }

    /// <summary>
    /// Processa le estrazioni delle unità di carico disponibili
    /// </summary>
    private async Task ProcessExtractionLu()
    {
        if (device.QueuedLuToPickUpCount != 0 || device.LUtoPick != null)
            return;

        var dtLu = await DbInfo.GetLuToExtract(device);

        if (dtLu?.Rows.Count == 0)
            return;

        // Le collezioni seguenti servono per gestire i magazzini con doppia uscita da singolo traslo
        // se una delle due uscite è bloccata indirizzano il processo di estrazione verso l'altra uscita disponibile
        var bufferCellsNotExtractable = new List<decimal>();
        var doorCodeNotExtractable = new List<string?>();

        if (dtLu?.Rows == null) return;

        foreach (DataRow row in dtLu.Rows)
        {
            if (!await ProcessExtractionRow(row, bufferCellsNotExtractable, doorCodeNotExtractable))
                continue;

            var navigatorCommand = new Command();

            if (!row.GetValueOrThrow<bool>("ESTRARRE"))
                await MoveObstacleLu(bufferCellsNotExtractable, doorCodeNotExtractable, row, navigatorCommand);
            else
                await ExtractTargetLu(bufferCellsNotExtractable, doorCodeNotExtractable, row, navigatorCommand);
        }
    }

    /// <summary>
    /// Processa una singola riga di estrazione verificando le condizioni di validità
    /// </summary>
    /// <param name="row">La riga di dati da processare</param>
    /// <param name="bufferCellsNotExtractable">Lista delle celle buffer non estraibili</param>
    /// <param name="doorCodeNotExtractable">Lista dei codici porta non utilizzabili</param>
    /// <returns>True se la riga può essere processata, false altrimenti</returns>
    private static async Task<bool> ProcessExtractionRow(DataRow? row, List<decimal> bufferCellsNotExtractable, List<string?> doorCodeNotExtractable)
    {
        if (row == null) return false;

        if (row.GetValueOrThrow<decimal>("ID_UDC") == 0)
        {
            await DbInfo.DeleteExtractionItemQueueById(row.GetValueOrThrow<decimal>("ID_ESTRAZIONE_UDC"));
            return false;
        }

        var missionToCheck = Navigator.GetMissionByLuId(row.GetValueOrThrow<decimal>("ID_UDC"));

        if (missionToCheck != null)
        {
            return await ProcessExistingMission(row, missionToCheck);
        }

        if (row.GetValueOrThrow<decimal>("ID_CELLA_POLMONE") > 0 &&
            bufferCellsNotExtractable.Contains(row.GetValueOrThrow<decimal>("ID_CELLA_POLMONE")))
            return false;

        var doorCode = row.GetValueOrThrow<string>("CODICE_PORTA");
        if (string.IsNullOrEmpty(doorCode) && doorCodeNotExtractable.Contains(doorCode))
            return false;

        await DbInfo.SetOperationLockingCell(row.GetValueOrThrow<decimal>("ID_CELLA"), StatoOperazioneCella.Prelievo);
        return true;
    }

    /// <summary>
    /// Verifica e aggiorna le informazioni di deposito per un'unità di carico
    /// </summary>
    /// <param name="lu">L'unità di carico da verificare e aggiornare</param>
    /// <returns>
    /// <see cref="StoreVerificationResults.DestinationConfirmed"/> se il deposito puo' partire,
    /// <see cref="StoreVerificationResults.DestinationChanged"/> se la cella non va piu' bene,
    /// <see cref="StoreVerificationResults.ChosenByOperator"/> se non va piu' bene ma l'ha scelta
    /// l'operatore, <see cref="StoreVerificationResults.SearchPostponed"/> se il freno non e' ancora
    /// scaduto.
    /// </returns>
    /// <remarks>
    /// Il freno sta qui e non piu' in basso, accanto alla ricerca: quando l'ultima ricerca non ha
    /// trovato posto, l'esito di questo giro e' gia' deciso - il deposito non parte - e rifare le
    /// tre letture del confine per riscoprirlo sarebbe il costo che si sta togliendo.
    /// </remarks>
    private async Task<StoreVerificationResults> VerifyStoreInfo(PathMission lu)
    {
        if (device.RecordStorageLength != RecordStorageTypes.ConvertDepthToPartition)
            return StoreVerificationResults.DestinationConfirmed;

        if (VerificationPostponed(lu))
            return StoreVerificationResults.SearchPostponed;

        var result = await VerifyStoreDestination(lu);

        // La cella va bene: questa missione non e' piu' quella che non trovava posto, e il conteggio
        // dei tentativi non deve restarle addosso - ne' per far ripartire il freno da un'attesa
        // lunga la prossima volta, ne' per crescere nel dizionario per tutta la vita del processo.
        if (result == StoreVerificationResults.DestinationConfirmed)
            StoreSearchRetries.Forget(lu.MissionId);

        return result;
    }

    /// <summary>
    /// Il freno della riverifica della cella di deposito (<see cref="StoreSearchRetries"/>) e' ancora
    /// in corso per la missione: la riverifica non si fa, e il deposito non parte. Solo per i
    /// dispositivi che la riverifica la fanno (<see cref="RecordStorageTypes.ConvertDepthToPartition"/>).
    /// </summary>
    private bool VerificationPostponed(PathMission lu) =>
        device.RecordStorageLength == RecordStorageTypes.ConvertDepthToPartition &&
        StoreSearchRetries.IsWaiting(lu.MissionId);

    /// <summary>
    /// La verifica vera e propria della cella di deposito, senza il freno: la cella e' ancora
    /// libera, non bloccata, e con quota sufficiente?
    /// </summary>
    /// <param name="lu">L'unità di carico da verificare e aggiornare</param>
    private async Task<StoreVerificationResults> VerifyStoreDestination(PathMission lu)
    {
        var cellId = await DbInfo.GetCellId(lu.StorageDoorCode);
        var lockState = cellId == 0 ? CellLockState.None : (await lu.DescribeCellAsync(cellId)).Locked;

        // Compito 9: ex DbInfo.IsCellBusyByExtraction(cellId), che controllava prima la coda di
        // estrazione locale del WCS e solo se libera interrogava il gestionale - la meta' locale
        // resta in DbInfo (non e' un dato del gestionale), la meta' gestionale e' migrata in
        // IWarehouse.GetCellBusyStateAsync; l'OR qui sotto e' lo stesso ritorno anticipato di prima.
        var extractionBusy = await DbInfo.IsExtractionQueuedLocally(cellId) ||
                              (await lu.GetCellBusyStateAsync(cellId)).Extraction;

        if (extractionBusy || BlocksStoring(lockState))
            return await ChooseAnotherStoreLocation(lu, "la cella e' impegnata da un'estrazione o bloccata al deposito");

        // Compito 9: ex Navigator.WmsBusinessArea.CelleMagazzino.QuotaDepositoUdc(cellId, TipiPorta.A,
        // 0, false, lu.LuId), ora IWarehouse.GetCellStoringConstraintsAsync.
        var storageDepth = (await lu.GetCellStoringConstraintsAsync(cellId, CellDoor.A, 0, lu.LuId))
            .TheoreticalStoreDepth;

        if (storageDepth == 0)
            return await ChooseAnotherStoreLocation(lu, "la cella non ha piu' quota di deposito");

        if (lu.StorageDepth == storageDepth) return StoreVerificationResults.DestinationConfirmed;

        lu.StorageDepth = storageDepth;

        var toAsiItem = await lu.GetAsiCellInfoAsync(lu.StorageDoorCode, lu.StorageDepth);

        if (toAsiItem != null)
        {
            lu.DestinationAsi = toAsiItem.Asi;
            lu.DestinationSubitem = toAsiItem.Subitem;
            lu.DestinationPartition = toAsiItem.Partition;
        }

        return StoreVerificationResults.DestinationConfirmed;
    }

    /// <summary>
    /// Cerca un'altra cella di deposito e riferisce che la destinazione non e' piu' quella di prima.
    /// </summary>
    /// <param name="lu">L'unità di carico che deve cambiare destinazione</param>
    /// <param name="reason">Perche' la cella scelta non va piu' bene, per il registro</param>
    /// <remarks>
    /// <para>
    /// <b>La ricerca si attende.</b> Era lanciata e scartata (<c>device.StorageManager?.FindNewStoreLocation();</c>):
    /// si sospendeva al primo <c>await</c> - una scrittura sul database e due giri verso il
    /// gestionale - e restava in volo mentre <c>StartStoreLu</c> proseguiva sulla stessa missione e
    /// sullo stesso dispositivo. Con un <c>Task</c> scartato non c'era nemmeno un posto dove
    /// un'eccezione potesse comparire: un guasto del confine lasciava la missione senza
    /// prenotazione, in silenzio.
    /// </para>
    /// <para>
    /// La chiamata e' diretta e non passa piu' da <c>device.StorageManager</c>: quel campo e'
    /// <b>questo stesso oggetto</b>, e il <c>?.</c> avrebbe fatto sembrare riuscita una ricerca mai
    /// avvenuta.
    /// </para>
    /// <para>
    /// <b>Il semaforo di esecuzione.</b> Attendere qui vuol dire tenere <c>_executionSemaphore</c>
    /// del dispositivo anche per la ricerca: una scrittura locale e due giri verso il gestionale in
    /// piu'. In cambio questo giro non paga piu' l'attesa fissa di 200 ms di <c>StartStoreLu</c> ne'
    /// la spedizione del comando, e il confine stava gia' dentro quel semaforo poche righe sopra
    /// (<c>DescribeCellAsync</c>, <c>GetCellBusyStateAsync</c>): non e' una specie di attesa nuova,
    /// e' la stessa piu' lunga. Un'esecuzione che arrivi nel frattempo viene rifiutata <b>con una
    /// riga di log</b>: da <c>IsBusyWithCommand</c>, che comprende <c>ExecuteStepIsRunning</c>, cioe'
    /// proprio «il semaforo e' preso»; e negli stati <c>Warning</c> ed <c>Error</c>, che
    /// <c>IsBusyWithCommand</c> dichiara sempre non impegnati, dal semaforo stesso, che
    /// <c>ExecuteStep</c> prende con attesa zero e il cui fallimento ha la sua riga. E' la regola del
    /// 2026-09-02, «un elemento impegnato rifiuta l'esecuzione, non la rimanda e non la accoda», non
    /// una perdita silenziosa.
    /// E il deposito rifiutato non si perde: qui non si passa per <c>Unloading</c> e non si
    /// valorizza <c>RunningStepId</c>, quindi il dispositivo resta come stava e il proprio ciclo di
    /// sfondo (<c>StartProcessExtractionLu</c>, ogni due secondi, per i dispositivi legati al
    /// magazzino e senza gruppo) richiama <c>ExecuteStep</c> al giro dopo.
    /// </para>
    /// <para>
    /// <b>Quando non c'e' posto si frena.</b> Il giro dopo non ripeterebbe niente di utile: e'
    /// <see cref="StoreSearchRetries"/> a decidere quando riprovare, e la riga di registro esce al
    /// primo fallimento e poi di rado, invece che a ogni passata.
    /// </para>
    /// <para>
    /// <b>La cella dell'operatore si aspetta.</b> Se la destinazione l'ha scelta l'operatore la
    /// ricerca non parte (<see cref="FindNewStoreLocation"/>), e l'esito e'
    /// <see cref="StoreVerificationResults.ChosenByOperator"/>, non «riscelta»: la missione e'
    /// rimasta intera. Il freno e' lo stesso - riverificare la cella a ogni passata costerebbe le
    /// tre letture del confine ogni due secondi - e la riga di registro ha la stessa cadenza, ma
    /// dice che cosa succede davvero: nessuna ricerca, la missione aspetta la sua cella. A ogni
    /// scadenza del freno, prima di riverificare, la cella si conferma presso il gestionale
    /// (<c>StartStoreLu</c>): una cella scaduta e data a un'altra UDC non si aspetta per sempre.
    /// </para>
    /// </remarks>
    private async Task<StoreVerificationResults> ChooseAnotherStoreLocation(PathMission lu, string reason)
    {
        var search = await FindNewStoreLocation(keepTheGateway: false);

        if (search == NewStoreLocationResults.Found)
        {
            StoreSearchRetries.Forget(lu.MissionId);
            return StoreVerificationResults.DestinationChanged;
        }

        var attempts = StoreSearchRetries.RegisterFailure(lu.MissionId);

        if (search == NewStoreLocationResults.ChosenByOperator)
        {
            if (attempts == 1 || attempts % 30 == 0)
                Navigator.WriteLog(
                    $"VerifyStoreInfo {device.ItemGuid}: {reason} per la missione {lu.MissionId}, ma la cella l'ha scelta l'operatore e non se ne cerca un'altra - la missione la aspetta da {attempts} verifiche",
                    LogEntryType.Warning);

            return StoreVerificationResults.ChosenByOperator;
        }

        // Il primo fallimento si dice subito - e' la notizia - e poi uno ogni trenta: al tetto sono
        // circa quindici minuti fra una riga e l'altra. Il ciclo, senza freno, ne scriveva una ogni
        // due secondi.
        if (attempts == 1 || attempts % 30 == 0)
            Navigator.WriteLog(
                $"VerifyStoreInfo {device.ItemGuid}: {reason} per la missione {lu.MissionId}, e la ricerca di un'altra cella non ha trovato niente da {attempts} tentativi - la missione resta senza destinazione utilizzabile",
                LogEntryType.Warning);

        return StoreVerificationResults.DestinationChanged;
    }

    #endregion Private Methods

}
