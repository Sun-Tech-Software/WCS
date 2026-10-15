> **Historical — not current.** Internal design notes of Sun Tech, written in Italian on 2026-10-15. They describe the system as it was then and are kept for reference only; the current documentation is in [docs/](../../README.md).

# Roadmap del WCS

> **Nota per il repository pubblico.** Questo documento e' stato scritto per il repository interno. Non sono pubblicati: l'Editor WinForms (`Wcs.Editor`), `Wcs.Viewer.Test`, il driver PLC di Sun Tech con il suo protocollo e il suo simulatore (proprietari) e i documenti che lo descrivono (fra questi la mappa dell'area `WCS/doc/README.md` e il registro del debito tecnico), l'importatore che migra gli impianti dal database del gestionale di Sun Tech, gli script della demo, le specifiche, i piani, la coda dei lavori e i rapporti delle revisioni, e il monorepo del gestionale (i riferimenti `Kazam:`). I collegamenti a quelle parti sono resi come testo e segnati «(non pubblicato)». `Common` qui e' una cartella normale, non un submodule.

Le tappe dichiarate dal proprietario del progetto, nell'ordine in cui si fanno. Ogni tappa ha una specifica e un piano a compiti (interni, non pubblicati) prima di cominciare; qui c'è solo il
verso.

> **Due numerazioni, non una.** Questa è la roadmap del prodotto. C'è anche una numerazione più
> vecchia — le tappe del piano che fra il 2 e l'8 settembre 2026 ha portato il WCS fuori dal
> database del gestionale: lì la «tappa 4» è **il taglio delle 31 tabelle**, fatto il 2026-09-08, e
> il suo nome sopravvive nello script `2026-09-08_TAPPA4_TAGLIO_TABELLE_WCS.sql` e nei documenti
> che lo citano. Quella tappa 4 non c'entra nulla con la tappa 4 di questa tabella. Nei documenti
> vivi il taglio si chiama **«il taglio delle tabelle»**; il nome del file resta com'è, perché porta
> la sua data ed è citato nella procedura di rilascio.

| # | Tappa | Stato | Dove |
|---|---|---|---|
| 1 | **Il confine con il gestionale**: il WCS dichiara `IWarehouse`, lo realizza via HTTP, nessun progetto del WCS referenzia i progetti del gestionale | **fatta** (2026-09-02 → 2026-09-10) | `Kazam: docs/superpowers/specs/2026-09-08-wcs-confine-gestionale-design.md` |
| 2 | **Il WCS esce da Kazam** nel suo repository, con la storia; Kazam lo consuma come submodule | **fatta** (2026-09-09) | `README.md` |
| 3 | **Il motore senza SQL Server**: le 21 istruzioni dirette, le 2 letture dell'host e i fallback PLC passano il confine; `Wms:ConnectionString` e `MsSqlHelper` escono dal WCS | **fatta** (2026-09-11 sera → 2026-09-12) | una specifica interna, non pubblicata |
| 4 | **L'Editor diventa web**: abbandono dell'Editor WinForms/Syncfusion Diagram per un'applicazione web che disegna il **grafo logico** dell'impianto e fa tutte le altre cose che l'Editor fa oggi, meno la parte grafica/HMI (che diventa la tappa 7); e un **Viewer web in Blazor** | **in corso**: **4a fatta** il 2026-09-12 (provata sul disegno vero: 51 dispositivi su 51, contatori a zero); **4b specificata** il 2026-09-13, da pianificare | una specifica interna, non pubblicata, `…-13-…-4b-design.md` |
| 5 | **Dispositivi definiti dall'integratore**: dal motore esce la firma del **dispositivo base**; i tipi di oggi diventano i **dispositivi evoluti** che la rispettano; un'entità logica di movimentazione nuova si scrive in C# contro la stessa firma, in un assembly che l'host carica, senza toccare il motore | **dichiarata** il 2026-09-10, da specificare | — |
| 6 | I quattro progetti condivisi (`Wcs.Wms.Contracts`, `Wcs.Api.Client`, `Wcs.Api.Contracts`, `Wcs.Common`) come **pacchetti NuGet** su un feed, al posto del submodule in Kazam | dichiarata il 2026-09-09, da specificare | `README.md` |
| 7 | **L'editor HMI**: un secondo disegnatore sullo stesso modello del grafo, dove a un elemento si associano qualità estetiche e si agganciano gli eventi del campo — il cambio di stato, l'arrivo di un'UDC — come in un pannello di supervisione. Nell'Editor di oggi il sinottico e il grafo sono la stessa cosa; qui si separano, e l'HMI si aggancia al grafo invece di coincidervi | **dichiarata** il 2026-09-12, da specificare | — |

## La tappa 3, com'è andata

Sedici compiti fra la sera dell'11 e il 12 settembre 2026, uno alla volta, ognuno con la sua
revisione. Il risultato in una riga: **né il motore né l'host né lo strato PLC aprono più SQL
Server del gestionale** (lo apre soltanto un importatore interno degli impianti esistenti, non pubblicato).
Sotto `WCS/Wcs.Engine/src` non c'è più un'istruzione T-SQL, `MsSqlHelper` e `Navigator.Database`
sono spariti, `Wms:ConnectionString` è uscita dalla configurazione dell'host, e
`EngineDependenciesTest` tiene chiusa la porta per tutti i `.csproj` sotto `WCS/`.

Sei decisioni sono state prese **in corso d'opera**, e non stavano nella specifica:

1. **Le cinque scritture non erano tutte assorbite dai fatti esistenti.** Per la fase che chiude
   l'estrazione sono serviti un valore in più (`ExtractionRowStatus.Extracted`) e un *upsert* nel
   gestionale, che rende idempotente anche il ritentativo del ciclo di servizio.
2. **La coda di estrazione ha una seconda colonna di lavorazione** (`NoteProcessingId`): una riga
   può servire una lavorazione di estrazione e una di nota insieme, e le due tabelle del gestionale
   condividono lo spazio degli identificativi. Procedura di aggiornamento: **la coda di estrazione
   va svuotata prima di applicare le migrazioni** (voce `D178` del registro).
3. **Il motore non scrive più «prelevato»** sull'assegnazione di nota, e la scrittura non è stata
   sostituita: su quel percorso l'assegnazione viene cancellata dalla conseguenza del fatto
   segnalato nello stesso giro. Resta scoperta una finestra, dichiarata in `D174`.
4. **Il registro dei messaggi del PLC vive in un database del WCS**, non più in una tabella del
   gestionale: tabella `PlcMessages`, stessa ritenzione di prima, e una rotta di lettura
   (`GET /api/v1/history/plc-messages`) perché chi li guardava possa continuare a guardarli. Dalla
   voce `D183` il file è suo (`wcs-plc-AAAA-MM.db`) e non più quello dello storico: la coda di
   scrittura è separata, così un'ondata di telegrammi non fa cadere le righe del navigatore.
5. **`DBInfo*.cs` non sono stati cancellati.** La premessa dell'ultimo compito — «a quel punto
   contengono solo query locali» — era falsa: sono 1512 righe (misura del 12/9) di orchestrazione e di chiamate al
   confine. Il nome resta, il contenuto non apre più SQL Server.
6. **`GetDatiPostazione` è stata tolta senza sostituto**, come la specifica prevedeva.

Resta aperta **una decisione del proprietario**: quando un'assegnazione va in pausa, dalla coda del
WCS esce la sola riga di quell'assegnazione o tutte le righe di quell'unità di carico, comprese
quelle dei ricollocamenti, che un'assegnazione non ce l'hanno?

## L'ordine fra la 4 e la 5, invertito il 2026-09-12

Fino all'11 settembre l'ordine era il contrario: prima i dispositivi dell'integratore, poi l'Editor
web, perché «l'Editor web nasce già con i tipi estendibili». **Il responsabile del prodotto ha invertito**: prima
l'Editor web.

Il motivo dell'ordine vecchio resta vero e non è stato smentito: `DeviceTypes` è un enumeratore
`byte` **chiuso** (`Wcs.Common/src/Enums.cs`) e **attraversa l'API** — sta dentro `PlantItemDto` e
`PlantItemConfigDto`. Un Editor web scritto prima nascerebbe contro quella lista chiusa, e quando i
tipi diventano un registro aperto la palette, le icone e i pannelli delle proprietà si rifanno. Non
si rifà il disegno del grafo, che parla di nodi, archi e coordinate.

Contro quel costo pesa il rischio dell'Editor di oggi: nessun test automatico, la licenza Syncfusion
legata alla versione, il `Diagram` che va in `NullReference` se il mouse lo tocca prima del modello
(vedi `D161` e le note del collaudo). Ogni mese in più è rischio in sala, e un Editor web è anche
l'unica delle due tappe che si vede.

**Il primo compito della tappa 4 toglie quasi tutto il costo dell'inversione:** aprire il registro
dei tipi **prima** — una rotta che elenca i tipi con il loro nome e le loro proprietà — e far
consumare quella all'Editor web, invece di compilarlo contro l'enumeratore. La tappa 5 poi riempie
il registro e l'Editor non se ne accorge. Va scritto nella specifica della 4, non lasciato alla
buona volontà di chi la implementa.

## La tappa 4, in due righe

L'host è già l'unico interlocutore di chi disegna: l'Editor legge e scrive l'impianto attraverso
`/api/v1/plant/*` e riceve gli stati dall'hub. Un Editor web parla con lo stesso host e le stesse
rotte; la tappa 3 lo ha preparato senza toccarlo — le due letture dell'Editor, `/wms/cells` e
`/wms/configuration-check`, hanno la stessa forma di prima, e dietro non c'è più SQL ma il confine.
Le tre domande che la specifica doveva sciogliere hanno una risposta, presa in dialogo il
2026-09-12 e argomentata in una specifica interna, non pubblicata:

1. **Che cosa disegna il grafo**: SVG scritto da noi, in Blazor, senza libreria di diagrammi. Il
   prodotto è un quadro astratto — nodi che cambiano stato, missioni che avanzano — e quelle due
   operazioni in SVG sono attributi, mentre con una libreria passano dal suo modello a ogni giro.
   In più nessuna licenza a pagamento nel prodotto nuovo, ed è ciò che rende l'HMI della tappa 7 un
   fratello di questo disegnatore invece che un erede della dipendenza.
2. **Che cosa si migra**: le sole **posizioni**. Misurato sul disegno vero: 51 elementi con GUID
   (i dispositivi), 59 connettori (le giunzioni, già in `PlantJunctions`), zero spezzate tracciate a
   mano. La geometria entra nel modello — cinque numeri annullabili per dispositivo — e il blob
   smette di essere una verità.
3. **Come arrivano i tipi**: da `GET /plant/device-types`, dove ogni tipo dichiara anche la propria
   forma schematica, le porte e i due punti dove si mostrano l'UDC e lo stato. Nella 4a l'insieme lo
   popola ancora il motore; la tappa 5 lo apre.

La tappa si fa in quattro sottoprogetti, ognuno con la sua specifica e una consegna che funziona da
sola: **4a** il modello del grafo (nessuna interfaccia; dopo di lei la forma dell'impianto è
congelata finché non c'è la 4b), **4b** l'editor del grafo nel browser, **4c** il quadro dal vivo e
i comandi, **4d** la parità voce per voce e lo spegnimento dell'Editor WinForms.

## La tappa 5, in due righe

Oggi il motore ha una sola classe di dispositivo, `AutomationDevice`, e la differenza fra un nastro,
uno smistatore, un buffer e un magazzino sta in un enumeratore (`DeviceTypes`) e in quattro
strategie di esecuzione scelte con uno `switch`. La tappa 5 rovescia il rapporto: **il motore
dichiara la firma del dispositivo base** — lo stato, l'unità di carico che ha in mano, i punti in
cui il motore lo chiama (posso ricevere? posso cedere? che cosa faccio quando arriva un oggetto,
quando il campo risponde, quando la rotta chiede di passare da me) — e **ogni tipo è un
dispositivo evoluto** che quella firma la rispetta. I sette tipi di oggi diventano i primi evoluti,
riscritti contro la firma; un'entità nuova (una navetta con regole proprie, una baia con
precedenze, un robot di picking) è una classe in un assembly che l'host carica all'avvio e che
l'Editor vede come un tipo in più nella palette.

È il gemello, per la logica, di quello che `IConnection` è già per il protocollo: il driver dice
*come si parla* con una macchina, il dispositivo evoluto dice *come ragiona*. Le due cose restano
separate — un'entità nuova può usare un driver esistente, un driver nuovo può servire un tipo di
oggi.

Le domande della sua specifica: quale sia la firma minima (che cosa il motore chiama davvero,
ricavato dalle quattro strategie e da `AutomationDevice`), come si registra un tipo per nome
(`DeviceTypes` smette di essere un enumeratore chiuso), come l'Editor e il Viewer mostrano un tipo
che non conoscono (icona, proprietà), e come si prova un dispositivo evoluto senza motore, con lo
stesso modello di `FakeWarehouse` e della controfigura di `IConnection`. Va fatta **dopo la 3** (la
firma base non deve nascere con SQL dentro) e, dall'inversione del 2026-09-12, **dopo la 4**: il
registro dei tipi lo apre l'Editor web, e questa tappa lo riempie.

## Quello che non è una tappa ma pesa

Il registro del debito (`debito-tecnico.md` (non pubblicato)) tiene le voci aperte con il loro stato; le decisioni
pendenti al 2026-09-10 stanno nella sintesi della revisione finale: i GUID disgiunti fra
`ASSEGNAZIONE_PICKING` e `ASSEGNAZIONE_NOTE`, il destino di `Wcs.Viewer`,
le cinque scritture del motore (chiuse dalla tappa 3: voce `D142`), lo `switch` sul tipo di connessione in
`DeviceConnections.OpenConnection` (era l'unico punto del motore che nominava un driver; dal
2026-10-15 il motore non ne nomina nessuno: il driver di campo è una `IConnectionFactory` che l'host
carica dalla chiave `Wcs:ConnectionFactory`, e lo `switch` sceglie solo fra il driver di prova e
quello installato).

Il 2026-09-23 sono state chiuse anche `D123` e `D181`: le due ricerche di deposito raggruppate
usano un proprietario esplicito per i blocchi porta e ripristinano gruppo e singole posizioni in
`finally`, con prove di contesa, guasto e annullamento sul secondo elemento del gruppo.

Nella stessa data sono state chiuse `D143`, `D155` e `D169`, tre difetti del driver PLC di Sun Tech (non pubblicato).

Sempre il 2026-09-23 sono state chiuse `D118`, `D146`, `D156` e `D157`: il deposito terminale e
ogni uscita fisica ricevono un `OperationId` stabile, entrano prima in una outbox SQLite del WCS e
vengono ritentati anche in Stop; Kazam applica l'effetto e la ricevuta nella stessa transazione SQL
Server e rifiuta un replay con contenuto diverso. L'Allocate sulla cella operatore resta sincrono,
ma e' comunque atomico lato Kazam. Lo scadenzario delle prenotazioni consulta il WCS prima di
cancellare e, se il confine fallisce, non cancella niente. L'ordine di rilascio e' vincolante:
prima lo script delle ricevute, poi il servizio Kazam/WMS, infine il WCS; anticipare il WCS non e'
supportato. Nello stesso giorno e' stata chiusa anche `D204`: esiti di estrazione, conferme delle
note e cancellazioni delle assegnazioni per missione condividono ora una transazione SQL Server
locale e serializzabile per l'intera sequenza, con prove reali di rollback e concorrenza.
