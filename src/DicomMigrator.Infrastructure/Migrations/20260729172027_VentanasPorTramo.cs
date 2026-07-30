using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DicomMigrator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class VentanasPorTramo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExecutionWindows_MigrationId",
                table: "ExecutionWindows");

            migrationBuilder.AddColumn<bool>(
                name: "AllDay",
                table: "ExecutionWindows",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "ExecutionWindows",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Tramo1");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionWindows_MigrationId",
                table: "ExecutionWindows",
                column: "MigrationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExecutionWindows_MigrationId",
                table: "ExecutionWindows");

            migrationBuilder.DropColumn(
                name: "AllDay",
                table: "ExecutionWindows");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "ExecutionWindows");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionWindows_MigrationId",
                table: "ExecutionWindows",
                column: "MigrationId",
                unique: true);
        }
    }
}
