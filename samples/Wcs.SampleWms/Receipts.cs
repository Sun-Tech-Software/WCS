namespace Wcs.SampleWms;

/// <summary>
/// The receipts of the writes that the WCS sends from a durable queue, one per <c>OperationId</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> The WCS writes some facts into a local queue first and sends them until the WMS
/// answers (at-least-once): <c>load-units/allocate</c>, <c>load-units/release</c>,
/// <c>reservations/release-load-unit-cell</c> and <c>assignments/picked-extraction-became-deposit</c>.
/// If an answer is lost on the network, the same request arrives again. The WMS must not apply it
/// twice - a load unit released twice, a row reopened twice - so it keeps what it answered the first
/// time and answers the same again.
/// </para>
/// <para>
/// <b>The rule.</b> Same id and same content: the first answer, without applying anything. Same id and
/// different content: <b>409 Conflict</b> - it is a bug on one side, and the WCS parks the request
/// for a person to look at instead of retrying it forever. An empty id (<see cref="Guid.Empty"/>) is
/// not a queued request (the WCS calls allocate synchronously for an operator cell): no receipt.
/// </para>
/// <para>
/// <b>Same content means the same values, not the same text.</b> The requests are compared as the
/// records they bind to, with record equality: <c>5003</c> and <c>5003.0</c> are the same decimal, and
/// camelCase or PascalCase bind to the same record. Comparing the JSON text instead would answer 409 to
/// a legitimate repetition that a proxy or a client in another language rewrote. Record equality is
/// value equality only for scalar members: the four requests have no list, and a request that had one
/// would need its own comparison.
/// </para>
/// <para>
/// In a real WMS the receipt is a row written in the same transaction as the change it records;
/// here a dictionary, which lives as long as the process.
/// </para>
/// </remarks>
public sealed class Receipts
{
    private readonly Dictionary<Guid, Receipt> _receipts = [];
    private readonly Lock _gate = new();

    private sealed record Receipt(string Route, object Request, IResult Answer);

    /// <summary>
    /// Apply <paramref name="apply"/> once per <paramref name="operationId"/>, and answer the same for a
    /// repetition of the same request; 409 for the same id with a different request.
    /// </summary>
    public IResult Once<TRequest>(Guid operationId, string route, TRequest request, Func<IResult> apply)
        where TRequest : notnull
    {
        if (operationId == Guid.Empty) return apply();

        // One lock across "look up, apply, record": two copies of the same request arriving together
        // must not both apply.
        lock (_gate)
        {
            if (_receipts.TryGetValue(operationId, out var receipt))
            {
                return receipt.Route == route && receipt.Request.Equals(request)
                    ? receipt.Answer
                    : Results.Problem(
                        $"Operation {operationId} was already received with a different content. The first one was applied; " +
                        "this one is refused.",
                        statusCode: StatusCodes.Status409Conflict, title: "Same OperationId, different request");
            }

            var answer = apply();

            // Only a success is a receipt: a refusal (400) applied nothing, and a corrected request
            // with the same id must be able to go through.
            if (answer is IStatusCodeHttpResult { StatusCode: >= 200 and < 300 })
                _receipts[operationId] = new Receipt(route, request, answer);

            return answer;
        }
    }

    /// <summary>How many receipts are kept.</summary>
    public int Count
    {
        get
        {
            lock (_gate) return _receipts.Count;
        }
    }
}
