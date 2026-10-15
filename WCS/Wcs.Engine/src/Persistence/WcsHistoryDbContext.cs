using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Persistence.Entities;

namespace Wcs.Engine.Persistence;

/// <summary>
/// Lo storico del WCS: quello che e' successo. Un file al mese, <c>wcs-history-AAAA-MM.db</c>, in
/// sola aggiunta per il mese corrente.
/// </summary>
/// <remarks>
/// Tiene le tabelle il cui contenuto descrive il passato e cresce ogni giorno: il registro del
/// navigatore per primo. Lo storico non entra mai nel percorso del motore, che ci scrive
/// soltanto; lo leggono i riepiloghi. Ha migrazioni proprie, in <c>Migrations/History</c>.
/// <para>
/// Il registro dei messaggi dei PLC ha abitato qui fra il compito 15 e la voce <c>D183</c>: e'
/// uscito con la sua tabella <c>PlcMessages</c> e vive in <see cref="WcsPlcLogDbContext"/>, cosi'
/// che la coda di scrittura, le interrogazioni e la ritenzione dei due registri non si diano piu'
/// fastidio.
/// </para>
/// </remarks>
public sealed class WcsHistoryDbContext(DbContextOptions<WcsHistoryDbContext> options) : DbContext(options)
{
    public DbSet<NavigatorLogEntry> NavigatorLog => Set<NavigatorLogEntry>();
    public DbSet<HandledLoadUnit> HandledLoadUnits => Set<HandledLoadUnit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<HandledLoadUnit>(handled =>
        {
            handled.ToTable("HandledLoadUnits");
            handled.HasKey(h => h.Id);
            handled.HasIndex(h => h.MissionId).IsUnique();
            handled.HasIndex(h => h.Date);
            handled.HasIndex(h => h.LoadUnitId);
            handled.Property(h => h.LoadUnitCode).HasMaxLength(50);
            handled.Property(h => h.ResultDescription).HasMaxLength(500);
        });

        modelBuilder.Entity<NavigatorLogEntry>(entry =>
        {
            entry.ToTable("NavigatorLog");
            entry.HasKey(e => e.Id);
            entry.HasIndex(e => e.Date);
            entry.HasIndex(e => e.MissionId);
            entry.HasIndex(e => e.ItemId);
            entry.Property(e => e.Asi).HasMaxLength(4);
            entry.Property(e => e.Partition).HasMaxLength(4);
            entry.Property(e => e.Subitem).HasMaxLength(4);
            entry.Property(e => e.Description).HasMaxLength(255);
        });
    }
}
