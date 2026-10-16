using Microsoft.EntityFrameworkCore;
using Wcs.Engine.Persistence.Entities;

namespace Wcs.Engine.Persistence;

/// <summary>
/// Il database operativo del WCS: lo stato di adesso. Un file, <c>wcs.db</c>.
/// </summary>
/// <remarks>
/// Lo storico — registro del navigatore, movimentazioni — avra' un contesto proprio con un file al
/// mese, come deciso nel documento sul database del WCS. Qui stanno solo le tabelle il cui
/// contenuto descrive lo stato corrente dell'impianto.
/// </remarks>
public sealed class WcsDbContext(DbContextOptions<WcsDbContext> options) : DbContext(options)
{
    public DbSet<Mission> Missions => Set<Mission>();
    public DbSet<PlantItem> PlantItems => Set<PlantItem>();
    public DbSet<PlantJunction> PlantJunctions => Set<PlantJunction>();
    public DbSet<PlantDiagram> PlantDiagrams => Set<PlantDiagram>();
    public DbSet<PlantDecoration> PlantDecorations => Set<PlantDecoration>();
    public DbSet<ExtractionQueueItem> ExtractionQueue => Set<ExtractionQueueItem>();
    public DbSet<PendingExtractionOutcome> PendingExtractionOutcomes => Set<PendingExtractionOutcome>();
    public DbSet<PendingWarehouseTransition> PendingWarehouseTransitions => Set<PendingWarehouseTransition>();
    public DbSet<PendingPickedExtractionReport> PendingPickedExtractionReports => Set<PendingPickedExtractionReport>();
    public DbSet<ReservationToGiveBack> ReservationsToGiveBack => Set<ReservationToGiveBack>();
    public DbSet<PendingCellGiveBack> PendingCellGiveBacks => Set<PendingCellGiveBack>();
    public DbSet<CellToMarkFull> CellsToMarkFull => Set<CellToMarkFull>();
    public DbSet<RetiredHandledReference> RetiredHandledReferences => Set<RetiredHandledReference>();
    public DbSet<CommandRecord> Commands => Set<CommandRecord>();
    public DbSet<DoorMapping> DoorMappings => Set<DoorMapping>();
    public DbSet<PlcConnection> PlcConnections => Set<PlcConnection>();
    public DbSet<RecordDataSettings> RecordDataSettings => Set<RecordDataSettings>();
    public DbSet<CheckDataSettings> CheckDataSettings => Set<CheckDataSettings>();

