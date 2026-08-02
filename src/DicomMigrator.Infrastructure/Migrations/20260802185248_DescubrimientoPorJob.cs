using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DicomMigrator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DescubrimientoPorJob : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DiscoveredStudies_StudyInstanceUid",
                table: "DiscoveredStudies");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveredStudies_DiscoveryJobId_StudyInstanceUid",
                table: "DiscoveredStudies",
                columns: new[] { "DiscoveryJobId", "StudyInstanceUid" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DiscoveredStudies_DiscoveryJobId_StudyInstanceUid",
                table: "DiscoveredStudies");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveredStudies_StudyInstanceUid",
                table: "DiscoveredStudies",
                column: "StudyInstanceUid",
                unique: true);
        }
    }
}
