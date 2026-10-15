using System.Collections.Concurrent;
using Wcs.Engine.Items;

namespace Wcs.Engine.Collections;

/// <summary>
/// Rappresenta una collezione thread-safe singleton di dispositivi di automazione indicizzati per GUID.
/// Fornisce funzionalità per la gestione, ricerca e manipolazione di oggetti AutomationDevice
/// utilizzando vari tipi di chiavi di accesso.
/// </summary>
public sealed class PathDevices
{
    #region Private Constructors

    /// <summary>
    /// Inizializza una nuova istanza della classe PathDevices.
    /// Costruttore privato per implementare il pattern Singleton.
    /// </summary>
    private PathDevices()
    {
    }

    #endregion Private Constructors

    #region Private Fields

    /// <summary>
    /// Istanza lazy per l'implementazione thread-safe del pattern Singleton.
    /// </summary>
    private static readonly Lazy<PathDevices> LazyPathDevices = new(() => new PathDevices());
    
    /// <summary>
    /// Dizionario concorrente che memorizza i dispositivi di automazione indicizzati per GUID.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, AutomationDevice?> _items = new();

    #endregion Private Fields

    #region Public Properties

    /// <summary>
    /// Ottiene l'istanza singleton di PathDevices.
    /// </summary>
    /// <value>L'istanza singleton di PathDevices.</value>
    public static PathDevices Instance => LazyPathDevices.Value;

    /// <summary>
    /// Ottiene il numero totale di dispositivi nella collezione.
    /// </summary>
    /// <value>Il numero di dispositivi attualmente memorizzati nella collezione.</value>
    public int Count => _items.Count;

    /// <summary>
    /// Ottiene una lista di tutti i dispositivi di automazione non nulli nella collezione.
    /// </summary>
    /// <value>Una lista contenente tutti gli AutomationDevice non nulli presenti nella collezione.</value>
    public List<AutomationDevice> List
    {
        get
        {
            var capacity = _items.Count;
            var result = new List<AutomationDevice>(capacity);
            result.AddRange(_items.Values.OfType<AutomationDevice>());

            return result;
        }
    }

    #endregion Public Properties

    #region Public Indexers

    /// <summary>
    /// Ottiene o imposta un dispositivo di automazione utilizzando il suo GUID come chiave.
    /// </summary>
    /// <param name="key">Il GUID del dispositivo da ottenere o impostare.</param>
    /// <returns>Il dispositivo di automazione associato al GUID specificato, o null se non trovato.</returns>
    public AutomationDevice? this[Guid key]
    {
        get => _items.GetValueOrDefault(key);
        set
        {
            if (value != null) _items[key] = value;
        }
    }

    /// <summary>
    /// Ottiene o imposta un dispositivo di automazione utilizzando un indice numerico.
    /// </summary>
    /// <param name="id">L'indice numerico del dispositivo (basato sull'ordine di enumerazione).</param>
    /// <returns>Il dispositivo di automazione all'indice specificato, o null se l'indice è fuori range.</returns>
    /// <remarks>
    /// L'accesso per indice ha complessità O(n) poiché i dispositivi sono memorizzati in un dizionario.
    /// Se l'indice è fuori range durante l'impostazione, il dispositivo viene aggiunto come nuovo elemento.
    /// </remarks>
    public AutomationDevice? this[int id]
    {
        get
        {
            if (id < 0) return null;

            var index = 0;
            foreach (var value in _items.Values)
            {
                if (index == id) return value;
                index++;
            }
            
            return null;
        }
        set
        {
            if (value == null || id < 0) return;

            var index = 0;
            foreach (var kvp in _items)
            {
                if (index == id)
                {
                    _items[kvp.Key] = value;
                    return;
                }
                index++;
            }
            
            // Se l'indice è fuori range, aggiungi come nuovo elemento
            _items[value.ItemGuid] = value;
        }
    }
    
