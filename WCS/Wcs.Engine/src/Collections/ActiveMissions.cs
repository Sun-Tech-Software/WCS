using System.Collections.Concurrent;
using Wcs.Engine.Api;
using Wcs.Engine.ORM;
using Log.Interface;
using Wcs.Wms.Contracts;
using static Wcs.Common.Enums;
using PathMission = Wcs.Engine.Items.PathMission;

namespace Wcs.Engine.Collections;

/// <summary>
/// Gestisce una collezione thread-safe di missioni attive utilizzando il pattern Singleton.
/// Fornisce operazioni per aggiungere, rimuovere e interrogare missioni di percorso attive nel sistema di automazione.
/// </summary>
public sealed class ActiveMissions
{
    #region Private Constructors

    /// <summary>
    /// Inizializza una nuova istanza della classe <see cref="ActiveMissions"/>.
    /// Costruttore privato per implementare il pattern Singleton.
    /// </summary>
    private ActiveMissions()
    {
    }

    #endregion Private Constructors

    #region Public Indexers

    /// <summary>
    /// Ottiene o imposta una missione di percorso utilizzando la sua chiave identificativa.
    /// </summary>
    /// <param name="key">L'identificatore univoco della missione.</param>
    /// <returns>La missione di percorso associata alla chiave specificata, o null se non trovata.</returns>
    public PathMission? this[Guid key]
    {
        get => _items.GetValueOrDefault(key);
        set
        {
            if (value == null) return;

            value.MarkBackInTheEngine();
            _items[key] = value;
        }
    }


    /// <summary>
    /// Ottiene o imposta una missione di percorso utilizzando il suo indice posizionale nella collezione.
    /// L'indice si basa sulla sequenza delle missioni valide (non null) presenti nella collezione.
    /// </summary>
    /// <param name="id">L'indice posizionale della missione nella collezione (base 0).</param>
    /// <returns>
    /// La missione di percorso all'indice specificato, o null se l'indice è negativo o fuori range.
    /// </returns>
    /// <remarks>
    /// <para>Nel setter: se l'indice è valido e corrisponde a una missione esistente, sostituisce quella missione.</para>
    /// <para>Se l'indice è fuori range ma il valore non è null, aggiunge la missione come nuovo elemento utilizzando il suo MissionId.</para>
    /// <para>Valori null o indici negativi vengono ignorati nel setter.</para>
    /// </remarks>
    public PathMission? this[int id]
    {
        get
        {
            if (id < 0) return null;

            var validItems = _items.Values.Where(x => x != null).ToArray();
            return id < validItems.Length ? validItems[id] : null;
        }
        set
        {
            if (value == null || id < 0) return;

            var validItems = _items.Where(x => x.Value != null).ToArray();
            if (id < validItems.Length)
            {
                var existingId = validItems[id].Key;
                value.MarkBackInTheEngine();
                _items[existingId] = value;
            }
            else
            {
                // Se l'indice è fuori range, aggiungi come nuovo elemento
                value.MarkBackInTheEngine();
                _items[value.MissionId] = value;
            }
        }
    }
    
    #endregion Public Indexers

    #region Private Methods

    /// <summary>
    /// Registra che questa missione si trova su questo dispositivo.
    /// </summary>
    /// <param name="mission">La missione che dichiara la posizione.</param>
    /// <param name="itemGuid">Il dispositivo su cui si trova.</param>
    /// <remarks>
    /// Se il posto risulta gia' occupato da un'<b>altra</b> missione vince chi arriva — e' la
    /// semantica di <c>PositionItemId</c>, che e' l'ultimo a dichiarare dove si trova — ma il fatto
    /// finisce a log. Prima non si vedeva affatto: la ricerca ne restituiva una delle due a caso, e
    /// il dispositivo lavorava ora sull'una ora sull'altra.
    /// </remarks>
    private void IndicizzaPosizione(PathMission mission, Guid itemGuid)
    {
        AggiornaIndice(copia =>
        {
            copia.TryGetValue(itemGuid, out var precedente);
            copia[itemGuid] = mission;
            return precedente;
        }, itemGuid, mission);
    }

