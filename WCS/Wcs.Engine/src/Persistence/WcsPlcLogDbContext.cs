using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Persistence.Entities;

namespace Wcs.Engine.Persistence;

/// <summary>
/// Il registro dei messaggi scambiati con i PLC: un file al mese,
/// <c>wcs-plc-AAAA-MM.db</c>, in sola aggiunta per il mese corrente.
/// </summary>
/// <remarks>
/// Una tabella sola, <c>PlcMessages</c>. Stava nello storico
/// (<see cref="WcsHistoryDbContext"/>) fino alla voce <c>D183</c>: la stessa coda di scrittura
/// serviva i due registri, e sotto pieno cadevano le righe piu' vecchie senza guardare di che
/// registro fossero — cioe' anche quelle del navigatore, che sono molte meno e dicono perche'
/// l'impianto si e' fermato. Separare il database separa la coda, le interrogazioni e le
/// ritenzioni: qui la potatura e' a sette giorni
/// (<c>Wcs.Engine.ORM.PlcMessageLog.ClearOlderThan</c>), nello storico e' la vita del file del
/// mese. Ha migrazioni proprie, in <c>Migrations/PlcLog</c>.
/// </remarks>
public sealed class WcsPlcLogDbContext(DbContextOptions<WcsPlcLogDbContext> options) : DbContext(options)
{
    public DbSet<PlcMessage> PlcMessages => Set<PlcMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Le lunghezze sono quelle delle colonne di PCM_LOG, che questa tabella sostituisce.
        modelBuilder.Entity<PlcMessage>(message =>
        {
            message.ToTable("PlcMessages");
            message.HasKey(m => m.Id);
            message.HasIndex(m => m.Date);
            message.HasIndex(m => m.PlcId);
            message.HasIndex(m => m.Asi);
            message.Property(m => m.Syscode).HasMaxLength(50);
            message.Property(m => m.CodingErrorDescription).HasMaxLength(100);
            message.Property(m => m.Asi).HasMaxLength(4);
            message.Property(m => m.Subitem).HasMaxLength(4);
            message.Property(m => m.Partition).HasMaxLength(4);
        });
    }
}
