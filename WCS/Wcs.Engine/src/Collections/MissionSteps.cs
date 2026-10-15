using System.Collections.Concurrent;
using Wcs.Engine.Api;
using Wcs.Engine.Items;

namespace Wcs.Engine.Collections;

/// <summary>
/// Singleton che gestisce una collezione thread-safe di step di missione.
/// Fornisce accesso centralizzato e operazioni CRUD per tutti gli step di missione nel sistema.
/// </summary>
public sealed class MissionSteps
{
    #region Private Fields

    private static readonly Lazy<MissionSteps> LazyMissionSteps = new(() => new MissionSteps());
    private readonly ConcurrentDictionary<Guid, MissionStep> _items = new();

    #endregion Private Fields

    #region Internal Constructors

    /// <summary>
    /// Inizializza una nuova istanza della classe <see cref="MissionSteps"/>.
    /// Costruttore interno per garantire il pattern singleton.
    /// </summary>
    internal MissionSteps()
    {
    }

    #endregion Internal Constructors

    #region Public Properties

    /// <summary>
    /// Ottiene l'istanza singleton della collezione di step di missione.
    /// </summary>
    /// <value>L'istanza singleton di <see cref="MissionSteps"/>.</value>
    public static MissionSteps Instance => LazyMissionSteps.Value;

    /// <summary>
    /// Ottiene il numero di step di missione presenti nella collezione.
    /// </summary>
    /// <value>Il numero totale di step di missione.</value>
    public int Count => _items.Count;

    /// <summary>
    /// Ottiene una lista di tutti gli step di missione nella collezione.
    /// </summary>
    /// <value>Una nuova lista contenente tutti gli step di missione.</value>
    public List<MissionStep?> List => [.._items.Values];

    #endregion Public Properties

    #region Public Indexers

    /// <summary>
    /// Ottiene o imposta uno step di missione utilizzando il suo identificativo univoco.
    /// </summary>
    /// <param name="stepId">L'identificativo univoco dello step di missione.</param>
    /// <value>Lo step di missione corrispondente o null se non trovato.</value>
    public MissionStep? this[Guid stepId]
    {
        get => _items.GetValueOrDefault(stepId);
        set
        {
            if (value != null) _items[stepId] = value;
        }
    }

    /// <summary>
    /// Ottiene o imposta uno step di missione utilizzando un indice numerico.
    /// L'ordine degli elementi non è garantito a causa della natura del dizionario concorrente.
    /// </summary>
    /// <param name="id">L'indice numerico dell'elemento.</param>
    /// <value>Lo step di missione all'indice specificato o null se l'indice non è valido.</value>
    public MissionStep? this[int id]
    {
        get
        {
            if (id < 0) return null;

            var values = _items.Values;
            var count = 0;
            foreach (var item in values)
            {
                if (count == id) return item;
                count++;
            }
            return null;
        }
        set
        {
            if (value == null || id < 0) return;

            var pairs = _items.ToArray();
            if (id < pairs.Length)
            {
                var existingId = pairs[id].Key;
                _items[existingId] = value;
            }
            else
            {
                _items[value.MissionId] = value;
            }
        }
    }
    
    #endregion Public Indexers

    #region Public Methods

    /// <summary>
    /// Ottiene tutti gli step di missione che appartengono a un movimento raggruppato specifico.
    /// </summary>
    /// <param name="groupStepGuid">L'identificativo univoco del movimento raggruppato.</param>
    /// <returns>Una lista di step di missione che appartengono al movimento raggruppato specificato.</returns>
    public static List<MissionStep> GetGroupedMovementId(Guid groupStepGuid)
    {
        return Instance._items.Values.Where(item => item.GroupedMovementId == groupStepGuid).ToList();
    }

    /// <summary>
    /// Aggiunge un nuovo step di missione alla collezione.
    /// </summary>
    /// <param name="value">Lo step di missione da aggiungere.</param>
    /// <remarks>Se uno step con lo stesso StepId esiste già, l'operazione non ha effetto.</remarks>
    public void Add(MissionStep value)
    {
        // L'evento parte solo se lo step e' entrato davvero: uno StepId gia' presente non e' un
        // cambiamento, e annunciarlo farebbe rileggere la missione per nulla.
        if (_items.TryAdd(value.StepId, value) && value.PathMission is { } mission)
            Navigator.EventSink?.MissionChanged(mission, EngineMissionChange.StepChanged);
    }

    /// <summary>
    /// Aggiunge una collezione di step di missione alla collezione.
    /// </summary>
    /// <param name="values">Gli step di missione da aggiungere.</param>
    /// <remarks>Gli step con StepId già esistenti vengono ignorati.</remarks>
    public void AddRange(IEnumerable<MissionStep> values)
    {
        foreach (var value in values) 
            _items.TryAdd(value.StepId, value);
    }