    /// <summary>
    /// Copia l'indice, lo lascia modificare, e pubblica la copia con una sola assegnazione.
    /// </summary>
    /// <param name="modifica">
    /// Riceve la copia da modificare e restituisce chi occupava il posto, se qualcuno.
    /// </param>
    /// <param name="itemGuid">Il posto toccato, per il log.</param>
    /// <param name="mission">La missione che lo occupa ora, per il log; null se lo sta lasciando.</param>
    private void AggiornaIndice(Func<Dictionary<Guid, PathMission>, PathMission?> modifica,
        Guid itemGuid, PathMission? mission)
    {
        PathMission? precedente;

        lock (PerPosizioneLock)
        {
            var copia = new Dictionary<Guid, PathMission>(_perPosizione);
            precedente = modifica(copia);
            Volatile.Write(ref _perPosizione, copia);
        }

        if (mission == null || precedente == null || ReferenceEquals(precedente, mission)) return;

        // Sono davvero due solo se la precedente e' ancora attiva: altrimenti era una voce scaduta,
        // e non c'e' nessun conflitto da segnalare.
        if (_items.ContainsKey(precedente.MissionId))
            Navigator.WriteLog(
                $"Due missioni dichiarano la posizione {itemGuid}: {precedente.MissionId} e {mission.MissionId}. Vale la seconda.",
                LogEntryType.Warning);
    }

    /// <summary>
    /// Toglie dall'indice la posizione occupata da questa missione.
    /// </summary>
    /// <param name="mission">La missione che lascia il posto.</param>
    /// <remarks>
    /// E' <b>pulizia, non correttezza</b>: <see cref="MissionAtPosition"/> ricontrolla comunque che
    /// la missione sia ancora fra le attive, quindi una voce non sganciata non fa rispondere il
    /// falso — fa solo crescere l'indice. Vale la pena saperlo, perche' vuol dire che dimenticare
    /// questa chiamata su un percorso di rimozione nuovo non produce un guasto visibile, ma una
    /// perdita silenziosa. Tutti i percorsi di rimozione passano di qui o da <c>RemoveAll</c>.
    /// </remarks>
    private void SganciaPosizione(PathMission? mission)
    {
        if (mission == null) return;

        var posizione = mission.PositionItemId;

        if (posizione == Guid.Empty) return;

        AggiornaIndice(copia =>
        {
            if (copia.TryGetValue(posizione, out var presente) && ReferenceEquals(presente, mission))
                copia.Remove(posizione);

            return null;
        }, posizione, null);
    }

    /// <summary>
    /// Verifica se una missione deve essere rimossa in base alle condizioni del PositionItem.
    /// Rimuove automaticamente le missioni che hanno un PositionItem null o di tipo Storage.
    /// </summary>
    /// <param name="mission">La missione da verificare.</param>
    private async Task CheckAndRemoveMission(PathMission? mission)
    {
        if (mission == null) return;

        if (mission.PositionItem is null or { DeviceType: DeviceTypes.Storage }) await Remove(mission);
    }

    #endregion Private Methods

    #region Private Fields

    /// <summary>
    /// Istanza lazy del singleton per garantire l'inizializzazione thread-safe.
    /// </summary>
    private static readonly Lazy<ActiveMissions> LazyActiveMissions = new(() => new ActiveMissions());
    
    /// <summary>
    /// Dizionario thread-safe che contiene tutte le missioni attive indicizzate per ID missione.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, PathMission?> _items = new();

    /// <summary>
    /// Serializza «controlla che questa UDC non ci sia gia'» e «inseriscila». Il dizionario e' gia'
    /// concorrente: quello che non lo era e' la sequenza fra i due passi.
    /// </summary>
    private static readonly object AddLock = new();

    /// <summary>
    /// Quale missione si trova su quale dispositivo, indicizzata per <c>PositionItemId</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Esiste perche' <c>AutomationDevice.LoadedLu</c> era una <b>ricerca</b>: un
    /// <c>FirstOrDefault</c> su tutte le missioni attive, rifatto a ogni lettura, in un percorso
    /// dove la proprieta' si legge di continuo e spesso due volte nella stessa istruzione. Due
    /// letture consecutive potevano dare risposte diverse, e se due missioni dichiaravano la stessa
    /// posizione quale delle due tornasse era <b>arbitrario</b>: l'ordine di enumerazione di una
    /// <c>ConcurrentDictionary</c> non e' definito. Voce <c>D74</c> del registro.
    /// </para>
    /// <para>
    /// La verita' resta <c>PathMission.PositionItemId</c>: questo e' un indice, non una seconda
    /// fonte. Si aggiorna in <see cref="Add"/>, nelle rimozioni, e nel setter di
    /// <c>PositionItemId</c> attraverso <see cref="PositionChanged"/>.
    /// </para>
    /// </remarks>
    /// <para>
    /// <b>E' un'istantanea immutabile scambiata in un colpo solo, e non una collezione
    /// concorrente.</b> Uno spostamento tocca <b>due</b> chiavi — il posto vecchio e quello nuovo —
    /// e su una <c>ConcurrentDictionary</c> non esiste un ordine che le renda atomiche insieme: chi
    /// legge nel mezzo trova la missione su <b>nessuno</b> dei due dispositivi, oppure su
    /// <b>entrambi</b>. Con la vecchia ricerca nessuna delle due cose poteva capitare, perche' la
    /// posizione era un solo campo, e riaprire quella finestra sarebbe una regressione. Qui chi
    /// scrive copia, modifica e pubblica con una sola assegnazione; chi legge prende il riferimento
    /// e vede sempre uno stato coerente, senza prendere nessun lock.
    /// </para>
    private Dictionary<Guid, PathMission> _perPosizione = new();

