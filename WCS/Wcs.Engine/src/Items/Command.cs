using System.ComponentModel;
using System.Runtime.CompilerServices;
using Wcs.Engine.ORM;

namespace Wcs.Engine.Items;

/// <summary>
/// Rappresenta un comando del sistema di automazione che gestisce le operazioni
/// sui dispositivi, unità di carico e processi di magazzino automatico.
/// Implementa l'interfaccia INotifyPropertyChanged per supportare il data binding.
/// </summary>
public class Command
{

    #region Private Fields

    private decimal _bufferCellId;
    private CommandTypes _commandType;
    private DateTime _date = DateTime.Now;
    private string? _errorDescription = string.Empty;
    private string? _info = string.Empty;
    private Guid _itemCommandId;
    private Guid _itemDestinationId = Guid.Empty;

    /// <summary>
    /// Vero mentre il comando viene ricostruito da una riga gia' presente in tabella: finche' resta
    /// vero i setter non persistono nulla. E' volatile perche' l'oggetto viene poi letto da altri
    /// thread, anche se solo dopo <see cref="EndLoadFromDatabase"/>.
    /// </summary>
    private volatile bool _loadingFromDatabase;

    private decimal _luId;
    private decimal _luTypeId;
    private string? _pickUpDoorCode = string.Empty;
    private decimal _processId;
    private string? _referenceId = string.Empty;
    private StatusTypes _status;
    private string? _storageDoorCode = string.Empty;
    private string? _workstationId = string.Empty;

    #endregion Private Fields

    #region Public Events

    /// <summary>
    /// Si verifica quando il valore di una proprietà cambia.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    #endregion Public Events

    #region Public Enums

    /// <summary>
    /// Definisce i tipi di comando disponibili nel sistema di automazione.
    /// </summary>
    public enum CommandTypes
    {
        /// <summary>Avvia il navigatore del sistema</summary>
        SetNavigatorRun = 1001,
        /// <summary>Ferma il navigatore del sistema</summary>
        SetNavigatorStop = 1002,
        /// <summary>Ricarica il diagramma di configurazione</summary>
        ReloadDiagram = 1003,
        /// <summary>Rimuove tutte le unità di carico dal sistema</summary>
        RemoveAllLu = 1004,
        /// <summary>Imposta tutti gli elementi in stato pronto</summary>
        SetStatusReadyAllItems = 1005,
        /// <summary>Esegue i passi di processo</summary>
        ExecuteSteps = 1007,
        /// <summary>Abilita la deframmentazione</summary>
        SetDefragmentationEnabled = 1008,
        /// <summary>Disabilita la deframmentazione</summary>
        SetDefragmentationDisabled = 1009,
        /// <summary>Rotta di prova dall'Editor: una UDC senza codice dalla sorgente alla destinazione</summary>
        TestRoute = 1010,
        /// <summary>Esegue un passo specifico per un elemento</summary>
        ItemExecuteStep = 2001,
        /// <summary>Imposta un elemento offline</summary>
        ItemSetOffLine = 2002,
        /// <summary>Imposta un elemento online</summary>
        ItemSetOnLine = 2003,
        /// <summary>Scarica un'unità di carico da un elemento</summary>
        ItemUnloadLu = 2004,
        /// <summary>Segna un passo come completato</summary>
        ItemStepDone = 2007,
        /// <summary>Rifiuta un'unità di carico</summary>
        ItemRejectLu = 2008,
        /// <summary>Trova una posizione di stoccaggio</summary>
        ItemFindStoringLocation = 2009,
        /// <summary>Movimento automatico di un'unità di carico</summary>
        MoveLuAutomatic = 3001,
        /// <summary>Carica un'unità di carico</summary>
        LoadLu = 3002,
        /// <summary>Movimento manuale di un'unità di carico</summary>
        MoveLuManual = 3003,
        /// <summary>Svuota logicamente una cella</summary>
        EmptyCellLogically = 3004,
        /// <summary>Svuota fisicamente una cella</summary>
        EmptyCellPisically = 3005,
        /// <summary>Cambia la posizione di stoccaggio delle unità di carico</summary>
        ChangeLUsStoreLocation = 3006,
        /// <summary>Sposta un'unità di carico verso un elemento specifico</summary>
        MoveLuToItem = 3007,
        /// <summary>Movimento manuale per riferimento</summary>
        MoveReferenceManual = 3008,
        /// <summary>Elimina un'unità di carico dalla coda di estrazione</summary>
        DeleteLuFromExtractionQueue = 4001,

