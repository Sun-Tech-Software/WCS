> **Historical — not current.** Internal design notes of Sun Tech, written in Italian on 2026-09-12. They describe the system as it was then and are kept for reference only; the current documentation is in [docs/](../../README.md).

# Il database del WCS

> **Nota per il repository pubblico.** Questo documento e' stato scritto per il repository interno. Non sono pubblicati: l'Editor WinForms (`Wcs.Editor`), `Wcs.Viewer.Test`, il driver PLC di Sun Tech con il suo protocollo e il suo simulatore (proprietari) e i documenti che lo descrivono (fra questi la mappa dell'area `WCS/doc/README.md` e il registro del debito tecnico), l'importatore che migra gli impianti dal database del gestionale di Sun Tech, gli script della demo, le specifiche, i piani, la coda dei lavori e i rapporti delle revisioni, e il monorepo del gestionale (i riferimenti `Kazam:`). I collegamenti a quelle parti sono resi come testo e segnati «(non pubblicato)». `Common` qui e' una cartella normale, non un submodule.

Il WCS conserva i propri dati per conto proprio, in file suoi che vivono accanto al servizio.
Nessuna delle sue tabelle sta nel database del gestionale: al gestionale il motore chiede quello
che il gestionale sa, a cominciare da quale cella usare, ma quello che sa lui se lo tiene.

Questo documento serve a chi tocca la persistenza. Per il quadro d'insieme si parte da
[per-cominciare.md](per-cominciare.md).

## Perché SQLite

Il volume non decide. Negli impianti più carichi il WCS produce circa **100.000 righe in dieci ore
di lavoro**, cioè tre righe al secondo di media con picchi stimabili in cinquanta: qualunque motore
serio le assorbe. Decidono tre cose.

| | SQLite (embedded) | PostgreSQL (server) | SQL Server Express |
|---|---|---|---|
| installazione sul PC d'impianto | nessuna: è un file | un servizio da installare e aggiornare | un servizio, solo Windows |
| scrittori concorrenti | uno per volta | molti | molti |
| accesso da remoto senza API | no | sì | sì |
| limite di dimensione | praticamente nessuno | nessuno | 10 GB per database |
| 25 milioni di righe l'anno | sì, con rotazione dei file | sì, con partizionamento | vicino al limite in due anni |

1. **Il WCS ha un solo scrittore per costruzione**, il motore; tutti gli altri entrano dall'API. Il
   limite di SQLite coincide con la regola che serve. Un server aggiungerebbe proprio la dipendenza
   che il WCS non vuole avere: chi lo adotta lo installa e parte.
2. **Quello che conta è separare i dati operativi da quelli che raccontano il passato**, non il
   motore. Con SQLite la separazione è fisica, un file per famiglia: la ritenzione è cancellare un
   file, il backup è copiarne uno.
3. **Le scritture ad alto volume vanno tolte dal thread del campo** con qualunque motore, e un solo
   thread scrittore lo rende strutturale invece che una buona intenzione.

SQL Server Express è scartato: dieci gigabyte si raggiungono in due anni con questi volumi, ed è
Windows soltanto. LiteDB e simili sono scartati per maturità.

## I tre file

**`wcs.db`, operativo.** Piccolo, caldo, letto e scritto di continuo. Contiene quello che descrive
*lo stato di adesso*.

| Entità | Che cos'è |
|---|---|
| `PlantDiagram`, `PlantItem`, `PlantJunction` | il disegno dell'impianto e la configurazione di ogni punto |
| `Mission` | le missioni attive |
| `CommandRecord` | i comandi in ingresso |
| `ExtractionQueueItem` | la coda di estrazione e ricollocamento |
| `DoorMapping` | la corrispondenza fra codice porta e dispositivo |
| `PlcConnection` | la configurazione delle connessioni al campo |
| `RecordDataSettings`, `CheckDataSettings` | a riga singola: quali dati del campo registrare e quali verificare |

**`wcs-history-AAAA-MM.db`, storico.** Un file al mese, in sola aggiunta per il mese corrente e
chiuso al cambio di mese. Contiene quello che descrive *quello che è successo*, e ci finiscono le
decine di migliaia di righe al giorno.

| Entità | Che cos'è |
|---|---|
| `HandledLoadUnit` | le unità di carico movimentate, con la loro missione |
| `NavigatorLogEntry` | il registro del navigatore |

A duecento byte per riga sono circa venti megabyte al giorno e cinque gigabyte l'anno. Lo storico
non entra mai nel percorso del motore: il motore ci scrive soltanto, e lo legge l'API per i
riepiloghi.

**`wcs-plc-AAAA-MM.db`, il registro dei messaggi dei PLC.** Stessa forma dello storico — un file al
mese, rotazione, WAL — e una tabella sola, `PlcMessage`: una riga per telegramma scambiato con ogni
PLC, con dentro l'XML del comando. Ha una coda di scrittura sua, una ritenzione sua (sette giorni,
piu' la rotazione che tiene due mesi di file) e nessuna interrogazione in comune con lo storico. Sta
a parte dalla voce `D183`: finche' i due registri erano un file solo, erano anche una coda sola, e a
coda piena cadevano le righe piu' vecchie senza guardare di quale registro fossero — cioe'
soprattutto quelle del navigatore, che sono molte meno.

**I passi delle missioni non sono persistiti.** Al riavvio la rotta si ricostruisce, e se la
posizione o la destinazione non esistono più la missione resta ferma. È un limite noto, ed è nel
registro del debito (non pubblicato).

## Come scrive il motore

**Modalità WAL**, con `synchronous = NORMAL`, impostata all'apertura di ogni file. I lettori non
bloccano lo scrittore e viceversa, e una caduta di corrente perde al più l'ultimo lotto, mai la
coerenza.

**Un solo thread scrittore per file, con una coda in memoria.** Chi produce una riga, il thread del
campo o un ciclo del motore, la mette in coda e prosegue. Lo scrittore svuota la coda a lotti, in
una sola transazione. Le tre code hanno impostazioni diverse, e la differenza è deliberata.

| | coda dello stato | coda dello storico | coda dei messaggi PLC |
|---|---|---|---|
| che cosa porta | missioni, comandi, impianto | registro e movimentazioni | i telegrammi dei PLC |
| capienza | 5.000 | 50.000 | 50.000 |
| lotto | 100 righe | 200 righe | 200 righe |
| intervallo | 50 ms | 250 ms | 250 ms |
| quando è piena | **fa aspettare chi scrive** | **scarta le righe più vecchie** | **scarta le righe più vecchie** |

La riga in fondo è la scelta che conta: perdere una voce di registro sotto carico è accettabile,
perdere il cambio di stato di una missione no. Le ultime due colonne si somigliano, e la ragione
per cui sono due code e non una è proprio lo scarto: i telegrammi sono molti di più delle righe del
navigatore, e in una coda sola a cadere sarebbero state soprattutto le altre (voce `D183`). Quanti
telegrammi al minuto arrivino da un impianto vero non è ancora stato misurato: la capienza dei
messaggi è quella di prima, e i contatori per misurarla esistono a parte, ma solo **dentro il
motore** (`PlantQueries.PlcLogWriterCounters`, accanto a `HistoryWriterCounters`): nessuna rotta li
pubblica, quindi per prendere quella misura da fuori del processo va aggiunta prima una rotta.

**Chi ha bisogno di rileggere subito quello che ha scritto attende.** Lo scrittore espone due modi
di accodare: uno che ritorna appena la riga è in coda, e uno che restituisce un compito completato
alla conferma della scrittura. Chi crea una missione o prende in carico un comando usa il secondo,
perché vuole indietro la chiave; tutto il resto usa il primo.

## Lo strato di accesso

**EF Core con il provider SQLite.** Le query non sono SQL scritto a mano, le migrazioni dello
schema vivono nel codice e si applicano all'apertura del file, e i test del motore girano su un
database vero, vuoto, in memoria.

- Un `DbContext` per file: uno per l'operativo, uno per lo storico, uno per il registro dei messaggi
  dei PLC, ciascuno con la propria cartella di migrazioni.
- Le entità stanno in `Persistence/Entities/` e hanno nomi inglesi, come vuole la convenzione
  dell'area.
- Sopra i contesti ci sono store sottili dove serve un'operazione composta, e le facciate che il
  resto del motore usa.
- Lo strumento `dotnet-ef` è fissato nel manifesto `WCS/dotnet-tools.json`, così la versione con cui
  si generano le migrazioni non dipende da che cosa è installato sulla macchina.

Una cautela che vale la pena ricordare: una migrazione generata mentre la compilazione è rotta nasce
dal modello vecchio. Si rigenera dopo una compilazione pulita.

## Ritenzione e backup

**La ritenzione** è un parametro di configurazione, `Wcs:HistoryMonthsToKeep`, con dodici mesi come
valore predefinito. All'avvio e al cambio di mese i file più vecchi vengono cancellati, e la
cancellazione viene segnalata. Chi importa uno storico più vecchio deve alzare quel numero **prima**
del primo avvio, altrimenti la ritenzione cancella i mesi appena importati. Il registro dei messaggi
dei PLC ha la propria, che non si configura: sette giorni di righe sul file del mese corrente e due
mesi di file, perché è il minimo che non contraddice quei sette giorni quando la finestra entra nel
mese prima.

**Il backup** si fa copiando i file con l'API di backup di SQLite, che è coerente anche a motore
acceso: `wcs.db` ogni giorno, lo storico alla chiusura del mese. Va scritto nella documentazione
d'installazione insieme alla posizione dei file, che è configurabile.

## Che cosa non c'è

**PostgreSQL non è implementato.** EF Core lo renderebbe una questione di provider e di stringa di
connessione, e la struttura non lo impedisce, ma nel codice non esiste: nessun pacchetto, nessuna
scelta di provider, il provider SQLite è cablato. Vale la pena tenerlo come possibilità dichiarata
e non come capacità, perché serve solo a chi voglia report su anni di storico direttamente dal
database, più motori che condividono lo stesso storico, o gli strumenti di un amministratore di
database.
