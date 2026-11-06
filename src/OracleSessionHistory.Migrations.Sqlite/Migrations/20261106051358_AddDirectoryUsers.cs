using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OracleSessionHistory.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddDirectoryUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DirectoryUsers",
                columns: table => new
                {
                    Account = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Found = table.Column<bool>(type: "INTEGER", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectoryUsers", x => x.Account);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DirectoryUsers_DisplayName",
                table: "DirectoryUsers",
                column: "DisplayName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DirectoryUsers");
        }
    }
}
