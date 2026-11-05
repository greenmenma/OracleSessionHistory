using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OracleSessionHistory.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeAppNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // プロセス情報で確定していない既存の行のアプリ名・補足を、新しい行と同じ規則（AppIdentifier.Identify のプロセス情報なしの場合）にそろえる。
            // 以前の移行は PROGRAM をそのまま入れていたため、w3wp.exe（OSUSER が IIS APPPOOL\名前）が「IIS: 名前」と別の集計キーになっていた
            migrationBuilder.Sql(@"UPDATE Sessions SET AppName = CASE WHEN LOWER(NULLIF(LTRIM(RTRIM(Program)), '')) = 'w3wp.exe' AND UPPER(SUBSTRING(LTRIM(RTRIM(OsUser)), 1, 12)) = 'IIS APPPOOL\' AND LEN(LTRIM(RTRIM(OsUser))) > 12 THEN SUBSTRING('IIS: ' + SUBSTRING(LTRIM(RTRIM(OsUser)), 13, 4000), 1, 256) ELSE SUBSTRING(COALESCE(NULLIF(LTRIM(RTRIM(Program)), ''), N'(不明)'), 1, 256) END, AppDetail = CASE WHEN LOWER(NULLIF(LTRIM(RTRIM(Program)), '')) = 'w3wp.exe' AND UPPER(SUBSTRING(LTRIM(RTRIM(OsUser)), 1, 12)) = 'IIS APPPOOL\' AND LEN(LTRIM(RTRIM(OsUser))) > 12 THEN NULL WHEN NULLIF(LTRIM(RTRIM(Module)), '') IS NOT NULL AND (NULLIF(LTRIM(RTRIM(Program)), '') IS NULL OR LOWER(NULLIF(LTRIM(RTRIM(Module)), '')) <> LOWER(NULLIF(LTRIM(RTRIM(Program)), ''))) THEN NULLIF(LTRIM(RTRIM(Module)), '') ELSE NULL END WHERE ProcInfoState <> 'Resolved'");
            migrationBuilder.Sql(@"UPDATE DbLinkUsages SET AppName = CASE WHEN LOWER(NULLIF(LTRIM(RTRIM(Program)), '')) = 'w3wp.exe' AND UPPER(SUBSTRING(LTRIM(RTRIM(OsUser)), 1, 12)) = 'IIS APPPOOL\' AND LEN(LTRIM(RTRIM(OsUser))) > 12 THEN SUBSTRING('IIS: ' + SUBSTRING(LTRIM(RTRIM(OsUser)), 13, 4000), 1, 256) ELSE SUBSTRING(COALESCE(NULLIF(LTRIM(RTRIM(Program)), ''), N'(不明)'), 1, 256) END, AppDetail = CASE WHEN LOWER(NULLIF(LTRIM(RTRIM(Program)), '')) = 'w3wp.exe' AND UPPER(SUBSTRING(LTRIM(RTRIM(OsUser)), 1, 12)) = 'IIS APPPOOL\' AND LEN(LTRIM(RTRIM(OsUser))) > 12 THEN NULL WHEN NULLIF(LTRIM(RTRIM(Module)), '') IS NOT NULL AND (NULLIF(LTRIM(RTRIM(Program)), '') IS NULL OR LOWER(NULLIF(LTRIM(RTRIM(Module)), '')) <> LOWER(NULLIF(LTRIM(RTRIM(Program)), ''))) THEN NULLIF(LTRIM(RTRIM(Module)), '') ELSE NULL END WHERE ProcInfoState <> 'Resolved'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 補正前の値は残していないため戻さない（スキーマの変更は無い）
        }
    }
}
