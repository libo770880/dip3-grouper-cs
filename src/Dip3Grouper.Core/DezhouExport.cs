namespace Dip3Grouper;

/// <summary>
/// 前台「导 出」：把当前筛选到的物价项目写成 Excel，供科室对账、贴进报表或转给别人核对。
///
/// 首行仍是与后台导入模板完全一致的表头（列顺序取自 <see cref="DezhouPriceStore.Columns"/>），
/// 所以导出的文件可以直接再导回后台，不会变成「单向垃圾文件」。
/// 第二个 sheet 附上导出范围覆盖到的「类别使用说明」——物价人员最常问的就是「这个类的加收怎么算」。
/// </summary>
public static class DezhouExport
{
    /// <summary>第二个 sheet 的名字（也是前台导出与后台数据的差别所在）。</summary>
    public const string UsageSheet = "类别使用说明";

    /// <summary>
    /// 生成工作簿。<paramref name="scopeLabel"/> 只用于文件名（如「全部结果」「仅收藏」「第 2 页」）。
    /// </summary>
    public static (byte[] Bytes, string FileName) Front(AppState S, IReadOnlyList<DezhouPriceItem> items,
                                                        string scopeLabel)
    {
        items ??= Array.Empty<DezhouPriceItem>();
        var cols = DezhouPriceStore.Columns;

        var aoa = new List<List<object>> { cols.Select(c => (object)c.Label).ToList() };
        aoa.AddRange(items.Select(r => cols.Select(c => (object)Cell(S, r, c.Key)).ToList()));

        var sheets = new List<ExcelUtil.SheetSpec>
        {
            new()
            {
                Name = "物价项目",
                Aoa = aoa,
                ColWidths = ExcelUtil.WidthsFromPixels(cols.Select(c => c.Width ?? 120)),
            },
        };

        var usage = UsageSheetRows(S, items);
        if (usage.Count > 1)                       // 只有表头说明没内容，就不加这张空表
            sheets.Add(new ExcelUtil.SheetSpec
            {
                Name = UsageSheet,
                Aoa = usage,
                ColWidths = ExcelUtil.WidthsFromPixels(new[] { 150, 124, 56, 460 }),
            });

        var name = $"德州物价_{San(scopeLabel)}_{DateTime.Now:yyyyMMdd-HHmm}.xlsx";
        return (ExcelUtil.BuildWorkbook(sheets), name);
    }

    /// <summary>
    /// 单元格取值：价格三列走 <see cref="DezhouPriceStore.PriceOf"/>，把加收项的 Excel 公式
    /// （<c>ROUND(主项*0.2,0)</c>）换算成金额再落表——导出是给人对账的，不该出现机器文本。
    /// </summary>
    private static string Cell(AppState S, DezhouPriceItem it, string key)
        => key is "price3" or "price2" or "price1"
            ? S.Dezhou.PriceOf(it, key)
            : AdminService.DezhouField(it, key);

    // ==================================================================
    // 新旧比对视图导出：与界面同源的比对行（现行 ↔ 旧版物价）逐列落表。
    // 汇总单独成 sheet —— 绝不能把汇总行插在表头正下方，否则筛选 / 排序 / 再导入全错
    // （2026-10-30 征求意见稿导出踩过的坑，这里一开始就走对的路）。
    // ==================================================================

