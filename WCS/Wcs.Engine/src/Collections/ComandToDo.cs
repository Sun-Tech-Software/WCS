using System.Collections.Concurrent;
using Wcs.Common;
using Wcs.Engine.Items;
using Wcs.Engine.ORM;
using Log.Interface;

namespace Wcs.Engine.Collections;

/// <summary>
/// Gestisce l'esecuzione sequenziale dei comandi del sistema di automazione utilizzando il pattern Singleton.
/// Fornisce un meccanismo thread-safe per l'elaborazione di comandi da database in modalità FIFO.
/// </summary>
public sealed class CommandToDo
{
    #region Private Fields

    /// <summary>
    /// Implementazione lazy del pattern Singleton per garantire istanza unica thread-safe.
    /// </summary>
    private static readonly Lazy<CommandToDo> LazyCommandToDo = new(() => new CommandToDo());

    /// <summary>
    /// Flag volatile per prevenire esecuzioni concorrenti del metodo Execute.
    /// Utilizza volatile per garantire visibilità immediata tra thread.
    /// </summary>
    private volatile bool _executeProcessRunning;

    /// <summary>
    /// Timestamp dell'ultimo comando ExecuteSteps per implementare throttling di 10 secondi.
    /// </summary>
    private DateTime _lastCommandExecuteSteps;

    #endregion Private Fields

    #region Private Constructors

    /// <summary>
    /// Costruttore privato per implementare il pattern Singleton.
    /// Inizializza la coda concorrente per la gestione thread-safe dei comandi.
    /// </summary>
    private CommandToDo()
    {
        Items = new ConcurrentQueue<Command>();
    }

    #endregion Private Constructors

    #region Public Properties

    /// <summary>
    /// Ottiene l'istanza singleton di CommandToDo.
    /// Garantisce un unico punto di accesso globale al gestore comandi.
    /// </summary>
    /// <value>L'istanza singleton di CommandToDo</value>
    public static CommandToDo Instance => LazyCommandToDo.Value;

    /// <summary>
    /// Ottiene una lista snapshot di tutti i comandi attualmente in coda.
    /// Crea una copia thread-safe della collezione al momento della chiamata.
    /// </summary>
    /// <value>Lista immutabile dei comandi in coda</value>
    public List<Command> List => Items.ToList();

    #endregion Public Properties

    #region Public Methods

    /// <summary>
    /// Svuota la coda dei comandi.
    /// </summary>
    /// <remarks>
    /// Esiste perche' <c>List</c> restituisce una copia: <c>Instance.List.Clear()</c> svuota la
    /// copia e la butta via. Si veda la voce <c>B1</c> del registro in
    /// <c>WCS/doc/debito-tecnico.md</c>.
    /// </remarks>
    public void Clear()
    {
        Items.Clear();
    }

    #endregion Public Methods

    #region Private Properties

    /// <summary>
    /// Coda concorrente thread-safe per la gestione FIFO dei comandi.
    /// Utilizzata internamente per memorizzare i comandi da elaborare.
    /// </summary>
    private ConcurrentQueue<Command> Items { get; }

    #endregion Private Properties

    #region Public Indexers

    /// <summary>
    /// Ottiene un comando specifico tramite il suo identificatore univoco.
    /// Esegue una ricerca lineare sulla snapshot corrente della coda.
    /// </summary>
    /// <param name="key">L'identificatore univoco del comando da cercare</param>
    /// <returns>Il comando corrispondente all'identificatore, o null se non trovato</returns>
    public Command? this[decimal key]
    {
        get
        {
            var items = Items.ToArray();
            return items.FirstOrDefault(t => t.IdCommandToDo == key);
        }
    }

    #endregion Public Indexers

    #region Internal Methods

