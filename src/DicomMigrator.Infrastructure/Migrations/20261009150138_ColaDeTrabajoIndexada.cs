using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DicomMigrator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ColaDeTrabajoIndexada : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MigStudies_active",
                table: "MigrationStudies");

            migrationBuilder.AddColumn<short>(
                name: "ModalityRank",
                table: "MigrationStudies",
                type: "smallint",
                nullable: false,
                defaultValue: (short)999);

            migrationBuilder.CreateIndex(
                name: "IX_MigStudies_queue_newest",
                table: "MigrationStudies",
                columns: new[] { "MigrationId", "ModalityRank", "RetryCount", "StudyDate", "Id" },
                descending: new[] { false, false, false, true, false },
                filter: "\"MigrationStatus\" = 'Pending'")
                .Annotation("Npgsql:IndexNullSortOrder", new[] { NullSortOrder.NullsLast, NullSortOrder.NullsLast, NullSortOrder.NullsLast, NullSortOrder.NullsLast, NullSortOrder.NullsLast });

            migrationBuilder.CreateIndex(
                name: "IX_MigStudies_queue_oldest",
                table: "MigrationStudies",
                columns: new[] { "MigrationId", "ModalityRank", "RetryCount", "StudyDate", "Id" },
                filter: "\"MigrationStatus\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_MigStudies_verify_queue",
                table: "MigrationStudies",
                columns: new[] { "MigrationId", "Id" },
                filter: "\"MigrationStatus\" = 'Migrated'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MigStudies_queue_newest",
                table: "MigrationStudies");

            migrationBuilder.DropIndex(
                name: "IX_MigStudies_queue_oldest",
                table: "MigrationStudies");

            migrationBuilder.DropIndex(
                name: "IX_MigStudies_verify_queue",
                table: "MigrationStudies");

            migrationBuilder.DropColumn(
                name: "ModalityRank",
                table: "MigrationStudies");

            migrationBuilder.CreateIndex(
                name: "IX_MigStudies_active",
                table: "MigrationStudies",
                columns: new[] { "MigrationId", "StudyDate" },
                filter: "\"MigrationStatus\" IN ('Pending','RetryPending')");
        }
    }
}
