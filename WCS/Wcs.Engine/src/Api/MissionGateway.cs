using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Wms.Contracts;
using Log.Interface;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Api;

/// <summary>L'esito di <see cref="MissionGateway.Move"/>: l'host lo traduce in un codice HTTP.</summary>
public enum MissionMoveOutcome
{
    Moved,
    MissionNotFound,
    DeviceNotFound,
    DeviceOccupied,

    /// <summary>
    /// Item 54, decision 4 of the owner of 13/10: a step of the mission is in flight, and the move waits
    /// instead of being refused (the refusal it replaces, <c>StepInFlight</c>, is gone: the move's route
    /// write compares no version and writes no steps, so the step in flight was its only refusal). It is
    /// applied as soon as that step ends, from where it ends
    /// (<see cref="MissionGateway.PendingChangeOf"/> says it while it waits; it lives only in memory,
    /// owner's answer 4 of 13/10). The step in flight wins: the field never contradicts itself halfway.
    /// </summary>
    Pending,

    /// <summary>
    /// Il motore e' in Recover: sta ancora caricando le missioni, e lo spostamento e' rifiutato senza
    /// toccare niente, come gli altri comandi che non valgono prima del caricamento (risposta 4 del
    /// proprietario dell'8/10). Da riprovare quando il caricamento e' finito.
    /// </summary>
    EngineRecovering
}

/// <summary>L'esito di <see cref="MissionGateway.ChangeDestinationAsync"/>: l'host lo traduce in un codice HTTP.</summary>
public enum MissionDestinationOutcome
{
    /// <summary>Destinazione, gateway e rotta nuovi, con il segno della scelta dell'operatore.</summary>
    Changed,

    /// <summary>
    /// Gia' cosi': stessa destinazione e stesso gateway, gia' scelti dall'operatore, e la rotta c'e'.
    /// Una destinazione uguale ma messa dalla configurazione, o senza rotta, e' un cambio: la scelta
    /// diventa dell'operatore e la rotta si calcola. Mai verso un magazzino: li' la cella la sceglie
    /// il gestionale, e scegliere di nuovo il magazzino e' il modo di cambiarla.
    /// </summary>
    Unchanged,

    MissionNotFound,
    DeviceNotFound,

    /// <summary>Il gateway da impostare non e' un dispositivo.</summary>
    GatewayNotFound,

    /// <summary>
    /// La missione si sta muovendo, o si e' mossa mentre il cambio era in corso: da riprovare. Le
    /// cause, che la riga di registro del rifiuto dice una per una: uno step della missione e' in
    /// volo; un dispositivo sta avviando uno step della missione; il dispositivo di posizione sta
    /// eseguendo; la missione e' stata spostata (<see cref="MissionGateway.Move"/>) mentre il
    /// gestionale rispondeva o prima del commit; la missione aspetta il rigetto dal campo, rimandato per
    /// uno step in volo, che il giro delle rotte rifa'; il prelievo di un'estrazione si sta ancora
    /// consegnando al gestionale dopo i trenta secondi del lucchetto delle estrazioni (voce 45,
    /// <c>MissionGateway.CommitAfterThePickUpHandOffAsync</c>).
    /// </summary>
    StepInFlight,

    /// <summary>
    /// Item 54, decision 4 of the owner of 13/10: a step of the mission is in flight, or a device is
    /// starting one, when the change is asked. The change is not refused: it waits, one per mission and
    /// the last one wins, and it is applied as soon as the step ends, from where it ends
    /// (<see cref="MissionGateway.PendingChangeOf"/> says it while it waits; it lives only in memory and
    /// a restart loses it, owner's answer 4 of 13/10). The step in flight wins. The other causes of
    /// <see cref="StepInFlight"/> (the mission moved while the warehouse answered, a postponed field
    /// rejection, the pick-up hand-off) stay refusals.
    /// </summary>
    Pending,

    /// <summary>
    /// Un'altra missione dello stesso gruppo, che ha copiato la porta della missione, non si puo'
    /// fermare adesso (<c>CopiesOfTheDoor.Stop</c>): un dispositivo sta avviando un suo step, ha uno
    /// step in volo, o per lei e' in corso una ricerca o un altro cambio. La missione dell'operatore e'
    /// ferma, e il cambio non ha toccato niente: da riprovare fra un momento (specifica, risposta 4 del
    /// 9/10). Prima usciva come <see cref="StepInFlight"/>, e l'operatore leggeva che si muoveva la sua
    /// missione. Lo step in volo della missione stessa resta <see cref="StepInFlight"/>.
    /// </summary>
    CopyInFlight,

    /// <summary>Una ricerca della cella di deposito (o un altro cambio) e' in corso per la missione: da riprovare.</summary>
    CellSearchInFlight,

    /// <summary>La destinazione scelta e' la posizione dell'UDC, o il gruppo della sua posizione.</summary>
    AlreadyThere,

    /// <summary>Dalla posizione non c'e' percorso verso la destinazione, con il gateway scelto.</summary>
    NoRoute,

    /// <summary>
    /// Nessuna cella da prenotare nel magazzino scelto (o nel suo gruppo). Raccoglie piu' cause, che
    /// la riga di registro del rifiuto dice una per una: il gestionale non ha una cella libera (la
    /// risposta di ogni giorno), o non ne ha un'altra quando l'operatore sceglie di nuovo il
    /// magazzino dove la missione sta gia' andando (<see cref="MissionDestinationResult.NoOtherFreeCell"/>,
    /// e la cella che ha resta sua); la cella che propone non e' nel magazzino scelto; l'UDC non ha
    /// l'identificativo del gestionale (<c>LuId</c> 0: il seme delle prove del browser, o una mancata
    /// lettura all'ingresso) o il gestionale risponde che non ha riferimenti; il magazzino scelto non
    /// ha porte mappate a una cella nel database del WCS, o la sua cella non ha un magazzino del
    /// gestionale. Tutte tranne le prime due non sono un magazzino pieno, e la loro riga e' un avviso.
    /// </summary>
    NoStorageCell,

    /// <summary>Il gestionale non risponde, o non e' configurato.</summary>
    WarehouseUnavailable,

    /// <summary>
    /// Il gestionale ha risposto di no (un 4xx che non e' «riprova») alla prima segnalazione del cambio
    /// cella di un'estrazione con l'UDC ancora nella sua cella: non l'ha applicata, il cambio e'
    /// rifiutato e la missione resta l'estrazione che era (specifica, risposta 4 dell'8/10 sera). Non
    /// e' un gestionale fermo: ha ricevuto, e ha rifiutato.
    /// </summary>
    WarehouseRefused,

    /// <summary>
    /// Item 54, decision 5 of the owner of 13/10: the operator chose what a relocator does with the load
    /// unit (<see cref="MissionDestinationOptions.AtTheRelocator"/>), and the destination is not a
    /// relocator - no storage names it as its <c>RelocateItemId</c>. Nothing was touched.
    /// </summary>
    NotARelocator,

    /// <summary>
    /// Item 54, decision 6 of the owner of 13/10: the precise cell the operator chose
    /// (<see cref="MissionDestinationOptions.CellDoorCode"/>) is refused, by the warehouse or by the WCS
    /// before asking it: <see cref="MissionDestinationResult.CellRefusal"/> says why, and
    /// <see cref="MissionDestinationResult.CellRefusalReason"/> says it in words. As for every refusal of
    /// the storage flow, a refusal that comes after the mission's old reservation was released makes it
    /// lose its cell (<see cref="MissionDestinationResult.LostTheCell"/>).
    /// </summary>
    CellRefused
}

/// <summary>Che cosa fare del gateway della missione insieme al cambio di destinazione.</summary>
public enum MissionGatewayMode
{
    /// <summary>Tenere quello che c'e' (anche nessuno): la scelta di oggi.</summary>
    Keep,

    /// <summary>Toglierlo.</summary>
    Clear,

    /// <summary>Impostare un dispositivo, al posto di quello che c'e' o dove non c'era.</summary>
    Set
}

/// <summary>
/// La scelta dell'operatore sul gateway (decisione del proprietario del 30/9, sera): viaggia con il
/// cambio di destinazione, il motore sonda la rotta con lei, e sulla missione si scrive solo se il
/// cambio passa. Il valore predefinito tiene il gateway che c'e'.
/// </summary>
/// <param name="Mode">Tenere, togliere o impostare.</param>
/// <param name="ItemId">Il dispositivo da impostare, per <see cref="MissionGatewayMode.Set"/>.</param>
public readonly record struct MissionGatewayChoice(MissionGatewayMode Mode, Guid ItemId)
{
    public static MissionGatewayChoice Keep { get; } = new(MissionGatewayMode.Keep, Guid.Empty);

    public static MissionGatewayChoice Clear { get; } = new(MissionGatewayMode.Clear, Guid.Empty);

    public static MissionGatewayChoice Set(Guid itemId) => new(MissionGatewayMode.Set, itemId);
}

/// <summary>
/// L'esito di un cambio di destinazione, con quello che serve a spiegare un
/// <see cref="MissionDestinationOutcome.NoRoute"/> - il gateway con cui si e' sondato, e se senza
/// di lui il percorso ci sarebbe (la pagina propone allora di toglierlo) - e un
/// <see cref="MissionDestinationOutcome.NoStorageCell"/> per un'altra cella che non c'e'.
/// </summary>
/// <param name="Outcome">L'esito.</param>
/// <param name="Gateway">Per <see cref="MissionDestinationOutcome.NoRoute"/>, il gateway usato dalla sonda; <c>null</c> senza gateway.</param>
/// <param name="WouldRouteWithoutGateway">Per <see cref="MissionDestinationOutcome.NoRoute"/> con un gateway: senza di lui il percorso c'e'.</param>
/// <param name="NoOtherFreeCell">
/// Per <see cref="MissionDestinationOutcome.NoStorageCell"/>: l'operatore ha scelto di nuovo il
/// magazzino dove la missione sta gia' andando, cioe' ha chiesto un'altra cella, e il gestionale non
/// ne ha un'altra libera. La domanda non prenota, quindi il rifiuto non tocca la cella che la
/// missione ha (decisione del proprietario dell'1/10); la pagina lo dice diversamente da «nessuna
/// cella libera». Che il gestionale gliela tenga ancora non lo si chiede qui: se nel frattempo e'
/// scaduta, la sostituisce il navigatore prima del deposito.
/// </param>
/// <param name="LostTheCell">
/// Per un rifiuto del cambio verso un magazzino arrivato dopo la prenotazione: la prenotazione aveva
/// gia' lasciato la cella che la missione teneva, e la missione l'ha persa - porta, indirizzo e rotta
/// tolti, e un'altra cella nel magazzino della sua destinazione gliela prenota il navigatore
/// (decisioni del proprietario dell'1/10; per una missione della configurazione, se li' non c'e'
/// posto, in un altro magazzino del suo ricollocatore o allo scarto, risposta 5 del 2/10). La
/// destinazione non e' cambiata, ma la missione si', e la pagina lo dice.
/// </param>
/// <param name="MissionClosed">
/// Per il rifiuto del cambio cella di un'estrazione con l'UDC ancora nella sua cella, arrivato dopo
/// che il gestionale aveva gia' saputo che la missione lascia l'assegnazione e che l'UDC esce dalla
/// coda di estrazione: la missione e' chiusa, l'UDC resta nella sua cella senza ordine, e la riga
/// della coda di estrazione del WCS non c'e' piu' (risposta del proprietario del 2/10 sulla domanda
/// del compito 4). L'esito dice perche' il cambio e' stato rifiutato; la pagina dice anche questo.
/// </param>
/// <param name="CellRefusal">For <see cref="MissionDestinationOutcome.CellRefused"/>, why the precise cell was refused (item 54).</param>
/// <param name="CellRefusalReason">For <see cref="MissionDestinationOutcome.CellRefused"/>, the reason in words: the warehouse's, or the WCS's.</param>
public sealed record MissionDestinationResult(
    MissionDestinationOutcome Outcome,
    Guid? Gateway = null,
    bool WouldRouteWithoutGateway = false,
    bool NoOtherFreeCell = false,
    bool LostTheCell = false,
    bool MissionClosed = false,
    MissionCellRefusal? CellRefusal = null,
    string? CellRefusalReason = null);

/// <summary>
/// L'esito di <see cref="MissionGateway.AnotherCellInTheChosenStorageAsync"/>: la cella che il PLC ha
/// rifiutato a una missione mandata dall'operatore in un magazzino.
/// </summary>
internal enum RefusedCellOutcome
{
    /// <summary>
    /// La missione non va in un magazzino scelto dall'operatore: niente e' stato toccato, e chi chiama
    /// fa come sempre.
    /// </summary>
    NotTheOperatorsStorage,

    /// <summary>
    /// Un'altra cella nello stesso magazzino, prenotata alla missione: porta, indirizzo e rotta nuovi;
    /// destinazione, gateway e segno quelli di prima.
    /// </summary>
    AnotherCell,

    /// <summary>
    /// Nel magazzino scelto non c'e' un'altra cella per la missione (ogni causa di
    /// <see cref="MissionDestinationOutcome.NoStorageCell"/>): la causa e' scritta sulla missione, e chi
    /// chiama la manda allo scarto, che le toglie il gateway dell'operatore (<c>PathMission.RejectTo</c>);
    /// senza un punto di scarto il dispositivo va in errore e aspetta l'operatore (risposta 1 del
    /// proprietario dell'8/10).
    /// </summary>
    NoOtherCell,

    /// <summary>
    /// Adesso non si sa: la porta proposta non ha un percorso, la missione si e' mossa o e' uscita dal
    /// motore, o un'altra ricerca la tiene. La missione tiene la cella che aveva; se la prenotazione
    /// l'aveva gia' lasciata la perde, e il navigatore gliene prenota un'altra nel magazzino scelto.
    /// Chi chiama manda il dispositivo in errore, e il dispositivo aspetta l'operatore: nessun nuovo
    /// deposito nella cella rifiutata, nessuno scarto (risposta 1 del proprietario dell'8/10, che toglie
    /// le tre regole di prima - ritentare la stessa cella, tenerla, lo scarto).
    /// </summary>
    NotNow,

    /// <summary>
    /// Come <see cref="NotNow"/>, perche' il gestionale non risponde: in piu' chi chiama, dopo l'errore
    /// del dispositivo, fa lasciare alla missione la porta rifiutata (<see cref="MissionGateway.LeaveTheRefusedCell"/>,
    /// risposta 7 del proprietario dell'8/10 sera).
    /// </summary>
    WarehouseSilent
}

/// <summary>
/// Le scritture sulle missioni attive che arrivano da fuori: lo spostamento di una missione su un
/// altro dispositivo, e il cambio della sua destinazione. Il flusso del magazzino del cambio serve
/// anche al navigatore, che con lui rifa' la cella persa, e controlla quella di un magazzino scelto
/// dall'operatore (<see cref="KeepTheStorageCellAsync"/>); e ai dispositivi, che con lui cercano
/// un'altra cella quando il PLC rifiuta quella di una missione dell'operatore
/// (<see cref="AnotherCellInTheChosenStorageAsync"/>).
/// </summary>
/// <remarks>
/// <para>
/// Prima dell'estrazione l'Editor caricava il motore nel proprio processo e la lista delle
/// missioni in corso era legata agli oggetti vivi: cambiare la cella «PositionItemId» nella
/// griglia era un'assegnazione diretta a <see cref="Items.PathMission.PositionItemId"/>, e il
/// setter faceva il resto (indice delle posizioni, evento ai client, tipo di missione, coda di
/// estrazione, allocazione nella cella collegata, salvataggio). Dopo l'estrazione l'Editor vede
/// solo DTO e quella modifica non aveva piu' una strada: questa e' la strada.
/// </para>
/// <para>
/// Lo spostamento passa dallo stesso setter, quindi fa esattamente quello che faceva la griglia
/// di prima, con una guardia in piu' che prima non c'era: un dispositivo che porta gia' un'altra
/// missione non se ne prende una seconda, perche' l'indice delle posizioni tiene una missione
/// per dispositivo e la seconda avrebbe reso invisibile la prima.
/// </para>
/// <para>
/// Due cose che la griglia di prima non faceva, viste in sala prove l'11/9: la rotta vecchia
/// viene scartata e la costruzione riaccodata subito (prima la missione restava con gli step
/// del posto vecchio finche' qualcuno non premeva «esegui passo»), e con uno step in volo lo
/// spostamento e' rifiutato — la stessa rete di <c>DestinationItemId</c> e di
/// <c>ActiveMissions.Remove</c>, perche' cancellare lo step in volo rende libero a meta'
/// movimento il dispositivo che lo sta eseguendo. Dal 30/9, dove il navigatore andrebbe dritto
/// alla destinazione la rotta nuova si costruisce subito, anche a motore fermo, invece di
/// aspettare il Run in coda (<see cref="Move"/>). In Recover, dall'8/10, lo spostamento e' rifiutato.
/// </para>
/// </remarks>
public static class MissionGateway
{
    /// <summary>
    /// Sposta la missione sul dispositivo indicato. Vale in Stop e, come la griglia di prima, anche in
    /// Run; in Recover e' rifiutato (<see cref="MissionMoveOutcome.EngineRecovering"/>). La rotta dal
    /// posto vecchio si scarta; dove il navigatore andrebbe dritto alla destinazione quella nuova si
    /// costruisce subito, anche a motore fermo, altrove la missione va in coda come prima (decisione 2
    /// del proprietario del 30/9).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>La rotta subito.</b> Solo dove il navigatore, dal dispositivo raggiunto, costruirebbe la
    /// rotta dritta verso la destinazione senza chiedere niente al gestionale
    /// (<c>Navigator.GoesStraightToTheDestination</c>: la destinazione dell'operatore, o un
    /// dispositivo senza modalita' di deposito ne' destinazione fissa; e non un cambio cella, che la
    /// missione diventa anche per le sue due porte, ne' un'estrazione prelevata che le porte fanno
    /// deposito, item 54: <c>PathMission.TheDoorsKeepItOffTheStraightRoute</c>),
    /// e con la stessa funzione del navigatore (<c>Navigator.BuildRouteStraightToTheDestination</c>):
    /// cosi' la rotta e' quella che il navigatore costruirebbe al Run, e l'operatore la vede adesso.
    /// Verso una destinazione fissa o con una modalita' di deposito la missione va in coda come
    /// prima, perche' li' il navigatore riscrive la destinazione o chiede una cella al gestionale; ci
    /// va un cambio cella, che il navigatore costruisce a modo suo; e ci va anche quando dalla
    /// posizione nuova un percorso non c'e': lo spostamento e' un fatto del campo, e non si rifiuta.
    /// La costruzione tiene il posto delle ricerche della missione (<c>Navigator.TryClaimCellSearch</c>),
    /// come il giro delle rotte, e con una ricerca o un cambio di destinazione in corso la missione
    /// va in coda come prima. In Run il dispositivo di posizione si sveglia, come dopo una rotta del
    /// giro (<c>Navigator.StartCellSearch</c>).
    /// </para>
    /// <para>
    /// <b>Con la rotta subito la missione esce dalla coda</b>, se c'era: ci va quando si ferma su un
    /// dispositivo, e ci resta finche' il giro non le costruisce la rotta, anche per ore se un
    /// percorso non c'e'. Quella richiesta chiedeva la rotta che adesso c'e', e il giro, ripescandola
    /// piu' avanti con la missione ferma su un dispositivo di passaggio, rifarebbe da li' la ricerca
    /// secondo la modalita' di deposito di quel dispositivo: una cella, o il rigetto, per una missione
    /// che va da un'altra parte.
    /// </para>
    /// <para>
    /// <b>Il freno del giro delle rotte dimentica i fallimenti</b> che contava alla missione
    /// (<c>Navigator.ForgetRouteRetries</c>), con la rotta subito o senza: erano della posizione di
    /// prima, e lo spostamento e' un fatto dell'operatore che li rende vecchi. Con la missione in coda,
    /// verso una destinazione fissa o con una modalita' di deposito, la richiesta si
    /// riprende al giro dopo e non allo scadere di un'attesa della posizione lasciata, che cresce
    /// fino a 5 secondi; e un fallimento dalla posizione nuova conta da uno, invece
    /// di scrivere l'avviso del decimo giro per una rotta che l'operatore ha appena rifatto.
    /// </para>
    /// <para>
    /// <b>La rotta vecchia si scarta prima di cambiare la posizione</b>, non dopo: il setter della
    /// posizione avvia il lavoro di sfondo (<c>PathMission.PositionWork</c>), che annuncia subito i due
    /// dispositivi e, per un <c>PickUpAndStoreAgain</c> arrivato alla sua destinazione, scrive lui la
    /// destinazione e la rotta verso il magazzino. Scartata dopo, il primo annuncio poteva riportare
    /// i gettoni della rotta vecchia (il posto tenuto dal dispositivo raggiunto, che lo scarto
    /// restituisce), e lo scarto poteva cancellare la rotta appena scritta dal lavoro di sfondo. Per
    /// lo stesso motivo la rotta subito non si costruisce verso la destinazione che la missione aveva
    /// prima dello spostamento quando e' il dispositivo raggiunto: li' la rotta, se c'e', e' del lavoro
    /// di sfondo, e la missione va in coda come prima.
    /// </para>
    /// <para>
    /// <b>Lo spostamento vince sempre</b> (decisione 2 del proprietario del 10/10, voce 41): non
    /// confronta la versione della rotta, e lo sguardo allo step in volo, la rimozione e la posizione
    /// stanno insieme sotto il lucchetto della rotta (<c>PathMission.MoveTo</c>). Uno scrittore che ha
    /// calcolato una rotta dal posto vecchio (il commit di un cambio, il giro delle rotte) trova la
    /// versione cambiata e non la scrive; la rotta nuova la costruisce lo spostamento stesso, o il
    /// navigatore al giro dopo.
    /// </para>
    /// <para>
    /// <b>Gli annunci.</b> Costruire una rotta non annuncia niente (<c>MissionSteps.AddRange</c>): la
    /// missione si annuncia con i suoi step nuovi. Il setter di <c>RunningStepId</c> annuncia il
    /// dispositivo che teneva lo step scartato, non quello che gli teneva il posto e se lo vede
    /// restituire: tutti e due si annunciano dopo il lavoro sulla rotta. I due dispositivi dello
    /// spostamento li annuncia il lavoro di sfondo, partito dopo lo scarto.
    /// </para>
    /// </remarks>
    public static MissionMoveOutcome Move(Guid missionId, Guid itemId) => Move(missionId, itemId, applying: null);

    /// <summary>
    /// <see cref="Move(Guid, Guid)"/>, asked by the operator (<paramref name="applying"/> null) or applied
    /// from the pending change that waited for the end of a step (<see cref="PendingMissionChanges"/>, item
    /// 54). With a step of the mission in flight the operator's move becomes the mission's pending change
    /// (the last one wins); the pending one being applied stays pending. A move that is written replaces
    /// any pending change of the mission.
    /// </summary>
    internal static MissionMoveOutcome Move(Guid missionId, Guid itemId, PendingMissionChanges.Entry? applying)
    {
        // In Recover il motore sta ancora caricando le missioni: uno spostamento scritto adesso non
        // arriverebbe al wcs.db (le scritture dei setter non partono durante il caricamento), e al
        // riavvio dopo la missione tornerebbe dov'era; e li' PathBuilder salta i filtri dei
        // magazzini. Si rifiuta prima di guardare la missione, che puo' non essere ancora caricata
        // (risposta 4 del proprietario dell'8/10, che sostituisce la decisione 11 del 2/10).
        if (Navigator.NavStatus == NavigatorStatusTypes.Recover) return MissionMoveOutcome.EngineRecovering;

        var mission = ActiveMissions.Instance[missionId];
        if (mission == null) return MissionMoveOutcome.MissionNotFound;

        var position = PathDevices.Instance[itemId];
        if (position == null) return MissionMoveOutcome.DeviceNotFound;

        if (mission.PositionItemId == itemId)
        {
            // Item 54, fix round 1 of task 4: one change per mission, the last one asked wins, also when
            // it has nothing to do now. With a step in flight the mission is leaving this device: the
            // move onto it waits for the end of the step like any other (and replaces the pending one).
            // With nothing in flight it is already done, and it is the operator's last word: a pending
            // change of the mission is gone, and is not applied after it.
            if (mission.StepInFlight() is { } leaving)
                return MoveAfterTheStep(mission, itemId, leaving, applying);

            if (PendingMissionChanges.Settle(mission, applying)) WakeAfterAPendingChange(mission);
            return MissionMoveOutcome.Moved;
        }

        var occupant = ActiveMissions.Instance.MissionAtPosition(itemId);
        if (occupant != null && !ReferenceEquals(occupant, mission)) return MissionMoveOutcome.DeviceOccupied;

        var freed = PathDevices.Instance[mission.PositionItemId];
        var destination = mission.DestinationItemId;

        // La rotta calcolata dal posto vecchio non vale piu', e si scarta prima che la posizione
        // cambi e parta il lavoro di sfondo del setter (vedi le note). Non con RemoveRouteAndItsSeats,
        // come il commit del cambio: quello restituisce anche i posti appesi, e lo puo' fare perche'
        // con il cambio cominciato nessuno sta avviando uno step della missione. Lo spostamento non
        // ferma gli avvii, e un posto appena preso da un avvio a meta' (StartPutToMOVE_LU, fra il
        // posto e il gettone) sembrerebbe appeso: restituirlo lo darebbe a un secondo mittente. Un
        // posto appeso davvero lo restituisce il prossimo ExecuteStep di chi lo tiene.
        //
        // Lo sguardo allo step in volo, la rimozione e la posizione stanno insieme sotto il lucchetto
        // della rotta (PathMission.MoveTo, voce 41): nessuno scrittore della rotta passa in mezzo con
        // una rotta dal posto vecchio. Lo spostamento non confronta la versione: vince sempre
        // (decisione 2 del proprietario del 10/10). La guardia sta prima della rimozione, per la
        // stessa ragione del setter di DestinationItemId.
        List<AutomationDevice> holders = [];
        MissionStep? inFlight = null;

        var moved = mission.MoveTo(itemId, () =>
        {
            inFlight = mission.StepInFlight();
            if (inFlight != null) return false;

            holders = mission.DevicesHoldingItsSteps();
            return true;
        });

        // Item 54, decision 4: the step in flight wins, and the move waits for its end instead of being
        // refused. Nothing was touched: the route and the position are the ones of the step. The write
        // has no version to compare and no steps to write (MoveTo), so the guard above is the only way
        // it is not written.
        if (moved != PathMission.RouteWriteOutcome.Written)
            return MoveAfterTheStep(mission, itemId, inFlight, applying);

        // The move is written: it is the operator's last word on the mission, and a pending change
        // (the one being applied, or an older one) has nothing left to wait for (item 54).
        PendingMissionChanges.Settle(mission, applying);

        // I fallimenti che il freno del giro delle rotte le contava erano della posizione di prima,
        // che lo spostamento costruisca la rotta o no (vedi le note): una richiesta in coda si
        // riprende al giro dopo.
        Navigator.ForgetRouteRetries(mission.MissionId);

        // Senza destinazione non si costruisce niente (la costruzione la riscriverebbe: un tipo None
        // diventerebbe MovingToItem verso nessuno), e nemmeno verso il dispositivo raggiunto (vedi le
        // note). DestinationItemId e' annullabile: il nullo e il vuoto sono la stessa assenza.
        var built = destination is { } target && target != Guid.Empty && target != itemId &&
                    BuildTheRouteAtOnce(mission, position);

        // Con la rotta la missione esce dalla coda (vedi le note), e dopo che il posto delle ricerche
        // e' stato restituito: esce anche se il giro l'ha rimessa in coda trovandolo preso.
        // Altrimenti la costruzione si riaccoda, e la fa il ciclo del navigatore in Run (con il
        // motore fermo resta in coda fino al Run).
        if (built)
            RoutesToBuild.Instance.Remove(mission.MissionId);
        else
            RoutesToBuild.Instance.EnqueueIfAbsent(mission);

        if (built)
            Navigator.EventSink?.MissionChanged(mission, EngineMissionChange.StepChanged);
        foreach (var device in holders)
            Navigator.EventSink?.ItemChanged(device);

        // Il dispositivo lasciato libero si risveglia da se', come dopo uno scarico
        // (ProcessStandardUnloading): nel motore nessuno ascolta l'evento di carico, e' il
        // dispositivo che si e' liberato a tirare a se' l'UDC ferma a monte (CheckGetFromItem).
        // Senza questa riga l'UDC a monte aspettava un «esegui passo» dell'operatore (sala prove,
        // 11/9). ExecuteStep esce da solo se il motore non e' in Run.
        Wake(freed);

        // La rotta nuova non la vede nessun altro: si svegliano il dispositivo di posizione e chi
        // riceve il suo primo step, come dopo il cambio di destinazione e come dopo una rotta
        // costruita dal giro del navigatore.
        if (built) WakeTheDevicesThatStartTheNextStep(mission, itemId);

        return MissionMoveOutcome.Moved;
    }

    /// <summary>
    /// La rotta dello spostamento, costruita subito dove il navigatore andrebbe dritto alla
    /// destinazione (<see cref="Move"/>). Vero se la missione ha adesso una rotta; falso se qui non si
    /// costruisce, o se un percorso non c'e', e allora la missione va in coda come prima.
    /// </summary>
    /// <remarks>
    /// Tiene il posto delle ricerche della missione come il giro delle rotte
    /// (<c>Navigator.StartCellSearch</c>): due costruzioni della stessa rotta insieme possono lasciare
    /// gli step di una con la destinazione dell'altra. Con il posto preso guarda le due porte della
    /// missione: se ne fanno un cambio cella, o un deposito che aspetta la consegna del prelievo (item 54,
    /// <c>PathMission.TheDoorsKeepItOffTheStraightRoute</c>) il
    /// navigatore non andrebbe dritto, e la missione va in coda come prima. Niente gestionale: nei
    /// casi ammessi (<c>Navigator.GoesStraightToTheDestination</c>, e le porte) la costruzione legge la
    /// mappa delle porte del database del WCS, cerca nel grafo e scrive solo sulla missione, quindi
    /// <c>Move</c> resta sincrona come la chiama l'host; il <c>Task.Run</c> la tiene fuori dal
    /// contesto di sincronizzazione di chi chiama, come <c>PathMission.GetDoorCodeSync</c>.
    /// </remarks>
    private static bool BuildTheRouteAtOnce(PathMission mission, AutomationDevice position)
    {
        if (!Navigator.GoesStraightToTheDestination(mission, position)) return false;

        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Navigator.TryClaimCellSearch(mission.MissionId, finished.Task)) return false;

