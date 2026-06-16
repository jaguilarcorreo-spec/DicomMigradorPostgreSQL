using DicomMigrator.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace DicomMigrator.Infrastructure.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    // ── DbSets ───────────────────────────────────────────────────────────────
    public DbSet<DicomNode>         DicomNodes       => Set<DicomNode>();
    public DbSet<Migration>         Migrations       => Set<Migration>();
    public DbSet<ExecutionWindow>   ExecutionWindows => Set<ExecutionWindow>();
    public DbSet<MigrationStudy>    MigrationStudies => Set<MigrationStudy>();
    public DbSet<MigrationAuditLog> AuditLogs        => Set<MigrationAuditLog>();
    public DbSet<LocalConfiguration> LocalConfigurations => Set<LocalConfiguration>();

    // ── Discovery Engine (RF-020) ──────────────────────────────────────────────
    public DbSet<DiscoveryJob>       DiscoveryJobs       => Set<DiscoveryJob>();
    public DbSet<DiscoveryPartition> DiscoveryPartitions => Set<DiscoveryPartition>();
    public DbSet<DiscoveredStudy>    DiscoveredStudies   => Set<DiscoveredStudy>();
    public DbSet<DiscoveryRequest>   DiscoveryRequests   => Set<DiscoveryRequest>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // ── DicomNode ────────────────────────────────────────────────────────
        mb.Entity<DicomNode>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Alias).IsRequired().HasMaxLength(100);
            e.Property(x => x.RemoteAet).HasMaxLength(16);
            e.Property(x => x.LocalAet).HasMaxLength(16);

            e.HasMany(x => x.MigrationsAsOrigin)
             .WithOne(x => x.OriginNode)
             .HasForeignKey(x => x.OriginNodeId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasMany(x => x.MigrationsAsDest)
             .WithOne(x => x.DestNode)
             .HasForeignKey(x => x.DestNodeId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // ── Migration ────────────────────────────────────────────────────────
        mb.Entity<Migration>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(200);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.DiscoveryMethod).HasMaxLength(20);
            e.Property(x => x.TransferMethod).HasMaxLength(30);

            e.HasOne(x => x.Window)
             .WithOne(x => x.Migration)
             .HasForeignKey<ExecutionWindow>(x => x.MigrationId);

            e.HasMany(x => x.Studies)
             .WithOne(x => x.Migration)
             .HasForeignKey(x => x.MigrationId);

            e.HasMany(x => x.AuditLogs)
             .WithOne(x => x.Migration)
             .HasForeignKey(x => x.MigrationId);
        });

        // ── ExecutionWindow ──────────────────────────────────────────────────
        mb.Entity<ExecutionWindow>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.TimeZoneId).HasMaxLength(50);
            // SQLite doesn't have TimeOnly natively — store as string HH:mm
            e.Property(x => x.StartTime)
             .HasConversion(t => t.ToString("HH:mm"), s => TimeOnly.Parse(s));
            e.Property(x => x.EndTime)
             .HasConversion(t => t.ToString("HH:mm"), s => TimeOnly.Parse(s));
        });

        // ── MigrationStudy ───────────────────────────────────────────────────
        mb.Entity<MigrationStudy>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.StudyInstanceUid).IsRequired().HasMaxLength(64);
            e.Property(x => x.MigrationStatus).HasMaxLength(30);

            // Composite unique: same UID cannot appear twice in the same migration
            e.HasIndex(x => new { x.MigrationId, x.StudyInstanceUid }).IsUnique();

            // Index on status for efficient worker queries
            e.HasIndex(x => new { x.MigrationId, x.MigrationStatus });
        });

        // ── MigrationAuditLog ────────────────────────────────────────────────
        mb.Entity<MigrationAuditLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Level).HasMaxLength(10);
            e.Property(x => x.Action).HasMaxLength(40);
            e.Property(x => x.Result).HasMaxLength(10);
            e.HasIndex(x => x.MigrationId);
            // Las consultas de la UI ordenan por Timestamp DESC con LIMIT N.
            // Sin estos índices, con la tabla llena (millones de filas tras una
            // migración masiva) cada consulta hace un escaneo + ordenación completos.
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => new { x.MigrationId, x.Timestamp });
        });

        // ── Discovery Engine (RF-020) ────────────────────────────────────────
        mb.Entity<DiscoveryJob>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(200);
            e.Property(x => x.DiscoveryType).HasMaxLength(20);
            e.Property(x => x.QueryMethod).HasMaxLength(10);
            e.Property(x => x.Status).HasMaxLength(20);

            e.HasOne(x => x.SourcePacs)
             .WithMany()
             .HasForeignKey(x => x.SourcePacsId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasMany(x => x.Partitions)
             .WithOne(x => x.DiscoveryJob)
             .HasForeignKey(x => x.DiscoveryJobId)
             .OnDelete(DeleteBehavior.Cascade);

            // SQLite stores DateOnly as TEXT
            e.Property(x => x.StartDate).HasConversion(
                d => d.HasValue ? d.Value.ToString("yyyy-MM-dd") : null,
                s => string.IsNullOrEmpty(s) ? null : DateOnly.Parse(s));
            e.Property(x => x.EndDate).HasConversion(
                d => d.HasValue ? d.Value.ToString("yyyy-MM-dd") : null,
                s => string.IsNullOrEmpty(s) ? null : DateOnly.Parse(s));
        });

        mb.Entity<DiscoveryPartition>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.PartitionType).HasMaxLength(20);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Modality).HasMaxLength(16);
            e.HasIndex(x => new { x.DiscoveryJobId, x.Status });

            e.Property(x => x.StartDate).HasConversion(
                d => d.HasValue ? d.Value.ToString("yyyy-MM-dd") : null,
                s => string.IsNullOrEmpty(s) ? null : DateOnly.Parse(s));
            e.Property(x => x.EndDate).HasConversion(
                d => d.HasValue ? d.Value.ToString("yyyy-MM-dd") : null,
                s => string.IsNullOrEmpty(s) ? null : DateOnly.Parse(s));
        });

        mb.Entity<DiscoveredStudy>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.StudyInstanceUid).IsRequired().HasMaxLength(64);
            // Unique inventory key — same study not duplicated across the inventory
            e.HasIndex(x => x.StudyInstanceUid).IsUnique();
            e.HasIndex(x => new { x.SourcePacsId, x.StudyDate });
            e.HasIndex(x => x.ModalitiesInStudy);
        });

        mb.Entity<DiscoveryRequest>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.QueryType).HasMaxLength(10);
            e.Property(x => x.Result).HasMaxLength(10);
            e.HasIndex(x => x.DiscoveryJobId);
        });

        // ── LocalConfiguration ────────────────────────────────────────────────
        mb.Entity<LocalConfiguration>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.LocalAet).IsRequired().HasMaxLength(16);

            // Seed — mismo patrón que el Tester
            e.HasData(new LocalConfiguration
            {
                Id            = 1,
                LocalAet      = "MIGRATOR_SCU",
                LocalPort     = 11113,
                LocalHostname = "",
                Description   = "Configuración SCU local por defecto",
                MaxConcurrentMigrations = 3,
                UpdatedAt     = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
        });
    }
}
