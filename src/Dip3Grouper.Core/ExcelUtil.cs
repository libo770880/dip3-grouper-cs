using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace Dip3Grouper;

/// <summary>工作簿读写工具（NPOI 承载，替代 Node 端 SheetJS）。</summary>
public static class ExcelUtil
{
    public static IWorkbook Open(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return WorkbookFactory.Create(ms);
    }

    public static string CellText(ICell cell)
    {
        if (cell == null) return "";
        try
        {
            return new DataFormatter().FormatCellValue(cell) ?? "";
        }
        catch
        {
            return cell.ToString() ?? "";
        }
    }

    /// <summary>等价 SheetJS sheet_to_json(ws, {header:1, defval:'', blankrows:true}) —— 保留空行。</summary>
    public static List<List<string>> ReadAoa(ISheet sheet)
    {
        var outRows = new List<List<string>>();
        if (sheet == null) return outRows;
        var first = sheet.FirstRowNum;
        var last = sheet.LastRowNum;
        for (var r = first; r <= last; r++)
        {
            var row = sheet.GetRow(r);
            var list = new List<string>();
            if (row != null)
            {
                var lastCell = row.LastCellNum; // 已是"最后一个单元格索引 + 1"
                for (var c = 0; c < lastCell; c++) list.Add(CellText(row.GetCell(c)));
            }
            outRows.Add(list);
        }
        return outRows;
    }

    /// <summary>等价 SheetJS sheet_to_json(ws, {defval:''}) —— 首行表头, 跳过全空行。</summary>
    public static List<Dictionary<string, string>> ReadObjects(ISheet sheet)
    {
        var outRows = new List<Dictionary<string, string>>();
        if (sheet == null) return outRows;
        var first = sheet.FirstRowNum;
        var last = sheet.LastRowNum;
        if (first > last) return outRows;

        var headerRow = sheet.GetRow(first);
        if (headerRow == null) return outRows;
        var headers = new List<string>();
        for (var c = 0; c < headerRow.LastCellNum; c++) headers.Add(CellText(headerRow.GetCell(c)));

        for (var r = first + 1; r <= last; r++)
        {
            var row = sheet.GetRow(r);
            if (row == null) continue;
            var dict = new Dictionary<string, string>();
            var any = false;
            for (var c = 0; c < headers.Count; c++)
            {
                var key = headers[c] ?? "";
                if (key.Length == 0) continue;
                var v = CellText(row.GetCell(c));
                if (v.Length > 0) any = true;
                dict[key] = v;
            }
            if (!any) continue;
            outRows.Add(dict);
        }
        return outRows;
    }

    public sealed class SheetSpec
    {
        public string Name { get; set; }
        /// <summary>首行为表头, 其余为数据行</summary>
        public List<List<object>> Aoa { get; set; } = new();
        /// <summary>列宽（Excel 字符数）；为空则不设置</summary>
        public int[] ColWidths { get; set; }
    }

    public static byte[] BuildWorkbook(IEnumerable<SheetSpec> sheets)
    {
        var wb = new XSSFWorkbook();
        // Excel 要求 sheet 名唯一（且不区分大小写）：区段名截到 31 字后可能撞名，
        // 撞了 NPOI 会直接抛异常 —— 整个文件导不出来。这里统一去重，而不是让各导出各自规避。
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in sheets)
        {
            var name = SanitizeSheetName(spec.Name, used);
            used.Add(name);
            var sh = wb.CreateSheet(name);
            for (var r = 0; r < spec.Aoa.Count; r++)
            {
                var row = sh.CreateRow(r);
                var cells = spec.Aoa[r] ?? new List<object>();
                for (var c = 0; c < cells.Count; c++)
                {
                    var cell = row.CreateCell(c);
                    var v = cells[c];
                    switch (v)
                    {
                        case null: cell.SetCellValue(""); break;
                        case double d: cell.SetCellValue(d); break;
                        case int i: cell.SetCellValue(i); break;
                        case bool b: cell.SetCellValue(b); break;
                        default: cell.SetCellValue(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? ""); break;
                    }
                }
            }
            if (spec.ColWidths != null)
            {
                for (var c = 0; c < spec.ColWidths.Length; c++)
                {
                    var w = Math.Max(10, spec.ColWidths[c]);
                    sh.SetColumnWidth(c, Math.Min(w * 256, 255 * 256));
                }
            }
        }
        using var ms = new MemoryStream();
        wb.Write(ms, true);
        return ms.ToArray();
    }

    /// <summary>把任意文本规整成合法的 sheet 名（供调用方预判表名，导出时还会再做一次去重）。</summary>
    public static string SheetName(string raw) => SanitizeSheetName(raw);

    /// <summary>
    /// Excel sheet 名合法化：非法字符替换、空名兜底、超 31 字截断，
    /// 与已用名重复时补「(2)」「(3)」并保证仍不超 31 字。
    /// </summary>
    private static string SanitizeSheetName(string name, ISet<string> used = null)
    {
        var s = string.IsNullOrWhiteSpace(name) ? "Sheet" : name.Trim();
        foreach (var ch in new[] { ':', '\\', '/', '?', '*', '[', ']', '\'', '：' }) s = s.Replace(ch, '_');
        if (s.Length > 31) s = s[..31];
        if (s.Length == 0) s = "Sheet";
        if (used == null || !used.Contains(s)) return s;

        for (var n = 2; n < 1000; n++)
        {
            var suffix = "(" + n + ")";
            var keep = Math.Min(s.Length, 31 - suffix.Length);
            var cand = s[..keep] + suffix;
            if (!used.Contains(cand)) return cand;
        }
        return s[..Math.Min(s.Length, 26)] + Guid.NewGuid().ToString("N")[..4];
    }

    /// <summary>列宽像素 → Excel 字符宽度（对齐前端 wch ≈ width/7）。</summary>
    public static int[] WidthsFromPixels(IEnumerable<int> px)
        => px.Select(w => Math.Max(10, (int)Math.Ceiling(w / 7.0))).ToArray();
}
