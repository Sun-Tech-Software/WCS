using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Wcs.Common;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Api;

public sealed record AutomaticCellInfo(decimal CellId, Guid ItemId, Guid RelocatorItemId);

public sealed record PlantBackupSnapshot(
    IReadOnlyList<PlantItem> Items,
    IReadOnlyDictionary<Guid, VersionedItemGeometry> Geometries,
    IReadOnlyList<PlantJunction> Junctions,
    IReadOnlyList<DoorMapping> DoorMappings,
    string Decoration,
    IReadOnlyList<PlcConnection> PlcConnections);

/// <summary>L'impianto visto dall'host: i dispositivi in memoria, il diagramma e la mappa porte sullo store.</summary>
public static class PlantQueries
{
    private static readonly SemaphoreSlim PlantMutationGate = new(1, 1);

    private static async Task<T> MutatePlantAsync<T>(Func<Task<T>> mutation)
    {
        await PlantMutationGate.WaitAsync();
        try { return await mutation(); }
        finally { PlantMutationGate.Release(); }
    }

    /// <summary>
    /// Il cancello delle scritture dell'impianto. Interno e non privato perche' anche
    /// <see cref="PlcSettingsFacade.ReplaceAsync"/> ci passa: le connessioni PLC le scrive anche
    /// <see cref="ReplaceGraphAsync"/>, e le due scritture non devono incrociarsi.
    /// </summary>
    internal static async Task MutatePlantAsync(Func<Task> mutation)
    {
        await PlantMutationGate.WaitAsync();
        try { await mutation(); }
        finally { PlantMutationGate.Release(); }
    }

    /// <summary>
    /// Il caricamento dell'impianto sotto il cancello delle scritture (decisione del proprietario del
    /// 27/9, D2): il comando di ricaricamento (<c>CommandToDo.HandleReloadDiagram</c>) e l'avvio del
    /// motore (<see cref="Navigator.StartAsync"/>, anche nel ritentativo dell'host) ci passano, cosi'
    /// che una scrittura non cada a meta' di un caricamento che rilegge proprio quelle tabelle.
    /// </summary>
    /// <remarks>
    /// Sta qui e non dentro <c>Navigator.LoadNavigatorData</c> perche' il cancello non e' rientrante
    /// e <see cref="SaveDiagramAndReloadAsync"/> ricarica gia' da dentro il cancello: messo nel
    /// caricamento, quella strada si bloccherebbe su se stessa. Il cancello e' di questa classe, e
    /// <see cref="Navigator"/> e <c>CommandToDo</c> stanno nello stesso assembly: nessuna dipendenza
    /// nuova fra progetti.
    /// </remarks>
    internal static Task ReloadUnderGateAsync(Func<Task> reload) => MutatePlantAsync(reload);

    public static IReadOnlyList<AutomationDevice> Devices() => Devices(null);

    /// <summary>I dispositivi, tutti o solo quelli dei tipi indicati.</summary>
    public static IReadOnlyList<AutomationDevice> Devices(IReadOnlyCollection<DeviceTypes>? deviceTypes) =>
        PathDevices.Instance.List.Where(d => d != null && (deviceTypes == null || deviceTypes.Count == 0 || deviceTypes.Contains(d.DeviceType))).ToList()!;
    public static AutomationDevice? Device(Guid itemId) => PathDevices.Instance[itemId];
    public static IReadOnlyList<AutomationJunction> Junctions() => PathJunctions.Instance.List.Where(j => j != null).ToList()!;

    public static Task<PlantDiagram?> DiagramAsync() => StoreAccess.ReadAsync(PlantStore.LoadDiagramAsync);

    /// <summary>
    /// Vero se la geometria e' gia' passata dal blob al modello: da quel momento il disegno e'
    /// congelato e non si salva piu' da <c>PUT /plant/diagram</c>.
    /// </summary>
    /// <remarks>
    /// Legge solo la colonna <c>GeometryMigratedAt</c>
    /// (<see cref="PlantStore.LoadGeometryMigratedAtAsync"/>), non l'entita' intera: questa domanda
    /// gira a ogni <c>PUT /plant/diagram</c>, e caricare anche <c>DiagramXml</c> e <c>View</c> per
    /// leggere una data sola era la voce <c>I10</c> del registro, peggiorata proprio qui.
    /// </remarks>
    public static async Task<bool> GeometryMigratedAsync() =>
        await StoreAccess.ReadAsync(PlantStore.LoadGeometryMigratedAtAsync) != null;

    /// <summary>
    /// La riga di configurazione del dispositivo, presa ora: e' quello che l'host mappa nel DTO.
    /// Passaggio verso <c>PlantStore</c>, che resta interno al motore.
    /// </summary>
    public static PlantItem Snapshot(AutomationDevice device) => PlantStore.Snapshot(device);

    /// <summary>Salva il modello e ricarica il motore, come fa l'Editor dopo un salvataggio.</summary>
    public static async Task SaveDiagramAndReloadAsync(byte[] binary, byte[] xml)
    {
        await MutatePlantAsync(async () =>
        {
            await PlantPersistence.SaveDiagramAsync(binary, xml);
            await ExecutableCommands.ReloadRouteDiagram();
        });
    }

    /// <summary>Toglie il dispositivo e le giunzioni che lo toccano; falso se il dispositivo non c'e'. Rifiutata in Run.</summary>
    /// <remarks>
    /// Le due asimmetrie che questa famiglia aveva, e che le sorelle non hanno. <b>La guardia:</b>
    /// scrittura di configurazione, svuotamento, modalita' di connessione, aggiunta e priorita'
    /// della giunzione chiedono tutte il motore fermo; togliere un dispositivo o un arco no, e a
    /// motore in Run vuol dire cambiare la topologia sotto i piedi del pianificatore mentre ci
    /// passano missioni. <b>La memoria:</b> la cancellazione toccava solo lo store, mentre
    /// <c>GET /plant/items</c> e <c>GET /plant/junctions</c> leggono le collezioni in memoria.
    /// Dopo un <c>DELETE</c> riuscito il dispositivo continuava a rispondere <c>200</c> e la sua
    /// configurazione non c'era piu': la divergenza si scopriva al riavvio successivo, quando il
    /// dispositivo spariva con l'UDC che portava. Le rotte che creano toccano tutte e due.
    /// <para>
    /// L'ordine e' store-poi-memoria, lo stesso di <see cref="RemoveAllItemsAsync"/> e per lo
    /// stesso motivo: un errore in scrittura non deve lasciare la memoria vuota e il database
    /// pieno.
    /// </para>
    /// </remarks>
    public static async Task<bool> RemoveItemAsync(Guid itemId)
    {
        return await MutatePlantAsync(async () =>
        {
            RequireNotRunning("cancellazione del dispositivo");
            if (await StoreAccess.ReadAsync(c => PlantStore.FindItemAsync(c, itemId)) == null) return false;
            await PlantPersistence.RemoveItemAsync(itemId);
            ForgetItem(itemId);
            return true;
        });
    }

