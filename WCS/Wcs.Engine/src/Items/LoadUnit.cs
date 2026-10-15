using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Wcs.Engine.Items;

/// <summary>
/// Rappresenta un'unità di carico (Load Unit) che gestisce le operazioni di caricamento ed estrazione 
/// con supporto per la notifica di modifiche delle proprietà e persistenza automatica nel database.
/// </summary>
public class LoadUnit
{

    #region Private Fields

    private readonly Guid _missionId;

    private decimal _cellId;

    private DateTime _date = DateTime.Now;

    private string? _luCode = string.Empty;

    private decimal _luExtractionHeaderId;

    private decimal _luId;

    private decimal _luProcessId;

    private decimal _noteHeaderId;

    private decimal _processId;

    private string? _resultDescription = string.Empty;

    private LoadUnitTypes _type;

    #endregion Private Fields

    #region Public Constructors

    /// <summary>
    /// Inizializza una nuova istanza della classe LoadUnit.
    /// </summary>
    public LoadUnit()
    {
    }

    /// <summary>
    /// Inizializza una nuova istanza della classe LoadUnit con l'ID specificato.
    /// </summary>
    /// <param name="value">L'ID dell'unità di carico.</param>
    public LoadUnit(decimal value)
    {
        Id = value;
    }

    /// <summary>
    /// Inizializza una nuova istanza della classe LoadUnit caricando i dati dal database 
    /// utilizzando l'ID della missione specificato.
    /// </summary>
    /// <param name="valMissionId">L'ID della missione per caricare i dati dal database.</param>
    public LoadUnit(Guid valMissionId)
    {
        // Il flag va alzato prima della chiave: anche il suo setter scrive, e scriverebbe una riga
        // vuota sopra quella della missione prima ancora di rileggerla.
        RecorevingDataFromDb = true;
        MissionId = valMissionId;
        ORM.LoadUnit.Get(this);
        RecorevingDataFromDb = false;
    }

    #endregion Public Constructors

    #region Public Events

    /// <summary>
    /// Evento che viene generato quando una proprietà dell'oggetto cambia.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    #endregion Public Events

    #region Public Enums

    /// <summary>
    /// Enumera i tipi di unità di carico disponibili.
    /// </summary>
    public enum LoadUnitTypes : byte
    {
        /// <summary>
        /// Unità di carico in fase di caricamento.
        /// </summary>
        LuLoading = 1,
        /// <summary>
        /// Unità di carico in fase di estrazione.
        /// </summary>
        LuExtraction = 2,
        /// <summary>
        /// Unità di carico estratta tramite assegnazione.
        /// </summary>
        LuExtractionByAssignment = 3,
        /// <summary>
        /// Unità di carico caricata e respinta.
        /// </summary>
        LuLoadedAndRejected = 4
    }

    #endregion Public Enums

    #region Public Properties

    /// <summary>
    /// Ottiene o imposta l'ID della cella associata all'unità di carico.
    /// </summary>
    public decimal CellId
    {
        get => _cellId;
        set => SetProperty(ref _cellId, value, true);
    }

    /// <summary>
    /// Ottiene o imposta la data e ora dell'unità di carico.
    /// </summary>
    public DateTime Date
    {
        get => _date;
        set => SetProperty(ref _date, value, true);
    }

    /// <summary>
    /// Ottiene l'identificatore univoco dell'unità di carico.
    /// </summary>
    public decimal Id { get; private set; }

    /// <summary>
    /// Ottiene o imposta il codice dell'unità di carico.
    /// </summary>
    public string? LuCode
    {
        get => _luCode;
        set => SetProperty(ref _luCode, value, true);
    }

    /// <summary>
    /// Ottiene o imposta l'ID dell'header di estrazione dell'unità di carico.
    /// </summary>
    public decimal LuExtractionHeaderId
    {
        get => _luExtractionHeaderId;
        set => SetProperty(ref _luExtractionHeaderId, value, true);
    }

    /// <summary>
    /// Ottiene o imposta l'ID dell'unità di carico.
    /// </summary>
    public decimal LuId
    {
        get => _luId;
        set => SetProperty(ref _luId, value, true);
    }

    /// <summary>
    /// Ottiene o imposta l'ID del processo dell'unità di carico.
    /// </summary>
    public decimal LuProcessId
    {
        get => _luProcessId;
        set => SetProperty(ref _luProcessId, value, true);
    }

    /// <summary>
    /// Ottiene l'ID della missione associata all'unità di carico.
    /// </summary>
    public Guid MissionId
    {
        get => _missionId;
        private init => SetProperty(ref _missionId, value, true);
    }

    /// <summary>
    /// Ottiene o imposta l'ID dell'header delle note.
    /// </summary>
    public decimal NoteHeaderId
    {
        get => _noteHeaderId;
        set => SetProperty(ref _noteHeaderId, value, true);
    }

    /// <summary>
    /// Ottiene o imposta l'ID del processo.
    /// </summary>
    public decimal ProcessId
    {
        get => _processId;
        set => SetProperty(ref _processId, value, true);
    }

    /// <summary>
    /// Ottiene o imposta un valore che indica se i dati vengono recuperati dal database.
    /// </summary>
    public bool RecorevingDataFromDb { get; set; }

    /// <summary>
    /// Ottiene o imposta la descrizione del risultato dell'operazione.
    /// </summary>
    public string? ResultDescription
    {
        get => _resultDescription;
        set => SetProperty(ref _resultDescription, value, true);
    }

    /// <summary>
    /// Ottiene o imposta il tipo di unità di carico.
    /// </summary>
    public LoadUnitTypes Type
    {
        get => _type;
        set => SetProperty(ref _type, value, true);
    }

    #endregion Public Properties

    #region Public Methods

    /// <summary>
    /// Imposta l'ID dell'unità di carico.
    /// </summary>
    /// <param name="value">Il nuovo valore dell'ID.</param>
    public void SetId(decimal value)
    {
        Id = value;
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// Imposta il valore di una proprietà, aggiorna il database se necessario e notifica la modifica.
    /// </summary>
    /// <typeparam name="T">Il tipo della proprietà.</typeparam>
    /// <param name="field">Riferimento al campo da aggiornare.</param>
    /// <param name="value">Il nuovo valore da assegnare.</param>
    /// <param name="updateDb">Indica se aggiornare il database.</param>
    /// <param name="propertyName">Il nome della proprietà (fornito automaticamente dal compilatore).</param>
    private void SetProperty<T>(ref T field, T value, bool updateDb,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        if (updateDb) ORM.LoadUnit.Update(this);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    #endregion Private Methods

}