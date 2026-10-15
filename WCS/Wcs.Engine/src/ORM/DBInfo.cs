using Helper.Converters;
using Wcs.Engine.Collections;
using Wcs.Engine.Items;
using Log.Interface;
using System.Data;
using Microsoft.EntityFrameworkCore;
using System.Reflection.PortableExecutable;
using Wcs.Connection.Interface;
using Wcs.Wms.Contracts;
using static Wcs.Common.Enums;
using static Wcs.Engine.Navigator;

namespace Wcs.Engine.ORM;

internal static partial class DbInfo
{

    #region Internal Methods

    /// <summary>
    /// Azzera le operazioni rimaste aperte sulle celle. Le celle si leggono dalla mappa porte nel
    /// database del WCS, e ognuna viene ripristinata e attesa. Gira a <b>ogni</b> giro di
    /// <c>Navigator.MasterProcess</c>, in coda ad <c>AssignmentsProcess</c> e
    /// <c>ExtractionProcess</c>, e non solo all'avvio.
    /// </summary>
    /// <remarks>
    /// <b>Una parte sola, dalla revisione del compito 6.</b> Fino a quel momento questo metodo ne
    /// aveva due: prima <c>DeleteQueueLuOfNoteDone</c> - la pulizia delle assegnazioni di nota gia'
    /// lavorate - e poi il ripristino delle celle. La pulizia delle note e' stata cancellata perche'
    /// poneva al gestionale, nello stesso giro, la <b>stessa domanda</b> che
    /// <see cref="DeleteLuInExtranctionByPausedNote"/> pone poco prima dentro
    /// <c>Navigator.AssignmentsProcess</c>, e per giunta quando le arrivava il turno le righe che
    /// avrebbe dovuto togliere erano gia' uscite dalla coda: il vincolo e' una domanda per giro.
    /// <para>
    /// <b>Che ne e' della segnalazione I-2 dell'ondata 1.</b> Diceva che i due <c>try</c> dovevano
    /// restare separati, perche' con un <c>try</c> solo un gestionale non ancora acceso al primo
    /// avvio usciva dalla prima parte e <b>saltava l'intero ciclo</b> di ripristino delle celle, che
    /// con il gestionale non c'entra niente. Quel caso adesso non esiste piu' perche' non esiste
    /// piu' la prima parte: qui dentro non si tocca il confine, si legge solo il database del WCS.
    /// Il <c>try</c> che resta non serve piu' a proteggere il ripristino da qualcun altro, serve a
    /// non far uscire il suo guasto verso <c>MasterProcess</c>, che chiama questo metodo a ogni
    /// giro.
    /// </para>
    /// </remarks>
    internal static Task CheckOperation() => CheckOperation(ResetOpenCellOperations);

    /// <summary>
    /// La sola parte di <see cref="CheckOperation"/>, con la sua rete. La riceve come parametro
    /// perche' quella vera vuole il database del WCS, mentre cio' che qui conta - che il suo guasto
    /// non esca dal metodo - non ne ha bisogno.
    /// </summary>
    internal static async Task CheckOperation(Func<Task> resetOpenCellOperations)
    {
        try
        {
            await resetOpenCellOperations();
        }
        catch (Exception ex)
        {
            WriteLog(
                $"Error On DBInfo->CheckOperation (ResetOpenCellOperations) Description: {ex.Message}\r\nStackTrace: {ex.StackTrace}",
                LogEntryType.Error);
        }
    }

    /// <summary>
    /// Azzera le operazioni rimaste aperte sulle celle: le celle si leggono dalla mappa porte nel
    /// database del WCS, e ognuna viene ripristinata e attesa.
    /// </summary>
    private static async Task ResetOpenCellOperations()
    {
        var cells = await ExtractionQueue.ReadAsync(Persistence.DoorMappingStore.CellsWithActiveOperationAsync, new List<decimal>());

        foreach (var cellId in cells)
            await ResetCellOperationsStatus(cellId);
    }

