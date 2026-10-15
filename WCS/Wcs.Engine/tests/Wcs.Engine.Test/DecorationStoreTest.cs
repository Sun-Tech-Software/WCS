using Wcs.Engine.Persistence;
using Xunit;

namespace Wcs.Engine.Test;

/// <summary>
/// La decorazione del disegno sul database del WCS: una riga sola, letta e scritta come blocco
/// unico. Le due prove seguono <see cref="PlantStoreTest"/>: lo store lavora su un contesto preso
/// direttamente, senza passare dallo scrittore di stato.
/// </summary>
public sealed class DecorationStoreTest : IDisposable
{
    private readonly WcsDatabase _database = WcsDatabase.InMemory();
    private readonly WcsDbContext _context;

    public DecorationStoreTest()
    {
        _context = _database.CreateContext();
    }

    public void Dispose()
    {
        _context.Dispose();
        _database.Dispose();
    }

    [Fact]
    public async Task TheDecorationSurvivesASaveAndAReread()
    {
        await DecorationStore.SetAsync(_context, "<mxCell id=\"nota-1\" value=\"Zona A\"/>");

        Assert.Contains("Zona A", await DecorationStore.GetAsync(_context));
    }

    /// <summary>Un impianto che non ha mai avuto decorazione risponde con il vuoto, non con un errore.</summary>
    [Fact]
    public async Task AnEmptyDecorationReadsAsEmptyNotAsAFailure()
        => Assert.Equal(string.Empty, await DecorationStore.GetAsync(_context));
}
