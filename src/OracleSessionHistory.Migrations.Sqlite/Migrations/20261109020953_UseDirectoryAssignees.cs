using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OracleSessionHistory.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class UseDirectoryAssignees : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 文字列で登録していた担当者は引き継がない（AD から選び直す）。担当者だけを設定していた行は削除する
            migrationBuilder.Sql("DELETE FROM AppNodeNotes WHERE DisplayName IS NULL AND Memo IS NULL");

            migrationBuilder.DropColumn(
                name: "MainAssignee",
                table: "AppNodeNotes");

            migrationBuilder.DropColumn(
                name: "SubAssignees",
                table: "AppNodeNotes");

            migrationBuilder.CreateTable(
                name: "AppNodeAssignees",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NodeKey = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    IsMain = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Account = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppNodeAssignees", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppNodeAssignees_AppNodeNotes_NodeKey",
                        column: x => x.NodeKey,
                        principalTable: "AppNodeNotes",
                        principalColumn: "NodeKey",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppNodeAssignees_Account",
                table: "AppNodeAssignees",
                column: "Account");

            migrationBuilder.CreateIndex(
                name: "IX_AppNodeAssignees_NodeKey_Account",
                table: "AppNodeAssignees",
                columns: new[] { "NodeKey", "Account" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppNodeAssignees");

            migrationBuilder.AddColumn<string>(
                name: "MainAssignee",
                table: "AppNodeNotes",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubAssignees",
                table: "AppNodeNotes",
                type: "TEXT",
                maxLength: 1010,
                nullable: true);
        }
    }
}
