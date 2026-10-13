using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DicomMigrator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ComposicionLote : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MigrationComposition",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MigrationId = table.Column<int>(type: "integer", nullable: false),
                    Month = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    Modality = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Studies = table.Column<int>(type: "integer", nullable: false),
                    Images = table.Column<long>(type: "bigint", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationComposition", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MigrationComposition_Migrations_MigrationId",
                        column: x => x.MigrationId,
                        principalTable: "Migrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MigComposition_Mig_Month_Mod",
                table: "MigrationComposition",
                columns: new[] { "MigrationId", "Month", "Modality" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MigrationComposition");
        }
    }
}
