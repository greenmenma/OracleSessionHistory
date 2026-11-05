using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OracleSessionHistory.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Targets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ConnectionString = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IntervalSeconds = table.Column<int>(type: "int", nullable: false),
                    LinkIntervalSeconds = table.Column<int>(type: "int", nullable: false),
                    IncludeBackground = table.Column<bool>(type: "bit", nullable: false),
                    CollectSqlText = table.Column<bool>(type: "bit", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastPollAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSuccessAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "int", nullable: false),
                    LastSessionCount = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Targets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LinkSources",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TargetId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ConnectionString = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    SelectedLinks = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastPollAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSuccessAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LinkSources_Targets_TargetId",
                        column: x => x.TargetId,
                        principalTable: "Targets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TargetId = table.Column<int>(type: "int", nullable: false),
                    InstId = table.Column<int>(type: "int", nullable: false),
                    Sid = table.Column<int>(type: "int", nullable: false),
                    Serial = table.Column<int>(type: "int", nullable: false),
                    LogonTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AuditSid = table.Column<long>(type: "bigint", nullable: true),
                    Username = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SchemaName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OsUser = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Machine = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Terminal = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Program = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Module = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Action = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClientInfo = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClientIdentifier = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ServiceName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClientProcess = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ServerProcess = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Port = table.Column<int>(type: "int", nullable: true),
                    ClientDriver = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClientVersion = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SessionType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SqlId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastSqlText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Event = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ProcessKind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    JobType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    JobOwner = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    JobName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    JobAction = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ParentInfo = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FirstSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastActiveAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsConnected = table.Column<bool>(type: "bit", nullable: false),
                    DisconnectedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DisconnectDetectedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DisconnectUncertain = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sessions_Targets_TargetId",
                        column: x => x.TargetId,
                        principalTable: "Targets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DbLinkUsages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LinkSourceId = table.Column<int>(type: "int", nullable: false),
                    TargetId = table.Column<int>(type: "int", nullable: false),
                    DbLinkName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    MatchType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    InstId = table.Column<int>(type: "int", nullable: false),
                    Sid = table.Column<int>(type: "int", nullable: false),
                    Serial = table.Column<int>(type: "int", nullable: false),
                    LogonTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Username = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SchemaName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OsUser = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Machine = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Terminal = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Program = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Module = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Action = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ServerProcess = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SqlId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SqlText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MatchedSessionId = table.Column<long>(type: "bigint", nullable: true),
                    FirstSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    HitCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DbLinkUsages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DbLinkUsages_LinkSources_LinkSourceId",
                        column: x => x.LinkSourceId,
                        principalTable: "LinkSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DbLinkUsages_Sessions_MatchedSessionId",
                        column: x => x.MatchedSessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_DbLinkUsages_LinkSourceId_DbLinkName_InstId_Sid_Serial_LogonTime",
                table: "DbLinkUsages",
                columns: new[] { "LinkSourceId", "DbLinkName", "InstId", "Sid", "Serial", "LogonTime" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DbLinkUsages_MatchedSessionId",
                table: "DbLinkUsages",
                column: "MatchedSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_DbLinkUsages_TargetId_LastSeenAt",
                table: "DbLinkUsages",
                columns: new[] { "TargetId", "LastSeenAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LinkSources_TargetId",
                table: "LinkSources",
                column: "TargetId");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_LastSeenAt",
                table: "Sessions",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_LogonTime",
                table: "Sessions",
                column: "LogonTime");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_Machine",
                table: "Sessions",
                column: "Machine");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_Program",
                table: "Sessions",
                column: "Program");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_TargetId_InstId_Sid_Serial_LogonTime",
                table: "Sessions",
                columns: new[] { "TargetId", "InstId", "Sid", "Serial", "LogonTime" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_TargetId_IsConnected",
                table: "Sessions",
                columns: new[] { "TargetId", "IsConnected" });

            migrationBuilder.CreateIndex(
                name: "IX_Targets_Name",
                table: "Targets",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DbLinkUsages");

            migrationBuilder.DropTable(
                name: "LinkSources");

            migrationBuilder.DropTable(
                name: "Sessions");

            migrationBuilder.DropTable(
                name: "Targets");
        }
    }
}
