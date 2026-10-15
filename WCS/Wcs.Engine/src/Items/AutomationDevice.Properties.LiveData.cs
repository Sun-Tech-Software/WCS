using System.ComponentModel;
using System.Drawing;
using System.Runtime.CompilerServices;
using Helper.Converters;
using Wcs.Engine.Collections;
using Wcs.Common.Events;
using Wcs.Engine.Converters;
using Wcs.Connection.Interface;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Items;

public sealed partial class AutomationDevice
{
    #region Private Fields

    /// <summary>
    /// Iscrizioni dei dispositivi che hanno una UDC da trasferire su questo. Vi scrivono i
    /// <b>mittenti</b>, dal proprio thread; vi legge questo dispositivo dal proprio. Sempre sotto
    /// <see cref="_devicesReadyToTransferLock"/>.
    /// </summary>
    private readonly List<AutomationDevice> _devicesReadyToTransferLu = [];

    private readonly object _devicesReadyToTransferLock = new();

    /// <summary>
    /// Coda delle UDC da prelevare dal magazzino. Sempre sotto <see cref="_queuedLuToPickUpLock"/>.
    /// </summary>
    private readonly List<PathMission?> _queuedLuToPickUp = [];

    private readonly object _queuedLuToPickUpLock = new();

    /// <summary>
    /// La prenotazione di questo dispositivo come destinazione di uno step: il gettone e il lock
    /// che lo protegge, insieme. Si veda <see cref="IncomingStepReservation"/>.
    /// </summary>
    /// <remarks>
    /// Qui c'era il solo lock, e il gettone stava in una proprieta' <c>{ get; set; }</c> pubblica
    /// accanto: le scritture passavano dal lock, le letture no, e il campo si lacerava. Il campo e
    /// il lock stanno adesso nello stesso oggetto e non c'e' altra via per toccarli.
    /// </remarks>
    private readonly IncomingStepReservation _incomingStep = new();

    #endregion Private Fields

    #region Methods

    /// <summary>
    ///     Returns the list of current picking missions
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the list of current picking missions")]
    [DisplayName("ActivePickingMission")]
    [Category("Live Data")]
    public List<PathMission?> ActivePickingMission
    {
        get
        {
            if (BelongedItems.Count != 0)
                return (from c in BelongedItemsMissionSteps
                        group c by c.PathMission
                    into Group
                        let Missions = Group.Key
                        select Missions).ToList();
            return MissionSteps
                .Where(x => x.DestinationItem == this && x.SourceItem is { DeviceType: DeviceTypes.Storage })
                .Select(x => x.PathMission).ToList();
        }
    }

    /// <summary>
    ///     Returns the list of ongoing depot missions
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the list of ongoing depot missions")]
    [DisplayName("ActiveStoringMission")]
    [Category("Live Data")]
    public List<PathMission?> ActiveStoringMission
    {
        get
        {
            if (BelongedItems.Count != 0)
                return (from c in BelongedItemsMissionSteps
                        group c by c.PathMission
                    into Group
                        let Missions = Group.Key
                        select Missions).ToList();
            return Collections.MissionSteps.Instance
                .Where(x => x.SourceItem == this && x.DestinationItem is { DeviceType: DeviceTypes.Storage })
                .Select(x => x.PathMission).ToList();
        }
    }

    /// <summary>
    ///     Returns the PLC code of the last deposit refused in a row (67, 85 or 133), 0 if none
    ///     <para>
    ///     Il conto degli errori di deposito (risposta 1 del proprietario dell'8/10): per dispositivo,
    ///     per codice, sugli errori di deposito consecutivi (<see cref="CountRefusedDeposit"/>). Prende il
    ///     posto di <c>AttemptsToChangeLocation</c>, che contava i cambi di cella senza guardare il codice.
    ///     </para>
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the PLC code of the last deposit refused in a row (67, 85 or 133), 0 if none")]
    [DisplayName("RefusedDepositErrorCode")]
    [Category("Live Data")]
    public int RefusedDepositErrorCode
    {
        get
        {
            lock (_refusedDepositsLock) return _refusedDepositErrorCode;
        }
    }

    /// <summary>
    ///     Returns how many times in a row the PLC refused a deposit with <see cref="RefusedDepositErrorCode"/>
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns how many times in a row the PLC refused a deposit with RefusedDepositErrorCode")]
    [DisplayName("RefusedDepositsInARow")]
    [Category("Live Data")]
    public int RefusedDepositsInARow
    {
        get
        {
            lock (_refusedDepositsLock) return _refusedDepositsInARow;
        }
    }

    /// <summary>
    /// Quante volte di fila lo stesso codice cerca una cella nuova: al rifiuto successivo con lo stesso
    /// codice, il quarto, il dispositivo va in errore e aspetta l'operatore (risposta 1 del proprietario
    /// dell'8/10).
    /// </summary>
    internal const int SearchesForTheSameRefusal = 3;

    /// <summary>
    /// Conta un deposito rifiutato dal PLC con <paramref name="errorCode"/>: lo stesso codice del
    /// rifiuto di prima allunga la fila, un codice diverso la fa ripartire da uno.
    /// </summary>
    /// <param name="errorCode">Il codice del PLC: 67, 85 o 133.</param>
    /// <returns>Quante volte di fila e' arrivato questo codice, compreso questo rifiuto.</returns>
    /// <remarks>
    /// Le celle rifiutate non stanno qui ma sulla missione (<see cref="PathMission.CellsRefusedByThePlc"/>,
    /// risposta 7 del proprietario dell'8/10 sera): restano escluse finche' l'UDC non deposita, anche
    /// quando l'operatore rimette in marcia il dispositivo, che fa ripartire solo il conto. Prima le
    /// celle erano del dispositivo e si dimenticavano con il conto.
    /// </remarks>
    internal int CountRefusedDeposit(int errorCode)
    {
        lock (_refusedDepositsLock)
        {
            _refusedDepositsInARow = errorCode == _refusedDepositErrorCode ? _refusedDepositsInARow + 1 : 1;
            _refusedDepositErrorCode = errorCode;
            return _refusedDepositsInARow;
        }
    }

    /// <summary>
    /// Dimentica la fila degli errori di deposito (risposta 5 del proprietario dell'8/10 sera): solo un
    /// movimento riuscito (<c>AutomationDeviceConnectionManager.HandleMovementSuccess</c>, ogni movimento
    /// riuscito, che prende o posa un'UDC: un deposito, o il prelievo di un'UDC nuova) e l'operatore che
    /// rimette in marcia il dispositivo (<c>ExecutableCommands.ItemExecuteStep</c>, che per un gruppo o un suo membro
    /// rimette in marcia e fa dimenticare il gruppo e tutti i membri, <c>ExecutableCommands.ExecuteSteps</c>,
    /// <c>ExecutableCommands.SetStatusReadyAllItems</c>). Un timeout o un errore di connessione non la
    /// toccano: 85, 85, 85, timeout, 85 e' il quarto 85 di fila. Nemmeno un errore di deposito del gruppo
    /// che deframmenta o non ha UDC a bordo, che va in errore senza contare. Prima la dimenticavano anche
    /// loro, come faceva <c>AttemptsToChangeLocation</c>.
    /// </summary>
    internal void ForgetRefusedDeposits()
    {
        lock (_refusedDepositsLock)
        {
            _refusedDepositErrorCode = 0;
            _refusedDepositsInARow = 0;
        }
    }

