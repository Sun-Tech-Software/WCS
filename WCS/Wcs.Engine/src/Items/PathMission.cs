using Helper.Converters;
using Wcs.Connection.Interface;
using Wcs.Engine.Collections;
using Wcs.Common;
using Wcs.Engine.ORM;
using Log.Interface;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Wcs.Wms.Contracts;
using static Wcs.Common.Enums;
using CommandToDo = Wcs.Engine.ORM.CommandToDo;

namespace Wcs.Engine.Items;

public partial class PathMission
{

    #region Private Fields

    /// <summary>
    /// Serializza scrittura in memoria e scrittura su database per QUESTA missione. Ogni setter
    /// riscrive l'intera riga di MISSION_LU con i valori del momento: due thread che scrivono due
    /// campi insieme mandavano due righe intere, e quella eseguita per ultima poteva essere stata
    /// letta per prima, senza il campo dell'altro. Con il lock la riga scritta per ultima porta
    /// sempre tutti i valori scritti prima di lei. Voce D81.
    /// </summary>
    private readonly object _scritturaSuDb = new();

    /// <summary>
    /// Indica se il thread corrente sta componendo una scrittura persistente della missione.
    /// L'ORM usa questa informazione per rifiutare istantanee costruite fuori dalla sezione
    /// critica per missione, che potrebbero arrivare sul database in ordine incoerente.
    /// </summary>
    internal bool IsDatabaseWriteSynchronized => Monitor.IsEntered(_scritturaSuDb);

    /// <summary>
    /// Il lucchetto della scelta della destinazione (compito 2 del piano delle missioni): sotto di
    /// lui l'operatore scrive la destinazione e alza il segno (<see cref="SetOperatorDestination"/>),
    /// chi scrive la destinazione dalla configurazione guarda il segno e scrive
    /// (<see cref="UnlessChosenByOperator"/>), il setter della destinazione abbassa il segno e il
    /// setter della posizione lo abbassa all'arrivo. Serve perche' una ricerca della cella legge il segno,
    /// aspetta il gestionale fino a 30 s e scrive dopo: il segno va riguardato nel momento della
    /// scrittura, e fra lo sguardo e la scrittura l'operatore non deve poter passare. Non e'
    /// <see cref="_scritturaSuDb"/>: sotto questo gira il setter intero della destinazione, con la
    /// rimozione degli step e i suoi annunci, e la voce D81 vuole i gestori fuori da quello. Dentro
    /// niente gestionale e niente <c>await</c>: al piu' la lettura sincrona della porta di un polmone
    /// dal database del WCS, che il setter della destinazione fa gia' da se'. Il lucchetto e' per
    /// missione, e se lo contendono solo l'operatore e chi scrive dalla configurazione.
    /// </summary>
    private readonly object _destinationChoice = new();

    /// <summary>
    /// Vero mentre <see cref="SetOperatorDestination"/> scrive la destinazione, sotto
    /// <see cref="_destinationChoice"/>: il setter della destinazione abbassa il segno vecchio senza la
    /// riga di avviso della scelta caduta, perche' a cambiarla e' l'operatore stesso.
    /// </summary>
    private bool _operatorIsChoosing;

    /// <summary>
    /// Il lucchetto della rotta (voce 41, disegno C): sotto di lui gli step della missione nella
    /// collezione globale e la versione della rotta (<see cref="_routeVersion"/>) cambiano insieme, e
    /// nessun'altra scrittura della rotta che lo prende passa in mezzo. Lo prendono la primitiva di
    /// scrittura (<see cref="TryWriteRoute"/>, e con lei il setter di <see cref="MissionSteps"/>,
    /// <see cref="CommitRoute"/>, <see cref="MoveTo"/>, il rigetto e la rimozione della strategia
    /// <c>Standard</c>), la rimozione (<see cref="RemoveMissionSteps"/>, e lo sguardo con la rimozione di
    /// <see cref="RemoveTheRouteUnlessAStepIsInFlight"/>), l'uscita dal motore con il suo sguardo
    /// (<see cref="LeaveUnlessAStepIsInFlight"/>, che sotto di lui toglie la missione anche dall'indice
    /// delle posizioni, come gia' il setter della posizione), il gettone di chi avvia uno step
    /// (<see cref="TryTakeTheStepToken"/>) e i setter di <see cref="DestinationItemId"/> e di
    /// <see cref="PositionItemId"/>. Si tiene per il tempo di scrivere: niente gestionale e niente
    /// <c>await</c> sotto di lui, il calcolo della rotta sta fuori. E' il primo dei lucchetti della
    /// missione: chi lo tiene puo' prendere <see cref="_destinationChoice"/>, <see cref="_storageWork"/>
    /// e <see cref="_scritturaSuDb"/>, chi tiene uno di quelli non prende lui. Per questo lo prendono
    /// prima di <see cref="_destinationChoice"/> anche <see cref="UnlessChosenByOperator"/> e
    /// <see cref="SetOperatorDestination"/>, che sotto la scelta scrivono la destinazione: preso dopo,
    /// un commit che tiene la rotta e aspetta la scelta e una scrittura che tiene la scelta e aspetta
    /// la rotta si fermerebbero tutti e due per sempre. Fra due missioni, solo il commit del cambio
    /// verso un dispositivo prende, con questo, quello delle copie ferme della porta
    /// (<c>MissionGateway.CopiesOfTheDoor.Leave</c>): dalla missione alle copie, mai al contrario.
    /// Dal compito 6 della voce 41 anche i dispositivi: il gettone di chi avvia uno step scrive, sotto
    /// questo lucchetto, il <c>RunningStepId</c> e lo stato del dispositivo (il deposito
    /// <c>Unloading</c>, il prelievo <c>Loading</c>, il gruppo il suo gettone), e i loro setter prendono
    /// il lucchetto dello stato del dispositivo e fanno la parte sincrona dei loro annunci: dalla
    /// missione al dispositivo, mai al contrario. Sotto questo lucchetto gira anche la lettura sincrona
    /// della porta di un polmone dal database del WCS che il setter della destinazione fa da se'.
    /// </summary>
    private readonly object _route = new();

    /// <summary>
    /// La versione della rotta (voce 41, disegno C): sale, sotto <see cref="_route"/>, a ogni scrittura
    /// degli step con la primitiva (<see cref="TryWriteRoute"/>), a ogni rimozione che toglie degli
    /// step (<see cref="RemoveMissionSteps"/>), a ogni cambio della destinazione, che li toglie, a ogni
    /// cambio della posizione (setter di <see cref="PositionItemId"/>), da cui la rotta parte, e a ogni
    /// gettone di uno step che parte (<see cref="TryTakeTheStepToken"/>). Chi calcola una rotta la legge
    /// prima di cominciare (<see cref="RouteVersion"/>) e la scrive solo se e' ancora quella. Non sta
    /// nel database: dopo un riavvio riparte da zero, con la rotta che si rifa'.
    /// </summary>
    /// <remarks>
    /// Non la fanno salire tutti quelli che il disegno C elenca: il gateway, la porta
    /// (<see cref="StorageDoorCode"/>) e i campi della cella scritti fuori dalla primitiva, la rimozione
    /// per gruppo (<c>RemoveStepsByGroupedMovementId</c>), quella dello step eseguito
    /// (<c>FinalizeStepProcessing</c>) e il <c>RemoveAll</c> del gruppo dopo un errore. Nessuno di questi
    /// e' una delle sei corse della ricognizione (revisione finale della voce 41). Il distributore
    /// circolare senza la scelta dell'operatore, che toglieva e rimetteva uno step a mano, scrive con la
    /// primitiva dal giro finale delle correzioni.
    /// </remarks>
    private long _routeVersion;

    /// <summary>
    /// Il rigetto dal campo rimandato perche' uno step della missione era in volo, con il suo motivo
    /// (<see cref="RejectionTypes.NoLuDataFromPlc"/> o <see cref="RejectionTypes.RejectedFromAttendant"/>):
    /// lo rifa' il giro delle rotte quando lo step e' finito (<see cref="RedoInTheRouteLoopAsync"/>,
    /// risposta 1 del proprietario dell'11/10), con lo stesso motivo. Finche' c'e', una rotta calcolata con
    /// il confronto della versione non si scrive (<see cref="TryWriteRoute"/> risponde
    /// <see cref="RouteWriteOutcome.AwaitingRejection"/>); chi scrive senza confronto (lo spostamento, il
    /// rigetto) e chi toglie soltanto passano. <c>null</c> per nessuno. Sotto <see cref="_route"/>. Non
    /// sta nel database: un riavvio lo perde.
    /// </summary>
    private RejectionTypes? _fieldRejectionPending;

    /// <summary>
    /// Il punto di scarto di un rigetto che ha perso la costruzione della sua rotta contro uno
    /// spostamento (o un altro scrittore che non ha cambiato la destinazione): la missione e' gia' un
    /// rigetto verso di lui, e la rotta la rifa' il giro delle rotte dalla posizione nuova
    /// (<see cref="RedoInTheRouteLoopAsync"/>, risposta 5 del proprietario dell'11/10).
    /// <see cref="Guid.Empty"/> per nessuno. Sotto <see cref="_route"/>.
    /// </summary>
    private Guid _rejectionToComplete;

    /// <summary>
    /// Il lucchetto del lavoro sul magazzino e sugli step di questa missione (compito 3 delle missioni): protegge
    /// soltanto i cinque campi qui sotto, il conto delle ricerche della cella in corso, il conto degli
    /// avvii di uno step in corso, il cambio di destinazione dell'operatore in corso, la missione
    /// chiusa e la conferma della cella per il deposito. Si tiene per il tempo di leggerli e scriverli:
    /// niente gestionale e niente <c>await</c> sotto di lui. Vedi <see cref="TryBeginOperatorChange"/>,
    /// <see cref="TryBeginStepStart"/>, <see cref="Withdraw"/> e <see cref="TakeStorageCellConfirmation"/>.
    /// </summary>
    private readonly object _storageWork = new();

    /// <summary>Quante ricerche della cella sono dentro <c>FindRouteAllocateLu</c> per questa missione.</summary>
    private int _cellSearches;

    /// <summary>
    /// Quanti avvii di uno step di questa missione sono in corso: dalla presa del primo gettone (il
    /// posto sulla destinazione) a quando il gettone dell'esecuzione e' scritto, o l'avvio e' rinunciato.
    /// Contano allo stesso modo i lavori di un gruppo sulla cella e sulla rotta della missione.
    /// </summary>
    private int _stepStarts;

    /// <summary>Il cambio di destinazione dell'operatore in corso, o <c>null</c>: si completa quando finisce.</summary>
    private TaskCompletionSource? _operatorChange;

    /// <summary>La missione e' chiusa e non torna (<see cref="Withdraw"/>): alzato, non si abbassa piu'.</summary>
    private bool _withdrawn;

    /// <summary>
    /// The mission has left the active missions (<see cref="MarkRemovedFromTheEngine"/>): its row is
    /// being deleted, and no write of this object may bring it back. Written and read under
    /// <see cref="_scritturaSuDb"/>.
    /// </summary>
    private bool _removedFromTheEngine;

    /// <summary>
    /// Il dispositivo su cui la missione era quando il navigatore ha visto, chiedendo al gestionale,
    /// che la sua cella di deposito e' ancora sua (o gliene ha appena prenotata un'altra); <c>null</c>
    /// se non c'e' una conferma da usare. Ogni giro di deposito la prende per se'
    /// (<see cref="TakeStorageCellConfirmation"/>), e quello di una missione dell'operatore, o con una
    /// cella prenotata per lei, parte solo con lei (<see cref="MayStartTheDeposit"/>). Cade anche
    /// quando la cella cambia: al commit di una rotta nuova (<see cref="CommitRoute"/>) e quando la
    /// cella si lascia (<see cref="DropStorageCell"/>), e quando il dispositivo su cui la missione e' a
    /// bordo va in errore o ne esce (<see cref="ForgetStorageCellConfirmation"/>).
    /// Non sta nel database: dopo un riavvio la conferma si rifa'.
    /// </summary>
    private Guid? _cellConfirmedOn;

    /// <summary>
    /// La porta che la conferma di <see cref="_cellConfirmedOn"/> ha guardato: la conferma vale solo
    /// finche' la missione ha ancora quella porta (<see cref="TakeStorageCellConfirmation"/>).
    /// </summary>
    private string? _cellConfirmedDoor;

    /// <summary>
    /// La missione ha lasciato la porta che aveva copiato da un'altra missione del gruppo, che ha perso
    /// la cella (<see cref="DropTheCopiedDoor"/>): la rifa' la ricerca per tipo del gruppo, in qualunque
    /// modalita' di deposito sia il gruppo (<see cref="LeftACopiedDoor"/>). Sta nel database del WCS
    /// (<see cref="LeftACopiedDoorMark"/>).
    /// </summary>
    private bool _leftACopiedDoor;

    private bool _activeFilter;

    private decimal _bufferCellId;

    /// <summary>
    /// La memoria per missione delle celle gia' decodificate (compito 5): un id gia' chiesto una
    /// volta al gestionale non ci torna una seconda, finche' la missione resta aperta. Si svuota in
    /// <see cref="Collections.ActiveMissions.Remove"/>, quando la missione chiude - vedi
    /// <see cref="ClearWarehouseCaches"/>. Non serializzata: e' cache, non stato della missione.
    /// </summary>
    private readonly Dictionary<decimal, CellDescription> _cellDescriptions = [];

    /// <summary>
    /// La memoria per missione delle mappe di partizione gia' chieste (compito 8), stesso schema di
    /// <see cref="_cellDescriptions"/>: si svuota insieme a quella in <see cref="ClearWarehouseCaches"/>.
    /// </summary>
    private readonly Dictionary<decimal, CellPartitionMap> _cellPartitionMaps = [];

    private DateTime _date = DateTime.Now;

    private string? _destinationAsi = string.Empty;

    private Guid? _destinationItemId = Guid.Empty;

    private string? _destinationPartition = string.Empty;

    private string? _destinationSubitem = string.Empty;

    private decimal _extractionQueueId;

    private Guid _gateway = Guid.Empty;

    private Guid _groupedMovementId = Guid.Empty;

    private Guid _guidAssignmentLu;

    private int _handlingMode;

    private decimal _idLuType;

    private string? _luCode = string.Empty;

    private decimal _luId;

    private decimal _luNoteAssignmentId;

    private decimal _servedAssignmentId;

    private Guid _servedAssignmentGuid;

    private decimal _servedExtractionRowId;

    private Guid _missionId = Guid.Empty;

    private MissionTypes _missionType = MissionTypes.None;

    private string? _movingDescription = string.Empty;

    private decimal _noteProcessingId;

