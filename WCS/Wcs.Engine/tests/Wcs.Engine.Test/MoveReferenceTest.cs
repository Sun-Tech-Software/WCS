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
/// Compito 20: la ricerca della referenza del comando manuale
/// (<c>ExecutableCommands.MoveReference</c>) passa dal confine invece che dai servizi del gestionale
/// caricati nel processo del motore. Non e' <see cref="IWarehouse.FindLoadUnitAsync"/> con un
/// parametro in piu': la chiave e' la referenza e il polmone, non l'unita' di carico.
/// <para>
/// Il motore non costruisce piu' ne' le opzioni di ricerca del gestionale ne' la lettura delle aree
/// servite dal polmone: manda i due dati che sa - la referenza chiesta dall'operatore e il polmone -
/// e riceve un esito. I tre test coprono i tre esiti, e i due messaggi che l'operatore legge quando
/// il comando non parte restano quelli di prima, distinti fra loro.
/// </para>
/// </summary>
public sealed class MoveReferenceTest : IDisposable
{
    private const string ReferenceId = "MR-REF-1";
    private const string BufferDoorCode = "MR-BUF";
    private const string DestinationDoorCode = "MR-DST";
    private const decimal BufferCellId = 9500m;

    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly NavigatorStatusTypes _previousNavStatus = Navigator.NavStatus;

    /// <summary>
    /// Il dispositivo messo nel diagramma, per toglierlo alla fine: <c>PathDevices</c> e' statico e
    /// vive quanto il processo dei test.
    /// </summary>
    private readonly List<AutomationDevice> _devices = [];

    public MoveReferenceTest()
    {
        Navigator.Store = _database;

        // Il comando esce subito se il navigatore non e' in marcia (e Stop e' il valore che il campo
        // statico ha nei test): senza questa riga i tre test proverebbero quel rifiuto, non il loro.
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
    /// Primo esito: nessuna unita' di carico raggiungibile ha quella referenza. La domanda che il
    /// motore fa porta <b>due soli campi</b> - la referenza e il polmone - e il messaggio che
    /// l'operatore legge e' quello di sempre.
    /// </summary>
    [Fact]
    public async Task TheQuestionCarriesTheReferenceAndTheBufferAndNothingFoundKeepsItsOwnMessage()
    {
        var warehouse = await PrepareDiagramAsync();

        var command = await MoveTheReferenceAsync();

        Assert.Equal([new FindLoadUnitByReferenceRequest(ReferenceId, BufferCellId)], warehouse.ReferenceSearches);

        Assert.Equal(
            string.Format(CommandMessages.strErrMoveLuNotFoundOrLocked, null, DestinationDoorCode),
            command.ErrorDescription);
        Assert.Equal(Command.StatusTypes.Error, command.Status);
    }

    /// <summary>
    /// Secondo esito: il polmone non serve nessuna area di prelievo. Era il rifiuto che il motore
    /// decideva da se' leggendo <c>AREE_PRELIEVO_PICKING</c>; ora lo decide il gestionale, e il
    /// messaggio dell'operatore resta <b>diverso</b> da quello del primo esito - e' per questo che
    /// l'esito e' un'enumerazione e non un'assenza.
    /// </summary>
    [Fact]
    public async Task ABufferThatServesNoPickingAreaKeepsItsOwnMessage()
    {
        var warehouse = await PrepareDiagramAsync();
        warehouse.ReferenceSearchResults[ReferenceId] =
            new ReferenceSearchResult(ReferenceSearchOutcome.NoPickingAreas, 0, 0);

        var command = await MoveTheReferenceAsync();

        Assert.Equal(
            string.Format(CommandMessages.strErrMoveLuNoPickingZoneReference, ReferenceId),
            command.ErrorDescription);
        Assert.Equal(Command.StatusTypes.Error, command.Status);
    }

    /// <summary>
    /// Terzo esito, ed e' quello che tiene il comportamento: l'unita' di carico e la cella trovate
    /// dalla ricerca <b>sono</b> quelle su cui il motore chiede la porta di prelievo. Sono i due
    /// soli campi che leggeva dalla trentina che il gestionale rispondeva.
    /// <para>
    /// Il test si ferma dove il diagramma finisce: la porta di prelievo risposta non ha nessun
    /// dispositivo dietro, e il comando si chiude con il rifiuto che il motore da' in quel caso.
    /// Serve a dichiarare fin dove arriva la copertura - il seguito (la costruzione del movimento)
    /// non e' toccato da questo lavoro.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheLoadUnitAndTheCellFoundAreTheOnesThePickUpDoorIsAskedFor()
    {
        var warehouse = await PrepareDiagramAsync();
        warehouse.ReferenceSearchResults[ReferenceId] =
            new ReferenceSearchResult(ReferenceSearchOutcome.Found, LoadUnitId: 7100m, CellId: 4300m);
        warehouse.PickUpDoorChoices[7100m] = new PickUpDoorChoice(CellDoor.A, "MR-PICK", 0, []);

        var command = await MoveTheReferenceAsync();

        Assert.Equal([new ChoosePickUpDoorRequest(7100m, 4300m)], warehouse.PickUpDoorChoiceRequests);

        Assert.Equal(
            string.Format(CommandMessages.strErrMoveLuSourceNoLink, "MR-PICK"),
            command.ErrorDescription);
        Assert.Equal(Command.StatusTypes.Error, command.Status);
    }

    /// <summary>
    /// Il diagramma minimo che <c>MoveReference</c> pretende prima di arrivare alla ricerca: un
    /// dispositivo che sia insieme quello del comando e quello dietro la cella polmone (la ricerca
    /// non guarda che tipo sia), e la mappa porte che risolve il polmone. Senza uno dei due pezzi il
    /// comando si ferma prima, con un altro messaggio.
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
        Navigator.Warehouse = warehouse;

        return warehouse;
    }

    /// <summary>
    /// Il comando cosi' come lo scrive l'operatore, in modalita' manuale: e' quella che lascia il
    /// motivo del rifiuto in <c>Command.ErrorDescription</c> invece di riassorbirlo.
    /// </summary>
    private async Task<Command> MoveTheReferenceAsync()
    {
        var command = new Command
        {
            CommandType = Command.CommandTypes.MoveReferenceManual,
            ItemCommandId = _devices[0].ItemGuid,
            BufferCellId = BufferCellId,
            ReferenceId = ReferenceId,
            StorageDoorCode = DestinationDoorCode,
            WorkstationId = "MR-WS"
        };

        await ExecutableCommands.ManageMoveReferenceManual(command, "prova: ");

        return command;
    }
}
