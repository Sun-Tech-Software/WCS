> **Historical — not current.** Internal design notes of Sun Tech, written in Italian on 2026-09-12. They describe the system as it was then and are kept for reference only; the current documentation is in [docs/](../../../README.md).

# WCS.Connection.Test — architettura

> **Nota per il repository pubblico.** Questo documento e' stato scritto per il repository interno. Non sono pubblicati: l'Editor WinForms (`Wcs.Editor`), `Wcs.Viewer.Test`, il driver PLC di Sun Tech con il suo protocollo e il suo simulatore (proprietari) e i documenti che lo descrivono (fra questi la mappa dell'area `WCS/doc/README.md` e il registro del debito tecnico), l'importatore che migra gli impianti dal database del gestionale di Sun Tech, gli script della demo, le specifiche, i piani, la coda dei lavori e i rapporti delle revisioni, e il monorepo del gestionale (i riferimenti `Kazam:`). I collegamenti a quelle parti sono resi come testo e segnati «(non pubblicato)». `Common` qui e' una cartella normale, non un submodule.

## Scopo

`WCS.Connection.Test` è la controfigura del driver PLC di Sun Tech (non pubblicato):
un'unica classe,
Connection.cs:9 «public class Connection(int id) : IConnection», implementa `IConnection`
(architettura.md (non pubblicato)) senza parlare
con nessun PLC — nessun socket, nessun protocollo, nessuna scrittura su database. Il componente
è deliberatamente piccolo: un solo file sorgente reale, 142 righe. Questo documento esiste per dire, membro per membro, che cosa la simulazione fa e dove tace.

## Dipendenze

**In entrata.** Un solo progetto dichiara `ProjectReference` verso questo `.csproj`: `Wcs.Engine`,
che lo usa davvero, per i dispositivi in modalità test (si veda sotto).

**In uscita.** Il `.csproj`
(Wcs.Connection.Test.csproj «<Project Sdk="Microsoft.NET.Sdk">») ha un solo
`ProjectReference`, verso `WCS.Connection.Interface`. Nessun riferimento al protocollo, a
`Log.Interface`, a `MsSqlHelper` o a pacchetti di accesso dati: la differenza di dipendenze fra i
due `.csproj` è già, da sola, la misura di quanto la simulazione sia più leggera della realtà che
sostituisce.

## Che cosa simula e che cosa no

### Gli eventi: 2 sollevati su 6

Il simulatore dichiara tutti e sei gli eventi di
`IConnection` (per soddisfare l'interfaccia,
Connection.cs:46-56 «public event Handlers.AsiEventEventHandler? AsiEvent;») ma ne solleva solo due:
`AsiMovementIsCompleted` (dai due overload di `MoveLuFromAsiToAsi`,
Connection.cs:75 «AsiMovementIsCompleted?.Invoke(this,» e
:100 «AsiMovementIsCompleted?.Invoke(this,») e `AsiSendLuData` (da
`RequestLuDataFromAsi`, Connection.cs:159 «AsiSendLuData?.Invoke(this,»).
Verificato leggendo il file per intero: nessun `?.Invoke` su `AsiEvent`,
`AsiRequestsLuInfo`, `LuUnloadedFromAsi`, `NewLuOnAsi` in nessun punto del file. Per chi collauda un
impianto con questa connessione, la conseguenza pratica è:

- **mai un evento generico ASI** (`AsiEvent`) — qualunque logica del motore che dipenda da
  `Navigator.AsiEvent`/`item.ConnectionAsiEvent` non viene mai esercitata in modalità test;
- **mai una richiesta di dati LU dall'ASI verso il motore** (`AsiRequestsLuInfo`) — il percorso
  «l'ASI chiede i dati di una LU che ha in carico»
  (`Navigator.AsiRequestsLuData`/`SendLuDataToAsi`) non è mai innescato dal simulatore: può essere
  eseguito solo chiamando `RequestLuDataFromAsi` dal lato motore (il verso opposto), non ricevendo
  la richiesta dall'ASI;
- **mai uno scarico di LU segnalato dal campo** (`LuUnloadedFromAsi`) — un test basato su questa
  connessione non può verificare cosa succede quando un ASI comunica di aver scaricato una LU;
- **mai una nuova LU rilevata su un ASI** (`NewLuOnAsi`) — allo stesso modo, il percorso di
  rilevamento fisico di una LU comparsa su un ASI non è simulabile con questa connessione.

In pratica, il simulatore copre solo il ciclo «il motore chiede un movimento, riceve conferma» e
«il motore chiede dati di una LU, li riceve»: qualunque test che debba osservare il motore
*reagire* a un evento iniziato dal campo (i quattro eventi sopra) non può usare
`WCS.Connection.Test` come sostituto della realtà.

### I due metodi che sollevano eventi, e la temporizzazione che dichiarano

**`MoveLuFromAsiToAsi`, overload singola LU**
(Connection.cs:60-82 «public void MoveLuFromAsiToAsi(RunAsi sourceItem, DataAsi»): costruisce un
`AsiMovementIsCompletedData` con la posizione finale dichiarata (`toAsi`), poi
`Task.Run(async () => { await Task.Delay(sourceItem.DelayToStepDone); AsiMovementIsCompleted?.Invoke(...); })`
(Connection.cs:72-81 «Task.Run(async () =>») — il `Task.Delay` **è
correttamente atteso** (`await`): l'evento parte davvero dopo `sourceItem.DelayToStepDone`
millisecondi, il tempo che il chiamante ha impostato su `RunAsi` per simulare la durata del
movimento. L'esito (`Result`/`ErrorCode`/`ErrorDescription`) non è calcolato dal simulatore: è letto
tale e quale da `sourceItem.ResultType`/`sourceItem.ErrorResultCode`
(Connection.cs:96-100 «new AsiMovementIsCompletedEventArgs(sourceItem.ItemGuid, stepGuid») — è il chiamante (chi prepara
il `RunAsi`, lato motore/test) a decidere se il movimento simulato deve avere successo o fallire, e
con quale codice; il simulatore si limita a restituirlo dopo l'attesa.

**`MoveLuFromAsiToAsi`, overload LU multiple raggruppate**
(Connection.cs:84-107 «public void MoveLuFromAsiToAsi(RunAsi sourceItem, DataAsi»): stessa logica di esito
(`sourceItem.ResultType`/`ErrorResultCode`) e stessa temporizzazione del primo overload:
`Task.Run(async () => { await Task.Delay(sourceItem.DelayToStepDone); AsiMovementIsCompleted?.Invoke(...); })`
(Connection.cs:97-106 «Task.Run(async () =>») — anche qui il `Task.Delay` è
correttamente atteso: l'evento parte dopo `DelayToStepDone` millisecondi, come nel primo overload.

**`RequestLuDataFromAsi`** (Connection.cs:145-160 «public void RequestLuDataFromAsi(RunAsi item)»):
risponde sempre con dati fissi (`DataConfirm = 1`, `DataErrorCode = 0`, `LuCode = "0001"`, tutti gli
altri campi di `AsiSendLuData` a default, incluse tutte le dimensioni `Lu*`) e
`ResultCodeTypes.Success` — mai un errore, indipendentemente da cosa chiede il motore. La
temporizzazione è corretta come nei due overload sopra:
`Task.Run(async () => { await Task.Delay(200); AsiSendLuData?.Invoke(...); })`
(Connection.cs:122-127 «Task.Run(async () =>») — l'evento parte
davvero dopo 200 ms.

### Gli altri quattro metodi: implementazioni vuote

`CloseConnection()`, `Dispose()`, `SendCommandToAsi()`, `SendLuDataToAsi()`, `ShowMessageToAsi()`,
`OpenConnection()` (Connection.cs:70-158 «public void CloseConnection()», corpi
vuoti a `{ }`): la simulazione non fa nulla per aprire/chiudere la connessione né per i tre metodi
di invio verso l'ASI che non producono un evento di risposta. Non c'è modo, con questa connessione,
di osservare l'effetto di `ShowMessageToAsi` o `SendCommandToAsi`: il chiamante lato motore non
riceve né conferma né errore, la chiamata è un no-op puro. `SendLuDataToAsi` è coerente col fatto
che il simulatore non solleva mai `AsiRequestsLuInfo` (nessuno chiede mai dati di LU al motore in
modalità test, quindi non c'è mai una risposta da inviare con questo metodo).

## Come viene scelta a runtime

`DeviceConnections.OpenConnection()` (in `Wcs.Engine`, non in questo componente) sceglie il tipo
di connessione da assegnare a ciascun dispositivo con uno `switch` su `item.ConnectionType`
(DeviceConnections.cs:268 «catch (Exception ex)»): nel `case
Enums.ConnectionTypes.Test:` (DeviceConnections.cs:270 «WriteLog($"Error On PLCs->Remove Description:»)
assegna `GetTestConnection()`, un metodo privato
(DeviceConnections.cs:355 «LogEntryType.Error);») che
costruisce **una sola volta** una `WCS.Connection.Test.Connection(99)` — il campo statico
`_tstConnection`
(DeviceConnections.cs:29 «private static Wcs.Connection.Test.Connection? _tstConnection;») — e la
riusa per ogni chiamata successiva. **Tutti i dispositivi in modalità test condividono la stessa
istanza**: stesso `Id` (`99`), stessi sei sottoscrittori d'evento (registrati una sola volta, alla
prima costruzione, DeviceConnections.cs:441-446 «_tstConnection.AsiMovementIsCompleted += AsiMovementIsCompleted;»).
Poiché `AsiMovementIsCompletedEventArgs`/`AsiSendLuDataEventArgs` portano l'`ItemGuid` del
dispositivo chiamante (letto da `sourceItem.ItemGuid`/`item.ItemGuid`, cioè dal `RunAsi` che il
motore passa, non da un campo della connessione), la condivisione non mescola le risposte fra
dispositivi diversi — la correlazione è per `ItemGuid`, non per connessione — ma resta il fatto che
un solo oggetto `Connection` di test serve l'intero impianto simulato, mentre in modalità reale ogni
PLC ha la propria istanza costruita da `DeviceConnections.Initialize()`.

Osservato leggendo per intero `DeviceConnections.OpenConnection()`
(DeviceConnections.cs:369-455 «// "Connected" servito dal simulatore fa credere al motore di»): il
`case Enums.ConnectionTypes.Connected:` (DeviceConnections.cs:303 «if (PathDevices.Instance.Count == 0) return;»)
per un dispositivo con `DeviceConnectionId > 0` cerca la connessione reale con quell'`ID_PCM_PLC`
nella variabile locale `communicatorToPlc`
(DeviceConnections.cs:363 «var communicatorToPlc = Instance[item.DeviceConnectionId];»):
se non la trova, ripiega su `GetTestConnection()` — la stessa connessione di test usata per i
dispositivi in modalità test — e lo scrive nel registro persistente, perché un dispositivo
`Connected` servito dal simulatore deve risultare all'operatore anche senza diagnostica accesa; se
la trova, l'assegna al dispositivo e la marca `Managed`
(DeviceConnections.cs:383-384 «item.DeviceConnection = communicatorToPlc;»). Per chi legge
questo documento chiedendosi «quali dispositivi usano davvero `WCS.Connection.Test`»: anche un
dispositivo dichiarato `Connected` può finire sul simulatore, se il suo `ID_PCM_PLC` non
corrisponde a nessuna connessione reale configurata.

## Confronto membro per membro con un driver verso PLC reali

| Membro | Driver verso PLC reali | Test |
|---|---|---|
| `Id` | l'identificativo della connessione configurata (`ID_PCM_PLC`) | sempre `99` (unica connessione costruita da `DeviceConnections`) |
| `Managed` | scritto da `DeviceConnections`, come per ogni `IConnection` | idem |
| `AsiMovementIsCompleted` | sollevato quando il PLC riporta la fine di un movimento, con il suo esito | sollevato con esito dichiarato da `RunAsi.ResultType`, dopo un'attesa vera in entrambi gli overload |
| `AsiSendLuData` | i dati della LU riportati dal PLC | dati fissi (`LuCode = "0001"`), sempre successo, dopo un'attesa vera di 200 ms |
| `AsiRequestsLuInfo`, `LuUnloadedFromAsi`, `NewLuOnAsi`, `AsiEvent` | sollevati quando il campo li segnala | **mai sollevati** |
| `OpenConnection`/`CloseConnection`/`Dispose` | aprono e chiudono il collegamento col PLC | no-op |
| `SendCommandToAsi`/`SendLuDataToAsi`/`ShowMessageToAsi` | inviano il comando al PLC | no-op, nessuna conferma né errore |

## Stato e proprietà

`WCS.Connection.Test.Connection` non ha stato mutabile proprio oltre `Id` (impostato una sola volta
nel costruttore primario, Connection.cs:9 «public class Connection(int id) : IConnection») e
`Managed` (proprietà ereditata dal contratto, scritta solo da `DeviceConnections`, si veda
architettura.md (non pubblicato)). Nessun campo statico proprio di questo componente: l'unico stato
condiviso fra dispositivi è quello discusso sopra — il fatto che sia una sola istanza a servire più
dispositivi — mantenuto in `DeviceConnections`, non qui.

## Punti di attrito

I difetti trovati leggendo questo componente sono nel registro d'area,
debito-tecnico.md (non pubblicato), dove ogni voce ha anche una stima del costo di
riparazione.

- **`G4`** — Quattro dei sei eventi di `IConnection` non sono mai simulati (non pubblicato)