        try
        {
            // Con il posto preso si riguarda che la missione sia ancora attiva e ancora li': uno
            // spostamento o una rimozione arrivati fra il setter e il posto lascerebbero una rotta
            // che non parte da dove la missione e', o step orfani di una missione tolta.
            if (!ReferenceEquals(ActiveMissions.Instance[mission.MissionId], mission) ||
                mission.PositionItemId != position.ItemGuid)
                return false;

            var built = Task.Run(async () =>
                    !await mission.TheDoorsKeepItOffTheStraightRoute() &&
                    await Navigator.BuildRouteStraightToTheDestination(mission, position) == AutomationDevice.RouteBuildStatusTypes.Available)
                .GetAwaiter().GetResult();

            if (built)
            {
                // Move li ha gia' dimenticati, e si dimenticano di nuovo qui, prima di restituire il
                // posto: una ricerca del giro partita prima dello spostamento, finita male fra quella
                // dimenticanza e la presa del posto, ne puo' aver contato uno. Cosi' il giro che ripesca
                // la richiesta vecchia fra la restituzione e la rimozione dalla coda (Move) non la
                // rimette in coda per l'attesa, la cerca da qui e trova la rotta.
                Navigator.ForgetRouteRetries(mission.MissionId);
                Navigator.WriteLog(
                    $"Missione {mission.MissionId} spostata su {position.ItemGuid}: rotta verso {mission.DestinationItemId} costruita subito, di {mission.MissionSteps.Count} passi",
                    LogEntryType.Information);
            }

            return built;
        }
        catch (Exception ex)
        {
            Navigator.WriteLog(
                $"Missione {mission.MissionId} spostata su {position.ItemGuid}: la rotta non si e' costruita subito, la costruisce il navigatore ({ex.Message})\r\nStackTrace: {ex.StackTrace}",
                LogEntryType.Warning);
            return false;
        }
        finally
        {
            Navigator.ReleaseCellSearchClaim(mission.MissionId, finished.Task);
            finished.TrySetResult();
        }
    }

    /// <summary>
    /// Cambia la destinazione della missione con il ricalcolo immediato della rotta (specifica del
    /// 30/9, compito 3 del piano). Vale in ogni stato del motore: la rotta si calcola subito anche a
    /// motore fermo, cosi' l'operatore la vede; in Run il dispositivo di posizione riparte da se'.
    /// </summary>
    /// <param name="missionId">La missione.</param>
    /// <param name="itemId">La destinazione scelta: un dispositivo, o un magazzino (la cella la sceglie il gestionale).</param>
    /// <param name="gateway">La scelta sul gateway; il valore predefinito tiene quello che c'e'.</param>
    /// <remarks>
    /// <para>
    /// <b>Prima si calcola, poi si cambia.</b> Le guardie non toccano niente; la rotta si sonda con
    /// <c>PathMission.ProbeRouteSteps</c>, che non tocca la missione; e solo se c'e' un percorso (e,
    /// per un magazzino, una cella) la missione cambia, in un tratto breve. Un rifiuto lascia
    /// destinazione, gateway, rotta, gettoni dei dispositivi e prenotazione com'erano - tranne la
    /// cella persa: se il cambio verso un magazzino e' rifiutato dopo che la prenotazione ha lasciato
    /// la cella che la missione teneva (il contratto ne tiene una per missione), la missione lascia
    /// porta, indirizzo e rotta e torna al navigatore, che le prenota un'altra cella nel magazzino
    /// della sua destinazione (decisioni del proprietario dell'1/10, <see cref="ChangeToStorageAsync"/>).
    /// </para>
    /// <para>
    /// <b>Il magazzino.</b> Prima la rotta verso il magazzino scelto (nessun percorso: rifiuto senza
    /// chiamare il gestionale); poi la domanda al gestionale senza prenotare, e la cella che propone
    /// si guarda subito: nessuna cella, una cella fuori dal magazzino scelto, senza percorso verso la
    /// sua porta o senza indirizzo leggibile, e il rifiuto non tocca la cella che la missione ha. Solo
    /// poi la prenotazione, con il ciclo di vita di sempre (<c>ReserveOrKeepStorageAsync</c>, che
    /// lascia la vecchia), e la porta prenotata si riguarda se e' un'altra, perche' il gestionale puo'
    /// rispondere diverso dalla domanda di prima. Ogni rifiuto dopo la prenotazione la restituisce. E'
    /// lo stesso flusso con cui il navigatore rifa' la cella persa (<see cref="ReserveACellAndCommitAsync"/>).
    /// Cambiando destinazione via da un magazzino, la prenotazione vecchia si restituisce dopo il
    /// commit. Un gruppo di magazzini scelto vale come un magazzino (<see cref="StoragesOf"/>): la
    /// cella in uno dei suoi. Verso un magazzino non c'e' mai <see cref="MissionDestinationOutcome.Unchanged"/>:
    /// sceglierlo di nuovo, dopo averlo scelto, e' chiedere un'altra cella, e quella che la missione
    /// ha si esclude dalla domanda (<see cref="CellToReplaceAsync"/>); senza un'altra, il rifiuto lo
    /// dice (<see cref="MissionDestinationResult.NoOtherFreeCell"/>) e la cella resta. Ma se con la
    /// scelta di nuovo cambia il gateway, la cella resta e si rifa' solo la rotta, senza chiedere
    /// niente al gestionale (decisione 6 del proprietario del 2/10, <see cref="KeepTheCellAndRouteAgain"/>).
    /// </para>
    /// <para>
    /// <b>Un'estrazione mandata in un magazzino</b> diventa quello che e' fisicamente (decisione 8 e
    /// risposte 1-3 del proprietario del 2/10): gia' prelevata, un deposito, e dopo il commit il fatto
    /// «l'estrazione prelevata e' diventata un deposito» va nella coda durevole del WCS, da cui il
    /// dispatcher lo manda al gestionale, che riapre l'ordine servito al prelievo (voce 45, decisione 1
    /// del proprietario del 10/10: il cambio resta valido anche con il gestionale fermo, e il fatto
    /// parte quando risponde; <see cref="WaitForThePickedExtractionReportAsync"/>); con l'UDC ancora
    /// nella sua cella, un cambio cella, e prima di prenotare
    /// il gestionale sa che la missione lascia l'assegnazione e che l'UDC esce dalla coda di
    /// estrazione. Se dopo quelle segnalazioni il cambio e' rifiutato lo stesso, la missione si chiude
    /// e l'UDC resta nella sua cella (<see cref="ChangeToStorageAsync"/>).
    /// </para>
    /// <para>
    /// <b>Il gateway</b> lo decide l'operatore (decisione del 30/9, sera): si sonda con quello scelto,
    /// e sulla missione si scrive solo al commit. Senza percorso con lui, l'esito dice quale era e se
    /// senza di lui il percorso ci sarebbe. Un gateway che la missione ha gia' raggiunto (la sua
    /// posizione, o il gruppo della posizione) non obbliga a niente e non si scrive: il setter della
    /// posizione lo azzera all'arrivo, e scritto adesso non cadrebbe piu'.
    /// </para>
    /// <para>
    /// <b>Fuori da ogni lucchetto le chiamate al gestionale</b>, e senza il gettone di una richiesta
    /// HTTP: una prenotazione presa con la risposta persa lascerebbe una cella appesa. Mentre il
    /// cambio e' in corso nessuna ricerca della cella prenota per la missione
    /// (<c>Navigator.TryClaimCellSearch</c> per quelle del giro delle rotte,
    /// <c>PathMission.TryBeginOperatorChange</c> per quelle dei dispositivi), e nessun dispositivo
    /// avvia uno step della missione (<c>PathMission.TryBeginStepStart</c>): il movimento singolo,
    /// il <c>GetFrom</c>, il prelievo, il deposito e i movimenti di gruppo si contano sulla missione
    /// prima del primo gettone, come la riverifica e la ricerca delle celle di un gruppo prima di
    /// toccarla, e con il cambio cominciato rinunciano. Cosi' lo sguardo sugli step in
    /// volo (<c>PathMission.StepInFlight</c>), fatto subito dopo, vale fino al commit: la rotta
    /// vecchia non parte durante le chiamate al gestionale. Il commit scrive poi solo se la versione
    /// della rotta e' ancora quella che ha letto, senza prendere il semaforo di esecuzione del
    /// dispositivo di posizione: una posizione che esegue, o che dorme nella pausa dopo un arrivo, non
    /// rifiuta piu' il cambio (voce 41, corsa 4). Alla fine si svegliano i dispositivi
    /// che possono avviare il prossimo step, perche' un avvio rinunciato nessuno lo ripete.
    /// </para>
    /// </remarks>
    public static Task<MissionDestinationResult> ChangeDestinationAsync(Guid missionId, Guid itemId,
        MissionGatewayChoice gateway = default) =>
        ChangeDestinationAsync(missionId, itemId, gateway, MissionDestinationOptions.None, applying: null);

    /// <summary>
    /// <see cref="ChangeDestinationAsync(Guid, Guid, MissionGatewayChoice)"/> with what else the operator
    /// chose (item 54, decisions 5 and 6 of the owner of 13/10): what a relocator does with the load unit,
    /// or a precise storage cell.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Towards a relocator</b> (a device some storage names as its <c>RelocateItemId</c>) with
    /// <see cref="RelocatorArrival.Deposit"/> or <see cref="RelocatorArrival.Stay"/>: the change towards a
    /// device of every day, whose commit also writes the choice on the mission
    /// (<c>PathMission.RelocatorArrival</c>). Arrived at the relocator, the choice decides instead of the
    /// relocator's configuration (<c>Navigator.FindWhereStoreLu</c>): the warehouse gives a cell the
    /// relocator reaches and the relocator deposits it, or the load unit stays. A choice towards a device
    /// that is not a relocator is refused (<see cref="MissionDestinationOutcome.NotARelocator"/>).
    /// </para>
    /// <para>
    /// <b>A precise cell</b> (<see cref="MissionDestinationOptions.CellDoorCode"/>), chosen in a storage,
    /// in a group of storages or among the cells of a relocator (<paramref name="itemId"/>): the cell's
    /// storage comes from the WCS's door map, and must be in that choice; then the storage flow of every
    /// day (<see cref="ReserveACellAndCommitAsync"/>), where the warehouse is not asked for a cell but
    /// reserves that one (<c>IWarehouse.ReserveStorageCellAsync</c>), or refuses it with the reason
    /// (<see cref="MissionDestinationOutcome.CellRefused"/>). The destination is the cell's storage, so the
    /// route goes through the relocator that serves it, and the relocator deposits in that cell. A door B
    /// code is refused before asking anything (owner's answer 7 of 13/10), when the WCS's door map says so.
    /// </para>
    /// </remarks>
    public static Task<MissionDestinationResult> ChangeDestinationAsync(Guid missionId, Guid itemId,
        MissionGatewayChoice gateway, MissionDestinationOptions options) =>
        ChangeDestinationAsync(missionId, itemId, gateway, options, applying: null);

    /// <summary>
    /// <see cref="ChangeDestinationAsync(Guid, Guid, MissionGatewayChoice)"/>, asked by the operator
    /// (<paramref name="applying"/> null) or applied from the pending change that waited for the end of
    /// a step (<see cref="PendingMissionChanges"/>, item 54). With a step of the mission in flight, or a
    /// device starting one, the operator's change becomes the mission's pending change (the last one
    /// wins) and the answer is <see cref="MissionDestinationOutcome.Pending"/>; the pending one being
    /// applied stays pending. A change that begins replaces any pending change of the mission: it is
    /// the operator's last word, whatever its outcome.
    /// </summary>
    internal static async Task<MissionDestinationResult> ChangeDestinationAsync(Guid missionId, Guid itemId,
        MissionGatewayChoice gateway, MissionDestinationOptions options, PendingMissionChanges.Entry? applying)
    {
        var mission = ActiveMissions.Instance[missionId];
        if (mission == null) return new(MissionDestinationOutcome.MissionNotFound);

        var destination = PathDevices.Instance[itemId];
        if (destination == null) return new(MissionDestinationOutcome.DeviceNotFound);

        if (gateway.Mode == MissionGatewayMode.Set && PathDevices.Instance[gateway.ItemId] == null)
            return new(MissionDestinationOutcome.GatewayNotFound);

        if (options.AtTheRelocator != RelocatorArrival.None && !string.IsNullOrEmpty(options.CellDoorCode))
            throw new ArgumentException(
                "A relocator's arrival and a precise cell do not go together: the precise cell is deposited by the relocator that serves it",
                nameof(options));

        // Item 54, decision 5: what a relocator does with the load unit is chosen only for a relocator.
        if (options.AtTheRelocator != RelocatorArrival.None && !IsARelocator(destination))
        {
            Navigator.WriteLog(
                $"Missione {missionId}: destinazione non cambiata in {itemId} ({MissionDestinationOutcome.NotARelocator}): nessun magazzino lo ha come ricollocatore, e la scelta «{options.AtTheRelocator}» non vale",
                LogEntryType.Information);
            return new(MissionDestinationOutcome.NotARelocator);
        }

        // The device the operator asked for: the pending change keeps it, also when the precise cell
        // makes the destination the cell's storage (below).
        var requestedId = itemId;

        // Item 54, decision 6: a precise cell. Its storage, from the WCS's door map, is the destination,
        // and it must be in what the operator chose; a door B is refused here, before asking anything.
        if (options.CellDoorCode != null)
        {
            var (cellStorage, notThisCell) = await CellInTheChoiceAsync(options.CellDoorCode, destination);
            if (cellStorage == null)
            {
                Navigator.WriteLog(
                    $"Missione {missionId}: destinazione non cambiata in {itemId} ({MissionDestinationOutcome.CellRefused}, {notThisCell!.CellRefusal}): {notThisCell.CellRefusalReason}",
                    LogEntryType.Information);
                return notThisCell;
            }

            destination = cellStorage;
            itemId = cellStorage.ItemGuid;
        }

        // Item 54 (decision 3 of the owner of 13/10): a change of cell or a defragmentation is
        // redirected like every other mission. Until then they answered «not redirectable», because
        // they build their steps their own way: the commit now writes the probed route, the mission
        // takes the type of where it goes (PathMission.BecomeWhatTheNewDestinationMakesIt), and the
        // cell it was going to goes back to the warehouse through the durable queue (CellToGiveBackAsync).

        var origin = mission.PositionItemId;
        if (IsReachedFrom(origin, itemId))
        {
            // Item 54, fix round 1 of task 4: with a step of the mission in flight, or a device starting
            // one, the load unit is leaving the device it is on, and the change is applied from where
            // the step ends: it waits like any other, as the last one asked (and replaces the pending
            // one). Only with the load unit standing there is it «already there», a refusal that, like
            // the others before the change begins, leaves a pending change as it is.
            var leaving = mission.StepInFlight();
            if (leaving == null && !mission.StepStarting) return new(MissionDestinationOutcome.AlreadyThere);

            var reason = leaving != null
                ? $"lo step {leaving.StepId} e' in volo ({leaving.FromNodeId} -> {leaving.ToNodeId}) e lascia la destinazione chiesta"
                : "un dispositivo sta avviando uno step della missione, che lascia la destinazione chiesta";
            var deferred = Defer(mission, PendingMissionChange.Destination(requestedId, gateway, options), leaving, applying, reason);
            Navigator.WriteLog(
                deferred.Result.Outcome == MissionDestinationOutcome.Pending
                    ? $"Missione {missionId}: il cambio di destinazione in {itemId} aspetta la fine dello step: {reason}"
                    : $"Missione {missionId}: destinazione non cambiata in {itemId} ({deferred.Result.Outcome}): {deferred.Reason}",
                deferred.Result.Outcome == MissionDestinationOutcome.Pending ? LogEntryType.Information : deferred.ReasonLevel);
            return deferred.Result;
        }

        // Un gateway gia' raggiunto (la posizione, o il gruppo della posizione) non obbliga a niente,
        // e il setter della posizione lo azzera proprio per questo: scritto sulla missione, con la
        // missione gia' li', non cadrebbe piu' e ogni rotta successiva dovrebbe tornarci.
        var chosenGateway = gateway.Mode switch
        {
            MissionGatewayMode.Clear => Guid.Empty,
            MissionGatewayMode.Set => gateway.ItemId,
            _ => mission.Gateway
        };
        if (chosenGateway != Guid.Empty && IsReachedFrom(origin, chosenGateway))
            chosenGateway = Guid.Empty;

        // Il gateway cambia solo se l'operatore lo tocca (togliere, impostarne un altro) e quello che
        // ne viene non e' quello che la missione ha gia': nominare di nuovo lo stesso, o toglierlo a
        // una missione che non ne ha, non cambia niente. Verso il magazzino della cella che la
        // missione ha, cambiare solo il gateway tiene la cella (ChangeToStorageAsync).
        var gatewayChanged = gateway.Mode != MissionGatewayMode.Keep && chosenGateway != mission.Gateway;

        // Verso un magazzino non c'e' «gia' cosi'»: la cella la sceglie il gestionale, e sceglierlo di
        // nuovo e' il modo che l'operatore ha di cambiarla quando quella della missione non va piu'
        // bene. Con il segno il motore non ne cerca un'altra (StoreVerificationResults.ChosenByOperator)
        // e l'UDC aspetta sul ricollocatore: un «gia' cosi'» che non guarda la cella ve la lasciava
        // per sempre, senza nemmeno chiedere al gestionale.
        var storages = StoragesOf(destination);

        if (storages.Count == 0 && mission.DestinationItemId == itemId && mission.Gateway == chosenGateway &&
            mission.OperatorDestination && mission.MissionSteps.Count > 0 &&
            mission.RelocatorArrival == options.AtTheRelocator)
        {
            // Item 54, fix round 1 of task 4: «unchanged» is the operator's last word too, also during a
            // step (the destination they ask is the one the mission keeps from where the step ends): a
            // pending change of the mission is gone, and is not applied after it. The starts it held
            // are woken.
            if (PendingMissionChanges.Settle(mission, applying)) WakeAfterAPendingChange(mission);
            return new(MissionDestinationOutcome.Unchanged);
        }

        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DestinationChange change;

        if (!Navigator.TryClaimCellSearch(missionId, finished.Task))
        {
            change = DestinationChange.Refused(MissionDestinationOutcome.CellSearchInFlight,
                "il posto delle ricerche della missione e' preso: una ricerca della cella del giro delle rotte, o un altro cambio o spostamento");
        }
        else
        {
            var began = false;

            try
            {
                switch (mission.TryBeginOperatorChange())
                {
                    case PathMission.OperatorChangeStart.CellSearchInFlight:
                        change = DestinationChange.Refused(MissionDestinationOutcome.CellSearchInFlight,
                            "una ricerca della cella di un dispositivo, o un altro cambio, e' in corso per la missione");
                        break;

                    case PathMission.OperatorChangeStart.StepStarting:
                        // Item 54, decision 4: the step being started will be in flight in a moment,
                        // and the change waits for its end instead of being refused.
                        change = Defer(mission, PendingMissionChange.Destination(requestedId, gateway, options), null, applying,
                            "un dispositivo sta avviando uno step della missione");
                        break;

                    default:
                        began = true;

                        // Le copie della sua porta, se ne ha (CopiesOfTheDoor): le fermano e le lasciano
                        // i due cambi, e ripartono qui sotto, dopo la missione.
                        var copies = new CopiesOfTheDoor(mission);

                        // The pending change being applied (item 54, fix round 2 of task 4) stays the
                        // mission's pending change until its outcome is known: a refusal that passes by
                        // itself after the change has begun (a copy of the door that cannot be stopped,
                        // the mission moved, or the route changed, meanwhile) keeps it, and it is still
                        // there when the change ends (EndOperatorChange) and wakes the devices below, so
                        // no step of the old route starts in between. Taken out when it began, and put
                        // back only after the wake, it let one start.
                        var keepsWaiting = false;

                        try
                        {
                            // Da qui nessuno avvia uno step della missione: uno step partito prima ha
                            // gia' i suoi gettoni scritti, e se c'e' lo si vede adesso, prima di
                            // chiamare il gestionale.
                            if (mission.StepInFlight() is { } inFlight)
                            {
                                // Item 54, decision 4: the step in flight wins, and the change waits
                                // for its end. It is recorded before the change ends (EndOperatorChange
                                // below), so no step of the old route starts in between.
                                change = Defer(mission, PendingMissionChange.Destination(requestedId, gateway, options), inFlight, applying,
                                    $"lo step {inFlight.StepId} e' in volo ({inFlight.FromNodeId} -> {inFlight.ToNodeId})");
                                keepsWaiting = true;
                            }
                            else
                            {
                                // The change begins: it is the operator's last word on the mission,
                                // and a pending change of the mission has nothing left to wait for. The
                                // one being applied is taken out when its outcome is known (above).
                                if (applying == null) PendingMissionChanges.Settle(mission, applying: null);

                                var giveBack = await CellToGiveBackAsync(mission);
                                change = storages.Count > 0
                                    ? await ChangeToStorageAsync(mission, origin, destination, storages, chosenGateway,
                                        gatewayChanged, copies, giveBack, options.CellDoorCode?.Trim())
                                    : await ChangeToDeviceAsync(mission, origin, destination, chosenGateway, copies, giveBack,
                                        options.AtTheRelocator);
                                keepsWaiting = PendingMissionChanges.PassesByItself(change.Result.Outcome);
                            }
                        }
                        finally
                        {
                            if (applying != null && !keepsWaiting) PendingMissionChanges.Settle(mission, applying);

                            copies.LeaveIfTheDoorIsGone();
                            mission.EndOperatorChange();
                            copies.Restart();
                        }

                        break;
                }
            }
            finally
            {
                Navigator.ReleaseCellSearchClaim(missionId, finished.Task);
                finished.TrySetResult();

                // Il risveglio a cambio finito e a posto delle ricerche restituito: chi si sveglia
                // trova la missione libera da ogni cambio e da ogni ricerca.
                if (began) WakeTheDevicesThatStartTheNextStep(mission, origin);
            }
        }

        if (change.Result.Outcome == MissionDestinationOutcome.Pending)
            Navigator.WriteLog(
                $"Missione {missionId}: il cambio di destinazione in {itemId} aspetta la fine dello step: {change.Reason}",
                LogEntryType.Information);
        else if (change.Result.Outcome == MissionDestinationOutcome.Changed)
            Navigator.WriteLog(
                change.Reason == null
                    ? $"Missione {missionId}: destinazione cambiata dall'operatore in {mission.DestinationItemId}, gateway {mission.Gateway}, rotta di {mission.MissionSteps.Count} passi"
                    : $"Missione {missionId}: destinazione cambiata dall'operatore in {mission.DestinationItemId}, gateway {mission.Gateway}, rotta di {mission.MissionSteps.Count} passi: {change.Reason}",
                LogEntryType.Information);
        else
            Navigator.WriteLog(
                change.Reason == null
                    ? $"Missione {missionId}: destinazione non cambiata in {itemId} ({change.Result.Outcome})"
                    : $"Missione {missionId}: destinazione non cambiata in {itemId} ({change.Result.Outcome}): {change.Reason}",
                change.ReasonLevel);

        return change.Result;
    }

    /// <summary>
    /// The change waits for the end of the mission's step (item 54, decision 4): it becomes the mission's
    /// pending change (<see cref="PendingMissionChanges.Defer"/>), and the answer says so.
    /// </summary>
    private static DestinationChange Defer(PathMission mission, PendingMissionChange pending, MissionStep? inFlight,
        PendingMissionChanges.Entry? applying, string reason)
    {
        // Item 54, fix round 3 of task 4: the mission waits for a field rejection postponed for the step
        // in flight, that the route loop redoes when the step has ended. The rejection wins, and the
        // operator's change is refused at once, as before item 54 (the commit refused it with this same
        // reason): kept as a pending change it would meet the rejection when the step ends, and be tried
        // again for ever with the engine stopped, or be applied on top of the rejection route in Run.
        if (applying == null && mission.AwaitsFieldRejection)
            return DestinationChange.Refused(MissionDestinationOutcome.StepInFlight,
                $"la missione aspetta il rigetto dal campo, rimandato per uno step che era in volo, e il giro delle rotte lo rifa' quando lo step e' finito ({reason})",
                LogEntryType.Warning);

        PendingMissionChanges.Defer(mission, pending, inFlight, applying);
        return new(new(MissionDestinationOutcome.Pending), reason);
    }

    /// <summary>
    /// The move waits for the end of the mission's step in flight (item 54, decision 4): it becomes the
    /// mission's pending change, and the answer says so.
    /// </summary>
    private static MissionMoveOutcome MoveAfterTheStep(PathMission mission, Guid itemId, MissionStep? inFlight,
        PendingMissionChanges.Entry? applying)
    {
        PendingMissionChanges.Defer(mission, PendingMissionChange.Position(itemId), inFlight, applying);
        Navigator.WriteLog(
            $"Missione {mission.MissionId}: lo spostamento su {itemId} aspetta la fine dello step {inFlight?.StepId} in volo su {inFlight?.FromNodeId}",
            LogEntryType.Information);
        return MissionMoveOutcome.Pending;
    }

    /// <summary>
    /// After a pending change was dropped without being written (its outcome was a refusal, or there was
    /// nothing to do), the devices that can start the mission's next step are woken: while it waited
    /// they gave up their starts (<c>PathMission.TryBeginStepStart</c>), and nobody repeats them.
    /// </summary>
    internal static void WakeAfterAPendingChange(PathMission mission) =>
        WakeTheDevicesThatStartTheNextStep(mission, mission.PositionItemId);

    /// <summary>
    /// The change that waits for the end of the mission's step, if there is one (item 54, decision 4):
    /// the Editor shows it on the mission's row while it exists. Only in memory: a restart loses it
    /// (owner's answer 4 of 13/10).
    /// </summary>
    public static PendingMissionChange? PendingChangeOf(Guid missionId) => PendingMissionChanges.Of(missionId);

    /// <summary>
    /// A cambio finito, comunque sia andato, si svegliano i dispositivi che possono avviare il
    /// prossimo step della missione: la posizione (il movimento singolo, il deposito) e la
    /// destinazione degli step che partono da li' (il <c>GetFrom</c> e il prelievo li avvia chi
    /// riceve). Durante il cambio i loro avvii sono stati rinunciati (<c>PathMission.TryBeginStepStart</c>)
    /// e nessuno li ripeterebbe; e una rotta costruita qui, in Run, non la vede nessun altro, come
    /// dopo la costruzione di una rotta nel giro del navigatore (<c>StartCellSearch</c>). I cicli dei
    /// gruppi riprovano da se'. <c>ExecuteStep</c> esce da solo se il motore non e' in Run.
    /// </summary>
    private static void WakeTheDevicesThatStartTheNextStep(PathMission mission, Guid origin)
    {
        WakingTheDevicesForTesting?.Invoke(mission);

        var position = PositionOf(mission, origin);
        if (position == null) return;

        var devices = new List<AutomationDevice> { position };
        foreach (var step in mission.MissionSteps.Where(step => step.FromNodeId == position.ItemGuid))
            if (step.DestinationItem is { } next && !devices.Contains(next))
                devices.Add(next);

        foreach (var device in devices)
            Wake(device);
    }

    /// <summary>
    /// Probe for the tests: <see cref="WakeTheDevicesThatStartTheNextStep"/> calls it before waking the
    /// devices, so a test can see, without the field and in Stop (where <c>ExecuteStep</c> exits at once),
    /// whether a step of the mission could start at that moment (item 54, fix round 2 of task 4). Always
    /// null in production.
    /// </summary>
    internal static Action<PathMission>? WakingTheDevicesForTesting { get; set; }

    /// <summary>
    /// Sveglia un dispositivo che avvia step da se' (<c>AutoExecuteStep</c>); un magazzino non avvia
    /// niente, lo fa il suo ricollocatore. <c>ExecuteStep</c> esce da solo se il motore non e' in Run.
    /// </summary>
    private static void Wake(AutomationDevice? device)
    {
        if (device is { AutoExecuteStep: true } && device.DeviceType != DeviceTypes.Storage)
            _ = device.ExecuteStep();
    }

    /// <summary>
    /// Il dispositivo su cui la missione stava quando il cambio e' cominciato; se non c'e' piu' fra i
    /// dispositivi, quello che la missione ha come sorgente.
    /// </summary>
    private static AutomationDevice? PositionOf(PathMission mission, Guid origin) =>
        PathDevices.Instance[origin] ?? mission.SourceItem;

    /// <summary>
    /// Come e' andato un cambio: l'esito e, per un rifiuto, il perche' che va nella riga di
    /// registro, con il suo livello (un avviso per le cause che non sono un magazzino pieno ne' un
    /// percorso che manca: configurazione, dati dell'UDC, la cella proposta fuori dal magazzino). Per
    /// un cambio riuscito che tiene la cella (<see cref="KeepTheCellAndRouteAgain"/>), la nota che lo dice.
    /// </summary>
    private sealed record DestinationChange(MissionDestinationResult Result, string? Reason = null,
        LogEntryType ReasonLevel = LogEntryType.Information)
    {
        public static DestinationChange Refused(MissionDestinationOutcome outcome, string reason,
            LogEntryType level = LogEntryType.Information) => new(new(outcome), reason, level);
    }

    /// <summary>Il cambio verso un dispositivo che non e' un magazzino: la sonda, e il commit.</summary>
    /// <param name="copies">
    /// Le copie della porta della missione (<see cref="CopiesOfTheDoor"/>): lasciando il magazzino la
    /// missione lascia la cella, e le copie la porta, nel commit (<see cref="Commit"/>), solo se passa.
    /// </param>
    /// <param name="giveBack">
    /// The cell of a redirected change of cell or defragmentation (<see cref="CellToGiveBackAsync"/>, item
    /// 54): the commit puts it in the durable queue of the cells to give back, with the mission's row.
    /// </param>
    /// <param name="relocatorArrival">
    /// Towards a relocator, what it does with the load unit (item 54, decision 5): the commit writes it on
    /// the mission with the destination. <see cref="RelocatorArrival.None"/> for every other change.
    /// </param>
    private static async Task<DestinationChange> ChangeToDeviceAsync(PathMission mission, Guid origin,
        AutomationDevice destination, Guid gateway, CopiesOfTheDoor copies, PathMission.CellGiveBack? giveBack,
        RelocatorArrival relocatorArrival = RelocatorArrival.None)
    {
        var steps = mission.ProbeRouteSteps(destination.ItemGuid, gateway);
        if (steps.Count == 0) return new(NoRoute(mission, destination.ItemGuid, gateway));

        // Il codice porta di un polmone si legge adesso, prima del commit: e' la lettura che il setter
        // della destinazione fa da se', in modo sincrono, e solo se la destinazione di prima c'era.
        var bufferDoorCode = destination.DeviceType is DeviceTypes.Buffer or DeviceTypes.LoadingAndBuffer
            ? await ORM.DbInfo.GetDoorCode(destination.ItemGuid)
            : null;

        if (copies.Stop() is { } notStopped) return notStopped;

        var commit = Commit(mission, origin, destination.ItemGuid, steps, gateway, cell: null, bufferDoorCode,
            operatorChoice: true, changeOfCell: false, copies, giveBack, relocatorArrival);
        await WaitForTheCellGiveBackAsync(mission);

        if (commit is { Change.Result.Outcome: MissionDestinationOutcome.Changed, LeftAStorage: true } &&
            Navigator.Warehouse is { } warehouse)
            await GiveBackReservationAsync(warehouse, mission, "la destinazione non e' piu' un magazzino");

        return commit.Change;
    }

    /// <summary>
    /// Il cambio verso un magazzino: il flusso del magazzino (<see cref="ReserveACellAndCommitAsync"/>),
    /// e la cella persa se il cambio e' rifiutato dopo che la prenotazione ha lasciato quella che la
    /// missione teneva.
    /// </summary>
    /// <param name="chosen">Il dispositivo scelto: il magazzino, o un gruppo di magazzini.</param>
    /// <param name="storages">I magazzini che la scelta indica (<see cref="StoragesOf"/>), almeno uno.</param>
    /// <param name="gatewayChanged">L'operatore ha cambiato il gateway (<see cref="ChangeDestinationAsync"/>).</param>
    /// <remarks>
    /// <para>
    /// La prenotazione lascia quella che la missione tiene (<c>ReserveOrKeepStorageAsync</c> con
    /// <c>keepExisting</c> falso: il contratto ne tiene una per missione). Se dopo il cambio e'
    /// rifiutato (risposta persa, porta prenotata fuori dal magazzino scelto o senza percorso,
    /// indirizzo non letto, commit rifiutato, errore imprevisto), la missione andrebbe verso una cella
    /// che il gestionale non le tiene piu'. La specifica (sezione «La cella persa», decisioni del
    /// proprietario dell'1/10): la missione lascia porta, indirizzo e rotta e torna in coda al
    /// navigatore (<see cref="LoseTheCell"/>), che le prenota una cella nel magazzino della
    /// destinazione con questo stesso flusso (<see cref="KeepTheStorageCellAsync"/>), qualunque sia la
    /// destinazione: scelta prima dall'operatore, o dalla configurazione (il caso piu' comune, il
    /// primo cambio di una missione mandata in magazzino dal dispositivo d'ingresso). Il giro della
    /// configurazione del dispositivo di posizione non la rifa': con la modalita' <c>No</c> e senza
    /// destinazione fissa farebbe la rotta verso il magazzino nudo, senza niente prenotato.
    /// </para>
    /// <para>
    /// Si lascia solo la cella che la missione teneva davvero con la sua prenotazione (letta prima di
    /// prenotare): una porta avuta per un'altra via - il ritorno nella cella da cui l'UDC e' uscita,
    /// che il rilascio non tocca - resta della missione.
    /// </para>
    /// <para>
    /// <b>Il cambio cella di un'estrazione</b> con l'UDC ancora nella sua cella
    /// (<c>PathMission.LoadUnitIsInItsStorageCell</c>; risposte 1 e 2 del proprietario del 2/10): prima
    /// di prenotare il gestionale sa che la missione lascia l'assegnazione e che l'UDC esce dalla coda
    /// di estrazione (<see cref="LeaveTheExtractionAsync"/>), e al commit la missione diventa un cambio
    /// cella senza estrazione ne' assegnazione. Se dopo quelle segnalazioni il cambio e' rifiutato lo
    /// stesso - il gestionale che non risponde a una segnalazione dopo che almeno una puo' essergli
    /// arrivata (risposta dell'8/10 al punto 2), nessuna cella alla prenotazione, nessun percorso verso
    /// la porta prenotata, il commit rifiutato, un errore imprevisto - la missione non puo' tornare
    /// l'estrazione che era: per il gestionale non lo e' piu'. Si chiude (<see cref="CloseTheMissionAsync"/>):
    /// l'UDC resta nella sua cella senza ordine, e l'esito lo dice
    /// (<see cref="MissionDestinationResult.MissionClosed"/>; risposta del proprietario del 2/10 sulla
    /// domanda del compito 4), che l'host porta alla pagina.
    /// L'errore imprevisto chiude la missione allo stesso modo ma risale, come ogni eccezione del
    /// cambio, e non ha un esito che lo dica: l'host risponde 500, e la pagina, che non sa com'e'
    /// andata, rilegge l'elenco. Se la chiusura non riesce la missione resta ritirata e attiva, con
    /// una riga di avviso, e non perde una cella che come estrazione non teneva.
    /// </para>
    /// <para>
    /// <b>Cambiare solo il gateway</b> (decisione 6 del proprietario del 2/10, e la scelta tecnica
    /// che ne viene): se la porta della missione sta in uno dei magazzini scelti e il gateway cambia,
    /// la cella resta e si rifa' solo la rotta, senza il gestionale (<see cref="KeepTheCellAndRouteAgain"/>).
    /// Vale con o senza il segno. Senza porta (la cella persa) non c'e' una cella da tenere, e un
    /// cambio cella di un'estrazione non tiene la cella da cui l'UDC esce: tutti e due fanno il
    /// flusso intero.
    /// </para>
    /// </remarks>
    /// <param name="giveBack">
    /// The cell of a redirected change of cell or defragmentation (<see cref="CellToGiveBackAsync"/>, item
    /// 54): the commit with the new cell puts it in the durable queue of the cells to give back.
    /// </param>
    /// <param name="preciseDoor">
    /// The door A code of the precise cell the operator chose (item 54, decision 6), in the storage
    /// <paramref name="chosen"/>: the warehouse reserves that cell instead of choosing one; <c>null</c> for
    /// the change of every day.
    /// </param>
    private static async Task<DestinationChange> ChangeToStorageAsync(PathMission mission, Guid origin,
        AutomationDevice chosen, IReadOnlyList<AutomationDevice> storages, Guid gateway, bool gatewayChanged,
        CopiesOfTheDoor copies, PathMission.CellGiveBack? giveBack, string? preciseDoor = null)
    {
        var changeOfCell = mission.MissionType == MissionTypes.Extraction && mission.LoadUnitIsInItsStorageCell();

        // La guardia sul cambio cella e' difensiva: un'estrazione ancora nella sua cella non ha una porta
        // di deposito in un magazzino (il motore non ne scrive), quindi oggi non ci arriva; se ce
        // l'avesse, non e' una cella da tenere.
        // A precise cell keeps the cell only if it is the one the mission has (item 54).
        if (gatewayChanged && !changeOfCell &&
            (preciseDoor == null || string.Equals(preciseDoor, mission.StorageDoorCode, StringComparison.Ordinal)) &&
            await DoorInTheChosenStoragesAsync(mission, storages) is { } door)
            return await KeepTheCellAndRouteAgain(mission, origin, door, gateway);

        // Le copie della porta si fermano prima di chiedere al gestionale, e la lasciano quando la
        // prenotazione lascia la cella della missione (ReserveACellAndCommitAsync): un rifiuto senza
        // prenotare tiene la cella, e loro la porta.
        if (copies.Stop() is { } notStopped) return notStopped;

        var excluded = await CellToReplaceAsync(mission, chosen);

        return await ReserveOrLetGoAsync(mission, origin, chosen, storages, gateway, excluded,
            new ReservationProgress { Copies = copies }, changeOfCell, giveBack, preciseDoor);
    }

    /// <summary>
    /// Il flusso del magazzino per la missione dell'operatore (<see cref="ReserveACellAndCommitAsync"/>,
    /// con il segno), e quello che un rifiuto lascia: il cambio dell'operatore (<see cref="ChangeToStorageAsync"/>)
    /// e la cella rifiutata dal PLC (<see cref="AnotherCellInTheChosenStorageAsync"/>). Le due vie lo
    /// scrivevano due volte, a mano (revisione finale del primo giro della voce 39, C57).
    /// </summary>
    /// <remarks>
    /// Dopo le segnalazioni del cambio cella la missione non e' piu' l'estrazione che era: un rifiuto la
    /// chiude, e se la chiusura non riesce resta ritirata (<see cref="CloseTheMissionAsync"/> lo scrive),
    /// non perde una cella che come estrazione non teneva. Ci arriva solo il cambio cella
    /// (<paramref name="changeOfCell"/>). Altrimenti, se la prenotazione aveva gia' lasciato la cella che
    /// la missione teneva, la missione la perde (<see cref="LoseTheCell"/>), e il rifiuto lo dice. Lo
    /// stesso per l'errore imprevisto, che poi risale.
    /// </remarks>
    private static async Task<DestinationChange> ReserveOrLetGoAsync(PathMission mission, Guid origin,
        AutomationDevice chosen, IReadOnlyList<AutomationDevice> storages, Guid gateway, IReadOnlyList<decimal> excluded,
        ReservationProgress progress, bool changeOfCell, PathMission.CellGiveBack? giveBack = null,
        string? preciseDoor = null)
    {
        try
        {
            var change = await ReserveACellAndCommitAsync(mission, origin, chosen, storages, gateway, excluded, progress,
                operatorChoice: true, changeOfCell, giveBack, preciseDoor);

            if (change.Result.Outcome != MissionDestinationOutcome.Changed && progress.LeftTheExtraction)
                return await CloseTheMissionAsync(mission, change.Result.Outcome.ToString())
                    ? change with { Result = change.Result with { MissionClosed = true } }
                    : change;

            if (change.Result.Outcome != MissionDestinationOutcome.Changed && progress.LeftACell &&
                LoseTheCell(mission, change.Result.Outcome.ToString(), progress.ReplacedCellToGiveBack))
            {
                await WaitForTheCellGiveBackAsync(mission);
                return change with { Result = change.Result with { LostTheCell = true } };
            }

            return change;
        }
        catch
        {
            if (progress.LeftTheExtraction) await CloseTheMissionAsync(mission, "errore imprevisto");
            else if (progress.LeftACell &&
                     LoseTheCell(mission, "errore imprevisto", progress.ReplacedCellToGiveBack))
                await WaitForTheCellGiveBackAsync(mission);
            throw;
        }
    }

    /// <summary>
    /// Il cambio che tiene la cella: l'operatore sceglie di nuovo il magazzino della cella che la
    /// missione ha cambiando solo il gateway (decisione 6 del proprietario del 2/10). Si sonda la
    /// rotta verso la porta attuale con il gateway nuovo e si fa il commit con la cella che la
    /// missione ha (<c>PathMission.TheCellItHas</c>): niente gestionale, nessuna prenotazione toccata.
    /// </summary>
    /// <param name="door">Il dispositivo della porta della missione, in uno dei magazzini scelti.</param>
    /// <remarks>
    /// Senza percorso verso la porta con il gateway nuovo il rifiuto e' quello di sempre, con la
    /// seconda sonda senza il gateway (<see cref="NoRoute"/>), e la cella resta. Il commit consuma
    /// la conferma della cella per il deposito, come ogni commit dell'operatore: una missione con il
    /// segno aspetta che il navigatore la confermi di nuovo (<c>PathMission.MayStartTheDeposit</c>).
    /// </remarks>
    private static async Task<DestinationChange> KeepTheCellAndRouteAgain(PathMission mission, Guid origin,
        AutomationDevice door, Guid gateway)
    {
        var steps = mission.ProbeRouteSteps(door.ItemGuid, gateway);
        if (steps.Count == 0)
            return new(NoRoute(mission, door.ItemGuid, gateway),
                $"nessun percorso verso la porta {mission.StorageDoorCode} che la missione ha, con il gateway nuovo");

        var change = (await CommitAfterThePickUpHandOffAsync(mission, changeOfCell: false,
            () => Commit(mission, origin, door.ItemGuid, steps, gateway, PathMission.TheCellItHas, bufferDoorCode: null,
                operatorChoice: true, changeOfCell: false))).Change;

        // Come in ReserveACellAndCommitAsync: se il commit ha messo il fatto della voce 45, la sua
        // scrittura si aspetta qui, e un errore lo dice una riga (revisione finale della voce 45).
        await WaitForThePickedExtractionReportAsync(mission);

        // La riga del cambio lo dice: la cella e' la stessa, e cambia solo la rotta (revisione finale del
        // primo giro della voce 39, C17). Prima diceva solo destinazione, gateway e passi, come per una
        // cella nuova.
        return change.Result.Outcome == MissionDestinationOutcome.Changed
            ? change with { Reason = $"la missione tiene la cella della porta {mission.StorageDoorCode}, e si rifa' solo la rotta" }
            : change;
    }

    /// <summary>
    /// Fin dove e' arrivato il flusso del magazzino (<see cref="ReserveACellAndCommitAsync"/>), per
    /// chi deve sapere se la missione ha perso la sua cella, o se il gestionale ha gia' saputo che
    /// l'estrazione lascia il suo ordine, anche quando il flusso finisce con un'eccezione.
    /// </summary>
    private sealed class ReservationProgress
    {
        /// <summary>La missione teneva con la sua prenotazione la cella della sua porta, letta prima di prenotare.</summary>
        public bool HeldItsCell { get; set; }

        /// <summary>La prenotazione e' partita: da qui la cella che la missione teneva non e' piu' sua.</summary>
        public bool Reserving { get; set; }

        /// <summary>La missione teneva una cella, e la prenotazione l'ha lasciata.</summary>
        public bool LeftTheHeldCell => Reserving && HeldItsCell;

        /// <summary>
        /// La cella senza legame della missione reindirizzata: una prenotazione riuscita, o la cui
        /// risposta puo' essersi persa, l'ha sostituita per la stessa UDC.
        /// </summary>
        public PathMission.CellGiveBack? ReplacedCellToGiveBack { get; set; }

        /// <summary>La prenotazione ha lasciato una cella legata alla missione o all'UDC.</summary>
        public bool LeftACell => LeftTheHeldCell || ReplacedCellToGiveBack != null;

        /// <summary>
        /// Il cambio cella di un'estrazione: il gestionale puo' aver saputo, anche solo in parte, che la
        /// missione lascia l'assegnazione e che l'UDC esce dalla coda di estrazione
        /// (<see cref="LeaveTheExtractionAsync"/>). Da qui la missione non e' piu' l'estrazione che era.
        /// </summary>
        public bool LeftTheExtraction { get; set; }

        /// <summary>
        /// Le copie della porta della missione, ferme per il cambio dell'operatore (<see cref="CopiesOfTheDoor"/>):
        /// lasciano la porta quando parte la prenotazione, che lascia la cella della missione. Nessuna per
        /// il navigatore, che le ha gia' lasciate, e per la cella rifiutata dal PLC.
        /// </summary>
        public CopiesOfTheDoor? Copies { get; init; }
    }

    /// <summary>
    /// Il flusso del magazzino, lo stesso per il cambio dell'operatore (<see cref="ChangeToStorageAsync"/>),
    /// per il navigatore che rifa' la cella persa nel magazzino della destinazione
    /// (<see cref="KeepTheStorageCellAsync"/>) e per la cella che il PLC ha rifiutato a una missione
    /// dell'operatore (<see cref="AnotherCellInTheChosenStorageAsync"/>). Non e' l'unica via che prenota
    /// per il navigatore: la cella persa di una missione della configurazione, con il magazzino pieno,
    /// si cerca negli altri magazzini del ricollocatore con la ricerca del deposito ancorata a lui
    /// (<see cref="AnotherStorageOrTheRejectionAsync"/>, <c>PathMission.SearchTheStoreAsync</c>), che
    /// guarda porta e rotta per conto suo prima di scrivere. Qui:
    /// la rotta verso il magazzino, la domanda al gestionale senza prenotare e la cella che propone
    /// (nel magazzino, con un percorso verso la sua porta e il suo indirizzo di campo), la
    /// prenotazione, la porta prenotata riguardata se e' un'altra, e il commit.
    /// </summary>
    /// <param name="chosen">Il magazzino in cui cercare la cella, o un gruppo di magazzini.</param>
    /// <param name="storages">I magazzini che <paramref name="chosen"/> indica (<see cref="StoragesOf"/>), almeno uno.</param>
    /// <param name="gateway">Il gateway con cui sondare le rotte, e da scrivere al commit.</param>
    /// <param name="excluded">
    /// Le celle da non proporre: quella che la missione ha, quando l'operatore ne chiede un'altra o
    /// quando il PLC l'ha rifiutata.
    /// </param>
    /// <param name="progress">
    /// Fin dove si e' arrivati: la cella che la missione teneva, se la prenotazione e' partita, e se il
    /// gestionale ha gia' saputo che un'estrazione lascia il suo ordine (il cambio cella).
    /// </param>
    /// <param name="operatorChoice">
    /// Vero per il cambio dell'operatore, che scrive la destinazione con il segno, e per la cella
    /// rifiutata dal PLC, che il segno ce l'ha; per il navigatore che rifa' la cella persa, il segno che
    /// la missione ha (<c>PathMission.CommitRoute</c>).
    /// </param>
    /// <param name="changeOfCell">
    /// Vero per il cambio dell'operatore di un'estrazione con l'UDC ancora nella sua cella
    /// (<see cref="ChangeToStorageAsync"/>): prima di prenotare il gestionale sa che la missione lascia
    /// estrazione e assegnazione (<see cref="LeaveTheExtractionAsync"/>, che lo segna in
    /// <paramref name="progress"/>), e il commit ne fa un cambio cella. Falso per il navigatore.
    /// </param>
    /// <remarks>
    /// <para>
    /// Tutto quello che si puo' sapere prima di prenotare si guarda prima (primo e secondo giro di
    /// correzioni, decisioni del proprietario dell'1/10): la porta proposta e il suo magazzino, il
    /// percorso verso di lei, il suo indirizzo di campo, e che la missione sia ancora attiva, ancora
    /// li' e senza step in volo. Un rifiuto fin qui non tocca la cella che la missione tiene, perche'
    /// la domanda e' senza prenotare. Dopo la prenotazione restano la risposta persa, una porta
    /// prenotata diversa da quella proposta (il gestionale risponde di nuovo, e fra le due risposte
    /// la cella puo' essere stata presa da un altro), e il commit. Ognuno di questi rifiuti
    /// restituisce la cella presa, anche quello di un'eccezione imprevista, che poi risale.
    /// </para>
    /// <para>
    /// Le segnalazioni del cambio cella stanno fra le due cose: dopo tutto quello che si guarda prima
    /// di prenotare, e prima della prenotazione. Prima no, perche' un rifiuto senza prenotare non deve
    /// lasciare un'estrazione che il gestionale non ha piu'; dopo nemmeno, perche' «l'UDC esce dalla
    /// coda di estrazione» toglie per contratto ogni posizione prenotata dell'UDC, e cancellerebbe la
    /// cella appena presa (risposta del proprietario del 2/10 sulla domanda del compito 4).
    /// </para>
    /// <para>
    /// Fuori da ogni lucchetto, senza il gettone di una richiesta HTTP, e con la missione ferma: chi
    /// chiama tiene il posto delle ricerche della missione e il suo cambio cominciato
    /// (<c>PathMission.TryBeginOperatorChange</c>), o, per la cella rifiutata dal PLC, un avvio contato
    /// sulla missione (<c>PathMission.TryBeginStepStart</c>), che il cambio dell'operatore lo tiene fuori
    /// allo stesso modo.
    /// </para>
    /// </remarks>
    /// <param name="giveBack">
    /// The cell of a change of cell or a defragmentation that the operator redirects (item 54,
    /// <see cref="CellToGiveBackAsync"/>): the commit queues it to be given back. Null for every other caller.
    /// </param>
    private static async Task<DestinationChange> ReserveACellAndCommitAsync(PathMission mission, Guid origin,
        AutomationDevice chosen, IReadOnlyList<AutomationDevice> storages, Guid gateway,
        IReadOnlyList<decimal> excluded, ReservationProgress progress, bool operatorChoice, bool changeOfCell,
        PathMission.CellGiveBack? giveBack = null, string? preciseDoor = null)
    {
        if (mission.ProbeRouteSteps(chosen.ItemGuid, gateway).Count == 0)
            return new(NoRoute(mission, chosen.ItemGuid, gateway));

        // Senza l'identificativo del gestionale non c'e' niente da prenotare: e' lo stesso no del
        // flusso del deposito normale (GetStoreToWarehouse), detto senza chiamarlo.
        if (mission.LuId == 0)
            return DestinationChange.Refused(MissionDestinationOutcome.NoStorageCell,
                "l'UDC non ha l'identificativo del gestionale (LuId 0): non c'e' niente da prenotare",
                LogEntryType.Warning);

        if (Navigator.Warehouse is not { } warehouse)
            return DestinationChange.Refused(MissionDestinationOutcome.WarehouseUnavailable,
                "il gestionale non e' configurato", LogEntryType.Warning);

        // Item 54, decision 6: with a precise cell there is no question to ask - the operator chose the
        // cell, and the warehouse checks it when it reserves it, below.
        ReserveStorageRequest? request = null;
        string proposedDoor;
        if (preciseDoor != null)
        {
            if (StillWhereTheChangeStarted(mission, origin) is { } movedBeforeTheCell) return movedBeforeTheCell;
            proposedDoor = preciseDoor;
        }
        else
        {
            StorageReservation available;
            try
            {
                // Il magazzino del gestionale si legge da una cella del magazzino scelto: la prima porta
                // mappata, del primo che ne ha una.
                decimal cellId = 0;
                foreach (var storage in storages)
                {
                    cellId = await ORM.DbInfo.GetCellId(await ORM.DbInfo.GetDoorCode(storage.ItemGuid));
                    if (cellId != 0) break;
                }

                if (cellId == 0)
                    return DestinationChange.Refused(MissionDestinationOutcome.NoStorageCell,
                        $"nessuna porta del magazzino scelto {chosen.ItemGuid} e' mappata a una cella nel database del WCS",
                        LogEntryType.Warning);

                var warehouseId = (await mission.DescribeCellAsync(cellId)).WarehouseId;
                if (warehouseId == 0)
                    return DestinationChange.Refused(MissionDestinationOutcome.NoStorageCell,
                        $"la cella {cellId} del magazzino scelto non ha un magazzino del gestionale",
                        LogEntryType.Warning);

                // I dispositivi raggiungibili sono i ricollocatori dei soli magazzini scelti, non di tutti
                // come nella ricerca di una cella qualunque (SetParamsForStoreInAnyAvailableCell): la cella
                // deve stare nel magazzino scelto. Un magazzino senza ricollocatore si indica da se', perche'
                // una lista vuota non restringerebbe niente.
                request = new ReserveStorageRequest(
                    mission.MissionId,
                    mission.LuId,
                    warehouseId,
                    await Persistence.LocalQueries.GetAutomaticAreasByWarehouseId(warehouseId),
                    excluded,
                    [.. storages.Select(storage => storage.RelocateItem?.ItemGuid ?? storage.ItemGuid).Distinct()],
                    StorageAreaSelectionMode.All,
                    StorageCellSearchMode.FreeAutomaticOperator,
                    CellFullnessPreference.Fullest,
                    StorageSearchOperatorType.Automatic,
                    AssignPosition: false,
                    VerifyPosition: false,
                    Navigator.WorkstationId,
                    Navigator.UserId);

                available = await warehouse.ReserveStorageAsync(request);
            }
            catch (WarehouseUnavailableException ex)
            {
                return DestinationChange.Refused(MissionDestinationOutcome.WarehouseUnavailable,
                    $"il gestionale non risponde per il cambio verso {chosen.ItemGuid}: {ex.Message}", LogEntryType.Warning);
            }

            // Le guardie del commit, guardate anche adesso e di nuovo prima di prenotare: una missione
            // tolta, spostata o con uno step partito mentre il gestionale rispondeva si rifiuta senza
            // lasciare la cella che ha. Il commit le riguarda, perche' anche la prenotazione si fa
            // attendere.
            if (StillWhereTheChangeStarted(mission, origin) is { } moved) return moved;

            // La domanda senza prenotare, e la cella che propone si guarda subito: nessuna cella, una
            // cella fuori dal magazzino scelto, senza percorso verso la sua porta o senza indirizzo, e il
            // rifiuto non tocca la cella che la missione ha.
            var question = excluded.Count == 0
                ? "la domanda senza prenotare"
                : $"la domanda senza prenotare, esclusa la cella {excluded[0]} che la missione ha";
            if (NoCell(mission, available, question, anotherCell: excluded.Count > 0) is { } noCell) return noCell;
            proposedDoor = available.DoorCode;
        }

        var (door, steps, notThisDoor) = await LookAtTheDoorAsync(mission, proposedDoor, chosen, gateway,
            preciseDoor == null ? "la porta proposta" : "la cella scelta dall'operatore");
        if (notThisDoor != null) return notThisDoor;

        Wcs.Connection.Interface.DataAsi? address;
        try
        {
            // La cella che la missione tiene con la sua prenotazione, se ne tiene una: la prenotazione
            // qui sotto la lascia, e se poi il cambio e' rifiutato la missione non deve piu' andarci.
            // Fino alla prenotazione resta sua, anche se un rifiuto arriva dopo questa riga.
            progress.HeldItsCell = await HoldsItsCellAsync(warehouse, mission);

            // L'indirizzo di campo viene dal gestionale, come nel flusso del deposito: letto per la
            // cella proposta, un guasto rifiuta prima di prenotare. Se la porta prenotata e' la
            // stessa, e' gia' quello giusto.
            address = await mission.GetAsiCellInfoAsync(proposedDoor);
        }
        catch (WarehouseUnavailableException ex)
        {
            return DestinationChange.Refused(MissionDestinationOutcome.WarehouseUnavailable,
                $"il gestionale non risponde sulla cella che la missione tiene o su quella proposta {proposedDoor}: {ex.Message}",
                LogEntryType.Warning);
        }

        // Il cambio cella di un'estrazione: il gestionale lo sa adesso, prima della prenotazione (vedi
        // le note). Se la prima non puo' essere arrivata, il cambio e' rifiutato e la missione resta
        // l'estrazione che era; se ne e' arrivata almeno una e le altre no, il rifiuto la chiude
        // (risposta dell'8/10 al punto 2). Le segnalazioni non si ritirano: prima, le guardie del
        // commit si rifanno dopo le domande qui sopra (la cella tenuta, l'indirizzo), perche' una
        // missione tolta, spostata o partita intanto non lasci l'ordine per un cambio che non avverra'
        // (revisione finale della voce 39).
        if (changeOfCell && StillWhereTheChangeStarted(mission, origin) is { } beforeTheReports)
            return beforeTheReports;

        if (changeOfCell && await LeaveTheExtractionAsync(warehouse, mission, progress) is { } unreported)
            return unreported;

        // Item 54, fix round 1 of task 3: the claim of the load unit, from before the reservation until
        // the commit has written and the rows of the reserved cell have left the queue of the cells to
        // give back (LoadUnitCellClaims). Without it the dispatcher could give back by load unit and cell
        // the cell just reserved here, before the commit writes its door. Taken before the last look,
        // because waiting for it may take a release by the warehouse.
        using var giveBackClaim = await LoadUnitCellClaims.EnterAsync(mission.LuId);

        if (StillWhereTheChangeStarted(mission, origin) is { } movedMeanwhile) return movedMeanwhile;

        // Da qui il gestionale puo' tenere una cella per la missione, e quella che teneva prima non e'
        // piu' sua: ogni rifiuto restituisce la cella presa, anche quello di un'eccezione imprevista.
        // Le copie della sua porta la lasciano adesso, prima che la missione scriva la sua riga con la
        // cella nuova o senza cella (punto 3 dell'8/10 sera). Anche se la missione la cella non la teneva
        // piu' (HeldItsCell falso): allora non e' sua gia' adesso, e le copie la lasciano lo stesso, anche
        // se un rifiuto lascia poi la porta alla missione (LoseTheCell non gira), che il navigatore rifa'
        // con la cella persa. Lasciarla dopo il commit invertirebbe l'ordine delle due righe, e una
        // ricarica fra le due riporterebbe la copia sulla porta vecchia senza nessuna missione che la tiene.
        //
        // Item 54, fix round 1 of task 6: the precise cell lets go of the cell the mission holds only when
        // the warehouse has reserved the new one, or may have (an answer lost, an unexpected error). Its
        // reservation replaces the one the load unit had (IWarehouse.ReserveStorageCellAsync), and a
        // refusal writes nothing: an occupied cell picked from a list a few moments old, or a code typed
        // by hand, is refused with the reason and the mission keeps its cell, its door and its route.
        // Only the cell the mission holds, chosen again, is let go first, as in ReserveOrKeepStorageAsync:
        // the warehouse would not find it free while the load unit holds it.
        var letGoFirst = request != null ||
                         (progress.HeldItsCell && string.Equals(proposedDoor, mission.StorageDoorCode, StringComparison.Ordinal));
        if (letGoFirst) LetGoOfTheHeldCell(progress);

        StorageReservation reservation;
        try
        {
            if (request != null)
            {
                try
                {
                    reservation = await PathMission.ReserveOrKeepStorageAsync(warehouse,
                        request with { AssignPosition = true }, keepExisting: false);
                }
                catch
                {
                    // La risposta puo' essersi persa dopo che l'assegnazione nuova ha sostituito la
                    // vecchia prenotazione della stessa UDC.
                    progress.ReplacedCellToGiveBack = giveBack;
                    throw;
                }

                if (reservation.Outcome == StorageReservationOutcome.CellFound)
                    progress.ReplacedCellToGiveBack = giveBack;
            }
            else
            {
                // The precise cell (item 54, decision 6): the warehouse reserves that cell or says why not.
                StorageCellReservationResult answer;
                try
                {
                    if (letGoFirst) await warehouse.ReleaseStorageReservationAsync(mission.MissionId);
                    answer = await warehouse.ReserveStorageCellAsync(new ReserveStorageCellRequest(mission.MissionId,
                        mission.LuId, proposedDoor, Navigator.WorkstationId, Navigator.UserId));
                }
                catch
                {
                    // The answer may have been lost after the reservation, which replaced the held one.
                    LetGoOfTheHeldCell(progress);
                    progress.ReplacedCellToGiveBack = giveBack;
                    throw;
                }

                if (answer.Outcome != StorageCellReservationOutcome.Reserved)
                    return CellRefused(answer.Outcome switch
                    {
                        StorageCellReservationOutcome.UnknownDoor => MissionCellRefusal.UnknownDoor,
                        StorageCellReservationOutcome.NotADoorA => MissionCellRefusal.NotADoorA,
                        StorageCellReservationOutcome.UnknownLoadUnit => MissionCellRefusal.UnknownLoadUnit,
                        _ => MissionCellRefusal.CellNotAvailable
                    }, answer.Reason ?? $"il gestionale non prenota la cella della porta {proposedDoor} ({answer.Outcome})");

                // Reserved: the cell the mission held is no longer its own.
                LetGoOfTheHeldCell(progress);
                progress.ReplacedCellToGiveBack = giveBack;

                if (answer.Reservation is not { } reserved)
                {
                    await GiveBackReservationAsync(warehouse, mission, "prenotazione della cella scelta senza la prenotazione nella risposta");
                    return DestinationChange.Refused(MissionDestinationOutcome.WarehouseUnavailable,
                        $"il gestionale ha risposto «prenotata» per la porta {proposedDoor} senza la prenotazione: la cella torna al gestionale",
                        LogEntryType.Warning);
                }

                reservation = reserved;
            }
        }
        catch (WarehouseUnavailableException ex)
        {
            // La risposta puo' essersi persa dopo la scrittura: si restituisce comunque.
            await GiveBackReservationAsync(warehouse, mission, "prenotazione senza risposta");
            return DestinationChange.Refused(MissionDestinationOutcome.WarehouseUnavailable,
                $"il gestionale non risponde alla prenotazione verso {chosen.ItemGuid}: {ex.Message}", LogEntryType.Warning);
        }

        if (NoCell(mission, reservation, "la prenotazione") is { } notReserved) return notReserved;

        try
        {
            if (!string.Equals(reservation.DoorCode, proposedDoor, StringComparison.Ordinal))
            {
                // La porta prenotata non e' quella proposta: si riguarda tutto per lei.
                var (reserved, reservedSteps, notTheReservedDoor) = await LookAtTheDoorAsync(mission,
                    reservation.DoorCode, chosen, gateway, "la porta prenotata");
                if (notTheReservedDoor != null)
                {
                    await GiveBackReservationAsync(warehouse, mission, notTheReservedDoor.Reason ?? "porta prenotata non usabile");
                    return notTheReservedDoor with { Reason = $"{notTheReservedDoor.Reason}: la cella torna al gestionale" };
                }

                steps = reservedSteps;
                address = await mission.GetAsiCellInfoAsync(reservation.DoorCode);
                door = reserved;
            }

            var cell = new PathMission.StorageCellChoice(reservation.DoorCode, address?.Asi, address?.Subitem,
                reservation.Partition, reservation.StorageDepth);

            var commit = await CommitAfterThePickUpHandOffAsync(mission, changeOfCell,
                () => Commit(mission, origin, door.ItemGuid, steps, gateway, cell, bufferDoorCode: null, operatorChoice,
                    changeOfCell, giveBack: giveBack));

            // L'estrazione gia' prelevata diventata un deposito: il fatto per il gestionale e' nella
            // coda del WCS con la riga della missione, e lo manda il dispatcher (voce 45).
            await WaitForThePickedExtractionReportAsync(mission);

            // Item 54: the cell of the redirected change of cell, in the queue with the mission's row.
            await WaitForTheCellGiveBackAsync(mission);

            if (commit.Change.Result.Outcome != MissionDestinationOutcome.Changed)
            {
                await GiveBackReservationAsync(warehouse, mission, $"cambio non avvenuto ({commit.Change.Result.Outcome})");
            }
            else
            {
                // Item 54: the cell is the mission's again, and a row that would give it back leaves the
                // queue, still under the claim of the load unit.
                await DropTheGiveBacksOfTheReservedCellAsync(mission, reservation);

                if (changeOfCell)
                    await TakeTheMarkDownAsync(mission, "il cambio cella non e' stato scritto nel database del WCS");
            }

            return commit.Change;
        }
        catch (WarehouseUnavailableException ex)
        {
            await GiveBackReservationAsync(warehouse, mission, "indirizzo della cella non letto");
            return DestinationChange.Refused(MissionDestinationOutcome.WarehouseUnavailable,
                $"il gestionale non risponde per l'indirizzo della porta {reservation.DoorCode}: {ex.Message}",
                LogEntryType.Warning);
        }
        catch
        {
            await GiveBackReservationAsync(warehouse, mission, "errore durante il cambio");
            throw;
        }
    }

    /// <summary>
    /// La porta di una cella che il gestionale propone o ha prenotato, guardata prima di usarla: il
    /// dispositivo a cui la mappa delle porte del database del WCS la lega, nel magazzino scelto
    /// (<see cref="IsInChosenStorage"/>), e un percorso verso di lui con il gateway scelto. La stessa
    /// guardia per la porta proposta e per quella prenotata, con il perche' del rifiuto che dice
    /// quale delle due e che cosa manca.
    /// </summary>
    /// <param name="which">«la porta proposta» o «la porta prenotata», per la riga di registro.</param>
    /// <returns>Il dispositivo della porta e la rotta verso di lui, oppure il rifiuto.</returns>
    private static async Task<(AutomationDevice Door, List<MissionStep> Steps, DestinationChange? Refusal)> LookAtTheDoorAsync(
        PathMission mission, string doorCode, AutomationDevice chosen, Guid gateway, string which)
    {
        if (PathDevices.Instance[await ORM.DbInfo.GetItemId(doorCode)] is not { } door)
            return (chosen, [], DestinationChange.Refused(MissionDestinationOutcome.NoStorageCell,
                $"{which} {doorCode} non e' mappata a nessun dispositivo nel database del WCS", LogEntryType.Warning));

        if (!IsInChosenStorage(door, chosen))
            return (door, [], DestinationChange.Refused(MissionDestinationOutcome.NoStorageCell,
                $"{which} {doorCode} non e' nel magazzino scelto", LogEntryType.Warning));

        var steps = mission.ProbeRouteSteps(door.ItemGuid, gateway);
        return steps.Count == 0
            ? (door, steps, new DestinationChange(NoRoute(mission, door.ItemGuid, gateway), $"nessun percorso verso {which} {doorCode}"))
            : (door, steps, null);
    }

    /// <summary>
    /// Le guardie del commit (<see cref="Commit"/>) fatte prima di prenotare: la missione e' ancora
    /// attiva, ancora sulla posizione da cui le rotte sono state sondate, e senza step partiti
    /// (<c>PathMission.StepInFlight</c>). <c>null</c> se va tutto bene, altrimenti il rifiuto.
    /// </summary>
    private static DestinationChange? StillWhereTheChangeStarted(PathMission mission, Guid origin)
    {
        if (!ReferenceEquals(ActiveMissions.Instance[mission.MissionId], mission))
            return DestinationChange.Refused(MissionDestinationOutcome.MissionNotFound, "la missione non e' piu' attiva");

        if (mission.PositionItemId != origin)
            return DestinationChange.Refused(MissionDestinationOutcome.StepInFlight,
                $"la missione e' stata spostata su {mission.PositionItemId} mentre il gestionale rispondeva",
                LogEntryType.Warning);

        return mission.StepInFlight() is { } inFlight
            ? DestinationChange.Refused(MissionDestinationOutcome.StepInFlight,
                $"lo step {inFlight.StepId} e' partito mentre il gestionale rispondeva ({inFlight.FromNodeId} -> {inFlight.ToNodeId})",
                LogEntryType.Warning)
            : null;
    }

    /// <summary>
    /// The reservation has let go of the cell the mission held, or may have: the copies of its door leave
    /// it now, before the mission writes its row, and from here every refusal gives back the cell taken
    /// and the mission loses the one it held (<see cref="ReservationProgress.LeftTheHeldCell"/>). Calling
    /// it again does nothing more.
    /// </summary>
    private static void LetGoOfTheHeldCell(ReservationProgress progress)
    {
        progress.Copies?.Leave();
        progress.Reserving = true;
    }

    /// <summary>
    /// La missione tiene presso il gestionale la cella della sua porta? La prenotazione che il
    /// gestionale le lega c'e', ed e' proprio di quella cella. Chiederlo la rinnova
    /// (<c>IWarehouse.GetStorageReservationAsync</c>). Una missione senza porta, o verso un dispositivo
    /// che non e' un magazzino, non tiene niente e non si chiede.
    /// </summary>
    /// <exception cref="WarehouseUnavailableException">Il gestionale non risponde: non si sa.</exception>
    private static async Task<bool> HoldsItsCellAsync(IWarehouse warehouse, PathMission mission)
    {
        var door = mission.StorageDoorCode;
        if (string.IsNullOrEmpty(door) || mission.DestinationItem?.DeviceType != DeviceTypes.Storage) return false;

        var cellId = await ORM.DbInfo.GetCellId(door);

        return await warehouse.GetStorageReservationAsync(mission.MissionId) is
            { Outcome: StorageReservationOutcome.CellFound } held && held.CellId == cellId;
    }

    /// <summary>
    /// La cella della porta della missione e' una di quelle che il PLC le ha rifiutato al deposito
    /// (<c>PathMission.CellsRefusedByThePlc</c>). Una lettura del database del WCS, niente gestionale.
    /// </summary>
    private static async Task<bool> TheDoorIsACellRefusedByThePlcAsync(PathMission mission)
    {
        if (mission.CellsRefusedByThePlc is not { Count: > 0 } refused) return false;

        var cellId = await ORM.DbInfo.GetCellId(mission.StorageDoorCode);
        return cellId != 0 && refused.Contains(cellId);
    }

    /// <summary>
    /// Da <paramref name="position"/> la missione va in un magazzino: lo step che parte da li' e' il
    /// deposito. E' la condizione con cui chi deposita chiede la conferma della cella
    /// (<c>PathMission.MayStartTheDeposit</c>): il dispositivo il cui step da fare va in un magazzino
    /// (<c>AutomationDeviceStorageManager.StartStoreLu</c>), e il gruppo per lo step di un suo membro
    /// verso un magazzino. Chi deposita e' di solito il ricollocatore del magazzino, o un membro del
    /// suo gruppo; puo' essere anche un altro dispositivo collegato al magazzino, se la rotta passa
    /// da lui.
    /// </summary>
    private static bool TheDepositIsTheNextStep(PathMission mission, Guid position) =>
        mission.MissionSteps.Any(step =>
            step.FromNodeId == position && step.DestinationItem is { DeviceType: DeviceTypes.Storage });

    /// <summary>
    /// Le copie della porta di una missione la cui cella non e' piu' sua, o sta per non esserlo (risposta 8
    /// del proprietario dell'8/10, punto 3 dell'8/10 sera): la cella persa che il navigatore rifa', il
    /// cambio dell'operatore, la missione tolta, la porta rifiutata dal PLC. Sono le altre missioni sul suo
    /// stesso gruppo senza una cella prenotata per loro, verso un magazzino, con la stessa porta
    /// (<see cref="IsACopy"/>), anche quelle a cui l'operatore ha cambiato solo il gateway. Sono quelle a cui la
    /// ricerca per tipo del gruppo ha dato la porta della missione che ha prenotato la cella
    /// (<c>AutomationDevice.VerifyWhereAllocateLuGrouped</c>, e la sua gemella
    /// <c>FindNewStoreLocationGrouped</c>), e che depositano con lei. Si fermano tutte come lei
    /// (<c>PathMission.TryBeginOperatorChange</c>), perche' nessun ciclo del gruppo ne avvii il deposito
    /// mentre lasciano la porta.
    /// </summary>
    /// <param name="mission">La missione che ha prenotato la cella, gia' ferma.</param>
    /// <param name="door">La porta della sua cella, che non le resta.</param>
    /// <returns>
    /// Le copie, ferme: a ognuna, alla fine, <c>EndOperatorChange</c>. Nessuna se la missione non ha una
    /// porta o non e' su un gruppo. <c>null</c> se una copia non si puo' fermare (un ciclo del gruppo ne
    /// sta avviando il deposito, o una ricerca o un cambio sono in corso per lei) o, ferma, ha uno step
    /// in volo (<c>PathMission.StepInFlight</c>), e quindi non potrebbe lasciare la porta: prima la
    /// missione lasciava la cella e la copia, con lo step in volo, restava verso la cella persa, e
    /// atterrato lo step ci depositava da sola, perche' nessuna missione con il segno aveva piu' quella
    /// porta e il giro del gruppo non la tratteneva piu' (primo giro di correzioni del compito 12).
    /// Tutte o nessuna: nessuna resta ferma, e chi chiama non tocca niente e riprova al giro dopo, con il
    /// freno. Intanto il deposito della copia non parte da solo: aspetta con la missione, che e' ferma
    /// (<c>AutomationDeviceStepExecutor.BeginStepStarts</c>).
    /// </returns>
    /// <param name="notStopped">
    /// Con <c>null</c>, quale copia non si e' fermata e perche', per la riga di chi chiama: una sola, con
    /// la causa precisa. Prima la scriveva anche questo metodo, e chi chiama ne scriveva un'altra che
    /// diceva sempre «step in volo», anche per un avvio, una ricerca o un cambio in corso.
    /// </param>
    /// <param name="leaveOut">
    /// Le missioni che chi chiama tiene gia' per conto suo, e che non si fermano qui: le missioni rifiutate
    /// dal PLC, contate dalla ricerca del gruppo che le rifa' (<see cref="StopTheCopiesOfTheRefusedDoors"/>).
    /// </param>
    private static List<PathMission>? StopTheCopiesOfTheDoor(PathMission mission, string? door, out string? notStopped,
        IReadOnlyCollection<PathMission>? leaveOut = null)
    {
        notStopped = null;
        var stopped = new List<PathMission>();
        if (string.IsNullOrEmpty(door) || mission.PositionItem?.BelongedGroup is not { } group) return stopped;

        var copies = ActiveMissions.Instance.List.OfType<PathMission>().Where(other =>
            !ReferenceEquals(other, mission) && leaveOut?.Contains(other) != true && IsACopy(other) &&
            string.Equals(other.StorageDoorCode, door, StringComparison.Ordinal) &&
            ReferenceEquals(other.PositionItem?.BelongedGroup, group));

        foreach (var copy in copies)
        {
            var start = copy.TryBeginOperatorChange();
            string? why = null;

            if (start != PathMission.OperatorChangeStart.Begun)
            {
                why = start.ToString();
            }
            else
            {
                stopped.Add(copy);

                // Ferma, nessuno step parte piu'; uno gia' partito si vede adesso
                // (PathMission.StepInFlight), e la copia non potrebbe lasciare la porta
                // (PathMission.DropTheCopiedDoor): si guarda qui, prima che la missione lasci la sua.
                if (copy.StepInFlight() is { } inFlight)
                    why = $"lo step {inFlight.StepId} e' in volo";
                else
                    continue;
            }

            foreach (var held in stopped)
                held.EndOperatorChange();

            notStopped = $"la missione {copy.MissionId}, che ne ha copiato la porta, non si puo' fermare adesso ({why})";
            return null;
        }

        return stopped;
    }

    /// <summary>
    /// La cella della porta <paramref name="door"/> non e' piu' della missione (persa, cambiata
    /// dall'operatore, la missione tolta, rifiutata dal PLC), e le sue copie, ferme
    /// (<see cref="StopTheCopiesOfTheDoor"/>), lasciano la porta vecchia con lei
    /// (<c>PathMission.DropTheCopiedDoor</c>, risposta 8 del proprietario dell'8/10): senza porta, il
    /// ciclo del gruppo le rifa' con la ricerca per tipo, che le puo' rimettere con lei nella cella
    /// nuova o altrove. Prima tenevano la porta vecchia, e quando la missione depositava nella cella
    /// nuova la copia depositava da sola nella vecchia, che nessuna missione teneva piu'.
    /// </summary>
    private static void LeaveTheCopiedDoor(PathMission mission, List<PathMission> copies, string door)
    {
        foreach (var copy in copies)
        {
            if (!string.Equals(copy.StorageDoorCode, door, StringComparison.Ordinal)) continue;

            LeaveTheDoorAsACopy(copy,
                $"Missione {copy.MissionId}: aveva copiato la porta {door} dalla missione {mission.MissionId}, che non ha piu' la cella: porta, indirizzo e rotta tolti",
                "la cella la rifa' la ricerca per tipo del gruppo",
                $"il magazzino {copy.DestinationItemId} l'ha scelto l'operatore: la cella gliela prenota il navigatore");
        }
    }

    /// <summary>
    /// Una copia lascia la porta che aveva copiato (<see cref="IsACopy"/>), perche' la missione da cui l'ha
    /// copiata non ha piu' la cella, o perche' il PLC gliel'ha rifiutata e nessuna missione del gruppo la
    /// presta piu'. Della configurazione, la lascia come copia (<c>PathMission.DropTheCopiedDoor</c>): la
    /// porta gliela rifa' la ricerca per tipo del gruppo. Con il segno dell'operatore (una copia a cui ha
    /// cambiato solo il gateway, risposta del 9/10 alla seconda domanda del compito 17) la lascia come una
    /// cella persa (<c>PathMission.DropStorageCell</c>) e va in coda al navigatore, che le prenota una cella
    /// nel magazzino della sua destinazione con il suo gateway (<see cref="KeepTheStorageCellAsync"/>): la
    /// ricerca per tipo del gruppo non scrive la destinazione scelta dall'operatore
    /// (<c>PathMission.UnlessChosenByOperator</c>), e una copia dell'operatore senza porta la aspetterebbe per
    /// sempre (<c>PathMission.LeftACopiedDoor</c> non la conta).
    /// </summary>
    /// <param name="what">La riga di avviso, fino a cio' che la copia ha lasciato.</param>
    /// <param name="asACopy">Il seguito della riga per la copia della configurazione.</param>
    /// <param name="asALostCell">Il seguito della riga per la copia con il segno dell'operatore.</param>
    /// <returns>
    /// Falso se la copia e' stata tolta intanto (le scritture della porta ne rifarebbero la riga sul
    /// database del WCS), o ha uno step in volo, e tiene porta e rotta.
    /// </returns>
    /// <remarks>Chi chiama tiene la copia ferma.</remarks>
    private static bool LeaveTheDoorAsACopy(PathMission copy, string what, string asACopy, string asALostCell)
    {
        if (copy.OperatorDestination)
            return DropTheCellAndRequeue(copy, _ => $"{what}, e {asALostCell}");

        if (!ReferenceEquals(ActiveMissions.Instance[copy.MissionId], copy) || !copy.DropTheCopiedDoor())
            return false;

        Navigator.WriteLog($"{what}, {asACopy}", LogEntryType.Warning);
        return true;
    }

    /// <summary>
    /// Le copie della porta della missione che un cambio dell'operatore sta per toccare (punto 3
    /// dell'8/10 sera): la cella o la destinazione che cambiano, anche solo un'altra cella nello stesso
    /// magazzino, tolgono alla missione la cella che le copie avevano copiato, come la cella persa. Si
    /// fermano prima delle chiamate al gestionale (<see cref="Stop"/>), come nel navigatore
    /// (<see cref="KeepTheStorageCellAsync"/>), lasciano la porta quando la cella della missione non e'
    /// piu' sua (<see cref="Leave"/>), e ripartono dopo di lei (<see cref="Restart"/>). Prima tenevano la
    /// porta vecchia, e a fermo finito ci depositavano da sole. Il cambio del solo gateway che tiene la
    /// cella (<see cref="KeepTheCellAndRouteAgain"/>) non le ferma e non le tocca.
    /// </summary>
    private sealed class CopiesOfTheDoor(PathMission mission)
    {
        private string? _door;
        private List<PathMission> _copies = [];
        private bool _left;

        /// <summary>
        /// Ferma le copie della porta della missione, se ne puo' avere (<see cref="MayHaveCopies"/>), con
        /// <see cref="StopTheCopiesOfTheDoor"/>. La missione e' gia' ferma, con il cambio cominciato.
        /// </summary>
        /// <returns>
        /// Il rifiuto del cambio se una copia non si puo' fermare, da riprovare: nessuna resta ferma, e
        /// niente e' stato toccato. <c>null</c> se le copie sono ferme, o se non ce ne sono.
        /// </returns>
        public DestinationChange? Stop()
        {
            if (!MayHaveCopies(mission)) return null;

            var door = mission.StorageDoorCode;
            if (StopTheCopiesOfTheDoor(mission, door, out var notStopped) is not { } copies)
                return DestinationChange.Refused(MissionDestinationOutcome.CopyInFlight,
                    $"le copie della porta {door} della missione non si fermano: {notStopped}", LogEntryType.Warning);

            _door = door;
            _copies = copies;
            return null;
        }

        /// <summary>
        /// La cella della missione da qui non e' piu' sua, comunque vada il cambio: le copie lasciano la
        /// porta (<see cref="LeaveTheCopiedDoor"/>) prima che la missione scriva la sua riga, come nel
        /// navigatore, perche' una ricarica fra le due righe converga. Lo chiamano la prenotazione, che
        /// lascia la cella anche se il cambio poi si rifiuta (la cella persa), e il commit verso un
        /// dispositivo quando passa (<see cref="Commit"/>).
        /// </summary>
        /// <remarks>
        /// Dal commit gira sotto il lucchetto della rotta della missione (<c>PathMission.CommitRoute</c>), e
        /// la rimozione della rotta delle copie prende i loro: dalla missione alle sue copie, mai al
        /// contrario. Le copie sono ferme per il cambio (<see cref="Stop"/>), e fuori da qui nessuno che tiene il
        /// lucchetto della rotta di una missione prende quello di un'altra.
        /// </remarks>
        public void Leave()
        {
            if (_left || _copies.Count == 0) return;

            _left = true;
            LeaveTheCopiedDoor(mission, _copies, _door!);
        }

        /// <summary>
        /// A cambio finito, con la missione ancora ferma: se non tiene piu' la porta che le copie avevano
        /// copiato (tolta, un'altra porta, nessuna) e le copie non l'hanno ancora lasciata, la lasciano
        /// adesso. E' una rete per un'eccezione a meta': oggi ogni via che cambia la porta della missione fa
        /// lasciare prima la porta alle copie (la prenotazione, <see cref="Commit"/>), e una missione ferma
        /// per il cambio non si puo' togliere (<see cref="TheCopiesLeaveTheDoorOfAMissionThatLeaves"/>).
        /// </summary>
        public void LeaveIfTheDoorIsGone()
        {
            if (_copies.Count > 0 &&
                (!ReferenceEquals(ActiveMissions.Instance[mission.MissionId], mission) ||
                 !string.Equals(mission.StorageDoorCode, _door, StringComparison.Ordinal)))
                Leave();
        }

        /// <summary>Le copie ripartono, dopo la missione: il giro del gruppo non ne conta una senza di lei.</summary>
        public void Restart()
        {
            foreach (var copy in _copies)
                copy.EndOperatorChange();

            _copies = [];
        }
    }

    /// <summary>
    /// La missione esce dal motore (<c>ActiveMissions.Remove</c>) da un dispositivo di un gruppo, e la
    /// cella che aveva prenotato non e' piu' sua (punto 3 dell'8/10 sera): le copie della sua porta si
    /// fermano e la lasciano prima che lei esca (<see cref="StopTheCopiesOfTheDoor"/>,
    /// <see cref="LeaveTheCopiedDoor"/>), e le rifa' la ricerca per tipo del gruppo. Prima tenevano la
    /// porta, e ci depositavano da sole in una cella che nessuna missione teneva piu'.
    /// </summary>
    /// <param name="held">
    /// Le missioni ferme: la missione che esce, e le copie, senza porta. A rimozione fatta, con la missione
    /// gia' fuori dalle attive, <see cref="RestartTheCopies"/>.
    /// </param>
    /// <param name="notStopped">Con il falso, quale missione non si e' fermata e perche'.</param>
    /// <returns>
    /// Falso se la missione o una copia non si puo' fermare (un avvio, una ricerca o un cambio in corso per
    /// lei) o, ferma, ha uno step in volo: niente e' toccato, nessuno resta fermo, e la rimozione si
    /// rifiuta, da riprovare. <paramref name="notStopped"/> dice quale: la missione stessa, o una copia con
    /// le parole del cambio dell'operatore rifiutato per lo stesso motivo («another load unit sharing its
    /// cell is moving», <see cref="MissionDestinationOutcome.CopyInFlight"/>).
    /// </returns>
    /// <remarks>
    /// Prima delle copie si ferma la missione stessa (<c>PathMission.TryBeginOperatorChange</c>), come fanno
    /// il navigatore (<see cref="KeepTheStorageCellAsync"/>) e il cambio dell'operatore: un ciclo del gruppo
    /// che l'ha gia' contata (<c>AutomationDevice.VerifyWhereAllocateLuGrouped</c>,
    /// <c>AutomationDevice.RedoTheCopiedDoorsAsync</c>) ne legge la porta e la da' a una missione che non ne
    /// ha ancora una, che oggi non e' una copia e non si ferma qui. Con il ciclo in corso la rimozione si
    /// rifiuta; ferma, nessun ciclo la conta piu' fino a che non e' fuori. Prima la missione usciva, con la
    /// prenotazione restituita, e il ciclo ripartiva su di lei: le riprenotava la cella, e ne prestava la
    /// porta a una copia nuova, che nessuna missione teneva (primo giro di correzioni del compito 15).
    /// </remarks>
    internal static bool TheCopiesLeaveTheDoorOfAMissionThatLeaves(PathMission mission, out List<PathMission> held,
        out string? notStopped)
    {
        held = [];
        notStopped = null;
        if (!MayHaveCopies(mission) || mission.PositionItem?.BelongedGroup == null) return true;

        if (mission.TryBeginOperatorChange() is var start and not PathMission.OperatorChangeStart.Begun)
        {
            notStopped = $"la missione stessa non si puo' fermare adesso ({start})";
            return false;
        }

        List<PathMission>? copies = null;
        var stopped = false;

        try
        {
            // Ferma, nessuno step della missione parte piu': uno partito fra lo sguardo della rimozione e
            // l'arresto si vede adesso, con la regola della rimozione (PathMission.StepInFlight), come
            // fanno il navigatore e il cambio dopo averla fermata. Prima la rimozione andava avanti e
            // toglieva la rotta di uno step che il dispositivo stava muovendo.
            if (mission.StepInFlight() is { } inFlight)
            {
                notStopped = $"lo step {inFlight.StepId} della missione stessa e' in volo su {inFlight.FromNodeId}";
                return false;
            }

            var door = mission.StorageDoorCode!;
            if (StopTheCopiesOfTheDoor(mission, door, out var copyNotStopped) is not { } stoppedCopies)
            {
                // Una copia che si muove ha le parole del cambio dell'operatore rifiutato per lo stesso
                // motivo (MissionDestinationOutcome.CopyInFlight): la rimozione resta interna al motore,
                // e solo la riga del registro le usa (risposta del 9/10 alla domanda del compito 17).
                // Prima diceva solo quale copia non si fermava, e non si distingueva dalla missione stessa.
                notStopped = $"another load unit sharing its cell is moving - {copyNotStopped}";
                return false;
            }

            copies = stoppedCopies;
            LeaveTheCopiedDoor(mission, copies, door);
            held = [mission, .. copies];
            stopped = true;
            return true;
        }
        finally
        {
            // Senza la rimozione - rifiutata, o un'eccezione mentre le copie lasciano la porta - nessuno
            // resta fermo: prima un'eccezione dalla scrittura della porta lasciava ferme per sempre la
            // missione e le copie, perche' la rimozione apre il suo try solo dopo questo metodo.
            if (!stopped)
            {
                mission.EndOperatorChange();
                if (copies != null) RestartTheCopies(copies);
            }
        }
    }

    /// <summary>
    /// La missione uscita e le copie fermate da <see cref="TheCopiesLeaveTheDoorOfAMissionThatLeaves"/> ripartono.
    /// La missione uscita la chiude prima chi la toglie (<c>PathMission.Withdraw</c>, in <c>ActiveMissions.Remove</c>):
    /// ripartita, nessun ciclo la conta piu'.
    /// </summary>
    internal static void RestartTheCopies(List<PathMission> held)
    {
        foreach (var mission in held)
            mission.EndOperatorChange();
    }

    /// <summary>
    /// La missione puo' avere delle copie della sua porta (<see cref="StopTheCopiesOfTheDoor"/>): ha una
    /// porta in un magazzino e non e' una copia lei stessa. Una copia a cui l'operatore ha cambiato solo il
    /// gateway resta una copia (<see cref="IsACopy"/>), e non ne ha: quando se ne va, o cambia cella, le
    /// copie vere della porta la tengono, perche' la cella e' ancora della missione che l'ha prenotata
    /// (risposta del 9/10 alla seconda domanda del compito 17). Non e' <see cref="LendsItsDoor"/>, che dice chi
    /// presta la porta alle copie nuove: dopo il cambio del solo gateway la missione che ha prenotato la
    /// cella ha il segno dell'operatore e non la presta piu', ma le copie che l'avevano copiata la tengono
    /// (<see cref="KeepTheCellAndRouteAgain"/>), e la lasciano quando la missione perde la cella
    /// (punto 3 dell'8/10 sera). E' la regola del navigatore, che ferma le copie di ogni missione con
    /// la cella persa, e di ogni via che fa lasciare la porta alle copie: il cambio dell'operatore, la
    /// missione tolta, e le tre del rifiuto del PLC (<see cref="StopTheCopiesOfTheRefusedDoors"/>,
    /// <see cref="LeaveTheDoorsThatChanged"/>, <see cref="LeaveTheRefusedCellWithItsCopies"/>), che fino al
    /// primo giro di correzioni del compito 15 guardavano <see cref="LendsItsDoor"/>: dopo il solo gateway
    /// la copia teneva la porta che il PLC aveva rifiutato alla missione, e ci depositava da sola.
    /// </summary>
    private static bool MayHaveCopies(PathMission mission) =>
        !string.IsNullOrEmpty(mission.StorageDoorCode) &&
        mission.DestinationItem is { DeviceType: DeviceTypes.Storage } &&
        !IsACopy(mission);

    /// <summary>
    /// La missione presta la sua porta alle copie del gruppo: e' della configurazione, con una cella
    /// prenotata per lei, verso un magazzino. Solo da lei la ricerca per tipo del gruppo prende il
    /// selettore (<c>AutomationDevice.VerifyWhereAllocateLuGrouped</c>, <c>AutomationDevice.RedoTheCopiedDoorsAsync</c>,
    /// <c>AutomationDeviceStorageManager.FindNewStoreLocationGrouped</c>); la missione dell'operatore non
    /// lascia il suo, e un'altra missione della configurazione con la stessa porta non e' una sua copia.
    /// </summary>
    private static bool LendsItsDoor(PathMission mission) =>
        mission is { OperatorDestination: false, StorageCellReserved: true } &&
        !string.IsNullOrEmpty(mission.StorageDoorCode) &&
        mission.DestinationItem is { DeviceType: DeviceTypes.Storage };

    /// <summary>
    /// La missione ha copiato la porta di un'altra del gruppo con la ricerca per tipo: senza una cella
    /// prenotata per lei, verso un magazzino, su un dispositivo di un gruppo. <see cref="StopTheCopiesOfTheDoor"/>
    /// la usa per trovare le copie di una porta, con la porta e il gruppo della missione da cui le hanno
    /// copiata.
    /// </summary>
    /// <remarks>
    /// Il segno dell'operatore non conta (risposta del 9/10 alla seconda domanda del compito 17): una copia
    /// a cui l'operatore cambia solo il gateway tiene la porta copiata (<see cref="KeepTheCellAndRouteAgain"/>)
    /// e ne prende il segno, ma la cella resta della missione che l'ha prenotata, e lei resta una copia. Segue
    /// la regola delle copie: lascia la porta con quella missione (<see cref="StopTheCopiesOfTheDoor"/>), il
    /// suo deposito non chiede la cella al gestionale e aspetta con lei (<c>PathMission.MayStartTheDeposit</c>,
    /// <c>AutomationDeviceStepExecutor.BeginStepStarts</c>), e il navigatore non le rifa' la cella
    /// (<see cref="KeepTheStorageCellAsync"/>); quando se ne va, o cambia cella, le copie vere tengono la
    /// porta (<see cref="MayHaveCopies"/>). Prima, con il segno, non era piu' una copia: la sua rimozione
    /// fermava le copie vere e le faceva lasciare la porta come se la cella fosse persa, e si poteva
    /// rifiutare dove nessuno la riprova (<c>AutomationDeviceLuManager.ProcessStandardUnloading</c>); e il
    /// navigatore, prima del suo deposito, le toglieva la porta con le copie vere e le prenotava una cella sua.
    /// Senza porta non e' piu' una copia: la cella gliela prenota il navigatore nel magazzino che ha scelto
    /// l'operatore (<see cref="LeaveTheDoorAsACopy"/>).
    /// </remarks>
    internal static bool IsACopy(PathMission mission) =>
        mission is { StorageCellReserved: false } &&
        !string.IsNullOrEmpty(mission.StorageDoorCode) &&
        mission.DestinationItem is { DeviceType: DeviceTypes.Storage } &&
        mission.PositionItem?.BelongedGroup != null;

    /// <summary>
    /// Le copie delle porte rifiutate dal PLC, ferme per la ricerca del gruppo dopo il rifiuto
    /// (<see cref="StopTheCopiesOfTheRefusedDoors"/>): per ogni missione rifiutata che puo' avere delle copie
    /// (<see cref="MayHaveCopies"/>), la porta letta prima della ricerca e le sue copie. Alla fine,
    /// <see cref="LeaveTheDoorsThatChanged"/>.
    /// </summary>
    internal sealed record HeldCopies(PathMission Mission, string Door, List<PathMission> Copies);

    /// <summary>
    /// Prima della ricerca del gruppo dopo un deposito rifiutato dal PLC
    /// (<c>AutomationDeviceStorageManager.FindNewStoreLocationGrouped</c>): le copie delle porte delle
    /// missioni rifiutate che non sono rifiutate anche loro si fermano (<see cref="StopTheCopiesOfTheDoor"/>),
    /// perche' nessun ciclo del gruppo ne avvii il deposito mentre la missione da cui hanno copiato cambia
    /// cella. Sono le copie che stanno in un altro movimento: quelle nello stesso movimento rifiutato le
    /// conta e le rifa' la ricerca stessa.
    /// </summary>
    /// <param name="refused">Le missioni rifiutate, contate dalla ricerca: non si fermano qui.</param>
    /// <param name="worker">Chi chiede, per la riga di registro.</param>
    /// <returns>
    /// Le copie ferme, per porta; nessuna se nessuna missione rifiutata ne puo' avere. <c>null</c> se una
    /// copia non si puo' fermare (un avvio, una ricerca o un cambio in corso per lei, o uno step in volo):
    /// nessuna resta ferma, e la ricerca non si fa, come quando una missione rifiutata non si puo' contare.
    /// </returns>
    internal static List<HeldCopies>? StopTheCopiesOfTheRefusedDoors(IReadOnlyCollection<PathMission> refused,
        string worker)
    {
        var held = new List<HeldCopies>();

        foreach (var mission in refused.Where(MayHaveCopies))
        {
            var door = mission.StorageDoorCode!;
            if (held.Any(already => already.Door == door)) continue;

            if (StopTheCopiesOfTheDoor(mission, door, out var notStopped, refused) is not { } copies)
            {
                foreach (var copy in held.SelectMany(already => already.Copies))
                    copy.EndOperatorChange();

                Navigator.WriteLog(
                    $"{worker}: il PLC ha rifiutato la cella della porta {door} alla missione {mission.MissionId}, ma {notStopped}: adesso nessuna cella nuova",
                    LogEntryType.Information);
                return null;
            }

            held.Add(new HeldCopies(mission, door, copies));
        }

        return held;
    }

    /// <summary>
    /// Dopo la ricerca del gruppo dopo un deposito rifiutato: la missione rifiutata la cui porta non e'
    /// piu' quella di prima (una cella nuova, lo scarto, la cella persa) non ha piu' la cella che le copie
    /// avevano copiato, e le copie lasciano la porta vecchia (<see cref="LeaveTheCopiedDoor"/>): le rifa' la
    /// ricerca per tipo del gruppo, di solito con lei nella cella nuova (risposta 8 del proprietario
    /// dell'8/10, punto 3 dell'8/10 sera). Prima la ricerca, che dall'8/10 sera tocca solo le missioni
    /// rifiutate, lasciava la copia di un altro movimento verso la cella rifiutata, che nessuna missione
    /// teneva piu', e ci depositava da sola. Con la porta uguale, ancora prenotata per lei (nessun posto,
    /// adesso non si sa, il gestionale muto), le copie restano com'erano: con il gestionale muto le fa
    /// lasciare poi <see cref="LeaveTheRefusedCell"/>, con la missione. Poi tutte ripartono.
    /// </summary>
    internal static void LeaveTheDoorsThatChanged(List<HeldCopies> held)
    {
        try
        {
            foreach (var (mission, door, copies) in held)
                if (!string.Equals(mission.StorageDoorCode, door, StringComparison.Ordinal) || !MayHaveCopies(mission))
                    LeaveTheCopiedDoor(mission, copies, door);
        }
        finally
        {
            // Tutte ripartono, anche se una scrittura della porta solleva: prima un'eccezione lasciava
            // ferme per sempre le copie di quella porta e delle porte che venivano dopo.
            foreach (var copy in held.SelectMany(entry => entry.Copies))
                copy.EndOperatorChange();
        }
    }

    /// <summary>
    /// La cella persa (decisioni del proprietario dell'1/10): il cambio verso un magazzino e'
    /// rifiutato dopo che la prenotazione ha lasciato la cella che la missione teneva. La missione
    /// lascia porta, indirizzo e rotta (<c>PathMission.DropStorageCell</c>, che alza
    /// <c>PathMission.StorageCellLost</c>) e torna in coda al navigatore, che le prenota un'altra
    /// cella nel magazzino della sua destinazione (quella di prima, perche' il cambio e' rifiutato)
    /// con il flusso del cambio (<see cref="KeepTheStorageCellAsync"/>), che la destinazione l'abbia
    /// scelta l'operatore o la configurazione; se li' non c'e' posto, la missione della configurazione
    /// va in un altro magazzino del suo ricollocatore o allo scarto. Il segno dell'operatore resta
    /// com'era.
    /// </summary>
    /// <param name="why">Perche' il cambio e' stato rifiutato, per la riga di registro.</param>
    /// <remarks>
    /// Chi chiama tiene ferma la missione: il cambio dell'operatore ancora cominciato, o, per la cella
    /// rifiutata dal PLC (<see cref="AnotherCellInTheChosenStorageAsync"/>), un avvio contato e il posto
    /// delle ricerche. Nessuno avvia uno step della missione mentre la rotta si toglie. La coda si
    /// riempie anche a motore fermo; il giro delle rotte la svuota in Run, come per lo spostamento.
    /// </remarks>
    /// <returns>Vero se la missione ha lasciato la cella; falso se era stata tolta, o con uno step in volo.</returns>
    private static bool LoseTheCell(PathMission mission, string why, PathMission.CellGiveBack? giveBack = null) =>
        DropTheCellAndRequeue(mission, door =>
                $"Missione {mission.MissionId}: la prenotazione di un'altra cella, non andata a buon fine ({why}), aveva gia' lasciato la cella della porta {door}, che non e' piu' prenotata: porta, indirizzo e rotta tolti, la missione torna al navigatore per una cella in {mission.DestinationItemId}",
            giveBack,
            $"La prenotazione di un'altra cella ha sostituito la cella {giveBack?.CellId:0} della porta {giveBack?.DoorCode}, ma il cambio della missione non e' stato scritto ({why})");

    /// <summary>
    /// Il corpo di <see cref="LoseTheCell"/>, di <see cref="LeaveTheRefusedCell"/> e della copia con il segno
    /// dell'operatore che lascia la porta (<see cref="LeaveTheDoorAsACopy"/>): la missione lascia
    /// porta, indirizzo e rotta, con la cella persa, e torna in coda al navigatore. Chi chiama la tiene ferma.
    /// </summary>
    /// <param name="line">La riga di avviso, dalla porta lasciata.</param>
    private static bool DropTheCellAndRequeue(PathMission mission, Func<string?, string> line,
        PathMission.CellGiveBack? giveBack = null, string? giveBackReason = null)
    {
        // Tolta intanto: la rimozione ha gia' restituito la sua prenotazione, e non c'e' niente da rifare.
        if (!ReferenceEquals(ActiveMissions.Instance[mission.MissionId], mission)) return false;

        var door = mission.StorageDoorCode;
        var dropped = giveBack == null
            ? mission.DropStorageCell()
            : mission.DropStorageCellAndQueueGiveBack(giveBack,
                giveBackReason ?? "la nuova prenotazione ha sostituito la cella");
        if (!dropped) return false;

        RoutesToBuild.Instance.EnqueueIfAbsent(mission);

        Navigator.WriteLog(line(door), LogEntryType.Warning);
        return true;
    }

    /// <summary>
    /// Il PLC ha rifiutato il deposito della missione nella cella della porta <paramref name="refusedDoor"/>,
    /// e il gestionale non ha risposto dopo (alla domanda della cella nuova, o al blocco della cella piena
    /// del 67): la missione lascia la porta rifiutata, come una cella persa (risposta 7 del proprietario
    /// dell'8/10 sera). Il dispositivo e' gia' in errore e aspetta l'operatore; quando il gestionale
    /// risponde, il navigatore le prenota un'altra cella nel magazzino della sua destinazione
    /// (<see cref="KeepTheStorageCellAsync"/>), con le celle rifiutate ancora escluse
    /// (<c>PathMission.CellsRefusedByThePlc</c>), prima del deposito. Prima la missione teneva la porta
    /// rifiutata, e alla ripartenza dell'operatore ci depositava di nuovo.
    /// </summary>
    /// <param name="refusedDoor">La porta che il PLC ha rifiutato, letta prima della ricerca.</param>
    /// <param name="worker">Chi chiede, per le righe di registro.</param>
    /// <param name="refusedTogether">
    /// Le missioni il cui deposito il PLC ha rifiutato con lo stesso errore, questa compresa: una copia
    /// resta con la missione da cui ha copiato la porta solo se e' fra queste (<see cref="LeaveTheRefusedCopiedDoor"/>).
    /// </param>
    /// <returns>Vero se la missione ha lasciato la porta.</returns>
    /// <remarks>
    /// Solo se la porta e' ancora quella rifiutata: una missione che intanto ha avuto una cella nuova
    /// (un'altra del gruppo, prima che il gestionale smettesse di rispondere) o che l'ha gia' persa
    /// (<see cref="LoseTheCell"/>) la tiene com'e'. La missione si ferma qui, come per la ricerca dopo il
    /// rifiuto: un avvio contato (<c>PathMission.TryBeginStepStart</c>), che tiene fuori il cambio
    /// dell'operatore, e il posto delle ricerche (<c>Navigator.TryClaimCellSearch</c>). Se un cambio
    /// dell'operatore o una ricerca la tiene, la porta la decide chi la tiene, e qui non si tocca niente.
    /// La prenotazione presso il gestionale non si chiede: non risponde, e la cella nuova la sostituisce.
    /// Nessun lucchetto, nessun semaforo, nessuna chiamata al gestionale. Le copie della porta del gruppo
    /// la lasciano con la missione da cui l'hanno copiata (<see cref="LeaveTheRefusedCellWithItsCopies"/>),
    /// e una copia rifiutata la lascia come copia, non come cella persa, o come cella persa se la missione
    /// da cui l'ha copiata, non rifiutata, la tiene ancora (<see cref="LeaveTheRefusedCopiedDoor"/>).
    /// </remarks>
    internal static bool LeaveTheRefusedCell(PathMission mission, string? refusedDoor, string worker,
        IReadOnlyCollection<PathMission> refusedTogether)
    {
        if (string.IsNullOrEmpty(refusedDoor) || mission.StorageDoorCode != refusedDoor) return false;

        if (!mission.TryBeginStepStart())
        {
            Navigator.WriteLog(
                $"{worker}: la missione {mission.MissionId} sta cambiando destinazione: la porta rifiutata {refusedDoor} la decide il cambio",
                LogEntryType.Information);
            return false;
        }

        try
        {
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            if (!Navigator.TryClaimCellSearch(mission.MissionId, finished.Task))
            {
                Navigator.WriteLog(
                    $"{worker}: una ricerca della cella e' in corso per la missione {mission.MissionId}: la porta rifiutata {refusedDoor} la decide lei",
                    LogEntryType.Information);
                return false;
            }

            try
            {
                if (mission.StorageDoorCode != refusedDoor) return false;

                return IsACopy(mission)
                    ? LeaveTheRefusedCopiedDoor(mission, refusedDoor, worker, refusedTogether)
                    : LeaveTheRefusedCellWithItsCopies(mission, refusedDoor, worker);
            }
            finally
            {
                Navigator.ReleaseCellSearchClaim(mission.MissionId, finished.Task);
                finished.TrySetResult();
            }
        }
        finally
        {
            mission.EndStepStart();
        }
    }

    /// <summary>
    /// La missione rifiutata ha la cella (dell'operatore, o prenotata per lei, o comunque non copiata):
    /// le copie della sua porta si fermano e la lasciano prima di lei (<see cref="StopTheCopiesOfTheDoor"/>,
    /// <see cref="LeaveTheCopiedDoor"/>, punto 3 dell'8/10 sera), anche quelle di un altro movimento;
    /// poi lei lascia porta, indirizzo e rotta con la cella persa e torna al navigatore. Se una copia non
    /// si puo' fermare non si tocca niente, come nel navigatore (<see cref="KeepTheStorageCellAsync"/>):
    /// la missione tiene la porta rifiutata, e il dispositivo e' gia' in errore. Prima la missione lasciava
    /// la porta e le copie no: nessuna missione con quella porta le tratteneva piu', e il navigatore, che
    /// rifa' la cella persa cercando le copie per porta, con la porta gia' vuota non le trovava.
    /// </summary>
    private static bool LeaveTheRefusedCellWithItsCopies(PathMission mission, string refusedDoor, string worker)
    {
        List<PathMission> copies = [];

        if (MayHaveCopies(mission))
        {
            if (StopTheCopiesOfTheDoor(mission, refusedDoor, out var notStopped) is not { } stopped)
            {
                Navigator.WriteLog(
                    $"{worker}: il PLC ha rifiutato la cella della porta {refusedDoor} alla missione {mission.MissionId}, e il gestionale non risponde, ma {notStopped}: porta e rotta restano",
                    LogEntryType.Warning);
                return false;
            }

            copies = stopped;
        }

        try
        {
            // Prima le copie, poi la missione, come nel navigatore: una ricarica fra le due righe converge.
            LeaveTheCopiedDoor(mission, copies, refusedDoor);

            return DropTheCellAndRequeue(mission, door =>
                $"{worker}: il PLC ha rifiutato la cella della porta {door} alla missione {mission.MissionId}, e il gestionale non risponde: porta, indirizzo e rotta tolti, la missione torna al navigatore per un'altra cella in {mission.DestinationItemId}, esclusa quella rifiutata");
        }
        finally
        {
            foreach (var copy in copies)
                copy.EndOperatorChange();
        }
    }

    /// <summary>
    /// La missione rifiutata e' una copia (<see cref="IsACopy"/>): la porta non era sua. Se nessuna missione
    /// del gruppo che presta la porta la tiene piu', la lascia come copia (<c>PathMission.DropTheCopiedDoor</c>),
    /// senza la cella persa e senza tornare al navigatore: la rifa' la ricerca per tipo del gruppo, di solito
    /// con la missione da cui aveva copiato, nella sua cella nuova (risposta 8 del proprietario dell'8/10,
    /// punto 3 dell'8/10 sera). Prima, in questo caso, la lasciava come una cella persa sua: il navigatore le
    /// prenotava una cella sua nel gestionale, e la copia depositava da sola. Una copia con il segno
    /// dell'operatore la lascia come una cella persa, e la cella gliela prenota il navigatore nel magazzino
    /// scelto (<see cref="LeaveTheDoorAsACopy"/>). Se la missione che presta la
    /// porta la tiene ancora, decide il paragrafo qui sotto: la copia resta con lei, o lascia la porta come
    /// una cella persa sua.
    /// <para>
    /// Se una missione del gruppo che presta la porta la tiene ancora, conta se il PLC ha rifiutato anche
    /// lei (<paramref name="refusedTogether"/>). Rifiutata con la copia, nello stesso movimento, la copia
    /// resta con lei: la porta la lascia lei, con le sue copie, o la tiene se non la puo' lasciare, e la
    /// decide quella che ha la cella. Non rifiutata - sta in un altro movimento - tiene cella, rotta e
    /// gateway (risposta 8 dell'8/10 sera), e la copia non puo' restare con lei: la sua cella e' quella
    /// che il PLC ha appena rifiutato alla copia, e la ricerca per tipo del gruppo le ridarebbe la stessa
    /// porta. La copia la lascia allora come una cella persa sua (<see cref="DropTheCellAndRequeue"/>), e
    /// il navigatore le prenota un'altra cella con le rifiutate escluse (risposta 7 dell'8/10 sera), come
    /// fa la ricerca del gruppo dopo il rifiuto quando il gestionale risponde. Prima restava con lei con
    /// la porta rifiutata, e alla ripartenza dell'operatore ci depositava di nuovo (secondo giro di
    /// correzioni del terzo giro del compito 13).
    /// </para>
    /// </summary>
    private static bool LeaveTheRefusedCopiedDoor(PathMission mission, string refusedDoor, string worker,
        IReadOnlyCollection<PathMission> refusedTogether)
    {
        var group = mission.PositionItem?.BelongedGroup;

        if (ActiveMissions.Instance.List.OfType<PathMission>().FirstOrDefault(other =>
                !ReferenceEquals(other, mission) && LendsItsDoor(other) &&
                string.Equals(other.StorageDoorCode, refusedDoor, StringComparison.Ordinal) &&
                ReferenceEquals(other.PositionItem?.BelongedGroup, group)) is { } lender)
        {
            if (refusedTogether.Contains(lender))
            {
                Navigator.WriteLog(
                    $"{worker}: il PLC ha rifiutato la cella della porta {refusedDoor} alla missione {mission.MissionId}, che l'aveva copiata dalla missione {lender.MissionId}, rifiutata con lei: la porta resta con quella, che la tiene ancora",
                    LogEntryType.Information);
                return false;
            }

            return DropTheCellAndRequeue(mission, door =>
                $"{worker}: il PLC ha rifiutato la cella della porta {door} alla missione {mission.MissionId}, che l'aveva copiata dalla missione {lender.MissionId}, non rifiutata, e il gestionale non risponde: porta, indirizzo e rotta tolti, la missione torna al navigatore per un'altra cella in {mission.DestinationItemId}, esclusa quella rifiutata");
        }

        return LeaveTheDoorAsACopy(mission,
            $"{worker}: il PLC ha rifiutato la cella della porta {refusedDoor} alla missione {mission.MissionId}, che l'aveva copiata da un'altra missione del gruppo, e il gestionale non risponde: porta, indirizzo e rotta tolti",
            "la cella la rifa' la ricerca per tipo del gruppo",
            $"il magazzino {mission.DestinationItemId} l'ha scelto l'operatore: la cella gliela prenota il navigatore, esclusa quella rifiutata");
    }

    /// <summary>
    /// Il cambio cella di un'estrazione con l'UDC ancora nella sua cella (risposte 1 e 2 del
    /// proprietario del 2/10): il gestionale sa che la missione lascia l'assegnazione senza servirla e
    /// che l'UDC esce dalla coda di estrazione, e la riga della coda di estrazione del WCS si cancella.
    /// Chiamata prima della prenotazione, fuori da ogni lucchetto, con la missione ferma
    /// (<see cref="ReserveACellAndCommitAsync"/>).
    /// </summary>
    /// <returns>
    /// <c>null</c> se il gestionale ha avuto le segnalazioni; il rifiuto se non le ha avute tutte, con
    /// <paramref name="progress"/> che dice se la missione va chiusa.
    /// </returns>
    /// <exception cref="Exception">
    /// Il segno «segnalazioni partite» non si e' potuto scrivere nel database del WCS: nessuna
    /// segnalazione e' partita, e la missione e' l'estrazione che era. O un guasto imprevisto durante
    /// le segnalazioni, con <paramref name="progress"/> che dice di chiudere.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Le segnalazioni e i campi sono quelli dei chiamanti di oggi: la missione tolta dalle attive
    /// (<c>ActiveMissions.Remove</c>) lascia l'assegnazione che nomina con il GUID e quella che porta la
    /// sua riga di coda (la richiesta di <c>DbInfo.ReportQueuedAssignmentLeftUnserved</c>, mandata da
    /// qui per sapere quale segnalazione e' la prima); il comando che toglie un'UDC dalla coda
    /// (<c>DbInfo.DeleteLuFromExtractionQueue</c>) dice che l'UDC esce dalla coda, con l'assegnazione
    /// della sua riga, zero se non ne porta. Una missione senza assegnazione e senza riga di coda non
    /// ha niente da dire, e il suo cambio cella e' un cambio come gli altri. L'esito «non
    /// raggiungibile» della riga di estrazione, che quel comando manda, qui no (risposta del
    /// proprietario del 2/10): la riga non e' irraggiungibile, l'operatore ha deciso un'altra cosa.
    /// </para>
    /// <para>
    /// <b>Le segnalazioni consegnate a meta'</b> (specifica, risposta dell'8/10 al punto 2). Se almeno
    /// una puo' essere arrivata al gestionale e il cambio non va in porto - anche perche' il gestionale
    /// non risponde alla seconda - la missione non e' piu' l'estrazione che era, e chi chiama la chiude
    /// (<see cref="ReservationProgress.LeftTheExtraction"/>, <see cref="ChangeToStorageAsync"/>). Una
    /// segnalazione senza risposta conta come arrivata: con un tempo scaduto, una connessione caduta a
    /// richiesta partita o un 5xx il gestionale puo' averla applicata. Solo la <b>prima</b> che non puo'
    /// essere arrivata (<see cref="CouldNotHaveArrived"/>) lascia la missione l'estrazione che era, con
    /// il segno tolto e il rifiuto «gestionale fermo», o «il gestionale ha rifiutato»
    /// (<see cref="MissionDestinationOutcome.WarehouseRefused"/>) se ha risposto di no (risposta 4
    /// dell'8/10 sera): niente invio differito (risposta del proprietario del 2/10).
    /// </para>
    /// <para>
    /// <b>Il segno «segnalazioni partite»</b> (risposta dell'8/10 al punto 3) si scrive nel database
    /// del WCS, e si aspetta che ci sia, prima della prima segnalazione
    /// (<c>PathMission.MarkTheChangeOfCellReportsAsync</c>): se l'host si ferma fra le segnalazioni e il
    /// commit, la ricarica chiude l'estrazione che lo porta invece di rimetterla in piedi
    /// (<c>DbInfo.PrepareLoadedMission</c>). Il commit lo toglie (<c>PathMission.CommitRoute</c>), e
    /// la riga del cambio registrato si scrive e si aspetta, anche in Recover
    /// (<see cref="TakeTheMarkDownAsync"/>); la chiusura toglie la riga. Se non si scrive, le
    /// segnalazioni non partono.
    /// </para>
    /// <para>
    /// La riga della coda di estrazione del WCS si cancella dopo le segnalazioni, come nel comando,
    /// e la riga di estrazione per UDC che serviva torna da servire con il fatto della coda durevole
    /// (<see cref="DeleteTheQueueRowAsync"/>, voce 45, compito 4), anche quando non sono arrivate
    /// tutte e la missione si chiude: rimasta senza la sua estrazione,
    /// terrebbe l'UDC «in estrazione» per i comandi manuali (<c>DbInfo.IsLuPresentInExtranctionLu</c>),
    /// e nessuno la toglierebbe piu'. Un guasto qui si registra e basta: il gestionale puo' aver gia'
    /// saputo, e la riga va tolta a mano. L'UDC non esce dalle code di prelievo dei dispositivi, come
    /// fa il comando: il cambio cella ha bisogno del prelievo.
    /// </para>
    /// </remarks>
    private static async Task<DestinationChange?> LeaveTheExtractionAsync(IWarehouse warehouse, PathMission mission,
        ReservationProgress progress)
    {
        const string reason =
            "l'operatore ha mandato l'unita' di carico in un altro magazzino prima del prelievo: la missione e' diventata un cambio cella";

        var queueRow = mission.ExtractionQueueId;
        var assignment = mission.GuidAssignmentLu;
        if (assignment == Guid.Empty && queueRow <= 0) return null;

        // Le segnalazioni, nell'ordine, composte prima del segno: l'assegnazione della riga di coda si
        // legge dal database del WCS, e un guasto qui non ha ancora mandato niente.
        var queuedAssignment = queueRow > 0 ? await ORM.DbInfo.AssignmentOfQueueRow(queueRow) : null;
        var reports = new List<Func<Task>>();
        if (assignment != Guid.Empty)
            reports.Add(() => warehouse.ReportMissionLeftAssignmentUnservedAsync(new MissionLeftAssignmentUnservedRequest(
                mission.MissionId, mission.LuId, assignment, 0m, reason)));
        if (queuedAssignment is > 0)
            reports.Add(() => warehouse.ReportMissionLeftAssignmentUnservedAsync(new MissionLeftAssignmentUnservedRequest(
                mission.MissionId, mission.LuId, Guid.Empty, queuedAssignment.Value, reason)));
        if (queueRow > 0)
            reports.Add(() => warehouse.ReportLoadUnitLeftExtractionQueueAsync(new LoadUnitLeftExtractionQueueRequest(
                mission.LuId, queuedAssignment ?? 0m)));

        // Il segno, nel database, prima della prima segnalazione. Se non si scrive non parte niente, e
        // il segno in memoria torna giu'.
        try
        {
            await mission.MarkTheChangeOfCellReportsAsync(true);
        }
        catch
        {
            mission.ChangeOfCellReportsSent = false;
            throw;
        }

        // Da qui, per il gestionale, la missione puo' non essere piu' un'estrazione: un rifiuto la chiude.
        progress.LeftTheExtraction = true;

        try
        {
            for (var sent = 0; sent < reports.Count; sent++)
            {
                try
                {
                    await reports[sent]();
                }
                catch (WarehouseUnavailableException ex) when (sent == 0 && CouldNotHaveArrived(ex))
                {
                    progress.LeftTheExtraction = false;
                    await TakeTheMarkDownAsync(mission,
                        "il segno delle segnalazioni del cambio cella non e' stato tolto dal database del WCS");

                    // Un no del gestionale non e' un gestionale fermo: l'ha ricevuta e non l'ha
                    // applicata, e l'operatore lo deve leggere cosi' (risposta 4 dell'8/10 sera).
                    if (ex.IsPermanentRejection)
                        return DestinationChange.Refused(MissionDestinationOutcome.WarehouseRefused,
                            $"il gestionale ha rifiutato la prima segnalazione del cambio cella (la missione lascia l'assegnazione, l'UDC {mission.LuId} esce dalla coda di estrazione) e non l'ha applicata: il cambio e' rifiutato, e la missione resta l'estrazione che era: {ex.Message}",
                            LogEntryType.Warning);

                    return DestinationChange.Refused(MissionDestinationOutcome.WarehouseUnavailable,
                        $"il gestionale non ha ricevuto la prima segnalazione del cambio cella (la missione lascia l'assegnazione, l'UDC {mission.LuId} esce dalla coda di estrazione), e la missione resta l'estrazione che era: {ex.Message}",
                        LogEntryType.Warning);
                }
            }
        }
        catch (WarehouseUnavailableException ex)
        {
            await DeleteTheQueueRowAsync(mission, queueRow);
            return DestinationChange.Refused(MissionDestinationOutcome.WarehouseUnavailable,
                $"il gestionale non risponde a una segnalazione del cambio cella (la missione lascia l'assegnazione, l'UDC {mission.LuId} esce dalla coda di estrazione) dopo che almeno una puo' essergli arrivata, e la missione non e' piu' l'estrazione che era: {ex.Message}",
                LogEntryType.Warning);
        }
        catch
        {
            await DeleteTheQueueRowAsync(mission, queueRow);
            throw;
        }

        Navigator.WriteLog(
            $"Missione {mission.MissionId}: l'estrazione con l'UDC {mission.LuId} ancora nella sua cella diventa un cambio cella: il gestionale sa che la missione lascia l'assegnazione e che l'UDC esce dalla coda di estrazione",
            LogEntryType.Information);

        await DeleteTheQueueRowAsync(mission, queueRow);
        return null;
    }

    /// <summary>
    /// La segnalazione non puo' essere arrivata al gestionale, o lui non l'ha applicata: la connessione
    /// non si e' nemmeno aperta (nome non risolto, connessione rifiutata, TLS o proxy falliti: la
    /// richiesta non e' partita), o il gestionale ha risposto di no
    /// (<see cref="WarehouseUnavailableException.IsPermanentRejection"/>, un 4xx che non e' «riprova»).
    /// Tutto il resto - un tempo scaduto, una connessione caduta dopo l'invio, un 5xx, un'eccezione
    /// senza la causa di trasporto - puo' essere arrivato, e conta come arrivato.
    /// </summary>
    /// <remarks>
    /// E' la stessa distinzione che la pagina fa per le sue scritture (<c>MayHaveBeenExecuted</c> nei
    /// coordinatori di draw.io): la causa di trasporto la porta <see cref="HttpRequestException.HttpRequestError"/>
    /// dell'eccezione interna, che <c>HttpWarehouse</c> conserva.
    /// </remarks>
    private static bool CouldNotHaveArrived(WarehouseUnavailableException ex) =>
        ex.IsPermanentRejection ||
        ex.InnerException is HttpRequestException
        {
            StatusCode: null,
            HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError
            or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError
        };

    /// <summary>
    /// Il segno «segnalazioni partite» torna giu' nel database del WCS, e si aspetta la scrittura
    /// (<c>PathMission.MarkTheChangeOfCellReportsAsync</c>). Due vie: la prima segnalazione che non puo'
    /// essere arrivata, e la missione resta l'estrazione che era, che una ricarica non deve chiudere; e il
    /// cambio cella appena registrato (<c>PathMission.CommitRoute</c>), con il tipo nuovo, senza estrazione
    /// ne' assegnazione (specifica, risposta 1 dell'8/10 sera). Scrive anche in Recover, dove i setter del
    /// commit non scrivono (<c>ORM.PathMission.Update</c>): senza questa scrittura la riga restava
    /// l'estrazione con il segno, e se l'host si fermava prima di un'altra scrittura della missione la
    /// ricarica chiudeva un cambio cella riuscito (<c>DbInfo.PrepareLoadedMission</c>).
    /// </summary>
    /// <param name="what">Che cosa non e' stato scritto, per la riga di avviso.</param>
    /// <remarks>
    /// <para>
    /// Fuori da ogni lucchetto, con la missione ferma (il cambio cominciato). Non solleva: il cambio
    /// avvenuto non si disfa, e un'estrazione rimasta tale resta. Se la scrittura non riesce lo dice una
    /// riga di avviso, e il segno resta su nel database (giu' in memoria) fino alla prossima scrittura
    /// della missione: fino ad allora una ricarica chiuderebbe la missione. E' il contratto della risposta
    /// 1 dell'8/10 sera per una scrittura che cade: il motore la aspetta, non la rifa'.
    /// </para>
    /// <para>
    /// Una missione tolta intanto (la rimozione di un'estrazione ancora nella sua cella non la ferma) non si
    /// scrive: la scrittura e' un'istantanea intera, e ne rifarebbe la riga che la rimozione ha appena
    /// cancellato, come le scritture della porta (<see cref="DropTheCellAndRequeue"/>). Prima le due vie
    /// erano due metodi uguali, e nessuno dei due lo guardava (revisione finale del terzo giro).
    /// </para>
    /// </remarks>
    private static async Task TakeTheMarkDownAsync(PathMission mission, string what)
    {
        if (!StillActive(mission)) return;

        try
        {
            await mission.MarkTheChangeOfCellReportsAsync(false);
        }
        catch (Exception ex)
        {
            Navigator.WriteLog(
                $"Missione {mission.MissionId}: {what} ({ex.Message}); se l'host si ferma prima della prossima scrittura della missione, la ricarica la chiude come un'estrazione con le segnalazioni partite",
                LogEntryType.Warning);
        }
    }

    /// <summary>
    /// Sonda per i test: <see cref="CommitAfterThePickUpHandOffAsync"/> la chiama prima di aspettare la
    /// consegna del prelievo, cosi' una prova sa che il cambio e' arrivato li' senza un'attesa a tempo.
    /// In esercizio e' sempre nulla.
    /// </summary>
    internal static Action<PathMission>? WaitingForThePickUpHandOffForTesting { get; set; }

    /// <summary>
    /// Il commit di un'estrazione gia' prelevata verso un magazzino viene dopo la consegna del suo
    /// prelievo al gestionale, e la consegna non parte mentre il commit guarda che cosa il prelievo ha
    /// servito (voce 45, primo giro di correzioni del compito 3). Per ogni altro commit chiama e basta.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Perche'.</b> Il commit legge i tre campi di quello che il prelievo ha servito
    /// (<c>PathMission.BecomeADepositOfAPickedExtraction</c>) e la missione diventa un deposito. I tre
    /// campi li scrive la consegna del prelievo (<c>PathMission.ProcessExtractionQueue</c>: la riga di
    /// coda letta, l'assegnazione e la riga di estrazione copiate, la conferma diretta al gestionale, poi
    /// il GUID copiato e «assegnazione servita»), che parte nel lavoro di sfondo del cambio di posizione
    /// (<c>PathMission.PositionWork</c>) e non ha niente in comune con il lucchetto della rotta. Senza
    /// quest'ordine un commit arrivato prima della copia faceva un deposito senza niente da riaprire,
    /// e la consegna, partita dopo sul deposito, confermava l'assegnazione e la diceva servita: l'ordine
    /// restava servito senza una riga nel registro. Arrivato fra la copia e il GUID, il fatto partiva
    /// senza il GUID e il deposito si teneva il GUID servito; arrivato mentre la conferma diretta era in
    /// volo (fino a trenta secondi), la riapertura poteva arrivare al gestionale prima della conferma che
    /// doveva disfare.
    /// </para>
    /// <para>
    /// <b>Come.</b> Prima si aspetta il lavoro di sfondo dell'ultimo cambio di posizione, che porta la
    /// consegna dovuta all'arrivo; poi si prende il lucchetto delle estrazioni
    /// (<c>Navigator.SyncExtraction</c>), quello sotto cui la consegna gira, per il solo tratto sincrono
    /// del commit: una consegna partita da un'altra via (lo scarico,
    /// <c>PathMission.TryPersistExtractionOutcomeBeforeUnload</c>) o da uno spostamento arrivato intanto
    /// finisce prima o comincia dopo, e in quel caso trova la missione gia' spostata e il commit
    /// rifiutato. Le due attese stanno fuori da ogni altro lucchetto, e sotto quello delle estrazioni non
    /// si chiama il gestionale. Se il lucchetto non arriva nei suoi trenta secondi il cambio e' rifiutato
    /// come da riprovare (<see cref="MissionDestinationOutcome.StepInFlight"/>), e chi chiama restituisce
    /// la cella presa come per ogni commit rifiutato.
    /// </para>
    /// <para>
    /// <b>Che cosa resta fuori.</b> Su un dispositivo non agganciato a un magazzino la consegna non e'
    /// ancora dovuta (<c>PathMission.ShouldProcessQueue</c>), e una consegna che non ha scritto l'esito
    /// nel <c>wcs.db</c> lascia la riga di coda alla missione: in tutti e due i casi il commit trova
    /// i tre campi vuoti e fa un deposito senza fatto, come prima della voce 45.
    /// </para>
    /// </remarks>
    private static async Task<CommitOutcome> CommitAfterThePickUpHandOffAsync(PathMission mission, bool changeOfCell,
        Func<CommitOutcome> commit)
    {
        if (changeOfCell || mission.MissionType != MissionTypes.Extraction ||
            mission.PositionItem is not { DeviceType: not DeviceTypes.Storage })
            return commit();

        var (handedOff, outcome) = await AfterThePickUpHandOffAsync(mission, commit);

        return handedOff
            ? outcome!
            : new(DestinationChange.Refused(MissionDestinationOutcome.StepInFlight,
                "il prelievo dell'estrazione si sta ancora consegnando al gestionale (lucchetto delle estrazioni non preso in tempo)",
                LogEntryType.Warning), false);
    }

    /// <summary>
    /// The order of item 45 between a picked-up extraction becoming a deposit and the hand-off of its
    /// pick-up (see <see cref="CommitAfterThePickUpHandOffAsync"/>): first the background work of the
    /// last change of position, which carries the hand-off due on arrival, then <paramref name="work"/>
    /// under the extraction lock (<c>Navigator.SyncExtraction</c>). The operator's commit and the route
    /// rebuild that makes the extraction a deposit (item 54, <c>PathMission.BecomeADepositAfterThePickUpHandOffAsync</c>)
    /// share it. Both waits are out of every other lock, and nothing under the extraction lock calls
    /// the warehouse. False, with nothing done, if the lock did not come in its thirty seconds.
    /// </summary>
    /// <remarks>
    /// The caller must not be the background work of the position itself (<c>PathMission.PositionWork</c>),
    /// which it would wait for: the route rebuild it starts is for a <c>PickUpAndStoreAgain</c>, never an
    /// extraction.
    /// </remarks>
    internal static async Task<(bool HandedOff, T? Result)> AfterThePickUpHandOffAsync<T>(PathMission mission, Func<T> work)
    {
        WaitingForThePickUpHandOffForTesting?.Invoke(mission);
        await mission.PositionWork;

        if (!await Navigator.SyncExtraction.WaitAsync(Navigator.SyncExtractionTimeout))
            return (false, default);

        try
        {
            return (true, work());
        }
        finally
        {
            Navigator.SyncExtraction.Release();
        }
    }

    /// <summary>
    /// Aspetta la scrittura che il commit ha messo in coda per l'estrazione gia' prelevata diventata un
    /// deposito: la riga della missione con il tipo nuovo e la riga della coda del fatto per il
    /// gestionale, nella stessa transazione (<c>PathMission.BecomeADepositOfAPickedExtraction</c>, voce
    /// 45). Niente se il commit non l'ha messa.
    /// </summary>
    /// <remarks>
    /// Fuori da ogni lucchetto, con la missione ferma (il cambio cominciato). Non solleva: il cambio
    /// avvenuto non si disfa. Se la scrittura non riesce lo dice una riga di errore, perche' il
    /// gestionale non sapra' di riaprire l'ordine: la missione e' un deposito in memoria, e la sua
    /// prossima scrittura la porta nel database senza il fatto.
    /// </remarks>
    private static async Task WaitForThePickedExtractionReportAsync(PathMission mission)
    {
        if (mission.TakeThePickedExtractionReport() is not { } written) return;

        try
        {
            await written;
        }
        catch (Exception ex)
        {
            Navigator.WriteLog(
                $"Missione {mission.MissionId}, UDC {mission.LuId:0}: l'estrazione gia' prelevata e' diventata un deposito, ma la riga con il fatto per il gestionale non e' stata scritta nel database del WCS ({ex.Message}): il gestionale non riaprira' l'ordine, che va sistemato a mano",
                LogEntryType.Error);
        }
    }

    /// <summary>Il motivo del fatto che rimette da servire la riga di estrazione del cambio cella.</summary>
    private const string ChangeOfCellReopenReason =
        "The operator sent the load unit, still in its cell, to another storage before the pick-up: the extraction became a change of cell";

    /// <summary>
    /// La riga della coda di estrazione del WCS si cancella dopo le segnalazioni del cambio cella,
    /// arrivate tutte o in parte (<see cref="LeaveTheExtractionAsync"/>), e la riga di estrazione per
    /// UDC che serviva torna da servire (voce 45, compito 4). Non solleva: un guasto si registra, e
    /// riga di coda e riga di estrazione vanno sistemate a mano.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>La riga di estrazione</b> (decisione 3 del proprietario del 10/10). Il gestionale l'aveva
    /// messa «in estrazione» quando il motore ha costruito la coda, e le segnalazioni del cambio cella
    /// non la toccano: senza una coda che la serva restava ferma, e teneva aperta la lavorazione. Il
    /// fatto che la rimette da servire e' quello dell'estrazione prelevata diventata un deposito
    /// (<see cref="IWarehouse.ReportPickedExtractionBecameDepositAsync"/>, decisione A dell'11/10), con
    /// la sola riga di estrazione: l'assegnazione l'hanno gia' lasciata le segnalazioni. Va nella coda
    /// durevole nella stessa scrittura che cancella la riga di coda
    /// (<c>ORM.PickedExtractionReportOutbox.LeaveTheExtractionQueueAsync</c>), e il dispatcher lo manda
    /// quando nessuna missione porta piu' l'UDC: a cambio cella finito, o subito se la missione si
    /// chiude (<see cref="PendingPickedExtractionReportDispatcher"/>). Qui nessuna chiamata al
    /// gestionale.
    /// </para>
    /// </remarks>
    private static async Task DeleteTheQueueRowAsync(PathMission mission, decimal queueRow)
    {
        if (queueRow <= 0) return;

        try
        {
            await ORM.PickedExtractionReportOutbox.LeaveTheExtractionQueueAsync((long)queueRow,
                extractionRowId => new Persistence.Entities.PendingPickedExtractionReport
                {
                    OperationId = Guid.NewGuid(),
                    MissionId = mission.MissionId,
                    LoadUnitId = mission.LuId,
                    AssignmentId = 0m,
                    AssignmentGuid = Guid.Empty,
                    ExtractionRowId = extractionRowId,
                    UserId = Navigator.UserId,
                    Reason = ChangeOfCellReopenReason,
                    CreatedAtUtc = DateTimeOffset.UtcNow
                });
        }
        catch (Exception ex)
        {
            Navigator.WriteLog(
                $"Missione {mission.MissionId}: la riga {queueRow} della coda di estrazione non e' stata cancellata dopo le segnalazioni del cambio cella, e va tolta a mano; la riga di estrazione che serviva, se c'era, non tornera' da servire da sola, e va rimessa da servire nel gestionale: {ex.Message}",
                LogEntryType.Error);
        }
    }

    /// <summary>
    /// Il cambio cella di un'estrazione e' stato rifiutato dopo che il gestionale aveva saputo che la
    /// missione lascia estrazione e assegnazione (<see cref="LeaveTheExtractionAsync"/>): la missione si
    /// chiude, e l'UDC resta nella sua cella senza ordine (risposta del proprietario del 2/10 sulla
    /// domanda del compito 4). Per il gestionale non e' piu' un'estrazione, e il cambio cella non c'e'.
    /// </summary>
    /// <param name="why">Perche' il cambio e' stato rifiutato, per la riga di registro.</param>
    /// <returns>
    /// Vero se la missione non e' piu' fra le attive; falso se la rimozione e' stata rifiutata (uno
    /// step in volo) o e' caduta.
    /// </returns>
    /// <remarks>
    /// <para>
    /// La riga della coda di estrazione e' gia' cancellata, con le segnalazioni. Estrazione e
    /// assegnazione si staccano anche dalla missione prima di toglierla: la rimozione le segnalerebbe
    /// di nuovo se intanto l'operatore l'avesse spostata su un dispositivo. Chi l'aveva presa in carico
    /// per prelevarla - il ricollocatore fra l'attivazione e il prelievo (<c>LUtoPick</c>), la coda di
    /// prelievo dopo un riavvio, o il gruppo che l'aveva presa con altre (<c>LUsToPick</c>) - la lascia
    /// (<c>AutomationDevice.LetGoOfLuToPickUp</c>), e la coda delle rotte da costruire la perde:
    /// prelevata, l'UDC uscirebbe dalla cella per una missione che non c'e' piu'.
    /// </para>
    /// <para>
    /// <b>La missione chiusa non torna</b> (<c>PathMission.Withdraw</c>, voce 39, compito 4, primo
    /// giro di correzioni). Il cambio e' ancora cominciato, e nessuno avvia un suo step mentre si
    /// chiude; ma chi l'aveva gia' presa dalla coda di prelievo prima della chiusura la tiene in mano
    /// mentre chiede al gestionale se l'UDC e' li' (<c>VerifyPickUp</c>), e dopo la rimette fra le
    /// attive e comincia il prelievo, a cambio finito: un'estrazione verso la baia senza riga di coda
    /// ne' assegnazione, con la sua riga di nuovo nel database del WCS. Il segno si alza per primo, e
    /// da li' le attive non la riprendono e nessun dispositivo avvia un suo step, per sempre; poi la
    /// presa in carico si lascia, prima della rimozione, che aspetta il gestionale.
    /// </para>
    /// <para>
    /// La rimozione restituisce al gestionale la prenotazione di deposito che la missione avesse
    /// ancora, a miglior sforzo e fuori da ogni lucchetto (<c>ActiveMissions.Remove</c>). Se la
    /// rifiuta (uno step in volo) la missione resta fra le attive, chiusa: nessuno la muove piu', e va
    /// tolta a mano, come dice la riga di registro. Non solleva: chiamata anche da un <c>catch</c>, un
    /// guasto qui coprirebbe quello vero.
    /// </para>
    /// </remarks>
    private static async Task<bool> CloseTheMissionAsync(PathMission mission, string why)
    {
        try
        {
            if (!ReferenceEquals(ActiveMissions.Instance[mission.MissionId], mission)) return true;

            mission.Withdraw();
            mission.LeaveTheOrder();

            // Un dispositivo che non la lascia (la sua lista cambiata dal ciclo del gruppo nel mezzo)
            // non ferma la chiusura: la missione e' gia' ritirata, e restare attiva senza ordine sarebbe
            // peggio. Prima un'eccezione qui la lasciava ritirata e attiva, con estrazione e assegnazione
            // gia' azzerate sul database del WCS (revisione finale della voce 39).
            foreach (var device in PathDevices.Instance.List)
            {
                try
                {
                    device.LetGoOfLuToPickUp(mission);
                }
                catch (Exception ex)
                {
                    Navigator.WriteLog(
                        $"Missione {mission.MissionId}: chiudendola, il dispositivo {device.ItemGuid} non l'ha lasciata ({ex.Message}); la chiusura prosegue",
                        LogEntryType.Warning);
                }
            }
            RoutesToBuild.Instance.Remove(mission.MissionId);

            if (!await ActiveMissions.Instance.Remove(mission))
            {
                Navigator.WriteLog(
                    $"Missione {mission.MissionId}: il cambio cella e' stato rifiutato ({why}) dopo le segnalazioni al gestionale, e la missione non si e' potuta togliere dalle attive: e' chiusa, nessun dispositivo la muove piu', senza estrazione ne' assegnazione, da togliere a mano",
                    LogEntryType.Warning);
                return false;
            }

            Navigator.WriteLog(
                $"Missione {mission.MissionId}: il cambio cella e' stato rifiutato ({why}) dopo che il gestionale aveva saputo che la missione lascia l'assegnazione e che l'UDC esce dalla coda di estrazione: la missione e' chiusa, e l'UDC {mission.LuId} resta nella sua cella senza ordine",
                LogEntryType.Warning);
            return true;
        }
        catch (Exception ex)
        {
            Navigator.WriteLog(
                $"Missione {mission.MissionId}: il cambio cella e' stato rifiutato ({why}) dopo le segnalazioni al gestionale, e la chiusura della missione e' caduta: {ex.Message}\r\nStackTrace: {ex.StackTrace}",
                LogEntryType.Warning);
            return false;
        }
    }

    /// <summary>
    /// La cella di una missione che l'operatore ha mandato in un magazzino, che il motore ha prenotato
    /// per lei (<c>PathMission.StorageCellReserved</c>), o che ha perso la sua
    /// (<c>PathMission.StorageCellLost</c>), vista dal giro delle rotte del navigatore (decisioni del
    /// proprietario dell'1/10 e risposta 4 del 2/10): se la missione non la tiene piu' - persa dopo un
    /// cambio rifiutato (<see cref="LoseTheCell"/>), o tolta in un altro modo dal gestionale, scaduta
    /// o revocata - ne prenota un'altra nel magazzino della sua destinazione, con lo stesso flusso del
    /// cambio (<see cref="ReserveACellAndCommitAsync"/>), e rifa' la rotta verso la sua porta. Se la
    /// tiene, lo conferma per il deposito (<c>PathMission.MayStartTheDeposit</c>). Se il magazzino non
    /// ha una cella per lei, una missione della configurazione la cerca negli altri magazzini del suo
    /// ricollocatore, e senza va allo scarto (<see cref="AnotherStorageOrTheRejectionAsync"/>, risposta
    /// 5 del proprietario del 2/10); una missione dell'operatore aspetta nel magazzino che ha scelto.
    /// </summary>
    /// <returns>
    /// <c>null</c> se qui non c'e' niente da fare, e il navigatore prosegue come sempre
    /// (<c>Navigator.FindWhereStoreLu</c>): la missione non va in un magazzino scelto dall'operatore,
    /// non ha una cella prenotata per lei e non l'ha persa, o e' una copia anche con il segno
    /// (<see cref="IsACopy"/>), o la cella e' ancora sua e la missione e'
    /// dell'operatore, o il suo prossimo passo non e' il deposito.
    /// <see cref="AutomationDevice.RouteBuildStatusTypes.Available"/> per una missione della
    /// configurazione la cui cella e' ancora sua e il cui prossimo passo, da dove e', e' il deposito
    /// (<see cref="TheDepositIsTheNextStep"/>): confermata per il deposito, e niente da rifare.
    /// Altrimenti come e' andata: la rotta verso la cella nuova, il rigetto, o nessuna rotta, e il
    /// navigatore riprova con il suo freno. Nessuna rotta anche quando
    /// il gestionale non dice se la cella della porta e' ancora della missione (non risponde, o non e'
    /// configurato) e il prossimo passo e' il deposito: porta e rotta restano, il deposito aspetta, e
    /// la domanda si rifa' con il freno invece che a ogni giro; lontano dal deposito, <c>null</c>
    /// (<see cref="CellNotConfirmed"/>).
    /// </returns>
    /// <remarks>
    /// <para>
    /// Chiedere la prenotazione al gestionale la rinnova, ed e' il modo in cui il motore si accorge
    /// che la cella non e' piu' della missione (<see cref="HoldsItsCellAsync"/>). La porta che non e'
    /// piu' sua si lascia subito, prima di prenotare: qualunque cosa succeda dopo, la missione non ci
    /// va, e senza porta nessuno le costruisce una rotta verso il magazzino nudo
    /// (<c>PathMission.BuildPathToDestination</c>). La cella si cerca nel magazzino della
    /// destinazione (o nel suo gruppo), con il gateway della missione, escluse soltanto le celle che il
    /// PLC le ha rifiutato al deposito (<c>PathMission.CellsRefusedByThePlc</c>).
    /// </para>
    /// <para>
    /// Le missioni del gruppo che ne hanno copiato la porta (la ricerca per tipo del gruppo) lasciano la
    /// porta vecchia con lei, ferme come lei finche' non ha la cella nuova, e le rifa' la ricerca per tipo
    /// del gruppo (risposta 8 del proprietario dell'8/10, <see cref="StopTheCopiesOfTheDoor"/>): nessuna
    /// copia deposita da sola nella cella persa. Se una copia non si puo' fermare, non si tocca niente e
    /// il navigatore riprova con il freno.
    /// </para>
    /// <para>
    /// Vale per ogni destinazione: la cella persa di una destinazione della configurazione si rifa'
    /// qui come quella dell'operatore, e la destinazione resta della configurazione (il segno resta
    /// quello che la missione ha). Non la rifa' il giro della configurazione del dispositivo di
    /// posizione, che con la modalita' <c>No</c> e senza destinazione fissa costruirebbe la rotta
    /// verso il magazzino nudo. Le due si separano solo quando il magazzino non ha una cella: la
    /// missione della configurazione va altrove o allo scarto, quella dell'operatore aspetta.
    /// </para>
    /// <para>
    /// Il deposito di una missione dell'operatore, o con una cella prenotata per lei, aspetta questa
    /// domanda (<c>PathMission.MayStartTheDeposit</c>): una missione con la sua rotta puo' arrivare al
    /// deposito senza ripassare dal giro delle rotte, e una prenotazione scaduta mentre viaggiava si
    /// vede solo qui. Una missione della configurazione ci passa anche quando un dispositivo la manda
    /// al giro per un altro motivo: senza step da fare, o con la sua rotta, da una stazione di lettura
    /// con la modalita' di deposito o la destinazione fissa che ne legge l'UDC
    /// (<c>AutomationDeviceConnectionManager.ProcessDeviceTypeSpecificLogic</c>). La cella si guarda
    /// prima, e la configurazione della posizione poi gira come sempre; solo dove il prossimo passo e'
    /// il deposito non gira, e la rotta resta com'e'.
    /// La conferma vale per la posizione in cui la missione e' adesso e per un giro di deposito solo
    /// (<c>PathMission.TakeStorageCellConfirmation</c>); la usa il giro dopo del dispositivo di
    /// posizione, che e' collegato al magazzino e lo richiama il suo ciclo (il risveglio del giro
    /// delle rotte non lo muove: <c>AutoExecuteStep</c> e' falso per lui).
    /// </para>
    /// <para>
    /// Gira nel lavoro della ricerca della cella del giro delle rotte (<c>Navigator.StartCellSearch</c>),
    /// che tiene il posto delle ricerche della missione (nessun cambio dell'operatore comincia intanto)
    /// e nessun semaforo: le chiamate al gestionale stanno fuori da ogni lucchetto. La missione si
    /// ferma come per un cambio (<c>PathMission.TryBeginOperatorChange</c>): nessuno avvia uno step
    /// della rotta vecchia mentre il gestionale risponde, e nessuna ricerca di un dispositivo prenota
    /// per lei. Il commit e' quello del cambio, con il confronto della versione della rotta; il
    /// risveglio del dispositivo di posizione lo fa il giro delle rotte, come dopo ogni rotta, per
    /// chi si avvia da se'.
    /// </para>
    /// </remarks>
    internal static async Task<AutomationDevice.RouteBuildStatusTypes?> KeepTheStorageCellAsync(PathMission mission)
    {
        // Una copia a cui l'operatore ha cambiato solo il gateway ne ha il segno, ma resta una copia
        // (IsACopy, risposta del 9/10 alla seconda domanda del compito 17): la cella non e' sua, e il
        // gestionale non la tiene per lei. Prima il navigatore gliela trovava «non piu' sua», le toglieva la
        // porta con le copie vere della stessa porta (StopTheCopiesOfTheDoor), che la cella la tenevano
        // ancora con la missione che l'ha prenotata, e le prenotava una cella sua. E' la regola del
        // deposito che chiede la cella (PathMission.TheDepositAsksForTheCell), piu' la cella persa, che il
        // navigatore rifa' per ogni missione: una copia non e' mai prenotata (IsACopy), e le due forme
        // scritte a mano prima di questa coincidevano.
        if (!mission.TheDepositAsksForTheCell && !mission.StorageCellLost ||
            mission.DestinationItem is not { DeviceType: DeviceTypes.Storage } storage)
            return null;

        var origin = mission.PositionItemId;

        // La cella della porta che il PLC ha rifiutato a questa missione non si conferma: resta esclusa
        // finche' l'UDC non deposita (risposta 7 del proprietario dell'8/10 sera), anche se il gestionale
        // la tiene ancora per lei. Si fa il flusso intero, che lascia la porta e ne prenota un'altra con le
        // rifiutate escluse. Ci arriva la missione rimasta sulla porta rifiutata: dopo il gestionale muto,
        // quando non si e' potuta fermare per lasciarla (LeaveTheRefusedCell), e dopo il quarto rifiuto
        // uguale, che non cerca. Prima la conferma passava, e il deposito tornava nella cella rifiutata
        // (revisione finale del terzo giro).
        if (!string.IsNullOrEmpty(mission.StorageDoorCode) &&
            !await TheDoorIsACellRefusedByThePlcAsync(mission))
        {
            try
            {
                if (Navigator.Warehouse is not { } warehouse)
                    return CellNotConfirmed(mission, origin, "il gestionale non e' configurato");

                // La porta che il gestionale conferma, letta prima di chiedere: la conferma vale per lei.
                var asked = mission.StorageDoorCode;

                if (await HoldsItsCellAsync(warehouse, mission))
                {
                    mission.StorageCellLost = false;
                    mission.ConfirmStorageCell(origin, asked);

                    // Una missione della configurazione il cui prossimo passo, da dove e', e' il
                    // deposito (di solito arriva qui perche' il deposito l'ha mandata al navigatore,
                    // ma conta solo il passo) aspetta solo la conferma
                    // (PathMission.MayStartTheDeposit): la rotta resta, e la configurazione di chi
                    // deposita non si rifa'. Rifarla cercherebbe di nuovo la cella con la sua modalita'
                    // di deposito, o la destinazione fissa, dove prima il deposito partiva e basta.
                    // Altrove la configurazione della posizione gira come prima del controllo: una
                    // stazione di lettura a monte, con la modalita' di deposito o la destinazione fissa,
                    // rimanda al giro la missione con la sua rotta quando ne legge l'UDC
                    // (ProcessDeviceTypeSpecificLogic), e la sua ricerca puo' lasciare la cella (il
                    // ricollocatore fuori linea, o al suo limite di depositi) o mandare la missione alla
                    // destinazione fissa. Cosi' anche senza rotta, e per la missione dell'operatore (che
                    // il giro manda dritta alla sua destinazione).
                    return !mission.OperatorDestination && TheDepositIsTheNextStep(mission, origin)
                        ? AutomationDevice.RouteBuildStatusTypes.Available
                        : null;
                }
            }
            catch (WarehouseUnavailableException ex)
            {
                return CellNotConfirmed(mission, origin, ex.Message);
            }
        }

        // Il segno si legge una volta, prima di fermare la missione: mentre il navigatore tiene il
        // posto delle ricerche nessun cambio dell'operatore lo puo' alzare o abbassare.
        var operatorChoice = mission.OperatorDestination;

        if (mission.TryBeginOperatorChange() is var start and not PathMission.OperatorChangeStart.Begun)
        {
            Navigator.WriteLog(
                $"Missione {mission.MissionId}: la cella in {storage.ItemGuid} non e' confermata, e il navigatore la riguarda al prossimo giro ({start})",
                LogEntryType.Information);
            return AutomationDevice.RouteBuildStatusTypes.NotAvailable;
        }

        DestinationChange change;

        // Le copie della porta, ferme con la missione (StopTheCopiesOfTheDoor): si lasciano dopo di lei.
        List<PathMission>? held = null;

        try
        {
            // La missione ferma, si riguarda che sia ancora quella di prima: tolta intanto, non le si
            // toglie ne' prenota niente (le scritture della porta ne rifarebbero la riga sul database
            // del WCS); con la destinazione cambiata intanto per un'altra via, qui non c'e' piu'
            // niente da fare e il giro prosegue come sempre.
            if (!ReferenceEquals(ActiveMissions.Instance[mission.MissionId], mission))
            {
                change = DestinationChange.Refused(MissionDestinationOutcome.MissionNotFound, "la missione non e' piu' attiva");
            }
            else if (mission.DestinationItem != storage)
            {
                return null;
            }
            else if (mission.StepInFlight() is { } inFlight)
            {
                change = DestinationChange.Refused(MissionDestinationOutcome.StepInFlight,
                    $"lo step {inFlight.StepId} e' in volo ({inFlight.FromNodeId} -> {inFlight.ToNodeId})");
            }
            else
            {
                var lost = mission.StorageDoorCode;

                if (StopTheCopiesOfTheDoor(mission, lost, out var notStopped) is not { } copies)
                {
                    // Lo stesso esito del cambio dell'operatore rifiutato per lo stesso motivo: la riga del
                    // registro dice CopyInFlight, non StepInFlight, che e' lo step della missione stessa.
                    change = DestinationChange.Refused(MissionDestinationOutcome.CopyInFlight,
                        $"la cella della porta {lost} non e' piu' prenotata, ma {notStopped}: porta e rotta restano, il navigatore la riguarda al prossimo giro");
                }
                else
                {
                    held = copies;

                    // Prima le copie, poi la missione: sono scritture separate della riga di ognuna, e se
                    // l'host si ferma fra le due la ricarica deve poter convergere. Con le copie gia' senza
                    // porta e con il segno, e la missione ancora con la porta, il giro del gruppo rifa' le
                    // copie e il navigatore, che rivede persa la cella della missione, ne ferma di nuovo le
                    // copie con lei. Nell'ordine opposto la missione tornava senza porta e le copie con la
                    // porta persa e senza segno: nessuna missione aveva piu' quella porta per trattenerle.
                    // Lo step in volo della missione si e' guardato qui sopra con lei ferma: se parte lo
                    // stesso, le copie senza porta le rifa' il giro del gruppo con la porta che lei tiene.
                    LeaveTheCopiedDoor(mission, copies, lost ?? string.Empty);

                    if (!mission.DropStorageCell())
                    {
                        change = DestinationChange.Refused(MissionDestinationOutcome.StepInFlight,
                            "uno step della missione e' partito: porta e rotta restano");
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(lost))
                            Navigator.WriteLog(
                                $"Missione {mission.MissionId}: la cella della porta {lost} non e' piu' prenotata dal gestionale: porta, indirizzo e rotta tolti",
                                LogEntryType.Warning);

                        // Le celle che il PLC le ha rifiutato restano escluse (risposta 7 del proprietario
                        // dell'8/10 sera): la cella persa dopo il gestionale muto non torna quella rifiutata.
                        change = await ReserveACellAndCommitAsync(mission, origin, storage, StoragesOf(storage),
                            mission.Gateway, mission.CellsRefusedByThePlc, new ReservationProgress(), operatorChoice,
                            changeOfCell: false);
                    }
                }
            }
        }
        finally
        {
            mission.EndOperatorChange();

            // Le copie restano ferme finche' la missione non ha la cella nuova, e ripartono dopo di lei:
            // il giro del gruppo che arriva intanto non le conta, e non le rifa' senza la missione da
            // cui prendere il selettore. Prima ripartivano appena lasciata la porta, durante la
            // domanda al gestionale e il commit della missione, ancora ferma: il giro del gruppo che
            // cadeva li' - il caso solito sul campo, perche' il ciclo del gruppo passa ogni
            // TimeLapseToExecuteStep - contava la copia e non la missione, e la copia si prenotava una
            // cella sua e depositava da sola (terzo giro di correzioni del compito 12). Ferme non
            // costano niente: senza porta non hanno un deposito da fare. Dopo la missione e non prima:
            // con la missione contata e la copia ancora ferma il giro del gruppo non deposita, perche'
            // la copia con il segno lo manda alla ricerca (AutomationDeviceGroupManager.GroupExecuteStep).
            if (held != null)
                foreach (var copy in held)
                    copy.EndOperatorChange();
        }

        if (change.Result.Outcome == MissionDestinationOutcome.Changed)
        {
            // La cella e' appena stata prenotata: il deposito da qui la puo' usare.
            mission.ConfirmStorageCell(origin);
            Navigator.WriteLog(
                $"Missione {mission.MissionId}: il navigatore le ha prenotato la cella della porta {mission.StorageDoorCode} in {mission.DestinationItemId}, rotta di {mission.MissionSteps.Count} passi",
                LogEntryType.Information);
            return AutomationDevice.RouteBuildStatusTypes.Available;
        }

        Navigator.WriteLog(
            change.Reason == null
                ? $"Missione {mission.MissionId}: il navigatore non le ha rifatto la cella in {storage.ItemGuid} ({change.Result.Outcome})"
                : $"Missione {mission.MissionId}: il navigatore non le ha rifatto la cella in {storage.ItemGuid} ({change.Result.Outcome}): {change.Reason}",
            change.ReasonLevel);

        // Una missione della configurazione senza una cella nel magazzino della sua destinazione la
        // cerca altrove, e senza va allo scarto (risposta 5 del proprietario del 2/10). Quella
        // dell'operatore aspetta con il freno, nel magazzino che ha scelto lui; e senza percorso o con
        // il gestionale che non risponde aspettano tutte e due.
        if (!operatorChoice && change.Result.Outcome == MissionDestinationOutcome.NoStorageCell)
            return await AnotherStorageOrTheRejectionAsync(mission, origin, storage);

        return AutomationDevice.RouteBuildStatusTypes.NotAvailable;
    }

    /// <summary>
    /// La cella persa di una missione della configurazione, e il magazzino della destinazione che non
    /// ha una cella per lei (risposta 5 del proprietario del 2/10, che sostituisce la decisione 7): la
    /// cella si cerca negli altri magazzini del ricollocatore che fa il deposito, e se non c'e' la
    /// missione va allo scarto configurato; senza un punto di scarto aspetta e ritenta con il freno,
    /// con una riga di avviso.
    /// </summary>
    /// <param name="storage">Il magazzino della destinazione, senza una cella per la missione.</param>
    /// <returns>
    /// La rotta verso la cella nuova; il rigetto (<see cref="AutomationDevice.RouteBuildStatusTypes.Rejected"/>,
    /// come nel flusso del deposito); o nessuna rotta, e il navigatore riprova con il suo freno.
    /// </returns>
    /// <remarks>
    /// <para>
    /// «Nessuna cella» e' l'esito <see cref="MissionDestinationOutcome.NoStorageCell"/> del flusso del
    /// magazzino: nessuna cella libera, l'UDC senza riferimenti o senza l'identificativo del
    /// gestionale, una porta non mappata, una cella senza magazzino del gestionale, la cella proposta
    /// fuori dal magazzino. Sono le cause per cui il flusso del deposito rigetta. Senza percorso verso
    /// il magazzino, o con il gestionale che non risponde, non si arriva qui.
    /// </para>
    /// <para>
    /// La cella altrove e' quella che il ricollocatore della destinazione cerca per se'
    /// (<c>PathMission.StoringSelectionTypes.StoreInLinkedCell</c>: le celle collegate a lui, nel
    /// magazzino della sua cella collegata, nei magazzini di cui e' il ricollocatore), ancorata a lui
    /// ovunque sia la missione: una missione ancora a monte, sull'ingresso, va in un altro magazzino di
    /// quel ricollocatore passando da lui, e la rotta dalla posizione alla porta si guarda davvero
    /// (<c>PathMission.GetStoreToWarehouse</c>, con chi deposita). La destinazione diventa quel
    /// magazzino, con porta, cella e rotta; la cella persa cade. Un magazzino senza ricollocatore non
    /// ne ha altri: si va allo scarto. Per un ricollocatore di gruppo con la missione a monte la ricerca
    /// si fa a turno, con la cella collegata del gruppo se ne ha una e poi con quella di ogni membro
    /// (<see cref="DepositorsOf"/>, risposta 5 del proprietario dell'8/10): vince il primo turno che da'
    /// la cella con la rotta, ogni turno che non la da' restituisce la cella che ha preso, e lo scarto
    /// viene solo se nessun turno trova posto, con la causa scritta dall'ultimo.
    /// </para>
    /// <para>
    /// Se la ricerca non da' la cella con la rotta, la cella che ha preso torna al gestionale e la
    /// missione resta (o torna) verso il suo magazzino con la cella persa (<see cref="BackToTheLostCellAsync"/>),
    /// prima di decidere fra lo scarto e l'attesa: senza, una porta scartata dopo la prenotazione
    /// lasciava la missione verso un magazzino senza rotta e fuori dalla cella persa, e il giro dopo non
    /// riprovava piu' il suo magazzino. Il gestionale che non risponde alla ricerca non e' «nessuna
    /// cella»: la missione aspetta con il freno, senza scarto.
    /// </para>
    /// <para>
    /// Lo scarto e' quello del flusso del deposito (<c>Navigator.RejectUnlessChosenByOperator</c>, cioe'
    /// <c>PathMission.RejectTo</c> per mancanza di celle): il punto di scarto del gruppo del dispositivo
    /// di posizione o del dispositivo, qualunque sia la sua modalita' di deposito, con la causa scritta
    /// prima, perche' il comando della missione si chiude con lei. Il punto di scarto si guarda prima
    /// (<c>PathMission.NoStoreCellRejectionPoint</c>): senza, <c>RejectTo</c> non fa niente e la ricerca
    /// risponderebbe «rigettata», cioe' la missione resterebbe senza rotta e fuori dalla coda.
    /// </para>
    /// <para>
    /// Gira dopo la fine del cambio cominciato (<c>PathMission.EndOperatorChange</c>): la ricerca entra
    /// nell'imbuto delle ricerche della cella, che con il cambio cominciato aspetterebbe la sua fine,
    /// cioe' se stessa. Il navigatore tiene ancora il posto delle ricerche della missione e nessun cambio
    /// dell'operatore comincia intanto: e' la situazione in cui il flusso del deposito cerca e rigetta.
    /// Fuori da ogni lucchetto e semaforo, come le sue chiamate al gestionale.
    /// </para>
    /// </remarks>
    private static async Task<AutomationDevice.RouteBuildStatusTypes> AnotherStorageOrTheRejectionAsync(
        PathMission mission, Guid origin, AutomationDevice storage)
    {
        // Tolta, spostata o partita dopo la fine del cambio: non le si prenota niente, e il navigatore
        // la riguarda quando torna in coda.
        if (StillWhereTheChangeStarted(mission, origin) is not null || PositionOf(mission, origin) is not { } position)
        {
            Navigator.WriteLog(
                $"Missione {mission.MissionId}: nessuna cella in {storage.ItemGuid}, e la missione non e' piu' dov'era: niente altrove ne' scarto per adesso",
                LogEntryType.Information);
            return AutomationDevice.RouteBuildStatusTypes.NotAvailable;
        }

        // La causa con cui lo scarto chiude il comando: quella che la ricerca altrove ha scritto, se ne
        // ha scritta una (l'UDC senza riferimenti, il ricollocatore senza cella collegata...), o la
        // mancanza di celle. Scritta sopra, la causa precisa spariva prima che una riga la leggesse.
        string? cause = null;

        // Chi deposita, a turno: il ricollocatore, o per un gruppo la sua cella collegata e poi ogni
        // membro (risposta 5 del proprietario dell'8/10). Vince il primo che da' una cella con la rotta.
        var depositors = DepositorsOf(storage, position);

        for (var turn = 0; turn < depositors.Count; turn++)
        {
            var depositor = depositors[turn];

            // Dal secondo turno: tolta, spostata o partita mentre il gestionale rispondeva al turno di
            // prima, come in cima.
            if (turn > 0 && StillWhereTheChangeStarted(mission, origin) is not null)
            {
                Navigator.WriteLog(
                    $"Missione {mission.MissionId}: nessuna cella in {storage.ItemGuid}, e la missione non e' piu' dov'era: gli altri membri del gruppo non si provano per adesso",
                    LogEntryType.Information);
                return AutomationDevice.RouteBuildStatusTypes.NotAvailable;
            }

            bool found;
            PathMission.RouteWriteOutcome? routeNotWritten;
            try
            {
                (found, cause, routeNotWritten) = await mission.SearchTheStoreAsync(PathMission.StoringSelectionTypes.StoreInLinkedCell,
                    keepExistingReservation: false, depositor);
            }
            catch (WarehouseUnavailableException ex)
            {
                // Il gestionale che non risponde non e' «nessuna cella»: niente scarto, il freno.
                await BackToTheLostCellAsync(mission, storage, "il gestionale non risponde");
                var failure = Navigator.RoutePathRetries.Failures(mission.MissionId) + 1;
                Navigator.WriteLog(
                    $"Missione {mission.MissionId}: nessuna cella in {storage.ItemGuid}, e il gestionale non risponde alla ricerca negli altri magazzini per {depositor.ItemGuid}, che fa il deposito ({ex.Message}): la missione aspetta e ritenta con il freno (fallimento {failure})",
                    Navigator.IsABrakeThreshold(failure) ? LogEntryType.Warning : LogEntryType.Information);
                return AutomationDevice.RouteBuildStatusTypes.NotAvailable;
            }
            catch
            {
                await BackToTheLostCellAsync(mission, storage, "errore durante la ricerca");
                throw;
            }

            if (found && !StillActive(mission))
            {
                // Tolta mentre il gestionale rispondeva: la cella che la ricerca ha preso torna al
                // gestionale, e la missione non si tocca piu' (BackToTheLostCellAsync).
                await BackToTheLostCellAsync(mission, storage, "la missione non e' piu' attiva");
                return AutomationDevice.RouteBuildStatusTypes.NotAvailable;
            }

            if (routeNotWritten != null)
            {
                // La cella era presa e la porta scritta, ma la rotta verso di lei l'ha cambiata un altro
                // mentre la si scriveva (voce 41): la missione e' gia' in coda al giro delle rotte, con lo
                // stato di chi ha vinto. La ricerca che ha perso ha gia' restituito la cella, tolto la sua
                // porta e contato il fallimento nel freno (PathMission.GiveBackTheCellOfALostSearchAsync,
                // risposta 6 del proprietario dell'11/10). Niente ritorno alla cella persa (scriverebbe
                // sopra chi ha vinto), niente altro turno ne' scarto. Il segno della cella persa non si
                // abbassa qui: lo abbassa da se' la destinazione nuova di chi ha vinto (setter di
                // DestinationItemId); con la destinazione ancora nel magazzino resta, e senza porta il giro
                // dopo le prenota un'altra cella. Prima la cella restava sua, e qui il segno si abbassava:
                // tolta la porta, la missione sarebbe andata verso il magazzino nudo. Lo stesso se la rotta
                // non si e' scritta perche' la missione aspetta il rigetto dal campo rimandato
                // (AwaitingRejection), con la riga che lo dice e niente contato nel freno: nessuno ha
                // cambiato la rotta (secondo giro di correzioni del compito 8 della voce 41).
                Navigator.WriteLog(
                    routeNotWritten == PathMission.RouteWriteOutcome.AwaitingRejection
                        ? $"Missione {mission.MissionId}: in {storage.ItemGuid} non c'e' una cella, e quella trovata per {depositor.ItemGuid}, che fa il deposito, e' tornata al gestionale: la missione aspetta il rigetto dal campo, che il giro delle rotte rifa'"
                        : $"Missione {mission.MissionId}: in {storage.ItemGuid} non c'e' una cella, e quella trovata per {depositor.ItemGuid}, che fa il deposito, e' tornata al gestionale: la rotta l'ha cambiata un altro intanto, e la rifa' il giro delle rotte",
                    LogEntryType.Information);
                return AutomationDevice.RouteBuildStatusTypes.TakenOver;
            }

            if (found)
            {
                // La cella e' appena stata prenotata: il deposito da qui la puo' usare, e la missione non
                // aspetta piu' una cella (la destinazione nuova la toglie gia', lo stesso magazzino no).
                mission.StorageCellLost = false;
                mission.ConfirmStorageCell(origin);
                Navigator.WriteLog(
                    $"Missione {mission.MissionId}: in {storage.ItemGuid} non c'e' una cella, e il navigatore le ha prenotato quella della porta {mission.StorageDoorCode} in {mission.DestinationItemId}, cercata per {depositor.ItemGuid}, che fa il deposito: rotta di {mission.MissionSteps.Count} passi",
                    LogEntryType.Information);
                return AutomationDevice.RouteBuildStatusTypes.Available;
            }

            // Ogni turno che ha prenotato e poi scartato restituisce la cella, anche l'ultimo: il turno
            // dopo cerca con la missione com'era, e senza altri turni nessuna cella resta appesa.
            await BackToTheLostCellAsync(mission, storage, $"nessuna cella altrove con {depositor.ItemGuid}");

            if (!StillActive(mission))
                break;
        }

        // Tolta mentre il gestionale rispondeva: ogni scrittura (la causa, lo scarto) ne rifarebbe la
        // riga sul database del WCS, come sa BackToTheLostCellAsync.
        if (!StillActive(mission))
            return AutomationDevice.RouteBuildStatusTypes.NotAvailable;

        cause ??= PathMission.NoStorageCellAvailable;
        var rejectionPoint = mission.NoStoreCellRejectionPoint();

        // Un punto di scarto che da qui non si raggiunge e' come nessuno: RejectTo chiuderebbe il
        // comando e riscriverebbe la destinazione prima di accorgersene, e la missione resterebbe
        // senza rotta e fuori dalla coda. Questa via ci arriva anche da posizioni che prima non
        // rigettavano mai (un ingresso a monte, un dispositivo in modalita' No).
        var unreachable = rejectionPoint != Guid.Empty && mission.ProbeRouteSteps(rejectionPoint).Count == 0;

        if (rejectionPoint == Guid.Empty || unreachable)
        {
            // Una riga di avviso (risposta 5 del 2/10), non una per giro del freno: al primo
            // fallimento e alle soglie del freno, come RiaccodaConAttesaCrescente. Il fallimento di
            // questo giro lo conta il navigatore dopo che la ricerca ha risposto.
            var failure = Navigator.RoutePathRetries.Failures(mission.MissionId) + 1;
            Navigator.WriteLog(
                unreachable
                    ? $"Missione {mission.MissionId}: nessuna cella in {storage.ItemGuid} ne' negli altri magazzini del suo ricollocatore ({cause}), e da {position.ItemGuid} non c'e' un percorso verso il punto di scarto {rejectionPoint}: la missione aspetta e ritenta con il freno (fallimento {failure})"
                    : $"Missione {mission.MissionId}: nessuna cella in {storage.ItemGuid} ne' negli altri magazzini del suo ricollocatore ({cause}), e nessun punto di scarto configurato per {position.ItemGuid} o per il suo gruppo: la missione aspetta e ritenta con il freno (fallimento {failure})",
                Navigator.IsABrakeThreshold(failure) ? LogEntryType.Warning : LogEntryType.Information);
            return AutomationDevice.RouteBuildStatusTypes.NotAvailable;
        }

        // La causa con cui lo scarto chiude il comando della missione, come nel flusso del deposito.
        mission.StatusDescription = cause;

        var rejected = await Navigator.RejectUnlessChosenByOperator(mission, position);
        Navigator.WriteLog(
            mission.MissionType == MissionTypes.Rejection && mission.DestinationItemId == rejectionPoint
                ? $"Missione {mission.MissionId}: nessuna cella in {storage.ItemGuid} ne' negli altri magazzini del suo ricollocatore ({cause}): allo scarto {mission.DestinationItemId}, rotta di {mission.MissionSteps.Count} passi"
                : $"Missione {mission.MissionId}: nessuna cella in {storage.ItemGuid} ne' negli altri magazzini del suo ricollocatore ({cause}), e lo scarto verso {rejectionPoint} non e' riuscito: destinazione {mission.DestinationItemId}, rotta di {mission.MissionSteps.Count} passi",
            mission.MissionType == MissionTypes.Rejection ? LogEntryType.Information : LogEntryType.Warning);
        return rejected;
    }

    /// <summary>
    /// La missione e' ancora quella attiva nel motore: dopo un'attesa del gestionale, chi la toglie
    /// intanto (<c>ActiveMissions.Remove</c>) non e' rifiutato da una ricerca in corso, e una
    /// scrittura su una missione tolta ne rifarebbe la riga sul database del WCS.
    /// </summary>
    private static bool StillActive(PathMission mission) =>
        ReferenceEquals(ActiveMissions.Instance[mission.MissionId], mission);

    /// <summary>
    /// Chi fa il deposito nel magazzino, per la ricerca negli altri suoi magazzini, nell'ordine in cui
    /// si prova: il suo ricollocatore, un dispositivo o un gruppo di ricollocatori. Se la missione e'
    /// gia' su di lui, o su un membro del suo gruppo, e' la posizione, e la ricerca e' quella che il
    /// ricollocatore fa per se'. Vuoto per un magazzino senza ricollocatore.
    /// </summary>
    /// <remarks>
    /// Un gruppo con la missione a monte (risposta 5 del proprietario dell'8/10, domanda D4 della
    /// revisione finale): la cella collegata del gruppo e' di solito 0, e ancorata a lui la ricerca
    /// rispondeva «nessuna cella automatica» senza chiedere al gestionale. Si prova la cella collegata
    /// del gruppo, se ne ha una, e poi quella di ogni membro, nell'ordine dei membri
    /// (<c>AutomationDevice.BelongedItems</c>), saltando chi non ne ha una e chi ripete una cella gia'
    /// provata (la stessa cella collegata; due celle diverse dello stesso magazzino del gestionale fanno
    /// due domande, che possono rispondere uguale). Le celle si chiedono come per il membro che cerca per
    /// se': le celle collegate al gruppo, nel magazzino del gestionale della cella collegata di chi si
    /// prova (<c>PathMission.SetParamsForStoreInLinkedCell</c>). Se nessuno ha una cella collegata
    /// resta il gruppo, e la ricerca risponde come prima.
    /// </remarks>
    private static IReadOnlyList<AutomationDevice> DepositorsOf(AutomationDevice storage, AutomationDevice position)
    {
        if (storage.RelocateItem is not { } relocator)
            return [];

        if (position.ItemGuid == relocator.ItemGuid || position.BelongedGroup?.ItemGuid == relocator.ItemGuid)
            return [position];

        if (relocator.DeviceType != DeviceTypes.GroupOfAutomationDevices)
            return [relocator];

        var depositors = new List<AutomationDevice>();
        var cells = new HashSet<decimal>();

        foreach (var candidate in relocator.BelongedItems.OfType<AutomationDevice>().Prepend(relocator))
            if (candidate.LinkedCellId != 0 && cells.Add(candidate.LinkedCellId))
                depositors.Add(candidate);

        return depositors.Count > 0 ? depositors : [relocator];
    }

    /// <summary>
    /// La ricerca negli altri magazzini del ricollocatore non ha dato alla missione una cella con la
    /// rotta (<see cref="AnotherStorageOrTheRejectionAsync"/>): la cella che puo' aver preso torna al
    /// gestionale, e la missione torna com'era, verso il magazzino della sua destinazione con la cella
    /// persa (<c>PathMission.DropStorageCell</c>). E' la regola del flusso del magazzino - ogni rifiuto
    /// dopo la prenotazione restituisce la cella presa (<see cref="ReserveACellAndCommitAsync"/>) - e
    /// quella della cella persa: nessuna missione va verso una cella che non le e' prenotata.
    /// </summary>
    /// <param name="storage">Il magazzino della destinazione, senza una cella per la missione.</param>
    /// <param name="why">Perche' la ricerca non ha dato la cella, per la riga di registro.</param>
    /// <remarks>
    /// <para>
    /// Senza restituirla, una porta proposta e scartata (nessuna rotta, il magazzino di un altro
    /// ricollocatore, l'indirizzo che il gestionale non legge) teneva la cella al gestionale finche'
    /// scadeva o la missione usciva, anche con la missione allo scarto. Il rilascio va per missione, e
    /// la missione qui non ne tiene un'altra: la cella del suo magazzino l'aveva gia' persa, e la
    /// ricerca lascia ogni prenotazione prima di chiedere la sua. Si chiede sempre, anche quando la
    /// ricerca non e' arrivata a prenotare, perche' l'esito non lo dice e una risposta persa puo' aver
    /// prenotato lo stesso; un guasto lo copre la scadenza del gestionale.
    /// </para>
    /// <para>
    /// La porta proposta si guarda prima di scrivere (<c>PathMission.GetStoreToWarehouse</c> con chi
    /// deposita), e di solito la missione e' rimasta com'era. Se la ricerca ha scritto e poi non ha
    /// costruito la rotta - cambiata fra la sonda e la costruzione - o e' caduta dopo aver scritto, la
    /// missione andrebbe verso un magazzino senza rotta, o con la porta di un altro magazzino, senza
    /// piu' la cella persa: il giro dopo non la cercherebbe piu' (<see cref="KeepTheStorageCellAsync"/>
    /// risponde «niente da fare»), e la porta di un altro magazzino potrebbe perfino risultare tenuta.
    /// Qui torna verso il suo magazzino, senza porta, indirizzo ne' rotta, con la cella persa, e il
    /// giro dopo la cerca di nuovo. Una missione tolta intanto non si tocca: le scritture ne
    /// rifarebbero la riga sul database del WCS.
    /// </para>
    /// <para>
    /// Senza il cambio cominciato, come lo scarto del flusso del deposito: il navigatore tiene il posto
    /// delle ricerche della missione (nessun cambio dell'operatore ne' spostamento intanto), e la
    /// ricerca che non riesce non le lascia step da avviare. Fuori da ogni lucchetto e semaforo, come
    /// la chiamata al gestionale.
    /// </para>
    /// </remarks>
    private static async Task BackToTheLostCellAsync(PathMission mission, AutomationDevice storage, string why)
    {
        if (Navigator.Warehouse is { } warehouse)
            await GiveBackReservationAsync(warehouse, mission, $"cella altrove non presa: {why}");

        if (!ReferenceEquals(ActiveMissions.Instance[mission.MissionId], mission) ||
            mission.DestinationItemId == storage.ItemGuid && string.IsNullOrEmpty(mission.StorageDoorCode) &&
            mission.StorageCellLost)
            return;

        var door = mission.StorageDoorCode;
        var destination = mission.DestinationItemId;

        if (destination != storage.ItemGuid)
            mission.DestinationItemId = storage.ItemGuid;

        mission.DropStorageCell();

        Navigator.WriteLog(
            $"Missione {mission.MissionId}: la ricerca negli altri magazzini del ricollocatore aveva scritto la porta {door} verso {destination} senza darle la cella ({why}): la cella torna al gestionale, e la missione torna verso {storage.ItemGuid} con la cella persa",
            LogEntryType.Warning);
    }

    /// <summary>
    /// La cella di una missione che l'operatore ha mandato in un magazzino, rifiutata dal PLC al
    /// deposito: gli errori 67, 85 e 133, del dispositivo singolo o del gruppo (risposta 1 del
    /// proprietario dell'8/10, che sostituisce le risposte 6 e 7 del 2/10; il 137 non cerca piu', manda
    /// il gruppo subito in errore). Il motore cerca un'altra cella nello stesso magazzino, esclusa quella
    /// rifiutata, con il flusso del cambio (<see cref="ReserveACellAndCommitAsync"/>): magazzino, gateway
    /// e segno restano dell'operatore, cambiano porta, indirizzo e rotta. Se nel magazzino non ce n'e'
    /// un'altra la missione va allo scarto, e lo fa chi chiama, con il rigetto del suo percorso; se
    /// adesso non si sa (<see cref="RefusedCellOutcome.NotNow"/>) chi chiama manda il dispositivo in
    /// errore, e il dispositivo aspetta l'operatore; se il gestionale non risponde
    /// (<see cref="RefusedCellOutcome.WarehouseSilent"/>), dopo l'errore la missione lascia anche la porta
    /// rifiutata (<see cref="LeaveTheRefusedCell"/>, risposta 7 del proprietario dell'8/10 sera).
    /// </summary>
    /// <param name="worker">Chi chiede, per le righe di registro.</param>
    /// <remarks>
    /// <para>
    /// «Lo stesso magazzino» e' la destinazione della missione (<see cref="StoragesOf"/>), come per la
    /// cella persa (<see cref="KeepTheStorageCellAsync"/>); la cella esclusa e' quella della sua porta,
    /// con la regola di quando l'operatore sceglie di nuovo il magazzino (<see cref="CellToReplaceAsync"/>),
    /// e con lei tutte quelle che il PLC ha rifiutato alla missione, che restano escluse finche' l'UDC
    /// non deposita (<c>PathMission.CellsRefusedByThePlc</c>, risposta 7 del proprietario dell'8/10 sera). Se la prenotazione lascia la cella che la missione
    /// teneva e poi il cambio e' rifiutato, la missione la perde come dopo un cambio rifiutato
    /// (<see cref="LoseTheCell"/>), e il navigatore gliene prenota un'altra nel magazzino scelto. Trovata la cella, e' confermata per il deposito da dove la
    /// missione e' (<c>PathMission.ConfirmStorageCell</c>): e' appena stata prenotata, come quella che il
    /// navigatore rifa'.
    /// </para>
    /// <para>
    /// Chi chiama ha contato un avvio sulla missione (<c>PathMission.TryBeginStepStart</c>), come la
    /// ricerca delle celle di un gruppo: nessun cambio dell'operatore comincia intanto. Qui si prende il
    /// posto delle ricerche della missione (<c>Navigator.TryClaimCellSearch</c>), cosi' ne' il giro
    /// delle rotte ne' uno spostamento la toccano mentre il gestionale risponde; se e' preso, adesso non
    /// si cerca. Lo step del deposito rifiutato non e' piu' in volo, perche' il campo ha risposto, e chi
    /// chiama lo toglie, o ne toglie il gettone, prima di chiamare: il flusso non tocca una missione con
    /// uno step in volo. Fuori da ogni lucchetto e semaforo, come ogni chiamata al gestionale; il commit
    /// con il confronto della versione della rotta, come quello del cambio.
    /// </para>
    /// </remarks>
    internal static async Task<RefusedCellOutcome> AnotherCellInTheChosenStorageAsync(PathMission mission, string worker)
    {
        if (!mission.OperatorDestination || mission.DestinationItem is not { DeviceType: DeviceTypes.Storage } storage)
            return RefusedCellOutcome.NotTheOperatorsStorage;

        var refused = mission.StorageDoorCode;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!Navigator.TryClaimCellSearch(mission.MissionId, finished.Task))
        {
            Navigator.WriteLog(
                $"{worker}: il PLC ha rifiutato la cella della porta {refused} alla missione {mission.MissionId}, ma una ricerca della cella o un cambio e' in corso per lei: per adesso nessun'altra cella",
                LogEntryType.Information);
            return RefusedCellOutcome.NotNow;
        }

        var origin = mission.PositionItemId;
        DestinationChange change;

        try
        {
            // La cella della porta, e tutte quelle che il PLC ha rifiutato alla missione
            // (PathMission.CellsRefusedByThePlc, finche' l'UDC non deposita): la cella nuova non e'
            // nessuna di queste. Un rifiuto dopo la prenotazione le fa perdere la cella che teneva
            // (ReserveOrLetGoAsync).
            IReadOnlyList<decimal> excluded =
                [.. (await CellToReplaceAsync(mission, storage)).Concat(mission.CellsRefusedByThePlc).Distinct()];
            change = await ReserveOrLetGoAsync(mission, origin, storage, StoragesOf(storage), mission.Gateway,
                excluded, new ReservationProgress(), changeOfCell: false);
        }
        finally
        {
            Navigator.ReleaseCellSearchClaim(mission.MissionId, finished.Task);
            finished.TrySetResult();
        }

        if (change.Result.Outcome == MissionDestinationOutcome.Changed)
        {
            mission.ConfirmStorageCell(origin);
            Navigator.WriteLog(
                $"{worker}: il PLC ha rifiutato la cella della porta {refused} alla missione {mission.MissionId}, e il motore le ha prenotato la cella della porta {mission.StorageDoorCode} nello stesso magazzino {storage.ItemGuid} scelto dall'operatore: gateway {mission.Gateway}, rotta di {mission.MissionSteps.Count} passi",
                LogEntryType.Information);
            return RefusedCellOutcome.AnotherCell;
        }

        Navigator.WriteLog(
            change.Reason == null
                ? $"{worker}: il PLC ha rifiutato la cella della porta {refused} alla missione {mission.MissionId}, e il motore non le ha trovato un'altra cella in {storage.ItemGuid} ({change.Result.Outcome})"
                : $"{worker}: il PLC ha rifiutato la cella della porta {refused} alla missione {mission.MissionId}, e il motore non le ha trovato un'altra cella in {storage.ItemGuid} ({change.Result.Outcome}): {change.Reason}",
            change.ReasonLevel);

        // Tolta mentre il gestionale rispondeva (il flusso lo dice con «missione non trovata», ma
        // l'uscita puo' venire anche dopo la sua ultima domanda): niente causa ne' scarto, che ne
        // rifarebbero la riga sul database del WCS. Per chi chiama e' «adesso non si sa»; con il
        // gestionale che non risponde, in piu' la missione lascia la porta rifiutata.
        if (change.Result.Outcome != MissionDestinationOutcome.NoStorageCell || !StillActive(mission))
            return change.Result.Outcome == MissionDestinationOutcome.WarehouseUnavailable && StillActive(mission)
                ? RefusedCellOutcome.WarehouseSilent
                : RefusedCellOutcome.NotNow;

        // La causa con cui lo scarto chiude il comando della missione, come la scrive la ricerca del
        // flusso del deposito quando il gestionale non ha celle (PathMission.SearchTheStoreAsync). Chi
        // chiama manda allo scarto solo se un punto di scarto c'e'; senza, la causa resta sulla
        // missione come per la ricerca del deposito che non trova.
        mission.StatusDescription = PathMission.NoStorageCellAvailable;

        // Il gateway dell'operatore lo toglie lo scarto stesso (PathMission.RejectTo), per ogni via che
        // ci arriva, non solo per questa.
        return RefusedCellOutcome.NoOtherCell;
    }

    /// <summary>
    /// Il gestionale non dice se la cella della porta della missione e' ancora sua: porta e rotta
    /// restano (non si sa che non lo sia), il deposito aspetta una conferma che non c'e', e il
    /// navigatore rifa' la domanda al giro dopo, con il suo freno. Rispondere «niente da fare» farebbe
    /// proseguire il giro come se la cella fosse confermata, e il deposito senza conferma rimetterebbe
    /// la missione in coda subito: un giro continuo di domande a un gestionale che non risponde.
    /// </summary>
    /// <param name="origin">La posizione della missione, da cui si guarda se il prossimo passo e' il deposito.</param>
    /// <returns>
    /// Nessuna rotta, se da dove la missione e' il prossimo passo e' il deposito; altrimenti
    /// <c>null</c>, «niente da fare», e il giro prosegue con la configurazione della posizione come
    /// prima del controllo (vedi le note).
    /// </returns>
    /// <remarks>
    /// Ad aspettare la conferma e' il deposito (risposta 4 del proprietario del 2/10): lontano dal
    /// deposito non c'e' niente che la conferma sblocchi, e nessuna conferma si scrive, quindi il
    /// deposito la chiedera' di nuovo quando ci arriva. Fino alla revisione finale della voce 39 la
    /// missione aspettava ovunque fosse: con il gestionale fermo il giro delle rotte non rifaceva piu'
    /// la rotta a nessuna missione con il segno della cella, in tutto l'impianto, anche sui
    /// dispositivi in modalita' <c>No</c> la cui configurazione il gestionale non lo chiede.
    /// </remarks>
    private static AutomationDevice.RouteBuildStatusTypes? CellNotConfirmed(PathMission mission, Guid origin, string why)
    {
        if (!TheDepositIsTheNextStep(mission, origin))
        {
            Navigator.WriteLog(
                $"Missione {mission.MissionId}: non si sa se la cella della porta {mission.StorageDoorCode} e' ancora sua ({why}): il deposito non e' il suo prossimo passo, e lo chiedera' quando ci arriva",
                LogEntryType.Information);
            return null;
        }

        Navigator.WriteLog(
            $"Missione {mission.MissionId}: non si sa se la cella della porta {mission.StorageDoorCode} e' ancora sua ({why}): porta e rotta restano, e il deposito aspetta",
            LogEntryType.Information);
        return AutomationDevice.RouteBuildStatusTypes.NotAvailable;
    }

    /// <summary>
    /// Il rifiuto per una risposta del gestionale senza cella, con la causa: l'UDC senza riferimenti
    /// per il gestionale (un avviso, come <c>StorageLuResultTypes.LuWithNoReference</c> nel flusso
    /// del deposito), o nessuna cella libera; <c>null</c> se la cella c'e'.
    /// </summary>
    /// <param name="stage">Quale delle due domande: quella senza prenotare, o la prenotazione.</param>
    /// <param name="anotherCell">
    /// La domanda escludeva la cella che la missione ha, perche' l'operatore ne chiede un'altra: senza
    /// cella libera il rifiuto lo dice (<see cref="MissionDestinationResult.NoOtherFreeCell"/>), e la
    /// cella della missione resta sua, perche' la domanda non prenota.
    /// </param>
    private static DestinationChange? NoCell(PathMission mission, StorageReservation answer, string stage,
        bool anotherCell = false) =>
        PathMission.CategorizeReservationOutcome(answer.Outcome) switch
        {
            PathMission.ReservationOutcomeCategory.CellFound => null,
            PathMission.ReservationOutcomeCategory.LoadUnitHasNoReferences => DestinationChange.Refused(
                MissionDestinationOutcome.NoStorageCell,
                $"{stage}: il gestionale risponde che l'UDC {mission.LuId} non ha riferimenti ({answer.Outcome})",
                LogEntryType.Warning),
            _ when anotherCell => new(new(MissionDestinationOutcome.NoStorageCell, NoOtherFreeCell: true),
                $"{stage}: il gestionale non ha un'altra cella libera nel magazzino scelto ({answer.Outcome}), e la missione tiene la sua"),
            _ => DestinationChange.Refused(MissionDestinationOutcome.NoStorageCell,
                $"{stage}: il gestionale non ha una cella libera nel magazzino scelto ({answer.Outcome})")
        };

    /// <summary>Che cosa ha fatto il tratto di commit: il cambio, e se la missione ha lasciato un magazzino.</summary>
    private sealed record CommitOutcome(DestinationChange Change, bool LeftAStorage);

    /// <summary>
    /// Il tratto di commit: niente gestionale e niente attese qui dentro. Riguarda che la missione sia
    /// ancora attiva e ancora dove la rotta parte, poi <c>PathMission.CommitRoute</c>, che scrive solo se
    /// la versione della rotta e' ancora quella letta qui.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nessun semaforo di esecuzione si prende, ne' quello della posizione ne' quello del suo gruppo. Il
    /// conto degli avvii (<c>PathMission.TryBeginStepStart</c>) ferma gli step che partono, i movimenti di
    /// gruppo e le ricerche dei dispositivi; il resto di quello che l'esecuzione della posizione fa sulla
    /// rotta lo ferma la versione della rotta. La strategia <c>Standard</c>, con lo step da fare nullo e
    /// la rotta non vuota, toglie la rotta solo se e' ancora quella su cui ha guardato
    /// (<c>PathMission.RemoveTheRouteSeenWithoutAStepToDo</c>): una rotta scritta qui nel frattempo resta.
    /// E se la toglie fra la lettura della versione qui sotto e la scrittura, il commit perde il
    /// confronto e il cambio e' rifiutato, come per uno spostamento arrivato in mezzo.
    /// </para>
    /// <para>
    /// Fino alla voce 41 (compito 5) il commit prendeva il semaforo di esecuzione della posizione senza
    /// aspettare, per la sola strategia <c>Standard</c>, e se un'esecuzione era in corso il cambio era
    /// rifiutato. Anche quando la posizione stava solo per avviare uno step che il cambio cominciato le
    /// avrebbe comunque fermato, o dormiva nella pausa di <c>ExecuteStep(500)</c> dopo un arrivo: il
    /// rifiuto dopo la prenotazione faceva perdere alla missione la cella che teneva, e a un'estrazione
    /// nella sua cella la missione stessa (corsa 4 della ricognizione della voce 41).
    /// </para>
    /// </remarks>
    /// <param name="copies">
    /// Le copie della porta della missione, ferme, per il cambio verso un dispositivo
    /// (<see cref="ChangeToDeviceAsync"/>): lasciano la porta dentro <c>PathMission.CommitRoute</c>, sotto
    /// il lucchetto della rotta, dopo il confronto della versione e lo sguardo allo step in volo e prima
    /// che la missione scriva la sua riga (<see cref="CopiesOfTheDoor.Leave"/>). Un commit rifiutato non le
    /// tocca. Prima lasciavano la porta prima del commit, e un rifiuto (allora anche del semaforo) o della missione
    /// spostata le lasciava senza porta mentre la missione andava ancora nella cella (primo giro di
    /// correzioni del compito 15); e lasciata qui, prima di <c>CommitRoute</c>, uno spostamento arrivato
    /// in mezzo faceva perdere il confronto della versione con le copie gia' senza porta (primo giro di
    /// correzioni del compito 4 della voce 41). Nessuna per il flusso del magazzino, dove la lasciano alla
    /// prenotazione.
    /// </param>
    /// <param name="giveBack">
    /// The cell of a redirected change of cell or defragmentation (item 54, <see cref="CellToGiveBackAsync"/>),
    /// that <c>PathMission.CommitRoute</c> queues with the mission's row.
    /// </param>
    private static CommitOutcome Commit(PathMission mission, Guid origin, Guid destinationId, List<MissionStep> steps,
        Guid gateway, PathMission.StorageCellChoice? cell, string? bufferDoorCode, bool operatorChoice, bool changeOfCell,
        CopiesOfTheDoor? copies = null, PathMission.CellGiveBack? giveBack = null,
        RelocatorArrival relocatorArrival = RelocatorArrival.None)
    {
        if (!ReferenceEquals(ActiveMissions.Instance[mission.MissionId], mission))
            return new(DestinationChange.Refused(MissionDestinationOutcome.MissionNotFound,
                "la missione non e' piu' attiva"), false);

        // La versione della rotta si legge prima di guardare la posizione, e il commit scrive solo
        // se e' ancora quella (voce 41, corse 3 e 4): uno spostamento arrivato dopo lo sguardo qui sotto
        // la fa salire, e la rotta sondata dal posto vecchio non si scrive. Lo spostamento vince
        // (decisione 2 del proprietario del 10/10); il cambio e' rifiutato come quando lo
        // spostamento arriva prima. Lo stesso per la rimozione della strategia Standard, che prima
        // escludeva il semaforo della posizione (vedi le note).
        var seenRouteVersion = mission.RouteVersion;

        // Mossa intanto: la rotta sondata parte da un posto dove la missione non e' piu'.
        if (mission.PositionItemId != origin)
            return new(DestinationChange.Refused(MissionDestinationOutcome.StepInFlight,
                $"la missione e' stata spostata su {mission.PositionItemId} mentre il cambio era in corso",
                LogEntryType.Warning), false);

        // CommitRoute puo' non scrivere in quattro modi: la versione della rotta cambiata (Lost), che uno
        // spostamento o la rimozione di Standard possono portare in ogni momento; il rigetto dal campo
        // rimandato che la missione aspetta (AwaitingRejection); lo step in volo, che il cambio cominciato
        // (ChangeDestinationAsync) ha gia' escluso (Refused); e la destinazione scartata dopo la rimozione
        // della rotta vecchia (Refused anche lui, e con il cambio cominciato non succede). Le copie lasciano
        // la porta dentro di lui, nei primi tre casi mai, nel quarto si' (vedi il parametro copies e il
        // beforeWriting di CommitRoute). Codice sincrono, senza il gestionale.
        var leftAStorage = cell == null && mission.DestinationItem?.DeviceType == DeviceTypes.Storage;

        // Il motivo del rifiuto lo dice il commit (revisione finale della voce 41): prima qui si rileggeva
        // la versione, che dopo un rifiuto per lo step in volo poteva essere salita per altro.
        var committed = mission.CommitRoute(destinationId, steps, gateway, cell, bufferDoorCode, operatorChoice,
            changeOfCell, seenRouteVersion, copies == null ? null : copies.Leave, giveBack, relocatorArrival);

        // Il rigetto dal campo rimandato (AwaitingRejection) non e' una rotta cambiata da un altro: la
        // missione aspetta che il giro delle rotte la rigetti, e il motivo lo dice (secondo giro di
        // correzioni del compito 8 della voce 41; prima si leggeva «la rotta e' cambiata»). L'esito resta
        // StepInFlight, «da riprovare»: il rigetto aspettava uno step in volo, e lo rifa' il giro.
        if (committed != PathMission.RouteWriteOutcome.Written)
            return new(DestinationChange.Refused(MissionDestinationOutcome.StepInFlight,
                committed switch
                {
                    PathMission.RouteWriteOutcome.Lost =>
                        $"la rotta della missione e' cambiata mentre il cambio era in corso (la missione e' su {mission.PositionItemId})",
                    PathMission.RouteWriteOutcome.AwaitingRejection =>
                        $"la missione aspetta il rigetto dal campo, rimandato per uno step che era in volo, e il giro delle rotte lo rifa' da {mission.PositionItemId}",
                    _ => "uno step della missione e' in volo"
                },
                LogEntryType.Warning), false);

        // Il freno della riverifica della cella sul ricollocatore era per la cella di prima: la
        // nuova si verifica al primo giro, non quando scade un'attesa che non la riguarda. Lo
        // stesso per il freno del giro delle rotte, che contava i fallimenti verso la
        // destinazione di prima: la richiesta rimasta in coda si riprende al giro dopo (per un
        // magazzino e' li' che il navigatore conferma la cella, e il deposito la aspetta), e un
        // fallimento di domani conta da uno.
        PositionOf(mission, origin)?.StorageManager?.StoreSearchRetries.Forget(mission.MissionId);
        Navigator.ForgetRouteRetries(mission.MissionId);

        return new(new DestinationChange(new(MissionDestinationOutcome.Changed)), leftAStorage);
    }

    /// <summary>
    /// Il rifiuto per mancanza di percorso, con il gateway usato e se senza di lui il percorso ci
    /// sarebbe: la seconda sonda si fa solo se un gateway c'era.
    /// </summary>
    private static MissionDestinationResult NoRoute(PathMission mission, Guid destinationId, Guid gateway) =>
        gateway == Guid.Empty
            ? new(MissionDestinationOutcome.NoRoute)
            : new(MissionDestinationOutcome.NoRoute, gateway, mission.ProbeRouteSteps(destinationId, Guid.Empty).Count > 0);

    /// <summary>
    /// The cell a change of cell or a defragmentation was going to, read before the operator's change
    /// (item 54, decision 3 and answer 1 of the owner of 13/10): the commit that sends the mission
    /// elsewhere puts it in the durable queue of the cells to give back (<c>PathMission.CommitRoute</c>),
    /// and the dispatcher of the master loop gives it back by load unit and cell. Null for every other
    /// type, or without a door mapped to a cell in the WCS's door map. Only a read of the WCS's
    /// database, no warehouse.
    /// </summary>
    /// <remarks>
    /// By load unit and cell, not by mission: the changes of cell born in the engine (the obstacle, the
    /// reload) hold a reservation with no link to the mission, which the release by mission does not
    /// reach. The courtesy release by mission of <see cref="ChangeToDeviceAsync"/> stays as it was.
    /// </remarks>
    private static async Task<PathMission.CellGiveBack?> CellToGiveBackAsync(PathMission mission)
    {
        if (mission.MissionType is not (MissionTypes.ChangeStoreLocation or MissionTypes.DefragmentCell) ||
            mission.StorageDoorCode is not { Length: > 0 } door)
            return null;

        var cellId = await ORM.DbInfo.GetCellId(door);
        return cellId == 0 ? null : new PathMission.CellGiveBack(door, cellId);
    }

    /// <summary>
    /// The cell the storage flow has just reserved and committed for the mission (item 54, fix round 1
    /// of task 3): the rows of the queue of the cells to give back for its load unit and that cell leave
    /// the queue - an earlier redirect left it there, and the operator sent the mission back to it - so
    /// that no later turn of the dispatcher gives back the cell the mission now goes to, also after the
    /// mission is gone. Under the claim of the load unit, out of every lock. Does not raise: the change
    /// done is not undone, and while the mission goes there the dispatcher keeps the cell anyway
    /// (<c>PendingCellGiveBackDispatcher</c>, a cell that is the mission's again); a failure says so.
    /// </summary>
    private static async Task DropTheGiveBacksOfTheReservedCellAsync(PathMission mission, StorageReservation reservation)
    {
        try
        {
            var dropped = await ORM.CellGiveBackOutbox.DeleteForCellAsync(mission.LuId, reservation.CellId, reservation.DoorCode);
            if (dropped > 0)
                Navigator.WriteLog(
                    $"Missione {mission.MissionId}, UDC {mission.LuId:0}: la cella {reservation.CellId:0} (porta {reservation.DoorCode}) e' di nuovo della missione, e la sua restituzione al gestionale ({dropped} righe) e' tolta dalla coda",
                    LogEntryType.Information);
        }
        catch (Exception ex)
        {
            Navigator.WriteLog(
                $"Missione {mission.MissionId}, UDC {mission.LuId:0}: la cella {reservation.CellId:0} (porta {reservation.DoorCode}) e' di nuovo della missione, ma la sua restituzione al gestionale non e' stata tolta dalla coda ({ex.Message}): finche' la missione ci va il WCS non la restituisce, dopo va controllata a mano",
                LogEntryType.Error);
        }
    }

    /// <summary>
    /// Waits for the write the commit queued for the cell to give back (item 54,
    /// <c>PathMission.QueueTheCellGiveBack</c>): the mission's row and the queue's row in the same
    /// transaction. Nothing if the commit did not queue it. Out of every lock; does not raise, the change
    /// done is not undone: a write that fails says so with an error line, because the cell will not go
    /// back to the warehouse by itself.
    /// </summary>
    private static async Task WaitForTheCellGiveBackAsync(PathMission mission)
    {
        if (mission.TakeTheCellGiveBack() is not { } written) return;

        try
        {
            await written;
        }
        catch (Exception ex)
        {
            Navigator.WriteLog(
                $"Missione {mission.MissionId}, UDC {mission.LuId:0}: la missione e' stata mandata altrove, ma la riga della cella da restituire al gestionale non e' stata scritta nel database del WCS ({ex.Message}): la cella va liberata a mano",
                LogEntryType.Error);
        }
    }

    /// <summary>
    /// Restituisce la prenotazione di deposito della missione, di cortesia: un guasto si registra e
    /// basta, e la cella la libera la scadenza del gestionale. Il rilascio va per missione, e mentre il
    /// cambio e' in corso nessuna ricerca prenota per lei: quella che si restituisce e' del cambio, o
    /// quella vecchia.
    /// </summary>
    private static async Task GiveBackReservationAsync(IWarehouse warehouse, PathMission mission, string reason)
    {
        try
        {
            await warehouse.ReleaseStorageReservationAsync(mission.MissionId);
        }
        catch (Exception ex)
        {
            Navigator.WriteLog(
                $"Missione {mission.MissionId}: la prenotazione di deposito non e' stata restituita ({reason}): {ex.Message}",
                LogEntryType.Warning);
        }
    }

    /// <summary>
    /// La missione su <paramref name="origin"/> e' gia' arrivata a <paramref name="target"/>: e' lui,
    /// o il gruppo di cui fa parte. La stessa regola del setter della posizione (<c>PathMission.Reaches</c>).
    /// </summary>
    private static bool IsReachedFrom(Guid origin, Guid target) => PathMission.Reaches(origin, target);

    /// <summary>
    /// I magazzini che la scelta dell'operatore indica: il magazzino stesso, o i magazzini di un
    /// gruppo scelto. Vuoto per ogni altro dispositivo, che non ha celle: la rotta va verso di lui.
    /// </summary>
    /// <remarks>
    /// Un gruppo di magazzini e' un magazzino, per chi sceglie: verso di lui la rotta finisce in uno
    /// dei suoi magazzini (un magazzino non e' mai transito), e senza una cella prenotata l'UDC ci
    /// arriverebbe senza porta, cioe' senza un posto dove il deposito la registri.
    /// </remarks>
    private static IReadOnlyList<AutomationDevice> StoragesOf(AutomationDevice chosen) => chosen.DeviceType switch
    {
        DeviceTypes.Storage => [chosen],
        DeviceTypes.GroupOfAutomationDevices =>
            [.. chosen.BelongedItems.OfType<AutomationDevice>().Where(member => member.DeviceType == DeviceTypes.Storage)],
        _ => []
    };

    /// <summary>
    /// A relocator (item 54, decision 5): a device some storage names as its <c>RelocateItemId</c> - a
    /// stacker crane, or a group of them. The same rule the Editor uses to recognise one
    /// (<c>PlantItemDto.RelocateItemId</c>).
    /// </summary>
    internal static bool IsARelocator(AutomationDevice device) =>
        PathDevices.Instance.List.Any(storage =>
            storage is { DeviceType: DeviceTypes.Storage } && storage.RelocateItemId == device.ItemGuid);

    /// <summary>The storages a relocator serves: those that name it, or its group, as their <c>RelocateItemId</c>.</summary>
    private static IReadOnlyList<AutomationDevice> StoragesServedBy(AutomationDevice relocator) =>
        [.. PathDevices.Instance.List.Where(storage => storage is { DeviceType: DeviceTypes.Storage } && IsServedBy(storage, relocator))];

    /// <summary>The storage is served by the relocator: it names it, or the group it belongs to.</summary>
    private static bool IsServedBy(AutomationDevice storage, AutomationDevice relocator) =>
        storage.RelocateItemId == relocator.ItemGuid ||
        storage.RelocateItem?.BelongedGroup?.ItemGuid == relocator.ItemGuid;

    /// <summary>
    /// The storage of the precise cell the operator chose (item 54, decision 6), from the WCS's door map,
    /// and whether it is in what they chose: the storage itself, a storage of the chosen group, or a
    /// storage served by the chosen relocator. Only reads of the WCS's database, no warehouse: the
    /// refusals here are said before asking anything (a door B by its side in the door map, owner's
    /// answer 7 of 13/10), and the warehouse checks the rest when it reserves the cell.
    /// </summary>
    private static async Task<(AutomationDevice? Storage, MissionDestinationResult? Refusal)> CellInTheChoiceAsync(
        string doorCode, AutomationDevice chosen)
    {
        var door = doorCode.Trim();

        var device = PathDevices.Instance[await ORM.DbInfo.GetItemId(door)];
        if (device == null)
            return (null, CellRefusedResult(MissionCellRefusal.UnknownDoor,
                $"la porta {door} non e' nella mappa delle porte del WCS"));

        if (string.Equals(await ORM.DbInfo.GetDoorSide(door), "B", StringComparison.OrdinalIgnoreCase))
            return (null, CellRefusedResult(MissionCellRefusal.NotADoorA,
                $"la porta {door} e' la porta B della sua cella: la cella si sceglie con il codice della porta A"));

        var inTheChoice = StoragesOf(chosen).Any(storage => storage.ItemGuid == device.ItemGuid) ||
                          IsARelocator(chosen) && IsServedBy(device, chosen);

        return inTheChoice
            ? (device, null)
            : (null, CellRefusedResult(MissionCellRefusal.NotInTheChoice,
                $"la cella della porta {door} non e' nel dispositivo scelto {chosen.ItemGuid}, ne' servita da lui"));
    }

    private static MissionDestinationResult CellRefusedResult(MissionCellRefusal why, string reason) =>
        new(MissionDestinationOutcome.CellRefused, CellRefusal: why, CellRefusalReason: reason);

    /// <summary>The refusal of a precise cell, with the reason for the operator and for the log.</summary>
    private static DestinationChange CellRefused(MissionCellRefusal why, string reason) =>
        new(CellRefusedResult(why, reason), reason);

    /// <summary>
    /// The storage cells the operator can choose for the mission's load unit (item 54, decision 6 and
    /// owner's answer 6 of 13/10): the first 20 the warehouse proposes with the search of every day
    /// (<c>IWarehouse.ListStorageCellCandidatesAsync</c>), in a storage, a group of storages, or among the
    /// cells a relocator serves. Nothing is reserved: the choice is reserved by the change of destination
    /// with the precise cell (<see cref="MissionDestinationOptions.CellDoorCode"/>), which checks it again.
    /// </summary>
    /// <remarks>
    /// The request is the one the storage flow would make to reserve (<see cref="ReserveACellAndCommitAsync"/>):
    /// the warehouse of the first mapped door of the storages, its automatic areas, the relocators of the
    /// storages (for a relocator, itself). The cells the warehouse answers that are not in those storages
    /// by the WCS's door map are left out, so the list holds only cells the change would accept. Read
    /// only, in every state of the engine, out of every lock.
    /// </remarks>
    public static async Task<StorageCellCandidates> StorageCellCandidatesAsync(Guid missionId, Guid itemId)
    {
        if (ActiveMissions.Instance[missionId] is not { } mission)
            return StorageCellCandidates.Refused(StorageCellCandidatesOutcome.MissionNotFound);

        if (PathDevices.Instance[itemId] is not { } chosen)
            return StorageCellCandidates.Refused(StorageCellCandidatesOutcome.DeviceNotFound);

        var storages = StoragesOf(chosen);
        IReadOnlyList<Guid> reachable;
        if (storages.Count > 0)
        {
            reachable = [.. storages.Select(storage => storage.RelocateItem?.ItemGuid ?? storage.ItemGuid).Distinct()];
        }
        else if (IsARelocator(chosen))
        {
            storages = StoragesServedBy(chosen);
            reachable = [chosen.ItemGuid];
        }
        else
        {
            return StorageCellCandidates.Refused(StorageCellCandidatesOutcome.NotAStorage,
                $"il dispositivo {itemId} non e' un magazzino, un gruppo di magazzini o un ricollocatore");
        }

        if (mission.LuId == 0)
            return StorageCellCandidates.Refused(StorageCellCandidatesOutcome.NoStorageCell,
                "l'UDC non ha l'identificativo del gestionale (LuId 0)");

        if (Navigator.Warehouse is not { } warehouse)
            return StorageCellCandidates.Refused(StorageCellCandidatesOutcome.WarehouseUnavailable,
                "il gestionale non e' configurato");

        try
        {
            decimal cellId = 0;
            foreach (var storage in storages)
            {
                cellId = await ORM.DbInfo.GetCellId(await ORM.DbInfo.GetDoorCode(storage.ItemGuid));
                if (cellId != 0) break;
            }

            if (cellId == 0)
                return StorageCellCandidates.Refused(StorageCellCandidatesOutcome.NoStorageCell,
                    $"nessuna porta dei magazzini di {itemId} e' mappata a una cella nel database del WCS");

            var warehouseId = (await mission.DescribeCellAsync(cellId)).WarehouseId;
            if (warehouseId == 0)
                return StorageCellCandidates.Refused(StorageCellCandidatesOutcome.NoStorageCell,
                    $"la cella {cellId} dei magazzini di {itemId} non ha un magazzino del gestionale");

            var cells = await warehouse.ListStorageCellCandidatesAsync(new StorageCellCandidatesRequest(
                mission.LuId,
                warehouseId,
                await Persistence.LocalQueries.GetAutomaticAreasByWarehouseId(warehouseId),
                [],
                reachable,
                StorageCellSearchMode.FreeAutomaticOperator));

            var inTheChoice = new List<StorageCellCandidate>();
            foreach (var cell in cells)
            {
                var storageOfTheCell = await ORM.DbInfo.GetItemId(cell.DoorCode);
                if (storageOfTheCell != Guid.Empty && storages.Any(storage => storage.ItemGuid == storageOfTheCell))
                    inTheChoice.Add(cell);
            }

            return new(StorageCellCandidatesOutcome.Listed, inTheChoice);
        }
        catch (WarehouseUnavailableException ex)
        {
            return StorageCellCandidates.Refused(StorageCellCandidatesOutcome.WarehouseUnavailable, ex.Message);
        }
    }

    /// <summary>
    /// La porta prenotata e' del magazzino scelto (decisione 4 del proprietario): il magazzino stesso
    /// o un magazzino del suo gruppo; per un gruppo scelto, uno dei suoi magazzini.
    /// </summary>
    private static bool IsInChosenStorage(AutomationDevice doorDevice, AutomationDevice chosen) =>
        doorDevice.ItemGuid == chosen.ItemGuid ||
        doorDevice.BelongedGroup?.ItemGuid == chosen.ItemGuid ||
        (chosen.BelongedGroup is { } group && doorDevice.BelongedGroup?.ItemGuid == group.ItemGuid);

    /// <summary>
    /// Il dispositivo della porta della missione, se sta in uno dei magazzini scelti: il magazzino
    /// stesso, o un magazzino del gruppo scelto (<see cref="StoragesOf"/>). Non un fratello del gruppo
    /// del magazzino scelto, come accetta invece <see cref="IsInChosenStorage"/>: scegliere S2 con la
    /// cella in S1 non e' scegliere di nuovo il magazzino della cella (scelta tecnica del 2/10).
    /// <c>null</c> senza porta. Letture del database del WCS soltanto, niente gestionale.
    /// </summary>
    private static async Task<AutomationDevice?> DoorInTheChosenStoragesAsync(PathMission mission,
        IReadOnlyList<AutomationDevice> storages)
    {
        if (string.IsNullOrEmpty(mission.StorageDoorCode)) return null;

        var doorDevice = PathDevices.Instance[await ORM.DbInfo.GetItemId(mission.StorageDoorCode)];
        return doorDevice != null && storages.Any(storage => storage.ItemGuid == doorDevice.ItemGuid) ? doorDevice : null;
    }

    /// <summary>
    /// La cella da non riproporre: quella verso cui la missione va gia', quando l'operatore sceglie
    /// di nuovo il magazzino che aveva scelto lui (la porta della missione e' nel magazzino scelto e
    /// il segno e' alzato), o quando il PLC l'ha rifiutata al deposito (<see cref="AnotherCellInTheChosenStorageAsync"/>).
    /// Nessuna negli altri casi.
    /// </summary>
    /// <remarks>
    /// Con il segno il motore non cerca un'altra cella per la missione: cambiarla e' un nuovo cambio
    /// dell'operatore (<c>StoreVerificationResults.ChosenByOperator</c>), e lo fa scegliendo di nuovo
    /// il magazzino. Senza escluderla, il gestionale la puo' ridare uguale: la prenotazione viene
    /// dopo il rilascio di quella che la missione tiene (<c>ReserveOrKeepStorageAsync</c>), quando la
    /// cella e' di nuovo libera, e la porta prenotata si riguarda solo contro il magazzino scelto,
    /// quindi la missione resterebbe con la cella che non va. Una destinazione messa dalla
    /// configurazione non esclude niente: sceglierla e' farla dell'operatore, non chiedere un'altra
    /// cella. Letture del database del WCS soltanto, niente gestionale.
    /// </remarks>
    private static async Task<IReadOnlyList<decimal>> CellToReplaceAsync(PathMission mission, AutomationDevice chosen)
    {
        if (!mission.OperatorDestination || string.IsNullOrEmpty(mission.StorageDoorCode)) return [];

        var doorDevice = PathDevices.Instance[await ORM.DbInfo.GetItemId(mission.StorageDoorCode)];
        if (doorDevice == null || !IsInChosenStorage(doorDevice, chosen)) return [];

        var cellId = await ORM.DbInfo.GetCellId(mission.StorageDoorCode);
        return cellId == 0 ? [] : [cellId];
    }
}