        /// <summary>Traccia dell'operatore: arriva già conclusa, il motore non la esegue</summary>
        ModifyRushMoverStatus = 4002,
        /// <summary>Traccia dell'operatore: arriva già conclusa, il motore non la esegue</summary>
        ChangeCellLockStatus = 4004,
        /// <summary>Traccia dell'operatore: arriva già conclusa, il motore non la esegue</summary>
        AddOrModifyLuPosition = 4005,
        /// <summary>Traccia dell'operatore: arriva già conclusa, il motore non la esegue</summary>
        ModifyDatabaseCellContents = 4006
    }

    /// <summary>
    /// Definisce gli stati possibili di un comando.
    /// </summary>
    public enum StatusTypes : byte
    {
        /// <summary>Comando da eseguire</summary>
        ToDo = 0,
        /// <summary>Comando in esecuzione</summary>
        Running = 1,
        /// <summary>Comando completato con successo</summary>
        Done = 2,
        /// <summary>Comando completato con avvisi</summary>
        Warning = 3,
        /// <summary>Comando completato con errore</summary>
        Error = 99
    }

    #endregion Public Enums

    #region Constructors

    /// <summary>
    /// Costruisce un comando nuovo: ogni assegnazione di proprieta' viene persistita, ed e' proprio
    /// cosi' che i comandi creati dal motore finiscono in tabella.
    /// </summary>
    public Command()
    {
    }

    /// <summary>
    /// Costruisce un comando in fase di caricamento dal database. Finche' non viene chiamata
    /// <see cref="EndLoadFromDatabase"/> i setter cambiano il valore e notificano il binding, ma non
    /// riscrivono la riga da cui il valore proviene: quella scrittura sarebbe pura perdita.
    /// </summary>
    /// <param name="loadingFromDatabase">Vero se il comando sta materializzando una riga letta</param>
    internal Command(bool loadingFromDatabase) => _loadingFromDatabase = loadingFromDatabase;

    #endregion Constructors

    #region Public Properties

    /// <summary>
    /// Ottiene o imposta l'identificatore della cella buffer associata al comando.
    /// </summary>
    /// <value>L'identificatore numerico della cella buffer</value>
    public decimal BufferCellId
    {
        get => _bufferCellId;
        set => SetProperty(ref _bufferCellId, value, true);
    }

    /// <summary>
    /// Ottiene o imposta il tipo di comando da eseguire.
    /// </summary>
    /// <value>Il tipo di comando dalla enumerazione <see cref="CommandTypes"/></value>
    public CommandTypes CommandType
    {
        get => _commandType;
        set => SetProperty(ref _commandType, value, true);
    }

    /// <summary>
    /// Ottiene o imposta la data e ora di creazione del comando.
    /// </summary>
    /// <value>La data e ora del comando, inizializzata al momento corrente</value>
    public DateTime Date
    {
        get => _date;
        set => SetProperty(ref _date, value, true);
    }

    /// <summary>
    /// Ottiene o imposta la descrizione dell'errore in caso di fallimento del comando.
    /// </summary>
    /// <value>La descrizione testuale dell'errore, o stringa vuota se non ci sono errori</value>
    public string? ErrorDescription
    {
        get => _errorDescription;
        set => SetProperty(ref _errorDescription, value, true);
    }

    /// <summary>
    /// Ottiene o imposta l'identificatore univoco del comando da eseguire.
    /// </summary>
    /// <value>L'identificatore numerico del comando</value>
    public decimal IdCommandToDo { get; set; }

    /// <summary>
    /// Ottiene o imposta le informazioni aggiuntive associate al comando.
    /// </summary>
    /// <value>Informazioni testuali supplementari sul comando</value>
    public string? Info
    {
        get => _info;
        set => SetProperty(ref _info, value, true);
    }

    /// <summary>
    /// Ottiene o imposta l'identificatore univoco dell'elemento che deve eseguire il comando.
    /// </summary>
    /// <value>L'identificatore GUID dell'elemento target</value>
    public Guid ItemCommandId
    {
        get => _itemCommandId;
        set => SetProperty(ref _itemCommandId, value, true);
    }

    /// <summary>
    /// Ottiene o imposta l'identificatore univoco dell'elemento di destinazione.
    /// </summary>
    /// <value>L'identificatore GUID dell'elemento di destinazione</value>
    public Guid ItemDestinationId
    {
        get => _itemDestinationId;
        set => SetProperty(ref _itemDestinationId, value, true);
    }

