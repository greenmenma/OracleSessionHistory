using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OracleSessionHistory.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkOriginUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LinkOriginUser",
                table: "Sessions",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            // 接続元を特定済みのセッションに、接続元の実行ユーザを入れる（LinkOriginSync と同じく最後に観測した利用元）
            migrationBuilder.Sql(@"UPDATE Sessions SET LinkOriginUser = (SELECT TOP 1 COALESCE(u.ProcOwner, u.OsUser) FROM DbLinkUsages u WHERE u.MatchedSessionId = Sessions.Id ORDER BY u.LastSeenAt DESC, u.Id DESC) WHERE LinkOriginApp IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LinkOriginUser",
                table: "Sessions");
        }
    }
}
