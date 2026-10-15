using System.Text.Json;
using Wcs.Wms.Contracts;
using Xunit;

namespace Wcs.Wms.Contracts.Test;

/// <summary>
/// Voce 45: la forma del fatto «l'estrazione prelevata e' diventata un deposito» sul filo, e il
/// gestionale finto che lo riceve nelle prove del motore.
/// </summary>
public class PickedExtractionBecameDepositContractTest
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static PickedExtractionBecameDepositRequest Request(decimal assignmentId = 902m, decimal extractionRowId = 8045m) =>
        new(Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444"),
            Guid.Parse("bbbbbbbb-1111-2222-3333-444444444444"),
            4711m,
            assignmentId,
            Guid.Parse("cccccccc-1111-2222-3333-444444444444"),
            extractionRowId,
            "KazamControl.Process",
            "mandata in magazzino");

    [Fact]
    public void TheRequestRoundTripsThroughJson()
    {
        var request = Request();

        var copy = JsonSerializer.Deserialize<PickedExtractionBecameDepositRequest>(JsonSerializer.Serialize(request));

        Assert.Equal(request, copy);
    }

    /// <summary>
    /// Gli esiti viaggiano come numeri, e i numeri sono quelli scritti nel tipo: un riordino dei nomi
    /// cambierebbe il significato di una risposta del gestionale senza cambiarne la forma.
    /// </summary>
    [Fact]
    public void TheOutcomesTravelAsTheirWrittenNumbers()
    {
        var result = JsonSerializer.Deserialize<PickedExtractionBecameDepositResult>(
            """{"assignment":2,"extractionRow":1,"detail":"spedizione spedita"}""", Web);

        Assert.Equal(new PickedExtractionBecameDepositResult(
            PickedExtractionReopenOutcome.OrderAlreadyClosed, PickedExtractionReopenOutcome.NothingToReopen, "spedizione spedita"), result);
        Assert.Equal(0, (int)PickedExtractionReopenOutcome.Reopened);
        Assert.Equal(1, (int)PickedExtractionReopenOutcome.NothingToReopen);
        Assert.Equal(2, (int)PickedExtractionReopenOutcome.OrderAlreadyClosed);
    }

    /// <summary>
    /// Un esito mancante non si legge come 0, cioe' «riaperto»: la risposta senza esiti non e' una
    /// risposta.
    /// </summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"assignment":0,"detail":"x"}""")]
    [InlineData("""{"extractionRow":0}""")]
    public void AResultWithoutBothOutcomesDoesNotRead(string json)
    {
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<PickedExtractionBecameDepositResult>(json, Web));
    }

    [Fact]
    public void TheDetailMayBeMissing()
    {
        var result = JsonSerializer.Deserialize<PickedExtractionBecameDepositResult>(
            """{"assignment":0,"extractionRow":0}""", Web);

        Assert.Equal(new PickedExtractionBecameDepositResult(
            PickedExtractionReopenOutcome.Reopened, PickedExtractionReopenOutcome.Reopened, null), result);
    }

    /// <summary>
    /// Per default il finto risponde come un gestionale con l'ordine aperto: riapre quello che la
    /// missione aveva servito e non trova niente da riaprire dove non aveva servito niente. E
    /// registra la richiesta intera.
    /// </summary>
    [Theory]
    [InlineData(902, 8045, PickedExtractionReopenOutcome.Reopened, PickedExtractionReopenOutcome.Reopened)]
    [InlineData(902, 0, PickedExtractionReopenOutcome.Reopened, PickedExtractionReopenOutcome.NothingToReopen)]
    [InlineData(0, 8045, PickedExtractionReopenOutcome.NothingToReopen, PickedExtractionReopenOutcome.Reopened)]
    public async Task TheFakeReopensWhatTheMissionServedByDefaultAndRecordsTheRequest(
        decimal assignmentId, decimal extractionRowId,
        PickedExtractionReopenOutcome assignment, PickedExtractionReopenOutcome extractionRow)
    {
        var warehouse = new FakeWarehouse();
        var request = Request(assignmentId, extractionRowId);

        var result = await warehouse.ReportPickedExtractionBecameDepositAsync(request);

        Assert.Equal(new PickedExtractionBecameDepositResult(assignment, extractionRow, null), result);
        Assert.Equal([request], warehouse.PickedExtractionsBecameDeposit);
        Assert.Equal(["ReportPickedExtractionBecameDepositAsync"], warehouse.Calls);
    }

    [Theory]
    [InlineData(PickedExtractionReopenOutcome.Reopened)]
    [InlineData(PickedExtractionReopenOutcome.NothingToReopen)]
    [InlineData(PickedExtractionReopenOutcome.OrderAlreadyClosed)]
    public async Task TheFakeAnswersTheOutcomeItIsGiven(PickedExtractionReopenOutcome outcome)
    {
        var warehouse = new FakeWarehouse
        {
            PickedExtractionBecameDepositAnswer = _ => new PickedExtractionBecameDepositResult(outcome, outcome, "dettaglio")
        };

        var result = await warehouse.ReportPickedExtractionBecameDepositAsync(Request());

        Assert.Equal(new PickedExtractionBecameDepositResult(outcome, outcome, "dettaglio"), result);
    }

    /// <summary>
    /// Il gestionale muto solleva e non registra il fatto: la richiesta resta solo fra le chiamate,
    /// come per le altre segnalazioni. Vale per tutt'e due i modi di chiederlo.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheMuteFakeThrowsAndDoesNotRecordTheFact(bool allMute)
    {
        var warehouse = new FakeWarehouse();
        if (allMute) warehouse.Unavailable = true;
        else warehouse.UnavailableOperations.Add(nameof(IWarehouse.ReportPickedExtractionBecameDepositAsync));

        var ex = await Assert.ThrowsAsync<WarehouseUnavailableException>(
            () => warehouse.ReportPickedExtractionBecameDepositAsync(Request()));

        Assert.Equal("ReportPickedExtractionBecameDepositAsync", ex.Operation);
        Assert.Empty(warehouse.PickedExtractionsBecameDeposit);
        Assert.Equal(["ReportPickedExtractionBecameDepositAsync"], warehouse.Calls);
    }

    /// <summary>Il rifiuto (il 409 del gestionale vero) si chiede con <see cref="FakeWarehouse.Fault"/>.</summary>
    [Fact]
    public async Task TheFakeCanRejectTheFact()
    {
        var warehouse = new FakeWarehouse
        {
            Fault = operation => operation == nameof(IWarehouse.ReportPickedExtractionBecameDepositAsync)
                ? FakeWarehouse.Rejection(operation)
                : null
        };

        var ex = await Assert.ThrowsAsync<WarehouseUnavailableException>(
            () => warehouse.ReportPickedExtractionBecameDepositAsync(Request()));

        Assert.True(ex.IsPermanentRejection);
        Assert.Empty(warehouse.PickedExtractionsBecameDeposit);
    }
}
