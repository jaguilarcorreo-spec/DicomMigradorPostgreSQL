using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DicomMigrator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PobladoProgreso : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PopulateDone",
                table: "Migrations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PopulateError",
                table: "Migrations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PopulateSourceJobId",
                table: "Migrations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PopulateStatus",
                table: "Migrations",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "PopulateTotal",
                table: "Migrations",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PopulateDone",
                table: "Migrations");

            migrationBuilder.DropColumn(
                name: "PopulateError",
                table: "Migrations");

            migrationBuilder.DropColumn(
                name: "PopulateSourceJobId",
                table: "Migrations");

            migrationBuilder.DropColumn(
                name: "PopulateStatus",
                table: "Migrations");

            migrationBuilder.DropColumn(
                name: "PopulateTotal",
                table: "Migrations");
        }
    }
}