    /// <summary>
    /// Determina se almeno uno step di missione soddisfa una condizione specificata.
    /// </summary>
    /// <param name="match">La funzione che definisce la condizione da verificare.</param>
    /// <returns>true se almeno uno step soddisfa la condizione; altrimenti, false.</returns>
    public bool Any(Func<MissionStep, bool> match)
    {
        return _items.Values.Any(match);
    }

    /// <summary>
    /// Determina se la collezione contiene almeno uno step di missione.
    /// </summary>
    /// <returns>true se la collezione non è vuota; altrimenti, false.</returns>
    public bool Any()
    {
        return !_items.IsEmpty;
    }

    /// <summary>
    /// Rimuove tutti gli step di missione dalla collezione.
    /// </summary>
    public void Clear()
    {
        _items.Clear();
    }

    /// <summary>
    /// Determina se la collezione contiene uno step di missione specifico.
    /// </summary>
    /// <param name="item">Lo step di missione da cercare.</param>
    /// <returns>true se lo step è presente nella collezione; altrimenti, false.</returns>
    public bool Contains(MissionStep item)
    {
        return _items.ContainsKey(item.StepId);
    }

    /// <summary>
    /// Cerca il primo step di missione che soddisfa una condizione specificata.
    /// </summary>
    /// <param name="match">La condizione che definisce l'elemento da cercare.</param>
    /// <returns>Il primo step che soddisfa la condizione o null se nessun elemento è trovato.</returns>
    public MissionStep? Find(Predicate<MissionStep> match)
    {
        return _items.Values.FirstOrDefault(item => match(item));
    }

    /// <summary>
    /// Cerca l'indice del primo step di missione che soddisfa una condizione specificata.
    /// </summary>
    /// <param name="match">La condizione che definisce l'elemento da cercare.</param>
    /// <returns>L'indice del primo step che soddisfa la condizione o -1 se nessun elemento è trovato.</returns>
    /// <remarks>L'indice si basa sull'ordine di enumerazione del dizionario concorrente e può variare.</remarks>
    public int FindIndex(Predicate<MissionStep> match)
    {
        var index = 0;
        foreach (var item in _items.Values)
        {
            if (match(item)) return index;
            index++;
        }
        return -1;
    }

    /// <summary>
    /// Restituisce il primo step di missione che soddisfa una condizione specificata o null se nessun elemento è trovato.
    /// </summary>
    /// <param name="match">La condizione che definisce l'elemento da cercare.</param>
    /// <returns>Il primo step che soddisfa la condizione o null se nessun elemento è trovato.</returns>
    public MissionStep? FirstOrDefault(Func<MissionStep, bool> match)
    {
        return _items.Values.FirstOrDefault(match);
    }

    /// <summary>
    /// Restituisce il primo step di missione nella collezione o null se la collezione è vuota.
    /// </summary>
    /// <returns>Il primo step nella collezione o null se la collezione è vuota.</returns>
    public MissionStep? FirstOrDefault()
    {
        return _items.Values.FirstOrDefault();
    }

    /// <summary>
    /// Rimuove uno step di missione specifico dalla collezione.
    /// </summary>
    /// <param name="value">Lo step di missione da rimuovere.</param>
    /// <returns>true se lo step è stato rimosso con successo; altrimenti, false.</returns>
    public bool Remove(MissionStep? value)
    {
        return value != null && _items.TryRemove(value.StepId, out _);
    }

    /// <summary>
    /// Rimuove tutti gli step di missione che soddisfano una condizione specificata.
    /// </summary>
    /// <param name="match">La condizione che definisce gli elementi da rimuovere.</param>
    /// <returns>Il numero di step rimossi dalla collezione.</returns>
    public int RemoveAll(Predicate<MissionStep> match)
    {
        var keysToRemove = (from kvp in _items where match(kvp.Value) select kvp.Key).ToList();

        return keysToRemove.Count(key => _items.TryRemove(key, out _));
    }

    /// <summary>
    /// Rimuove lo step di missione all'indice specificato.
    /// </summary>
    /// <param name="index">L'indice dell'elemento da rimuovere.</param>
    /// <returns>true se l'elemento è stato rimosso con successo; altrimenti, false.</returns>
    /// <remarks>L'indice si basa sull'ordine di enumerazione del dizionario concorrente e può variare.</remarks>
    public bool RemoveAt(int index)
    {
        if (index < 0) return false;
        
        var currentIndex = 0;
        foreach (var kvp in _items)
        {
            if (currentIndex == index)
                return _items.TryRemove(kvp.Key, out _);
            currentIndex++;
        }
        return false;
    }

    /// <summary>
    /// Filtra gli step di missione in base a una condizione specificata.
    /// </summary>
    /// <param name="match">La condizione che definisce gli elementi da includere nel risultato.</param>
    /// <returns>Una sequenza enumerabile di step che soddisfano la condizione.</returns>
    public IEnumerable<MissionStep> Where(Func<MissionStep, bool> match)
    {
        return _items.Values.Where(match);
    }

    #endregion Public Methods
}