using Microsoft.EntityFrameworkCore;
using Wcs.Engine.ORM;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// La pulizia delle assegnazioni di nota la cui lavorazione non e' piu' in corso
/// (<c>DbInfo.DeleteLuInExtranctionByPausedNote</c>, dentro <c>Navigator.AssignmentsProcess</c>):
/// qui si guarda il <b>ciclo</b>, cioe' quello che succede dopo che il gestionale ha detto quali
/// assegnazioni sono in pausa. Che cosa si chiede e a chi sta invece in
/// <see cref="PausedNoteAssignmentsTest"/>.
/// </summary>
/// <remarks>
/// <para>
/// La callback per riga era un <c>async void</c> passato dove <c>ExecuteReaderAllAsync</c> vuole un
/// <c>Action&lt;T&gt;</c>: ritornava al primo <c>await</c>, quindi il ciclo del lettore proseguiva e
/// il metodo si diceva finito <b>prima</b> che le cancellazioni fossero avvenute. E quel primo
/// <c>await</c> era la chiamata al gestionale: da quando attraversa il confine HTTP puo' sollevare,
/// e un'eccezione da un <c>async void</c> non ha nessun <see cref="Task"/> che la raccolga —
/// termina il processo dell'host. E' la forma buona che questi collaudi sorvegliano.
/// </para>
/// <para>
/// <b>Fino alla revisione del compito 6 il soggetto era un altro:</b> <c>DeleteQueueLuOfNoteDone</c>
/// e il suo <c>DeleteNoteAssignmentsOfDoneProcessing</c>, la prima parte di <c>CheckOperation</c>.
/// Poneva al gestionale la stessa domanda di <c>DeleteLuInExtranctionByPausedNote</c> nello stesso
/// giro, ed e' stata cancellata; questi collaudi sono passati al chiamante che resta, che ha il
/// ciclo della stessa forma.
/// </para>
/// </remarks>
public sealed class NoteAssignmentCleanupTest : IDisposable
{
    private const decimal LoadUnitId = 77;
    private const decimal AssignmentId = 770;

    // Qui c'era un SQL Server irraggiungibile in Navigator.Database: la cancellazione
    // dell'assegnazione (DELETE FROM ASSEGNAZIONE_NOTE) era una scrittura diretta, e la riga di
    // coda usciva solo dopo che quella era stata tentata. Non c'e' piu' niente da tentare: la
    // cancellazione passa dal confine, e il motore non ha piu' nessuna connessione a SQL Server.

    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly FakeWarehouse _warehouse = new();

    public NoteAssignmentCleanupTest()
    {
        Navigator.Store = _database;
        Navigator.Warehouse = _warehouse;
    }