    /// <summary>
    /// Serializza chi <b>scrive</b> l'indice: copiare, modificare e pubblicare devono essere un
    /// solo passo rispetto agli altri scrittori. Chi legge non lo prende.
    /// </summary>
    private readonly object PerPosizioneLock = new();

    #endregion Private Fields

    #region Public Properties

    /// <summary>
    /// Ottiene l'istanza singleton della collezione di missioni attive.
    /// </summary>
    /// <value>L'istanza univoca di <see cref="ActiveMissions"/>.</value>
    public static ActiveMissions Instance => LazyActiveMissions.Value;

    /// <summary>
    /// Ottiene una lista di tutti gli step di missione nella collezione.
    /// </summary>
    /// <value>Una nuova lista contenente tutti gli step di missione.</value>
    public List<PathMission?> List => [.._items.Values];
    
    /// <summary>
    /// Ottiene il numero totale di missioni attive nella collezione.
    /// </summary>
    /// <value>Il numero di missioni attualmente presenti nella collezione.</value>
    public int Count => _items.Count;

    /// <summary>
    /// Restituisce un array di tutte le missioni attive non nulle.
    /// </summary>
    /// <returns>Un array contenente tutte le missioni attive valide.</returns>
    public PathMission[] ToArray() => _items.Values.Where(x => x != null).ToArray()!;

    #endregion Public Properties

    #region Public Methods

    /// <summary>
    /// Aggiunge una nuova missione alla collezione delle missioni attive.
    /// La missione viene aggiunta solo se il navigatore non è in stato Stop o Pause e se non esiste già una missione con lo stesso LuId.
    /// </summary>
    /// <param name="value">La missione da aggiungere.</param>
    /// <param name="buildRoute">Indica se costruire automaticamente il percorso verso la destinazione.</param>
    public void Add(PathMission? value, bool buildRoute = true)
    {
        if (value == null) return;

        // Early exit for better performance
        if (Navigator.NavStatus == NavigatorStatusTypes.Stop ||
            Navigator.NavStatus == NavigatorStatusTypes.Pause) return;

        // Il controllo anti-duplicato e l'inserimento stanno sotto lo stesso lock. Erano due passi
        // distinti: il dizionario e' concorrente, la sequenza no. Due thread che accodano la stessa
        // UDC -- il ciclo delle estrazioni e un evento del campo, per dire -- potevano superare
        // entrambi il controllo e creare due missioni per lo stesso bancale, con due percorsi e
        // due comandi.
        //
        // Lo stato si ricontrolla sotto il lucchetto del cambio di stato, e l'inserimento finisce
        // prima che lo si rilasci: una volta assegnato lo Stop, nessuno inserisce piu'. Il controllo
        // qui sopra da solo lasciava passare chi era gia' oltre. Si veda Navigator.StatusGate.
        lock (Navigator.StatusGate)
        lock (AddLock)
        {
            if (Navigator.NavStatus == NavigatorStatusTypes.Stop ||
                Navigator.NavStatus == NavigatorStatusTypes.Pause) return;

            if (value.LuId > 0)
            {
                // Use direct iteration instead of LINQ for better performance
                foreach (var item in _items.Values)
                {
                    if (item != null && item.LuId == value.LuId) return;
                }
            }

            // Una missione chiusa non torna (PathMission.Withdraw): chi l'aveva presa dalla coda di
            // prelievo prima della chiusura la rimetterebbe qui dopo la sua domanda al gestionale,
            // con la riga nel database del WCS e la rotta. Si guarda dopo il controllo anti-duplicato,
            // e l'ordine conta: la chiusura alza il segno prima di toglierla da qui, quindi chi non la
            // trova piu' fra le attive vede anche il segno. (Si chiudono solo missioni con l'UDC del
            // gestionale, che il controllo anti-duplicato guarda.) La rimozione di una missione con le
            // copie della sua porta (Remove) lo alza invece dopo averla tolta: e' una missione su un
            // dispositivo di un gruppo, che nessuno rimette qui, e il segno serve ai cicli del gruppo.
            // (PathMission.Withdraw.)
            if (value.IsWithdrawn)
            {
                Navigator.WriteLog(
                    $"La missione {value.MissionId} e' stata chiusa: non torna fra le missioni attive",
                    LogEntryType.Information);
                return;
            }

            value.MarkBackInTheEngine();
            _items.TryAdd(value.MissionId, value);

            // Sotto lo stesso lock dell'inserimento: cosi' non esiste un istante in cui la missione
            // e' attiva ma il suo posto non risulta occupato.
            if (value.PositionItemId != Guid.Empty)
                IndicizzaPosizione(value, value.PositionItemId);
        }

        ORM.PathMission.Add(value);

        // La costruzione del percorso parte DOPO l'inserimento e dopo la riga sul database. Prima
        // partiva prima di entrambi: il percorso si costruiva su una missione che ActiveMissions
        // non conteneva ancora -- e AutomationDevice.LoadedLu si ricava cercando proprio li dentro,
        // quindi il dispositivo di partenza risultava scarico -- mentre le scritture che la
        // costruzione fa sulla missione finivano su una riga di MISSION_LU non ancora inserita.
        if (buildRoute)
        {
            value.RouteWork = BackgroundWork.Track(Task.Run(async () =>
            {
                try
                {
                    await value.BuildPathToDestination();
                }
                catch (Exception ex)
                {
                    Navigator.WriteLog($"Error building path for mission {value.MissionId}: {ex.Message}\r\nStackTrace: {ex.StackTrace}", LogEntryType.Warning);
                }
            }));
        }

        Navigator.EventSink?.MissionChanged(value, EngineMissionChange.Started);
    }

