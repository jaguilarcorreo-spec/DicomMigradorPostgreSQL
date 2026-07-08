using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DicomMigrator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Level2MissingReport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "VerifyExtraCount",
                table: "MigrationStudies",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "VerifyMissingCount",
                table: "MigrationStudies",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "VerifyMissingUids",
                table: "MigrationStudies",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VerifyExtraCount",
                table: "MigrationStudies");

            migrationBuilder.DropColumn(
                name: "VerifyMissingCount",
                table: "MigrationStudies");

            migrationBuilder.DropColumn(
                name: "VerifyMissingUids",
                table: "MigrationStudies");
        }
    }
}
