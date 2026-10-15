using Wcs.Connection.Interface;
using Wcs.Connection.Interface.CommandData;
using Wcs.Connection.Interface.ConnectionData;
using Wcs.Connection.Interface.Events;
using static Wcs.Common.Enums;

namespace Wcs.Connection.Test;

public class Connection(int id) : IConnection
{

    #region Private Methods

    private static string GetErrorDescription(int errorCode)
    {
        var errorDescription = FieldErrorTexts.ResourceManager.GetString($"D{errorCode}");

        // Il codice e' decimale. La D e' solo il prefisso che rende il codice un nome di risorsa
        // valido (un identificatore non puo' cominciare con una cifra): letta come cifra
        // esadecimale mostrava «Code 3349» per il codice 15, e sui sessantotto codici a nove cifre
        // del .resx la stringa da leggere ne aveva dieci - OverflowException.
        errorDescription = string.IsNullOrEmpty(errorDescription) ? $"Code {errorCode}" : $"Code {errorCode} - {errorDescription}";

        return errorDescription;
    }

    /// <summary>
    /// Il guasto di una risposta simulata, detto dove si puo' dirlo.
    /// </summary>
    /// <remarks>
    /// Il simulatore non ha un registro - non ne ha uno da inventare - e il corpo dei Task.Run non
    /// e' atteso da nessuno: un'eccezione la' dentro lasciava il Task in stato faulted e in
    /// silenzio, quindi nessun evento di completamento, e il dispositivo restava Moving per sempre.
    /// </remarks>
    private static void ReportFault(string origin, Exception ex)
    {
        var message = $"Wcs.Connection.Test.{origin}: la risposta simulata non e' partita: {ex}";
        Console.Error.WriteLine(message);
        System.Diagnostics.Debug.WriteLine(message);
    }

    #endregion Private Methods

    #region Public Events

    // The simulator answers commands; it has no field of its own that could start a conversation,
    // so these four events of IConnection are never raised here. Their accessors keep no handler:
    // the subscription is accepted, as the contract asks, and holding the delegate would only keep
    // the subscriber alive for an event that cannot come.
    public event Handlers.AsiEventEventHandler? AsiEvent { add { } remove { } }

    public event Handlers.AsiMovementIsCompletedEventHandler? AsiMovementIsCompleted;

    public event Handlers.AsiRequestsLuInfoEventHandler? AsiRequestsLuInfo { add { } remove { } }

    public event Handlers.AsiSendLuDataEventHandler? AsiSendLuData;

    public event Handlers.LuUnloadedFromAsiEventHandler? LuUnloadedFromAsi { add { } remove { } }

    public event Handlers.NewLuOnAsiEventHandler? NewLuOnAsi { add { } remove { } }

    #endregion Public Events

    #region Public Properties

    public decimal Id { get; } = id;

    public bool Managed { get; set; }

    #endregion Public Properties

    #region Public Methods

    public void CloseConnection()
    {
    }

    public void Dispose()
    {
    }

    public void MoveLuFromAsiToAsi(RunAsi sourceItem, DataAsi actionItemPerformer, DataAsi fromAsi, DataAsi toAsi,
        Guid stepGuid, decimal luId, int luHandlingMode = 0)
    {
        var luData = new MoveLuDataResult
        {
            FinalPosAsi = toAsi.Asi,
            FinalPosSubitem = toAsi.Subitem,
            FinalPosPartition = toAsi.Partition
        };

        var resultData = new AsiMovementIsCompletedData { Lu = [luData] };

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(sourceItem.DelayToStepDone);
                AsiMovementIsCompleted?.Invoke(this,
                    new AsiMovementIsCompletedEventArgs(sourceItem.ItemGuid, stepGuid, sourceItem.ResultType,
                        sourceItem.ResultType == ResultCodeTypes.Error ? sourceItem.ErrorResultCode : 0,
                        GetErrorDescription(sourceItem.ResultType == ResultCodeTypes.Error
                            ? sourceItem.ErrorResultCode
                            : 0), resultData));
            }
            catch (Exception ex)
            {
                ReportFault(nameof(MoveLuFromAsiToAsi), ex);
            }
        });
    }

    public void MoveLuFromAsiToAsi(RunAsi sourceItem, DataAsi actionItemPerformer, Guid groupedMovementId,
        List<MovementLuDataCommand> loadUnitsToMove, int luHandlingMode = 0)
    {
        var resultData = new AsiMovementIsCompletedData();

        foreach (var lu in loadUnitsToMove)
            resultData.Lu.Add(new MoveLuDataResult
            {
                FinalPosAsi = lu.DestAsi ?? string.Empty,
                FinalPosSubitem = lu.DestSubitem ?? string.Empty,
                FinalPosPartition = lu.DestPartition ?? string.Empty
            });

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(sourceItem.DelayToStepDone);
                AsiMovementIsCompleted?.Invoke(this,
                    new AsiMovementIsCompletedEventArgs(sourceItem.ItemGuid, groupedMovementId, sourceItem.ResultType,
                        sourceItem.ResultType == ResultCodeTypes.Error ? sourceItem.ErrorResultCode : 0,
                        GetErrorDescription(sourceItem.ResultType == ResultCodeTypes.Error
                            ? sourceItem.ErrorResultCode
                            : 0), resultData));
            }
            catch (Exception ex)
            {
                ReportFault(nameof(MoveLuFromAsiToAsi), ex);
            }
        });
    }

    public void OpenConnection()
    {
    }

    public void RequestLuDataFromAsi(RunAsi item)
    {
        var resultData = new AsiSendLuData
        {
            DataConfirm = 1,
            DataErrorCode = 0,
            LuCode = "0001"
        };

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(200);
                AsiSendLuData?.Invoke(this,
                    new AsiSendLuDataEventArgs(item.ItemGuid, ResultCodeTypes.Success, resultData));
            }
            catch (Exception ex)
            {
                ReportFault(nameof(RequestLuDataFromAsi), ex);
            }
        });
    }

    public void SendCommandToAsi(RunAsi destinationItem, int commandCode, List<string?> parameters)
    {
    }

    public void SendLuDataToAsi(RunAsi item, LuInfoDataCommand value)
    {
    }

    public void ShowMessageToAsi(RunAsi destinationItem, string message)
    {
    }

    #endregion Public Methods
}