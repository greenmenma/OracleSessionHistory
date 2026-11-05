using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OracleSessionHistory.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkOriginAndTargetPrograms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TargetPrograms",
                table: "ProcessInspectionSettings",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AppDetail",
                table: "DbLinkUsages",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AppName",
                table: "DbLinkUsages",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ClientProcess",
                table: "DbLinkUsages",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IisAppPool",
                table: "DbLinkUsages",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IisSite",
                table: "DbLinkUsages",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProcCommandLine",
                table: "DbLinkUsages",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProcInfoAt",
                table: "DbLinkUsages",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProcInfoError",
                table: "DbLinkUsages",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProcInfoState",
                table: "DbLinkUsages",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ProcName",
                table: "DbLinkUsages",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProcOwner",
                table: "DbLinkUsages",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProcParentName",
                table: "DbLinkUsages",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProcPath",
                table: "DbLinkUsages",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProcStartedAt",
                table: "DbLinkUsages",
                type: "datetime2",
                nullable: true);

            // 既存の DB Link 利用元は観測済みのため、プロセス情報は取得しない（新しく観測した行から取得する）
            migrationBuilder.Sql("UPDATE DbLinkUsages SET AppName = COALESCE(Program, N'(不明)'), ProcInfoState = 'NotApplicable'");

            // このシステム自身の接続（MODULE = OracleSessionHistory）は種別を分けて一覧から除く
            migrationBuilder.Sql("UPDATE Sessions SET ProcessKind = 'Self', ProcInfoState = 'NotApplicable' " +
                "WHERE ProcessKind = 'Client' AND (UPPER(Module) = 'ORACLESESSIONHISTORY' OR UPPER(Program) LIKE 'ORACLESESSIONHISTORY.%')");

            migrationBuilder.CreateIndex(
                name: "IX_DbLinkUsages_ProcInfoState_LastSeenAt",
                table: "DbLinkUsages",
                columns: new[] { "ProcInfoState", "LastSeenAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DbLinkUsages_ProcInfoState_LastSeenAt",
                table: "DbLinkUsages");

            migrationBuilder.DropColumn(
                name: "TargetPrograms",
                table: "ProcessInspectionSettings");

            migrationBuilder.DropColumn(
                name: "AppDetail",
                table: "DbLinkUsages");

            migrationBuilder.DropColumn(
                name: "AppName",
                table: "DbLinkUsages");

            migrationBuilder.DropColumn(
                name: "ClientProcess",
                table: "DbLinkUsages");

            migrationBuilder.DropColumn(
                name: "IisAppPool",
                table: "DbLinkUsages");

            migrationBuilder.DropColumn(
                name: "IisSite",
                table: "DbLinkUsages");

            migrationBuilder.DropColumn(
                name: "ProcCommandLine",
                table: "DbLinkUsages");

            migrationBuilder.DropColumn(
                name: "ProcInfoAt",
                table: "DbLinkUsages");

            migrationBuilder.DropColumn(
                name: "ProcInfoError",
                table: "DbLinkUsages");

            migrationBuilder.DropColumn(
                name: "ProcInfoState",
                table: "DbLinkUsages");

            migrationBuilder.DropColumn(
                name: "ProcName",
                table: "DbLinkUsages");

            migrationBuilder.DropColumn(
                name: "ProcOwner",
                table: "DbLinkUsages");

            migrationBuilder.DropColumn(
                name: "ProcParentName",
                table: "DbLinkUsages");

            migrationBuilder.DropColumn(
                name: "ProcPath",
                table: "DbLinkUsages");

            migrationBuilder.DropColumn(
                name: "ProcStartedAt",
                table: "DbLinkUsages");
        }
    }
}
