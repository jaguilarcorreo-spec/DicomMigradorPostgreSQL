using DicomMigrator.Core.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

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
    public DbSet<LicenseState>      LicenseStates    => Set<LicenseState>();
    public DbSet<NotificationSettings> NotificationSettings => Set<NotificationSettings>();
    public DbSet<NotificationOutbox>   NotificationOutbox   => Set<NotificationOutbox>();
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

            // 1:N — una migración tiene como mucho dos tramos (Tramo1 / Tramo2), cada
            // uno con sus propios días, horario y modo 24 h.
            // Antes era 1:1 (HasOne/WithOne), lo que forzaba un índice ÚNICO sobre
            // MigrationId e impedía separar el horario de L-V del de S-D.
            e.HasMany(x => x.Windows)
             .WithOne(x => x.Migration)
             .HasForeignKey(x => x.MigrationId)
             .OnDelete(DeleteBehavior.Cascade);

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
            e.Property(x => x.Kind).HasMaxLength(20);
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
            // ── Colas de trabajo (CONC-9) ────────────────────────────────────────
            // Índices PARCIALES que ya están en el ORDEN en que los workers toman los
            // estudios. Con FOR UPDATE SKIP LOCKED cada worker recorre el índice en orden
            // y se queda con el primero que nadie tenga bloqueado: ~1 ms aunque haya
            // millones pendientes, y sin que todos compitan por el mismo estudio. Solo
            // cubren los estudios en cola, así que encogen a medida que se migra.
            // Sustituyen a IX_MigStudies_active (MigrationId, StudyDate), que el
            // planificador no usaba porque no coincidía con el orden de la cola (BD-7).
            e.Property(x => x.ModalityRank).HasDefaultValue((short)999);

            // Orden por defecto: más recientes primero (StudyDate DESC, sin fecha al final).
            e.HasIndex(x => new { x.MigrationId, x.ModalityRank, x.RetryCount, x.StudyDate, x.Id }, "IX_MigStudies_queue_newest")
             .HasFilter("\"MigrationStatus\" = 'Pending'")
             .IsDescending(false, false, false, true, false)
             .HasNullSortOrder(NullSortOrder.NullsLast, NullSortOrder.NullsLast, NullSortOrder.NullsLast,
                               NullSortOrder.NullsLast, NullSortOrder.NullsLast);

            // Opción "más antiguos primero" (StudyDate ASC, sin fecha al final).
            e.HasIndex(x => new { x.MigrationId, x.ModalityRank, x.RetryCount, x.StudyDate, x.Id }, "IX_MigStudies_queue_oldest")
             .HasFilter("\"MigrationStatus\" = 'Pending'");

            // Cola de verificación: los migrados por orden de Id.
            e.HasIndex(x => new { x.MigrationId, x.Id }, "IX_MigStudies_verify_queue")
             .HasFilter("\"MigrationStatus\" = 'Migrated'");

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
            // Sello de seguridad de la sesión (SEC-2). Vacío en los usuarios existentes
            // hasta su próximo acceso, que les asigna uno.
            e.Property(x => x.SecurityStamp).IsRequired().HasMaxLength(64).HasDefaultValue("");
            // El nombre se guarda ya normalizado en minúsculas, así que el índice
            // único basta para impedir duplicados por diferencias de mayúsculas.
            e.HasIndex(x => x.UserName).IsUnique();
        });

        // ── LicenseState (fila única, Id=1) ──────────────────────────────────
        mb.Entity<LicenseState>(e =>
        {
            e.HasKey(x => x.Id);
            // Id fijo a 1: no autogenerado, hay una sola fila de estado de licencia.
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Token).HasColumnType("text");
            e.Property(x => x.LicId).HasMaxLength(64);
        });

        // ── NotificationSettings (fila única, Id=1) ──────────────────────────
        mb.Entity<NotificationSettings>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
        });

        // ── NotificationOutbox ───────────────────────────────────────────────
        mb.Entity<NotificationOutbox>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.EventKind).HasMaxLength(40);
            e.Property(x => x.Status).HasMaxLength(20);
            // El dispatcher consulta por estado → índice para no escanear la tabla.
            e.HasIndex(x => new { x.Status, x.Id });
        });

        mb.Entity<MigrationAuditLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Level).HasMaxLength(10);
            e.Property(x => x.Action).HasMaxLength(40);
            e.Property(x => x.Result).HasMaxLength(10);
            // Sin índice propio sobre MigrationId: lo cubre (MigrationId, Timestamp), que
            // sirve también para la FK. Uno más solo encarecía cada INSERT de auditoría.
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
            // Clave única POR JOB (no global): cada descubrimiento es independiente, así que
            // el mismo StudyInstanceUID (con sus series e instancias) puede existir en varios
            // jobs a la vez. La clave lidera por DiscoveryJobId, con lo que también sirve de
            // índice para las consultas por job (que son la mayoría).
            e.HasIndex(x => new { x.DiscoveryJobId, x.StudyInstanceUid }).IsUnique();
            e.HasIndex(x => new { x.SourcePacsId, x.StudyDate });
            // Sin índice sobre ModalitiesInStudy: el filtro busca por subcadena (Contains →
            // strpos), que un B-tree no puede resolver, y los códigos de modalidad (2
            // caracteres) son demasiado cortos para pg_trgm. Solo encarecía cada INSERT.
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