    /// <summary>
    /// Conferma le assegnazioni servite da una riga della coda (compito 19, terza operazione).
    /// L'assegnazione si legge dalla riga nel database del WCS - quello e' un dato suo - e da li' in
    /// poi e' tutto lavoro del gestionale: la <c>SELECT DISTINCT</c> su tre sue tabelle e la
    /// conferma di ogni riga trovata sono passate di la', dentro
    /// <see cref="IWarehouse.ConfirmServedAssignmentsAsync"/>. E' l'unico taglio di questo compito
    /// che elimina anche una query SQL diretta.
    /// </summary>
    /// <remarks>
    /// <b>Il nome e' cambiato con il contenuto:</b> non legge piu' nessun dato («Get…Data») e non ha
    /// mai risposto niente - conferma, e basta.
    /// <para>
    /// <b>E cambia il comportamento in errore.</b> Prima l'errore veniva inghiottito: si scriveva una
    /// riga nel registro e si proseguiva, senza marcare nessuno stato. La regola del confine conserva
    /// le degradazioni solo dove il codice marca uno stato che il motore gia' modella e mostra, e qui
    /// non e' il caso, quindi adesso l'eccezione risale. Il chiamante e' uno solo,
    /// <c>PathMission.ProcessExtractionItems</c>, che gia' registra e prosegue: quello che cambia in
    /// campo e' che nel giro fallito la riga di coda del WCS non viene cancellata, perche' la
    /// cancellazione sta dopo questa chiamata.
    /// </para>
    /// </remarks>
    internal static async Task ConfirmServedAssignmentsByExtractionId(decimal extractionId)
    {
        if (extractionId == 0) return;

        var assignmentId = await ExtractionQueue.ReadAsync(
            async context => (await Persistence.ExtractionQueueStore.FindAsync(context, (long)extractionId))?.AssignmentId, null);
        if (assignmentId is null or 0) return;

        await Navigator.RequireWarehouse("ConfirmServedAssignmentsByExtractionId")
            .ConfirmServedAssignmentsAsync(new ConfirmServedAssignmentsRequest(assignmentId.Value, UserId));
    }

    internal static async Task<decimal> GetCellId(string? doorCode)
    {
        if (string.IsNullOrEmpty(doorCode)) return 0;

        return await ExtractionQueue.ReadAsync(context => Persistence.DoorMappingStore.CellIdByDoorAsync(context, doorCode), 0m);
    }

    internal static async Task<DefragmentationTypes> GetDefragmentationStatus()
    {
        return (DefragmentationTypes)await Persistence.PlantPersistence.LoadCompactingStatusAsync();
    }

    internal static async Task<string?> GetDoorCode(Guid? itemId)
    {
        if (itemId == null || itemId == Guid.Empty)
            return string.Empty;

        return await ExtractionQueue.ReadAsync(context => Persistence.DoorMappingStore.DoorCodeByItemAsync(context, itemId.Value), string.Empty)
               ?? string.Empty;
    }

    internal static async Task<decimal> GetExtractionLuRowId(decimal luId)
    {
        if (luId == 0) return 0;

        return await ExtractionQueue.ReadAsync(
            async context => (await Persistence.ExtractionQueueStore.FindByLoadUnitAsync(context, luId))?.ExtractionRowId ?? 0, 0m);
    }

