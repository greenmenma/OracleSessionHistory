using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OracleSessionHistory.Migrations.SqlServer.Migrations
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
                    Account = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Found = table.Column<bool>(type: "bit", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
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