    private readonly object _refusedDepositsLock = new();
    private int _refusedDepositErrorCode;
    private int _refusedDepositsInARow;

    /// <summary>
    ///     Returns or Sets the queue of AutomationDevices that are waiting to transfer an ActiveMissions
    /// </summary>
    /// <returns></returns>
    /// <remarks>
    /// <para>
    /// <b>Il getter restituisce una copia.</b> Questa lista e' l'unico stato del dispositivo che
    /// **altri** dispositivi modificano: chi ha una UDC da consegnare e trova la destinazione
    /// occupata si iscrive qui, dal proprio thread. Era una <c>List</c> nuda, quindi l'iscrizione
    /// di un mittente correva contro l'iterazione del destinatario — <c>InvalidOperationException</c>
    /// dentro <c>ExecuteStep</c>, o un'iscrizione persa.
    /// </para>
    /// <para>
    /// Le modifiche passano da <see cref="AddDeviceReadyToTransferLu"/>,
    /// <see cref="RemoveDeviceReadyToTransferLu"/>, <see cref="PruneDevicesReadyToTransferLu"/> e
    /// <see cref="ClearDevicesReadyToTransferLu"/>, che prendono <c>_devicesReadyToTransferLock</c>.
    /// I predicati sono valutati <b>fuori</b> dal lock: leggono lo stato di altri dispositivi
    /// (<c>LoadedLu</c>, <c>CheckLuToReceive</c>) e valutarli dentro aprirebbe un'inversione di
    /// ordine fra i lock di due dispositivi.
    /// </para>
    /// </remarks>
    [Browsable(true)]
    [Description("Returns or Sets the queue of AutomationDevices that are waiting to transfer an ActiveMissions")]
    [DisplayName("QueuedItemsReadyToTransferLU")]
    [Category("Live Data")]
    public List<AutomationDevice> DevicesReadyToTransferLu
    {
        get
        {
            lock (_devicesReadyToTransferLock) return [.._devicesReadyToTransferLu];
        }
    }

    /// <summary>
    /// Numero di dispositivi iscritti in attesa di trasferire una UDC su questo.
    /// </summary>
    internal int DevicesReadyToTransferLuCount
    {
        get
        {
            lock (_devicesReadyToTransferLock) return _devicesReadyToTransferLu.Count;
        }
    }

    /// <summary>
    /// Iscrive un dispositivo fra quelli in attesa di trasferire una UDC su questo.
    /// </summary>
    /// <param name="value">Il dispositivo mittente.</param>
    /// <returns>True se l'iscrizione e' stata aggiunta, false se c'era gia'.</returns>
    internal bool AddDeviceReadyToTransferLu(AutomationDevice? value)
    {
        if (value == null) return false;

        lock (_devicesReadyToTransferLock)
        {
            if (_devicesReadyToTransferLu.Contains(value)) return false;
            _devicesReadyToTransferLu.Add(value);
            return true;
        }
    }

    /// <summary>
    /// Toglie l'iscrizione di un dispositivo mittente.
    /// </summary>
    /// <param name="value">Il dispositivo mittente.</param>
    /// <returns>True se era iscritto.</returns>
    internal bool RemoveDeviceReadyToTransferLu(AutomationDevice? value)
    {
        if (value == null) return false;

        lock (_devicesReadyToTransferLock) return _devicesReadyToTransferLu.Remove(value);
    }

    /// <summary>
    /// Toglie le iscrizioni scadute. Il predicato e' valutato fuori dal lock.
    /// </summary>
    /// <param name="stale">Vero per le iscrizioni da togliere.</param>
    internal void PruneDevicesReadyToTransferLu(Func<AutomationDevice, bool> stale)
    {
        List<AutomationDevice> snapshot;

        lock (_devicesReadyToTransferLock)
        {
            if (_devicesReadyToTransferLu.Count == 0) return;
            snapshot = [.._devicesReadyToTransferLu];
        }

        var toRemove = snapshot.Where(stale).ToList();

        if (toRemove.Count == 0) return;

        lock (_devicesReadyToTransferLock)
            foreach (var item in toRemove)
                _devicesReadyToTransferLu.Remove(item);
    }

    /// <summary>
    /// Svuota le iscrizioni.
    /// </summary>
    internal void ClearDevicesReadyToTransferLu()
    {
        lock (_devicesReadyToTransferLock) _devicesReadyToTransferLu.Clear();
    }

    /// <summary>
    ///     Returns or Sets the error number of the move-completed report in case of ConnectionType=Test
    /// </summary>
    /// <returns></returns>
    [DisplayName("ErrorCodeResult")]
    [Description("Returns or Sets the error number of the move-completed report in case of ConnectionType=Test")]
    [Category("Test Options")]
    public int ErrorCodeResult { get; set; }
    /// <summary>
    ///     Returns whether a MissionStep is executing
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns whether a MissionStep is executing")]
    [DisplayName("ExecuteStepIsRunning")]
    [Category("Live Data")]
    public bool ExecuteStepIsRunning
    {
        // Il semaforo e' SemaphoreSlim(1, 1): CurrentCount vale 1 quando e' LIBERO e 0 quando
        // e' preso, cioe' quando uno step e' in esecuzione. Il confronto con 1 restituiva
        // quindi il contrario di quanto il nome promette.
        get => _executionSemaphore.CurrentCount == 0;
    }

    /// <summary>
    ///     Returns whether the device is busy carrying out a command and cannot accept another one
    /// </summary>
    /// <remarks>
    /// <para>
    /// Regola del progetto: <b>un elemento che sta eseguendo un comando non ne accetta altri</b>, e
    /// vale sia mentre esegue uno step sia mentre sta scaricando. Non e' una prudenza: e' che
    /// mandarne un secondo mentre il primo e' in volo verso il campo significa comandare due volte
    /// lo stesso pezzo di impianto.
    /// </para>
    /// <para>
    /// «Occupato» sono tre cose distinte, e servono tutte e tre. Un comando in volo
    /// (<c>RunningStepId</c> valorizzato) e' quello che si vede da <see cref="IsReadyToExecuteStep"/>.
    /// Un'esecuzione gia' dentro il semaforo e' <see cref="ExecuteStepIsRunning"/>. E lo stato di
    /// lavorazione — <c>Moving</c>, <c>Loading</c>, <c>Unloading</c>, <c>Busy</c> — copre il caso
    /// dello scarico, che i primi due non prendono: quando il campo comunica lo scarico lo step e'
    /// gia' stato chiuso, quindi <c>RunningStepId</c> e' vuoto mentre il dispositivo sta ancora
    /// lavorando.
    /// </para>
    /// <para>
    /// <b><c>Recovering</c> resta fuori, ed e' voluto.</b> Non e' un dispositivo che sta eseguendo:
    /// e' un dispositivo che si sta rimettendo in ordine, e il prelievo dalla coda lo ammette
    /// esplicitamente (<c>ExecuteQueueLuToPickUp</c> accetta <c>Ready</c> o <c>Recovering</c>).
    /// Metterlo qui fermerebbe il rientro dopo una ricarica.
    /// </para>
    /// <para>
    /// <b><c>Warning</c> ed <c>Error</c> non sono MAI impegnati, anche se <c>RunningStepId</c> e'
    /// ancora valorizzato.</b> Lo stato di errore e' logico: dice «l'ultimo comando non e' passato»,
    /// e uno step concluso male non e' uno step in volo. L'identificativo puo' restare addosso di
    /// proposito — il ritentativo di gruppo lo usa per sapere che cosa ripetere — ma non significa
    /// che il campo stia facendo qualcosa. Senza questa esclusione, un dispositivo in <c>Warning</c>
    /// dopo uno <c>StepError</c> rifiutava lo scarico manuale, cioe' proprio il comando con cui
    /// l'operatore lo rimette in ordine: togliere il bancale da un elemento in errore deve riportarlo
    /// pronto, non trovarlo occupato.
    /// </para>
    /// </remarks>
    [Browsable(true)]
    [Description("Returns whether the device is busy carrying out a command")]
    [DisplayName("IsBusyWithCommand")]
    [Category("Live Data")]
    public bool IsBusyWithCommand
    {
        get
        {
            if (Status is DeviceStatusTypes.Warning or DeviceStatusTypes.Error)
                return false;

            return !IsReadyToExecuteStep
                   || ExecuteStepIsRunning
                   || Status is DeviceStatusTypes.Moving
                       or DeviceStatusTypes.Loading
                       or DeviceStatusTypes.Unloading
                       or DeviceStatusTypes.Busy;
        }
    }

