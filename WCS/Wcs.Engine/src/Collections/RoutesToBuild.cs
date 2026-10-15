using Wcs.Engine.Items;
using System.Collections.Concurrent;

namespace Wcs.Engine.Collections;

/// <summary>
/// Gestisce una collezione singleton di percorsi (route) da costruire per le missioni di automazione.
/// Fornisce accesso ottimizzato tramite indicizzazione e mantiene sincronizzata una cache per ricerche veloci.
/// </summary>
public sealed class RoutesToBuild
{
    #region Private Fields

    /// <summary>
    /// Implementazione lazy del pattern Singleton per garantire un'unica istanza thread-safe.
    /// </summary>
    private static readonly Lazy<RoutesToBuild> Lazy = new(() => new RoutesToBuild());

    /// <summary>
    /// Cache dictionary per lookup O(1) delle missioni tramite MissionId.
    /// Mantiene sincronizzazione con la coda Items per prestazioni ottimali.
    /// Thread-safe per accessi concorrenti.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, PathMission> _missionLookup = new();

    /// <summary>
    /// Serializza le scritture della coda. <see cref="Remove"/> la gira tutta per lasciare fuori una
    /// missione: senza il lock un Enqueue o un Dequeue concorrente (il giro del navigatore, ogni
    /// 10 ms) la troverebbe a meta', e la cache di lookup e la coda direbbero cose diverse.
    /// </summary>
    private readonly object _gate = new();

    #endregion Private Fields

    #region Public Indexers

    /// <summary>
    /// Ottiene una missione specifica tramite il suo identificativo univoco.
    /// Utilizza lookup ottimizzato O(1) invece di ricerca lineare.
    /// </summary>
    /// <param name="key">L'identificativo univoco della missione (MissionId).</param>
    /// <returns>La missione corrispondente se trovata, altrimenti null.</returns>
    public PathMission? this[Guid key] => _missionLookup.GetValueOrDefault(key);

    #endregion Public Indexers

    #region Public Properties

    /// <summary>
    /// Ottiene l'istanza singleton della collezione RoutesToBuild.
    /// Thread-safe e lazy-initialized.
    /// </summary>
    public static RoutesToBuild Instance => Lazy.Value;

    /// <summary>
    /// Campo privato per la coda delle missioni.
    /// Thread-safe per operazioni concorrenti.
    /// </summary>
    private ConcurrentQueue<PathMission?> _items = new();

    /// <summary>
    /// Ottiene o imposta la coda delle missioni in attesa di costruzione del percorso.
    /// Il setter ricostruisce automaticamente la cache di lookup per mantenere la sincronizzazione.
    /// NOTA: Sostituire l'intera queue dovrebbe essere fatto solo durante inizializzazione.
    /// </summary>
    /// <value>Una coda di missioni PathMission che possono essere null.</value>
    public ConcurrentQueue<PathMission?> Items
    {
        get => _items;
        set
        {
            lock (_gate)
            {
                _items = value;
                RebuildLookup();
            }
        }
    }

    #endregion Public Properties

    #region Private Methods

    /// <summary>
    /// Ricostruisce completamente la cache di lookup dalla coda corrente.
    /// Utilizzato quando la collezione Items viene sostituita completamente.
    /// Filtra automaticamente i valori null dalla coda.
    /// Thread-safe utilizzando ConcurrentDictionary.
    /// </summary>
    private void RebuildLookup()
    {
        _missionLookup.Clear();
        foreach (var item in _items.OfType<PathMission>())
        {
            _missionLookup.TryAdd(item.MissionId, item);
        }
    }

    #endregion Private Methods

    #region Public Methods

    /// <summary>
    /// Aggiunge una nuova missione alla fine della coda e aggiorna la cache di lookup.
    /// Operazione thread-safe e performante O(1).
    /// </summary>
    /// <param name="mission">La missione da aggiungere alla coda. Può essere null.</param>
    public void Enqueue(PathMission? mission)
    {
        lock (_gate)
        {
            _items.Enqueue(mission);
            if (mission != null)
            {
                _missionLookup.TryAdd(mission.MissionId, mission);
            }
        }
    }

    /// <summary>
    /// Accoda la missione solo se non e' gia' in coda: lo sguardo e l'accodamento insieme, sotto lo
    /// stesso lucchetto delle scritture della coda.
    /// </summary>
    /// <param name="mission">La missione da rimettere al giro delle rotte.</param>
    /// <returns>Vero se l'ha accodata, falso se c'era gia'.</returns>
    /// <remarks>
    /// Chi rimette in coda una missione «se non c'e' gia'» passa da qui: la UDC caricata di un
    /// dispositivo (<c>AutomationDevice.RequeueLoadedLu</c>), chi ha perso il confronto della versione
    /// della rotta (<c>PathMission.BackToTheRouteLoop</c>), e dal giro finale delle correzioni della voce
    /// 41 anche lo spostamento senza rotta (<c>MissionGateway.Move</c>), la cella lasciata
    /// (<c>MissionGateway.DropTheCellAndRequeue</c>) e il deposito che aspetta la conferma della cella
    /// (<c>PathMission.MayStartTheDeposit</c>). Prima ognuno guardava l'indice e poi accodava,
    /// fuori dal lucchetto: due di loro insieme accodavano la missione due volte, e il primo
    /// <see cref="Dequeue"/> toglieva l'indice lasciando l'altra copia in coda (revisione finale della
    /// voce 41).
    /// </remarks>
    public bool EnqueueIfAbsent(PathMission mission)
    {
        lock (_gate)
        {
            if (!_missionLookup.TryAdd(mission.MissionId, mission))
                return false;

            _items.Enqueue(mission);
            return true;
        }
    }

    /// <summary>
    /// Rimuove e restituisce la missione all'inizio della coda, aggiornando la cache di lookup.
    /// Operazione thread-safe e performante O(1).
    /// </summary>
    /// <returns>La prima missione nella coda se presente, altrimenti null.</returns>
    public PathMission? Dequeue()
    {
        lock (_gate)
        {
            if (!_items.TryDequeue(out var mission))
                return null;

            if (mission != null)
            {
                _missionLookup.TryRemove(mission.MissionId, out _);
            }
            return mission;
        }
    }

    /// <summary>
    /// Toglie la missione dalla coda, tutte le volte che c'e' (chi accoda non sempre guarda se c'e'
    /// gia'), e dalla cache di lookup; le altre restano nel loro ordine. Serve a chi le ha dato la
    /// rotta fuori dal giro del navigatore (lo spostamento, <c>MissionGateway.Move</c>): la richiesta
    /// rimasta chiedeva quella rotta, e il giro, ripescandola piu' avanti, farebbe ripartire la
    /// ricerca dal dispositivo dove la missione si trova allora.
    /// </summary>
    /// <param name="missionId">La missione da togliere. Se non e' in coda non cambia niente.</param>
    public void Remove(Guid missionId)
    {
        lock (_gate)
        {
            // Un giro completo della coda: ogni voce esce, e rientra in fondo se non e' della missione.
            var count = _items.Count;
            for (var i = 0; i < count && _items.TryDequeue(out var mission); i++)
            {
                if (mission?.MissionId != missionId)
                    _items.Enqueue(mission);
            }

            _missionLookup.TryRemove(missionId, out _);
        }
    }

    #endregion Public Methods
}