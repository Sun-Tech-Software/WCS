> **Historical — not current.** Internal design notes of Sun Tech, written in Italian on 2026-09-12. They describe the system as it was then and are kept for reference only; the current documentation is in [docs/](../../../README.md).

# Wcs.Viewer — architettura

> **Nota per il repository pubblico.** Questo documento e' stato scritto per il repository interno. Non sono pubblicati: l'Editor WinForms (`Wcs.Editor`), `Wcs.Viewer.Test`, il driver PLC di Sun Tech con il suo protocollo e il suo simulatore (proprietari) e i documenti che lo descrivono (fra questi la mappa dell'area `WCS/doc/README.md` e il registro del debito tecnico), l'importatore che migra gli impianti dal database del gestionale di Sun Tech, gli script della demo, le specifiche, i piani, la coda dei lavori e i rapporti delle revisioni, e il monorepo del gestionale (i riferimenti `Kazam:`). I collegamenti a quelle parti sono resi come testo e segnati «(non pubblicato)». `Common` qui e' una cartella normale, non un submodule.

## Scopo

`Wcs.Viewer` è una libreria client (non un eseguibile: il suo `.csproj` non ha
`OutputType`/`UseWindowsForms` e la cartella non contiene un `Program.cs`,
Wcs.Viewer.csproj:1 «<Project Sdk="Microsoft.NET.Sdk">») che
ricostruisce e mantiene, in un processo separato dal motore, una copia locale e in sola
lettura del suo stato — dispositivi, missioni, step, giunzioni — ricevuta dall'host di
produzione, `Wcs.Host`. Nel repository ha un solo consumatore:
`WCS/Wcs.Viewer/tests/Wcs.Viewer.Test` (non pubblicato), un'applicazione WinForms
dimostrativa (grep ricorsivo su tutti i `.csproj` del repository per
`Wcs.Viewer.csproj` come `ProjectReference`: un solo risultato, il suo).

Il canale è HTTP/JSON più un hub SignalR: il Viewer parla con l'host che ospita il motore,
`Wcs.Host`, tramite il client tipizzato `Wcs.Api.Client`.

## Che cosa consuma del core

**Niente per riferimento diretto verso il motore.** `Wcs.Viewer.csproj` dichiara tre
`ProjectReference` — `Helper`, `Wcs.Common` e `Wcs.Api.Client`
(Wcs.Viewer.csproj:10-12 «<ProjectReference Include="..\..\..\Common\Helper\Helper.csproj" />») — nessuno verso
`Wcs.Engine.csproj`. Il legame con `Wcs.Common` è solo gli enum (`Enums.DeviceTypes` e
affini, usati dalle proprietà di `Items/*`); il contratto di rete è invece nei DTO di
`Wcs.Api.Contracts`, portati in scope transitivamente da `Wcs.Api.Client`
(si veda [architettura.md](../Wcs.Common/architettura.md)).

**Connessione.** `Connector.ConnectAsync(address, apiKey)`
(Connector.cs:18 «public static async Task ConnectAsync(Uri address, string apiKey)») costruisce un `WcsClient` (le
richieste HTTP puntuali: `GetItemsAsync`, `GetMissionsAsync`, …) e una
`PlantEventsConnection` (l'hub SignalR), entrambi da `Wcs.Api.Client`, con la stessa
`WcsClientOptions { BaseAddress, ApiKey }`. Due canali distinti:

- **Push dall'hub.** `PlantEventsConnection` si iscrive a `ItemStatusChanged` e
  `NavigatorStatusChanged` e li instrada su `Navigator.ApplyItemStatus`/`ApplyStatus`
  (Connector.cs:20-21 «Address = address;»); `Watchdog` porta
  `Navigator.IsConnected` a `true` (:22 «Client = new WcsClient(options);»),
  `Reconnected` rilancia un ricarico completo con `ReloadAllAsync`
  (:23 «Events = new PlantEventsConnection(options);») e `Closed` porta `IsConnected` a `false`
  (:24 «Events.ItemStatusChanged += Navigator.ApplyItemStatus;») — la riconnessione automatica e il suo
  contratto (prima GET, poi le differenze) sono descritti nel documento di
  `Wcs.Api.Client`.
- **Pull su richiesta.** Ad ogni chiamata (`GetActiveMissions`, `GetAutomationDevices`,
  `GetMissionSteps`, `GetPathJunctions`, `GetViewItemsStatus`, `GetViewItemStatus`)
  `Connector` chiama `WcsClient` via HTTP e sostituisce per intero la lista locale
  corrispondente col risultato — non più uno svuota-e-ripopola in place, ma
  un'assegnazione dell'intera lista, tipizzata a `List<T?>` per accogliere gli elementi
  finché `Wcs.Common` resta `Nullable=enable`
  (Connector.cs:132-177 «public static async Task<bool> GetActiveMissions()»).

**Formato sul filo.** JSON, non più binario: `WcsClient` usa
`HttpClient.GetFromJsonAsync`/`PostAsJsonAsync` sui DTO di `Wcs.Api.Contracts.Dtos`
(descritti in [architettura.md](../Wcs.Common/architettura.md)), e
`PlantEventsConnection` li riceve dall'hub SignalR con lo stesso schema.

**Che cosa fa dei messaggi ricevuti.** Ogni DTO viene copiato in un wrapper locale sotto
`Items/` (`AutomationDevice`, `AutomationDeviceBase`, `AutomationJunction`, `MissionStep`,
`PathMission`) che vive in una delle liste statiche di `Wcs.Viewer.Navigator`
(Navigator.cs:12-16 «public static List<PathMission?> ActiveMissions =»). Il ricopiamento passa
da un `static From(<Dto>)` per classe, che assegna le proprietà con lo
stesso nome una per una (es.
PathMission.cs:58 «public static PathMission From(MissionDto dto) => new()»,
AutomationJunction.cs:58 «public static AutomationJunction From(JunctionDto dto) => new()»,
MissionStep.cs:221 «public static MissionStep From(MissionStepDto dto) => new()»,
AutomationDevice.cs:91 «public static AutomationDevice From(PlantItemDto dto) => new()»); dove il DTO
porta campi nullable o di tipo diverso da quello della proprietà locale (`Guid?` contro
`Guid`, `decimal` contro `int`), `From` applica il default che la proprietà aveva già.
`AutomationDeviceBase` ha in più un `Apply(ItemStatus)`
(AutomationDeviceBase.cs:37 «public void Apply(ItemStatus dto)») che
aggiorna lo stato dal vivo su un'istanza già esistente, usato sia da `Connector.GetViewItemStatus`
sia da `Navigator.ApplyItemStatus` per l'evento in arrivo dall'hub.

`Wcs.Viewer.Items.AutomationDeviceBase`, `.AutomationJunction`, `.MissionStep` e
`.PathMission` sono modelli passivi: nessuna chiamata I/O, nessuna comunicazione con
l'host; le proprietà che "risolvono" un riferimento (es. `AutomationJunction.SourceItem`,
AutomationJunction.cs:15-23 «public Guid DestinationId { get; set; }»)
sono getter calcolati che cercano nelle liste statiche di `Navigator`, popolate solo dalle
risposte HTTP e dagli eventi dell'hub.

## Punti d'ingresso

- **Connector.cs «public sealed class Connector»** (94 righe) — la facciata
  statica che un'applicazione host usa per aprire/chiudere la connessione
  (`ConnectAsync`/`DisconnectAsync`, Connector.cs:18-39 «public static async Task ConnectAsync(Uri address, string apiKey)»)
  e per chiedere un aggiornamento di una delle cinque collezioni o di un singolo dispositivo.
  Non contiene logica di rete propria: ogni metodo delega a `WcsClient`.
- **Navigator.cs «public sealed class Navigator»** (121 righe) — non è un
  motore, è uno stato condiviso statico: cinque liste pubbliche
  (Navigator.cs:12-16 «public static List<PathMission?> ActiveMissions =») più quattro proprietà
  osservabili (`Defragmentation`, `IsConnected`, `IsRunning`, `NavStatus`) che sollevano
  un evento .NET quando cambiano, e due metodi che il `Connector` chiama per applicare
  gli eventi dell'hub: `ApplyItemStatus` e `ApplyStatus`
  (Navigator.cs:201-216 «internal static void ApplyItemStatus(ItemStatus status)»). Nessun membro fa I/O
  proprio: chi lo scrive è sempre `Connector`.
- **`Items/*.cs`** — i cinque modelli passivi più le tre partial di `AutomationDevice`
  (`.Properties.GroupOptions.cs`, `.LiveData.cs`, `.PathOptions.cs`).

## Punti di attrito

I difetti trovati leggendo questo componente sono nel registro d'area,
debito-tecnico.md (non pubblicato), dove ogni voce ha anche una stima del costo di
riparazione.

- **`A1`** — Un pin non documentato tiene lontane otto vulnerabilità di gravità alta, e sembra un riferimento ridondante (non pubblicato)
  (`System.Security.Cryptography.Xml`, che resta un `PackageReference` diretto)
