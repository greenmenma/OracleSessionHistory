using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OracleSessionHistory.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class SkipProcessInfoExceptIisAndSqlPlus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // プロセス情報は w3wp.exe・sqlplus.exe だけ取得するようにしたため、それ以外の行に残っている
            // 取得待ち・取得失敗・終了済み・不一致・対象外の理由を消して「対象外（理由なし）」にする（取得済みの行はそのまま）
            foreach (var table in new[] { "Sessions", "DbLinkUsages" })
            {
                migrationBuilder.Sql($@"UPDATE {table} SET ProcInfoState = 'NotApplicable', ProcInfoError = NULL, ProcInfoAt = NULL WHERE ProcInfoState <> 'Resolved' AND LOWER(TRIM(COALESCE(Program, ''))) NOT IN ('w3wp.exe', 'sqlplus.exe') AND (ProcInfoState <> 'NotApplicable' OR ProcInfoError IS NOT NULL)");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 消した状態・理由は残していないため戻さない（スキーマの変更は無い）
        }
    }
}
