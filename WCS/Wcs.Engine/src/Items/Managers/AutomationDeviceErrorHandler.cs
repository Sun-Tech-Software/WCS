using System.Diagnostics;
using Wcs.Common;

namespace Wcs.Engine.Items.Managers;

/// <summary>
/// Gestisce gli errori dei dispositivi di automazione, fornendo funzionalit� per il reset e la pulizia degli stati di errore.
/// Coordina il ripristino dello stato operativo del dispositivo e la riattivazione dell'esecuzione automatica degli step.
/// </summary>
/// <param name="device">Il dispositivo di automazione associato a questo gestore di errori</param>
public class AutomationDeviceErrorHandler(AutomationDevice device)
{
    #region Public Methods

    /// <summary>
    /// Pulisce lo stato di errore del dispositivo e ripristina la condizione operativa normale.
    /// Reimposta lo stato del dispositivo su Ready, cancella gli ID degli step in esecuzione e in attesa,
    /// e riavvia l'esecuzione automatica degli step se abilitata.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Il ritardo di 500 ms non c'e' piu'. Non serviva a "stabilizzare il sistema": compensava il
    /// fatto che l'assegnazione di <c>Status</c> era asincrona, quindi lo stato poteva non essere
    /// ancora cambiato quando si rilanciava l'esecuzione. Ora il cambio e' visibile al ritorno
    /// dall'assegnazione, e l'attesa non ha piu' oggetto. Bloccava per giunta il thread del
    /// chiamante. Si vedano le voci <c>D43</c> e <c>D59</c> del registro in
    /// <c>WCS/doc/debito-tecnico.md</c>.
    /// </para>
    /// <para>
    /// L'ordine delle prime tre istruzioni e' cambiato: gli identificativi si azzerano prima di
    /// portare lo stato a <c>Ready</c>, come fa gia' <see cref="ResetError"/>. Con l'assegnazione
    /// sincrona l'ordine precedente avrebbe reso visibile un dispositivo <c>Ready</c> che portava
    /// ancora addosso lo step in esecuzione.
    /// </para>
    /// <para>
    /// <b>Il rilascio della prenotazione qui non e' una rinuncia, ed e' un rischio dichiarato.</b>
    /// Quando questo dispositivo lascia andare il posto non sta dicendo «lo step che aveva
    /// prenotato ha rinunciato»: sta dicendo «io, destinazione, non accetto piu' lo step che stava
    /// arrivando». Il <b>mittente</b>, pero', continua a credere di avere il posto. Fra questo
    /// rilascio e il suo ritentativo un terzo puo' prendere il posto, e allora due mittenti
    /// puntano di nuovo allo stesso elemento — che e' esattamente cio' che la prenotazione della
    /// voce <c>D73</c> esiste per impedire. <see cref="AutomationDevice.ForceReleaseIncomingStep"/>
    /// conserva il comportamento di prima e lo porta sotto lock: <b>rende visibile il problema, non
    /// lo risolve.</b> Oggi la porta e' chiusa perche' nessuno chiama questo metodo (si veda la
    /// voce <c>D121</c> del registro in <c>WCS/doc/debito-tecnico.md</c>); come vada riaperta e' una
    /// decisione di progetto, non di memoria.
    /// </para>
    /// </remarks>
    public void ClearError()
    {
        try
        {
            device.RunningStepId = Guid.Empty;
            device.ForceReleaseIncomingStep();
            device.Status = Enums.DeviceStatusTypes.Ready;

            if (device.AutoExecuteStep)
                _ = device.ExecuteStep();
        }
        catch (Exception ex)
        {
            device.LogError(ex);
            Debug.Print(ex.Message);
        }
    }

    /// <summary>
    /// Reimposta lo stato di errore del dispositivo senza ritardi o elaborazioni aggiuntive.
    /// Verifica che il dispositivo sia effettivamente in stato di errore prima di procedere con il reset.
    /// Cancella gli ID degli step in esecuzione e in attesa, imposta lo stato su Ready e riavvia l'esecuzione automatica se abilitata.
    /// </summary>
    /// <remarks>
    /// A differenza di <see cref="ClearError"/>, questo metodo non include ritardi e procede immediatamente
    /// con la riattivazione dell'esecuzione automatica degli step.
    /// Sul rilascio forzato della prenotazione vale parola per parola quanto scritto su
    /// <see cref="ClearError"/>: e' una revoca, il mittente non lo sa, e la voce <c>D121</c> del
    /// registro dice che cosa resta aperto.
    /// </remarks>
    public void ResetError()
    {
        if (device.Status != Enums.DeviceStatusTypes.Error)
            return;

        device.RunningStepId = Guid.Empty;
        device.ForceReleaseIncomingStep();
        device.Status = Enums.DeviceStatusTypes.Ready;

        if (device.AutoExecuteStep)
            _ = device.ExecuteStep();
    }

    #endregion Public Methods
}