    /// <summary>Toglie la giunzione; falso se non c'e'. Rifiutata in Run.</summary>
    public static async Task<bool> RemoveJunctionAsync(Guid junctionId)
    {
        return await MutatePlantAsync(async () =>
        {
            RequireNotRunning("cancellazione della giunzione");
            if (await StoreAccess.ReadAsync(c => PlantStore.FindJunctionAsync(c, junctionId)) == null) return false;
            await PlantPersistence.RemoveJunctionAsync(junctionId);
            PathJunctions.Instance.RemoveAll(j => j.ItemGuid == junctionId);
            return true;
        });
    }

    /// <summary>
    /// «Tieni nessuno» non vuol dire «cancella tutto»: una lista vuota non tocca lo store, non lo
    /// svuota. Rifiutata in Run.
    /// </summary>
    public static async Task KeepItemsAsync(IReadOnlyCollection<Guid> keptItemIds)
    {
        await MutatePlantAsync(async () =>
        {
            RequireNotRunning("pulizia dei dispositivi");
            if (keptItemIds.Count == 0) return;
            await PlantPersistence.RemoveItemsNotInAsync(keptItemIds);
            foreach (var itemId in PathDevices.Instance.List.Where(d => d != null && !keptItemIds.Contains(d.ItemGuid))
                         .Select(d => d!.ItemGuid).ToList())
                ForgetItem(itemId);
        });
    }

    /// <summary>
    /// Toglie dalla memoria il dispositivo e le giunzioni che lo toccano, come
    /// <c>PlantStore.DeleteItemAsync</c> fa sullo store: un arco senza un estremo e' una
    /// giunzione orfana, che e' proprio quello che l'importatore interno (non pubblicato) scarta.
    /// </summary>
    private static void ForgetItem(Guid itemId)
    {
        PathDevices.Instance.Remove(itemId);
        PathJunctions.Instance.RemoveAll(j => j.SourceId == itemId || j.DestinationId == itemId);
    }

    /// <summary>
    /// Le giunzioni sullo store, non dalla memoria (a differenza di <see cref="Junctions"/>): la usa
    /// <c>GET /plant/graph</c> (correzione compito 12, Importante 8 della revisione), che deve restare
    /// un backup fedele anche in uno stato "store pieno, memoria non ancora caricata" che
    /// <see cref="Junctions"/> non vedrebbe - lo stesso stato che <c>DBInfo.LoadJunctions</c> puo'
    /// raggiungere senza che l'host se ne accorga, perche' cattura ogni eccezione senza impostare
    /// <c>Navigator.LoadError</c>.
    /// </summary>
    public static Task<List<PlantJunction>> JunctionsFromStoreAsync() => StoreAccess.ReadAsync(PlantStore.LoadJunctionsAsync);

    public static Task<List<DoorMapping>> DoorMappingsAsync() =>
        StoreAccess.ReadAsync(c => c.DoorMappings.AsNoTracking().ToListAsync());

    /// <summary>
    /// Tutti i dati configurabili del backup letti dallo stesso snapshot del database. Una
    /// sostituzione concorrente può comparire interamente prima o interamente dopo, mai metà per
    /// ciascuna versione.
    /// </summary>
    public static Task<PlantBackupSnapshot> ReadPlantBackupAsync() => ReadPlantBackupCoreAsync();

    internal static Task<PlantBackupSnapshot> ReadPlantBackupCoreAsync(Func<Task>? afterItemsRead = null) =>
        StoreAccess.ReadAsync(async context =>
        {
            await context.Database.OpenConnectionAsync();
            await using var transaction = ((SqliteConnection)context.Database.GetDbConnection())
                .BeginTransaction(deferred: true);
            await context.Database.UseTransactionAsync(transaction);

            var items = await context.PlantItems.AsNoTracking()
                .OrderBy(item => item.Asi).ThenBy(item => item.Name).ToListAsync();
            if (afterItemsRead is not null) await afterItemsRead();
            var geometries = await PlantStore.LoadGeometriesAsync(context);
            var junctions = await PlantStore.LoadJunctionsAsync(context);
            var doorMappings = await context.DoorMappings.AsNoTracking().ToListAsync();
            var decoration = await DecorationStore.GetAsync(context);
            var plcConnections = await context.PlcConnections.AsNoTracking()
                .OrderBy(connection => connection.Id).ToListAsync();

            await transaction.CommitAsync();
            return new PlantBackupSnapshot(items, geometries, junctions, doorMappings, decoration,
                plcConnections);
        });

    /// <summary>
    /// Sostituisce la mappa di ogni dispositivo presente nell'elenco; i dispositivi assenti
    /// dall'elenco restano come sono. Restituisce gli identificativi che non sono dispositivi
    /// dell'impianto: se l'elenco non e' vuoto <b>nessuna</b> mappa e' stata scritta. Rifiutata in Run.
    /// </summary>
    /// <remarks>
    /// Il rifiuto in blocco e' quello di <see cref="SetItemGeometriesAsync"/>, e qui serve a una
    /// regola in piu': una riga su un dispositivo che non c'e' e' una riga orfana, e da quando
    /// <c>DoorMapping.ItemId</c> ha una chiave esterna verso <c>PlantItems</c> il database la
    /// rifiuterebbe comunque - ma con un errore di vincolo a meta' del giro, invece che con un elenco
    /// di identificativi che chi ha chiamato puo' leggere.
    /// </remarks>
    public static async Task<IReadOnlyList<Guid>> ReplaceDoorMappingsAsync(IReadOnlyCollection<DoorMapping> mappings)
    {
        return await MutatePlantAsync(async () =>
        {
            RequireNotRunning("mappature porte");
            IReadOnlyList<Guid> missing = [];
            await StoreAccess.WriteAsync(async context =>
            {
                missing = await PlantStore.MissingItemIdsAsync(context, mappings.Select(m => m.ItemId).Distinct().ToList());
                if (missing.Count > 0) return;
                foreach (var group in mappings.GroupBy(m => m.ItemId))
                    await DoorMappingStore.ReplaceForItemAsync(context, group.Key, group.ToList());
            });
            return missing;
        });
    }