    /// <summary>
    /// Esegue tutti i comandi pendenti caricati dal database in modalità thread-safe.
    /// Implementa un meccanismo di guard per prevenire esecuzioni concorrenti.
    /// Ogni comando viene elaborato sequenzialmente secondo il suo tipo specifico.
    /// </summary>
    /// <remarks>
    /// Il metodo carica i comandi dal database, li elabora uno per volta e aggiorna il loro stato.
    /// Include gestione degli errori e logging completo delle operazioni.
    /// Alcuni tipi di comando (Run/Stop/Defrag) sono mutuamente esclusivi per sessione.
    /// </remarks>
    internal async Task Execute()
    {
        if (_executeProcessRunning)
            return;

        try
        {
            _executeProcessRunning = true;
            Items.Clear();
            await ORM.CommandToDo.LoadFromDatabase(Items);

            if (Items.IsEmpty) return;

            bool runCmdExecuted = false, stopCmdExecuted = false,
                defragEnabledCmdExecuted = false, defragDisabledCmdExecuted = false;
            var commands = Items.ToArray();

            foreach (var navigatorCommand in commands)
            {
                if (navigatorCommand.Status != Command.StatusTypes.ToDo)
                    continue;

                // L'identificativo della postazione, non il suo nome esteso: il nome e il dominio
                // stavano in POSTAZIONI, una tabella del gestionale che il motore leggeva in SQL
                // diretto per decorare questa riga di registro. La lettura e' uscita senza
                // sostituto (decisione della specifica «Il motore senza SQL Server»): il WCS
                // scrive l'identificativo, che ha gia'.
                var infoLog = string.Format(CommandMessages.strInfoLog, navigatorCommand.WorkstationId);

                try
                {
                    switch (navigatorCommand.CommandType)
                    {
                        case Command.CommandTypes.SetNavigatorRun:
                            if (runCmdExecuted) continue;
                            runCmdExecuted = true;
                            infoLog = await HandleSetNavigatorRun(navigatorCommand, infoLog);
                            break;

                        case Command.CommandTypes.SetNavigatorStop:
                            if (stopCmdExecuted) continue;
                            stopCmdExecuted = true;
                            infoLog = HandleSetNavigatorStop(navigatorCommand, infoLog);
                            break;

                        case Command.CommandTypes.ReloadDiagram:
                            infoLog = await HandleReloadDiagram(navigatorCommand, infoLog);
                            break;

                        case Command.CommandTypes.RemoveAllLu:
                            infoLog = await HandleRemoveAllLu(navigatorCommand, infoLog);
                            break;

                        case Command.CommandTypes.SetStatusReadyAllItems:
                            infoLog = HandleSetStatusReadyAllItems(navigatorCommand, infoLog);
                            break;

                        case Command.CommandTypes.ExecuteSteps:
                            infoLog = HandleExecuteSteps(navigatorCommand, infoLog);
                            break;

                        case Command.CommandTypes.SetDefragmentationEnabled:
                            if (defragEnabledCmdExecuted) continue;
                            defragEnabledCmdExecuted = true;
                            infoLog = HandleSetDefragmentationEnabled(navigatorCommand, infoLog);
                            break;

                        case Command.CommandTypes.SetDefragmentationDisabled:
                            if (defragDisabledCmdExecuted) continue;
                            defragDisabledCmdExecuted = true;
                            infoLog = HandleSetDefragmentationDisabled(navigatorCommand, infoLog);
                            break;

                        case Command.CommandTypes.ItemStepDone:
                            infoLog = await HandleItemStepDone(navigatorCommand, infoLog);
                            break;

                        case Command.CommandTypes.ItemExecuteStep:
                            infoLog = await HandleItemExecuteStep(navigatorCommand, infoLog);
                            break;

                        case Command.CommandTypes.ItemSetOffLine:
                            infoLog = await HandleItemSetOffLine(navigatorCommand, infoLog);
                            break;

                        case Command.CommandTypes.ItemSetOnLine:
                            infoLog = await HandleItemSetOnLine(navigatorCommand, infoLog);
                            break;

                        case Command.CommandTypes.ItemUnloadLu:
                            infoLog = await HandleItemUnloadLu(navigatorCommand, infoLog);
                            break;

                        case Command.CommandTypes.ItemRejectLu:
                            infoLog = await HandleItemRejectLu(navigatorCommand, infoLog);
                            break;

                        case Command.CommandTypes.ItemFindStoringLocation:
                            infoLog = await HandleItemFindStoringLocation(navigatorCommand, infoLog);
                            break;

                        case Command.CommandTypes.LoadLu:
                            infoLog = await HandleLoadLu(navigatorCommand, infoLog);
                            break;

                        case Command.CommandTypes.TestRoute:
                            infoLog = await HandleTestRoute(navigatorCommand, infoLog);
                            break;

                        case Command.CommandTypes.MoveLuManual:
                            infoLog = await HandleMoveLuManual(navigatorCommand, infoLog);
                            break;

                        case Command.CommandTypes.MoveReferenceManual:
                            await ExecutableCommands.ManageMoveReferenceManual(navigatorCommand, infoLog);
                            break;

                        case Command.CommandTypes.MoveLuToItem:
                            await ExecutableCommands.ManageMoveLuToItem(navigatorCommand, infoLog);
                            break;

                        case Command.CommandTypes.EmptyCellLogically:
                            infoLog = await HandleEmptyCellLogically(navigatorCommand, infoLog);
                            break;

                        case Command.CommandTypes.ChangeLUsStoreLocation:
                            infoLog = await HandleChangeLUsStoreLocation(navigatorCommand, infoLog);
                            break;

                        case Command.CommandTypes.EmptyCellPisically:
                            infoLog = await HandleEmptyCellPhysically(navigatorCommand, infoLog);
                            break;

                        case Command.CommandTypes.MoveLuAutomatic:
                            navigatorCommand.Status = Command.StatusTypes.Done;
                            break;

                        case Command.CommandTypes.DeleteLuFromExtractionQueue:
                            infoLog = await HandleDeleteLuFromExtractionQueue(navigatorCommand, infoLog);
                            break;

                        default:
                            navigatorCommand.Status = Command.StatusTypes.Done;
                            break;
                    }

                    LogCommandExecution(infoLog, navigatorCommand);
                }
                catch (Exception ex)
                {
                    // Un'eccezione in un gestore lasciava il comando «in esecuzione» per sempre, e
                    // l'errore finiva solo nel log di diagnostica, che per impostazione e' spento:
                    // il comando si chiude in errore con il messaggio, cosi' l'operatore lo vede
                    // nella lista dei comandi.
                    navigatorCommand.ErrorDescription = ex.Message;
                    navigatorCommand.Status = Command.StatusTypes.Error;
                    Navigator.WriteLog(
                        $"Error On CommandToDo->Execute ({navigatorCommand.CommandType}) Description: {ex.Message}\r\nStackTrace: {ex.StackTrace}",
                        LogEntryType.Error);
                }
            }
        }
        catch (Exception ex)
        {
            Navigator.WriteLog(
                $"Error On CommandToDo->Execute Description: {ex.Message}\r\nStackTrace: {ex.StackTrace}",
                LogEntryType.Error);
        }
        finally
        {
            _executeProcessRunning = false;
        }
    }

    #endregion Internal Methods

    #region Private Methods