    /// <summary>
    ///     Returns whether the given step is in flight on this device: sent to the field, and no
    ///     answer yet
    /// </summary>
    /// <param name="stepId">Lo step da controllare.</param>
    /// <remarks>
    /// E' la definizione unica di «in volo», e serve a chi vuole rimuovere una missione o rifarne la
    /// rotta: finche' uno step e' in volo, gli step di quella missione non si toccano, perche'
    /// cancellarli renderebbe libero questo dispositivo a meta' movimento e farebbe scartare il
    /// completamento quando il campo risponde. Un dispositivo in <c>Warning</c> o <c>Error</c> non
    /// ha nulla in volo, per la stessa ragione detta in <see cref="IsBusyWithCommand"/>.
    /// </remarks>
    internal bool IsStepInFlight(Guid stepId)
    {
        if (stepId == Guid.Empty) return false;

        if (Status is DeviceStatusTypes.Warning or DeviceStatusTypes.Error)
            return false;

        return RunningStepId == stepId;
    }

    /// <summary>
    ///     Returns the next group of PathMissions to fetch based on the GroupedMovementId grouping
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the next group of PathMissions to fetch based on the GroupedMovementId grouping")]
    [DisplayName("GroupedLuToPickUp")]
    [Category("Live Data")]
    [ReadOnly(true)]
    public List<PathMission?> GroupedLuToPickUp
    {
        get
        {
            var result = new List<PathMission?>();

            if (QueuedLUtoPickUp.Count == 0 || LUsLoaded.Count > 0 || LUsToPick.Count > 0 ||
                Status is not (DeviceStatusTypes.Ready or DeviceStatusTypes.Recovering))
                return result;

            var groupedMovement = QueuedLUtoPickUp.Where(x => x != null && x.MissionType != MissionTypes.DefragmentCell)
                .Select(x => x!.GroupedMovementId).Distinct();

            var guids = groupedMovement as Guid[] ?? groupedMovement.ToArray();

            if (!guids.Any())
                return result;

            var luToPick = QueuedLUtoPickUp.Where(x => x != null && x.GroupedMovementId == guids.First());

            var missions = luToPick as PathMission[] ?? luToPick.ToArray();

            if (!missions.Any())
                return result;

            result.AddRange(missions);

            return result;
        }
    }