    /// <summary>
    /// Svuota la mappa porte di un dispositivo: nessuna riga, non una riga vuota (che
    /// <see cref="DoorMappingStore.ReplaceForItemAsync"/> scriverebbe se gli si passasse una
    /// mappatura fittizia). Falso se il dispositivo non c'e'. Rifiutata in Run.
    /// </summary>
    public static async Task<bool> ClearDoorMappingsAsync(Guid itemId)
    {
        return await MutatePlantAsync(async () =>
        {
            RequireNotRunning("mappature porte");
            if (!await ItemIsOnTheStoreAsync(itemId)) return false;
            await StoreAccess.WriteAsync(c => DoorMappingStore.ReplaceForItemAsync(c, itemId, []));
            return true;
        });
    }

    /// <summary>Collega una porta alla cella di un ricollocatore. Falso se il dispositivo non c'e'. Rifiutata in Run.</summary>
    public static async Task<bool> LinkBufferCellAsync(Guid itemId, string doorCode, decimal cellId)
    {
        return await MutatePlantAsync(async () =>
        {
            RequireNotRunning("collegamento cella");
            if (!await ItemIsOnTheStoreAsync(itemId)) return false;
            await PlantPersistence.LinkBufferCellAsync(itemId, doorCode, cellId);
            return true;
        });
    }

    /// <summary>
    /// Il dispositivo esiste <b>sullo store</b>: e' il controllo che precede una scrittura sulla
    /// mappa porte, ed e' volutamente piu' stretto di <see cref="ItemExistsAsync"/>.
    /// </summary>
    /// <remarks>
    /// La chiave esterna di <c>DoorMappings.ItemId</c> guarda <c>PlantItems</c>, non
    /// <c>PathDevices</c>: un dispositivo che stesse solo in memoria passerebbe il controllo piu'
    /// largo, la rotta risponderebbe <c>204</c>, e il vincolo rifiuterebbe la riga a meta' del giro.
    /// Le due definizioni restano quindi diverse perche' rispondono a due domande diverse - «il
    /// database accettera' questa riga» contro «l'impianto ha questo dispositivo» - e la seconda,
    /// usata da <see cref="AddJunctionAsync"/>, deve restare la piu' larga per non rifiutare
    /// giunzioni buone su un host che non ha ancora caricato. Passa dalla stessa
    /// <see cref="PlantStore.MissingItemIdsAsync"/> della PUT d'insieme, cosi' «esiste per la mappa
    /// porte» ha una definizione sola.
    /// </remarks>
    private static async Task<bool> ItemIsOnTheStoreAsync(Guid itemId) =>
        (await StoreAccess.ReadAsync(c => PlantStore.MissingItemIdsAsync(c, new[] { itemId }))).Count == 0;

    /// <summary>Le celle servite da un magazzino automatico con ricollocatore: la CTE che le procedure del WMS facevano su ITEMS_MAP e NAVIGATOR_ITEMS.</summary>
    public static Task<List<AutomaticCellInfo>> AutomaticCellsAsync() =>
        StoreAccess.ReadAsync(c => (
            from mapping in c.DoorMappings.AsNoTracking()
            join item in c.PlantItems.AsNoTracking() on mapping.ItemId equals item.ItemId
            where item.RelocateItemId != null
            select new AutomaticCellInfo(mapping.CellId, item.ItemId, item.RelocateItemId!.Value)).Distinct().ToListAsync());

    /// <summary>
    /// La decorazione del disegno: scritte e riquadri liberi dell'operatore. Il vuoto se
    /// l'impianto non ne ha mai avuta una - il caso normale, non un guasto.
    /// </summary>
    public static Task<string> PlantDecorationAsync() => StoreAccess.ReadAsync(DecorationStore.GetAsync);

    /// <summary>Sostituisce la decorazione del disegno. Rifiutata in Run, come ogni altra scrittura sull'impianto (decisione del 2026-09-15).</summary>
    public static Task SetPlantDecorationAsync(string xml)
    {
        return MutatePlantAsync(async () =>
        {
            RequireNotRunning("decorazione del disegno");
            await StoreAccess.WriteAsync(c => DecorationStore.SetAsync(c, xml));
        });
    }

    /// <summary>
    /// La guardia delle scritture dell'impianto: rifiuta Run e, dalla decisione del proprietario del
    /// 27/9 (D2), anche Recover - il caricamento dell'impianto, che finisce da solo in Stop - e Dispose,
    /// il motore che si spegne. Pause resta permessa (decisione del 15/9). Il dettaglio nomina lo stato.
    /// </summary>
    private static void RequireNotRunning(string what)
    {
        var status = Navigator.NavStatus;
        if (status is NavigatorStatusTypes.Run or NavigatorStatusTypes.Recover or NavigatorStatusTypes.Dispose)
            throw new EngineBusyException(
                $"{what}: il motore è in {status}, e l'impianto si cambia solo a motore fermo (Stop) o in pausa (Pause)");
    }

    /// <summary>
    /// Perche' la modalita' di connessione (di tutto l'impianto, o di un dispositivo con la sua
    /// connessione PLC) aspetta Stop (decisione del proprietario del 27/9, D1).
    /// </summary>
    private const string ConnectionWaitsForStop =
        "la modalità di connessione e la connessione PLC dei dispositivi si cambiano solo a motore fermo (Stop): " +
        "il motore assegna simulatore o PLC a un dispositivo solo quando apre le connessioni, e la ripresa da Pause non le riapre";

    /// <summary>
    /// Perche' un impianto si importa e un dispositivo nasce solo a motore fermo (decisione del
    /// proprietario del 27/9 sulla Pause): l'importazione ricostruisce i dispositivi in memoria
    /// (<see cref="PublishGraphMemory"/>) e la creazione ne fa nascere uno
    /// (<see cref="WriteItemConfigurationAsync"/>) SENZA connessione; il motore assegna simulatore o
    /// PLC solo quando apre le connessioni, la ripresa da Pause non le riapre
    /// (<c>ExecutableCommands.StartNavigator</c>), e i comandi del dispositivo passano da
    /// <c>DeviceConnection?.</c>: dopo la ripresa non arriverebbero a niente. Rovescia il rilassamento del
    /// compito 4c (importazione in Pause con le connessioni dell'host).
    /// </summary>
    private const string NewDevicesWaitForStop =
        "i dispositivi nuovi nascono senza connessione, e il motore gliela assegna solo quando apre le connessioni: " +
        "la ripresa da Pause non le riapre";