    /// <summary>
    /// Ottiene o imposta un dispositivo di automazione utilizzando i parametri ASI, SUBITEM e PARTITION.
    /// </summary>
    /// <param name="asi">Il valore ASI del dispositivo.</param>
    /// <param name="subitem">Il valore SUBITEM del dispositivo.</param>
    /// <param name="partition">Il valore PARTITION del dispositivo.</param>
    /// <returns>Il dispositivo che corrisponde ai parametri specificati, o null se non trovato o se uno dei parametri è null.</returns>
    public AutomationDevice? this[string? asi, string? subitem, string? partition]
    {
        get
        {
            if (asi == null || subitem == null || partition == null) return null;

            return _items.Values.FirstOrDefault(device => device?.ASI == asi && device.SUBITEM == subitem && device.PARTITION == partition);
        }
        set
        {
            if (asi == null || subitem == null || partition == null || value == null) return;
            
            foreach (var (key, device) in _items)
            {
                if (device?.ASI != asi || device.SUBITEM != subitem || device.PARTITION != partition) continue;
                _items[key] = value;
                return;
            }
        }
    }

    /// <summary>
    /// Ottiene o imposta un dispositivo di automazione utilizzando una chiave generica.
    /// </summary>
    /// <param name="key">La chiave per identificare il dispositivo (attualmente supporta solo GUID).</param>
    /// <returns>Il dispositivo associato alla chiave specificata, o null se la chiave non è supportata o non trovata.</returns>
    public AutomationDevice? this[object? key]
    {
        get
        {
            if (key == null) return null;
            return key switch
            {
                Guid guid => _items.GetValueOrDefault(guid),
                _ => null
            };
        }
        set
        {
            if (key == null || value == null) return;
            if (key is Guid guid) _items[guid] = value;
        }
    }

    /// <summary>
    /// Ottiene o imposta un dispositivo di automazione utilizzando un altro AutomationDevice come chiave.
    /// </summary>
    /// <param name="key">Il dispositivo di automazione da utilizzare come chiave (utilizza il suo ItemGuid).</param>
    /// <returns>Il dispositivo associato al GUID del dispositivo chiave, o null se la chiave è null.</returns>
    public AutomationDevice? this[AutomationDevice? key]
    {
        get => key == null ? null : this[key.ItemGuid];
        set
        {
            if (key == null || value == null) return;
            this[key.ItemGuid] = value;
        }
    }

    #endregion Public Indexers

    #region Public Methods

    /// <summary>
    /// Ottiene un dispositivo di automazione dal suo GroupItemId.
    /// </summary>
    /// <param name="groupId">L'ID del gruppo da cercare.</param>
    /// <returns>Il primo dispositivo che corrisponde al GroupItemId specificato, o null se non trovato o se groupId è 0.</returns>
    public static AutomationDevice? GetFromGroupItemId(int groupId)
    {
        return groupId == 0 ? null : Instance._items.Values.FirstOrDefault(device => device?.GroupItemId == groupId);
    }

    /// <summary>
    /// Aggiunge un nuovo dispositivo di automazione alla collezione con un GUID generato automaticamente.
    /// </summary>
    /// <returns>Il nuovo dispositivo di automazione creato e aggiunto alla collezione.</returns>
    public AutomationDevice Add()
    {
        var device = new AutomationDevice();

        if (!_items.TryAdd(device.ItemGuid, device))
        {
            // Guid gia' presente: il dispositivo appena costruito non entra, e va rilasciato perche'
            // il costruttore lo ha gia' iscritto a Navigator.StatusChanged.
            device.Dispose();
            return _items[device.ItemGuid]!;
        }

        device.Load();
        device.Start();

        return device;
    }

