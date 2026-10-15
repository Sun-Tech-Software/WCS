using System.Net;
using Xunit;

namespace Wcs.Wms.Contracts.Test;

/// <summary>
/// Un rifiuto e un'assenza del gestionale arrivano con la stessa eccezione - ogni <c>catch</c>
/// esistente continua a vederla uguale - ma chi deve decidere se riprovare adesso li sa separare.
/// </summary>
public sealed class WarehouseUnavailableExceptionTest
{
    private static WarehouseUnavailableException Answered(HttpStatusCode status) =>
        new("op", new HttpRequestException("risposta", null, status));

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public void AFourHundredIsAPermanentRejection(HttpStatusCode status)
    {
        var rejection = Answered(status);

        Assert.Equal(status, rejection.StatusCode);
        Assert.True(rejection.IsPermanentRejection);
    }

    /// <summary>408 e 429 sono il modo in cui il gestionale dice «riprova piu' tardi», e un 5xx e' un suo guasto: tutti e tre guariscono.</summary>
    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void AnAnswerThatCanHealIsNotAPermanentRejection(HttpStatusCode status) =>
        Assert.False(Answered(status).IsPermanentRejection);

    /// <summary>Una connessione caduta o un timeout del client: il gestionale non ha risposto affatto, quindi non ha rifiutato niente.</summary>
    [Fact]
    public void NoAnswerAtAllIsNotAPermanentRejection()
    {
        Assert.False(new WarehouseUnavailableException("op").IsPermanentRejection);
        Assert.False(new WarehouseUnavailableException("op", new HttpRequestException("caduta")).IsPermanentRejection);
        Assert.False(new WarehouseUnavailableException("op", new TaskCanceledException()).IsPermanentRejection);
        Assert.Null(new WarehouseUnavailableException("op", new TaskCanceledException()).StatusCode);
    }
}