    private OperationTypes _operationType = OperationTypes.None;

    private bool _operatorDestination;

    private Api.RelocatorArrival _relocatorArrival;

    private string? _pickUpDoorCode = string.Empty;

    private Guid _positionItemId = Guid.Empty;

    private bool _reallocatedDestination;

    private Guid _relocateItemId = Guid.Empty;

    private string? _sourceAsi = string.Empty;

    private Guid _sourceItemId = Guid.Empty;

    private string? _sourcePartition = string.Empty;

    private string? _sourceSubitem = string.Empty;

    private MissionStatusTypes _status = MissionStatusTypes.Stop;

    private string? _statusDescription = string.Empty;

    private int _storageDepth = -1;

    private int? _storageDepthAfterPickUp;

    private string? _storageDoorCode = string.Empty;

    private bool _storageCellLost;

    private bool _storageCellReserved;

    private bool _changeOfCellReportsSent;

    private decimal _tare;

    #endregion Private Fields

    #region Public Constructors

    public PathMission()
    {
        MissionId = Guid.NewGuid();
    }

    public PathMission(Guid missionId)
    {
        MissionId = missionId;
    }

    #endregion Public Constructors

    #region Public Events

    public event PropertyChangedEventHandler? PropertyChanged;

    #endregion Public Events

    #region Public Enums

    public enum RejectionTypes : byte
    {
        [Description("There are no cells available for storage")]
        NoStoreCellAvailable = 0,

        [Description("The PLC did not return data for the PathMission")]
        NoLuDataFromPlc = 1,

        [Description("the Navigator's default rejection item")]
        DefaultNavigatorRejectionItem = 2,

        [Description("Operator rejection request")]
        RejectedFromAttendant = 3
    }

    public enum StoringSelectionTypes : byte
    {
        [Description("Check if there is an available Cell and build the depot road")]
        StoreInAnyAvailableCell = 0,

        [Description("Only checks if there is an available Cell")]
        CheckExistsAvailableCell = 1,

        [Description(
            "Checks whether there is an available Cell connected to the element containing the PathMission and builds the storage road")]
        StoreInLinkedCell = 2,

        [Description(
            "Checks whether there is an available Cell reachable from the element containing the PathMission and builds the storage road")]
        StoreInReachableCell = 3,

        [Description("Check if there is a Cell available for free storage element and build the storage road")]
        StoreInAnyAvailableCellDifferentStoreLocation = 4
    }

    #endregion Public Enums

    #region Private Enums

    private enum StorageLuResultTypes : byte
    {
        [Description("The automation cell id has not been set for the AutomationDevice requesting the deposit")]
        NoAutomaticCellSetForItem = 0,

        [Description("No Warehouse has been set for the cell set on the AutomationDevice requesting the warehouse")]
        NoWarehouseSetForAutomaticCell = 1,

        [Description("No storage cells available")]
        NoCellAvailable = 2,

        [Description("No Automation Device connected to the storage cell door")]
        NoItemForRequiredLinkedDoor = 3,

        [Description("The storage location is not reachable from the current AutomationDevice")]
        ItemLinkedToWarehouseDoorNotAchievable = 4,

        [Description("No PathMissions loaded")]
        NoLuLoaded = 5,

        [Description("No Reference contained in the UDC")]
        LuWithNoReference = 6,

        [Description("The destination was chosen by the operator while the warehouse was being asked")]
        ChosenByOperator = 7,

        [Description("The cell was reserved, and the route was changed by someone else while it was being built: the route loop builds it again")]
        RouteTakenOver = 8,

        [Description("The cell was reserved, and the route was not written because the mission waits for the postponed field rejection: the route loop redoes the rejection")]
        AwaitingRejection = 9,

        [Description("Path created")] RouteBuild = 99
    }

    #endregion Private Enums

    #region Public Properties

    /// <summary>
    ///     Returns or Sets the identifier of the Buffer cell where to bring the ActiveMissions during the extraction phase
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or Sets the identifier of the Buffer cell where to bring the ActiveMissions during the extraction phase")]
    [DisplayName("BufferCellId")]
    [Category("PathMission")]
    public decimal BufferCellId
    {
        get => _bufferCellId;
        set
        {
            _bufferCellId = value;
            StartUpdate();
        }
    }

    /// <summary>
    ///     Returns or Sets the date/time of the last PathMission operation
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the date/time of the last PathMission operation")]
    [DisplayName("DateTime")]
    [Category("MissionStep")]
    public DateTime Date
    {
        get => _date;
        set => SetProperty(ref _date, value, true);
    }

    /// <summary>
    ///     Returns or Sets the destination Asi
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the destination ASI")]
    [DisplayName("DestinationAsi")]
    [Category("PathMission")]
    public string? DestinationAsi
    {
        get => _destinationAsi;
        set => SetProperty(ref _destinationAsi, value, true);
    }

    /// <summary>
    ///     Returns or Sets the destination AutomationDeviceBase of the PathMission
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the destination AutomationDevice of the PathMission")]
    [DisplayName("DestinationItem")]
    [Category("PathMission")]
    public AutomationDevice? DestinationItem
    {
        get => PathDevices.Instance[DestinationItemId];
        set
        {
            if (value == null) return;
            DestinationItemId = value.ItemGuid;
        }
    }

    /// <summary>
    ///     Returns or Sets the identifier of the AutomationDeviceBase destination of the PathMission
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the identifier of the AutomationDevice destination of the PathMission")]
    [DisplayName("Destination")]
    [Category("PathMission")]
    public Guid? DestinationItemId
    {
        get => _destinationItemId;
        set
        {
            lock (_route)
            {
                // Cambiare destinazione cancella la rotta -- e' giusto, la rotta non vale piu' -- ma
                // non con uno step in volo: si renderebbe libero a meta' movimento il dispositivo che
                // lo sta eseguendo. E' la seconda rete sotto BuildPathToDestination e RejectTo, per chi
                // scrive la destinazione direttamente. Decisione del 2026-09-03.
                //
                // La guardia sta PRIMA di RemoveMissionSteps(), non dopo. StepInFlight() scorre gli step
                // di questa missione, e RemoveMissionSteps() li ha gia' tolti tutti: quando la
                // destinazione cambiava davvero -- cioe' nell'unico caso in cui la rete ha un senso --
                // non c'era piu' niente da trovare e la guardia rispondeva sempre null. Peggio:
                // RemoveMissionSteps chiama SourceItem.RemoveStep, che azzera il RunningStepId della
                // sorgente, cioe' esattamente il danno che questo commento dice di voler evitare, fatto
                // una riga prima del controllo. I tre gemelli (BuildPathToDestination, RejectTo,
                // ActiveMissions.Remove) hanno sempre avuto l'ordine giusto.
                //
                // La rete e' SPOSTATA, non ristretta: vale anche in Recover, com'e' sempre valsa, ed e'
                // quello che RimozioneERottaTest.LaDestinazioneNonCambiaConUnoStepInVolo tiene fermo.
                // Cio' che resta legato a Recover e' il blocco qui sotto, che cancella la rotta e
                // ricalcola il codice porta: quello la ricarica lo fa da se'.
                //
                // CAMBIAMENTO DI COMPORTAMENTO IN CAMPO (dichiarato il 2026-09-10, revisione
                // dell'ondata 1, voce I-3). Dallo spostamento la guardia SCATTA, e prima non scattava
                // mai: la scrittura della destinazione viene scartata - una riga di avviso e un return,
                // senza SetProperty e senza la cancellazione degli step. Chi scrive DestinationItemId e
                // poi da' per buono che abbia preso si comporta da adesso diversamente, e va letto
                // prima di installare: e' la riparazione voluta, ma e' un comportamento che in campo
                // non c'e' mai stato.
                if (StepInFlight() is { } inVolo)
                {
                    Navigator.WriteLog(
                        $"Destinazione della missione {MissionId} non cambiata: lo step {inVolo.StepId} e' in volo su {inVolo.FromNodeId}",
                        LogEntryType.Warning);
                    return;
                }

                // Il confronto si fa una volta, prima di scrivere, sotto il lucchetto della rotta: ogni
                // scrittura della destinazione passa da questo setter, e nessuna arriva in mezzo. Lo
                // usano tutti i rami qui sotto, anche quelli sotto il lucchetto della scelta.
                var changed = _destinationItemId != value;

                if (Navigator.NavStatus != Enums.NavigatorStatusTypes.Recover && changed && _destinationItemId != null)
                {
                    RemoveMissionSteps();
                    var newDestinationItem = PathDevices.Instance[value];

                    if (newDestinationItem is { DeviceType: DeviceTypes.Buffer or DeviceTypes.LoadingAndBuffer })
                    {
                        // Nota: Operazione sincrona per mantenere compatibilità property setter
                        // Considera refactoring a metodo async SetDestinationItemAsync() in futuro
                        var doorCode = GetDoorCodeSync(value);

                        if (BufferCellId != newDestinationItem.LinkedCellId)
                            BufferCellId = newDestinationItem.LinkedCellId;
                        if (StorageDoorCode != doorCode)
                            StorageDoorCode = doorCode;
                    }
                    else if (newDestinationItem is { DeviceType: DeviceTypes.Storage })
                    {
                        // Verso un magazzino il codice porta e' la cella di deposito, scelta prima della
                        // destinazione: azzerarlo qui lasciava la rilocazione di una UDC (quella davanti,
                        // da spostare per liberarne una dietro) senza cella, non costruibile, e in testa
                        // alla fila di prelievo del magazzino, che si fermava. Il campo parte da
                        // Guid.Empty, quindi anche la prima assegnazione passa di qui.
                        BufferCellId = 0;
                    }
                    else
                    {
                        BufferCellId = 0;
                        StorageDoorCode = null;
                    }
                }

                // Il segno della destinazione scelta dall'operatore vale per QUESTA destinazione: chi la
                // cambia per un'altra via (un rifiuto, la ricarica che non trova piu' la porta) ne
                // sceglie un'altra, e il segno cade; il distributore circolare non la cambia piu' a
                // un'UDC con il segno (voce 40). SetOperatorDestination lo
                // rialza subito dopo, quando a cambiarla e' l'operatore. Si abbassa senza riga propria
                // sul database (la riga che manda la scrittura della destinazione, qui sotto, li porta
                // tutti e due), ma con una riga di avviso nel registro quando a cambiarla non e'
                // l'operatore: la sua scelta sparisce, e senza la riga non lo saprebbe nessuno.
                // Segno e destinazione cambiano sotto il lucchetto della scelta: SetOperatorDestination
                // scrive, controlla e alza sotto lo stesso, e una scrittura d'altri non puo' cadere fra
                // il suo controllo e il segno (resterebbe il segno su una destinazione non scelta).
                // La cella persa cade allo stesso modo: era una cella del magazzino della destinazione
                // di prima, e da una destinazione nuova il navigatore non la deve piu' rifare.
                Guid? choiceDropped = null;

                lock (_destinationChoice)
                {
                    if (_operatorDestination && changed)
                    {
                        SetProperty(ref _operatorDestination, false, false, nameof(OperatorDestination));
                        if (!_operatorIsChoosing) choiceDropped = _destinationItemId;
                    }

                    if (_storageCellLost && changed)
                        SetProperty(ref _storageCellLost, false, false, nameof(StorageCellLost));

                    // Item 54: the relocator's arrival (deposit it, or only bring it there) is for the
                    // relocator chosen, and falls with it, whoever changes the destination: the deposit
                    // search that gives it a cell, a rejection, a new change of the operator (whose
                    // commit writes its own choice after the destination).
                    if (_relocatorArrival != Api.RelocatorArrival.None && changed)
                        SetProperty(ref _relocatorArrival, Api.RelocatorArrival.None, false, nameof(RelocatorArrival));

                    SetProperty(ref _destinationItemId, value, true);
                }

                if (choiceDropped != null)
                    Navigator.WriteLog(
                        $"Missione {MissionId}: la destinazione {choiceDropped} scelta dall'operatore e' cambiata in {value} per un'altra via, e la scelta dell'operatore cade",
                        LogEntryType.Warning);

                // La rotta di una destinazione che cambia non vale piu', anche in Recover (dove il blocco
                // qui sopra non gira) e anche senza una destinazione prima: si toglie, e la versione
                // sale. La stessa destinazione riscritta invece non tocca niente, ne' la rotta ne' la
                // porta (voce 41): chi rimette la destinazione che la missione ha gia' (la scelta fra
                // due destinazioni fisse, Navigator.BuildRouteToFixedDestination) conta su questo. Prima
                // la rotta si toglieva sempre, senza passare dai dispositivi. Fuori da Recover, con una
                // destinazione prima, la rotta l'ha gia' tolta RemoveMissionSteps qui sopra (e la
                // versione e' gia' salita): qui non si toglie niente, e la versione sale una seconda
                // volta, che a chi confronta non cambia niente.
                if (changed)
                {
                    Collections.MissionSteps.Instance.RemoveAll(x => x.MissionId == MissionId);
                    Interlocked.Increment(ref _routeVersion);
                }
            }
        }
    }

    /// <summary>
    ///     Returns or Sets the destination Partition
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the destination PARTITION")]
    [DisplayName("DestinationPartition")]
    [Category("PathMission")]
    public string? DestinationPartition
    {
        get => _destinationPartition;
        set => SetProperty(ref _destinationPartition, value, true);
    }

    /// <summary>
    ///     Returns or Sets the destination Subitem
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the destination SUBITEM")]
    [DisplayName("DestinationSubitem")]
    [Category("PathMission")]
    public string? DestinationSubitem
    {
        get => _destinationSubitem;
        set => SetProperty(ref _destinationSubitem, value, true);
    }

    /// <summary>
    ///     Returns or Sets the unique identifier to the extraction queue UDC_EXTRATION_ID of the AUTOMATIC_UDC_EXTRATIONS
    ///     table
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or Sets the unique identifier to the extraction queue UDC_EXTRATION_ID of the AUTOMATIC_UDC_EXTRATIONS table")]
    [DisplayName("IdExtractionQueue")]
    [Category("PathMission")]
    public decimal ExtractionQueueId
    {
        get => _extractionQueueId;
        set => SetProperty(ref _extractionQueueId, value, true);
    }

    /// <summary>
    ///     Returns or Sets the identifier of the AutomationDeviceBase through which the PathMission must pass
    ///     <para>
    ///     Obbligo di passaggio: la ricerca rifiuta le rotte che non lo attraversano. Si azzera in tre
    ///     casi, tutti nel setter di <see cref="PositionItemId"/>: quando la posizione lo raggiunge,
    ///     quando la posizione arriva su un dispositivo il cui gruppo di appartenenza e' il gateway, e
    ///     quando la posizione diventa vuota (la missione esce dall'impianto).
    ///     </para>
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the identifier of the AutomationDevice through which the PathMission must pass")]
    [DisplayName("Gateway")]
    [Category("PathMission")]
    public Guid Gateway
    {
        get => _gateway;
        set => SetProperty(ref _gateway, value, true);
    }

