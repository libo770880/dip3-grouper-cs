using System.IO.Compression;
using System.Text;
using Dip3Grouper;
using Xunit;

namespace Dip3Grouper.Tests;

/// <summary>
/// 灰码（xlsx 单元格背景色）解析测试 —— 对齐 Node 版 src/xlsx-gray.js。
///
/// 关键回归点: workbook.xml.rels 里 Target / Id 的属性顺序不可假定。
/// Excel 写的是 Id→Type→Target，而 SheetJS 等第三方库写的是 Target→Id；
/// 早期实现用 `Id="x"[^>]*Target="..."` 这一"前向"正则，遇到后者必然失配，
/// 使灰码背景色识别静默失效（调用方 try/catch 退化为「医保编码为空」判定）。
/// 修复后改为逐标签取属性，对属性顺序不敏感。
/// </summary>
public class XlsxGrayTests
{
    // -----------------------------------------------------------------
    // 构造最小可用 xlsx（解析器只需这几个 entry）
    // -----------------------------------------------------------------
    private static byte[] MakeXlsx(string workbookXml, string relsXml, string stylesXml, string sheetXml)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Add(string name, string content)
            {
                var entry = zip.CreateEntry(name);
                using var s = entry.Open();
                var bytes = Encoding.UTF8.GetBytes(content);
                s.Write(bytes, 0, bytes.Length);
            }

