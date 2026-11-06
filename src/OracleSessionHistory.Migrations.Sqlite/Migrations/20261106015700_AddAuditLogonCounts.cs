using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OracleSessionHistory.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditLogonCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AuditError",
                table: "Targets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AuditFetchedAt",
                table: "Targets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AuditFrom",
                table: "Targets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AuditTo",
                table: "Targets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AuditLogonCounts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TargetId = table.Column<int>(type: "INTEGER", nullable: false),
                    Day = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Machine = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Count = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogonCounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditLogonCounts_Targets_TargetId",
                        column: x => x.TargetId,
                        principalTable: "Targets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogonCounts_TargetId_Day_Machine",
                table: "AuditLogonCounts",
                columns: new[] { "TargetId", "Day", "Machine" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLogonCounts");

            migrationBuilder.DropColumn(
                name: "AuditError",
                table: "Targets");

            migrationBuilder.DropColumn(
                name: "AuditFetchedAt",
                table: "Targets");

            migrationBuilder.DropColumn(
                name: "AuditFrom",
                table: "Targets");

            migrationBuilder.DropColumn(
                name: "AuditTo",
                table: "Targets");
        }
    }
}
