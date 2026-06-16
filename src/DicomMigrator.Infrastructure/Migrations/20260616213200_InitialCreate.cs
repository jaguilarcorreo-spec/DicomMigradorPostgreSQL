using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DicomMigrator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DicomNodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Alias = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    NodeType = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LocalAet = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RemoteAet = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RemoteHost = table.Column<string>(type: "text", nullable: false),
                    RemotePort = table.Column<int>(type: "integer", nullable: false),
                    UseTls = table.Column<bool>(type: "boolean", nullable: false),
                    AssociationTimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    OperationTimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    MaxConcurrentAssociations = table.Column<int>(type: "integer", nullable: false),
                    HasDicomWeb = table.Column<bool>(type: "boolean", nullable: false),
                    QidoBaseUrl = table.Column<string>(type: "text", nullable: true),
                    WadoBaseUrl = table.Column<string>(type: "text", nullable: true),
                    StowBaseUrl = table.Column<string>(type: "text", nullable: true),
                    WebBaseUrl = table.Column<string>(type: "text", nullable: true),
                    WebQidoPath = table.Column<string>(type: "text", nullable: true),
                    WebWadoPath = table.Column<string>(type: "text", nullable: true),
                    WebStowPath = table.Column<string>(type: "text", nullable: true),
                    AuthType = table.Column<string>(type: "text", nullable: false),
                    AuthUsername = table.Column<string>(type: "text", nullable: true),
                    EncryptedSecret = table.Column<string>(type: "text", nullable: true),
                    ValidateTls = table.Column<bool>(type: "boolean", nullable: false),
                    HttpTimeoutSeconds = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DicomNodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DiscoveredStudies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StudyInstanceUid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PatientId = table.Column<string>(type: "text", nullable: true),
                    IssuerOfPatientId = table.Column<string>(type: "text", nullable: true),
                    PatientName = table.Column<string>(type: "text", nullable: true),
                    PatientBirthDate = table.Column<string>(type: "text", nullable: true),
                    PatientSex = table.Column<string>(type: "text", nullable: true),
                    AccessionNumber = table.Column<string>(type: "text", nullable: true),
                    StudyDate = table.Column<string>(type: "text", nullable: true),
                    StudyTime = table.Column<string>(type: "text", nullable: true),
                    StudyDescription = table.Column<string>(type: "text", nullable: true),
                    ModalitiesInStudy = table.Column<string>(type: "text", nullable: true),
                    NumberOfStudyRelatedSeries = table.Column<int>(type: "integer", nullable: true),
                    NumberOfStudyRelatedInstances = table.Column<int>(type: "integer", nullable: true),
                    InstitutionName = table.Column<string>(type: "text", nullable: true),
                    RetrieveAETitle = table.Column<string>(type: "text", nullable: true),
                    DiscoveryDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SourcePacsId = table.Column<int>(type: "integer", nullable: false),
                    DiscoveryJobId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveredStudies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DiscoveryRequests",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DiscoveryJobId = table.Column<int>(type: "integer", nullable: false),
                    PartitionId = table.Column<int>(type: "integer", nullable: true),
                    RequestDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SourcePacsId = table.Column<int>(type: "integer", nullable: false),
                    QueryType = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Filters = table.Column<string>(type: "text", nullable: true),
                    DurationMs = table.Column<double>(type: "double precision", nullable: false),
                    Result = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    StudiesReturned = table.Column<int>(type: "integer", nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveryRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LocalConfigurations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LocalAet = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LocalPort = table.Column<int>(type: "integer", nullable: false),
                    LocalHostname = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    MaxConcurrentMigrations = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalConfigurations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DiscoveryJobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    SourcePacsId = table.Column<int>(type: "integer", nullable: false),
                    DiscoveryType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    QueryMethod = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    StartDate = table.Column<string>(type: "text", nullable: true),
                    EndDate = table.Column<string>(type: "text", nullable: true),
                    Modalities = table.Column<string>(type: "text", nullable: true),
                    PacsResultLimit = table.Column<int>(type: "integer", nullable: false),
                    WorkerThreads = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveryJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscoveryJobs_DicomNodes_SourcePacsId",
                        column: x => x.SourcePacsId,
                        principalTable: "DicomNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Migrations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    OriginNodeId = table.Column<int>(type: "integer", nullable: false),
                    DestNodeId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    VerificationStatus = table.Column<string>(type: "text", nullable: false),
                    DiscoveryMethod = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TransferMethod = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    WorkerThreads = table.Column<int>(type: "integer", nullable: false),
                    ModalityPriority = table.Column<string>(type: "text", nullable: false),
                    MaxRetries = table.Column<int>(type: "integer", nullable: false),
                    StartFromDate = table.Column<DateOnly>(type: "date", nullable: true),
                    OldestFirst = table.Column<bool>(type: "boolean", nullable: false),
                    InventoryDateFrom = table.Column<string>(type: "text", nullable: true),
                    InventoryDateTo = table.Column<string>(type: "text", nullable: true),
                    RetryDelaySeconds = table.Column<int>(type: "integer", nullable: false),
                    MigrationAutoPaused = table.Column<bool>(type: "boolean", nullable: false),
                    VerificationAutoPaused = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Migrations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Migrations_DicomNodes_DestNodeId",
                        column: x => x.DestNodeId,
                        principalTable: "DicomNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Migrations_DicomNodes_OriginNodeId",
                        column: x => x.OriginNodeId,
                        principalTable: "DicomNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DiscoveryPartitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DiscoveryJobId = table.Column<int>(type: "integer", nullable: false),
                    PartitionType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StartDate = table.Column<string>(type: "text", nullable: true),
                    EndDate = table.Column<string>(type: "text", nullable: true),
                    Modality = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    StudyTimeFrom = table.Column<string>(type: "text", nullable: true),
                    StudyTimeTo = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    StudiesFound = table.Column<int>(type: "integer", nullable: false),
                    StudiesInserted = table.Column<int>(type: "integer", nullable: false),
                    StudiesUpdated = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DurationMs = table.Column<double>(type: "double precision", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    LockedByWorker = table.Column<string>(type: "text", nullable: true),
                    LockDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveryPartitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscoveryPartitions_DiscoveryJobs_DiscoveryJobId",
                        column: x => x.DiscoveryJobId,
                        principalTable: "DiscoveryJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MigrationId = table.Column<int>(type: "integer", nullable: false),
                    Level = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Action = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Result = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    StudyInstanceUid = table.Column<string>(type: "text", nullable: true),
                    UserOrProcess = table.Column<string>(type: "text", nullable: true),
                    TechnicalMessage = table.Column<string>(type: "text", nullable: true),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditLogs_Migrations_MigrationId",
                        column: x => x.MigrationId,
                        principalTable: "Migrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExecutionWindows",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MigrationId = table.Column<int>(type: "integer", nullable: false),
                    EnabledDays = table.Column<string>(type: "text", nullable: false),
                    StartTime = table.Column<string>(type: "text", nullable: false),
                    EndTime = table.Column<string>(type: "text", nullable: false),
                    TimeZoneId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionWindows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExecutionWindows_Migrations_MigrationId",
                        column: x => x.MigrationId,
                        principalTable: "Migrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MigrationStudies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MigrationId = table.Column<int>(type: "integer", nullable: false),
                    StudyInstanceUid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PatientId = table.Column<string>(type: "text", nullable: true),
                    IssuerOfPatientId = table.Column<string>(type: "text", nullable: true),
                    AccessionNumber = table.Column<string>(type: "text", nullable: true),
                    StudyDate = table.Column<string>(type: "text", nullable: true),
                    ModalitiesInStudy = table.Column<string>(type: "text", nullable: true),
                    SourceSeriesCount = table.Column<int>(type: "integer", nullable: true),
                    SourceInstanceCount = table.Column<int>(type: "integer", nullable: true),
                    TargetSeriesCount = table.Column<int>(type: "integer", nullable: true),
                    TargetInstanceCount = table.Column<int>(type: "integer", nullable: true),
                    MigrationStatus = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    LockedByWorker = table.Column<string>(type: "text", nullable: true),
                    LockDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VerifyLockedByWorker = table.Column<string>(type: "text", nullable: true),
                    VerifyLockDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VerifyRetryCount = table.Column<int>(type: "integer", nullable: false),
                    DiscoveryDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MigrationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MigrationStartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VerificationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VerificationStartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationStudies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MigrationStudies_Migrations_MigrationId",
                        column: x => x.MigrationId,
                        principalTable: "Migrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "LocalConfigurations",
                columns: new[] { "Id", "Description", "LocalAet", "LocalHostname", "LocalPort", "MaxConcurrentMigrations", "UpdatedAt" },
                values: new object[] { 1, "Configuración SCU local por defecto", "MIGRATOR_SCU", "", 11113, 3, new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_MigrationId",
                table: "AuditLogs",
                column: "MigrationId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_MigrationId_Timestamp",
                table: "AuditLogs",
                columns: new[] { "MigrationId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Timestamp",
                table: "AuditLogs",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveredStudies_ModalitiesInStudy",
                table: "DiscoveredStudies",
                column: "ModalitiesInStudy");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveredStudies_SourcePacsId_StudyDate",
                table: "DiscoveredStudies",
                columns: new[] { "SourcePacsId", "StudyDate" });

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveredStudies_StudyInstanceUid",
                table: "DiscoveredStudies",
                column: "StudyInstanceUid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryJobs_SourcePacsId",
                table: "DiscoveryJobs",
                column: "SourcePacsId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryPartitions_DiscoveryJobId_Status",
                table: "DiscoveryPartitions",
                columns: new[] { "DiscoveryJobId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryRequests_DiscoveryJobId",
                table: "DiscoveryRequests",
                column: "DiscoveryJobId");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionWindows_MigrationId",
                table: "ExecutionWindows",
                column: "MigrationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Migrations_DestNodeId",
                table: "Migrations",
                column: "DestNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_Migrations_OriginNodeId",
                table: "Migrations",
                column: "OriginNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_MigrationStudies_MigrationId_MigrationStatus",
                table: "MigrationStudies",
                columns: new[] { "MigrationId", "MigrationStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_MigrationStudies_MigrationId_StudyInstanceUid",
                table: "MigrationStudies",
                columns: new[] { "MigrationId", "StudyInstanceUid" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "DiscoveredStudies");

            migrationBuilder.DropTable(
                name: "DiscoveryPartitions");

            migrationBuilder.DropTable(
                name: "DiscoveryRequests");

            migrationBuilder.DropTable(
                name: "ExecutionWindows");

            migrationBuilder.DropTable(
                name: "LocalConfigurations");

            migrationBuilder.DropTable(
                name: "MigrationStudies");

            migrationBuilder.DropTable(
                name: "DiscoveryJobs");

            migrationBuilder.DropTable(
                name: "Migrations");

            migrationBuilder.DropTable(
                name: "DicomNodes");
        }
    }
}