    /// <summary>
    /// Gestisce il comando di cambio posizione di stoccaggio per le unità di carico.
    /// Esegue l'operazione in modalità thread-safe utilizzando il lock di sincronizzazione delle estrazioni.
    /// </summary>
    /// <param name="navigatorCommand">Il comando da elaborare</param>
    /// <param name="infoLog"></param>
    private static async Task<string> HandleChangeLUsStoreLocation(Command navigatorCommand, string infoLog)
    {
        if (!await Navigator.SyncExtraction.WaitAsync(Navigator.SyncExtractionTimeout))
        {
            Navigator.WriteLog("Timeout acquiring extraction lock in HandleChangeLUsStoreLocation", Log.Interface.LogEntryType.Warning);
            navigatorCommand.Status = Command.StatusTypes.Error;
            navigatorCommand.ErrorDescription = "Timeout acquiring extraction lock";
            return infoLog;
        }

        try
        {

            navigatorCommand.Status = Command.StatusTypes.Running;
            var idItem = await DbInfo.GetItemId(navigatorCommand.StorageDoorCode);
            var item = PathDevices.Instance[idItem];

            if (item != null)
            {
                infoLog += " " + string.Format(
                    CommandMessages.strChangeStorageLocation,
                    navigatorCommand.PickUpDoorCode,
                    navigatorCommand.StorageDoorCode);

                await ExecutableCommands.ChangeLUsStoreLocation(navigatorCommand);
            }
            else
            {
                // C38: dispositivo bersaglio non risolto (StorageDoorCode inesistente/rimosso):
                // segnala l'errore invece di chiudere il comando come se fosse andato a buon fine.
                navigatorCommand.ErrorDescription = $"Target device not found for StorageDoorCode '{navigatorCommand.StorageDoorCode}'";
                Navigator.RecordNavigatorLog(null, navigatorCommand.ErrorDescription, LogEntryType.Warning);
                navigatorCommand.Status = Command.StatusTypes.Error;
            }
        }
        finally
        {
            Navigator.SyncExtraction.Release();
        }

        return infoLog;
    }

    /// <summary>
    /// Gestisce l'eliminazione di un'unità di carico dalla coda di estrazione.
    /// Verifica la validità dell'unità di carico e il suo stato prima dell'eliminazione.
    /// </summary>
    /// <param name="navigatorCommand">Il comando contenente l'ID dell'unità di carico da rimuovere</param>
    /// <param name="infoLog">Log delle informazioni da aggiornare (parametro per riferimento)</param>
    private static async Task<string> HandleDeleteLuFromExtractionQueue(Command navigatorCommand, string infoLog)
    {
        navigatorCommand.Status = Command.StatusTypes.Running;
        var luId = navigatorCommand.LuId;

        if (luId == 0)
        {
            navigatorCommand.Status = Command.StatusTypes.Done;
            return infoLog;
        }

        var luCodeExtraction = (await Navigator.RequireWarehouse($"HandleDeleteLuFromExtractionQueue sull'unita' di carico {luId}").DescribeLoadUnitAsync(luId))?.Code;

        if (luCodeExtraction is { Length: 0 })
        {
            navigatorCommand.Status = Command.StatusTypes.Done;
            return infoLog;
        }

        infoLog += " " + string.Format(CommandMessages.strDeleteLuExtractionQueue, luCodeExtraction);
        Navigator.RecordNavigatorLog(null, infoLog, LogEntryType.Information);
        infoLog = string.Empty;

        switch (await DbInfo.GetLuExtractionStatus(luId))
        {
            case -1:
                infoLog += " " + string.Format(CommandMessages.strErrLuNotPresentExtractionQueue, luCodeExtraction);
                break;
            case 1:
                infoLog += " " + string.Format(CommandMessages.strErrLuExtractionQueueProcessed, luCodeExtraction);
                break;
        }

        if (!string.IsNullOrEmpty(infoLog))
        {
            navigatorCommand.ErrorDescription = infoLog;
            navigatorCommand.Status = Command.StatusTypes.Error;
            return infoLog;
        }

        await DbInfo.DeleteLuFromExtractionQueue(luId);
        navigatorCommand.Status = Command.StatusTypes.Done;
        return infoLog;
    }

    /// <summary>
    /// Gestisce lo svuotamento logico di una cella di stoccaggio.
    /// L'operazione viene eseguita in modalità thread-safe e include logging dettagliato.
    /// </summary>
    /// <param name="navigatorCommand">Il comando contenente la cella da svuotare logicamente</param>
    /// <param name="infoLog"></param>
    private static async Task<string> HandleEmptyCellLogically(Command navigatorCommand, string infoLog)
    {
        if (!await Navigator.SyncExtraction.WaitAsync(Navigator.SyncExtractionTimeout))
        {
            Navigator.WriteLog("Timeout acquiring extraction lock in HandleEmptyCellLogically", Log.Interface.LogEntryType.Warning);
            navigatorCommand.Status = Command.StatusTypes.Error;
            navigatorCommand.ErrorDescription = "Timeout acquiring extraction lock";
            return infoLog;
        }

        try
        {

            navigatorCommand.Status = Command.StatusTypes.Running;
            var idItem = await DbInfo.GetItemId(navigatorCommand.StorageDoorCode);
            var item = PathDevices.Instance[idItem];

            if (item != null)
            {
                infoLog +=
                    $" Empty Cell Logically contains LU {(await Navigator.RequireWarehouse($"HandleEmptyCellLogically sull'unita' di carico {navigatorCommand.LuId}").DescribeLoadUnitAsync(navigatorCommand.LuId))?.Code} on Item {item.ASI}.{item.SUBITEM}.{item.PARTITION}";
                await ExecutableCommands.EmptyCellLogically(navigatorCommand, ExecutableCommands.ExecuteMoveLuTypes.Manual);
            }
            else
            {
                // C38: dispositivo bersaglio non risolto (StorageDoorCode inesistente/rimosso):
                // segnala l'errore invece di chiudere il comando come se fosse andato a buon fine.
                navigatorCommand.ErrorDescription = $"Target device not found for StorageDoorCode '{navigatorCommand.StorageDoorCode}'";
                Navigator.RecordNavigatorLog(null, navigatorCommand.ErrorDescription, LogEntryType.Warning);
                navigatorCommand.Status = Command.StatusTypes.Error;
            }
        }
        finally
        {
            Navigator.SyncExtraction.Release();
        }

        return infoLog;
    }

