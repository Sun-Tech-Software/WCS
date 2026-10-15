using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Wcs.Engine.Persistence;

/// <summary>
/// Serve solo allo strumento delle migrazioni (<c>dotnet ef</c>) per il contesto del registro dei
/// messaggi dei PLC. In esercizio i contesti li crea <see cref="WcsPlcLogDatabase"/>.
/// </summary>
public sealed class WcsPlcLogDbContextFactory : IDesignTimeDbContextFactory<WcsPlcLogDbContext>
{
    public WcsPlcLogDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<WcsPlcLogDbContext>()
            .UseSqlite("Data Source=wcs-plc-design-time.db")
            .Options;

        return new WcsPlcLogDbContext(options);
    }
}