    /// <summary>
    /// Ottiene o imposta l'identificatore dell'unità di carico coinvolta nel comando.
    /// </summary>
    /// <value>L'identificatore numerico dell'unità di carico</value>
    public decimal LuId
    {
        get => _luId;
        set => SetProperty(ref _luId, value, true);
    }

    /// <summary>
    /// Ottiene o imposta l'identificatore del tipo di unità di carico.
    /// </summary>
    /// <value>L'identificatore numerico del tipo di unità di carico</value>
    public decimal LuTypeId
    {
        get => _luTypeId;
        set => SetProperty(ref _luTypeId, value, true);
    }

    /// <summary>
    /// Ottiene o imposta il codice della porta di prelievo per operazioni di carico/scarico.
    /// </summary>
    /// <value>Il codice testuale della porta di prelievo</value>
    public string? PickUpDoorCode
    {
        get => _pickUpDoorCode;
        set => SetProperty(ref _pickUpDoorCode, value, true);
    }

    /// <summary>
    /// Ottiene o imposta l'identificatore del processo associato al comando.
    /// </summary>
    /// <value>L'identificatore numerico del processo</value>
    public decimal ProcessId
    {
        get => _processId;
        set => SetProperty(ref _processId, value, true);
    }

    /// <summary>
    /// Ottiene o imposta l'identificatore di riferimento esterno del comando.
    /// </summary>
    /// <value>L'identificatore testuale di riferimento</value>
    public string? ReferenceId
    {
        get => _referenceId;
        set => SetProperty(ref _referenceId, value, true);
    }

    /// <summary>
    /// Ottiene o imposta lo stato attuale del comando.
    /// </summary>
    /// <value>Lo stato del comando dalla enumerazione <see cref="StatusTypes"/></value>
    public StatusTypes Status
    {
        get => _status;
        set => SetProperty(ref _status, value, true);
    }

    /// <summary>
    /// Ottiene o imposta il codice della porta di stoccaggio per operazioni di magazzino.
    /// </summary>
    /// <value>Il codice testuale della porta di stoccaggio</value>
    public string? StorageDoorCode
    {
        get => _storageDoorCode;
        set => SetProperty(ref _storageDoorCode, value, true);
    }

    /// <summary>
    /// Ottiene o imposta l'identificatore della stazione di lavoro associata al comando.
    /// </summary>
    /// <value>L'identificatore testuale della stazione di lavoro</value>
    public string? WorkstationId
    {
        get => _workstationId;
        set => SetProperty(ref _workstationId, value, true);
    }

    #endregion Public Properties

    #region Internal Methods

    /// <summary>
    /// Chiude il caricamento dal database: da qui in poi ogni modifica del comando torna a essere
    /// persistita, come per un comando costruito dal motore. Va chiamata prima che l'oggetto sia
    /// visibile ad altri.
    /// </summary>
    internal void EndLoadFromDatabase() => _loadingFromDatabase = false;

    #endregion Internal Methods

    #region Private Methods

    /// <summary>
    /// Imposta il valore di una proprietà e notifica il cambiamento, con opzione di aggiornamento del database.
    /// </summary>
    /// <typeparam name="T">Il tipo della proprietà</typeparam>
    /// <param name="field">Il campo di backing della proprietà</param>
    /// <param name="value">Il nuovo valore da impostare</param>
    /// <param name="updateDb">Indica se aggiornare il database dopo la modifica</param>
    /// <param name="propertyName">Il nome della proprietà (fornito automaticamente dal compilatore)</param>
    private void SetProperty<T>(ref T field, T value, bool updateDb,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        // Durante il caricamento il valore arriva dalla riga stessa: riscriverlo sarebbe rimandare
        // al database quello che il database ha appena dato. La notifica al binding resta.
        //
        // Senza chiave non si scrive: chi costruisce un comando ne imposta otto o nove proprieta'
        // di fila, e i Task.Run partivano tutti prima che il primo finisse, quindi ognuno prendeva
        // l'istantanea con IdCommandToDo == 0 e CommandStore.UpsertAsync la inseriva come riga
        // nuova - nove righe per un comando solo, e nove esecuzioni alla ripresa. La prima
        // scrittura la fa chi lo costruisce, che chiama CommandToDo.Update alla fine; da li' in
        // poi il comando ha la chiave e i setter aggiornano la sua riga.
        if (updateDb && !_loadingFromDatabase && IdCommandToDo != 0)
            CommandToDo.QueueUpdate(this);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    #endregion Private Methods

}