    /// <summary>
    /// La guardia delle connessioni PLC (compito 4b della console, decisione del proprietario del
    /// 26/9): piu' severa di <see cref="RequireNotRunning"/>, rifiuta ogni stato che non sia
    /// <see cref="NavigatorStatusTypes.Stop"/>, con la stessa eccezione (il <c>409</c> dell'host).
    /// </summary>
    /// <remarks>
    /// Le connessioni del driver nascono solo al caricamento dell'impianto
    /// (<c>DeviceConnections.Initialize</c>, chiamata da <c>Navigator.LoadNavigatorData</c>), e da li'
    /// ogni dispositivo tiene l'oggetto della sua. In <b>Run</b> il motore le usa. In <b>Pause</b>
    /// restano aperte (<c>ExecutableCommands.PauseNavigator</c> cambia solo lo stato) e la ripresa non
    /// le riapre (<c>StartNavigator</c> salta <c>OpenDeviceConnections</c> da Pause): una connessione
    /// tolta adesso resta in mano ai suoi dispositivi, e i loro comandi falliscono appena serve un nuovo
    /// blocco di identificativi (<c>PlcSettingsStore.ReserveCommandIdBlock</c>). In <b>Recover</b>
    /// il caricamento dell'impianto e' a meta': appena torna in Stop costruisce le connessioni leggendo
    /// proprio questa tabella (<c>LoadNavigatorData</c> mette Stop e subito dopo chiama
    /// <c>Initialize</c>). In <b>Dispose</b> il motore si sta spegnendo. Solo in Stop una
    /// sostituzione non tocca connessioni in uso - e anche li' un indirizzo o una porta cambiati
    /// valgono solo al prossimo caricamento dell'impianto.
    /// <para>
    /// Come per <see cref="RequireNotRunning"/>, il controllo corre sotto il cancello delle scritture
    /// dell'impianto ma l'avvio del motore non lo prende: un avvio che arriva fra il controllo e la
    /// scrittura non viene visto. E' la stessa finestra di ogni altra scrittura dell'impianto. Il
    /// caricamento dell'impianto invece, dal 27/9 (D2), passa dal cancello anche quando non lo chiede
    /// una scrittura (l'avvio dell'host, il ricaricamento chiesto con un comando:
    /// <see cref="ReloadUnderGateAsync"/>), e non si incrocia piu' con il controllo.
    /// </para>
    /// </remarks>
    internal static void RequireStopped(string what, string why = "le connessioni PLC si cambiano solo a motore fermo (Stop)")
    {
        if (Navigator.NavStatus != NavigatorStatusTypes.Stop)
            throw new EngineBusyException($"{what}: il motore è in {Navigator.NavStatus}, e {why}");
    }

    /// <summary>Le configurazioni dallo store, non dalla memoria: l'Editor le legge anche quando l'host non ha ancora caricato un diagramma.</summary>
    public static Task<List<PlantItem>> ItemConfigurationsAsync() =>
        StoreAccess.ReadAsync(c => c.PlantItems.AsNoTracking().OrderBy(x => x.Asi).ThenBy(x => x.Name).ToListAsync());

    public static Task<PlantItem?> ItemConfigurationAsync(Guid itemId) =>
        StoreAccess.ReadAsync(c => PlantStore.FindItemAsync(c, itemId));

    /// <summary>La geometria di tutti i dispositivi, per identita'.</summary>
    public static Task<Dictionary<Guid, VersionedItemGeometry>> ItemGeometriesAsync() =>
        StoreAccess.ReadAsync(PlantStore.LoadGeometriesAsync);

    /// <summary>
    /// Colloca un dispositivo soltanto se la sua versione geometrica coincide con quella letta dal
    /// chiamante. Usa la stessa operazione atomica della forma di gruppo ed e' rifiutata in Run.
    /// </summary>
    /// <remarks>
    /// Fino a questo compito questa facciata e <see cref="SetItemGeometriesAsync"/> non chiamavano
    /// <see cref="RequireNotRunning"/>, a differenza delle loro sorelle: la cancellazione del
    /// dispositivo e della giunzione, la scrittura della configurazione
    /// (<see cref="UpsertItemConfigurationAsync"/>), lo svuotamento dell'impianto
    /// (<see cref="RemoveAllItemsAsync"/>), la modalita' di connessione
    /// (<see cref="SetConnectionTypeForAllAsync"/>), l'aggiunta e la priorita' della giunzione
    /// (<see cref="AddJunctionAsync"/>, <see cref="SetJunctionPriorityAsync"/>) la chiedevano gia'.
    /// La ragione dell'asimmetria era solo probabile - il motore non legge mai la geometria, e' un
    /// dato di chi disegna - ma non era scritta da nessuna parte, ed era la sola ragione per cui
    /// reggeva. La decisione del proprietario del progetto (l'impianto in sola lettura mentre il
    /// motore e' in Run) l'ha chiusa: da qui in poi la guardia c'e' anche su questa facciata, su
    /// <see cref="SetItemGeometriesAsync"/> e sulle tre rotte delle mappature porte
    /// (<see cref="ReplaceDoorMappingsAsync"/>, <see cref="ClearDoorMappingsAsync"/>,
    /// <see cref="LinkBufferCellAsync"/>).
    /// <para>
    /// Resta un solo controesempio, dichiarato e non un'omissione:
    /// <see cref="SaveDiagramAndReloadAsync"/> (sopra, <c>PUT /plant/diagram</c>) non chiama
    /// <see cref="RequireNotRunning"/>. Rifa' per intero dispositivi e giunzioni via
    /// <see cref="ExecutableCommands.ReloadRouteDiagram"/> dopo aver fermato il navigatore se era in
    /// esecuzione e averlo poi riavviato - una scelta diversa dal rifiuto, non una svista, e non la
    /// si tocca qui.
    /// </para>
    /// </remarks>
    public static Task<GeometryWriteOutcome> SetItemGeometryAsync(GeometryWrite request) =>
        SetItemGeometriesAsync([request]);

    /// <summary>
    /// Colloca un gruppo in blocco: mancanti e versioni obsolete lasciano invariato l'intero
    /// insieme. Rifiutata in Run come le scritture geometriche preesistenti.
    /// </summary>
    public static async Task<GeometryWriteOutcome> SetItemGeometriesAsync(
        IReadOnlyCollection<GeometryWrite> requests)
    {
        return await MutatePlantAsync(async () =>
        {
            RequireNotRunning("posizione dei dispositivi");
            var outcome = GeometryWriteOutcome.Success([]);
            await StoreAccess.WriteAsync(async c =>
                outcome = await PlantStore.SetGeometriesAsync(c, requests));
            return outcome;
        });
    }

