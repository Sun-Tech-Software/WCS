using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Wcs.Engine.Persistence;

/// <summary>
/// Serve solo allo strumento delle migrazioni (<c>dotnet ef</c>), che deve costruire un contesto
/// senza avviare il motore. In esercizio i contesti li crea <see cref="WcsDatabase"/>.
/// </summary>
public sealed class WcsDbContextFactory : IDesignTimeDbContextFactory<WcsDbContext>
{
    public WcsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<WcsDbContext>()
            .UseSqlite("Data Source=wcs-design-time.db")
            .Options;

        return new WcsDbContext(options);
    }
}
