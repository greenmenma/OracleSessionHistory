using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OracleSessionHistory.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddAncestryAndLinkOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TargetPrograms",
                table: "ProcessInspectionSettings");

            migrationBuilder.AddColumn<string>(
                name: "LinkOriginApp",
                table: "Sessions",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LinkOriginMachine",
                table: "Sessions",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProcAncestry",
                table: "Sessions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProcAncestry",
                table: "DbLinkUsages",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_LinkOriginMachine_LinkOriginApp",
                table: "Sessions",
                columns: new[] { "LinkOriginMachine", "LinkOriginApp" });

            // 登録した実行ファイルだけを調べる仕様をやめたため、それで対象外にしていた接続中のクライアントを調べ直す
            migrationBuilder.Sql(@"UPDATE Sessions SET ProcInfoState = 'Pending', ProcInfoError = NULL WHERE IsConnected = 1 AND ProcessKind = 'Client' AND ProcInfoState = 'NotApplicable' AND ProcInfoError LIKE N'%実行ファイルに登録されていません%'");

            // 端末名の前後の空白を除く（新しい行は SessionTracker・CollectionService が除いて保存する）
            migrationBuilder.Sql(@"UPDATE Sessions SET Machine = LTRIM(RTRIM(Machine)) WHERE Machine LIKE ' %' OR Machine LIKE '% '");
            migrationBuilder.Sql(@"UPDATE DbLinkUsages SET Machine = LTRIM(RTRIM(Machine)) WHERE Machine LIKE ' %' OR Machine LIKE '% '");

            // Oracle のツールのコマンドラインはパスワードを含むことがあるため、以前に保存したものは消す（新しく取得したものは隠して保存する）
            migrationBuilder.Sql(@"UPDATE Sessions SET ProcCommandLine = NULL WHERE LOWER(ProcName) IN ('sqlplus.exe', 'sqlldr.exe', 'exp.exe', 'imp.exe', 'expdp.exe', 'impdp.exe', 'rman.exe', 'dgmgrl.exe', 'tkprof.exe')");
            migrationBuilder.Sql(@"UPDATE DbLinkUsages SET ProcCommandLine = NULL WHERE LOWER(ProcName) IN ('sqlplus.exe', 'sqlldr.exe', 'exp.exe', 'imp.exe', 'expdp.exe', 'impdp.exe', 'rman.exe', 'dgmgrl.exe', 'tkprof.exe')");

            // 接続中の sqlplus.exe は実行元を取得し直す
            migrationBuilder.Sql(@"UPDATE Sessions SET ProcInfoState = 'Pending' WHERE IsConnected = 1 AND ProcessKind = 'Client' AND ProcInfoState = 'Resolved' AND LOWER(ProcName) = 'sqlplus.exe'");

            // DB Link の可能性があるセッションに、特定済みの接続元の端末・アプリを入れる
            migrationBuilder.Sql(@"UPDATE Sessions SET LinkOriginMachine = (SELECT TOP 1 u.Machine FROM DbLinkUsages u WHERE u.MatchedSessionId = Sessions.Id ORDER BY u.LastSeenAt DESC, u.Id DESC), LinkOriginApp = (SELECT TOP 1 u.AppName FROM DbLinkUsages u WHERE u.MatchedSessionId = Sessions.Id ORDER BY u.LastSeenAt DESC, u.Id DESC) WHERE EXISTS (SELECT 1 FROM DbLinkUsages u WHERE u.MatchedSessionId = Sessions.Id)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sessions_LinkOriginMachine_LinkOriginApp",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "LinkOriginApp",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "LinkOriginMachine",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "ProcAncestry",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "ProcAncestry",
                table: "DbLinkUsages");

            migrationBuilder.AddColumn<string>(
                name: "TargetPrograms",
                table: "ProcessInspectionSettings",
                type: "nvarchar(max)",
                nullable: true);
        }
    }
}
