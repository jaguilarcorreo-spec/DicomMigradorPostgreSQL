using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DicomMigrator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Level2CaptureOnDiscovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CaptureStatus",
                table: "Migrations");

            migrationBuilder.AddColumn<string>(
                name: "CaptureStatus",
                table: "DiscoveryJobs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "DiscoveredInstances",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DiscoveredStudyId = table.Column<long>(type: "bigint", nullable: false),
                    SeriesInstanceUid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SopInstanceUid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveredInstances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscoveredInstances_DiscoveredStudies_DiscoveredStudyId",
                        column: x => x.DiscoveredStudyId,
                        principalTable: "DiscoveredStudies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DiscInstances_Study_Sop",
                table: "DiscoveredInstances",
                columns: new[] { "DiscoveredStudyId", "SopInstanceUid" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiscoveredInstances");

            migrationBuilder.DropColumn(
                name: "CaptureStatus",
                table: "DiscoveryJobs");

            migrationBuilder.AddColumn<string>(
                name: "CaptureStatus",
                table: "Migrations",
                type: "text",
                nullable: false,
                defaultValue: "");
        }
    }
}