    /// <summary>
    /// Il dispositivo esiste: in memoria, o sullo store.
    /// </summary>
    /// <remarks>
    /// Tutti e due, perche' i due ingressi si riempiono in momenti diversi. L'Editor crea il
    /// dispositivo e subito dopo la giunzione, e in quel momento il dispositivo e' in memoria;
    /// un host appena avviato che non ha ancora caricato l'impianto ha lo store pieno e la
    /// memoria vuota. Chiedere a uno solo dei due rifiuterebbe giunzioni buone.
    /// <para>
    /// Le rotte della mappa porte <b>non</b> usano questa: si veda
    /// <see cref="ItemIsOnTheStoreAsync"/>, che e' piu' stretta apposta.
    /// </para>
    /// </remarks>
    public static async Task<bool> ItemExistsAsync(Guid itemId) =>
        PathDevices.Instance[itemId] != null || await ItemConfigurationAsync(itemId) != null;

    /// <summary>
    /// <b>La porta di servizio che resuscita</b>, riservata a <c>PUT /plant/graph</c>
    /// (<see cref="ReplaceGraphAsync"/>) e alla semina delle prove. Scrive la configurazione di un
    /// dispositivo: se e' in memoria la applica li' (una scrittura sola, stato dal vivo intatto); se
    /// non c'e', <b>lo crea</b> (<c>PathDevices.Add</c> legge o crea la riga) e poi applica.
    /// Rifiutata in Run, Recover e Dispose; e un dispositivo che non c'e' lo crea solo a motore fermo
    /// (Stop), come <see cref="CreateItemConfigurationAsync"/> (decisione del 27/9 sulla Pause, si veda
    /// <see cref="NewDevicesWaitForStop"/>).
    /// </summary>
    /// <remarks>
    /// <b>Non e' la facciata da cui passa una rotta nuova.</b> Le altre due -
    /// <see cref="CreateItemConfigurationAsync"/> e <see cref="UpdateItemConfigurationAsync"/> -
    /// dicono al chiamante se l'identificativo era libero o occupato; questa no, ed e' proprio la
    /// distinzione che il lotto del 404 e' venuto a introdurre. Resta perche' la sostituzione
    /// dell'impianto intero non ha un «prima» da rispettare: svuota e riscrive. Chi ci agganciasse
    /// una rotta di modifica rimetterebbe in piedi l'upsert silenzioso, e <b>nessuna prova a questo
    /// livello lo fermerebbe</b> - il contratto e' difeso una rotta piu' in su, in
    /// <c>Wcs.Host.Test/WritingOnAMissingDeviceTest</c>.
    /// <para>
    /// La riga la scrive questa facciata, non <c>ApplyConfiguration</c>: una sola scrittura, attesa,
    /// che solleva se fallisce. Prima la scrittura vera partiva da <c>ORM.AutomationDevice.Update</c>
    /// (<c>EnqueueOrWait</c>, non attendibile) e qui si attendeva una scrittura <b>vuota</b> come
    /// barriera: un lotto fallito rientrava riga per riga, la vuota passava, e l'host rispondeva
    /// <c>204</c> su una configurazione che non era mai arrivata sul database.
    /// </para>
    /// </remarks>
    public static async Task UpsertItemConfigurationAsync(PlantItem config)
    {
        await MutatePlantAsync(async () =>
        {
            RequireNotRunning("configurazione del dispositivo");
            if (!await ItemExistsAsync(config.ItemId))
                RequireStopped("creazione del dispositivo", "un dispositivo si crea solo a motore fermo (Stop): " + NewDevicesWaitForStop);
            await WriteItemConfigurationAsync(config);
        });
    }

    /// <summary>
    /// Crea un dispositivo con l'identificativo scelto da chi chiama. Falso se quell'identificativo
    /// e' gia' un dispositivo dell'impianto: e' il <c>409</c> di <c>POST /plant/items</c>. Solo a
    /// motore fermo (Stop; decisione del 27/9, si veda <see cref="NewDevicesWaitForStop"/>).
    /// </summary>
    /// <remarks>
    /// La creazione ha una facciata sua da quando <see cref="UpdateItemConfigurationAsync"/> rifiuta
    /// il dispositivo assente: prima nasceva dallo stesso <c>upsert</c> che serviva a modificare, e
    /// nessun chiamante poteva distinguere «aggiorno» da «resuscito quello che qualcun altro ha
    /// appena cancellato».
    /// <para>
    /// Il controllo e la scrittura non sono una transazione sola: due creazioni dello stesso
    /// identificativo partite insieme potrebbero passare tutte e due il controllo. Non e' il caso che
    /// questa facciata difende - il Guid lo sceglie il client con <c>Guid.NewGuid()</c>, e chi lo
    /// ripete lo sta ripetendo apposta - ma la chiave primaria di <c>PlantItems</c> resta l'ultima
    /// parola: la seconda scrittura sovrascrive la riga, non ne aggiunge una seconda.
    /// </para>
    /// </remarks>
    public static async Task<bool> CreateItemConfigurationAsync(PlantItem config)
    {
        return await MutatePlantAsync(async () =>
        {
            // Solo in Stop (decisione del proprietario del 27/9 sulla Pause): si veda NewDevicesWaitForStop.
            RequireStopped("creazione del dispositivo", "un dispositivo si crea solo a motore fermo (Stop): " + NewDevicesWaitForStop);
            if (await ItemExistsAsync(config.ItemId)) return false;
            await WriteItemConfigurationAsync(config);
            return true;
        });
    }

    /// <summary>
    /// Scrive la configurazione di un dispositivo che esiste gia'. Falso se non esiste: e' il
    /// <c>404</c> di <c>PUT /plant/items/{id}/config</c>, la rotta che fino a questo lotto lo
    /// ricreava in silenzio. Rifiutata in Run.
    /// </summary>
    public static async Task<bool> UpdateItemConfigurationAsync(PlantItem config)
    {
        return await MutatePlantAsync(async () =>
        {
            RequireNotRunning("configurazione del dispositivo");
            if (!await ItemExistsAsync(config.ItemId)) return false;
            if (await ChangesTheConnectionAsync(config))
                RequireStopped("connessione del dispositivo", ConnectionWaitsForStop);
            await WriteItemConfigurationAsync(config);
            return true;
        });
    }

    /// <summary>
    /// Vero se la configurazione proposta cambia il tipo di connessione o la connessione PLC del
    /// dispositivo (D1): confrontata con il dispositivo in memoria, che e' quello a cui il motore ha
    /// assegnato la connessione, o con la riga dello store se in memoria non c'e'.
    /// </summary>
    private static async Task<bool> ChangesTheConnectionAsync(PlantItem config)
    {
        if (PathDevices.Instance[config.ItemId] is { } device)
            return (short)device.ConnectionType != config.ConnectionType || device.DeviceConnectionId != config.DeviceConnectionId;
        return await ItemConfigurationAsync(config.ItemId) is { } stored
               && (stored.ConnectionType != config.ConnectionType || stored.DeviceConnectionId != config.DeviceConnectionId);
    }

