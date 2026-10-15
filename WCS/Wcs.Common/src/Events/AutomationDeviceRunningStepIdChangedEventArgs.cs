namespace Wcs.Common.Events;

public class AutomationDeviceRunningStepIdChangedEventArgs(Guid runningStepId, Guid waitingStepId) : EventArgs
{

    #region Public Fields

    public Guid RunningStepId = runningStepId;
    public Guid WaitingStepId = waitingStepId;

    #endregion Public Fields

}