    /// <summary>
    /// Gestisce lo svuotamento fisico di una cella di stoccaggio.
    /// Rimuove fisicamente l'unità di carico dalla posizione specificata.
    /// </summary>
    /// <param name="navigatorCommand">Il comando contenente la cella da svuotare fisicamente</param>
    /// <param name="infoLog"></param>
    private static async Task<string> HandleEmptyCellPhysically(Command navigatorCommand, string infoLog)
    {

        navigatorCommand.Status = Command.StatusTypes.Running;
        var idItemPhysically = await DbInfo.GetItemId(navigatorCommand.StorageDoorCode);
        var item = PathDevices.Instance[idItemPhysically];

        if (item != null)
        {
            infoLog += " " + string.Format(CommandMessages.strPhysicallyEmptyCell, navigatorCommand.StorageDoorCode);
            await ExecutableCommands.EmptyCellPhisically(navigatorCommand, ExecutableCommands.ExecuteMoveLuTypes.Manual);
        }
        else
        {
            // C38: dispositivo bersaglio non risolto (StorageDoorCode inesistente/rimosso):
            // segnala l'errore invece di chiudere il comando come se fosse andato a buon fine.
            navigatorCommand.ErrorDescription = $"Target device not found for StorageDoorCode '{navigatorCommand.StorageDoorCode}'";
            Navigator.RecordNavigatorLog(null, navigatorCommand.ErrorDescription, LogEntryType.Warning);
            navigatorCommand.Status = Command.StatusTypes.Error;
        }

        return infoLog;
    }

    /// <summary>
    /// Gestisce l'esecuzione di un singolo passo di processo per un elemento specifico.
    /// Identifica l'elemento target e delega l'esecuzione al sistema di comandi eseguibili.
    /// </summary>
    /// <param name="navigatorCommand">Il comando contenente l'ID dell'elemento da processare</param>
    /// <param name="infoLog">Log delle informazioni da aggiornare (parametro per riferimento)</param>
    private static async Task<string> HandleItemExecuteStep(Command navigatorCommand, string infoLog)
    {
        navigatorCommand.Status = Command.StatusTypes.Running;
        var item = PathDevices.Instance[navigatorCommand.ItemCommandId];

        if (item != null)
        {
            infoLog += " " + string.Format(CommandMessages.strExecuteStep, item.ASI, item.SUBITEM, item.PARTITION);
            await ExecutableCommands.ItemExecuteStep(navigatorCommand);
        }
        else
        {
            MissingTargetDevice(navigatorCommand);
        }

        // Solo se non e' gia' in errore. Prima si scriveva Done incondizionatamente, e cancellava
        // l'errore che ItemExecuteStep aveva appena impostato -- sia il rifiuto per elemento
        // impegnato, sia il «dispositivo bersaglio non trovato» della voce C38, che risultava
        // quindi riparata solo a meta'.
        if (navigatorCommand.Status != Command.StatusTypes.Error)
            navigatorCommand.Status = Command.StatusTypes.Done;

        return infoLog;
    }

    /// <summary>
    /// Gestisce la ricerca di posizioni di stoccaggio per le unità di carico presenti su un elemento.
    /// Verifica la presenza di unità di carico valide prima di avviare la ricerca.
    /// </summary>
    /// <param name="navigatorCommand">Il comando contenente l'elemento per cui cercare posizioni di stoccaggio</param>
    /// <param name="infoLog">Log delle informazioni da aggiornare (parametro per riferimento)</param>
    private static async Task<string> HandleItemFindStoringLocation(Command navigatorCommand, string infoLog)
    {
        var item = PathDevices.Instance[navigatorCommand.ItemCommandId];

        if (item != null)
        {
            var toFindLocation = false;
            var lusLoaded = item.LUsLoaded;

            foreach (var lu in lusLoaded.Where(lu => lu is not { LuId: 0 }))
            {
                toFindLocation = true;
                var luCode = lu?.LuCode;

                infoLog += string.IsNullOrEmpty(luCode)
                    ? " " + string.Format(CommandMessages.strSearchStorageLuNoCode, item.ASI, item.SUBITEM,
                        item.PARTITION)
                    : " " + string.Format(CommandMessages.strSearchStorageLuWithCode, item.ASI, item.SUBITEM,
                        item.PARTITION, luCode);
            }

            if (toFindLocation)
            {
                await ExecutableCommands.ItemFindStoringLocation(navigatorCommand);
                navigatorCommand.Status = Command.StatusTypes.Done;
            }
            else
            {
                navigatorCommand.ErrorDescription = CommandMessages.strErrSearchStorageLu;
                Navigator.RecordNavigatorLog(item, navigatorCommand.ErrorDescription, LogEntryType.Warning);
                navigatorCommand.Status = Command.StatusTypes.Error;
            }
        }
        else
        {
            MissingTargetDevice(navigatorCommand);
        }

        return infoLog;
    }