    /// <summary>
    /// La scrittura vera, condivisa dalle tre facciate sopra: applica la configurazione al
    /// dispositivo in memoria (creandolo li' se non c'e' ancora, come <c>PathDevices.Add</c> ha
    /// sempre fatto) e salva l'istantanea sullo store. Non chiama <see cref="RequireNotRunning"/> -
    /// lo fa chi la chiama, prima di qualunque altro controllo, cosi' che il motore in marcia resti
    /// un <c>409</c> e non diventi un <c>404</c>.
    /// </summary>
    private static async Task WriteItemConfigurationAsync(PlantItem config)
    {
        var device = PathDevices.Instance[config.ItemId] ?? PathDevices.Instance.Add(config.ItemId)
            ?? throw new InvalidOperationException($"Dispositivo {config.ItemId} non creabile");
        device.ApplyConfiguration(config);
        var snapshot = PlantStore.Snapshot(device);
        await StoreAccess.WriteAsync(c => PlantStore.UpsertItemAsync(c, snapshot));
    }

    /// <summary>
    /// Svuota l'impianto: sullo store le righe dei dispositivi, delle giunzioni, della mappa
    /// porte, delle missioni e della coda di estrazione; solo se la scrittura va a buon fine,
    /// dispositivi e giunzioni in memoria e le missioni attive. Rifiutata in Run.
    /// </summary>
    /// <remarks>
    /// E' quello che «Nuovo diagramma» dell'Editor faceva quando il motore girava nel suo stesso
    /// processo. <see cref="KeepItemsAsync"/> non puo' sostituirla: con una lista vuota non tocca
    /// nulla, per non scambiare «non ho niente da tenere» con «cancella tutto». Qui la cancellazione
    /// e' chiesta esplicitamente, ed e' l'unica rotta che svuota l'impianto.
    /// <para>
    /// L'ordine e' store-poi-memoria: prima si svuotava la memoria e poi lo store, e un errore
    /// nella scrittura lasciava la memoria gia' vuota con il database ancora pieno -- il prossimo
    /// avvio ripartiva da un impianto che l'host non aveva piu'. Le righe di <c>Missions</c> ed
    /// <c>ExtractionQueue</c> restavano orfane anche a scrittura riuscita: <c>Clear(false)</c>
    /// toglie le missioni solo dalla collezione in memoria, mai dal database.
    /// </para>
    /// </remarks>
    public static async Task RemoveAllItemsAsync()
    {
        await MutatePlantAsync(async () =>
        {
            RequireNotRunning("svuotamento dell'impianto");
            await StoreAccess.WriteAsync(async c =>
            {
                c.ExtractionQueue.RemoveRange(await c.ExtractionQueue.ToListAsync());
                c.Missions.RemoveRange(await c.Missions.ToListAsync());
                c.PlantJunctions.RemoveRange(await c.PlantJunctions.ToListAsync());
                c.DoorMappings.RemoveRange(await c.DoorMappings.ToListAsync());
                c.PlantItems.RemoveRange(await c.PlantItems.ToListAsync());
            });
            PathDevices.Instance.Clear();
            PathJunctions.Instance.Clear();
            await ActiveMissions.Instance.Clear(false);
        });
    }

    /// <summary>
    /// Sostituisce l'intero impianto con quello dato: dispositivi, geometrie, giunzioni, mappature
    /// porte e, quando fornite, decorazione e configurazioni PLC. Solo a motore fermo (Stop), qualunque
    /// file (decisione del proprietario del 27/9 sulla Pause, che rovescia il rilassamento del compito
    /// 4c: si veda <see cref="NewDevicesWaitForStop"/>).
    /// </summary>
    /// <remarks>
    /// Una sola scrittura esclusiva sincronizza tutte le righe, senza cancellare missioni o coda.
    /// La memoria viene ricostruita soltanto dopo il commit riuscito.
    /// </remarks>
    /// <param name="decoration">
    /// Il disegno libero da rimettere, oppure <b>nullo</b> per non toccare quello che c'è. Non e'
    /// un valore come gli altri quattro, che sono liste: il nullo qui non vuol dire «vuota», vuol
    /// dire «il chiamante non parla del disegno» — la stringa vuota, invece, lo parla e lo svuota.
    /// Prima del 2026-09-19 questa facciata non toccava affatto la decorazione, quindi dopo
    /// un'importazione il disegno libero restava quello di prima in mezzo a dispositivi svuotati e
    /// ricreati: il nullo conserva quel comportamento esatto per chi non passa niente (i file
    /// esportati prima che il campo esistesse), e il non nullo è il ripristino vero.
    /// </param>
    /// <param name="plcConnections">
    /// Le configurazioni PLC da sostituire nella stessa transazione, oppure nullo per conservarle.
    /// Lo stato operativo degli identificativi che restano viene preservato.
    /// </param>
    public static Task ReplaceGraphAsync(
        IReadOnlyList<PlantItem> items,
        IReadOnlyList<(Guid ItemId, ItemGeometry Geometry)> geometries,
        IReadOnlyList<(Guid JunctionId, Guid SourceItemId, Guid DestinationItemId, int Priority)> junctions,
        IReadOnlyList<DoorMapping> doorMappings,
        string? decoration = null,
        IReadOnlyList<PlcConnection>? plcConnections = null) =>
        ReplaceGraphCoreAsync(items, geometries, junctions, doorMappings, decoration,
            ReloadGraphMemoryAsync, plcConnections);

