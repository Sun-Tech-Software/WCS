using Wcs.Engine.ORM;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// Compito 6: «fra queste assegnazioni di nota, quali sono in pausa?». La domanda la pone
/// <c>DbInfo.DeleteLuInExtranctionByPausedNote</c>, dentro il giro periodico di
/// <c>Navigator.MasterProcess</c>, e la risposta decide una cosa sola: quali righe escono dalla
/// coda di estrazione del WCS.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il difetto C2 era l'altro chiamante</b>, ed e' la ragione di questo compito.
/// <c>DbInfo.DeleteQueueLuOfNoteDone</c>, prima parte di <c>DbInfo.CheckOperation</c>, leggeva
/// <c>ASSEGNAZIONE_NOTE</c> per intero, senza nessuna chiave - tutte le assegnazioni sospese
/// dell'impianto - e da quelle risaliva alle righe di coda da cancellare. Il compito 6 l'ha fatta
/// partire dalla coda, che ha poche righe; la revisione l'ha poi cancellata, perche' cosi' la
/// stessa domanda partiva <b>due volte per giro</b> e la seconda, quando le arrivava il turno, non
/// trovava piu' niente da togliere. Oggi il chiamante e' uno solo, ed e' quello che queste prove
/// esercitano.
/// </para>
/// <para>
/// <b>Cambia il modo di rompersi, ed e' voluto.</b> <c>PausedAmongAsync</c> registrava l'errore
/// della lettura e rispondeva <see langword="null"/>, che il chiamante leggeva come «nessuna in
/// pausa»: la pulizia saltava in silenzio e le righe di una lavorazione sospesa restavano in coda a
/// farsi servire. Adesso un gestionale che non risponde solleva
/// <see cref="WarehouseUnavailableException"/>, e a raccoglierla e' il <c>catch</c> per giro di
/// <c>Navigator.AssignmentsProcess</c>.
/// </para>
/// <para>
/// <b>Su SQL Server non resta piu' niente.</b> La cancellazione dell'assegnazione
/// (<c>DELETE FROM ASSEGNAZIONE_NOTE</c>, in <c>DbInfo.DeleteAssignedLuByAssignmentId</c>) era una
/// scrittura diretta sui dati del gestionale, e per essa queste prove puntavano
/// <c>Navigator.Database</c> a un'istanza che non esiste: falliva, veniva registrata, e il giro
/// proseguiva. Il compito 13 l'ha sostituita con lo scarto dichiarato al confine, e il compito 16
/// ha tolto al motore la connessione stessa: qui non c'e' piu' nessun SQL Server da rendere
/// irraggiungibile.
/// </para>
/// </remarks>
public sealed class PausedNoteAssignmentsTest : IDisposable
{
    private static readonly Guid Relocator = Guid.NewGuid();

    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly FakeWarehouse _warehouse = new();

    public PausedNoteAssignmentsTest()
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

    private static Task QueuedAsync(decimal loadUnitId, decimal? assignmentId) =>
        ExtractionQueue.UpsertAsync(new ExtractionQueueItem
        {
            LoadUnitId = loadUnitId,
            AssignmentId = assignmentId,
            SourceItemId = Guid.NewGuid(),
            RelocatorItemId = Relocator,
            DoorCode = "P01A",
            WorkstationId = "PC-01"
        });

    private async Task<decimal[]> QueuedLoadUnitsAsync()
    {
        await using var context = _database.CreateContext();
        return (await ExtractionQueueStore.PendingForAsync(context, Relocator))
            .Select(r => r.LoadUnitId).ToArray();
    }

