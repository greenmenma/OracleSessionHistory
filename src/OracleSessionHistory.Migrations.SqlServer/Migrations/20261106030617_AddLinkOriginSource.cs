using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OracleSessionHistory.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkOriginSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LinkOriginSource",
                table: "Sessions",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            // 接続元を特定済みのセッションに、DB Link 接続元の名前を入れる（LinkOriginSync と同じく最後に観測した利用元）
            migrationBuilder.Sql(@"UPDATE Sessions SET LinkOriginSource = (SELECT TOP 1 ls.Name FROM DbLinkUsages u JOIN LinkSources ls ON ls.Id = u.LinkSourceId WHERE u.MatchedSessionId = Sessions.Id ORDER BY u.LastSeenAt DESC, u.Id DESC) WHERE LinkOriginApp IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LinkOriginSource",
                table: "Sessions");
        }
    }
}
