using System.Collections.Concurrent;
using Wcs.Engine.Items;

namespace Wcs.Engine.Collections;

/// <summary>
/// Rappresenta una collezione thread-safe singleton di giunzioni di automazione.
/// Gestisce l'archiviazione, la ricerca e la manipolazione delle giunzioni che collegano
/// i dispositivi di automazione nel sistema di controllo dei percorsi.
/// </summary>
public sealed class PathJunctions
{
    #region Private Constructors

    private PathJunctions()
    {
    }

    #endregion Private Constructors

    #region Private Fields

    private static readonly Lazy<PathJunctions> LazyPathJunctions = new(() => new PathJunctions());
    private readonly ConcurrentDictionary<Guid, AutomationJunction> _items = new();

    #endregion Private Fields

    #region Public Properties

    /// <summary>
    /// Ottiene l'istanza singleton della collezione di giunzioni di percorso.
    /// Utilizza il pattern Lazy per garantire l'inizializzazione thread-safe.
    /// </summary>
    /// <value>L'istanza singleton di <see cref="PathJunctions"/></value>
    public static PathJunctions Instance => LazyPathJunctions.Value;

    /// <summary>
    /// Ottiene il numero totale di giunzioni di automazione presenti nella collezione.
    /// </summary>
    /// <value>Il numero di giunzioni contenute nella collezione</value>
    public int Count => _items.Count;

    /// <summary>
    /// Ottiene una lista contenente tutte le giunzioni di automazione presenti nella collezione.
    /// La lista viene creata ad ogni accesso e pre-dimensionata per ottimizzare le prestazioni.
    /// </summary>
    /// <value>Una nuova lista contenente tutte le giunzioni di automazione</value>
    public List<AutomationJunction?> List
    {
        get
        {
            var count = _items.Count;
            var result = new List<AutomationJunction?>(count);
            result.AddRange(_items.Values);
            return result;
        }
    }

    #endregion Public Properties

    #region Public Methods

    /// <summary>
    /// Aggiunge una giunzione di automazione alla collezione utilizzando il suo ItemGuid come chiave.
    /// Se esiste già una giunzione con la stessa chiave, l'operazione non viene eseguita.
    /// </summary>
    /// <param name="value">La giunzione di automazione da aggiungere alla collezione</param>
    /// <exception cref="ArgumentNullException">Lanciata quando <paramref name="value"/> è null</exception>
    public void Add(AutomationJunction value)
    {
        _items.TryAdd(value.ItemGuid, value);
    }

    /// <summary>
    /// Aggiunge multiple giunzioni di automazione alla collezione.
    /// Ogni giunzione viene aggiunta utilizzando il metodo <see cref="Add(AutomationJunction)"/>.
    /// </summary>
    /// <param name="values">La collezione di giunzioni di automazione da aggiungere</param>
    /// <exception cref="ArgumentNullException">Lanciata quando <paramref name="values"/> è null</exception>
    public void AddRange(IEnumerable<AutomationJunction> values)
    {
        foreach (var value in values) Add(value);
    }

    /// <summary>
    /// Determina se almeno una giunzione nella collezione soddisfa la condizione specificata.
    /// </summary>
    /// <param name="match">La funzione di predicato da testare su ogni giunzione</param>
    /// <returns>true se almeno una giunzione soddisfa la condizione; altrimenti, false</returns>
    /// <exception cref="ArgumentNullException">Lanciata quando <paramref name="match"/> è null</exception>
    public bool Any(Func<AutomationJunction, bool> match)
    {
        return _items.Values.Any(match);
    }

    /// <summary>
    /// Determina se la collezione contiene almeno una giunzione di automazione.
    /// </summary>
    /// <returns>true se la collezione non è vuota; altrimenti, false</returns>
    public bool Any()
    {
        return !_items.IsEmpty;
    }

    /// <summary>
    /// Rimuove tutte le giunzioni di automazione dalla collezione.
    /// Questa operazione è thread-safe.
    /// </summary>
    public void Clear()
    {
        _items.Clear();
    }

    /// <summary>
    /// Determina se la collezione contiene la giunzione di automazione specificata.
    /// Il confronto viene effettuato utilizzando l'ItemGuid della giunzione.
    /// </summary>
    /// <param name="item">La giunzione di automazione da cercare nella collezione</param>
    /// <returns>true se la giunzione è presente nella collezione; altrimenti, false</returns>
    /// <exception cref="ArgumentNullException">Lanciata quando <paramref name="item"/> è null</exception>
    public bool Contains(AutomationJunction item)
    {
        return _items.ContainsKey(item.ItemGuid);
    }

    /// <summary>
    /// Cerca la prima giunzione di automazione che soddisfa la condizione specificata.
    /// </summary>
    /// <param name="match">Il predicato che definisce la condizione di ricerca</param>
    /// <returns>La prima giunzione che soddisfa la condizione, oppure null se non trovata</returns>
    /// <exception cref="ArgumentNullException">Lanciata quando <paramref name="match"/> è null</exception>
    public AutomationJunction? Find(Predicate<AutomationJunction> match)
    {
        return _items.Values.FirstOrDefault(value => match(value));
    }

