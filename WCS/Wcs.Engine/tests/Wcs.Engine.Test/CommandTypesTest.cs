using Wcs.Engine.Items;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.Engine.Test;

/// <summary>
/// I tipi di comando che i client conoscono (Wcs.Common) e quelli che il motore esegue
/// (Command.CommandTypes) sono lo stesso elenco: nomi e valori uguali, nessuno in piu'.
/// </summary>
public sealed class CommandTypesTest
{
    [Fact]
    public void TheSharedEnumMatchesTheEngineOneNameByNameAndValueByValue()
    {
        var engine = Enum.GetValues<Command.CommandTypes>().ToDictionary(v => v.ToString(), v => (int)v);
        var shared = Enum.GetValues<CommandTypes>().ToDictionary(v => v.ToString(), v => (int)v);
        Assert.Equal(engine.OrderBy(p => p.Key), shared.OrderBy(p => p.Key));
    }
}