    /// <summary>
    ///     Returns or Sets the AutomationDeviceBase the PathMission should pass through
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the AutomationDevice the PathMission should pass through")]
    [DisplayName("GatewayItem")]
    [Category("PathMission")]
    public AutomationDevice? GatewayItem
    {
        get => PathDevices.Instance[Gateway];
        set
        {
            if (value == null) return;
            Gateway = value.ItemGuid;
        }
    }

    /// <summary>
    ///     Returns or sets the unique identifier for group movements generated by AutomationDeviceBase of type
    ///     GroupOfAutomationDevices
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or sets the unique identifier for group movements generated by AutomationDevice of type GroupOfAutomationDevices")]
    [DisplayName("GroupedMovementId")]
    [Category("PathMission")]
    public Guid GroupedMovementId
    {
        get => _groupedMovementId;
        set => SetProperty(ref _groupedMovementId, value, true);
    }

    /// <summary>
    ///     Returns or Sets the unique identifier to the extraction queue UDC_ASSIGNMENT_GUID of the NOTE_ASSIGNMENT table
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or Sets the unique identifier to the extraction queue UDC_ASSIGNMENT_GUID of the NOTE_ASSIGNMENT table")]
    [DisplayName("GuidAssignmentLu")]
    [Category("PathMission")]
    public Guid GuidAssignmentLu
    {
        get => _guidAssignmentLu;
        set => SetProperty(ref _guidAssignmentLu, value, true);
    }

    /// <summary>
    ///     Returns or Sets the type of Handling Mode necessary for the move command relating to the type of LU
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or Sets the type of Handling Mode necessary for the move command relating to the type of LU")]
    [DisplayName("HandlingMode")]
    [Category("PathMission")]
    public int HandlingMode
    {
        get => _handlingMode;
        set => SetProperty(ref _handlingMode, value, true);
    }

    /// <summary>
    ///     Returns or Sets the unique identifier to the ID_COMMAND_TODO command queue of the COMMAND_TODO table
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or Sets the unique identifier to the ID_COMMAND_TODO command queue of the COMMAND_TODO table")]
    [DisplayName("IdCommandToDo")]
    [Category("PathMission")]
    public decimal IdCommandToDo { get; set; }

    /// <summary>
    ///     Returns or Sets the Load Unit type
    /// </summary>
    /// <returns></returns>
    /// <remarks>
    /// Compito 10: fino a qui il setter chiedeva anche <c>HandlingMode</c> al gestionale
    /// (<c>DbInfo.GetHandlingMode</c>), sincrono dentro un setter di proprieta'. Non si puo' rendere
    /// asincrono un setter, quindi la lettura e' stata tolta da qui: chi costruisce la missione la
    /// chiede una volta sola insieme a codice, tipo e tara (<see cref="IWarehouse.DescribeLoadUnitAsync"/>)
    /// e imposta <see cref="HandlingMode"/> esplicitamente. Chi imposta <see cref="IdLuType"/> senza
    /// passare da li' (un tipo scelto a mano, senza un'unita' di carico dietro) deve impostare anche
    /// <see cref="HandlingMode"/> per conto suo, se gli serve.
    /// </remarks>
    [Browsable(true)]
    [Description("Returns or Sets the Load Unit type")]
    [DisplayName("IdLuType")]
    [Category("PathMission")]
    public decimal IdLuType
    {
        get => _idLuType;
        set => SetProperty(ref _idLuType, value, true);
    }

    /// <summary>
    ///     Returns or Sets the unique identifier COD_UDC of the UDC table
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the unique identifier COD_UDC of the UDC table")]
    [DisplayName("LuCode")]
    [Category("PathMission")]
    public string? LuCode
    {
        get => _luCode;
        set => SetProperty(ref _luCode, value, true);
    }

    /// <summary>
    ///     Returns or Sets the LuId unique identifier of the UDC table
    /// </summary>
    /// <returns></returns>
    /// <remarks>
    /// Compito 10: fino a qui il setter chiedeva anche <c>IdLuType</c> e <c>Tare</c> al gestionale
    /// (<c>DbInfo.GetLuData</c>), sincrono dentro un setter di proprieta'. Non si puo' rendere
    /// asincrono un setter, quindi la lettura e' stata tolta da qui e spostata su chi costruisce la
    /// missione, che chiama <see cref="IWarehouse.DescribeLoadUnitAsync"/> una volta sola e imposta
    /// <see cref="LuId"/>, <see cref="LuCode"/>, <see cref="IdLuType"/>, <see cref="HandlingMode"/> e
    /// <see cref="Tare"/> con la stessa risposta - non piu' due chiamate (una per <c>LuId</c>, una
    /// cascata da <c>IdLuType</c>) ma una sola.
    /// </remarks>
    [Browsable(true)]
    [Description("Returns or Sets the LuId unique identifier of the UDC table")]
    [DisplayName("LuId")]
    [Category("PathMission")]
    public decimal LuId
    {
        get => _luId;
        set => SetProperty(ref _luId, value, true);
    }

    /// <summary>
    ///     Returns or Sets the unique identifier to the extraction queue NOTE_ASSIGNMENT_ID of the NOTE_ASSIGNMENT table
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or Sets the unique identifier to the extraction queue NOTE_ASSIGNMENT_ID of the NOTE_ASSIGNMENT table")]
    [DisplayName("LuNoteAssignmentId")]
    [Category("PathMission")]
    public decimal LuNoteAssignmentId
    {
        get => _luNoteAssignmentId;
        set => SetProperty(ref _luNoteAssignmentId, value, true);
    }

    /// <summary>
    /// L'assegnazione della nota (<c>ID_ASSEGNAZIONE_UDC</c>) che il prelievo di questa estrazione ha
    /// servito: la copia <see cref="ProcessExtractionItems"/> dalla riga di coda prima di staccarsene.
    /// Zero se la missione non ha servito nessuna assegnazione.
    /// </summary>
    /// <remarks>
    /// Voce 45: al prelievo la missione si stacca dalla riga di coda (<see cref="ExtractionQueueId"/>) e
    /// dal GUID (<see cref="GuidAssignmentLu"/>); se poi l'operatore manda in un magazzino l'estrazione
    /// gia' prelevata, e' da qui che il WCS sa che cosa chiedere al gestionale di riaprire. Persistito
    /// nella riga della missione del <c>wcs.db</c>, sopravvive a un riavvio.
    /// </remarks>
    [Browsable(true)]
    [Description("The note assignment (ID_ASSEGNAZIONE_UDC) the pick-up of this extraction served")]
    [DisplayName("ServedAssignmentId")]
    [Category("PathMission")]
    public decimal ServedAssignmentId
    {
        get => _servedAssignmentId;
        set => SetProperty(ref _servedAssignmentId, value, true);
    }

    /// <summary>
    /// Il GUID dell'assegnazione (<c>GUID_ASSEGNAZIONE_UDC</c>) che il prelievo di questa estrazione ha
    /// servito: lo copia <see cref="ProcessAssignments"/> prima di azzerare
    /// <see cref="GuidAssignmentLu"/>. Vuoto se la missione non ne ha servito nessuno. Vedi
    /// <see cref="ServedAssignmentId"/>.
    /// </summary>
    [Browsable(true)]
    [Description("The assignment GUID (GUID_ASSEGNAZIONE_UDC) the pick-up of this extraction served")]
    [DisplayName("ServedAssignmentGuid")]
    [Category("PathMission")]
    public Guid ServedAssignmentGuid
    {
        get => _servedAssignmentGuid;
        set => SetProperty(ref _servedAssignmentGuid, value, true);
    }

    /// <summary>
    /// La riga di estrazione per UDC (<c>ID_RIGA_ESTRAZIONE_UDC</c>) che il prelievo di questa
    /// estrazione ha servito: la copia <see cref="ProcessExtractionItems"/> dalla riga di coda prima di
    /// staccarsene. Zero se la missione non ne ha servita nessuna. Vedi <see cref="ServedAssignmentId"/>.
    /// </summary>
    [Browsable(true)]
    [Description("The extraction row (ID_RIGA_ESTRAZIONE_UDC) the pick-up of this extraction served")]
    [DisplayName("ServedExtractionRowId")]
    [Category("PathMission")]
    public decimal ServedExtractionRowId
    {
        get => _servedExtractionRowId;
        set => SetProperty(ref _servedExtractionRowId, value, true);
    }

    /// <summary>
    ///     Returns or Sets the unique identifier of PathMission
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the unique identifier of PathMission")]
    [DisplayName("MissionId")]
    [Category("PathMission")]
    public Guid MissionId
    {
        get => _missionId;
        private set => SetProperty(ref _missionId, value, true);
    }

    /// <summary>
    ///     Returns or sets the list of all mission steps
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or sets the list of all mission steps")]
    [DisplayName("MissionSteps")]
    [Category("PathMission")]
    public List<MissionStep> MissionSteps
    {
        get { return Collections.MissionSteps.Instance.Where(x => x.MissionId == MissionId).ToList(); }

        // Dalla primitiva della rotta, senza confronto della versione (voce 41): togliere la rotta
        // vecchia e mettere la nuova erano due passi separati, e due scrittori insieme potevano
        // lasciare alla missione le rotte di tutti e due. Sotto il lucchetto della rotta la
        // sostituzione e' una sola, e l'ultimo che scrive vince intero.
        set => TryWriteRoute(null, value);
    }

    /// <summary>
    ///     Returns or Sets the Mission type for the PathMission
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the Mission type for the PathMission")]
    [DisplayName("MissionType")]
    [Category("PathMission")]
    [TypeConverter(typeof(PropertyTypeConverter))]
    public MissionTypes MissionType
    {
        get => _missionType;
        set => SetProperty(ref _missionType, value, true);
    }

    /// <summary>
    ///     Returns or Sets the current movement description of the PathMission
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the current movement description of the PathMission")]
    [DisplayName("MovingDescription")]
    [Category("PathMission")]
    public string? MovingDescription
    {
        get => _movingDescription;
        set => SetProperty(ref _movingDescription, value, true);
    }

    /// <summary>
    ///     Returns or sets the unique identifier to the JOB_ID extraction queue of the NOTE_JOBS table
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or sets the unique identifier to the JOB_ID extraction queue of the NOTE_JOBS table")]
    [DisplayName("NoteProcessingId")]
    [Category("PathMission")]
    public decimal NoteProcessingId
    {
        get => _noteProcessingId;
        set => SetProperty(ref _noteProcessingId, value, true);
    }

    /// <summary>
    ///     Returns or Sets the type of operation for the PathMission (Inbound/Outbound)
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the type of operation for the PathMission (Inbound/Outbound)")]
    [DisplayName("OperationType")]
    [TypeConverter(typeof(PropertyTypeConverter))]
    [Category("PathMission")]
    public OperationTypes OperationType
    {
        get => _operationType;
        set => SetProperty(ref _operationType, value, true);
    }

    /// <summary>
    ///     Returns whether the destination of the PathMission was chosen by the operator
    ///     <para>
    ///     La scelta dell'operatore vince sulla configurazione (decisione del proprietario del
    ///     30/9): finche' il segno c'e', chi ricostruisce la rotta (<c>Navigator.FindWhereStoreLu</c>,
    ///     i selettori per tipo di UDC, la distribuzione sui polmoni) costruisce verso
    ///     <see cref="DestinationItemId"/> invece di riapplicare la destinazione fissa, la modalita' di
    ///     deposito o il selettore del dispositivo; e chi ha cominciato prima che ci fosse lo
    ///     riguarda nel momento in cui scrive (<see cref="UnlessChosenByOperator"/>). Si alza solo con
    ///     <see cref="SetOperatorDestination"/>, insieme alla destinazione. Cade in due casi: quando
    ///     la missione arriva alla destinazione (setter di <see cref="PositionItemId"/>, come il
    ///     gateway), e da li' il dispositivo raggiunto fa quello che fa con ogni missione arrivata;
    ///     e quando la destinazione cambia per un'altra via (setter di <see cref="DestinationItemId"/>:
    ///     un rifiuto), perche' da quel momento la destinazione non e' piu' quella che l'operatore
    ///     ha scelto. Il distributore circolare la rispetta (voce 40): fa uscire l'UDC da un'uscita
    ///     che ci porta, e senza un'uscita cosi' la missione aspetta con il freno e il segno resta.
    ///     Sta nel database del WCS, quindi sopravvive a un riavvio.
    ///     </para>
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns whether the destination of the PathMission was chosen by the operator")]
    [DisplayName("OperatorDestination")]
    [Category("PathMission")]
    public bool OperatorDestination
    {
        get => _operatorDestination;
        private set => SetProperty(ref _operatorDestination, value, true);
    }

    /// <summary>
    /// What the relocator the operator sent the mission to does with the load unit (item 54, decision 5
    /// of the owner of 13/10): deposit it in a cell the warehouse gives for it, or keep it there.
    /// <para>
    /// The commit of the operator's change writes it (<c>CommitRoute</c>), together with the destination;
    /// unlike <see cref="OperatorDestination"/> it does not fall at the arrival, because the arrival is
    /// where it applies (<see cref="RelocatorArrivalApplies"/>, <c>Navigator.FindWhereStoreLu</c>). It falls
    /// when the destination changes by any way (setter of <see cref="DestinationItemId"/>): the cell the
    /// deposit search gives, a rejection, another change. The WCS's database keeps it.
    /// </para>
    /// </summary>
    [Browsable(true)]
    [Description("What the relocator chosen by the operator does with the load unit: deposit it, or keep it")]
    [DisplayName("RelocatorArrival")]
    [Category("PathMission")]
    public Api.RelocatorArrival RelocatorArrival
    {
        get => _relocatorArrival;
        internal set => SetProperty(ref _relocatorArrival, value, true);
    }

    /// <summary>
    /// The mission stands on the relocator the operator sent it to, with a choice of what the relocator
    /// does with it (<see cref="RelocatorArrival"/>, item 54): from here that choice decides, not the
    /// relocator's configuration.
    /// </summary>
    internal bool RelocatorArrivalApplies =>
        _relocatorArrival != Api.RelocatorArrival.None && _destinationItemId is { } destination &&
        destination != Guid.Empty && Reaches(PositionItemId, destination);

