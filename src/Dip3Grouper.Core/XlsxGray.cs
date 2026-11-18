using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Dip3Grouper;

/// <summary>
/// xlsx 单元格背景色解析（用于"灰码诊断"识别）—— 对齐 src/xlsx-gray.js，直接扫描 xlsx 压缩包内 XML。
///
/// 灰码判定规则（任一满足即为灰码填充）:
///   - solid 填充且 fgColor.rgb 非纯白(FFFFFF)
///   - solid 填充且 fgColor.theme ≠ 0
///   - solid 填充且 fgColor.tint ≤ -0.01
/// </summary>
public static class XlsxGray
{
    private static readonly Regex FillRe = new(@"<fill>[\s\S]*?</fill>", RegexOptions.Compiled);
    private static readonly Regex CellXfsRe = new(@"<cellXfs\b[\s\S]*?</cellXfs>", RegexOptions.Compiled);
    private static readonly Regex XfRe = new(@"<xf\b[^>]*?fillId=""(\d+)""[^>]*?/?>", RegexOptions.Compiled);
    private static readonly Regex SheetTagRe = new(@"<sheet\b[^>]*?/?>", RegexOptions.Compiled);
    private static readonly Regex RelTagRe = new(@"<Relationship\b[^>]*?/?>", RegexOptions.Compiled);
    private static readonly Regex CellRe = new(@"<c\s+r=""([A-Z]+)(\d+)""((?:\s+[a-zA-Z:]+=""[^""]*"")*)", RegexOptions.Compiled);
    private static readonly Regex StyleRe = new(@"\ss=""(\d+)""", RegexOptions.Compiled);

