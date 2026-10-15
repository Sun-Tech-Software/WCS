using System.Collections.Concurrent;
using Wcs.Engine.Items;
using Log.Interface;

namespace Wcs.Engine.Collections;

/// <summary>
/// Gestisce una collezione thread-safe di unità di carico (LoadUnit) utilizzando il pattern Singleton.
/// Fornisce operazioni per aggiungere, rimuovere e accedere alle unità di carico tramite il loro ID missione.
/// </summary>
public sealed class ManagedLu
{
    #region Private Fields

    /// <summary>
    /// Istanza lazy del singleton per garantire l'inizializzazione thread-safe.
    /// </summary>
    private static readonly Lazy<ManagedLu> LazyManagedLu = new(() => new ManagedLu());

    #endregion Private Fields

    #region Private Constructors

    /// <summary>
    /// Inizializza una nuova istanza della classe ManagedLu.
    /// Costruttore privato per implementare il pattern Singleton.
    /// </summary>
    private ManagedLu()
    {
        Items = new ConcurrentDictionary<Guid, LoadUnit>();
    }

    
    #endregion Private Constructors

    #region Public Properties

    /// <summary>
    /// Ottiene l'istanza singleton di ManagedLu.
    /// </summary>
    /// <value>L'istanza unica di ManagedLu.</value>
    public static ManagedLu Instance => LazyManagedLu.Value;

    /// <summary>
    /// Ottiene un array contenente tutte le unità di carico gestite.
    /// </summary>
    /// <value>Un array di LoadUnit contenente tutte le unità di carico presenti nella collezione.</value>
    public LoadUnit[] List => Items.Values.ToArray();

    #endregion Public Properties

    #region Private Properties

    /// <summary>
    /// Dizionario thread-safe che contiene le unità di carico indicizzate per ID missione.
    /// </summary>
    private ConcurrentDictionary<Guid, LoadUnit> Items { get; }

    #endregion Private Properties

    #region Public Indexers

    /// <summary>
    /// Ottiene o imposta un'unità di carico utilizzando l'ID della missione come chiave.
    /// </summary>
    /// <param name="missionId">L'ID della missione utilizzato come chiave.</param>
    /// <returns>L'unità di carico associata all'ID della missione, o null se non trovata.</returns>
    public LoadUnit? this[Guid missionId]
    {
        get => Items.GetValueOrDefault(missionId);
        set
        {
            if (value != null) Items[missionId] = value;
        }
    }

    #endregion Public Indexers

    #region Public Methods

    /// <summary>
    /// Aggiunge o aggiorna un'unità di carico nella collezione e nel database.
    /// </summary>
    /// <param name="value">L'unità di carico da aggiungere o aggiornare.</param>
    public void Add(LoadUnit value)
    {
        Items[value.MissionId] = value;
        ORM.LoadUnit.Update(value);
    }

    /// <summary>
    /// Verifica se l'unità di carico specificata è presente nella collezione.
    /// </summary>
    /// <param name="value">L'unità di carico da verificare.</param>
    /// <returns>true se l'unità di carico è presente nella collezione; altrimenti, false.</returns>
    public bool Contains(LoadUnit value)
    {
        return Items.ContainsKey(value.MissionId);
    }

    /// <summary>
    /// Rimuove l'unità di carico specificata dalla collezione e dal database.
    /// </summary>
    /// <param name="value">L'unità di carico da rimuovere.</param>
    public void Remove(LoadUnit value)
    {
        try
        {
            Items.TryRemove(value.MissionId, out _);
            ORM.LoadUnit.Remove(value);
        }
        catch (Exception ex)
        {
            Navigator.WriteLog($"Error On ManagedLU->Remove Description: {ex.Message}\r\nStackTrace: {ex.StackTrace}",
                LogEntryType.Warning);
        }
    }

    /// <summary>
    /// Rimuove l'unità di carico all'indice specificato dalla collezione.
    /// </summary>
    /// <param name="index">L'indice dell'unità di carico da rimuovere.</param>
    /// <remarks>Se l'indice è negativo o fuori intervallo, l'operazione viene ignorata.</remarks>
    public void Remove(int index)
    {
        if (index < 0)
            return;

        var itemsArray = Items.ToArray();
        if (index < itemsArray.Length)
        {
            Remove(itemsArray[index].Value);
        }
    }

    public void Clear()
    {
        Items.Clear();
    }
    
    #endregion Public Methods
}