    /// <summary>
    /// C38: il dispositivo bersaglio non si risolve (<c>ItemCommandId</c> inesistente o rimosso):
    /// il comando si chiude in errore invece che come riuscito. Prima la riparazione stava in un
    /// solo gestore su sette, e gli altri sei scrivevano <c>Done</c> fuori dall'<c>if</c>: un
    /// comando verso un dispositivo che non esiste risultava eseguito senza aver fatto niente.
    /// </summary>
    private static void MissingTargetDevice(Command navigatorCommand)
    {
        navigatorCommand.ErrorDescription = $"Target device not found for ItemCommandId '{navigatorCommand.ItemCommandId}'";
        Navigator.RecordNavigatorLog(null, navigatorCommand.ErrorDescription, LogEntryType.Warning);
        navigatorCommand.Status = Command.StatusTypes.Error;
    }

    /// <summary>
    /// Gestisce il rifiuto di tutte le unità di carico presenti su un elemento.
    /// Elabora ogni unità di carico caricata sull'elemento specificato.
    /// </summary>
    /// <param name="navigatorCommand">Il comando contenente l'elemento da cui rifiutare le unità di carico</param>
    /// <param name="infoLog">Log delle informazioni da aggiornare (parametro per riferimento)</param>
    private static async Task<string> HandleItemRejectLu(Command navigatorCommand, string infoLog)
    {
        navigatorCommand.Status = Command.StatusTypes.Running;
        var item = PathDevices.Instance[navigatorCommand.ItemCommandId];

        if (item != null)
        {
            var toReject = false;
            var lusLoaded = item.LUsLoaded;

            foreach (var lu in lusLoaded)
            {
                toReject = true;
                var luCode = lu?.LuCode;

                infoLog += string.IsNullOrEmpty(luCode)
                    ? " " + string.Format(CommandMessages.strRejectLuNoCode, item.ASI, item.SUBITEM, item.PARTITION)
                    : " " + string.Format(CommandMessages.strRejectLuWithCode, item.ASI, item.SUBITEM, item.PARTITION,
                        luCode);
            }

            if (toReject)
                await ExecutableCommands.ItemRejectLu(navigatorCommand);
        }
        else
        {
            MissingTargetDevice(navigatorCommand);
        }

        if (navigatorCommand.Status != Command.StatusTypes.Error)
            navigatorCommand.Status = Command.StatusTypes.Done;

        return infoLog;
    }

    /// <summary>
    /// Gestisce l'impostazione di un elemento in stato offline.
    /// L'elemento non sarà disponibile per operazioni automatiche fino al ripristino online.
    /// </summary>
    /// <param name="navigatorCommand">Il comando contenente l'elemento da impostare offline</param>
    /// <param name="infoLog">Log delle informazioni da aggiornare (parametro per riferimento)</param>
    private static async Task<string> HandleItemSetOffLine(Command navigatorCommand, string infoLog)
    {
        navigatorCommand.Status = Command.StatusTypes.Running;
        var item = PathDevices.Instance[navigatorCommand.ItemCommandId];

        if (item != null)
        {
            infoLog += " " + string.Format(CommandMessages.strSetOffLine, item.ASI, item.SUBITEM, item.PARTITION);
            await ExecutableCommands.SetItemOffOnLine(navigatorCommand, true);
        }
        else
        {
            MissingTargetDevice(navigatorCommand);
        }

        if (navigatorCommand.Status != Command.StatusTypes.Error)
            navigatorCommand.Status = Command.StatusTypes.Done;

        return infoLog;
    }

    /// <summary>
    /// Gestisce l'impostazione di un elemento in stato online.
    /// L'elemento diventa disponibile per operazioni automatiche del sistema.
    /// </summary>
    /// <param name="navigatorCommand">Il comando contenente l'elemento da impostare online</param>
    /// <param name="infoLog">Log delle informazioni da aggiornare (parametro per riferimento)</param>
    private static async Task<string> HandleItemSetOnLine(Command navigatorCommand, string infoLog)
    {
        navigatorCommand.Status = Command.StatusTypes.Running;
        var item = PathDevices.Instance[navigatorCommand.ItemCommandId];

        if (item != null)
        {
            infoLog += " " + string.Format(CommandMessages.strSetOnLine, item.ASI, item.SUBITEM, item.PARTITION);
            await ExecutableCommands.SetItemOffOnLine(navigatorCommand, false);
        }
        else
        {
            MissingTargetDevice(navigatorCommand);
        }

        if (navigatorCommand.Status != Command.StatusTypes.Error)
            navigatorCommand.Status = Command.StatusTypes.Done;
        return infoLog;
    }

    /// <summary>
    /// Gestisce la marcatura di un passo come completato per un elemento specifico.
    /// Aggiorna lo stato del processo e consente la progressione al passo successivo.
    /// </summary>
    /// <param name="navigatorCommand">Il comando contenente l'elemento per cui marcare il passo come completato</param>
    /// <param name="infoLog"></param>
    private static async Task<string> HandleItemStepDone(Command navigatorCommand, string infoLog)
    {
        navigatorCommand.Status = Command.StatusTypes.Running;
        var item = PathDevices.Instance[navigatorCommand.ItemCommandId];

        if (item != null)
        {
            infoLog += " " + string.Format(CommandMessages.strStepDone, item.ASI, item.SUBITEM, item.PARTITION);
            await ExecutableCommands.ItemStepDone(navigatorCommand);
        }
        else
        {
            MissingTargetDevice(navigatorCommand);
        }

        if (navigatorCommand.Status != Command.StatusTypes.Error)
            navigatorCommand.Status = Command.StatusTypes.Done;

        return infoLog;
    }

