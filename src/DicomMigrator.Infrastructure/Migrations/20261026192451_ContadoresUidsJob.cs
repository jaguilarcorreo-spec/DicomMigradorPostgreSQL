using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DicomMigrator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ContadoresUidsJob : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CapturedInstances",
                table: "DiscoveryJobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CapturedStudies",
                table: "DiscoveryJobs",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CapturedInstances",
                table: "DiscoveryJobs");

            migrationBuilder.DropColumn(
                name: "CapturedStudies",
                table: "DiscoveryJobs");
        }
    }
}
