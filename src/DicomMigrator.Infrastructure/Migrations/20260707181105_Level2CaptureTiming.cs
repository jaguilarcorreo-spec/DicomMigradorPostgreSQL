using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DicomMigrator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Level2CaptureTiming : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CaptureFinishedDate",
                table: "DiscoveryJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CaptureStartedDate",
                table: "DiscoveryJobs",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CaptureFinishedDate",
                table: "DiscoveryJobs");

            migrationBuilder.DropColumn(
                name: "CaptureStartedDate",
                table: "DiscoveryJobs");
        }
    }
}
