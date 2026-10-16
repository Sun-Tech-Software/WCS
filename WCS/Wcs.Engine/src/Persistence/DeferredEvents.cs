using Log.Interface;

namespace Wcs.Engine.Persistence;

/// <summary>
/// Le notifiche che un contesto manda fuori <b>dopo</b> il salvataggio, non appena l'operazione
/// entra nel contesto.
/// </summary>
/// <remarks>
/// Una riga inserita prende la chiave dal database dentro <c>SaveChanges</c>: annunciarla prima
/// manda fuori un identificatore 0, che nessun client sa riconciliare con la chiave vera della
/// risposta HTTP. Gli store accodano qui anche aggiornamenti e cancellazioni: annunciati subito,
/// un lotto fallito li renderebbe falsi senza nessun evento che li smentisca. Un lotto che fallisce non arriva mai a
/// <see cref="Raise"/>: il contesto viene buttato con la sua coda, e la scrittura ritentata la
/// riaccoda su un contesto nuovo.
/// </remarks>
internal sealed class DeferredEvents
{
    private readonly List<Action> _actions = [];

    public void Add(Action action) => _actions.Add(action);

    /// <summary>
    /// Esegue le notifiche accodate e svuota la coda. Un'azione che solleva non ferma le altre e
    /// non fa fallire il salvataggio: la riga e' gia' scritta, e propagare l'eccezione farebbe
    /// ritentare — cioe' duplicare — un lotto riuscito. Il guasto finisce nel registro.
    /// </summary>
    public void Raise()
    {
        if (_actions.Count == 0) return;

        var pending = _actions.ToArray();
        _actions.Clear();
        foreach (var action in pending)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Navigator.WriteLog($"Notifica dopo il salvataggio fallita: {ex.Message}", LogEntryType.Warning);
            }
        }
    }
}
