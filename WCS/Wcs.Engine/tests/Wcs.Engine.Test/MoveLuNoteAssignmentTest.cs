using Wcs.Engine.Collections;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;
using AutomationDevice = Wcs.Engine.Items.AutomationDevice;
using Command = Wcs.Engine.Items.Command;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Compito 3: lo spostamento a mano di un'unita' di carico chiede al confine se una nota di
/// spedizione l'abbia gia' impegnata, invece di leggere <c>ASSEGNAZIONE_NOTE</c> da SQL Server
/// (<c>DbInfo.IsLuPresentInAssignmentNote</c>, cancellata).
/// <para>
/// I tre test sono le tre risposte possibili, e la terza e' quella che prima non esisteva:
/// «assegnata» ferma il comando con il messaggio di sempre, «non assegnata» lo lascia proseguire,
/// e un gestionale che non risponde <b>solleva</b>. Prima rispondeva falso - «libera» - e l'unita'
/// di carico che una nota stava aspettando partiva lo stesso.
/// </para>
/// </summary>
public sealed class MoveLuNoteAssignmentTest : IDisposable
{
    private const decimal LoadUnitId = 6100m;
    private const string LoadUnitCode = "UDC-6100";
    private const string BufferDoorCode = "ML-BUF";
    private const string DestinationDoorCode = "ML-DST";
    private const decimal BufferCellId = 9600m;

    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly NavigatorStatusTypes _previousNavStatus = Navigator.NavStatus;

    /// <summary>
    /// Il dispositivo messo nel diagramma, per toglierlo alla fine: <c>PathDevices</c> e' statico e
    /// vive quanto il processo dei test.
    /// </summary>
    private readonly List<AutomationDevice> _devices = [];

    public MoveLuNoteAssignmentTest()
    {
        Navigator.Store = _database;

        // Il comando esce subito se il navigatore non e' in marcia (e Stop e' il valore che il campo
        // statico ha nei test): senza questa riga i test proverebbero quel rifiuto, non il loro.
        Navigator.NavStatus = NavigatorStatusTypes.Run;
    }

    public void Dispose()
    {
        foreach (var device in _devices)
        {
            PathDevices.Instance.Remove(device.ItemGuid);
            device.Dispose();
        }

        Navigator.NavStatus = _previousNavStatus;
        Navigator.Store = null;
        Navigator.Warehouse = _previousWarehouse;
        _database.Dispose();
    }

    /// <summary>
    /// L'unita' di carico che una nota ha impegnato non si sposta a mano, e l'operatore legge il
    /// messaggio di sempre - con il codice dell'unita' di carico, non con l'identificativo.
    /// </summary>
    [Fact]
    public async Task ALoadUnitAssignedToANoteDoesNotMove()
    {
        var warehouse = await PrepareDiagramAsync();
        warehouse.LoadUnitsAssignedToNotes.Add(LoadUnitId);

        var command = await MoveTheLoadUnitAsync();

        Assert.Contains(nameof(IWarehouse.GetLoadUnitAssignmentStateAsync), warehouse.Calls);
        Assert.Equal(
            string.Format(CommandMessages.strErrMoveLuLockedByNote, LoadUnitCode),
            command.ErrorDescription);
        Assert.Equal(Command.StatusTypes.Error, command.Status);
    }

    /// <summary>
    /// L'altra meta', senza la quale una guardia che rispondesse sempre «assegnata» passerebbe il
    /// test qui sopra: nessuna nota impegna questa unita' di carico, e il comando arriva alla
    /// guardia successiva - le aree servite dal polmone, che in questo diagramma non ce ne sono -
    /// dove si ferma con un messaggio <b>diverso</b>.
    /// </summary>
    [Fact]
    public async Task ALoadUnitNoNoteHasTakenGoesPastTheGuard()
    {
        var warehouse = await PrepareDiagramAsync();

        var command = await MoveTheLoadUnitAsync();

        Assert.Contains(nameof(IWarehouse.GetLoadUnitAssignmentStateAsync), warehouse.Calls);
        Assert.Equal(
            string.Format(CommandMessages.strErrMoveLuNoPickingZoneLu, LoadUnitCode),
            command.ErrorDescription);
        Assert.Equal(Command.StatusTypes.Error, command.Status);
    }

    /// <summary>
    /// Il cambiamento in campo: il gestionale che non risponde <b>a questa domanda</b> solleva, e
    /// non risponde «libera». Il comando non si chiude qui - lo chiude in errore il <c>catch</c> per
    /// comando di <c>CommandToDo.Execute</c>, che e' l'unica strada per cui questo metodo passa - ma
    /// l'unita' di carico impegnata da una nota non parte piu' per un guasto della lettura.
    /// <para>
    /// Non si prova con <see cref="FakeWarehouse.Unavailable"/>: <c>MoveLu</c> chiede al confine
    /// anche il codice dell'unita' di carico, qualche riga prima, e solleverebbe li' - il test
    /// passerebbe senza dire niente su questa domanda.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AWarehouseThatDoesNotAnswerRaisesInsteadOfLettingTheLoadUnitMove()
    {
        var warehouse = await PrepareDiagramAsync();
        warehouse.UnavailableOperations.Add(nameof(IWarehouse.GetLoadUnitAssignmentStateAsync));

        var command = NewCommand();

        await Assert.ThrowsAsync<WarehouseUnavailableException>(
            () => ExecutableCommands.MoveLu(command, ExecutableCommands.ExecuteMoveLuTypes.Manual));
    }

    /// <summary>
    /// Il diagramma minimo che <c>MoveLu</c> pretende prima di arrivare alla domanda: un dispositivo
    /// dietro la cella polmone del comando, la mappa porte che lo risolve, e il codice dell'unita'
    /// di carico, che il metodo legge dal confine e mette nei messaggi.
    /// </summary>
    private async Task<FakeWarehouse> PrepareDiagramAsync()
    {
        var bufferItem = Guid.NewGuid();

        var device = TestDevices.WithRow(bufferItem, d => d.DeviceType = DeviceTypes.Buffer);
        PathDevices.Instance.Add(device);
        _devices.Add(device);

        await PlantPersistence.SaveDoorMappingsAsync(bufferItem,
            [new DoorMapping { ItemId = bufferItem, DoorCode = BufferDoorCode, CellId = BufferCellId, Side = "A" }]);

        var warehouse = new FakeWarehouse();
        warehouse.LoadUnits[LoadUnitId] = new LoadUnitDescription(LoadUnitId, LoadUnitCode, LoadUnitTypeId: 1,
            HandlingMode: 0, Tare: 0, Height: 100, Width: 80, Depth: 120, Weight: 250);
        Navigator.Warehouse = warehouse;

        return warehouse;
    }

    /// <summary>
    /// Il comando cosi' come lo scrive l'operatore, in modalita' manuale: e' quella che lascia il
    /// motivo del rifiuto in <c>Command.ErrorDescription</c> invece di riassorbirlo.
    /// </summary>
    private Command NewCommand() => new()
    {
        CommandType = Command.CommandTypes.MoveLuManual,
        ItemCommandId = _devices[0].ItemGuid,
        BufferCellId = BufferCellId,
        LuId = LoadUnitId,
        StorageDoorCode = DestinationDoorCode,
        WorkstationId = "ML-WS"
    };

    private async Task<Command> MoveTheLoadUnitAsync()
    {
        var command = NewCommand();

        await ExecutableCommands.MoveLu(command, ExecutableCommands.ExecuteMoveLuTypes.Manual);

        return command;
    }
}
