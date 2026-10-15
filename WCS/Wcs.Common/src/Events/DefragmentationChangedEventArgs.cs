using static Wcs.Common.Enums;

namespace Wcs.Common.Events;

public class DefragmentationChangedEventArgs(DefragmentationTypes value) : EventArgs
{

    #region Public Properties

    public DefragmentationTypes DefragmentationStatus { get; private set; } = value;

    #endregion Public Properties
}
