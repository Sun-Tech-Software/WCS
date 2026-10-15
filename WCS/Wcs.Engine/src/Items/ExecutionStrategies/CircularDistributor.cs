using Wcs.Common;
using Wcs.Engine.Interfaces;
using Log.Interface;

namespace Wcs.Engine.Items.ExecutionStrategies;

/// <summary>
/// Strategia di esecuzione per dispositivi di tipo distributore circolare.
/// Gestisce l'avvio della distribuzione circolare quando il dispositivo non è in stato di errore o warning.
/// </summary>
/// <param name="device">Il dispositivo di automazione su cui eseguire la strategia</param>
internal class CircularDistributor(AutomationDevice device) : IExecutionStrategy
{
    #region Public Methods

    /// <summary>
    /// Esegue la strategia di distribuzione circolare in modo asincrono.
    /// Avvia il processo di distribuzione circolare solo se il dispositivo non è in stato di errore o warning.
    /// </summary>
    /// <param name="cancellationToken">Token di cancellazione per interrompere l'operazione</param>
    /// <returns>Task che rappresenta l'operazione asincrona</returns>
    /// <exception cref="OperationCanceledException">Sollevata se il token viene cancellato.</exception>
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // La guardia ora chiude davvero: prima la condizione era scritta al contrario
        // (attendeva Task.CompletedTask nel ramo "sano") e la chiamata seguente era fuori
        // dall'if, cosi' un distributore in errore continuava a distribuire.
        if (device.Status is Enums.DeviceStatusTypes.Error or Enums.DeviceStatusTypes.Warning)
        {
            // Questo e' il percorso caldo: ExecuteStep viene richiamato in continuazione e un
            // distributore che resta in errore passerebbe di qui ad ogni giro. Il canale e'
            // WriteLog, spento per impostazione predefinita, e non RecordNavigatorLog, che
            // scriverebbe una riga sul database ad ogni ciclo. Lo stato di errore e' gia'
            // visibile all'operatore dallo stato del dispositivo.
            Navigator.WriteLog(
                $"CircularDistribution {device.ItemGuid} skipped: device status is {device.Status}",
                LogEntryType.Information);
            return;
        }

        if (device.StepExecutor == null) return;

        cancellationToken.ThrowIfCancellationRequested();

        await device.StepExecutor.StartCircularDistribution();
    }

    #endregion Public Methods
}