    /// <summary>
    /// Aggiunge una collezione di missioni alla collezione delle missioni attive.
    /// </summary>
    /// <param name="collection">La collezione di missioni da aggiungere.</param>
    public void AddRange(IEnumerable<PathMission?> collection)
    {
        foreach (var item in collection)
            Add(item);
    }

    /// <summary>
    /// Determina se esiste almeno una missione che soddisfa la condizione specificata.
    /// </summary>
    /// <param name="match">Il predicato da testare su ogni missione.</param>
    /// <returns>true se almeno una missione soddisfa la condizione; altrimenti, false.</returns>
    public bool Any(Func<PathMission?, bool> match)
    {
        // Use direct iteration for better performance
        foreach (var item in _items.Values)
        {
            if (match(item)) return true;
        }
        return false;
    }

    /// <summary>
    /// Determina se la collezione contiene almeno una missione.
    /// </summary>
    /// <returns>true se la collezione non è vuota; altrimenti, false.</returns>
    public bool Any()
    {
        return !_items.IsEmpty;
    }

    /// <summary>
    /// Rimuove tutte le missioni dalla collezione.
    /// </summary>
    /// <param name="removeFromDb">Indica se rimuovere anche le missioni dal database.</param>
    public async Task Clear(bool removeFromDb)
    {
        if (removeFromDb)
        {
            // Use Span<T> for better performance when iterating
            var values = _items.Values.ToArray();
            foreach (var item in values)
                await Remove(item);
        }
        else
        {
            // Direct iteration without intermediate collection
            foreach (var item in _items.Values)
                item?.RemoveMissionSteps();

            _items.Clear();

            lock (PerPosizioneLock)
                Volatile.Write(ref _perPosizione, new Dictionary<Guid, PathMission>());
        }
    }

    /// <summary>
    /// Restituisce la missione che si trova sul dispositivo indicato, se ce n'e' una.
    /// </summary>
    /// <param name="itemGuid">Identificativo del dispositivo.</param>
    /// <returns>La missione a bordo, oppure <c>null</c>.</returns>
    /// <remarks>
    /// Ricontrolla che la missione sia ancora fra le attive. <b>Non</b> ricontrolla
    /// <c>PositionItemId</c>, ed e' voluto: l'indice si aggiorna un istante prima del campo, e
    /// verificare anche il campo aprirebbe una finestra in cui la missione non risulta a bordo di
    /// nessuno dei due dispositivi.
    /// </remarks>
    public PathMission? MissionAtPosition(Guid itemGuid)
    {
        if (itemGuid == Guid.Empty) return null;

        // Un riferimento solo, letto una volta: da qui in poi si lavora su un'istantanea che
        // nessuno puo' cambiare sotto i piedi.
        if (!Volatile.Read(ref _perPosizione).TryGetValue(itemGuid, out var mission)) return null;

        return _items.ContainsKey(mission.MissionId) ? mission : null;
    }