    /// <summary>
    /// La domanda si fa sulle righe della coda - <b>solo</b> quelle che hanno un'assegnazione - e a
    /// uscire dalla coda sono soltanto quelle che il gestionale dice in pausa.
    /// <para>
    /// Le due asserzioni non sono ripetute: la prima dice <b>che cosa si e' chiesto</b> (una
    /// domanda con gli identificativi sbagliati riceverebbe lo stesso una risposta plausibile), la
    /// seconda <b>che cosa se n'e' tratto</b>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task OnlyTheAssignmentsTheWarehouseCallsPausedLeaveTheQueue()
    {
        await QueuedAsync(1, 901);
        await QueuedAsync(2, 902);
        await QueuedAsync(3, assignmentId: null); // una riga di coda che con le note non c'entra

        _warehouse.PausedNoteAssignments.Add(901);

        await DbInfo.DeleteLuInExtranctionByPausedNote();

        Assert.Equal(new decimal[] { 901, 902 }, Assert.Single(_warehouse.PausedNoteAssignmentsAsked));
        Assert.Equal(new decimal[] { 2, 3 }, await QueuedLoadUnitsAsync());
    }

    /// <summary>
    /// <b>Compito 13</b>: l'assegnazione che esce dalla coda perche' la sua lavorazione non e' piu'
    /// in corso il motore la <b>scarta</b> dicendo perche', invece di cancellarla da se'. Era
    /// l'ultima meta' di questo giro rimasta su SQL Server
    /// (<c>DbInfo.DeleteAssignedLuByIdEstrazioneUdc</c> → <c>DeleteAssignedLuByAssignmentId</c>),
    /// quella per cui le prove di questa classe dovevano puntare a un'istanza che non esiste.
    /// <para>
    /// Che quella meta' sia finita ora lo dice il compilatore: il motore non ha piu' nessuna
    /// connessione a SQL Server da rendere irraggiungibile, e a tenere chiusa la porta e'
    /// <c>EngineDependenciesTest</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ThePausedAssignmentThatLeavesTheQueueIsReportedAsRejected()
    {
        await QueuedAsync(1, 901);
        await QueuedAsync(2, 902);

        _warehouse.PausedNoteAssignments.Add(901);

        await DbInfo.DeleteLuInExtranctionByPausedNote();

        var rejected = Assert.Single(_warehouse.RejectedNoteAssignments);
        Assert.Equal(901m, rejected.AssignmentId);
        Assert.Equal(1m, rejected.LoadUnitId);
        Assert.NotEmpty(rejected.Reason);

        Assert.Equal(new decimal[] { 2 }, await QueuedLoadUnitsAsync());
    }

    /// <summary>
    /// La meta' che deve riuscire al contrario, senza la quale una pulizia che cancellasse tutto
    /// passerebbe quella qui sopra: nessuna delle assegnazioni e' in pausa, la coda resta intera, e
    /// la domanda e' comunque <b>stata fatta</b>, e al confine: la scansione di prima era una
    /// query su SQL Server, e il motore non ne ha piu' una.
    /// </summary>
    [Fact]
    public async Task WithNothingPausedTheQueueIsLeftAloneAndTheQuestionIsStillAsked()
    {
        await QueuedAsync(1, 901);
        await QueuedAsync(2, 902);

        await DbInfo.DeleteLuInExtranctionByPausedNote();

        Assert.Contains(nameof(IWarehouse.GetPausedNoteAssignmentsAsync), _warehouse.Calls);
        Assert.Equal(new decimal[] { 1, 2 }, await QueuedLoadUnitsAsync());
    }

    /// <summary>
    /// Nessuna riga di coda legata a un'assegnazione: il confine non si tocca proprio. Vale la pena
    /// provarlo perche' questa pulizia gira a <b>ogni</b> giro di <c>Navigator.MasterProcess</c>,
    /// cioe' circa una volta al secondo, e su un impianto fermo la coda e' vuota quasi sempre.
    /// </summary>
    [Fact]
    public async Task AnEmptyQueueDoesNotAskTheWarehouseAnything()
    {
        await QueuedAsync(1, assignmentId: null);

        await DbInfo.DeleteLuInExtranctionByPausedNote();
        await DbInfo.CheckOperation();

        Assert.DoesNotContain(nameof(IWarehouse.GetPausedNoteAssignmentsAsync), _warehouse.Calls);
        Assert.Empty(_warehouse.PausedNoteAssignmentsAsked);
    }

