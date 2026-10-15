using Wcs.Engine.Items.Managers;
using Wcs.Wms.Contracts;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// <see cref="AutomationDeviceStepExecutor.CellLockBlocksStoring"/>, cioe' la soglia che il compito
/// 16 aveva migrato com'era da <c>CelleMagazzino.CellaAreaBloccataCarico</c> (<c>BLOCCA &gt;= 2</c>
/// su <c>CELLE_AREE</c>) e che il 2026-09-09 e' stata <b>allineata alla regola del deposito</b>:
/// bloccato per <see cref="CellLockState.Full"/> o <see cref="CellLockState.Loading"/>, non per
/// <see cref="CellLockState.Unloading"/>. Da non confondere con
/// <see cref="CellLockBlocksStoringTest"/>, che prova l'altra guardia,
/// <see cref="AutomationDeviceStorageManager.BlocksStoring"/>.
/// <para>
/// L'ultimo test e' quello che conta, ed e' il capovolgimento di quello scritto dal compito 16:
/// finche' la discrepanza era aperta teneva ferma la <b>differenza</b> fra le due guardie, perche'
/// nessuno le allineasse in silenzio. La decisione e' stata presa - <c>BLOCCA = 1</c> blocca
/// l'ingresso, <c>2</c> l'uscita, e la guardia del deposito e' quella che si accende su 1 e 3 - e
/// adesso il test pretende che le due regole <b>coincidano</b> su tutti e quattro gli stati,
/// perche' nessuno le faccia divergere di nuovo.
/// </para>
/// </summary>
public sealed class StepExecutorCellLockBlocksStoringTest
{
    [Fact]
    public void ACellLockedForLoadingBlocksStoring() =>
        Assert.True(AutomationDeviceStepExecutor.CellLockBlocksStoring(CellLockState.Loading));

    [Fact]
    public void AFullCellBlocksStoring() =>
        Assert.True(AutomationDeviceStepExecutor.CellLockBlocksStoring(CellLockState.Full));

    [Fact]
    public void ACellLockedForUnloadingDoesNotBlockStoring() =>
        Assert.False(AutomationDeviceStepExecutor.CellLockBlocksStoring(CellLockState.Unloading));

    [Fact]
    public void AnUnlockedCellDoesNotBlockStoring() =>
        Assert.False(AutomationDeviceStepExecutor.CellLockBlocksStoring(CellLockState.None));

    /// <summary>
    /// Le due regole di blocco al deposito del motore dicono la stessa cosa su tutti e quattro gli
    /// stati, e questo test lo dichiara invece di lasciarlo implicito nei quattro qui sopra.
    /// </summary>
    [Theory]
    [InlineData(CellLockState.None)]
    [InlineData(CellLockState.Loading)]
    [InlineData(CellLockState.Unloading)]
    [InlineData(CellLockState.Full)]
    public void TheTwoStoringGuardsAgree(CellLockState lockState) =>
        Assert.Equal(
            AutomationDeviceStorageManager.BlocksStoring(lockState),
            AutomationDeviceStepExecutor.CellLockBlocksStoring(lockState));
}
