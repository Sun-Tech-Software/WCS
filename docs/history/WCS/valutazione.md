> **Historical — not current.** Internal design notes of Sun Tech, written in Italian on 2026-09-23. They describe the system as it was then and are kept for reference only; the current documentation is in [docs/](../../README.md).

# WCS — valutazione

> **Nota per il repository pubblico.** Questo documento e' stato scritto per il repository interno. Non sono pubblicati: l'Editor WinForms (`Wcs.Editor`), `Wcs.Viewer.Test`, il driver PLC di Sun Tech con il suo protocollo e il suo simulatore (proprietari) e i documenti che lo descrivono (fra questi la mappa dell'area `WCS/doc/README.md` e il registro del debito tecnico), l'importatore che migra gli impianti dal database del gestionale di Sun Tech, gli script della demo, le specifiche, i piani, la coda dei lavori e i rapporti delle revisioni, e il monorepo del gestionale (i riferimenti `Kazam:`). I collegamenti a quelle parti sono resi come testo e segnati «(non pubblicato)». `Common` qui e' una cartella normale, non un submodule.

Scritta dal punto di vista di uno sviluppatore di un'azienda di automazione che deve decidere se
portare questo WCS in un impianto proprio. I numeri sono presi dal repository con i comandi
indicati; le opinioni sono opinioni, e sono segnate come tali.

Chi arriva adesso sul progetto trovi prima [per-cominciare.md](per-cominciare.md), che spiega di
che pezzi è fatto e come si avvia. Questo documento presuppone quello.

## Che cosa vale

**L'impianto è un dato, non codice.** Il layout si disegna e il motore ci calcola sopra le strade,
riconsiderandole a ogni salto. Non è una promessa architetturale: **lo stesso motore ha governato
50 impianti, fino a 386 macchine in contemporanea**. Cinquanta topologie diverse servite dallo
stesso programma sono la prova che quella separazione fra disegno e logica regge davvero, e sono
l'argomento più forte che il progetto abbia.

**La logica di magazzino automatico.** Code di estrazione e di deposito, ostacoli davanti
all'unità di carico voluta, polmoni di picking, ricollocazioni, gruppi di dispositivi che
gestiscono la profondità da sé. È la parte che costa cara a riscrivere e che nessuna
riorganizzazione del codice regala: chi adotta il sistema prende soprattutto questa.

**Il confine con il gestionale è un contratto.** Il WCS possiede i propri dati e li tiene in file
suoi; il gestionale gli parla attraverso un'unica area di servizi con otto metodi, e non ne legge
le tabelle. Chi ha già un gestionale proprio ha quindi un punto solo su cui lavorare, ed è la
differenza fra integrare e riscrivere.

**Si collauda senza impianto.** Il motore gira su un database in memoria e l'host si monta dentro
un test. Nel software di reparto è raro, e cambia il costo di ogni modifica.

**La documentazione cita il codice riga per riga**, e uno strumento verifica che le righe citate
esistano ancora. Il registro del debito (non pubblicato) elenca i difetti noti con una stima
del costo. Per chi valuta è il primo posto da leggere, prima ancora di questo documento.

## I numeri

| Misura sul motore | Valore | Come |
|---|---|---|
| righe di `Wcs.Engine` | 31.902 in 115 file | `wc -l` sui `.cs` di `src`, esclusi i `Designer` |
| file oltre le 1.300 righe | 6 | `wc -l` ordinato |
| test automatici sull'area | 338 in cinque suite | `dotnet test` |
| query SQL scritte a mano nel motore | 75 | grep su `SELECT`, `UPDATE`, `INSERT INTO`, `DELETE FROM` |
| usi di singleton `.Instance` | 294 | grep |
| membri statici in `Navigator.cs` | 89 | grep |
| chiamate lanciate e non attese (`_ = `) | 39 | grep |
| `async void` | 7 | grep |
| `catch (Exception)` che degradano a registro | 90 | grep |

Le cinque suite sono `Wcs.Engine.Test`, `Wcs.Host.Test`, `Wcs.Api.Client.Test`, `Kazam.Wcs.Test` e
la parte di `WMS.Services.Test` che copre l'automazione. L'Editor non ha test automatici: si
collauda a video.

## I limiti, in ordine di gravità

**1. Non c'è un banco di prova senza macchine.** La controfigura di prova risponde ai comandi ma
non solleva tutti gli eventi che l'impianto vero manda. Ne consegue che le modifiche al dialogo
con il campo si verificano sull'impianto, e per un'azienda di automazione è il rischio operativo
più alto. È anche il limite che, risolto, rende tutti gli altri più economici da affrontare.

**2. Il driver della macchina non è compreso.** L'interfaccia verso le macchine è dichiarata e non
è legata a una marca: sei eventi in ingresso e nove metodi in uscita, in cui compaiono punti
dell'impianto, unità di carico ed esiti, non indirizzi di memoria. Ma va detto con precisione che
cosa i 50 impianti dimostrano: che il disegno regge 50 topologie, non che l'interfaccia sia già
stata provata contro macchine di costruttori diversi. Il primo driver nuovo che si scriverà dirà
anche se quell'interfaccia ha la forma giusta, e conviene mettere a bilancio il costo di
modificarla, non solo quello di scrivere il driver.

**3. Il modo in cui il sistema fallisce è peggiore del modo in cui funziona.** Il motore si avvia
da un effetto collaterale: è l'assegnazione della stringa di connessione a far partire il
caricamento, in un compito che nessuno attende, e gli errori di quel caricamento finiscono solo
nel registro. Attorno, 90 `catch (Exception)` degradano qualunque errore a una riga di log e 39
chiamate partono senza essere attese. Un impianto che si ferma «senza motivo» è la conseguenza
tipica, ed è il guasto peggiore in questo dominio: non fermarsi, ma fermarsi senza che nessuno
sappia perché.

**4. Correttezza per convenzione, non per costruzione.** Non esiste un thread per dispositivo: i
cicli girano sul pool e lo stato è mutabile e condiviso. Le regole che tengono coerente il sistema
— prenotazione della destinazione, rifiuto se occupato, passo in volo, rotta ricostruita solo fra
un passo e il successivo — sono invarianti difesi da lock e da test, descritti in
[architettura.md](Wcs.Engine/architettura.md) del motore. Reggono, e 50 impianti lo
confermano; ma chi tocca il codice deve conoscerli, perché il compilatore non li difende.
Dal 2026-09-23 anche la ricerca di deposito raggruppata possiede atomicamente i propri blocchi e
ripristina in `finally` gruppo e posizioni dopo guasto o annullamento (`D123`, `D181`): resta un
modello a stato condiviso, ma quella specifica finestra non e' piu' affidata alla convenzione.

**5. Tutto è statico e globale.** `Navigator` è insieme configurazione, stato, orchestrazione e
gestore degli eventi di campo, e ogni oggetto di dominio risolve le proprie relazioni cercando in
un singleton. Conseguenze: un impianto per processo, nessuna iniezione di dipendenze, test
possibili solo con espedienti. Vale la pena essere onesti sulla gravità: se il modello di
installazione è un servizio per impianto, «un impianto per processo» non è un limite di esercizio.
Il costo è sui test e sulla leggibilità, e cresce con il tempo.

**6. I passi delle missioni non sono persistiti.** Il database del WCS conserva missioni, impianto,
code e comandi, ma non i singoli passi. Al riavvio la rotta si ricostruisce, e se la posizione o
la destinazione non esistono più la missione resta ferma. Manca anche un registro degli eventi di
una missione da cui ricostruire che cosa le è successo, che è esattamente la diagnosi che servirebbe
al limite 3.

**7. Nessuna ridondanza.** Il servizio che ospita il motore è uno solo: se cade, l'impianto si
ferma finché non riparte. Se sia un problema dipende dall'impianto, e conviene deciderlo con il
cliente invece che a tavolino.

**8. Il confine è chiuso dalle due parti (dal 2026-09-12).** Era il limite più grosso di questo
elenco: il gestionale passava da un contratto, il motore no — interrogava direttamente le tabelle di
merce, celle e lavorazioni. Adesso anche quella metà passa da un contratto dichiarato dal WCS
(`IWarehouse`) e realizzato via HTTP: sotto `WCS/Wcs.Engine/src` non c'è più un'istruzione T-SQL, e
un test lo tiene chiuso. Dal 23/9 il deposito terminale e le uscite fisiche sono durevoli: il WCS
conserva una outbox SQLite e ritenta, mentre il gestionale deduplica e applica ogni operazione in
una transazione SQL Server. L'Allocate sulla cella operatore resta sincrono ma partecipa comunque a
una transazione SQL Server lato Kazam. Dal 23/9 anche le tre sequenze di chiusura gestionali della
voce `D204` sono atomiche su una transazione SQL Server locale e serializzabile.

**9. Windows per gli strumenti, e una libreria commerciale.** L'editor del layout e il
visualizzatore sono applicazioni WinForms e usano una libreria di diagrammi commerciale, con
licenza legata alla versione. Il servizio, che è la parte che deve restare accesa, non ha vincoli
di piattaforma nella sua costruzione, ma su sistemi diversi da Windows non è stato provato.

## Che cosa fare, in ordine

Il criterio di ordinamento è: prima quello che rende verificabile il resto, poi quello che rende
visibili gli errori, e solo dopo le ristrutturazioni. Nell'ordine inverso si ristruttura al buio.

1. **Il banco di emulazione.** Una controfigura che sollevi tutti gli eventi, pilotabile da uno
   scenario scritto, capace di iniettare guasti: dispositivo in errore, comando che scade, unità
   di carico che compare dove non dovrebbe. Sopra, prove che facciano girare un impianto intero
   senza macchine. Il ritorno è doppio, e vale la pena vederlo: un'emulazione completa è anche la
   **seconda implementazione vera dell'interfaccia** verso le macchine, quindi è la prova che
   quell'astrazione abbia la forma giusta.
2. **Rendere visibili i guasti.** Classificare i `catch` che degradano a registro, perché la
   maggior parte deve rilanciare o riportare su un canale che qualcuno guarda. Dare al motore un
   avvio che restituisca un compito e uno stato interrogabile. Esporre uno stato di salute che
   dica «caricato, in marcia, degradato, e perché».
3. **Il secondo driver.** Ha senso dopo il banco, e apre o chiude un mercato.
4. ~~**Chiudere il confine anche dall'altro lato**, cioè le letture e le scritture del motore sul
   database del gestionale.~~ **Fatto il 2026-09-12**, con lo stesso schema già applicato dalla
   parte opposta, come qui si diceva. Dal 2026-09-23 il deposito terminale e le uscite fisiche
   attraversano il confine con outbox, ritentativo e ricezione transazionale idempotente; l'Allocate
   sulla cella operatore e' atomico ma resta sincrono. Dal 2026-09-23 anche le tre sequenze di
   chiusura gestionali di `D204` sono atomiche su una transazione SQL Server locale e serializzabile.
5. **Missioni e passi come registro di eventi**, con la riga di stato derivata. Risolve il riavvio
   e dà la diagnosi che manca al limite 3.
6. **Un proprietario per dispositivo**, e **istanze al posto dei globali**. Sono le due
   ristrutturazioni grosse. Opinione: farle per principio è una spesa che compra soprattutto
   eleganza, visto che il modello di concorrenza ha retto a 386 macchine. Le deciderei sui dati
   degli incidenti, non sull'estetica del codice.

Sul criterio dei percorsi non c'è una raccomandazione ma una misura da fare prima: contare quante
volte una rotta viene ricostruita e quante volte due missioni si aspettano a vicenda. Se quei
numeri sono piccoli, il criterio a salti va bene e modellare la congestione sarebbe lavoro
sprecato; se sono grandi, si hanno anche i dati per progettare la cosa giusta invece di
indovinarla.

## Verdetto

Opinione, dichiarata come tale. È un WCS onesto e ben ragionato, la cui idea centrale è dimostrata
sul campo su una scala che pochi progetti di questo tipo possono esibire. Quello che ha davanti è
il lavoro noioso che separa un buon progetto da uno che si vende a chiunque: il banco di
emulazione, il secondo driver, e la diagnosi dei guasti. Nessuna di queste tre cose richiede di
rimettere in discussione l'architettura, ed è questa la ragione principale per considerarlo.

Il rischio che pesa più di tutti non è un difetto singolo. È la combinazione fra correttezza
affidata a convenzioni e assenza di un banco di prova: finché le due cose convivono, ogni modifica
al dialogo con il campo si collauda in produzione.