    /// <summary>
    /// Gestisce lo scarico di tutte le unità di carico da un elemento specifico.
    /// Genera un log dettagliato di tutte le unità di carico scaricate.
    /// </summary>
    /// <param name="navigatorCommand">Il comando contenente l'elemento da cui scaricare le unità di carico</param>
    /// <param name="infoLog">Log delle informazioni da aggiornare (parametro per riferimento)</param>
    private static async Task<string> HandleItemUnloadLu(Command navigatorCommand, string infoLog)
    {
        navigatorCommand.Status = Command.StatusTypes.Running;
        var item = PathDevices.Instance[navigatorCommand.ItemCommandId];

        if (item is { LUsLoaded.Count: > 0 })
        {
            var lusLoaded = item.LUsLoaded;
            var luCodes = new string[lusLoaded.Count];
            for (var i = 0; i < lusLoaded.Count; i++)
            {
                luCodes[i] = lusLoaded[i]?.LuCode ?? string.Empty;
            }
            var luCode = string.Join(", ", luCodes);

            infoLog += string.IsNullOrEmpty(luCode)
                ? " " + string.Format(CommandMessages.strUnloadLuNoCode, item.ASI, item.SUBITEM, item.PARTITION)
                : " " + string.Format(CommandMessages.strUnloadLuWithCode, item.ASI, item.SUBITEM, item.PARTITION,
                    luCode);

            await ExecutableCommands.ItemUnloadLu(navigatorCommand);
        }
        else if (item == null)
        {
            MissingTargetDevice(navigatorCommand);
        }

        if (navigatorCommand.Status != Command.StatusTypes.Error)
            navigatorCommand.Status = Command.StatusTypes.Done;

        return infoLog;
    }

    /// <summary>
    /// Gestisce il caricamento di un'unità di carico su un elemento specifico.
    /// Verifica la validità dell'unità di carico prima dell'operazione di caricamento.
    /// </summary>
    /// <param name="navigatorCommand">Il comando contenente l'elemento target e l'unità di carico da caricare</param>
    /// <param name="infoLog">Log delle informazioni da aggiornare (parametro per riferimento)</param>
    private static async Task<string> HandleLoadLu(Command navigatorCommand, string infoLog)
    {
        navigatorCommand.Status = Command.StatusTypes.Running;
        var item = PathDevices.Instance[navigatorCommand.ItemCommandId];

        if (item != null)
        {
            var luCode = navigatorCommand.LuId > 0
                ? (await Navigator.RequireWarehouse($"HandleLoadLu sull'unita' di carico {navigatorCommand.LuId}").DescribeLoadUnitAsync(navigatorCommand.LuId))?.Code
                : string.Empty;

            infoLog += string.IsNullOrEmpty(luCode)
                ? " " + string.Format(CommandMessages.strLoadLuNoCode, item.ASI, item.SUBITEM, item.PARTITION)
                : " " + string.Format(CommandMessages.strUnloadLuWithCode, item.ASI, item.SUBITEM, item.PARTITION,
                    luCode);

            await ExecutableCommands.LoadLu(navigatorCommand);
            navigatorCommand.Status = Command.StatusTypes.Done;
        }
        else
        {
            // C38: dispositivo bersaglio non risolto (ItemCommandId inesistente/rimosso):
            // segnala l'errore invece di chiudere il comando come se fosse andato a buon fine.
            navigatorCommand.ErrorDescription = $"Target device not found for ItemCommandId '{navigatorCommand.ItemCommandId}'";
            Navigator.RecordNavigatorLog(null, navigatorCommand.ErrorDescription, LogEntryType.Warning);
            navigatorCommand.Status = Command.StatusTypes.Error;
        }
        return infoLog;
    }

    /// <summary>
    /// Gestisce la rotta di prova dell'Editor. A differenza di <see cref="HandleLoadLu"/>, l'esito
    /// (Done o Error) lo decide <see cref="ExecutableCommands.TestRoute"/> stesso: qui si passa solo
    /// a Running prima di delegare, senza sovrascrivere lo stato al ritorno.
    /// </summary>
    /// <param name="navigatorCommand">Il comando contenente dispositivo sorgente, destinazione e tipo UDC</param>
    /// <param name="infoLog">Log delle informazioni da aggiornare (parametro per riferimento)</param>
    private static async Task<string> HandleTestRoute(Command navigatorCommand, string infoLog)
    {
        navigatorCommand.Status = Command.StatusTypes.Running;
        await ExecutableCommands.TestRoute(navigatorCommand);
        return infoLog;
    }

    /// <summary>
    /// Gestisce il movimento manuale di un'unità di carico verso una posizione specifica.
    /// L'operazione viene eseguita in modalità thread-safe utilizzando il lock di sincronizzazione delle estrazioni.
    /// </summary>
    /// <param name="navigatorCommand">Il comando contenente l'unità di carico e la destinazione del movimento</param>
    /// <param name="infoLog">Log delle informazioni da aggiornare (parametro per riferimento)</param>
    private static async Task<string> HandleMoveLuManual(Command navigatorCommand, string infoLog)
    {
        if (!await Navigator.SyncExtraction.WaitAsync(Navigator.SyncExtractionTimeout))
        {
            Navigator.WriteLog("Timeout acquiring extraction lock in HandleMoveLuManual", Log.Interface.LogEntryType.Warning);
            navigatorCommand.Status = Command.StatusTypes.Error;
            navigatorCommand.ErrorDescription = "Timeout acquiring extraction lock";
            return infoLog;
        }

        try
        {

            navigatorCommand.Status = Command.StatusTypes.Running;
            var idItem = await DbInfo.GetItemId(navigatorCommand.StorageDoorCode);
            var item = PathDevices.Instance[idItem];

            if (item != null)
            {
                infoLog += " " + string.Format(CommandMessages.strMoveLuManual, item.ASI, item.SUBITEM, item.PARTITION,
                    (await Navigator.RequireWarehouse($"HandleMoveLuManual sull'unita' di carico {navigatorCommand.LuId}").DescribeLoadUnitAsync(navigatorCommand.LuId))?.Code);
                await ExecutableCommands.MoveLu(navigatorCommand, ExecutableCommands.ExecuteMoveLuTypes.Manual);
            }
            else
            {
                // C38: dispositivo bersaglio non risolto (StorageDoorCode inesistente/rimosso):
                // segnala l'errore invece di chiudere il comando come se fosse andato a buon fine.
                navigatorCommand.ErrorDescription = $"Target device not found for StorageDoorCode '{navigatorCommand.StorageDoorCode}'";
                Navigator.RecordNavigatorLog(null, navigatorCommand.ErrorDescription, LogEntryType.Warning);
                navigatorCommand.Status = Command.StatusTypes.Error;
            }
        }
        finally
        {
            Navigator.SyncExtraction.Release();
        }

        return infoLog;
    }