    /// <summary>
    /// 从单个 XML 标签串中取属性值（属性顺序无关）。
    /// name 为不带前缀的属性名，如 "id"（匹配 r:id / rel:id）、"Id"、"Target"、"name"。
    /// 精确名匹配: 不会把 sheetId 误当成 id，也不会把 xmlns:r 误当成 r:id。
    /// </summary>
    public static string AttrOf(string tag, string name)
    {
        var m = Regex.Match(tag, $@"(?:^|\s)(?:[\w]+:)?{Regex.Escape(name)}=""([^""]*)""");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>0-based 列号 → Excel 列字母</summary>
    public static string ColLetter(int idx)
    {
        var s = "";
        var n = idx + 1;
        while (n > 0)
        {
            var m = (n - 1) % 26;
            s = (char)('A' + m) + s;
            n = (n - 1) / 26;
        }
        return s;
    }

    private static bool IsGrayFill(string fillXml)
    {
        if (!fillXml.Contains("patternType=\"solid\"")) return false;
        var fgMatch = Regex.Match(fillXml, @"<fgColor\s+([^/>]*?)/?>");
        if (!fgMatch.Success) return false;
        var attrs = fgMatch.Groups[1].Value;

        var rgbMatch = Regex.Match(attrs, "rgb=\"([0-9A-Fa-f]{6,8})\"");
        if (rgbMatch.Success)
        {
            var hex = rgbMatch.Groups[1].Value;
            hex = hex[^6..].ToUpperInvariant();
            return hex != "FFFFFF";
        }
        var themeMatch = Regex.Match(attrs, "theme=\"(\\d+)\"");
        var tintMatch = Regex.Match(attrs, "tint=\"(-?\\d*\\.?\\d+)\"");
        var tint = tintMatch.Success
            ? double.Parse(tintMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        if (themeMatch.Success) return int.Parse(themeMatch.Groups[1].Value) != 0 || tint <= -0.01;
        return false;
    }

    private static string ReadEntry(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        if (entry == null) return null;
        using var s = entry.Open();
        using var sr = new StreamReader(s, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    /// <summary>解析 xlsx，返回指定列中带灰色背景的行号集合（1-based sheet 行号）。</summary>
    public static HashSet<int> ParseGrayRows(byte[] buffer, IEnumerable<string> colLetters, string sheetName = null)
    {
        using var ms = new MemoryStream(buffer);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

        // 1. styles.xml → fills 灰色标记 + cellXfs 样式索引 → fillId
        var stylesXml = ReadEntry(zip, "xl/styles.xml");
        if (stylesXml == null) throw new InvalidOperationException("xl/styles.xml 未找到");
        var fills = new List<bool>();
        foreach (Match fm in FillRe.Matches(stylesXml)) fills.Add(IsGrayFill(fm.Value));

        var fillByXf = new List<bool>();
        // 注意: 只取 <cellXfs> 块内的 xf（前面的 <cellStyleXfs> 也有 <xf>，会导致索引偏移）
        var blockMatch = CellXfsRe.Match(stylesXml);
        var cellXfsBlock = blockMatch.Success ? blockMatch.Value : "";
        foreach (Match xm in XfRe.Matches(cellXfsBlock))
        {
            var fid = int.Parse(xm.Groups[1].Value);
            fillByXf.Add(fid >= 0 && fid < fills.Count && fills[fid]);
        }

        // 2. 定位目标工作表 XML 路径
        //    注意: 属性顺序不可假定。Excel 写 workbook.xml 是 name→sheetId→r:id、
        //    写 rels 是 Id→Type→Target；但第三方工具（如 SheetJS）会写成
        //    Target→Id，此时 `Id="x"[^>]*Target=` 这类"前向"正则必然失配，
        //    导致灰码背景色识别静默失效（仅在 try/catch 里退化为纯值判定）。
        //    故这里统一改为"逐标签取属性"，对属性顺序不敏感。
        var wbXml = ReadEntry(zip, "xl/workbook.xml") ?? "";
        var sheetTags = SheetTagRe.Matches(wbXml);
        if (sheetTags.Count == 0) throw new InvalidOperationException("workbook.xml 中未找到工作表");
        var targetTag = sheetTags[0].Value;
        if (!string.IsNullOrEmpty(sheetName))
        {
            foreach (Match sm in sheetTags)
            {
                if (AttrOf(sm.Value, "name") == sheetName) { targetTag = sm.Value; break; }
            }
        }
        // r:id / rel:id 等任意前缀均可（Excel 用 r:）
        var targetRid = AttrOf(targetTag, "id");
        if (string.IsNullOrEmpty(targetRid)) throw new InvalidOperationException("workbook.xml 中未找到工作表关系 id");

        var relsXml = ReadEntry(zip, "xl/_rels/workbook.xml.rels") ?? "";
        var relTags = RelTagRe.Matches(relsXml);
        var sheetPath = (string)null;
        foreach (Match rm in relTags)
        {
            if (AttrOf(rm.Value, "Id") == targetRid) { sheetPath = AttrOf(rm.Value, "Target"); break; }
        }
        if (string.IsNullOrEmpty(sheetPath)) throw new InvalidOperationException("workbook.xml.rels 中未找到工作表目标");
        sheetPath = sheetPath.StartsWith("/") ? sheetPath[1..] : "xl/" + Regex.Replace(sheetPath, "^xl/", "");

        // 3. 扫描工作表单元格: 目标列上带样式且 fill 为灰 → 记录行号
        var sheetXml = ReadEntry(zip, sheetPath);
        if (sheetXml == null) throw new InvalidOperationException($"工作表 {sheetPath} 未找到");
        var letters = new HashSet<string>(colLetters ?? Enumerable.Empty<string>());
        var grayRows = new HashSet<int>();
        foreach (Match cm in CellRe.Matches(sheetXml))
        {
            if (!letters.Contains(cm.Groups[1].Value)) continue;
            var sMatch = StyleRe.Match(cm.Groups[3].Value);
            if (!sMatch.Success) continue;
            var si = int.Parse(sMatch.Groups[1].Value);
            if (si >= 0 && si < fillByXf.Count && fillByXf[si]) grayRows.Add(int.Parse(cm.Groups[2].Value));
        }
        return grayRows;
    }
}
