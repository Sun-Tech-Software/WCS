using Helper.Converters;
using Wcs.Engine.Collections;
using Wcs.Common;
using Wcs.Engine.ORM;
using Log.Interface;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Wcs.Engine.Persistence.Entities;
using Wcs.Wms.Contracts;
using static Wcs.Common.Enums;
using CommandToDo = Wcs.Engine.ORM.CommandToDo;

namespace Wcs.Engine.Items;

public partial class PathMission
{

    #region Internal Methods

    /// <summary>
    /// Ricostruisce la rotta della missione verso la sua destinazione: toglie quella che c'e', calcola
    /// la nuova e la scrive.
    /// </summary>
    /// <remarks>
    /// Il calcolo sta fuori da ogni lucchetto, anche dove chiede il database del WCS o il gestionale; la
    /// scrittura passa dalla primitiva della rotta (<see cref="TryWriteRoute"/>, voce 41, disegno C) con
    /// la versione letta subito dopo la rimozione. Se intanto un altro ha scritto la rotta o cambiato la
    /// destinazione o la posizione, la versione e' salita: la rotta calcolata non si scrive, e la missione
    /// torna al giro delle rotte del navigatore, che la ricalcola (<see cref="BackToTheRouteLoop"/>,
    /// decisione 1 del proprietario del 10/10). Vale per tutti quelli che la chiamano: l'ingresso fra le
    /// attive (<c>ActiveMissions.Add</c>), l'arrivo con la distribuzione sui polmoni, il giro delle rotte
    /// e i dispositivi. Senza nessun altro scrittore in mezzo la rotta si scrive come prima.
    /// </remarks>
    /// <returns>
    /// Vero se la rotta calcolata non si e' scritta perche' un altro l'ha cambiata intanto: la missione
    /// e' gia' in coda al giro delle rotte, e lo stato che ha (rotta, destinazione, porta) e' quello di
    /// chi ha vinto. Falso in ogni altro caso, anche quando non si e' costruito niente. Chi guarda
    /// <see cref="MissionSteps"/> dopo non deve leggere una perdita come «nessun percorso»: e' una
    /// rotta da rifare, non un fallimento (rilievo 1 del secondo giro del compito 3; la ricerca della
    /// cella che la leggeva cosi' mandava allo scarto un'UDC con una cella appena prenotata). Vero anche
    /// quando la rotta non si e' scritta perche' la missione aspetta il rigetto dal campo rimandato
    /// (<see cref="RouteWriteOutcome.AwaitingRejection"/>): anche allora e' in coda al giro, che rifa'
    /// prima il rigetto. Chi deve dire quale dei due casi e' usa <see cref="BuildTheRouteToDestinationAsync"/>.
    /// </returns>
    internal async Task<bool> BuildPathToDestination() =>
        await BuildTheRouteToDestinationAsync() is RouteWriteOutcome.Lost or RouteWriteOutcome.AwaitingRejection;

    /// <summary>
    /// Vedi <see cref="BuildPathToDestination"/>: lo stesso lavoro, con l'esito della scrittura.
    /// </summary>
    /// <returns>
    /// <c>null</c> se non si e' arrivati a scrivere (uno step in volo, nessuna destinazione, nessun
    /// percorso, la guardia della cella); altrimenti l'esito di <see cref="TryWriteRoute"/>. Con
    /// <see cref="RouteWriteOutcome.Lost"/> e <see cref="RouteWriteOutcome.AwaitingRejection"/> la
    /// missione e' gia' di nuovo in coda al giro delle rotte. <see cref="RouteWriteOutcome.Lost"/> anche
    /// quando l'estrazione prelevata che diventa un deposito non si converte (item 54: the hand-off of
    /// the pick-up did not let go in time, or the mission changed while waiting): nothing written, the
    /// mission back in the route loop. Lo usa la ricerca della cella, che con i due
    /// esiti fa cose diverse (<see cref="FindRouteAllocateLuCore"/>): una rotta presa da un altro conta
    /// un fallimento nel freno, l'attesa del rigetto no (secondo giro di correzioni del compito 8 della
    /// voce 41).
    /// </returns>
    private async Task<RouteWriteOutcome?> BuildTheRouteToDestinationAsync()
    {
        // La rotta si ricostruisce solo fra uno step e il successivo, mai durante. Qui sotto si
        // cancellano tutti gli step, e cancellare quello in volo rende libero il dispositivo che lo
        // sta eseguendo a meta' movimento; il completamento dal campo andrebbe perso. La missione
        // resta com'e', e chi la teneva in coda la riprende al giro dopo. Decisione del 2026-09-03.
        // Lo sguardo e la rimozione stanno insieme sotto il lucchetto della rotta, e con loro la
        // lettura della versione su cui si calcola: da qui in avanti ogni scrittura della rotta, della
        // destinazione o della posizione fatta da altri la fa salire, e la rotta calcolata qui sotto non
        // si scrive. Insieme, uno step che parte non si toglie in volo (RemoveTheRouteUnlessAStepIsInFlight).
        if (RemoveTheRouteUnlessAStepIsInFlight(out var seen) is { } inVolo)
        {
            Navigator.WriteLog(
                $"Rotta della missione {MissionId} non ricostruita: lo step {inVolo.StepId} e' in volo su {inVolo.FromNodeId}",
                LogEntryType.Information);
            return null;
        }

        // Verso un magazzino scelto dall'operatore, o verso il magazzino di una cella persa, la rotta
        // va alla porta della cella che il gestionale le tiene. Senza porta la cella non c'e' (persa
        // dopo un cambio rifiutato, o lasciata perche' il gestionale non la teneva piu': decisioni
        // del proprietario dell'1/10), e la prenota il giro delle rotte del navigatore con il flusso
        // del cambio (MissionGateway.KeepTheStorageCellAsync). Una rotta verso il magazzino nudo ci
        // porterebbe l'UDC senza un posto. Vale anche per la ricarica dopo un riavvio, che
        // costruisce la rotta da qui: la cella persa sta nel database del WCS. Lo stesso per la copia
        // che ha lasciato la porta (LeftACopiedDoor), a cui la porta la rifa' il ciclo del gruppo: anche
        // il suo segno sta nel database del WCS.
        if ((OperatorDestination || StorageCellLost) && string.IsNullOrEmpty(StorageDoorCode) &&
            DestinationItem is { DeviceType: DeviceTypes.Storage } || LeftACopiedDoor)
        {
            Navigator.WriteLog(
                LeftACopiedDoor
                    ? $"Rotta della missione {MissionId} non costruita: ha lasciato la porta copiata in {DestinationItemId}, e un'altra gliela da' il ciclo del gruppo"
                    : OperatorDestination
                    ? $"Rotta della missione {MissionId} non costruita: il magazzino {DestinationItemId} l'ha scelto l'operatore e la missione non ha una cella, che le prenota il navigatore"
                    : $"Rotta della missione {MissionId} non costruita: la missione ha perso la cella in {DestinationItemId}, e un'altra gliela prenota il navigatore",
                LogEntryType.Information);
            return null;
        }

        if (!string.IsNullOrEmpty(PickUpDoorCode) &&
            !string.IsNullOrEmpty(StorageDoorCode))
        {
            // Se è una missione di spostamento da cella a cella di Magazzino

            var sourceGuid = await DbInfo.GetItemId(PickUpDoorCode);
            var destinationGuid = await DbInfo.GetItemId(StorageDoorCode);

            // La stessa regola la guarda lo spostamento prima di costruire la rotta subito
            // (TheDoorsKeepItOffTheStraightRoute): la condizione sulle porte e sul tipo qui sopra e' scritta
            // anche li', e la regola delle porte e' una sola (DoorsMakeAChangeOfCellWhereTheLoadUnitIs,
            // with the picked-up extraction that becomes a deposit, PickedExtractionBecomingADeposit:
            // item 54). Se una delle due cambia, cambia per tutti e due.
            if (MissionType != MissionTypes.PickUpAndStoreAgain &&
                MissionType != MissionTypes.DefragmentCell)
            {
                if (DoorsMakeAChangeOfCellWhereTheLoadUnitIs(sourceGuid, destinationGuid))
                {
                    BecomeAChangeOfCell(PathDevices.Instance[sourceGuid], PathDevices.Instance[destinationGuid]);
                }
                else
                {
                    var automationDevice = PathDevices.Instance[destinationGuid];

                    if (automationDevice == null) return null;

                    // Item 54, decision 2 and answer 3 of the owner of 13/10: out of its cell an extraction
                    // bound for a storage is a deposit, and the warehouse reopens what the pick-up had
                    // served, as for the operator's commit. If the hand-off of the pick-up does not let
                    // go in time, or the mission changed while waiting, nothing is written and the
                    // mission goes back to the route loop, still what it was: the answer is Lost, as for a
                    // route taken over, because null means «not queued» and the cell search would read it
                    // as «no cell» and send the load unit to rejection (first fix round of task 1).
                    if (PickedExtractionBecomingADeposit(automationDevice) &&
                        !await BecomeADepositAfterThePickUpHandOffAsync(
                            $"The route rebuild found the picked-up extraction out of its cell, bound for a storage (cell {StorageDoorCode}): it became a deposit"))
                    {
                        BackToTheRouteLoop();
                        return RouteWriteOutcome.Lost;
                    }

                    // Item 54: a change of cell out of its cell, off its relocator (moved by hand onto a
                    // conveyor), is a deposit towards its storage: with no pick-up door, like the deposit
                    // the operator's commit makes of it (BecomeWhatTheNewDestinationMakesIt).
                    if (MissionType == MissionTypes.ChangeStoreLocation &&
                        automationDevice.DeviceType == DeviceTypes.Storage)
                        PickUpDoorCode = null;

                    MissionType = automationDevice.DeviceType switch
                    {
                        DeviceTypes.Storage => MissionTypes.Storing,
                        DeviceTypes.Buffer or DeviceTypes.LoadingAndBuffer => MissionTypes.Extraction,
                        _ => MissionType
                    };
                }
            }
        }
        else if (DestinationItemId == PositionItemId && MissionType == MissionTypes.Rejection)
        {
            return null;
        }
        else if (MissionType == MissionTypes.None)
        {
            MissionType = MissionTypes.MovingToItem;
        }

        List<MissionStep> resultSteps = [];
        Guid? arrivalOfTheRoute = null;

        switch (MissionType)
        {
            case MissionTypes.DefragmentCell:
                {
                    resultSteps = await GetStepToDefragmentCell();
                    break;
                }
            case MissionTypes.ChangeStoreLocation:
                {
                    resultSteps = await GetStepsToChangeStoreLocation();
                    break;
                }

            case MissionTypes.Extraction:
            case MissionTypes.Storing:
            case MissionTypes.MovingToItem:
            case MissionTypes.Rejection:
            case MissionTypes.PickUpAndStoreAgain:
            case MissionTypes.StoringByPickUp:
                {
                    if (DestinationItemId == Guid.Empty || PositionItemId == DestinationItemId)
                        return null;

                    resultSteps = GetMissionStepsToDestination();

                    if (resultSteps.Count == 0)
                        return null;

                    // La destinazione di un PickUpAndStoreAgain e' dove la rotta arriva: si scrive insieme
                    // alla rotta, nella primitiva. Scritta qui, prima, avrebbe fatto salire la versione
                    // e la costruzione avrebbe perso contro se stessa.
                    if (MissionType == MissionTypes.PickUpAndStoreAgain)
                        arrivalOfTheRoute = resultSteps.Last().ToNodeId;
                    break;
                }
            case MissionTypes.None:
            default:
                break;
        }

        // Niente da scrivere (un tipo che non costruisce, un cambio cella o una compattazione senza step):
        // la rotta e' gia' tolta qui sopra, e una scrittura vuota farebbe solo salire la versione, con chi
        // confronta che perderebbe per niente. Come gli altri rami senza percorso (giro finale delle
        // correzioni della voce 41).
        if (resultSteps.Count == 0)
            return null;

        if (RouteBuildPauseForTesting is { } pause)
            await pause();

        var outcome = TryWriteRoute(seen, resultSteps, arrivalOfTheRoute is { } arrival
            ? () =>
            {
                DestinationItemId = arrival;
                return true;
            }
            : null);

        if (outcome is not (RouteWriteOutcome.Lost or RouteWriteOutcome.AwaitingRejection))
            return outcome;

        BackToTheRouteLoop();
        return outcome;
    }

    /// <summary>
    /// Chi ha perso il confronto della versione della rotta (<see cref="RouteWriteOutcome.Lost"/>) non
    /// ritenta da se': la missione torna al giro delle rotte del navigatore, che la ricalcola dalla
    /// posizione e con la destinazione che ha allora (decisione 1 del proprietario del 10/10). In Stop il
    /// giro non gira, e la missione aspetta li' la marcia.
    /// </summary>
    /// <remarks>
    /// Una missione che non e' piu' fra le attive non torna in coda: il giro la scarterebbe, e chi l'ha
    /// tolta ha gia' tolto anche la sua richiesta (<c>ActiveMissions.Remove</c>). Una volta sola: se c'e'
    /// gia', non si accoda di nuovo, e lo sguardo e l'accodamento stanno insieme sotto il lucchetto della
    /// coda (<see cref="RoutesToBuild.EnqueueIfAbsent"/>; prima erano separati, e due perdenti, o un
    /// perdente e un dispositivo che rimette in coda la sua UDC, la accodavano due volte). La riga di
    /// registro dice se la missione e' tornata in coda o c'era gia' (revisione finale della voce 41).
    /// </remarks>
    private void BackToTheRouteLoop()
    {
        if (!ReferenceEquals(ActiveMissions.Instance[MissionId], this))
            return;

        Navigator.WriteLog(
            RoutesToBuild.Instance.EnqueueIfAbsent(this)
                ? $"Missione {MissionId}: torna al giro delle rotte, che ricalcola la rotta"
                : $"Missione {MissionId}: e' gia' in coda al giro delle rotte",
            LogEntryType.Information);
    }

    /// <summary>
    /// Le due porte della missione ne fanno un cambio cella alla prossima costruzione della rotta
    /// (<see cref="BuildPathToDestination"/>), che allora non va dritta alla destinazione: ci sono la
    /// porta di partenza e quella di arrivo, il tipo e' uno di quelli che le porte riscrivono, e le
    /// porte portano dove <see cref="DoorsMakeAChangeOfCellWhereTheLoadUnitIs"/> vuole. Con le altre due
    /// porte (per esempio un'estrazione dal magazzino verso un polmone) il tipo lo da' il dispositivo di
    /// arrivo e la rotta va dritta. Lo guarda lo spostamento (<c>MissionGateway.Move</c>), che costruisce
    /// la rotta subito solo dove il navigatore andrebbe dritto.
    /// Item 54 (decision 2 and answer 3 of the owner of 13/10): with the load unit out of its cell the
    /// doors no longer make a change of cell, and an extraction bound for a storage becomes a deposit
    /// there, after the hand-off of its pick-up (<see cref="PickedExtractionBecomingADeposit"/>): that
    /// too keeps the mission off the straight route, because the wait may involve the warehouse and the
    /// move is synchronous. Before item 54 this was <c>TheDoorsMakeItAChangeOfCell</c>.
    /// </summary>
    /// <remarks>
    /// Le porte si cercano nella mappa del database del WCS, come fa la costruzione: niente
    /// gestionale. Senza la mappa (nessuno store) le due porte non portano a nessun dispositivo, e la
    /// costruzione ne farebbe un cambio cella: qui la risposta e' la stessa. With the load unit out of
    /// its cell (item 54) it does not: the rebuild finds no device of arrival and writes nothing, and
    /// here the answer is false, so the move tries the straight route and finds none.
    /// </remarks>
    internal async Task<bool> TheDoorsKeepItOffTheStraightRoute()
    {
        // Item 54: the move builds the route of a change of cell at once (Navigator.GoesStraightToTheDestination);
        // without its two doors the rebuild has no cells to go between, and the route loop builds it as
        // before.
        if (MissionType == MissionTypes.ChangeStoreLocation &&
            (string.IsNullOrEmpty(PickUpDoorCode) || string.IsNullOrEmpty(StorageDoorCode)))
            return true;

        if (string.IsNullOrEmpty(PickUpDoorCode) || string.IsNullOrEmpty(StorageDoorCode) ||
            MissionType is MissionTypes.PickUpAndStoreAgain or MissionTypes.DefragmentCell)
            return false;

        var sourceGuid = await DbInfo.GetItemId(PickUpDoorCode);
        var destinationGuid = await DbInfo.GetItemId(StorageDoorCode);

        // Item 54: out of its cell the doors no longer make a change of cell, but an extraction bound for
        // a storage becomes a deposit there, and the rebuild waits for the hand-off of the pick-up
        // first (BecomeADepositAfterThePickUpHandOffAsync), which may call the warehouse: the move,
        // synchronous, leaves that to the route loop too.
        return DoorsMakeAChangeOfCellWhereTheLoadUnitIs(sourceGuid, destinationGuid) ||
               PickedExtractionBecomingADeposit(PathDevices.Instance[destinationGuid]);
    }

    /// <summary>
    /// Il ricollocatore di un cambio cella (<see cref="RelocateItemId"/>): quello del magazzino da cui
    /// l'UDC parte, che la preleva - la coda di prelievo dopo un riavvio
    /// (<c>DbInfo.AddMissionToPickUpQueue</c>) e lo step di prelievo
    /// (<see cref="GetStepsToChangeStoreLocation"/>) lo usano - e solo senza di lui quello del
    /// magazzino di arrivo. Il deposito nel magazzino di arrivo e' uno step della rotta come gli
    /// altri. Fino alla revisione finale della voce 39 era quello di arrivo: lo stesso, finche' i cambi
    /// cella restavano nel magazzino di un ricollocatore; il cambio cella dell'operatore verso il
    /// magazzino di un altro ricollocatore (risposta 1 del 2/10) metteva il prelievo, dopo un riavvio,
    /// nella coda di un dispositivo che dalla cella di partenza non preleva.
    /// </summary>
    private static Guid PickUpRelocator(AutomationDevice? source, AutomationDevice? destination) =>
        source?.RelocateItem?.ItemGuid ?? destination?.RelocateItem?.ItemGuid ?? Guid.Empty;

    /// <summary>
    /// La regola delle due porte di <see cref="BuildPathToDestination"/>: la missione diventa un
    /// cambio cella (<see cref="MissionTypes.ChangeStoreLocation"/>) se le porte portano allo stesso
    /// dispositivo, se una delle due non porta a un dispositivo conosciuto, o se portano tutte e due
    /// a un magazzino.
    /// </summary>
    private static bool DoorsMakeAChangeOfCell(Guid sourceGuid, Guid destinationGuid) =>
        sourceGuid == destinationGuid ||
        PathDevices.Instance[sourceGuid] is not { } source ||
        PathDevices.Instance[destinationGuid] is not { } destination ||
        source.DeviceType == DeviceTypes.Storage && destination.DeviceType == DeviceTypes.Storage;

    /// <summary>
    /// The two-doors rule (<see cref="DoorsMakeAChangeOfCell"/>) looking at where the load unit is
    /// (item 54, decision 2 of the owner of 13/10): the doors make a change of cell only while the load
    /// unit is still in its storage cell (<see cref="LoadUnitIsInItsStorageCell"/>). Out of the cell the
    /// mission is what it physically is, like decision 8 of 2/10 for the operator's commit: a deposit
    /// towards a storage (<see cref="PickedExtractionBecomingADeposit"/> for an extraction), otherwise
    /// what the destination says. A change of cell already under way, with the load unit on its
    /// relocator, stays one: the relocator carries it from one cell to the other.
    /// </summary>
    /// <remarks>
    /// Before item 54 the rule did not look at the load unit: an extraction taken out of its cell and
    /// given a cell of the same storage on an entry point (case UC256355) became a change of cell whose
    /// first step started from the storage, not from the conveyor the load unit was on, and nobody ran it.
    /// </remarks>
    private bool DoorsMakeAChangeOfCellWhereTheLoadUnitIs(Guid sourceGuid, Guid destinationGuid) =>
        DoorsMakeAChangeOfCell(sourceGuid, destinationGuid) &&
        (LoadUnitIsInItsStorageCell() ||
         MissionType == MissionTypes.ChangeStoreLocation && Reaches(PositionItemId, RelocateItemId));

    /// <summary>
    /// An extraction whose load unit is out of its storage cell, bound for a storage by its doors: the
    /// two-doors rule makes it a deposit, with the fact of item 45 if the pick-up had served something
    /// (<see cref="BecomeADepositOfAPickedExtraction"/>; answer 3 of the owner of 13/10, item 54).
    /// </summary>
    private bool PickedExtractionBecomingADeposit(AutomationDevice? target) =>
        target is { DeviceType: DeviceTypes.Storage } && MissionType == MissionTypes.Extraction &&
        !LoadUnitIsInItsStorageCell();

    /// <summary>
    /// L'UDC della missione e' ancora nella sua cella di magazzino: la missione non ha una posizione e
    /// la sua sorgente e' un magazzino (un'estrazione fra l'attivazione e il prelievo, o ricaricata dopo
    /// un riavvio), oppure la sua posizione e' un magazzino (un'estrazione di gruppo). Un'estrazione
    /// cosi', mandata dall'operatore in un altro magazzino, e' un cambio cella (risposta 1 del
    /// proprietario del 2/10); con la posizione su un dispositivo che non e' un magazzino e' gia'
    /// prelevata, e diventa un deposito (decisione 8).
    /// </summary>
    internal bool LoadUnitIsInItsStorageCell() =>
        PositionItem is { } position
            ? position.DeviceType == DeviceTypes.Storage
            : SourceItem is { DeviceType: DeviceTypes.Storage };

    /// <summary>
    /// The mission keeps its destination whatever the storing mode of the device it is on (item 54,
    /// decision 1 and answer 8 of the owner of 13/10): it is bound for a known device that is neither a
    /// storage nor a group with storages in it, and has not reached it (<see cref="Reaches"/>). Such a
    /// mission is not a deposit: a storing mode (<c>LookWhereAllocateLu</c> 1-5, forcing included) does
    /// not ask the warehouse for a cell, does not give it the selector of its load unit type, and does
    /// not rewrite door or destination; the route goes straight to its destination
    /// (<c>Navigator.FindWhereStoreLu</c>, and the group selectors that bypass it). The rule is this one
    /// property: whoever writes the storing mode over a mission looks here.
    /// </summary>
    /// <remarks>
    /// Before item 54 an extraction bound for the bay OUT02, moved by hand onto the entry point CV07,
    /// got a cell from the warehouse there, and the cell rewrote its destination (case UC256355). A
    /// mission bound for a storage, with no destination, or already where it was going, is searched a
    /// cell as before. The mode <c>No</c> is not a storing mode: its fixed destination still applies.
    /// </remarks>
    internal bool KeepsItsDestination =>
        DestinationItem is { } destination && !Reaches(PositionItemId, destination.ItemGuid) &&
        destination.DeviceType != DeviceTypes.Storage &&
        !(destination.DeviceType == DeviceTypes.GroupOfAutomationDevices &&
          destination.BelongedItems.OfType<AutomationDevice>().Any(member => member.DeviceType == DeviceTypes.Storage));

    /// <summary>
    /// L'UDC e' ancora nella sua cella di partenza (<see cref="LoadUnitIsInItsStorageCell"/>), e la
    /// missione ha la porta da cui prelevarla: <see cref="StorageDepth"/> e' la profondita' a cui sta,
    /// e serve al prelievo. La profondita' di una cella prenotata per il deposito aspetta in
    /// <see cref="StorageDepthAfterPickUp"/>.
    /// </summary>
    private bool StillToBePickedUp => LoadUnitIsInItsStorageCell() && !string.IsNullOrEmpty(PickUpDoorCode);

    internal bool CheckRouteToDestination(Guid destinationId) => ProbeRouteSteps(destinationId).Count > 0;

    /// <summary>
    /// La rotta verso una destinazione ipotetica, senza toccare la missione: la destinazione va
    /// solo al costruttore di percorsi, e la missione non se ne accorge. Prima chi doveva
    /// provare una destinazione la assegnava alla missione e poi rimetteva quella vera, e ogni
    /// scrittura cancellava gli step (con uno step in volo, il dispositivo che lo eseguiva
    /// risultava libero per la durata della domanda). Le due passate rispecchiano
    /// <see cref="GetMissionStepsToDestination"/>: la seconda considera anche i dispositivi
    /// non in linea.
    /// </summary>
    /// <param name="destinationId">La destinazione da provare.</param>
    /// <param name="gateway">
    /// Il gateway da provare al posto di quello della missione (<see cref="Guid.Empty"/>: nessuno);
    /// <c>null</c>, il default, tiene quello della missione. Il cambio di destinazione sonda con il
    /// gateway scelto dall'operatore, e senza, prima di toccare la missione (compito 3 delle missioni).
    /// </param>
    /// <param name="sourceId">
    /// La partenza da provare al posto della posizione della missione; <c>null</c>, il default, tiene
    /// la posizione. Il distributore circolare sonda da ognuna delle sue uscite per un'UDC con la
    /// destinazione scelta dall'operatore (voce 40): da questa uscita la destinazione si raggiunge?
    /// </param>
    /// <param name="avoidId">
    /// Un dispositivo che la rotta non attraversa; <c>null</c>, il default, nessuno. Per il distributore
    /// circolare e' il distributore stesso.
    /// </param>
    /// <param name="asType">
    /// Il tipo di missione con cui cercare al posto di quello della missione; <c>null</c>, il default,
    /// tiene il suo. L'arrivo di un <see cref="MissionTypes.PickUpAndStoreAgain"/> cerca la rotta del
    /// deposito che la missione diventa scrivendola (<see cref="HandlePickUpAndStoreAgainMission"/>, voce 41).
    /// </param>
    internal List<MissionStep> ProbeRouteSteps(Guid destinationId, Guid? gateway = null, Guid? sourceId = null, Guid? avoidId = null,
        MissionTypes? asType = null)
    {
        if (destinationId == Guid.Empty) return [];

        try
        {
            var steps = new PathBuilder(this, destinationId, gateway, sourceId, avoidId, asType).GetRouteSteps();
            if (steps == null || steps.Count == 0)
                steps = new PathBuilder(this, destinationId, gateway, sourceId, avoidId, asType).GetRouteSteps(true);
            return steps ?? [];
        }
        catch (Exception ex)
        {
            // Stessa rete di GetMissionStepsToDestination: chi sonda (la scelta fra due
            // destinazioni fisse, la ricerca del posto di deposito) ripristina lo stato del
            // dispositivo fuori da un finally, e un'eccezione qui lo lascerebbe in Recovering.
            var from = sourceId is { } source ? $" from {source}" : "";
            Navigator.WriteLog($"Error probing the route of {MissionId}{from} towards {destinationId}: {ex.Message}", LogEntryType.Error);
            return [];
        }
    }

    /// <summary>
    /// La missione diventa un cambio cella, con il ricollocatore che preleva dalla cella di partenza
    /// (<see cref="PickUpRelocator"/>), se c'e'. La stessa regola per la ricostruzione della rotta
    /// (<see cref="BuildPathToDestination"/>, le due porte di due magazzini) e per il commit del cambio
    /// dell'operatore (<see cref="CommitRoute"/>), che la scrivevano due volte (revisione finale del primo
    /// giro della voce 39, C57). Prima il ricollocatore, poi il tipo, come la ricostruzione: una riga del
    /// database del WCS scritta fra le due e' un'estrazione con il ricollocatore nuovo, non un cambio
    /// cella con quello vecchio.
    /// </summary>
    /// <param name="cellOf">Il magazzino della cella da cui l'UDC parte.</param>
    /// <param name="target">Il magazzino in cui va.</param>
    private void BecomeAChangeOfCell(AutomationDevice? cellOf, AutomationDevice? target)
    {
        var relocator = PickUpRelocator(cellOf, target);
        if (relocator != Guid.Empty)
            RelocateItemId = relocator;

        MissionType = MissionTypes.ChangeStoreLocation;
    }

