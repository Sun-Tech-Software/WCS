using System.Reflection;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// Voce B9: il motore si avvia con <see cref="Navigator.StartAsync"/>, che restituisce l'esito,
/// e in nessun altro modo. Il setter di <c>DatabaseConnectionString</c> avviava il caricamento
/// come effetto collaterale, in un <c>Task.Run</c> che nessuno poteva attendere ne' interrogare;
/// l'unico chiamante per cui era stato tenuto — l'Editor, dentro un costruttore — non referenzia
/// piu' il motore.
/// </summary>
/// <remarks>
/// Come <c>IncomingStepReservationTest.TheTokenPropertyHasNoSetterAtAll</c>: si vieta una
/// <b>forma</b>. Prima qui si provava che quella proprieta' non avesse piu' il setter; adesso la
/// proprieta' non c'e' proprio, perche' era la connessione a SQL Server del gestionale e il motore
/// non ne ha piu' una. Chi volesse di nuovo avviare il motore assegnando una stringa non trova
/// nemmeno il nome, e <see cref="Navigator.StartAsync"/> non prende argomenti.
/// </remarks>
public sealed class EngineStartIsExplicitTest
{
    [Fact]
    public void ThereIsNoConnectionStringToAssignAtAll()
    {
        const BindingFlags everything = BindingFlags.Public | BindingFlags.NonPublic |
                                        BindingFlags.Static | BindingFlags.Instance;

        Assert.Empty(typeof(Navigator).GetMember("DatabaseConnectionString", everything));
        Assert.Empty(typeof(Navigator).GetMember("Database", everything));
    }

    /// <summary>
    /// L'avvio non prende piu' niente: la cartella dei database e' <c>Navigator.DatabaseDirectory</c>
    /// e il gestionale sta oltre il confine. Un giorno che qualcuno rimettesse una stringa nella
    /// firma, questa lo direbbe.
    /// </summary>
    [Fact]
    public void StartingTheEngineTakesNoArguments()
    {
        var start = typeof(Navigator).GetMethod(nameof(Navigator.StartAsync));

        Assert.NotNull(start);
        Assert.Empty(start!.GetParameters());
    }

    /// <summary>
    /// Quello che resta e' <see cref="Navigator.IsConfigured"/>, che si legge soltanto: dice se i
    /// database del WCS sono aperti, e non e' una leva per aprirli.
    /// </summary>
    [Fact]
    public void TheConfiguredFlagHasNoSetterAtAll()
    {
        var property = typeof(Navigator).GetProperty(nameof(Navigator.IsConfigured));

        Assert.NotNull(property);
        Assert.Null(property!.SetMethod);
    }
}
