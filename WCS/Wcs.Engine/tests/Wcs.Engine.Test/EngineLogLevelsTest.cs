using Log.Interface;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// Che cosa passa da <c>Navigator.WriteLog</c> con la diagnostica spenta, che e' la configurazione
/// di serie dell'host (<c>Wcs:ActivateDebugLog</c> falso in <c>Wcs.Host/src/appsettings.json</c>).
/// </summary>
/// <remarks>
/// <para>
/// La voce <b>D94</b> del registro: il flag governava insieme la <i>verbosita'</i> e la
/// <i>gravita'</i>, quindi con la diagnostica spenta tacevano anche le 97 chiamate di livello
/// Errore e Avviso del solo <c>Wcs.Engine/src</c> — fra cui l'errore di ogni scrittura fallita su
/// SQL Server, il rifiuto di <c>ExecuteStep</c>, lo scioglimento di un gettone appeso e
/// <c>AutomationDevice.LogError</c>, dove finisce ogni <c>catch</c> dei gestori. Alzare il flag non
/// era la cura: riaccende anche le righe informative sui percorsi caldi, che e' la ragione per cui
/// e' spento.
/// </para>
/// <para>
/// <b>E' una scelta, non un difetto riparato:</b> da adesso in produzione la console e il registro
/// dell'host ricevono le segnalazioni di errore e di avviso del motore, che prima non ricevevano.
/// </para>
/// </remarks>
public sealed class EngineLogLevelsTest : IDisposable
{
    private readonly bool _previousDebugLog = Navigator.ActivateDebugLog;

    public void Dispose()
    {
        Navigator.ActivateDebugLog = _previousDebugLog;
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task WithDiagnosticsOffErrorsAndWarningsAreStillWritten()
    {
        Navigator.ActivateDebugLog = false;
        using var log = new RecordingLogWriter();

        Navigator.WriteLog("un errore del motore", LogEntryType.Error);
        Navigator.WriteLog("un avviso del motore", LogEntryType.Warning);

        await log.Wait((d, l) => d == "un errore del motore" && l == LogEntryType.Error)
            .WaitAsync(TimeSpan.FromSeconds(30));
        await log.Wait((d, l) => d == "un avviso del motore" && l == LogEntryType.Warning)
            .WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// La meta' che deve restare com'era: la diagnostica resta diagnostica. Senza questa, un
    /// <c>WriteLog</c> che scrivesse tutto sempre passerebbe il collaudo qui sopra, e riaccenderebbe
    /// le righe informative sui percorsi caldi.
    /// </summary>
    /// <remarks>
    /// <b>Il discriminante e' la console, e la ragione e' la voce m4 della revisione dell'ondata
    /// 1.</b> La prima versione mandava una riga d'errore come sentinella, l'aspettava, e poi
    /// asseriva che l'informativa non fosse arrivata, con il commento «passa dallo stesso
    /// <c>Task.Run</c>». Non e' vero: <c>WriteLog</c> ne lancia uno <b>per chiamata</b>, e il thread
    /// pool non garantisce l'ordine di completamento — da un thread del pool la coda locale e'
    /// perfino LIFO, quindi la sentinella poteva arrivare per prima. In pratica passava quasi
    /// sempre, ma l'argomento non reggeva, e qui servono discriminanti, non probabilita'.
    /// <para>
    /// La <c>Console.WriteLine</c> del <c>finally</c> di <c>WriteLog</c> e' <b>sincrona</b> e sta
    /// dopo l'unica guardia che decide se la riga passa: quando la chiamata e' tornata, se la riga
    /// non e' sulla console la guardia e' scattata e nessun <c>Task.Run</c> e' mai stato creato.
    /// </para>
    /// </remarks>
    [Fact]
    public void WithDiagnosticsOffInformationIsStillSilent()
    {
        Navigator.ActivateDebugLog = false;

        using var console = new RecordingConsole();

        Navigator.WriteLog("una riga informativa", LogEntryType.Information);

        Assert.DoesNotContain(console.Lines, line => line == "una riga informativa");
    }

    /// <summary>
    /// Che la console sia davvero il canale che il collaudo qui sopra sta guardando: la stessa
    /// riga, con la diagnostica accesa, ci finisce. Senza questa, una console che non ricevesse
    /// niente farebbe passare quello a prescindere.
    /// </summary>
    [Fact]
    public void TheConsoleDoesReceiveTheSameLineWithDiagnosticsOn()
    {
        Navigator.ActivateDebugLog = true;

        using var console = new RecordingConsole();

        Navigator.WriteLog("una riga informativa", LogEntryType.Information);

        Assert.Contains(console.Lines, line => line == "una riga informativa");
    }

    /// <summary>Con la diagnostica accesa passa tutto, come prima.</summary>
    [Fact]
    public async Task WithDiagnosticsOnInformationIsWrittenToo()
    {
        Navigator.ActivateDebugLog = true;
        using var log = new RecordingLogWriter();

        Navigator.WriteLog("una riga informativa", LogEntryType.Information);

        await log.Wait((d, l) => d == "una riga informativa" && l == LogEntryType.Information)
            .WaitAsync(TimeSpan.FromSeconds(30));
    }
}
