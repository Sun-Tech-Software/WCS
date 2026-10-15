using Wcs.Engine.Api;
using Wcs.Engine.Collections;
using Wcs.Common;
using Wcs.Engine.Items;
using Wcs.Engine.ORM;
using Log.Interface;
using static Wcs.Engine.Navigator;
using PathMission = Wcs.Engine.Items.PathMission;

namespace Wcs.Engine;

/// <summary>
/// Classe astratta che gestisce i comandi eseguibili del sistema di automazione del magazzino.
/// Fornisce metodi per il controllo del navigatore, la gestione delle unità di carico (LU) e le operazioni di movimentazione.
/// </summary>
public abstract partial class ExecutableCommands
{
    #region Private Fields

    #endregion Private Fields

    #region Internal Enums

    /// <summary>
    /// Definisce le modalità di esecuzione dei comandi di movimentazione delle unità di carico.
    /// </summary>
    internal enum ExecuteMoveLuTypes : byte
    {
        /// <summary>
        /// Modalità automatica, gestita dal sistema senza intervento dell'operatore.
        /// </summary>
        Automatic = 0,

        /// <summary>
        /// Modalità manuale, richiesta esplicitamente dall'operatore.
        /// </summary>
        Manual = 1
    }

    #endregion Internal Enums

    #region Public Methods

    /// <summary>
    /// Mette in pausa il navigatore del sistema di automazione.
    /// </summary>
    /// <remarks>
    /// Se il navigatore è già in stato di stop o pausa, il metodo non ha effetto.
    /// </remarks>
    public static void PauseNavigator()
    {
        if (NavStatus is Enums.NavigatorStatusTypes.Stop or Enums.NavigatorStatusTypes.Pause)
            return;
        NavStatus = Enums.NavigatorStatusTypes.Pause;
    }

    /// <summary>
    /// Ricarica il diagramma delle rotte utilizzato dal navigatore.
    /// </summary>
    /// <remarks>
    /// Se il navigatore è in esecuzione, viene fermato temporaneamente, ricaricato e poi riavviato.
    /// </remarks>
    public static async Task ReloadRouteDiagram()
    {
        var lastNavStatus = NavStatus;

        if (lastNavStatus == Enums.NavigatorStatusTypes.Run)
        {
            StopNavigator();
            await Task.Delay(2000);
        }

        await LoadNavigatorData();

        if (lastNavStatus == Enums.NavigatorStatusTypes.Run)
            await StartNavigator();
    }

    /// <summary>
    /// Rimuove tutte le unità di carico presenti nel sistema di automazione.
    /// </summary>
    /// <remarks>
    /// Questo metodo scarica tutte le unità di carico da tutti i dispositivi e cancella tutte le missioni attive.
    /// </remarks>
    public static async Task RemoveAllLu()
    {
        if (ActiveMissions.Instance.Count == 0 || PathDevices.Instance.Count == 0)
            return;

        var devicesList = PathDevices.Instance.List;
        foreach (var item in devicesList)
            if (item.LUsLoaded.Count > 0)
                await item.LuUnloadedFromAsi(Enums.StepTypes.Manual);

        await ActiveMissions.Instance.Clear(true);
    }

    /// <summary>
    /// Imposta lo stato di tutti i dispositivi a "Ready".
    /// </summary>
    /// <remarks>
    /// Utilizzato per ripristinare lo stato del sistema dopo un blocco o un'anomalia.
    /// </remarks>
    public static void SetStatusReadyAllItems()
    {
        if (PathDevices.Instance.Count == 0)
            return;

        // Rimessi in marcia dall'operatore: anche la fila degli errori di deposito ricomincia (risposta 1
        // del proprietario dell'8/10).
        foreach (var item in PathDevices.Instance.List)
        {
            item.ForgetRefusedDeposits();
            item.Status = Enums.DeviceStatusTypes.Ready;
        }
    }

    /// <summary>
    /// Avvia il navigatore del sistema di automazione.
    /// </summary>
    /// <remarks>
    /// Se il navigatore è già in esecuzione, il metodo non ha effetto.
    /// From Stop (not from Pause, which keeps the connections it had) Run is refused while a device
    /// of connection type Connected lacks its connection: <see cref="EngineRunRefusedException"/>,
    /// the engine stays in Stop and nothing is opened (<see cref="DeviceConnections.RequireConnectionsForRun"/>).
    /// Se il motore non è configurato (i database del WCS non sono aperti), il metodo non ha altro effetto.
    /// Se il navigatore non è in pausa, apre le connessioni ai dispositivi e attende 2 secondi prima di avviare il navigatore.
    /// </remarks>
    /// <exception cref="EngineRunRefusedException">A Connected device lacks its connection and the
    /// setting Wcs:AllowSimulatorForMissingConnections is off.</exception>
    public static async Task StartNavigator()
    {
        if (NavStatus == Enums.NavigatorStatusTypes.Run)
            return;
        if (NavStatus != Enums.NavigatorStatusTypes.Pause)
            DeviceConnections.RequireConnectionsForRun();
        if (!IsConfigured)
            return;
        if (NavStatus != Enums.NavigatorStatusTypes.Pause)
        {
            OpenDeviceConnections();
            await Task.Delay(2000);
        }

        NavStatus = Enums.NavigatorStatusTypes.Run;
    }

    /// <summary>
    /// Ferma il navigatore del sistema di automazione.
    /// </summary>
    /// <remarks>
    /// Se il navigatore è già fermo, il metodo non ha effetto.
    /// Chiude tutte le connessioni PLC prima di impostare lo stato a Stop.
    /// </remarks>
    public static void StopNavigator()
    {
        if (NavStatus == Enums.NavigatorStatusTypes.Stop)
            return;
        ClosePlcConnections();
        NavStatus = Enums.NavigatorStatusTypes.Stop;
    }

    #endregion Public Methods

}