    /// <summary>
    /// Il gestionale non risponde: l'eccezione risale e la coda resta intera. E' il cambiamento
    /// voluto - <c>PausedAmongAsync</c> rispondeva <see langword="null"/>, indistinguibile da
    /// «nessuna in pausa» - e chi lo raccoglie e' il <c>catch</c> per giro di
    /// <c>AssignmentsProcess</c>, che registra e riprova al giro dopo.
    /// </summary>
    [Fact]
    public async Task AWarehouseThatDoesNotAnswerRaisesInsteadOfLeavingTheQueueUncleaned()
    {
        await QueuedAsync(1, 901);
        _warehouse.PausedNoteAssignments.Add(901);
        _warehouse.Unavailable = true;

        await Assert.ThrowsAsync<WarehouseUnavailableException>(DbInfo.DeleteLuInExtranctionByPausedNote);

        Assert.Equal(new decimal[] { 1 }, await QueuedLoadUnitsAsync());
    }

    /// <summary>
    /// <b>Il difetto C2, e una domanda sola per giro.</b> Il giro periodico di
    /// <c>Navigator.MasterProcess</c> chiama in fila <c>AssignmentsProcess</c> - dove sta questa
    /// pulizia - e <c>DbInfo.CheckOperation</c>: due assegnazioni in coda, la prima in pausa, un
    /// giro intero, e in coda resta solo la seconda avendo chiesto al gestionale <b>una volta
    /// sola</b>.
    /// </summary>
    /// <remarks>
    /// La scansione senza chiave di <c>DeleteQueueLuOfNoteDone</c> - il difetto C2 - e' stata
    /// dapprima trasformata nella stessa domanda con chiave, e poi cancellata del tutto dalla
    /// revisione: la domanda partiva due volte per giro, e la seconda non poteva nemmeno piu'
    /// cancellare niente, perche' la prima aveva gia' tolto dalla coda le righe che l'altra
    /// cercava. <see cref="DbInfo.DeleteLuInExtranctionByPausedNote"/> e' l'unico posto da cui
    /// questa domanda parte, e <c>CheckOperation</c> non la pone piu'.
    /// </remarks>
    [Fact]
    public async Task TheWholeTurnAsksTheWarehouseOnlyOnce()
    {
        await QueuedAsync(1, 901);
        await QueuedAsync(2, 902);

        _warehouse.PausedNoteAssignments.Add(901);

        await DbInfo.DeleteLuInExtranctionByPausedNote();
        await DbInfo.CheckOperation();

        Assert.Equal(new decimal[] { 901, 902 }, Assert.Single(_warehouse.PausedNoteAssignmentsAsked));
        Assert.Equal(new decimal[] { 2 }, await QueuedLoadUnitsAsync());
    }

    /// <summary>
    /// La meta' contraria sul giro intero: nessuna assegnazione in pausa, la coda resta dov'e', e
    /// la domanda e' stata posta <b>una volta sola</b> e senza nessun SQL Server - la scansione di
    /// prima leggeva <c>ASSEGNAZIONE_NOTE</c> per intero.
    /// </summary>
    /// <remarks>
    /// Senza questa, una pulizia che cancellasse tutto passerebbe quella qui sopra: li' la riga che
    /// resta in coda e' una sola, qui la coda deve restare intera perche' il gestionale non ha
    /// messo niente in pausa.
    /// </remarks>
    [Fact]
    public async Task WithNothingPausedTheWholeTurnStillAsksOnlyOnce()
    {
        await QueuedAsync(1, 901);

        await DbInfo.DeleteLuInExtranctionByPausedNote();
        await DbInfo.CheckOperation();

        Assert.Equal(new decimal[] { 901 }, Assert.Single(_warehouse.PausedNoteAssignmentsAsked));
        Assert.Equal(new decimal[] { 1 }, await QueuedLoadUnitsAsync());
    }
}
