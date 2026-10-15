using System.Text;

namespace Wcs.Engine.Test;

/// <summary>
/// La console del processo per il tempo di un blocco: raccoglie cio' che ci viene scritto e la
/// rimette com'era quando viene smaltita.
/// </summary>
/// <remarks>
/// Serve per le asserzioni <b>negative</b> sul registro del motore. <c>Navigator.WriteLog</c> manda
/// la riga all'<c>IWriter</c> da dentro un <c>Task.Run</c> — quindi «non e' arrivata» non si puo'
/// asserire subito, e nemmeno mandando una riga sentinella: sono due <c>Task.Run</c> distinti e il
/// pool non garantisce l'ordine (e da un thread del pool la coda locale e' pure LIFO). La
/// <c>Console.WriteLine</c> del <c>finally</c>, invece, e' <b>sincrona</b> e sta dopo l'unica
/// guardia che decide se la riga passa: quando la chiamata che avrebbe dovuto scriverla e' gia'
/// tornata, se la riga non e' qui non nascera' piu'.
/// <para>
/// La console e' stato del processo, come <c>Navigator.NavStatus</c>: vale perche' la
/// parallelizzazione dei collaudi e' spenta (<c>Impostazioni.cs</c>).
/// </para>
/// </remarks>
internal sealed class RecordingConsole : IDisposable
{
    private readonly TextWriter _previous = Console.Out;
    private readonly StringWriter _recorded = new(new StringBuilder());

    public RecordingConsole() => Console.SetOut(_recorded);

    /// <summary>Le righe scritte sulla console da quando questo oggetto e' stato costruito.</summary>
    public IReadOnlyList<string> Lines =>
        _recorded.GetStringBuilder().ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    public void Dispose()
    {
        Console.SetOut(_previous);
        _recorded.Dispose();
    }
}
