using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Wcs.Engine.Persistence;

/// <summary>
/// Serve solo allo strumento delle migrazioni (<c>dotnet ef</c>) per il contesto dello storico. In
/// esercizio i contesti li crea <see cref="WcsHistoryDatabase"/>.
/// </summary>
public sealed class WcsHistoryDbContextFactory : IDesignTimeDbContextFactory<WcsHistoryDbContext>
{
    public WcsHistoryDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<WcsHistoryDbContext>()
            .UseSqlite("Data Source=wcs-history-design-time.db")
            .Options;

        return new WcsHistoryDbContext(options);
    }
}