    /// <summary>
    /// La cella in cui sta l'unita' di carico, o 0 se non e' a magazzino.
    /// </summary>
    /// <remarks>
    /// Era una <c>SELECT ID_CELLA FROM POSIZIONI_UDC WHERE ID_UDC = @ID_UDC AND ASSEGNAZIONE = 0</c>
    /// diretta su SQL Server. <see cref="IWarehouse.LocateLoadUnitAsync"/> risponde quella stessa
    /// riga — stessa tabella, stessa chiave, stesso filtro <c>ASSEGNAZIONE = 0</c> — insieme alla
    /// profondita' di stoccaggio, e il motore la usava gia' altrove per lo stesso dato. C'erano due
    /// strade per la stessa cosa, e in un punto erano adiacenti.
    /// <para>
    /// <b>Cambia il modo di rompersi:</b> prima un guasto della connessione tornava 0, cioe' «non e'
    /// a magazzino», indistinguibile da un dato; adesso solleva
    /// <c>WarehouseUnavailableException</c>, che e' la regola del confine.
    /// </para>
    /// </remarks>
    internal static async Task<decimal> GetIdCellLocationByLuId(decimal luId)
    {
        if (luId == 0)
            return 0;

        var location = await Navigator.RequireWarehouse($"LocateLoadUnitAsync sull'unita' di carico {luId}")
            .LocateLoadUnitAsync(luId);

        return location?.CellId ?? 0;
    }

    internal static async Task<Guid> GetItemId(decimal? cellId)
    {
        if (cellId is null or 0) return Guid.Empty;

        // Qui la cella non mappata e il dispositivo vuoto sono la stessa risposta, Guid.Empty, come
        // senza store: chi chiede l'item di una cella lo usa per cercare il dispositivo, e non c'e'.
        return await ExtractionQueue.ReadAsync<Guid?>(context => Persistence.DoorMappingStore.ItemIdByCellAsync(context, cellId.Value), Guid.Empty) ?? Guid.Empty;
    }

    /// <summary>
    /// La cella e' bloccata al deposito (<c>StoringLocked</c> su una delle sue porte)? Prima il
    /// motore lo chiedeva al WMS, che leggeva ITEMS_MAP: e' un dato del WCS, e si legge qui. Senza
    /// store risponde falso, come le altre letture della mappa porte.
    /// </summary>
    internal static async Task<bool> GetCellLockedForStoring(decimal cellId)
    {
        if (cellId == 0) return false;

        return await ExtractionQueue.ReadAsync(context => Persistence.DoorMappingStore.StoringLockedByCellAsync(context, cellId), false);
    }

    /// <summary>
    /// La porta e' bloccata al deposito? Serve a chi sta per bloccarla e dovra' poi rimetterla come
    /// l'ha trovata: il flag non ha un proprietario, e chi lo trova gia' alzato non e' chi lo ha
    /// alzato. Senza store risponde falso, come le altre letture della mappa porte, e li' il blocco
    /// non si scrive nemmeno.
    /// </summary>
    internal static async Task<bool> GetDoorLockedForStoring(string? doorCode)
    {
        if (string.IsNullOrEmpty(doorCode)) return false;

        return await ExtractionQueue.ReadAsync(context => Persistence.DoorMappingStore.StoringLockedByDoorAsync(context, doorCode), false);
    }

    /// <summary>
    /// Le celle servite dal ricollocatore: quelle delle porte dei magazzini che lo hanno come
    /// <c>RelocateItemId</c>. Era la sottoquery su ITEMS_MAP e NAVIGATOR_ITEMS.RELOCATE_LU_ITEM che il
    /// WMS faceva in TrovaUdc(idUdc, idItem) per conto del motore: il dato e' del WCS, si legge qui,
    /// e il motore passa l'insieme al WMS. Con un id vuoto, o senza store, nessuna cella.
    /// </summary>
    internal static async Task<List<decimal>> GetCellsServedByRelocator(Guid relocatorItemId)
    {
        if (relocatorItemId == Guid.Empty) return [];

        return await ExtractionQueue.ReadAsync(context => Persistence.DoorMappingStore.CellsByRelocatorAsync(context, relocatorItemId), []);
    }

