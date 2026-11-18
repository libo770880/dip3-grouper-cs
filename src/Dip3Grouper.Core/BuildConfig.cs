using NPOI.SS.UserModel;
using System.Text.RegularExpressions;

namespace Dip3Grouper;

/// <summary>
/// DIP 3.0 分组方案 Excel → 配置对象（对齐 tools/build-config.js）
/// 工作表按标题关键字定位，顺序不限。
/// </summary>
public static class BuildConfig
{
    private static readonly Regex AgeLtRe = new(@"^(?:年龄)?<(\d+)天$", RegexOptions.Compiled);
    private static readonly Regex BwRe = new(@"(\d+)-(\d+)", RegexOptions.Compiled);
    private static readonly Regex WsRe = new(@"^\s+$", RegexOptions.Compiled);

    private static string Cell(string v)
    {
        if (v == null) return "";
        var s = v.Trim();
        return WsRe.IsMatch(s) || s.Length == 0 ? "" : s;
    }

    private static List<string> SplitCodes(string v)
    {
        var s = Cell(v);
        if (s.Length == 0) return new List<string>();
        return s.Split('|').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
    }

    private static List<string> SplitPlus(string v)
    {
        var s = Cell(v);
        if (s.Length == 0) return new List<string>();
        return s.Split('+').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
    }

    /// <summary>对齐 JS parseInt(v, 10)：取前导数字，无法解析返回 null。</summary>
    private static int? ToInt(string v)
    {
        var s = (v ?? "").TrimStart();
        var i = 0;
        var sign = 1;
        if (i < s.Length && (s[i] == '+' || s[i] == '-')) { if (s[i] == '-') sign = -1; i++; }
        var start = i;
        while (i < s.Length && char.IsDigit(s[i])) i++;
        if (i == start) return null;
        var digits = s.Substring(start, i - start);
        if (long.TryParse(digits, out var l)) return (int)(sign * l);
        return null;
    }

    private static string Get(List<string> row, int i) => row != null && i < row.Count ? (row[i] ?? "") : "";

    /// <summary>跳过标题行读取数据行，过滤空行与重复表头。</summary>
    private static List<List<string>> RowsOf(ISheet ws)
    {
        var aoa = ExcelUtil.ReadAoa(ws);
        return aoa.Skip(2)
            .Where(r => r != null && r.Count > 0 && Cell(Get(r, 0)) != "" && Cell(Get(r, 0)) != "分类")
            .Select(r => r ?? new List<string>())
            .ToList();
    }

    private static List<List<string>> AoaOf(IWorkbook wb, string keyword, int skip)
    {
        var sheet = FindSheet(wb, keyword);
        var aoa = ExcelUtil.ReadAoa(sheet);
        return aoa.Skip(skip).Select(r => r ?? new List<string>()).ToList();
    }

    private static ISheet FindSheet(IWorkbook wb, string keyword)
    {
        for (var i = 0; i < wb.NumberOfSheets; i++)
        {
            var n = wb.GetSheetName(i);
            if (n != null && n.Contains(keyword, StringComparison.Ordinal)) return wb.GetSheetAt(i);
        }
        throw new InvalidOperationException($"未找到包含「{keyword}」的工作表");
    }

