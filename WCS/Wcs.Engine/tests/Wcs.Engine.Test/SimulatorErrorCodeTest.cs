using Wcs.Connection.Interface;
using Wcs.Connection.Interface.ConnectionData;
using Wcs.Connection.Interface.Events;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Il simulatore con un codice d'errore vero: il completamento arriva lo stesso.
/// </summary>
/// <remarks>
/// <para>
/// <c>GetErrorDescription</c> leggeva il codice come esadecimale - <c>int.Parse($"D{errorCode}",
/// HexNumber)</c> - riusando come cifra la <c>D</c> che serve solo a rendere il codice un nome di
/// risorsa valido. I 116 nomi del <c>.resx</c> vanno da <c>D15</c> a <c>D208207873</c>: sessantotto
/// hanno nove cifre, quindi la stringa da leggere ne aveva dieci e un <c>int</c> ne regge otto -
/// <c>OverflowException</c>. L'eccezione avveniva mentre si costruivano gli argomenti
/// dell'<c>Invoke</c>, dentro un <c>Task.Run</c> che nessuno osserva: nessun evento di
/// completamento, nessuna riga di registro, e il dispositivo restava <c>Moving</c> per sempre -
/// <c>TryReleaseHungRunningStep</c> esige <c>Ready</c>, che non arriva.
/// </para>
/// <para>
/// <c>ErrorCodeResult</c> e <c>MoveToResult</c> stanno nella griglia delle proprieta' dell'Editor
/// sotto «Test Options»: e' esattamente il posto in cui si mette un codice vero per far vedere come
/// il sistema reagisce a un guasto.
/// </para>
/// </remarks>
public sealed class SimulatorErrorCodeTest
{
    private const int SupercapFault = 208207873;

    private static RunAsi Source(ResultCodeTypes result, int errorCode) =>
        new(Guid.NewGuid(), "0001", "0002", "0003", timeOutPeriod: 30, delayToStepDone: 1, result, errorCode);

    private static async Task<AsiMovementIsCompletedEventArgs> MovementOf(RunAsi source)
    {
        var connection = new global::Wcs.Connection.Test.Connection(1);
        var completed = new TaskCompletionSource<AsiMovementIsCompletedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.AsiMovementIsCompleted += (_, args) => completed.TrySetResult(args);

        var here = new DataAsi("0001", "0002", "0003");
        connection.MoveLuFromAsiToAsi(source, here, here, here, Guid.NewGuid(), luId: 77);

        var arrived = await Task.WhenAny(completed.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(arrived == completed.Task, "L'evento di completamento non e' arrivato entro cinque secondi.");
        return await completed.Task;
    }

    /// <summary>Un codice d'errore a nove cifre: l'evento parte, e porta il codice che gli e' stato dato.</summary>
    [Fact]
    public async Task ATrueErrorCodeStillCompletesTheMovement()
    {
        var args = await MovementOf(Source(ResultCodeTypes.Error, SupercapFault));

        Assert.Equal(ResultCodeTypes.Error, args.Result);
        Assert.Equal(SupercapFault, args.ErrorCode);
        Assert.Contains($"Code {SupercapFault}", args.ErrorDescription);
        Assert.Contains("Supercap", args.ErrorDescription);
    }

    /// <summary>
    /// Un codice piccolo: il numero mostrato e' quello vero, non <c>0xD15</c>. Con la lettura
    /// esadecimale la descrizione diceva «Code 3349» accanto al testo dell'errore 15.
    /// </summary>
    [Fact]
    public async Task ASmallErrorCodeIsShownAsItselfAndNotAsHexadecimal()
    {
        var args = await MovementOf(Source(ResultCodeTypes.Error, 15));

        Assert.Equal(15, args.ErrorCode);
        Assert.Contains("Code 15", args.ErrorDescription);
        Assert.Contains("Aisle not enabled", args.ErrorDescription);
        Assert.DoesNotContain("3349", args.ErrorDescription);
    }

    /// <summary>
    /// La meta' che deve continuare a riuscire: il percorso felice della dimostrazione, con i
    /// valori predefiniti - nessun errore, codice zero.
    /// </summary>
    [Fact]
    public async Task TheHappyPathStillCompletes()
    {
        var args = await MovementOf(Source(ResultCodeTypes.Success, 0));

        Assert.Equal(ResultCodeTypes.Success, args.Result);
        Assert.Equal(0, args.ErrorCode);
    }
}
