using Wcs.Engine.Interfaces;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Items.ExecutionStrategies;

/// <summary>
/// Strategia di esecuzione per dispositivi di automazione raggruppati.
/// Gestisce lo stato del dispositivo e coordina l'esecuzione degli step di gruppo
/// in base al tipo di stato corrente del dispositivo.
/// </summary>
/// <param name="device">Il dispositivo di automazione da gestire</param>
internal class GroupDevice(AutomationDevice device) : IExecutionStrategy
{
    #region Public Methods

    /// <summary>
    /// Esegue la strategia di controllo per il dispositivo di gruppo in modalità asincrona.
    /// Gestisce i diversi stati del dispositivo: errore, warning e ready, applicando
    /// le azioni appropriate per ciascun stato.
    /// </summary>
    /// <param name="cancellationToken">Token per la cancellazione dell'operazione</param>
    /// <returns>Un task completato che rappresenta l'operazione asincrona</returns>
    /// <exception cref="OperationCanceledException">Sollevata se il token viene cancellato.</exception>
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (device.Status)
        {
            case DeviceStatusTypes.Error:
            case DeviceStatusTypes.Warning:
                await device.ResetStatusBelongedItems();
                break;

            case DeviceStatusTypes.Ready:
                device.GroupExecuteStepRunning = false;
                break;
        }

        return;
    }

    #endregion Public Methods
}