    public void Dispose()
    {
        Navigator.Warehouse = _previousWarehouse;
        Navigator.Store = null;
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// La pulizia <b>attende</b> il confine: finche' il gestionale non ha risposto alla descrizione
    /// dell'unita' di carico, il <see cref="Task"/> restituito non e' completo e la riga di coda e'
    /// ancora li'. Con la callback <c>async void</c> il metodo si diceva finito subito, mentre le
    /// cancellazioni erano ancora da fare — ed e' il gettone del gestionale finto a fermare il
    /// giro, non un'attesa a tempo.
    /// </summary>
    [Fact]
    public async Task TheCleanupIsNotDoneUntilTheWarehouseHasAnswered()
    {
        await AQueuedRowFor(LoadUnitId, AssignmentId);
        _warehouse.PausedNoteAssignments.Add(AssignmentId);
        _warehouse.LoadUnits[LoadUnitId] = ALoadUnit();
        _warehouse.DescribeLoadUnitGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var cleanup = DbInfo.DeleteLuInExtranctionByPausedNote();

        await _warehouse.DescribeLoadUnitAsked;

        Assert.False(cleanup.IsCompleted);
        Assert.Equal([LoadUnitId], await QueuedLoadUnits());

        _warehouse.DescribeLoadUnitGate.SetResult();
        await cleanup;

        Assert.Empty(await QueuedLoadUnits());
        Assert.Contains(nameof(IWarehouse.DescribeLoadUnitAsync), _warehouse.Calls);
    }

    /// <summary>
    /// Il gestionale che smette di rispondere <b>a meta' ciclo</b> risale al chiamante —
    /// <c>Navigator.AssignmentsProcess</c>, che ha il proprio <c>catch</c> per giro — invece di
    /// uscire dal processo su un thread del pool. E la riga di coda resta: al giro dopo si riprova.
    /// </summary>
    /// <remarks>
    /// Il guasto e' sulla sola <see cref="IWarehouse.DescribeLoadUnitAsync"/>, non su tutto il
    /// gestionale: la domanda sulle assegnazioni in pausa deve riuscire, se no si proverebbe la
    /// strada gia' provata in <see cref="PausedNoteAssignmentsTest"/> e non questa.
    /// </remarks>
    [Fact]
    public async Task AWarehouseThatDoesNotAnswerReachesTheCaller()
    {
        await AQueuedRowFor(LoadUnitId, AssignmentId);
        _warehouse.PausedNoteAssignments.Add(AssignmentId);
        _warehouse.UnavailableOperations.Add(nameof(IWarehouse.DescribeLoadUnitAsync));

        await Assert.ThrowsAsync<WarehouseUnavailableException>(DbInfo.DeleteLuInExtranctionByPausedNote);

        Assert.Equal([LoadUnitId], await QueuedLoadUnits());
    }

    /// <summary>
    /// La meta' che deve riuscire su piu' righe: senza questa, una pulizia che si fermasse alla
    /// prima riga passerebbe il primo collaudo.
    /// </summary>
    [Fact]
    public async Task EveryRowOfTheCleanupIsServed()
    {
        await AQueuedRowFor(1, 901);
        await AQueuedRowFor(2, 902);
        await AQueuedRowFor(3, 903);
        _warehouse.PausedNoteAssignments.Add(901);
        _warehouse.PausedNoteAssignments.Add(902);
        _warehouse.PausedNoteAssignments.Add(903);
        _warehouse.LoadUnits[1] = ALoadUnit();
        _warehouse.LoadUnits[2] = ALoadUnit();
        _warehouse.LoadUnits[3] = ALoadUnit();

        await DbInfo.DeleteLuInExtranctionByPausedNote();

        Assert.Empty(await QueuedLoadUnits());
    }

    /// <summary>
    /// <c>CheckOperation</c> gira a ogni giro di <c>Navigator.MasterProcess</c> e il suo guasto non
    /// deve uscire: il ripristino delle celle fallisce, il metodo ritorna lo stesso.
    /// </summary>
    /// <remarks>
    /// <b>Che ne e' della segnalazione I-2 dell'ondata 1.</b> Diceva che i due <c>try</c> di
    /// <c>CheckOperation</c> dovevano restare separati, perche' con un <c>try</c> solo un gestionale
    /// non ancora acceso al primo avvio usciva dalla prima parte e <b>saltava l'intero ciclo</b> di
    /// ripristino delle celle, che con il gestionale non c'entra niente. La revisione del compito 6
    /// ha cancellato quella prima parte: adesso le parti sono una sola, dentro
    /// <c>CheckOperation</c> non si tocca piu' il confine, e il caso dell'I-2 non e' piu'
    /// componibile. Quello che resta da sorvegliare e' che il <c>try</c> superstite ci sia.
    /// </remarks>
    [Fact]
    public async Task CheckOperationDoesNotLetTheCellResetFailureOut()
    {
        var cellResetRan = false;

        await DbInfo.CheckOperation(() =>
        {
            cellResetRan = true;
            throw new InvalidOperationException("il ripristino delle celle non riesce");
        });

        Assert.True(cellResetRan);
    }

    private static LoadUnitDescription ALoadUnit() =>
        new(LoadUnitId, "UDC-077", LoadUnitTypeId: 1, HandlingMode: 0, Tare: 0, Height: 0, Width: 0, Depth: 0,
            Weight: 0);

    private static Task AQueuedRowFor(decimal loadUnitId, decimal assignmentId) =>
        ExtractionQueue.UpsertAsync(new ExtractionQueueItem
        {
            LoadUnitId = loadUnitId,
            AssignmentId = assignmentId,
            SourceItemId = Guid.NewGuid(),
            RelocatorItemId = Guid.NewGuid(),
            Extract = true,
            DoorCode = "P01A",
            WorkstationId = "PC-01"
        });

    private async Task<List<decimal>> QueuedLoadUnits()
    {
        await using var context = _database.CreateContext();
        return await context.ExtractionQueue.Select(r => r.LoadUnitId).OrderBy(id => id).ToListAsync();
    }
}
