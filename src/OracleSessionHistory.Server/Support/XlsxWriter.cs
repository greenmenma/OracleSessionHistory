using System.IO.Compression;
using System.Security;
using System.Text;

namespace OracleSessionHistory.Server.Support;

/// <summary>Excel のシート（見出しの行・データの行・列幅）</summary>
/// <param name="Name">シート名（Excel の制限に合わせて 31 文字以内・使えない文字は置き換える）</param>
/// <param name="Header">見出しの行</param>
/// <param name="Rows">データの行（string・数値・DateTime・<see cref="XlsxIndented"/>・null）</param>
/// <param name="Widths">列幅（文字数。足りない列は既定の幅）</param>
public sealed record XlsxSheet(string Name, IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<object?>> Rows, IReadOnlyList<double>? Widths = null);

/// <summary>字下げして表示する文字列（木の段を表す）</summary>
public sealed record XlsxIndented(string Text, int Level);

/// <summary>
/// 最小限の Excel（.xlsx）を作る。外部ライブラリを使わず、Office Open XML を ZIP にまとめる。
/// 見出しの行は太字・固定表示・オートフィルター付き
/// </summary>
public static class XlsxWriter
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>字下げのスタイルの最大段（それより深い段はこの段で表示する）</summary>
    private const int MaxIndent = 8;

    // スタイル番号（styles.xml の cellXfs の順）
    private const int HeaderStyle = 1;
    private const int DateStyle = 2;
    private const int IndentStyleBase = 2; // 字下げ n 段は IndentStyleBase + n

    public static byte[] Write(IReadOnlyList<XlsxSheet> sheets)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var names = UniqueNames(sheets.Select(s => s.Name));
            Entry(zip, "[Content_Types].xml", ContentTypes(sheets.Count));
            Entry(zip, "_rels/.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Entry(zip, "xl/workbook.xml", Workbook(names));
            Entry(zip, "xl/_rels/workbook.xml.rels", WorkbookRels(sheets.Count));
            Entry(zip, "xl/styles.xml", Styles());
            for (var i = 0; i < sheets.Count; i++) Entry(zip, $"xl/worksheets/sheet{i + 1}.xml", Sheet(sheets[i]));
        }
        return ms.ToArray();
    }

    private static void Entry(ZipArchive zip, string path, string content)
    {
        using var w = new StreamWriter(zip.CreateEntry(path, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    /// <summary>シート名：Excel で使えない文字を置き換え、31 文字以内で重複しないようにする</summary>
    internal static List<string> UniqueNames(IEnumerable<string> names)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in names)
        {
            var baseName = new string(raw.Select(c => "[]:*?/\\".Contains(c) ? '_' : c).ToArray()).Trim('\'');
            if (baseName.Length == 0) baseName = "Sheet";
            if (baseName.Length > 31) baseName = baseName[..31];
            var name = baseName;
            for (var n = 2; !used.Add(name); n++)
            {
                var suffix = $"({n})";
                name = (baseName.Length + suffix.Length > 31 ? baseName[..(31 - suffix.Length)] : baseName) + suffix;
            }
            result.Add(name);
        }
        return result;
    }

    private static string ContentTypes(int count)
    {
        var sb = new StringBuilder(
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>""");
        for (var i = 1; i <= count; i++)
            sb.Append($"""<Override PartName="/xl/worksheets/sheet{i}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>""");
        return sb.Append("</Types>").ToString();
    }

    private static string Workbook(IReadOnlyList<string> names)
    {
        var sb = new StringBuilder(
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>""");
        for (var i = 0; i < names.Count; i++) sb.Append($"""<sheet name="{Escape(names[i])}" sheetId="{i + 1}" r:id="rId{i + 1}"/>""");
        return sb.Append("</sheets></workbook>").ToString();
    }

    private static string WorkbookRels(int count)
    {
        var sb = new StringBuilder(
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""");
        for (var i = 1; i <= count; i++)
            sb.Append($"""<Relationship Id="rId{i}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{i}.xml"/>""");
        sb.Append($"""<Relationship Id="rId{count + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>""");
        return sb.Append("</Relationships>").ToString();
    }

    private static string Styles()
    {
        var sb = new StringBuilder(
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""" +
            """<numFmts count="1"><numFmt numFmtId="164" formatCode="yyyy/mm/dd hh:mm:ss"/></numFmts>""" +
            """<fonts count="2"><font><sz val="11"/><name val="Yu Gothic"/><family val="3"/><charset val="128"/></font><font><b/><sz val="11"/><name val="Yu Gothic"/><family val="3"/><charset val="128"/></font></fonts>""" +
            """<fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFDDEBF7"/><bgColor indexed="64"/></patternFill></fill></fills>""" +
            """<borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>""" +
            """<cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>""");
        sb.Append($"""<cellXfs count="{IndentStyleBase + MaxIndent + 1}">""");
        sb.Append("""<xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>""");
        sb.Append("""<xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1" applyAlignment="1"><alignment vertical="center" wrapText="1"/></xf>""");
        sb.Append("""<xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>""");
        for (var i = 1; i <= MaxIndent; i++)
            sb.Append($"""<xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment indent="{i}"/></xf>""");
        sb.Append("</cellXfs>");
        sb.Append("""<cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>""");
        return sb.ToString();
    }

    private static string Sheet(XlsxSheet sheet)
    {
        var columns = Math.Max(sheet.Header.Count, sheet.Rows.Count == 0 ? 0 : sheet.Rows.Max(r => r.Count));
        var sb = new StringBuilder(
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">""");
        // 見出しの行を固定する
        sb.Append("""<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>""");
        if (columns > 0)
        {
            sb.Append("<cols>");
            for (var c = 0; c < columns; c++)
            {
                var width = sheet.Widths is { } w && c < w.Count ? w[c] : 12;
                sb.Append($"""<col min="{c + 1}" max="{c + 1}" width="{width.ToString(System.Globalization.CultureInfo.InvariantCulture)}" customWidth="1"/>""");
            }
            sb.Append("</cols>");
        }
        sb.Append("<sheetData>");
        Row(sb, 1, sheet.Header.Cast<object?>().ToList(), header: true);
        for (var r = 0; r < sheet.Rows.Count; r++) Row(sb, r + 2, sheet.Rows[r], header: false);
        sb.Append("</sheetData>");
        if (columns > 0) sb.Append($"""<autoFilter ref="A1:{Column(columns - 1)}{sheet.Rows.Count + 1}"/>""");
        return sb.Append("</worksheet>").ToString();
    }

    private static void Row(StringBuilder sb, int row, IReadOnlyList<object?> cells, bool header)
    {
        sb.Append($"""<row r="{row}">""");
        for (var c = 0; c < cells.Count; c++)
        {
            var reference = $"{Column(c)}{row}";
            switch (cells[c])
            {
                case null:
                    if (header) sb.Append($"""<c r="{reference}" s="{HeaderStyle}"/>""");
                    break;
                case int or long or double or decimal or float:
                    sb.Append($"""<c r="{reference}"><v>{Convert.ToString(cells[c], System.Globalization.CultureInfo.InvariantCulture)}</v></c>""");
                    break;
                case DateTime d:
                    sb.Append($"""<c r="{reference}" s="{DateStyle}"><v>{d.ToOADate().ToString(System.Globalization.CultureInfo.InvariantCulture)}</v></c>""");
                    break;
                case XlsxIndented t:
                    var level = Math.Clamp(t.Level, 0, MaxIndent);
                    var style = level == 0 ? "" : $""" s="{IndentStyleBase + level}" """.TrimEnd();
                    sb.Append($"""<c r="{reference}"{style} t="inlineStr"><is><t xml:space="preserve">{Escape(t.Text)}</t></is></c>""");
                    break;
                default:
                    var s = header ? $""" s="{HeaderStyle}" """.TrimEnd() : "";
                    sb.Append($"""<c r="{reference}"{s} t="inlineStr"><is><t xml:space="preserve">{Escape(cells[c]!.ToString() ?? "")}</t></is></c>""");
                    break;
            }
        }
        sb.Append("</row>");
    }

    /// <summary>列番号（0 始まり）を A・B・…・AA の列名にする</summary>
    internal static string Column(int index)
    {
        var name = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26) name = (char)('A' + (n - 1) % 26) + name;
        return name;
    }

    /// <summary>XML の特殊文字をエスケープし、XML で使えない制御文字を除く。Excel のセルの上限（32767 文字）で切る</summary>
    private static string Escape(string s)
    {
        var clean = new string(s.Where(c => c is '\t' or '\n' or '\r' || c >= ' ' && c != '￾' && c != '￿').ToArray());
        if (clean.Length > 32767) clean = clean[..32767];
        return SecurityElement.Escape(clean);
    }
}