    internal static async Task ReplaceGraphCoreAsync(
        IReadOnlyList<PlantItem> items,
        IReadOnlyList<(Guid ItemId, ItemGeometry Geometry)> geometries,
        IReadOnlyList<(Guid JunctionId, Guid SourceItemId, Guid DestinationItemId, int Priority)> junctions,
        IReadOnlyList<DoorMapping> doorMappings,
        string? decoration,
        Func<Task> reloadMemory,
        IReadOnlyList<PlcConnection>? plcConnections = null)
    {
        await PlantMutationGate.WaitAsync();
        try
        {
        // Solo in Stop, qualunque file (decisione del proprietario del 27/9 sulla Pause): si veda
        // NewDevicesWaitForStop. Prima del lavoro pendente e della validazione.
        RequireStopped("sostituzione dell'impianto", "un impianto si importa solo a motore fermo (Stop): " + NewDevicesWaitForStop);
        if (ActiveMissions.Instance.Count != 0)
            throw new PlantGraphWorkPendingException("Ci sono missioni attive in memoria");
        if (Navigator.StateWriter == null && Navigator.Store == null)
            throw new EngineStoreUnavailableException("Il database del WCS non e' aperto: la scrittura non e' possibile.");

        var itemIds = items.Select(i => i.ItemId).ToHashSet();
        if (itemIds.Contains(Guid.Empty) || itemIds.Count != items.Count)
            throw new ArgumentException("Identificativi dei dispositivi vuoti o duplicati", nameof(items));
        if (plcConnections is { } proposedConnections)
        {
            var plcIds = proposedConnections.Select(connection => connection.Id).ToHashSet();
            if (plcIds.Any(id => id <= 0) || plcIds.Count != proposedConnections.Count)
                throw new ArgumentException("Identificativi delle connessioni PLC non validi o duplicati",
                    nameof(plcConnections));
            if (items.Any(item => item.DeviceConnectionId < 0 ||
                                  item.DeviceConnectionId != decimal.Truncate(item.DeviceConnectionId) ||
                                  item.DeviceConnectionId > long.MaxValue ||
                                  item.DeviceConnectionId > 0 && !plcIds.Contains((long)item.DeviceConnectionId)))
                throw new ArgumentException("Il grafo proposto riferisce connessioni PLC assenti",
                    nameof(items));

        }
        if (geometries.Any(g => !itemIds.Contains(g.ItemId)) ||
            junctions.Any(j => !itemIds.Contains(j.SourceItemId) || !itemIds.Contains(j.DestinationItemId)) ||
            doorMappings.Any(m => !itemIds.Contains(m.ItemId)))
            throw new ArgumentException("Il grafo proposto contiene riferimenti a dispositivi assenti");
        if (junctions.Where(j => j.JunctionId != Guid.Empty).GroupBy(j => j.JunctionId).Any(g => g.Count() > 1))
            throw new ArgumentException("Identificativi delle giunzioni duplicati", nameof(junctions));
        // Una giunzione per coppia ordinata di dispositivi (voce 17 della coda, decisione del
        // proprietario del 28/9): il motore la riconosce dalla coppia origine-destinazione
        // (PlantStore.UpsertJunctionAsync, FindJunctionAsync, AddJunctionAsync) e la ricerca dei
        // percorsi ne farebbe due passi identici. A->B e B->A restano due giunzioni diverse.
        if (junctions.GroupBy(j => (j.SourceItemId, j.DestinationItemId)).FirstOrDefault(g => g.Count() > 1) is { } twice)
        {
            string Named(Guid id) => items.First(i => i.ItemId == id) is { Name: { Length: > 0 } name } ? $"{name} ({id})" : id.ToString();
            throw new ArgumentException(
                $"Due giunzioni collegano {Named(twice.Key.SourceItemId)} a {Named(twice.Key.DestinationItemId)} nello stesso verso",
                nameof(junctions));
        }
        // Una posizione per dispositivo (voce 5 della coda dei lavori): con due, il ciclo di
        // SynchronizeGraphRowsAsync teneva l'ultima senza dirlo. L'host la rifiuta gia' con un 400.
        if (geometries.GroupBy(g => g.ItemId).Any(g => g.Count() > 1))
            throw new ArgumentException("Un dispositivo ha piu' di una posizione", nameof(geometries));

        // Generate legacy identifiers once, so a writer retry uses exactly the same graph.
        var normalizedJunctions = junctions.Select(j => new PlantJunction
        {
            JunctionId = j.JunctionId == Guid.Empty ? Guid.NewGuid() : j.JunctionId,
            SourceItemId = j.SourceItemId, DestinationItemId = j.DestinationItemId, Priority = j.Priority
        }).ToList();
        // Create once outside the retryable writer: every retry represents the same proposed graph.
        var replacementGeometryVersions = items.ToDictionary(item => item.ItemId, _ => Guid.NewGuid());
        string? conflict = null;
        await WcsStateWrites.WriteExclusiveAndWaitAsync(async c =>
        {
            RequireStopped("sostituzione dell'impianto", "un impianto si importa solo a motore fermo (Stop): " + NewDevicesWaitForStop);
            if (ActiveMissions.Instance.Count != 0 || await c.Missions.AnyAsync() || await c.ExtractionQueue.AnyAsync())
            {
                // A normal conflict is not a failed state write and must not enter retry accounting.
                conflict = "Missioni o coda di estrazione non vuote";
                return;
            }
            await SynchronizeGraphRowsAsync(c, items, geometries, replacementGeometryVersions,
                normalizedJunctions, doorMappings, decoration, plcConnections);
        });
        if (conflict is not null) throw new PlantGraphWorkPendingException(conflict);
        try { await reloadMemory(); }
        catch (Exception ex)
        {
            Navigator.NavStatus = NavigatorStatusTypes.Stop;
            Navigator.LoadError = $"Grafo salvato ma non ricaricato: {ex.Message}. Riavviare il motore.";
            throw new EngineNotLoadedException(Navigator.LoadError);
        }
        }
        finally
        {
            PlantMutationGate.Release();
        }
    }

    private static async Task SynchronizeGraphRowsAsync(WcsDbContext c,
        IReadOnlyList<PlantItem> items,
        IReadOnlyList<(Guid ItemId, ItemGeometry Geometry)> geometries,
        IReadOnlyDictionary<Guid, Guid> geometryVersions,
        IReadOnlyList<PlantJunction> junctions,
        IReadOnlyList<DoorMapping> doorMappings, string? decoration,
        IReadOnlyList<PlcConnection>? plcConnections)
    {
        var proposedItems = items.ToDictionary(i => i.ItemId);
        var existingItems = await c.PlantItems.ToDictionaryAsync(i => i.ItemId);
        foreach (var (id, geometry) in geometries)
        {
            var item = proposedItems[id];
            (item.X, item.Y, item.Width, item.Height, item.Rotation) =
                (geometry.X, geometry.Y, geometry.Width, geometry.Height, geometry.Rotation);
        }
        foreach (var item in items)
            item.GeometryVersion = geometryVersions[item.ItemId];
        c.PlantItems.RemoveRange(existingItems.Values.Where(i => !proposedItems.ContainsKey(i.ItemId)));
        foreach (var item in items)
            if (existingItems.TryGetValue(item.ItemId, out var existing)) c.Entry(existing).CurrentValues.SetValues(item);
            else c.PlantItems.Add(item);

        var junctionIds = junctions.Select(j => j.JunctionId).ToHashSet();
        var existingJunctions = await c.PlantJunctions.ToDictionaryAsync(j => j.JunctionId);
        c.PlantJunctions.RemoveRange(existingJunctions.Values.Where(j => !junctionIds.Contains(j.JunctionId)));
        foreach (var junction in junctions)
            if (existingJunctions.TryGetValue(junction.JunctionId, out var existing)) c.Entry(existing).CurrentValues.SetValues(junction);
            else c.PlantJunctions.Add(junction);

        c.DoorMappings.RemoveRange(await c.DoorMappings.ToListAsync());
        c.DoorMappings.AddRange(doorMappings.Select(m => new DoorMapping
        {
            ItemId = m.ItemId, DoorCode = m.DoorCode, CellId = m.CellId, Side = m.Side,
            OperationActive = m.OperationActive, StoringLocked = m.StoringLocked
        }));
        if (decoration is not null)
            await DecorationStore.SetAsync(c, decoration);
        if (plcConnections is not null)
            await PlcSettingsStore.ReplaceConnectionsAsync(c, plcConnections);
    }

