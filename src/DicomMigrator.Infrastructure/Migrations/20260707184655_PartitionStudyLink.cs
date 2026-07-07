using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DicomMigrator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PartitionStudyLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PartitionId",
                table: "DiscoveredStudies",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveredStudies_PartitionId",
                table: "DiscoveredStudies",
                column: "PartitionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DiscoveredStudies_PartitionId",
                table: "DiscoveredStudies");

            migrationBuilder.DropColumn(
                name: "PartitionId",
                table: "DiscoveredStudies");
        }
    }
}
