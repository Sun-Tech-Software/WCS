> **Historical — not current.** Internal design notes of Sun Tech, written in Italian on 2026-10-11. They describe the system as it was then and are kept for reference only; the current documentation is in [docs/](../../../README.md).

# Wcs.Engine — architettura

> **Nota per il repository pubblico.** Questo documento e' stato scritto per il repository interno. Non sono pubblicati: l'Editor WinForms (`Wcs.Editor`), `Wcs.Viewer.Test`, il driver PLC di Sun Tech con il suo protocollo e il suo simulatore (proprietari) e i documenti che lo descrivono (fra questi la mappa dell'area `WCS/doc/README.md` e il registro del debito tecnico), l'importatore che migra gli impianti dal database del gestionale di Sun Tech, gli script della demo, le specifiche, i piani, la coda dei lavori e i rapporti delle revisioni, e il monorepo del gestionale (i riferimenti `Kazam:`). I collegamenti a quelle parti sono resi come testo e segnati «(non pubblicato)». `Common` qui e' una cartella normale, non un submodule.

## Indice

- [Scopo](#scopo)
- [Dipendenze](#dipendenze)
- [Dal segnale al comando](#dal-segnale-al-comando-come-nasce-e-si-esegue-un-movimento)
- [Il modello: grafo, missioni, step](#il-modello-grafo-missioni-step)
- [Avvio](#avvio)
- [I tre loop](#i-tre-loop)
- [La costruzione del percorso](#la-costruzione-del-percorso)
- [Il dialogo con il campo](#il-dialogo-con-il-campo)
- [Il dispositivo](#il-dispositivo)
- [Stato e proprietà](#stato-e-proprietà)
- [Punti di attrito](#punti-di-attrito)
  - [Trasversali: sicurezza e compilazione](#trasversali-sicurezza-e-compilazione)
  - [Il ciclo di vita del motore e i tre loop](#il-ciclo-di-vita-del-motore-e-i-tre-loop)
  - [I comandi in ingresso e il dialogo col campo](#i-comandi-in-ingresso-e-il-dialogo-col-campo)
  - [Il dispositivo, i manager e le strategie](#il-dispositivo-i-manager-e-le-strategie)

## Scopo

`Wcs.Engine` è il motore del WCS: decide **dove deve andare ogni UDC e per quali tappe
intermedie**, e tiene in memoria lo stato di tutto l'impianto (dispositivi, giunzioni,
missioni, step). Non decide *cosa* muovere — quello arriva dal WMS attraverso il database e
i comandi in tabella — e non parla direttamente col PLC: delega a `IConnection`
(si veda architettura.md (non pubblicato)).
Non ha interfaccia utente: la ospita `Wcs.Host`, l'eseguibile di produzione, che referenzia questo
progetto in-process ed espone l'API. `Wcs.Editor` è un client dell'host e non ha alcun legame di
progetto con il motore.

È il componente più grande dell'area: 120 file `.cs` sorgente e 35.317 righe (misurate
escludendo `obj/`, `bin/` e `.vs/`; 18 di quei file, per 4.847 righe, sono le migrazioni EF
generate in `Persistence/Migrations/`). Il nucleo è `Navigator.cs` (1637), i sei file parziali
di `AutomationDevice` (1410 + 1164 + 909 + 403 + 389 + 258) e i due di `PathMission`
(976 + 1346); `PathBuilder.cs` ha 240 righe, con la ricerca in
`Routing/` (52 + 19 + 176). Questo documento copre il
modello, l'avvio, i tre cicli concorrenti, la costruzione del percorso, il dialogo con il
campo, il dispositivo con i suoi manager e le sue strategie, e lo stato di runtime; si chiude
con il registro dei punti di attrito trovati durante la stesura.

## Dipendenze

**In uscita.** Sette `ProjectReference`
(Wcs.Engine.csproj:36-46 «Helper.csproj»). Nessun progetto sotto `WCS/` referenzia più i progetti del gestionale: l'ultimo residuo era `WMS.Services.Interface`, tenuto in vita solo da `Enumeratori.TipoArea.CondizioneCapacita`, il frammento SQL che `DbInfo.GetAutomaticAreasByWarehouseId` interpolava nella propria query. La condizione era ridondante — il gestionale la riapplica già in AND dentro `getDtCELLE_DISPONIBILI` — ed è stata tolta insieme al riferimento; dal 2026-09-12 quella lettura non è più una query ma l'incrocio in memoria fra la topologia chiesta al confine e le celle mappate del database del WCS (LocalQueries.cs:70 «internal static async Task<List<decimal>> GetAutomaticAreasByWarehouseId(decimal warehouseId)»). Con lo stesso compito è uscito anche `MsSqlHelper`, e il motore **non apre più SQL Server del gestionale**:

| Riferimento | Perché |
|---|---|
| `Log.Interface` | `IWriter` e `LogEntryType`: il motore scrive tutti i log tramite l'unico writer statico `_eventLogWriter` (Navigator.cs:118 «private static IWriter? _eventLogWriter;»), iniettato dall'esterno con `SetEventLogWriter` (Navigator.cs:325 «public static void SetEventLogWriter(IWriter value)») |
| `Helper` | `Helper.Converters.GetValue<T>`/`GetValueOrThrow<T>` per la lettura tipizzata dei `DataRow`. Fino al 2026-09-12 arrivava di rimbalzo da `MsSqlHelper`; con `MsSqlHelper` fuori, il motore lo dichiara (Wcs.Engine.csproj:36 «Helper.csproj») |
| `Wcs.Wms.Contracts` | `IWarehouse`, i DTO e le rotte: il contratto verso il gestionale, ed è da qui che passa tutto quello che il motore chiede di là. Ha preso il posto di `MsSqlHelper`, cioè della connessione a SQL Server (Wcs.Engine.csproj:46 «Wcs.Wms.Contracts.csproj») |
| `Wcs.Common` | il vocabolario condiviso: i 21 enum e gli `EventArgs`. Descritto in [architettura.md](../Wcs.Common/architettura.md), qui non lo si ripete |
| `Wcs.Connection.Interface` | `IConnection`, `RunAsi`, `DataAsi` e i sei eventi dal campo. Il motore li consuma in `Navigator` (i sei handler Navigator.cs:362 «internal static void AsiEvent(object sender, AsiEventEventArgs? e)», :387 «internal static void AsiMovementIsCompleted(object sender», :378 «internal static void AsiRequestsLuData(object sender», :475 «internal static void AsiSendLuData(object sender», :857 «catch (Exception ex)», :928 «catch (Exception ex)») e in `DeviceConnections` |
| `Wcs.Connection.Test` | il simulatore, portato in scope dallo `using Wcs.Connection.Test` di DeviceConnections.cs:5 «using Wcs.Connection.Interface;» e istanziato come singleton da `GetTestConnection()` (DeviceConnections.cs:355 «LogEntryType.Error);») |

Due `PackageReference` (Wcs.Engine.csproj:26-30 «<PackageReference Include="Microsoft.EntityFrameworkCore.Design"»):
`Microsoft.EntityFrameworkCore.Design` e `Microsoft.EntityFrameworkCore.Sqlite`, cioè la
persistenza del motore (si veda `WCS/doc/database-del-wcs.md`). Il terzo,
`System.Security.Cryptography.Xml`, è stato tolto l'11/9 sera perché non aveva usi e non era un pin
(voce `A1` del registro).
Gli eventi verso l'esterno passano da `IEngineEventSink` (si veda «Eventi verso l'host»).

**In entrata.** Un solo progetto in tutto il repository dichiara un `ProjectReference` verso
`Wcs.Engine.csproj`: `Wcs.Editor`
(Wcs.Editor.csproj:22 «</ItemGroup>» (non pubblicato)).
`Wcs.Viewer` **non** dipende da questo progetto: parla con l'host di
produzione, `Wcs.Host`, via HTTP/JSON e hub SignalR (`Wcs.Api.Client`), non con il motore
direttamente.

## Dal segnale al comando: come nasce e si esegue un movimento

Il ciclo canonico di un'automazione di magazzino, e come questo motore lo realizza. Ogni passaggio
rimanda al punto di codice che lo esegue.

1. **Una UDC viene posta su un elemento.** L'unità fisica lo comunica al motore con l'evento
   `NewLuOnAsi`, che `Navigator` inoltra al dispositivo che ha segnalato
   (Navigator.cs:1039 «$"NewLuOnASI: dispositivo non trovato per Asi: {e.Data.Asi}, Subitem:» →
   AutomationDevice.cs:271 «public async Task ConnectionNewLuOnASI(NewLuOnAsiEventArgs? e)»).
2. **Nasce la missione.**
   NewLuOnASI «internal async Task NewLuOnASI(NewLuOnAsiEventArgs? e)» crea una
   `PathMission` con il dispositivo come sorgente, risolve il codice o l'identificativo della UDC sul
   database, e la mette fra le missioni attive.
3. **Il dispositivo decide se serve una rotta.** `LoadLu`
   (AutomationDeviceLuManager.cs:41 «public async Task LoadLu(Guid missionId, bool activateExecuteStep =»)
   porta a
   ExecuteNextAction «private void ExecuteNextAction(bool isToReadLuInfo, bool», che sceglie
   fra tre esiti: leggere i dati della UDC, eseguire subito uno step, oppure **chiedere una rotta**.
   La terza via si apre quando
   ShouldBuildRoute «private bool ShouldBuildRoute()» trova **sul
   dispositivo stesso** una destinazione fissa (`DestinationItem`, `DestinationItem2`) o una regola di
   allocazione (`LookWhereAllocateLu`). È il caso dell'elemento che sa a chi passare quello che riceve:
   la destinazione non è nella UDC, è nelle proprie proprietà.
4. **La richiesta va al gestore di rotte.**
   AddLoadedLuToRouteToBuildQueue «private void AddLoadedLuToRouteToBuildQueue()»
   accoda la missione in `RoutesToBuild`; il ciclo
   BuildRoutePath «PathDevices.Instance.Add(item.SourceId);» la preleva e chiama
   FindWhereStoreLu «internal static async Task<RouteBuildStatusTypes> FindWhereStoreLu», che **stabilisce la destinazione** — con le
   destinazioni fisse passando da
   BuildRouteToFixedDestination «if (mission != null && RoutesToBuild.Instance», altrimenti cercando dove
   depositare secondo `LookWhereAllocateLu`. Se non trova nulla la missione torna in coda con attesa
   crescente: non sparisce e non fa girare una ricerca completa a ogni giro.
5. **La rotta si spacchetta per singolo elemento.** Con la destinazione nota,
   BuildPathToDestination «internal async Task BuildPathToDestination()» chiama
   GetMissionStepsToDestination «internal List<MissionStep> GetMissionStepsToDestination()» e quindi
   PathBuilder.GetRouteSteps «public List<MissionStep>? GetRouteSteps(bool considerOffLineAvailable = false)», che cerca sul grafo e restituisce
   un **elenco di `MissionStep`**: uno per ciascun elemento attraversato, con sorgente e destinazione
   proprie. È il passaggio che trasforma «da qui a là» in «cosa deve fare ognuno».
6. **Ogni elemento esegue il proprio pezzo.** Da quel momento la macchina a stati del dispositivo
   trova il proprio step e lo esegue: `ExecuteStep`
   (AutomationDevice.cs:314 «if (Navigator.NavStatus != NavigatorStatusTypes.Run)») sceglie la strategia
   e arriva a
   StartPutToMOVE_LU «await ExecuteQueueToReceiveLu();»,
   che **manda il comando al device** e si mette in attesa della risposta, secondo il modello descritto
   in «Il dialogo con il campo».

Due conseguenze utili leggendo il resto del documento. La prima: il lavoro nasce **da due
parti** — dal campo, con la sequenza appena descritta, e dal database, che `MasterProcess` interroga
a ogni giro per comandi ed estrazioni. Le due strade si incontrano nella stessa `PathMission`, e da
lì in avanti il percorso è identico. La seconda: la rotta si calcola **una volta** e si conserva come
step per elemento, quindi un difetto nella decomposizione non si vede al momento del calcolo ma molto
più tardi, quando un singolo elemento non trova il proprio step.

## Il modello: grafo, missioni, step

Il motore modella l'impianto come un **grafo diretto** e le movimentazioni come **missioni
scomposte in step**. Quattro tipi, quattro collezioni singleton che li contengono.

**I nodi — `PathDevices` / `AutomationDevice`.** Ogni dispositivo fisico (rullieva, traslo,
cella di stoccaggio, distributore circolare, gruppo) è un `AutomationDevice`, identificato da
`ItemGuid` (AutomationDevice.Properties.LiveData.cs:317 «Description("Returns the next group of PathMissions to fetch based on»),
classificato da `DeviceType` (AutomationDevice.Properties.cs:290 «public DeviceTypes DeviceType»).
Vivono tutti nel dizionario concorrente della collezione singleton `PathDevices`
(PathDevices.cs:35 «private readonly ConcurrentDictionary<Guid, AutomationDevice?> _items =»), indicizzato per
`Guid`, per `ASI`/`SUBITEM`/`PARTITION` e per id numerico
(PathDevices.cs:78 «public AutomationDevice? this»,
:138 «public AutomationDevice? this»,
:96 «public AutomationDevice? this»).

**Gli archi — `PathJunctions` / `AutomationJunction`.** Una giunzione è una coppia orientata
`SourceId → DestinationId` con una `Priority` intera
(AutomationJunction.cs:44 «public Guid DestinationId { get; set; }»,
:68 «public int Priority { get; set; }»,
:74 «public Guid SourceId { get; set; }»). Le proprietà `SourceItem` e
`DestinationItem` non sono campi: risolvono l'estremo interrogando `PathDevices` ad ogni
lettura (AutomationJunction.cs:53 «get => Collections.PathDevices.Instance»,
:83 «get => Collections.PathDevices.Instance»). Vivono nella collezione singleton
`PathJunctions` (PathJunctions.cs:24 «private readonly ConcurrentDictionary<Guid, AutomationJunction> _items»).
Il grafo è quindi **definito dagli archi**: `BuildNavigatorItemsJunctions` carica le giunzioni
dalla tabella `NAVIGATOR_JUNCTIONS` e crea i nodi scorrendo gli estremi di ogni arco
(Navigator.cs:1448-1472 «catch (Exception ex)»); un dispositivo non
citato da nessuna giunzione non entra nel grafo per questa via (l'Editor può però
aggiungerlo direttamente alla collezione: si veda «Stato e proprietà»).

**La missione — `ActiveMissions` / `PathMission`.** Una `PathMission` è *una UDC da spostare*:
porta `LuId`/`LuCode`, il `MissionType` (`Storing`, `Extraction`, `Rejection`, …), la posizione
corrente `PositionItemId` e la destinazione `DestinationItemId`
(PathMission.cs:525 «get => _luCode;»,
:587 «DisplayName("MissionSteps")»,
:676 «Description("Returns or Sets the AutomationDevice where the PathMission»,
:277 «public Guid? DestinationItemId»). Vive nel dizionario di
`ActiveMissions`, chiavato per `MissionId`
(ActiveMissions.cs:116 «/// <param name="modifica">»).
Il legame device→missione **non è memorizzato**: `AutomationDevice.LoadedLu` scandisce
`ActiveMissions` cercando la missione la cui `PositionItemId` coincide col proprio `ItemGuid`
(AutomationDevice.Properties.cs:400-407 «"Returns or sets the warehouse cell connected to the element, by»).

**La tratta — `MissionSteps` / `MissionStep`.** Uno `MissionStep` è *un salto elementare*
fra due nodi: `FromNodeId` → `ToNodeId`, con `StepId`, `MissionId`, tempi di inizio e fine
e un `StepStatus` (`ResultCodeTypes`)
(MissionStep.cs:182 «Description("Returns or sets the unique id of the step")»,
:202 «Description("Returns or sets the ItemGuid of the Destination»,
:192 «Description("Returns or sets the execution status of the step")»). Anche qui `SourceItem`/`DestinationItem`
sono viste risolte su `PathDevices`
(MissionStep.cs:148 «Description("Returns or sets the source element")»,
:21 «public AutomationDevice? DestinationItem»).

Il punto che conta per chi rifattorizza: **gli step non appartengono né alla missione né al
device.** Stanno tutti in un unico dizionario globale, `MissionSteps.Instance`
(MissionSteps.cs:16 «private readonly ConcurrentDictionary<Guid, MissionStep> _items = new();»), e sia
`PathMission.MissionSteps` sia `AutomationDevice.MissionSteps` sono **proprietà calcolate**
che filtrano quel dizionario ad ogni lettura, e che in scrittura cancellano-e-riaggiungono:

- `PathMission.MissionSteps` filtra per `MissionId`
  (PathMission.cs:589-602 «public List<MissionStep> MissionSteps»);
- `AutomationDevice.MissionSteps` filtra per `SourceItem.ItemGuid`
  (AutomationDevice.Properties.cs:455-468 «Description("Returns or Sets the maximum weight that can be loaded on»).

Ne segue che un `mission.MissionSteps.Count` non è la lettura di un campo ma una scansione
completa del dizionario di tutti gli step dell'impianto, e che due letture consecutive possono
dare risultati diversi.

## Avvio

Il motore non ha un `Main`: si avvia da solo, in tre momenti distinti.

**1. Inizializzazione del tipo.** `Navigator` è `sealed` e ha membri interamente statici
(Navigator.cs:24 «public sealed class Navigator»); non dichiara alcun costruttore
d'istanza. Il **costruttore statico** (Navigator.cs:28-31 «static Navigator()»)
iscrive il motore al proprio evento `DefragmentationChanged`
(Navigator.cs:30 «DefragmentationChanged += Navigator_DefragmentationChanged;»). Non apre nessun socket: chi ospita il motore valorizza
`Navigator.EventSink` a parte (si veda «Eventi verso l'host») — e `LoadNavigatorData`
(Navigator.cs:796 «if (previousState != null)») si limita ad azzerare lo stato di
runtime e a ricreare il token di cancellazione a ogni caricamento, senza toccare porte o
connessioni esterne.

**2. `StartAsync` come innesco.** L'avvio ha un metodo pubblico dedicato,
`Navigator.StartAsync()`
(Navigator.cs:729 «public static async Task<bool> StartAsync()»),
che l'host chiama all'avvio (`EngineHostedService`): apre i database del WCS e i loro scrittori con
`OpenDatabases`
(Navigator.cs:756 «private static void OpenDatabases()»),
poi attende `LoadNavigatorData()` e restituisce l'esito; un errore finisce nel log, in
`Navigator.LoadError`, e fa rispondere `false`. **Non prende più parametri**: fino al 2026-09-12
riceveva la stringa di connessione a SQL Server del gestionale e la applicava con
`ApplyDatabaseConnectionString`, che costruiva `Navigator.Database`; quel parametro, quel metodo e
quella variabile non esistono più. Anche il setter di `DatabaseConnectionString`, che faceva partire
il caricamento in un `Task.Run` **scartato** senza che chi lo invocava potesse sapere quando — o se
— fosse finito, era già stato cancellato l'11 settembre 2026 (voce `B9` del registro del debito).

**3. `LoadNavigatorData`** (Navigator.cs:806 «internal static async Task LoadNavigatorData()») è la
sequenza di bootstrap, nell'ordine:

1. **Svuotamento dello stato di esecuzione**
   (Navigator.cs:826-831 «if (oldCts != null)»). Le righe erano sette e
   chiamavano `Instance.List.Clear()`: `List` è una proprietà che **costruisce una copia nuova a
   ogni lettura**, quindi il `Clear` agiva sulla copia e veniva buttato via — quattro di quelle
   righe non svuotavano niente. Ora sono sei e chiamano lo svuotamento vero: `MissionSteps`
   (MissionSteps.cs:168 «public void Clear()»), `ActiveMissions`
   (ActiveMissions.cs:372 «public async Task Clear(bool removeFromDb)»), `ManagedLu`
   (ManagedLU.cs:136 «public void Clear()»), `CommandToDo`
   (ComandToDo.cs:62 «public List<Command> List => Items.ToList();»), la coda di
   `RoutesToBuild` e il contatore dei tentativi di costruzione percorso.
   **`PathDevices` e `PathJunctions` non si svuotano, e non è una dimenticanza**: `Add(Guid)` fa
   `GetOrAdd`, quindi finché la collezione resta i dispositivi vengono riusati, mentre
   svuotandola se ne creerebbero di nuovi lasciando vivi i vecchi — ancora iscritti a
   `Navigator.StatusChanged` e con i propri cicli di fondo attivi. Si avrebbero due insiemi di
   dispositivi che comandano lo stesso impianto. Le giunzioni si ripuliscono comunque, perché
   `DbInfo.LoadJunctions` chiama il `Clear()` vero prima di ricaricare
   (DBInfo.cs:335 «PathJunctions.Instance.Clear();»). Si vedano le voci `B1` e `B12`.
2. **Rinnovo del `CancellationTokenSource`** (Navigator.cs:793-863 «while (!_cancelToken.IsCancellationRequested)»):
   il precedente viene annullato con `await CancelAsync()` e rilasciato, poi se ne crea uno
   nuovo. È il meccanismo con cui una ricarica ferma i loop della sessione precedente.
3. Lettura dello stato di sfrangiatura dal DB. Qui il motore costruiva anche `WmsBusinessArea`,
   cioè caricava nel proprio processo lo strato dei servizi del gestionale: non lo fa più.
4. **Avvio del primo loop**, `UpdateStatus`, ogni 1000 ms
   (Navigator.cs:793-810 «while (!_cancelToken.IsCancellationRequested)»).
5. `NavStatus = Recover` (Navigator.cs:1071 «prelievoInCorso = await DbInfo.IsCellBusyByPickUp(idCella, luId);»), poi
   `BuildNavigatorItemsJunctions()` (carica archi e nodi) e `DbInfo.RecoverFailure()`
   (DBInfo.Writes.cs:321 «internal static async Task RecoverFailure()»), che ricarica le missioni
   dal database SQLite del WCS. `LoadMissions` legge un gruppo alla volta e isola poi ogni missione
   sia nella preparazione sia nel ripristino sul dispositivo
   (DBInfo.Private.cs:194-243 «private static async Task LoadMissions(OperationTypes operationType, bool activeFilter)»): un guasto viene registrato con missione e UDC, ma non ferma le righe successive. Infine riscrive lo stato di **tutti** i dispositivi: oltre a `Loading`,
   `Unloading`, `Moving`, `Busy` e `Recovering` — che sono transitori — lo `switch` riporta a
   `Ready` anche `Error` e `Warning`, che transitori non sono. Un guasto segnalato dal campo
   prima della ricarica risulta quindi cancellato dopo.
6. `NavStatus = Stop` (Navigator.cs:918 «WriteLog(») e
   `DeviceConnections.Initialize()` (Navigator.cs:825 «while (!_cancelToken.IsCancellationRequested)»),
   che costruisce gli oggetti `Connection` dalle righe PLC del database
   (DeviceConnections.cs:133 «_tstConnection = null;»).
   **Costruire non è aprire**: l'apertura avviene solo più tardi, in `OpenConnection`, ed è
   materia della sezione «Il dialogo con il campo».
7. **Avvio degli altri due loop**, `BuildRoutePath` ogni 10 ms
   (Navigator.cs:795-814 «await UpdateStatus();») e `MasterProcess` ogni
   `MasterProcessOnEvery` ms (Navigator.cs:912-931 «if (e == null) return;»).

Al termine di `LoadNavigatorData` il navigatore è in **`Stop`**: non costruisce percorsi e non
assegna estrazioni. I tre loop girano comunque, e uno solo dei tre resta davvero inerte:
`BuildRoutePath` esce subito sulla guardia `NavStatus != Run`
(Navigator.cs:1448 «catch (Exception ex)»); `UpdateStatus` non ha guardia;
`MasterProcess` ne ha una, ma **dopo** `await CommandToDo.Instance.Execute()`
(Navigator.cs:1677 «catch (Exception ex)», guardia a
:1679 «WriteLog(»), per cui anche a navigatore fermo la coda dei
comandi viene svuotata e ricaricata dal database ad ogni giro. È il comportamento riassunto
nella colonna «Guardia» della tabella dei tre loop.

**Gli stati e chi li cambia.** `NavStatus` è una proprietà statica con setter `internal`, che
ad ogni cambio scrive il log, solleva `StatusChanged` e notifica l'esterno tramite `EventSink`
(Navigator.cs:237-251 «public static NavigatorStatusTypes NavStatus»); il valore iniziale è `Stop`.
I cinque valori sono definiti in `Enums.NavigatorStatusTypes`
(Enums.cs:203 «public enum NavigatorStatusTypes : byte»). Chi scrive:

| Stato | Chi lo imposta | Quando |
|---|---|---|
| `Recover` | `LoadNavigatorData` (Navigator.cs:803 «catch (Exception ex)») | solo durante il bootstrap, per la durata di `BuildNavigatorItemsJunctions` + `RecoverFailure`. In questo stato lo strato `ORM/` sopprime le scritture su DB (es. PathMission.cs:30 «if (Navigator.NavStatus == Enums.NavigatorStatusTypes.Recover», LoadUnit.cs:30 «if (Navigator.NavStatus == Enums.NavigatorStatusTypes.Recover)») e `PathBuilder` salta l'inizializzazione dei filtri (PathBuilder.cs:245 «return Navigator.NavStatus == NavigatorStatusTypes.Recover») |
| `Stop` | `LoadNavigatorData` (Navigator.cs:918 «WriteLog(») e `ExecutableCommands.StopNavigator` (ExecutableCommands.cs:143 «NavStatus = Enums.NavigatorStatusTypes.Stop;»), che prima chiude le connessioni PLC | fine bootstrap; comando di arresto |
| `Run` | **un solo punto**: `ExecutableCommands.StartNavigator` (ExecutableCommands.cs:128 «NavStatus = Enums.NavigatorStatusTypes.Run;»). Se non si veniva da `Pause`, prima apre le connessioni e aspetta 2 secondi (ExecutableCommands.cs:122-126 «if (NavStatus != Enums.NavigatorStatusTypes.Pause)») | comando di avvio |
| `Pause` | `ExecutableCommands.PauseNavigator` (ExecutableCommands.cs:53 «NavStatus = Enums.NavigatorStatusTypes.Pause;») | comando di pausa; `StartNavigator` da `Pause` riparte senza riaprire le connessioni |
| `Dispose` | **nessuno**: nessun punto assegna `NavigatorStatusTypes.Dispose` a `Navigator.NavStatus` del motore. Oltre alle cinque assegnazioni delle righe sopra c'è una lettura (`var lastNavStatus = NavStatus`, ExecutableCommands.cs:64 «var lastNavStatus = NavStatus;»); il Viewer tiene una copia propria del valore, aggiornata da `Navigator.ApplyStatus` quando arriva l'evento `NavigatorStatusChanged` o la risposta di `GetStatusAsync` (Navigator.cs:228 «NavStatus = status.NavStatus;») | mai |

`ExecutableCommands.ReloadRouteDiagram` (ExecutableCommands.cs:62 «public static async Task ReloadRouteDiagram()»)
è il percorso di ricarica a caldo: ferma, aspetta 2 secondi, richiama `LoadNavigatorData` e
riavvia se prima era in `Run`.

## I tre loop

Sono tre `Task.Run` fire-and-forget avviati da `LoadNavigatorData`, tutti con la stessa forma:
un `while (!_cancelToken.IsCancellationRequested)` che **attende** il corpo e poi **attende**
un `Task.Delay` sul token. La cadenza è quindi «pausa fra una fine e l'inizio successivo», non
un periodo fisso: se il corpo impiega più della pausa, il ciclo rallenta.

| Loop | Cadenza | Responsabilità | Guardia |
|---|---|---|---|
| `UpdateStatus` (Navigator.cs:2069 «await DbInfo.UpdateDefragmentationStatus(value.DefragmentationStatus);») | 1000 ms fissi (Navigator.cs:789 «var statusLoop = Task.Run(async () =>») | scrive i contatori di stato su DB e allinea i timer dei dispositivi | nessuna: gira in ogni stato |
| `BuildRoutePath` (Navigator.cs:1733 «PathDevices.Instance.Add(item.SourceId);») | 10 ms (Navigator.cs:821 «var routeLoop = Task.Run(async () =>») | prende una missione dalla coda `RoutesToBuild` e le costruisce il percorso | `NavStatus != Run` → esce subito |
| `MasterProcess` (Navigator.cs:2170 «internal static async Task MasterProcess()») | `MasterProcessOnEvery`, default 1000 ms (Navigator.cs:261 «public static int MasterProcessOnEvery { get; set; } = 1000;», :1015 «var masterLoop = BackgroundLoop("MasterProcess", MasterProcess,») | esegue i comandi arrivati dal DB, ritenta gli esiti `Extracted` pendenti, poi assegnazioni ed estrazioni | **parziale**: comandi e outbox girano sempre, il resto solo in `Run` |

### `UpdateStatus` — il battito

Due righe (Navigator.cs:2065-2066 «private static async void Navigator_DefragmentationChanged(object?»): aggiorna i
contatori sul database passando `NavStatus == Run` e assegna `ExtractionTimers` con lo stesso
valore. `ExtractionTimers` non è un flag inerte: il suo setter
(Navigator.cs:261-281 «internal static bool ExtractionTimers») scorre tutti i dispositivi e
invoca `StartActiveProcesses()` o `StopActiveProcesses()` su ciascuno. Il setter esce senza far
nulla se il valore non cambia **o se `PathDevices` è vuota**
(Navigator.cs:264 «if (field == value)») — con la conseguenza che il campo
di appoggio non viene aggiornato finché non esiste almeno un dispositivo. È l'unico dei tre
loop il cui corpo gira per intero anche a navigatore fermo, ed è quello che, ogni secondo, riporta
`ExtractionTimers` in linea con `NavStatus`: le assegnazioni temporanee fatte altrove
(ExecutableCommands.Internal.cs:516 «ExtractionTimers = false;» e
:566 «ExtractionTimers = true;», attorno a `ExecuteSteps`) hanno vita
al più di un secondo.

### `BuildRoutePath` — la coda dei percorsi

È il consumatore unico di `RoutesToBuild`, la coda concorrente di missioni in attesa di un
percorso (RoutesToBuild.cs:10 «public sealed class RoutesToBuild»). La
coda è una `ConcurrentQueue<PathMission?>` affiancata da un `ConcurrentDictionary` di lookup
per `MissionId`, tenuti allineati da `Enqueue`/`Dequeue`
(RoutesToBuild.cs:98 «public void Enqueue(PathMission? mission)»,
:112 «public PathMission? Dequeue()»). I produttori sono quattro, tutti
lato dispositivo: Standard.cs:162 «RoutesToBuild.Instance.Enqueue(device.LoadedLu);»,
AutomationDeviceConnectionManager.cs:967 «case DeviceTypes.Automation:»,
AutomationDeviceLuManager.cs:432 «Collections.RoutesToBuild.Instance.Enqueue(device.LoadedLu);»
e AutomationDeviceStepExecutor.Private.cs:1051 «RoutesToBuild.Instance.Enqueue(value);».

Il corpo del giro (Navigator.cs:1733-1843 «PathDevices.Instance.Add(item.SourceId);»):

1. esce se `NavStatus != Run` **oppure** se `_isBuildingPath` è già `true`
   (Navigator.cs:1448 «catch (Exception ex)»), poi alza il flag;
2. estrae una missione dalla coda (Navigator.cs:1508 «LogEntryType.Warning);»)
   e ne legge `PositionItem`;
3. se non c'è posizione, il ramo di recupero scatta solo a **due** condizioni insieme:
   `!string.IsNullOrEmpty(mission?.PickUpDoorCode) && mission.RelocateItemId == Guid.Empty`
   (Navigator.cs:1766 «if (!string.IsNullOrEmpty(mission?.PickUpDoorCode) && mission.RelocateItemId == Guid.Empty)»). Se valgono, risolve
   `SourceItemId` e `RelocateItemId` dal codice porta
   (Navigator.cs:1850-1852 «RoutesToBuild.Instance.Enqueue(m);») — ma la variabile locale
   `positionItem` resta `null`, quindi il giro finisce comunque al passo 5. Altrimenti la missione
   torna in coda con l'attesa crescente e il giro esce
   (Navigator.cs:1542 «RiaccodaConAttesaCrescente(mission,»);
4. se c'è una posizione, chiama `FindWhereStoreLu` con la modalità configurata sul
   dispositivo; se l'esito è diverso da `NotAvailable` la missione ha un percorso, e se il
   dispositivo ha `AutoExecuteStep` fa partire l'esecuzione con un `_ = positionItem.ExecuteStep()`
   non atteso (Navigator.cs:1777-1784 «"nessun PositionItem e nessun PickUpDoorCode utilizzabile");»);
5. **altrimenti riaccoda**, con il controllo di duplicato
   (Navigator.cs:1767 «RiaccodaConAttesaCrescente(mission, "nessuna posizione di deposito trovata");»).
   Lo stesso riaccodamento avviene, senza il controllo di duplicato, nel `catch`
   (Navigator.cs:1826 «RiaccodaConAttesaCrescente(mission, "eccezione durante la costruzione del percorso");»);
6. il `finally` rimette `_isBuildingPath` a `false`
   (Navigator.cs:1886 «_ = positionItem.ExecuteStep();»).

Una missione che non riesce a trovare percorso torna in fondo alla coda: ogni riaccodamento passa da `RiaccodaConAttesaCrescente`, che
dichiara il fallimento al freno `RoutePathRetries`
(Navigator.cs:1843 «var tentativi = RoutePathRetries.RegisterFailure(m.MissionId);»).
Il freno è un `RetryBackoff`, che alza un contatore per `MissionId` e fissa il prossimo tentativo a
200 ms per tentativo, con un tetto di 5 secondi
(Navigator.cs:138 «private static readonly RetryBackoff RoutePathRetries =»):
lo stesso oggetto lo usa `AutomationDeviceStorageManager` per la ricerca di un'altra cella di
deposito, con misure sue.
Il giro che estrae una missione la cui attesa non è ancora scaduta la rimette in coda senza
rifare la ricerca (Navigator.cs:1753 «if (mission != null && RoutePathRetries.IsWaiting(mission.MissionId))»), e al decimo tentativo — poi ogni
cinquecento — scrive un avviso sul registro persistente
(Navigator.cs:1845 «if (tentativi == 10 || tentativi % 500 == 0)»). Il contatore si azzera appena la missione
diventa costruibile (Navigator.cs:1786 «RoutePathRetries.Forget(mission.MissionId);»).

Il flag `_isBuildingPath` va letto per quello che è: il `finally` lo azzera **anche quando
l'uscita è avvenuta perché il flag era già alto**. Come guardia di rientranza è quindi
inefficace; è innocuo oggi solo perché l'unico chiamante di `BuildRoutePath` è il proprio loop,
che attende ogni giro prima del successivo (grep ricorsivo: nessun altro chiamante).

### `MasterProcess` — comandi, outbox, assegnazioni, estrazioni

L'ordine delle istruzioni conta
(Navigator.cs:2170 «internal static async Task MasterProcess()»):

```
await CommandToDo.Instance.Execute();
await ExtractionOutcomeDispatcher.TryDispatchAsync();
if (NavStatus != Run || _masterProcessIsRunning) return;
```

`CommandToDo.Instance.Execute()` e il tentativo di consegna degli esiti `Extracted` rimasti
nell'outbox sono **prima** della guardia: i comandi caricati dal database e il dispatcher girano
anche a navigatore fermo o in pausa. È così che i comandi di avvio arrivano a destinazione e gli
esiti arretrati vengono ritentati anche in `Stop` dopo un riavvio — `CommandToDo` filtra
internamente quali comandi sono ammessi in quali stati
(ComandToDo.cs:783 «navigatorCommand.Status = Command.StatusTypes.Error;»,
:994 «ExecutableCommands.SetStatusReadyAllItems();»,
:913 «navigatorCommand.Status = Command.StatusTypes.Done;»).

Superata la guardia, il giro esegue in sequenza `AssignmentsProcess()`, `ExtractionProcess()`
e `DbInfo.CheckOperation()` (Navigator.cs:2186 «await AssignmentsProcess();»).
`AssignmentsProcess` (Navigator.cs:1628 «private static async Task AssignmentsProcess()») assegna le
UDC alle note di spedizione e alle estrazioni; `ExtractionProcess`
(Navigator.cs:2108 «internal static async Task ExtractionProcess()») chiede al gestionale il
lavoro pronto (:2128 «.GetPendingNoteWorkAsync(LimitOneShippingNotePerBuffer);») **prima** di
prendere il semaforo globale `SyncExtraction` con timeout di 30 secondi
(:2141 «if (!await SyncExtraction.WaitAsync(SyncExtractionTimeout))»), ricontrolla `NavStatus`, e
costruisce le righe di estrazione automatica. Entrambi hanno un secondo controllo
`NavStatus != Run` al proprio
interno (Navigator.cs:1632 «if (NavStatus != NavigatorStatusTypes.Run) return;»,
:2149 «if (NavStatus != NavigatorStatusTypes.Run) return;»): lo stato può cambiare mentre il giro è in
corso, perché nulla lo congela.

Il `try/finally` annidato corregge una cosa precisa: una chiamata che osserva
`_masterProcessIsRunning == true` esce prima di entrare nel `try`, quindi non può eseguire il
`finally` e azzerare il flag del giro attivo
(Navigator.cs:2177 «if (NavStatus != NavigatorStatusTypes.Run || _masterProcessIsRunning) return;»,
:2190 «finally»). L'unico chiamante ordinario è il loop
sequenziale. Il `bool` non è però una sincronizzazione thread-safe: il controllo e l'assegnazione
sono due operazioni separate (:2179 «_masterProcessIsRunning = true;»),
quindi due chiamate realmente concorrenti potrebbero leggere entrambe `false` ed entrare.

## La costruzione del percorso

Due strati distinti: **`FindWhereStoreLu`** decide *dove* mandare l'UDC, **`PathBuilder`**
calcola *per quali tappe* arrivarci.

### `FindWhereStoreLu` — dove metto questa UDC

Navigator.cs:525-604 «internal static async Task<RouteBuildStatusTypes> FindWhereStoreLu». Riceve la missione, il
dispositivo su cui l'UDC si trova e una modalità `LookWhereAllocateLuTypes`
(Enums.cs:135 «public enum LookWhereAllocateLuTypes : byte») letta dalla proprietà
`LookWhereAllocateLu` del dispositivo
(AutomationDevice.Properties.PathOptions.cs:175 «public LookWhereAllocateLuTypes LookWhereAllocateLu»),
a sua volta caricata dalla colonna `LookWhereAllocateLoadUnit` di `PlantItems`, nel database del WCS
(PlantStore.cs:145 «device.LookWhereAllocateLu =»). Restituisce
`Available` / `NotAvailable` / `Rejected`
(AutomationDevice.cs:189 «internal enum RouteBuildStatusTypes : byte»). È uno `switch`
su quattro rami:

- **`CheckOnly`** (Navigator.cs:535 «case LookWhereAllocateLuTypes.CheckOnly:»): chiede al WMS se
  esiste *una* cella libera, con `GetStoreToWarehouse(CheckExistsAvailableCell)`; se non esiste,
  rigetta l'UDC (`RejectTo(NoStoreCellAvailable)`) e restituisce `Rejected`. Se esiste, non
  costruisce nulla verso il magazzino: costruisce il percorso solo se il dispositivo ha una
  destinazione fissa configurata (`DestinationItem` o `DestinationItem2`), e conclude
  guardando se la missione ha ottenuto almeno uno step.
- **`BuildRouteToStore` e `BuildRouteToStoreByIdLuType`** — stesso ramo, trattati in modo
  identico (Navigator.cs:582-583 «foreach (var item in PathDevices.Instance.Where(x => x is {»): sceglie la
  strategia di selezione cella in base a `IsLinkedToStorage`, cioè a se il dispositivo ha
  almeno una giunzione uscente verso un device di tipo `Storage`
  (AutomationDevice.Properties.PathOptions.cs:161 «public bool IsLinkedToStorage»):
  `StoreInLinkedCell` se sì, `StoreInAnyAvailableCell` se no
  (PathMission.cs:160 «StoreInLinkedCell = 2,»,
  :144 «DefaultNavigatorRejectionItem = 2,»). Se `GetStoreToWarehouse` riesce →
  `Available`; altrimenti rigetta → `Rejected`.
- **`BuildRouteToDifferentStoreLocation`** (Navigator.cs:573 «case LookWhereAllocateLuTypes.BuildRouteToDifferentStoreLocation:»):
  identico al precedente, salvo che il ramo «non collegato a storage» usa
  `StoreInAnyAvailableCellDifferentStoreLocation`
  (PathMission.cs:144 «Description(»).
- **`No`** (Navigator.cs:588 «case LookWhereAllocateLuTypes.No:»): nessuna ricerca di
  cella. Se la missione ha già step, `Available`. Altrimenti, se il dispositivo ha una
  destinazione fissa e non si tratta di un rigetto verso la posizione stessa, va di
  `BuildRouteToFixedDestination`; se invece la destinazione della missione è diversa dal
  dispositivo corrente, chiama `mission.BuildPathToDestination()`. Conclude di nuovo
  contando gli step prodotti.

**Che cosa fa `GetStoreToWarehouse`: chiedere la cella, e da quella risalire alla macchina.**
I rami dicono *quale* strategia, non che cosa succede. In una missione di deposito la sequenza
esce dal motore e torna dentro, in sei passaggi.

1. FindRouteAllocateLu «stompedItems.Add(sourceItem.ItemGuid);» parte dalla cella
   collegata al dispositivo (`LinkedCellId`), ne ricava il magazzino e compone i parametri di
   ricerca.
2. **Chiede la cella al WMS e attende la risposta**:
   CercaDestinazioneUdc «var res = await Navigator.WmsBusinessArea.CelleMagazzino.CercaDestinazio» sull'area di
   business `CelleMagazzino`. La cella non la sceglie il motore: la domanda, e si ferma finché non
   arriva l'esito.
3. La risposta porta il **codice porta** della cella (`CodPorta`), la partizione e la quota di
   deposito.
4. **Dal codice porta si risale alla macchina**: `DbInfo.GetItemId(cellData.CodPorta)` dà
   l'`AutomationDevice` a cui quella cella è collegata
   (PathMission.Methods.cs:1085 «if (NoteProcessingId <= 0 || await DbInfo.QtToExtractByProcessingNote»). Se per
   quella porta non esiste nessun elemento l'esito è `NoItemForRequiredLinkedDoor`, e la missione
   non parte.
5. Quella macchina diventa la destinazione, e **da lì si costruisce la strada**:
   `DestinationItemId = itemDestination` seguito da
   BuildPathToDestination «RisultatoRicercaDestinazioneUdc cellData)».
6. I dati della cella restano sulla missione — `DestinationAsi`, `DestinationSubitem`,
   `DestinationPartition`, `StorageDepth` — e vengono ricopiati nel comando fisico **quando lo step
   arriva alla macchina**, in
   ConfigureStorageDestination «lu.SourceSubitem = cellAsi?.Subitem;».
   È lì che parte il comando di stoccaggio con dentro la cella che il WMS aveva indicato.

**`CheckOnly` fa la stessa domanda ma non prenota.** I parametri di ricerca nascono con
`AssegnaPosizione = true`
(PathMission.Methods.cs:700 «tipoRicerca.ListaStoreRaggiungibili = PathDevices.Instance»), e il solo ramo
che lo riporta a `false` è quello del controllo
(PathMission.Methods.cs:682 «tipoRicerca.AssegnaPosizione = false;»). Gli altri
rami chiedono con `AssegnaPosizione` attivo — e due lo riaffermano esplicitamente
(PathMission.Methods.cs:1237 «tipoRicerca.AssegnaPosizione = true;»,
:1210 «var newIdCella = await DbInfo.GetCellId(idCella, cursoreGruppo);») — cioè domandano al WMS di
**assegnare** la posizione, non solo di indicarla. È la differenza fra «ce n'è una?» e «dammene
una»: nel ramo di controllo l'esito serve soltanto a decidere se rigettare l'UDC.

**Perché la domanda esiste separata dall'allocazione.** Chiedere «c'è una cella?» senza prenotarla
serve a **decidere che cosa fare**, e la decisione più frequente è il rigetto: se non c'è posto,
l'UDC va mandata a un punto di rigetto invece di restare ferma dov'è.
RejectTo(NoStoreCellAvailable) «internal async Task<bool> RejectTo(RejectionTypes rejectionType, bool» sceglie quel
punto in ordine — prima `RejectItemDestinationAllocateLu` del gruppo di appartenenza
(:255 «rejectionPointGuid = PositionItem.BelongedGroup.RejectItemDestinationAll»), poi quello del dispositivo
(:257 «rejectionPointGuid = PositionItem.RejectItemDestinationAllocateLu;») — e se **nessuno dei due è
configurato** restituisce `false` senza rigettare: la missione resta in coda, e nel registro non
compare nulla che lo spieghi.

**Quali punti di stoccaggio si passano al motore di ricerca.** Lo stoccaggio può essere deciso in
qualunque punto del grafo, quindi la ricerca va ristretta ai depositi **raggiungibili da lì**: è a
questo che serve `ListaStoreRaggiungibili`. Se da quella posizione non se ne raggiunge nessuno,
l'UDC va rigettata. Il motore riempie quella lista in quattro modi, e **due dei quattro non
guardano affatto la posizione**:

| ramo | che cosa mette in `ListaStoreRaggiungibili` | parte dalla posizione |
| --- | --- | --- |
| `StoreInLinkedCell` (:1227 «private async Task SetParamsForStoreInLinkedCell(OpzioneRicercaCelle») | il dispositivo stesso, o il gruppo a cui appartiene | sì |
| `StoreInReachableCell` (:1240 «private void SetParamsForStoreInReachableCell(OpzioneRicercaCelle») | `ReachableStoreOperators`, filtrati a `Ready` | sì |
| `CheckExistsAvailableCell` (:630 «if (!ManagedLu.Instance.Contains(newHandledLu))») | tutti i `Storage` dell'impianto che hanno un `RelocateItem` | no |
| `StoreInAnyAvailableCell` (:639 «if (item == null) return;») e `StoreInAnyAvailableCellDifferentStoreLocation` (:652 «if (!ManagedLu.Instance.Contains(newHandledLu))») | tutti i dispositivi collegati a storage, non offline, fuori da un gruppo | no |

`ReachableStoreOperators` è la vera risposta alla domanda «da qui dove posso stoccare»: percorre il
grafo con `SearchReachableStoreOperators`
(AutomationDevice.Properties.PathOptions.cs:254 «public List<AutomationDevice> ReachableStoreOperators»).
Ma **`StoreInReachableCell` non si raggiunge da `FindWhereStoreLu`**: l'unico punto che lo passa è
AutomationDeviceStorageManager.cs:503 «if (device.IsLinkedToStorage)»,
cioè l'altro ingresso alla ricerca. Dal ciclo di costruzione dei percorsi la lista contiene tutti i
depositi dell'impianto, e la raggiungibilità si scopre **dopo**, quando `BuildPathToDestination` non
produce nessuno step. Cercato su tutto il repository, esclusi `obj/` e `bin/`.

**Il rigetto su richiesta dell'operatore ha sempre una destinazione.** È il ramo
`DefaultNavigatorRejectionItem`: usa `RejectDestinationUserRequest` del dispositivo se configurato,
altrimenti `Navigator.DefaultRejectItem`
(Navigator.cs:200 «public static Guid DefaultRejectItem { get; set; } = Guid.Empty;») come ripiego d'impianto. I due percorsi
che lo attivano sono
RebuildGroupRejection «newMission.DestinationItemId = item.GetValue<Guid>» e
RebuildRejection «private async Task RebuildRejection()», entrambi
marcano la missione con «User requested to reject LU». È l'unico rigetto che non dipende da come è
configurato il singolo dispositivo.

**Da verificare: due esiti dichiarati non vengono mai prodotti.**
`StorageLuResultTypes.ItemLinkedToWarehouseDoorNotAchievable` ha un messaggio pronto — «The storage
location it's not achievable from current item» — ma **nessuno lo assegna**: è dichiarato
(PathMission.cs:180 «NoWarehouseSetForAutomaticCell = 1,») e gestito
(PathMission.Methods.cs:209 «case StorageLuResultTypes.NoItemForRequiredLinkedDoor:»), e basta. Quando
il deposito esiste ma non è raggiungibile, l'esito che arriva all'operatore è invece
`NoItemForRequiredLinkedDoor`, «No item linked to storage door cell», che dice un'altra cosa. Stessa forma per
`RejectionTypes.RejectedFromAttendant`, gestito in due punti di `RejectTo`
(:274 «case RejectionTypes.RejectedFromAttendant:»,
:312 «case RejectionTypes.RejectedFromAttendant:») e mai passato da nessun chiamante.
Entrambe le ricerche fatte sull'intero repository, esclusi `obj/` e `bin/`. Resta da capire se
fossero previsti e non collegati, o se siano residui.

Due cose vanno dette per intero perché non si vedono dal nome del metodo.

**`BuildRouteToStoreForcing` non è gestito.** L'enum ha sei valori
(Enums.cs:131-148 «Description("No")»); lo `switch` ne copre cinque.
Una missione su un dispositivo configurato `BuildRouteToStoreForcing` cade fuori dallo `switch`
e ottiene `NotAvailable` (Navigator.cs:546 «return RouteBuildStatusTypes.NotAvailable;»), che in
`BuildRoutePath` significa riaccodamento — indefinito, ogni 10 ms. Il valore *è* distinto
altrove nel motore (AutomationDevice.cs:1108 «_cancellationCircularDistTokenSource?.Cancel();»,
AutomationDeviceLuManager.cs:784 «device.LookWhereAllocateLu ==»),
quindi non è un valore morto: è un valore che questo percorso non conosce.

`FindWhereStoreLu` chiama tre volte `RejectTo`, che ha un parametro `resetGroupExecuteStepRunning`,
consumato fino a `CheckGroupExecuteStepRunning()`.

**Da verificare:** l'intenzione originaria del parametro. Il nome e il default combaciano
esattamente con la catena `RejectTo` → `RemoveMissionSteps` → `RemoveStep`
(PathMission.Methods.cs:377 «internal void RemoveMissionSteps(bool resetGroupExecuteStepRunning =»,
:338 «StorageDoorCode = await DbInfo.GetDoorCode»), ed è quindi *plausibile* che dovesse
essere inoltrato lì; ma nel codice non c'è traccia che lo confermi, e il comportamento dei
gruppi di dispositivi cambierebbe. Chi conosce la sfrangiatura e i
`GroupOfAutomationDevices` dovrebbe dire se l'inoltro è quello atteso o se il parametro è
residuo da rimuovere.

**Da verificare:** il ramo `CheckOnly` chiama `GetStoreToWarehouse(CheckExistsAvailableCell)`,
che a sua volta forza `MissionType = Storing` prima e dopo la ricerca
(PathMission.Methods.cs:189 «MissionType = MissionTypes.Storing;»,
:189 «MissionType = MissionTypes.Storing;»). La mutazione **non è transitoria**:
il setter di `MissionType` è `SetProperty(ref _missionType, value, true)`
(PathMission.cs:611 «set => SetProperty(ref _missionType, value, true);») e il terzo argomento di
`SetProperty` è `updateDb`, per cui il nuovo tipo viene scritto sul database con
`ORM.PathMission.Update` (PathMission.Methods.cs:1218 «if (newDestination == Guid.Empty || string.IsNullOrEmpty»).
Un «controllo di disponibilità» riscrive quindi in modo permanente il tipo della missione.
Resta aperta l'intenzione: se l'effetto sia voluto, e cosa debba accadere a una missione che
era di tipo diverso.

### `PathBuilder` — la ricerca sul grafo

La ricerca è in due pezzi. **`PathBuilder` è l'adattatore**: fotografa il motore
(PathBuilder.cs:134 «internal RouteGraph Snapshot()»),
chiama la ricerca e converte il risultato in `MissionStep`
(:104 «var hops = RouteSearch.Find(Snapshot(), request, out var arrivalsRejectedByGateway);»,
:113 «return hops.Select(hop => new MissionStep»).
**La ricerca è una funzione pura**, in `Routing/RouteSearch.cs`: riceve un grafo e una domanda, non
conosce `Navigator`, `PathDevices`, `ActiveMissions` né `MissionSteps`, e restituisce sempre una
lista, mai `null`
(RouteSearch.cs:26 «public static IReadOnlyList<RouteHop> Find(RouteGraph graph, RouteRequest request)»).
I tipi del confine sono `RouteNode`, `RouteEdge` e `RouteGraph`
(RouteGraph.cs:12 «public sealed record RouteNode(»,
:23 «public sealed record RouteEdge(Guid SourceId, Guid DestinationId, int Priority);»),
la domanda `RouteRequest`
(RouteRequest.cs:10 «public sealed record RouteRequest(»)
e il passo `RouteHop`
(RouteRequest.cs:19 «public readonly record struct RouteHop(Guid FromId, Guid ToId, int FromGroupRoutePath);»).

Fuori dai test, `PathBuilder` si costruisce **in un file solo**, `PathMission.Methods.cs`, in
quattro punti dentro due metodi: `GetMissionStepsToDestination`
(PathMission.Methods.cs:163 «var result = new PathBuilder(this).GetRouteSteps();»,
:171 «result = new PathBuilder(this).GetRouteSteps(true);»)
e la sonda `ProbeRouteSteps`
(:144 «var steps = new PathBuilder(this, destinationId).GetRouteSteps();»,
:146 «steps = new PathBuilder(this, destinationId).GetRouteSteps(true);»).
In tutti e due i metodi la seconda chiamata è su una **istanza nuova**. Il costruttore
(PathBuilder.cs:69 «public PathBuilder(PathMission mission, Guid? destinazione = null)»)
fissa sorgente (`PositionItemId` se valorizzato, altrimenti `SourceItemId`,
:77 «SourceItemId = mission.PositionItemId != Guid.Empty»),
destinazione — quella della missione, oppure quella *ipotetica* passata al costruttore, che è ciò
che permette di sondare senza scrivere sulla missione
(:81 «DestinationItemId = destinazione ?? mission.DestinationItemId;») —,
il gateway (:75 «GateWayItemId = mission.Gateway;») e il flag
`IgnoreItemStatus`, che è `true` **solo** per le missioni di tipo `PickUpAndStoreAgain`
(:82 «IgnoreItemStatus = mission.MissionType == MissionTypes.PickUpAndStoreAgain;»).

**La fotografia.** `Snapshot()` legge una volta sola gli step in coda di tutto l'impianto
(:137 «foreach (var step in MissionSteps.Instance.List)»),
i dispositivi con tipo, stato, livello, gruppo di appartenenza e UDC a bordo
(:141 «var devices = PathDevices.Instance.List;») e le
giunzioni (:160 «var edges = PathJunctions.Instance.List»).
Il grafo indicizza i nodi per identificatore e tiene le liste di adiacenza già ordinate per
`Priority`
(RouteGraph.cs:42 «.ToDictionary(g => g.Key, g => (IReadOnlyList<RouteEdge>)g.OrderBy(e => e.Priority).ToList());»),
per cui ogni lettura del grafo durante la visita costa O(1)
(:45 «public RouteNode? Node(Guid id) => _nodes.GetValueOrDefault(id);»,
:49 «public IReadOnlyList<RouteEdge> OutgoingFrom(Guid id) => _outgoing.GetValueOrDefault(id, NoEdges);»).
L'impianto è giudicato tutto nello stesso istante. Un
arco che punta a un identificatore senza nodo resta nell'adiacenza, ma non si attraversa.

**Le quattro passate.** `GetRouteSteps` verifica che sorgente e destinazione esistano e siano
diverse
(PathBuilder.cs:98 «if (SourceItemId == Guid.Empty || DestinationItemId is not { } destination»),
compone la domanda
(:103 «var request = new RouteRequest(SourceItemId, destination, GateWayItemId, IgnoreItemStatus,»)
e chiama `Find`, che esegue una visita a passata `High` e, **solo se non ha trovato nulla**, una
seconda a `Normal`
(RouteSearch.cs:48 «state.Visit(Guid.Empty, request.SourceId, Pass.High, []);»,
:49 «if (state.Routes.Count == 0)»,
:50 «state.Visit(Guid.Empty, request.SourceId, Pass.Normal, []);»).
Il chiamante fa la stessa cosa un livello sopra: prima `GetRouteSteps()`, poi — se non è uscito
nulla — `GetRouteSteps(true)`, cioè accettando anche i dispositivi fuori linea o in errore. Quattro
passate al massimo, e **due fotografie**, una per chiamata.

**Le regole, nell'ordine in cui `Visit` le applica**
(RouteSearch.cs:62 «public void Visit(Guid callingId, Guid fromId, Pass pass, List<RouteHop> soFar)»).
La rotta parziale è la lista dei passi fatti finora, e il passo appena compiuto vi entra all'inizio
della chiamata.

1. **Profondità**: oltre 500 passi si torna indietro
   (:72 «if (route.Count > MaxRouteDepth) return;»,
   :19 «private const int MaxRouteDepth = 500;»). È
   una guardia contro un grafo malformato, non un criterio: sull'impianto di esempio dei test (`SamplePlantRouteTest`) la rotta più lunga è di 11 passi.
2. **Potatura per lunghezza**: si abbandona la rotta parziale **già lunga quanto la migliore
   trovata**
   (:73 «if (_bestLength <= route.Count) return;»).
   Il confronto è `<=` e non `<`, e non perde parità: a una rotta parziale lunga quanto la migliore
   manca almeno un passo per arrivare, quindi non potrebbe né batterla né pareggiarla. Resta una
   potatura che dipende dall'ordine di visita, perché confronta con quanto è già stato trovato.
3. **Ciclo**: il nodo corrente è già partenza di un passo della rotta
   (:77 «FromId == fromId) return;»).
4. **Al più un nodo per livello**: se il nodo corrente ha `GroupRoutePathId` diverso da zero e quel
   livello è già quello di un nodo di partenza della rotta, si abbandona
   (:74 «var level = graph.Node(fromId)?.GroupRoutePathId ?? 0;»,
   :78 «FromGroupRoutePath == level) return;»).
   Non è «non tornare al livello di origine»: **due nodi dello stesso livello non possono stare
   nella stessa rotta nemmeno se sono adiacenti**, e il vincolo comprende la sorgente. Sull'impianto di esempio dei test è ciò che rende irraggiungibili `ST01 → ST02` e `ST02 → ST01` — le due navette sono entrambe di livello 1 — e ciò che vieta a una rotta di usare due elevatori di livello 5. Il
   nodo di **arrivo** non ci passa: la sua uscita chiude la rotta prima che venga visitato.
5. **Le uscite**: gli archi che partono dal nodo corrente, meno quello che torna al chiamante e
   quello che torna alla sorgente
   (:82 «var edges = graph.OutgoingFrom(fromId)»,
   :83 «.Where(e => e.DestinationId != callingId && e.DestinationId != request.SourceId)»);
   poi la **guida del gateway**, che se il gateway è raggiungibile da qui scarta tutte le altre
   uscite
   (:85 «if (request.GatewayId != Guid.Empty && edges.Any(e => e.DestinationId == request.GatewayId))»,
   :86 «edges = edges.Where(e => e.DestinationId == request.GatewayId).ToList();»);
   poi la **lista di scarto**
   (:88 «edges = edges.Where(e => !request.ItemsToDiscard.Contains(e.DestinationId)).ToList();»).
6. **L'arrivo**: la prima uscita che porta alla destinazione, o a un dispositivo che appartiene al
   gruppo di destinazione, chiude la rotta
   (:96 «var arrival = edges.FirstOrDefault(IsArrival);»,
   :129 «private bool IsArrival(RouteEdge edge) =>»).
   La destinazione **non passa dalla tabella di stato** — si raggiunge qualunque sia il suo stato —
   ma **passa dalla lista di scarto e dalla guida del gateway**, applicate al punto 5.
7. **Il vincolo del gateway**: la rotta chiusa vale solo se uno dei suoi passi **arriva** sul
   gateway, o su un dispositivo il cui gruppo di appartenenza **è** il gateway
   (:100 «if (PassesThroughGateway(complete))»,
   :140-143 «private bool PassesThroughGateway(List<RouteHop> route) =>»).
   La seconda metà della regola è la stessa con cui il setter di `PositionItemId` azzera il
   gateway (PathMission.cs:725 «?.BelongedGroup?.ItemGuid == Gateway»): senza,
   un gateway che è un dispositivo-gruppo non sarebbe raggiungibile da nessuna rotta, perché un
   membro non è il gruppo, e la clausola del setter non scatterebbe mai. Vale
   anche per l'ultimo passo, se il gateway è la destinazione o un membro del suo gruppo. Se non lo
   attraversa, la rotta non viene registrata e la ricerca da questo nodo **prosegue sulle altre
   uscite**, senza attraversare la destinazione
   (:107 «edges = edges.Where(e => !IsArrival(e)).ToList();»):
   può esistere una deviazione che passa dal gateway. Un gateway che non è un dispositivo del
   grafo, o che coincide con la sorgente, non vincola nulla — la sorgente non compare mai come
   arrivo di un passo, e nessuna rotta potrebbe soddisfarlo
   (:43 «var gateway = graph.Contains(request.GatewayId) && request.GatewayId != request.SourceId»).
8. **Fermata su magazzino o buffer**: un dispositivo `Storage` o `Buffer` è partenza o arrivo, mai
   transito
   (:111 «if (route.Count > 0 && graph.Node(fromId)?.DeviceType is DeviceTypes.Storage or DeviceTypes.Buffer)»);
   la condizione `route.Count > 0` esclude il nodo di partenza. `LoadingAndBuffer` **non** è in
   questo elenco: si attraversa. La regola viene **dopo** il controllo di arrivo, quindi una
   destinazione adiacente a un buffer si raggiunge passando dal buffer.
9. **Le uscite attraversabili**, scelte con la tabella di stato qui sotto, visitate in ordine di
   step in coda crescente
   (:123 «foreach (var (id, _) in next.OrderBy(n => n.Queued))»).
   `OrderBy` è stabile, quindi a parità di step in coda l'ordine resta quello di `Priority`.

**La tabella di stato**
(RouteSearch.cs:146 «private bool IsAccessible(RouteNode node, Pass pass)»):

| Stato del dispositivo | Si attraversa |
|---|---|
| `Ready`, `Busy`, `Recovering`, `Locked` | sempre (:153 «DeviceStatusTypes.Ready or DeviceStatusTypes.Busy or DeviceStatusTypes.Recovering or DeviceStatusTypes.Locked => true,») |
| `Loading`, `Unloading`, `Moving` | se non ha step bloccanti (:154 «DeviceStatusTypes.Loading or DeviceStatusTypes.Unloading or DeviceStatusTypes.Moving => !IsBlocking(node, pass),») |
| `OffLine` | **entrambe** le condizioni: si sta accettando l'offline **e** non ha step bloccanti (:155 «DeviceStatusTypes.OffLine => request.ConsiderOffLineAvailable && !IsBlocking(node, pass),») |
| `Error`, `Warning` | con la sola accettazione dell'offline, senza il controllo sugli step (:156 «DeviceStatusTypes.Error or DeviceStatusTypes.Warning => request.ConsiderOffLineAvailable,») |
| ogni altro | mai |

Con `IgnoreItemStatus` — le sole `PickUpAndStoreAgain` — la tabella si salta del tutto e resta il
solo controllo sugli step bloccanti. «Step bloccante» vuol dire: **nella passata `High`**, il
dispositivo ha almeno uno step in coda e l'UDC che ha a bordo va altrove, oppure non ha nessuna UDC
a bordo
(:165 «private bool IsBlocking(RouteNode node, Pass pass)»,
:167 «if (node.QueuedSteps > 0 && pass == Pass.High)»,
:168 «return !node.HasLoadedLu || node.LoadedLuDestinationId != request.DestinationId;»).
Nella passata `Normal` nessuno step blocca: la seconda passata è la stessa ricerca con i vincoli di
occupazione rilassati.

**La scelta è in due tempi.** Una rotta entra nell'elenco solo se è fra le più corte viste finora, e
una più corta scarta le altre
(:178 «private void AddRoute(List<RouteHop> route)»,
:180 «if (route.Count == 0 || route.Count > _bestLength) return;»,
:181 «if (route.Count < _bestLength)»). Fra quelle
rimaste vince la rotta con meno step in coda sui dispositivi di partenza dei suoi passi
(:190 «public IReadOnlyList<RouteHop> Best()»,
:196 «var wait = route.Sum(h => graph.Node(h.FromId)?.QueuedSteps ?? 0);»);
il confronto è stretto
(:197 «if (wait >= bestWait) continue;»), quindi a
parità vince la prima incontrata.

Il secondo criterio **non è un tempo**: è un conteggio di congestione, e non tiene conto del nodo
finale, che non è partenza di alcun passo. **E così resta.** Il criterio di scelta è «meno passi,
poi meno step in coda», senza costo temporale e senza pesi sugli archi: il motore è logico, e un
tempo sarebbe una stima teorica, non una misura.

Non è quindi un cammino minimo pesato noto — non è Dijkstra né A\*: è una enumerazione dei cammini
semplici in profondità con potature, seguita da una selezione lessicografica (prima la lunghezza,
poi la congestione). Con gli indici il costo **per chiamata** è sceso, e con la potatura `<=` sono
scese le chiamate, ma l'esponente non è cambiato: su un impianto a maglie l'enumerazione esplode, ed è la voce `D89` del
registro. Quante chiamate ricorsive ha fatto l'ultima ricerca lo dice un contatore per thread, che
esiste solo per i test di regressione sulle prestazioni
(:21 «/// <summary>Quante chiamate ricorsive ha fatto l'ultima ricerca su questo thread»).

**La lista di scarto** è la parte che legge lo stato del motore fuori dalla fotografia, e la legge
nel costruttore
(PathBuilder.cs:174 «private void InitializeFilteredItems()»):
si popola solo se il navigatore non è in `Recover`, la missione stessa non ha `ActiveFilter` e la
sua posizione è un `Storage` o è collegata a uno
(:245 «return Navigator.NavStatus == NavigatorStatusTypes.Recover»),
cioè **per le estrazioni dal magazzino**; le missioni che filtrano sono quelle attive con
`ActiveFilter` e destinazione `Storage`
(:274 «return ActiveMissions.Instance»). Su DEMOPLANT
il meccanismo è spento — misurato il 2026-09-04, nessuno dei 51 dispositivi ha `ToBeFiltered` né
`OnLoadLuActiveFilter` — e quando invece scarta qualcosa non lascia traccia nel log: voce `D90` del
registro.

**Le sonde.** Quattro punti del motore chiedono «qual è il prossimo salto di questa UDC?». Due di
loro non partono dalla ricerca:

- `CanPickUpLu` verifica la prontezza sul dispositivo a cui **lo step pianificato** manda davvero
  l'UDC, e sonda solo quando uno step che parta da qui non c'è — la missione è ancora in coda di
  prelievo, oppure la sua rotta non passa da questo dispositivo
  (AutomationDeviceLuManager.cs:185 «var step = device.StepToDoSource(luToCheck)»,
  :216 «?? luToCheck?.GetMissionStepsToDestination().FirstOrDefault(x => x.FromNodeId == device.ItemGuid);»);
- `BuildRouteToFixedDestination` prova le due destinazioni possibili **senza scrivere**
  `DestinationItemId`, che cancellava gli step della missione a ogni domanda
  (Navigator.cs:1929 «var result1 = CheckAvailableStepToDo(value, positionItem, first);»,
  :1956 «CheckRoute(value.ProbeRouteSteps(destinationId), positionItem, ref result);»).

Le altre due sono volute, e il codice lo dice: `ValidateStorageLinkCrossroad`
(AutomationDeviceLuManager.cs:1198 «var stepToAnalyze = newMission?.GetMissionStepsToDestination()»)
guarda una missione che può venire dalla coda di prelievo, e il controllo d'ingresso di
un'estrazione
(AutomationDevice.cs:762 «var checkMissionSteps = newMission.GetMissionStepsToDestination();»)
guarda una missione appena creata, che nel motore non c'è ancora: in nessuno dei due casi esiste
una rotta pianificata da leggere.

La sonda condivisa è `ProbeRouteSteps`
(PathMission.Methods.cs:138 «internal List<MissionStep> ProbeRouteSteps(Guid destinationId)»):
due passate come `GetMissionStepsToDestination`, nessuna scrittura sulla missione, e la stessa rete
`try`/`catch`, perché chi sonda ripristina lo stato del dispositivo fuori da un `finally` e
un'eccezione qui lo lascerebbe in `Recovering`. `CheckRouteToDestination` è oggi una riga sola, che
la interroga
(:127 «internal bool CheckRouteToDestination(Guid destinationId) => ProbeRouteSteps(destinationId).Count > 0;»).

**Il gateway sopravvive al primo passo.** Il setter di `PositionItemId` lo azzera solo quando la
missione lo raggiunge — o arriva su un dispositivo del gruppo che è il gateway, o esce dall'impianto
(posizione vuota, alla rimozione)
(PathMission.cs:723 «if (Gateway != Guid.Empty &&»,
:726 «Gateway = Guid.Empty;»). Prima si azzerava a
ogni cambio di posizione, e una rotta ricostruita dopo il primo passo non conosceva più il vincolo.
**Conseguenza da sapere in esercizio:** per un magazzino a gruppi, se la corsia indicata dal gateway
non è raggiungibile adesso, la missione **non ha rotta**, dove prima l'UDC sarebbe finita in
un'altra corsia. È voluto — la corsia sbagliata era un errore di stoccaggio silenzioso — ma cambia
ciò che vede l'operatore: «nessuna rotta» al posto di un deposito nel posto sbagliato.

Un ramo dipendeva da quell'azzeramento: `GetStepToDefragmentCell` deduceva dal gateway vuoto che
l'UDC avesse già lasciato la cella di partenza. Adesso lo chiede alla posizione, e la condizione è
cambiata **per restare equivalente**
(PathMission.Methods.cs:1016 «if (Gateway == Guid.Empty || PositionItemId != SourceItemId)»):
un ripristino all'avvio, o un movimento di gruppo, può riscrivere la posizione senza passare da uno
step.

**La rete di regressione** sta in
`WCS/Wcs.Engine/tests/Wcs.Engine.Test/Routing/`: 18 casi sulle regole di questa sezione
(`RouteRulesTest`), 10 su un impianto di esempio inventato come fixture (`SamplePlantRouteTest`, 28 dispositivi e 33 giunzioni), 11 sul gateway (`GatewayTest`), 4 sulle sonde
(`RouteProbeTest`) e 3 sulla funzione pura (`RouteSearchTest`). La guardia più larga è il conteggio
su tutte le coppie ordinate: **471 raggiungibili su 756, la più lunga di 11 passi** — qualunque
intervento che cambi una sola rotta cambia uno dei due numeri.

**Da verificare:** l'ordinamento per `Priority`. Il grafo ordina le uscite di ogni nodo per
`Priority` **crescente**
(RouteGraph.cs:42 «.ToDictionary(g => g.Key, g => (IReadOnlyList<RouteEdge>)g.OrderBy(e => e.Priority).ToList());»),
mentre la documentazione della proprietà dice che valori più alti indicano priorità maggiore
(AutomationJunction.cs:65 «/// Valori più alti indicano priorità maggiore.»).
L'ordine conta in due punti: l'arrivo è la **prima** uscita che porta a destinazione
(RouteSearch.cs:96 «var arrival = edges.FirstOrDefault(IsArrival);»),
e — poiché `OrderBy` è stabile — la priorità resta il criterio di parità dopo il riordino per step
in coda
(:123 «foreach (var (id, _) in next.OrderBy(n => n.Queued))»).
Se il commento descrive l'intenzione, l'ordinamento è invertito; se invece la convenzione reale è
«numero più basso = priorità più alta», è il commento a essere sbagliato. Non si può stabilire
quale delle due sia vera leggendo solo il codice: serve la convenzione con cui l'Editor scrive la
colonna `PRIORITY`. Su DEMOPLANT la domanda è inerte, perché tutte le giunzioni hanno
`Priority` 99.

## Il dialogo con il campo

Il motore parla con tre interlocutori su tre canali indipendenti, che non si toccano fra loro:

| Canale | Verso | Trasporto | Chi c'è dall'altra parte |
|---|---|---|---|
| coda comandi | in ingresso | tabella `Commands` nel database del WCS, riletta per intero ad ogni giro di `MasterProcess`; ci si scrive con `POST /api/v1/commands` | i client del WMS fuori dall'area WCS, e il motore stesso |
| eventi verso l'esterno | in uscita | chiamate dirette su `IEngineEventSink`, senza socket né porta | `Wcs.Host`, che le spinge sull'hub SignalR (si veda «Eventi verso l'host») |
| eventi dal campo | in ingresso | i sei eventi di `IConnection`, in-process | il driver configurato con `IConnectionFactory` (PLC reali) o `Wcs.Connection.Test` (simulatore) |

Il canale degli eventi verso l'esterno **non** trasporta comandi verso il motore: è il primo punto da chiarire.

**Il modello di interazione col campo, che il codice non dichiara da nessuna parte.** La macchina a
stati esegue un flusso **logico**: manda un comando a un'unità fisica e **si mette in attesa della
risposta**, che può essere di successo o di insuccesso. Se la risposta non arriva entro un tempo
stabilito scatta il **timeout**, che segnala che *probabilmente* qualcosa non va — è un sospetto,
non una certezza sull'esito dell'operazione in campo.

Da qui discendono tre cose che leggendo i soli sorgenti si tenderebbe a leggere al contrario:

- **Lo stato di errore di un dispositivo non è uno stato fisico.** È contabilità del motore, e
  significa «l'ultimo comando non è stato eseguito». Le unità fisiche non tengono un errore
  persistente che il motore rispecchia: quando falliscono comunicano soltanto l'impossibilità di
  eseguire *quel* comando.
- **La via d'uscita prevista è riprovare.** L'utente ritenta, la macchina a stati rimanda il comando,
  e se nel frattempo il problema è rientrato il movimento riprende. Nel codice sono `ClearError` e
  `ResetError` (AutomationDeviceErrorHandler.cs:26 «/// chiamante. Si vedano le voci <c>D43</c> e <c>D59</c> del registro in»),
  che riportano a `Ready` e rilanciano `ExecuteStep`.
- **Una ricarica è un ritentativo.** Ricostruisce lo stato di esecuzione da zero, ed è per questo che
  `RecoverFailure` riporta a `Ready` anche `Error` e `Warning`: non «cancella un allarme», restituisce
  il dispositivo al lavoro. Si veda la voce `B23` del registro.

Ne discende anche perché la correlazione fra comando e risposta è critica: un comando spedito al PLC
ma **non tracciato** non può essere associato ad alcuna risposta, quindi non può che finire in
timeout. È esattamente il difetto della voce `E2`.

### Comandi in ingresso

**Da dove arrivano davvero.** Non da `IEngineEventSink`: è un canale a senso unico, verso
l'esterno, e non trasporta comandi (si veda «Eventi verso l'host»). L'unico modo di riempire la coda
`CommandToDo` è la lettura del database: dentro `Execute()`, la coda privata viene svuotata
(ComandToDo.cs:132 «Items.Clear();») e ricaricata da
`ORM.CommandToDo.LoadFromDatabase`
(ComandToDo.cs:133 «await ORM.CommandToDo.LoadFromDatabase(Items);»), che accoda una
`Command` per riga con `values.Enqueue`
(CommandToDo.cs:48 «values.Enqueue(CommandStore.ToCommand(row));»). Sono gli unici due punti
in cui la `ConcurrentQueue` privata viene modificata: `Items` compare in `ComandToDo.cs`
soltanto alle righe 43 «Items = new ConcurrentQueue<Command>();» (costruzione),
62 «public List<Command> List => Items.ToList();» e
105 «var items = Items.ToArray();» (copie in lettura),
89 «private ConcurrentQueue<Command> Items { get; }» (dichiarazione),
132 «Items.Clear();»,
133 «await ORM.CommandToDo.LoadFromDatabase(Items);»,
135 «if (Items.IsEmpty) return;» e
139 «var commands = Items.ToArray();».

La lettura è una sola: `CommandStore.PendingAsync` (CommandStore.cs:69 «public static Task<List<CommandRecord>> PendingAsync(WcsDbContext»), chiamata
da `LoadFromDatabase` (CommandToDo.cs:45 «rows = await CommandStore.PendingAsync(context, StatusTypes.Running);»), prende dal database del WCS le righe con
`Status < Running` nell'ordine di arrivo — cioè sotto `1`. Poiché `ToDo` vale `0` e
tutti gli altri stati valgono `1` o più
(Command.cs:106-117 «MoveReferenceManual = 3008,»), **la finestra è di un
valore solo**: un comando viene ripreso finché è `ToDo` e sparisce dalla vista del motore
appena passa a `Running`. Chi lascia un comando su `Running` lo lascia lì per sempre.

**Chi scrive le righe.** Fuori dall'area WCS, i client del WMS (`Client/Kazam`,
`API/WMS.CRUD.API`, e le altre superfici che espongono la tabella): sono fuori dal perimetro
di questo documento. Dentro l'area, il motore **scrive sulla stessa tabella su cui legge**:
`ORM.CommandToDo.Update` (CommandToDo.cs:73 «internal static async Task Update(Command value)») prende
un'istantanea del comando e la mette in coda allo scrittore di stato, attesa, cosi' la chiave
assegnata dal database torna sul comando; `CommandStore.UpsertAsync`
(CommandStore.cs:83 «public static async Task UpsertAsync(WcsDbContext context») inserisce se la chiave manca, altrimenti aggiorna. Ha nove punti di chiamata in `WCS/`
(grep ricorsivo su `CommandToDo.Update(` limitato a `WCS/`; le altre occorrenze del monorepo
sono un tipo omonimo di `WMS.Route.Interface`, senza rapporto con questo):
AutomationDevice.cs:482 «Status = DeviceStatusTypes.Ready;» e
:545 «if (_groupManager != null) await _groupManager.StepDoneGroup»,
Command.cs:314 «set => SetProperty(ref _workstationId, value, true);»,
AutomationDeviceConnectionManager.cs:451 «await CommandToDo.Update(new Command»,
:1104 «device.LogError(ex);»,
:1235 «var fromAsiItem = Navigator.GetDataAsi(stepError.SourceItem);»,
:1290 «: DeviceStatusTypes.Ready;»,
AutomationDeviceGroupManager.cs:460 «await CommandToDo.Update(navigatorCommand);»
e Navigator.cs:1154 «ErrorDescription = value.StatusDescription,» (`WriteAlert`, che registra un
`ItemExecuteStep` già in stato `Error`). Il secondo metodo dell'ORM, `SetStatus`
(CommandToDo.cs:61 «internal static Task SetStatus(decimal idCommandToDo, StatusTypes» → CommandStore.cs:109 «context.Entry(existing).CurrentValues.SetValues(snapshot);»), scrive invece solo
stato ed errore — e azzera anche `ShowStatus`, cosa che `Update` non fa — con quattro chiamanti, tutti legati a `PathMission.IdCommandToDo`
(AutomationDeviceConnectionManager.cs:479 «await CommandToDo.SetStatus(executedStep.PathMission.IdCommandToDo»,
:1364 «await CommandToDo.SetStatus(pathMission.IdCommandToDo»,
PathMission.Methods.cs:291 «await CommandToDo.SetStatus(IdCommandToDo, Command.StatusTypes.Done,»,
:296 «if (rejectionItem?.DeviceType == DeviceTypes.Buffer)»).

**Da verificare:** a cosa serve `SHOW_STATUS`, e perché solo una delle due scritture la
tocca. `SetStatus` la riporta a `0`
(CommandStore.cs:109 «context.Entry(existing).CurrentValues.SetValues(snapshot);»); `Update` la lascia com'era, insieme a utente, provenienza
dall'API e GUID dell'assegnazione, che il motore non governa
(CommandStore.cs:104-113 «snapshot.AssignmentGuid = existing.AssignmentGuid;»), e `LoadFromDatabase` non la legge.
Nell'area WCS la colonna non compare in nessun altro punto: l'unico uso trovato nel monorepo
è nel Client, che la legge e la scrive attraverso l'API (`ShowStatus` del
contratto)
(UsrListCommandStatus.cs:112 «public ShowStatusTypes ShowStatus { get; set; } = ShowStatusTypes.Active;» (non pubblicato)),
fuori dal perimetro di questo documento. Se sia un flag di «già mostrato all'operatore», e
cosa comporti che l'upsert lo lasci al valore precedente, va chiesto a chi conosce
l'interfaccia del client.

La colonna del riferimento (`ReferenceId`) la scrive come ogni altra proprieta'
(CommandStore.cs:19-39 «public static CommandRecord Snapshot(Command command) => new()»), e la rilettura la riprende
(CommandStore.cs:43 «public static Command ToCommand(CommandRecord row)»).

**Come il comando torna sul database.** Non c'è una scrittura esplicita a fine giro: la
persistenza è un effetto collaterale dei setter. Quindici delle sedici proprietà di `Command`
passano da `SetProperty(ref …, value, true)`
(Command.cs:168-314 «set => SetProperty(ref _bufferCellId, value, true);») — la sedicesima,
`IdCommandToDo`, è una proprietà automatica
(Command.cs:177 «get => _commandType;») — e quel terzo argomento è
`updateDb`: quando il valore cambia davvero, il setter lancia un `Task.Run` **scartato**
(`_ =`, Command.cs:308 «/// Ottiene o imposta l'identificatore della stazione di lavoro») che esegue
`CommandToDo.Update(this)` (Command.cs:314 «set => SetProperty(ref _workstationId, value, true);»).
Ne segue che ogni `navigatorCommand.Status = …` dei gestori è un upsert di tutte e
quattordici le colonne scritte da `Update`, asincrono e non atteso; e che anche la
**lettura** produce scritture,
perché l'inizializzatore d'oggetto di `CommandStore.ToCommand`
(CommandStore.cs:43 «public static Command ToCommand(CommandRecord row)») assegna le stesse
proprietà una per una — ma il comando nasce in materializzazione (`loadingFromDatabase: true`), e
finché non è finita le assegnazioni non scrivono.

**Il giro di esecuzione.** `MasterProcess` attende `CommandToDo.Instance.Execute()` come
prima istruzione, **prima** della guardia su `NavStatus`
(Navigator.cs:1677 «catch (Exception ex)») — è il fatto già descritto in
«I tre loop», ed è ciò che permette a `SetNavigatorRun` di arrivare a destinazione a
navigatore fermo. È l'**unico chiamante** di `Execute()` in tutto il repository: grep
ricorsivo su `CommandToDo.Instance` dà tre risultati, la dichiarazione
(ComandToDo.cs:55 «public static CommandToDo Instance => LazyCommandToDo.Value;»), lo
svuotamento in `LoadNavigatorData` (Navigator.cs:850 «await Task.Delay(delay, _cancelToken);»,
descritto in «Avvio») e questa chiamata.

Dentro `Execute()` (ComandToDo.cs:124 «internal async Task Execute()»):

1. guardia di rientranza `_executeProcessRunning`, campo `volatile`
   (ComandToDo.cs:126 «if (_executeProcessRunning)»,
   :26 «private volatile bool _executeProcessRunning;»), azzerata nel `finally`
   (:294 «_executeProcessRunning = false;»); una seconda guardia dello
   stesso tipo, `_loadingFromDatabase`, protegge la sola lettura
   (CommandToDo.cs:33 «if (_loadingFromDatabase)») — e se la ricarica esce
   per quella guardia, `Execute()` **non** prosegue: la coda è stata appena svuotata alla riga
   prima (:132 «Items.Clear();»), quindi il giro cade
   sull'`if (Items.IsEmpty) return;` del passo 2
   (:135 «if (Items.IsEmpty) return;»). Il giro salta per intero, e i
   comandi già in tabella aspettano il giro successivo;
2. svuotamento e ricarica della coda, poi uscita immediata se non è arrivato nulla
   (:135 «if (Items.IsEmpty) return;»);
3. scansione di una copia della coda
   (:139 «var commands = Items.ToArray();»), saltando quanto non è
   `ToDo` (:126 «if (_executeProcessRunning)») — filtro ridondante
   con la `WHERE` della query, ma che intercetta i cambi di stato avvenuti nel frattempo;
4. `switch` su `CommandType`
   (:132 «Items.Clear();»), che copre **tutti e
   ventiquattro** i valori dell'enum
   (Command.cs:57-108 «public enum CommandTypes») più un `default` che
   marca `Done` (:270 «LogCommandExecution(infoLog, navigatorCommand);»);
5. `LogCommandExecution` (:253 «case Command.CommandTypes.EmptyCellPisically:»), che
   scrive il messaggio informativo e, se lo stato è `Warning` o `Error`, anche la descrizione
   dell'errore (:1023 «if (_lastCommandExecuteSteps.AddSeconds(10) > DateTime.Now)»).

Due dettagli del giro contano più di quanto sembri. Il primo: due flag locali,
`runCmdExecuted` e `stopCmdExecuted`
(:137 «bool runCmdExecuted = false, stopCmdExecuted = false,»), limitano a **uno per giro** i
comandi di avvio e di arresto — ma `runCmdExecuted` è condiviso fra *tre* comandi, non uno:
`SetNavigatorRun` (:135 «if (Items.IsEmpty) return;»),
`SetDefragmentationEnabled` (:185 «case Command.CommandTypes.SetDefragmentationEnabled:») e
`SetDefragmentationDisabled` (:191 «case Command.CommandTypes.SetDefragmentationDisabled:»), che
si escludono quindi a vicenda. Il secondo: il ramo `MoveLuToItem` chiude con `return` e non
con `break` (:222 «infoLog = await HandleItemFindStoringLocation(navigatorCommand»), unico caso su
ventiquattro: abbandona l'intero giro, senza passare da `LogCommandExecution` e lasciando i
comandi successivi in attesa del giro dopo.

**I comandi e il loro effetto.** L'enum `CommandTypes`
(Command.cs:57 «public enum CommandTypes») numera i valori per famiglia:
`1xxx` agisce sul navigatore, `2xxx` su un singolo dispositivo (`ItemCommandId`), `3xxx` sulle
unità di carico, `4xxx` sulle code del WMS.

| Comando | Gestore | Effetto sull'impianto |
|---|---|---|
| `SetNavigatorRun` (1001) | ComandToDo.cs:883 «navigatorCommand.Status = Command.StatusTypes.Running;» | Solo da `Stop` o `Pause`, altrimenti `Warning`. Chiama `StartNavigator` (ExecutableCommands.cs:118 «public static async Task StartNavigator()»): se non si veniva da `Pause` apre le connessioni e aspetta 2 secondi, poi porta `NavStatus` a `Run` — che, come descritto in «Stato e proprietà», è un fan-out sincrono su ogni dispositivo dell'impianto |
| `SetNavigatorStop` (1002) | :908 «navigatorCommand.Status = Command.StatusTypes.Running;» | Solo da `Run`, altrimenti `Warning`. Chiama `PauseNavigator` (ExecutableCommands.cs:49 «public static void PauseNavigator()») e **non** `StopNavigator`: lo stato risultante è `Pause`, e le connessioni al campo restano aperte |
| `ReloadDiagram` (1003) | :783 «navigatorCommand.Status = Command.StatusTypes.Error;» | Solo in `Stop` o `Pause`, altrimenti `Warning`. Ricarica a caldo l'intero impianto con `ReloadRouteDiagram` (ExecutableCommands.cs:62 «public static async Task ReloadRouteDiagram()») → `LoadNavigatorData` (si veda «Avvio»): grafo, missioni e stati dei dispositivi vengono riletti dal database |
| `RemoveAllLu` (1004) | :818 «navigatorCommand.Status = Command.StatusTypes.Error;» | Scarica logicamente le UDC da **ogni** dispositivo che ne ha (`LuUnloadedFromAsi(Manual)`) e svuota `ActiveMissions` (ExecutableCommands.cs:84-95 «public static async Task RemoveAllLu()»). Non fa nulla se non ci sono missioni attive o dispositivi |
| `SetStatusReadyAllItems` (1005) | :977 «Navigator.RecordNavigatorLog(null, infoLog, LogEntryType.Information);» | Porta a `Ready` lo `Status` di **tutti** i dispositivi, senza distinguere (ExecutableCommands.cs:108 «foreach (var item in PathDevices.Instance.List) item.Status =»): cancella quindi anche gli `Error` e i `Warning` segnalati dal campo |
| `ExecuteSteps` (1007) | :1021 «private string HandleExecuteSteps(Command navigatorCommand, string infoLog)» | Con limitazione a un'esecuzione ogni 10 secondi (:1001 «/// Utilizza il sistema di logging del Navigator per tracciare le»); oltre la soglia il comando resta `ToDo` e viene ritentato al giro dopo. `ExecuteSteps` (ExecutableCommands.Internal.cs:525 «item.Status != Enums.DeviceStatusTypes.OffLine &&») gira solo in `Run`: riporta a `Ready` i dispositivi che non hanno figli, non sono distributori circolari e non sono già `Ready` né `OffLine` (:523-526 «var needsReset = item.BelongedItems.Count == 0 &&»), e lancia `ExecuteStep()` (non atteso, :509 «RecordNavigatorLog(null, navigatorCommand.ErrorDescription» e :473 «_ = item.ExecuteStep();») su gruppi e distributori circolari sempre, sugli altri solo se hanno UDC caricate, non sono di tipo `Storage` e non sono collegati a uno storage (:471-472 «case Enums.DeviceTypes.GroupOfAutomationDevices:») |
| `SetDefragmentationEnabled` (1008) | :843 «navigatorCommand.Status = Command.StatusTypes.Running;» | Solo se la sfrangiatura è `Disabled`. `EnableDefragmentation` (ExecutableCommands.Internal.cs:492 «internal static void EnableDefragmentation()») porta `Defragmentation` a `Enabled`, valore che il setter persiste su DB e da cui dipende l'avvio della sfrangiatura nei gruppi |
| `SetDefragmentationDisabled` (1009) | :886 «private static string HandleSetDefragmentationDisabled(Command navigatorCommand» | Simmetrico del precedente (ExecutableCommands.Internal.cs:385 «internal static void DisableDefragmentation()»); entrambi lasciano intatto il valore `None`, che significa «impianto senza sfrangiatura» |
| `TestRoute` (1010) | ComandToDo.cs:233 «case Command.CommandTypes.MoveLuManual:» | Il pulsante «rotta di prova» dell'Editor: `HandleTestRoute` porta a `Running` e delega a `TestRoute` (ExecutableCommands.Internal.cs:926 «internal static async Task TestRoute(Command navigatorCommand)»), che rifiuta se il navigatore non e' in `Run`, se il dispositivo sorgente non esiste, se la destinazione non esiste o se la sorgente ha gia' una UDC; altrimenti costruisce una `PathMission` senza codice UDC dalla sorgente alla destinazione e la carica, come `LoadLu` ma senza passare da `MoveLuToItem` (3007), che rifiuterebbe `LuId == 0` |
| `ItemExecuteStep` (2001) | :480 «Navigator.RecordNavigatorLog(null, navigatorCommand.ErrorDescription» | Forza l'avanzamento del dispositivo: `_ = item.ExecuteStep()`, non atteso (ExecutableCommands.Internal.cs:509 «RecordNavigatorLog(null, navigatorCommand.ErrorDescription»). È lo stesso `CommandType` che `WriteAlert` usa per registrare gli errori di missione (Navigator.cs:1145 «await DbInfo.SetOperationLockingCell(idCella») |
| `ItemSetOffLine` (2002) | :589 «luCode);» | `SetItemOffOnLine(…, true)` (ExecutableCommands.Internal.cs:1261 «errorDescription = CommandMessages.strErrMoveLuStoreNoPickup;») → `item.SetStatusOffLine(true)`: il dispositivo esce dai percorsi calcolabili, salvo la seconda passata di `PathBuilder`, che accetta l'offline solo se il dispositivo non ha step bloccanti (si veda «La costruzione del percorso») |
| `ItemSetOnLine` (2003) | :616 «private static async Task<string> HandleItemSetOffLine(Command» | Lo stesso metodo con `statusOffLine = false`: rimette il dispositivo in servizio |
| `ItemUnloadLu` (2004) | :669 «private static async Task<string> HandleItemStepDone(Command» | Scarico logico delle UDC dal dispositivo. Rifiutato con `Error` se la proprietà globale `DisableUnloadLu` è attiva, se il dispositivo non è `Ready`/`Warning`, o se non ha UDC (ExecutableCommands.Internal.cs:832-851 «if (DisableUnloadLu)») |
| `ItemStepDone` (2007) | :632 «var item = PathDevices.Instance» | Dichiara concluso a mano lo step in corso, con `StepTypes.Manual` (ExecutableCommands.Internal.cs:747 «internal static async Task ItemStepDone(Command navigatorCommand)»): è la scorciatoia manuale allo stesso `StepDone` a cui arriva il campo (si veda «Ritorno dal campo»). Sui gruppi propaga a `StepDoneGroup` o, se il movimento non era raggruppato, a ciascun figlio con uno step in corso |
| `ItemRejectLu` (2008) | :544 «Navigator.RecordNavigatorLog(item, navigatorCommand.ErrorDescription» | `item.RejectLu()` (ExecutableCommands.Internal.cs:623 «RecordNavigatorLog(null, navigatorCommand.ErrorDescription»). Rifiutato se il dispositivo non è `Ready`/`Warning`, se non ha UDC, o se una delle UDC sta già attraversando un gateway |
| `ItemFindStoringLocation` (2009) | :498 «if (item != null)» | Cerca una cella per le UDC ferme sul dispositivo, tramite `StorageManager.FindStoringLocation()` (ExecutableCommands.Internal.cs:556 «navigatorCommand.ErrorDescription =»); se la trova e il dispositivo ha `AutoExecuteStep`, fa anche partire il movimento (:672 «if (item.AutoExecuteStep)») |
| `MoveLuAutomatic` (3001) | nessuno: marcato `Done` allo `switch` (:241 «case Command.CommandTypes.MoveLuToItem:») | Nessun effetto. È una riga di sola tracciatura, che il motore scrive già chiusa quando avvia uno svuotamento cella automatico (AutomationDevice.cs:482-492 «Status = DeviceStatusTypes.Ready;») |
| `LoadLu` (3002) | :712 «? " " + string.Format(CommandMessages.strUnloadLuNoCode, item.ASI» | Solo in `Run`. Crea una `PathMission` in ingresso (`OperationTypes.Inbound`, `StepTypes.Manual`), la aggiunge ad `ActiveMissions` e la carica sul dispositivo (ExecutableCommands.Internal.cs:808-890 «LuCode = navigatorCommand.LuId > 0 ? await DbInfo.GetLuCode»); non fa nulla se il dispositivo ha già una UDC o se il `LuManager` non l'accetta |
| `MoveLuManual` (3003) | :718 «luCode);» | Sotto il semaforo globale `SyncExtraction`, con timeout di 30 s. `MoveLu(…, Manual)` (ExecutableCommands.Internal.cs:1144 «internal static async Task MoveLu(Command navigatorCommand, ExecuteMoveLuTypes moveLuType)») localizza l'UDC nel magazzino, verifica che non sia già in estrazione o impegnata da una nota, controlla il peso massimo della destinazione, e costruisce il movimento — raggruppato o singolo secondo il tipo del dispositivo di prelievo (:1100 «var destinationItem = PathDevices.Instance», :1107 «await CommandToDoError(navigatorCommand, moveLuType, errorDescription);») |
| `EmptyCellLogically` (3004) | :421 «Navigator.WriteLog("Timeout acquiring extraction lock in» | Sotto `SyncExtraction`. Trova l'UDC e la porta di prelievo della sua cella e delega a `EmptyCellLogically` (ExecutableCommands.Internal.cs:406 «internal static async Task EmptyCellLogically(Command navigatorCommand») |
| `EmptyCellPisically` (3005) | :446 «navigatorCommand.Status = Command.StatusTypes.Error;» | A differenza del precedente avvia una movimentazione reale (ExecutableCommands.Internal.cs:474 «internal static async Task EmptyCellPhisically(Command navigatorCommand») |
| `ChangeLUsStoreLocation` (3006) | :321 «Navigator.WriteLog("Timeout acquiring extraction lock in» | Sotto `SyncExtraction`. **Non muove nulla**: per ogni UDC della cella sorgente non assegnata e non già in estrazione, scrive o aggiorna una riga di estrazione automatica con la nuova destinazione (ExecutableCommands.Internal.cs:262-293 «var lUtoDownload = await WmsBusinessArea.OperazioniUdc.UdcCella»). Il movimento fisico lo farà, se mai, il ciclo di estrazione |
| `MoveLuToItem` (3007) | chiamata diretta allo `switch` (:242 «await ExecutableCommands.ManageMoveLuToItem(navigatorCommand, infoLog);») | Sposta un'UDC verso un dispositivo indicato. `ManageMoveLuToItem` (ExecutableCommands.Internal.cs:1004 «internal static async Task ManageMoveLuToItem(Command navigatorCommand, string infoLog)») valida missione, codice UDC e stato del dispositivo che la tiene, poi chiama `MoveLuToItem` (:338 «private static async Task MoveLuToItem(Command navigatorCommand)»). È il ramo che termina il giro con `return` |
| `MoveReferenceManual` (3008) | chiamata diretta allo `switch` (:217 «case Command.CommandTypes.ItemRejectLu:») | Solo in `Run`, sotto `SyncExtraction`. Movimenta cercando le UDC per riferimento anziché per id (ExecutableCommands.Internal.cs:928 «else if (string.IsNullOrEmpty(await DbInfo.GetLuCode(idUdc)))», `MoveReference` a :437 «private static async Task MoveReference(Command navigatorCommand») |
| `DeleteLuFromExtractionQueue` (4001) | :349 «navigatorCommand.Status = Command.StatusTypes.Error;» | Toglie l'UDC dalla coda di estrazione del WMS (:397 «await DbInfo.DeleteLuFromExtractionQueue(luId);»); fallisce con `Error` se l'UDC non è in coda o se la sua estrazione è già stata elaborata (:382-395 «case -1:») |

Una forma ricorre in molti gestori e conviene isolarla: quando il dispositivo indicato non
si trova in `PathDevices`, il comando viene chiuso come **`Done`**, non come errore. Si
vedono così, in `ComandToDo.cs`, i rami `else` di `HandleChangeLUsStoreLocation`
(:332 «var item = PathDevices.Instance»),
`HandleEmptyCellLogically` (:422 «navigatorCommand.Status = Command.StatusTypes.Error;»),
`HandleEmptyCellPhysically` (:446 «navigatorCommand.Status = Command.StatusTypes.Error;»),
`HandleItemFindStoringLocation` (:532 «: " " + string.Format(CommandMessages.strSearchStorageLuWithCode»),
`HandleLoadLu` (:729 «navigatorCommand.Status = Command.StatusTypes.Error;») e
`HandleMoveLuManual` (:806 «await ExecutableCommands.MoveLu(navigatorCommand»); e, in
`ExecutableCommands.cs`, le uscite anticipate di `ItemExecuteStep`
(:522 «RecordNavigatorLog(item, navigatorCommand.ErrorDescription, LogEntryType.Warning);»), `ItemRejectLu`
(:616 «var item = PathDevices.Instance»), `ItemStepDone`
(:653 «navigatorCommand.Status = Command.StatusTypes.Done;»), `ItemUnloadLu`
(:728 «navigatorCommand.Status = Command.StatusTypes.Done;»), `ManageMoveLuToItem`
(:810 «OperationType = Enums.OperationTypes.Inbound,») e `SetItemOffOnLine`
(:1283 «await BuildSingleMoveLu(navigatorCommand.LuId»). Chi ha inviato il comando lo
vede riuscito; l'impianto non si è mosso.

### Eventi verso l'host

Il motore pubblica gli eventi su un'interfaccia, `IEngineEventSink`
(IEngineEventSink.cs:20 «public interface IEngineEventSink»), e lascia a chi lo
ospita il compito di spingerli fuori. L'istanza vive nella proprietà statica
`Navigator.EventSink` (Navigator.cs:221 «public static Api.IEngineEventSink? EventSink { get; set; }»); se nessuno la
valorizza gli eventi sono scartati (ogni chiamata usa `?.`). Cinque metodi, uno per tipo di
cambiamento — `ItemChanged`, `NavigatorStatusChanged`, `MissionChanged`,
`ExtractionQueueChanged`, `CommandChanged` — e le chiamate arrivano dal thread che produce
l'evento: il dispositivo che cambia stato
(AutomationDevice.cs:703 «Navigator.EventSink?.ItemChanged(this);»,
:725 «Navigator.EventSink?.ItemChanged(this);»,
AutomationDevice.Properties.LiveData.cs:1089 «Navigator.EventSink?.ItemChanged(this);»),
i setter di `Defragmentation` e `NavStatus`
(Navigator.cs:216 «EventSink?.NavigatorStatusChanged();»,
:247 «EventSink?.NavigatorStatusChanged();»), le missioni e i loro step
(ActiveMissions.cs:331 «Navigator.EventSink?.MissionChanged(value, EngineMissionChange.Started);»,
:611 «Navigator.EventSink?.MissionChanged(value,»,
MissionSteps.cs:132 «Navigator.EventSink?.MissionChanged(mission»,
e il cambio di posizione dell'UDC, `PositionChanged`, dal setter di `PositionItemId`:
PathMission.cs:748 «Navigator.EventSink?.MissionChanged(this, Api.EngineMissionChange.PositionChanged);»), la coda di
estrazione (ExtractionQueueStore.cs:99 «context.Deferred.Add(() => Navigator.EventSink?.ExtractionQueueChanged»
e le righe successive,
ExtractionQueueQueries.cs:51 «Navigator.EventSink?.ExtractionQueueChanged(row») e i comandi
(CommandStore.cs:88 «context.Deferred.Add(() => Navigator.EventSink?.CommandChanged» e le righe
successive, CommandGateway.cs:86 «Navigator.EventSink?.CommandChanged(existing);») — e
l'implementazione non deve né bloccare né sollevare: il motore chiama il sink senza guardia
propria, e un'eccezione che nascesse lì romperebbe la scrittura o la decisione da cui l'evento è
nato (IEngineEventSink.cs:20 «public interface IEngineEventSink»).

**Gli inserimenti si annunciano dopo il salvataggio.** Una riga nuova prende la chiave dal
database dentro `SaveChanges`: annunciarla prima manderebbe fuori un `Id` 0, che nessun client
sa riconciliare con la chiave della risposta. Gli store, nei rami di inserimento, accodano perciò
l'annuncio in `DeferredEvents`
(DeferredEvents.cs:17 «internal sealed class DeferredEvents»), e
`WcsDbContext` lo esegue subito dopo il salvataggio
(WcsDbContext.cs:43 «Deferred.Raise();»); un lotto fallito
butta il contesto con la sua coda, quindi l'annuncio non parte per una scrittura che non c'è.
Gli aggiornamenti restano immediati, perché la chiave c'è già.

L'unica implementazione nel repository è `PlantEventRelay`
(PlantEventRelay.cs:31 «public sealed class PlantEventRelay : IEngineEventSink, IHostedService»), in `Wcs.Host`,
assegnata a `Navigator.EventSink` all'avvio del servizio ospitato
(EngineHostedService.cs:69 «Navigator.EventSink = relay;») e tolta
all'arresto (:301 «Navigator.EventSink = null;»).

**Che cosa gira su quale thread.** Il DTO si costruisce *sul thread del motore*, dentro la
chiamata del sink, perché l'oggetto vivo può cambiare un istante dopo; in coda finisce solo
l'invio già confezionato, cioè una funzione che prende l'hub e vi scrive un DTO immutabile.
Mappa e accodamento stanno in un `try`
(PlantEventRelay.cs:119 «private void Relay(string what, Action mapAndPost)»), così un guasto qui
non torna nel motore. Il canale è limitato a diecimila voci e scarta la più vecchia se pieno; un
solo consumatore lo svuota e spinge gli invii sull'hub SignalR
(PlantEventRelay.cs:161 «try { await send(_hub.Clients.All); }») — così il motore non
aspetta mai la rete. Un secondo compito batte il `Watchdog` ogni secondo
(PlantEventRelay.cs:182 «Post(c => c.Watchdog(DateTime.UtcNow));»).

**Gli scarti non sono silenziosi.** Quando il canale scarta, il relay alza un segnale e scrive
una riga di registro per raffica
(PlantEventRelay.cs:140 «private void NoteDrop()»); appena la coda si
svuota, il consumatore manda un `NavigatorStatusChanged`
(PlantEventRelay.cs:198 «private async Task ResyncIfSomethingWasDroppedAsync()»), che per i client
vale anche come «ricarica lo stato completo»: la sequenza delle differenze ha un buco e l'unico
rimedio è rifare le `GET`.

Il client tipizzato è `PlantEventsConnection` di `Wcs.Api.Client`, oggi usato dal solo
`Wcs.Viewer` (`Connector.ConnectAsync`), e la sua riconnessione non si arrende mai
(InfiniteRetryPolicy.cs:16 «public sealed class InfiniteRetryPolicy : IRetryPolicy»); fra motore e
Viewer non c'è più alcun `ProjectReference` né porta TCP: il contratto è la forma dei DTO di
`Wcs.Api.Contracts`.

### Ritorno dal campo

Il campo entra nel motore per **una sola porta**: i sei eventi di `IConnection`. Il motore non
parla mai col protocollo dei PLC: `Wcs.Engine.csproj` non referenzia alcun driver (Wcs.Engine.csproj:28-35 «<PrivateAssets>all</PrivateAssets>»), e le sottoscrizioni ai
sei eventi in tutto il monorepo sono dodici righe, tutte in `DeviceConnections`
(:416-421 «item.AsiMovementIsCompleted += AsiMovementIsCompleted;» e
:441-446 «_tstConnection.AsiMovementIsCompleted += AsiMovementIsCompleted;»). I sei eventi
sono descritti membro per membro in
architettura.md (non pubblicato) di
`Wcs.Connection.Interface`, insieme a chi li solleva; qui interessa cosa ne fa il motore.

La sottoscrizione avviene in due punti, entrambi in `DeviceConnections`: sulle connessioni
«gestite» dentro `OpenConnection`
(DeviceConnections.cs:416-421 «item.AsiMovementIsCompleted += AsiMovementIsCompleted;») e
sulla connessione di test singleton dentro `GetTestConnection`
(:441-446 «_tstConnection.AsiMovementIsCompleted += AsiMovementIsCompleted;»); la disiscrizione,
solo per le prime, in `CloseConnection`
(:151-156 «item.AsiSendLuData -= AsiSendLuData;»). Il gestore è sempre
un metodo statico `internal` di `Navigator`, e in quattro casi su sei — `AsiEvent`,
`AsiMovementIsCompleted`, `AsiSendLuData`, `LuUnloadedFromAsi` — **porta esattamente lo
stesso nome dell'evento**: leggendo `item.AsiEvent += AsiEvent` va tenuto presente che il
primo identificatore è l'evento di `IConnection` e il secondo il metodo di `Navigator`. Dei
due rimanenti, `NewLuOnAsi` è agganciato a `NewLuOnASI` (stesso nome, maiuscole diverse) e
`AsiRequestsLuInfo` a `AsiRequestsLuData`.

| Evento | Gestore | Cosa fa il gestore |
|---|---|---|
| `AsiEvent` | Navigator.cs:338 «var item = PathDevices.Instance» | risolve il dispositivo per `Asi`/`Subitem`/`Partition` e delega a `item.ConnectionAsiEvent(e)` (AutomationDevice.cs:246 «_connectionManager?.AsiEvent(e);»), che a sua volta chiama `AutomationDeviceConnectionManager.AsiEvent`, dichiarato `public void` (AutomationDeviceConnectionManager.cs:27 «public void AsiEvent(AsiEventEventArgs? e)»): con `AsiRequestsLuInfo`, è uno dei due gestori che si esauriscono in modo interamente sincrono. **E si esauriscono perché non fanno nulla**: il corpo di quel metodo è vuoto, fra le graffe c'è solo un commento (:29 «// Implementazione del metodo ConnectionAsiEvent originale»), per cui l'intera catena `AsiEvent` → `ConnectionAsiEvent` → `AsiEvent` non produce alcun effetto |
| `AsiMovementIsCompleted` | :363 «var item = PathDevices.Instance» | risolve il dispositivo per `ItemGuid` e lancia `_ = item.ConnectionAsiMovementIsCompleted(e)` (:442 «var luId = item.LoadedLu.LuId;») — chiamata `async` **scartata**, non attesa: il `try`/`catch` che la avvolge (:442 «var luId = item.LoadedLu.LuId;») intercetta solo ciò che esplode prima del primo `await` |
| `AsiRequestsLuInfo` | :378 «internal static void AsiRequestsLuData(object sender» | non delega al dispositivo: risponde da sé. Legge l'UDC caricata, ne prende i dati dal database con `DbInfo.GetLuData` (:489 «if (SwitchLuLengthVsLuWidth && e.LuDataFromAsiResult != null)») e li rispedisce al campo con `SendLuDataToAsi` (:461 «item.DeviceConnection?.SendLuDataToAsi(GetAskingRunAsi(item)») — l'unica invocazione di `SendLuDataToAsi` in tutto il monorepo, oltre alla dichiarazione e alle due implementazioni |
| `AsiSendLuData` | :475 «internal static void AsiSendLuData(object sender» | se la proprietà globale `SwitchLuLengthVsLuWidth` è attiva scambia lunghezza e larghezza del dato ricevuto (:491 «(e.LuDataFromAsiResult.LuLength, e.LuDataFromAsiResult.LuWidth) =»), poi delega a `item.ConnectionAsiSendLuData(e)` dentro un `Task.Run` con `try`/`catch` proprio (:436-446 «Task.Run(async () =>») |
| `LuUnloadedFromAsi` | :857 «catch (Exception ex)» | `async void`: risolve il dispositivo per indirizzo e attende `item.LuUnloadedFromAsi(StepTypes.Automatic)` (:1046 «await item.ConnectionNewLuOnASI(e);»). Il `try`/`catch` interno copre l'intero corpo, per cui l'`async void` non porta qui eccezioni non osservate |
| `NewLuOnAsi` | :928 «catch (Exception ex)» | `async void` con la stessa forma: attende `item.ConnectionNewLuOnASI(e)` (:1039 «$"NewLuOnASI: dispositivo non trovato per Asi: {e.Data.Asi}, Subitem:»), da cui nasce la missione della UDC appena comparsa |

Tutti e sei cominciano con lo stesso schema — `if (e == null) return;`, ricerca del
dispositivo in `PathDevices`, guardia sul risultato — e **un evento che non trova il proprio
dispositivo lascia una riga di Avviso** che dice quale indirizzo non è stato risolto
(Navigator.cs:371 «$"AsiEvent: dispositivo non trovato per Asi: {e.Data?.Asi}, Subitem:»,
:395 «WriteLog($"AsiMovementIsCompleted: dispositivo non trovato per ItemGuid:»,
:437 «$"AsiRequestsLuData: dispositivo non trovato o senza UDC caricata per Asi:»,
:483 «WriteLog($"AsiSendLuData: dispositivo non trovato per ItemGuid: {e.ItemGuid}"»,
:1014 «$"LuUnloadedFromAsi: dispositivo non trovato per Asi: {e.Data?.Asi}, Subitem:»,
:1039 «$"NewLuOnASI: dispositivo non trovato per Asi: {e.Data.Asi}, Subitem:»).

Fino al 2026-09-10 questo capoverso diceva «*viene scartato in silenzio, senza log*», e la frase
era falsa due volte. Le sei righe c'erano già; e passavano da `Navigator.WriteLog`, che usciva
subito con `ActivateDebugLog` falso — il valore distribuito — quindi la frase era vera per un
motivo diverso da quello scritto. Dal commit `a37400f` (voce `D94` del registro) `WriteLog` lascia
passare **sempre** Errore e Avviso, e l'host collega un `IWriter` al proprio registro: queste sei
righe, che sono tutte di Avviso, arrivano da qualche parte anche con la configurazione di serie.
Resta spento per default tutto ciò che è **Informazione**, e quello è ancora `D94`.

La guardia è `if (item == null) return;` in cinque casi
(:340 «if (item == null)»,
:365 «if (item == null)»,
:423 «if (item == null)»,
:825 «while (!_cancelToken.IsCancellationRequested)»,
:1069 «newLog.PARTITION = sourceItem.PARTITION;»); nel sesto, `AsiRequestsLuData`, è
`if (item?.LoadedLu == null) return;` (:434 «if (item?.LoadedLu == null)»), cioè
più severa: scarta in silenzio anche il dispositivo che esiste ma non ha UDC caricata, e in
quel caso il campo resta senza risposta alla propria richiesta di dati.

**Il percorso completo fino a `StepDone`.** È il ritorno che chiude un movimento chiesto al
campo, e vale la pena vederlo per intero:

```
IConnection.AsiMovementIsCompleted                     (l'implementazione lo solleva)
  → Navigator.AsiMovementIsCompleted                   (risolve il device per ItemGuid)
  → AutomationDevice.ConnectionAsiMovementIsCompleted  (chiamata scartata, non attesa)
  → AutomationDeviceConnectionManager.AsiMovementIsCompleted   (switch su e.Result)
  → HandleMovementSuccess                              (solo se Result == Success)
  → AutomationDevice.StepDone                          (facciata di due righe)
  → AutomationDeviceStepExecutor.StepDone
```

Le righe, nell'ordine: Navigator.cs:387 «internal static void AsiMovementIsCompleted(object sender»,
AutomationDevice.cs:253 «public async Task ConnectionAsiMovementIsCompleted»,
AutomationDeviceConnectionManager.cs:41 «internal async Task AsiMovementIsCompleted»,
:640 «private async Task HandleMovementSuccess»,
AutomationDevice.cs:475 «RemoveDevicesReadyToTransferLu(stepToDo.DestinationItem);»,
AutomationDeviceStepExecutor.cs:28 «public async Task StepDone(Guid idStepDone, StepTypes stepType, int».

Lo smistamento sta nello `switch` su `e.Result`
(AutomationDeviceConnectionManager.cs:46 «switch (e.Result)»):
`Success` porta a `HandleMovementSuccess`
(:640 «private async Task HandleMovementSuccess»),
`Error` a `HandleMovementError`
(:625 «private async Task HandleMovementError(AsiMovementIsCompletedEventArgs»),
`TimeOut` e `ConnectionError` insieme a `HandleMovementTimeoutOrConnectionError`
(:679 «private async Task HandleMovementTimeoutOrConnectionError»), e
`None` non fa nulla
(:61 «case ResultCodeTypes.None:»). Dentro
il ramo di successo la strada si biforca ancora: sui gruppi (`GroupOfAutomationDevices` o
`GroupItemId != 0`) si va a `StepDoneGroup`
(:650 «await device.StepDoneGroup(e.StepGuid, StepTypes.Automatic);»,
:669 «await device.StepDone(e.StepGuid, StepTypes.Automatic, depth);»), sui
dispositivi singoli a `StepDone`
(:652 «await device.StepDone(e.StepGuid, StepTypes.Automatic);»,
:669 «await device.StepDone(e.StepGuid, StepTypes.Automatic, depth);»); e la
profondità rilevata viene passata solo quando il campo ha restituito una posizione finale
(`e.MoveLuResult != null`,
:647 «if (e.MoveLuResult == null)»).

Dentro `AutomationDeviceConnectionManager`, `device.StepDone(` compare in due sole righe, la
609 e la 626, entrambe nel corpo di `HandleMovementSuccess`: degli eventi dal campo è quindi
`AsiMovementIsCompleted`, e solo con esito `Success`, a condurre a `StepDone` per questa via
diretta.

`AutomationDevice.StepDone` (AutomationDevice.cs:475 «RemoveDevicesReadyToTransferLu(stepToDo.DestinationItem);»)
è però una facciata di due righe, e i suoi punti di chiamata in tutta l'area `WCS/` sono
cinque (grep ricorsivo su `\.StepDone` in tutto il monorepo, `AutomationDevice.cs:534` escluso
perché e' `StepExecutor.StepDone`, un'altra facciata): i due appena visti, con
`StepTypes.Automatic`; tre in `ExecutableCommands.ItemStepDone`
(ExecutableCommands.Internal.cs:685 «switch (item.DeviceType)»,
:790 «await item.StepDone(item.RunningStepId, Enums.StepTypes.Manual);»,
:693 «await childItem?.StepDone(childItem.RunningStepId»), con `StepTypes.Manual`, cioè il
comando `ItemStepDone` della tabella qui sopra. L'Editor accoda il comando `ItemStepDone` con
`POST /commands`
(AutomationItemManager.cs:38 «internal static Task ItemStepDone(Node? contextMenuSelectedNode) =>» (non pubblicato)), e il Client fa lo stesso da
`UsrNavigatorViewerNew.ItemStepDone`
(UsrNavigatorViewerNew.cs:1149 «await NavigatorSession.SendCommandAsync(new CommandRequest((int)CommandTypes.ItemStepDone,» (non pubblicato)).
Il `StepTypes`
che arriva a `StepExecutor.StepDone`
(AutomationDeviceStepExecutor.cs:28 «public async Task StepDone(Guid idStepDone, StepTypes stepType, int»)
è quindi l'unica traccia di *chi* ha dichiarato concluso lo step.

### Come si aprono le connessioni

Vanno tenuti distinti tre momenti, con tre metodi diversi e tre chiamanti diversi.

**Costruire** — `DeviceConnections.Initialize()`
(DeviceConnections.cs:133 «_tstConnection = null;»),
chiamato da `LoadNavigatorData` (Navigator.cs:825 «while (!_cancelToken.IsCancellationRequested)»):
svuota la lista e chiede al driver configurato (`IConnectionFactory`) una connessione per ogni riga
restituita da `DbInfo.GetPlCs()`, con indirizzi e porte lette dalle colonne del database
(:150-156 «item.LuUnloadedFromAsi -= LuUnloadedFromAsi;»). Le righe senza
`PLC_ADDRESS` o `LOCAL_ADDRESS` vengono saltate
(:150 «item.LuUnloadedFromAsi -= LuUnloadedFromAsi;»). Nessun socket viene
aperto qui.

**Aprire** — `DeviceConnections.OpenConnection()`
(:297 «IConnection missionStep;»). La catena dei chiamanti
è lineare e, verificata con grep ricorsivo su tutto il repository, ha un solo ramo:
`OpenConnection` è invocato unicamente da `Navigator.OpenDeviceConnections`
(Navigator.cs:1050 «WriteLog($"Error On NewLuOnAsi Description:»), a sua volta invocato unicamente
da `ExecutableCommands.StartNavigator`
(ExecutableCommands.cs:124 «OpenDeviceConnections();»), e solo quando
non si sta ripartendo da `Pause`. Le connessioni si aprono quindi al comando `SetNavigatorRun`
dato da fermo, mai in altro momento.

**Chiudere** — `DeviceConnections.CloseConnection()`
(:143 «t.DeviceConnection = null;»), attraverso
`Navigator.ClosePlcConnections` (Navigator.cs:512 «internal static void ClosePlcConnections()»),
chiamato dal solo `ExecutableCommands.StopNavigator`
(ExecutableCommands.cs:142 «ClosePlcConnections();»). E
`StopNavigator` ha a sua volta due chiamanti soltanto, nessuno dei quali è il comando di
arresto: `ReloadRouteDiagram`, quando ferma il motore per ricaricare
(ExecutableCommands.cs:68 «StopNavigator();»), e l'Editor dalla
propria interfaccia
(FrmEDITOR.Private.cs:1136 «await OpenDiagramAs(editor, OpenFileDialog1.FileName» (non pubblicato)). Il comando
`SetNavigatorStop` passa invece da `PauseNavigator`, che non chiude nulla: si veda la tabella
dei comandi. Esiste anche
un terzo metodo, `DeviceConnections.Clear()`
(:84 «public IConnection? this»), l'unico che chiami
`IConnection.Dispose()` — è l'unica riga dell'area che lo faccia
(:93 «if (value == null) return;») — ma grep ricorsivo su
`DeviceConnections.` in tutto il repository non gli trova alcun chiamante, dentro né fuori
l'area. Conferma il «da verificare» lasciato aperto
in architettura.md (non pubblicato): quel percorso non è
eseguito.

#### Cosa fa davvero `OpenConnection`

Il metodo si legge in due tempi. **Il primo ciclo** assegna a ciascun dispositivo la propria
connessione (:297-369 «IConnection missionStep;»), saltando
i dispositivi di tipo `Storage` e `None`
(:348 «if (item?.DeviceType == Enums.DeviceTypes.Storage || item?.DeviceType == Enums.DeviceTypes.None) continue;») e smistando sul
`ConnectionType` del dispositivo
(:350 «switch (item.ConnectionType)»), che è la colonna
`ConnectionType` di `PlantItems`, nel database del WCS
(PlantStore.cs:142 «device.ConnectionType = (ConnectionTypes)row.ConnectionType;»). **Il secondo
ciclo** (:375-385 «foreach (var item in Snapshot().Where(item => item.Managed))») prende dalla
lista `Items` le connessioni con `Managed == true`, vi aggancia i sei eventi e chiama
`item.OpenConnection()`.

Nel primo ciclo, per `ConnectionType.Connected`
(:356 «case Enums.ConnectionTypes.Connected:»), il dispositivo trova la propria
connessione reale con l'indicizzatore di `DeviceConnections`, cercando l'`Id` uguale al proprio
`DeviceConnectionId`
(:363 «var communicatorToPlc = Instance») — la stessa relazione su cui, nel
database del WCS, il dispositivo indica la propria riga `PlcConnections` — ma solo se
`DeviceConnectionId` è maggiore di zero
(:357 «if (item.DeviceConnectionId > 0)»). Tre esiti:

1. **Nessuna connessione trovata con quell'`Id`**
   (:365 «if (communicatorToPlc == null)»): il dispositivo usa il simulatore
   (:340 «item.DeviceConnection = GetTestConnection();») e la scelta finisce nel
   registro persistente, non in `WriteLog`, che tace se `ActivateDebugLog` è spento — un
   dispositivo «Connected» servito dal simulatore deve risultare all'operatore anche senza
   diagnostica accesa
   (:336 «RecordNavigatorLog(item,»).
2. **La connessione esiste**: il dispositivo la riceve
   (:383 «item.DeviceConnection = communicatorToPlc;»), e quella connessione viene
   marcata `Managed = true`
   (:384 «communicatorToPlc.Managed = true;») — in tutto il repository, l'unico
   punto che porta `Managed` a `true` su una `IConnection` (grep ricorsivo su
   `Managed\s*=\s*true`: gli altri risultati riguardano un campo privato
   `_loadingChangedManaged` del client WinForms, di altro tipo e altra classe).
3. **`DeviceConnectionId` non è valorizzato** (`<= 0`): il dispositivo resta senza
   connessione, e lo dice nel registro persistente con `LogEntryType.Error`
   (:353 «RecordNavigatorLog(item,») — è la configurazione sbagliata più
   probabile, un dispositivo «Connected» senza PLC assegnato.

Un `ConnectionType` fuori dai due valori validi (`Test`/`Connected`) non solleva un'eccezione:
il ramo `default`
(:399 «default:») registra un errore sul registro persistente e salta il
dispositivo, che resta senza connessione, invece di far fallire l'intero avvio
(:368 «RecordNavigatorLog(item,»).

Il secondo ciclo, `Snapshot().Where(item => item.Managed)`
(:375 «foreach (var item in Snapshot().Where(item => item.Managed))»), trova quindi
ogni dispositivo `Connected` la cui connessione reale è stata risolta al passo precedente: vi
aggancia i sei eventi
(:416-421 «item.AsiMovementIsCompleted += AsiMovementIsCompleted;») e chiama
`item.OpenConnection()`
(:423 «item.OpenConnection();») — il metodo con cui il driver apre il collegamento col suo PLC.

`GetTestConnection()`
(:436 «private static Wcs.Connection.Test.Connection GetTestConnection()») crea al primo uso una sola
`WCS.Connection.Test.Connection` con `Id = 99`
(:440 «_tstConnection = new Wcs.Connection.Test.Connection(99);»), le aggancia i sei
gestori, la aggiunge a `Items`
(:450 «Instance.Add(_tstConnection);») e la conserva nel campo statico
`_tstConnection` (:29 «private static Wcs.Connection.Test.Connection? _tstConnection;»);
tutte le chiamate successive restituiscono la stessa istanza
(:438 «if (_tstConnection != null) return _tstConnection;»), per cui i gestori sono
sottoscritti una volta sola e non si duplicano. `Clear()` azzera anche il campo statico
(:133 «_tstConnection = null;»), così una connessione di test disposta non resta
raggiungibile da una chiamata successiva a `GetTestConnection()`.

Va detto anche come si chiude. `CloseConnection()`, il metodo statico
(:136 «public static void CloseConnection()») — da non confondere con
l'omonimo di `IConnection` — azzera `DeviceConnection` su **tutti** i dispositivi del grafo
(:143 «t.DeviceConnection = null;»), poi, filtrando sullo stesso
`Where(item => item.Managed)`, disiscrive i sei eventi, chiama `item.CloseConnection()` e
riporta `Managed` a `false`
(:146-157 «foreach (var item in Snapshot().Where(item => item.Managed))»). `Clear()`, invece, chiude e dispone
**ogni** connessione presente in `Items`, senza filtro: è il metodo che `Initialize()` chiama
per primo, per non abbandonare le connessioni della sessione precedente senza `Dispose`
(:112-134 «public static void Clear()»).

Il difetto per cui il secondo ciclo di `OpenConnection` non trovava mai nulla, e ogni
dispositivo finiva collegato al simulatore indipendentemente dalla propria configurazione, era
la voce 27 del registro dei «Punti di attrito», in coda al documento.

## Il dispositivo

`AutomationDevice` è una `partial class` divisa in due file
(AutomationDevice.cs:24 «public partial class AutomationDevice : IDisposable»): il
comportamento in `AutomationDevice.cs` (1131 righe), le proprietà in
`AutomationDevice.Properties.cs` (2657). Non fa quasi nulla di persona: costruisce sette
collaboratori nel proprio `Initialize()` e delega a loro.

### `AutomationDevice` e i suoi manager

`Initialize()` (AutomationDevice.cs:1227 «_executionSemaphore?.Dispose();»)
è chiamato da entrambi i costruttori
(:92 «public AutomationDevice()»,
:101 «public AutomationDevice(Guid value)») e costruisce i sette manager in
sette righe consecutive
(:1229-1235 «Navigator.StatusChanged -= Navigator_StatusChanged;»). Nessuno dei sette è
sostituibile dall'esterno: non c'è iniezione, il tipo concreto è scritto nel `new`.

| Manager | Campo | Responsabilità | Stato che tiene |
|---|---|---|---|
| `AutomationDeviceStepExecutor` (AutomationDeviceStepExecutor.cs:11 «public partial class AutomationDeviceStepExecutor(AutomationDevice») | `StepExecutor`, **`internal`** (:42 «internal AutomationDeviceStepExecutor? StepExecutor;») | esegue i movimenti verso il campo e chiude gli step: `StepDone` (:28 «public async Task StepDone(Guid idStepDone, StepTypes stepType, int»), `ExecuteGroupedMOVE_LU_TO` (:76 «internal async Task ExecuteGroupedMOVE_LU_TO(List<MissionStep> value)»), `ExecuteSingleMOVE_LU_TO` (:163 «internal async Task ExecuteSingleMOVE_LU_TO(List<MissionStep> value)»), `StartPickUpLu` (:290 «internal async Task StartPickUpLu(PathMission? value)»), `StartReceivingLu` (:321 «internal void StartReceivingLu(AutomationDevice? item)») | nessuno di proprio: l'unico campo è il riferimento statico alla collezione singleton degli step (:16 «private static readonly MissionSteps MissionSteps =») |
| `AutomationDeviceLuManager` (AutomationDeviceLuManager.cs:17 «public class AutomationDeviceLuManager(AutomationDevice device)») | `LuManager`, **`internal`** (:37 «internal AutomationDeviceLuManager? LuManager;») | decide se una UDC può salire o scendere: `CanAcceptLu` (:137 «internal bool CanAcceptLu(PathMission? luToCheck)»), `CanPickUpLu` (:194 «internal bool CanPickUpLu(PathMission? luToCheck)»), `StepIsAvailable` (:330 «internal StepAvailableStatusTypes StepIsAvailable(PathMission?»); e la esegue: `LoadLu` (:41 «public async Task LoadLu(Guid missionId, bool activateExecuteStep =»), `LuUnloadedFromAsi` (:73 «public async Task LuUnloadedFromAsi(StepTypes value)»), `RejectLu` (:116 «public async Task RejectLu()»), `CheckGetFromItem` (:278 «internal async Task CheckGetFromItem()») | due contatori per la distribuzione sui buffer (:25 «private int _indexLastBufferDestination;», :30 «private decimal _lastIdLavorazioneBufferDestination;») |
| `AutomationDeviceGroupManager` (AutomationDeviceGroupManager.cs:18 «public class AutomationDeviceGroupManager(AutomationDevice device) :») | `_groupManager`, `private` (:71 «private AutomationDeviceGroupManager? _groupManager;») | i movimenti raggruppati e la sfrangiatura: `StepDoneGroup` (:68 «public async Task StepDoneGroup(Guid groupedMovementId, StepTypes»), il ciclo `GroupExecuteStep` (:475 «private async Task GroupExecuteStep()»), `ProcessGroupExtractionLu` (:826 «case RecordStorageTypes.ConvertDepthToPartition:») | il `CancellationTokenSource` del proprio ciclo (:26 «internal CancellationTokenSource? CancellationGroupExStepTokenSource;»), un contatore di tentativi e l'ora dell'ultimo controllo (:40 «private int _numCheckLUsToPick;», :45 «private DateTime _timeLastCheck;») |
| `AutomationDeviceStorageManager` (AutomationDeviceStorageManager.cs:17 «public class AutomationDeviceStorageManager(AutomationDevice device) :») | `StorageManager`, **`internal`** (:47 «internal AutomationDeviceStorageManager? StorageManager;») | il magazzino: `FindStoringLocation` (:101 «internal async Task<bool> FindStoringLocation()»), `StartStoreLu` (:111 «while (!_cancelExtractionToken.IsCancellationRequested)») e il ciclo di estrazione `CheckExtraction` (:356 «lu.DestinationAsi = string.Empty;») | il `CancellationTokenSource` del ciclo di estrazione (:25 «internal CancellationTokenSource? CancellationExtractionTokenSource;») |
| `AutomationDeviceConnectionManager` (AutomationDeviceConnectionManager.cs:18 «public class AutomationDeviceConnectionManager(AutomationDevice device)») | `_connectionManager`, `private` (:71 «private AutomationDeviceErrorHandler? _errorHandler;») | il ritorno dal campo, descritto in «Il dialogo con il campo»: `AsiMovementIsCompleted` (:41 «internal async Task AsiMovementIsCompleted»), `AsiSendLuData` (:78 «internal async Task AsiSendLuData(AsiSendLuDataEventArgs? e)»), `NewLuOnASI` (:100 «_ = device.ExecuteStep();») | nessuno: la classe non dichiara alcun campo |
| `AutomationDeviceErrorHandler` (AutomationDeviceErrorHandler.cs:11 «public class AutomationDeviceErrorHandler(AutomationDevice device)») | `_errorHandler`, `private` (:83 «private AutomationDeviceStateMachine? _stateMachine;») | due soli metodi, `ClearError` (:49 «public void ClearError()») e `ResetError` (:79 «public void ResetError()»): riportano il dispositivo a `Ready`, azzerano gli step e rilanciano `ExecuteStep` se `AutoExecuteStep` | nessuno |
| `AutomationDeviceStateMachine` (AutomationDeviceStateMachine.cs:11 «public class AutomationDeviceStateMachine(AutomationDevice device) :») | `_stateMachine`, `private` (:83 «private AutomationDeviceStateMachine? _stateMachine;») | è lo stato del dispositivo, non un suo custode: si veda la sottosezione dedicata | `_currentState` e il proprio lock (:46 «private volatile DeviceStatusTypes _currentState =», :26 «private readonly object _stateLock = new();») |

La divisione fra `internal` e `private` non è casuale ed è il confine che conta per chi
rifattorizza. I tre campi `internal` — `StepExecutor`, `LuManager`, `StorageManager` — sono
letti **dall'esterno del dispositivo**: le strategie di esecuzione e gli altri manager si
chiamano fra loro nella forma `device.StepExecutor?.…`, `device.LuManager?.…`,
`device.StorageManager?.…` (per esempio
Standard.cs:41 «device.LuManager?.CheckLuToReceive();»,
:143 «device.StepExecutor?.AddDevicesReadyToTransferLu(crossRoadItem);»,
:207 «// StorageManager e' costruito da Initialize e non torna mai nullo: la»,
AutomationDeviceLuManager.cs:259 «if (device.StepExecutor != null) await»,
AutomationDeviceGroupManager.cs:1139 «if (device.StepExecutor != null) await device.StepExecutor.ExecuteGroupedMOVE_LU_TO(stepsToDo);»).
Gli altri quattro sono raggiungibili solo attraverso metodi-facciata di `AutomationDevice`,
tutti di poche righe: `ClearError` (:235 «public void ClearError()»),
`ConnectionAsiEvent` (:244 «public void ConnectionAsiEvent(AsiEventEventArgs? e)»),
`StepDoneGroup` (:448 «itemToSetBusy?.ReleaseIncomingStep(stepToDo.StepId);»),
`SetStatusOffLine` (:437 «if (_stateMachine != null) await _stateMachine.SetStatusOffLine(value);»).

**Costruire un dispositivo ha effetti.** Oltre ai sette `new`, `Initialize` legge o scrive la
riga del dispositivo sul database in modo **sincrono**
(:1083 «GroupExecuteStepRunning = false;»:
`if (!ORM.AutomationDevice.Get(this)) ORM.AutomationDevice.Update(this);`), crea il
`CancellationTokenSource` di dismissione
(:1205 «if (Status != DeviceStatusTypes.OffLine)»), iscrive il dispositivo a
`Navigator.StatusChanged` (:1280 «RunObserved(() => ExecuteStep());») e
lancia un primo `ExecuteStep()` in un `Task.Run`
(:1244 «await strategy.ExecuteAsync(cancellationToken);»). Poiché `PathDevices.Add(Guid)`
costruisce il device quando manca (PathDevices.cs:252 «var device = _items.GetOrAdd(guid, created);»),
caricare il grafo significa fare una query per nodo e far partire un'esecuzione per nodo.

`Dispose(bool)` (:1215 «private void Dispose(bool disposing)») fa il percorso
inverso: annulla il token, chiama `StopActiveProcesses()`
(:1222 «StopActiveProcesses();»), dispone il gestore di gruppo, quello di magazzino e la
macchina a stati, più il semaforo di esecuzione (:1104-1107 «_groupManager?.Dispose();») e si
disiscrive da `Navigator.StatusChanged`
(:1229 «Navigator.StatusChanged -= Navigator_StatusChanged;»). Va detto però che **quattro dei
sette `Dispose` avevano il corpo vuoto**, e con la voce `D58` sono stati tolti insieme a
`IDisposable`: `StepExecutor`, `LuManager`, `ConnectionManager` ed `ErrorHandler` non hanno nulla
da rilasciare.
Fanno qualcosa solo `GroupManager` e `StorageManager`, che annullano il proprio
`CancellationTokenSource` (:54 «public void Dispose()»,
:85 «public void Dispose()»), e
`StateMachine`, che dispone il proprio lock
(:70 «public void Dispose()»).

**Il semaforo di esecuzione.** Tutta l'attività di un dispositivo passa da `ExecuteStep`
(:333 «public async Task<bool> ExecuteStep(int delay = 0)»), e la sua forma va letta per
intero perché è la sola difesa contro l'esecuzione concorrente sullo stesso dispositivo:

1. attende l'eventuale pausa richiesta, **prima** di tutto il resto e fuori dal semaforo
   (:346 «if (delay > 0)») — sei chiamanti la passano
   diversa da zero: ExecutableCommands.Private.cs:449 «_ = BackgroundWork.Track(positionItem.ExecuteStep(1000));»
   e :450 «_ = BackgroundWork.Track(lastPositionItem.ExecuteStep(1000));» con 1000 ms,
   AutomationDeviceGroupManager.cs:267 «_ = item.ExecuteStep(100);»
   con 100, e tre punti dello `StepExecutor` con 500: `ExecuteMoveTo`
   (:848 «_ = item.ExecuteStep(500);»),
   `FinalizeStepProcessing`, dopo ogni step completato
   (:1060 «_ = device.ExecuteStep(500);»),
   e `ProcessReadyDevicesWithoutLu`
   (:1694 «_ = item.ExecuteStep(500);»).
   Fino alla voce 41 (10/10/2026) la pausa si faceva dopo la presa del semaforo, e per tutta la sua
   durata il dispositivo risultava impegnato: il commit del cambio di destinazione
   (`TryHoldExecution`, oggi `TryHoldExecutionForTesting`, che dal compito 5 della voce 41 il commit
   non prende più: confronta la versione della rotta) era rifiutato perché «il dispositivo sta eseguendo» mentre dormiva soltanto,
   e con lui i comandi dell'operatore che guardano `IsBusyWithCommand`, la rete del gettone appeso
   e gli altri `ExecuteStep`. Quella posizione non era una scelta: in Atomind la pausa era un
   `Thread.Sleep` prima della guardia, ed è finita dopo il `WaitAsync` nel passaggio ad `async`. La
   pausa sta dentro il `try` che cattura la cancellazione (D41), con il semaforo non ancora preso;
2. esce se il navigatore non è in `Run`, guardato **dopo** la pausa
   (:350 «if (Navigator.NavStatus != NavigatorStatusTypes.Run)»): un passaggio a Stop
   mentre la pausa dura ferma l'esecuzione;
3. prende `_executionSemaphore`, un `SemaphoreSlim(1,1)`
   (AutomationDevice.Properties.cs:24 «private readonly SemaphoreSlim _executionSemaphore = new(1, 1);»),
   **con timeout zero** (:383 «if (!await _executionSemaphore.WaitAsync(0, CancelDisposingToken))»): se
   un'esecuzione è già in corso la chiamata **viene rifiutata**, non accodata e non ritentata. Non è
   una coda, è un filtro. Il rifiuto però **non è più silenzioso**: dalla decisione del 2026-09-02
   `ExecuteStep` restituisce `false` e scrive una riga di log, e chi trova il semaforo preso di
   solito è già stato fermato prima dalla guardia `IsBusyWithCommand`, che comprende
   `ExecuteStepIsRunning` (AutomationDevice.Properties.LiveData.cs:306 «public bool ExecuteStepIsRunning»);
   al `WaitAsync(0)` arriva solo chi passa nella finestra fra i due controlli;
4. esegue `ExecuteStepCoreAsync` dentro lo stesso `try`
   (:398 «await ExecuteStepCoreAsync(CancelDisposingToken);») e rilascia il semaforo nel
   `finally`, solo se l'ha preso (:417 «_executionSemaphore.Release();»); dopo il
   rilascio, se un avvio che ha rinunciato al gettone durante l'esecuzione ha chiesto di riprovare,
   lancia un'esecuzione nuova (`ExecuteAgainWhenFree`, risposta 4 del proprietario dell'11/10).

Il semaforo esclude solo le esecuzioni dello stesso dispositivo. La **rotta di una missione** la
scrivono in tanti, da thread diversi e con il gestionale in mezzo (l'ingresso fra le attive, l'arrivo,
il giro delle rotte, lo spostamento, i rigetti, il cambio di destinazione dell'operatore, la strategia
`Standard`, gli avvii degli step), e dalla voce 41 (ottobre 2026) li mette d'accordo il **disegno C**
della ricognizione (una specifica interna, non pubblicata):

- ogni missione ha un lucchetto della rotta, breve e sincrono, sotto cui non si chiama il gestionale e
  non si attende niente (PathMission.cs:87 «private readonly object _route = new();»),
  e una versione della rotta che sale a ogni scrittura degli step, a ogni rimozione, a ogni cambio di
  destinazione o di posizione e a ogni gettone di uno step che parte
  (:107 «private long _routeVersion;»);
- chi calcola una rotta legge la versione, calcola fuori da ogni lucchetto e scrive con una primitiva
  sola, che confronta la versione e scrive i campi e gli step tutti insieme
  (PathMission.Methods.cs:576 «internal RouteWriteOutcome TryWriteRoute(»);
  lo spostamento e il rigetto scrivono senza confronto, perché vincono
  (:646 «internal RouteWriteOutcome MoveTo(»);
- chi perde il confronto non ritenta da sé: la missione torna al giro delle rotte del navigatore
  (:226 «private void BackToTheRouteLoop()»), che la
  accoda una volta sola (RoutesToBuild.cs:137 «public bool EnqueueIfAbsent(PathMission mission)»);
- l'avvio di uno step scrive il suo gettone sotto lo stesso lucchetto, solo se la versione è quella
  su cui ha letto lo step (:1951 «internal bool TryTakeTheStepToken(long seenVersion, MissionStep step, Action token, out long versionAfter)»),
  e chi ricostruisce guarda gli step in volo e toglie la rotta nello stesso colpo
  (:1873 «internal MissionStep? RemoveTheRouteUnlessAStepIsInFlight(»):
  o il gettone c'è già e la rotta non si tocca, o la rotta è tolta prima e l'avvio rinuncia; lo stesso
  l'uscita dal motore (:1907 «internal MissionStep? LeaveUnlessAStepIsInFlight(Action leave)»);
- la rimozione della strategia `Standard`, che vede la missione senza uno step da fare, toglie la rotta
  solo se è ancora quella vista (:674 «internal bool RemoveTheRouteSeenWithoutAStepToDo(»),
  e per questo il commit del cambio di destinazione non prende più il semaforo della posizione.

Le risposte del proprietario dell'11/10 alla revisione finale della voce 41 completano il disegno:

- lo step in volo vince su tutti, anche sul rigetto dal campo e sul comando «sposta l'UDC», e si
  riconosce da tutti i gettoni con cui parte: la sorgente, la destinazione (il prelievo) e il gruppo
  (:1808 «internal MissionStep? StepInFlight()»);
- quello che un perdente o un rimandato lascia a metà lo rifà il giro delle rotte, quando lo step
  che lo bloccava è finito: il rigetto dal campo rimandato, la conversione di un `PickUpAndStoreAgain`
  arrivato, la rotta di un rigetto che l'ha persa contro uno spostamento
  (:3147 «internal async Task<AutomationDevice.RouteBuildStatusTypes?> RedoInTheRouteLoopAsync()»);
- l'avvio che rinuncia al gettone scrive lo stato della missione, il suo movimento di gruppo e
  l'uscita dalla lista di attesa solo dopo il gettone, e risveglia il dispositivo appena è libero:
  tutti gli avvii, dal giro finale delle correzioni anche il prelievo, il deposito e i gruppi
  (AutomationDevice.cs:444 «internal void ExecuteAgainWhenFree()»);
- la ricerca della cella che perde restituisce la cella al gestionale, ne toglie la porta e conta il
  fallimento nel freno (PathMission.Methods.cs:2641 «private async Task GiveBackTheCellOfALostSearchAsync(StorageReservation reservation, bool awaitingRejection)»).

Non tutti gli scrittori sono passati dalla primitiva: il gateway, la porta e i campi della cella scritti
fuori da lei, la rimozione per gruppo e quella dello step eseguito non fanno salire la versione. Nessuno
di questi è una delle sei corse della ricognizione; il loro elenco sta nella documentazione di
`_routeVersion`. Il distributore circolare senza la scelta dell'operatore scrive l'uscita con la
primitiva, confrontando la versione, dal giro finale delle correzioni della voce 41.

**I tre cicli di sfondo del dispositivo.** Oltre ai tre loop del navigatore, ogni dispositivo
può avere un proprio ciclo, avviato da `StartActiveProcesses()`
(:958 «var listOfSteps = Collections.MissionSteps.Instance.Where(x =>») e fermato da
`StopActiveProcesses()` (:985 «if (GroupedMoveLuTo)»). Quale dei
tre dipende dal tipo:

| Tipo | Ciclo | Cadenza | Forma |
|---|---|---|---|
| `CircularDistributor` con `GroupItemId == 0` (:1026 «case DeviceTypes.GroupOfAutomationDevices:») | `StartProcessCircularDistributorExecuteStep` (:966 «if (step.DestinationItem is { Status: DeviceStatusTypes.Warning or») → `StepExecutor.StartCircularDistribution()` | 1000 ms (:1007 «case DeviceStatusTypes.Busy:») | `Task.Run` con `while` (:1053 «while (!_cancelCircularDistToken.IsCancellationRequested)») |
| `GroupOfAutomationDevices` con `GroupItemId > 0` (:935 «x.GroupedMovementId == groupedMovementId);») | `GroupManager.StartProcessGroupExecuteStep` (:283 «internal void StartProcessGroupExecuteStep()») → `GroupExecuteStep()` | `TimeLapseToExecuteStep`, default 1000 ms (AutomationDevice.Properties.cs:124 «private int _timeLapseToExecuteStep = 1000;») | `Task.Run` con `while` (:299 «Task.Run(async () =>») |
| ogni altro tipo, se `IsLinkedToStorage` e senza gruppo (:940 «item?.RemoveStepsByGroupedMovementId(groupedMovementId);») | `StorageManager.StartProcessExtractionLu` (:157 «internal void StartProcessExtractionLu()») → `CheckExtraction()` | 2000 ms (:159 «if (CancellationExtractionTokenSource != null) return;») | **nessun `Task.Run`**: il `while` è nel corpo del metodo (:111 «while (!_cancelExtractionToken.IsCancellationRequested)»), che diventa un ciclo di sfondo solo perché i tre chiamanti lo scartano con `_ =` (:940 «item?.RemoveStepsByGroupedMovementId(groupedMovementId);», AutomationDeviceStepExecutor.cs:36 «device.StorageManager?.StartProcessExtractionLu();», :891 «device.StorageManager?.StartProcessExtractionLu();») |

`StartActiveProcesses` ha tre chiamanti: il setter di `ExtractionTimers` del navigatore
(Navigator.cs:277 «item.StartActiveProcesses();») — quello che, come descritto in «I
tre loop», gira ogni secondo — e i setter di due proprietà di configurazione, `DeviceType`
(AutomationDevice.Properties.cs:289 «TypeConverter(typeof(PropertyTypeConverter))») e
`GroupItemId` (:342 «Category("Basic Option")»). Caricare la
configurazione dal database avvia quindi i cicli.

### La macchina a stati

`AutomationDeviceStateMachine` è costruita una per dispositivo
(AutomationDevice.cs:1083 «GroupExecuteStepRunning = false;») e il campo è
`private`: in tutto il repository compare in sei righe e cinque punti, tutti nei due file
parziali di `AutomationDevice` (grep ricorsivo su `_stateMachine`: la dichiarazione
:83 «private AutomationDeviceStateMachine? _stateMachine;», la costruzione
:1296 «_stateMachine = new AutomationDeviceStateMachine(this);», il `Dispose`
:1226 «_stateMachine?.Dispose();», `SetStatusOffLine`
:437 «if (_stateMachine != null) await _stateMachine.SetStatusOffLine(value);» e le due righe della proprietà
`Status`, il getter (AutomationDevice.Properties.cs:636 «get => _stateMachine?.CurrentState ?? DeviceStatusTypes.OffLine;»)
e il setter (:641 «set => _stateMachine?.TryChangeStateNow(value);»)).

**`Status` *è* la macchina a stati.** Non esiste un campo di stato nel dispositivo: il getter
restituisce `_stateMachine?.CurrentState ?? OffLine`
(AutomationDevice.Properties.cs:636 «get => _stateMachine?.CurrentState ?? DeviceStatusTypes.OffLine;») e
il setter è `set => _stateMachine?.TryChangeStateNow(value);`
(:641 «set => _stateMachine?.TryChangeStateNow(value);»): dalla voce `D43` lo stato è già
cambiato quando l'assegnazione ritorna, e resta in sottofondo solo la pubblicazione. Fino ad
allora il setter era `set => _ = Task.Run(async () => … await _stateMachine.TryChangeState(value))`,
ed era il fatto che pesava di più su tutta questa sezione: **assegnare `Status` non cambiava lo
stato, lo chiedeva**. Ne seguivano tre cose che il codice del motore dava continuamente per
scontate:

- dopo `device.Status = Ready` una lettura immediata di `device.Status` poteva restituire ancora
  il valore vecchio;
- due assegnazioni da thread diversi si applicavano in ordine non definito, perché erano due
  `Task.Run` indipendenti;
- un'eccezione della transizione non tornava a chi aveva assegnato: veniva registrata nel log
  dentro la lambda.

**Quali transizioni sono ammesse.** Non c'è alcuna tabella di transizioni.
`IsValidTransition` (AutomationDeviceStateMachine.cs:237-240 «private bool IsValidTransition(DeviceStatusTypes from»)
è una riga sola:

```
return from != to && device.DeviceType != DeviceTypes.Storage;
```

Qualunque dei dieci valori di `DeviceStatusTypes`
(Enums.cs:67-88 «public enum DeviceStatusTypes : byte») può quindi seguire qualunque
altro: da `Error` si va a `Moving` come da `Ready`. E **un dispositivo di tipo `Storage` non
cambia mai stato**: il suo `_currentState` resta al valore di inizializzazione, `Ready`
(:46 «private volatile DeviceStatusTypes _currentState =»), per tutta la
vita del processo. È coerente con il resto del motore, che i magazzini li tratta a parte —
`ExecuteSteps` li esclude (ExecutableCommands.Internal.cs:471 «case Enums.DeviceTypes.GroupOfAutomationDevices:»)
e `OpenConnection` non gli assegna connessione
(DeviceConnections.cs:266 «Remove(missionStep);») — ma
significa che lo stato mostrato per un `Storage` è una costante, non una misura.

**Il lock.** `TryChangeState`
(:119 «public async Task SetStatusOffLine(bool value)») prende
`_stateLock`, un `SemaphoreSlim(1,1)` d'istanza — il commento dice esplicitamente che è
d'istanza per non serializzare tutti i dispositivi fra loro
(:26 «private readonly object _stateLock = new();») — e dentro
il lock fa tre cose (:121-139 «if (_disposed) return;»):
valida la transizione, aggiorna `_currentState`, e **attende `device.FireStatusChanged()`**
(:129 «if (value) return; // Se si sta andando offline»). Quest'ultima
(AutomationDevice.cs:688-705 «newMission.LuId = item.GetValue<decimal>("ID_UDC");») fa una
scrittura su database (`ORM.AutomationDevice.UpdateStatus`,
:664 «ExtractionQueueId = item.GetValue<decimal>("ID_ESTRAZIONE_UDC")»), solleva l'evento `StatusChanged`
del dispositivo (:670 «newMission.GuidAssignmentLu = item.GetValue<Guid>») e pubblica
`ItemChangeStatus` sul socket del Viewer
(:669 «if (item.GetValue<Guid?>("GUID_ASSEGNAZIONE_UDC") != null)»). Il lock di stato del dispositivo
resta quindi preso per la durata di un giro sul database.

I tre metodi pubblici controllano `_disposed`
(:149 «if (_disposed) return false;», :177 «if (_disposed) return false;»,
:96 «if (_disposed) return;»): dopo il `Dispose`, che dispone `_publishLock`
(:73 «_publishLock.Dispose();»), un'assegnazione di `Status` ancora in volo esce senza
fare nulla, invece di sollevare `ObjectDisposedException` come faceva `TryChangeState` quando era
l'unico dei tre a non controllare il flag.

**La propagazione al gruppo.** Avviene solo dopo una transizione riuscita e solo se valgono
insieme tre condizioni (:268-270 «propagate = device.DeviceType == DeviceTypes.GroupOfAutomationDevices &&»):
il dispositivo è `GroupOfAutomationDevices`, ha `GroupedMoveLuTo`, e il nuovo stato **non** è
uno fra `Moving`, `Recovering`, `Unloading` e `Busy` (`IsMovementOrBusyState`,
:195-201 «private static bool IsMovementOrBusyState(DeviceStatusTypes state)»). In quel
caso `PropagateStateToGroupMembers`
(:355-361 «private void PropagateStateToGroupMembers(DeviceStatusTypes newState)») scrive
`itemBelonged.Status = newState` su ciascun membro restituito da `BelongedItems`
(AutomationDevice.Properties.GroupOptions.cs:48 «public List<AutomationDevice?> BelongedItems»), che
è a sua volta una scansione di `PathDevices` per `BelongingGroupId` ed esclude distributori
circolari e gruppi (:57-59 «belongedItems.AddRange(PathDevices.Instance.Where(x =>»).
Poiché il setter è quello asincrono descritto sopra, la propagazione è un fan-out di task: il
gruppo può risultare nel nuovo stato mentre alcuni membri sono ancora nel vecchio.

**Le due porte d'ingresso.** Oltre al setter, l'unico modo di cambiare stato è
`SetStatusOffLine` (:98 «long version;»),
raggiunto dal comando `ItemSetOffLine`/`ItemSetOnLine` (si veda la tabella dei comandi).
Questo valida a parte, con `CanChangeToOfflineState`
(:208-220 «private bool CanChangeToOfflineState(bool goingOffline)»): si va
offline solo da `Ready`, `OffLine` o `Locked`; si torna online solo da `OffLine`. Al rientro
azzera `RunningStepId` e lancia `_ = device.ExecuteStep()` se il dispositivo ha
`AutoExecuteStep` (:106-110 «version = ++_stateVersion;»).

Il terzo metodo pubblico, `ForceState`
(:72 «_disposed = true;») — quello che
salta la validazione — **non ha alcun chiamante in tutto il repository** (grep ricorsivo su
`ForceState` in tutti i `.cs`: solo la dichiarazione).

### Le strategie di esecuzione

`ExecuteStepCoreAsync` (AutomationDevice.cs:1243 «var strategy = GetExecutionStrategy();»)
fa due righe: sceglie una strategia e la esegue. `GetExecutionStrategy`
(:1042-1055 «lu.PositionItem.Status = currentItemStatus;») è **tutto** il criterio di
scelta:

```
if (BelongedGroup != null) return new Grouped(this);

return DeviceType switch
{
    DeviceTypes.GroupOfAutomationDevices => new GroupDevice(this),
    DeviceTypes.CircularDistributor      => new CircularDistributor(this),
    _                                    => new Standard(this)
};
```

**L'appartenenza a un gruppo vince sul tipo**
(:1198 «private void CheckGroupExecuteStepRunning()»). E `BelongedGroup` non è un
campo: è una proprietà calcolata che restituisce il dispositivo il cui `GroupItemId` è uguale
al proprio `BelongingGroupId`, cercandolo in `PathDevices` ad ogni lettura
(AutomationDevice.Properties.GroupOptions.cs:27-35 «public AutomationDevice? BelongedGroup»).
Il criterio è quindi, in ultima analisi, una colonna del database — `BelongingGroupId`
(:223 «public int BelongingGroupId») — risolta attraverso la
collezione globale. Solo un dispositivo *senza* gruppo arriva allo `switch` sul tipo; lì
`Standard` è il ramo di default per i sei tipi restanti (`Automation`, `Loading`, `Buffer`,
`Storage`, `LoadingAndBuffer` e `None`, Enums.cs:90-107 «public enum DeviceTypes : byte»).

Da notare: la strategia viene **costruita ad ogni chiamata** di `ExecuteStep` e mai
conservata, per cui nessuno stato sopravvive fra un giro e il successivo. Una sola delle
quattro prova a tenerne — `Standard._buildRouteOnNoStepToDo` — e la voce corrispondente sta
fra i punti di attrito.

Le quattro implementano `IExecutionStrategy`
(IExecutionStrategy.cs:5 «internal interface IExecutionStrategy»), che
dichiara un solo metodo, `ExecuteAsync(CancellationToken)`
(:9 «Task ExecuteAsync(CancellationToken cancellationToken);»). Nessuna delle quattro usa il
token: in ognuno dei quattro file `cancellationToken` compare esattamente due volte, il
`<param>` della documentazione e il parametro della firma.

| Strategia | Quando | Cosa fa |
|---|---|---|
| `Standard` (Standard.cs:13 «internal class Standard(AutomationDevice device) : IExecutionStrategy») | dispositivo senza gruppo, tipo diverso da gruppo e distributore circolare | è il ciclo di vita di un dispositivo singolo: 338 righe, contro le 32-41 delle altre tre. È l'unica dichiarata `internal` |
| `Grouped` (Grouped.cs:12 «internal class Grouped(AutomationDevice device) : IExecutionStrategy») | dispositivo **membro** di un gruppo | non muove nulla: ripulisce gli stati. Se il gruppo è in `Error` o `Warning`, chiama `ResetStatusBelongedItems()` sul gruppo e torna (:32-36 «if (device.BelongedGroup!.Status is Enums.DeviceStatusTypes.Error or»); altrimenti, se il dispositivo ha una UDC con uno step da fare **ed è esso stesso in `Error` o `Warning`**, ripulisce il gruppo della destinazione (:38-42 «if (device.LUsLoaded.Count == 0 || device.LoadedLu?.StepToDo == null ||») |
| `GroupDevice` (GroupDevice.cs:12 «internal class GroupDevice(AutomationDevice device) : IExecutionStrategy») | dispositivo di tipo `GroupOfAutomationDevices` senza gruppo di appartenenza | uno `switch` su tre valori di stato (:31 «case DeviceStatusTypes.Warning:»): `Error`/`Warning` → `ResetStatusBelongedItems()` (:32 «await device.ResetStatusBelongedItems();»); `Ready` → `GroupExecuteStepRunning = false` (:36 «device.GroupExecuteStepRunning = false;»), che dal setter fa ripartire il ciclo di gruppo (AutomationDevice.Properties.LiveData.cs:171-176 «/// <returns>True se era iscritto.</returns>»); ogni altro stato non fa nulla. **Il lavoro vero di un gruppo non è qui**: sta nel ciclo `GroupExecuteStep` del `GroupManager` |
| `CircularDistributor` (CircularDistributor.cs:12 «internal class CircularDistributor(AutomationDevice device) :») | dispositivo di tipo `CircularDistributor` senza gruppo di appartenenza | due istruzioni (:25-32 «cancellationToken.ThrowIfCancellationRequested();»): delega tutto a `StepExecutor.StartCircularDistribution()`. Anche qui il lavoro vero sta altrove (AutomationDeviceStepExecutor.cs:253 «internal async Task StartCircularDistribution()») |

**Il corpo di `Standard`** merita di essere letto, perché è la descrizione più compatta di che
cosa fa un dispositivo quando gli si dice «avanza»
(Standard.cs:34-95 «public async Task ExecuteAsync(CancellationToken cancellationToken)»). Prima
ripulisce i riferimenti alle UDC in arrivo (`CheckLuToReceive`,
:41 «device.LuManager?.CheckLuToReceive();»), poi sceglie fra quattro
casi:

1. **nessuna UDC a bordo** (:43 «if (device.LUsLoaded.Count == 0 || device.LoadedLu == null)»):
   se il dispositivo ha una porta di svuotamento cella configurata ed è `Ready`, avvia lo
   svuotamento (:47-49 «if (!string.IsNullOrEmpty(device.EmptyCellSourceDoor) && device.Status»); poi
   `CheckGetFromItem()` (:51 «device.LuManager?.CheckGetFromItem();»),
   che è da dove parte un prelievo;
2. **la UDC è già a destinazione**
   (:74 «else if (device.LoadedLu?.DestinationItem == device)»): missione a `Done`,
   dispositivo a `Ready`, e — a meno che il dispositivo non abbia né modalità di allocazione né
   destinazione fissa (:60-64 «if (device is») —
   la UDC torna in coda su `RoutesToBuild`
   (:66 «await AddLoadedLuToRouteToBuildQueue();» →
   :156-165 «private async Task AddLoadedLuToRouteToBuildQueue()»). È uno dei quattro
   produttori della coda elencati in «I tre loop»;
3. **UDC a bordo senza step da fare**
   (:89 «else if (device.LoadedLu?.StepToDo == null)»): stesso
   riaccodamento, preceduto dalla rimozione degli step residui della missione
   (:105 «if (device.LoadedLu != null && device.LoadedLu.MissionSteps.Any())»);
4. **UDC a bordo con uno step da fare**
   (:115 «await CheckPutToItem();»): non fa nulla qui.

In tutti e quattro i casi si finisce su `CheckPutToItem()`
(:115 «await CheckPutToItem();»), che è la consegna vera
e propria. Questa procede **solo se la destinazione dello step non appartiene a un gruppo**
(:349 «return destinationItem is { BelongedGroup: null };»), poi smista sul
`DeviceType` della destinazione
(:203-225 «await HandleAutomationDeviceTypes(destinationItem);»): i cinque tipi
«di trasporto» vanno a `HandleAutomationDeviceTypes`, `Storage` va a
`StorageManager.StartStoreLu()`
(:209 «if (device.StorageManager != null) await»), `GroupOfAutomationDevices`
e `None` non fanno nulla.

L'ultimo bivio è il `MotionType` **della destinazione**
(:246 «switch (destinationItem.MotionType)»), enum a due valori
(Enums.cs:188-194 «public enum MotionTypes : byte»), ed è quello che decide chi
muove la merce:

- **`MoveTo`** — spinge il mittente. Se `CanStartPutToOperation`
  (:172-183 «private bool CanStartPutToOperation(AutomationDevice destinationItem)») è vera, parte
  `StartPutToMOVE_LU()`
  (:288 «device.StartPutToMOVE_LU();» →
  AutomationDevice.cs:446 «if (LoadedLu?.StepToDo?.DestinationItem == null)»), che è il
  punto in cui il motore chiama `IConnection.MoveLuFromAsiToAsi`
  (:452 «var stepToDo = LoadedLu.StepToDo;»). **Altrimenti il mittente si
  prenota**: la missione va a `Stop`, il dispositivo torna `Ready`, e — questo è il punto — il
  mittente **si iscrive nella lista `DevicesReadyToTransferLu` della destinazione**
  (:301-312 «private async Task HandlePutToOperationBlocked(AutomationDevice» →
  :137 «private void AddDestinationItemToTransferQueue(AutomationDevice» →
  AutomationDeviceStepExecutor.cs:67 «internal void AddDevicesReadyToTransferLu(AutomationDevice? value)»);
- **`GetFrom`** — tira il destinatario. Il mittente si iscrive nella stessa lista, si dichiara
  `Ready`, azzera il proprio step in corso e — se la destinazione è in condizione di riceverla —
  le lancia un `ExecuteStep()` non atteso
  (:262-277 «private void HandleGetFromMotion(AutomationDevice destinationItem)»).

**Chi richiama chi, quando la destinazione si libera.** L'iscrizione non serve a niente se
nessuno la guarda, e il punto in cui viene guardata non sta nella strategia del mittente ma in
quella del **destinatario**. Quando il destinatario esegue il proprio step e si trova **vuoto**
(Standard.cs:43 «if (device.LUsLoaded.Count == 0 || device.LoadedLu == null)») chiama
`CheckGetFromItem`
(:51 «device.LuManager?.CheckGetFromItem();» →
AutomationDeviceLuManager.cs:278 «internal async Task CheckGetFromItem()»),
che dopo aver smaltito un eventuale prelievo proprio finisce su
ExecuteStepOnQueuedProcedure «internal async Task ExecuteStepOnQueuedProcedure()».
Lì ci sono tre esiti, e sono un **bilanciamento fra prendere e ricevere**:

| Condizione | Cosa fa |
| --- | --- |
| ha sia iscritti sia UDC da prelevare | ExecuteBalancedSteps «private async Task ExecuteBalancedSteps()», che alterna secondo `LastExecutedStepType` |
| solo UDC da prelevare | `ExecuteQueueLuToPickUp` |
| **solo iscritti** | ExecuteQueueToReceiveLu «private async Task ExecuteQueueToReceiveLu()» |

`ExecuteQueueToReceiveLu` prende le UDC **degli iscritti che sono pronti e carichi**
(GetLUsToReceive «private List<PathMission?> GetLUsToReceive()»)
e le tira dentro. Se nessun iscritto ha una UDC a bordo, invece,
ProcessReadyDevicesWithoutLu «private void ProcessReadyDevicesWithoutLu()»
lancia a ciascuno un `ExecuteStep(500)`: è il caso di chi si è prenotato per una UDC che sta
ancora arrivando.

**L'iscrizione va potata, o resta appesa.** Prima di ogni giro il destinatario chiama
CheckLuToReceive «internal void CheckLuToReceive()»
(Standard.cs:41 «device.LuManager?.CheckLuToReceive();»), che toglie
dalla lista chi **non ha più una UDC a bordo** e non è configurato per accettare comunque. Senza
quella potatura un mittente che ha perso la propria UDC per altra via resterebbe in coda a
tempo indefinito.

### Assegnazioni ed estrazioni

Sono i due metodi di `Navigator` che `MasterProcess` esegue dopo la guardia su
`NavStatus`, nell'ordine
(Navigator.cs:2049-2050 «await AssignmentsProcess();»): `AssignmentsProcess()`
direttamente, `BuildLuExtraction(noteWork)` attraverso `ExtractionProcess()`
(:1972 «internal static async Task ExtractionProcess()»), che avvolge **la sola costruzione
della coda** nel semaforo globale `SyncExtraction`, con il timeout condiviso
`SyncExtractionTimeout`, 30 secondi
(:2005 «if (!await SyncExtraction.WaitAsync(SyncExtractionTimeout))»,
:103 «public static readonly TimeSpan SyncExtractionTimeout = TimeSpan.FromSeconds(30);») e ricontrolla lo stato
(:1716 «if (NavStatus != NavigatorStatusTypes.Run) return;»). La lettura del lavoro pronto sta
**fuori** dal lucchetto, prima della presa
(:1991-1992 «noteWork = await RequireWarehouse("BuildLuExtraction")»): costa 1 + 2N andate e
ritorni di rete — una richiesta al gestionale, e due richiami del gestionale al WCS per ogni
assegnazione candidata da validare — e sotto il lucchetto quella spesa la pagavano gli altri sette
punti che si contendono lo stesso semaforo. Se non c'è lavoro pronto, il giro esce senza nemmeno
prendere il semaforo (:1706 «if (noteWork.Count == 0) return;»). Nel codice di produzione
non hanno altri chiamanti: `AssignmentsProcess` solo da `MasterProcess`, `BuildLuExtraction` solo da
`ExtractionProcess`; le altre occorrenze sono nei test.

Vanno letti insieme perché fanno lo stesso mestiere in due tempi: **decidere quale UDC serve**
(assegnazioni) e **metterla in coda di estrazione** (estrazioni). Entrambi lavorano su tabelle
del WMS, non sul grafo in memoria: il grafo entra solo per risolvere un codice porta in un
dispositivo e per chiedere se una missione per quella UDC esiste già.

#### `AssignmentsProcess` — chi prende quale UDC

(Navigator.cs:1492 «private static async Task AssignmentsProcess()») è un guscio: guardia
`NavStatus != Run`, due chiamate, e un `catch` che degrada qualunque errore a una riga di log.

**1. Le note di spedizione — non più qui.** Dal compito 14 il motore non decide più quali note
assegnare e non assegna: `AssignLUbyReferenceExtractions` non esiste più, e con essa le letture di
`DbInfo` che la servivano (`GetProcessingNotes`, `GetProcessingNotesToProcess`,
`NumActiveProcessingSameCellId`, `SetNoteAssigned`). Di quel metodo resta qui solo la pulizia della
coda di estrazione per le note in pausa
(:1511 «await DbInfo.DeleteLuInExtranctionByPausedNote();»), che sta in questo processo e non
nell'estrazione perché non deve dipendere dal semaforo `SyncExtraction`.

L'orchestrazione è passata al gestionale, che decide, assegna e valida
(`MagazzinoPerWcs.AssegnaNotePronteAsync`, Services/WMS.Services/src/Automazione/MagazzinoPerWcs.cs:869 «internal static async Task AssegnaNotePronteAsync(bool limitOneShippingNotePerBuffer)» (non pubblicato)):
il motore riceve righe già pronte da `IWarehouse.GetPendingNoteWorkAsync`, che chiama
`ExtractionProcess` prima di prendere il semaforo dell'estrazione
(:1991-1992 «noteWork = await RequireWarehouse("BuildLuExtraction")»).
`LimitOneShippingNotePerBuffer` è rimasta un'opzione della configurazione dell'host del WCS
(:231 «public static bool LimitOneShippingNotePerBuffer { get; set; }») e viaggia in quella
richiesta: chi la applica è il gestionale, con la query di `NumActiveProcessingSameCellId` migrata
di là (MagazzinoPerWcs.cs:889 «private static async Task<int> LavorazioniAttiveSullaStessaCellaAsync(decimal idLavorazione)» (non pubblicato)) — la
tabella che interroga, `LAVORAZIONI_NOTE`, è già sua, quindi la lettura è locale e non aggiunge
nessun giro di rete.

La decisione, per le note, non è mai stata del motore: prima il motore decideva *quando* chiedere e
*per quale* lavorazione, e il WMS quale UDC soddisfacesse il riferimento; oggi il WMS decide anche
il *quando* e il *per quale*.

**2. `ServeExtractionWork`** (:1319 «internal static async Task ServeExtractionWork(IReadOnlyList<PendingExtraction> work)») — le
estrazioni per UDC. Dal compito 15 ha la stessa forma della gemella delle note: il gestionale
decide, il motore muove. Qui il motore non ha **mai** assegnato niente — l'elenco delle UDC da
estrarre gli arrivava già fatto — ma decideva quali lavorazioni processare e quando chiuderle, e
scriveva lui gli stati su `DISTINTA_ESTRAZIONI_UDC` di questo ciclo. Le tre cose sono passate di
là, insieme al loro difetto storico: `GetProcessingLuExtractions`, `GetLuToExtractByLuExtraction` e `GetLuLocked`
non sono più nel motore, e la lettura severa delle due colonne che le voci `R10` e `R11` avevano
fatto emergere (`ID_CELLA_PICKING`, `ID_TESTATA_ESTRAZIONE_UDC`) è rimasta attaccata alla query,
nella sua sede nuova
(MagazzinoPerWcs.Estrazioni.cs:28 «private static readonly string LavorazioniEstrazioniSql» (non pubblicato)):
le due colonne viaggiano nel tipo dato come campi obbligatori, così il difetto non può tornare per
la stessa porta.

Il ciclo del motore è due anelli. Quello esterno, per testata
(:1319 «internal static async Task ServeExtractionWork(IReadOnlyList<PendingExtraction> work)»), riferisce gli esiti al gestionale
(:1334 «await warehouse.ReportExtractionOutcomesAsync(») — **anche quando sono zero**: è la chiamata
che fa chiudere una lavorazione le cui righe sono tutte già risolte, e prima era il controllo del
residuo che girava dopo ogni testata qualunque cosa fosse stata scritta. Quello interno, per riga
(:1367 «private static async Task<List<ExtractionRowOutcome>> ServeExtraction(IWarehouse warehouse, PendingExtraction extraction)»), calcola un esito e, quando
può, costruisce il movimento:

| Condizione | Esito |
|---|---|
| la UDC è bloccata (`PendingExtractionRow.LoadUnitLocked`, :1378 «status = ExtractionRowStatus.LoadUnitLocked;») | `LoadUnitLocked`, nessun movimento |
| il gestionale non la trova nelle aree servite dal polmone, o non risponde (:1269 «status = ExtractionRowStatus.NotReachable;», :1286 «status = ExtractionRowStatus.NotReachable;») | `NotReachable` |
| esiste già una riga di coda per quella UDC, ma di un'altra estrazione (:1445 «status = ExtractionRowStatus.InOtherExtraction;») | `InOtherExtraction` |
| non esiste alcuna riga di coda (`GetExtractionLuRowId == 0`, :1413 «if (queuedRowId == 0)») | `InExtraction`, **e si costruisce il movimento** |
| la riga di coda era già la sua: nessun ramo la tocca | `InExtraction`, dal valore iniziale (:1373 «var status = ExtractionRowStatus.InExtraction;») |

L'ultima riga della tabella non è un caso limite ma il caso normale della riga già in lavorazione,
e nel motore era invisibile: la variabile nasceva `byte locked = 0` e lo zero **è anche** il valore
di `InExtraction`. Tradurre il ciclo scrivendo solo negli stati espliciti la farebbe sparire, e la
lavorazione non si chiuderebbe più.

I dati su cui decide sono quattro, e due arrivano ora dalla riga invece che da una lettura. Le
**aree** raggiungibili dalla cella polmone (`PendingExtraction.BufferAreaIds`, ex
`PickingBufferAreas`) e il **blocco della UDC** li porta il tipo dato; una testata il cui polmone
non serve nessuna area non arriva affatto. La **porta di prelievo** della cella in cui il
gestionale ha trovato la UDC resta una domanda al confine (`ChoosePickUpDoorAsync`). I **due
dispositivi**, sorgente e destinazione, si risolvono da codice porta e cella polmone con `GetItemId`
sul database del WCS, e il **dispositivo sorgente in `PathDevices`**
(:1402 «var automationItem = PathDevices.Instance[itemSource];») viene sostituito dal proprio
`RelocateItemId`, letto di nuovo da `PathDevices`, se è di tipo `Storage`.

È il **tipo** di quel dispositivo a scegliere la forma del movimento: `GroupOfAutomationDevices` →
`ExecutableCommands.BuildGroupedMoveLu`, ogni altro tipo → `BuildSingleMoveLu`. È lo stesso bivio
del comando `MoveLuManual`, e lo stesso di `BuildLuExtraction`.

**Due percorsi non producono alcun esito**, e la differenza conta: se sorgente o destinazione non
si risolvono (:1397 «"Error On Navigator->ServeExtraction No Guid Item on Source or Destination",»), o se il dispositivo — o il
suo ricollocatore — non è in `PathDevices` (:1223 «if (automationItem == null) continue;»), la riga
viene saltata **senza** una riga di `DISTINTA_ESTRAZIONI_UDC`. Non scrivere non è lo stesso che
scrivere zero: uno zero terrebbe aperta la lavorazione, l'assenza di esito la lascia chiudibile. Le
righe seguenti proseguono — nel motore di prima quei percorsi erano `continue` dentro il `foreach`,
e prima ancora tre `return` che abbandonavano l'intero elenco (voce `D57`).

**La degradazione riga per riga si è conservata.** Un'indisponibilità del gestionale mentre si
cerca la UDC non risale: viene registrata e la riga è marcata `NotReachable`
(:1286 «status = ExtractionRowStatus.NotReachable;»), che è uno stato esplicito che il motore già
modella. Con l'eccezione che risale, pochi secondi di gestionale assente fermerebbero l'intera
testata, comprese le righe che dal gestionale non dipendono. Alla stessa logica risponde la cattura
attorno alla scrittura degli esiti: un guasto su una testata non porta via le testate seguenti,
come non le portava via l'errore di una `UPSERT` inghiottita.

**Di là, la chiusura — per le testate di questo ciclo.** La lavorazione si chiude quando e solo
quando non resta più niente da estrarre (MagazzinoPerWcs.Estrazioni.cs:224 «private static async Task ChiudiSeEvasaAsync(decimal idLavorazione)» (non pubblicato)), e per
le testate che questo ciclo serve non è un'operazione che il WCS chiede: è la conseguenza che il
gestionale trae da chi scrive gli stati.
Le due porte del ciclo originale ci sono tutte e due — quella delle testate che non arrivano al
motore, in `GetPendingExtractionWorkAsync`
(MagazzinoPerWcs.Estrazioni.cs:125 «public async Task<IReadOnlyList<PendingExtraction>> GetPendingExtractionWorkAsync(CancellationToken cancellationToken = default)» (non pubblicato)), e quella
delle testate servite, in `ReportExtractionOutcomesAsync`
(MagazzinoPerWcs.Estrazioni.cs:167 «public async Task ReportExtractionOutcomesAsync(ReportExtractionOutcomesRequest request, CancellationToken cancellationToken = default)» (non pubblicato)) — e
l'ordine fra le due è la cosa da non toccare: valutare la chiusura **prima** di servire la testata
chiuderebbe ogni lavorazione appena nata, perché il residuo conta le righe con una distinta a stato
non positivo e una lavorazione nuova non ne ha nessuna.

La pulizia in testa al ciclo si è divisa allo stesso confine. La cancellazione delle distinte delle
lavorazioni sospese, che era una scrittura sui dati del gestionale, è passata di là
(MagazzinoPerWcs.Estrazioni.cs:95 «private const string CancellaDistinteSospeseSql» (non pubblicato)); la cancellazione
delle righe di coda resta nel motore, perché la coda vive nel database del WCS, e la chiama
`AssignmentsProcess` accanto alla gemella delle note
(:1512 «await DbInfo.DeleteLuInExtranctionByPausedExtractionLu();»).

**Quello che il motore scrive fuori da questo ciclo: niente, dal 2026-09-12.** `DISTINTA_ESTRAZIONI_UDC`
la scriveva il motore nella fase che chiude il movimento (`DbInfo.UpdateStatusExtractionLu`), e con
lei rifaceva per conto proprio il conteggio del residuo e la chiusura della lavorazione: erano due
copie testualmente identiche ai due lati del confine, voce `D120` del registro. Il compito 12 ha
cancellato quel metodo e le sue due query. Al loro posto il motore **segnala** l'esito della riga
(DBInfo.Writes.cs:498 «internal static async Task ReportExtractionRowOutcome(decimal rowId, ExtractionRowStatus status)»),
e lo stato sulla distinta e la chiusura della lavorazione li trae il gestionale, che ricava la
lavorazione dalle righe segnalate. `D120` è chiusa.

**L'esito finale `Extracted` passa da un'outbox durevole.** Quando la missione arriva a destinazione,
`ProcessExtractionItems` legge dalla riga di coda l'identificativo della riga WMS e registra prima
`Extracted` nella tabella SQLite `PendingExtractionOutcomes`. La scrittura passa dal canale
serializzato dello stato e deve completare il commit locale prima che la missione possa azzerare
`ExtractionQueueId` e cancellare la riga di coda. Se il commit locale fallisce, missione e riga
restano collegate; se riesce, la chiamata WMS non trattiene più la conclusione della missione.

Subito dopo il commit viene fatto un primo tentativo di consegna. Lo stesso dispatcher viene
richiamato da `MasterProcess` prima della guardia `NavStatus != Run`, quindi continua a fare
progresso anche in `Stop`. Legge in ordine al massimo **100** elementi per volta e cancella il
lotto da `PendingExtractionOutcomes` soltanto dopo che `ReportExtractionOutcomesAsync` è riuscita.
Un fallimento lascia intatte le righe e attiva un backoff in memoria che cresce da **2** a un
massimo di **30 secondi**; al riavvio il primo tentativo torna immediato, mentre le righe restano
nel file operativo.

La garanzia è **at-least-once**, non exactly-once: se il WMS applica l'esito ma il processo cade
prima della cancellazione locale, lo stesso `Extracted` può essere inviato di nuovo. Il contratto
WMS è idempotente proprio per rendere sicuro questo duplicato. La migrazione crea l'outbox senza
inventare dati storici: gli esiti già persi prima che `PendingExtractionOutcomes` esistesse non
sono ricostruibili automaticamente.

Questa durabilità riguarda soltanto l'esito finale `Extracted`. Non rende durevoli le conferme
delle assegnazioni eseguite nello stesso giro: la finestra fra conferma, consumo e GUID vuoto resta
aperta ed è registrata separatamente come `D174`.

#### `BuildLuExtraction` — le assegnazioni delle note entrano in coda

(Navigator.cs:1596 «internal static async Task BuildLuExtraction(IReadOnlyList<PendingNote> noteWork)») è la prima delle due porte
d'ingresso dell'estrazione automatica. La seconda è il comando `MoveLu` scritto in
`COMMAND_TODO` (`ExecutableCommands.MoveLu`,
ExecutableCommands.Internal.cs:1144 «internal static async Task MoveLu(Command navigatorCommand»),
e le due **convergono**: stessa risoluzione della UDC, stessi costruttori della coda.

**La sorgente** è il gestionale: `IWarehouse.GetPendingNoteWorkAsync`, letta da `ExtractionProcess`
fuori dal semaforo e passata qui in `noteWork`
(:1992 «.GetPendingNoteWorkAsync(LimitOneShippingNotePerBuffer);»),
che dal compito 14 ha preso il posto di `DbInfo.GetNoteAssignmentsToQueue`, cancellata insieme
alla lettura diretta di `ASSEGNAZIONE_NOTE` dal motore. Il gestionale legge le proprie
assegnazioni — quelle che scrive con `AssegnaRiferimentiMagazzinoAutomatico` — sbloccate
(`BLOCCATO = 0`), non ancora prelevate, con un polmone, di una lavorazione in corso
(`LAVORAZIONI_NOTE.STATO = 0`)
(MagazzinoPerWcs.cs:1013 «private const string AssegnazioniProntePerCodaSql» (non pubblicato)),
le valida, e risponde con righe già pronte. Resta al motore la sola cosa che il gestionale non
può sapere: **quali sono già in coda di estrazione**, perché la coda sta nel database del WCS;
si tolgono in memoria dopo la risposta, e la lettura della coda avviene **sotto il lucchetto**
(:1609 «var queued = await ExtractionQueue.ReadAsync(Persistence.ExtractionQueueStore.QueuedAssignmentIdsAsync, new List<decimal>());»). La riga porta solo ciò
che non si ricava dal vivo: UDC, polmone, aree, destinazione, postazione. Cella, porta e ostacoli
**non** si leggono dalla riga, perché da quando il WMS l'ha scritta possono essere cambiati — e a
maggior ragione da quando `ExtractionProcess` l'ha letta, visto che nel mezzo può esserci passata
l'attesa del semaforo.

**Che cosa si rilegge sotto il lucchetto, e che cosa no.** La finestra fra la lettura di
`ExtractionProcess` e questo punto è l'attesa del semaforo, fino ai trenta secondi del suo tetto.
Si rileggono qui: le assegnazioni già in coda, la posizione della UDC e la sua prendibilità, la
porta e gli ostacoli, il diagramma; e al punto di scrittura i costruttori ricontrollano i **primi
due** dei tre rifiuti del gestionale, con `IsLuInExtraction` — lo stesso insieme di
`MissionQueries.HoldsLoadUnit`, che è la domanda con cui il gestionale ha rifiutato il primo
(ExecutableCommands.Internal.cs:47 «if (automationItem == null || IsLuInExtraction(luId)) return;»,
:107 «if (automationItem == null || IsLuInExtraction(luId)) return;») — e con
`DbInfo.IsLuPresentInExtranctionLu` per il secondo.

**Non** si rileggono, e sono stantii quanto quella finestra:

1. **le aree del polmone** (`BufferAreaIds`): non è un identificativo, è il risultato di una lettura
   di `AREE_PRELIEVO_PICKING` che il gestionale fa al momento della risposta
   (MagazzinoPerWcs.cs:1116 «private static async Task<List<decimal>> AreePrelievoPickingAsync(decimal idCellaPolmone)» (non pubblicato)),
   e qui diventa il **filtro** con cui si decide se la UDC è disponibile
   (:1624 «var cella = await warehouse.FindLoadUnitAsync(new FindLoadUnitRequest(»). Prima che la
   lettura uscisse dal lucchetto, quel calcolo cadeva dentro la finestra. Sulla stessa lettura
   stantia poggia anche il **terzo** rifiuto del gestionale, il polmone che non serve nessuna area;
2. **la validità stessa dell'assegnazione**: che la riga esista ancora, che sia sbloccata, non
   prelevata, e che la lavorazione sia ancora in corso. Le quattro condizioni stanno solo nella
   `SELECT` del gestionale, fuori dal lucchetto;
3. **gli altri campi della riga**, che entrano in coda come sono arrivati: UDC, polmone,
   destinazione e postazione. Della destinazione si ricontrolla solo la **presenza nel diagramma**,
   mai il valore: se nel frattempo il gestionale ha riscritto l'assegnazione — `NoteMagazzino` ne
   cambia `ID_UDC` e `ID_POSTAZIONE` — si accoda quello di prima.

**Perché è accettato, e che cosa recupera.** L'esito peggiore è mite: un giro perso o un rifiuto
spurio, non un movimento sbagliato, perché le due guardie fisiche — missione e coda — si rileggono.
Il recupero, per nome: la lavorazione che **non è più in corso** la prende
`DbInfo.DeleteLuInExtranctionByPausedNote`, chiamata a ogni giro da `AssignmentsProcess`
(:1511 «await DbInfo.DeleteLuInExtranctionByPausedNote();»), che toglie la riga dalla coda e
cancella l'assegnazione. Quella pulizia **non** copre però l'assegnazione *cancellata* nel
frattempo — dall'operatore in `GestNOTE_REFERENZA`, o dal prelievo in `NoteMagazzino` — perché
guarda lo stato della lavorazione e non l'esistenza dell'assegnazione: quella riga di coda resta
orfana finché non la consuma il motore. Quanto al blocco, `BLOCCATO` oggi viaggia solo da 1 a 0
(`AssegnaRiferimentiMagazzinoAutomatico` lo azzera a fine ciclo), quindi una riga letta sbloccata
non torna bloccata sotto il motore.

**Per ogni assegnazione**, nell'ordine:

1. i **primi tre motivi di rifiuto** — la UDC è già tenuta dal motore, è già in coda per un'altra
   estrazione, il polmone non serve nessuna area — non si valutano più qui: dal compito 14 li
   applica il gestionale prima di rispondere, chiedendo al WCS le due cose che solo lui sa
   (MagazzinoPerWcs.cs:1057 «internal static async Task<IReadOnlyList<PendingNote>> ValidaAssegnazioniPronteAsync()» (non pubblicato)).
   Le righe che arrivano qui li hanno già superati;
2. **la UDC viene cercata** nel magazzino fra le aree del polmone, solo se non bloccata e in una
   cella scaricabile (:1624 «var cella = await warehouse.FindLoadUnitAsync(new FindLoadUnitRequest(»): se non si
   trova, è il quarto motivo di rifiuto — l'unico che resta al motore, perché è il solo che
   richiede il vivo del diagramma — ed è il punto in cui si verifica che la UDC sia
   **disponibile**;
3. **porta e ostacoli** si calcolano da dove la UDC è ora (`PickUpDoor`,
   :1408 «var porta = await PickUpDoor(cella);» → :965 «internal static async Task<CellInfo> PickUpDoor(CellaReferenza luCell)»),
   che sceglie la porta con meno bancali davanti; la sorgente è il dispositivo di quella porta
   (:1639 «var idSorgente = await DbInfo.GetItemId(porta.DoorCode);»), la destinazione quella dell'assegnazione o,
   se manca, il dispositivo della porta A del polmone
   (:1411-1416 «var idDestinazione = riga.GetValue<Guid>("ID_ITEM_DESTINATION");»);
4. se sorgente, ricollocatore o destinazione **non sono nel diagramma**, l'assegnazione resta in
   attesa con un `Warning` (:1662-1669 «if (ricollocatore == null || PathDevices.Instance»): è un difetto
   del diagramma, non dell'assegnazione, e non si cancella;
5. la scrittura in coda è dei costruttori di `ExecutableCommands`
   (:1436-1439 «await ExecutableCommands.BuildGroupedMoveLu(idUdc, polmone, idSorgente»), scelti dal tipo del ricollocatore come
   in `ServeExtraction`.

**Rigettare** non è più cosa del motore. Il quarto motivo lo scopre lui, ma la conseguenza la
chiede al gestionale con un'operazione del confine che porta anche il motivo
(:1633 «await warehouse.RejectNoteAssignmentAsync(new RejectNoteAssignmentRequest(»); di là la riga di
`ASSEGNAZIONE_NOTE` viene cancellata e il motivo finisce nel registro del gestionale, dove stanno
già gli altri tre
(MagazzinoPerWcs.cs:846 «public async Task RejectNoteAssignmentAsync(RejectNoteAssignmentRequest request, CancellationToken cancellationToken = default)» (non pubblicato)).
Al giro successivo il WMS potrà assegnare un'altra UDC alla stessa riga di nota. `RigettaAssegnazione`
e la sua chiamata a `DbInfo.DeleteAssignedLuByAssignmentId` non ci sono più; quella scrittura resta
per gli altri chiamanti, che non passano da qui.

**Gli ostacoli** li gestisce `BuildSingleMoveLu`
(ExecutableCommands.Internal.cs:122 «internal static async Task BuildSingleMoveLu(decimal luId, decimal»). Con
`Obstacles == 0` scrive la sola riga della UDC (:111 «if (unloadingDoorCode.Obstacles == 0)»);
altrimenti chiede al WMS le UDC davanti (`UdcOstacolo`,
:135 «var lUtoRelocate = await WmsBusinessArea.OperazioniUdc.UdcOstacolo(luId») e scrive **prima una riga di
ricollocamento per ciascuna**, con `Extract = false`
(:184-197 «var relocateRow = new ExtractionQueueItem»), **poi** la riga della UDC
voluta. L'estrazione è quindi programmata dal fondo. `BuildGroupedMoveLu`
(:41 «internal static async Task BuildGroupedMoveLu(decimal luId, decimal») scrive la sola riga della UDC: i
gruppi gestiscono la profondità da sé. L'assegnazione viaggia **solo sulla riga della UDC
voluta** (:67-68 «if (idAssegnazioneUdc > 0) row.AssignmentId = idAssegnazioneUdc;»,
:128-129 «if (idAssegnazioneUdc > 0) row.AssignmentId = idAssegnazioneUdc;»,
:173-174 «if (idAssegnazioneUdc > 0) row.AssignmentId = idAssegnazioneUdc;»), non su quelle di
ricollocamento: il consumatore cancella l'assegnazione di una riga la cui UDC ha una missione
diversa, e un ostacolo che portasse l'assegnazione del bersaglio la farebbe cancellare. Le
scritture in coda sono **attese**, non lanciate
(:70 «await ExtractionQueue.UpsertAsync(row);»,
:197 «await ExtractionQueue.UpsertAsync(relocateRow);»): la verifica «già in coda»
dell'ostacolo successivo legge il database e deve trovare la riga precedente.

`BuildLuExtraction` non muove nulla: riempie una coda. Il consumatore è il ciclo di estrazione
dei dispositivi collegati a magazzino (`StorageManager.CheckExtraction`,
AutomationDeviceStorageManager.cs:379 «private async Task CheckExtraction()»
→ `ProcessExtractionLu`, :878 «private async Task ProcessExtractionLu()»),
che legge la propria coda con `GetLuToExtract(device)`
(DBInfo.cs:287 «internal static async Task<DataTable?> GetLuToExtract») e distingue le righe su `ESTRARRE`
(:881 «if (!row.GetValue<bool>("ESTRARRE"))»): un ostacolo
viene ricollocato in un'altra cella cercata al momento (`MoveObstacleLu`,
:703 «private async Task MoveObstacleLu(List<decimal>») e, se una cella
non si trova, viene mandato al polmone di picking come estrazione
(:774 «missionType = MissionTypes.Extraction;»); la UDC voluta
viene estratta (`ExtractTargetLu`,
:431 «private async Task ExtractTargetLu(List<decimal>»). All'arrivo la
missione, che ha ereditato l'`ExtractionQueueId`, conferma l'assegnazione al WMS attraverso
l'`ID_ASSEGNAZIONE_UDC` della riga di coda (`ProcessExtractionItems`,
PathMission.Methods.cs:1199 «private async Task ProcessExtractionItems()»).

**Da collaudare su un impianto di prova** (voce `D56` del registro): è codice che comincia a
muovere merce che oggi non si muove.

### L'accesso ai dati

**Questa sezione è cambiata di natura il 2026-09-12.** Fino a quel giorno lo strato `ORM/` era dove
il motore parlava con **SQL Server del gestionale**, e passava tutto da una variabile globale,
`Navigator.Database`, l'unica `Instance` di `MsSqlHelper`, assegnata da
`ApplyDatabaseConnectionString` dentro `StartAsync`. Quella variabile, quel metodo e quel
riferimento non esistono più, e non c'è più un'istruzione T-SQL sotto `WCS/Wcs.Engine/src`:
`EngineDependenciesTest` lo verifica a ogni collaudo, sia sui `.csproj` sia sulle assembly del
motore compilato.

Oggi `ORM/` è **il vocabolario del motore verso due posti**, e nessuno dei due è il database del
gestionale:

- **il database del WCS** (SQLite, `Persistence/`): l'impianto, le missioni, la coda di estrazione,
  i comandi, la mappa porte, le connessioni PLC e le due tabelle dei dati dal campo. Il dettaglio in
  [database-del-wcs.md](../database-del-wcs.md);
- **il confine `IWarehouse`**, per tutto ciò che è del gestionale: merce, celle, prenotazioni, note
  ed estrazioni. Ci si arriva da `Navigator.Warehouse`, che l'host cabla prima di avviare il motore,
  e `Navigator.RequireWarehouse` è la guardia che solleva se non c'è.

**Tredici file, 2227 righe** (misura del 2026-09-12). Si dividono in tre gruppi:

| Gruppo | File | Cosa fa |
|---|---|---|
| Un file per tipo di dominio | `AutomationDevice.cs` (84), `AutomationJunction.cs` (82), `PathMission.cs` (106), `LoadUnit.cs` (77), `CommandToDo.cs` (101), `NavigatorLog.cs` (45), `PlcMessageLog.cs` (90), `ExtractionQueue.cs` (45) | `Get`/`Update`/`Add`/`Delete` sull'oggetto corrispondente, accodati allo scrittore del database del WCS |
| Le due tabelle dei dati dal campo | `CheckDataFromASIInfo.cs` (42), `RecordDataFromASIInfo.cs` (43) | le due istanze costruite all'apertura dei database (Navigator.cs:801 «CheckDataFromAsi = new();») |
| Il contenitore di tutto il resto | `DBInfo.cs` (370), `DBInfo.Private.cs` (615), `DBInfo.Writes.cs` (527), della stessa classe parziale | una classe statica, `DbInfo` (DBInfo.cs:15 «internal static partial class DbInfo»), con i metodi `internal` che sono il vocabolario con cui il motore orchestra: legge dal proprio database, chiede al confine, e segnala i fatti |

`DbInfo` è quindi **un nome che promette un accesso dati che non c'è più**: dentro c'è
orchestrazione e sono chiamate al confine. Spezzarlo o rinominarlo è una decisione a sé, con dei
`git mv` e una quarantina di chiamanti da seguire.

Accanto c'erano due cartelle, `CRUD/` e `Models/`, con repository generati sullo stesso stampo e i
POCO corrispondenti: la sola parte dello strato con modelli tipizzati, che esponeva
`GetAsync`/`UpsertAsync`/`DeleteAsync` nascondendo l'`IF EXISTS … UPDATE … ELSE INSERT`. Sono
sparite. Il compito 6 aveva tolto i tre repository senza chiamanti
(`SqlEstrazioniUdcAutomaticheRepository`, `SqlRoutesFoundRepository`,
`SqlRoutesFoundStepsRepository`) e il grosso dei metodi del quarto, lasciando in piedi solo
`SqlDistintaEstrazioniUdcRepository.UpsertAsync` perché aveva un chiamante; il compito 15 ha
spostato quel chiamante di là dal confine, e con lui l'ultima ragione di quelle due cartelle. La
scrittura vive ora in `MagazzinoPerWcs.ScriviEsitoRigaAsync`, come un `INSERT` e basta — il ramo di
aggiornamento dell'upsert non era raggiungibile da quel chiamante
(MagazzinoPerWcs.Estrazioni.cs:83 «private const string ScriviEsitoRigaSql» (non pubblicato)).

**Chi chiama `DbInfo`.** Un grep ricorsivo su `DbInfo\.` in `WCS/Wcs.Engine/src` dà **164 righe** al
2026-09-12 (commenti compresi), distribuite fra una quindicina di file: `Navigator.cs` 23,
`Items/PathMission.Methods.cs` 22, `AutomationDeviceStorageManager.cs` 21,
`AutomationDeviceGroupManager.cs` 21, `ExecutableCommands.Internal.cs` 16, `Items/PathMission.cs` 11,
`ExecutableCommands.Private.cs` 11, `AutomationDeviceStepExecutor.Private.cs` 9,
`AutomationDeviceLuManager.cs` 7, `Items/AutomationDevice.cs` 7, `Collections/ComandToDo.cs` 6, e
poche altre a una o due. Non c'è nessun livello intermedio: un manager di dispositivo interroga il
database del WCS e il confine esattamente come il navigatore. **Il numero è una misura, non una
proprietà**: si rifà prima di citarlo.

**Le tabelle.** Sono tutte del WCS, e stanno in tre file SQLite. In quello operativo (`wcs.db`):
`PlantItems` (i dispositivi), `PlantJunctions` (le giunzioni), `PlantDiagrams` (il disegno),
`Missions`, `ExtractionQueue`, `Commands`, `DoorMappings`, `PlcConnections`, `RecordDataSettings` e
`CheckDataSettings`. In quello mensile dello storico (`wcs-history-AAAA-MM.db`): `NavigatorLog` e
`HandledLoadUnits`. In quello mensile del registro dei messaggi dei PLC (`wcs-plc-AAAA-MM.db`, voce
`D183`): `PlcMessages`, con una coda di scrittura sua e la ritenzione di sette giorni. Le tabelle del gestionale — `ESTRAZIONI_UDC_AUTOMATICHE`, `CELLE_AREE`,
`ASSEGNAZIONE_NOTE`, `POSIZIONI_UDC`, `TESTATE_NOTE` e le altre — il motore non le nomina più: le
conosce solo il gestionale, dall'altra parte del confine.

**Quando il motore non scrive.** Due guardie diverse sopprimono le scritture, ed è utile
tenerle distinte:

- **`Navigator.LoadingStatus`** protegge le operazioni su `NAVIGATOR_ITEMS` e
  `NAVIGATOR_JUNCTIONS` e le due tabelle dei dati dal campo: dodici condizioni nella forma «se
  sta caricando, non fare nulla» (oggi la guardia è per istanza, `_loadingFromDatabase`, nel solo
  `SetProperty` del dispositivo,
  AutomationDevice.Properties.Private.cs:245 «if (updateDb && !_loadingFromDatabase) ORM.AutomationDevice.Update»;
  le dodici dell'ORM su SQL Server sono sparite con lui). Come già detto in «Stato e
  proprietà», quel campo **non veniva mai scritto** e le dodici guardie erano permanentemente
  false. Tolte con la voce `B7`, insieme al campo: quattro di esse stavano su **letture** e,
  con il flag vero, avrebbero fatto sovrascrivere la configurazione dell'impianto;
- **`NavStatus == Recover`** protegge missioni e unità di carico
  (ORM/PathMission.cs:30 «if (Navigator.NavStatus == Enums.NavigatorStatusTypes.Recover»,
  ORM/LoadUnit.cs:30 «if (Navigator.NavStatus == Enums.NavigatorStatusTypes.Recover)») durante il tratto di bootstrap
  descritto in «Avvio». Questa funziona.

**Come le scritture partono davvero.** Non c'è un `Save()`: la persistenza è un effetto
collaterale dei setter, con **tre discipline diverse** nello stesso motore.

| Tipo | Meccanismo | Sincrono? |
|---|---|---|
| `AutomationDevice` (configurazione) | `SetProperty(ref …, value, true)` → `ORM.AutomationDevice.Update(this)` (AutomationDevice.Properties.Private.cs:240-247 «private void SetProperty<T>(ref T field, T value, bool updateDb,»), che mette l'istantanea in coda allo scrittore di stato (ORM/AutomationDevice.cs:49 «internal static void Update(Items.AutomationDevice value)») | no: chi assegna non aspetta |
| `AutomationDevice` (stato) | `FireStatusChanged`/`FireLuLoadStatusChanged` → `await ORM.AutomationDevice.UpdateStatus` (AutomationDevice.cs:663 «MissionType = MissionTypes.Extraction,», :664 «ExtractionQueueId = item.GetValue<decimal>("ID_ESTRAZIONE_UDC")»); e il setter di `RunningStepId`, che la lancia in un `Task.Run` scartato (AutomationDevice.Properties.LiveData.cs:781-785 «public List<PathMission> LUsToReceive») | misto |
| `Command` | `SetProperty(…, true)` → `Task.Run` **scartato** → `CommandToDo.Update` (Command.cs:308 «/// Ottiene o imposta l'identificatore della stazione di lavoro», :314 «set => SetProperty(ref _workstationId, value, true);») | no, e non atteso: descritto in «Comandi in ingresso» |
| `PathMission` | `SetProperty(…, true)` → `ORM.PathMission.Update(this)` (PathMission.Methods.cs:1218 «if (newDestination == Guid.Empty || string.IsNullOrEmpty»), che mette l'istantanea in coda allo scrittore di stato | no |

La conseguenza pratica, per chi rifattorizza, è che **non esiste un confine transazionale**:
una sequenza di quattro assegnazioni su una missione sono quattro `UPDATE` distinti, alcuni
attesi e alcuni no, e un'eccezione a metà lascia lo stato persistente e quello in memoria
diversi fra loro.

## Stato e proprietà

Tutto lo stato di runtime del motore è **globale e statico**. Non esiste un'istanza di
«impianto» da passare in giro: `Navigator` è una classe di soli membri statici, e le collezioni
sono singleton `Lazy<T>` a cui si accede da qualunque punto tramite `.Instance`. Questa sezione
elenca dove vive quello stato e chi lo tocca — è il perimetro reale di qualunque estrazione.

### Le collezioni singleton

Tutte in `Collections/`, tutte con lo stesso schema `private static readonly Lazy<T>` +
`public static T Instance`, tutte con un `ConcurrentDictionary` o una `ConcurrentQueue`
privati come archivio, tranne `DeviceConnections`, che tiene una `List` sotto lock. Sono otto:
`RoutesFound` non esiste più, né come singleton né come lista locale di `PathBuilder` — le
rotte trovate stanno nello stato locale della ricerca
(RouteSearch.cs:58 «public readonly List<List<RouteHop>> Routes = [];»), che vive quanto una chiamata a `Find`.

| Collezione | Archivio | Chi scrive | Chi legge |
|---|---|---|---|
| `PathDevices` (PathDevices.cs:30 «Lazy<PathDevices> LazyPathDevices») | `ConcurrentDictionary<Guid, AutomationDevice?>` (:35 «private readonly ConcurrentDictionary<Guid, AutomationDevice?> _items») | nel motore, un solo punto: `BuildNavigatorItemsJunctions` chiama `Add(Guid)` sugli estremi di ogni giunzione (Navigator.cs:1733-1734 «PathDevices.Instance.Add(item.SourceId);»); `Add(Guid)` è un `GetOrAdd` che costruisce il device se manca (PathDevices.cs:252 «var device = _items.GetOrAdd(guid, created);»). **Per conto dell'Editor scrive l'host**, con `PlantQueries` dietro gli endpoint di `PlantEndpoints`: la configurazione di un dispositivo lo crea se manca (PlantQueries.cs:166 «PathDevices.Instance.Add(config.ItemId)», da PlantEndpoints.cs:38 «PlantQueries.UpsertItemConfigurationAsync») e lo svuotamento dell'impianto svuota la collezione (:204 «PathDevices.Instance.Clear();», da PlantEndpoints.cs:62 «PlantQueries.RemoveAllItemsAsync»); togliere un dispositivo o salvare il diagramma toccano solo il database, e la memoria si allinea con la ricarica intera (PlantQueries.cs:35 «await ExecutableCommands.ReloadRouteDiagram();») | ovunque: `Navigator` per risolvere i device dagli eventi PLC (Navigator.cs:338 «var item = PathDevices.Instance»), `PathBuilder.Snapshot` (PathBuilder.cs:141 «var devices = PathDevices.Instance.List;»), `ExecutableCommands` in una quarantina di righe, `CommandToDo`, `AutomationJunction`/`MissionStep` per risolvere i propri estremi, `DeviceConnections.OpenConnection` (DeviceConnections.cs:363 «var communicatorToPlc = Instance») e l'API, che espone la lista e il singolo dispositivo (PlantQueries.cs:15-16 «public static IReadOnlyList<AutomationDevice> Devices()») |
| `PathJunctions` (PathJunctions.cs:23 «Lazy<PathJunctions> LazyPathJunctions») | `ConcurrentDictionary<Guid, AutomationJunction>` (:24 «private readonly ConcurrentDictionary<Guid, AutomationJunction> _items») | nel motore, un solo punto: `DbInfo.LoadJunctions`, che svuota e ricarica da `PlantJunctions` (DBInfo.cs:335 «PathJunctions.Instance.Clear();», :349 «PathJunctions.Instance.Add(new Items.AutomationJunction»). **Per conto dell'Editor scrive l'host**: `PlantQueries.AddJunctionAsync` aggiunge la giunzione disegnata (PlantQueries.cs:227 «PathJunctions.Instance.Add(junction);», da PlantEndpoints.cs:85 «PlantQueries.AddJunctionAsync») e `RemoveAllItemsAsync` svuota (:205 «PathJunctions.Instance.Clear();»); la cancellazione di una giunzione tocca solo il database (:65 «public static async Task<bool> RemoveJunctionAsync(Guid junctionId)») | `PathBuilder.Snapshot` (PathBuilder.cs:160 «var edges = PathJunctions.Instance.List»), `AutomationDevice.DestinationItemsLinked` (AutomationDevice.Properties.PathOptions.cs:117 «return PathJunctions.Instance.Where(x =>»), da cui dipende `IsLinkedToStorage`, `BuildNavigatorItemsJunctions` per creare i device (Navigator.cs:1731 «foreach (var item in PathJunctions.Instance.List.OfType<AutomationJunction>())») e l'API (PlantQueries.cs:21 «Junctions()») |
| `ActiveMissions` (ActiveMissions.cs:15 «public sealed class ActiveMissions») | `ConcurrentDictionary<Guid, PathMission?>` (:195 «private readonly ConcurrentDictionary<Guid, PathMission?> _items = new();») | nel motore, molti punti sparsi fra `ExecutableCommands`, i manager del dispositivo e `ORM/`. Aggiungono `ExecutableCommands` (`LoadLu` ExecutableCommands.Internal.cs:816 «ActiveMissions.Instance.Add(mission);», `ManageMoveLuToItem` :880 «ActiveMissions.Instance.Add(mission);», `MoveLuToItem` ExecutableCommands.Private.cs:406 «ActiveMissions.Instance.Add(newMission);»), `AutomationDeviceConnectionManager.NewLuOnASI` (:159 «Collections.ActiveMissions.Instance.Add(newMission);»), `AutomationDeviceGroupManager.StartGroupPickUp` (:1112 «Collections.ActiveMissions.Instance.Add(lu);»), `AutomationDeviceStepExecutor.ExecuteQueueLuToPickUp` (:773 «ActiveMissions.Instance.Add(missionToPickUp);») e il caricamento iniziale `DbInfo.LoadMissions` (DBInfo.Private.cs:192 «private static async Task LoadMissions(OperationTypes operationType», con l'`Add` a :412 «ActiveMissions.Instance.Add(mission);»); rimuovono `AutomationDeviceLuManager` (:471 «await Collections.ActiveMissions.Instance.RemoveUnused(device.LUtoPick);», :886 «await Collections.ActiveMissions.Instance.Remove(device.LoadedLu);», :913 «await Collections.ActiveMissions.Instance.RemoveUnused(device.LoadedLu);», :1078 «await Collections.ActiveMissions.Instance.RemoveUnused(lu);»), `AutomationDeviceConnectionManager` (:476 «await Collections.ActiveMissions.Instance.RemoveUnused», :524 «await Collections.ActiveMissions.Instance.RemoveUnused(device.LUtoPick);»), `AutomationDeviceGroupManager` (:761 «await Collections.ActiveMissions.Instance.RemoveUnused(lu);», :938 «await Collections.ActiveMissions.Instance.RemoveUnused(lu);», :1064 «await Collections.ActiveMissions.Instance.RemoveUnused(lu);»), `AutomationDeviceStepExecutor` (:982 «await ActiveMissions.Instance.RemoveUnused(luToPick);»), `PathMission` (PathMission.Methods.cs:1071 «await ActiveMissions.Instance.RemoveUnused(this);») e `DbInfo` in cinque punti del caricamento (DBInfo.Private.cs:37 «await ActiveMissions.Instance.RemoveUnused(loadUnit);» e seguenti); svuotano `ExecutableCommands.RemoveAllLu` (ExecutableCommands.cs:94 «await ActiveMissions.Instance.Clear(true);») e `LoadNavigatorData` (Navigator.cs:849 «await ActiveMissions.Instance.Clear(false);»). **Per conto dell'Editor scrive l'host**, e solo per svuotare: `PlantQueries.RemoveAllItemsAsync` (PlantQueries.cs:206 «await ActiveMissions.Instance.Clear(false);»); la missione creata a mano dall'Editor passa oggi dalla coda dei comandi come ogni altra | `Navigator.GetMissionByLuAssignmentId` e `GetMissionByLuId` (Navigator.cs:630 «internal static PathMission? GetMissionByLuAssignmentId(decimal luAssignmentId)», :666 «internal static PathMission? GetMissionByLuId(decimal luId)»), `AutomationDevice.LoadedLu` (AutomationDevice.Properties.cs:434 «public PathMission? LoadedLu => ActiveMissions.Instance»), `PathBuilder.GetActiveFilterMissions` (PathBuilder.cs:274 «return ActiveMissions.Instance») e l'API (MissionQueries.cs:11-20 «public static IReadOnlyList<PathMission> Active()») |
| `MissionSteps` (MissionSteps.cs:11 «public sealed class MissionSteps») | `ConcurrentDictionary<Guid, MissionStep>` (:16 «private readonly ConcurrentDictionary<Guid, MissionStep> _items = new();») | dieci punti, tutti nel motore e quasi tutti sotto `Items/`: i setter di `PathMission.MissionSteps` (PathMission.cs:550-551 «Collections.MissionSteps.Instance.RemoveAll(x => x.MissionId == MissionId);») e `AutomationDevice.MissionSteps` (AutomationDevice.Properties.LiveData.cs:873-875 «Collections.MissionSteps.Instance.RemoveAll(x =>»), `MissionStep.Remove()` (MissionStep.cs:213 «MissionSteps.Instance.Remove(this);»), `AutomationDevice.RemoveStep` e `RemoveStepsByGroupedMovementId` (AutomationDevice.cs:901 «Collections.MissionSteps.Instance.Remove(value);», :933 «Collections.MissionSteps.Instance.RemoveAll(x =>»), quattro `RemoveAll` sparsi (PathMission.cs:313 «Collections.MissionSteps.Instance.RemoveAll(x => x.MissionId == MissionId);», PathMission.Methods.cs:386 «Collections.MissionSteps.Instance.RemoveAll(c => c.MissionId == MissionId);», AutomationDeviceConnectionManager.cs:715 «Collections.MissionSteps.Instance.RemoveAll(x => x.GroupedMovementId == stepGuid);», AutomationDeviceGroupManager.cs:233 «Collections.MissionSteps.Instance.RemoveAll(x => x.MissionId == lu.MissionId);») e lo svuotamento di `LoadNavigatorData` (Navigator.cs:848 «MissionSteps.Instance.Clear();»). La classe irraggiungibile `AutomationDeviceStepExecutor1` di `Class1.cs`, che vi aggiungeva anche lei, non esiste più | i getter delle stesse due proprietà (PathMission.cs:591 «get { return Collections.MissionSteps.Instance.Where(x => x.MissionId == MissionId).ToList(); }», AutomationDevice.Properties.LiveData.cs:844 «return Collections.MissionSteps.Instance»), ogni lettura una scansione dell'intero dizionario, e l'API per gli step di una missione e di tutte (MissionQueries.cs:20 «Steps(Guid missionId)», :26 «AllSteps()») |
| `RoutesToBuild` (RoutesToBuild.cs:17 «Lazy<RoutesToBuild> Lazy») | `ConcurrentQueue<PathMission?>` + `ConcurrentDictionary` di lookup (:52 «private ConcurrentQueue<PathMission?> _items = new();», :24 «private readonly ConcurrentDictionary<Guid, PathMission> _missionLookup») | quattro produttori lato dispositivo (`Standard.AddLoadedLuToRouteToBuildQueue` Standard.cs:162 «RoutesToBuild.Instance.Enqueue(device.LoadedLu);», `AutomationDeviceLuManager.AddLoadedLuToRouteToBuildQueue` :432 «Collections.RoutesToBuild.Instance.Enqueue(device.LoadedLu);», `AutomationDeviceConnectionManager.ProcessDeviceTypeSpecificLogic` :977 «Collections.RoutesToBuild.Instance.Enqueue(device.LoadedLu);», `AutomationDeviceStepExecutor.HandleNoStepToDo` :1051 «RoutesToBuild.Instance.Enqueue(value);»), i riaccodamenti di `BuildRoutePath` (con attesa crescente Navigator.cs:1850 «RoutesToBuild.Instance.Enqueue(m);», senza rifare il lavoro quando non è ancora ora di ritentare :1707 «RoutesToBuild.Instance.Enqueue(mission);») e la sostituzione della coda in `LoadNavigatorData` (:852 «RoutesToBuild.Instance.Items = new();») | un solo consumatore, `BuildRoutePath` (Navigator.cs:1751 «mission = RoutesToBuild.Instance.Dequeue();»); l'indicizzatore di lookup (RoutesToBuild.cs:36 «_missionLookup.GetValueOrDefault(key)») è letto dai produttori per evitare i duplicati e da `BuildRoutePath` stesso (Navigator.cs:1515 «RoutesToBuild.Instance») |
| `ManagedLu` (ManagedLU.cs:18 «Lazy<ManagedLu> LazyManagedLu») | `ConcurrentDictionary<Guid, LoadUnit>` (:30 «Items = new ConcurrentDictionary<Guid, LoadUnit>();») | `PathMission.MissionCompleted` e `MissionStarted`, che vi aggiungono l'UDC in lavorazione se non c'è già (PathMission.Methods.cs:597 «ManagedLu.Instance.Add(newHandledLu);», :631 «ManagedLu.Instance.Add(newHandledLu);», :653 «ManagedLu.Instance.Add(newHandledLu);»), e lo svuotamento di `LoadNavigatorData` (Navigator.cs:850 «ManagedLu.Instance.Clear();»); i due `Remove` (ManagedLU.cs:105 «public void Remove(LoadUnit value)») non hanno chiamanti (grep ricorsivo su `ManagedLu.Instance`: solo `Add`, `Clear`, `Contains` e l'indicizzatore) | quasi solo `PathMission` stesso, per indicizzatore (PathMission.Methods.cs:313 «var handledLu = ManagedLu.Instance»), e `AutomationDeviceConnectionManager` (:993 «var handledLu = Collections.ManagedLu.Instance») |
| `CommandToDo` (ComandToDo.cs:20 «Lazy<CommandToDo> LazyCommandToDo») | `ConcurrentQueue<Command>` privata (:89 «private ConcurrentQueue<Command> Items { get; }») | `Execute()`, che ad ogni giro svuota la coda e la ricarica dal database (ComandToDo.cs:132-133 «Items.Clear();»), e il `Clear` pubblico chiamato da `LoadNavigatorData` (:78 «Items.Clear();», Navigator.cs:851 «CommandToDo.Instance.Clear();») | `MasterProcess` (Navigator.cs:2038 «await CommandToDo.Instance.Execute();»); `List` e l'indicizzatore non hanno chiamanti fuori dalla classe (grep ricorsivo su `CommandToDo.Instance`: solo `Clear` ed `Execute`) |
| `DeviceConnections` (DeviceConnections.cs:24 «Lazy<DeviceConnections> LazyPlCs») | `List<IConnection>` **non concorrente**, protetta dal lock `SyncItems` e con setter privato (:54 «public List<IConnection> Items { get; private set; }», :60 «internal static readonly object SyncItems = new();») | `Initialize()` (:222 «public static void Initialize()»), che svuota con `Clear()` (:112 «public static void Clear()») e aggiunge una connessione per PLC configurato (:255 «Instance.Add(plc);»), e `GetTestConnection`, che aggiunge il simulatore (:450 «Instance.Add(_tstConnection);»). I tre `Remove` (:276 «public void Remove(IConnection value)») non hanno più chiamanti: l'Editor, che li usava dalla griglia dei PLC, scrive oggi le connessioni sul database dell'host e il motore le rilegge alla `Initialize` successiva | `OpenConnection` e `CloseConnection` (:340 «internal static void OpenConnection()», :136 «public static void CloseConnection()»), chiamate da `Navigator` (Navigator.cs:1057 «DeviceConnections.OpenConnection();», :514 «DeviceConnections.CloseConnection();»), e l'indicizzatore per `Id` (DeviceConnections.cs:84 «public IConnection? this»); materia della sezione «Il dialogo con il campo» |

Sei delle otto espongono una proprietà `List` che **costruisce una copia nuova ad ogni
lettura** (per esempio PathDevices.cs:57 «public List<AutomationDevice> List» e
ActiveMissions.cs:253 «public List<PathMission?> List =>»): è il motivo per cui gli
svuotamenti di `LoadNavigatorData` chiamano il `Clear()` di ciascuna collezione e non
`List.Clear()` (vedi «Avvio» e la voce `B1` del registro), ed è un dettaglio da tenere presente in
ogni ciclo `foreach` su queste collezioni, perché ogni accesso alloca. Le due eccezioni sono
`RoutesToBuild.Items`, che restituisce la coda reale
(RoutesToBuild.cs:62 «get => _items;»), e `DeviceConnections.Items`, che è una
`List<IConnection>` esposta direttamente, anche se il setter è ormai privato
(DeviceConnections.cs:54 «public List<IConnection> Items { get; private set; }»).

### I campi e le proprietà statiche di `Navigator`

**Stato interno del motore** (`private`/`internal`):

| Membro | Chi scrive | Chi legge |
|---|---|---|
| `SyncExtraction` (Navigator.cs:90 «public static readonly SemaphoreSlim SyncExtraction = new SemaphoreSlim(1, 1);») | `readonly`: `SemaphoreSlim(1,1)`, il lock globale delle estrazioni | preso e rilasciato in otto punti: `ExtractionProcess` (:2005 «if (!await SyncExtraction.WaitAsync(SyncExtractionTimeout))»), tre punti di `ComandToDo` (:306 «return datiPostazione == null», :402 «navigatorCommand.Status = Command.StatusTypes.Error;», :751 «await ExecutableCommands.LoadLu(navigatorCommand);»), `ExecutableCommands` (:944 «navigatorCommand.Status = Command.StatusTypes.Error;»), `PathMission` (:986 «Navigator.WriteLog(»), `AutomationDeviceGroupManager` (:656 «acquired = await Navigator.SyncExtraction.WaitAsync(») e `AutomationDeviceStorageManager` (:356 «lu.DestinationAsi = string.Empty;»). Tutti e otto usano ora lo stesso tetto, `Navigator.SyncExtractionTimeout` (voce `B20`): prima sei avevano un timeout di 30 s e due attendevano sul solo `CancellationToken`, e in quei due l'attesa stava fuori dal `try`, quindi la cancellazione saltava il `finally` |
| `LoadingStatus` | **rimosso** con la voce `B7` | Era `internal static bool` mai assegnato, interrogato da dodici guardie dello strato `ORM/`. Quattro di quelle guardie stavano su **letture** e, col flag vero, avrebbero fatto sovrascrivere la configurazione dell'impianto: non era una protezione persa, era incoerente. La protezione vera — non riscrivere la riga mentre la si legge — e' ora un campo **per istanza** su `AutomationDevice` |
| `Database` | **rimosso** il 2026-09-12 | Era l'unica `Instance` di `MsSqlHelper`, letta da ogni file dello strato `ORM/` e passata da `DeviceConnections.Initialize` al costruttore di una `Connection`. Il motore non apre più SQL Server del gestionale: al suo posto ci sono `Navigator.Store`/`Navigator.History` per il database del WCS e `Navigator.Warehouse` per il confine |
| `Warehouse` (Navigator.cs:1113 «internal static IWarehouse RequireWarehouse(string context) =>») | l'host, prima di avviare il motore (`EngineHostedService`) | tutto il motore, sempre attraverso `RequireWarehouse`, che solleva se il confine non è cablato |
| `_eventLogWriter` (Navigator.cs:118 «private static IWriter? _eventLogWriter;») | `SetEventLogWriter` (:321 «WriteLog($"HistoryWriter: arresto scaduto con {history.Pending}») | solo `WriteLog` (:1148 «internal static void WriteAlert(PathMission value)») |
| `_cancellationTokenSource` / `_cancelToken` (Navigator.cs:119-120 «private static CancellationTokenSource? _cancellationTokenSource;») | assegnati in un solo punto, `LoadNavigatorData` (:837-838 «WriteLog($"BuildRoutePath background task failed:»), che prima annulla e rilascia il precedente (:878-737 «// Loop MasterProcess»). `Dispose` non assegna: chiama `Cancel()` sull'istanza corrente e la lascia lì (:307 «_cancellationTokenSource?.Cancel();») | le condizioni di uscita e i `Task.Delay` dei tre loop |
| `_isBuildingPath` / `_masterProcessIsRunning` (Navigator.cs:129-130 «private static bool _isBuildingPath;») | solo `BuildRoutePath` e `MasterProcess`, in apertura e nel `finally` | le rispettive guardie di rientranza |
| `CheckDataFromAsi` / `RecordDataFromAsi` (Navigator.cs:37-38 «public static CheckDataFromAsiInfo? CheckDataFromAsi;») | `OpenDatabases` (:801-802 «CheckDataFromAsi = new();») | solo l'Editor, per mostrarne le proprietà (FrmEDITOR.Private.cs:52 «frmWarnings.ShowDialog(this);» (non pubblicato), :173 «private void btnMissionsLUList_ItemClick(object sender» (non pubblicato)) |
| `UserId` (Navigator.cs:272 «if (PathDevices.Instance.Count == 0)») | **nessuno**: `private set` mai invocato, resta sempre `"KazamControl.Process"` | `PathMission`, `AutomationDeviceLuManager`, `AutomationDeviceStorageManager`, `DBInfo`: è l'utente con cui il motore firma le operazioni sul WMS |
| `WorkstationId` (Navigator.cs:293 «internal static string WorkstationId { get; } = "KazamControl";») | costante di fatto (`get`-only) | `WriteAlert`, `ServeExtractionWork` (che lo fa viaggiare come `ID_UTENTE` degli esiti, :1335 «new ReportExtractionOutcomesRequest(extraction.ExtractionHeaderId, WorkstationId, outcomes));»), `DBInfo` |

**Stato osservabile** (proprietà con logica nel setter):

- **`NavStatus`** (Navigator.cs:237 «public static NavigatorStatusTypes NavStatus») — descritto in
  «Avvio». Scritto da cinque punti, letto in decine di punti dell'area, di cui sei nel solo
  `Navigator` (:1326 «if (NavStatus != NavigatorStatusTypes.Run) return;»,
  :1742 «if (NavStatus != NavigatorStatusTypes.Run || _isBuildingPath)»,
  :1674 «if (NavStatus != NavigatorStatusTypes.Run) return;»,
  :2040 «if (NavStatus != NavigatorStatusTypes.Run || _masterProcessIsRunning) return;»,
  :2082 «await DbInfo.UpdateNavigatorStatusCounters(NavStatus == NavigatorStatusTypes.Run);»,
  :2083 «ExtractionTimers = NavStatus == NavigatorStatusTypes.Run;»). È l'interruttore generale: quasi ogni
  metodo che agisce sull'impianto comincia con un confronto su di esso.
- **`Defragmentation`** (Navigator.cs:202 «public static DefragmentationTypes Defragmentation») — scritta
  da `LoadNavigatorData` all'avvio (:851 «CommandToDo.Instance.Clear();»); il setter
  solleva `DefragmentationChanged`, a cui il motore stesso è iscritto dal costruttore statico
  con un handler `async void` senza `try` che scrive il nuovo valore sul database
  (Navigator.cs:2080-2087 «private static async Task UpdateStatus()»). Letta da
  `AutomationDeviceGroupManager` per decidere se avviare la sfrangiatura
  (AutomationDeviceGroupManager.cs:1133 «catch (Exception ex)»).
- **`ExtractionTimers`** (Navigator.cs:261 «internal static bool ExtractionTimers») — vedi
  «I tre loop».

**I due eventi statici.** `Navigator` dichiara due `public static event`. Non sono stato nel
senso dei campi qui sopra, ma vanno letti insieme a loro: sono code di delegati trattenute da
un tipo statico, e sono il canale con cui il cambio di stato raggiunge l'impianto.

| Evento | Chi lo solleva | Chi si iscrive |
|---|---|---|
| `StatusChanged` (Navigator.cs:202 «public static DefragmentationTypes Defragmentation») | il setter di `NavStatus`, ad ogni cambio effettivo di stato (Navigator.cs:200 «public static Guid DefaultRejectItem { get; set; } = Guid.Empty;») | **ogni `AutomationDevice`**, in `Initialize()`, che entrambi i costruttori chiamano (AutomationDevice.cs:1229 «Navigator.StatusChanged -= Navigator_StatusChanged;»; costruttori a :92 «public AutomationDevice()» e :101 «public AutomationDevice(Guid value)»); la disiscrizione avviene **solo** nel `Dispose` (:1014 «lu.PositionItem.Status = DeviceStatusTypes.Recovering;»). **Fuori dal motore, nessuno**: l'Editor non referenzia piu' il motore e non puo' iscriversi a questo evento — si iscrive a `EditorSession.EngineStatusChanged` (DiagramHandler.cs:291 «EditorSession.EngineStatusChanged += HandlerToEngineStatus;» (non pubblicato), disiscrizione a :658 «EditorSession.EngineStatusChanged -= HandlerToEngineStatus;» (non pubblicato)), che nasce dall'hub SignalR e non da qui |
| `DefragmentationChanged` (Navigator.cs:168 «public static event DefragmentationChangedEventHandler?») | il setter di `Defragmentation` (Navigator.cs:209 «DefragmentationChanged?.Invoke(null, new») | solo il motore stesso, dal costruttore statico (Navigator.cs:30 «DefragmentationChanged += Navigator_DefragmentationChanged;»); l'handler è l'`async void` senza `try` che persiste il valore (Navigator.cs:2080 «private static async Task UpdateStatus()»). Nessuna disiscrizione, e nessun altro iscritto in tutto il repository |

`StatusChanged` è il membro che pesa di più su qualunque estrazione, e va detto per esteso. Il
gestore lato dispositivo (AutomationDevice.cs:1266-1275 «DeviceTypes.CircularDistributor =>»)
ignora ogni stato diverso da `Run`; al passaggio a `Run`, invece, per **ciascun** dispositivo
chiama `LuManager.LuUnloadedFromAsi(StepTypes.Automatic)` se il dispositivo ha `AutoUnloadLu`, e
lancia un `_ = ExecuteStep()` **non atteso** se non è di tipo `Storage` e ha `AutoExecuteStep`.
Non è un evento, malgrado il nome coincida con quello di un gestore PLC del `Navigator`
(Navigator.cs:857 «catch (Exception ex)»): è una chiamata, e per giunta a un
`public async Task` (AutomationDeviceLuManager.cs:73 «public async Task LuUnloadedFromAsi(StepTypes value)»)
invocato **senza `await` e senza nemmeno lo scarto `_ =`**
(AutomationDevice.cs:1104 «_groupManager?.Dispose();»), quindi con
eccezioni non osservate. Sono due i task lanciati e mai attesi per ogni dispositivo, non uno. `NavStatus = Run` non è quindi
un flag che i loop leggono con comodo al giro successivo: è un fan-out sincrono su tutto
l'impianto, eseguito dentro il setter, sul thread di chi ha scritto lo stato. Ed è il motivo per
cui il tipo statico `Navigator` trattiene un riferimento a ogni `AutomationDevice` mai costruito
e non ancora disposto: è questa catena, prima ancora dei singleton, a rendere difficile estrarre
il `Navigator` dal resto.

**Configurazione** — nove proprietà `public static` con `{ get; set; }` semplice, senza
alcuna logica: `ActivateDebugLog`, `DefaultRejectItem`,
`DisableUnloadLu`, `DiscardLuManagedInWarehouse`, `LimitOneShippingNotePerBuffer`,
`LockCellOnStoringError`, `MasterProcessOnEvery`, `NumCurrentStoringStep`,
`SwitchLuLengthVsLuWidth` (Navigator.cs:176-265 «public static bool ActivateDebugLog { get; set; }»); a
queste si affianca `EventSink` (Navigator.cs:221 «public static Api.IEngineEventSink? EventSink { get; set; }»), della
stessa forma ma valorizzata dall'host e non dall'Editor (si veda «Eventi verso l'host»).
L'Editor non scrive nessuna di queste proprietà: i valori arrivano dalla sezione `Wcs:Engine` di
`appsettings.json` dell'host
(WcsHostOptions.cs:4 «public sealed class WcsHostOptions»). Le legge il motore, ciascuna in uno
o due punti soltanto:

| Proprietà | Letta da |
|---|---|
| `ActivateDebugLog` | `WriteLog`: se è `false` il log applicativo non viene scritto affatto, resta solo la `Console.WriteLine` del `finally` (Navigator.cs:1148-1153 «internal static void WriteAlert(PathMission value)») |
| `MasterProcessOnEvery` | il ritardo del loop `MasterProcess`, riletto ad ogni giro (Navigator.cs:842 «var masterLoop = Task.Run(async () =>»): modificarla ha effetto immediato |
| `DefaultRejectItem` | `PathMission` per il punto di rigetto di riserva (PathMission.Methods.cs:252 «case RejectionTypes.NoStoreCellAvailable:») |
| `DisableUnloadLu` | `ExecutableCommands` (:832 «if (DisableUnloadLu)») |
| `LimitOneShippingNotePerBuffer` | `ExtractionProcess`, che **non la applica**: la passa al gestionale nella richiesta del lavoro pronto (Navigator.cs:1992 «.GetPendingNoteWorkAsync(LimitOneShippingNotePerBuffer);»), dove dal compito 14 vive la decisione che governa |
| `LockCellOnStoringError` | `PathMission` (:444 «CellaDeposito datiCella = new(datiCelleDeposito.FirstOrDefault());») |
| `NumCurrentStoringStep` | `PathMission`, per limitare i dispositivi di stoccaggio candidati (:647 «var newHandledLu = ManagedLu.Instance») |
| `SwitchLuLengthVsLuWidth` | `AsiSendLuData`, che scambia larghezza e profondità del dato ricevuto dal PLC (Navigator.cs:489 «if (SwitchLuLengthVsLuWidth && e.LuDataFromAsiResult != null)») |
| `DiscardLuManagedInWarehouse` | **nessuno** nell'area WCS: grep ricorsivo trova ormai solo la dichiarazione e la voce di configurazione dell'host. Le quattro righe dell'Editor che la scrivevano e la persistevano sono sparite (voce `B17` del registro) |

### Cosa comporta per un'estrazione

Chi volesse isolare una parte del motore deve fare i conti con tre catene:

1. **Ogni oggetto di dominio conosce i singleton.** `AutomationJunction`, `MissionStep`,
   `PathMission` e `AutomationDevice` risolvono le proprie relazioni chiamando
   `PathDevices.Instance` / `MissionSteps.Instance` / `ActiveMissions.Instance` dentro i
   getter. Non ci sono riferimenti diretti fra oggetti: c'è un `Guid` e una ricerca globale.
   Estrarre `PathBuilder` senza portarsi dietro `PathDevices` e `PathJunctions` non è possibile
   senza cambiare quei getter.
2. **`Navigator.Store` e `Navigator.Warehouse` sono i due punti d'ingresso, tutt'e due statici.**
   Il primo è il database del WCS, il secondo il confine verso il gestionale; ogni file di `ORM/`
   passa da uno dei due come variabile globale. Uno strato di persistenza iniettabile passa da lì.
   (Fino al 2026-09-12 il punto unico era `Navigator.Database`, la connessione a SQL Server del
   gestionale: la forma del problema non è cambiata, sono cambiati i due bersagli.)
3. **`Navigator` è insieme configurazione, stato, orchestrazione e handler PLC.** Le quattro
   responsabilità stanno nello stesso tipo statico, e le prime due sono già oggi scritte da un
   progetto esterno (l'Editor) tramite proprietà pubbliche mutabili.

## Punti di attrito

I difetti e gli attriti del motore non stanno qui: stanno nel registro d'area,
debito-tecnico.md (non pubblicato), dove ogni voce ha anche una stima del costo di
riparazione e trova posto in una tabella ordinata per rapporto costo/beneficio. Questa sezione è
l'indice delle voci aperte che riguardano il motore, raccolte nei quattro gruppi in cui conviene
leggerle.

Le due più urgenti sono `D50`, il ripetitore di passi singoli irraggiungibile, e `D56`, la colonna
del numero di ostacoli non selezionata. Stanno entrambe nell'ultimo gruppo, quello del dispositivo,
e `D56` aspetta il collaudo in campo.

### Trasversali: sicurezza e compilazione

- **`A1`** — Un pin non documentato tiene lontane otto vulnerabilità di gravità alta, e sembra un riferimento ridondante (non pubblicato)

### Il ciclo di vita del motore e i tre loop

- **`B1`** — Svuotare `PathDevices` e `PathJunctions` alla ricarica richiederebbe di rilasciare i dispositivi, e oggi non li rilascia (non pubblicato)
- **`B3`** — `LookWhereAllocateLuTypes.BuildRouteToStoreForcing` non è gestito da `FindWhereStoreLu` (non pubblicato)
- **`B9`** — Il setter di `DatabaseConnectionString` avvia ancora il motore come effetto collaterale, per chi non può attendere (non pubblicato)
- **`B12`** — `ORM.AutomationDevice.ClearUnUsed` cancella righe in base a una collezione mai svuotata (non pubblicato)
- **`B17`** — `DiscardLuManagedInWarehouse` non è letta da nessuno (non pubblicato)
- **`B68`** — `Navigator` e' interamente statico, e questo blocca ogni test del motore (non pubblicato)
- **`B70`** — I riaccodamenti di `BuildRoutePath` continuano indefinitamente per una missione irrisolvibile (non pubblicato)

### I comandi in ingresso e il dialogo col campo

- **`C38`** — Tre punti dove un comando rivolto a un dispositivo inesistente potrebbe chiudersi come riuscito (non pubblicato)

### Il dispositivo, i manager e le strategie

- **`D49`** — Tre `await` residui su un `Task` che può essere nullo (non pubblicato)
- **`D50`** — `RepeatSingleSteps` ora è raggiungibile ma non ha mai girato in produzione, e non controlla se un comando è già stato mandato (non pubblicato)
- **`D54`** — `AutomationDeviceConnectionManager.AsiEvent` ha il corpo vuoto (non pubblicato)
- **`D56`** — `BuildLuExtraction` è codice nuovo che comincia a muovere merce, e non è ancora stato collaudato in campo (non pubblicato)
