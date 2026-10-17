using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DicomMigrator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LineaTiempoRendimiento : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MigrationPerformance",
                columns: table => new
                {
                    MigrationId = table.Column<int>(type: "integer", nullable: false),
                    Events = table.Column<string>(type: "jsonb", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationPerformance", x => x.MigrationId);
                    table.ForeignKey(
                        name: "FK_MigrationPerformance_Migrations_MigrationId",
                        column: x => x.MigrationId,
                        principalTable: "Migrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MigrationPerformance");
        }
    }
}