    /// <summary>
    /// Aggiunge o ottiene un dispositivo di automazione con il GUID specificato.
    /// </summary>
    /// <param name="guid">Il GUID del dispositivo da aggiungere o ottenere.</param>
    /// <returns>Il dispositivo di automazione associato al GUID specificato.</returns>
    public AutomationDevice? Add(Guid guid)
    {
        if (_items.TryGetValue(guid, out var existing)) return existing;

        // Non si usa piu' GetOrAdd con una funzione di fabbrica: quella funzione puo' essere
        // invocata piu' volte in caso di concorrenza e i perdenti vengono scartati in silenzio.
        // Finche' costruire un dispositivo faceva anche una query e avviava dei cicli, ogni
        // invocazione scartata lasciava dietro di se' un dispositivo vivo. Si veda la voce D62.
        var created = new AutomationDevice(guid);

        // Si carica PRIMA di inserire: cosi' il dispositivo e' gia' configurato quando diventa
        // visibile agli altri thread. Costruire, caricare e avviare sono tre fasi distinte.
        created.Load();

        var device = _items.GetOrAdd(guid, created);

        if (!ReferenceEquals(device, created))
        {
            // Corsa persa: il dispositivo costruito qui non e' entrato nella collezione.
            created.Dispose();
            return device;
        }

        // Avvia solo chi ha davvero inserito il dispositivo, e a configurazione completa.
        created.Start();

        return device;
    }

    /// <summary>
    /// Aggiunge un dispositivo di automazione utilizzando un oggetto generico come chiave.
    /// </summary>
    /// <param name="value">L'oggetto da utilizzare come chiave (deve essere un GUID).</param>
    /// <returns>Il dispositivo aggiunto o ottenuto, o null se il valore non è un GUID valido.</returns>
    public AutomationDevice? Add(object? value)
    {
        if (value is not Guid guid) return null;
        return Add(guid);
    }

    /// <summary>
    /// Aggiunge un dispositivo di automazione esistente alla collezione.
    /// </summary>
    /// <param name="value">Il dispositivo di automazione da aggiungere.</param>
    public void Add(AutomationDevice? value)
    {
        if (value != null) _items.TryAdd(value.ItemGuid, value);
    }

    /// <summary>
    /// Aggiunge una collezione di dispositivi di automazione alla collezione.
    /// </summary>
    /// <param name="values">La collezione di dispositivi da aggiungere.</param>
    public void AddRange(IEnumerable<AutomationDevice?> values)
    {
        foreach (var value in values) Add(value);
    }

    /// <summary>
    /// Determina se almeno un dispositivo nella collezione soddisfa la condizione specificata.
    /// </summary>
    /// <param name="match">La funzione che definisce la condizione da verificare per ogni dispositivo.</param>
    /// <returns>true se almeno un dispositivo soddisfa la condizione; altrimenti, false.</returns>
    public bool Any(Func<AutomationDevice?, bool> match)
    {
        return _items.Values.Any(match);
    }

    /// <summary>
    /// Determina se la collezione contiene almeno un dispositivo.
    /// </summary>
    /// <returns>true se la collezione non è vuota; altrimenti, false.</returns>
    public bool Any()
    {
        return !_items.IsEmpty;
    }

    /// <summary>
    /// Rimuove tutti i dispositivi dalla collezione.
    /// </summary>
    public void Clear()
    {
        // Si prende l'elenco prima di svuotare, perche' i dispositivi scartati vanno RILASCIATI.
        // Ognuno e' iscritto all'evento statico Navigator.StatusChanged e tiene vivi i propri cicli
        // di sfondo: senza Dispose restavano agganciati all'evento e continuavano a girare, e ogni
        // ricarica a caldo del diagramma dall'Editor aggiungeva un giro intero di gestori,
        // moltiplicando il fan-out di B26. Dispose e' idempotente, quindi rilasciare due volte lo
        // stesso dispositivo non fa danno. Si veda la voce B25 del registro in
        // WCS/doc/debito-tecnico.md.
        var scartati = _items.Values.OfType<AutomationDevice>().ToArray();

        _items.Clear();

        foreach (var device in scartati)
            device.Dispose();
    }

    /// <summary>
    /// Determina se la collezione contiene il dispositivo specificato.
    /// </summary>
    /// <param name="item">Il dispositivo da cercare nella collezione.</param>
    /// <returns>true se il dispositivo è presente nella collezione; altrimenti, false.</returns>
    public bool Contains(AutomationDevice item)
    {
        return _items.ContainsKey(item.ItemGuid);
    }