    /// <summary>
    /// Gestisce il ricaricamento del diagramma di routing del sistema.
    /// L'operazione è consentita solo quando il navigator è in stato Stop o Pause.
    /// </summary>
    /// <param name="navigatorCommand">Il comando di ricaricamento del diagramma</param>
    /// <param name="infoLog">Log delle informazioni da aggiornare (parametro per riferimento)</param>
    private static async Task<string> HandleReloadDiagram(Command navigatorCommand, string infoLog)
    {
        infoLog += $" {CommandMessages.strReloadDiagram}";
        navigatorCommand.Status = Command.StatusTypes.Running;

        if (Navigator.NavStatus == Enums.NavigatorStatusTypes.Stop || Navigator.NavStatus == Enums.NavigatorStatusTypes.Pause)
        {
            try
            {
                // Sotto il cancello delle scritture dell'impianto (D2): si veda PlantQueries.ReloadUnderGateAsync.
                await Api.PlantQueries.ReloadUnderGateAsync(ExecutableCommands.ReloadRouteDiagram);
                navigatorCommand.Status = Command.StatusTypes.Done;
            }
            catch (Exception ex)
            {
                // Il comando va chiuso: uscire dal catch lasciandolo su Running lo rendeva
                // irrecuperabile, perche' la ricarica della coda seleziona STATUS < Running.
                // E' il terzo punto della voce C28, insieme ai due gestori della deframmentazione.
                navigatorCommand.ErrorDescription = ex.Message;
                navigatorCommand.Status = Command.StatusTypes.Error;

                Navigator.RecordNavigatorLog(null,
                    $"Error On CommandToDo->Execute->ReloadDiagram Description: {ex.Message}\r\nStackTrace: {ex.StackTrace}",
                    LogEntryType.Error);
            }
        }
        else
        {
            navigatorCommand.ErrorDescription = CommandMessages.strErrReloadDiagram;
            navigatorCommand.Status = Command.StatusTypes.Warning;
        }

        return infoLog;
    }

    /// <summary>
    /// Gestisce la rimozione di tutte le unità di carico dal sistema.
    /// Operazione di pulizia generale che elimina tutti i riferimenti alle unità di carico.
    /// </summary>
    /// <param name="navigatorCommand">Il comando di rimozione di tutte le unità di carico</param>
    /// <param name="infoLog">Log delle informazioni da aggiornare (parametro per riferimento)</param>
    private static async Task<string> HandleRemoveAllLu(Command navigatorCommand, string infoLog)
    {
        infoLog += $" {CommandMessages.strRemoveAllLUs}";
        navigatorCommand.Status = Command.StatusTypes.Running;
        await ExecutableCommands.RemoveAllLu();
        navigatorCommand.Status = Command.StatusTypes.Done;
        return infoLog;
    }

    /// <summary>
    /// Gestisce la disabilitazione della deframmentazione del sistema.
    /// Verifica lo stato attuale prima di procedere con la modifica.
    /// </summary>
    /// <param name="navigatorCommand">Il comando di disabilitazione della deframmentazione</param>
    /// <param name="infoLog">Log delle informazioni da aggiornare (parametro per riferimento)</param>
    private static string HandleSetDefragmentationDisabled(Command navigatorCommand, string infoLog)
    {
        infoLog += $" {CommandMessages.strNavDefragDisabled}";
        navigatorCommand.Status = Command.StatusTypes.Running;

        if (Navigator.Defragmentation != Enums.DefragmentationTypes.Enabled)
        {
            // Gia' nello stato richiesto: non c'e' niente da fare, ma il comando e' soddisfatto.
            // Lasciarlo su Running lo rendeva irrecuperabile, perche' la ricarica della coda
            // seleziona STATUS < Running e quella riga non sarebbe mai piu' tornata.
            navigatorCommand.Status = Command.StatusTypes.Done;
            return infoLog;
        }

        ExecutableCommands.DisableDefragmentation();
        navigatorCommand.Status = Command.StatusTypes.Done;
        return infoLog;
    }

    /// <summary>
    /// Gestisce l'abilitazione della deframmentazione del sistema.
    /// Verifica lo stato attuale prima di procedere con la modifica.
    /// </summary>
    /// <param name="navigatorCommand">Il comando di abilitazione della deframmentazione</param>
    /// <param name="infoLog">Log delle informazioni da aggiornare (parametro per riferimento)</param>
    private static string HandleSetDefragmentationEnabled(Command navigatorCommand, string infoLog)
    {
        infoLog += $" {CommandMessages.strNavDefragEnabled}";
        navigatorCommand.Status = Command.StatusTypes.Running;

        if (Navigator.Defragmentation != Enums.DefragmentationTypes.Disabled)
        {
            // Gia' nello stato richiesto: si veda la nota gemella in
            // HandleSetDefragmentationDisabled.
            navigatorCommand.Status = Command.StatusTypes.Done;
            return infoLog;
        }

        ExecutableCommands.EnableDefragmentation();
        navigatorCommand.Status = Command.StatusTypes.Done;

        return infoLog; 
    }