    /// <summary>
    /// La missione lascia il suo ordine: la riga della coda di estrazione e l'assegnazione (il GUID e il
    /// numero). Solo la missione: il gestionale lo sa gia' da chi chiama. La stessa al commit del cambio
    /// cella (<see cref="CommitRoute"/>) e alla chiusura della missione dopo le segnalazioni
    /// (<c>Api.MissionGateway.CloseTheMissionAsync</c>), che la scrivevano due volte (C57).
    /// </summary>
    internal void LeaveTheOrder()
    {
        ExtractionQueueId = 0;
        GuidAssignmentLu = Guid.Empty;
        LuNoteAssignmentId = 0;
    }

    /// <summary>
    /// La cella di deposito che il gestionale ha prenotato per un cambio di destinazione verso un
    /// magazzino (compito 3 delle missioni), con l'indirizzo di campo: i campi che il flusso del deposito normale
    /// scrive sulla missione (<c>SetDataForStoreInAnyAvailableCellDifferentStoreLocation</c>).
    /// </summary>
    /// <param name="Kept">
    /// La cella che la missione ha gia', tenuta da un cambio che rifa' solo la rotta (l'operatore che
    /// sceglie di nuovo il magazzino cambiando solo il gateway, decisione 6 del proprietario del 2/10):
    /// il commit non riscrive niente della cella (<see cref="TheCellItHas"/>).
    /// </param>
    internal sealed record StorageCellChoice(string DoorCode, string? Asi, string? Subitem, string? Partition, int StorageDepth,
        bool Kept = false);

    /// <summary>
    /// La cella che la missione ha, da tenere al commit di un cambio che rifa' solo la rotta: porta,
    /// indirizzo, profondita' e il segno della prenotazione restano come sono. Riscriverli come una
    /// cella appena prenotata sbaglierebbe due cose: il segno «la cella e' una prenotazione sua» si
    /// alzerebbe anche su una porta che non lo e' (il deposito chiederebbe al gestionale una
    /// prenotazione che non c'e'), e con l'UDC ancora da prelevare la profondita' a cui sta finirebbe
    /// in quella del deposito.
    /// </summary>
    /// <remarks>
    /// Una sola, per tutte le missioni, e con i campi vuoti: il commit guarda solo <c>Kept</c>, e la cella
    /// resta quella che la missione ha al commit. Prima portava porta, indirizzo e profondita' letti dalla
    /// missione prima della sonda, che nessuno leggeva (revisione finale del primo giro della voce 39, C17).
    /// </remarks>
    internal static StorageCellChoice TheCellItHas { get; } = new(string.Empty, null, null, null, 0, Kept: true);

    /// <summary>Come e' finita una scrittura della rotta con <see cref="TryWriteRoute"/>.</summary>
    internal enum RouteWriteOutcome
    {
        /// <summary>La rotta nuova e' scritta, e la versione e' salita.</summary>
        Written,

        /// <summary>
        /// Perso: la versione non e' piu' quella letta all'inizio del calcolo, qualcun altro ha scritto
        /// la rotta nel frattempo. Niente e' stato toccato, e il calcolo si butta.
        /// </summary>
        Lost,

        /// <summary>
        /// Chi scriveva ha rinunciato nei suoi campi (per esempio uno step in volo): gli step nuovi non
        /// si sono scritti. Quello che i campi hanno gia' fatto resta, e lo dice chi scrive.
        /// </summary>
        Refused,

        /// <summary>
        /// La missione aspetta il rigetto dal campo rimandato (<see cref="FieldRejectionPostponed"/>), che
        /// il giro delle rotte rifa' (<see cref="RedoInTheRouteLoopAsync"/>): una rotta calcolata non si
        /// scrive, e niente e' stato toccato. Non e' <see cref="Lost"/>: nessun altro ha cambiato la rotta,
        /// e chi chiama non lo conta come una rotta presa da un altro (il freno della ricerca della cella,
        /// la riga di registro, il motivo del rifiuto all'operatore). Fino al secondo giro di correzioni del
        /// compito 8 della voce 41 la guardia rispondeva <see cref="Lost"/>.
        /// </summary>
        AwaitingRejection
    }

    /// <summary>
    /// La versione della rotta (voce 41, disegno C): chi calcola una rotta fuori dai lucchetti la legge
    /// prima di cominciare e la passa a <see cref="TryWriteRoute"/>, che scrive solo se e' ancora
    /// quella. Sale a ogni scrittura della rotta, a ogni rimozione, a ogni cambio della destinazione e a
    /// ogni cambio della posizione.
    /// </summary>
    internal long RouteVersion => Interlocked.Read(ref _routeVersion);

    /// <summary>
    /// Cucitura per le prove (nulla in esercizio): <see cref="TryWriteRoute"/> la chiama sotto il
    /// lucchetto della rotta, con la rotta vecchia gia' tolta e la nuova non ancora scritta. Una prova
    /// ci ferma uno scrittore nel punto in cui un secondo scrittore, senza il lucchetto, lascerebbe
    /// alla missione una rotta mista.
    /// </summary>
    internal Action? RouteWritePauseForTesting { get; set; }

    /// <summary>
    /// Cucitura per le prove (nulla in esercizio): <see cref="RemoveTheRouteUnlessAStepIsInFlight"/> e
    /// <see cref="LeaveUnlessAStepIsInFlight"/> la chiamano sotto il lucchetto della rotta, dopo aver visto
    /// che nessuno step e' in volo e prima di togliere la rotta (o la missione). Una prova ci ferma chi
    /// toglie mentre un dispositivo avvia uno step: e' il punto della corsa 5 della ricognizione della
    /// voce 41, con il gettone che arriva dopo lo sguardo.
    /// </summary>
    internal Action? RouteRemovalPauseForTesting { get; set; }

    /// <summary>
    /// Cucitura per le prove (nulla in esercizio): <see cref="LeaveUnlessAStepIsInFlight"/> la chiama prima
    /// di prendere il lucchetto della rotta, cioe' fra il primo sguardo allo step in volo di
    /// <c>ActiveMissions.Remove</c> e il secondo, sotto il lucchetto. Una prova ci fa partire uno step: e'
    /// il ramo «partito mentre usciva» della rimozione.
    /// </summary>
    internal Action? LeavePauseForTesting { get; set; }

    /// <summary>
    /// Cucitura per le prove (nulla in esercizio): <see cref="TryTakeTheStepToken(long, MissionStep, Action, out long)"/>
    /// la chiama subito prima di prendere il lucchetto della rotta, con la versione e lo step gia' letti da
    /// chi avvia. Una prova che vede la cucitura passata e poi il thread fermo sa che e' fermo sul
    /// lucchetto della rotta, non in un'attesa qualunque prima (giro finale delle correzioni della voce 41).
    /// </summary>
    internal Action? StepTokenPauseForTesting { get; set; }

    /// <summary>
    /// Cucitura per le prove (nulla in esercizio): <see cref="BuildPathToDestination"/> la aspetta con
    /// la rotta gia' calcolata e non ancora scritta, fuori da ogni lucchetto. Una prova ci ferma un
    /// costruttore, per esempio quello dell'ingresso fra le attive, e intanto fa scrivere la rotta a un
    /// altro: e' il punto delle corse della ricognizione della voce 41.
    /// </summary>
    internal Func<Task>? RouteBuildPauseForTesting { get; set; }

    /// <summary>
    /// Cucitura per le prove (nulla in esercizio): l'arrivo di un <see cref="MissionTypes.PickUpAndStoreAgain"/>
    /// (<see cref="HandlePickUpAndStoreAgainMission"/>) la aspetta dopo aver letto la porta di deposito,
    /// prima di scrivere: e' il punto della corsa 2 della ricognizione della voce 41.
    /// </summary>
    internal Func<Task>? ArrivalPauseForTesting { get; set; }

    /// <summary>
    /// Cucitura per le prove (nulla in esercizio): <see cref="CommitRoute"/> la chiama per prima, con
    /// il commit del cambio gia' deciso (la missione ancora attiva e ancora sulla posizione da cui la
    /// rotta parte) e niente ancora scritto. Una prova ci sposta la missione: e' il punto della corsa 3
    /// della ricognizione della voce 41.
    /// </summary>
    internal Action? CommitRoutePauseForTesting { get; set; }

    /// <summary>
    /// Cucitura per le prove (nulla in esercizio): lo spostamento della missione (quello dell'operatore
    /// e il comando «sposta l'UDC») la chiama con la rotta vecchia gia' tolta e la posizione non ancora
    /// cambiata. Una prova ci fa scrivere la rotta a un altro scrittore.
    /// </summary>
    internal Action? MoveWritePauseForTesting { get; set; }

    /// <summary>
    /// Cucitura per le prove (nulla in esercizio): il rigetto (<see cref="RejectTo"/>) la aspetta con la
    /// porta del punto di scarto gia' letta, prima di scrivere la destinazione. Una prova ci fa scrivere
    /// la rotta a un altro scrittore e partire il suo step.
    /// </summary>
    internal Func<Task>? RejectionPauseForTesting { get; set; }

    /// <summary>
    /// La primitiva di scrittura della rotta (voce 41, disegno C): breve e sincrona, sotto il lucchetto
    /// della rotta della missione. Riguarda la versione, scrive i campi che vanno con la rotta, toglie
    /// gli step vecchi, mette i nuovi e fa salire la versione, tutto insieme: nessuno scrittore che
    /// prende lo stesso lucchetto passa in mezzo, e la missione non resta mai con gli step di due
    /// scrittori.
    /// </summary>
    /// <param name="seenVersion">
    /// La versione letta (<see cref="RouteVersion"/>) prima di calcolare la rotta: se nel frattempo e'
    /// cambiata la rotta non si scrive, e la risposta e' <see cref="RouteWriteOutcome.Lost"/>. Con la
    /// versione ancora quella, degli step da scrivere e un rigetto dal campo rimandato
    /// (<see cref="_fieldRejectionPending"/>), la rotta non si scrive lo stesso, e la risposta e'
    /// <see cref="RouteWriteOutcome.AwaitingRejection"/>.
    /// <c>null</c> per chi scrive senza confronto, ed e' una scelta, non un passaggio: il setter di
    /// <see cref="MissionSteps"/>, per cui vince l'ultimo che scrive, ma per intero; lo spostamento
    /// (<see cref="MoveTo"/>) e il rigetto (<see cref="RejectTo"/>), che sono decisioni prese adesso e
    /// vincono (decisione 2 del proprietario del 10/10, e il rigetto come prima della voce 41).
    /// </param>
    /// <param name="steps">La rotta nuova, assegnata cosi' com'e'.</param>
    /// <param name="fields">
    /// I campi che vanno con la rotta (la destinazione, la porta, il gateway), scritti dopo il confronto
    /// e prima degli step; falso per rinunciare, e gli step non si scrivono. Gira con il lucchetto
    /// preso: niente <c>await</c> e niente gestionale. Gli annunci che i setter fanno da se' girano qui
    /// dentro, sotto il lucchetto: il setter della posizione (<c>MissionChanged</c>, il lavoro di
    /// <see cref="PositionWork"/>, che parte in un <c>Task.Run</c>) nello spostamento, il setter della
    /// destinazione e quello di <c>RunningStepId</c> (attraverso <see cref="RemoveMissionSteps"/>) nel
    /// rigetto e nel commit, e le copie della porta che la lasciano nel commit
    /// (<c>CopiesOfTheDoor.Leave</c>). Gli annunci espliciti, quelli che nessun setter fa, li fa chi
    /// chiama, dopo, fuori dal lucchetto (<see cref="CommitRoute"/>).
    /// </param>
    /// <param name="writer">Chi scrive, per la riga di registro.</param>
    /// <returns>Come e' finita: vedi <see cref="RouteWriteOutcome"/>.</returns>
    /// <remarks>
    /// <para>
    /// Chi perde non ritenta qui: il calcolo era su una fotografia vecchia, e chi l'ha fatto rimette
    /// la missione al giro delle rotte del navigatore, che la ricalcola (decisione 1 del proprietario
    /// del 10/10). Lo decide chi chiama, che sa che cosa vuol dire per lui.
    /// </para>
    /// <para>
    /// Gli step vecchi si tolgono qui dalla collezione, senza passare dai dispositivi: chi tiene un
    /// gettone di uno step della rotta li toglie prima nei suoi campi, come fa
    /// <see cref="CommitRoute"/> (<see cref="RemoveRouteAndItsSeats"/>). Gli step in volo li guarda
    /// chi scrive, nei campi: il setter di <see cref="MissionSteps"/>, che non li guardava, non li
    /// guarda neanche adesso.
    /// </para>
    /// </remarks>
    internal RouteWriteOutcome TryWriteRoute(long? seenVersion, List<MissionStep> steps, Func<bool>? fields = null,
        [CallerMemberName] string? writer = null)
    {
        lock (_route)
        {
            if (seenVersion is { } seen && seen != _routeVersion)
            {
                Navigator.WriteLog(
                    $"Missione {MissionId}: {writer} non scrive la rotta, calcolata sulla versione {seen} che intanto e' diventata {_routeVersion}",
                    LogEntryType.Information);
                return RouteWriteOutcome.Lost;
            }

            // Un rigetto dal campo rimandato (FieldRejectionPostponed) aspetta il giro delle rotte: una
            // rotta calcolata non si scrive, e chi l'ha calcolata rimette la missione al giro, che rifa'
            // prima il rigetto (RedoInTheRouteLoopAsync, che il segno lo toglie prima di rigettare).
            // Senza, l'UDC ripartiva verso la destinazione di prima appena finito lo step in volo (primo
            // giro di correzioni del compito 8 della voce 41). Un esito suo, non Lost: nessuno ha
            // cambiato la rotta, e chi chiama non conta un fallimento ne' scrive che l'ha cambiata un
            // altro (secondo giro). Chi scrive senza confronto (lo spostamento, il rigetto) e chi toglie
            // soltanto passano.
            if (seenVersion != null && steps.Count > 0 && _fieldRejectionPending != null)
            {
                Navigator.WriteLog(
                    $"Missione {MissionId}: {writer} non scrive la rotta, la missione aspetta il rigetto dal campo che il giro delle rotte rifa'",
                    LogEntryType.Information);
                return RouteWriteOutcome.AwaitingRejection;
            }

            if (fields != null && !fields())
                return RouteWriteOutcome.Refused;

            Collections.MissionSteps.Instance.RemoveAll(x => x.MissionId == MissionId);
            RouteWritePauseForTesting?.Invoke();
            Collections.MissionSteps.Instance.AddRange(steps);
            Interlocked.Increment(ref _routeVersion);
            return RouteWriteOutcome.Written;
        }
    }

    /// <summary>
    /// Sposta la missione su un altro dispositivo senza la sua rotta: toglie la rotta e cambia la
    /// posizione in un colpo solo, dentro la primitiva della rotta (<see cref="TryWriteRoute"/>, senza
    /// confronto: lo spostamento e' un fatto e vince sempre, decisione 2 del proprietario del 10/10).
    /// Lo usano lo spostamento dell'operatore (<c>MissionGateway.Move</c>) e il comando «sposta
    /// l'UDC» (<c>ExecutableCommands.MoveLuToItem</c>).
    /// </summary>
    /// <param name="positionId">Il dispositivo su cui la missione va.</param>
    /// <param name="first">
    /// Quello che lo spostamento guarda e scrive prima di togliere la rotta, con il lucchetto preso:
    /// falso per rinunciare, e allora niente e' tolto ne' spostato (<see cref="RouteWriteOutcome.Refused"/>).
    /// Le stesse regole dei campi della primitiva: niente <c>await</c> e niente gestionale.
    /// </param>
    /// <remarks>
    /// <para>
    /// Prima la rimozione e il cambio di posizione erano due scritture separate. In mezzo poteva
    /// passare uno scrittore della rotta che aveva letto la versione dopo la rimozione e la posizione
    /// vecchia: la sua rotta, dal posto vecchio, restava alla missione gia' sul posto nuovo (corsa 3
    /// della ricognizione della voce 41, con il cambio dell'operatore o con il giro delle rotte al posto
    /// di quello scrittore). Insieme sotto il lucchetto, chi ha calcolato dal posto vecchio trova la
    /// versione cambiata e non scrive; chi scrive dopo calcola dal posto nuovo.
    /// </para>
    /// <para>
    /// Sotto il lucchetto girano anche gli annunci che la rimozione (il setter di <c>RunningStepId</c>) e
    /// il setter della posizione fanno da se', come gia' sotto la rimozione e il setter della
    /// destinazione; il lavoro di sfondo della posizione (<see cref="PositionWork"/>) parte in un
    /// <c>Task.Run</c> e prende il lucchetto dopo, se gli serve. La missione che ha perso la rotta torna
    /// al giro delle rotte: ce la mette chi la sposta, che sa se puo' costruirla subito.
    /// </para>
    /// </remarks>
    internal RouteWriteOutcome MoveTo(Guid positionId, Func<bool>? first = null) =>
        TryWriteRoute(null, [], () =>
        {
            if (first != null && !first())
                return false;

            RemoveMissionSteps();
            MoveWritePauseForTesting?.Invoke();
            PositionItemId = positionId;
            return true;
        });

    /// <summary>
    /// Toglie la rotta che un dispositivo ha visto senza uno step da fare per lui (la strategia
    /// <c>Standard</c>), solo se e' ancora quella che ha visto: la versione della rotta e' ancora
    /// <paramref name="seenVersion"/>, letta prima di guardare lo step da fare.
    /// </summary>
    /// <returns>
    /// Falso se intanto la rotta l'ha scritta un altro: resta com'e', e chi guardava va avanti come con
    /// uno step da fare. Vero se la rotta e' tolta, o se non c'era niente da togliere.
    /// </returns>
    /// <remarks>
    /// Lo step da fare e' nullo anche mentre un costruttore e' fra la sua rimozione e la sua scrittura
    /// (<see cref="BuildPathToDestination"/>): tolta senza confronto, la rotta appena scritta andava persa
    /// e la missione faceva un giro di coda in piu' (corsa 6 della ricognizione della voce 41). Senza
    /// niente da togliere la versione non sale, come in <see cref="RemoveMissionSteps"/>: un costruttore
    /// in corso non perde per una rimozione che non c'e' stata.
    /// </remarks>
    internal bool RemoveTheRouteSeenWithoutAStepToDo(long seenVersion) =>
        TryWriteRoute(seenVersion, [], () =>
        {
            if (!Collections.MissionSteps.Instance.Any(x => x.MissionId == MissionId))
                return false;

            RemoveMissionSteps();
            return true;
        }) != RouteWriteOutcome.Lost;

