using Wcs.Common;
using Wcs.Engine.Interfaces;

namespace Wcs.Engine.Items.ExecutionStrategies;

/// <summary>
///     Strategia di esecuzione per dispositivi di automazione raggruppati.
///     Gestisce la logica di reset degli stati per i dispositivi appartenenti allo stesso gruppo,
///     coordinando le operazioni in base allo stato del gruppo di appartenenza e alle condizioni delle unità di carico.
/// </summary>
/// <param name="device">Il dispositivo di automazione per cui implementare la strategia di esecuzione raggruppata</param>
internal class Grouped(AutomationDevice device) : IExecutionStrategy
{
    #region Public Methods

    /// <summary>
    ///     Esegue la strategia di esecuzione per dispositivi raggruppati.
    ///     Verifica lo stato del gruppo di appartenenza e, se necessario, resetta lo stato degli elementi appartenenti.
    ///     Se il dispositivo ha unità di carico caricate con step da eseguire ed è esso stesso in
    ///     stato di errore o warning, resetta lo stato degli elementi del gruppo di destinazione.
    ///     È un percorso di ripristino: entrambi i rami scattano su errore o warning, come la
    ///     strategia gemella <c>GroupDevice</c> e come il contratto di
    ///     <c>ResetStatusBelongedItems</c>, che riporta a Ready i soli dispositivi in quegli stati.
    /// </summary>
    /// <param name="cancellationToken">Token di cancellazione per l'operazione asincrona</param>
    /// <returns>Task completato che rappresenta l'operazione asincrona</returns>
    /// <exception cref="OperationCanceledException">Sollevata se il token viene cancellato.</exception>
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (device.BelongedGroup!.Status is Enums.DeviceStatusTypes.Error or Enums.DeviceStatusTypes.Warning)
        {
            await device.BelongedGroup.ResetStatusBelongedItems();
            return;
        }

        if (device.LUsLoaded.Count == 0 || device.LoadedLu?.StepToDo == null ||
            device.Status is not (Enums.DeviceStatusTypes.Error or Enums.DeviceStatusTypes.Warning))
            return;

        // La destinazione dello step puo' legittimamente non appartenere a un gruppo: in quel
        // caso non c'e' alcuno stato di gruppo da ripristinare e il ramo nullo e' silenzioso.
        // La forma precedente "await a?.b?.c?.M()" sollevava invece NullReferenceException.
        var destinationGroup = device.LoadedLu?.StepToDo?.DestinationItem?.BelongedGroup;

        if (destinationGroup == null) return;

        cancellationToken.ThrowIfCancellationRequested();

        await destinationGroup.ResetStatusBelongedItems();
    }

    #endregion Public Methods
}