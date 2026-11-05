using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OracleSessionHistory.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddProcessInfo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AppDetail",
                table: "Sessions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AppName",
                table: "Sessions",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "IisAppPool",
                table: "Sessions",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IisSite",
                table: "Sessions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProcCommandLine",
                table: "Sessions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProcInfoAt",
                table: "Sessions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProcInfoError",
                table: "Sessions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProcInfoState",
                table: "Sessions",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ProcName",
                table: "Sessions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProcOwner",
                table: "Sessions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProcParentName",
                table: "Sessions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProcPath",
                table: "Sessions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProcStartedAt",
                table: "Sessions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProcessInspectionSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    UserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Password = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DnsSuffix = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    TimeoutSeconds = table.Column<int>(type: "int", nullable: false),
                    ExcludeMachines = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessInspectionSettings", x => x.Id);
                });

            // 既存の履歴はプロセスが終了しているため取得対象外とし、アプリ名は PROGRAM で埋める
            migrationBuilder.Sql("UPDATE Sessions SET AppName = COALESCE(Program, N'(不明)'), ProcInfoState = 'NotApplicable'");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_IsConnected_ProcInfoState",
                table: "Sessions",
                columns: new[] { "IsConnected", "ProcInfoState" });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_TargetId_Machine_AppName",
                table: "Sessions",
                columns: new[] { "TargetId", "Machine", "AppName" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProcessInspectionSettings");

            migrationBuilder.DropIndex(
                name: "IX_Sessions_IsConnected_ProcInfoState",
                table: "Sessions");

            migrationBuilder.DropIndex(
                name: "IX_Sessions_TargetId_Machine_AppName",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "AppDetail",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "AppName",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "IisAppPool",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "IisSite",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "ProcCommandLine",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "ProcInfoAt",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "ProcInfoError",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "ProcInfoState",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "ProcName",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "ProcOwner",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "ProcParentName",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "ProcPath",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "ProcStartedAt",
                table: "Sessions");
        }
    }
}
