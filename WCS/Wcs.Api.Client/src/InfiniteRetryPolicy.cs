using Microsoft.AspNetCore.SignalR.Client;

namespace Wcs.Api.Client;

/// <summary>
/// Ritenta la connessione all'hub <b>per sempre</b>, con attesa crescente fino a trenta secondi.
/// </summary>
/// <remarks>
/// La politica predefinita di <c>WithAutomaticReconnect()</c> fa quattro tentativi in una
/// trentina di secondi e poi si arrende: un client che era aperto durante un riavvio dell'host
/// resta muto finché qualcuno non lo riavvia a mano, e chi guarda lo schermo vede uno stato fermo
/// senza sapere che è fermo. Qui non ci si arrende: i primi tentativi sono ravvicinati, perché la
/// maggior parte delle interruzioni dura pochi secondi, poi si rallenta fino a un tentativo ogni
/// trenta secondi per non martellare un host che non c'è.
/// </remarks>
public sealed class InfiniteRetryPolicy : IRetryPolicy
{
    /// <summary>Le attese dei primi tentativi, in secondi; dopo si va a <see cref="MaxDelay"/>.</summary>
    private static readonly TimeSpan[] FirstDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20)
    ];

    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
        retryContext.PreviousRetryCount < (uint)FirstDelays.Length
            ? FirstDelays[retryContext.PreviousRetryCount]
            : MaxDelay;
}