    /// <summary>
    /// Aggiorna l'indice quando una missione cambia posizione.
    /// </summary>
    /// <param name="mission">La missione che si sposta.</param>
    /// <param name="precedente">Il dispositivo da cui viene.</param>
    /// <param name="nuova">Il dispositivo su cui va.</param>
    /// <remarks>
    /// Chiamato dal setter di <c>PathMission.PositionItemId</c>, che e' il punto unico da cui la
    /// posizione cambia. Indicizza solo le missioni gia' fra le attive: una UDC in coda di prelievo
    /// non e' «a bordo» di nulla e non deve risultare tale.
    /// </remarks>
    internal void PositionChanged(PathMission mission, Guid precedente, Guid nuova)
    {
        if (!_items.ContainsKey(mission.MissionId)) return;

        // Le due chiavi si toccano nella stessa copia e si pubblicano insieme: e' cio' che impedisce
        // a chi legge di trovare la missione su nessuno dei due dispositivi, o su entrambi.
        AggiornaIndice(copia =>
        {
            if (precedente != Guid.Empty &&
                copia.TryGetValue(precedente, out var presente) && ReferenceEquals(presente, mission))
                copia.Remove(precedente);

            if (nuova == Guid.Empty) return null;

            copia.TryGetValue(nuova, out var occupante);
            copia[nuova] = mission;

            return occupante;
        }, nuova, nuova == Guid.Empty ? null : mission);
    }

    /// <summary>
    /// Determina se la collezione contiene la missione specificata.
    /// </summary>
    /// <param name="item">La missione da cercare nella collezione.</param>
    /// <returns>true se la missione è presente nella collezione; altrimenti, false.</returns>
    public bool Contains(PathMission item)
    {
        return _items.ContainsKey(item.MissionId);
    }

    /// <summary>
    /// Cerca la prima missione che soddisfa la condizione specificata.
    /// </summary>
    /// <param name="match">Il predicato da testare su ogni missione.</param>
    /// <returns>La prima missione che soddisfa la condizione, o null se non trovata.</returns>
    public PathMission? Find(Predicate<PathMission?> match)
    {
        // Use direct iteration instead of ToList().Find()
        foreach (var item in _items.Values)
        {
            if (match(item)) return item;
        }
        return null;
    }

    /// <summary>
    /// Cerca l'indice della prima missione che soddisfa la condizione specificata.
    /// </summary>
    /// <param name="match">Il predicato da testare su ogni missione.</param>
    /// <returns>L'indice della prima missione che soddisfa la condizione, o -1 se non trovata.</returns>
    public int FindIndex(Predicate<PathMission?> match)
    {
        // Direct iteration with index tracking
        int index = 0;
        foreach (var item in _items.Values)
        {
            if (match(item)) return index;
            index++;
        }
        return -1;
    }

    /// <summary>
    /// Restituisce la prima missione che soddisfa la condizione specificata, o null se non trovata.
    /// </summary>
    /// <param name="match">Il predicato da testare su ogni missione.</param>
    /// <returns>La prima missione che soddisfa la condizione, o null se non trovata.</returns>
    public PathMission? FirstOrDefault(Func<PathMission?, bool> match)
    {
        // Use direct iteration for better performance
        foreach (var item in _items.Values)
        {
            if (match(item)) return item;
        }
        return null;
    }

    /// <summary>
    /// Restituisce la prima missione nella collezione, o null se la collezione è vuota.
    /// </summary>
    /// <returns>La prima missione nella collezione, o null se vuota.</returns>
    public PathMission? FirstOrDefault()
    {
        // Use enumeration directly
        foreach (var item in _items.Values)
        {
            return item;
        }
        return null;
    }