    /// <summary>
    ///     Returns or Sets the door code of the warehouse picking cell
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the door code of the warehouse picking cell")]
    [DisplayName("PickUpDoorCode")]
    [Category("PathMission")]
    public string? PickUpDoorCode
    {
        get => _pickUpDoorCode;
        set => SetProperty(ref _pickUpDoorCode, value, true);
    }

    /// <summary>
    ///     Returns or Sets the AutomationDeviceBase where the PathMission is positioned
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the AutomationDevice where the PathMission is positioned")]
    [DisplayName("PositionItemId")]
    [Category("PathMission")]
    public AutomationDevice? PositionItem
    {
        get => PathDevices.Instance[PositionItemId];
        set
        {
            if (value == null) return;
            PositionItemId = value.ItemGuid;
        }
    }

    /// <summary>
    /// Il lavoro di sfondo che il setter di <see cref="PositionItemId"/> avvia a ogni cambio di
    /// posizione (annuncio dei due dispositivi, tipo di missione, coda di estrazione,
    /// <b>allocazione nella cella collegata</b>, aggiornamento, di nuovo l'annuncio). Parte in un
    /// <c>Task.Run</c> e non lo attende nessuno: e' giusto per il movimento, che non deve aspettare
    /// il gestionale a ogni salto, ma chi <b>scarica</b> l'UDC all'arrivo deve prima lasciarlo
    /// finire — altrimenti la disallocazione da scarico precede l'allocazione nella cella, e la
    /// posizione risorge nel gestionale dopo che l'UDC e' uscita (visto sull'elemento di uscita
    /// OUT02 il 2026-09-10: due righe nello stesso secondo, in ordine inverso). La lambda cattura
    /// ogni eccezione, quindi attenderlo non solleva mai.
    /// </summary>
    internal Task PositionWork { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// La costruzione della rotta che l'ingresso fra le attive avvia senza attenderla
    /// (<c>ActiveMissions.Add</c>, anche alla ricarica dopo un riavvio), per chi deve sapere che e'
    /// finita prima di toccare la rotta. Due costruzioni insieme non mescolano piu' i loro step (voce
    /// 41): quella che scrive per seconda trova la versione della rotta cambiata, non scrive, e rimette
    /// la missione al giro delle rotte (<see cref="BuildPathToDestination"/>). La lambda cattura ogni
    /// eccezione, quindi attenderla non solleva mai.
    /// </summary>
    internal Task RouteWork { get; set; } = Task.CompletedTask;

    /// <summary>
    ///     Returns or Sets the identifier of the AutomationDeviceBase where the PathMission is positioned
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the identifier of the AutomationDevice where the PathMission is positioned")]
    [DisplayName("PositionItemId")]
    [Category("PathMission")]
    public Guid PositionItemId
    {
        get => _positionItemId;
        set
        {
            try
            {
                if (_positionItemId == value) return;

                // Il gateway e' un obbligo di passaggio: resta finche' la missione non lo raggiunge
                // (o non arriva su un dispositivo del gruppo che e' il gateway), e si azzera anche
                // quando la missione esce dall'impianto (posizione vuota, alla rimozione). Prima si
                // azzerava a ogni cambio di posizione, e una rotta ricostruita dopo il primo passo
                // non lo conosceva piu'.
                if (Gateway != Guid.Empty && (value == Guid.Empty || Reaches(value, Gateway)))
                    Gateway = Guid.Empty;

                var itemSource = PathDevices.Instance[_positionItemId];
                var itemDestination = PathDevices.Instance[value];

                var precedente = _positionItemId;

                // L'indice si aggiorna PRIMA del campo, e l'ordine non e' un dettaglio: e' cio' che
                // impedisce che esista un istante in cui la missione non risulta a bordo ne' del
                // dispositivo di partenza ne' di quello di arrivo. Con la vecchia ricerca su tutte le
                // missioni attive quella finestra non c'era, e riaprirla sarebbe una regressione sulla
                // proprieta' piu' letta del percorso di movimento. Voce D74 del registro.
                Collections.ActiveMissions.Instance.PositionChanged(this, precedente, value);

                // La posizione cambia insieme alla versione della rotta, sotto il suo lucchetto (voce 41):
                // una rotta calcolata dalla posizione di prima non si scrive piu' (TryWriteRoute), e
                // nessuna scrittura della rotta e' a meta' mentre la posizione cambia. E' qui, e non
                // nella rimozione dello step eseguito, che l'avanzamento normale fa salire la versione:
                // l'arrivo che ricostruisce la rotta (PositionWork, qui sotto) la legge dopo, e la
                // rimozione dello step che l'ha portato qui, che arriva quando arriva, non lo fa perdere.
                lock (_route)
                {
                    _positionItemId = value;
                    Interlocked.Increment(ref _routeVersion);
                }

                Date = DateTime.Now;

                // L'UDC lascia la sua cella di partenza: da qui la profondita' che conta e' quella
                // della cella prenotata, che il deposito legge (StorageDepthAfterPickUp). Prima del
                // lavoro di sfondo, che non attende nessuno: il deposito puo' partire subito.
                if (itemDestination is { DeviceType: not DeviceTypes.Storage } &&
                    _storageDepthAfterPickUp is { } depthAfterPickUp)
                {
                    StorageDepth = depthAfterPickUp;
                    StorageDepthAfterPickUp = null;
                }

                // La destinazione scelta dall'operatore vale fino all'arrivo, con la stessa regola
                // del gateway (Reaches): il dispositivo scelto, o un dispositivo del gruppo scelto.
                // Da li' il dispositivo raggiunto fa quello che fa con ogni missione arrivata (la sua
                // destinazione fissa, la sua modalita' di deposito), come oggi; con il segno ancora
                // su, la ricostruzione non avrebbe niente verso cui costruire e la missione
                // girerebbe in coda per sempre. Il segno cade dopo la posizione, non prima: una
                // ricostruzione che arriva in mezzo trova il segno giu' solo con la missione gia'
                // sul dispositivo raggiunto, e non applica a una missione che sta arrivando la
                // configurazione del dispositivo che lascia. E cade sotto il lucchetto della scelta,
                // con la destinazione riletta li': come ogni altra scrittura del segno, controllo e
                // scrittura stanno insieme, e un segno alzato intanto per un'altra destinazione resta.
                lock (_destinationChoice)
                {
                    if (_operatorDestination && Reaches(value, _destinationItemId))
                        SetProperty(ref _operatorDestination, false, true, nameof(OperatorDestination));
                }

                // La posizione e' la proprieta' della missione che i client leggono di piu' (dove sta
                // l'UDC, la freccia del passo successivo), e fino al 2026-09-07 il suo cambio non
                // usciva dal motore: l'hub annunciava una missione all'avvio, alla fine e quando
                // entrava un passo, cosi' la cache dei client la teneva ferma sulla sorgente dopo la
                // prima mossa. L'evento parte dopo l'aggiornamento del campo e dell'indice.
                Navigator.EventSink?.MissionChanged(this, Api.EngineMissionChange.PositionChanged);

                // Il catch qui sotto copre solo la parte sincrona: Task.Run ritorna subito, e da li'
                // in poi l'eccezione vive in un Task che nessuno osserva - in .NET moderno viene
                // scartata in silenzio alla raccolta. Dentro questa lambda passano tre operazioni
                // del confine (ReportMissionServedAssignmentAsync, ConfirmServedAssignmentsAsync e
                // AllocateLoadUnitAsync), e la terza e' la registrazione del deposito fisico: se
                // falliva, la missione proseguiva come se l'unita' di carico fosse stata riposta e
                // non lo sapeva nessuno, ne' un registro ne' uno stato di errore. Il catch va
                // DENTRO la lambda, come fa Navigator.AsiSendLuData.
                //
                // L'annuncio dei due dispositivi (quello lasciato e quello raggiunto) parte due
                // volte, prima e dopo il resto. Voce 31 della coda: stava solo in fondo, dopo le
                // chiamate al gestionale, e un'eccezione di AllocateLoadUnitAsync lo saltava; carico
                // e UDC a bordo li calcola il motore dalle missioni, quindi la pagina non sapeva che
                // l'UDC si era spostata (giro dal vivo del 30/9). Prima, perche' cio' che l'annuncio
                // riporta (carico e UDC a bordo, dall'indice delle posizioni) e' gia' giusto qui, e
                // un gestionale in timeout invece che in rifiuto costa fino a 30 s per chiamata.
                // Dopo, nel finally, perche' ProcessMissionType puo' cambiarlo in silenzio: il giro
                // di PickUpAndStoreAgain riscrive la destinazione e la rotta vecchia si scarta; se
                // l'ultimo passo era rimasto addosso alla sorgente (in Warning, quindi non in volo),
                // azzerarne il RunningStepId restituisce il posto che il dispositivo raggiunto teneva
                // per lui (il suo WaitingStepId), e nessuno lo annuncia. FireStatusLuChanged non
                // solleva, quindi il finally non maschera l'errore, che il catch registra come prima.
                PositionWork = BackgroundWork.Track(Task.Run(async () =>
                {
                    try
                    {
                        await FireStatusLuChanged(itemSource, itemDestination);
                        try
                        {
                            await ProcessMissionType();
                            await ProcessExtractionQueue();
                            await ProcessAllocateOnItem();
                            StartUpdate();
                        }
                        finally
                        {
                            await FireStatusLuChanged(itemSource, itemDestination);
                        }

                        // The arrival ends a step: a change of the operator that waited for it is
                        // applied now, from here (item 54, decision 4). Only nudged, never awaited: the
                        // change can wait for this very work (the commit of a picked-up extraction that
                        // becomes a deposit, MissionGateway.AfterThePickUpHandOffAsync), and awaited
                        // from here it would wait for itself for ever.
                        Api.PendingMissionChanges.Nudge(MissionId);
                    }
                    catch (Exception ex)
                    {
                        LogError($"Error On PathMission->Position, missione {MissionId} verso {value}", ex);
                    }
                }));
            }
            catch (Exception ex)
            {
                Navigator.WriteLog(
                    $"Error On PathMission->Position Description: {ex.Message}\r\nStackTrace: {ex.StackTrace}",
                    LogEntryType.Warning);
            }
        }
    }

    /// <summary>
    ///     Returns or Sets if the destination has been recalculated, for example in case of distribution on the destination
    ///     buffers
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or Sets if the destination has been recalculated, for example in case of distribution on the destination buffers")]
    [DisplayName("ReallocatedDestination")]
    [Category("PathMission")]
    public bool ReallocatedDestination
    {
        get => _reallocatedDestination;
        set => SetProperty(ref _reallocatedDestination, value, true);
    }

    /// <summary>
    ///     Returns or sets the identifier of the AutomationDeviceBase which is responsible for moving the PathMission within
    ///     the warehouse cells
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Returns or sets the identifier of the AutomationDevice which is responsible for moving the PathMission within the warehouse cells")]
    [DisplayName("RelocateItemId")]
    [Category("PathMission")]
    public Guid RelocateItemId
    {
        get => _relocateItemId;
        set => SetProperty(ref _relocateItemId, value, true);
    }

    /// <summary>
    ///     Returns or Sets the source Asi
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the source ASI")]
    [DisplayName("SourceAsi")]
    [Category("PathMission")]
    public string? SourceAsi
    {
        get => _sourceAsi;
        set => SetProperty(ref _sourceAsi, value, true);
    }

    /// <summary>
    ///     Returns or Sets the source AutomationDeviceBase of the PathMission
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the source AutomationDevice of the PathMission")]
    [DisplayName("SourceItem")]
    [Category("PathMission")]
    public AutomationDevice? SourceItem
    {
        get => PathDevices.Instance[SourceItemId];
        set
        {
            if (value == null) return;
            SourceItemId = value.ItemGuid;
        }
    }

    /// <summary>
    ///     Returns or Sets the identifier of the AutomationDeviceBase originating from the PathMission
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the identifier of the AutomationDevice originating from the PathMission")]
    [DisplayName("Source")]
    [Category("PathMission")]
    public Guid SourceItemId
    {
        get => _sourceItemId;
        set => SetProperty(ref _sourceItemId, value, true);
    }

    /// <summary>
    ///     Returns or Sets the source Partition
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the source PARTITION")]
    [DisplayName("SourcePartition")]
    [Category("PathMission")]
    public string? SourcePartition
    {
        get => _sourcePartition;
        set => SetProperty(ref _sourcePartition, value, true);
    }

    /// <summary>
    ///     Returns or Sets the source Subitem
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the source SUBITEM")]
    [DisplayName("SourceSubitem")]
    [Category("PathMission")]
    public string? SourceSubitem
    {
        get => _sourceSubitem;
        set => SetProperty(ref _sourceSubitem, value, true);
    }

    /// <summary>
    ///     Returns or Sets the current state of the PathMission
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the current state of the PathMission")]
    [DisplayName("Status")]
    [TypeConverter(typeof(PropertyTypeConverter))]
    public MissionStatusTypes Status
    {
        get => _status;
        set => SetProperty(ref _status, value, true);
    }

    /// <summary>
    ///     Returns or Sets the description of the current state of the PathMission
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the description of the current state of the PathMission")]
    [DisplayName("StatusDescription")]
    [Category("PathMission")]
    public string? StatusDescription
    {
        get => _statusDescription;
        set => SetProperty(ref _statusDescription, value, true);
    }

    /// <summary>
    ///     Returns or Sets the type of execution of the last move command
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the type of execution of the last move command")]
    [DisplayName("StepOrigin")]
    [Category("PathMission")]
    [TypeConverter(typeof(PropertyTypeConverter))]
    public StepTypes StepOrigin { get; set; }

    /// <summary>
    ///     Returns the MissionStep to execute
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the MissionStep to execute")]
    [DisplayName("StepToDo")]
    [Category("PathMission")]
    public MissionStep? StepToDo
    {
        get
        {
            if (PositionItem == null) return null;
            return PositionItem.StepToDoSource(this);
        }
    }

    /// <summary>
    ///     Returns or Sets the door code of the warehouse storage cell
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the door code of the warehouse storage cell")]
    [DisplayName("StorageDoorCode")]
    [Category("PathMission")]
    public string? StorageDoorCode
    {
        get => _storageDoorCode;
        set
        {
            // Il segno «la cella e' una prenotazione sua» vale per QUESTA porta: ogni altra porta
            // (copiata da un'altra missione, scritta da un comando, da un rigetto, dalla ricarica, o
            // tolta) non e' una prenotazione che il motore ha fatto per la missione, e il segno cade.
            // Chi scrive la porta di una prenotazione sua lo rialza subito dopo. Cade prima della
            // porta: se fra le due righe sul database il processo si ferma, resta la porta vecchia
            // senza segno, cioe' il deposito di prima, e non una porta nuova con il segno di un'altra.
            if (_storageCellReserved && !string.Equals(_storageDoorCode, value, StringComparison.Ordinal))
                StorageCellReserved = false;

            SetProperty(ref _storageDoorCode, value, true);
        }
    }

    /// <summary>
    ///     Returns whether the PathMission lost its storage cell and waits for the navigator to reserve another one
    ///     <para>
    ///     La cella persa (decisioni del proprietario dell'1/10): la missione non tiene piu' presso il
    ///     gestionale la cella verso cui andava - lasciata da un cambio di destinazione rifiutato dopo
    ///     la prenotazione, o da una prenotazione di un'altra cella per quella rifiutata dal PLC, o
    ///     tolta dal gestionale - e ha lasciato porta, indirizzo e rotta (<see cref="DropStorageCell"/>).
    ///     Finche' c'e', il navigatore le prenota una cella nel magazzino della sua destinazione con il
    ///     flusso del cambio (<c>MissionGateway.KeepTheStorageCellAsync</c>), per ogni destinazione,
    ///     scelta dall'operatore o dalla configurazione; per una missione della configurazione, se li'
    ///     non c'e' posto, in un altro magazzino del ricollocatore, e senza nemmeno li' allo scarto
    ///     (<c>MissionGateway.AnotherStorageOrTheRejectionAsync</c>). La configurazione del dispositivo
    ///     di posizione non le sceglie un'altra cella ne' un'altra destinazione
    ///     (<c>Navigator.FindWhereStoreLu</c>), e nessuno le costruisce una rotta verso il magazzino
    ///     nudo (<see cref="BuildPathToDestination"/>). Cade quando la cella nuova e' sua
    ///     (<see cref="CommitRoute"/>; la cella trovata altrove la abbassa da se', anche nello stesso
    ///     magazzino; o la porta che ha si scopre tenuta), e quando la destinazione cambia per
    ///     un'altra via (setter di <see cref="DestinationItemId"/>: un altro magazzino, lo scarto),
    ///     come il segno dell'operatore. Sta nel database del WCS:
    ///     dopo un riavvio la missione non va nel magazzino nudo, e la cella la rifa' il navigatore.
    ///     </para>
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns whether the PathMission lost its storage cell and waits for the navigator to reserve another one")]
    [DisplayName("StorageCellLost")]
    [Category("PathMission")]
    public bool StorageCellLost
    {
        get => _storageCellLost;
        internal set => SetProperty(ref _storageCellLost, value, true);
    }

    /// <summary>
    ///     Returns whether the storage cell of the PathMission is a reservation the engine made for this mission
    ///     <para>
    ///     La cella e' una prenotazione sua (risposta 4 del proprietario del 2/10, chiarita sulla
    ///     domanda del compito 3 della voce 39): la porta e' quella di una cella che il motore ha
    ///     prenotato presso il gestionale con l'identificativo di QUESTA missione - la ricerca del
    ///     navigatore e la riverifica del ricollocatore (<see cref="GetStoreToWarehouse"/>), il flusso
    ///     del cambio e la cella persa rifatta (<see cref="CommitRoute"/>). Con il segno il deposito
    ///     chiede prima al gestionale se la cella e' ancora sua (<see cref="MayStartTheDeposit"/>).
    ///     </para>
    ///     <para>
    ///     Senza, la missione deposita come prima: la sua porta non e' una prenotazione sua, e il
    ///     gestionale risponderebbe sempre «non c'e'». Sono l'ostacolo spostato per estrarre l'UDC
    ///     dietro (<c>MoveObstacleLu</c>, che prenota prima che la missione esista), lo spostamento
    ///     manuale da cella a cella, l'UDC rimessa nella stessa cella
    ///     (<see cref="MissionTypes.PickUpAndStoreAgain"/>), le missioni di un deposito di gruppo che
    ///     copiano la porta prenotata da un'altra, e ogni porta di un polmone.
    ///     </para>
    ///     <para>
    ///     Cade a ogni altra scrittura della porta (setter di <see cref="StorageDoorCode"/>). Sta nel
    ///     database del WCS: dopo un riavvio il deposito chiede ancora. Le righe scritte prima del
    ///     segno valgono «non sua», cioe' il deposito di prima; quelle dell'operatore con una porta
    ///     valgono «sua», perche' allora ogni cambio dell'operatore verso un magazzino la prenotava
    ///     (migrazione <c>AddMissionStorageCellReserved</c>).
    ///     </para>
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns whether the storage cell of the PathMission is a reservation the engine made for this mission")]
    [DisplayName("StorageCellReserved")]
    [Category("PathMission")]
    public bool StorageCellReserved
    {
        get => _storageCellReserved;
        internal set => SetProperty(ref _storageCellReserved, value, true);
    }

    /// <summary>
    ///     Returns or Sets the Tare of the LU
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the Tare of the LU")]
    [DisplayName("Tare")]
    [Category("PathMission")]
    public decimal Tare
    {
        // Difetto preesistente (dal 2026-07-07, commit 222774261): il getter leggeva _idLuType
        // invece di _tare, quindi Tare rispondeva sempre il tipo dell'unita' di carico. Trovato dal
        // test del compito 10 che verifica "tara popolata come oggi" - non lo era mai stata, in
        // lettura. L'unico lettore nel motore e' MissionStore.Snapshot (mission.Tare), quindi ogni
        // riga di MISSION_LU scritta finora porta il tipo al posto della tara nella colonna TARA.
        // Riparato: il getter torna a leggere il proprio campo.
        get => _tare;
        set => SetProperty(ref _tare, value, true);
    }

    #endregion Public Properties

    #region Internal Properties

    /// <summary>
    ///     Returns or Sets whether the mission requires filtering of specific route elements
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets whether the mission requires filtering of specific route elements")]
    [DisplayName("ActiveFilter")]
    [Category("PathMission")]
    internal bool ActiveFilter
    {
        get => _activeFilter;
        set => SetProperty(ref _activeFilter, value, true);
    }

    /// <summary>
    ///     Returns or sets the storage quota detected by the AutomationDeviceBase that carried out the storage
    /// </summary>
    /// <returns></returns>
    internal int DetectedDepth { get; set; }

    /// <summary>
    ///     Checks whether the grouped movement of multiple LUs from a group has been completed
    /// </summary>
    /// <returns></returns>
    internal bool IsGroupedMovementCompleted
    {
        get
        {
            if (GroupedMovementId == Guid.Empty) return true;

            var lastGroupLevelId = 0;
            foreach (var lu in ActiveMissions.Instance.Where(x =>
                         x != null && x.GroupedMovementId == GroupedMovementId))
            {
                if (lu?.PositionItem == null) continue;

                if (lastGroupLevelId == 0)
                    lastGroupLevelId = lu.PositionItem.GroupLevelId;
                else if (lu.PositionItem.GroupLevelId != lastGroupLevelId) return false;
            }

            return true;
        }
    }

    /// <summary>
    ///     Returns or Sets the storage quota within the warehouse cells
    /// </summary>
    /// <returns></returns>
    internal int StorageDepth
    {
        get => _storageDepth;
        set => SetProperty(ref _storageDepth, value, true);
    }

    /// <summary>
    /// La profondita' della cella di deposito prenotata per la missione mentre l'UDC e' ancora nella
    /// sua cella di partenza (voce 39, compito 4, primo giro di correzioni); <c>null</c> se non ce n'e'
    /// una in attesa.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Finche' l'UDC e' nella cella, <see cref="StorageDepth"/> e' la profondita' a cui sta, e il
    /// prelievo la legge per l'indirizzo della cella di partenza: il ricollocatore che converte la
    /// profondita' in partizione cerca la partizione con quella profondita'
    /// (<c>GetPickUpAsiItem</c>), e il gruppo sceglie con lei il membro che preleva. Il cambio cella di
    /// un'estrazione mandata dall'operatore in un altro magazzino prenota la cella nuova prima del
    /// prelievo: la profondita' della cella prenotata, scritta in <see cref="StorageDepth"/>, mandava
    /// il prelievo nella partizione sbagliata della cella di partenza, o in nessuna. Aspetta qui
    /// (<see cref="CommitRoute"/>), e diventa <see cref="StorageDepth"/> quando l'UDC lascia la cella,
    /// cioe' quando la posizione passa su un dispositivo che non e' un magazzino (setter di
    /// <see cref="PositionItemId"/>): e' quella che il deposito legge.
    /// </para>
    /// <para>
    /// Sta nel database del WCS, come <see cref="StorageDepth"/>: dopo un riavvio il prelievo legge
    /// ancora la profondita' di partenza, e il deposito trova quella prenotata. Le righe scritte prima
    /// valgono «nessuna in attesa», cioe' com'era.
    /// </para>
    /// </remarks>
    internal int? StorageDepthAfterPickUp
    {
        get => _storageDepthAfterPickUp;
        set => SetProperty(ref _storageDepthAfterPickUp, value, true);
    }

    /// <summary>
    /// Le segnalazioni del cambio cella sono partite (specifica, risposte dell'8/10 ai punti 2 e 3):
    /// il gestionale puo' aver saputo che l'estrazione lascia l'assegnazione e che l'UDC esce dalla
    /// coda di estrazione (<c>MissionGateway.LeaveTheExtractionAsync</c>), e la missione non e' piu'
    /// l'estrazione che era.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Si alza nel database prima della prima segnalazione (<see cref="MarkTheChangeOfCellReportsAsync"/>);
    /// lo abbassa il commit del cambio, che ne fa un cambio cella (<see cref="CommitRoute"/>), o la
    /// prima segnalazione che non puo' essere arrivata; la chiusura toglie la missione, e la riga con
    /// lei. Alla ricarica una missione che lo porta ed e' ancora un'estrazione - l'host si e' fermato
    /// fra le segnalazioni e il commit, o la chiusura non e' riuscita - si chiude, senza chiamare il
    /// gestionale durante il caricamento (<c>DbInfo.PrepareLoadedMission</c>); la prenotazione che puo'
    /// aver preso torna al gestionale al passaggio a Run (<c>ReservationsOfMissionsClosedOnReload</c>,
    /// risposta 10 del proprietario dell'8/10 sera).
    /// </para>
    /// <para>
    /// Le righe scritte prima del segno valgono «nessuna segnalazione partita», cioe' com'era.
    /// </para>
    /// </remarks>
    internal bool ChangeOfCellReportsSent
    {
        get => _changeOfCellReportsSent;
        set => SetProperty(ref _changeOfCellReportsSent, value, true);
    }

    #endregion Internal Properties

    #region Internal Methods

    /// <summary>
    /// Scrive <see cref="ChangeOfCellReportsSent"/> e aspetta che la riga sia nel database del WCS: il
    /// segno deve esserci prima che la prima segnalazione del cambio cella parta, e non basta che sia
    /// in coda (<c>ORM.PathMission.UpdateAndWaitAsync</c>, che scrive anche in Recover).
    /// </summary>
    /// <exception cref="Exception">
    /// La scrittura non e' riuscita, o la coda dello scrittore non l'ha accettata: il segno in memoria
    /// resta quello scritto qui, e chi chiama decide (per il segno che si alza: niente segnalazioni).
    /// </exception>
    internal async Task MarkTheChangeOfCellReportsAsync(bool sent)
    {
        Task written;

        lock (_scritturaSuDb)
        {
            _changeOfCellReportsSent = sent;
            written = ORM.PathMission.UpdateAndWaitAsync(this);
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChangeOfCellReportsSent)));
        await written;
    }

    /// <summary>
    /// L'unico modo di dire «questa destinazione l'ha scelta l'operatore»: scrive
    /// <see cref="DestinationItemId"/> e, solo se la scrittura e' stata presa, alza
    /// <see cref="OperatorDestination"/>. Il segno non si imposta da solo, perche' vale per QUESTA
    /// destinazione: il setter della destinazione lo abbassa a ogni cambio, quindi l'ordine e'
    /// prima la destinazione e poi il segno.
    /// </summary>
    /// <param name="destinationItemId">Il dispositivo scelto dall'operatore</param>
    /// <returns>
    /// False se la destinazione non e' stata scritta: un identificativo vuoto, o una destinazione
    /// diversa da quella che la missione ha, che la rete del setter scarta con uno step in volo (una
    /// riga di avviso e nient'altro). Il segno allora resta com'era, e chi chiama sa che il cambio
    /// non c'e' stato. La destinazione che la missione ha gia' non si riscrive: il segno si alza su
    /// di lei, anche con uno step in volo, che va gia' li'.
    /// </returns>
    /// <remarks>
    /// Non costruisce la rotta, e non la tocca quando la destinazione e' gia' quella (non la riscrive:
    /// fino alla voce 41 il setter cancellava la rotta anche per lo stesso valore, senza passare dai
    /// dispositivi; ora la stessa destinazione riscritta non tocca niente, ma qui non si riscrive lo
    /// stesso); con una destinazione nuova la rotta vecchia la toglie il setter. Non annuncia niente: e' il pezzo che
    /// il commit del cambio di destinazione (<c>CommitRoute</c>) usa dopo aver tolto la rotta, e la
    /// ricarica all'avvio (<c>MissionStore.ToPathMission</c>) per rimettere il segno letto dal
    /// database.
    /// </remarks>
    internal bool SetOperatorDestination(Guid destinationItemId)
    {
        if (destinationItemId == Guid.Empty) return false;

        // Scrittura, controllo e segno sotto il lucchetto della scelta: chi scrive dalla
        // configurazione (UnlessChosenByOperator) passa o tutto prima o tutto dopo, e dopo trova
        // il segno. Prima della scelta il lucchetto della rotta, che il setter della destinazione
        // prende: l'ordine dei lucchetti della missione e' quello (vedi _route).
        lock (_route)
        lock (_destinationChoice)
        {
            if (_destinationItemId != destinationItemId)
            {
                // Il segno vecchio cade nel setter senza la riga di avviso: e' l'operatore che sceglie
                // di nuovo, non un'altra via che gli toglie la scelta.
                _operatorIsChoosing = true;
                try
                {
                    DestinationItemId = destinationItemId;
                }
                finally
                {
                    _operatorIsChoosing = false;
                }
            }

            if (DestinationItemId != destinationItemId) return false;

            OperatorDestination = true;
            return true;
        }
    }

    /// <summary>
    /// Una missione su <paramref name="position"/> e' arrivata a <paramref name="target"/>: e' lui, o
    /// un dispositivo del gruppo che e' <paramref name="target"/>. E' la regola con cui cadono il
    /// gateway e il segno della scelta dell'operatore (setter di <see cref="PositionItemId"/>), e con
    /// cui il cambio di destinazione rifiuta una destinazione gia' raggiunta e non scrive un gateway
    /// gia' raggiunto (<c>MissionGateway.ChangeDestinationAsync</c>): una sola, perche' il cambio
    /// conta sul fatto che il setter la applichi uguale.
    /// </summary>
    internal static bool Reaches(Guid position, Guid? target) =>
        position != Guid.Empty && target is { } reached && reached != Guid.Empty &&
        (position == reached || PathDevices.Instance[position]?.BelongedGroup?.ItemGuid == reached);

    /// <summary>Come e' andato l'inizio di un cambio di destinazione dell'operatore (<see cref="TryBeginOperatorChange"/>).</summary>
    internal enum OperatorChangeStart
    {
        /// <summary>Cominciato: fino a <see cref="EndOperatorChange"/> la missione e' ferma.</summary>
        Begun,

        /// <summary>Una ricerca della cella e' dentro, o un altro cambio e' in corso.</summary>
        CellSearchInFlight,

        /// <summary>Un dispositivo sta avviando uno step della missione (<see cref="TryBeginStepStart"/>).</summary>
        StepStarting
    }

    /// <summary>
    /// Il cambio di destinazione dell'operatore comincia (compito 3 delle missioni): da qui alla
    /// <see cref="EndOperatorChange"/> le ricerche della cella che arrivano a
    /// <c>FindRouteAllocateLu</c> aspettano, prima di chiamare il gestionale, e nessun dispositivo
    /// avvia uno step della missione (<see cref="TryBeginStepStart"/> risponde no).
    /// </summary>
    /// <returns>
    /// <see cref="OperatorChangeStart.Begun"/>, oppure perche' il cambio non comincia: una ricerca
    /// della cella gia' dentro (o un altro cambio in corso), o un avvio di uno step in corso. Chi
    /// chiama allora lo rifiuta senza aver toccato niente.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Le ricerche servono al cambio verso un magazzino: prenota per la missione, e la prenotazione
    /// e' una per missione. Una ricerca che prenotasse intanto toglierebbe la cella al cambio, o il
    /// cambio alla ricerca, e la missione resterebbe con la porta di uno e la cella dell'altro. Le
    /// ricerche del giro delle rotte le esclude gia' il posto in <c>Navigator.CellSearchesInFlight</c>,
    /// che il cambio prende per primo; queste sono quelle dei thread dei dispositivi, che di li' non
    /// passano.
    /// </para>
    /// <para>
    /// Gli avvii servono a tutti e due i cambi. Il semaforo
    /// di esecuzione del dispositivo di posizione, che il commit tiene, esclude solo chi avvia da li':
    /// il prelievo lo avvia il dispositivo che preleva (il gettone va sulla destinazione dello step),
    /// il <c>GetFrom</c> il dispositivo successivo sotto il suo semaforo, e i movimenti di un gruppo
    /// il ciclo del gruppo, che di semafori non ne prende. Ognuno di loro, prima del primo gettone, si
    /// conta qui; dopo l'avvio lo step e' in volo e lo si vede dai gettoni
    /// (<see cref="StepInFlight"/>). Cosi', una volta cominciato il cambio, la missione e' ferma:
    /// la rotta vecchia non parte durante le chiamate al gestionale, e il commit non trova uno step
    /// partito nel frattempo dopo che la prenotazione vecchia e' gia' stata lasciata.
    /// </para>
    /// <para>
    /// Le ricerche della cella dei thread dei dispositivi - il deposito di un ricollocatore
    /// (<c>StartStoreLu</c>), la riverifica e la ricerca delle celle di un gruppo - stanno tutte
    /// dentro un lavoro contato qui: con il cambio cominciato non partono, e mentre sono in corso il
    /// cambio non comincia. L'attesa nell'imbuto di <c>FindRouteAllocateLu</c> resta per chi ci
    /// arrivasse senza contarsi, e non tiene niente che il cambio chieda. Nessuno aspetta l'altro
    /// tenendo qualcosa che l'altro chiede: il cambio non aspetta mai, e chi avvia uno step rinuncia
    /// invece di aspettare.
    /// </para>
    /// </remarks>
    internal OperatorChangeStart TryBeginOperatorChange()
    {
        lock (_storageWork)
        {
            if (_cellSearches > 0 || _operatorChange != null) return OperatorChangeStart.CellSearchInFlight;
            if (_stepStarts > 0) return OperatorChangeStart.StepStarting;

            _operatorChange = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return OperatorChangeStart.Begun;
        }
    }

    /// <summary>
    /// Un dispositivo comincia ad avviare uno step di questa missione, o un gruppo comincia a lavorare
    /// sulla sua cella e sulla sua rotta: si conta prima di prendere il primo gettone (il posto sulla
    /// destinazione) o di toccare la missione, e chiude con <see cref="EndStepStart"/> quando il
    /// gettone dell'esecuzione e' scritto, quando il lavoro e' finito o quando rinuncia.
    /// </summary>
    /// <returns>
    /// False se un cambio di destinazione dell'operatore e' in corso: lo step non parte, come quando
    /// la destinazione e' gia' impegnata, e a cambio finito il cambio sveglia chi lo avvia. False
    /// anche, per sempre, per una missione chiusa (<see cref="Withdraw"/>). False also while a change
    /// of the operator waits for the end of the mission's step (item 54, decision 4:
    /// <c>Api.PendingMissionChanges</c>): the old route does not take one more step before it, and
    /// whoever applies it wakes the devices.
    /// </returns>
    /// <remarks>
    /// Le due cose si escludono sotto lo stesso lucchetto (<see cref="TryBeginOperatorChange"/>): o
    /// l'avvio si conta prima e il cambio non comincia, o il cambio comincia prima e l'avvio
    /// rinuncia. Nessuno dei due aspetta l'altro. Fuori da un cambio dell'operatore la risposta e'
    /// sempre si': il comportamento dei dispositivi non cambia.
    /// </remarks>
    internal bool TryBeginStepStart()
    {
        lock (_storageWork)
        {
            if (_operatorChange != null || _withdrawn || Api.PendingMissionChanges.Holds(MissionId)) return false;

            _stepStarts++;
            return true;
        }
    }

    /// <summary>L'avvio contato con <see cref="TryBeginStepStart"/> e' finito: il gettone e' scritto, o si e' rinunciato.</summary>
    internal void EndStepStart()
    {
        lock (_storageWork)
            _stepStarts--;

        // A start that ends - its step is in flight now, or it gave up: a change waiting for the end of
        // the mission's step looks again (item 54, decision 4). Outside the lock, and never waited for.
        Api.PendingMissionChanges.Nudge(MissionId);
    }

    /// <summary>
    /// A device is starting a step of the mission (<see cref="TryBeginStepStart"/>, not ended yet): a
    /// change waiting for the end of the mission's step waits for this one too (item 54, decision 4).
    /// </summary>
    internal bool StepStarting
    {
        get
        {
            lock (_storageWork)
                return _stepStarts > 0;
        }
    }

    /// <summary>
    /// The mission waits for a field rejection postponed for a step in flight, that only the route loop
    /// redoes (<see cref="_fieldRejectionPending"/>, <see cref="RedoInTheRouteLoopAsync"/>): a route
    /// computed with the comparison of the version is not written meanwhile. The rejection wins over a
    /// change of destination of the operator (item 54, fix round 3 of task 4).
    /// </summary>
    internal bool AwaitsFieldRejection
    {
        get
        {
            lock (_route)
                return _fieldRejectionPending != null;
        }
    }

    /// <summary>
    /// La missione si chiude e non torna: nessun dispositivo avvia piu' un suo step
    /// (<see cref="TryBeginStepStart"/>), e le missioni attive non la riprendono
    /// (<c>ActiveMissions.Add</c>). Non si abbassa.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lo alzano due vie. Il cambio cella di un'estrazione rifiutato dopo le segnalazioni al gestionale
    /// (<c>MissionGateway.CloseTheMissionAsync</c>, voce 39, compito 4, primo giro di correzioni), prima di
    /// toglierla dalle attive: chi non la trova piu' fra le attive vede anche il segno. E la rimozione di
    /// una missione che esce da un gruppo con le copie della sua porta (<c>ActiveMissions.Remove</c>, compito
    /// 15), dopo averla tolta, mentre e' ancora ferma (<see cref="TryBeginOperatorChange"/>, che tiene fuori
    /// ogni avvio): li' conta che un ciclo del gruppo che l'ha letta prima non la riparta. Nessuno rimette
    /// fra le attive una missione tolta da un dispositivo: le chiamate di <c>ActiveMissions.Add</c> con una
    /// missione che c'era gia' la prendono dalle code di prelievo, dove stanno missioni ancora in un
    /// magazzino; le altre creano una missione nuova, o ricaricano dal database.
    /// </para>
    /// <para>
    /// Chi
    /// prende la missione dalla coda di prelievo per prelevarla la tiene in mano mentre chiede al
    /// gestionale se l'UDC e' li' (<c>VerifyPickUp</c>), e dopo la rimette fra le attive e avvia il
    /// prelievo: senza il segno, una chiusura arrivata durante quella domanda la vedeva tornare, come
    /// un'estrazione verso la baia senza riga di coda ne' assegnazione, e prelevata.
    /// </para>
    /// </remarks>
    internal void Withdraw()
    {
        lock (_storageWork)
            _withdrawn = true;
    }

    /// <summary>
    /// The mission leaves the active missions (<c>ActiveMissions.Remove</c>), and its row in wcs.db
    /// goes with it: from now on the setters' writes of this object (<c>ORM.PathMission.Add</c>,
    /// <c>Update</c> and <c>UpdatePosition</c>) are dropped. <c>ORM.PathMission.UpdateAndWaitAsync</c>,
    /// whose callers await the row on purpose, is not affected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Work that started before the removal still holds the object and writes the whole row when it
    /// ends. The arrival of a stored mission is the case seen: its arrival work dispatches the
    /// Allocate (<see cref="AllocateOnStorage"/>), whose completion removes the mission and deletes
    /// the row, and then writes the row again (<see cref="StartUpdate"/>). The row stayed in wcs.db
    /// with status Run, no API showed it, and <c>PUT /plant/graph</c> answered 409 "work pending"
    /// until the host restarted and the reload dropped it.
    /// </para>
    /// <para>
    /// Under <see cref="_scritturaSuDb"/>, the lock every write of the row is composed under: a write
    /// composed before the mark is queued before the deletion, which the removal queues after the
    /// mark, and one composed after it is not queued at all.
    /// </para>
    /// </remarks>
    internal void MarkRemovedFromTheEngine()
    {
        lock (_scritturaSuDb)
            _removedFromTheEngine = true;
    }

    /// <summary>
    /// The mission is active again (<c>ActiveMissions.Add</c>): its writes reach wcs.db again.
    /// </summary>
    internal void MarkBackInTheEngine()
    {
        lock (_scritturaSuDb)
            _removedFromTheEngine = false;
    }

    /// <summary>The mission has left the engine (<see cref="MarkRemovedFromTheEngine"/>).</summary>
    internal bool IsRemovedFromTheEngine
    {
        get
        {
            lock (_scritturaSuDb)
                return _removedFromTheEngine;
        }
    }

    /// <summary>La missione e' chiusa e non torna (<see cref="Withdraw"/>).</summary>
    internal bool IsWithdrawn
    {
        get
        {
            lock (_storageWork)
                return _withdrawn;
        }
    }

    /// <summary>
    /// <see cref="TryBeginStepStart"/> su ognuna delle missioni, una volta per missione, per chi
    /// lavora su piu' missioni insieme (il gruppo che riverifica o ricerca le celle delle UDC dei suoi
    /// membri): le missioni che stanno cambiando destinazione per mano dell'operatore restano fuori,
    /// con una riga che lo dice.
    /// </summary>
    /// <param name="missions">Le missioni su cui si sta per lavorare; i nulli si ignorano.</param>
    /// <param name="worker">Chi lavora, per la riga di registro.</param>
    /// <returns>Le missioni contate: chi chiama lavora solo su queste, e alla fine chiude ognuna con <see cref="EndStepStart"/>.</returns>
    internal static List<PathMission> TryBeginStepStarts(IEnumerable<PathMission?> missions, string worker)
    {
        var started = new List<PathMission>();

        foreach (var mission in missions.OfType<PathMission>().Distinct())
        {
            if (mission.TryBeginStepStart())
                started.Add(mission);
            else
                Navigator.WriteLog(
                    $"{worker}: la missione {mission.MissionId} sta cambiando destinazione, resta fuori da questo giro",
                    LogEntryType.Information);
        }

        return started;
    }

    /// <summary>
    /// Il navigatore ha visto, chiedendo al gestionale, che la cella di deposito della missione e'
    /// sua (la domanda la rinnova), o gliene ha appena prenotata un'altra, mentre la missione era su
    /// <paramref name="position"/>: il prossimo giro di deposito da li' puo' partire
    /// (<see cref="TakeStorageCellConfirmation"/>, <see cref="MayStartTheDeposit"/>).
    /// </summary>
    /// <param name="position">Dove la missione era.</param>
    /// <param name="door">
    /// La porta della cella che il gestionale ha confermato, letta prima di chiedere; non data, quella
    /// che la missione ha adesso (la cella appena prenotata e scritta). La conferma vale solo per lei:
    /// se intanto un'altra via ha cambiato la porta (la riverifica del ricollocatore, che lascia la
    /// cella e ne cerca un'altra), la conferma arrivata dopo non fa partire il deposito verso una
    /// porta che il gestionale non ha confermato (revisione finale della voce 39).
    /// </param>
    internal void ConfirmStorageCell(Guid position, string? door = null)
    {
        lock (_storageWork)
        {
            _cellConfirmedOn = position;
            _cellConfirmedDoor = door ?? StorageDoorCode;
        }
    }

    /// <summary>
    /// Un giro di deposito comincia e prende per se' la conferma della cella che c'e' adesso, se
    /// c'e' (<see cref="ConfirmStorageCell"/>): la usa lui (<see cref="MayStartTheDeposit"/>), o si
    /// perde con lui, anche se il giro finisce prima di arrivare al deposito (la cella impegnata sul
    /// ricollocatore, il freno della riverifica). Una conferma lasciata li' varrebbe per un giro molto
    /// piu' tardi, quando il gestionale puo' aver gia' lasciato la cella. Una conferma che arriva
    /// mentre il giro e' in corso resta per il giro dopo: e' quello che il risveglio del navigatore
    /// fa partire.
    /// </summary>
    /// <returns>
    /// La posizione su cui la cella era confermata, o <c>null</c>: anche quando la porta che la
    /// conferma ha guardato non e' piu' quella della missione.
    /// </returns>
    internal Guid? TakeStorageCellConfirmation()
    {
        lock (_storageWork)
        {
            var confirmedOn = _cellConfirmedOn;
            var sameDoor = string.Equals(_cellConfirmedDoor, StorageDoorCode, StringComparison.Ordinal);
            _cellConfirmedOn = null;
            _cellConfirmedDoor = null;
            return sameDoor ? confirmedOn : null;
        }
    }

    /// <summary>
    /// Il dispositivo su cui la missione e' a bordo va in errore e aspetta l'operatore
    /// (<c>StepError</c>, <c>StepErrorGroup</c>), o l'operatore lo rimette in marcia (la macchina a stati
    /// che lo vede uscire da <c>Warning</c> o <c>Error</c>): la conferma della cella, se c'e', si butta
    /// (risposta 6 del proprietario dell'8/10). L'operatore lo rimette in marcia quando vuole, anche ore
    /// dopo, e intanto il gestionale puo' aver lasciato la cella: alla ripartenza il deposito la richiede
    /// al gestionale (<see cref="MayStartTheDeposit"/>), anche se il navigatore l'aveva confermata mentre
    /// il dispositivo aspettava. Senza errori non cambia niente: la conferma resta per il giro di
    /// deposito che la prende.
    /// </summary>
    internal void ForgetStorageCellConfirmation() => TakeStorageCellConfirmation();

    /// <summary>
    /// La missione aspetta che la ricerca per tipo del gruppo le rifaccia la porta: ha lasciato quella
    /// che aveva copiato da un'altra missione del gruppo, che ha perso la cella
    /// (<see cref="DropTheCopiedDoor"/>, risposta 8 del proprietario dell'8/10), e da allora non ha
    /// avuto un'altra porta, ne' un'altra destinazione. Il ciclo del gruppo la guarda in ogni modalita'
    /// di deposito (<c>AutomationDevice.VerifyWhereAllocateLuGrouped</c>): le copie le fa anche la
    /// ricerca di una cella nuova dopo un rifiuto del PLC, che cerca per tipo qualunque sia la
    /// modalita' (primo giro di correzioni del compito 12). Senza porta la rotta non si costruisce
    /// (<see cref="BuildPathToDestination"/>): andrebbe al magazzino nudo.
    /// </summary>
    internal bool LeftACopiedDoor =>
        _leftACopiedDoor && string.IsNullOrEmpty(StorageDoorCode) && !OperatorDestination && !StorageCellLost &&
        DestinationItem is { DeviceType: DeviceTypes.Storage };

    /// <summary>
    /// Il segno di <see cref="LeftACopiedDoor"/> com'e', senza le condizioni sulla scena: e' quello che
    /// va nel database del WCS e ne torna con la ricarica. Prima stava solo in memoria, mentre la
    /// porta vuota della copia andava nel database: dopo un riavvio fra il momento in cui la copia
    /// lasciava la porta e quello in cui il gruppo gliela rifaceva, la copia tornava senza porta e
    /// senza segno, e con la modalita' <c>No</c> nessuno la cercava piu', con le altre il ciclo del
    /// gruppo tornava a ogni passata nella ricerca della sua modalita', che non la guarda (secondo giro
    /// di correzioni del compito 12).
    /// </summary>
    internal bool LeftACopiedDoorMark
    {
        get => _leftACopiedDoor;
        set => SetProperty(ref _leftACopiedDoor, value, true);
    }

    /// <summary>La ricerca per tipo del gruppo ha rifatto la porta della copia (<see cref="LeftACopiedDoor"/>).</summary>
    internal void CopiedDoorRedone() => LeftACopiedDoorMark = false;

    /// <summary>
    /// Il deposito di questa missione nel magazzino puo' partire adesso? Per una missione che
    /// l'operatore ha mandato in un magazzino, o la cui cella il motore ha prenotato per lei
    /// (<see cref="StorageCellReserved"/>), si', solo se il giro di deposito ha preso una conferma
    /// della cella presso il gestionale, data dal navigatore con la missione dove e' adesso
    /// (<see cref="TakeStorageCellConfirmation"/>). Senza conferma la missione va al navigatore, che
    /// chiede al gestionale, conferma la cella o, se non e' piu' sua, ne prenota un'altra nel
    /// magazzino della destinazione e rifa' la rotta (per una missione della configurazione senza
    /// posto li', altrove o allo scarto); il deposito riparte al giro dopo del dispositivo, che lo
    /// richiama da se'. Per ogni altra missione la risposta e' si', come prima.
    /// </summary>
    /// <param name="confirmedOn">La conferma che il giro di deposito ha preso all'inizio, o <c>null</c>.</param>
    /// <param name="worker">Chi deposita, per la riga di registro.</param>
    /// <returns>False se il deposito non deve partire: chi chiama non prende gettoni e rinuncia.</returns>
    /// <remarks>
    /// <para>
    /// Le decisioni del proprietario dell'1/10: nessuna missione va verso una cella di magazzino che
    /// non le e' prenotata, anche quando la prenotazione si perde senza un cambio rifiutato - scade
    /// nel gestionale, o lui la revoca. Una missione con la sua rotta puo' arrivare al deposito senza
    /// ripassare dal giro delle rotte (un dispositivo la accoda senza step da fare, o quando una
    /// stazione di lettura con la modalita' di deposito o la destinazione fissa ne legge l'UDC, e dopo
    /// un riavvio la rotta la costruisce la ricarica), quindi l'ultimo punto in cui accorgersene e'
    /// prima del deposito. Li' il dispositivo tiene il suo semaforo di esecuzione, e sotto un semaforo
    /// non si chiama il gestionale: la domanda la fa il navigatore, fuori, e il deposito aspetta la sua
    /// risposta. Una conferma vale per un giro di deposito solo, e mai per un'altra posizione ne' per
    /// un'altra porta; cade anche quando il dispositivo a bordo va in errore o ne esce (risposta 6 del
    /// proprietario dell'8/10, <see cref="ForgetStorageCellConfirmation"/>): il deposito dopo la
    /// ripartenza richiede. Il commit del cambio verso un magazzino non conferma: puo' avvenire a
    /// motore fermo, e il deposito venire ore dopo.
    /// </para>
    /// <para>
    /// <b>Chi rifa' il deposito.</b> Un dispositivo che deposita in un magazzino gli e' collegato, e
    /// un dispositivo collegato a un magazzino non si avvia da se' (<c>AutoExecuteStep</c> e' falso):
    /// il risveglio del navigatore dopo la conferma non lo muove. Lo richiama il suo ciclo, che non
    /// aspetta nessuno: il ciclo delle estrazioni del ricollocatore ogni due secondi, o il ciclo del
    /// gruppo. Il giro dopo la conferma la prende; un giro che arriva prima rimanda la missione al
    /// navigatore, a meno che sia gia' in coda o che una ricerca sia in volo per lei.
    /// </para>
    /// <para>
    /// Sul ricollocatore che riverifica la cella (<c>ConvertDepthToPartition</c>) la domanda viene
    /// prima della riverifica, e dopo il suo freno: una cella che il gestionale ha lasciato scadere e
    /// dato a un'altra UDC si vede qui anche quando l'altra UDC la impegna, e il navigatore la
    /// sostituisce; e mentre il freno corre la missione non torna al navigatore a ogni giro.
    /// </para>
    /// <para>
    /// <b>Quali depositi chiedono</b> (risposta 4 del proprietario del 2/10, chiarita sulla domanda
    /// del compito 3 della voce 39): quelli delle missioni dell'operatore, come dall'1/10, e quelli di
    /// ogni missione la cui cella il motore ha prenotato apposta per lei - la ricerca del navigatore,
    /// la riverifica del ricollocatore, il flusso del cambio, la cella persa rifatta
    /// (<see cref="StorageCellReserved"/>). Per loro una prenotazione scaduta o revocata nel
    /// gestionale non porta piu' l'UDC in una cella che non e' sua, e con il gestionale che non
    /// risponde il deposito aspetta (il navigatore riprova con il suo freno). Depositano come prima,
    /// senza chiedere, le missioni la cui porta non e' una prenotazione loro: l'ostacolo spostato per
    /// estrarre l'UDC dietro, lo spostamento manuale da cella a cella, l'UDC rimessa nella stessa
    /// cella, le missioni di gruppo che copiano la porta di un'altra; il gestionale per loro
    /// risponderebbe sempre «non c'e'», e la cella si rifarebbe altrove. Le copie pero' non partono da
    /// sole: il loro deposito aspetta con la missione che ha prenotato la cella
    /// (<c>AutomationDeviceStepExecutor.BeginStepStarts</c>), e quando lei perde la cella la lasciano con
    /// lei (risposta 8 dell'8/10, <see cref="LeftACopiedDoor"/>). Anche una missione verso un
    /// magazzino senza porta non chiede: non ha una cella da confermare.
    /// </para>
    /// <para>
    /// Le missioni dell'operatore chiedono anche senza il segno della cella, fuori da un gruppo. Le
    /// righe del database dell'operatore con una porta, scritte prima del segno, lo hanno su per la
    /// migrazione (<c>AddMissionStorageCellReserved</c>): senza, su un membro di un gruppo sarebbero
    /// copie, e non chiederebbero piu'. Non chiede la copia a cui
    /// l'operatore ha cambiato solo il gateway (<c>MissionGateway.IsACopy</c>): ha il segno, ma resta una
    /// copia (risposta del 9/10 alla seconda domanda del compito 17).
    /// </para>
    /// </remarks>
    internal bool MayStartTheDeposit(Guid? confirmedOn, string worker)
    {
        if (!TheDepositAsksForTheCell) return true;

        if (confirmedOn == PositionItemId) return true;

        if (Navigator.CellSearchInFlight(MissionId) == null)
            RoutesToBuild.Instance.EnqueueIfAbsent(this);

        Navigator.WriteLog(
            $"{worker}: il deposito della missione {MissionId} aspetta che il navigatore confermi presso il gestionale la cella della porta {StorageDoorCode}",
            LogEntryType.Information);
        return false;
    }

    /// <summary>
    /// Il deposito di questa missione chiede la cella al gestionale, con la conferma del navigatore
    /// (<see cref="MayStartTheDeposit"/>): una missione dell'operatore, o con una cella prenotata per lei
    /// (<see cref="StorageCellReserved"/>), verso un magazzino, che non e' una copia. Una copia non chiede,
    /// anche con il segno dell'operatore messo cambiandole solo il gateway: la cella e' della missione da
    /// cui ha copiato la porta, e il deposito aspetta con lei (risposta del 9/10 alla seconda domanda del
    /// compito 17).
    /// </summary>
    /// <remarks>
    /// E' la stessa regola con cui il navigatore guarda la cella (<c>Api.MissionGateway.KeepTheStorageCellAsync</c>),
    /// che in piu' rifa' la cella persa (<see cref="StorageCellLost"/>): le due condizioni erano scritte
    /// due volte, a mano, e nessuno le teneva insieme (revisione finale del primo giro della voce 39, C15).
    /// </remarks>
    internal bool TheDepositAsksForTheCell =>
        (OperatorDestination || StorageCellReserved) &&
        DestinationItem is { DeviceType: DeviceTypes.Storage } &&
        !Api.MissionGateway.IsACopy(this);

    /// <summary>Il cambio di destinazione dell'operatore e' finito: le ricerche che lo aspettavano ripartono.</summary>
    internal void EndOperatorChange()
    {
        TaskCompletionSource? change;

        lock (_storageWork)
        {
            change = _operatorChange;
            _operatorChange = null;
        }

        change?.TrySetResult();
    }

    /// <summary>
    /// Una ricerca della cella entra in <c>FindRouteAllocateLu</c>: se un cambio dell'operatore e' in
    /// corso lo aspetta, poi si conta. L'attesa non tiene il lucchetto.
    /// </summary>
    private async Task EnterStorageSearchAsync()
    {
        while (true)
        {
            Task change;

            lock (_storageWork)
            {
                if (_operatorChange == null)
                {
                    _cellSearches++;
                    return;
                }

                change = _operatorChange.Task;
            }

            await change;
        }
    }

    /// <summary>La ricerca della cella esce da <c>FindRouteAllocateLu</c>.</summary>
    private void LeaveStorageSearch()
    {
        lock (_storageWork)
            _cellSearches--;
    }

    /// <summary>
    /// Una scrittura che viene dalla configurazione del dispositivo (la destinazione fissa, la
    /// modalita' di deposito, il selettore per tipo di UDC, la distribuzione sui polmoni, il rigetto
    /// per mancanza di celle): si fa solo se l'operatore non ha scelto la destinazione, e il segno
    /// si guarda sotto il lucchetto della scelta, nel momento della scrittura.
    /// </summary>
    /// <param name="write">
    /// La scrittura, sincrona: gira con il lucchetto preso, quindi niente <c>await</c> e niente
    /// gestionale. Puo' scrivere piu' campi insieme (la porta con la destinazione): passano tutti o
    /// nessuno.
    /// </param>
    /// <param name="writer">Chi scrive, per la riga di registro.</param>
    /// <returns>
    /// False se il segno c'e': niente e' stato scritto, e chi chiama lascia la missione com'e'.
    /// </returns>
    /// <remarks>
    /// Il segno letto una volta all'inizio non basta: una ricerca della cella lo legge, aspetta il
    /// gestionale fino a 30 s, e nel frattempo l'operatore puo' scegliere. Scritta dopo, la
    /// destinazione della ricerca prendeva il posto di quella dell'operatore e il setter faceva
    /// cadere il segno. Dal cambio di destinazione (compito 3 del piano delle missioni) le ricerche
    /// in corso escludono il cambio dell'operatore (<see cref="TryBeginOperatorChange"/>,
    /// <see cref="TryBeginStepStart"/>), quindi in campo il segno non arriva piu' a meta' ricerca:
    /// questa guardia resta la rete per chi alzasse il segno senza passare di li'.
    /// </remarks>
    internal bool UnlessChosenByOperator(Action write, [CallerMemberName] string? writer = null)
    {
        // Prima della scelta il lucchetto della rotta: la scrittura puo' cambiare la destinazione o
        // togliere gli step, che lo prendono, e l'ordine dei lucchetti della missione e' quello (vedi
        // _route). Le stesse regole: niente await e niente gestionale.
        lock (_route)
        lock (_destinationChoice)
        {
            if (!_operatorDestination)
            {
                write();
                return true;
            }
        }

        Navigator.WriteLog(
            $"Missione {MissionId}: {writer} non la tocca, la destinazione {DestinationItemId} l'ha scelta l'operatore",
            LogEntryType.Information);
        return false;
    }

    /// <summary>
    /// Che cosa c'e' scritto su questa cella, chiesto al gestionale una volta sola per missione
    /// (compito 5): la prima chiamata con un dato <paramref name="cellId"/> lo decodifica e lo tiene
    /// in <see cref="_cellDescriptions"/>, le successive rispondono dalla memoria. Il metodo generale
    /// che tutti i chiamanti usano, sorgente o destinazione che sia la cella: <see cref="DescribeDestinationAsync"/>
    /// e' solo la scorciatoia per il caso piu' comune, la cella di <see cref="StorageDoorCode"/>.
    /// </summary>
    internal async Task<CellDescription> DescribeCellAsync(decimal cellId, CancellationToken cancellationToken = default)
    {
        if (_cellDescriptions.TryGetValue(cellId, out var cached)) return cached;

        if (Navigator.Warehouse is not { } warehouse)
            throw new InvalidOperationException(
                $"DescribeCellAsync sulla missione {MissionId}: Navigator.Warehouse non e' impostato.");

        var description = await warehouse.DescribeCellAsync(cellId, cancellationToken);
        _cellDescriptions[cellId] = description;
        return description;
    }

    /// <summary>
    /// La decodifica della cella di deposito di questa missione (<see cref="StorageDoorCode"/>),
    /// memorizzata come <see cref="DescribeCellAsync(decimal,System.Threading.CancellationToken)"/>.
    /// La cella si ricava con <see cref="ORM.DbInfo.GetCellId(string?)"/>, che resta sul lato del WCS
    /// (la mappa porte e' nel suo database, non nel gestionale).
    /// </summary>
    internal async Task<CellDescription> DescribeDestinationAsync(CancellationToken cancellationToken = default)
    {
        var cellId = await DbInfo.GetCellId(StorageDoorCode);
        return await DescribeCellAsync(cellId, cancellationToken);
    }

    /// <summary>
    /// Le partizioni di questa cella, chieste al gestionale una volta sola per missione (compito 8),
    /// stesso schema di <see cref="DescribeCellAsync(decimal,System.Threading.CancellationToken)"/>:
    /// la prima chiamata con un dato <paramref name="cellId"/> la tiene in
    /// <see cref="_cellPartitionMaps"/>, le successive rispondono dalla memoria.
    /// </summary>
    internal async Task<CellPartitionMap> GetCellPartitionMapAsync(decimal cellId, CancellationToken cancellationToken = default)
    {
        if (_cellPartitionMaps.TryGetValue(cellId, out var cached)) return cached;

        if (Navigator.Warehouse is not { } warehouse)
            throw new InvalidOperationException(
                $"GetCellPartitionMapAsync sulla missione {MissionId}: Navigator.Warehouse non e' impostato.");

        var map = await warehouse.GetCellPartitionMapAsync(cellId, cancellationToken);
        _cellPartitionMaps[cellId] = map;
        return map;
    }

    /// <summary>
    /// L'indirizzo di campo (ASI/SUBITEM/PARTIZIONE) della porta <paramref name="doorCode"/>, alla
    /// profondita' <paramref name="storageDepth"/> (compito 8, ex <c>Navigator.GetAsiCellInfo</c>).
    /// Risolve la cella dalla porta (mappa del WCS, <see cref="ORM.DbInfo.GetCellId(string?)"/>),
    /// legge la sua descrizione per sapere quale fra le due porte e' <paramref name="doorCode"/>
    /// (<see cref="DescribeCellAsync(decimal,System.Threading.CancellationToken)"/>, gia' in memoria
    /// se qualcun altro l'ha chiesta prima per questa missione), poi cerca nella mappa delle
    /// partizioni (<see cref="GetCellPartitionMapAsync"/>) quella il cui offset e' la profondita'
    /// voluta. Il confronto fra codici porta e' senza distinzione di maiuscole: la collazione di SQL
    /// Server con cui la query originale confrontava le stesse stringhe di solito lo e'.
    /// </summary>
    internal async Task<DataAsi?> GetAsiCellInfoAsync(string? doorCode, int storageDepth = -1, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(doorCode)) return null;

        var cellId = await DbInfo.GetCellId(doorCode);
        if (cellId == 0) return null;

        var description = await DescribeCellAsync(cellId, cancellationToken);
        var map = await GetCellPartitionMapAsync(cellId, cancellationToken);

        if (storageDepth == 0) storageDepth = -1;

        if (string.Equals(doorCode, description.DoorCodeA, StringComparison.OrdinalIgnoreCase))
            return AsiForOffset(map.AsiDoorA, map.SubitemDoorA, map, storageDepth);

        if (string.Equals(doorCode, description.DoorCodeB, StringComparison.OrdinalIgnoreCase))
            return AsiForOffset(map.AsiDoorB, map.SubitemDoorB, map, storageDepth);

        return null;
    }

    private static DataAsi? AsiForOffset(string asi, string subitem, CellPartitionMap map, int storageDepth)
    {
        var partition = map.Partitions.FirstOrDefault(p => p.Offset == storageDepth)?.Name;
        return partition == null ? null : new DataAsi(asi, subitem, partition);
    }

    /// <summary>
    /// L'offset (la profondita' di stoccaggio) della partizione <paramref name="partition"/> nella
    /// cella <paramref name="cellId"/> (compito 8, ex <c>DbInfo.GetCellDepth</c>): cerca nella mappa
    /// delle partizioni (<see cref="GetCellPartitionMapAsync"/>) quella con quel nome. Nessuna
    /// corrispondenza risponde zero, come faceva la query originale senza righe.
    /// </summary>
    internal async Task<int> GetPartitionOffsetAsync(decimal cellId, string? partition, CancellationToken cancellationToken = default)
    {
        if (cellId == 0 || string.IsNullOrEmpty(partition)) return 0;

        var map = await GetCellPartitionMapAsync(cellId, cancellationToken);
        return map.Partitions.FirstOrDefault(p => p.Name == partition)?.Offset ?? 0;
    }

    /// <summary>
    /// L'offset della partizione <paramref name="partition"/>, cercata pero' per indirizzo ASI invece
    /// che per id di cella (compito 8, ex <c>DbInfo.GetStorageDepthByAsi</c>). La query originale non
    /// conosceva <paramref name="cellId"/> in anticipo: cercava in tutta <c>CELLE_AREE</c> la cella la
    /// cui porta A avesse quell'indirizzo. Qui il chiamante la cella la conosce gia' (viene da
    /// <c>StorageDoorCode</c>), quindi si legge la sua mappa e si verifica che sia davvero lei -
    /// revisione del compito 8: <c>ASI_PORTA_A</c>/<c>SUBITEM_PORTA_A</c> non hanno un vincolo di
    /// unicita' nello schema (nessun indice univoco in <c>Database/Tables/dbo.CELLE_AREE.sql</c>), per
    /// cui una cella diversa da quella risolta potrebbe avere lo stesso indirizzo di porta A e la
    /// query originale avrebbe risposto lei. Un indirizzo che non e' quello della porta A della cella
    /// risolta non risponde silenziosamente zero (che e' anche un offset legittimo): si segnala
    /// l'incoerenza e si risponde -1, cosi' il chiamante (<c>GetToAsiItemForDepthConversion</c>)
    /// ricalcola il deposito da capo invece di fidarsi di una profondita' che sembra buona.
    /// </summary>
    internal async Task<int> GetStorageDepthByAsiAsync(decimal cellId, string? asi, string? subitem, string? partition, CancellationToken cancellationToken = default)
    {
        if (cellId == 0 || string.IsNullOrEmpty(asi) || string.IsNullOrEmpty(subitem) || string.IsNullOrEmpty(partition)) return 0;

        var map = await GetCellPartitionMapAsync(cellId, cancellationToken);
        if (map.AsiDoorA != asi || map.SubitemDoorA != subitem)
        {
            Navigator.WriteLog(
                $"GetStorageDepthByAsiAsync sulla missione {MissionId}: la cella {cellId} ha porta A {map.AsiDoorA}.{map.SubitemDoorA}, non {asi}.{subitem} come richiesto - CELLE_AREE non garantisce che l'indirizzo di porta A sia unico, la cella risolta da StorageDoorCode non e' quella cercata",
                LogEntryType.Warning);
            return -1;
        }

        return await GetPartitionOffsetAsync(cellId, partition, cancellationToken);
    }

    /// <summary>
    /// Svuota la memoria per missione del gestionale - celle decodificate e mappe di partizione: la
    /// missione chiude (<see cref="Collections.ActiveMissions.Remove"/>) e quello che sapeva non vale
    /// piu' per la prossima missione che tocchera' le stesse celle.
    /// </summary>
    internal void ClearWarehouseCaches()
    {
        _cellDescriptions.Clear();
        _cellPartitionMaps.Clear();
    }

    /// <summary>
    /// Imposta il blocco della cella la cui porta A e' <paramref name="doorCode"/> (compito 9, ex
    /// <c>DbInfo.SetLockCell</c>). Non e' una lettura, quindi non c'e' memoria da svuotare: passa da
    /// <see cref="PathMission"/> solo perche' il chiamante (<c>HandleGroupStorageErrors</c>) ha gia' in
    /// mano l'unita' di carico, sullo stesso schema di accesso delle letture qui sopra.
    /// </summary>
    internal async Task SetCellLockStateAsync(string doorCode, CellLockState lockState, CancellationToken cancellationToken = default)
    {
        if (Navigator.Warehouse is not { } warehouse)
            throw new InvalidOperationException(
                $"SetCellLockStateAsync sulla missione {MissionId}: Navigator.Warehouse non e' impostato.");

        await warehouse.SetCellLockStateAsync(doorCode, lockState, cancellationToken);
    }

    /// <summary>
    /// Le unita' di carico che occupano <paramref name="cellId"/> (compito 9, ex
    /// <c>DbInfo.GetLUsInCell</c>). Non memorizzata come <see cref="DescribeCellAsync(decimal,System.Threading.CancellationToken)"/>:
    /// il contenuto di una cella cambia proprio nel corso della missione che lo interroga, quindi
    /// tenerlo in cache per la sua durata risponderebbe con un dato vecchio invece che aggiornato.
    /// </summary>
    internal async Task<IReadOnlyList<decimal>> GetCellContentAsync(decimal cellId, CancellationToken cancellationToken = default)
    {
        if (Navigator.Warehouse is not { } warehouse)
            throw new InvalidOperationException(
                $"GetCellContentAsync sulla missione {MissionId}: Navigator.Warehouse non e' impostato.");

        return await warehouse.GetCellContentAsync(cellId, cancellationToken);
    }

    /// <summary>
    /// L'occupazione di <paramref name="cellId"/>, in tre stati (compito 9, ex
    /// <c>DbInfo.IsCellBusyByExtraction</c>/<c>IsCellBusyByPickUp</c>/<c>IsCellBusyByStoring</c>). Non
    /// memorizzata, per lo stesso motivo di <see cref="GetCellContentAsync"/>: l'occupazione e'
    /// esattamente quello che i chiamanti vogliono vedere aggiornato ad ogni domanda.
    /// </summary>
    internal async Task<CellBusyState> GetCellBusyStateAsync(decimal cellId, decimal loadUnitIdToExclude = 0, CancellationToken cancellationToken = default)
    {
        if (Navigator.Warehouse is not { } warehouse)
            throw new InvalidOperationException(
                $"GetCellBusyStateAsync sulla missione {MissionId}: Navigator.Warehouse non e' impostato.");

        return await warehouse.GetCellBusyStateAsync(cellId, loadUnitIdToExclude, cancellationToken);
    }

    /// <summary>
    /// A quale profondita' si puo' depositare in <paramref name="cellId"/> (compito 9, ex
    /// <c>QuotaDepositoUdc</c>). Non memorizzata: la quota dipende da quali unita' di carico sono gia'
    /// presenti, che cambia esattamente per l'effetto della missione che deposita.
    /// </summary>
    internal async Task<CellStoringConstraints> GetCellStoringConstraintsAsync(decimal cellId, CellDoor door, int loadUnitDepth, decimal loadUnitIdToExclude, CancellationToken cancellationToken = default)
    {
        if (Navigator.Warehouse is not { } warehouse)
            throw new InvalidOperationException(
                $"GetCellStoringConstraintsAsync sulla missione {MissionId}: Navigator.Warehouse non e' impostato.");

        return await warehouse.GetCellStoringConstraintsAsync(cellId, door, loadUnitDepth, loadUnitIdToExclude, cancellationToken);
    }

    /// <summary>
    /// Le celle di <paramref name="doorCode"/> (compito 9): usata qui da
    /// <c>PathMission.Methods.AllocateOnStorage</c> (compito 13) per la profondità e il codice porta
    /// che servono a decidere la quota di deposito, prima di chiedere il deposito vero e proprio con
    /// <see cref="AllocateLoadUnitAsync"/>. Non memorizzata, come <see cref="GetCellContentAsync"/>:
    /// non e' una lettura ripetuta nella stessa missione.
    /// </summary>
    internal async Task<IReadOnlyList<CellDescription>> DescribeCellsByDoorAsync(string doorCode, CancellationToken cancellationToken = default)
    {
        if (Navigator.Warehouse is not { } warehouse)
            throw new InvalidOperationException(
                $"DescribeCellsByDoorAsync sulla missione {MissionId}: Navigator.Warehouse non e' impostato.");

        return await warehouse.DescribeCellsByDoorAsync(doorCode, cancellationToken);
    }

    /// <summary>
    /// Deposita fisicamente l'unità di carico (compito 13, ex <c>OperazioniUdc.AllocaInCella</c> più,
    /// per la cella operatore, <c>OperazioniUdc.RientroUdc</c>). La usa direttamente soltanto
    /// <c>PathMission.Methods.AllocateOnItem</c>; il deposito terminale in magazzino passa invece
    /// dall'outbox durevole.
    /// </summary>
    internal async Task AllocateLoadUnitAsync(AllocateLoadUnitRequest request, CancellationToken cancellationToken = default)
    {
        if (Navigator.Warehouse is not { } warehouse)
            throw new InvalidOperationException(
                $"AllocateLoadUnitAsync sulla missione {MissionId}: Navigator.Warehouse non e' impostato.");

        await warehouse.AllocateLoadUnitAsync(request, cancellationToken);
    }

    #endregion Internal Methods

    #region Public Methods

    public bool IsReachableItem(AutomationDevice? targetItem)
    {
        if (targetItem == null)
            return false;
        if (PositionItem != null && PositionItem.ItemGuid == targetItem.ItemGuid)
            return true;

        var startItem = PositionItem;

        if (startItem == null && SourceItem is { DeviceType: DeviceTypes.Storage })
            startItem = SourceItem;

        if (startItem == null) return false;

        var argStompedItems = new List<Guid>();
        var argStompedGroupRoutePath = new List<int>();
        return SearchReachableItem(targetItem, startItem, 0, argStompedItems, argStompedGroupRoutePath);
    }

    #endregion Public Methods

}
