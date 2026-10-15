using Wcs.Engine.Items.Managers;
using Wcs.Wms.Contracts;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// Il difetto latente del compito 7: <c>CellDescription.Locked</c> era un booleano, e con un
/// booleano il primo dei due test qui sotto non era nemmeno esprimibile - non c'era modo di dire
/// "bloccata solo al prelievo". Il brief del compito illustra la prova con un metodo
/// <c>StorageManager.CanStoreInAsync(cellId)</c> che non esiste nel codice vero: la decisione
/// reale vive dentro <see cref="AutomationDeviceStorageManager.VerifyStoreInfo"/>, un metodo
/// privato che dipende da un dispositivo, un database e il gestionale interno
/// (<c>Navigator.WmsBusinessArea</c>) completamente cablati - non un banco di prova per un test
/// unitario. La condizione che decide, pero', e' stata estratta in
/// <see cref="AutomationDeviceStorageManager.BlocksStoring"/>, <c>internal</c> apposta perche' il
/// test la raggiunga senza construire tutto il resto: e' la stessa proprieta' che il brief voleva
/// provare, sulla funzione vera invece che su un'API inventata.
/// </summary>
public sealed class CellLockBlocksStoringTest
{
    [Fact]
    public void ACellLockedOnlyForUnloadingStillAcceptsStoring() =>
        Assert.False(AutomationDeviceStorageManager.BlocksStoring(CellLockState.Unloading));

    [Fact]
    public void ACellLockedForLoadingDoesNotAcceptStoring() =>
        Assert.True(AutomationDeviceStorageManager.BlocksStoring(CellLockState.Loading));

    [Fact]
    public void AFullCellDoesNotAcceptStoringEither() =>
        Assert.True(AutomationDeviceStorageManager.BlocksStoring(CellLockState.Full));

    [Fact]
    public void AnUnlockedCellAcceptsStoring() =>
        Assert.False(AutomationDeviceStorageManager.BlocksStoring(CellLockState.None));
}
