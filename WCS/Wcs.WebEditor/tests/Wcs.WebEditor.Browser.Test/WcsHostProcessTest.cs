using Xunit;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// Le decisioni che <see cref="WcsHostProcess"/> prende da sola, guardate senza avviare niente.
/// </summary>
public sealed class WcsHostProcessTest
{
    /// <summary>
    /// La riga che Kestrel stampa quando la porta gli e' stata portata via fra il momento in cui
    /// questa classe l'ha presa per vedere che fosse libera e il momento in cui l'host ha provato ad
    /// aprirla. Copiata da un avvio vero, non riscritta a memoria.
    /// </summary>
    private const string KestrelPortTaken =
        "Unhandled exception. System.IO.IOException: Failed to bind to address http://127.0.0.1:51234: address already in use.";

    [Fact]
    public void RecognisesKestrelSayingThePortWasTaken()
    {
        var log = string.Join(Environment.NewLine,
            "info: Microsoft.Hosting.Lifetime[0]",
            KestrelPortTaken,
            " ---> System.Net.Sockets.SocketException (10048): Only one usage of each socket address is normally permitted.");

        Assert.True(WcsHostProcess.LooksLikePortConflict(log));
    }

    [Fact]
    public void DoesNotReadEveryFailedStartAsAPortConflict()
    {
        // Il caso che conta: un host che non parte per un motivo SUO. Se lo si leggesse come una
        // porta occupata, la prova lo riavvierebbe tre volte e poi racconterebbe una corsa sulla
        // porta al posto del guasto vero - un messaggio sbagliato e' peggio di nessun messaggio.
        var log = string.Join(Environment.NewLine,
            "Unhandled exception. System.InvalidOperationException: Wcs:ApiKey manca o e' quella di esempio: senza chiave l'host non parte.",
            "   at Program.<Main>$(String[] args)");

        Assert.False(WcsHostProcess.LooksLikePortConflict(log));
    }

    [Fact]
    public void ReadsAnEmptyLogAsNoConflict()
    {
        // Un host morto senza dire niente non e' una porta occupata: riprovare su un'altra porta
        // nasconderebbe il silenzio invece di riportarlo.
        Assert.False(WcsHostProcess.LooksLikePortConflict(string.Empty));
    }
}
