using Wcs.Engine.Items;

namespace Wcs.Engine.Interfaces;

internal interface IExecutionStrategy
{
    #region Public Methods

    Task ExecuteAsync(CancellationToken cancellationToken);

    #endregion Public Methods
}