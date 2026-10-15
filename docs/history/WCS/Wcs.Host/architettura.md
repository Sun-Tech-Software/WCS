> **Historical — not current.** Internal design notes of Sun Tech, written in Italian on 2026-09-22. They describe the system as it was then and are kept for reference only; the current documentation is in [docs/](../../../README.md).

# Wcs.Host — architettura

> **Nota per il repository pubblico.** Questo documento e' stato scritto per il repository interno. Non sono pubblicati: l'Editor WinForms (`Wcs.Editor`), `Wcs.Viewer.Test`, il driver PLC di Sun Tech con il suo protocollo e il suo simulatore (proprietari) e i documenti che lo descrivono (fra questi la mappa dell'area `WCS/doc/README.md` e il registro del debito tecnico), l'importatore che migra gli impianti dal database del gestionale di Sun Tech, gli script della demo, le specifiche, i piani, la coda dei lavori e i rapporti delle revisioni, e il monorepo del gestionale (i riferimenti `Kazam:`). I collegamenti a quelle parti sono resi come testo e segnati «(non pubblicato)». `Common` qui e' una cartella normale, non un submodule.

`Wcs.Host` è **l'eseguibile di produzione del WCS**. Ospita il motore (`Wcs.Engine`) nello stesso
processo, senza interfaccia grafica, e lo espone in due modi: un'API HTTP/JSON per le domande e i
comandi puntuali, e un hub SignalR che spinge gli eventi ai client mentre accadono. L'Editor resta
lo strumento di progettazione, ma non è il modo di tenere acceso un impianto.

Il progetto è deliberatamente sottile: **non contiene logica d'impianto**. Un endpoint chiama il
motore attraverso le sue facciate `public static` (`Wcs.Engine/src/Api/`), o legge una proprietà
di `Navigator` quando basta quella, e mappa il risultato in un DTO di `Wcs.Api.Contracts`; la mappa sta tutta in un file
(DtoMapping.cs:11 «internal static class DtoMapping»). Se una domanda richiede una
decisione, la decisione sta nel motore.

## Le parti

| File | Ruolo |
|---|---|
| Program.cs «using Wcs.Api.Contracts;» | composizione: servizi, controllo della chiave, catena dei middleware, gruppi di rotte, hub |
| WcsHostOptions.cs «public sealed class WcsHostOptions» | la sezione `Wcs` di `appsettings.json`, tipizzata |
| Security/ApiKeyMiddleware.cs «public sealed class ApiKeyMiddleware(RequestDelegate next» | le chiavi statiche completa e di sola lettura, entrambe lette soltanto dall'header `X-Api-Key` |
| Engine/EngineHostedService.cs «public sealed class EngineHostedService(IOptions<WcsHostOptions>» | avvia e spegne il motore col servizio, e gli passa la configurazione |
| Engine/PlantEventRelay.cs «public sealed class PlantEventRelay : IEngineEventSink, IHostedService» | l'`IEngineEventSink` del motore: accoda gli eventi e un solo consumatore li spinge sull'hub |
| Hubs/PlantHub.cs «public sealed class PlantHub : Hub<IPlantEvents>;» | l'hub, senza gruppi: un host serve un impianto |
| EngineExceptionHandler.cs «internal sealed class EngineExceptionHandler(IProblemDetailsService» | traduce in codici HTTP le quattro eccezioni con cui le facciate dicono «non posso rispondere»: `503` senza database del motore e senza connessione al WMS, `400` per un mese fuori dallo storico, `409` per una scrittura di configurazione a motore in `Run` |
| `Endpoints/*.cs` | un file per gruppo di rotte: stato, comandi, impianto, missioni, coda di estrazione, storico, PLC |
| Mapping/DtoMapping.cs «internal static class DtoMapping» | entità del motore ↔ DTO dei contratti |

## Installazione come servizio Windows

L'host chiama `UseWindowsService` col nome di servizio `Wcs.Host`
(Program.cs:11 «builder.Host.UseWindowsService(options => options.ServiceName =»), quindi lo stesso eseguibile gira sia da console
(per le prove) sia come servizio, senza cambiare nulla.

Pubblicazione e installazione, da un terminale con privilegi di amministratore:

```powershell
dotnet publish WCS\Wcs.Host\src\Wcs.Host.csproj -c Release -o C:\Wcs

New-Service -Name Wcs.Host -BinaryPathName 'C:\Wcs\Wcs.Host.exe' `
            -DisplayName 'WCS Host' -StartupType Automatic
Start-Service Wcs.Host
```

L'equivalente con `sc.exe` — attenzione allo spazio obbligatorio dopo `binPath=`:

```cmd
sc create Wcs.Host binPath= "C:\Wcs\Wcs.Host.exe" start= auto DisplayName= "WCS Host"
sc start Wcs.Host
```

Per disinstallare: `Stop-Service Wcs.Host; sc.exe delete Wcs.Host`.

Due avvertenze pratiche. La cartella di lavoro di un servizio Windows **non** è quella
dell'eseguibile, ma `System32`: per questo i percorsi relativi della configurazione vengono
risolti su `AppContext.BaseDirectory`, non sulla cartella corrente
(EngineHostedService.cs:123-125 «Navigator.DatabaseDirectory = Path.IsPathRooted»). E l'account
del servizio deve poter scrivere nella cartella dei database e raggiungere in HTTP l'API del
gestionale. Dal 2026-09-12 non gli serve più nessun accesso a SQL Server: la connessione al
database del gestionale è uscita dal WCS.

## Configurazione

`appsettings.json` sta **accanto all'eseguibile**
(appsettings.json «"Wcs": {» nel sorgente, `C:\Wcs\appsettings.json`
dopo la pubblicazione). Valgono le sorgenti di configurazione consuete di ASP.NET Core, nell'ordine:
`appsettings.json`, `appsettings.{Environment}.json`, variabili d'ambiente (`Wcs__ApiKey`,
`Wcs__ReadOnlyApiKey` e `Wcs__AllowInsecureRemoteHttp`, con due trattini bassi al posto dei due
punti) e riga di comando; l'ultima che parla vince.

### Sezione `Wcs`

| Chiave | Predefinito | Che cosa fa |
|---|---|---|
| `ApiKey` | `cambiami-prima-di-installare` | la chiave completa, necessaria per letture e scritture. **L'host non parte** finché è questa o è vuota |
| `ReadOnlyApiKey` | vuota | la chiave facoltativa per `GET`, `HEAD` e hub. Se presente deve essere diversa dalla chiave completa e dal segnaposto; Viewer e monitoraggio usano questa |
| `AllowInsecureRemoteHttp` | `false` | consente esplicitamente un endpoint HTTP non loopback. Va usato soltanto su una LAN d'impianto segregata: non cifra chiavi o dati e produce un log `Critical` all'avvio |
| `DatabaseDirectory` | `Data` | dove stanno `wcs.db` e i file mensili dello storico; se relativo, si risolve sotto la cartella dell'eseguibile |
| `HistoryMonthsToKeep` | `12` | quanti file mensili dello storico si conservano, compreso quello corrente: all'avvio l'host cancella i più vecchi. |
| `MaxRequestBodyMegabytes` | `256` | quanto può pesare il corpo di una richiesta. Alza il limite di Kestrel, che di suo si ferma a 30 MB: il diagramma viaggia come JSON con i byte in base64 e quello dell'impianto di prova arriva a 48 MB, cioè un `413` al `PUT /plant/diagram` |
| `StartEngine` | `true` | falso espone l'API senza avviare il motore: è così che girano i collaudi |
| `AutoRun` | `false` | vero fa passare il motore in `Run` da solo dopo il caricamento, cioè apre le connessioni ai PLC |

### Sezione `Wcs:Engine`

Sono le proprietà globali del motore, quelle che l'Editor teneva nelle proprie `Settings`
(WcsHostOptions.cs:25-36 «public sealed class EngineSettings»); l'host le copia su `Navigator`
all'avvio (EngineHostedService.cs:126-135 «Navigator.HistoryMonthsToKeep = settings.HistoryMonthsToKeep;»).

| Chiave | Predefinito |
|---|---|
| `DefaultRejectItem` | `00000000-0000-0000-0000-000000000000` |
| `MasterProcessOnEvery` | `1000` |
| `NumCurrentStoringStep` | `0` |
| `LockCellOnStoringError` | `false` |
| `LimitOneShippingNotePerBuffer` | `false` |
| `DiscardLuManagedInWarehouse` | `false` |
| `DisableUnloadLu` | `false` |
| `SwitchLuLengthVsLuWidth` | `false` |
| `ActivateDebugLog` | `false` |

`LimitOneShippingNotePerBuffer` è l'unica delle nove che il motore **non applica**: dal compito 14
l'orchestrazione delle note di spedizione vive nel gestionale, e il motore si limita a passargli
l'opzione nella richiesta del lavoro pronto
(Navigator.cs:1992 «.GetPendingNoteWorkAsync(LimitOneShippingNotePerBuffer);»).
La voce resta qui, e non nella configurazione del gestionale, perché spostarla obbligherebbe ogni
impianto a spostarla durante l'aggiornamento: un aggiornamento che la dimenticasse spegnerebbe il
limite in silenzio.

### Le altre sezioni

| Chiave | Predefinito | Che cosa fa |
|---|---|---|
| `Wms:ApiAddress` | `http://localhost:5142` | l'origine dell'API del gestionale (schema, nome e porta, senza percorso): da qui il motore prende il magazzino. **Obbligatoria** quando `StartEngine` è vero, e l'host non parte se manca o non è un'origine http assoluta |
| `Wms:ApiKey` | `cambiami-prima-di-installare` | la chiave che il WCS porta all'API del gestionale nell'intestazione `X-Api-Key`. Deve essere **identica** a `WcsWarehouse:ApiKey` di `WMS.Services.API`. **L'host non parte** finché è questa o è vuota: si mette in `Wms__ApiKey`, non nel file tracciato |
| `Wms:TimeoutSeconds` | `30` | il tetto di una chiamata al gestionale. Sotto `5` l'host non parte: sarebbe un refuso, non una politica (WmsOptions.cs:49 «public const int MinimumTimeoutSeconds = 5;») |
| `Kestrel:Endpoints:Http:Url` | `http://127.0.0.1:5580` | l'indirizzo di ascolto predefinito: soltanto il loopback, porta 5580 |
| `Logging:LogLevel:Default` | `Information` | il registro di ASP.NET Core; come servizio finisce nel registro eventi di Windows |

**`Wms:ConnectionString` non c'è più.** Fino al 2026-09-12 era obbligatoria con `StartEngine` vero,
perché il motore apriva SQL Server del gestionale per leggerne le tabelle; adesso non la legge
nessuno, e una chiave rimasta in un'installazione vecchia viene ignorata
(WmsOptions.cs:9 «Nella stessa sezione viveva <c>Wms:ConnectionString</c>»).

Il confine con il gestionale è quindi uno solo, ed è HTTP: `Navigator.Warehouse` — l'unica porta
da cui il motore chiede al gestionale celle, prenotazioni, note e estrazioni — lo costruisce
**l'host**, con `Wms:ApiAddress` e `Wms:ApiKey`, e glielo mette **prima** di avviarlo
(EngineHostedService.cs:144-145 «_warehouse = CreateWarehouse(wmsOptions.Value);»).
Il motore non se lo costruisce mai da solo, e se la configurazione manca l'host **non parte**
(EngineHostedService.cs:228-251 «private IWarehouse CreateWarehouse(WmsOptions settings)»):
un motore avviato senza magazzino non farebbe un servizio ridotto, solleverebbe al primo
movimento. Il `HttpClient` viene dalla fabbrica dei client
(Program.cs:30 «builder.Services.AddHttpClient();»), con il nome
`wms-warehouse`, e vive quanto il motore.

Nella direzione opposta non c'è più niente. Il motore ospitava `WMS.Services` nel proprio
processo (`Navigator.WmsBusinessArea`) e gli passava alla costruzione un'implementazione
dell'area `Automazione` che rispondeva dalle proprie facciate senza passare da HTTP
(`InProcessAutomation`), perché altrimenti l'host avrebbe interrogato se stesso a ogni deposito
automatico. Con il taglio dei riferimenti del motore ai progetti del gestionale il campo, la
classe e il riferimento a `WMS.Services` sono stati cancellati: l'area `Automazione` ha una sola
implementazione, quella HTTP su `SessioneWcs`, e la usano i processi del WMS (Bff,
`WMS.Services.API`, Client), che dall'host passano dalle rotte come chiunque altro.

L'host collega comunque al motore, all'avvio e prima della guardia su `StartEngine`, i propri due
registri: quello degli errori
(EngineHostedService.cs:76 «Navigator.HostErrorLog = _hostErrorLog;»),
dove scrive `Navigator.ReportError` senza guardare `ActivateDebugLog`
(Navigator.cs:1189 «internal static void ReportError(Exception ex)»),
e quello della ritenzione dello storico (`WcsHistoryDatabase.Notice`). Dentro il motore
`ReportError` oggi non ha chiamanti — i due che ci arrivavano, l'`onError` della `BusinessArea` e
la traccia dell'area in processo, sono spariti con il taglio — e resta il posto dove va un errore
che non può sollevare. `ActivateDebugLog` governa solo `Navigator.WriteLog`, la diagnostica del
motore, e non tocca `HostErrorLog`.

### Perché l'host rifiuta la chiave d'esempio

Il controllo è esplicito (Program.cs:45-46 «if (string.IsNullOrWhiteSpace(apiKey) || apiKey ==»): con `ApiKey` vuota
o uguale a `cambiami-prima-di-installare` l'host solleva e il servizio non parte. È una
protezione contro l'installazione dimenticata, non contro un attacco: un `appsettings.json`
copiato tale e quale da un impianto all'altro avrebbe la stessa chiave dappertutto.

Il controllo sta **dopo** `Build()` e legge `app.Configuration`: nel collaudo,
`WebApplicationFactory` inietta la configurazione in memoria dentro `Build()`, e leggerla prima
vedrebbe ancora la chiave d'esempio del file.

## L'API

Tutto sotto `/api/v1` (ApiRoutes.cs:6 «public const string Prefix = "/api/v1";»), l'hub su
`/hub/plant` (ApiRoutes.cs:7 «public const string Hub = "/hub/plant";»), JSON `camelCase`.

| Gruppo | Rotte |
|---|---|
| stato | `GET /status` — porta anche `historyFrom`, il primo mese di storico ancora in linea (`null` se lo storico non è aperto: motore non configurato) (EngineStatus.cs:7 «string Version, DateTime UtcNow, DateOnly? HistoryFrom = null);») |
| comandi | `POST /commands` (con `Date` facoltativa: la traccia di un'operazione già avvenuta porta il suo istante, e comando e riga di registro ne hanno uno solo; senza `Date` vale adesso), `GET /commands` (filtri, tutti facoltativi e in AND: `status`, `workstationId`, `userId`, `from`/`to`, `itemId`, `showStatus`, `minType`/`maxType`, `excludeUserId`, `take` — pensati per le due liste dei comandi del Client: `itemId`/`minType`/`maxType`/`excludeUserId` per separare i comandi utente da quelli che il motore firma `ORCHESTRA_ROUTE`, `showStatus` per i comandi ancora da mostrare), `GET /commands/{id}`, `PUT /commands/{id}`. **Chi le usa: l'Editor e il Client del WMS**; l'Editor per ogni comando al motore (avvio e arresto del navigatore, ricarica del diagramma, comandi sul singolo dispositivo) e per la rotta di prova, che ha il tipo dedicato `1010` (`TestRoute`, Command.cs:76 «TestRoute = 1010,»): il `3007` non serve perché rifiuta `LuId 0`. Quattro tipi sono tracce dell'operatore dal Client del WMS — `4002` `ModifyRushMoverStatus`, `4004` `ChangeCellLockStatus`, `4005` `AddOrModifyLuPosition`, `4006` `ModifyDatabaseCellContents` (Command.cs:111-117 «ModifyRushMoverStatus = 4002,»): arrivano già con `Status 2` (fatto), il motore non le esegue e il gateway non pretende che sia in `Run` per accettarle (CommandGateway.cs:21 «public static bool RequiresRunningEngine(int commandType) =>») |
| impianto | `GET`/`PUT /plant/diagram`; `GET /plant/items` (filtro facoltativo e ripetibile `deviceType`, in OR fra i valori: `?deviceType=2&deviceType=6` è «Buffer o LoadingAndBuffer»; nessun valore vuol dire tutti), `/items/status`, `/items/{id}`, `DELETE /plant/items/{id}`, `DELETE /plant/items` (svuota l'impianto intero — dispositivi, giunzioni, missioni attive e mappa porte — `409` in `Run`: è «Nuovo diagramma» dell'Editor), `POST /plant/items/keep`; `GET /plant/junctions`, `DELETE /plant/junctions/{id}`; `GET`/`PUT /plant/door-mappings`, `POST /plant/door-mappings/link`, `DELETE /plant/door-mappings/{itemId}`; `GET /plant/automatic-cells`. **Chi le usa: l'Editor**, per il diagramma e per la mappa porte, **e il Client del WMS**, in sola lettura, per gli item (`GET /plant/items?deviceType=`), e la mappa porte (`GET /plant/door-mappings`, che gli dà anche l'insieme delle celle servite dal WCS per il filtro «allocate in automatico» delle ricerche) |
| configurazione dell'impianto | `GET /plant/items/config`, `GET /plant/items/{id}/config`, `PUT /plant/items/{id}/config` (`400` se l'`ItemId` del corpo non coincide con l'`id` del percorso, `409` se il motore è in `Run`); `POST /plant/items/connection-type` (`409` in `Run`). **Chi le usa: l'Editor**: sono le rotte della griglia delle proprietà e dell'interruttore del modo di connessione |
| giunzioni | `POST /plant/junctions` (crea, o restituisce quella già esistente per la stessa coppia; `409` in `Run`), `PUT /plant/junctions/{id}` (`404` se non esiste, `409` in `Run`). **Chi le usa: l'Editor**, quando si traccia una giunzione sul diagramma; la priorità si cambia via API, non dall'Editor, dove `FrmJUNCTIONS_LIST` è in sola lettura |
| missioni | `GET /missions` (filtri facoltativi e in AND: `udc`, `itemId` — il dispositivo su cui sta l'UDC, confrontato con `PositionItemId` — `type`, la `MissionTypes` per numero), `/missions/exists?udc=` (con `status` ripetuto, `&status=0&status=1`, per contare solo gli stati dati: il blocco modifica UDC del WMS chiede «in Run o Stop», e una missione in `Error` ancora in memoria non deve bloccare), `/missions/steps`, `/missions/{id}`, `/missions/{id}/steps` |
| coda di estrazione | `GET /extraction-queue` (filtri facoltativi e in AND: `processed`, `udc`), `/contains?udc=` (con `excludeRow`, l'`ExtractionRowId` della riga corrente: «la UDC è in un'altra estrazione?»; in quel conto le righe senza riga di estrazione non entrano, come nel T-SQL che replica) o `?cell=`, `/cells`, `/load-units`, `/{id}`; `PATCH /{id}/priority`; `DELETE /{id}` |
| storico | `GET /handled-load-units`, `/handled-load-units/summary?groupBy=day|week|month|year`, `/exists`; `GET /log`, `/log/transit-times`; `POST /log` |
| PLC | `GET`/`PUT /plc/connections`, `GET /plc/connections/{id}/status` |
| WMS | `GET /wms/cells`, `GET /wms/configuration-check` (entrambe `503` quando il gestionale non risponde; **chi le usa: l'Editor**, per collegare una cella a un dispositivo e per il controllo di configurazione. Dal 2026-09-12 l'host non le serve più con SQL proprio: le inoltra al confine, e la forma della rotta è rimasta quella di prima); `GET /wms/cell-search-context` (**chi la usa: `WMS.Services`**, una volta per ricerca celle): in una lettura sola dello store le celle automatiche con il blocco al deposito — vero solo se tutte le porte della cella sono bloccate — e lo stato dal vivo del ricollocatore (`OffLine` se il motore non lo ha in memoria), le celle con una riga di coda non lavorata e le UDC in coda (CellSearchQueries.cs:32 «public static async Task<CellSearchContext> ContextAsync()») |

I codici di risposta: `400` DTO o intervallo non validi (o `ItemId` del corpo diverso da quello del
percorso su `PUT /plant/items/{id}/config`), `401` chiave assente o non riconosciuta, `403` chiave
di sola lettura riconosciuta ma usata per una scrittura, `404` risorsa
assente, `409` scrittura di configurazione (dispositivo, tipo di connessione, giunzione) rifiutata
perché il motore è in `Run` (`EngineBusyException`), `503` comando che richiede il motore in `Run`
quando non lo è, **oppure** database del motore non disponibile, **oppure** gestionale non
raggiungibile dal motore (`WarehouseUnavailableException`: è la risposta anche delle rotte `/wms/*`,
che dal 2026-09-12 passano dal confine invece di leggere SQL Server — `WmsUnavailableException` non
esiste più). Il corpo di ogni rifiuto è
`ProblemDetails` (`application/problem+json`), compresi `401` e `403`
(ApiKeyMiddleware.cs:33 «await Results.Problem(title: "Chiave assente o sbagliata", statusCode:»).

L'hub `/hub/plant` accetta la chiave completa o quella di sola lettura, ma la legge soltanto
dall'header `X-Api-Key`. Un `access_token` nella query viene ignorato e la negoziazione risponde
`401` se manca l'header: la chiave non deve finire in URL, cronologia o registri intermedi.

`DELETE /extraction-queue/{id}` non cancella una riga: accoda il comando `4001` del motore e
risponde `202` con il comando accodato. Togliere una UDC dalla coda è una decisione del motore.

Le letture dello storico (`GET /handled-load-units`, `/summary`, `/log`, `/log/transit-times`)
rispondono dai file dei mesi in linea (`Wcs:HistoryMonthsToKeep`): un intervallo può attraversare
più mesi e i risultati dei file coinvolti si concatenano dal mese più vecchio; su
`/handled-load-units` e `/log`, che hanno un `take`, l'ordine (dal più recente) e il taglio si
applicano all'intervallo intero, non a un singolo mese — `/summary` e `/log/transit-times` non
hanno `take` e si limitano a concatenare. Un intervallo che parte prima del primo mese presente
riceve `400` con il primo mese disponibile nel messaggio. `GET /handled-load-units/exists` non ha
intervallo: guarda i mesi in linea dal più recente.

`POST /log`: `Date` è opzionale (chi non la porta prende l'istante corrente) ma, se indicata, deve
cadere nel mese corrente — la scrittura resta sul file del mese corrente; fuori da quell'intervallo
risponde `400`.

## Provarlo con `curl`

Con l'host in esecuzione su `localhost:5580` e la chiave `chiave-di-prova`:

```bash
# Senza chiave: 401 con corpo ProblemDetails
curl -i http://localhost:5580/api/v1/status

# Con la chiave: lo stato del motore
curl -s -H "X-Api-Key: chiave-di-prova" http://localhost:5580/api/v1/status

# Il contratto OpenAPI, che passa senza chiave
curl -s http://localhost:5580/openapi/v1.json

# Un comando al motore (1001 = avvia il navigatore)
curl -s -X POST http://localhost:5580/api/v1/commands \
     -H "X-Api-Key: chiave-di-prova" -H "Content-Type: application/json" \
     -d '{"commandType":1001,"workstationId":"PC1","userId":"operatore"}'

# Il riepilogo raggruppato per mese, su un intervallo che attraversa due file di mese
curl -s -H "X-Api-Key: chiave-di-prova" \
     "http://localhost:5580/api/v1/handled-load-units/summary?from=2026-08-01&to=2026-10-01&groupBy=month"
# -> 4 righe: period 2026-08-01 tipo 1 (1) e tipo 2 (1), period 2026-09-01 tipo 1 (1) e tipo 2 (1)

# Un intervallo che parte prima del primo mese in linea: 400 con il primo mese disponibile
curl -s -H "X-Api-Key: chiave-di-prova" \
     "http://localhost:5580/api/v1/log?from=2026-07-01&to=2026-10-01"
# -> 400, detail: "lo storico in linea comincia da 2026-08: l'intervallo non puo' partire prima"

# La fotografia per la ricerca celle del WMS: celle automatiche (blocco al deposito, stato del
# ricollocatore), celle e UDC in coda di estrazione
curl -s -H "X-Api-Key: chiave-di-prova" http://localhost:5580/api/v1/wms/cell-search-context
# -> {"automaticCells":[{"cellId":…,"itemId":"…","relocatorItemId":"…","storingLocked":false,"relocatorStatus":0},…],"cellsInExtraction":[…],"loadUnitsInExtraction":[…]}
```

`/openapi` passa senza chiave di proposito
(ApiKeyMiddleware.cs:19 «if (context.Request.Path.StartsWithSegments("/openapi"))»): il contratto
descrive la forma delle richieste, non i dati dell'impianto, e serve a chi deve generare un
client prima di avere una chiave.

## Collaudarlo con `Wcs.Viewer.Test`

> `Wcs.Viewer.Test` non e' pubblicato in questo repository (usa componenti DevExpress di un feed privato).

`Wcs.Viewer.Test` è l'app WinForms dimostrativa del Viewer: si collega all'host, ricostruisce lo
stato dell'impianto e mostra missioni, dispositivi e step. È il modo più veloce per vedere se un
host installato risponde davvero.

L'indirizzo e la chiave sono **scritti nel sorgente**, non configurabili:
`http://127.0.0.1:5580` e `chiave-di-prova`
(FormTest.cs:32 «await Connector.ConnectAsync(new Uri("http://127.0.0.1:5580")» (non pubblicato)). Per provare contro un
altro host, quella riga va cambiata e il progetto ricompilato.

## Gli eventi verso i client

`PlantEventRelay` implementa `IEngineEventSink` e riceve le chiamate **sul thread del motore**:
mappa subito in DTO (l'oggetto vivo può cambiare un istante dopo) e accoda solo l'invio, così il
motore non aspetta mai la rete. Mappa e accodamento stanno in un `try`
(PlantEventRelay.cs:119 «private void Relay(string what, Action mapAndPost)»): un guasto qui finisce
nel registro invece di rompere la scrittura del motore da cui l'evento è nato.

La coda tiene diecimila voci e a coda piena scarta la più vecchia. Quando scarta, il relay lo
scrive nel registro e, appena la coda si svuota, manda un `NavigatorStatusChanged`
(PlantEventRelay.cs:198 «private async Task ResyncIfSomethingWasDroppedAsync()»): per i client quel
messaggio vale anche come «ricarica lo stato completo», perché la sequenza delle differenze ha un
buco. Un secondo compito batte un `Watchdog` ogni secondo, così un client capisce di essere ancora
collegato anche quando l'impianto è fermo.

Il dettaglio del meccanismo, dal lato del motore, sta in
[architettura.md](../Wcs.Engine/architettura.md), sezione «Eventi verso l'host».

## Due decisioni registrate

**Entrambe le chiavi si confrontano a tempo costante.** `ApiKeySecurity.Authenticate` prova prima
la chiave completa e poi, se configurata, quella di sola lettura; `Matches` codifica entrambe in
UTF-8 e usa `CryptographicOperations.FixedTimeEquals`
(ApiKeySecurity.cs:36 «CryptographicOperations.FixedTimeEquals»).
La protezione dal timing non sostituisce la cifratura del trasporto.

**Un endpoint remoto deve usare HTTPS.** Il predefinito HTTP resta confinato a
`http://127.0.0.1:5580`. Per servire altre macchine si configura direttamente un endpoint HTTPS
di Kestrel, oppure si mette un reverse proxy TLS davanti all'host lasciando Kestrel sul loopback.
La sola eccezione è una LAN d'impianto segregata: l'installatore deve impostare esplicitamente
`Wcs:AllowInsecureRemoteHttp` (`Wcs__AllowInsecureRemoteHttp=true`), sapendo che l'opzione non
cifra nulla e che l'host registra un evento `Critical`. Senza tale consenso un endpoint HTTP non
loopback impedisce l'avvio.