    /// <summary>
    /// Gestisce l'avvio del navigator del sistema.
    /// Il comando viene eseguito solo se il navigator è attualmente in stato Stop o Pause.
    /// </summary>
    /// <param name="navigatorCommand">Il comando di avvio del navigator</param>
    /// <param name="infoLog">Log delle informazioni da aggiornare (parametro per riferimento)</param>
    private static async Task<string> HandleSetNavigatorRun(Command navigatorCommand, string infoLog)
    {
        infoLog += $" {CommandMessages.strNavigatorRun}";
        navigatorCommand.Status = Command.StatusTypes.Running;

        if (Navigator.NavStatus == Enums.NavigatorStatusTypes.Stop || Navigator.NavStatus == Enums.NavigatorStatusTypes.Pause)
        {
            await ExecutableCommands.StartNavigator();
            navigatorCommand.Status = Command.StatusTypes.Done;
        }
        else
        {
            navigatorCommand.ErrorDescription = CommandMessages.strNavErrNavRunMode;
            navigatorCommand.Status = Command.StatusTypes.Warning;
        }

        return infoLog;
    }

    /// <summary>
    /// Gestisce l'arresto del navigator del sistema.
    /// Il comando viene eseguito solo se il navigator è attualmente in stato Run.
    /// </summary>
    /// <param name="navigatorCommand">Il comando di arresto del navigator</param>
    /// <param name="infoLog">Log delle informazioni da aggiornare (parametro per riferimento)</param>
    private static string HandleSetNavigatorStop(Command navigatorCommand, string infoLog)
    {
        infoLog += $" {CommandMessages.strNavigatorStop}";
        navigatorCommand.Status = Command.StatusTypes.Running;

        if (Navigator.NavStatus == Enums.NavigatorStatusTypes.Run)
        {
            // C30: il comando di arresto deve fermare davvero il navigatore, non metterlo in
            // pausa. StopNavigator() chiude anche le connessioni ai PLC (ClosePlcConnections),
            // cosi' un successivo comando di avvio le riapre invece di saltarle come accadeva
            // ripartendo da Pause.
            ExecutableCommands.StopNavigator();
            navigatorCommand.Status = Command.StatusTypes.Done;
        }
        else
        {
            navigatorCommand.ErrorDescription = CommandMessages.strErrNavigatorStop;
            navigatorCommand.Status = Command.StatusTypes.Warning;
        }

        return infoLog;
    }

    /// <summary>
    /// Gestisce l'impostazione di tutti gli elementi del sistema in stato pronto.
    /// Operazione di inizializzazione generale per preparare tutti gli elementi alle operazioni.
    /// </summary>
    /// <param name="navigatorCommand">Il comando di impostazione stato pronto per tutti gli elementi</param>
    /// <param name="infoLog">Log delle informazioni da aggiornare (parametro per riferimento)</param>
    private static string HandleSetStatusReadyAllItems(Command navigatorCommand, string infoLog)
    {
        infoLog += $" {CommandMessages.strSerReadyAllItems}";
        navigatorCommand.Status = Command.StatusTypes.Running;
        ExecutableCommands.SetStatusReadyAllItems();
        navigatorCommand.Status = Command.StatusTypes.Done;
        return infoLog;
    }

    /// <summary>
    /// Registra nel log l'esecuzione di un comando e i suoi eventuali errori o avvisi.
    /// Utilizza il sistema di logging del Navigator per tracciare le operazioni eseguite.
    /// </summary>
    /// <param name="infoLog">Messaggio informativo da registrare</param>
    /// <param name="navigatorCommand">Il comando elaborato per verificare errori o avvisi</param>
    private static void LogCommandExecution(string infoLog, Command navigatorCommand)
    {
        if (!string.IsNullOrEmpty(infoLog))
            Navigator.RecordNavigatorLog(null, infoLog, LogEntryType.Information);

        if (navigatorCommand.ErrorDescription != null && (navigatorCommand.Status == Command.StatusTypes.Warning ||
                                                          navigatorCommand.Status == Command.StatusTypes.Error))
            Navigator.RecordNavigatorLog(null, navigatorCommand.ErrorDescription, LogEntryType.Warning);
    }

    /// <summary>
    /// Gestisce l'esecuzione dei passi di processo con throttling di sicurezza.
    /// Implementa un controllo temporale per evitare esecuzioni troppo frequenti (minimo 10 secondi di intervallo).
    /// </summary>
    /// <param name="navigatorCommand">Il comando di esecuzione dei passi</param>
    /// <param name="infoLog">Log delle informazioni da aggiornare (parametro per riferimento)</param>
    private string HandleExecuteSteps(Command navigatorCommand, string infoLog)
    {
        if (_lastCommandExecuteSteps.AddSeconds(10) > DateTime.Now)
        {
            infoLog += $" {CommandMessages.strErrToShortExecuteSteps}";
        }
        else
        {
            infoLog += $" {CommandMessages.strExecuteSteps}";
            navigatorCommand.Status = Command.StatusTypes.Running;
            _lastCommandExecuteSteps = DateTime.Now;
            ExecutableCommands.ExecuteSteps();
            navigatorCommand.Status = Command.StatusTypes.Done;
        }

        return infoLog;
    }

    #endregion Private Methods
}