    /// <summary>
    /// The side of a door in the WCS's door map (<c>A</c> or <c>B</c>), <c>null</c> for a door that is not
    /// mapped, or without a store. Item 54: a precise cell is chosen by its door A code.
    /// </summary>
    internal static async Task<string?> GetDoorSide(string? doorCode)
    {
        if (string.IsNullOrEmpty(doorCode)) return null;

        return await ExtractionQueue.ReadAsync(context => Persistence.DoorMappingStore.SideByDoorAsync(context, doorCode), null);
    }

    internal static async Task<Guid> GetItemId(string? doorCode)
    {
        if (string.IsNullOrEmpty(doorCode)) return Guid.Empty;

        return await ExtractionQueue.ReadAsync(context => Persistence.DoorMappingStore.ItemIdByDoorAsync(context, doorCode), Guid.Empty);
    }

    /// <summary>
    /// La profondita' dell'unita' di carico (<c>UDC.PROFONDITA</c>), o 0 se non esiste.
    /// </summary>
    /// <remarks>
    /// Era una lettura diretta su <c>UDC</c> con la chiave <c>ID_UDC</c>, cioe' la quarta lettura
    /// sparsa sulla stessa riga che <see cref="LoadUnitDescription"/> dichiara di aver sostituito:
    /// il suo <c>Depth</c> e' quella colonna, con la stessa chiave. Vale la stessa nota sul modo di
    /// rompersi di <see cref="GetIdCellLocationByLuId"/>.
    /// </remarks>
    internal static async Task<int> GetLuDepth(decimal luId)
    {
        if (luId == 0) return 0;

        var description = await Navigator.RequireWarehouse($"DescribeLoadUnitAsync sull'unita' di carico {luId}")
            .DescribeLoadUnitAsync(luId);

        return description?.Depth ?? 0;
    }

    /// <summary>
    /// -1 se la UDC non e' in coda; altrimenti 1 se la sua riga da estrarre e' stata elaborata, 0
    /// se no (o se in coda c'e' solo un ricollocamento).
    /// </summary>
    internal static async Task<int> GetLuExtractionStatus(decimal luId)
    {
        if (luId == 0) return 0;

        return await ExtractionQueue.ReadAsync(async context =>
        {
            var rows = await context.ExtractionQueue.AsNoTracking().Where(i => i.LoadUnitId == luId).ToListAsync();
            if (rows.Count == 0) return -1;
            var extract = rows.FirstOrDefault(i => i.Extract);
            return extract is { Processed: true } ? 1 : 0;
        }, 0);
    }

    /// <summary>
    /// A che profondita' l'unita' di carico sta dentro la sua cella, o 0 se non e' a magazzino.
    /// </summary>
    /// <remarks>
    /// L'altra colonna della stessa riga di <see cref="GetIdCellLocationByLuId"/>, e la risponde la
    /// stessa operazione del confine: <see cref="IWarehouse.LocateLoadUnitAsync"/> porta
    /// <c>CellId</c> e <c>StoringDepth</c> insieme. Vale la stessa nota sul modo di rompersi.
    /// </remarks>
    internal static async Task<int> GetLuStoringDepth(decimal luId)
    {
        if (luId == 0)
            return 0;

        var location = await Navigator.RequireWarehouse($"LocateLoadUnitAsync sull'unita' di carico {luId}")
            .LocateLoadUnitAsync(luId);

        return location?.StoringDepth ?? 0;
    }