    /// <summary>生成新旧比对工作簿。<paramref name="scopeLabel"/> 用于文件名（如「全部」「有旧价可比」）。</summary>
    public static (byte[] Bytes, string FileName) Cmp(AppState S, IReadOnlyList<DezhouDraftCompareRow> rows,
                                                      string scopeLabel)
    {
        rows ??= Array.Empty<DezhouDraftCompareRow>();
        var head = new List<object>
        {
            "新项目编码", "新项目名称", "类别", "性质",
            "新三级价", "新二级价", "新一级价",
            "旧项目编码", "旧项目名称",
            "旧三级价", "旧二级价", "旧一级价",
            "三级差额", "三级涨幅%", "二级差额", "二级涨幅%", "一级差额", "一级涨幅%",
            "基准来源",
        };
        var aoa = new List<List<object>> { head };
        aoa.AddRange(rows.Select(r => new List<object>
        {
            r.NewCode, r.NewName, r.Category, KindText(r.NewKind),
            r.NewPrice3, r.NewPrice2, r.NewPrice1,
            Nz(r.OldCode), Nz(r.OldName),
            Nz(r.OldPrice3), Nz(r.OldPrice2), Nz(r.OldPrice1),
            Num(r.Delta3), Pct(r.Ratio3), Num(r.Delta2), Pct(r.Ratio2), Num(r.Delta1), Pct(r.Ratio1),
            SourceText(r.OldSource),
        }));

        var sheets = new List<ExcelUtil.SheetSpec>
        {
            new()
            {
                Name = "新旧比对",
                Aoa = aoa,
                ColWidths = ExcelUtil.WidthsFromPixels(new[]
                {
                    130, 200, 90, 56, 80, 80, 80, 110, 180, 80, 80, 80,
                    70, 76, 70, 76, 70, 76, 90,
                }),
            },
        };
        var summary = CmpSummary(rows, scopeLabel);
        sheets.Add(new ExcelUtil.SheetSpec
        {
            Name = "比对汇总",
            Aoa = summary,
            ColWidths = ExcelUtil.WidthsFromPixels(new[] { 180, 420 }),
        });

        var name = $"德州物价_新旧比对_{San(scopeLabel)}_{DateTime.Now:yyyyMMdd-HHmm}.xlsx";
        return (ExcelUtil.BuildWorkbook(sheets), name);
    }

    private static List<List<object>> CmpSummary(IReadOnlyList<DezhouDraftCompareRow> rows, string scopeLabel)
    {
        var priced = rows.Count(r => r.OldSource == "legacy");
        var up = rows.Count(r => r.Delta3 > 0);
        var down = rows.Count(r => r.Delta3 < 0);
        var flat = rows.Count(r => r.OldSource == "legacy" && r.Delta3 == 0);
        return new List<List<object>>
        {
            new() { "指标", "数值" },
            new() { "比对结果合计", rows.Count },
            new() { "有旧价可比", priced },
            new() { "　其中 涨价", up },
            new() { "　其中 降价", down },
            new() { "　其中 持平", flat },
            new() { "纯新增项目", rows.Count(r => r.OldSource == "none") },
            new() { "仅作废对照", rows.Count(r => r.OldSource == "cancel") },
            new() { "导出口径", scopeLabel ?? "" },
        };
    }

    private static string KindText(string kind) => kind switch
    {
        "add" => "加收",
        "ext" => "扩展",
        _ => "主项",
    };

    private static string SourceText(string src) => src switch
    {
        "legacy" => "旧版物价",
        "cancel" => "仅作废对照",
        _ => "纯新增",
    };

    /// <summary>差额：数值直接落格（正数带 + 号），空值统一「—」。</summary>
    private static object Num(double? v) => v is null ? "—" : (v > 0 ? "+" : "") + v.Value.ToString("0.##");

    /// <summary>涨幅：百分比一位小数（正数带 + 号），空值统一「—」。</summary>
    private static object Pct(double? v) => v is null ? "—" : (v > 0 ? "+" : "") + v.Value.ToString("0.#") + "%";

    private static string Nz(string v) => string.IsNullOrWhiteSpace(v) ? "—" : v;

    /// <summary>类别使用说明附表：按〈文号 + 类别〉去重，每个组合把说明逐条列开。</summary>
    private static List<List<object>> UsageSheetRows(AppState S, IReadOnlyList<DezhouPriceItem> items)
    {
        var rows = new List<List<object>> { new() { "落地文号", "类别", "序号", "使用说明" } };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var it in items)
        {
            var key = (it.Doc ?? "") + "\u0001" + (it.Category ?? "");
            if (!seen.Add(key)) continue;
            var list = S.Dezhou.UsageOf(it.Doc, it.Category);
            for (var i = 0; i < list.Count; i++)
                rows.Add(new List<object> { it.Doc ?? "", it.Category ?? "", i + 1, list[i] });
        }
        return rows;
    }

    /// <summary>文件名里不能出现路径分隔符与 Windows 非法字符。</summary>
    private static string San(string s)
    {
        var t = (s ?? "").Trim();
        foreach (var ch in Path.GetInvalidFileNameChars()) t = t.Replace(ch, '-');
        return t.Length == 0 ? "导出" : t;
    }
}
