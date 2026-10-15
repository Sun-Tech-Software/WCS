using Wcs.Wms.Contracts;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// Lo stato di blocco di una cella (compito 7) esiste in due copie: <see cref="CellLockTypes"/>
/// lato motore (<c>Wcs.Common</c>) e <see cref="CellLockState"/> nel contratto verso il
/// gestionale (<c>Wcs.Wms.Contracts</c>). <c>MagazzinoPerWcs</c> converte il valore letto da
/// <c>CELLE_AREE.BLOCCA</c> facendo un cast diretto sul secondo; se le due enumerazioni
/// scivolassero l'una rispetto all'altra la conversione resterebbe silenziosamente sbagliata,
/// perche' un cast fra due enum <c>byte</c> non fallisce mai a compilazione.
/// </summary>
public sealed class CellLockStateAlignmentTest
{
    [Fact]
    public void TheContractEnumMatchesTheEngineOneNameByNameAndValueByValue()
    {
        var engine = Enum.GetValues<CellLockTypes>().ToDictionary(v => v.ToString(), v => (int)v);
        var contract = Enum.GetValues<CellLockState>().ToDictionary(v => v.ToString(), v => (int)v);
        Assert.Equal(engine.OrderBy(p => p.Key), contract.OrderBy(p => p.Key));
    }
}
