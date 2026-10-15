> **Historical — not current.** Internal design notes of Sun Tech, written in Italian on 2026-09-12. They describe the system as it was then and are kept for reference only; the current documentation is in [docs/](../../../README.md).

# Wcs.Common — architettura

> **Nota per il repository pubblico.** Questo documento e' stato scritto per il repository interno. Non sono pubblicati: l'Editor WinForms (`Wcs.Editor`), `Wcs.Viewer.Test`, il driver PLC di Sun Tech con il suo protocollo e il suo simulatore (proprietari) e i documenti che lo descrivono (fra questi la mappa dell'area `WCS/doc/README.md` e il registro del debito tecnico), l'importatore che migra gli impianti dal database del gestionale di Sun Tech, gli script della demo, le specifiche, i piani, la coda dei lavori e i rapporti delle revisioni, e il monorepo del gestionale (i riferimenti `Kazam:`). I collegamenti a quelle parti sono resi come testo e segnati «(non pubblicato)». `Common` qui e' una cartella normale, non un submodule.

## Scopo

`Wcs.Common` è una libreria di soli tipi condivisi: enumeratori e classi di
argomenti per eventi .NET. Non contiene alcuna logica di dominio, nessuna macchina a stati,
nessuna chiamata I/O: definisce il vocabolario — nomi e valori — che `Wcs.Engine`,
`Wcs.Editor`, `Wcs.Viewer`, `Wcs.Api.Contracts` e `Wcs.Connection.Interface`
(e a cascata i driver, come `Wcs.Connection.Test`) usano per intendersi tra loro.
Esplicitamente **non** definisce comportamento: non sa come uno stato cambia, chi lo cambia o
quando; sa solo come si chiama.

## Dipendenze

**In entrata.** Il file
Wcs.Common.csproj «<Project Sdk="Microsoft.NET.Sdk">» non ha
`ProjectReference` verso nessun altro progetto dell'area: è la foglia della gerarchia. Lo
referenziano cinque progetti, tutti con `ProjectReference` esplicito verso questo
`.csproj`: `Wcs.Engine` (il motore, che consuma gli enum per la logica), `Wcs.Editor`
(WinForms, usa gli enum e gli eventi per colorare/aggiornare il diagramma),
`Wcs.Viewer` (usa gli enum per tipizzare le proprietà di `Items/*`),
`Wcs.Api.Contracts` (i DTO dell'API del motore sono `using static Wcs.Common.Enums`, per
condividere gli stessi valori sul filo) e `Wcs.Connection.Interface`
(usa `Enums.ResultCodeTypes` per il contratto di risultato delle comunicazioni PLC, si
vedano RunASI.cs «using static Wcs.Common.Enums;»,
Connection.cs «using System.Globalization;» nel driver di prova, che dipende da Interface).

**In uscita.** Nessuna. `Wcs.Common.csproj` non dichiara alcun `PackageReference`. Il formato
sul filo fra motore e Viewer è JSON, portato da `Wcs.Api.Client`/`Wcs.Api.Contracts` (si
veda [architettura.md](../Wcs.Viewer/architettura.md) del Viewer).

## Tipi chiave nel loro ruolo

### Gli enumeratori

Tutti gli enumeratori vivono in un'unica classe statica,
Enums.cs «public static class Enums», con 22 enum annidati sotto un solo
`#region Public Enums`. Quasi tutti sono `byte` (eccezione: `StatusExtractionLu`, che non
dichiara un tipo sottostante esplicito e usa valori negativi, vedi sotto). Molti valori
portano un attributo `[Description]` in inglese (da `System.ComponentModel`); non tutti gli
enum lo usano in modo uniforme.

**Da verificare:** a cosa servono gli attributi `[Description]`. Nel codice pubblicato nessun punto legge un attributo `[Description]` via reflection (l'unico che lo faceva sta nel driver PLC di Sun Tech, non pubblicato, e riguarda un altro enum, `ChannelStatusTypes`).
Nessun uso accertato degli attributi `[Description]` degli enum di `Wcs.Common`
risulta da questa ricerca; chi conosce l'Editor dovrebbe confermare se servono alla sua UI o
sono vestigiali.

| Enum | Valori | Chi decide in base al valore |
|---|---|---|
| `CellLockTypes` | None, Loading, Unloading, Full (Enums.cs:9 «public enum CellLockTypes : byte») | `AutomationDeviceStorageManager` decide se una cella è occupata confrontando `Full`/`Loading` (AutomationDeviceStorageManager.cs:938 «if (string.IsNullOrEmpty(doorCode) && doorCodeNotExtractable.Contains»); `DBInfo.GetCellLockStatus` la legge dal DB (DBInfo.cs:285 «internal static async Task<CellLockTypes> GetCellLockStatus(decimal») |
| ~~`CommandStatusTypes`~~ | **non esiste più**: né la dichiarazione né un riferimento, in nessun file `.cs` del repository (grep del nome, 2026-09-10) | Nessuno. Era l'enumeratore senza lettori di cui parlava questo documento; lo stato dei comandi lo porta `Command.StatusTypes` nel motore, che è quello che lo scrittore e `CommandStore.PendingAsync` confrontano |
| `ConnectionTypes` | Test, Connected (Enums.cs:57 «public enum ConnectionTypes : byte») | `DeviceConnections.OpenConnection` assegna ai dispositivi con `ConnectionType == Test` la connessione fittizia singleton restituita da `GetTestConnection()`, nel `case Enums.ConnectionTypes.Test:` (DeviceConnections.cs:270 «WriteLog($"Error On PLCs->Remove Description:»); lo stesso valore governa anche, separatamente, un dettaglio di calcolo in `AutomationDeviceConnectionManager.CalculateDepth`, che salta la moltiplicazione `depth *= 10` per i dispositivi Test (AutomationDeviceConnectionManager.cs:433 «if (device.ConnectionType != ConnectionTypes.Test &&») |
| `DefragmentationTypes` | None, Disabled, Enabled (Enums.cs:64 «public enum DefragmentationTypes : byte») | `Navigator` (motore) espone lo stato globale (Navigator.cs:175 «get => _databaseConnectionString;»); `AutomationDeviceGroupManager` decide se avviare la sfrangiatura solo quando è `Enabled` (AutomationDeviceGroupManager.cs:1133 «catch (Exception ex)») |
| `DeviceStatusTypes` | Ready, Moving, Loading, Unloading, Busy, Recovering, OffLine, Locked, Warning=89, Error=99 (Enums.cs:73 «public enum DeviceStatusTypes : byte») | `AutomationDeviceStateMachine` è il proprietario dello stato: tiene `_currentState` (AutomationDeviceStateMachine.cs:46 «private volatile DeviceStatusTypes _currentState =»); `CanChangeToOfflineState` decide se la transizione verso/da offline è ammessa combinando più valori con pattern `is ... or ...` (AutomationDeviceStateMachine.cs:208 «private bool CanChangeToOfflineState(bool goingOffline)»), mentre la validazione generale di ogni altra transizione, `IsValidTransition`, non usa quel pattern (AutomationDeviceStateMachine.cs:237 «private bool IsValidTransition(DeviceStatusTypes from») |
| `DeviceTypes` | Automation, Loading, Buffer, Storage, CircularDistributor, GroupOfAutomationDevices=5, LoadingAndBuffer=6, None=99 (Enums.cs:96 «public enum DeviceTypes : byte») | Praticamente ogni manager sotto `Items/Managers` ramifica su questo valore per scegliere la strategia di esecuzione; esempio rappresentativo in `AutomationDeviceStateMachine` (AutomationDeviceStateMachine.cs:132 «device.RunningStepId = Guid.Empty;») |
| `DiagramItemTypes` | Automation, Junction, None=99 (Enums.cs:115 «public enum DiagramItemTypes : byte») | Solo l'Editor: `DiagramManager.GetDiagramType` risale la gerarchia dei nodi del diagramma per classificarli, delegando la lettura di ciascun nodo a `ReadDiagramType` (DiagramManager.cs:260 «/// <returns>Il tipo di elemento del diagramma.</returns>» (non pubblicato), che chiama DiagramManager.cs:609 «"JUNCTION" => DiagramItemTypes.Junction,» (non pubblicato)) |
| `EmptyCellStatusTypes` | None, Active (Enums.cs:122 «public enum EmptyCellStatusTypes : byte») | `AutomationDevice` e `AutomationDeviceStepExecutor` lo usano come guardia one-shot per evitare doppie notifiche di cella vuota (AutomationDevice.cs:509 «stepToDo.RunningItem = this;») |
| `ExecutedStepTypes` | PickUp, Store (Enums.cs:129 «public enum ExecutedStepTypes : byte») | `AutomationDeviceStepExecutor` registra l'ultima azione fisica compiuta dal device (AutomationDeviceStepExecutor.cs:202 «await ExecuteQueueLuToPickUp();») |
| `LookWhereAllocateLuTypes` | No, CheckOnly, BuildRouteToStore, BuildRouteToStoreByIdLuType, BuildRouteToDifferentStoreLocation, BuildRouteToStoreForcing (Enums.cs:135 «public enum LookWhereAllocateLuTypes : byte») | `AutomationDeviceLuManager` decide se e come costruire il percorso di stoccaggio in base al valore (AutomationDeviceLuManager.cs:781 «(device.LoadedLu?.DestinationItem != null &&») |
| `LuLoadStatusTypes` | None, Loaded (Enums.cs:157 «public enum LuLoadStatusTypes : byte») | `AutomationDevice` lo calcola in base a `LoadedLu` (AutomationDevice.cs:603 «IdLuType = idLuType»); è anche uno dei due parametri di `AutomationDeviceStatusChangedEventArgs` |
| `MissionStatusTypes` | Run, Stop, Done, Error=99 (Enums.cs:164 «public enum MissionStatusTypes : byte») | `PathMission.Status` è la proprietà di stato della missione; il motore la porta a `Run` all'avvio dell'esecuzione (PathMission.Methods.cs:1066 «MissionStarted(PositionItem, this);») |
| `MissionTypes` | Storing, Extraction, ChangeStoreLocation=2, MovingToItem=5, StoringByPickUp=6, Rejection=7, PickUpAndStoreAgain=9, DefragmentCell=10, None=99 (Enums.cs:172 «public enum MissionTypes : byte») | `PathBuilder` non sceglie un algoritmo diverso in base al tipo missione: l'unico uso di `MissionType` nel file, `IgnoreItemStatus = mission.MissionType == MissionTypes.PickUpAndStoreAgain` (PathBuilder.cs:82 «IgnoreItemStatus = mission.MissionType == MissionTypes.PickUpAndStoreAgain;»), imposta un solo flag booleano, che viaggia nella `RouteRequest` e poi sceglie quale verifica di stato applicare al nodo in `RouteSearch.IsAccessible` (RouteSearch.cs:148 «if (request.IgnoreItemStatus)») |
| `MotionTypes` | MoveTo, GetFrom (Enums.cs:194 «public enum MotionTypes : byte») | `AutomationDeviceStepExecutor` sceglie chi è l'esecutore fisico del movimento con uno `switch` esplicito (AutomationDeviceStepExecutor.cs:323 «if (item == null) return;») |
| `NavigatorStatusTypes` | Stop, Run, Recover, Dispose, Pause (Enums.cs:203 «public enum NavigatorStatusTypes : byte») | `Navigator` è il proprietario: tutto il ciclo di costruzione percorsi è condizionato da `NavStatus != Run` (Navigator.cs:1326 «if (NavStatus != NavigatorStatusTypes.Run) return;», Navigator.cs:1297 «await AssignLUbyReferenceExtractions(item.GetValue<decimal>», Navigator.cs:1846 «RecordNavigatorLog(null,») |
| `OperationTypes` | Inbound, Outbound, Compacting=3, None=99 (Enums.cs:216 «public enum OperationTypes : byte») | `AutomationDevice` lo imposta creando nuove missioni (AutomationDevice.cs:603 «IdLuType = idLuType») |
| `RecordStorageTypes` | None, FromPlc, FixedLength, ConvertDepthToPartition, CalculatedStorageDepth (Enums.cs:224 «public enum RecordStorageTypes : byte») | `AutomationDeviceStorageManager` sceglie con uno `switch` a espressione come calcolare la profondità di stoccaggio (AutomationDeviceStorageManager.cs:215 «RecordStorageTypes.ConvertDepthToPartition => await») |
| `ResultCodeTypes` | None, Success, TimeOut, ConnectionError, Error=99 (Enums.cs:239 «public enum ResultCodeTypes : byte») | Attraversa il confine coi driver PLC: usato sia da `AutomationDeviceStepExecutor` per marcare l'esito di uno step (AutomationDeviceStepExecutor.Private.cs:851 «executedStep.StepStatus = ResultCodeTypes.Success;») sia dai driver (come `Wcs.Connection.Test.Connection`) per riportare l'esito di una comunicazione |
| `StatoOperazioneCella` | Nessuna, Deposito, Prelievo (nomi in italiano, unico enum non in inglese, Enums.cs:248 «public enum StatoOperazioneCella : byte») | Solo `AutomationDeviceStorageManager`, per bloccare/sbloccare una cella nel DB durante deposito o prelievo (AutomationDeviceStorageManager.cs:721 «if (row.GetValue<decimal>("ID_CELLA_DESTINAZIONE") == 0 ||») |
| `StatusExtractionLu` | NotExtracted=-1, InExtraction=0, Extracted, NotReachable, InOtherExtraction, LuLocked (Enums.cs:255 «public enum StatusExtractionLu») | `AutomationDeviceStepExecutor` traccia se una LU (unità di carico) è raggiungibile per l'estrazione, aggiornando il DB (AutomationDeviceStepExecutor.Private.cs:896 «await DbInfo.UpdateStatusExtractionLu(luToPick.ExtractionQueueId») |
| `StepTypes` | Automatic, Manual (Enums.cs:265 «public enum StepTypes : byte») | `PathMission` distingue l'origine dello step (automatica o manuale) per decidere il flusso di stoccaggio (PathMission.Methods.cs:370 «foreach (var step in Collections.MissionSteps.Instance.Where(x =>») |

**Da verificare:** `StatusExtractionLu` è l'unico enum senza tipo sottostante dichiarato
esplicitamente e l'unico coi valori negativi; se viene serializzato in JSON verso l'esterno
in qualche punto (non risulta usato in nessun DTO di `Wcs.Api.Contracts` attuale) il
comportamento d'interoperabilità con `-1` andrebbe controllato con chi conosce lo schema.

### Gli eventi

I quattro tipi in `Events/` sono tutti `EventArgs` usati per la notifica locale, in-process,
di cambio proprietà. Solo uno è davvero immutabile dopo la costruzione:
`DefragmentationChangedEventArgs` espone `DefragmentationStatus` con `{ get; private set; }`
(DefragmentationChangedEventArgs.cs:10 «public DefragmentationTypes DefragmentationStatus { get; private set; }»).
Gli altri tre hanno membri pubblicamente mutabili:
`AutomationDeviceStatusChangedEventArgs.ItemStatus`/`.LuStatus` e
`AutomationDeviceRunningStepIdChangedEventArgs.RunningStepId`/`.WaitingStepId` sono campi
pubblici scrivibili
(AutomationDeviceStatusChangedEventArgs.cs:10-11 «public DeviceStatusTypes ItemStatus = valueItem;»,
AutomationDeviceRunningStepIdChangedEventArgs.cs:8-9 «public Guid RunningStepId = runningStepId;»),
e `NavigatorStatusChangedEventArgs.NewStatus`/`.OldStatus` hanno `{ get; set; }`
(NavigatorStatusChangedEventArgs.cs:33-33 «public NavigatorStatusTypes NewStatus { get; set; } =»).
**Restano locali a ciascun processo**: sono un
pattern C# standard (delegate + evento), riusato identicamente in tre progetti diversi
(`Wcs.Engine`, `Wcs.Editor`, `Wcs.Viewer`) su classi di dominio
distinte che modellano lo stesso concetto (il device fisico nel motore, il suo
corrispondente nell'Editor, il suo corrispondente nel Viewer). Il vocabolario è condiviso,
ma ogni progetto lo solleva sulla propria istanza locale.

- **AutomationDeviceStatusChangedEventArgs.cs «public class AutomationDeviceStatusChangedEventArgs(LuLoadStatusTypes»**
  porta `LuLoadStatusTypes` e `DeviceStatusTypes`. Sollevato da
  `Wcs.Engine.Items.AutomationDevice` sui propri setter di stato
  (AutomationDevice.cs:606 «EnqueueLuToPickUp(newMission);» e
  :670 «newMission.GuidAssignmentLu = item.GetValue<Guid>») e, allo stesso modo, da
  `Wcs.Viewer.Items.AutomationDeviceBase`
  (AutomationDeviceBase.cs:87 «LuLoadStatusChanged?.Invoke(this, new»
  e :91 «public string Name { get; set; } = string.Empty;»). Nell'Editor non è
  ascoltato ma **costruito**: lo stato arriva dall'hub come `ItemStatus`, e `DiagramHandler`
  ne fabbrica un `AutomationDeviceStatusChangedEventArgs` per riusare il codice che colora i nodi
  (DiagramHandler.Private.cs:376 «UpdateItemStatus(node, new AutomationDeviceStatusChangedEventArgs» (non pubblicato),
  :243 «private static void ManageStatusColor» (non pubblicato)).
- **AutomationDeviceRunningStepIdChangedEventArgs.cs «public class AutomationDeviceRunningStepIdChangedEventArgs(Guid»**
  porta due `Guid` (step in esecuzione e step in attesa). Sollevato analogamente dal motore
  (AutomationDevice.Properties.LiveData.cs:778 «Description("Returns the list of UDCs that are awaiting transfer")»)
  e dal Viewer
  (AutomationDeviceBase.cs:80 «public Enums.LuLoadStatusTypes LuLoadStatus»),
  nell'Editor, come il precedente, non è ascoltato ma costruito dallo stato ricevuto dall'hub,
  per aggiornare le linee di movimento sul diagramma
  (DiagramHandler.Private.cs:378 «UpdateItemRunningStepId(item, node, new» (non pubblicato)).
- **DefragmentationChangedEventArgs.cs «public class DefragmentationChangedEventArgs(DefragmentationTypes»**
  porta `DefragmentationTypes`. Sollevato dal `Navigator` del motore
  (Navigator.cs:738 «await LoadNavigatorData();») e dal `Navigator` del Viewer
  (Navigator.cs:148 «_defragmentation = value;»); il motore lo ascolta
  anche su sé stesso (Navigator.cs:1786 «RoutePathRetries.Forget(mission.MissionId);»).
- **NavigatorStatusChangedEventArgs.cs «public class NavigatorStatusChangedEventArgs : EventArgs»**
  porta `OldStatus`/`NewStatus` (`NavigatorStatusTypes`). Sollevato dal `Navigator` di
  motore e Viewer. L'Editor non vede gli eventi del motore: riceve un `EngineStatus` dall'hub
  e si iscrive a `EditorSession.EngineStatusChanged`
  (DiagramHandler.cs:291 «EditorSession.EngineStatusChanged += HandlerToEngineStatus;» (non pubblicato)), allineandosi subito
  allo stato corrente invece di fabbricare un evento
  (DiagramHandler.cs:297 «HandlerToEngineStatus(EditorSession.EngineStatus);» (non pubblicato)). Il difetto del
  costruttore descritto nei punti di attrito resta, per chi lo usa dentro il motore.

## Stato e proprietà

`Wcs.Common` **non ha stato mutabile proprio**. È interamente composto da:
definizioni di enum (`Enums.cs`, una singola classe `static`) e classi di eventi (i
costruttori impostano i valori iniziali, nessuna logica successiva; solo una delle quattro,
`DefragmentationChangedEventArgs`, è davvero immutabile dopo la costruzione — le altre tre
espongono membri pubblicamente scrivibili, vedi Gli eventi). Questo è rilevante per
chi rifattorizza: non c'è nessuna istanza singleton, nessun campo `static` con dati, nessun
side effect nascosto da cercare qui.

## Punti di attrito

Su questo componente il registro d'area,
debito-tecnico.md (non pubblicato), non ha voci aperte. È coerente con quello che
`Wcs.Common` è: un vocabolario senza logica e senza stato, che è anche il motivo per cui è la foglia
della gerarchia.

