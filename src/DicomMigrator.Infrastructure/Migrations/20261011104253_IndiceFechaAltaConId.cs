using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DicomMigrator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class IndiceFechaAltaConId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MigStudies_Mig_DiscDate",
                table: "MigrationStudies");

            migrationBuilder.CreateIndex(
                name: "IX_MigStudies_Mig_DiscDate",
                table: "MigrationStudies",
                columns: new[] { "MigrationId", "DiscoveryDate", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MigStudies_Mig_DiscDate",
                table: "MigrationStudies");

            migrationBuilder.CreateIndex(
                name: "IX_MigStudies_Mig_DiscDate",
                table: "MigrationStudies",
                columns: new[] { "MigrationId", "DiscoveryDate" });
        }
    }
}