    /// <summary>
    /// Il tratto di commit del cambio di destinazione dell'operatore (compito 3 delle missioni), e della cella persa
    /// che il navigatore rifa' con lo stesso flusso: la destinazione (con il segno, per l'operatore),
    /// il gateway scelto e la rotta gia' sondata con <see cref="ProbeRouteSteps"/>, nell'ordine che non
    /// lascia la missione a meta'. Non cerca niente e non chiama il gestionale: chi chiama ha gia'
    /// fatto tutte e due le cose, fuori da ogni lucchetto, e qui tiene il cambio cominciato
    /// (<see cref="TryBeginOperatorChange"/>, che ferma gli avvii degli step della missione); il resto
    /// lo esclude il confronto della versione della rotta (<paramref name="seenRouteVersion"/>).
    /// </summary>
    /// <param name="destinationId">La destinazione scelta: il dispositivo, o per un magazzino quello della porta prenotata.</param>
    /// <param name="steps">La rotta sondata verso <paramref name="destinationId"/>, assegnata cosi' com'e'.</param>
    /// <param name="gateway">Il gateway scelto dall'operatore, <see cref="Guid.Empty"/> per nessuno.</param>
    /// <param name="cell">
    /// La cella che il gestionale ha prenotato per questa missione, per un magazzino; <c>null</c> per
    /// ogni altro dispositivo. Con lei la missione prende il segno della cella sua
    /// (<see cref="StorageCellReserved"/>): chi passasse una cella non prenotata per lei farebbe
    /// chiedere al deposito una prenotazione che il gestionale non ha. La cella che la missione ha
    /// gia' (<see cref="TheCellItHas"/>) resta com'e', segno compreso.
    /// </param>
    /// <param name="bufferDoorCode">Il codice porta di un polmone, letto prima dal database del WCS.</param>
    /// <param name="operatorChoice">
    /// Vero per il cambio dell'operatore: la destinazione si scrive con il segno. Falso quando il
    /// navigatore rifa' la cella persa di una destinazione della configurazione
    /// (<c>MissionGateway.KeepTheStorageCellAsync</c>): il cambio che l'aveva fatta perdere e' stato
    /// rifiutato, e la destinazione resta della configurazione, senza segno.
    /// </param>
    /// <param name="changeOfCell">
    /// Vero per un'estrazione con l'UDC ancora nella sua cella (<see cref="LoadUnitIsInItsStorageCell"/>)
    /// che l'operatore manda in un altro magazzino: diventa un cambio cella, e lascia estrazione e
    /// assegnazione, che il gestionale ha gia' saputo prima della prenotazione
    /// (<c>MissionGateway.ReserveACellAndCommitAsync</c>). Falso in ogni altro caso, e sempre per il
    /// navigatore.
    /// </param>
    /// <param name="seenRouteVersion">
    /// La versione della rotta (<see cref="RouteVersion"/>) letta da chi chiama: se intanto e' cambiata il
    /// commit non scrive niente (<see cref="TryWriteRoute"/>). Obbligatoria: il commit confronta sempre
    /// (voce 41; fino al compito 5 il cambio dell'operatore passava <c>null</c> e scriveva senza
    /// confronto). <c>MissionGateway.Commit</c> la legge dopo la sonda e la prenotazione nel gestionale, e
    /// subito prima di guardare che la missione sia ancora sulla posizione da cui la rotta e' stata
    /// sondata: quello che e' successo prima della lettura lo prende lo sguardo alla posizione (uno
    /// spostamento la cambia), quello che succede dopo il confronto.
    /// </param>
    /// <param name="beforeWriting">
    /// Quello che va fatto solo se il commit scrive, prima delle righe della missione (piu' esattamente:
    /// se il commit passa il confronto, la guardia del rigetto rimandato e lo sguardo allo step in volo;
    /// se dopo la scrittura della destinazione fosse scartata, e con il cambio cominciato non succede, e'
    /// gia' girato, come la rimozione della rotta vecchia): le copie della
    /// porta che la lasciano (<c>MissionGateway.Commit</c>, <c>CopiesOfTheDoor.Leave</c>). Gira con il
    /// lucchetto della rotta preso, dopo il confronto della versione e lo sguardo allo step in volo:
    /// un commit che perde o trova uno step in volo non lo chiama. Prima le copie lasciavano la porta
    /// fuori, prima del commit, e uno spostamento arrivato in mezzo faceva perdere il commit con le
    /// copie gia' senza porta (primo giro di correzioni del compito 4 della voce 41). Le stesse regole
    /// dei campi: niente <c>await</c> e niente gestionale.
    /// </param>
    /// <returns>
    /// <see cref="RouteWriteOutcome.Written"/> se il commit ha scritto.
    /// <see cref="RouteWriteOutcome.Lost"/>, senza toccare niente, se la versione della rotta non e' piu'
    /// <paramref name="seenRouteVersion"/>. <see cref="RouteWriteOutcome.AwaitingRejection"/>, senza toccare
    /// niente, se la missione aspetta il rigetto dal campo rimandato, che il giro delle rotte rifa'.
    /// <see cref="RouteWriteOutcome.Refused"/> se uno step della
    /// missione e' in volo (<see cref="StepInFlight"/>, che vede anche il prelievo e i movimenti di
    /// gruppo): niente e' stato toccato. <see cref="RouteWriteOutcome.Refused"/> anche se la scrittura
    /// della destinazione, dopo, viene scartata: con il cambio cominciato non succede (nessuno step
    /// parte, e nessuno alza il segno), ma se succedesse la rotta vecchia e' gia' tolta, e la riga di
    /// avviso e gli annunci lo dicono. Chi chiama dice il motivo del rifiuto da qui, senza rileggere la
    /// versione dopo (revisione finale della voce 41: prima la risposta era un vero o falso, e
    /// <c>MissionGateway.Commit</c> indovinava il motivo rileggendo <see cref="RouteVersion"/>, che
    /// intanto poteva essere salita per altro).
    /// </returns>
    /// <remarks>
    /// <para>
    /// Non e' <see cref="BuildPathToDestination"/>: quella cancella gli step prima di cercare, rifa'
    /// la ricerca (con un esito che puo' essere diverso da quello sondato) e, con le due porte
    /// valorizzate, puo' riscrivere il tipo di missione. Qui la rotta e' quella che l'operatore ha
    /// visto accettare, e il tipo cambia solo da <see cref="MissionTypes.None"/>, come li', e per
    /// un'estrazione mandata in un magazzino (qui sotto).
    /// </para>
    /// <para>
    /// <b>Un'estrazione mandata in un magazzino</b> diventa quello che e' fisicamente (decisione 8
    /// del proprietario e risposte 1-3 del 2/10). Gia' prelevata - la posizione e' un dispositivo che
    /// non e' un magazzino - e' un deposito: la porta di prelievo si toglie e il tipo diventa
    /// <see cref="MissionTypes.Storing"/>, il tipo che la regola delle porte da' a un deposito. Senza
    /// la porta di prelievo una ricostruzione non la rifa' cambio cella (le due porte di due magazzini,
    /// <see cref="DoorsMakeAChangeOfCell"/>), e lo spostamento costruisce la rotta subito (fino alla
    /// voce 54 il cambio dopo rispondeva anche «non reindirizzabile»). <c>null</c> e non vuoto: il gruppo e il movimento
    /// raggruppato guardano la porta di prelievo con <c>!= null</c>. Con l'UDC ancora nella sua cella e' un cambio cella
    /// (<paramref name="changeOfCell"/>): il tipo diventa <see cref="MissionTypes.ChangeStoreLocation"/>,
    /// la porta di prelievo resta perche' serve al prelievo, il ricollocatore e' quello che la
    /// ricostruzione della rotta scrive per un cambio cella (senza, la ricarica dopo un riavvio puo'
    /// togliere la missione: <c>DbInfo.AddMissionToPickUpQueue</c>), ed estrazione e assegnazione si
    /// staccano, cosi' al prelievo non parte ne' «estratta» ne' «servita».
    /// </para>
    /// <para>
    /// <b>La profondita' della cella prenotata</b> va in <see cref="StorageDepth"/>, che il deposito
    /// legge, solo se l'UDC ha gia' lasciato la sua cella. Con l'UDC ancora nella cella di partenza
    /// (<see cref="LoadUnitIsInItsStorageCell"/>, con la porta di prelievo) <see cref="StorageDepth"/>
    /// e' la profondita' a cui sta, e il prelievo la legge per l'indirizzo della cella: la prenotata
    /// aspetta in <see cref="StorageDepthAfterPickUp"/> e prende il suo posto quando l'UDC esce dalla
    /// cella. Vale per ogni commit con una cella, dell'operatore o del navigatore che la rifa'.
    /// </para>
    /// <para>
    /// L'ordine: gli step vecchi si tolgono passando dai dispositivi (<see cref="RemoveMissionSteps"/>),
    /// cosi' che il gettone di chi teneva lo step torni libero con il suo annuncio e il posto
    /// prenotato sul dispositivo successivo venga restituito; poi la destinazione con il segno, che
    /// il setter accetta di sicuro perche' di step in volo non ce ne sono piu'; poi porta e cella,
    /// che il setter della destinazione riscrive a modo suo (e in Recover non riscrive); poi il
    /// gateway; per ultima la rotta, che il setter della destinazione cancellerebbe se venisse prima.
    /// Tutto dentro la primitiva della rotta (<see cref="TryWriteRoute"/>, voce 41): lo sguardo agli step
    /// in volo, la rimozione e i campi sono i suoi campi, la rotta i suoi step, e nessuna scrittura della
    /// rotta che passa dal suo lucchetto arriva in mezzo. Gli annunci vengono dopo, fuori dal lucchetto.
    /// </para>
    /// <para>
    /// Gli annunci sono espliciti, perche' assegnare gli step non annuncia niente
    /// (<c>MissionSteps.AddRange</c>): la missione, e i dispositivi i cui gettoni sono cambiati. Il
    /// setter di <c>RunningStepId</c> annuncia solo il dispositivo che lo teneva, non quello che
    /// teneva il posto; e un posto tenuto per uno step che non c'e' piu' (appeso) si restituisce qui,
    /// subito, invece di aspettare il prossimo <c>ExecuteStep</c> di quel dispositivo, che a motore
    /// fermo non arriva. I gettoni che restano da restituire sono solo di step finiti male
    /// (<c>Warning</c> o <c>Error</c>) o appesi: con il cambio cominciato nessuno sta avviando uno
    /// step della missione, e uno step in volo ferma il commit qui sopra.
    /// </para>
    /// <para>
    /// <b>A change of cell or a defragmentation redirected</b> (item 54, decision 3 and answers 1 and 2
    /// of the owner of 13/10) takes the type of where it now goes (<see cref="BecomeWhatTheNewDestinationMakesIt"/>),
    /// and with <paramref name="giveBack"/> the cell it was going to goes into the durable queue of the
    /// cells to give back, in the same write as the mission's row (<see cref="QueueTheCellGiveBack"/>).
    /// </para>
    /// </remarks>
    /// <param name="giveBack">
    /// The cell the redirected change of cell or defragmentation was going to (item 54): queued only if
    /// the mission still had its door when the commit writes, and the commit leaves it.
    /// </param>
    internal RouteWriteOutcome CommitRoute(Guid destinationId, List<MissionStep> steps, Guid gateway,
        StorageCellChoice? cell, string? bufferDoorCode, bool operatorChoice, bool changeOfCell, long seenRouteVersion,
        Action? beforeWriting = null, CellGiveBack? giveBack = null,
        Api.RelocatorArrival relocatorArrival = Api.RelocatorArrival.None)
    {
        List<AutomationDevice> touched = [];
        var removed = false;

        CommitRoutePauseForTesting?.Invoke();

        var outcome = TryWriteRoute(seenRouteVersion, steps, Fields);

        // Gli annunci fuori dal lucchetto della rotta: la missione e i dispositivi i cui gettoni sono
        // cambiati, se la rotta vecchia e' stata tolta (vedi le note). Chi ha perso il confronto, o ha
        // trovato uno step in volo, non ha toccato niente.
        if (removed)
        {
            Navigator.EventSink?.MissionChanged(this, Api.EngineMissionChange.StepChanged);
            foreach (var device in touched)
                Navigator.EventSink?.ItemChanged(device);
        }

        return outcome;

        bool Fields()
        {
            if (StepInFlight() is { } inVolo)
            {
                Navigator.WriteLog(
                    $"Destinazione della missione {MissionId} non cambiata: lo step {inVolo.StepId} e' in volo su {inVolo.FromNodeId}",
                    LogEntryType.Warning);
                return false;
            }

            // Item 54: what the mission was before the commit writes, for the change of cell or the
            // defragmentation redirected (see the remarks).
            var redirected = MissionType is MissionTypes.ChangeStoreLocation or MissionTypes.DefragmentCell;
            var doorBefore = StorageDoorCode;

            // Da qui il commit scrive (il setter della destinazione non la scarta: vedi il valore
            // restituito), e chi lascia qualcosa solo per un commit che scrive lo fa adesso, prima delle
            // righe della missione.
            beforeWriting?.Invoke();

            touched = RemoveRouteAndItsSeats();
            removed = true;

            var written = operatorChoice
                ? SetOperatorDestination(destinationId)
                : UnlessChosenByOperator(() => DestinationItemId = destinationId) && DestinationItemId == destinationId;

            if (!written)
            {
                // Non dovrebbe succedere (vedi il valore restituito): se succede, la rotta vecchia e' gia'
                // tolta, e lo si dice invece di farlo passare per «niente e' stato toccato».
                Navigator.WriteLog(
                    $"Destinazione della missione {MissionId} non scritta in {destinationId}: la rotta vecchia e' gia' stata tolta, e la missione resta senza rotta",
                    LogEntryType.Warning);
                return false;
            }

            // Item 54, decision 5: the operator's change writes what the relocator chosen does with the
            // load unit, and every other change of the operator takes the old choice away. The navigator's
            // commit (the lost cell) does not touch it.
            if (operatorChoice)
                RelocatorArrival = relocatorArrival;

            // La cella c'e' (o la destinazione non e' piu' un magazzino): la cella persa e' rifatta. La
            // conferma della cella di prima non vale per quella nuova: chi conferma la nuova lo fa dopo
            // (MissionGateway.KeepTheStorageCellAsync), e il commit dell'operatore non conferma.
            StorageCellLost = false;
            TakeStorageCellConfirmation();

            var target = PathDevices.Instance[destinationId];

            if (cell is { Kept: true })
            {
                // La cella che la missione ha gia' (TheCellItHas): si rifa' solo la rotta, e della cella
                // non si riscrive niente, nemmeno il segno della prenotazione.
            }
            else if (cell != null)
            {
                BufferCellId = 0;
                StorageDoorCode = cell.DoorCode;

                // La cella che il flusso del magazzino ha appena prenotato per questa missione: il deposito
                // chiede se e' ancora sua (MayStartTheDeposit).
                StorageCellReserved = true;
                DestinationAsi = cell.Asi;
                DestinationSubitem = cell.Subitem;
                DestinationPartition = cell.Partition;

                // Con l'UDC ancora nella sua cella la profondita' e' quella a cui sta, e serve al prelievo:
                // la cella prenotata aspetta che l'UDC esca (vedi le note).
                if (StillToBePickedUp)
                {
                    StorageDepthAfterPickUp = cell.StorageDepth;
                }
                else
                {
                    StorageDepth = cell.StorageDepth;
                    StorageDepthAfterPickUp = null;
                }

                Status = MissionStatusTypes.Run;
            }
            else if (target is { DeviceType: DeviceTypes.Buffer or DeviceTypes.LoadingAndBuffer })
            {
                BufferCellId = target.LinkedCellId;
                StorageDoorCode = bufferDoorCode;
            }
            else
            {
                BufferCellId = 0;
                StorageDoorCode = null;
            }

            // Un'estrazione mandata in un magazzino diventa quello che e' fisicamente (vedi le note).
            if (cell != null && MissionType == MissionTypes.Extraction)
            {
                if (changeOfCell)
                {
                    BecomeAChangeOfCell(PositionItem is { DeviceType: DeviceTypes.Storage } cellOf ? cellOf : SourceItem, target);

                    // Il gestionale ha gia' saputo che la missione lascia l'assegnazione e che l'UDC esce
                    // dalla coda di estrazione, prima della prenotazione.
                    LeaveTheOrder();

                    // Il cambio e' registrato: il segno delle segnalazioni partite non serve piu', e una
                    // ricarica non chiude un cambio cella (ChangeOfCellReportsSent). Per ultimo, dopo il
                    // tipo: una riga con il segno e il tipo nuovo resta, una con il segno e l'estrazione
                    // si chiude. La riga la scrive chi chiama, e la aspetta anche in Recover, dove i setter
                    // non scrivono (MissionGateway.TakeTheMarkDownAsync).
                    ChangeOfCellReportsSent = false;
                }
                else if (PositionItem is { DeviceType: not DeviceTypes.Storage })
                {
                    // Gia' prelevata: un deposito, e il gestionale riapre quello che il prelievo aveva
                    // servito (voce 45). La riga della coda va con la riga della missione.
                    BecomeADepositOfAPickedExtraction(
                        $"The operator sent the picked-up extraction to a storage (cell {StorageDoorCode}): it became a deposit");
                }
            }

            // Item 54: a change of cell or a defragmentation redirected is no longer one.
            if (redirected)
                BecomeWhatTheNewDestinationMakesIt(target, toACell: cell != null);

            Gateway = gateway;

            if (MissionType == MissionTypes.None)
                MissionType = MissionTypes.MovingToItem;

            // Item 54: last, so the mission's row that goes with the queue's row is the final one.
            if (redirected && giveBack != null && !string.IsNullOrEmpty(doorBefore) &&
                string.Equals(doorBefore, giveBack.DoorCode, StringComparison.Ordinal) &&
                !string.Equals(StorageDoorCode, giveBack.DoorCode, StringComparison.Ordinal))
                QueueTheCellGiveBack(giveBack, destinationId);

            // La rotta la scrive la primitiva, per ultima, dopo questi campi.
            return true;
        }
    }

    /// <summary>
    /// The cell a redirected change of cell or defragmentation was going to (item 54): its door, and the
    /// cell of the WCS's door map, read before the commit because the commit does not wait.
    /// </summary>
    internal sealed record CellGiveBack(string DoorCode, decimal CellId);

    /// <summary>
    /// A change of cell or a defragmentation redirected by the operator takes the type of where it now
    /// goes (item 54, decision 3 and answer 2 of the owner of 13/10). Towards a storage cell
    /// (<paramref name="toACell"/>): with the load unit still in its cell, a change of cell towards the
    /// new cell, with the relocator that picks it up (<see cref="BecomeAChangeOfCell"/>); out of its cell
    /// a deposit, with no pick-up door, like decision 8 of 2/10. Towards any other device - a bay, a
    /// conveyor, a buffer - an extraction: the pick-up door stays, it serves the pick-up of a load unit
    /// still in its cell.
    /// </summary>
    /// <remarks>
    /// Before item 54 both types answered «not redirectable»: they build their steps their own way
    /// (<see cref="GetStepsToChangeStoreLocation"/>, <see cref="GetStepToDefragmentCell"/>), and a probed
    /// route was not theirs. The commit writes the probed route, and the type now says what it is.
    /// </remarks>
    private void BecomeWhatTheNewDestinationMakesIt(AutomationDevice? target, bool toACell)
    {
        if (toACell && target is { DeviceType: DeviceTypes.Storage })
        {
            if (LoadUnitIsInItsStorageCell())
            {
                BecomeAChangeOfCell(PositionItem is { DeviceType: DeviceTypes.Storage } cellOf ? cellOf : SourceItem, target);
                return;
            }

            PickUpDoorCode = null;
            MissionType = MissionTypes.Storing;
            return;
        }

        MissionType = MissionTypes.Extraction;
    }

    /// <summary>
    /// The write of the mission's row that carries the row of the queue of the cells to give back
    /// (<see cref="QueueTheCellGiveBack"/>), waiting for whoever made the commit to wait for it
    /// (<see cref="TakeTheCellGiveBack"/>). Null if the commit did not queue one.
    /// </summary>
    private Task? _cellGiveBack;

    /// <summary>
    /// The cell the redirected mission was going to goes into the durable queue of the cells to give
    /// back (item 54, answer 1 of the owner of 13/10), in the same write as the mission's row: a stop
    /// between the two leaves neither a mission that forgot its cell with nobody giving it back, nor a
    /// cell given back while the mission still goes there. Under the write lock, like the fact of item
    /// 45 (<see cref="BecomeADepositOfAPickedExtraction"/>); whoever made the commit waits for the write
    /// afterwards, out of every lock. No warehouse here: the dispatcher of the master loop calls it.
    /// </summary>
    private void QueueTheCellGiveBack(CellGiveBack giveBack, Guid destinationId) =>
        QueueTheCellGiveBack(giveBack,
            $"The operator sent the mission to {destinationId}: the cell of the door {giveBack.DoorCode} it was going to goes back to the warehouse");

