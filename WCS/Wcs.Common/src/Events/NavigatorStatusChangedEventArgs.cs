using static Wcs.Common.Enums;

namespace Wcs.Common.Events;

public class NavigatorStatusChangedEventArgs : EventArgs
{

    #region Public Constructors

    /// <summary>
    /// Costruisce gli argomenti di una transizione di stato del navigatore.
    /// </summary>
    /// <param name="oldStatus">Lo stato da cui si proviene.</param>
    /// <param name="newStatus">Lo stato in cui si entra.</param>
    /// <remarks>
    /// Lo stato precedente lo conosce chi esegue la transizione e va passato: prima veniva
    /// letto da <see cref="NewStatus"/> nel corpo del costruttore, ma l'inizializzatore di
    /// proprieta' lo aveva gia' posto a <c>Stop</c>, quindi <see cref="OldStatus"/> valeva
    /// sempre <c>Stop</c>. Tenerlo invece in un campo statico lo renderebbe dipendente
    /// dall'ordine di costruzione: nello stesso processo l'Editor ne costruisce uno a mano
    /// all'avvio, e falserebbe la sequenza dell'evento vero.
    /// </remarks>
    public NavigatorStatusChangedEventArgs(NavigatorStatusTypes oldStatus, NavigatorStatusTypes newStatus)
    {
        OldStatus = oldStatus;
        NewStatus = newStatus;
    }

    #endregion Public Constructors

    #region Public Properties

    public NavigatorStatusTypes NewStatus { get; set; } = NavigatorStatusTypes.Stop;
    public NavigatorStatusTypes OldStatus { get; set; }

    #endregion Public Properties
}
