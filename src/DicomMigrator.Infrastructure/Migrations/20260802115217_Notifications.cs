using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DicomMigrator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Notifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificationOutbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EventKind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Recipients = table.Column<string>(type: "text", nullable: false),
                    Subject = table.Column<string>(type: "text", nullable: false),
                    BodyHtml = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    LastAttemptUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SentUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationOutbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    SmtpHost = table.Column<string>(type: "text", nullable: true),
                    SmtpPort = table.Column<int>(type: "integer", nullable: false),
                    SmtpUser = table.Column<string>(type: "text", nullable: true),
                    SmtpPassword = table.Column<string>(type: "text", nullable: true),
                    SmtpUseStartTls = table.Column<bool>(type: "boolean", nullable: false),
                    FromAddress = table.Column<string>(type: "text", nullable: true),
                    Recipients = table.Column<string>(type: "text", nullable: true),
                    BaseUrl = table.Column<string>(type: "text", nullable: true),
                    NotifyMigrationCompleted = table.Column<bool>(type: "boolean", nullable: false),
                    NotifyMigrationFailed = table.Column<bool>(type: "boolean", nullable: false),
                    NotifyAutoPaused = table.Column<bool>(type: "boolean", nullable: false),
                    NotifyDiscoveryCompleted = table.Column<bool>(type: "boolean", nullable: false),
                    NotifyDiscoveryFailed = table.Column<bool>(type: "boolean", nullable: false),
                    NotifyCaptureFinished = table.Column<bool>(type: "boolean", nullable: false),
                    NotifyPopulateFinished = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_Status_Id",
                table: "NotificationOutbox",
                columns: new[] { "Status", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationOutbox");

            migrationBuilder.DropTable(
                name: "NotificationSettings");
        }
    }
}