    private void QueueTheCellGiveBack(CellGiveBack giveBack, string reason)
    {
        var row = new PendingCellGiveBack
        {
            OperationId = Guid.NewGuid(),
            MissionId = MissionId,
            LoadUnitId = LuId,
            CellId = giveBack.CellId,
            DoorCode = giveBack.DoorCode,
            UserId = Navigator.UserId,
            Reason = reason,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        lock (_scritturaSuDb)
        {
            _cellGiveBack = ORM.PathMission.UpdateAndWaitAsync(this, context =>
            {
                Persistence.PendingCellGiveBackStore.Add(context, row);
                return Task.CompletedTask;
            });
        }
    }

    /// <summary>
    /// The write the commit queued for the cell to give back (<see cref="QueueTheCellGiveBack"/>), taken
    /// from the mission: whoever made the commit waits for it out of every lock. Null if there is none.
    /// </summary>
    internal Task? TakeTheCellGiveBack() => Interlocked.Exchange(ref _cellGiveBack, null);

    /// <summary>
    /// Toglie la cella non piu' posseduta e mette la sua restituzione durevole nella stessa scrittura
    /// finale della riga della missione. Serve quando una nuova prenotazione ha sostituito la vecchia
    /// assegnazione per UDC, ma il commit della nuova rotta e' stato rifiutato.
    /// </summary>
    internal bool DropStorageCellAndQueueGiveBack(CellGiveBack giveBack, string reason)
    {
        if (!LeaveTheDoor(lost: true)) return false;
        QueueTheCellGiveBack(giveBack, reason);
        return true;
    }

    /// <summary>
    /// La scrittura della riga della missione che porta con se' la riga della coda del fatto
    /// «l'estrazione prelevata e' diventata un deposito» (<see cref="BecomeADepositOfAPickedExtraction"/>),
    /// in attesa che chi ha fatto il commit la aspetti (<see cref="TakeThePickedExtractionReport"/>).
    /// Nulla se il commit non l'ha messa in coda.
    /// </summary>
    private Task? _pickedExtractionReport;

    /// <summary>
    /// L'estrazione gia' prelevata che l'operatore manda in un magazzino diventa un deposito (porta di
    /// prelievo nulla, tipo <see cref="MissionTypes.Storing"/>), e se il prelievo aveva servito qualcosa
    /// (<see cref="ServedAssignmentId"/>, <see cref="ServedAssignmentGuid"/>,
    /// <see cref="ServedExtractionRowId"/>) il fatto va nella coda durevole del WCS per il gestionale,
    /// che riaprira' l'ordine (voce 45, decisione 1 del proprietario del 10/10).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Una scrittura sola.</b> Il tipo nuovo, i tre campi azzerati e la riga della coda vanno nel
    /// <c>wcs.db</c> nella stessa transazione (<c>ORM.PathMission.UpdateAndWaitAsync</c> con la riga
    /// in piu'): un arresto non lascia ne' un deposito che ha dimenticato il fatto, ne' il fatto di
    /// un'estrazione che la ricarica rimette in piedi. Per questo i campi si scrivono sotto il lock
    /// della scrittura e non con i loro setter, che manderebbero ciascuno una riga per conto suo; gli
    /// annunci vengono dopo, fuori dal lock, come quelli dei setter. Scrive anche in Recover, come il
    /// segno delle segnalazioni del cambio cella: il fatto non si perde perche' il navigatore ricarica.
    /// </para>
    /// <para>
    /// Qui nessun gestionale: lo chiama il dispatcher della coda, a ogni giro del ciclo master. I campi
    /// si azzerano perche' il fatto e' accodato (specifica, sezione 3.5 punto 1), e un secondo cambio
    /// non lo rimanda: la missione e' gia' un deposito. Senza niente di servito - il prelievo non ha
    /// mandato niente al gestionale - resta il cambio di prima, con i due setter.
    /// </para>
    /// <para>
    /// La scrittura si mette in coda allo scrittore qui, sotto il lucchetto della rotta, e chi ha fatto
    /// il commit la aspetta dopo, fuori da ogni lucchetto (<see cref="TakeThePickedExtractionReport"/>).
    /// </para>
    /// <para>
    /// <b>Dopo la consegna del prelievo.</b> I tre campi li scrive la consegna del prelievo
    /// (<see cref="ProcessExtractionQueue"/>, nel lavoro di sfondo dell'arrivo), che non prende il
    /// lucchetto della rotta: l'ordine lo tiene chi chiama il commit, che prima aspetta quel lavoro e
    /// tiene il lucchetto delle estrazioni per il tratto del commit
    /// (<c>MissionGateway.CommitAfterThePickUpHandOffAsync</c>, primo giro di correzioni del compito 3).
    /// </para>
    /// </remarks>
    /// <param name="reason">
    /// Why it became a deposit, for the warehouse's log: the operator's commit, or the route rebuild
    /// (item 54, <see cref="BecomeADepositAfterThePickUpHandOffAsync"/>).
    /// </param>
    private void BecomeADepositOfAPickedExtraction(string reason)
    {
        PendingPickedExtractionReport? report;

        lock (_scritturaSuDb)
        {
            if (_servedAssignmentId <= 0 && _servedAssignmentGuid == Guid.Empty && _servedExtractionRowId <= 0)
            {
                report = null;
            }
            else
            {
                report = new PendingPickedExtractionReport
                {
                    OperationId = Guid.NewGuid(),
                    MissionId = MissionId,
                    LoadUnitId = LuId,
                    AssignmentId = _servedAssignmentId,
                    AssignmentGuid = _servedAssignmentGuid,
                    ExtractionRowId = _servedExtractionRowId,
                    UserId = Navigator.UserId,
                    Reason = reason,
                    CreatedAtUtc = DateTimeOffset.UtcNow
                };

                _pickUpDoorCode = null;
                _missionType = MissionTypes.Storing;
                _servedAssignmentId = 0;
                _servedAssignmentGuid = Guid.Empty;
                _servedExtractionRowId = 0;

                var queued = report;
                _pickedExtractionReport = ORM.PathMission.UpdateAndWaitAsync(this,
                    context =>
                    {
                        Persistence.PendingPickedExtractionReportStore.Add(context, queued);
                        return Task.CompletedTask;
                    });
            }
        }

        if (report == null)
        {
            PickUpDoorCode = null;
            MissionType = MissionTypes.Storing;
            return;
        }

        foreach (var name in new[]
                 {
                     nameof(PickUpDoorCode), nameof(MissionType), nameof(ServedAssignmentId),
                     nameof(ServedAssignmentGuid), nameof(ServedExtractionRowId)
                 })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// La scrittura che il commit ha messo in coda per il fatto «l'estrazione prelevata e' diventata un
    /// deposito» (<see cref="BecomeADepositOfAPickedExtraction"/>), tolta dalla missione: chi ha fatto il
    /// commit la aspetta fuori da ogni lucchetto. Nulla se non ce n'e'.
    /// </summary>
    internal Task? TakeThePickedExtractionReport() => Interlocked.Exchange(ref _pickedExtractionReport, null);

    /// <summary>
    /// The route rebuild makes a picked-up extraction a deposit (<see cref="BecomeADepositOfAPickedExtraction"/>,
    /// item 54, answer 3 of the owner of 13/10) and waits, out of every lock, for the write that carries
    /// the fact of item 45. False if the hand-off of the pick-up did not let go in time: nothing changed.
    /// </summary>
    /// <remarks>
    /// After the hand-off, like the operator's commit (<c>MissionGateway.AfterThePickUpHandOffAsync</c>):
    /// the three fields of what the pick-up served are written by the hand-off, in the background work
    /// of the arrival. Converted before it, the deposit had nothing to reopen, and the hand-off, run
    /// later on the deposit, told the warehouse the order was served.
    /// </remarks>
    private async Task<bool> BecomeADepositAfterThePickUpHandOffAsync(string reason)
    {
        // While the rebuild waited, the operator may have changed the mission (a commit, a move): then
        // it is not converted here, and the route loop rebuilds it as it is now.
        var doorCode = StorageDoorCode;
        var (handedOff, converted) = await Api.MissionGateway.AfterThePickUpHandOffAsync(this, () =>
        {
            if (MissionType != MissionTypes.Extraction || LoadUnitIsInItsStorageCell() || StorageDoorCode != doorCode)
                return false;

            BecomeADepositOfAPickedExtraction(reason);
            return true;
        });

        if (!handedOff || !converted)
        {
            Navigator.WriteLog(
                handedOff
                    ? $"Missione {MissionId}: la rotta non e' ricostruita, la missione e' cambiata mentre si aspettava la consegna del prelievo; torna al giro delle rotte"
                    : $"Missione {MissionId}: la rotta non e' ricostruita, il prelievo dell'estrazione si sta ancora consegnando al gestionale (lucchetto delle estrazioni non preso in tempo); la missione torna al giro delle rotte",
                LogEntryType.Warning);
            return false;
        }

        if (TakeThePickedExtractionReport() is not { } written) return true;

        try
        {
            await written;
        }
        catch (Exception ex)
        {
            Navigator.WriteLog(
                $"Missione {MissionId}, UDC {LuId:0}: l'estrazione gia' prelevata e' diventata un deposito, ma la riga con il fatto per il gestionale non e' stata scritta nel database del WCS ({ex.Message}): il gestionale non riaprira' l'ordine, che va sistemato a mano",
                LogEntryType.Error);
        }

        return true;
    }

    /// <summary>
    /// La cella di deposito non e' piu' della missione: porta, indirizzo di campo e rotta si lasciano
    /// (decisioni del proprietario dell'1/10, «la cella persa»). Nessuna missione va verso una cella
    /// di magazzino che il gestionale non le tiene: la destinazione resta, ed e' il magazzino in cui
    /// il navigatore le prenota un'altra cella con il flusso del cambio, per ogni destinazione,
    /// scelta dall'operatore o dalla configurazione (<see cref="StorageCellLost"/>, che si alza qui).
    /// </summary>
    /// <returns>
    /// False se uno step della missione e' in volo (<see cref="StepInFlight"/>): niente e' stato
    /// toccato, perche' cancellare lo step in volo rende libero a meta' movimento il dispositivo che
    /// lo esegue, e con un deposito in corso la porta e' quella che la sua chiusura riguarda.
    /// </returns>
    /// <remarks>
    /// Chi chiama tiene la missione ferma: il cambio cominciato (<see cref="TryBeginOperatorChange"/>,
    /// il cambio dell'operatore e il navigatore che rifa' la cella), un avvio contato con il posto
    /// delle ricerche (la cella rifiutata dal PLC), o il solo posto delle ricerche del navigatore,
    /// dopo la fine del cambio (<c>Api.MissionGateway.BackToTheLostCellAsync</c>, come lo scarto del
    /// flusso del deposito). In ogni caso nessun cambio dell'operatore comincia intanto, e uno step in
    /// volo lo guarda questo metodo. La rotta si toglie come al commit del cambio, passando dai
    /// dispositivi, con i posti prenotati restituiti e annunciati. Non accoda niente: chi la rimette al
    /// navigatore lo decide.
    /// </remarks>
    internal bool DropStorageCell() => LeaveTheDoor(lost: true);

    /// <summary>
    /// La missione aveva copiato la porta di un'altra missione del gruppo, quella che ha prenotato la
    /// cella (il selettore per tipo di UDC di <c>AutomationDevice.VerifyWhereAllocateLuGrouped</c>), e
    /// quella ha perso la cella (risposta 8 del proprietario dell'8/10): la porta vecchia, l'indirizzo e
    /// la rotta si lasciano come per la cella persa (<see cref="DropStorageCell"/>), ma la missione non
    /// alza <see cref="StorageCellLost"/>. La cella non era sua: non la rifa' il navigatore nel
    /// magazzino, la rifa' la ricerca per tipo del gruppo, che il ciclo del gruppo fa partire per lei
    /// in ogni modalita' di deposito (<see cref="LeftACopiedDoor"/>), e che la puo' rimettere con la
    /// missione da cui aveva copiato, nella sua cella nuova, o altrove.
    /// </summary>
    /// <returns>Come <see cref="DropStorageCell"/>: false, senza toccare niente, con uno step in volo.</returns>
    /// <remarks>Chi chiama tiene la missione ferma, come per <see cref="DropStorageCell"/>.</remarks>
    internal bool DropTheCopiedDoor() => LeaveTheDoor(lost: false);

    /// <summary>Il corpo di <see cref="DropStorageCell"/> e di <see cref="DropTheCopiedDoor"/>.</summary>
    /// <param name="lost">La cella era della missione, che la perde: si alza <see cref="StorageCellLost"/>.</param>
    private bool LeaveTheDoor(bool lost)
    {
        if (StepInFlight() is { } inVolo)
        {
            Navigator.WriteLog(
                $"La cella della missione {MissionId} non e' piu' prenotata, ma lo step {inVolo.StepId} e' in volo su {inVolo.FromNodeId}: porta e rotta restano",
                LogEntryType.Warning);
            return false;
        }

        var touched = RemoveRouteAndItsSeats();

        // La conferma era di questa cella, che non c'e' piu'.
        TakeStorageCellConfirmation();
        // Il segno della copia prima della porta vuota: la riga del database del WCS che la porta
        // vuota scrive lo porta gia', e una copia ricaricata senza porta non resta senza segno.
        LeftACopiedDoorMark = !lost;
        StorageDoorCode = null;
        DestinationAsi = string.Empty;
        DestinationSubitem = string.Empty;
        DestinationPartition = string.Empty;

        // La profondita' della cella persa: con l'UDC ancora da prelevare e' quella in attesa, e
        // StorageDepth, la profondita' a cui l'UDC sta, serve ancora al prelievo.
        if (!StillToBePickedUp) StorageDepth = 0;
        StorageDepthAfterPickUp = null;
        if (lost) StorageCellLost = true;

        Navigator.EventSink?.MissionChanged(this, Api.EngineMissionChange.StepChanged);
        foreach (var device in touched)
            Navigator.EventSink?.ItemChanged(device);

        return true;
    }

    /// <summary>
    /// Toglie la rotta della missione passando dai dispositivi (<see cref="RemoveMissionSteps"/>), e
    /// restituisce subito anche i posti prenotati per i suoi step che nessuno restituirebbe (appesi):
    /// il prossimo <c>ExecuteStep</c> di quel dispositivo, a motore fermo, non arriva.
    /// </summary>
    /// <returns>I dispositivi i cui gettoni sono cambiati, da annunciare.</returns>
    private List<AutomationDevice> RemoveRouteAndItsSeats()
    {
        var oldSteps = ItsStepIds();
        var touched = DevicesHolding(oldSteps);

        RemoveMissionSteps();

        foreach (var device in touched)
        foreach (var stepId in oldSteps)
            device.ReleaseIncomingStep(stepId);

        return touched;
    }

    /// <summary>
    /// I dispositivi che tengono un gettone di uno step della rotta della missione: lo step in
    /// esecuzione (<c>RunningStepId</c>) o il posto prenotato per lui (<c>WaitingStepId</c>). Togliere
    /// la rotta puo' cambiare il loro stato, e il setter di <c>RunningStepId</c> annuncia solo il
    /// dispositivo che lo teneva, non quello che teneva il posto e se lo vede restituire: chi toglie
    /// la rotta li annuncia dopo (lo spostamento, <c>MissionGateway.Move</c>; il commit del cambio di
    /// destinazione). Un posto appeso (tenuto senza che la sorgente abbia lo step) la rimozione non lo
    /// restituisce: lo restituisce <see cref="RemoveRouteAndItsSeats"/>, o il prossimo
    /// <c>ExecuteStep</c> del dispositivo che lo tiene; fino ad allora l'annuncio lo riporta com'e'.
    /// </summary>
    internal List<AutomationDevice> DevicesHoldingItsSteps() => DevicesHolding(ItsStepIds());

    private List<Guid> ItsStepIds() =>
        Collections.MissionSteps.Instance.Where(x => x.MissionId == MissionId).Select(x => x.StepId).ToList();

    private static List<AutomationDevice> DevicesHolding(List<Guid> stepIds) =>
        PathDevices.Instance
            .Where(device => device != null &&
                             stepIds.Any(stepId => device.RunningStepId == stepId || device.IsWaitingForStep(stepId)))
            .Select(device => device!)
            .ToList();

    internal List<MissionStep> GetMissionStepsToDestination()
    {
        try
        {
            var result = new PathBuilder(this).GetRouteSteps();

            // Seconda ricerca, questa volta ammettendo gli elementi offline o in errore. La nuova
            // istanza non serve piu' a tenere separati i due risultati, come chiedeva la voce B22:
            // PathBuilder non conserva stato fra due chiamate, e ogni GetRouteSteps rifa' la
            // fotografia del motore (Snapshot) prima di consegnarla a RouteSearch. Riusare
            // l'istanza di sopra darebbe oggi lo stesso esito.
            if (result == null || result.Count == 0)
                result = new PathBuilder(this).GetRouteSteps(true);

            return result ?? [];
        }
        catch (Exception ex)
        {
            Navigator.WriteLog($"Error getting mission steps for {MissionId}: {ex.Message}", LogEntryType.Error);
            return [];
        }
    }

    /// <param name="selectionType">Dove cercare la cella.</param>
    /// <param name="keepExistingReservation">Tenere la prenotazione che la missione ha, se va ancora bene (compito 17).</param>
    /// <param name="depositor">
    /// Chi fa il deposito, per la ricerca delle celle collegate (<see cref="StoringSelectionTypes.StoreInLinkedCell"/>).
    /// Non dato, e' la posizione: la ricerca che il ricollocatore fa per se', com'era. Dato, la ricerca
    /// e' ancorata a lui ovunque sia la missione - le sue celle collegate, il magazzino della sua cella
    /// collegata, i soli magazzini di cui e' il ricollocatore - e la rotta dalla posizione alla porta
    /// proposta deve esserci davvero, guardata prima di scrivere sulla missione (una porta che non va
    /// non la tocca, e la cella presa la restituisce chi chiama): e' la cella persa di una missione
    /// della configurazione con il magazzino della destinazione pieno, cercata in un altro magazzino
    /// del suo ricollocatore anche quando la missione e' ancora a monte (risposta 5 del proprietario
    /// del 2/10, <c>Api.MissionGateway.KeepTheStorageCellAsync</c>). Le altre ricerche non chiedono la
    /// rotta prima di scrivere, ma l'ancora la usano tutte: la cella collegata con cui la ricerca chiede
    /// al gestionale e' quella di chi deposita (<see cref="FindRouteAllocateLu"/>).
    /// </param>
    /// <returns>
    /// Vero se la cella e' trovata, anche quando la rotta verso di lei l'ha cambiata un altro mentre la
    /// si scriveva e la rifa' il giro delle rotte; chi deve distinguere i due casi usa
    /// <see cref="SearchTheStoreAsync"/>.
    /// </returns>
    internal async Task<bool> GetStoreToWarehouse(StoringSelectionTypes selectionType, bool keepExistingReservation,
        AutomationDevice? depositor = null) =>
        (await SearchTheStoreAsync(selectionType, keepExistingReservation, depositor)).Found;

    /// <summary>
    /// La causa con cui un rigetto per mancanza di celle chiude il comando della missione
    /// (<see cref="RejectTo"/>), quando il gestionale non ha celle libere: la scrivono la ricerca
    /// (<see cref="SearchTheStoreAsync"/>) e chi manda allo scarto senza una ricerca che dica di piu'.
    /// </summary>
    internal const string NoStorageCellAvailable = "No storage cell available";

    /// <summary>
    /// Le celle che il PLC ha rifiutato al deposito di questa missione (gli errori 67, 85 e 133,
    /// <c>AutomationDeviceConnectionManager.CountTheRefusalAsync</c>): ogni domanda di una cella al
    /// gestionale per lei le esclude (<see cref="FindRouteAllocateLuCore"/>, il flusso del cambio per la
    /// missione dell'operatore, <c>Api.MissionGateway.AnotherCellInTheChosenStorageAsync</c>, e la cella
    /// persa che il navigatore le rifa', <c>Api.MissionGateway.KeepTheStorageCellAsync</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Restano finche' l'UDC non deposita o non e' prelevata un'UDC nuova (risposta 7 del proprietario
    /// dell'8/10 sera). Vivono qui, sulla missione, cioe' sull'UDC: il deposito riuscito chiude la
    /// missione, e un'UDC nuova e' una missione nuova, che parte senza. Non stanno sul dispositivo,
    /// la cui fila degli errori di deposito (<c>AutomationDevice.CountRefusedDeposit</c>) l'operatore
    /// fa ripartire quando lo rimette in marcia: la ripartenza non le tocca, e la cella che il motore
    /// cerca dopo il gestionale muto (<c>Api.MissionGateway.LeaveTheRefusedCell</c>) le esclude ancora.
    /// Si scrivono nel database del WCS con la missione e tornano con la ricarica
    /// (<see cref="RestoreCellsRefusedByThePlc"/>): «esclusa finche' l'UDC non deposita» vale anche
    /// attraverso un riavvio dell'host (risposta 2 del proprietario del 9/10). Si cancellano con la
    /// riga della missione, quando l'UDC deposita o la missione finisce. Il conto del dispositivo
    /// invece non si scrive. Prima valevano per una ricerca sola, e dopo il gestionale muto la
    /// missione tornava a depositare nella cella rifiutata; poi stavano solo in memoria, e dopo un
    /// riavvio la ricerca poteva ridare alla missione proprio la cella rifiutata.
    /// </para>
    /// <para>
    /// Servono anche alle missioni della configurazione: la ricerca di prima teneva la prenotazione che
    /// la missione aveva (<see cref="ReserveOrKeepStorageAsync"/>) se la cella non era fra quelle
    /// escluse, e dopo un 85 o un 133 la cella rifiutata era ancora prenotata e ancora buona per lei: la
    /// ricerca la ridava uguale. Esclusa, la prenotazione si lascia e il gestionale ne da' un'altra.
    /// </para>
    /// </remarks>
    internal IReadOnlyList<decimal> CellsRefusedByThePlc
    {
        get
        {
            lock (_cellsRefusedByThePlcLock) return _cellsRefusedByThePlc;
        }
    }

    /// <summary>
    /// Il PLC ha rifiutato almeno un deposito di questa missione (<see cref="RememberCellsRefusedByThePlc"/>),
    /// anche verso una porta senza una cella nel database del WCS, che <see cref="CellsRefusedByThePlc"/>
    /// non conta. Lo guarda lo scarto (<see cref="RejectTo"/>): la ricerca dopo il rifiuto tiene il gateway
    /// della missione (risposta 2 del proprietario dell'8/10 sera), e lo scarto lo toglie. Prima lo scarto
    /// guardava le celle rifiutate, e dopo il rifiuto di una porta senza cella teneva il gateway, e la
    /// rotta verso il punto di scarto non si trovava (revisione finale del terzo giro).
    /// </summary>
    internal bool DepositRefusedByThePlc
    {
        get
        {
            lock (_cellsRefusedByThePlcLock) return _depositRefusedByThePlc;
        }
    }

    /// <summary>
    /// Aggiunge a <see cref="CellsRefusedByThePlc"/> le celle di un deposito rifiutato dal PLC (zero, per
    /// una porta senza cella, non conta), e alza <see cref="DepositRefusedByThePlc"/>.
    /// </summary>
    /// <remarks>
    /// La riga della missione si riscrive sotto il lucchetto delle scritture, come per ogni setter: le
    /// celle rifiutate sopravvivono a un riavvio (risposta 2 del proprietario del 9/10).
    /// </remarks>
    internal void RememberCellsRefusedByThePlc(IEnumerable<decimal> cellIds)
    {
        lock (_scritturaSuDb)
        {
            lock (_cellsRefusedByThePlcLock)
            {
                _depositRefusedByThePlc = true;
                _cellsRefusedByThePlc =
                    [.. _cellsRefusedByThePlc.Concat(cellIds.Where(cell => cell != 0)).Distinct()];
            }

            ORM.PathMission.Update(this);
        }
    }

    /// <summary>
    /// Rimette le celle rifiutate e il segno del deposito rifiutato come li ha scritti la riga della
    /// missione (<c>Persistence.MissionStore.ToPathMission</c>), senza riscriverla: e' la ricarica.
    /// </summary>
    internal void RestoreCellsRefusedByThePlc(IEnumerable<decimal> cellIds, bool depositRefused)
    {
        lock (_cellsRefusedByThePlcLock)
        {
            _depositRefusedByThePlc = depositRefused;
            _cellsRefusedByThePlc = [.. cellIds.Where(cell => cell != 0).Distinct()];
        }
    }

    private readonly object _cellsRefusedByThePlcLock = new();
    private IReadOnlyList<decimal> _cellsRefusedByThePlc = [];
    private bool _depositRefusedByThePlc;

    /// <summary>
    /// <see cref="GetStoreToWarehouse"/>, con la causa che la ricerca ha scritto in
    /// <see cref="StatusDescription"/> quando non trova: chi poi manda la missione allo scarto la lascia
    /// sul comando, invece di una causa generica scritta sopra (revisione finale della voce 39: l'UDC
    /// senza riferimenti, o il dispositivo senza cella collegata, finivano sul comando come «nessuna
    /// cella libera»). <c>null</c> per una ricerca che non ha scritto niente.
    /// </summary>
    /// <returns>
    /// <c>RouteNotWritten</c> <see cref="RouteWriteOutcome.Lost"/> quando la cella e' trovata e presa, ma
    /// la rotta verso di lei l'ha cambiata un altro mentre la si scriveva (<see cref="BuildPathToDestination"/>):
    /// la missione e' gia' in coda al giro delle rotte, con lo stato di chi ha vinto, e la ricerca ha gia'
    /// restituito la cella, tolto la sua porta e contato il fallimento nel freno (risposta 6 del
    /// proprietario dell'11/10). <see cref="RouteWriteOutcome.AwaitingRejection"/> quando la rotta non si e'
    /// scritta perche' la missione aspetta il rigetto dal campo rimandato: in coda anche lei, la cella
    /// restituita e la porta tolta allo stesso modo, ma niente contato nel freno (secondo giro di
    /// correzioni del compito 8 della voce 41). <c>null</c> in ogni altro caso. Con uno dei due
    /// <c>Found</c> e' vero: per chi chiede «c'e' una cella?» la risposta e' si', e nessuno deve mandare
    /// allo scarto ne' restituire di nuovo la cella; chi conta la rotta guarda il terzo valore.
    /// </returns>
    internal async Task<(bool Found, string? Cause, RouteWriteOutcome? RouteNotWritten)> SearchTheStoreAsync(
        StoringSelectionTypes selectionType, bool keepExistingReservation, AutomationDevice? depositor = null)
    {
        if (LuId == 0)
            return (false, null, null);

        if (selectionType == StoringSelectionTypes.CheckExistsAvailableCell)
            MissionType = MissionTypes.Storing;

        string? cause = null;

        switch (await FindRouteAllocateLu(selectionType, keepExistingReservation, depositor))
        {
            case StorageLuResultTypes.RouteBuild:
                {
                    if (selectionType == StoringSelectionTypes.CheckExistsAvailableCell)
                        MissionType = MissionTypes.Storing;
                    return (true, null, null);
                }
            case StorageLuResultTypes.RouteTakenOver:
                return (true, null, RouteWriteOutcome.Lost);
            case StorageLuResultTypes.AwaitingRejection:
                return (true, null, RouteWriteOutcome.AwaitingRejection);
            case StorageLuResultTypes.NoLuLoaded:
                {
                    MovingDescription = "No LU Loaded on Item";
                    cause = MovingDescription;
                    break;
                }
            case StorageLuResultTypes.NoCellAvailable:
                {
                    cause = NoStorageCellAvailable;
                    break;
                }
            case StorageLuResultTypes.NoItemForRequiredLinkedDoor:
                {
                    cause = "No item linked to storage door cell";
                    break;
                }
            case StorageLuResultTypes.NoAutomaticCellSetForItem:
                {
                    cause = "No cell has been set to the item that is requiring allocation";
                    break;
                }
            case StorageLuResultTypes.NoWarehouseSetForAutomaticCell:
                {
                    cause = "No warehouse has been set to the cell set to the item that is requiring allocation";
                    break;
                }
            case StorageLuResultTypes.ItemLinkedToWarehouseDoorNotAchievable:
                {
                    cause = "The storage location it's not achievable from current item";
                    break;
                }
            case StorageLuResultTypes.LuWithNoReference:
                {
                    cause = "The LU is empty";
                    break;
                }
        }

        // Ancorata a chi deposita (la cella persa, Api.MissionGateway.AnotherStorageOrTheRejectionAsync)
        // la ricerca gira anche per una missione che l'operatore toglie intanto: scritta, la causa ne
        // rifarebbe la riga sul database del WCS. Chi ha chiesto la riceve lo stesso.
        if (cause != null && (depositor == null || ReferenceEquals(ActiveMissions.Instance[MissionId], this)))
            StatusDescription = cause;

        return (false, cause, null);
    }

    /// <param name="rejectionType">Il motivo del rigetto, che dice verso dove.</param>
    /// <param name="resetGroupExecuteStepRunning">Passato alla rimozione degli step.</param>
    /// <param name="unlessChosenByOperator">
    /// Vero per il rigetto che viene dalla configurazione del dispositivo: il gestionale non ha
    /// celle per la sua modalita' di deposito (<c>Navigator.FindWhereStoreLu</c>). Se intanto
    /// l'operatore ha scelto la destinazione, il rigetto non si fa: false, e la missione, la sua
    /// rotta e la sua destinazione restano come le ha lasciate l'operatore. Vale per
    /// <see cref="RejectionTypes.NoStoreCellAvailable"/> e
    /// <see cref="RejectionTypes.DefaultNavigatorRejectionItem"/>.
    /// </param>
    /// <param name="withTheRejection">
    /// Quello che chi rigetta vuole scritto solo se il rigetto si fa, insieme alla destinazione del punto
    /// di scarto, sotto il lucchetto della rotta: un rigetto rimandato (uno step in volo, visto
    /// all'ingresso o dopo le attese) o ceduto alla scelta dell'operatore non lo chiama. Lo usa il
    /// rigetto di gruppo (<c>AutomationDevice.RebuildGroupRejection</c>), che prima toglieva gateway,
    /// indirizzi e filtro prima di chiamare qui, e un rigetto rimandato li lasciava tolti a una missione
    /// con uno step in volo (revisione finale della voce 41). Le stesse regole dei campi della
    /// primitiva: niente <c>await</c> e niente gestionale. Vale per
    /// <see cref="RejectionTypes.NoStoreCellAvailable"/> e
    /// <see cref="RejectionTypes.DefaultNavigatorRejectionItem"/>.
    /// </param>
    /// <returns>Vero se il rigetto e' fatto (<see cref="RejectionOutcome.Rejected"/>); vedi <see cref="RejectToAsync"/>.</returns>
    internal async Task<bool> RejectTo(RejectionTypes rejectionType, bool resetGroupExecuteStepRunning = true,
        bool unlessChosenByOperator = false, Action? withTheRejection = null) =>
        await RejectToAsync(rejectionType, resetGroupExecuteStepRunning, unlessChosenByOperator, withTheRejection) ==
        RejectionOutcome.Rejected;

    /// <summary>Come e' finito un rigetto (<see cref="RejectToAsync"/>).</summary>
    internal enum RejectionOutcome
    {
        /// <summary>
        /// Il rigetto e' fatto: la missione e' un rigetto verso il punto di scarto, con la sua rotta, o
        /// senza, se un altro le ha cambiato la posizione mentre la si calcolava: allora la rifa' il giro
        /// delle rotte dalla posizione nuova (<see cref="RedoInTheRouteLoopAsync"/>).
        /// </summary>
        Rejected,

        /// <summary>
        /// Rimandato: uno step della missione e' in volo, e il rigetto non lo toglie. Il rigetto della
        /// configurazione lo richiede chi lo chiedeva; quello dal campo lo rifa' il giro delle rotte
        /// quando lo step e' finito.
        /// </summary>
        Postponed,

        /// <summary>
        /// Un altro ha portato la missione verso un'altra destinazione mentre si calcolava la rotta del
        /// rigetto: non e' un rigetto, e la missione e' gia' in coda al giro delle rotte con lo stato di
        /// chi ha vinto. Non e' un rifiuto: chi chiama non mette il dispositivo in errore per lei.
        /// </summary>
        TakenOver,

        /// <summary>
        /// Il rigetto non si fa: nessun punto di scarto, la scelta dell'operatore che vince, o nessuna
        /// rotta verso lo scarto.
        /// </summary>
        NotRejected
    }

    /// <summary>
    /// Il rigetto della missione verso il punto di scarto che il motivo dice, con l'esito intero: vedi
    /// <see cref="RejectionOutcome"/>. I parametri sono quelli di <see cref="RejectTo"/>.
    /// </summary>
    /// <param name="rejectionType">Il motivo del rigetto, che dice verso dove.</param>
    /// <param name="resetGroupExecuteStepRunning">Passato alla rimozione degli step.</param>
    /// <param name="unlessChosenByOperator">Vedi <see cref="RejectTo"/>.</param>
    /// <param name="withTheRejection">Vedi <see cref="RejectTo"/>.</param>
    /// <param name="redoneByTheRouteLoop">
    /// Vero solo per il rigetto dal campo rimandato che il giro delle rotte rifa'
    /// (<see cref="RedoInTheRouteLoopAsync"/>): la sua prima parte (la descrizione dell'UDC gestita, il
    /// comando chiuso in errore con la causa, l'avviso sul registro del navigatore) e' gia' girata alla
    /// prima chiamata, e non si ripete.
    /// </param>
    /// <remarks>
    /// Lo step in volo vince sempre, anche sul rigetto dal campo (risposta 1 del proprietario
    /// dell'11/10, come la decisione del 3/9): il rigetto dal campo non lo toglie piu', ne' all'ingresso
    /// ne' sotto il lucchetto, e si rimanda; lo rifa' il giro delle rotte quando lo step e' finito. Prima
    /// toglieva la rotta senza guardare, e uno step partito durante le sue attese lo toglieva in volo.
    /// </remarks>
    internal async Task<RejectionOutcome> RejectToAsync(RejectionTypes rejectionType,
        bool resetGroupExecuteStepRunning = true, bool unlessChosenByOperator = false, Action? withTheRejection = null,
        bool redoneByTheRouteLoop = false)
    {
        switch (rejectionType)
        {
            case RejectionTypes.NoStoreCellAvailable:
            case RejectionTypes.DefaultNavigatorRejectionItem:
                {
                    // Prima di toccare qualcosa (gli step, il comando): e' il caso di tutti i giorni,
                    // la scelta arrivata mentre la ricerca aspettava il gestionale. La scrittura della
                    // destinazione qui sotto riguarda il segno per chi arriva dopo questa riga.
                    if (unlessChosenByOperator && OperatorDestination)
                        return RejectionOutcome.NotRejected;

                    var rejectionPointGuid = Guid.Empty;

                    switch (rejectionType)
                    {
                        case RejectionTypes.NoStoreCellAvailable:
                            {
                                rejectionPointGuid = NoStoreCellRejectionPoint();
                                if (rejectionPointGuid == Guid.Empty)
                                    return RejectionOutcome.NotRejected;

                                break;
                            }

                        case RejectionTypes.DefaultNavigatorRejectionItem:
                            {
                                if (PositionItem != null && PositionItem.RejectDestinationUserRequest == Guid.Empty)
                                    rejectionPointGuid = Navigator.DefaultRejectItem;
                                else if (PositionItem != null) rejectionPointGuid = PositionItem.RejectDestinationUserRequest;

                                break;
                            }
                        case RejectionTypes.NoLuDataFromPlc:
                        case RejectionTypes.RejectedFromAttendant:
                        default:
                            throw new ArgumentOutOfRangeException(nameof(rejectionType), rejectionType, null);
                    }

                    // Rigettare vuol dire rifare la rotta, e la rotta non si rifa' con uno step in
                    // volo. Il rigetto verra' richiesto di nuovo quando il movimento sara' finito. Lo
                    // sguardo si rifa' sotto il lucchetto della rotta, prima di scrivere (qui sotto):
                    // questo, prima delle attese, tiene aperto il comando di un rigetto rimandato.
                    //
                    // La rotta della missione si toglie subito, prima delle attese (il comando e la
                    // porta), come prima della voce 41: senza rotta nessun dispositivo le avvia uno step
                    // mentre il rigetto aspetta, e il rigetto non si rimanda per uno step che la sua
                    // stessa rotta gli ha fatto partire, con il comando gia' chiuso (primo giro di
                    // correzioni del compito 4 della voce 41). Lo sguardo e la rimozione insieme, sotto
                    // il lucchetto della rotta: uno step che parte in mezzo non si toglie in volo (corsa
                    // 5, compito 5).
                    if (RemoveTheRouteUnlessAStepIsInFlight(out _, resetGroupExecuteStepRunning) is { } inVoloAllIngresso)
                        return Postponed(inVoloAllIngresso);

                    if (IdCommandToDo > 0)
                        await CommandToDo.SetStatus(IdCommandToDo, Command.StatusTypes.Done,
                            "Rejection Cause: " + StatusDescription);

                    var rejectionItem = PathDevices.Instance[rejectionPointGuid];

                    var rejectionDoorCode = rejectionItem?.DeviceType == DeviceTypes.Buffer
                        ? await DbInfo.GetDoorCode(rejectionPointGuid)
                        : null;

                    void Reject()
                    {
                        if (rejectionItem?.DeviceType == DeviceTypes.Buffer)
                            StorageDoorCode = rejectionDoorCode;

                        // Il gateway di una missione con il segno e' della scelta dell'operatore, che
                        // con lo scarto cade (setter di DestinationItemId): la rotta verso il punto di
                        // scarto non passa da lui (PathBuilder usa il gateway della missione), e senza
                        // toglierlo il rigetto non trovava percorso. Qui e non in chi chiama: ogni via
                        // che manda allo scarto una missione dell'operatore (gli errori 85, 133 e 67
                        // senza un'altra cella nel magazzino scelto) passa di qui. Senza un
                        // punto di scarto per mancanza di celle il rigetto non arriva qui, e il gateway
                        // resta. Una missione che l'operatore ha mandato proprio nel punto di scarto
                        // tiene destinazione e segno, e con loro il gateway. Lo stesso per una missione
                        // senza il segno il cui deposito il PLC ha rifiutato (DepositRefusedByThePlc,
                        // anche verso una porta senza cella): la ricerca della cella nuova le tiene il
                        // gateway (risposta 2 del proprietario dell'8/10 sera), e senza un'altra cella
                        // lo scarto glielo toglie, come prima glielo toglieva la ricerca stessa.
                        if ((OperatorDestination || DepositRefusedByThePlc) &&
                            DestinationItemId != rejectionPointGuid)
                            Gateway = Guid.Empty;

                        DestinationItemId = rejectionPointGuid;
                        ActiveFilter = false;
                    }

                    if (RejectionPauseForTesting is { } pause)
                        await pause();

                    // La porta si e' letta prima (database del WCS, fuori da ogni lucchetto); lo sguardo
                    // allo step in volo, la rimozione della rotta e lo scarto si fanno insieme, dentro la
                    // primitiva della rotta (voce 41). La rotta tolta prima delle attese non basta: durante
                    // le attese un altro scrittore poteva rimetterne una e un dispositivo avviarne lo step,
                    // il setter scartava la destinazione del punto di scarto per lo step in volo, e la
                    // missione diventava un rigetto con la rotta e la destinazione di prima. Qui si
                    // rimanda, e si toglie di nuovo la rotta rimessa. Il rigetto non confronta la versione:
                    // e' una decisione presa adesso, e vince. Una scelta dell'operatore arrivata durante le
                    // attese vince lei, come prima (UnlessChosenByOperator).
                    MissionStep? inVolo = null;
                    var chosenByOperator = false;

                    TryWriteRoute(null, [], () =>
                    {
                        inVolo = StepInFlight();
                        if (inVolo != null)
                            return false;

                        // La rotta si toglie solo con il rigetto che si fa: la scelta dell'operatore
                        // arrivata durante le attese tiene la rotta che il suo commit ha scritto.
                        // Tolta prima di guardare il segno, si perdeva con l'esito «rifiutato»
                        // (revisione finale della voce 41).
                        if (!unlessChosenByOperator)
                            RemoveAndReject();
                        else if (!UnlessChosenByOperator(RemoveAndReject))
                            chosenByOperator = true;

                        return !chosenByOperator;

                        void RemoveAndReject()
                        {
                            RemoveMissionSteps(resetGroupExecuteStepRunning);
                            withTheRejection?.Invoke();
                            Reject();
                        }
                    });

                    if (inVolo != null)
                        return Postponed(inVolo);

                    if (chosenByOperator)
                        return RejectionOutcome.NotRejected;

                    return await TheRejectionRouteIsBuilt(rejectionPointGuid);

                    RejectionOutcome Postponed(MissionStep step)
                    {
                        Navigator.WriteLog(
                            $"Rigetto della missione {MissionId} rimandato: lo step {step.StepId} e' in volo su {step.FromNodeId}",
                            LogEntryType.Information);
                        return RejectionOutcome.Postponed;
                    }
                }

            case RejectionTypes.NoLuDataFromPlc:
            case RejectionTypes.RejectedFromAttendant:
                {
                    // La prima parte si fa una volta, alla prima chiamata: il rigetto rimandato che il giro
                    // rifa' non la ripete. Prima la ripeteva a ogni rinvio, con il comando chiuso di nuovo in
                    // errore e un avviso in piu' sul registro per lo stesso rigetto (giro finale delle
                    // correzioni della voce 41).
                    if (!redoneByTheRouteLoop)
                    {
                        var handledLu = ManagedLu.Instance[MissionId];

                        if (handledLu != null) handledLu.ResultDescription = StatusDescription;

                        var errorDescription = LuCode is { Length: > 0 }
                            ? $"LU Code {LuCode} rejected because {StatusDescription}"
                            : $"LU rejected because {StatusDescription}";

                        if (IdCommandToDo > 0)
                            await CommandToDo.SetStatus(IdCommandToDo, Command.StatusTypes.Error,
                                "Rejection Cause: " + StatusDescription);

                        Navigator.RecordNavigatorLog(PositionItem, errorDescription, LogEntryType.Warning);
                    }

                    if (PositionItem == null || PositionItem.RejectItemDestinationIdGetLuInfo == Guid.Empty)
                    {
                        // Il rigetto rimandato si rifa' dalla posizione di adesso (deviazione 1 del compito 8
                        // della voce 41): se da li' non c'e' un punto di scarto per i dati dell'UDC non si fa, e
                        // lo dice un avviso suo. La riga della prima chiamata e' di un'altra posizione, e non
                        // dice che il rigetto e' caduto.
                        if (redoneByTheRouteLoop)
                        {
                            var dropped =
                                $"Rigetto dal campo ({rejectionType}) della missione {MissionId} rimandato e non fatto: da {PositionItemId} non c'e' un punto di scarto per i dati dell'UDC, e la missione va avanti verso {DestinationItemId}";
                            Navigator.WriteLog(dropped, LogEntryType.Warning);
                            Navigator.RecordNavigatorLog(PositionItem, dropped, LogEntryType.Warning);
                        }

                        return RejectionOutcome.NotRejected;
                    }

                    var rejectionPoint = PositionItem.RejectItemDestinationIdGetLuInfo;
                    var toABuffer = PositionItem.RejectItemDestinationGetLuInfo?.DeviceType is
                        DeviceTypes.Buffer or DeviceTypes.LoadingAndBuffer;

                    // La rotta della missione si toglie subito, prima della lettura della porta, come
                    // prima della voce 41 e come nel rigetto dell'operatore: senza rotta nessun
                    // dispositivo le avvia uno step mentre il rigetto aspetta. Tolta solo sotto il
                    // lucchetto, P avviava lo step durante la lettura (secondo giro di correzioni del
                    // compito 4 della voce 41). Lo sguardo e la rimozione insieme, sotto il lucchetto
                    // della rotta, e con uno step gia' in volo non si toglie niente: lo step in volo vince
                    // anche sul rigetto dal campo (risposta 1 del proprietario dell'11/10), che si rimanda
                    // e della rotta le lascia solo lo step in volo.
                    if (RemoveTheRouteUnlessAStepIsInFlight(out _, resetGroupExecuteStepRunning) is not null)
                        return FieldRejectionPostponed(rejectionType, resetGroupExecuteStepRunning);

                    var rejectionDoorCode = toABuffer ? await DbInfo.GetDoorCode(rejectionPoint) : null;

                    if (RejectionPauseForTesting is { } pause)
                        await pause();

                    // La porta si e' letta prima (database del WCS, fuori da ogni lucchetto); lo sguardo allo
                    // step in volo, la rimozione della rotta, la porta e la destinazione si scrivono insieme,
                    // dentro la primitiva della rotta (voce 41). La rotta tolta prima della lettura non basta:
                    // durante l'attesa della porta un altro scrittore poteva rimettere una rotta e un
                    // dispositivo avviarne lo step. Fino alla risposta 1 del proprietario dell'11/10 questo
                    // rigetto non guardava gli step in volo, e quello step lo toglieva in volo, liberando a
                    // meta' movimento chi lo eseguiva; ora si rimanda, come il rigetto della configurazione.
                    MissionStep? inVolo = null;

                    TryWriteRoute(null, [], () =>
                    {
                        inVolo = StepInFlight();
                        if (inVolo != null)
                            return false;

                        RemoveMissionSteps(resetGroupExecuteStepRunning);

                        if (toABuffer)
                            StorageDoorCode = rejectionDoorCode;

                        DestinationItemId = rejectionPoint;
                        ActiveFilter = false;
                        return true;
                    });

                    if (inVolo != null)
                        return FieldRejectionPostponed(rejectionType, resetGroupExecuteStepRunning);

                    return await TheRejectionRouteIsBuilt(rejectionPoint);
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(rejectionType), rejectionType, null);
        }
    }

    /// <summary>
    /// La rotta verso il punto di scarto appena scritto da <see cref="RejectToAsync"/>: con la rotta la
    /// missione diventa un rigetto (<see cref="MissionTypes.Rejection"/>), e il rigetto e' fatto.
    /// </summary>
    /// <param name="rejectionPoint">Il punto di scarto che il rigetto ha scritto come destinazione.</param>
    /// <remarks>
    /// Una rotta che un altro ha cambiato mentre la si calcolava (<see cref="BuildPathToDestination"/>
    /// risponde vero) lascia la missione in coda al giro delle rotte. Se la destinazione e' ancora il punto
    /// di scarto, chi ha vinto le ha cambiato la posizione o la rotta, non la meta (uno spostamento,
    /// <see cref="MoveTo"/>): il rigetto si completa dalla posizione nuova, la missione e' un rigetto e la
    /// rotta la rifa' il giro (<see cref="RedoInTheRouteLoopAsync"/>), e il rigetto e' fatto (risposta 5
    /// del proprietario dell'11/10). Prima restava a meta': destinazione e porta dello scarto, gateway e
    /// filtro tolti, comando chiuso, il tipo di prima, e chi rigettava un deposito rifiutato dal PLC
    /// metteva il dispositivo in errore. Se la destinazione e' un'altra, non e' un rigetto
    /// (<see cref="RejectionOutcome.TakenOver"/>), e il tipo resta: prima si contavano gli step che
    /// c'erano, anche quelli dell'altro, e la missione diventava un rigetto verso la destinazione di un
    /// altro (voce 41).
    /// </remarks>
    private async Task<RejectionOutcome> TheRejectionRouteIsBuilt(Guid rejectionPoint)
    {
        if (await BuildPathToDestination())
            return TheRejectionWhoseRouteWasTakenOver(rejectionPoint);

        if (MissionSteps.Count <= 0) return RejectionOutcome.NotRejected;

        MissionType = MissionTypes.Rejection;
        return RejectionOutcome.Rejected;
    }

    /// <summary>
    /// Vedi <see cref="TheRejectionRouteIsBuilt"/>: la destinazione si riguarda sotto il lucchetto della
    /// rotta, insieme al tipo e al segno del rigetto da completare, e nessuna scrittura della destinazione
    /// passa in mezzo.
    /// </summary>
    private RejectionOutcome TheRejectionWhoseRouteWasTakenOver(Guid rejectionPoint)
    {
        lock (_route)
        {
            if (DestinationItemId != rejectionPoint)
            {
                Navigator.WriteLog(
                    $"Missione {MissionId}: il rigetto verso {rejectionPoint} non si fa, un altro l'ha mandata verso {DestinationItemId} mentre se ne calcolava la rotta",
                    LogEntryType.Information);
                return RejectionOutcome.TakenOver;
            }

            MissionType = MissionTypes.Rejection;
            _rejectionToComplete = rejectionPoint;
        }

        Navigator.WriteLog(
            $"Missione {MissionId}: il rigetto verso {rejectionPoint} si completa da {PositionItemId}, la rotta la rifa' il giro delle rotte",
            LogEntryType.Information);
        return RejectionOutcome.Rejected;
    }

    /// <summary>
    /// Il rigetto dal campo trova uno step della missione in volo: non lo toglie, e si rimanda. Lo rifa'
    /// il giro delle rotte quando lo step e' finito (<see cref="RedoInTheRouteLoopAsync"/>, risposta 1 del
    /// proprietario dell'11/10): la missione torna in coda, e il giro aspetta lo step prima di guardarla.
    /// </summary>
    /// <param name="rejectionType">Il motivo del rigetto rimandato: il giro lo rifa' con lo stesso.</param>
    /// <param name="resetGroupExecuteStepRunning">Come per <see cref="RemoveMissionSteps"/>.</param>
    /// <remarks>
    /// <para>
    /// Della rotta resta solo lo step in volo: il resto si toglie insieme al segno, sotto il lucchetto
    /// della rotta, e lo step in volo si riguarda li' (fra lo sguardo di chi chiama e questo lucchetto lo
    /// step puo' essere finito, e il successivo partito). Finche' il segno c'e', nessuna rotta calcolata
    /// si scrive (<see cref="TryWriteRoute"/>): l'UDC arriva in fondo allo step in volo e si ferma li', e
    /// il giro rifa' il rigetto da quella posizione.
    /// </para>
    /// <para>
    /// Fino al primo giro di correzioni del compito 8 della voce 41 la rotta restava intera: finito lo
    /// step in volo, il dispositivo d'arrivo avviava subito lo step dopo (i dispositivi non guardano la
    /// coda del giro), il giro trovava di nuovo uno step in volo e aspettava ancora, e cosi' fino alla
    /// destinazione di prima. L'UDC senza i dati del PLC finiva nella cella, o su un dispositivo senza
    /// punto di scarto, invece che allo scarto.
    /// </para>
    /// </remarks>
    private RejectionOutcome FieldRejectionPostponed(RejectionTypes rejectionType, bool resetGroupExecuteStepRunning)
    {
        MissionStep? inVolo;

        lock (_route)
        {
            _fieldRejectionPending = rejectionType;
            inVolo = KeepOnlyTheStepInFlight(resetGroupExecuteStepRunning);
        }

        Navigator.WriteLog(
            inVolo != null
                ? $"Rigetto dal campo della missione {MissionId} rimandato: lo step {inVolo.StepId} e' in volo su {inVolo.FromNodeId}, il resto della rotta e' tolto, e lo rifa' il giro delle rotte quando sara' finito"
                : $"Rigetto dal campo della missione {MissionId} rimandato: lo step che era in volo e' finito intanto, la rotta e' tolta, e lo rifa' il giro delle rotte",
            LogEntryType.Information);

        // Item 54, fix round 3 of task 4: the rejection wins over a change of destination that waits for
        // the end of the same step. Kept, it met the rejection when the step ended (the commit refuses
        // while the mark is up) and was tried again every round of the master loop, with a reservation
        // and a give-back of a cell each time, for as long as the route loop did not redo the rejection
        // (for ever with the engine stopped); in Run it could be applied on top of the rejection route.
        Api.PendingMissionChanges.DropAtAFieldRejection(this);

        BackToTheRouteLoop();
        return RejectionOutcome.Postponed;
    }

    /// <summary>
    /// Vedi <see cref="FieldRejectionPostponed"/>: toglie dalla rotta tutto tranne lo step in volo (con gli
    /// step dello stesso movimento di gruppo), passando dalle sorgenti come <see cref="RemoveMissionSteps"/>,
    /// e fa salire la versione se ha tolto qualcosa: chi ha letto uno step tolto per avviarlo rinuncia
    /// (<see cref="TryTakeTheStepToken"/>).
    /// </summary>
    /// <returns>Lo step in volo, o <c>null</c> se nessuno lo e' piu' (e allora la rotta e' tolta tutta).</returns>
    private MissionStep? KeepOnlyTheStepInFlight(bool resetGroupExecuteStepRunning)
    {
        lock (_route)
        {
            var inVolo = StepInFlight();

            var toRemove = Collections.MissionSteps.Instance
                .Where(x => x.MissionId == MissionId && !TheSameMovement(x))
                .ToList();

            if (toRemove.Count == 0)
                return inVolo;

            foreach (var step in toRemove)
            {
                if (step.SourceItem is { } source)
                    source.RemoveStep(step, resetGroupExecuteStepRunning);
                else
                    Collections.MissionSteps.Instance.Remove(step);
            }

            Interlocked.Increment(ref _routeVersion);
            return inVolo;

            bool TheSameMovement(MissionStep step) =>
                inVolo != null && (step.StepId == inVolo.StepId ||
                                   inVolo.GroupedMovementId != Guid.Empty &&
                                   step.GroupedMovementId == inVolo.GroupedMovementId);
        }
    }

    /// <summary>
    /// Il punto di scarto per mancanza di celle (<see cref="RejectionTypes.NoStoreCellAvailable"/>):
    /// quello del gruppo del dispositivo di posizione, se e' configurato, altrimenti quello del
    /// dispositivo; <see cref="Guid.Empty"/> se non ce n'e' nessuno, e allora <see cref="RejectTo"/> non
    /// rigetta. Una regola sola per <see cref="RejectTo"/> e per chi deve sapere prima se lo scarto
    /// c'e': la cella persa di una missione della configurazione, che senza un punto di scarto aspetta
    /// e ritenta con il freno (risposta 5 del proprietario del 2/10,
    /// <c>Api.MissionGateway.KeepTheStorageCellAsync</c>).
    /// </summary>
    internal Guid NoStoreCellRejectionPoint()
    {
        var position = PositionItem;

        if (position?.BelongedGroup is { } group && group.RejectItemDestinationAllocateLu != Guid.Empty)
            return group.RejectItemDestinationAllocateLu;

        return position?.RejectItemDestinationAllocateLu ?? Guid.Empty;
    }

    /// <summary>
    /// Dice se uno step di questa missione e' in volo: mandato al campo, e senza risposta ancora.
    /// </summary>
    /// <returns>Lo step in volo, oppure <c>null</c>.</returns>
    /// <remarks>
    /// <para>
    /// Finche' c'e' uno step in volo la missione non si rimuove e la sua rotta non si ricostruisce:
    /// entrambe le cose cancellano gli step, e cancellare lo step in volo rende libero il
    /// dispositivo che lo sta eseguendo a meta' movimento — <c>RunningStepId</c> e' derivato dalla
    /// collezione globale degli step, e sparisce con loro. La definizione di «in volo» sta sul
    /// dispositivo, in <c>AutomationDevice.IsStepInFlight</c>.
    /// </para>
    /// <para>
    /// Uno step e' in volo se porta il suo gettone la sorgente (il movimento singolo, il
    /// <c>GetFrom</c>, il deposito), la destinazione (il prelievo: <c>ConfigurePickUpStep</c> scrive
    /// il gettone sul dispositivo che preleva, che e' la destinazione dello step) o un dispositivo con
    /// il gettone del movimento di gruppo di cui lo step fa parte (<c>ExecuteGroupedMOVE_LU_TO</c>:
    /// il gettone e' del gruppo, e i membri non ne portano). Sempre con la regola di
    /// <c>AutomationDevice.IsStepInFlight</c>: un dispositivo in <c>Warning</c> o <c>Error</c> non ha
    /// niente in volo.
    /// </para>
    /// <para>
    /// Fino alla voce 41 guardava solo il gettone della sorgente, e la vista larga era di un metodo a
    /// parte, per il cambio di destinazione dell'operatore e la cella persa: la ricostruzione della
    /// rotta, lo spostamento, la rimozione e l'uscita dal motore toglievano uno step in volo nel
    /// prelievo o in un movimento di gruppo. Ora la vista e' una sola, per tutti (risposta 2 del
    /// proprietario dell'11/10).
    /// </para>
    /// </remarks>
    internal MissionStep? StepInFlight()
    {
        foreach (var step in Collections.MissionSteps.Instance.Where(x => x.MissionId == MissionId))
        {
            if (step.SourceItem?.IsStepInFlight(step.StepId) == true ||
                step.DestinationItem?.IsStepInFlight(step.StepId) == true)
                return step;

            if (step.GroupedMovementId != Guid.Empty &&
                PathDevices.Instance.Any(device => device?.IsStepInFlight(step.GroupedMovementId) == true))
                return step;
        }

        return null;
    }

    /// <summary>
    /// Toglie la rotta della missione passando dalle sorgenti degli step (<c>AutomationDevice.RemoveStep</c>,
    /// che ne azzera il gettone), e poi quello che resta direttamente dalla collezione.
    /// </summary>
    /// <remarks>
    /// Sotto il lucchetto della rotta (voce 41): una scrittura della rotta con la primitiva
    /// (<see cref="TryWriteRoute"/>) non arriva a meta' rimozione, e se c'era qualcosa da togliere la
    /// versione sale, cosi' che chi ha calcolato una rotta prima della rimozione perda il confronto.
    /// </remarks>
    internal void RemoveMissionSteps(bool resetGroupExecuteStepRunning = true)
    {
        lock (_route)
        {
            if (!Collections.MissionSteps.Instance.Any(c => c.MissionId == MissionId))
                return;

            do
            {
                var item = Collections.MissionSteps.Instance.FirstOrDefault(c => c.MissionId == MissionId);
                if (item?.SourceItem == null) break;
                item.SourceItem?.RemoveStep(item, resetGroupExecuteStepRunning);
            } while (true);

            Collections.MissionSteps.Instance.RemoveAll(c => c.MissionId == MissionId);
            Interlocked.Increment(ref _routeVersion);
        }
    }

    /// <summary>
    /// Toglie la rotta della missione se nessuno step e' in volo (<see cref="StepInFlight"/>), con lo
    /// sguardo e la rimozione insieme sotto il lucchetto della rotta, e legge la versione con cui chi
    /// toglie andra' a scrivere.
    /// </summary>
    /// <param name="seenVersion">
    /// La versione dopo la rimozione (o senza rimozione, se uno step e' in volo), letta sotto lo stesso
    /// lucchetto: nessuna scrittura d'altri passa fra la rimozione e la lettura.
    /// </param>
    /// <param name="resetGroupExecuteStepRunning">Come per <see cref="RemoveMissionSteps"/>.</param>
    /// <returns>Lo step in volo, e allora niente e' tolto; <c>null</c> se la rotta e' tolta.</returns>
    /// <remarks>
    /// L'avvio di uno step (<c>AutomationDevice.StartPutToMOVE_LU</c>) scrive il gettone sotto lo stesso
    /// lucchetto, e solo se la versione della rotta e' ancora quella su cui ha letto lo step
    /// (<see cref="TryTakeTheStepToken"/>). Cosi' delle due l'una: o il gettone c'e' gia', e qui si vede
    /// lo step in volo e non si toglie niente; o la rotta e' tolta prima, la versione e' salita, e
    /// l'avvio rinuncia e restituisce il posto. Prima lo sguardo e la rimozione erano separati, e il
    /// gettone arrivava fra la presa del posto e la sua scrittura senza che lo sguardo lo vedesse: lo
    /// step si toglieva, il comando partiva lo stesso, e l'UDC finiva sul dispositivo dopo mentre per
    /// il motore era ancora sul primo (corsa 5 della ricognizione della voce 41).
    /// </remarks>
    internal MissionStep? RemoveTheRouteUnlessAStepIsInFlight(out long seenVersion,
        bool resetGroupExecuteStepRunning = true)
    {
        lock (_route)
        {
            if (StepInFlight() is { } inVolo)
            {
                seenVersion = _routeVersion;
                return inVolo;
            }

            RouteRemovalPauseForTesting?.Invoke();
            RemoveMissionSteps(resetGroupExecuteStepRunning);
            seenVersion = _routeVersion;
            return null;
        }
    }

    /// <summary>
    /// L'uscita della missione dal motore (<c>ActiveMissions.Remove</c>) se nessuno step e' in volo
    /// (<see cref="StepInFlight"/>): lo sguardo e l'uscita insieme, sotto il lucchetto della rotta, come
    /// in <see cref="RemoveTheRouteUnlessAStepIsInFlight"/>.
    /// </summary>
    /// <param name="leave">
    /// L'uscita: la missione fuori dalle attive e dall'indice delle posizioni, la posizione e la rotta
    /// tolte. Gira con il lucchetto preso: niente <c>await</c> e niente gestionale.
    /// </param>
    /// <returns>Lo step in volo, e allora <paramref name="leave"/> non e' chiamata; <c>null</c> se la missione e' uscita.</returns>
    /// <remarks>
    /// Prima l'uscita guardava una volta e toglieva dopo, fuori dal lucchetto: uno step che partiva in
    /// mezzo scriveva il suo gettone e mandava il comando, e l'uscita lo toglieva in volo (risposta 2 del
    /// proprietario dell'11/10). Ora l'avvio aspetta il lucchetto, trova la rotta tolta e rinuncia
    /// (<see cref="TryTakeTheStepToken"/>).
    /// </remarks>
    internal MissionStep? LeaveUnlessAStepIsInFlight(Action leave)
    {
        LeavePauseForTesting?.Invoke();

        lock (_route)
        {
            if (StepInFlight() is { } inVolo)
                return inVolo;

            RouteRemovalPauseForTesting?.Invoke();
            leave();
            return null;
        }
    }

    /// <summary>
    /// Scrive il gettone dello step che un dispositivo avvia, sotto il lucchetto della rotta, solo se
    /// la rotta e' ancora quella su cui lo step si e' letto: la versione e' ancora
    /// <paramref name="seenVersion"/> e lo step e' ancora fra quelli della missione. Poi la versione
    /// sale: chi ha calcolato una rotta prima che lo step partisse non la scrive sopra uno step in volo.
    /// </summary>
    /// <param name="seenVersion">La versione letta prima di leggere lo step da avviare (<see cref="RouteVersion"/>).</param>
    /// <param name="step">Lo step che parte.</param>
    /// <param name="token">La scrittura del gettone, con il lucchetto preso: niente attese e niente gestionale.</param>
    /// <returns>Falso se la rotta e' cambiata intanto: il gettone non e' scritto, e chi avvia rinuncia.</returns>
    /// <remarks>
    /// Vedi <see cref="RemoveTheRouteUnlessAStepIsInFlight"/> (corsa 5 della ricognizione della voce 41).
    /// Lo prendono cosi' tutti gli avvii di uno step (compito 6 della voce 41): il movimento singolo
    /// (<c>AutomationDevice.StartPutToMOVE_LU</c>), il <c>GetFrom</c>, il prelievo dalla cella, il
    /// deposito in cella e i movimenti dei gruppi, che come gettone scrivono il loro
    /// <c>GroupedMovementId</c> sullo step e sulla missione.
    /// </remarks>
    internal bool TryTakeTheStepToken(long seenVersion, MissionStep step, Action token) =>
        TryTakeTheStepToken(seenVersion, step, token, out _);

    /// <summary>
    /// Come <see cref="TryTakeTheStepToken(long, MissionStep, Action)"/>, e dice la versione dopo il
    /// gettone: chi avvia nello stesso giro un altro step della stessa missione confronta quella.
    /// </summary>
    /// <param name="seenVersion">La versione letta prima di leggere lo step da avviare (<see cref="RouteVersion"/>).</param>
    /// <param name="step">Lo step che parte.</param>
    /// <param name="token">La scrittura del gettone, con il lucchetto preso: niente attese e niente gestionale.</param>
    /// <param name="versionAfter">La versione dopo il gettone, letta sotto lo stesso lucchetto; se il gettone non e' scritto, quella che ha fatto rinunciare.</param>
    /// <returns>Falso se la rotta e' cambiata intanto: il gettone non e' scritto, e chi avvia rinuncia.</returns>
    internal bool TryTakeTheStepToken(long seenVersion, MissionStep step, Action token, out long versionAfter)
    {
        StepTokenPauseForTesting?.Invoke();

        lock (_route)
        {
            if (seenVersion != _routeVersion ||
                !Collections.MissionSteps.Instance.Any(x => x.StepId == step.StepId && x.MissionId == MissionId))
            {
                Navigator.WriteLog(
                    $"Missione {MissionId}: lo step {step.StepId} non parte, la rotta e' cambiata mentre partiva (versione {seenVersion}, ora {_routeVersion})",
                    LogEntryType.Information);
                versionAfter = _routeVersion;
                return false;
            }

            token();
            versionAfter = Interlocked.Increment(ref _routeVersion);
            return true;
        }
    }

    internal void ResetGroupedMovementId()
    {
        if (GroupedMovementId == Guid.Empty)
            return;
        foreach (var lu in ActiveMissions.Instance.Where(x =>
                     x != null && x.GroupedMovementId == GroupedMovementId))
            if (lu != null)
                lu.GroupedMovementId = Guid.Empty;
    }

    #endregion Internal Methods

    #region Private Methods

    private static async Task AllocateOnItem(AutomationDevice item, PathMission lUtoStore)
    {
        if (item.LinkedCellId == 0 || lUtoStore.LuId == 0)
            return;

        var manuale = lUtoStore.StepOrigin == StepTypes.Manual;

        // Compito 13, ex OperazioniUdc.AllocaInCella su una CellaDeposito costruita qui stessa
        // (area Operatori, porta A, nessun codice porta) più, se l'unità di carico risultava
        // spedita, OperazioniUdc.RientroUdc: ora un'unica operazione atomica lato gestionale.
        // Questa via della cella operatore resta sincrona e legacy (OperationId vuoto), mentre il
        // deposito terminale in magazzino usa l'outbox durevole. StorageDoorCode vuoto e' il segnale
        // che fa scegliere al gestionale la cella operatore invece di rileggerla dal database.
        var request = new AllocateLoadUnitRequest(lUtoStore.LuId, item.LinkedCellId, StorageDoorCode: null,
            ResetAssignment: false, manuale, StorageDepth: -1, DetectedDepth: 0, Navigator.UserId);

        await lUtoStore.AllocateLoadUnitAsync(request);
    }

    internal static async Task AllocateOnStorage(AutomationDevice item, PathMission lUtoStore)
    {
        if (lUtoStore.DestinationItemId != item.ItemGuid || lUtoStore.LuId == 0 ||
            string.IsNullOrEmpty(lUtoStore.StorageDoorCode))
            return;

        var manuale = lUtoStore.StepOrigin == StepTypes.Manual;

        // Compito 9: la stessa cella per codice porta che gia' serve a IWarehouse.DescribeCellsByDoorAsync
        // altrove nel motore, usata qui solo per decidere la quota di deposito (profondita',
        // multiprofondita', codice porta A per il registro/il blocco). Il deposito vero e proprio
        // (compito 13) rilegge la cella una seconda volta lato gestionale, dallo stesso codice
        // porta: e' la stranezza preesistente sulla porta selezionata mai impostata, migrata
        // intatta - vedi AllocateLoadUnitRequest.StorageDoorCode e il rapporto del compito 13.
        var cellDescription = (await lUtoStore.DescribeCellsByDoorAsync(lUtoStore.StorageDoorCode)).FirstOrDefault();

        if (cellDescription is null)
            return;

        // La sola distinzione che qui conta e' la multiprofondita' (quota di deposito da
        // calcolare): per ogni altra area la quota e' zero. Lo diceva anche il vecchio switch
        // sui tipi, con dieci case impilati sullo stesso ramo.
        if (cellDescription.MultiDepthStorage)
        {
                    var luDepth = await DbInfo.GetLuDepth(lUtoStore.LuId);

                    // Compito 9: ex Navigator.WmsBusinessArea.CelleMagazzino.QuotaDepositoUdc(...), ora
                    // IWarehouse.GetCellStoringConstraintsAsync. Stranezza preesistente migrata
                    // intatta, non corretta: la CellaDeposito che il gestionale costruisce per il
                    // deposito vero e proprio (compito 13) non ha mai la porta selezionata impostata
                    // a "A", quindi la query di QuotaDepositoUdc confrontava solo "== TipiPorta.A" e
                    // prendeva sempre il ramo "else" (quello della porta B) per questo chiamante.
                    // CellDoor.B qui sotto prende lo stesso ramo - non e' una correzione, e' la stessa
                    // stranezza resa esprimibile nel nuovo enum a due soli valori (vedi il rapporto
                    // del compito 9).
                    var theoreticalStoreDepth = (await lUtoStore.GetCellStoringConstraintsAsync(
                        cellDescription.CellId, CellDoor.B, luDepth, lUtoStore.LuId)).TheoreticalStoreDepth;

                    if (lUtoStore.StorageDepth >= 0)
                    {
                        if (lUtoStore.DetectedDepth > cellDescription.Depth)
                        {
                            string errorDescription;

                            if (Navigator.LockCellOnStoringError)
                            {
                                errorDescription =
                                    $"Locked Cell {cellDescription.DoorCodeA} because on LU {lUtoStore.LuCode} the storing distance {lUtoStore.DetectedDepth} is greater than cell depth {cellDescription.Depth}";

                                await DbInfo.SetCellLockedForStoring(cellDescription.DoorCodeA);
                            }
                            else
                            {
                                errorDescription =
                                    $"suggest to check Cell {cellDescription.DoorCodeA} because on LU {lUtoStore.LuCode} the storing distance {lUtoStore.DetectedDepth} is greater than cell depth {cellDescription.Depth}";
                            }

                            Navigator.RecordNavigatorLog(item, errorDescription, LogEntryType.Warning);

                            lUtoStore.StorageDepth = theoreticalStoreDepth;
                        }
                        else if (Math.Abs(theoreticalStoreDepth - lUtoStore.DetectedDepth) >= luDepth)
                        {
                            var errorDescription =
                                $"suggest to check Cell {cellDescription.DoorCodeA} because on LU {lUtoStore.LuCode} the difference between theoretical storing distance {theoreticalStoreDepth} and the real storing distance {lUtoStore.DetectedDepth} is greater than LU depth {luDepth}";

                            Navigator.RecordNavigatorLog(item, errorDescription, LogEntryType.Warning);

                            // Database.SetCellLockedForStoring(_DatiCelleDeposito.CodicePorta)

                            // LUtoStore.StorageDepth = _QuotaDepositoTeorica
                        }
                    }
                    else
                    {
                        lUtoStore.StorageDepth = theoreticalStoreDepth;
                    }
        }
        else
        {
            lUtoStore.StorageDepth = 0;
        }

        var operationId = Guid.NewGuid();
        var request = new AllocateLoadUnitRequest(
            lUtoStore.LuId,
            cellDescription.CellId,
            lUtoStore.StorageDoorCode,
            ResetAssignment: true,
            manuale,
            lUtoStore.StorageDepth,
            lUtoStore.DetectedDepth,
            Navigator.UserId,
            operationId);

        // Il deposito fisico è già avvenuto quando si arriva qui: la decisione deve quindi vivere
        // nel database locale prima della prima chiamata al WMS e conservare lo stesso OperationId.
        await WarehouseTransitionOutbox.EnqueueAsync(
            operationId,
            WarehouseTransitionKind.Allocate,
            lUtoStore.MissionId,
            lUtoStore.LuId,
            JsonSerializer.Serialize(request));
        await Navigator.WarehouseTransitionDispatcher.TryDispatchAsync();
    }

    private static bool AnyExtractionCheckPoint()
    {
        return PathDevices.Instance.Any(x => x is { ExtractionCheckPoint: true });
    }

    private static List<AutomationDevice> AvailableStoringAutomationDevices(int numCurrentStoringStep)
    {
        var result = new List<AutomationDevice>();

        var usableAutomationDevices = PathDevices.Instance.Where(c =>
            c != null && c.Status != DeviceStatusTypes.OffLine &&
            c is { IsLinkedToStorage: true, BelongedGroup: null });

        var automationDevices = usableAutomationDevices as AutomationDevice[] ?? usableAutomationDevices.ToArray();

        foreach (var item in automationDevices)
            if (item != null && numCurrentStoringStep > 0 && item.ActiveStoringMission.Count < numCurrentStoringStep)
            {
                foreach (var storage in item.DestinationItemsLinked.Where(x =>
                             x is { DeviceType: DeviceTypes.Storage }))
                    if (storage != null && !result.Contains(storage))
                        result.Add(storage);
            }
            else if (numCurrentStoringStep == 0)
            {
                foreach (var storage in item?.DestinationItemsLinked.Where(x =>
                             x is { DeviceType: DeviceTypes.Storage })!)
                    if (storage != null && !result.Contains(storage))
                        result.Add(storage);
            }

        if (result.Count != 0 || numCurrentStoringStep <= 0)
            return
            [
                .. from c in result
                orderby c.ASI, c.SUBITEM, c.PARTITION
                select c
            ];
        {
            foreach (var item in automationDevices)
                foreach (var storage in item?.DestinationItemsLinked.Where(x =>
                             x is { DeviceType: DeviceTypes.Storage })!)
                    if (storage != null && !result.Contains(storage))
                        result.Add(storage);
        }

        return
        [
            .. from c in result
            orderby c.ASI, c.SUBITEM, c.PARTITION
            select c
        ];
    }

    private static async Task FireStatusLuChanged(AutomationDevice? itemSource, AutomationDevice? itemDestination)
    {
        if (itemDestination != null && itemDestination.DeviceType != DeviceTypes.Storage &&
            itemDestination.DeviceType != DeviceTypes.GroupOfAutomationDevices)
            await itemDestination.FireLuLoadStatusChanged();
        if (itemSource != null && itemSource.DeviceType != DeviceTypes.Storage &&
            itemSource.DeviceType != DeviceTypes.GroupOfAutomationDevices)
            await itemSource.FireLuLoadStatusChanged();
    }

    private static void LogError(string message, Exception ex)
    {
        Navigator.WriteLog($"Error: {message} Exception: {ex.Message}", LogEntryType.Error);
    }

    private static void MissionCompleted(AutomationDevice? item, PathMission value)
    {
        if (item == null) return;

        LoadUnit? newHandledLu;

        switch (value.MissionType)
        {
            case MissionTypes.Rejection:
                {
                    newHandledLu = ManagedLu.Instance[value.MissionId];

                    newHandledLu ??= new LoadUnit(value.MissionId);

                    newHandledLu.LuCode = value.LuCode;
                    newHandledLu.LuId = value.LuId;
                    newHandledLu.Type = LoadUnit.LoadUnitTypes.LuLoadedAndRejected;
                    newHandledLu.ResultDescription = value.StatusDescription;

                    if (!ManagedLu.Instance.Contains(newHandledLu))
                        ManagedLu.Instance.Add(newHandledLu);
                    break;
                }

            case MissionTypes.Extraction:
                {
                    if (value.LuId == 0)
                        return;

                    newHandledLu = ManagedLu.Instance[value.MissionId];

                    newHandledLu ??= new LoadUnit(value.MissionId);

                    if (value.NoteProcessingId > 0)
                    {
                        newHandledLu.Type = LoadUnit.LoadUnitTypes.LuExtractionByAssignment;
                        newHandledLu.ProcessId = value.NoteProcessingId;
                    }
                    else
                    {
                        newHandledLu.Type = LoadUnit.LoadUnitTypes.LuExtraction;
                        newHandledLu.ProcessId = 0;
                    }

                    newHandledLu.CellId = item.LinkedCellId;
                    newHandledLu.LuCode = value.LuCode;
                    newHandledLu.LuId = value.LuId;
                    newHandledLu.Date = DateTime.Now;
                    newHandledLu.ResultDescription = value.StatusDescription;
                    newHandledLu.NoteHeaderId = 0;

                    newHandledLu.LuExtractionHeaderId = 0;

                    if (!ManagedLu.Instance.Contains(newHandledLu))
                        ManagedLu.Instance.Add(newHandledLu);
                    break;
                }
        }
    }

    private static void MissionStarted(AutomationDevice? item, PathMission value)
    {
        if (item == null) return;

        var newHandledLu = ManagedLu.Instance[value.MissionId];

        newHandledLu ??= new LoadUnit(value.MissionId);

        newHandledLu.Type = LoadUnit.LoadUnitTypes.LuLoading;
        newHandledLu.CellId = item.LinkedCellId;
        newHandledLu.LuCode = value.LuCode;
        newHandledLu.LuId = value.LuId;
        newHandledLu.Date = DateTime.Now;
        newHandledLu.ProcessId = 0;

        if (!ManagedLu.Instance.Contains(newHandledLu))
            ManagedLu.Instance.Add(newHandledLu);
    }

    private static bool SearchReachableItem(AutomationDevice targetItem, AutomationDevice sourceItem, int level,
        List<Guid> stompedItems, List<int> stompedGroupRoutePath)
    {
        if (level > 50) return false;

        stompedItems.Add(sourceItem.ItemGuid);
        if (sourceItem.GroupRoutePathId > 0)
            stompedGroupRoutePath.Add(sourceItem.GroupRoutePathId);

        var destinations = sourceItem.DestinationItemsLinked;

        destinations = destinations.Where(x => x != null && x.ItemGuid != sourceItem.ItemGuid).ToList();

        return destinations.OfType<AutomationDevice>()
            .Where(item =>
                !stompedItems.Contains(item.ItemGuid) && !stompedGroupRoutePath.Contains(item.GroupRoutePathId))
            .Any(item => item.ItemGuid == targetItem.ItemGuid ||
                         SearchReachableItem(targetItem, item, level + 1, stompedItems, stompedGroupRoutePath));
    }

    private static ReserveStorageRequest SetParamsForCheckExistsAvailableCell(ReserveStorageRequest request)
    {
        var reachable = PathDevices.Instance
            .Where(x => x is { DeviceType: DeviceTypes.Storage, RelocateItem: not null })
            .Select(x => x!.RelocateItem!.ItemGuid);

        return request with
        {
            ReachableDeviceIds = [.. request.ReachableDeviceIds, .. reachable],
            CellSearchMode = StorageCellSearchMode.ConnectedAutomaticOperator,
            AssignPosition = false
        };
    }

    private static ReserveStorageRequest SetParamsForStoreInAnyAvailableCell(ReserveStorageRequest request)
    {
        var reachable = Navigator.NumCurrentStoringStep > 0
            ? AvailableStoringAutomationDevices(Navigator.NumCurrentStoringStep).Select(x => x.ItemGuid)
            : PathDevices.Instance
                .Where(x => x is { DeviceType: DeviceTypes.Storage, RelocateItem: not null })
                .Select(x => x!.RelocateItem!.ItemGuid);

        return request with
        {
            ReachableDeviceIds = [.. request.ReachableDeviceIds, .. reachable],
            CellSearchMode = StorageCellSearchMode.FreeAutomaticOperator
        };
    }

    private static ReserveStorageRequest SetParamsForStoreInAnyAvailableCellDifferentStoreLocation(ReserveStorageRequest request)
    {
        var reachable = PathDevices.Instance
            .Where(c => c != null && c.Status != DeviceStatusTypes.OffLine &&
                        c is { IsLinkedToStorage: true, BelongedGroup: null })
            .Select(x => x!.ItemGuid);

        return request with
        {
            ReachableDeviceIds = [.. reachable],
            CellSearchMode = StorageCellSearchMode.FreeAutomaticOperator
        };
    }

    /// <summary>
    /// I tre esiti che il motore distingue davvero (compito 12): cella trovata (con un ulteriore
    /// smistamento per <see cref="StoringSelectionTypes"/>), unità di carico senza referenze, e tutto
    /// il resto dei dodici valori di <see cref="StorageReservationOutcome"/> - stessa forma dello
    /// switch che sostituisce, ora una funzione pura per poterla collaudare senza un
    /// <see cref="AutomationDevice"/> completo.
    /// </summary>
    internal enum ReservationOutcomeCategory
    {
        CellFound,
        LoadUnitHasNoReferences,
        NoCellAvailable
    }

    internal static ReservationOutcomeCategory CategorizeReservationOutcome(StorageReservationOutcome outcome) => outcome switch
    {
        StorageReservationOutcome.CellFound => ReservationOutcomeCategory.CellFound,
        StorageReservationOutcome.LoadUnitHasNoReferences => ReservationOutcomeCategory.LoadUnitHasNoReferences,
        _ => ReservationOutcomeCategory.NoCellAvailable
    };

    /// <summary>
    /// La prenotazione di deposito di questa missione: quella che tiene gia', se e' ancora valida,
    /// altrimenti una nuova (compito 17). La prenotazione <b>appartiene alla missione</b> e resta
    /// valida mentre la missione aspetta: una missione che non trova posto viene riaccodata e
    /// riprova, e prima di questo compito ogni tentativo riscriveva - una scrittura in transazione
    /// attraverso la rete, la chiamata piu' costosa di questo confine, pagata proprio quando
    /// l'impianto e' gia' in difficolta'. In piu', senza prenotazione tenuta, la cella che la
    /// missione aspettava poteva essere presa da un'altra nel frattempo.
    /// <para>
    /// «Non c'e'» e «non e' piu' valida» sono la stessa risposta - <see langword="null"/>, che sul
    /// filo e' un 404: il gestionale non risponde con la prenotazione se l'assegnazione della
    /// posizione non c'e' piu'. In tutt'e due i casi si riprenota.
    /// </para>
    /// <para>
    /// <paramref name="keepExisting"/> distingue le due domande, che non sono la stessa: «trovami un
    /// posto» tiene quello che ha gia', «trovamene un <b>altro</b>» no. La seconda e'
    /// <c>AutomationDeviceStorageManager.FindNewStoreLocation</c>, che parte proprio perche' la cella
    /// che la missione aveva non va piu' bene (e' occupata da un'estrazione, o e' stata bloccata, o
    /// non ha piu' quota): li' la prenotazione si <b>lascia</b> prima di cercare, cosi' la missione
    /// non resta attaccata a un posto che non puo' usare nemmeno se la ricerca nuova non trova
    /// niente - e la <b>posizione presso il gestionale</b> torna libera per chi puo' usarla. Non e'
    /// la stessa cosa del <b>blocco al deposito</b> della cella, che e' un dato del WCS e ha una vita
    /// sua: quello lo mette e lo toglie <c>FindNewStoreLocation</c>, e solo se e' stato lui a
    /// metterlo.
    /// </para>
    /// <para>
    /// Tenuta non vuol dire buona: prima di restituirla si riverifica contro i vincoli di
    /// <b>questo</b> tentativo (<see cref="KeptReservationStillFitsRequestAsync"/>), che sono appena
    /// stati riempiti con lo stato vivo dell'impianto. Se non li rispetta piu' la si lascia e si
    /// riprenota, che e' la stessa strada del percorso «trovamene un altro».
    /// </para>
    /// <para>
    /// Non e' la prenotazione omonima del motore (<see cref="IncomingStepReservation"/>, il gettone
    /// con cui un dispositivo e' prenotato come destinazione di uno step): quella non ha niente a
    /// che vedere con il gestionale.
    /// </para>
    /// </summary>
    internal static async Task<StorageReservation> ReserveOrKeepStorageAsync(IWarehouse warehouse, ReserveStorageRequest request, bool keepExisting)
    {
        if (keepExisting)
        {
            if (await warehouse.GetStorageReservationAsync(request.MissionId) is { Outcome: StorageReservationOutcome.CellFound } kept)
            {
                if (await KeptReservationStillFitsRequestAsync(kept, request))
                    return kept;

                // Non va piu' bene: la si lascia prima di cercare, cosi' la missione non ci resta
                // attaccata nemmeno se la ricerca nuova non trova niente, e la cella torna libera
                // per chi puo' ancora usarla.
                await warehouse.ReleaseStorageReservationAsync(request.MissionId);
            }
        }
        else
        {
            await warehouse.ReleaseStorageReservationAsync(request.MissionId);
        }

        return await warehouse.ReserveStorageAsync(request);
    }

    /// <summary>
    /// La cella tenuta e' ancora una risposta valida a <b>questo</b> tentativo? Due domande sole, le
    /// stesse due che il tentativo pone al gestionale: la cella non e' fra quelle escluse, ed e'
    /// ancora raggiungibile con i dispositivi di adesso.
    /// <para>
    /// E' una verifica <b>locale</b>, non un giro di rete: i due elenchi il motore li ha appena
    /// riempiti lui (<c>SetSearchCellParameter</c>), e il legame fra la cella e la sua via d'accesso
    /// e' un dato del WCS (<see cref="AccessDeviceIdsOfCellAsync"/>). Il guadagno del compito 17 -
    /// non riscrivere attraverso la rete a ogni tentativo - resta intero.
    /// </para>
    /// <para>
    /// Nessun dispositivo raggiungibile vuol dire nessuna cella raggiungibile, non «nessun
    /// vincolo»: quando l'ultimo operatore va fuori linea l'elenco si svuota, ed e' proprio il caso
    /// in cui tenere la cella terrebbe ferma la missione per sempre. La prenotazione si lascia e la
    /// ricerca nuova risponde «nessuna cella», cioe' la missione viene rifiutata invece che
    /// dimenticata.
    /// </para>
    /// </summary>
    private static async Task<bool> KeptReservationStillFitsRequestAsync(StorageReservation kept, ReserveStorageRequest request)
    {
        if (request.ExcludedCellIds.Contains(kept.CellId))
            return false;

        var access = await AccessDeviceIdsOfCellAsync(kept.CellId);

        return access.Any(request.ReachableDeviceIds.Contains);
    }

    /// <summary>
    /// I modi in cui i vincoli di una richiesta possono nominare la via d'accesso a una cella di
    /// deposito. I cinque <c>SetParamsFor...</c> non usano tutti lo stesso: chi cerca «una cella
    /// libera con un operatore automatico» elenca i dispositivi di deposito, gli altri elencano i
    /// ricollocatori (o il gruppo a cui appartengono). Sono lo stesso impianto visto da due lati,
    /// quindi qui si restituiscono tutti e quattro i nomi e basta che il tentativo ne conosca uno.
    /// <para>
    /// Una cella che il motore non sa mappare a un dispositivo non ha nessun nome, quindi nessun
    /// tentativo la raggiunge: e' la risposta giusta, non un caso da aggirare - una cella a cui il
    /// motore non sa arrivare non e' un posto dove mandare una missione.
    /// </para>
    /// </summary>
    private static async Task<IReadOnlyList<Guid>> AccessDeviceIdsOfCellAsync(decimal cellId)
    {
        if (PathDevices.Instance[await DbInfo.GetItemId(cellId)] is not { } storage)
            return [];

        List<Guid> ids = [storage.ItemGuid];

        if (storage.BelongedGroup is { } storageGroup)
            ids.Add(storageGroup.ItemGuid);

        if (storage.RelocateItem is { } relocator)
        {
            ids.Add(relocator.ItemGuid);
            if (relocator.BelongedGroup is { } relocatorGroup)
                ids.Add(relocatorGroup.ItemGuid);
        }

        return ids;
    }

    /// <summary>
    /// La ricerca della cella di deposito di questa missione: prenotazione presso il gestionale,
    /// porta, indirizzo e destinazione. E' l'imbuto per cui passano tutte le ricerche che prenotano
    /// per una missione - quella del giro delle rotte e quelle dei thread dei dispositivi
    /// (<c>VerifyWhereAllocateLuGrouped</c>, <c>FindNewStoreLocation</c> e la sua gemella di
    /// gruppo) - ed e' qui che si escludono con il cambio di destinazione dell'operatore (compito 3 delle missioni).
    /// </summary>
    /// <remarks>
    /// <para>
    /// La prenotazione e' una per missione e il rilascio va per missione: una ricerca e un cambio
    /// verso un magazzino che prenotano insieme per la stessa missione si tolgono la cella a
    /// vicenda, e la missione resta con la porta di uno e la cella dell'altro. Per questo le due
    /// cose non si sovrappongono: il cambio non comincia mentre una ricerca e' qui dentro
    /// (<see cref="TryBeginOperatorChange"/> risponde no). In campo l'esclusione avviene gia' prima
    /// di arrivare qui: le ricerche dei dispositivi partono dentro un avvio contato
    /// (<see cref="TryBeginStepStart"/>: il deposito, e il lavoro dei gruppi sulle loro missioni), e
    /// quella del giro delle rotte con il posto delle ricerche preso (<c>Navigator.TryClaimCellSearch</c>):
    /// con l'una o l'altra cosa il cambio non comincia, e con il cambio cominciato non partono.
    /// L'attesa qui sotto - una ricerca che arriva mentre il cambio e' in corso lo aspetta prima della
    /// sua prima chiamata al gestionale - resta la rete per chi ci arrivasse senza passare di li'.
    /// </para>
    /// <para>
    /// Dopo l'attesa la ricerca riguarda il segno: se il cambio e' passato la destinazione l'ha
    /// scelta l'operatore, e la ricerca finisce senza prenotare niente
    /// (<see cref="StorageLuResultTypes.ChosenByOperator"/>). Prenotare adesso, con la cella
    /// dell'operatore gia' tenuta, la potrebbe lasciare (la tenuta che non va bene a questa ricerca
    /// si rilascia) e la missione andrebbe verso una cella che il gestionale non le tiene piu'. Per
    /// la stessa esclusione, il segno non si alza piu' a meta' di <c>FindRouteAllocateLuCore</c>: le
    /// guardie di scrittura dei <c>SetDataFor...</c> e <see cref="ReleaseStorageReservationTakenForNothingAsync"/>
    /// restano la rete di chi alzasse il segno senza passare dal cambio.
    /// </para>
    /// </remarks>
    private async Task<StorageLuResultTypes> FindRouteAllocateLu(StoringSelectionTypes selectionType, bool keepExistingReservation,
        AutomationDevice? depositor)
    {
        await EnterStorageSearchAsync();
        try
        {
            if (OperatorDestination)
                return StorageLuResultTypes.ChosenByOperator;

            return await FindRouteAllocateLuCore(selectionType, keepExistingReservation, depositor);
        }
        finally
        {
            LeaveStorageSearch();
        }
    }

    /// <param name="depositor">Chi fa il deposito; <c>null</c> per la posizione (<see cref="GetStoreToWarehouse"/>).</param>
    private async Task<StorageLuResultTypes> FindRouteAllocateLuCore(StoringSelectionTypes selectionType, bool keepExistingReservation,
        AutomationDevice? depositor)
    {
        if (PositionItem == null)
            return StorageLuResultTypes.NoAutomaticCellSetForItem;

        // Il magazzino del gestionale si legge dalla cella collegata di chi deposita: per difetto la
        // posizione, com'era.
        var anchor = depositor ?? PositionItem;

        if (anchor.LinkedCellId == 0)
            return StorageLuResultTypes.NoAutomaticCellSetForItem;

        var idMagazzino = (await DescribeCellAsync(anchor.LinkedCellId)).WarehouseId;

        if (idMagazzino == 0)
            return StorageLuResultTypes.NoWarehouseSetForAutomaticCell;

        var request = new ReserveStorageRequest(
            MissionId,
            LuId,
            idMagazzino,
            await Persistence.LocalQueries.GetAutomaticAreasByWarehouseId(idMagazzino),
            [],
            [],
            StorageAreaSelectionMode.All,
            StorageCellSearchMode.FreeAutomaticOperator,
            CellFullnessPreference.Fullest,
            StorageSearchOperatorType.Automatic,
            AssignPosition: true,
            VerifyPosition: false,
            Navigator.WorkstationId,
            Navigator.UserId);

        request = await SetSearchCellParameter(selectionType, request, depositor);

        // Le celle che il PLC ha rifiutato a questa missione (finche' l'UDC non deposita): per ogni modo
        // di cercare, accanto a quelle che il modo esclude gia'.
        if (CellsRefusedByThePlc is { Count: > 0 } refused)
            request = request with
            {
                ExcludedCellIds = [.. request.ExcludedCellIds, .. refused.Where(cell => !request.ExcludedCellIds.Contains(cell))]
            };

        var reservation = await ReserveOrKeepStorageAsync(
            Navigator.RequireWarehouse($"FindRouteAllocateLu sull'unita' di carico {LuId}"), request, keepExistingReservation);

        var result = CategorizeReservationOutcome(reservation.Outcome) switch
        {
            ReservationOutcomeCategory.CellFound => selectionType switch
            {
                StoringSelectionTypes.StoreInAnyAvailableCell =>
                   await SetDataForStoreInAnyAvailableCellDifferentStoreLocation(reservation),
                StoringSelectionTypes.StoreInReachableCell =>
                    await SetDataForStoreInAnyAvailableCellDifferentStoreLocation(reservation),
                StoringSelectionTypes.StoreInAnyAvailableCellDifferentStoreLocation =>
                    await SetDataForStoreInAnyAvailableCellDifferentStoreLocation(reservation),
                StoringSelectionTypes.CheckExistsAvailableCell => await SetDataForCheckExistsAvailableCell(reservation),
                StoringSelectionTypes.StoreInLinkedCell => await SetDataForStoreInLinkedCell(reservation, depositor),
                _ => StorageLuResultTypes.NoCellAvailable
            },
            ReservationOutcomeCategory.LoadUnitHasNoReferences => StorageLuResultTypes.LuWithNoReference,
            _ => StorageLuResultTypes.NoCellAvailable
        };

        if (result == StorageLuResultTypes.ChosenByOperator)
            await ReleaseStorageReservationTakenForNothingAsync(reservation);

        // Con la rotta presa da un altro (RouteTakenOver) chi ha perso restituisce la cella che ha preso,
        // ne toglie la porta e conta il fallimento nel freno (risposta 6 del proprietario dell'11/10). Con
        // la missione che aspetta il rigetto dal campo rimandato (AwaitingRejection) la cella torna lo
        // stesso, perche' la missione va allo scarto, ma nessuno ha perso niente e il freno non conta:
        // contato, il giro saltava la missione per l'attesa del freno, e rimandava proprio il rigetto che
        // lei aspetta (secondo giro di correzioni del compito 8 della voce 41).
        if (result is StorageLuResultTypes.RouteTakenOver or StorageLuResultTypes.AwaitingRejection)
            await GiveBackTheCellOfALostSearchAsync(reservation,
                awaitingRejection: result == StorageLuResultTypes.AwaitingRejection);

        return result;
    }

    /// <summary>
    /// La ricerca della cella ha preso una cella e ne ha scritto la porta, ma la rotta verso di lei l'ha
    /// cambiata un altro mentre la si scriveva (<see cref="StorageLuResultTypes.RouteTakenOver"/>), o non si
    /// e' scritta perche' la missione aspetta il rigetto dal campo rimandato
    /// (<see cref="StorageLuResultTypes.AwaitingRejection"/>): la ricerca restituisce la cella al gestionale
    /// e toglie i campi della porta che aveva scritto (risposta 6 del proprietario dell'11/10, il disegno C:
    /// il perdente «restituisce la cella presa»). Solo nel primo caso conta il fallimento nel freno del giro
    /// delle rotte.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Prima la cella restava della missione, con la porta scritta, e il freno non contava niente: la
    /// missione era gia' di nuovo in coda, e una missione che perdeva sempre rifaceva la ricerca, con la
    /// sua domanda al gestionale, a ogni giro.
    /// </para>
    /// <para>
    /// I campi della porta si tolgono solo se sono ancora quelli di questa ricerca e nessuno li usa, sotto
    /// il lucchetto della rotta (<see cref="ForgetTheDoorOfALostSearch"/>): chi ha vinto puo' averne
    /// scritti altri, e quelli restano; e puo' aver scritto la stessa porta, o costruito la sua rotta
    /// attraverso di lei, e allora porta, segno e cella sono anche suoi, e restano tutti. La porta da sola
    /// non dice chi l'ha scritta: due ricerche della stessa missione possono correre insieme (quella del
    /// giro delle rotte e quella di un dispositivo; <see cref="EnterStorageSearchAsync"/> le conta, non le
    /// esclude) e prendere la stessa cella tenuta, con la stessa porta. Il segno che la porta e' in uso e'
    /// una rotta della missione che arriva al suo dispositivo. La destinazione e la rotta sono di chi ha
    /// vinto, e non si toccano. La cella si restituisce solo se la porta non e' in uso e il gestionale
    /// tiene ancora proprio lei per la missione: la prenotazione e' una per missione, e il rilascio
    /// toglierebbe quella di chi ha vinto. Il dispositivo della porta si legge prima (database del WCS), e
    /// le due chiamate al gestionale dopo, fuori da ogni lucchetto; un guasto si scrive e basta, e la cella
    /// la libera la scadenza del gestionale.
    /// </para>
    /// <para>
    /// Fino al primo giro di correzioni del compito 8 della voce 41 bastava che la porta fosse la stessa:
    /// chi perdeva contro una ricerca che aveva scritto la stessa porta le toglieva porta e segno e
    /// restituiva la cella, e la missione restava con la rotta di chi aveva vinto verso il magazzino, senza
    /// porta e senza cella.
    /// </para>
    /// </remarks>
    /// <param name="reservation">La cella che la ricerca ha preso.</param>
    /// <param name="awaitingRejection">
    /// Vero se la rotta non si e' scritta perche' la missione aspetta il rigetto dal campo rimandato
    /// (<see cref="RouteWriteOutcome.AwaitingRejection"/>), non perche' l'ha cambiata un altro: la cella
    /// torna al gestionale e la porta si toglie allo stesso modo, ma il freno non conta niente e le righe
    /// dicono il perche' vero. Contato, il giro delle rotte saltava la missione per l'attesa del freno
    /// (<c>Navigator.RoutePathRetries.IsWaiting</c>) e rimandava il rigetto che lei aspetta (secondo giro
    /// di correzioni del compito 8 della voce 41).
    /// </param>
    private async Task GiveBackTheCellOfALostSearchAsync(StorageReservation reservation, bool awaitingRejection)
    {
        var doorDevice = await DbInfo.GetItemId(reservation.DoorCode);
        var door = ForgetTheDoorOfALostSearch(reservation.DoorCode, doorDevice);

        // Il perche' della rotta non scritta, per le righe qui sotto: un altro l'ha cambiata, o la
        // missione aspetta il rigetto dal campo rimandato. Solo il primo e' un fallimento per il freno.
        var why = awaitingRejection
            ? $"la rotta verso la cella {reservation.CellId} (porta {reservation.DoorCode}) non si e' scritta perche' la missione aspetta il rigetto dal campo, che il giro delle rotte rifa'"
            : $"la rotta verso la cella {reservation.CellId} (porta {reservation.DoorCode}) l'ha cambiata un altro";

        if (!awaitingRejection)
            Navigator.CountALostCellSearch(this);

        if (door == LostSearchDoor.InUse)
        {
            Navigator.WriteLog(
                $"Missione {MissionId}: {why}, ma una rotta della missione va alla stessa porta: porta e cella restano",
                LogEntryType.Information);
            return;
        }

        try
        {
            var warehouse = Navigator.RequireWarehouse($"GiveBackTheCellOfALostSearch sulla missione {MissionId}");

            if (await warehouse.GetStorageReservationAsync(MissionId) is not { Outcome: StorageReservationOutcome.CellFound } held ||
                held.CellId != reservation.CellId)
                return;

            await warehouse.ReleaseStorageReservationAsync(MissionId);

            Navigator.WriteLog(
                door == LostSearchDoor.Forgotten
                    ? $"Missione {MissionId}: {why}, e la cella torna al gestionale con la sua porta"
                    : $"Missione {MissionId}: {why}, e la cella torna al gestionale; la porta di adesso l'ha scritta un altro",
                LogEntryType.Information);
        }
        catch (Exception ex)
        {
            Navigator.WriteLog(
                $"Missione {MissionId}: la cella {reservation.CellId} non e' stata restituita ({why}): {ex.Message}",
                LogEntryType.Warning);
        }
    }

    /// <summary>Che cosa ha trovato <see cref="ForgetTheDoorOfALostSearch"/>.</summary>
    private enum LostSearchDoor
    {
        /// <summary>La porta era ancora quella della ricerca e nessuna rotta la usava: i campi sono tolti.</summary>
        Forgotten,

        /// <summary>La porta e' un'altra, scritta da chi ha vinto: niente e' toccato.</summary>
        Another,

        /// <summary>La porta e' la stessa, e una rotta della missione va al suo dispositivo: niente e' toccato.</summary>
        InUse
    }

    /// <summary>
    /// Vedi <see cref="GiveBackTheCellOfALostSearchAsync"/>: la porta, l'indirizzo di campo e la profondita'
    /// scritti dalla ricerca, con il segno della cella sua e la sua conferma, se la porta e' ancora
    /// <paramref name="doorCode"/> e nessuno step della missione arriva a <paramref name="doorDevice"/>.
    /// Sotto il lucchetto della rotta, come le scritture della rotta.
    /// </summary>
    /// <param name="doorCode">La porta che la ricerca ha scritto.</param>
    /// <param name="doorDevice">Il dispositivo della porta; <see cref="Guid.Empty"/> se non ne ha uno.</param>
    /// <remarks>
    /// Tolta la porta, la versione della rotta sale: una rotta calcolata con la porta ancora scritta (per
    /// esempio dopo lo spostamento che ha vinto, <c>MissionGateway.BuildTheRouteAtOnce</c>) non si scrive
    /// piu', e la missione torna al giro. Se la destinazione e' ancora il magazzino della porta, la
    /// missione ha perso la cella (<see cref="StorageCellLost"/>): nessuno le costruisce una rotta verso il
    /// magazzino nudo (<see cref="BuildPathToDestination"/>), e un'altra cella gliela prenota il navigatore
    /// (<c>MissionGateway.KeepTheStorageCellAsync</c>), come per ogni cella persa. Con la scelta
    /// dell'operatore il segno non serve: la guardia della rotta guarda gia' lei.
    /// </remarks>
    private LostSearchDoor ForgetTheDoorOfALostSearch(string doorCode, Guid doorDevice)
    {
        lock (_route)
        {
            if (StorageDoorCode != doorCode)
                return LostSearchDoor.Another;

            if (doorDevice != Guid.Empty &&
                Collections.MissionSteps.Instance.Any(x => x.MissionId == MissionId && x.ToNodeId == doorDevice))
                return LostSearchDoor.InUse;

            TakeStorageCellConfirmation();
            StorageDoorCode = null;
            StorageCellReserved = false;
            DestinationAsi = string.Empty;
            DestinationSubitem = string.Empty;
            DestinationPartition = string.Empty;

            // Come per la cella persa (LeaveTheDoor): con l'UDC ancora da prelevare StorageDepth e' la
            // profondita' a cui sta, e serve al prelievo.
            if (!StillToBePickedUp) StorageDepth = 0;

            if (!OperatorDestination && doorDevice != Guid.Empty && DestinationItemId == doorDevice &&
                DestinationItem is { DeviceType: DeviceTypes.Storage })
                StorageCellLost = true;

            Interlocked.Increment(ref _routeVersion);
            return LostSearchDoor.Forgotten;
        }
    }

    /// <summary>
    /// La ricerca ha preso (o tenuto) una cella, e intanto l'operatore ha scelto la destinazione: la
    /// cella non serve piu' a nessuno e torna al gestionale, come fa chi rinuncia a una prenotazione
    /// (compito 17). Resta in due casi: la sua porta e' proprio la destinazione scelta; oppure il
    /// gestionale per questa missione tiene ormai un'altra cella.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In campo non ci si arriva piu': il segno non si alza a meta' ricerca, perche' il cambio
    /// dell'operatore e le ricerche si escludono (<see cref="FindRouteAllocateLu"/>). Resta come rete
    /// per chi alzasse il segno senza passare dal cambio, ed e' una rete grossolana: guarda il
    /// dispositivo della porta e non la cella che la missione nomina, e la domanda e il rilascio
    /// sono due chiamate separate.
    /// </para>
    /// <para>
    /// Il secondo caso viene dal contratto: la prenotazione e' una per missione, e il rilascio va per
    /// missione, non per cella. Rilasciare quando il gestionale tiene ormai un'altra cella
    /// toglierebbe quella, non quella della ricerca (che il gestionale ha gia' lasciato quando ha dato
    /// l'altra). Si guarda quale cella il gestionale tiene, e si rilascia solo se e' ancora quella
    /// della ricerca.
    /// </para>
    /// <para>
    /// Fuori da ogni lucchetto, e senza il gettone di annullamento di una richiesta HTTP: un rilascio
    /// perso lo copre la scadenza del gestionale, una risposta persa no. Un guasto si registra e
    /// basta, come negli altri rilasci di cortesia: la missione dell'operatore non ne dipende. Dai
    /// thread dei dispositivi la ricerca gira sotto il loro semaforo di esecuzione, come tutta la
    /// ricerca della cella (la prenotazione compresa): queste due chiamate sono della stessa specie.
    /// </para>
    /// </remarks>
    private async Task ReleaseStorageReservationTakenForNothingAsync(StorageReservation reservation)
    {
        try
        {
            if (await DbInfo.GetItemId(reservation.DoorCode) == DestinationItemId) return;

            var warehouse = Navigator.RequireWarehouse($"ReleaseStorageReservationTakenForNothing sulla missione {MissionId}");

            if (await warehouse.GetStorageReservationAsync(MissionId) is not { Outcome: StorageReservationOutcome.CellFound } held ||
                held.CellId != reservation.CellId)
                return;

            await warehouse.ReleaseStorageReservationAsync(MissionId);
        }
        catch (Exception ex)
        {
            Navigator.WriteLog(
                $"Missione {MissionId}: la cella {reservation.CellId} presa da una ricerca superata dalla scelta dell'operatore non e' stata restituita: {ex.Message}",
                LogEntryType.Warning);
        }
    }

    private async Task<List<MissionStep>> GetStepsToChangeStoreLocation()
    {
        List<MissionStep> resultSteps = [];

        var sourceGuid = await DbInfo.GetItemId(PickUpDoorCode);
        var destinationGuid = await DbInfo.GetItemId(StorageDoorCode);

        if (sourceGuid == destinationGuid)
        {
            // Missione di spostamento da cella di deposito a cella di deposito tramite lo stesso AutomationDeviceBase (Tipo Traslo o Autosat Mover dello stesso piano)

            resultSteps = [];

            if (PositionItemId == RelocateItemId)
            {
                // Se si trova a bordo dell'elemento preposto agli spostamenti (Traslo, Mover)

                var stepTo = new MissionStep
                {
                    StepId = Guid.NewGuid(),
                    MissionId = MissionId,
                    Date = Date,
                    FromNodeId = RelocateItemId,
                    ToNodeId = DestinationItemId
                };

                resultSteps.Add(stepTo);
            }
            else
            {
                // Se si trova a bordo ancora nella cella di deposito

                var stepFrom = new MissionStep
                {
                    // Viene aggiunta la missione di caricamento sull'elemento preposto agli spostamenti (Traslo, Mover)

                    StepId = Guid.NewGuid(),
                    MissionId = MissionId,
                    Date = Date,
                    FromNodeId = SourceItemId,
                    ToNodeId = RelocateItemId
                };

                resultSteps.Add(stepFrom);

                var stepTo = new MissionStep
                {
                    // Viene aggiunta la missione di deposito in magazzino

                    StepId = Guid.NewGuid(),
                    MissionId = MissionId,
                    Date = Date,
                    FromNodeId = RelocateItemId,
                    ToNodeId = DestinationItemId
                };

                resultSteps.Add(stepTo);
            }

            return resultSteps;
        }
        // Missione di spostamento da cella di deposito a cella di deposito tramite diversi AutomationDeviceBase

        if (DestinationItemId == Guid.Empty || PositionItemId == DestinationItemId)
            return resultSteps;

        resultSteps = GetMissionStepsToDestination();

        return resultSteps;
    }

    private async Task<List<MissionStep>> GetStepToDefragmentCell()
    {
        // Missione di compattazione di celle con partizioni
        List<MissionStep> resultSteps = [];

        if (PositionItem is { BelongedGroup: not null } &&
            PositionItem.BelongedGroup.ItemGuid == RelocateItemId)
        {
            // Se si trova a bordo dell'elemento preposto agli spostamenti (Traslo, Mover)

            var stepTo = new MissionStep
            {
                StepId = Guid.NewGuid(),
                MissionId = MissionId,
                Date = Date,
                FromNodeId = PositionItemId,
                ToNodeId = DestinationItemId
            };

            resultSteps.Add(stepTo);

            return resultSteps;
        }
        else
        {
            // L'UDC e' ancora nella cella di partenza solo se la posizione e' la sorgente: prima
            // lo si deduceva dal gateway, che si azzerava a ogni cambio di posizione; dal
            // 2026-09-04 il gateway resta finche' non viene raggiunto, e un ripristino (avvio,
            // movimento di gruppo) puo' riscrivere la posizione senza passare da uno step.
            if (Gateway == Guid.Empty || PositionItemId != SourceItemId)
            {
                var item = PositionItem;

                if (item != null && item.DeviceType != DeviceTypes.Storage)
                {
                    if (item.DeviceType != DeviceTypes.Storage &&
                        item.DeviceType != DeviceTypes.GroupOfAutomationDevices)
                        await item.FireLuLoadStatusChanged();

                    if (GuidAssignmentLu != Guid.Empty)
                    {
                        if (LuId > 0)
                            Navigator.WriteLog(
                                $"DEBUG INFO: Cancellazione ASSEGNAZIONE_NOTE da BuildRouteToDestination->IdAssegnazioneNota UDC: {LuId} - {LuCode} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                                LogEntryType.Information);

                        // Compito 21, secondo dei tre punti dello stesso fatto: la missione di
                        // compattazione non trova piu' l'unita' di carico nella cella di partenza e
                        // si toglie da sola, poche righe piu' giu'. Stesso fatto di
                        // ActiveMissions.Remove - chiave, forma e semantica - quindi stessa
                        // operazione; cambia il motivo, che e' quello che un operatore legge.
                        await Navigator.RequireWarehouse("GetStepToDefragmentCell")
                            .ReportMissionLeftAssignmentUnservedAsync(new MissionLeftAssignmentUnservedRequest(
                                MissionId, LuId, GuidAssignmentLu, 0m,
                                "la missione di compattazione non trova l'unita' di carico nella cella di partenza"));
                    }

                    if (ExtractionQueueId > 0)
                    {
                        if (LuId > 0)
                        {
                            Navigator.WriteLog(
                                $"DEBUG INFO: Cancellazione ASSEGNAZIONE_NOTE da BuildRouteToDestination->IdExtractionQueue UDC: {LuId} - {LuCode} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                                LogEntryType.Information);
                            Navigator.WriteLog(
                                $"DEBUG INFO: Cancellazione ESTRAZIONI_UDC_AUTOMATICHE da BuildRouteToDestination->IdExtractionQueue UDC: {LuId} - {LuCode} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                                LogEntryType.Information);
                        }

                        // Compito 13: lo stesso fatto di poche righe sopra, con l'altro campo -
                        // l'assegnazione nominata per riga e non per GUID. Vale parola per parola
                        // la nota di ActiveMissions.Remove.
                        await DbInfo.ReportQueuedAssignmentLeftUnserved(MissionId, LuId, ExtractionQueueId,
                            "la missione di compattazione non trova l'unita' di carico nella cella di partenza");
                        await DbInfo.DeleteExtractionItemQueueById(ExtractionQueueId);
                    }
                }

                if (LuId > 0)
                    Navigator.WriteLog(
                        $"DEBUG INFO: Cancellazione MISSION_LU da BuildRouteToDestination UDC: {LuId} - {LuCode} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                        LogEntryType.Information);

                ORM.PathMission.Remove(this);
                await ActiveMissions.Instance.RemoveUnused(this);
                return resultSteps;
            }

            // Se si trova a bordo ancora nella cella di deposito

            var stepFrom = new MissionStep
            {
                // Viene aggiunta la missione di caricamento sull'elemento preposto agli spostamenti (Traslo, Mover)

                StepId = Guid.NewGuid(),
                MissionId = MissionId,
                Date = Date,
                FromNodeId = SourceItemId,
                ToNodeId = Gateway
            };

            resultSteps.Add(stepFrom);

            var stepTo = new MissionStep
            {
                // Viene aggiunta la missione di deposito in magazzino

                StepId = Guid.NewGuid(),
                MissionId = MissionId,
                Date = Date,
                FromNodeId = Gateway,
                ToNodeId = DestinationItemId
            };

            resultSteps.Add(stepTo);

            return resultSteps;
        }
    }

    /// <summary>Come e' finito l'arrivo di un <see cref="MissionTypes.PickUpAndStoreAgain"/> (<see cref="HandlePickUpAndStoreAgainMission"/>).</summary>
    private enum ArrivalOutcome
    {
        /// <summary>La missione e' un deposito verso il magazzino della sua porta, con la rotta.</summary>
        Converted,

        /// <summary>
        /// Niente e' scritto, e la missione e' gia' di nuovo in coda al giro delle rotte: un altro ha scritto
        /// la rotta mentre la si calcolava, la missione aspetta il rigetto dal campo rimandato, o uno step
        /// della missione era in volo. Non e' un fallimento.
        /// </summary>
        TakenOver,

        /// <summary>
        /// La porta di deposito non porta a nessun dispositivo, o porta alla posizione: non c'e' un magazzino
        /// verso cui convertire la missione. Riprovare non cambia niente (dipende solo dalla porta), e la
        /// missione la tratta il giro come le altre (la cella prenotata, la ricerca del deposito).
        /// </summary>
        NoStore,

        /// <summary>
        /// Il magazzino della porta c'e', ma nessuna rotta verso di lui: la missione e' di nuovo in coda, e il
        /// giro lo conta nel freno.
        /// </summary>
        NoRoute
    }

    /// <summary>
    /// L'arrivo di un <see cref="MissionTypes.PickUpAndStoreAgain"/> sulla sua destinazione: la missione
    /// diventa un deposito (<see cref="MissionTypes.StoringByPickUp"/>) verso il magazzino della sua porta
    /// di deposito, con la rotta dalla posizione, e la cella di quella porta si sblocca.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Voce 41, corsa 2 della ricognizione: l'arrivo legge la porta e aspetta il database del WCS, e
    /// intanto il giro delle rotte puo' prenotare alla missione un'altra cella e scriverle porta,
    /// destinazione e rotta. Prima l'arrivo scriveva dopo, sopra: la destinazione e la rotta verso il
    /// magazzino della porta letta prima, la porta e la cella del navigatore, e sbloccava la porta che la
    /// missione aveva allora, quella del navigatore.
    /// </para>
    /// <para>
    /// Adesso il calcolo sta fuori, sulla versione della rotta letta prima della porta, e la scrittura
    /// passa dalla primitiva (<see cref="TryWriteRoute"/>): la sorgente, il gateway, la destinazione, il
    /// tipo, la porta di prelievo e la rotta, tutti insieme. Se la versione e' cambiata non si scrive
    /// niente, e la missione torna al giro delle rotte (<see cref="BackToTheRouteLoop"/>). La porta letta
    /// all'inizio si sblocca lo stesso, anche nel database del WCS: chi perde lascia la porta che aveva
    /// preso, e se la missione la tiene ancora la risblocca la conversione rifatta dal giro (risposta 3 del
    /// proprietario dell'11/10). Prima restava bloccata per il deposito, anche dopo un riavvio.
    /// </para>
    /// <para>
    /// Senza un magazzino verso cui andare (la porta non porta a nessun dispositivo, o porta alla
    /// posizione) si scrivono la sorgente, il gateway e la destinazione, come prima, e nessuna rotta; lo
    /// stesso senza una rotta, con in piu' il tipo e la porta di prelievo. La rotta si calcola come la
    /// calcolava prima, quando il tipo era gia' quello del deposito: lo dice alla sonda l'argomento
    /// <c>asType</c>, perche' sulla missione il tipo si scrive solo insieme alla rotta.
    /// </para>
    /// <para>
    /// Se il setter della destinazione scarta la scrittura (uno step della missione in volo, per
    /// esempio quello che l'ha portata qui e che il completamento non ha ancora tolto) restano scritti
    /// la sorgente e il gateway, come prima, e nient'altro: niente tipo, porta di prelievo, rotta o
    /// sblocco. Un arrivo che non converte la missione la rimette al giro delle rotte, che rifa' la
    /// conversione quando lo step precedente e' finito (<see cref="RedoInTheRouteLoopAsync"/>): prima
    /// nessuno la ritentava, e la missione restava ferma sulla sua destinazione (risposta 3 del
    /// proprietario dell'11/10). Senza un magazzino verso cui andare la rimette in coda chi chiama, se
    /// vuole: l'arrivo dal campo si', il giro no, che va avanti con lei come prima del compito 8 della
    /// voce 41 (<see cref="ArrivalOutcome.NoStore"/>, giro finale delle correzioni).
    /// </para>
    /// </remarks>
    private async Task<ArrivalOutcome> HandlePickUpAndStoreAgainMission()
    {
        var seen = RouteVersion;
        var doorCode = StorageDoorCode;
        var destination = await DbInfo.GetItemId(doorCode);

        if (ArrivalPauseForTesting is { } pause)
            await pause();

        var position = PositionItemId;
        var towardsTheStore = destination != Guid.Empty && destination != position;
        var steps = towardsTheStore
            ? ProbeRouteSteps(destination, Guid.Empty, asType: MissionTypes.StoringByPickUp)
            : [];
        var stepInFlight = false;

        var outcome = TryWriteRoute(seen, steps, () =>
        {
            SourceItemId = position;
            Gateway = Guid.Empty;
            DestinationItemId = destination;

            // Il setter della destinazione scarta la scrittura se uno step della missione e' in volo:
            // all'arrivo puo' esserlo ancora quello che ha portato l'UDC qui, perche' il carico che
            // cambia la posizione parte (Task.Run) prima che il completamento tolga lo step eseguito.
            // Senza la destinazione non si scrive nient'altro: tipo, porta di prelievo e rotta verso il
            // magazzino lascerebbero la missione con la destinazione vecchia. Prima lo diceva la
            // rilettura della destinazione dopo l'assegnazione, con il ritorno anticipato.
            if (DestinationItemId != destination)
            {
                stepInFlight = true;
                return false;
            }

            if (!towardsTheStore)
                return false;

            MissionType = MissionTypes.StoringByPickUp;
            PickUpDoorCode = string.Empty;
            return steps.Count > 0;
        });

        switch (outcome)
        {
            case RouteWriteOutcome.Written:
                await DbInfo.SetCellUnlockedForStoring(doorCode);
                return ArrivalOutcome.Converted;

            // Chi ha perso, e la missione che aspetta il rigetto dal campo rimandato (il giro rifa' prima il
            // rigetto, e la missione va allo scarto, non nella cella): niente e' scritto, la missione torna al
            // giro e la porta letta all'inizio si sblocca. Se il rigetto non si fa, il giro rifa' la
            // conversione, che la sblocca di nuovo.
            case RouteWriteOutcome.Lost:
            case RouteWriteOutcome.AwaitingRejection:
                BackToTheRouteLoop();
                await DbInfo.SetCellUnlockedForStoring(doorCode);
                return ArrivalOutcome.TakenOver;

            default:
                if (stepInFlight)
                {
                    BackToTheRouteLoop();
                    return ArrivalOutcome.TakenOver;
                }

                // Senza un magazzino verso cui andare la missione non torna al giro da qui: lo decide chi
                // chiama (l'arrivo dal campo la rimette in coda, il giro va avanti con lei). Con il
                // magazzino e senza una rotta, si'.
                if (!towardsTheStore)
                    return ArrivalOutcome.NoStore;

                BackToTheRouteLoop();
                return ArrivalOutcome.NoRoute;
        }
    }

    /// <summary>
    /// Quello che il giro delle rotte rifa' per la missione prima di cercarle una cella o una rotta, se
    /// un altro l'ha lasciato a lui (risposte 1, 3 e 5 del proprietario dell'11/10): il rigetto dal campo
    /// rimandato per uno step in volo, la conversione di un <see cref="MissionTypes.PickUpAndStoreAgain"/>
    /// arrivato sulla sua destinazione, e la rotta di un rigetto che l'ha persa contro uno spostamento. Il
    /// giro la chiama solo quando nessuno step della missione e' in volo (<c>Navigator.BuildRoutePath</c>).
    /// </summary>
    /// <returns>
    /// L'esito per il giro, come quello della ricerca: <c>Available</c> con la rotta fatta,
    /// <c>TakenOver</c> se la missione e' gia' di nuovo in coda (un altro ha scritto, o uno step e' partito
    /// intanto: non e' un fallimento), <c>NotAvailable</c> se non si e' potuto (il freno lo conta).
    /// <c>null</c> se non c'e' niente da rifare, e il giro fa quello che fa sempre.
    /// </returns>
    internal async Task<AutomationDevice.RouteBuildStatusTypes?> RedoInTheRouteLoopAsync()
    {
        RejectionTypes? fieldRejection;
        Guid rejectionToComplete;

        // Il segno si toglie prima di rigettare: la rotta del rigetto (TheRejectionRouteIsBuilt) si scrive
        // con il confronto, e con il segno ancora su la guardia di TryWriteRoute non la scriverebbe.
        lock (_route)
        {
            fieldRejection = _fieldRejectionPending;
            _fieldRejectionPending = null;
            rejectionToComplete = _rejectionToComplete;
        }

        if (fieldRejection is { } rejectionType)
        {
            RejectionOutcome outcome;

            try
            {
                // Lo stesso motivo rimandato, non sempre NoLuDataFromPlc, e senza la prima parte del
                // rigetto, gia' fatta alla prima chiamata (giro finale delle correzioni della voce 41).
                outcome = await RejectToAsync(rejectionType, redoneByTheRouteLoop: true);
            }
            catch
            {
                // Un guasto a meta' (la porta letta dal database del WCS, un setter che scrive) non perde il
                // rigetto: il segno torna, se nessuno l'ha gia' rimesso, e il giro lo rifa' alla prossima
                // (StartCellSearch riaccoda la missione con il freno). Prima il segno era gia' tolto, e il
                // rigetto spariva (giro finale delle correzioni della voce 41).
                lock (_route) _fieldRejectionPending ??= rejectionType;
                throw;
            }

            switch (outcome)
            {
                case RejectionOutcome.Rejected:
                    return AutomationDevice.RouteBuildStatusTypes.Available;
                case RejectionOutcome.Postponed:
                case RejectionOutcome.TakenOver:
                    return AutomationDevice.RouteBuildStatusTypes.TakenOver;
            }

            // Dalla posizione di adesso il rigetto non si fa (nessun punto di scarto per i dati dell'UDC, e
            // allora l'avviso l'ha scritto il rigetto; o nessuna rotta verso di lui): la missione va avanti
            // come le altre.
            return null;
        }

        if (rejectionToComplete != Guid.Empty)
        {
            // Il rigetto si completa finche' la missione va ancora verso il suo punto di scarto: un'altra
            // destinazione (l'operatore) o l'arrivo sullo scarto lo chiudono.
            lock (_route)
            {
                if (DestinationItemId != rejectionToComplete || DestinationItemId == PositionItemId)
                {
                    _rejectionToComplete = Guid.Empty;
                    return null;
                }
            }

            if (await BuildPathToDestination())
                return AutomationDevice.RouteBuildStatusTypes.TakenOver;

            if (MissionSteps.Count == 0)
                return AutomationDevice.RouteBuildStatusTypes.NotAvailable;

            lock (_route) _rejectionToComplete = Guid.Empty;
            return AutomationDevice.RouteBuildStatusTypes.Available;
        }

        if (MissionType == MissionTypes.PickUpAndStoreAgain && DestinationItemId == PositionItemId)
        {
            // Una porta che non porta a un magazzino (a nessun dispositivo, o alla posizione stessa) non si
            // risolve ritentando: il giro va avanti con la missione come prima del compito 8 della voce 41
            // (la cella prenotata, la ricerca del deposito). Prima rispondeva NotAvailable a ogni giro, e la
            // missione aspettava con il freno per sempre (giro finale delle correzioni della voce 41).
            return await HandlePickUpAndStoreAgainMission() switch
            {
                ArrivalOutcome.Converted => AutomationDevice.RouteBuildStatusTypes.Available,
                ArrivalOutcome.NoRoute => AutomationDevice.RouteBuildStatusTypes.NotAvailable,
                ArrivalOutcome.NoStore => null,
                _ => AutomationDevice.RouteBuildStatusTypes.TakenOver
            };
        }

        return null;
    }

    private async Task ProcessAllocateOnItem()
    {
        if (PositionItem == null || Navigator.NavStatus == NavigatorStatusTypes.Recover)
            return;

        await AllocateOnItem(PositionItem, this);

        switch (PositionItem.DeviceType)
        {
            case DeviceTypes.Loading:
                MissionStarted(PositionItem, this);
                break;
            case DeviceTypes.LoadingAndBuffer:
                ProcessLoadingAndBuffer();
                break;
            case DeviceTypes.Storage:
                await ProcessStore();
                break;
            default:
                await ProcessDefaultCase();
                break;
        }
    }

    /// <summary>
    /// <c>internal</c> e non <c>private</c> perche' il suo unico chiamante di produzione
    /// (<see cref="ProcessExtractionQueue"/>) parte da un <c>Task.Run</c> senza attesa dentro il
    /// setter della posizione: una prova che entrasse di li' dovrebbe aspettare a tempo, e il tempo
    /// non e' un discriminante. Le prove del compito 21 la chiamano direttamente.
    /// </summary>
    internal async Task ProcessAssignments()
    {
        if (GuidAssignmentLu == Guid.Empty) return;

        try
        {
            if (LuId > 0)
                Navigator.WriteLog(
                    $"DEBUG INFO: Clearing assignment notes from PathMission->Position UDC: {LuCode} - {LuId} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                    LogEntryType.Information);

            // Compito 21, il fatto opposto: qui l'assegnazione e' stata SERVITA - l'unita' di carico
            // e' arrivata alla postazione, e le righe sono gia' state marcate prelevate da
            // ProcessExtractionItems. Non e' lo stesso fatto dei tre punti che dicono «non servita»,
            // anche se oggi la conseguenza del gestionale coincide.
            //
            // L'azzeramento nel finally e' cio' che tiene separati i due fatti: la chiusura della
            // missione che verra' dopo non trovera' piu' un'assegnazione da segnalare come non
            // servita.
            //
            // Voce 45: prima dell'azzeramento la missione ricorda il GUID servito, per poter chiedere al
            // gestionale di riaprire l'ordine se l'operatore manda in un magazzino l'estrazione gia'
            // prelevata. Prima della chiamata, perche' un guasto della chiamata non toglie il fatto.
            ServedAssignmentGuid = GuidAssignmentLu;
            await Navigator.RequireWarehouse("ProcessAssignments").ReportMissionServedAssignmentAsync(
                new MissionServedAssignmentRequest(MissionId, LuId, GuidAssignmentLu,
                    "l'unita' di carico e' arrivata alla postazione"));
        }
        catch (Exception ex)
        {
            LogError("Failed to process assignments", ex);
        }
        finally
        {
            GuidAssignmentLu = Guid.Empty;
        }
    }

    private async Task ProcessDefaultCase()
    {
        if (PositionItem is { ExtractionCheckPoint: true })
        {
            MissionCompleted(PositionItem, this);
            await ProcessStore();
        }
        else if (DestinationItem == PositionItem && !AnyExtractionCheckPoint())
        {
            MissionCompleted(PositionItem, this);
        }
    }

    /// <summary>
    /// <c>internal</c> e non <c>private</c> per la stessa ragione di <see cref="ProcessAssignments"/>,
    /// che gli sta accanto nello stesso giro: l'unico chiamante di produzione
    /// (<see cref="ProcessExtractionQueue"/>) parte da un <c>Task.Run</c> senza attesa dentro il
    /// setter della posizione, e una prova che entrasse di li' dovrebbe aspettare a tempo. Le prove
    /// del compito 12 la chiamano direttamente: e' qui che nasce l'esito «riga servita».
    /// </summary>
    internal async Task ProcessExtractionItems()
    {
        if (ExtractionQueueId <= 0) return;

        var outcomeDecisionPersisted = false;
        try
        {
            // L'esito Extracted entra prima nell'outbox locale. Dopo questo commit la missione puo'
            // separarsi dalla riga di coda anche se il WMS non risponde: il dispatcher conserva il
            // ritentativo at-least-once e assorbe gli errori di consegna.
            //
            // Voce 45: la riga di coda si legge prima dell'outbox, perche' e' l'ultima occasione di
            // sapere che cosa questo prelievo serve. Se la lettura fallisce non e' deciso niente e il
            // giro si rifa'.
            var servedRow = await ExtractionQueue.ReadAsync(
                context => Persistence.ExtractionQueueStore.FindAsync(context, (long)ExtractionQueueId), null);
            await ExtractionOutcomeOutbox.EnqueueAsync(ExtractionQueueId, ExtractionRowStatus.Extracted);
            outcomeDecisionPersisted = true;

            // Voce 45: la missione ricorda l'assegnazione e la riga di estrazione servite prima che il
            // finally la stacchi dalla riga di coda. Ogni setter scrive la riga della missione, e
            // queste scritture vengono prima di quella che azzera ExtractionQueueId: nel wcs.db non
            // c'e' un momento in cui la missione ha lasciato la coda senza ricordare che cosa serviva.
            // Si copiano appena l'esito «estratta» e' deciso, anche se la conferma qui sotto fallisce:
            // la riga per il gestionale e' comunque prelevata, e se la conferma non e' arrivata la
            // riapertura non trovera' niente da annullare (lo decide il gestionale, non il motore).
            if (servedRow != null)
            {
                ServedAssignmentId = servedRow.AssignmentId ?? 0m;
                ServedExtractionRowId = servedRow.ExtractionRowId ?? 0m;
            }

            await Navigator.ExtractionOutcomeDispatcher.TryDispatchAsync();
            // Compito 13: qui c'era DbInfo.SetAssignmentsNotesProcessed, l'UPDATE ASSEGNAZIONE_NOTE
            // SET PRELEVATO = 1. Il fatto che diceva - «l'assegnazione e' stata servita» - lo dice
            // gia' ProcessAssignments, due righe piu' giu' nello stesso giro di
            // ProcessExtractionQueue.
            //
            // PRELEVATO LA LEGGONO DUE QUERY DEL GESTIONALE (la prima stesura di questo commento
            // diceva il contrario; corretto dalla revisione del compito 13). Stanno tutt'e due in
            // WMS.Services/src/Automazione/MagazzinoPerWcs.cs: AssegnazioniProntePerCodaSql
            // (ISNULL(AN.PRELEVATO, 0) = 0), che e' il filtro con cui il gestionale decide quali
            // assegnazioni consegnare alla coda del WCS, e il pickUpSql di GetCellBusyStateAsync
            // (ASSEGNAZIONE_NOTE.PRELEVATO = 0, «cella occupata da un prelievo»).
            //
            // La scrittura si toglie lo stesso, ma per la ragione vera: su questo percorso
            // l'assegnazione non resta «marcata prelevata», viene CANCELLATA dalla conseguenza del
            // fatto segnalato nello stesso giro. La conferma qui sotto la consuma
            // (CancellaAssegnazioneConfermataSql, DELETE sulla chiave primaria) e
            // ReportMissionServedAssignment cancella per GUID tutte le righe dell'assegnazione: la
            // cancellazione e' piu' forte di PRELEVATO = 1, che lascia la riga dov'e'.
            //
            // IL PERCORSO DI GUASTO, riderivato. Se questa conferma non riesce, il catch qui sotto
            // ferma il giro PRIMA di DeleteExtractionItemQueueById: la riga di coda del WCS resta.
            // L'assegnazione torna candidata di AssegnazioniProntePerCodaSql (PRELEVATO a zero,
            // lavorazione ancora aperta), e le due validazioni del confine non la fermano, perche'
            // escludono proprio lei (UdcTenutaDallAutomazione e UdcInCodaEstrazione ricevono
            // idAssegnazioneDaEscludere). A fermarla e' il motore: Navigator.BuildLuExtraction salta
            // le righe la cui assegnazione e' gia' nella coda del WCS, e i due costruttori escono
            // comunque su IsLuInExtraction finche' la missione e' viva. Nessun doppio prelievo - e
            // li' non era stato confermato niente, quindi riservirla sarebbe legittimo.
            //
            // RESTA SCOPERTA UNA FINESTRA, che PRELEVATO = 1 copriva e adesso non copre piu': la
            // conferma scrive in DISTINTA_NOTE ma ConsumaAssegnazioneConfermataAsync non riesce a
            // cancellare l'assegnazione (di la' non solleva: registra e risponde «fatto»), e il GUID
            // e' vuoto, perche' GUID_ASSEGNAZIONE_UDC e' annullabile e GuidAssignmentLu resta
            // Guid.Empty, quindi ReportMissionServedAssignment esce muto. Li' l'assegnazione
            // sopravvive con la quantita' gia' confermata e PRELEVATO = 0, la riga di coda invece se
            // n'e' andata, e appena la missione esce dalle attive niente impedisce piu' di
            // riaccodare quella UDC per una nota gia' prelevata: la seconda conferma conterebbe due
            // volte la quantita'. Non riparato qui: voce di registro per il compito 17.
            //
            // Dal compito 19 questa chiamata puo' sollevare: prima l'errore della conferma lo
            // inghiottiva DbInfo (una riga nel registro, e si proseguiva). Se il gestionale non
            // risponde, il catch qui sotto registra e il giro finisce senza cancellare la riga di
            // coda del WCS - la cancellazione e' la chiamata dopo. La riga resta in coda e la
            // conferma non viene ritentata: e' quello che si guadagna a non perderla in silenzio.
            //
            // «Non viene ritentata» e' vero, e adesso lo dice anche il contratto (voce m6 della
            // revisione dell'ondata 1: IWarehouse.ConfirmServedAssignmentsAsync diceva l'opposto).
            // Il finally di questo metodo azzera ExtractionQueueId, quindi questa missione non
            // ripassa di qui; e ProcessAssignments, subito dopo nello stesso giro di
            // ProcessExtractionQueue, segnala «assegnazione servita», la cui conseguenza dal lato
            // del gestionale e' la cancellazione dell'assegnazione intera per GUID. L'idempotenza
            // di la' resta il contratto giusto - il confine non deve dipendere da quante volte lo
            // si chiama - ma non e' questa chiamata a metterla alla prova.
            await DbInfo.ConfirmServedAssignmentsByExtractionId(ExtractionQueueId);

            if (LuId > 0)
                Navigator.WriteLog(
                    $"DEBUG INFO: Clearing automatic UDC extractions from PathMission->Position UDC: {LuCode} - {LuId} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                    LogEntryType.Information);

            await DbInfo.DeleteExtractionItemQueueById(ExtractionQueueId);
        }
        catch (Exception ex)
        {
            LogError("Failed to process extraction items", ex);
        }
        finally
        {
            if (outcomeDecisionPersisted) ExtractionQueueId = 0;
        }
    }

    private async Task ProcessExtractionQueue()
    {
        if (!ShouldProcessQueue()) return;

        await TryCompleteExtractionQueueWork();
    }

    /// <summary>
    /// Prima che l'UDC lasci fisicamente l'impianto, completa l'handoff locale gia' dovuto senza
    /// rivalutare la topologia della posizione corrente. La topologia decide quando iniziare il
    /// lavoro; non puo' impedire il retry dopo che la missione si e' spostata.
    /// </summary>
    internal async Task<bool> TryPersistExtractionOutcomeBeforeUnload()
    {
        if (ExtractionQueueId <= 0) return true;

        return await TryCompleteExtractionQueueWork();
    }

    private async Task<bool> TryCompleteExtractionQueueWork()
    {
        if (!await Navigator.SyncExtraction.WaitAsync(Navigator.SyncExtractionTimeout))
        {
            Navigator.WriteLog($"Timeout acquiring extraction lock in ProcessExtractionQueue for mission {MissionId}", Log.Interface.LogEntryType.Warning);
            return false;
        }

        try
        {
            await ProcessExtractionItems();
            // Un ID ancora presente significa che l'handoff locale non e' riuscito. Non consumare
            // l'assegnazione: lo scarico conserva la missione per un tentativo successivo.
            if (ExtractionQueueId > 0) return false;
            // Compito 13: qui c'era ProcessNotes, che leggeva il residuo della nota e la chiudeva.
            // La chiusura e' una conseguenza del fatto che ProcessAssignments ha appena segnalato
            // («l'assegnazione e' stata servita»), e adesso la trae il gestionale, che la
            // lavorazione se la ricava dall'assegnazione senza che il motore gliela debba dire.
            await ProcessAssignments();
            return true;
        }
        finally
        {
            Navigator.SyncExtraction.Release();
        }
    }

    private void ProcessLoadingAndBuffer()
    {
        if (SourceItem == PositionItem)
            MissionStarted(PositionItem, this);
        else if (DestinationItem == PositionItem)
            MissionCompleted(PositionItem, this);
    }

    private async Task ProcessMissionType()
    {
        if (DestinationItemId != PositionItemId) return;

        switch (MissionType)
        {
            case MissionTypes.PickUpAndStoreAgain:
                // Senza un magazzino verso cui convertirla, la missione arrivata la tratta il giro delle
                // rotte (RedoInTheRouteLoopAsync la lascia al suo corso normale); gli altri esiti che non
                // convertono la rimettono in coda da se'.
                if (await HandlePickUpAndStoreAgainMission() == ArrivalOutcome.NoStore)
                    BackToTheRouteLoop();
                break;
        }
    }

    private async Task ProcessStore()
    {
        if (PositionItem?.DeviceType != DeviceTypes.Storage) return;
        await AllocateOnStorage(PositionItem, this);
    }

    private async Task<StorageLuResultTypes> SetDataForCheckExistsAvailableCell(StorageReservation reservation)
    {
        var itemDestination = await DbInfo.GetItemId(reservation.DoorCode);

        if (itemDestination == Guid.Empty)
            return StorageLuResultTypes.NoItemForRequiredLinkedDoor;

        Status = MissionStatusTypes.Run;

        return CheckRouteToDestination(itemDestination) ? StorageLuResultTypes.RouteBuild : StorageLuResultTypes.NoItemForRequiredLinkedDoor;
    }

    private async Task<StorageLuResultTypes> SetDataForStoreInAnyAvailableCellDifferentStoreLocation(
        StorageReservation reservation)
    {
        // La ricerca torna adesso dal gestionale, e l'operatore puo' aver scelto la destinazione
        // mentre aspettava: ogni scrittura guarda il segno nel momento in cui scrive, e con il
        // segno non si scrive niente (UnlessChosenByOperator). La porta e' quella della cella che la
        // ricerca ha prenotato con l'identificativo della missione: e' una cella sua
        // (StorageCellReserved), e il deposito chiedera' se lo e' ancora.
        if (!UnlessChosenByOperator(() =>
            {
                StorageDoorCode = reservation.DoorCode;
                StorageCellReserved = true;
            }))
            return StorageLuResultTypes.ChosenByOperator;

        var storageAsi = await GetAsiCellInfoAsync(reservation.DoorCode);
        var itemDestination = await DbInfo.GetItemId(reservation.DoorCode);

        // L'indirizzo di campo e la destinazione in un colpo solo, dopo le due letture: l'indirizzo
        // arriva dal gestionale, e la scelta puo' cadere anche li'.
        if (!UnlessChosenByOperator(() =>
            {
                DestinationAsi = storageAsi?.Asi;
                DestinationSubitem = storageAsi?.Subitem;

                // Il gestionale risolve gia' la scelta fra partizione calcolata e "0000" (compito 12,
                // ex CelleMagazzino.CercaDestinazioneUdc): reservation.Partition e' gia' il valore
                // finale, non serve piu' il ternario su Partizionata che leggeva la coppia di campi qui.
                DestinationPartition = reservation.Partition;

                StorageDepth = reservation.StorageDepth;

                if (itemDestination != Guid.Empty)
                {
                    Status = MissionStatusTypes.Run;
                    DestinationItemId = itemDestination;
                }
            }))
            return StorageLuResultTypes.ChosenByOperator;

        if (itemDestination == Guid.Empty)
            return StorageLuResultTypes.NoItemForRequiredLinkedDoor;

        // Un altro ha cambiato la rotta, la destinazione o la posizione mentre la si calcolava, o la
        // missione aspetta il rigetto dal campo rimandato: e' gia' in coda al giro delle rotte, e la
        // cella la restituisce chi chiama (FindRouteAllocateLuCore). Non e' «nessuna rotta», che chi
        // chiama legge come «nessuna cella» e manda allo scarto.
        if (TheRouteNotWritten(await BuildTheRouteToDestinationAsync()) is { } notWritten)
            return notWritten;

        return MissionSteps.Count > 0
            ? StorageLuResultTypes.RouteBuild
            : StorageLuResultTypes.NoItemForRequiredLinkedDoor;
    }

    /// <summary>
    /// L'esito della ricerca quando la rotta verso la cella non si e' scritta (<see cref="BuildTheRouteToDestinationAsync"/>):
    /// presa da un altro (<see cref="StorageLuResultTypes.RouteTakenOver"/>), o in attesa del rigetto dal
    /// campo rimandato (<see cref="StorageLuResultTypes.AwaitingRejection"/>). <c>null</c> in ogni altro caso.
    /// </summary>
    private static StorageLuResultTypes? TheRouteNotWritten(RouteWriteOutcome? outcome) => outcome switch
    {
        RouteWriteOutcome.Lost => StorageLuResultTypes.RouteTakenOver,
        RouteWriteOutcome.AwaitingRejection => StorageLuResultTypes.AwaitingRejection,
        _ => null
    };

    /// <param name="depositor">
    /// Chi fa il deposito; <c>null</c> per la posizione (<see cref="GetStoreToWarehouse"/>). Dato, la
    /// porta proposta si guarda tutta prima di scrivere - il suo indirizzo, un magazzino di chi
    /// deposita, la rotta dalla posizione - e senza la rotta la cella non e' trovata; una porta che
    /// non va non tocca la missione. Per la posizione l'ordine e l'esito sono quelli di sempre: la
    /// porta si scrive per prima, e la rotta si costruisce anche senza step.
    /// </param>
    private async Task<StorageLuResultTypes> SetDataForStoreInLinkedCell(StorageReservation reservation,
        AutomationDevice? depositor)
    {
        if (PositionItem == null)
            throw new ArgumentNullException(nameof(PositionItem), CommandMessages.strPositionItem_cannot_be_null);

        // Il magazzino della porta e' uno di quelli di cui chi deposita e' il ricollocatore: lui, o il
        // suo gruppo se ne fa parte.
        static bool OfTheRelocator(AutomationDevice? storage, AutomationDevice anchor) =>
            storage != null && storage.RelocateItemId == (anchor.BelongedGroup?.ItemGuid ?? anchor.ItemGuid);

        // Vedi le note sui due segni in SetDataForStoreInAnyAvailableCellDifferentStoreLocation qui
        // sopra. Ancorata a chi deposita, la porta si scrive con il resto, dopo averla guardata.
        if (depositor == null && !UnlessChosenByOperator(() =>
            {
                StorageDoorCode = reservation.DoorCode;
                StorageCellReserved = true;
            }))
            return StorageLuResultTypes.ChosenByOperator;

        var storageAsi = await GetAsiCellInfoAsync(reservation.DoorCode);
        var itemDestination = await DbInfo.GetItemId(reservation.DoorCode);
        var itemStorage = PathDevices.Instance[itemDestination];

        // Ancorata a chi deposita, la ricerca puo' partire da a monte (la cella persa di una missione
        // della configurazione, Api.MissionGateway.AnotherStorageOrTheRejectionAsync): come nel flusso
        // del magazzino (Api.MissionGateway.ReserveACellAndCommitAsync), la porta proposta si guarda
        // prima di scrivere niente - mappata a un magazzino di chi deposita, e con la rotta dalla
        // posizione con il gateway della missione. Una porta che non va lascia la missione com'era, e
        // la cella presa la restituisce chi ha chiesto. Le letture qui sopra (l'indirizzo dal
        // gestionale) vengono prima per lo stesso motivo: un guasto non lascia una porta scritta.
        if (depositor != null &&
            (!OfTheRelocator(itemStorage, depositor) || ProbeRouteSteps(itemDestination).Count == 0))
            return StorageLuResultTypes.NoItemForRequiredLinkedDoor;

        if (!UnlessChosenByOperator(() =>
            {
                if (depositor != null)
                {
                    StorageDoorCode = reservation.DoorCode;
                    StorageCellReserved = true;
                }

                DestinationAsi = storageAsi?.Asi;
                DestinationSubitem = storageAsi?.Subitem;

                // Vedi la stessa nota in SetDataForStoreInAnyAvailableCellDifferentStoreLocation qui sopra.
                DestinationPartition = reservation.Partition;

                StorageDepth = reservation.StorageDepth;

                if (itemDestination != Guid.Empty)
                    DestinationItemId = itemDestination;
            }))
            return StorageLuResultTypes.ChosenByOperator;

        if (itemDestination == Guid.Empty)
        {
            Navigator.WriteLog("Error On FindRouteAllocateLU No Guid Item on Destination", LogEntryType.Warning);
            return StorageLuResultTypes.NoItemForRequiredLinkedDoor;
        }

        if (!OfTheRelocator(itemStorage, depositor ?? PositionItem))
            return StorageLuResultTypes.NoItemForRequiredLinkedDoor;

        Status = MissionStatusTypes.Run;

        // Vedi la stessa nota in SetDataForStoreInAnyAvailableCellDifferentStoreLocation qui sopra. Con
        // chi deposita, «nessuna rotta» farebbe restituire la cella e rimettere la missione com'era,
        // sopra quello che ha scritto chi ha vinto.
        if (TheRouteNotWritten(await BuildTheRouteToDestinationAsync()) is { } notWritten)
            return notWritten;

        // Ancorata a chi deposita, la rotta fino alla porta deve esserci davvero anche dopo averla
        // sondata, come nella ricerca delle celle raggiungibili, invece di rispondere «trovata» anche
        // senza step: fra la sonda e la costruzione puo' essere cambiata (il gateway della missione,
        // un collegamento), e la missione scritta la rimette a posto chi ha chiesto.
        return depositor == null || MissionSteps.Count > 0
            ? StorageLuResultTypes.RouteBuild
            : StorageLuResultTypes.NoItemForRequiredLinkedDoor;
    }

    /// <param name="depositor">Chi fa il deposito; <c>null</c> per la posizione (<see cref="GetStoreToWarehouse"/>).</param>
    private async Task<ReserveStorageRequest> SetParamsForStoreInLinkedCell(ReserveStorageRequest request,
        AutomationDevice? depositor)
    {
        if (PositionItem == null)
            throw new ArgumentNullException(nameof(PositionItem), CommandMessages.strPositionItem_cannot_be_null);

        var cellToExclude = await DbInfo.GetCellId(PickUpDoorCode);
        var anchor = depositor ?? PositionItem;

        return request with
        {
            ReachableDeviceIds = [.. request.ReachableDeviceIds, anchor.BelongedGroup?.ItemGuid ?? anchor.ItemGuid],
            ExcludedCellIds = [cellToExclude],
            CellSearchMode = StorageCellSearchMode.ConnectedAutomaticOperator,
            VerifyPosition = false,
            AssignPosition = true
        };
    }

    private ReserveStorageRequest SetParamsForStoreInReachableCell(ReserveStorageRequest request)
    {
        var item = PathDevices.Instance[PositionItemId];

        IEnumerable<Guid> reachable = [];

        if (item != null && DestinationItem is { DeviceType: DeviceTypes.Storage } &&
            DestinationItem.RelocateItemId != Guid.Empty)
            reachable = item.ReachableStoreOperators
                .Where(x => x.Status == DeviceStatusTypes.Ready && x.ItemGuid != DestinationItem.RelocateItemId)
                .Select(x => x.ItemGuid);
        else if (item != null)
            reachable = item.ReachableStoreOperators
                .Where(x => x.Status == DeviceStatusTypes.Ready).Select(x => x.ItemGuid);

        return request with
        {
            ReachableDeviceIds = [.. request.ReachableDeviceIds, .. reachable],
            ExcludedCellIds = [],
            CellSearchMode = StorageCellSearchMode.ConnectedAutomaticOperator,
            VerifyPosition = false,
            AssignPosition = true
        };
    }

    private void SetProperty<T>(ref T field, T value, bool updateDb,
        [CallerMemberName] string? propertyName = null)
    {
        // Confronto, assegnazione e riga su database sotto lo stesso lock: chi arriva dopo trova
        // gia' in memoria il valore di chi e' arrivato prima, e lo porta nella propria riga. Il
        // lock e' per missione, quindi due missioni diverse non si aspettano. L'evento resta fuori:
        // un gestore non deve girare con il lock preso. Voce D81.
        lock (_scritturaSuDb)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            if (updateDb) ORM.PathMission.Update(this);
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private async Task<ReserveStorageRequest> SetSearchCellParameter(StoringSelectionTypes selectionType, ReserveStorageRequest request,
        AutomationDevice? depositor) =>
        selectionType switch
        {
            StoringSelectionTypes.StoreInAnyAvailableCellDifferentStoreLocation =>
                SetParamsForStoreInAnyAvailableCellDifferentStoreLocation(request),
            StoringSelectionTypes.StoreInAnyAvailableCell => SetParamsForStoreInAnyAvailableCell(request),
            StoringSelectionTypes.CheckExistsAvailableCell => SetParamsForCheckExistsAvailableCell(request),
            StoringSelectionTypes.StoreInLinkedCell => await SetParamsForStoreInLinkedCell(request, depositor),
            StoringSelectionTypes.StoreInReachableCell => SetParamsForStoreInReachableCell(request),
            _ => throw new ArgumentOutOfRangeException(nameof(selectionType), selectionType, null)
        };

    private bool ShouldProcessQueue()
    {
        return PositionItem != null &&
               Navigator.NavStatus != NavigatorStatusTypes.Recover &&
               PositionItem.DeviceType != DeviceTypes.Storage &&
               PositionItem.IsLinkedToStorage;
    }

    private void StartUpdate()
    {
        lock (_scritturaSuDb)
        {
            ORM.PathMission.Update(this);
        }
    }

    /// <summary>
    /// Helper sincrono per ottenere door code. Evita deadlock da property setter.
    /// NOTA: Usare Task.Run con ConfigureAwait per evitare blocking su SynchronizationContext.
    /// </summary>
    private string? GetDoorCodeSync(Guid? itemId)
    {
        try
        {
            return Task.Run(async () => await DbInfo.GetDoorCode(itemId).ConfigureAwait(false)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Navigator.WriteLog($"Error getting door code for item {itemId}: {ex.Message}", LogEntryType.Warning);
            return null;
        }
    }

    #endregion Private Methods

}
