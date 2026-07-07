using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DicomMigrator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Level2VerificationSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "VerificationLevel",
                table: "Migrations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "MigrationInstances",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MigrationStudyId = table.Column<long>(type: "bigint", nullable: false),
                    SeriesInstanceUid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SopInstanceUid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationInstances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MigrationInstances_MigrationStudies_MigrationStudyId",
                        column: x => x.MigrationStudyId,
                        principalTable: "MigrationStudies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MigInstances_Study_Sop",
                table: "MigrationInstances",
                columns: new[] { "MigrationStudyId", "SopInstanceUid" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MigrationInstances");

            migrationBuilder.DropColumn(
                name: "VerificationLevel",
                table: "Migrations");
        }
    }
}
