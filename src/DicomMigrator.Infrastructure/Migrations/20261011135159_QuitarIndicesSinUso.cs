using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DicomMigrator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class QuitarIndicesSinUso : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MigStudies_Mig_Accession",
                table: "MigrationStudies");

            migrationBuilder.DropIndex(
                name: "IX_MigStudies_Mig_Patient",
                table: "MigrationStudies");

            migrationBuilder.DropIndex(
                name: "IX_DiscoveredStudies_PartitionId",
                table: "DiscoveredStudies");

            migrationBuilder.DropIndex(
                name: "IX_DiscoveredStudies_SourcePacsId_StudyDate",
                table: "DiscoveredStudies");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveredStudies_Job_StudyDate",
                table: "DiscoveredStudies",
                columns: new[] { "DiscoveryJobId", "StudyDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DiscoveredStudies_Job_StudyDate",
                table: "DiscoveredStudies");

            migrationBuilder.CreateIndex(
                name: "IX_MigStudies_Mig_Accession",
                table: "MigrationStudies",
                columns: new[] { "MigrationId", "AccessionNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_MigStudies_Mig_Patient",
                table: "MigrationStudies",
                columns: new[] { "MigrationId", "PatientId" });

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveredStudies_PartitionId",
                table: "DiscoveredStudies",
                column: "PartitionId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveredStudies_SourcePacsId_StudyDate",
                table: "DiscoveredStudies",
                columns: new[] { "SourcePacsId", "StudyDate" });
        }
    }
}
