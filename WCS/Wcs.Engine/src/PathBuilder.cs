using System.Collections.Frozen;
using Log.Interface;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Routing;
using static Wcs.Common.Enums;

namespace Wcs.Engine;

/// <summary>
/// Costruisce percorsi ottimali per le missioni di automazione
/// </summary>
public class PathBuilder
{
    #region Private Properties

    /// <summary>
    /// Data e ora di creazione del PathBuilder
    /// </summary>
    private DateTime Date { get; }
    
    /// <summary>
    /// Identificatore della destinazione della missione
    /// </summary>
    private Guid? DestinationItemId { get; }
    
    /// <summary>
    /// Identificatore del gateway attraverso cui deve passare la missione
    /// </summary>
    private Guid GateWayItemId { get; }
    
    /// <summary>
    /// Indica se ignorare lo stato degli elementi durante la costruzione del percorso
    /// </summary>
    private bool IgnoreItemStatus { get; }
    
    /// <summary>
    /// Insieme degli identificatori degli elementi da scartare durante la ricerca del percorso
    /// </summary>
    private HashSet<Guid> ItemsIdToDiscard { get; } = new();
    
    /// <summary>
    /// Identificatore dell'elemento sorgente per la missione
    /// </summary>
    private Guid SourceItemId { get; }
    
    /// <summary>
    /// Missione di percorso di destinazione per cui costruire la rotta
    /// </summary>
    private PathMission TargetMission { get; }

    #endregion

    #region Public Constructors