    public static Dip3Config Build(byte[] xlsxBytes, string sourcePath)
    {
        var wb = ExcelUtil.Open(xlsxBytes);
        var config = new Dip3Config
        {
            // ============ 一、先期分组 ============
            Xianqi = RowsOf(FindSheet(wb, "先期分组")).Select(r =>
            {
                var row = new XianqiRow
                {
                    Seq = ToInt(Get(r, 1)) ?? 0,
                    Name = Cell(Get(r, 2)),
                    DiagCodes = SplitCodes(Get(r, 3)),
                    ProcAlts = new List<List<string>>(),
                    AgeDaysMax = null,
                    BirthWeightMin = null,
                    BirthWeightMax = null,
                    Note = Cell(Get(r, 7)),
                };
                var procField = Cell(Get(r, 5));
                var m = AgeLtRe.Match(procField);
                if (m.Success)
                {
                    row.AgeDaysMax = int.Parse(m.Groups[1].Value) - 1; // <29天 → 0..28
                    var bw = Cell(Get(r, 6));
                    var m2 = BwRe.Match(bw);
                    if (m2.Success)
                    {
                        row.BirthWeightMin = int.Parse(m2.Groups[1].Value);
                        row.BirthWeightMax = int.Parse(m2.Groups[2].Value);
                    }
                    row.BirthWeightNote = bw;
                }
                else if (procField.Length > 0)
                {
                    row.ProcAlts = SplitCodes(procField).Select(SplitPlus).ToList();
                }
                return row;
            }).ToList(),

            // ============ 二、并项规则核心病种 ============
            Bingxiang = RowsOf(FindSheet(wb, "并项规则")).Select(r => new BingxiangRow
            {
                Seq = ToInt(Get(r, 1)) ?? 0,
                DiagCodes = SplitCodes(Get(r, 2)),
                DiagName = Cell(Get(r, 3)),
                ProcCodes = SplitCodes(Get(r, 4)),
                ProcName = Cell(Get(r, 5)),
                RelProcs = SplitCodes(Get(r, 6)).Select(SplitPlus).ToList(),
                RelNames = SplitCodes(Get(r, 7)),
            }).ToList(),

            // ============ 三、诊断辅助细分 ============
            FzShaoshang = RowsOf(FindSheet(wb, "烧伤类病种")).Select(r => new ShaoshangRow
            {
                Seq = ToInt(Get(r, 1)) ?? 0,
                DiagCodes = SplitCodes(Get(r, 2)),
                Degree = Cell(Get(r, 3)),
                OtherDiagCodes = SplitCodes(Get(r, 4)),
                Area = Cell(Get(r, 5)),
                ProcCodes = SplitCodes(Get(r, 6)),
                ProcName = Cell(Get(r, 7)),
                RelProcs = SplitCodes(Get(r, 8)).Select(SplitPlus).ToList(),
                RelNames = SplitCodes(Get(r, 9)),
            }).ToList(),

            FzZhongliu = RowsOf(FindSheet(wb, "肿瘤类病种")).Select(r => new ZhongliuRow
            {
                Seq = ToInt(Get(r, 1)) ?? 0,
                DiagCodes = SplitCodes(Get(r, 2)),
                DiagName = Cell(Get(r, 3)),
                OtherDiagCodes = SplitCodes(Get(r, 4)),
                ProcAlt = SplitPlus(Cell(Get(r, 6)).Replace("\n", "")),
                ProcName = Cell(Get(r, 7)),
            }).ToList(),

            FzJiehe = RowsOf(FindSheet(wb, "结核类病种")).Select(r => new JieheRow
            {
                Seq = ToInt(Get(r, 1)) ?? 0,
                DiagCodes = SplitCodes(Get(r, 2)),
                DiagName = Cell(Get(r, 3)),
                ProcCodes = SplitCodes(Get(r, 4)),
                ProcName = Cell(Get(r, 5)),
                DrugResistant = Cell(Get(r, 6)) == "是",
                ResistNote = Cell(Get(r, 7)),
            }).ToList(),

            JieheResistCodes = AoaOf(wb, "结核耐药", 1)
                .Select(r => new ResistCode { Code = Cell(Get(r, 0)), Name = Cell(Get(r, 1)) })
                .Where(x => x.Code.Length > 0 && x.Code != "主要诊断编码")
                .ToList(),

            // ============ 四、基础规则核心病种 ============
            Jichu = RowsOf(FindSheet(wb, "基础规则")).Select(r => new DiagRow
            {
                Seq = ToInt(Get(r, 1)) ?? 0,
                DiagCodes = SplitCodes(Get(r, 2)),
                DiagName = Cell(Get(r, 3)),
                ProcCodes = SplitCodes(Get(r, 4)),
                ProcName = Cell(Get(r, 5)),
            }).ToList(),

            // ============ 五、不纳入分组 ============
            ExcludedDiags = AoaOf(wb, "不纳入分组的主要诊断", 2)
                .Select(r => new CodeName { Code = Cell(Get(r, 0)), Name = Cell(Get(r, 1)) })
                .Where(x => x.Code.Length > 0 && x.Code != "主要诊断编码")
                .ToList(),

            ExcludedProcs = AoaOf(wb, "不纳入分组的主要手术操作", 2)
                .Select(r => new ExcludedProc { Code = Cell(Get(r, 0)), Name = Cell(Get(r, 1)), Note = Cell(Get(r, 2)) })
                .Where(x => x.Code.Length > 0 && x.Code != "主要手术操作编码")
                .ToList(),

            // ============ 六、基层病种 ============
            Jiceng = AoaOf(wb, "基层病种", 1)
                .Where(r => Cell(Get(r, 0)) != "" && Cell(Get(r, 0)) != "序号")
                .Select(r => new DiagRow
                {
                    Seq = ToInt(Get(r, 0)) ?? 0,
                    DiagCodes = SplitCodes(Get(r, 1)),
                    DiagName = Cell(Get(r, 2)),
                    ProcCodes = SplitCodes(Get(r, 3)),
                    ProcName = Cell(Get(r, 4)),
                }).ToList(),
        };

        config.Meta = new ConfigMeta
        {
            Version = "DIP3.0",
            Source = sourcePath,
            Counts = new Dictionary<string, int>
            {
                ["xianqi"] = config.Xianqi.Count,
                ["bingxiang"] = config.Bingxiang.Count,
                ["fz_shaoshang"] = config.FzShaoshang.Count,
                ["fz_zhongliu"] = config.FzZhongliu.Count,
                ["fz_jiehe"] = config.FzJiehe.Count,
                ["jiehe_resist"] = config.JieheResistCodes.Count,
                ["jichu"] = config.Jichu.Count,
                ["excludedDiags"] = config.ExcludedDiags.Count,
                ["excludedProcs"] = config.ExcludedProcs.Count,
                ["jiceng"] = config.Jiceng.Count,
            },
        };
        return config;
    }
}