            Add("xl/workbook.xml", workbookXml);
            Add("xl/_rels/workbook.xml.rels", relsXml);
            Add("xl/styles.xml", stylesXml);
            Add("xl/worksheets/sheet1.xml", sheetXml);
        }
        return ms.ToArray();
    }

    /// <summary>fills[1] 为灰色实心填充；cellXfs[1] 指向它。</summary>
    private const string StylesXml =
        "<styleSheet><fills count=\"2\">" +
        "<fill><patternFill patternType=\"none\"/></fill>" +
        "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFD9D9D9\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
        "</fills><cellXfs count=\"2\">" +
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"1\" borderId=\"0\" xfId=\"0\" applyFill=\"1\"/>" +
        "</cellXfs></styleSheet>";

    private const string SheetXml =
        "<worksheet><sheetData>" +
        "<row r=\"1\"><c r=\"A1\" s=\"0\" t=\"s\"><v>0</v></c></row>" +
        "<row r=\"2\"><c r=\"A2\" s=\"1\" t=\"s\"><v>1</v></c><c r=\"B2\" s=\"1\" t=\"s\"><v>2</v></c></row>" +
        "<row r=\"3\"><c r=\"C3\" s=\"1\" t=\"s\"><v>3</v></c></row>" +
        "<row r=\"4\"><c r=\"D4\" t=\"s\"><v>4</v></c></row>" +
        "</sheetData></worksheet>";

    private const string WorkbookXmlSheetJsStyle =
        "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheets>" +
        "<sheet xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
        "name=\"映射\" sheetId=\"1\" state=\"visible\" r:id=\"rId1\"/>" +
        "</sheets></workbook>";

    /// <summary>SheetJS 风格: Target 在 Id 之前（修复前会失配）。</summary>
    private const string RelsTargetFirst =
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" " +
        "Target=\"/xl/worksheets/sheet1.xml\" Id=\"rId1\"/>" +
        "<Relationship Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" " +
        "Target=\"styles.xml\" Id=\"rId2\"/></Relationships>";

    /// <summary>Excel 风格: Id 在 Target 之前。</summary>
    private const string RelsIdFirst =
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" " +
        "Target=\"worksheets/sheet1.xml\"/>" +
        "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" " +
        "Target=\"styles.xml\"/></Relationships>";

    // -----------------------------------------------------------------
    // 灰码行识别
    // -----------------------------------------------------------------

    [Theory]
    // rels Target 在 Id 之前（SheetJS 风格）+ 列过滤
    [InlineData("A", RelsTargetFirst, new[] { 2 })]
    [InlineData("B", RelsTargetFirst, new[] { 2 })]
    [InlineData("C", RelsTargetFirst, new[] { 3 })]
    [InlineData("D", RelsTargetFirst, new int[0])]
    // rels Id 在 Target 之前（Excel 风格）—— 结果必须完全一致
    [InlineData("A", RelsIdFirst, new[] { 2 })]
    [InlineData("C", RelsIdFirst, new[] { 3 })]
    public void 灰码行_与rels属性顺序无关(string col, string relsXml, int[] expect)
    {
        var buf = MakeXlsx(WorkbookXmlSheetJsStyle, relsXml, StylesXml, SheetXml);
        var rows = XlsxGray.ParseGrayRows(buf, new[] { col }, "映射");
        Assert.Equal(expect, rows.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void 灰码行_多列合并去重()
    {
        var buf = MakeXlsx(WorkbookXmlSheetJsStyle, RelsTargetFirst, StylesXml, SheetXml);
        var rows = XlsxGray.ParseGrayRows(buf, new[] { "A", "B", "C" }, "映射");
        Assert.Equal(new[] { 2, 3 }, rows.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void 灰码行_无样式单元格不计入()
    {
        // A1 用 fillId=0（非灰），D4 完全没有 s 属性 → 都不应出现
        var buf = MakeXlsx(WorkbookXmlSheetJsStyle, RelsTargetFirst, StylesXml, SheetXml);
        var rows = XlsxGray.ParseGrayRows(buf, new[] { "A", "D" }, "映射");
        Assert.Equal(new[] { 2 }, rows.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void 灰码行_样式块外的xf不参与索引()
    {
        // cellStyleXfs 里也有 <xf fillId="1">，若被计入会导致索引整体偏移。
        var styles =
            "<styleSheet><fills count=\"2\">" +
            "<fill><patternFill patternType=\"none\"/></fill>" +
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFD9D9D9\"/></patternFill></fill>" +
            "</fills><cellStyleXfs count=\"1\">" +
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"1\" borderId=\"0\"/>" +
            "</cellStyleXfs><cellXfs count=\"2\">" +
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"1\" borderId=\"0\" xfId=\"0\"/>" +
            "</cellXfs></styleSheet>";
        var buf = MakeXlsx(WorkbookXmlSheetJsStyle, RelsTargetFirst, styles, SheetXml);
        var rows = XlsxGray.ParseGrayRows(buf, new[] { "A" }, "映射");
        // A1 是 s=0 → 非灰；A2 是 s=1 → 灰。若 cellStyleXfs 被误计入，A1 会被判成灰。
        Assert.Equal(new[] { 2 }, rows.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void 灰码行_工作表名不匹配时回落到首个工作表()
    {
        var buf = MakeXlsx(WorkbookXmlSheetJsStyle, RelsTargetFirst, StylesXml, SheetXml);
        var rows = XlsxGray.ParseGrayRows(buf, new[] { "A" }, "不存在的表");
        Assert.Equal(new[] { 2 }, rows.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void 灰码行_不传工作表名时取首个工作表()
    {
        var buf = MakeXlsx(WorkbookXmlSheetJsStyle, RelsTargetFirst, StylesXml, SheetXml);
        var rows = XlsxGray.ParseGrayRows(buf, new[] { "A" });
        Assert.Equal(new[] { 2 }, rows.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void 灰码行_无灰色填充时返回空集合而非报错()
    {
        // 有 styles.xml 但没有任何灰色 fill —— 修复前场景: 直接抛「未找到工作表目标」
        var styles =
            "<styleSheet><fills count=\"1\"><fill><patternFill patternType=\"none\"/></fill></fills>" +
            "<cellXfs count=\"1\"><xf numFmtId=\"0\" fillId=\"0\" xfId=\"0\"/></cellXfs></styleSheet>";
        var buf = MakeXlsx(WorkbookXmlSheetJsStyle, RelsTargetFirst, styles, SheetXml);
        var rows = XlsxGray.ParseGrayRows(buf, new[] { "A", "B", "C" }, "映射");
        Assert.NotNull(rows);
        Assert.Empty(rows);
    }

    [Fact]
    public void 灰码行_缺styles时抛错()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true)) { }
        Assert.ThrowsAny<Exception>(() => XlsxGray.ParseGrayRows(ms.ToArray(), new[] { "A" }));
    }

    // -----------------------------------------------------------------
    // 真实 fixture（两端基准一致）
    // -----------------------------------------------------------------

    public static IEnumerable<object[]> FixtureCases()
    {
        var path = Path.Combine(AppPaths.Root, "tools", "fixtures", "测试映射表.xlsx");
        if (!File.Exists(path)) yield break;
        yield return new object[] { path };
    }

    [Theory]
    [MemberData(nameof(FixtureCases))]
    public void 测试映射表_无灰底单元格_返回空集合(string path)
    {
        var rows = XlsxGray.ParseGrayRows(File.ReadAllBytes(path), new[] { "A", "B", "C", "D", "E" }, "映射");
        Assert.Empty(rows);
    }

    // -----------------------------------------------------------------
    // 列号 ↔ 列字母
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(0, "A")]
    [InlineData(1, "B")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(27, "AB")]
    [InlineData(51, "AZ")]
    [InlineData(52, "BA")]
    [InlineData(701, "ZZ")]
    [InlineData(702, "AAA")]
    public void 列号转列字母(int idx, string expect)
        => Assert.Equal(expect, XlsxGray.ColLetter(idx));

    // -----------------------------------------------------------------
    // 属性解析（AttrOf）—— 精确名匹配，不被同前缀/同后缀属性误伤
    // -----------------------------------------------------------------

    [Fact]
    public void 属性解析_带命名空间前缀()
    {
        const string tag =
            "<sheet xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
            "name=\"映射\" sheetId=\"1\" state=\"visible\" r:id=\"rId1\"/>";
        Assert.Equal("rId1", XlsxGray.AttrOf(tag, "id"));
        Assert.Equal("映射", XlsxGray.AttrOf(tag, "name"));
    }

    [Fact]
    public void 属性解析_sheetId不被误认为id()
    {
        const string tag = "<sheet name=\"映射\" sheetId=\"1\"/>";
        Assert.Null(XlsxGray.AttrOf(tag, "id"));
        Assert.Equal("映射", XlsxGray.AttrOf(tag, "name"));
    }

    [Fact]
    public void 属性解析_xmlns不被误认为r_id()
    {
        const string tag =
            "<sheet xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" name=\"映射\"/>";
        Assert.Null(XlsxGray.AttrOf(tag, "id"));
    }

    [Fact]
    public void 属性解析_关系标签任意顺序()
    {
        const string a = "<Relationship Type=\"T\" Target=\"/xl/worksheets/sheet1.xml\" Id=\"rId1\"/>";
        const string b = "<Relationship Id=\"rId1\" Type=\"T\" Target=\"worksheets/sheet1.xml\"/>";
        Assert.Equal("/xl/worksheets/sheet1.xml", XlsxGray.AttrOf(a, "Target"));
        Assert.Equal("rId1", XlsxGray.AttrOf(a, "Id"));
        Assert.Equal("worksheets/sheet1.xml", XlsxGray.AttrOf(b, "Target"));
        Assert.Equal("rId1", XlsxGray.AttrOf(b, "Id"));
    }

    [Fact]
    public void 属性解析_缺失属性返回null()
        => Assert.Null(XlsxGray.AttrOf("<sheet name=\"x\"/>", "Target"));
}