    /// <summary>
    /// Inizializza una nuova istanza di PathBuilder per la missione specificata
    /// </summary>
    /// <param name="mission">La missione di percorso per cui costruire la rotta</param>
    /// <exception cref="ArgumentNullException">Lanciata quando mission è null</exception>
    /// <param name="mission">La missione per cui cercare il percorso.</param>
    /// <param name="destinazione">
    /// Una destinazione da provare al posto di quella della missione, senza toccarla. Serve alle
    /// sonde: prima <c>CheckRouteToDestination</c> scriveva la destinazione sulla missione, faceva
    /// la ricerca, e la riscriveva com'era — e ogni scrittura cancellava gli step, due volte per
    /// fare una domanda. Con uno step in volo avrebbe liberato il dispositivo a meta' movimento.
    /// Oggi la sonda e' <c>PathMission.ProbeRouteSteps</c>, ed e' l'unica a valorizzare il parametro.
    /// </param>
    /// <param name="gateway">
    /// Un gateway da provare al posto di quello della missione, senza toccarla: <see cref="Guid.Empty"/>
    /// vuol dire nessun gateway, <c>null</c> quello della missione. Serve al cambio di destinazione
    /// (compito 3 del piano delle missioni): il gateway lo sceglie l'operatore insieme alla
    /// destinazione, e sulla missione si scrive solo se il cambio passa. Come la destinazione, lo
    /// valorizza solo <c>PathMission.ProbeRouteSteps</c>.
    /// </param>
    /// <param name="sorgente">
    /// Una partenza da provare al posto della posizione della missione, senza toccarla: la sonda
    /// del distributore circolare (<c>PathMission.ProbeRouteSteps</c>, voce 40) chiede se da una
    /// sua uscita la destinazione scelta dall'operatore si raggiunge.
    /// </param>
    /// <param name="daEvitare">
    /// Un dispositivo che la rotta non attraversa: per la stessa sonda, il distributore da cui la
    /// missione esce, perche' un'uscita che porta alla destinazione solo tornando indietro su di
    /// lui non e' un'uscita che ci porta.
    /// </param>
    /// <param name="tipo">
    /// Un tipo di missione da usare al posto di quello della missione, senza toccarla: decide se la
    /// ricerca ignora lo stato dei dispositivi. Serve all'arrivo di un <c>PickUpAndStoreAgain</c>, che
    /// calcola la rotta del deposito prima di scrivere il tipo nuovo insieme alla rotta (voce 41).
    /// </param>
    public PathBuilder(PathMission mission, Guid? destinazione = null, Guid? gateway = null,
        Guid? sorgente = null, Guid? daEvitare = null, MissionTypes? tipo = null)
    {
        ArgumentNullException.ThrowIfNull(mission);

        TargetMission = mission;
        Date = DateTime.Now;
        GateWayItemId = gateway ?? mission.Gateway;

        SourceItemId = sorgente ?? (mission.PositionItemId != Guid.Empty
            ? mission.PositionItemId
            : mission.SourceItemId);

        DestinationItemId = destinazione ?? mission.DestinationItemId;
        IgnoreItemStatus = (tipo ?? mission.MissionType) == MissionTypes.PickUpAndStoreAgain;

        InitializeFilteredItems();

        if (daEvitare is { } evitare && evitare != Guid.Empty)
            ItemsIdToDiscard.Add(evitare);
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Ottiene i passi della rotta ottimale
    /// </summary>
    /// <param name="considerOffLineAvailable">Se true, considera disponibili anche gli elementi offline o in errore</param>
    /// <returns>Lista degli step della missione per la rotta ottimale, o lista vuota se nessuna rotta è valida</returns>
    public List<MissionStep>? GetRouteSteps(bool considerOffLineAvailable = false)
    {
        if (SourceItemId == Guid.Empty || DestinationItemId is not { } destination || destination == Guid.Empty || SourceItemId == destination)
            return new List<MissionStep>();

        // La richiesta e' immutabile anche nell'insieme di scarto: una copia congelata, non il
        // HashSet vivo del costruttore.
        var request = new RouteRequest(SourceItemId, destination, GateWayItemId, IgnoreItemStatus, considerOffLineAvailable, ItemsIdToDiscard.ToFrozenSet());
        var hops = RouteSearch.Find(Snapshot(), request, out var arrivalsRejectedByGateway);

        // Senza rotta, ma con arrivi scartati per il gateway, l'operatore vede solo «nessuna rotta»
        // e la missione riaccodata: qui si scrive il perche', una riga per ricerca.
        if (hops.Count == 0 && arrivalsRejectedByGateway > 0)
            Navigator.WriteLog(
                $"No route for mission {TargetMission.MissionId} from {SourceItemId} to {destination}: {arrivalsRejectedByGateway} route(s) reached the destination without passing through the gateway {GateWayItemId}",
                LogEntryType.Information);

        return hops.Select(hop => new MissionStep
        {
            StepId = Guid.NewGuid(),
            MissionId = TargetMission.MissionId,
            Date = Date,
            FromNodeId = hop.FromId,
            ToNodeId = hop.ToId,
            FromGroupRoutePath = hop.FromGroupRoutePath
        }).ToList();
    }

    #endregion

    #region Internal Methods

    /// <summary>
    /// La fotografia del motore su cui gira la ricerca: dispositivi, giunzioni, step in coda e UDC a
    /// bordo letti una volta sola. Prima ogni nodo visitato rileggeva le collezioni condivise
    /// (copia di tutte le giunzioni, scansione di tutti gli step per contare quelli di un
    /// dispositivo), e ogni nodo veniva giudicato in un istante diverso.
    /// </summary>
    internal RouteGraph Snapshot()
    {
        var queued = new Dictionary<Guid, int>();
        foreach (var step in MissionSteps.Instance.List)
            if (step?.FromNodeId is { } from && from != Guid.Empty)
                queued[from] = queued.GetValueOrDefault(from) + 1;

        var devices = PathDevices.Instance.List;
        var groupById = devices.Where(d => d.GroupItemId != 0)
            .GroupBy(d => d.GroupItemId)
            .ToDictionary(g => g.Key, g => g.First().ItemGuid);

        var nodes = devices.Select(device =>
        {
            var loadedLu = device.LoadedLu;
            return new RouteNode(
                device.ItemGuid,
                device.DeviceType,
                device.Status,
                device.GroupRoutePathId,
                device.BelongingGroupId != 0 && groupById.TryGetValue(device.BelongingGroupId, out var group) ? group : null,
                queued.GetValueOrDefault(device.ItemGuid),
                loadedLu != null,
                loadedLu?.DestinationItemId);
        });

        var edges = PathJunctions.Instance.List
            .Where(j => j != null)
            .Select(j => new RouteEdge(j!.SourceId, j.DestinationId, j.Priority));

        return new RouteGraph(nodes, edges);
    }

    #endregion

    #region Private Methods - Initialization

    /// <summary>
    /// Inizializza la lista degli elementi filtrati da escludere dalla ricerca del percorso
    /// </summary>
    private void InitializeFilteredItems()
    {
        if (ShouldSkipFilterInitialization())
            return;

        var activeFilterMissions = GetActiveFilterMissions().ToList();

        foreach (var mission in activeFilterMissions)
        {
            var filtered = AddFilteredItemsFromMission(mission);
            if (filtered.Count > 0)
                ReportFilteringMission(mission, filtered);
        }

        ForgetMissionsNoLongerFiltering(activeFilterMissions);
    }

    /// <summary>
    /// Quando e' uscito l'ultimo avviso per ciascuna missione filtrante: e' la cadenza della riga
    /// di <see cref="ReportFilteringMission"/>, che altrimenti uscirebbe a ogni ricerca — e le
    /// ricerche delle estrazioni riaccodate si ripetono ogni pochi giri.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTime> LastFilterReports = new();

    /// <summary>Ogni quanto, al piu', si ripete l'avviso per la stessa missione filtrante: la cadenza dei riepiloghi di <c>BackgroundLoop</c>.</summary>
    internal static readonly TimeSpan FilterReportEvery = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Scrive quale missione sta tenendo fuori dalla ricerca quali dispositivi, e da quanto.
    /// </summary>
    /// <param name="mission">La missione con il filtro attivo</param>
    /// <param name="filtered">I dispositivi che i suoi step tolgono alla ricerca</param>
    /// <remarks>
    /// E' un avviso, non diagnostica: passa anche a <c>ActivateDebugLog</c> spento, perche' e'
    /// esattamente il caso in cui dal registro non si capiva perche' nessuna estrazione trovasse
    /// rotta (voce D90). Si ripete al piu' ogni <see cref="FilterReportEvery"/> per missione
    /// filtrante, come i riepiloghi di <c>BackgroundLoop</c>: una missione ferma da ore lo dice
    /// una volta al minuto, con la sua eta', invece di una volta per ricerca. Non c'e' una soglia
    /// di eta' oltre la quale smetta di filtrare: il valore sarebbe una scelta di impianto.
    /// </remarks>
    private void ReportFilteringMission(PathMission mission, IReadOnlyCollection<Guid> filtered)
    {
        var now = DateTime.Now;
        var last = LastFilterReports.GetOrAdd(mission.MissionId, DateTime.MinValue);
        if (now - last < FilterReportEvery) return;
        if (!LastFilterReports.TryUpdate(mission.MissionId, now, last)) return;

        var age = now - mission.Date;
        Navigator.WriteLog(
            $"Ricerca percorso per la missione {TargetMission.MissionId}: la missione {mission.MissionId} (filtro attivo, sulla posizione {mission.PositionItemId} da {age:hh\\:mm\\:ss}) tiene fuori dalla ricerca i dispositivi {string.Join(", ", filtered)}",
            LogEntryType.Warning);
    }

    /// <summary>
    /// Toglie dalla memoria degli avvisi le missioni che non filtrano piu', cosi' che non cresca
    /// per la vita del processo e che una missione che torni a filtrare avvisi subito.
    /// </summary>
    private static void ForgetMissionsNoLongerFiltering(IEnumerable<PathMission> filtering)
    {
        var alive = filtering.Select(m => m.MissionId).ToHashSet();
        foreach (var missionId in LastFilterReports.Keys)
            if (!alive.Contains(missionId))
                LastFilterReports.TryRemove(missionId, out _);
    }

    /// <summary>
    /// Determina se saltare l'inizializzazione del filtro degli elementi
    /// </summary>
    /// <returns>True se l'inizializzazione del filtro deve essere saltata</returns>
    private bool ShouldSkipFilterInitialization()
    {
        return Navigator.NavStatus == NavigatorStatusTypes.Recover
               || TargetMission.ActiveFilter
               || (!IsPositionItemLinkedToStorage() && !IsPositionItemStorage());
    }

    /// <summary>
    /// Verifica se l'elemento di posizione è collegato a uno storage
    /// </summary>
    /// <returns>True se l'elemento di posizione è collegato a uno storage</returns>
    private bool IsPositionItemLinkedToStorage()
    {
        return TargetMission.PositionItem?.IsLinkedToStorage == true;
    }

    /// <summary>
    /// Verifica se l'elemento di posizione è uno storage
    /// </summary>
    /// <returns>True se l'elemento di posizione è di tipo Storage</returns>
    private bool IsPositionItemStorage()
    {
        return TargetMission.PositionItem?.DeviceType == DeviceTypes.Storage;
    }

    /// <summary>
    /// Ottiene le missioni attive che hanno il filtro attivo e destinazione storage
    /// </summary>
    /// <returns>Enumerabile delle missioni di percorso con filtro attivo</returns>
    private IEnumerable<PathMission> GetActiveFilterMissions()
    {
        return ActiveMissions.Instance
            .Where(mission =>
                mission is { ActiveFilter: true, DestinationItem.DeviceType: DeviceTypes.Storage });
    }

    /// <summary>
    /// Aggiunge gli elementi filtrati dalla missione specificata alla lista di scarto
    /// </summary>
    /// <param name="mission">La missione da cui estrarre gli elementi filtrati</param>
    /// <returns>I dispositivi che questa missione ha aggiunto alla lista di scarto</returns>
    private List<Guid> AddFilteredItemsFromMission(PathMission? mission)
    {
        var filtered = new List<Guid>();
        if (mission?.MissionSteps == null) return filtered;

        foreach (var step in mission.MissionSteps)
        {
            // Gestione SourceItem
            if (step.SourceItem?.ToBeFiltered == true)
            {
                ItemsIdToDiscard.Add(step.SourceItem.ItemGuid);
                filtered.Add(step.SourceItem.ItemGuid);
            }

            // Gestione DestinationItem
            if (step.DestinationItem?.ToBeFiltered == true)
            {
                ItemsIdToDiscard.Add(step.DestinationItem.ItemGuid);
                filtered.Add(step.DestinationItem.ItemGuid);
            }
        }

        return filtered;
    }

    #endregion
}