    /// <summary>
    /// La coda di un ricollocatore: le righe non ancora elaborate che lo indicano, nell'ordine di
    /// accodamento, nella forma di tabella con cui i consumatori la leggono.
    /// </summary>
    /// <remarks>
    /// Compito 9: la lavorazione di ciascuna assegnazione viene dalla riga di coda, non piu' da una
    /// lettura sul gestionale. Era <c>ProcessingIdsByAssignment</c>, una <c>SELECT</c> con la lista
    /// letterale delle assegnazioni in coda (<c>ASSEGNAZIONE_NOTE</c> - <c>RIGHE_NOTE</c> -
    /// <c>TESTATE_NOTE</c>) fatta a <b>ogni</b> giro di <b>ogni</b> ricollocatore, cioe' dentro il
    /// ciclo veloce; quel JOIN ora lo fa il gestionale, una volta per assegnazione, dentro la query
    /// del lavoro pronto che c'era gia'. Qui non resta nessuna domanda.
    /// </remarks>
    internal static async Task<DataTable?> GetLuToExtract(Items.AutomationDevice item)
    {
        var rows = await ExtractionQueue.ReadAsync(
            context => Persistence.ExtractionQueueStore.PendingForAsync(context, item.ItemGuid),
            new List<Persistence.Entities.ExtractionQueueItem>());

        return rows.Count == 0 ? null : Persistence.ExtractionQueueStore.ToDataTable(rows);
    }


    /// <summary>
    /// La sola metà locale di quella che era <c>IsCellBusyByExtraction</c> (compito 9): se una riga
    /// della coda di estrazione del WCS insiste su questa cella. Non è un dato del gestionale, quindi
    /// resta qui; la metà gestionale (il conteggio su <c>ASSEGNAZIONE_NOTE</c>) è migrata in
    /// <see cref="IWarehouse.GetCellBusyStateAsync"/> (<see cref="CellBusyState.Extraction"/>). Il
    /// chiamante (<c>VerifyStoreInfo</c>) combina le due con un OR, come faceva il metodo originale
    /// internamente con un ritorno anticipato.
    /// </summary>
    internal static async Task<bool> IsExtractionQueuedLocally(decimal cellId)
    {
        if (cellId == 0) return false;

        return await ExtractionQueue.ReadAsync(context => Persistence.ExtractionQueueStore.AnyByCellAsync(context, cellId), false);
    }

    internal static async Task<bool> IsLuPresentInExtranctionLu(decimal luId)
    {
        if (luId == 0)
            return false;

        return await ExtractionQueue.ReadAsync(context => Persistence.ExtractionQueueStore.AnyByLoadUnitAsync(context, luId), false);
    }

    internal static async Task<bool> IsPresentInExtractionLu(decimal assignmentLuId)
    {
        if (assignmentLuId == 0)
            return false;

        return await ExtractionQueue.ReadAsync(context => Persistence.ExtractionQueueStore.AnyByAssignmentAsync(context, assignmentLuId), false);
    }

    /// <summary>
    /// Ricarica le giunzioni del diagramma dal database del WCS e le mette in <c>PathJunctions</c>.
    /// </summary>
    internal static async Task LoadJunctions()
    {
        try
        {
            PathJunctions.Instance.Clear();

            var store = Navigator.Store;
            if (store == null)
            {
                WriteLog("LoadJunctions: il database del WCS non e' aperto, nessuna giunzione ricaricata", LogEntryType.Warning);
                return;
            }

            List<Persistence.Entities.PlantJunction> rows;
            await using (var context = store.CreateContext())
                rows = await Persistence.PlantStore.LoadJunctionsAsync(context);

            foreach (var row in rows)
            {
                var junction = new Items.AutomationJunction(row.SourceItemId, row.DestinationItemId, hydrateOnly: true);
                Persistence.PlantStore.Apply(row, junction);
                PathJunctions.Instance.Add(junction);
            }
        }
        catch (Exception ex)
        {
            WriteLog($"Error On Database->LoadJunctions Description: {ex.Message}\r\nStackTrace: {ex.StackTrace}",
                LogEntryType.Warning);
        }
    }

    private static async Task<decimal> NumExtractionsByCellId(decimal cellId)
    {
        if (cellId == 0) return 0;

        var count = await ExtractionQueue.ReadAsync(context => Persistence.ExtractionQueueStore.CountPendingByCellAsync(context, cellId), 0);
        return count;
    }

    #endregion Internal Methods

}
