using System.IO.Compression;
using System.Xml.Linq;
using OracleSessionHistory.Server.Api;
using OracleSessionHistory.Server.Support;

namespace OracleSessionHistory.Tests;

/// <summary>Excel 出力（.xlsx の生成と接続アプリ表のシート）</summary>
public class XlsxWriterTests
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    /// <summary>シートの各行のセルの値（文字列は inlineStr の値、数値・日付は v の値）</summary>
    private static List<List<string>> ReadSheet(byte[] xlsx, int sheet)
    {
        using var zip = new ZipArchive(new MemoryStream(xlsx));
        using var stream = zip.GetEntry($"xl/worksheets/sheet{sheet}.xml")!.Open();
        // 空のセルは書かないため、セルの参照（A1 等）の列の位置に並べる
        return XDocument.Load(stream).Descendants(Main + "row").Select(r =>
        {
            var cells = r.Elements(Main + "c").ToDictionary(
                c => ColumnIndex((string)c.Attribute("r")!),
                c => (string?)c.Element(Main + "is")?.Element(Main + "t") ?? (string?)c.Element(Main + "v") ?? "");
            return Enumerable.Range(0, cells.Count == 0 ? 0 : cells.Keys.Max() + 1).Select(i => cells.GetValueOrDefault(i, "")).ToList();
        }).ToList();
    }

    private static int ColumnIndex(string reference)
    {
        var n = 0;
        foreach (var ch in reference.TakeWhile(char.IsLetter)) n = n * 26 + (ch - 'A' + 1);
        return n - 1;
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(701, "ZZ")]
    [InlineData(702, "AAA")]
    public void 列名(int index, string name) => Assert.Equal(name, XlsxWriter.Column(index));

    [Fact]
    public void シート名は使えない文字を置き換え31文字以内で重複させない()
    {
        Assert.Equal(["a_b_c", "Sheet", new string('x', 31), new string('x', 28) + "(2)", "A", "a(2)"],
            XlsxWriter.UniqueNames(["a/b:c", "", new string('x', 40), new string('x', 31), "A", "a"]));
    }

    [Fact]
    public void 文字列_数値_日付_字下げのセルを書き制御文字を除く()
    {
        var bytes = XlsxWriter.Write(
        [
            new("一覧", ["名前", "件数", "日時"], [["<販売&管理>\u0001", 12, new DateTime(2026, 11, 16, 9, 30, 0)], [new XlsxIndented("下の段", 2), null, 1.5]]),
        ]);
        using (var zip = new ZipArchive(new MemoryStream(bytes)))
        {
            Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
            Assert.NotNull(zip.GetEntry("xl/workbook.xml"));
            Assert.NotNull(zip.GetEntry("xl/styles.xml"));
        }
        var rows = ReadSheet(bytes, 1);
        Assert.Equal(["名前", "件数", "日時"], rows[0]);
        Assert.Equal(["<販売&管理>", "12", new DateTime(2026, 11, 16, 9, 30, 0).ToOADate().ToString(System.Globalization.CultureInfo.InvariantCulture)], rows[1]);
        Assert.Equal(["下の段", "", "1.5"], rows[2]);
    }

    [Fact]
    public void 対になっていないサロゲートと制御文字は除きサロゲートペアは残す()
    {
        Assert.Equal("a𠮷b&amp;", XlsxWriter.Escape("a𠮷b\uDFFF\u0007&\uD800"));
        // 上限で切るときもサロゲートペアの途中で切らない
        Assert.Equal(32766, XlsxWriter.Escape(new string('x', 32766) + "𠮷").Length);
        Assert.Equal(32767, XlsxWriter.Escape(new string('x', 40000)).Length);
    }

    [Fact]
    public void オートフィルターの範囲を定義名にも書く()
    {
        var bytes = XlsxWriter.Write([new("O'Brien", ["a", "b"], [["1", "2"], ["3"]])]);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        using var reader = new StreamReader(zip.GetEntry("xl/workbook.xml")!.Open());
        Assert.Contains("""<definedName name="_xlnm._FilterDatabase" localSheetId="0" hidden="1">&apos;O&apos;&apos;Brien&apos;!$A$1:$B$3</definedName>""", reader.ReadToEnd());
    }

    private static ConnectionCountNode Node(string kind, string label, int month1, params ConnectionCountNode[] children) =>
        new(kind, label, null, ["CORP\\yamada"], 1, new DateTime(2026, 11, 16, 9, 0, 0), 0, 0, 0, 0, 0, 0, 0, month1, 0, [], null, children);

    [Fact]
    public void 接続アプリ表をアプリ別_経路_端末別のシートにする()
    {
        var notes = new Dictionary<string, NodeNoteDto>
        {
            [ConnectionTreeKeys.AppKeyOf("販売.exe")] = new("販売管理", null, null, []),
            ["machine:WEB01\nexe:販売.exe"] = new(null, "メモ", new("yamada@corp.local", "山田 太郎"), []),
        };
        var tree = ConnectionTreeKeys.Apply([Node("machine", "WEB01", 3, Node("exe", "販売.exe", 3))], notes);
        var tables = new ReportEndpoints.ConnectionTables(
            ReportEndpoints.Buckets.Create(new DateTime(2026, 11, 16)), tree, ReportEndpoints.ToAppView(tree, notes), []);
        var bytes = ConnectionExport.Create(tables, new Dictionary<string, string> { ["CORP\\YAMADA"] = "山田 太郎" });

        var apps = ReadSheet(bytes, 1);
        Assert.Equal("本日 11/16", apps[0][8]);
        Assert.Equal(["販売.exe", "販売管理", "", "1", "山田 太郎（yamada@corp.local）", "山田 太郎（CORP\\yamada）"], apps[1].Take(6));
        var paths = ReadSheet(bytes, 2);
        Assert.Equal(["販売.exe", "販売管理", "WEB01 → 販売.exe", "", "メモ"], paths[1].Take(5));
        var machines = ReadSheet(bytes, 3);
        Assert.Equal(["1", "WEB01", "端末"], machines[1].Take(3));
        Assert.Equal(["2", "販売.exe", "実行ファイル", "販売管理"], machines[2].Take(4));
        Assert.Equal("WEB01 → 販売.exe", machines[2][^1]); // 経路（端末の件数の出典は端末の行だけ）
        Assert.Equal("履歴の件数", machines[1][^1]);
    }

    [Theory]
    [InlineData(0, "1分未満")]
    [InlineData(59, "59分")]
    [InlineData(135, "2時間15分")]
    [InlineData(60 * 26, "1日2時間")]
    public void 接続時間は画面と同じ表記(int minutes, string expected)
    {
        var from = new DateTime(2026, 11, 16, 9, 0, 0);
        Assert.Equal(expected, SessionExport.Duration(from, from.AddMinutes(minutes)));
    }

    [Fact]
    public void セッション履歴を一覧と検索条件のシートにする()
    {
        var logon = new DateTime(2026, 11, 16, 9, 0, 0);
        var session = new SessionListItem(10, 1, "本番DB", 1, 123, 45, logon, "SALES", "SALES", "yamada", "PC01", "販売.exe", "MOD",
            "INACTIVE", "DbLinkSuspect", null, false, logon, logon.AddMinutes(30), logon.AddMinutes(30), true, 0,
            "販売.exe", @"D:\app", "CORP\\yamada", "Resolved", null, LinkOriginApp: "在庫照会.exe", LinkOriginMachine: "AP02");
        var result = new OracleSessionHistory.Server.Support.PagedResult<SessionRow>(
            [new SessionRow("session", "本番DB", logon, session, null)], 60_000, 1, SessionExport.MaxRows);
        var search = new SessionSearch(null, "disconnected", "販売", null, null, null, null, null, null, null, Exact: true);
        var bytes = SessionExport.Create(result, search, null, new Dictionary<string, string> { ["CORP\\YAMADA"] = "山田 太郎" }, logon.AddHours(1));

        var rows = ReadSheet(bytes, 1);
        Assert.Equal(["行の種類", "状態", "監視対象"], rows[0].Take(3));
        Assert.Equal(["セッション", "切断", "本番DB"], rows[1].Take(3));
        Assert.Equal(["切断（推定）", logon.AddMinutes(30).ToOADate().ToString(System.Globalization.CultureInfo.InvariantCulture), "30分", "PC01", "販売.exe"],
            rows[1].Skip(4).Take(5));
        Assert.Equal(["在庫照会.exe", "AP02", "", "山田 太郎（CORP\\yamada）"], rows[1].Skip(10).Take(4));
        Assert.Equal("DB Linkの可能性", rows[1][18]);

        var conditions = ReadSheet(bytes, 2).ToDictionary(r => r[0], r => r.ElementAtOrDefault(1) ?? "");
        Assert.Equal("すべて", conditions["監視対象"]);
        Assert.Equal("切断済み", conditions["状態"]);
        Assert.Equal("販売", conditions["キーワード"]);
        Assert.Equal("完全一致", conditions["端末・アプリ"]);
        Assert.Equal("60000", conditions["該当件数"]);
        Assert.Contains("50,000", conditions["注意"]);
    }
}