    /// <summary>
    /// Le notifiche che partono dopo il salvataggio: e' qui che gli store accodano l'annuncio di
    /// una riga inserita, perche' prima di <c>SaveChanges</c> non ne conoscono la chiave.
    /// </summary>
    internal DeferredEvents Deferred { get; } = new();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        var result = base.SaveChanges(acceptAllChangesOnSuccess);
        Deferred.Raise();
        return result;
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken).ConfigureAwait(false);
        Deferred.Raise();
        return result;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PlcConnection>(plc =>
        {
            plc.ToTable("PlcConnections");
            plc.HasKey(p => p.Id);
            // La chiave e' quella che i dispositivi ricordano: si assegna, non si genera.
            plc.Property(p => p.Id).ValueGeneratedNever();
            plc.Property(p => p.Description).HasMaxLength(50);
            plc.Property(p => p.PlcAddress).HasMaxLength(50);
            plc.Property(p => p.LocalAddress).HasMaxLength(50);
            plc.Property(p => p.LastCommandId).HasDefaultValue(0);
        });

        modelBuilder.Entity<RecordDataSettings>(settings =>
        {
            settings.ToTable("RecordDataSettings");
            settings.HasKey(s => s.Id);
            settings.Property(s => s.Id).ValueGeneratedNever();
            settings.Property(s => s.NewLoadUnitReference).HasMaxLength(50);
            settings.Property(s => s.NewLoadUnitQuantityId).HasMaxLength(50);
        });

        modelBuilder.Entity<CheckDataSettings>(settings =>
        {
            settings.ToTable("CheckDataSettings");
            settings.HasKey(s => s.Id);
            settings.Property(s => s.Id).ValueGeneratedNever();
            settings.Property(s => s.Code).HasMaxLength(50);
        });

        modelBuilder.Entity<DoorMapping>(mapping =>
        {
            mapping.ToTable("DoorMappings");
            mapping.HasKey(m => m.Id);
            mapping.HasIndex(m => m.ItemId);
            mapping.HasIndex(m => m.DoorCode);
            mapping.HasIndex(m => m.CellId);
            mapping.Property(m => m.DoorCode).HasMaxLength(50);
            mapping.Property(m => m.Side).HasMaxLength(1);

            // La riga della mappa porte appartiene a un dispositivo dell'impianto, e fino alla
            // migrazione AddDoorMappingItemForeignKey questo era solo un accordo fra chi scriveva:
            // collegare una cella a un dispositivo cancellato riusciva, lasciava la riga orfana e
            // faceva rinascere il dispositivo. Senza proprieta' di navigazione (nessuna delle due
            // entita' ne ha, e nessuna query le userebbe): la chiave esterna e' un vincolo del
            // database, non un modo nuovo di leggere. Cascade e non Restrict perche' e' la scelta
            // che lascia riuscire la cancellazione di un dispositivo come riusciva prima - si veda
            // il remarks di PlantStore.DeleteItemAsync, che le righe se le porta via da se'.
            mapping.HasOne<PlantItem>()
                .WithMany()
                .HasForeignKey(m => m.ItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CommandRecord>(command =>
        {
            command.ToTable("Commands");
            command.HasKey(c => c.Id);
            command.HasIndex(c => c.Status);
            command.HasIndex(c => c.Date);
            command.Property(c => c.StorageDoorCode).HasMaxLength(50);
            command.Property(c => c.PickUpDoorCode).HasMaxLength(50);
            command.Property(c => c.ErrorDescription).HasMaxLength(500);
            command.Property(c => c.Info).HasMaxLength(500);
            command.Property(c => c.WorkstationId).HasMaxLength(40);
            command.Property(c => c.UserId).HasMaxLength(100);
            command.Property(c => c.ReferenceId).HasMaxLength(50);
        });

        modelBuilder.Entity<ExtractionQueueItem>(item =>
        {
            item.ToTable("ExtractionQueue");
            item.HasKey(i => i.Id);
            item.HasIndex(i => i.LoadUnitId);
            item.HasIndex(i => i.AssignmentId);
            item.HasIndex(i => new { i.RelocatorItemId, i.Processed });
            item.Property(i => i.WorkstationId).HasMaxLength(40);
            item.Property(i => i.DoorCode).HasMaxLength(50);
            item.Property(i => i.DestinationDoorCode).HasMaxLength(50);
        });

        modelBuilder.Entity<PendingExtractionOutcome>(outcome =>
        {
            outcome.ToTable("PendingExtractionOutcomes");
            outcome.HasKey(o => o.Id);
            outcome.HasIndex(o => o.ExtractionRowId).IsUnique();
            outcome.Property(o => o.WorkstationId).HasMaxLength(40);
        });

        modelBuilder.Entity<PendingPickedExtractionReport>(report =>
        {
            report.ToTable("PendingPickedExtractionReports");
            report.HasKey(row => row.Id);
            report.HasIndex(row => row.OperationId).IsUnique();
            report.HasIndex(row => row.MissionId);
            report.Property(row => row.UserId).HasMaxLength(100);
            report.Property(row => row.Reason).HasMaxLength(500);
        });

        modelBuilder.Entity<PendingWarehouseTransition>(transition =>
        {
            transition.ToTable("PendingWarehouseTransitions");
            transition.HasKey(row => row.Id);
            transition.HasIndex(row => row.OperationId).IsUnique();
            transition.HasIndex(row => new { row.MissionId, row.Kind }).IsUnique();
            transition.HasIndex(row => new { row.NextAttemptAtUtc, row.Id });
            transition.Property(row => row.PayloadJson).IsRequired();
            // SQLite non ordina DateTimeOffset in modo traducibile. Salviamo i tick UTC: oltre a
            // rendere traducibile il confronto, due offset diversi mantengono l'ordine temporale.
            transition.Property(row => row.CreatedAtUtc)
                .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
            transition.Property(row => row.NextAttemptAtUtc)
                .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        });

        modelBuilder.Entity<PendingCellGiveBack>(giveBack =>
        {
            giveBack.ToTable("PendingCellGiveBacks");
            giveBack.HasKey(row => row.Id);
            giveBack.HasIndex(row => row.OperationId).IsUnique();
            giveBack.HasIndex(row => row.LoadUnitId);
            giveBack.Property(row => row.DoorCode).HasMaxLength(50);
            giveBack.Property(row => row.UserId).HasMaxLength(100);
            giveBack.Property(row => row.Reason).HasMaxLength(500);
        });

        modelBuilder.Entity<ReservationToGiveBack>(reservation =>
        {
            reservation.ToTable("ReservationsToGiveBack");
            reservation.HasKey(row => row.Id);
            reservation.HasIndex(row => row.MissionId).IsUnique();
        });

        modelBuilder.Entity<RetiredHandledReference>(reference =>
        {
            reference.ToTable("RetiredHandledReferences");
            reference.HasKey(row => new { row.Kind, row.Value });
        });

        modelBuilder.Entity<CellToMarkFull>(cell =>
        {
            cell.ToTable("CellsToMarkFull");
            cell.HasKey(row => row.Id);
            cell.HasIndex(row => row.DoorCode).IsUnique();
            cell.Property(row => row.DoorCode).HasMaxLength(50).IsRequired();
        });

        modelBuilder.Entity<PlantItem>(item =>
        {
            item.ToTable("PlantItems");
            item.HasKey(i => i.ItemId);
            item.Property(i => i.Name).HasMaxLength(50);
            item.Property(i => i.Asi).HasMaxLength(4);
            item.Property(i => i.Partition).HasMaxLength(4);
            item.Property(i => i.Subitem).HasMaxLength(4);
            item.Property(i => i.StoragePartition).HasMaxLength(4);
            item.Property(i => i.StoragePartitionReversed).HasMaxLength(4);
        });

        modelBuilder.Entity<PlantJunction>(junction =>
        {
            junction.ToTable("PlantJunctions");
            junction.HasKey(j => j.JunctionId);
            junction.HasIndex(j => new { j.SourceItemId, j.DestinationItemId });
        });

        modelBuilder.Entity<PlantDiagram>(diagram =>
        {
            diagram.ToTable("PlantDiagrams");
            diagram.HasKey(d => d.Id);
            // La chiave e' assegnata da chi scrive (e' sempre 1), non generata.
            diagram.Property(d => d.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<PlantDecoration>(decoration =>
        {
            decoration.ToTable("PlantDecorations");
            decoration.HasKey(d => d.Id);
            // Come PlantDiagram: la chiave e' sempre DefaultId, assegnata da chi scrive.
            decoration.Property(d => d.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<Mission>(mission =>
        {
            mission.ToTable("Missions");
            mission.HasKey(m => m.Id);
            mission.HasIndex(m => m.MissionId).IsUnique();
            mission.Property(m => m.LoadUnitCode).HasMaxLength(50);
            mission.Property(m => m.StatusDescription).HasMaxLength(500);
            mission.Property(m => m.MovingDescription).HasMaxLength(500);
            mission.Property(m => m.StorageDoorCode).HasMaxLength(50);
            mission.Property(m => m.PickUpDoorCode).HasMaxLength(50);
            mission.Property(m => m.SourceAsi).HasMaxLength(4);
            mission.Property(m => m.SourceSubitem).HasMaxLength(4);
            mission.Property(m => m.SourcePartition).HasMaxLength(4);
            mission.Property(m => m.DestinationAsi).HasMaxLength(4);
            mission.Property(m => m.DestinationSubitem).HasMaxLength(4);
            mission.Property(m => m.DestinationPartition).HasMaxLength(4);
        });
    }
}