    /// <summary>
    ///     Identifies whether an ExecuteStep procedure is in progress for a GroupItem (Note: This function concerns
    ///     GroupedItems of type GroupOfAutomationDevices)
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description(
        "Identifies whether an ExecuteStep procedure is in progress for a GroupItem (Note: This function concerns GroupedItems of type GroupOfAutomationDevices)")]
    [DisplayName("GroupExecuteStepRunning")]
    [Category("Live Data")]
    public bool GroupExecuteStepRunning
    {
        get => _groupExecuteStepRunning;
        set
        {
            _groupExecuteStepRunning = value;

            if (!GroupLoadingActive && !GroupTransferringActive && !IsLinkedToStorage)
                return;

            if (!value)
                _ = GroupManager?.RestartAfterDelayAsync();
        }
    }

    /// <summary>
    ///     Returns the Availability to execute a MissionStep
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the Availability to execute a MissionStep")]
    [DisplayName("IsReadyToExecuteStep")]
    [Category("Live Data")]
    public bool IsReadyToExecuteStep => RunningStepId == Guid.Empty;

    /// <summary>
    ///     Returns the availability to receive the loaded ActiveMissions from another AutomationDeviceBase
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the availability to receive the loaded ActiveMissions from another AutomationDevice")]
    [DisplayName("IsReadyToReceiveLu")]
    [Category("Live Data")]
    public bool IsReadyToReceiveLu =>
        Status == DeviceStatusTypes.Ready && RunningStepId == Guid.Empty && LoadedLu == null;

    /// <summary>
    ///     Returns the Availability to move the loaded PathMission to another AutomationDeviceBase
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the Availability to move the loaded PathMission to another AutomationDevice")]
    [DisplayName("IsReadyToTransferLu")]
    [Category("Live Data")]
    public bool IsReadyToTransferLu => (Status == DeviceStatusTypes.Ready && LoadedLu != null) ||
                                       (Status == DeviceStatusTypes.Warning && RunningStepId != Guid.Empty &&
                                        LoadedLu != null);

    /// <summary>
    ///     Returns whether a PathMission is being received from an element
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns whether a PathMission is being received from an element")]
    [DisplayName("IsReceivingLu")]
    [Category("Live Data")]
    public bool IsReceivingLu
    {
        get
        {
            if (MotionType == MotionTypes.MoveTo)
                return false;

            var activeMissionStep =
                Collections.MissionSteps.Instance.FirstOrDefault(x => x.StepId == RunningStepId);

            return activeMissionStep != null && activeMissionStep.ToNodeId == ItemGuid;
        }
    }

    /// <summary>
    ///     Returns the last operation performed
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the last operation performed")]
    [DisplayName("LastExecutedStepType")]
    [Category("Live Data")]
    public ExecutedStepTypes LastExecutedStepType { get; set; }

    /// <summary>
    ///     Returns the loading status of the PathMission
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the loading status of the PathMission")]
    [DisplayName("LuLoadStatus")]
    [TypeConverter(typeof(PropertyTypeConverter))]
    [Category("Live Data")]
    [ReadOnly(true)]
    public LuLoadStatusTypes LuLoadStatus =>
        LUsLoaded is { Count: 0 } ? LuLoadStatusTypes.None : LuLoadStatusTypes.Loaded;

    /// <summary>
    ///     Returns the list of PathMissions on board
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the list of PathMissions on board")]
    [DisplayName("LUsLoaded")]
    [Category("Live Data")]
    [ReadOnly(true)]
    public List<PathMission?> LUsLoaded
    {
        get
        {
            var result = new List<PathMission?>();
            switch (DeviceType)
            {
                case DeviceTypes.GroupOfAutomationDevices:
                    {
                        var itemList = BelongedItems;

                        if (itemList.Count == 0)
                            return result;

                        foreach (var item in itemList.Where(x => x != null))
                            if (item?.LoadedLu is not null)
                                result.Add(item.LoadedLu);

                        result.Sort((x, y) => x!.Date.CompareTo(y!.Date));
                        break;
                    }
                case DeviceTypes.Automation:
                case DeviceTypes.Loading:
                case DeviceTypes.Buffer:
                case DeviceTypes.Storage:
                case DeviceTypes.CircularDistributor:
                case DeviceTypes.LoadingAndBuffer:
                case DeviceTypes.None:
                default:
                    {
                        if (LoadedLu != null) result.Add(LoadedLu);
                        break;
                    }
            }

            return result;
        }
    }

    /// <summary>
    ///     Returns the list of PathMissions ready to be loaded from the first GroupLevelId
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the list of PathMissions ready to be loaded from the first GroupLevelId")]
    [DisplayName("LUsReadyToLoad")]
    [Category("Live Data")]
    [ReadOnly(true)]
    public List<PathMission?> LUsReadyToLoad
    {
        get
        {
            var result = new List<PathMission?>();

            if (GroupItemId == 0)
                return result;

            var lUsToReceive = LUsToReceive;

            if (lUsToReceive.Count == 0)
                return result;

            if (lUsToReceive.Any(x => x.IsGroupedMovementCompleted == false))
            {
                var listOfLocalGroupedMovement = LUsLoaded.Select(x => x!.GroupedMovementId).Distinct().ToList();

                for (var count = lUsToReceive.Count - 1; count >= 0; count -= 1)
                    if (!listOfLocalGroupedMovement.Contains(lUsToReceive[count].GroupedMovementId))
                        lUsToReceive.RemoveAt(count);

                if (lUsToReceive.Count == 0)
                    return result;
            }

            if (lUsToReceive.Count > 0)
                switch (IsLinkedToStorage)
                {
                    case false when lUsToReceive.Any(x => x.MissionType == MissionTypes.Extraction):
                        lUsToReceive = lUsToReceive.Where(x => x.MissionType == MissionTypes.Extraction).ToList();
                        break;
                    case false when lUsToReceive.Any(x => x.MissionType == MissionTypes.Rejection):
                        lUsToReceive = lUsToReceive.Where(x => x.MissionType == MissionTypes.Rejection).ToList();
                        break;
                    case true when lUsToReceive.Any(x => x.MissionType == MissionTypes.Storing):
                        {
                            if (TriggerItemToExtractPoint1Item is { LUsLoaded.Count: > 0 } && LUsToReceive.Any(x =>
                                    x.PositionItem != null &&
                                    x.PositionItem.BelongedGroup == TriggerItemToExtractPoint1Item))
                                lUsToReceive = lUsToReceive.Where(x =>
                                        x.PositionItem != null &&
                                        x.PositionItem.BelongedGroup == TriggerItemToExtractPoint1Item)
                                    .ToList();
                            else if (TriggerItemToExtractPoint2Item is { LUsLoaded.Count: > 0 } && LUsToReceive.Any(x =>
                                         x.PositionItem != null &&
                                         x.PositionItem.BelongedGroup == TriggerItemToExtractPoint1Item))
                                lUsToReceive = lUsToReceive.Where(x =>
                                        x.PositionItem != null &&
                                        x.PositionItem.BelongedGroup == TriggerItemToExtractPoint2Item)
                                    .ToList();
                            else
                                lUsToReceive = lUsToReceive.Where(x => x.MissionType == MissionTypes.Storing).ToList();
                            break;
                        }
                    default:
                        {
                            if (LastLevelLoaded > 0 && lUsToReceive.Any(x =>
                                    x.PositionItem != null && x.PositionItem.GroupLevelId != LastLevelLoaded))
                                lUsToReceive = lUsToReceive.Where(x =>
                                    x.PositionItem != null && x.PositionItem.GroupLevelId != LastLevelLoaded).ToList();

                            break;
                        }
                }

            foreach (var lUtoAnalyze in lUsToReceive.Where(x => x.PositionItem != null))
            {
                var item = lUtoAnalyze.PositionItem;

                if (item == null) continue;

                var levelToAnalyze = item.GroupLevelId;

                try
                {
                    result = LoadableLUs(levelToAnalyze);

                    if (result.Count > 0)
                        break;
                }
                catch (Exception)
                {
                    // ignored
                }
            }

            return result;
        }
    }

    /// <summary>
    ///     Returns the list of MissionSteps related to the list of LUsReadyToLoad
    /// </summary>
    /// <returns></returns>
    [Description("Returns the list of MissionSteps related to the list of LUsReadyToLoad")]
    [DisplayName("LUsReadyToLoadSteps")]
    [Category("Live Data")]
    [ReadOnly(true)]
    public List<MissionStep> LUsReadyToLoadSteps
    {
        get
        {
            var lUsToReceive = LUsReadyToLoad;

            return lUsToReceive.Select(currentLu => currentLu?.StepToDo).OfType<MissionStep>().ToList();
        }
    }

    /// <summary>
    ///     Returns the list of PathMissions ready to be transferred to the first GroupLevelId
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the list of PathMissions ready to be transferred to the first GroupLevelId")]
    [DisplayName("LUsReadyToTransfer")]
    [Category("Live Data")]
    [ReadOnly(true)]
    public List<PathMission?> LUsReadyToTransfer
    {
        get
        {
            var result = new List<PathMission?>();

            if (GroupItemId == 0)
                return result;

            var lUsToTransfer = LUsLoaded;

            if (lUsToTransfer.Count == 0)
                return result;

            if (RunningStepId != Guid.Empty && !LUsLoaded.Any(x => x != null && x.GroupedMovementId == RunningStepId))
                RunningStepId = Guid.Empty;

            if (RunningStepId != Guid.Empty)
            {
                result.AddRange(lUsToTransfer.Where(x =>
                    x != null && x.GroupedMovementId == RunningStepId && x.StepToDo != null));
            }
            else
            {
                if (lUsToTransfer.Where(x => x is { IsGroupedMovementCompleted: false }).Any(childLu =>
                        ActiveMissions.Instance.Any(x =>
                            x != null &&
                            childLu != null &&
                            x.GroupedMovementId == childLu.GroupedMovementId &&
                            childLu.StepToDo is { DestinationItem: not null } &&
                            x.StepToDo is { SourceItem: not null } &&
                            childLu.StepToDo.DestinationItem.GroupLevelId != x.StepToDo.SourceItem.GroupLevelId &&
                            x != childLu)))
                    return result;

                foreach (var lUtoAnalyze in lUsToTransfer.Where(x => x?.StepToDo != null))
                {
                    var item = lUtoAnalyze?.StepToDo?.DestinationItem;

                    if (item == null || !Monitor.TryEnter(item, 1000)) continue;

                    try
                    {
                        var levelToAnalyze = item.GroupLevelId;

                        result = LUsReadyToTransferByLevel(levelToAnalyze);

                        if (result.Count > 0)
                            break;
                    }
                    finally
                    {
                        Monitor.Exit(item);
                    }
                }
            }

            return result;
        }
    }

    /// <summary>
    ///     Returns the list of MissionSteps related to the list of LUsReadyToTransfer
    /// </summary>
    /// <returns></returns>
    [Description("Returns the list of MissionSteps related to the list of LUsReadyToTransfer")]
    [DisplayName("LUsReadyToTransferSteps")]
    [Category("Live Data")]
    public List<MissionStep> LUsReadyToTransferSteps
    {
        get
        {
            var lUsReadyToTransfer = LUsReadyToTransfer;

            return lUsReadyToTransfer.Select(currentLu => currentLu?.StepToDo).OfType<MissionStep>().ToList();
        }
    }

    /// <summary>
    ///     Returns or Sets the PathMission that must be taken from the Warehouse
    /// </summary>
    /// <returns></returns>
    [Description("Returns or Sets the PathMission that must be taken from the Warehouse")]
    [DisplayName("LUsToPick")]
    [Category("Live Data")]
    public List<PathMission?> LUsToPick
    {
        get => Volatile.Read(ref _lUsToPick);
        set => Volatile.Write(ref _lUsToPick, value);
    }

    /// <summary>
    /// Il campo di <see cref="LUsToPick"/>: <see cref="LetGoOfLuToPickUp"/> lo sostituisce solo se e'
    /// ancora la lista che ha letto (<see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>).
    /// </summary>
    private List<PathMission?> _lUsToPick = [];

    /// <summary>
    ///     Returns the list of MissionSteps related to the list of LUsToPick
    /// </summary>
    /// <returns></returns>
    [Description("Returns the list of MissionSteps related to the list of LUsToPick")]
    [DisplayName("LUsToPickSteps")]
    [Category("Live Data")]
    public List<MissionStep> LUsToPickSteps
    {
        get
        {
            var result = new List<MissionStep>();

            var lUsToPickUp = LUsToPick;

            foreach (var currentLu in lUsToPickUp)
                foreach (var missionToDo in BelongedItems.Select(item =>
                             Collections.MissionSteps.Instance.FirstOrDefault(x =>
                                 currentLu != null && item != null && x.SourceItem != null &&
                                 x.SourceItem.ItemGuid == item.ItemGuid &&
                                 x.MissionId == currentLu.MissionId && x.ToNodeId == ItemGuid)).OfType<MissionStep>())
                {
                    result.Add(missionToDo);
                    break;
                }

            return result;
        }
    }

    /// <summary>
    ///     Returns the list of UDCs that are awaiting transfer
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the list of UDCs that are awaiting transfer")]
    [DisplayName("LUsToReceive")]
    [Category("Live Data")]
    public List<PathMission> LUsToReceive
    {
        get
        {
            var result = new List<PathMission>();

            // Se l'elemento non fa parte di un gruppo
            if ((GroupItemId == 0 && BelongedGroup == null) ||
                (GroupItemId == 0 && BelongedGroup != null && !BelongedGroup.EnableDefragmentation))
            {
                // Se l'elemento non fa parte di un gruppo

                foreach (var sourceItem in SourceItemsLinked.Where(x => x is { LoadedLu: not null }))
                {
                    if (sourceItem?.LoadedLu == null) continue;

                    var stepToDo = sourceItem.LoadedLu?.StepToDo;

                    if (stepToDo is null)
                        return result;

                    var destinationItem = stepToDo.DestinationItem;

                    if (destinationItem == null) continue;

                    if (destinationItem == this && sourceItem.LoadedLu != null)
                        result.Add(sourceItem.LoadedLu);
                }

                result.Sort((x, y) => x.Date.CompareTo(y.Date));
            }
            else
            {
                foreach (var sourceItem in SourceItemsLinked.Where(x => x is { LoadedLu: not null }))
                {
                    if (sourceItem == null || sourceItem.LoadedLu == null) continue;

                    var stepToDo = sourceItem.LoadedLu.StepToDo;

                    if (stepToDo == null)
                        return result;

                    var destinationItem = stepToDo.DestinationItem;

                    if (destinationItem == null) continue;
                    if (destinationItem == this)
                        result.Add(sourceItem.LoadedLu);
                }
            }

            return result;
        }
    }

    /// <summary>
    ///     Returns or Sets the PathMission that must be taken from the Warehouse
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the PathMission that must be taken from the Warehouse")]
    [DisplayName("LUtoPick")]
    [Category("Live Data")]
    public PathMission? LUtoPick { get; set; }

    /// <summary>
    ///     Returns or Sets the PathMission that is expected to receive from a different AutomationDeviceBase
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or Sets the PathMission that is expected to receive from a different AutomationDevice")]
    [DisplayName("LUtoReceive")]
    [Category("Live Data")]
    public PathMission? LUtoReceive { get; set; }

    /// <summary>
    ///     Returns the list of micromissions to execute for the loaded or expected PathMission
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the list of micromissions to execute for the loaded or expected PathMission")]
    [DisplayName("MissionSteps")]
    [Category("Live Data")]
    [ReadOnly(true)]
    public List<MissionStep> MissionSteps
    {
        get
        {
            return Collections.MissionSteps.Instance
                .Where(x => x.SourceItem != null && x.SourceItem.ItemGuid == ItemGuid).ToList();
        }
        set
        {
            Collections.MissionSteps.Instance.RemoveAll(x =>
                x.SourceItem != null && x.SourceItem.ItemGuid == ItemGuid);
            Collections.MissionSteps.Instance.AddRange(value);
        }
    }

    /// <summary>
    ///     Returns or sets the queue of PathMissions to be withdrawn from the warehouse, this property is valid if the Item is
    ///     connected to a Storage-type AutomationDeviceBase
    /// </summary>
    [Browsable(true)]
    [Description(
        "Returns or sets the queue of PathMissions to be withdrawn from the warehouse, this property is valid if the Item is connected to a Storage-type AutomationDevice")]
    /// <remarks>
    /// <b>Il getter restituisce una copia.</b> La coda e' scritta dal dispositivo stesso, dal
    /// gestore del gruppo a cui appartiene e dai comandi dell'operatore, ed e' <b>percorsa</b> da
    /// <c>Navigator.GetMissionByLuId</c> e dal pubblicatore verso il Viewer, che attraversano le
    /// code di tutti i dispositivi. Era una <c>List</c> nuda: bastava un'aggiunta durante una di
    /// quelle scansioni per far saltare l'iterazione. Le modifiche passano dai metodi
    /// <c>...LuToPickUp</c> qui sotto, che prendono <c>_queuedLuToPickUpLock</c>.
    /// </remarks>
    [DisplayName("QueuedLUtoPickUp")]
    [Category("Live Data")]
    public List<PathMission?> QueuedLUtoPickUp
    {
        get
        {
            lock (_queuedLuToPickUpLock) return [.._queuedLuToPickUp];
        }
    }

    /// <summary>
    /// Numero di UDC in coda di prelievo.
    /// </summary>
    internal int QueuedLuToPickUpCount
    {
        get
        {
            lock (_queuedLuToPickUpLock) return _queuedLuToPickUp.Count;
        }
    }

    /// <summary>
    /// Accoda una UDC da prelevare.
    /// </summary>
    internal void EnqueueLuToPickUp(PathMission? value)
    {
        lock (_queuedLuToPickUpLock) _queuedLuToPickUp.Add(value);
    }

    /// <summary>
    /// Toglie una UDC dalla coda di prelievo.
    /// </summary>
    /// <returns>True se era in coda.</returns>
    internal bool RemoveLuToPickUp(PathMission? value)
    {
        lock (_queuedLuToPickUpLock) return _queuedLuToPickUp.Remove(value);
    }

    /// <summary>
    /// Toglie dalla coda le UDC che soddisfano il predicato, che e' valutato fuori dal lock.
    /// </summary>
    /// <returns>Quante ne sono state tolte.</returns>
    internal int RemoveLuToPickUpWhere(Func<PathMission?, bool> match)
    {
        List<PathMission?> snapshot;

        lock (_queuedLuToPickUpLock)
        {
            if (_queuedLuToPickUp.Count == 0) return 0;
            snapshot = [.._queuedLuToPickUp];
        }

        var toRemove = snapshot.Where(match).ToList();

        if (toRemove.Count == 0) return 0;

        var removed = 0;

        lock (_queuedLuToPickUpLock)
            foreach (var item in toRemove)
                if (_queuedLuToPickUp.Remove(item))
                    removed++;

        return removed;
    }

    /// <summary>
    /// Svuota la coda di prelievo.
    /// </summary>
    internal void ClearLuToPickUp()
    {
        lock (_queuedLuToPickUpLock) _queuedLuToPickUp.Clear();
    }

    /// <summary>
    /// Prende in carico la prima UDC della coda, se il dispositivo non ne ha gia' una in carico e
    /// se il controllo passato la ritiene prelevabile.
    /// </summary>
    /// <param name="canPickUp">Controllo di prelevabilita', valutato <b>fuori</b> dal lock.</param>
    /// <returns>La UDC presa in carico, o null.</returns>
    /// <remarks>
    /// Assegnare <c>LUtoPick</c> e togliere dalla coda erano due passi distinti: fra l'uno e
    /// l'altro un secondo thread poteva prendere la stessa UDC, e il dispositivo si trovava a
    /// prelevare due volte la stessa. Qui la presa in carico e' un solo passo sotto il lock.
    /// Il controllo resta fuori perche' arriva fino a <c>CheckLuToReceive</c>, che prende il lock
    /// delle iscrizioni: valutarlo dentro metterebbe in fila due lock in ordine opposto.
    /// </remarks>
    internal PathMission? TryTakeLuToPickUp(Func<PathMission?, bool> canPickUp)
    {
        PathMission? candidate;

        lock (_queuedLuToPickUpLock)
        {
            if (LUtoPick != null || _queuedLuToPickUp.Count == 0) return null;
            candidate = _queuedLuToPickUp[0];
        }

        if (!canPickUp(candidate)) return null;

        lock (_queuedLuToPickUpLock)
        {
            // Ricontrollo: fra lo sgancio del lock e qui un altro thread puo' aver preso la UDC
            // o averla tolta dalla coda.
            if (LUtoPick != null || !_queuedLuToPickUp.Remove(candidate)) return null;

            LUtoPick = candidate;
            return candidate;
        }
    }

    /// <summary>
    /// Il dispositivo lascia una missione che non va piu' prelevata: esce dalla coda di prelievo, da
    /// <see cref="LUtoPick"/> se l'aveva presa in carico, e da <see cref="LUsToPick"/> se e' un gruppo
    /// che l'aveva presa con altre.
    /// </summary>
    /// <param name="mission">La missione da lasciare.</param>
    /// <remarks>
    /// Coda e presa in carico in un passo solo, sotto il lock della coda, come la presa
    /// (<see cref="TryTakeLuToPickUp"/>): o la presa viene prima, e la missione esce da
    /// <see cref="LUtoPick"/>, o viene dopo, e in coda non la trova piu'. Separati, una presa fra i due
    /// passi la teneva in mano. <see cref="LUsToPick"/> non sta sotto quel lock (lo scrive il ciclo del
    /// gruppo, senza lock): si sostituisce con una copia senza la missione, cosi' chi la sta
    /// percorrendo non si trova la lista cambiata sotto le mani.
    /// <para>
    /// Il ciclo del gruppo pero' la cambia anche sul posto, senza lock. La copia si fa con
    /// <c>ToArray</c>, che non percorre la lista con un enumeratore e quindi non solleva «la
    /// collezione e' cambiata» (con <c>Any</c> e <c>Where</c> la chiusura del cambio cella cadeva,
    /// revisione finale della voce 39); e si scrive solo se la lista e' ancora quella letta: se il
    /// gruppo ne ha assegnata un'altra intanto, si rifa' su quella, invece di cancellare la sua.
    /// </para>
    /// </remarks>
    internal void LetGoOfLuToPickUp(PathMission mission)
    {
        lock (_queuedLuToPickUpLock)
        {
            _queuedLuToPickUp.RemoveAll(queued => ReferenceEquals(queued, mission));
            if (ReferenceEquals(LUtoPick, mission)) LUtoPick = null;
        }

        while (true)
        {
            var takenWithOthers = LUsToPick;
            var snapshot = takenWithOthers.ToArray();
            if (!snapshot.Any(taken => ReferenceEquals(taken, mission))) return;

            List<PathMission?> without = [.. snapshot.Where(taken => !ReferenceEquals(taken, mission))];
            if (ReferenceEquals(Interlocked.CompareExchange(ref _lUsToPick, without, takenWithOthers), takenWithOthers))
                return;
        }
    }

    /// <summary>
    ///     Returns or sets the Executing MissionStep
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or sets the Executing MissionStep")]
    [DisplayName("RunningStep")]
    [Category("Live Data")]
    public MissionStep? RunningStep
    {
        get { return Collections.MissionSteps.Instance.FirstOrDefault(x => x.StepId == _runningStepId); }
        set
        {
            if (value == null)
                RunningStepId = Guid.Empty;
            else if (RunningStepId != value.StepId)
                RunningStepId = value.StepId;
        }
    }

    /// <summary>
    ///     Returns or sets the IdGuid of the Executing MissionStep
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or sets the IdGuid of the Executing MissionStep")]
    [DisplayName("RunningStepId")]
    [Category("Live Data")]
    public Guid RunningStepId
    {
        // Il getter non scrive piu' il campo. Prima lo azzerava quando lo step non esisteva piu',
        // quindi leggere una proprieta' aveva un effetto collaterale osservabile da altri thread e
        // due letture ravvicinate potevano dare risultati diversi. Ora la normalizzazione avviene
        // solo nel valore restituito. Si veda la voce D60 del registro in
        // WCS/doc/debito-tecnico.md.
        //
        // Un identificativo e' vivo se MissionSteps lo conosce: come StepId di uno step (movimento
        // singolo) o come GroupedMovementId di almeno uno step (movimento di gruppo). Il gettone del
        // gruppo non e' la chiave di nessuno step, e la normalizzazione sul solo StepId lo
        // scioglieva sempre: al PLC partiva Empty, e al ritorno i gestori di gruppo lavoravano tutti
        // gli step senza gruppo dell'impianto. Voce D166.
        get => _runningStepId != Guid.Empty &&
               !Collections.MissionSteps.Instance.Any(x => x.StepId == _runningStepId ||
                                                          x.GroupedMovementId == _runningStepId)
            ? Guid.Empty
            : _runningStepId;

        set
        {
            // Si confronta il valore EFFETTIVO, non il campo grezzo: cosi' assegnare Guid.Empty a
            // uno step gia' scomparso e' sempre un'operazione nulla, mentre prima dipendeva dal
            // fatto che qualcuno avesse letto la proprieta' prima, azzerando il campo.
            if (RunningStepId == value) return;

            AutomationDevice? prenotato = null;
            var stepConcluso = RunningStepId;

            if (stepConcluso != Guid.Empty && value == Guid.Empty)
                prenotato = PathDevices.Instance.FirstOrDefault(x => x != null && x.IsWaitingForStep(stepConcluso));

            _runningStepId = value;

            // Rilascio del posto sulla destinazione, quando lo step finisce o viene abbandonato.
            //
            // Qui c'era `destStep.DestinationItem.WaitingStepId = Guid.Empty`, e liberava il
            // dispositivo sbagliato: `prenotato` e' gia' il dispositivo che tiene il gettone, mentre
            // `DestinationItem` e' la destinazione **configurata** di quel dispositivo, un'altra
            // cosa. Il vero prenotato non veniva liberato, e se `DestinationItem` era nullo non si
            // liberava nessuno. Finche' `WaitingStepId` era solo un'informazione la cosa non si
            // vedeva; da quando e' il gettone della prenotazione (voce D73) una perdita qui
            // fermerebbe il tratto, perche' nessun altro potrebbe piu' consegnare a quell'elemento.
            prenotato?.ReleaseIncomingStep(stepConcluso);

            RunningStepChanged?.Invoke(this,
                new AutomationDeviceRunningStepIdChangedEventArgs(RunningStepId, WaitingStepId));
            Navigator.EventSink?.ItemChanged(this);

            _ = BackgroundWork.Track(Task.Run(async () =>
            {
                try
                {
                    await ORM.AutomationDevice.UpdateStatus(this);
                }
                catch (Exception ex)
                {
                    Navigator.WriteLog($"Error updating device status: {ex.Message}", Log.Interface.LogEntryType.Warning);
                }
            }));
        }
    }

    /// <summary>
    ///     Returns or sets the Waiting MissionStep
    /// </summary>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns or sets the Waiting MissionStep")]
    [DisplayName("WaitingStep")]
    [Category("Live Data")]
    public MissionStep? WaitingStep
    {
        get { return Collections.MissionSteps.Instance.FirstOrDefault(x => x.StepId == WaitingStepId); }

        // Il setter e' anteriore alla prenotazione: scriveva il gettone senza chiedere il posto a
        // nessuno, cioe' lo rubava a chi ce l'aveva. Oggi non ha chiamanti — ne' qui ne' in tutto
        // il monorepo — ma resta superficie pubblica di Wcs.Engine, quindi la via esiste ancora.
        // Passa almeno dal lock, cosi' il gettone non si lacera; che possa scavalcare una
        // prenotazione viva e' un problema di progetto, e sta scritto su IncomingStepReservation.
        set
        {
            if (value == null)
                _incomingStep.ForceRelease();
            else
                _incomingStep.Overwrite(value.StepId);
        }
    }

    /// <summary>
    ///     Returns the IdGuid of the Waiting MissionStep
    /// </summary>
    /// <remarks>
    /// Era <c>{ get; set; }</c>: il gettone della prenotazione stava qui, in chiaro, e chiunque
    /// poteva scriverlo e leggerlo fuori dal lock. Ora e' di sola lettura e inoltra a
    /// <see cref="IncomingStepReservation.Current"/>, che legge sotto lock — quindi restituisce
    /// sempre un valore intero, mai meta' di due <see cref="Guid"/> diverse. Per prendere e
    /// restituire il posto si passa da <see cref="TryReserveForIncomingStep"/> e
    /// <see cref="ReleaseIncomingStep"/>.
    /// </remarks>
    /// <returns></returns>
    [Browsable(true)]
    [Description("Returns the IdGuid of the Waiting MissionStep")]
    [DisplayName("WaitingStepId")]
    [Category("Live Data")]
    public Guid WaitingStepId => _incomingStep.Current;

    /// <summary>
    /// Prova a prenotare questo dispositivo come destinazione dello step indicato.
    /// </summary>
    /// <param name="stepId">Lo step per cui si chiede il posto.</param>
    /// <returns>
    /// True se il posto e' stato preso — o se lo aveva gia' preso lo stesso step. False se il
    /// dispositivo e' gia' impegnato per uno step diverso.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Il mittente scriveva <c>WaitingStepId</c> della destinazione direttamente, dopo aver
    /// chiesto <c>CanAcceptLu</c>. Fra la domanda e la scrittura non c'era niente: due mittenti
    /// potevano superare lo stesso controllo, il secondo sovrascriveva il gettone del primo, ed
    /// entrambi mandavano al campo il comando verso questo elemento. Il lock della macchina a
    /// stati rendeva atomica ciascuna scrittura, non esclusiva la decisione. Voce <c>D73</c>.
    /// </para>
    /// <para>
    /// La prenotazione va <b>restituita</b> quando il comando non parte, con
    /// <see cref="ReleaseIncomingStep"/>: una prenotazione appesa rende questo dispositivo
    /// inaccettabile per chiunque altro e ferma il tratto.
    /// </para>
    /// <para>
    /// Il lock rendeva esclusiva la <b>decisione</b>, non protetto il <b>gettone</b>: la
    /// proprieta' era pubblica e scrivibile, e ogni lettura dell'impianto la leggeva fuori dal
    /// lock. Adesso il gettone sta in <see cref="IncomingStepReservation"/>, che possiede insieme
    /// il campo e il lock, e non c'e' piu' modo di toccarlo altrimenti.
    /// </para>
    /// </remarks>
    internal bool TryReserveForIncomingStep(Guid stepId) => _incomingStep.TryReserve(stepId);

    /// <summary>
    /// Restituisce la prenotazione, se e' ancora quella dello step indicato.
    /// </summary>
    /// <param name="stepId">Lo step che rinuncia al posto.</param>
    /// <returns>True se la prenotazione era sua ed e' stata liberata.</returns>
    internal bool ReleaseIncomingStep(Guid stepId) => _incomingStep.Release(stepId);

    /// <summary>
    /// Libera il posto di questo dispositivo <b>chiunque</b> lo tenga.
    /// </summary>
    /// <returns>True se c'era una prenotazione da liberare.</returns>
    /// <remarks>
    /// Revoca, non rinuncia: il mittente che teneva il posto non viene avvisato e puo' ancora
    /// credere di averlo. I due chiamanti — la ricarica dello stato e il gestore degli errori —
    /// spiegano sul posto perche' se lo possono permettere.
    /// </remarks>
    internal bool ForceReleaseIncomingStep() => _incomingStep.ForceRelease();

    /// <summary>
    /// Dice se questo dispositivo tiene il posto proprio per lo step indicato.
    /// </summary>
    /// <param name="stepId">Lo step di cui si cerca la destinazione.</param>
    /// <returns>False per <see cref="Guid.Empty"/>: il posto libero non e' «di» nessuno.</returns>
    /// <remarks>
    /// Sostituisce il confronto <c>x.WaitingStepId == stepId</c>, che leggeva il gettone fuori dal
    /// lock: su una scansione di tutto l'impianto una lettura lacerata avrebbe dato un <b>falso
    /// negativo</b> — il proprietario non trovato, la prenotazione appesa per sempre.
    /// </remarks>
    internal bool IsWaitingForStep(Guid stepId) => _incomingStep.IsHeldBy(stepId);

    /// <summary>
    /// Scioglie una <b>prenotazione</b> rimasta appesa: un posto tenuto per uno step che non esiste
    /// piu' in <c>MissionSteps</c>.
    /// </summary>
    /// <returns>True se una prenotazione appesa e' stata sciolta.</returns>
    /// <remarks>
    /// <para>
    /// <b>E' il secondo passo della rete, indipendente dal primo.</b>
    /// <see cref="TryReleaseHungRunningStep"/> esce alla prima riga se <c>RunningStepId</c> e'
    /// vuoto, e non copre quindi lo stato in cui la prenotazione e' tenuta e il gettone no. Quello
    /// stato si produce in due modi: quando la presa del posto e la scrittura del gettone stanno in
    /// due metodi diversi e in mezzo qualcosa esce (la coppia
    /// <c>ConfigureGetFromStep</c>/<c>ExecuteGetFromMovement</c>), e da solo, perche' far sparire
    /// lo step dalla collezione azzera il gettone — il getter di <c>RunningStepId</c> risponde
    /// <c>Guid.Empty</c> quando lo step non c'e' piu' — e lascia la prenotazione.
    /// </para>
    /// <para>
    /// <b>La condizione e' sicura e non guarda nessuno stato.</b> Uno step che non esiste piu' non
    /// puo' essere in volo, e tutti i punti che prendono il posto lo prendono per uno step letto da
    /// <c>MissionSteps</c>: un posto tenuto per uno step che non c'e' e' appeso per definizione.
    /// E' la stessa regola che il getter di <c>RunningStepId</c> applica gia' al proprio gettone.
    /// </para>
    /// <para>
    /// Si rinuncia, non si revoca: <see cref="ReleaseIncomingStep"/> libera solo se il posto e'
    /// ancora di quello step, quindi una prenotazione presa nel frattempo da qualcun altro non
    /// viene toccata.
    /// </para>
    /// </remarks>
    internal bool TryReleaseHungIncomingReservation()
    {
        var waitingStep = WaitingStepId;

        if (waitingStep == Guid.Empty) return false;
        if (Collections.MissionSteps.Instance.Any(x => x.StepId == waitingStep)) return false;

        if (!ReleaseIncomingStep(waitingStep)) return false;

        Navigator.WriteLog(
            $"StepExecution {ItemGuid}: sciolgo la prenotazione appesa dello step {waitingStep} - quello step non esiste piu'",
            Log.Interface.LogEntryType.Warning);

        return true;
    }

    /// <summary>
    /// Scioglie un <see cref="RunningStepId"/> rimasto appeso: uno step che nessuno sta muovendo,
    /// su un dispositivo che dichiara di non star facendo niente.
    /// </summary>
    /// <returns>True se un gettone appeso e' stato sciolto.</returns>
    /// <remarks>
    /// <para>
    /// <b>Il guasto che ripara.</b> Il 2026-09-09 <c>SH01.0001.0001</c> resto' con un
    /// <c>RunningStepId</c> addosso dopo che la lettura verso il gestionale era fallita fra la
    /// presa del gettone e l'invio del comando: <c>Ready</c>, nessun movimento in atto, 2662
    /// esecuzioni rifiutate, e l'unico rimedio era riavviare il motore. La normalizzazione del
    /// getter non scattava perche' guarda <b>solo se lo step esiste ancora</b>, e quello step
    /// esisteva.
    /// </para>
    /// <para>
    /// <b>La condizione che distingue il gettone appeso da quello tenuto di proposito.</b> Un
    /// identificativo di passo puo' restare addosso volutamente — il ritentativo di gruppo lo usa
    /// per sapere che cosa ripetere (si veda <see cref="ReleaseAfterFailedStep"/>) — e scioglierlo
    /// sarebbe peggio del guasto. Le due cose si separano con quello che il motore scrive da se':
    /// </para>
    /// <list type="bullet">
    /// <item><b>lo stato</b> dev'essere <c>Ready</c>. Chi tiene il gettone di proposito e' in
    /// <c>Warning</c> o in <c>Error</c> — e' la regola scritta su <see cref="IsBusyWithCommand"/>,
    /// e in quegli stati il dispositivo non e' comunque «impegnato», quindi non e' lui a fermare
    /// l'impianto. Chi il gettone lo sta usando davvero e' in <c>Loading</c>, <c>Unloading</c>,
    /// <c>Moving</c> o <c>Busy</c>: ogni punto che prende il gettone scrive lo stato di lavorazione
    /// nella stessa sequenza, senza attese in mezzo. <c>Recovering</c> e <c>OffLine</c> restano
    /// fuori perche' non dichiarano «non sto facendo niente»;</item>
    /// <item><b>nessun comando dev'essere partito da qui</b> per quello step.
    /// <c>MissionStep.RunningNode</c> si scrive insieme a <c>StartStep</c> nell'istruzione che
    /// precede l'invio al campo, in tutti i punti che spediscono un movimento: se non porta
    /// l'identificativo di questo dispositivo, da qui non e' partito niente e non c'e' nessuna
    /// risposta da aspettare. E' la cintura di sicurezza: anche se un percorso lasciasse
    /// <c>Ready</c> un dispositivo con un comando in volo, il gettone non verrebbe sciolto.</item>
    /// </list>
    /// <para>
    /// <b>Perche' non nel getter.</b> Una lettura che scioglie in base allo stato scioglierebbe
    /// anche nell'istante in cui un prelievo sta prendendo il gettone, e due comandi partirebbero
    /// per lo stesso movimento. Qui e' un passo deliberato, chiamato da <see cref="ExecuteStep"/>
    /// prima del rifiuto — cioe' esattamente dove il danno si vede — e chiede in piu' che nessuna
    /// esecuzione sia in corso su questo dispositivo.
    /// </para>
    /// </remarks>
    internal bool TryReleaseHungRunningStep()
    {
        var runningStep = RunningStepId;

        if (runningStep == Guid.Empty) return false;
        if (Status != DeviceStatusTypes.Ready) return false;
        if (ExecuteStepIsRunning) return false;

        var step = Collections.MissionSteps.Instance.FirstOrDefault(x => x.StepId == runningStep);

        if (step == null || step.RunningNode == ItemGuid) return false;

        Navigator.WriteLog(
            $"StepExecution {ItemGuid}: sciolgo il gettone appeso dello step {runningStep} - stato Ready e nessun comando partito da qui per quello step",
            Log.Interface.LogEntryType.Warning);

        // Il setter restituisce anche la prenotazione presa insieme al gettone.
        RunningStepId = Guid.Empty;

        return true;
    }

    #endregion Methods

}