    internal sealed record GraphMemorySnapshot(IReadOnlyList<PlantItem> Items, IReadOnlyList<PlantJunction> Junctions);

    internal static async Task<GraphMemorySnapshot> CaptureGraphMemorySnapshotAsync() =>
        await StoreAccess.ReadAsync(async context =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            var items = await context.PlantItems.AsNoTracking().ToListAsync();
            var junctions = await context.PlantJunctions.AsNoTracking().ToListAsync();
            await transaction.CommitAsync();
            return new GraphMemorySnapshot(items, junctions);
        });

    internal static void PublishGraphMemory(GraphMemorySnapshot snapshot)
    {
        PathDevices.Instance.Clear();
        PathJunctions.Instance.Clear();
        foreach (var row in snapshot.Items)
        {
            var device = new AutomationDevice(row.ItemId);
            device.Apply(row);
            PathDevices.Instance.Add(device);
            device.Start();
        }
        foreach (var row in snapshot.Junctions)
        {
            var junction = new AutomationJunction(row.SourceItemId, row.DestinationItemId, hydrateOnly: true);
            PlantStore.Apply(row, junction);
            PathJunctions.Instance.Add(junction);
        }
    }

    private static async Task ReloadGraphMemoryAsync() => PublishGraphMemory(await CaptureGraphMemorySnapshotAsync());

    public static async Task SetConnectionTypeForAllAsync(ConnectionTypes connectionType)
    {
        await MutatePlantAsync(async () =>
        {
            RequireStopped("modalità di connessione", ConnectionWaitsForStop);
            foreach (var device in PathDevices.Instance.List)
                device!.ConnectionType = connectionType;
            await StoreAccess.WriteAsync(async c =>
            {
                foreach (var row in await c.PlantItems.ToListAsync())
                    row.ConnectionType = (short)connectionType;
            });
        });
    }

    public static async Task<AutomationJunction> AddJunctionAsync(Guid sourceItemId, Guid destinationItemId, int priority)
    {
        return await MutatePlantAsync(async () =>
        {
            RequireNotRunning("giunzione");
            var existing = PathJunctions.Instance.FirstOrDefault(j => j.SourceId == sourceItemId && j.DestinationId == destinationItemId);
            if (existing != null) return existing;
            var junction = new AutomationJunction(sourceItemId, destinationItemId) { Priority = priority };
            PathJunctions.Instance.Add(junction);
            await StoreAccess.WriteAsync(c => PlantStore.UpsertJunctionAsync(c, PlantStore.Snapshot(junction)));
            return junction;
        });
    }

    public static async Task<bool> SetJunctionPriorityAsync(Guid junctionId, int priority)
    {
        return await MutatePlantAsync(async () =>
        {
            RequireNotRunning("priorità della giunzione");
            var junction = PathJunctions.Instance.FirstOrDefault(j => j.ItemGuid == junctionId);
            var found = junction != null;
            if (junction != null) junction.Priority = priority;
            await StoreAccess.WriteAsync(async c =>
            {
                var row = await c.PlantJunctions.SingleOrDefaultAsync(j => j.JunctionId == junctionId);
                if (row == null) return;
                found = true;
                row.Priority = priority;
            });
            return found;
        });
    }

    /// <summary>
    /// Quanto uno scrittore a coda ha scritto, quanto gli e' rimasto in mano e quanto ha perso.
    /// </summary>
    /// <param name="Written">Scritture eseguite.</param>
    /// <param name="Pending">Scritture ancora in coda.</param>
    /// <param name="Dropped">Scritture cadute perche' la coda era piena (solo lo storico le lascia cadere).</param>
    /// <param name="Failed">Scritture tentate e non riuscite nemmeno al secondo tentativo: righe perse.</param>
    public sealed record WriterCounters(long Written, int Pending, long Dropped, long Failed);

    /// <summary>
    /// I contatori dei due scrittori a coda del WCS, o <c>null</c> se il motore non e' partito.
    /// </summary>
    /// <remarks>
    /// <c>Dropped</c> e <c>Failed</c> non li leggeva nessuno: una riga di stato che non si e'
    /// lasciata scrivere spariva in silenzio, e il solo segno era una riga di registro. Non c'era
    /// nessuna API, nessun allarme, nessun contatore esposto. Adesso l'host li puo' chiedere:
    /// <c>Failed &gt; 0</c> sullo stato significa che il motore e il suo database non dicono la
    /// stessa cosa.
    /// </remarks>
    public static WriterCounters? StateWriterCounters() => Counters(Navigator.StateWriter);

    /// <summary>Come <see cref="StateWriterCounters"/>, per lo storico.</summary>
    public static WriterCounters? HistoryWriterCounters() => Counters(Navigator.HistoryWriter);

    /// <summary>
    /// Come <see cref="StateWriterCounters"/>, per il registro dei messaggi dei PLC.
    /// </summary>
    /// <remarks>
    /// Fino alla voce <c>D183</c> i telegrammi erano contati insieme allo storico, perche' la coda
    /// era la stessa. Adesso sono contati a parte, ed e' con questi numeri che si misura quanti
    /// telegrammi al minuto arrivino da un impianto vero e se la capacita' della coda basti: la
    /// misura resta da fare, e vuole PLC collegati.
    /// </remarks>
    public static WriterCounters? PlcLogWriterCounters() => Counters(Navigator.PlcLogWriter);

    private static WriterCounters? Counters<TWrite>(QueuedDatabaseWriter<TWrite>? writer) =>
        writer == null ? null : new WriterCounters(writer.Written, writer.Pending, writer.Dropped, writer.Failed);
}
