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
    public DbSet<MigrationInstance> MigrationInstances => Set<MigrationInstance>();
    public DbSet<MigrationAuditLog> AuditLogs        => Set<MigrationAuditLog>();
    public DbSet<AppUser>           AppUsers         => Set<AppUser>();
    public DbSet<LocalConfiguration> LocalConfigurations => Set<LocalConfiguration>();

    // ── Discovery Engine (RF-020) ──────────────────────────────────────────────
    public DbSet<DiscoveryJob>       DiscoveryJobs       => Set<DiscoveryJob>();
    public DbSet<DiscoveryPartition> DiscoveryPartitions => Set<DiscoveryPartition>();
    public DbSet<DiscoveredStudy>    DiscoveredStudies   => Set<DiscoveredStudy>();
    public DbSet<DiscoveredInstance> DiscoveredInstances => Set<DiscoveredInstance>();
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
            // PostgreSQL tiene 'time' nativo: Npgsql mapea TimeOnly directamente,
            // sin conversión a string. (Antes se guardaba como "HH:mm" por SQLite.)
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

            // Índices de rendimiento (antes creados por SQL en EnsureIndexesAsync;
            // ahora en el modelo para que entren en las migraciones EF y los cree
            // el dueño del esquema con los permisos correctos).
            //
            // Índice PARCIAL para AcquireNextPending: solo cubre estudios accionables,
            // así se mantiene pequeño aunque la tabla tenga millones de filas.
            e.HasIndex(x => new { x.MigrationId, x.StudyDate })
             .HasDatabaseName("IX_MigStudies_active")
             .HasFilter("\"MigrationStatus\" IN ('Pending','RetryPending')");

            e.HasIndex(x => new { x.MigrationId, x.DiscoveryDate })
             .HasDatabaseName("IX_MigStudies_Mig_DiscDate");

            e.HasIndex(x => new { x.MigrationId, x.PatientId })
             .HasDatabaseName("IX_MigStudies_Mig_Patient");

            e.HasIndex(x => new { x.MigrationId, x.AccessionNumber })
             .HasDatabaseName("IX_MigStudies_Mig_Accession");
        });

        // Nivel 2 de verificación: conjunto de UIDs de ORIGEN por estudio.
        mb.Entity<MigrationInstance>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.SeriesInstanceUid).IsRequired().HasMaxLength(64);
            e.Property(x => x.SopInstanceUid).IsRequired().HasMaxLength(64);

            // Un SOPInstanceUID no puede repetirse dentro del mismo estudio.
            e.HasIndex(x => new { x.MigrationStudyId, x.SopInstanceUid })
             .IsUnique()
             .HasDatabaseName("IX_MigInstances_Study_Sop");

            // FK a MigrationStudy con borrado en cascada (limpia al borrar el
            // estudio o la migración completa).
            e.HasOne(x => x.Study)
             .WithMany(s => s.Instances)
             .HasForeignKey(x => x.MigrationStudyId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── MigrationAuditLog ────────────────────────────────────────────────
        mb.Entity<AppUser>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserName).IsRequired().HasMaxLength(64);
            e.Property(x => x.DisplayName).HasMaxLength(120);
            e.Property(x => x.PasswordHash).IsRequired().HasMaxLength(256);
            e.Property(x => x.Role).IsRequired().HasMaxLength(20);
            // El nombre se guarda ya normalizado en minúsculas, así que el índice
            // único basta para impedir duplicados por diferencias de mayúsculas.
            e.HasIndex(x => x.UserName).IsUnique();
        });

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

            // PostgreSQL tiene 'date' nativo: Npgsql mapea DateOnly? directamente.
            // (Antes se guardaba como TEXT "yyyy-MM-dd" por SQLite.)
        });

        mb.Entity<DiscoveryPartition>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.PartitionType).HasMaxLength(20);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Modality).HasMaxLength(16);
            e.HasIndex(x => new { x.DiscoveryJobId, x.Status });
            // PostgreSQL 'date' nativo: Npgsql mapea DateOnly? directamente.
        });

        mb.Entity<DiscoveredStudy>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.StudyInstanceUid).IsRequired().HasMaxLength(64);
            // Unique inventory key — same study not duplicated across the inventory
            e.HasIndex(x => x.StudyInstanceUid).IsUnique();
            e.HasIndex(x => new { x.SourcePacsId, x.StudyDate });
            e.HasIndex(x => x.ModalitiesInStudy);
            e.HasIndex(x => x.PartitionId);
        });

        // Nivel 2: UIDs de origen por estudio descubierto. FK en CASCADE para que
        // al borrar/resetear un job (que hace ExecuteDelete sobre DiscoveredStudies)
        // la BD arrastre estas instancias vía ON DELETE CASCADE.
        mb.Entity<DiscoveredInstance>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.SeriesInstanceUid).IsRequired().HasMaxLength(64);
            e.Property(x => x.SopInstanceUid).IsRequired().HasMaxLength(64);
            e.HasIndex(x => new { x.DiscoveredStudyId, x.SopInstanceUid })
             .IsUnique()
             .HasDatabaseName("IX_DiscInstances_Study_Sop");
            e.HasOne(x => x.Study)
             .WithMany(s => s.Instances)
             .HasForeignKey(x => x.DiscoveredStudyId)
             .OnDelete(DeleteBehavior.Cascade);
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
