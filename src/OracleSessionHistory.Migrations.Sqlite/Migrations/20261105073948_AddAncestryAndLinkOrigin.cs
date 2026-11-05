using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OracleSessionHistory.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddAncestryAndLinkOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {

            migrationBuilder.AddColumn<string>(
                name: "LinkOriginApp",
                table: "Sessions",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LinkOriginMachine",
                table: "Sessions",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProcAncestry",
                table: "Sessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProcAncestry",
                table: "DbLinkUsages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_LinkOriginMachine_LinkOriginApp",
                table: "Sessions",
                columns: new[] { "LinkOriginMachine", "LinkOriginApp" });

            // 登録した実行ファイルだけを調べる仕様をやめたため、それで対象外にしていた接続中のクライアントを調べ直す
            migrationBuilder.Sql(@"UPDATE Sessions SET ProcInfoState = 'Pending', ProcInfoError = NULL WHERE IsConnected = 1 AND ProcessKind = 'Client' AND ProcInfoState = 'NotApplicable' AND ProcInfoError LIKE '%実行ファイルに登録されていません%'");

            // 端末名の前後の空白を除く（新しい行は SessionTracker・CollectionService が除いて保存する）
            migrationBuilder.Sql(@"UPDATE Sessions SET Machine = TRIM(Machine) WHERE Machine LIKE ' %' OR Machine LIKE '% '");
            migrationBuilder.Sql(@"UPDATE DbLinkUsages SET Machine = TRIM(Machine) WHERE Machine LIKE ' %' OR Machine LIKE '% '");

            // Oracle のツールのコマンドラインはパスワードを含むことがあるため、以前に保存したものは消す（新しく取得したものは隠して保存する）
            migrationBuilder.Sql(@"UPDATE Sessions SET ProcCommandLine = NULL WHERE LOWER(ProcName) IN ('sqlplus.exe', 'sqlldr.exe', 'exp.exe', 'imp.exe', 'expdp.exe', 'impdp.exe', 'rman.exe', 'dgmgrl.exe', 'tkprof.exe')");
            migrationBuilder.Sql(@"UPDATE DbLinkUsages SET ProcCommandLine = NULL WHERE LOWER(ProcName) IN ('sqlplus.exe', 'sqlldr.exe', 'exp.exe', 'imp.exe', 'expdp.exe', 'impdp.exe', 'rman.exe', 'dgmgrl.exe', 'tkprof.exe')");

            // 接続中の sqlplus.exe は実行元を取得し直す
            migrationBuilder.Sql(@"UPDATE Sessions SET ProcInfoState = 'Pending' WHERE IsConnected = 1 AND ProcessKind = 'Client' AND ProcInfoState = 'Resolved' AND LOWER(ProcName) = 'sqlplus.exe'");

            // DB Link の可能性があるセッションに、特定済みの接続元の端末・アプリを入れる
            migrationBuilder.Sql(@"UPDATE Sessions SET LinkOriginMachine = (SELECT u.Machine FROM DbLinkUsages u WHERE u.MatchedSessionId = Sessions.Id ORDER BY u.LastSeenAt DESC, u.Id DESC LIMIT 1), LinkOriginApp = (SELECT u.AppName FROM DbLinkUsages u WHERE u.MatchedSessionId = Sessions.Id ORDER BY u.LastSeenAt DESC, u.Id DESC LIMIT 1) WHERE EXISTS (SELECT 1 FROM DbLinkUsages u WHERE u.MatchedSessionId = Sessions.Id)");

            // SQLite の DropColumn はテーブルを作り直す（トランザクション外の PRAGMA が必要になり警告が出る）ため、DROP COLUMN（SQLite 3.35 以降）で削除する
            migrationBuilder.Sql("ALTER TABLE ProcessInspectionSettings DROP COLUMN TargetPrograms");
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
                type: "TEXT",
                nullable: true);
        }
    }
}
