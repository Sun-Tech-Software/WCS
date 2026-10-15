using System.ComponentModel;

namespace Wcs.Common;

public static class Enums
{
    #region Public Enums

    public enum CellLockTypes : byte
    {
        None = 0,
        Loading = 1,
        Unloading = 2,
        Full = 3
    }

    /// <summary>
    /// I tipi di comando del motore, per chi li accoda da fuori (Editor, Client del WMS) con
    /// <c>POST /commands</c>. Gli stessi valori di <c>Command.CommandTypes</c> nel motore: un test
    /// li tiene uguali.
    /// </summary>
    public enum CommandTypes
    {
        SetNavigatorRun = 1001,
        SetNavigatorStop = 1002,
        ReloadDiagram = 1003,
        RemoveAllLu = 1004,
        SetStatusReadyAllItems = 1005,
        ExecuteSteps = 1007,
        SetDefragmentationEnabled = 1008,
        SetDefragmentationDisabled = 1009,
        TestRoute = 1010,
        ItemExecuteStep = 2001,
        ItemSetOffLine = 2002,
        ItemSetOnLine = 2003,
        ItemUnloadLu = 2004,
        ItemStepDone = 2007,
        ItemRejectLu = 2008,
        ItemFindStoringLocation = 2009,
        MoveLuAutomatic = 3001,
        LoadLu = 3002,
        MoveLuManual = 3003,
        EmptyCellLogically = 3004,
        EmptyCellPisically = 3005,
        ChangeLUsStoreLocation = 3006,
        MoveLuToItem = 3007,
        MoveReferenceManual = 3008,
        DeleteLuFromExtractionQueue = 4001,

        /// <summary>Tracce dell'operatore dal Client del WMS: arrivano gia' concluse (Done), il motore non le esegue; servono alla lista dei comandi utente.</summary>
        ModifyRushMoverStatus = 4002,
        ChangeCellLockStatus = 4004,
        AddOrModifyLuPosition = 4005,
        ModifyDatabaseCellContents = 4006
    }

    public enum ConnectionTypes : byte
    {
        [Description("Test (No Communication to PLC)")]
        Test = 0,
        [Description("Connected to PLC")] Connected = 1
    }

    public enum DefragmentationTypes : byte
    {
        [Description("None")] None = 0,

        [Description("Disabled")] Disabled = 1,

        [Description("Enabled")] Enabled = 2
    }

    public enum DeviceStatusTypes : byte
    {
        [Description("Ready")] Ready = 0,

        [Description("Moving")] Moving = 1,

        [Description("Loading")] Loading = 2,

        [Description("Unloading")] Unloading = 3,

        [Description("Busy")] Busy = 4,

        [Description("Recovering")] Recovering = 5,

        [Description("Offline")] OffLine = 6,

        [Description("Locked")] Locked = 7,

        [Description("Warning")] Warning = 89,

        [Description("In Error")] Error = 99
    }

    public enum DeviceTypes : byte
    {
        [Description("Automation")] Automation = 0,

        [Description("Loading")] Loading = 1,

        [Description("Buffer")] Buffer = 2,

        [Description("Storage")] Storage = 3,

        [Description("Circular Distributor")] CircularDistributor = 4,

        [Description("Group Automation")] GroupOfAutomationDevices = 5,

        [Description("LoadingAndBuffer")] LoadingAndBuffer = 6,

        [Description("None")] None = 99
    }

    public enum DiagramItemTypes : byte
    {
        Automation = 0,
        Junction = 1,
        None = 99
    }

    public enum EmptyCellStatusTypes : byte
    {
        [Description("None")] None = 0,

        [Description("Active")] Active = 1
    }

    public enum ExecutedStepTypes : byte
    {
        PickUp = 0,
        Store = 1
    }

    public enum LookWhereAllocateLuTypes : byte
    {
        [Description("No")] No = 0,

        [Description("Check the possibility of deposit if it has not already been calculated, otherwise reject")]
        CheckOnly = 1,

        [Description("Build the road to the depot if it has not already been calculated, otherwise reject")]
        BuildRouteToStore = 2,

        [Description(
            "Build the road to the depot by grouping by UDC type if it has not already been calculated, otherwise reject")]
        BuildRouteToStoreByIdLuType = 3,

        [Description(
            "Build the storage road for several storage items if it has not already been calculated, otherwise reject")]
        BuildRouteToDifferentStoreLocation = 4,

        [Description("Build the road to the depot even if it has already been calculated, otherwise reject")]
        BuildRouteToStoreForcing = 5
    }

    public enum LuLoadStatusTypes : byte
    {
        [Description("None")] None = 0,

        [Description("Loaded")] Loaded = 1
    }

    public enum MissionStatusTypes : byte
    {
        [Description("Run")] Run = 0,
        [Description("Stop")] Stop = 1,
        [Description("Done")] Done = 2,
        [Description("Error")] Error = 99
    }

    public enum MissionTypes : byte
    {
        [Description("Storing")] Storing = 0,

        [Description("Extraction")] Extraction = 1,

        [Description("Change Store Location")] ChangeStoreLocation = 2,

        [Description("Moving to item")] MovingToItem = 5,

        [Description("Storing By PickpUp")] StoringByPickUp = 6,

        [Description("Rejection")] Rejection = 7,

        [Description("PickUp And Store Again")]
        PickUpAndStoreAgain = 9,

        [Description("Defragment Cell")] DefragmentCell = 10,

        [Description("None")] None = 99
    }

    public enum MotionTypes : byte
    {
        [Description("When I receive a PathMission the executor is the source element")]
        MoveTo = 0,

        [Description("When I receive a PathMission the executor is me")]
        GetFrom = 1
    }

    public enum NavigatorStatusTypes : byte
    {
        [Description("Stop")] Stop = 0,

        [Description("Run")] Run = 1,

        [Description("Recover by Failure")] Recover = 2,

        [Description("Dispose Navigator")] Dispose = 3,

        [Description("Pause")] Pause = 4
    }

    public enum OperationTypes : byte
    {
        Inbound = 0,
        Outbound = 1,
        Compacting = 3,
        None = 99
    }

    public enum RecordStorageTypes : byte
    {
        [Description("None")] None = 0,

        [Description("Lenght From PLC")] FromPlc = 1,

        [Description("Fixed Lenght")] FixedLength = 2,

        [Description("Convert Depth to Partition")]
        ConvertDepthToPartition = 3,

        [Description("Calculated Storage Depth")]
        CalculatedStorageDepth = 4
    }

    public enum ResultCodeTypes : byte
    {
        [Description("None")] None = 0,
        [Description("Success")] Success = 1,
        [Description("Timeout")] TimeOut = 2,
        [Description("ConnectionError")] ConnectionError = 3,
        [Description("Error")] Error = 99
    }

    public enum StatoOperazioneCella : byte
    {
        Nessuna = 0,
        Deposito = 1,
        Prelievo = 2
    }

    public enum StatusExtractionLu
    {
        NotExtracted = -1,
        InExtraction = 0,
        Extracted = 1,
        NotReachable = 2,
        InOtherExtraction = 3,
        LuLocked = 4
    }

    public enum StepTypes : byte
    {
        Automatic = 0,
        Manual = 1
    }

    /// <summary>Il periodo su cui il riepilogo delle UDC movimentate raggruppa: giorno, settimana ISO (dal lunedi'), mese, anno.</summary>
    public enum SummaryPeriod : byte { Day = 0, Week = 1, Month = 2, Year = 3 }

    #endregion Public Enums
}