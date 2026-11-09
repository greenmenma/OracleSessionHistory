using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OracleSessionHistory.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddAppNodeNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppNodeNotes",
                columns: table => new
                {
                    NodeKey = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Memo = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    MainAssignee = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SubAssignees = table.Column<string>(type: "nvarchar(1010)", maxLength: 1010, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppNodeNotes", x => x.NodeKey);
                });

            // 設定済みの担当者をメインの担当者として引き継ぐ
            migrationBuilder.Sql(
                "INSERT INTO AppNodeNotes (NodeKey, MainAssignee, UpdatedAt) SELECT NodeKey, Assignee, UpdatedAt FROM AppAssignees");

            migrationBuilder.DropTable(
                name: "AppAssignees");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppAssignees",
                columns: table => new
                {
                    NodeKey = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Assignee = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppAssignees", x => x.NodeKey);
                });

            // メインの担当者だけを戻す（日本語名・メモ・サブの担当者は失われる）
            migrationBuilder.Sql(
                "INSERT INTO AppAssignees (NodeKey, Assignee, UpdatedAt) SELECT NodeKey, MainAssignee, UpdatedAt FROM AppNodeNotes WHERE MainAssignee IS NOT NULL");

            migrationBuilder.DropTable(
                name: "AppNodeNotes");
        }
    }
}
