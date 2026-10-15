using Wcs.Engine.ORM;
using Wcs.Engine.Persistence;
using Wcs.Engine.Persistence.Entities;
using Wcs.Wms.Contracts;
using Wcs.Wms.Contracts.Test;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// Compito 5: «fra queste righe di estrazione, quali sono in pausa?». La domanda la pone
/// <c>DbInfo.DeleteLuInExtranctionByPausedExtractionLu</c>, e la risposta decide una cosa sola:
/// quali righe escono dalla coda di estrazione del WCS.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il giro e' misto apposta, ed e' il punto del metodo.</b> La coda vive nel database del WCS,
/// che il gestionale non vede; le lavorazioni vivono nel gestionale, che il WCS non deve piu'
/// leggere. Il motore parte dalla coda - poche righe - e chiede al gestionale solo di quelle: il
/// contrario (tutte le righe sospese dell'impianto, 422.317 sull'impianto di prova, in un
/// <c>IN</c> verso SQLite) faceva cadere il processo delle assegnazioni a ogni giro. Prima la
/// domanda era una query in T-SQL diretto su tre tabelle del gestionale
/// (<c>RIGHE_ESTRAZIONI_UDC</c>, <c>TESTATE_ESTRAZIONI_UDC</c>, <c>LAVORAZIONI_ESTRAZIONI_UDC</c>),
/// spezzata in blocchi da mille parametri dal motore stesso; adesso la lista intera attraversa il
/// confine e i blocchi sono affare di chi esegue.
/// </para>
/// <para>
/// <b>Cambia il modo di rompersi, ed e' voluto.</b> <c>PausedAmongAsync</c> registrava l'errore
/// della lettura e rispondeva <see langword="null"/>, che il chiamante leggeva come «nessuna in
/// pausa»: la pulizia saltava in silenzio e le righe di una lavorazione sospesa restavano in coda
/// a farsi servire. Adesso un gestionale che non risponde solleva
/// <see cref="WarehouseUnavailableException"/>, che si ferma nel <c>catch</c> per giro di
/// <c>Navigator.AssignmentsProcess</c> - lo stesso che copriva gia' la
/// <c>GetPendingExtractionWorkAsync</c> due righe piu' sotto - e il giro dopo riprova.
/// </para>
/// </remarks>
public sealed class PausedExtractionRowsTest : IDisposable
{
    private static readonly Guid Relocator = Guid.NewGuid();

    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly IWarehouse? _previousWarehouse = Navigator.Warehouse;
    private readonly FakeWarehouse _warehouse = new();

    public PausedExtractionRowsTest()
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
    /// Una riga di coda. Senza assegnazione di nota apposta: e' una estrazione per unita' di carico,
    /// la famiglia di cui parla questo compito, e la cancellazione dell'assegnazione
    /// (<c>DeleteAssignedLuByIdEstrazioneUdc</c>, che va in T-SQL diretto) non ha quindi niente da
    /// fare - e' la sola ragione per cui queste prove arrivano in fondo senza un SQL Server.
    /// </summary>
    private static async Task QueuedAsync(decimal loadUnitId, decimal? extractionRowId) =>
        await ExtractionQueue.UpsertAsync(new ExtractionQueueItem
        {
            LoadUnitId = loadUnitId,
            ExtractionRowId = extractionRowId,
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
    /// La domanda si fa sulle righe della coda - <b>solo</b> quelle che hanno una riga di
    /// estrazione - e a uscire dalla coda sono soltanto quelle che il gestionale dice in pausa.
    /// <para>
    /// Le due asserzioni non sono ripetute: la prima dice <b>che cosa si e' chiesto</b> (una
    /// domanda con gli identificativi sbagliati riceverebbe lo stesso una risposta plausibile), la
    /// seconda <b>che cosa se n'e' tratto</b>. Ce n'era una terza, <c>Navigator.Database</c> nullo,
    /// per dire che la query di prima ci sarebbe morta sopra: quel campo non esiste piu' e a
    /// tenere chiusa la porta e' <c>EngineDependenciesTest</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task OnlyTheRowsTheWarehouseCallsPausedLeaveTheQueue()
    {
        await QueuedAsync(1, 501);
        await QueuedAsync(2, 502);
        await QueuedAsync(3, extractionRowId: null); // una riga di coda che con le estrazioni non c'entra

        _warehouse.PausedExtractionRows.Add(501);

        await DbInfo.DeleteLuInExtranctionByPausedExtractionLu();

        Assert.Equal(new decimal[] { 501, 502 }, Assert.Single(_warehouse.PausedExtractionRowsAsked));
        Assert.Equal(new decimal[] { 2, 3 }, await QueuedLoadUnitsAsync());
    }

    /// <summary>
    /// La meta' che deve riuscire al contrario, senza la quale una pulizia che cancellasse tutto
    /// passerebbe quella qui sopra: nessuna delle righe e' in pausa, la coda resta intera, e la
    /// domanda e' comunque <b>stata fatta</b> - e' la differenza fra questo e un motore che non la
    /// pone affatto.
    /// </summary>
    [Fact]
    public async Task WithNothingPausedTheQueueIsLeftAloneAndTheQuestionIsStillAsked()
    {
        await QueuedAsync(1, 501);
        await QueuedAsync(2, 502);

        await DbInfo.DeleteLuInExtranctionByPausedExtractionLu();

        Assert.Contains(nameof(IWarehouse.GetPausedExtractionRowsAsync), _warehouse.Calls);
        Assert.Equal(new decimal[] { 1, 2 }, await QueuedLoadUnitsAsync());
    }

    /// <summary>
    /// Nessuna riga di coda legata a un'estrazione: il confine non si tocca proprio. La guardia e'
    /// gia' del metodo (la coda vuota esce prima), e vale la pena provarla perche' questa pulizia
    /// gira a <b>ogni</b> giro di <c>Navigator.MasterProcess</c>, cioe' circa una volta al secondo,
    /// e su un impianto fermo la coda e' vuota quasi sempre.
    /// </summary>
    [Fact]
    public async Task AnEmptyQueueDoesNotAskTheWarehouseAnything()
    {
        await QueuedAsync(1, extractionRowId: null);

        await DbInfo.DeleteLuInExtranctionByPausedExtractionLu();

        Assert.DoesNotContain(nameof(IWarehouse.GetPausedExtractionRowsAsync), _warehouse.Calls);
        Assert.Empty(_warehouse.PausedExtractionRowsAsked);
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
        await QueuedAsync(1, 501);
        _warehouse.PausedExtractionRows.Add(501);
        _warehouse.Unavailable = true;

        await Assert.ThrowsAsync<WarehouseUnavailableException>(DbInfo.DeleteLuInExtranctionByPausedExtractionLu);

        Assert.Equal(new decimal[] { 1 }, await QueuedLoadUnitsAsync());
    }
}