    /// <summary>
    /// Rimuove una missione dalla collezione e dal database, gestendo la pulizia di risorse associate.
    /// Include la gestione di assegnazioni note e code di estrazione.
    /// </summary>
    /// <param name="value">La missione da rimuovere.</param>
    /// <returns>True se la missione e' stata tolta; false se la rimozione e' stata rifiutata.</returns>
    /// <remarks>
    /// <para>
    /// <b>Rifiuta finche' uno step della missione e' in volo.</b> Cancellare gli step di una
    /// missione che un dispositivo sta muovendo lo rende libero a meta' movimento — il suo
    /// <c>RunningStepId</c> e' derivato dalla collezione globale degli step — e quando il campo
    /// risponde con <c>StepDone</c> il completamento viene scartato: il bancale e' arrivato, ma
    /// nessuno lo carica e il mittente resta <c>Moving</c>. Regola decisa il 2026-09-03: si rifiuta
    /// sempre. L'operatore toglie il bancale da un elemento in <c>Warning</c>, dove lo step e' gia'
    /// concluso e quindi non c'e' niente in volo da proteggere.
    /// </para>
    /// <para>
    /// <b>Prima il motore, poi il resto del mondo.</b> Qui l'ordine era: azzerare la posizione,
    /// <i>attendere</i> le cancellazioni su WMS e database, togliere gli step, e solo alla fine la
    /// missione dalla collezione. Durante le attese la missione era «per aria» — attiva, senza
    /// posizione, con gli step ancora presenti — e se una cancellazione falliva restava cosi' per
    /// sempre. Ora esce dalla collezione, dall'indice e dagli step in un solo passo senza attese;
    /// le cancellazioni esterne vengono dopo, e se falliscono restano a log: il disallineamento
    /// possibile e' verso il gestionale, non verso l'impianto.
    /// </para>
    /// </remarks>
    public async Task<bool> Remove(PathMission? value, bool releaseStorageReservation = true)
    {
        if (value == null) return false;

        if (value.StepInFlight() is { } inVolo)
        {
            Navigator.WriteLog(
                $"Rimozione della missione {value.MissionId} rifiutata: lo step {inVolo.StepId} e' in volo su {inVolo.FromNodeId}",
                LogEntryType.Warning);
            return false;
        }

        var positionItem = value.PositionItem;
        var eraSuUnDispositivo = positionItem != null && positionItem.DeviceType != DeviceTypes.Storage;

        // 0. Le copie della sua porta (punto 3 dell'8/10 sera): la missione che esce da un dispositivo di
        //    un gruppo lascia la cella che aveva prenotato, e le missioni del gruppo che ne hanno copiato
        //    la porta la lasciano prima di lei, ferme finche' non e' uscita; ferma anche lei, perche' un
        //    ciclo del gruppo non ne presti la porta mentre esce. La guarda solo chi esce da un
        //    dispositivo: chi ha depositato esce dal magazzino, e la cella e' quella in cui le copie
        //    depositano ancora. Se lei o una copia non si puo' fermare, la rimozione si rifiuta, e niente e'
        //    toccato; per una copia la riga dice «another load unit sharing its cell is moving», le parole
        //    del cambio dell'operatore rifiutato per lo stesso motivo (risposta del 9/10).
        List<PathMission> ferme = [];
        if (eraSuUnDispositivo &&
            !MissionGateway.TheCopiesLeaveTheDoorOfAMissionThatLeaves(value, out ferme, out var nonFerma))
        {
            Navigator.WriteLog(
                $"Rimozione della missione {value.MissionId} rifiutata: esce da un gruppo con la porta {value.StorageDoorCode}, che le copie lascerebbero con lei, e non si puo': {nonFerma}",
                LogEntryType.Warning);
            return false;
        }

        // 1. Il motore. L'ordine fra le tre righe conta: SganciaPosizione legge PositionItemId, e
        //    PositionChanged non indicizza piu' una missione che non e' fra le attive. Lo sguardo allo
        //    step in volo si rifa' qui, insieme all'uscita, sotto il lucchetto della rotta
        //    (PathMission.LeaveUnlessAStepIsInFlight): quello in cima e' fuori, e uno step che partiva in
        //    mezzo si toglieva in volo (risposta 2 del proprietario dell'11/10). Con le copie ferme la
        //    missione e' ferma anche lei, e nessuno step parte in mezzo; senza, l'avvio aspetta il
        //    lucchetto e trova la rotta tolta.
        Items.MissionStep? partito = null;
        try
        {
            partito = value.LeaveUnlessAStepIsInFlight(() =>
            {
                _items.TryRemove(value.MissionId, out _);
                // Its row goes too: no later write of this object brings it back (D1 of the quick
                // start run of 15/10). Before the position is cleared, whose arrival work writes the row.
                value.MarkRemovedFromTheEngine();
                SganciaPosizione(value);
                if (eraSuUnDispositivo) value.PositionItemId = Guid.Empty;
                value.RemoveMissionSteps();
            });
        }
        finally
        {
            // Lei e le copie ripartono con la missione gia' fuori, rotta compresa: il giro del gruppo non
            // le conta con lei. Lei pero' riparte chiusa (PathMission.Withdraw): un ciclo del gruppo che
            // ha letto i carichi prima della rimozione e la conta adesso (PathMission.TryBeginStepStart)
            // la troverebbe libera, con la porta e la cella ancora scritte, e ne darebbe la porta a una
            // copia nuova, che nessuna missione tiene (secondo giro di correzioni del compito 15). Il
            // rilascio serve ancora a chi aspetta il cambio per cercarle la cella. Chiusa solo se e'
            // uscita davvero.
            if (ferme.Count > 0 && partito == null) value.Withdraw();
            MissionGateway.RestartTheCopies(ferme);
        }

        if (partito != null)
        {
            Navigator.WriteLog(
                $"Rimozione della missione {value.MissionId} rifiutata: lo step {partito.StepId} e' partito su {partito.FromNodeId} mentre usciva",
                LogEntryType.Warning);
            return false;
        }

        // La memoria per missione delle celle decodificate (compito 5), delle mappe di partizione e
        // dei gruppi di celle (compito 8) non vale piu' per la prossima missione che tocchera' le
        // stesse celle o gli stessi gruppi.
        value.ClearWarehouseCaches();
        // I fallimenti che il freno del giro delle rotte le contava: si dimenticano quando la
        // ricerca riesce, e una missione che esce prima li lascerebbe li' per tutta la vita del
        // processo (voce 39). Anche la sua richiesta di rotta esce dalla coda, come alla chiusura del
        // cambio cella; chi ce la rimette dopo l'uscita la trova scartata dal giro (BuildRoutePath).
        RoutesToBuild.Instance.Remove(value.MissionId);
        Navigator.ForgetRouteRetries(value.MissionId);

        // 2. Il resto del mondo.
        if (releaseStorageReservation)
            await ReleaseStorageReservation(value);

        try
        {
            if (eraSuUnDispositivo)
            {
                await positionItem!.FireLuLoadStatusChanged();

                if (value.GuidAssignmentLu != Guid.Empty)
                {
                    if (value.LuId > 0)
                        Navigator.WriteLog(
                            $"DEBUG INFO: Cancellazione ASSEGNAZIONE_NOTE da ActiveMissions->Remove->IdAssegnazioneNota UDC: {value.LuId} - {value.LuCode} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                            LogEntryType.Information);

                    // Compito 21, primo dei tre punti di questo fatto. Una segnalazione, non un
                    // ordine: la missione se n'e' andata senza servire l'assegnazione, e che cosa
                    // comporti lo decide il gestionale. Il motivo viaggia con il fatto perche' di
                    // la' finisce nel registro accanto alla cancellazione: la riga qui sopra resta
                    // per chi legge il registro del WCS, quella e' per chi legge il suo.
                    await Navigator.RequireWarehouse("ActiveMissions.Remove").ReportMissionLeftAssignmentUnservedAsync(
                        new MissionLeftAssignmentUnservedRequest(value.MissionId, value.LuId, value.GuidAssignmentLu, 0m,
                            "la missione e' stata tolta dalle missioni attive"));
                }

                if (value.ExtractionQueueId > 0)
                {
                    if (value.LuId > 0)
                    {
                        Navigator.WriteLog(
                            $"DEBUG INFO: Cancellazione ASSEGNAZIONE_NOTE da ActiveMissions->Remove->IdExtractionQueue UDC: {value.LuId} - {value.LuCode} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                            LogEntryType.Information);
                        Navigator.WriteLog(
                            $"DEBUG INFO: Cancellazione ESTRAZIONI_UDC_AUTOMATICHE da ActiveMissions->Remove->IdExtractionQueue UDC: {value.LuId} - {value.LuCode} Date: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                            LogEntryType.Information);
                    }

                    // Compito 13: lo stesso fatto di poche righe sopra, con l'altro campo.
                    // L'assegnazione che la riga di coda portava e' nominata per riga
                    // (ID_ASSEGNAZIONE_UDC), non per GUID, e prima la cancellava il motore con un
                    // DELETE in T-SQL diretto. La segnalazione viene prima della cancellazione della
                    // riga di coda: un guasto del confine risale al catch qui sotto e la riga resta,
                    // invece di sparire lasciando viva l'assegnazione di una missione che non c'e'
                    // piu'.
                    await DbInfo.ReportQueuedAssignmentLeftUnserved(value.MissionId, value.LuId,
                        value.ExtractionQueueId, "la missione e' stata tolta dalle missioni attive");
                    await DbInfo.DeleteExtractionItemQueueById(value.ExtractionQueueId);
                }
            }

            ORM.PathMission.Remove(value);
        }
        catch (Exception ex)
        {
            // Il motore e' gia' coerente: la missione non c'e' piu'. Quello che e' fallito e' la
            // pulizia verso il gestionale, e va ripresa a mano partendo da questo log.
            Navigator.WriteLog(
                $"Missione {value.MissionId} tolta dal motore, ma la pulizia esterna e' fallita: {ex.Message}\r\nStackTrace: {ex.StackTrace}",
                LogEntryType.Warning);
        }

        Navigator.EventSink?.MissionChanged(value,
            value.Status == MissionStatusTypes.Done ? EngineMissionChange.Completed : EngineMissionChange.Removed);

        return true;
    }

    /// <summary>
    /// La missione lascia la prenotazione di deposito che teneva presso il gestionale (compito 17):
    /// la cella che aspettava torna libera per le altre. Si chiama su <b>ogni</b> missione che esce
    /// dal motore, senza sapere se ne avesse una: e' il gestionale a saperlo, l'operazione e'
    /// idempotente, e un secondo elenco di missioni prenotate tenuto qui sarebbe una seconda fonte
    /// di verita' che il riavvio del WCS perderebbe.
    /// <para>
    /// <b>A miglior sforzo</b>, e fuori dal <c>try</c> della pulizia qui sotto apposta: un
    /// gestionale che non risponde non deve impedire alla missione di uscire, ne' portarsi via le
    /// cancellazioni che seguono (era proprio la forma del difetto che l'ordine «prima il motore,
    /// poi il mondo» ha riparato). La prenotazione che resta appesa la raccoglie il compito
    /// periodico del gestionale: quando il WCS torna raggiungibile, scade le piu' vecchie di un
    /// tempo configurabile che il WCS conferma di non trattenere. Durante una partizione non
    /// cancella niente, per non liberare la cella di un Allocate ancora pendente.
    /// </para>
    /// </summary>
    private static async Task ReleaseStorageReservation(PathMission value)
    {
        if (Navigator.Warehouse is not { } warehouse) return;

        try
        {
            await warehouse.ReleaseStorageReservationAsync(value.MissionId);
        }
        catch (Exception ex)
        {
            Navigator.WriteLog(
                $"Missione {value.MissionId}: la prenotazione di deposito non e' stata rilasciata ({ex.Message}). " +
                "La liberera' il compito periodico del gestionale.",
                LogEntryType.Warning);
        }
    }

    /// <summary>
    /// Rimuove tutte le missioni che soddisfano la condizione specificata.
    /// </summary>
    /// <param name="match">Il predicato da testare su ogni missione per determinare se rimuoverla.</param>
    public void RemoveAll(Predicate<PathMission> match)
    {
        // Collect keys to remove first, then remove them
        var keysToRemove = new List<Guid>();
        
        foreach (var kvp in _items)
        {
            if (kvp.Value != null && match(kvp.Value))
                keysToRemove.Add(kvp.Key);
        }

        foreach (var key in keysToRemove)
            if (_items.TryRemove(key, out var rimossa))
                SganciaPosizione(rimossa);
    }

    /// <summary>
    /// Rimuove la missione all'indice specificato.
    /// </summary>
    /// <param name="index">L'indice della missione da rimuovere.</param>
    public async Task RemoveAt(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        
        // Direct enumeration with index tracking
        var currentIndex = 0;
        foreach (var kvp in _items)
        {
            if (currentIndex == index)
            {
                await Remove(kvp.Value);
                return;
            }
            currentIndex++;
        }
    }

    /// <summary>
    /// Rimuove una missione non utilizzata dalla collezione.
    /// Se la missione è presente nella collezione, viene verificata e rimossa se necessario.
    /// Se non è presente, viene rimossa direttamente dal database.
    /// </summary>
    /// <param name="value">La missione da rimuovere se non utilizzata.</param>
    public async Task RemoveUnused(PathMission? value)
    {
        if (value == null) return;

        try
        {
            if (_items.TryGetValue(value.MissionId, out var mission))
                await CheckAndRemoveMission(mission);
            else
                ORM.PathMission.Remove(value.MissionId);
        }
        catch (Exception ex)
        {
            Navigator.WriteLog(
                $"Error On ActiveMissions->RemoveUnused Description: {ex.Message}\r\nStackTrace: {ex.StackTrace}",
                LogEntryType.Warning);
        }
    }

    /// <summary>
    /// Rimuove una missione non utilizzata dalla collezione utilizzando il suo identificatore.
    /// </summary>
    /// <param name="value">L'identificatore della missione da rimuovere se non utilizzata.</param>
    public async Task RemoveUnused(Guid value)
    {
        if (value == Guid.Empty) return;

        try
        {
            if (_items.TryGetValue(value, out var mission))
                await CheckAndRemoveMission(mission);
            else
                ORM.PathMission.Remove(value);
        }
        catch (Exception ex)
        {
            Navigator.WriteLog(
                $"Error On ActiveMissions->RemoveUnused Description: {ex.Message}\r\nStackTrace: {ex.StackTrace}",
                LogEntryType.Warning);
        }
    }

    /// <summary>
    /// Filtra le missioni attive che soddisfano la condizione specificata.
    /// </summary>
    /// <param name="match">Il predicato da applicare per filtrare le missioni.</param>
    /// <returns>Una sequenza enumerabile delle missioni che soddisfano la condizione.</returns>
    public IEnumerable<PathMission> Where(Func<PathMission?, bool> match)
    {
        // Use yield return for deferred execution and better memory usage
        return _items.Values.OfType<PathMission>().Where(match);
    }

    #endregion Public Methods
}