    /// <summary>
    /// Restituisce l'indice della prima giunzione di automazione che soddisfa la condizione specificata.
    /// L'indice si basa sull'ordine di enumerazione delle giunzioni nella collezione.
    /// </summary>
    /// <param name="match">Il predicato che definisce la condizione di ricerca</param>
    /// <returns>L'indice della prima giunzione che soddisfa la condizione, oppure -1 se non trovata</returns>
    /// <exception cref="ArgumentNullException">Lanciata quando <paramref name="match"/> è null</exception>
    public int FindIndex(Predicate<AutomationJunction> match)
    {
        var index = 0;
        foreach (var value in _items.Values)
        {
            if (match(value)) return index;
            index++;
        }
        return -1;
    }

    /// <summary>
    /// Restituisce la prima giunzione di automazione che soddisfa la condizione specificata,
    /// oppure null se non viene trovata alcuna giunzione.
    /// </summary>
    /// <param name="match">La funzione di predicato da testare su ogni giunzione</param>
    /// <returns>La prima giunzione che soddisfa la condizione, oppure null se non trovata</returns>
    /// <exception cref="ArgumentNullException">Lanciata quando <paramref name="match"/> è null</exception>
    public AutomationJunction? FirstOrDefault(Func<AutomationJunction, bool> match)
    {
        return _items.Values.FirstOrDefault(match);
    }

    /// <summary>
    /// Restituisce la prima giunzione di automazione nella collezione,
    /// oppure null se la collezione è vuota.
    /// </summary>
    /// <returns>La prima giunzione nella collezione, oppure null se vuota</returns>
    public AutomationJunction? FirstOrDefault()
    {
        return _items.Values.FirstOrDefault();
    }

    /// <summary>
    /// Rimuove la giunzione di automazione specificata dalla collezione.
    /// Se la rimozione ha successo, elimina anche il record corrispondente dal database tramite ORM.
    /// </summary>
    /// <param name="value">La giunzione di automazione da rimuovere</param>
    /// <remarks>
    /// Se <paramref name="value"/> è null, il metodo non esegue alcuna operazione.
    /// La rimozione dal database viene eseguita solo se la rimozione dalla collezione ha successo.
    /// </remarks>
    public void Remove(AutomationJunction? value)
    {
        if (value == null) return;
        if (_items.TryRemove(value.ItemGuid, out _)) ORM.AutomationJunction.Delete(value);
    }

    /// <summary>
    /// Rimuove tutte le giunzioni di automazione che soddisfano la condizione specificata.
    /// Per ogni giunzione rimossa, elimina anche il record corrispondente dal database tramite ORM.
    /// </summary>
    /// <param name="match">Il predicato che definisce la condizione per la rimozione</param>
    /// <returns>Il numero di giunzioni rimosse dalla collezione</returns>
    /// <exception cref="ArgumentNullException">Lanciata quando <paramref name="match"/> è null</exception>
    /// <remarks>
    /// L'operazione è thread-safe e garantisce che tutte le rimozioni dal database vengano
    /// eseguite prima di rimuovere gli elementi dalla collezione.
    /// </remarks>
    public int RemoveAll(Predicate<AutomationJunction> match)
    {
        var keysToRemove = new List<Guid>();
        
        foreach (var kvp in _items)
        {
            if (match(kvp.Value))
            {
                keysToRemove.Add(kvp.Key);
            }
        }

        var removedCount = 0;
        foreach (var key in keysToRemove)
        {
            if (!_items.TryGetValue(key, out var item)) continue;
            ORM.AutomationJunction.Delete(item);
            if (_items.TryRemove(key, out _)) removedCount++;
        }

        return removedCount;
    }

    /// <summary>
    /// Rimuove la giunzione di automazione all'indice specificato.
    /// L'indice si basa sull'ordine di enumerazione delle giunzioni nella collezione.
    /// </summary>
    /// <param name="index">L'indice della giunzione da rimuovere</param>
    /// <remarks>
    /// Se l'indice è fuori dai limiti della collezione, il metodo non esegue alcuna operazione.
    /// L'operazione potrebbe non essere deterministica a causa della natura concorrente del dizionario.
    /// </remarks>
    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        
        var currentIndex = 0;
        foreach (var kvp in _items)
        {
            if (currentIndex == index)
            {
                Remove(kvp.Value);
                return;
            }
            currentIndex++;
        }
    }

    /// <summary>
    /// Filtra le giunzioni di automazione nella collezione in base alla condizione specificata.
    /// </summary>
    /// <param name="match">La funzione di predicato da testare su ogni giunzione</param>
    /// <returns>Una collezione contenente le giunzioni che soddisfano la condizione</returns>
    /// <exception cref="ArgumentNullException">Lanciata quando <paramref name="match"/> è null</exception>
    /// <remarks>
    /// Il risultato è una nuova collezione che contiene solo le giunzioni che soddisfano
    /// la condizione specificata. La collezione originale rimane immutata.
    /// </remarks>
    public IEnumerable<AutomationJunction> Where(Func<AutomationJunction, bool> match)
    {
        return _items.Values.Where(match).ToList();
    }

    #endregion Public Methods
}