    /// <summary>
    /// Cerca il primo dispositivo che soddisfa la condizione specificata.
    /// </summary>
    /// <param name="match">Il predicato che definisce la condizione da verificare per ogni dispositivo.</param>
    /// <returns>Il primo dispositivo che soddisfa la condizione, o null se nessun dispositivo la soddisfa.</returns>
    public AutomationDevice? Find(Predicate<AutomationDevice?> match)
    {
        return _items.Values.FirstOrDefault(value => match(value));
    }

    /// <summary>
    /// Cerca l'indice del primo dispositivo che soddisfa la condizione specificata.
    /// </summary>
    /// <param name="match">Il predicato che definisce la condizione da verificare per ogni dispositivo.</param>
    /// <returns>L'indice del primo dispositivo che soddisfa la condizione, o -1 se nessun dispositivo la soddisfa.</returns>
    public int FindIndex(Predicate<AutomationDevice?> match)
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
    /// Restituisce il primo dispositivo che soddisfa la condizione specificata.
    /// </summary>
    /// <param name="match">La funzione che definisce la condizione da verificare per ogni dispositivo.</param>
    /// <returns>Il primo dispositivo che soddisfa la condizione.</returns>
    /// <exception cref="InvalidOperationException">Lanciata quando nessun dispositivo soddisfa la condizione.</exception>
    public AutomationDevice? First(Func<AutomationDevice?, bool> match)
    {
        foreach (var value in _items.Values)
        {
            if (match(value)) return value;
        }
        throw new InvalidOperationException("Sequence contains no matching element");
    }

    /// <summary>
    /// Restituisce il primo dispositivo che soddisfa la condizione specificata, o null se nessun dispositivo la soddisfa.
    /// </summary>
    /// <param name="match">La funzione che definisce la condizione da verificare per ogni dispositivo.</param>
    /// <returns>Il primo dispositivo che soddisfa la condizione, o null se nessun dispositivo la soddisfa.</returns>
    public AutomationDevice? FirstOrDefault(Func<AutomationDevice?, bool> match)
    {
        return _items.Values.FirstOrDefault(match);
    }

    /// <summary>
    /// Restituisce il primo dispositivo nella collezione, o null se la collezione è vuota.
    /// </summary>
    /// <returns>Il primo dispositivo nella collezione, o null se la collezione è vuota.</returns>
    public AutomationDevice? FirstOrDefault()
    {
        foreach (var value in _items.Values)
        {
            return value;
        }
        return null;
    }

    /// <summary>
    /// Rimuove il dispositivo con il GUID specificato dalla collezione e dal database.
    /// </summary>
    /// <param name="guid">Il GUID del dispositivo da rimuovere.</param>
    /// <returns>true se il dispositivo è stato rimosso con successo; altrimenti, false.</returns>
    public bool Remove(Guid guid)
    {
        var result = _items.TryRemove(guid, out var removed);
        if (!result) return false;
        ORM.AutomationDevice.Delete(guid);
        // Come in Clear: il dispositivo tolto va rilasciato, altrimenti resta iscritto a
        // Navigator.StatusChanged con i suoi cicli di sfondo, e al prossimo Run riceve
        // ExecuteStep come se fosse ancora nell'impianto (un fantasma sullo stesso PLC).
        (removed as AutomationDevice)?.Dispose();
        return true;
    }

    /// <summary>
    /// Rimuove un dispositivo dalla collezione utilizzando un oggetto generico come chiave.
    /// </summary>
    /// <param name="value">L'oggetto da utilizzare come chiave (deve essere un GUID).</param>
    /// <returns>true se il dispositivo è stato rimosso con successo; altrimenti, false.</returns>
    public bool Remove(object value)
    {
        return value is Guid guid && Remove(guid);
    }

    /// <summary>
    /// Filtra i dispositivi nella collezione in base alla condizione specificata.
    /// </summary>
    /// <param name="match">La funzione che definisce la condizione da verificare per ogni dispositivo.</param>
    /// <returns>Una collezione di dispositivi che soddisfano la condizione specificata.</returns>
    public IEnumerable<AutomationDevice?> Where(Func<AutomationDevice?, bool> match)
    {
        return _items.Values.Where(match).ToList();
    }

    #endregion Public Methods
}