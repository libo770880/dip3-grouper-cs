using Dip3Grouper;
using Dip3GrouperApp.Desktop.Views;

namespace Dip3GrouperApp.Desktop;

/// <summary>关联条目详情（弹窗用）：标题信息 + 量表 + 字段块。</summary>
public sealed class RelatedDoc
{
    public string Lib { get; set; }
    public string LibLabel { get; set; }
    /// <summary>主徽章：编码（规范/物价）或序号（指南）</summary>
    public string CodeChip { get; set; }
    /// <summary>次要徽章：项目性质（主项/加收/扩展）或类别</summary>
    public string ExtraChip { get; set; }
    public string ExtraChip2 { get; set; }
    public string Name { get; set; }
    /// <summary>一行补充说明（价格行 / 章·节 / 类别·单位）</summary>
    public string Sub { get; set; }
    public List<GfMeter> Meters { get; set; } = new();
    public List<LxBlock> Blocks { get; set; } = new();
    public bool HasExtraChip => !string.IsNullOrWhiteSpace(ExtraChip);
    public bool HasExtraChip2 => !string.IsNullOrWhiteSpace(ExtraChip2);
    public bool HasCodeChip => !string.IsNullOrWhiteSpace(CodeChip);
    public bool HasSub => !string.IsNullOrWhiteSpace(Sub);
    public bool HasMeters => Meters.Count > 0;
}

/// <summary>
/// 三库条目的统一取用入口：按 (库, Id) 定位条目，并复用各库前台详情卡的字段块构造，
/// 保证弹窗内容与列表展开的详情卡完全一致（含跨库关联块，可继续点开）。
/// </summary>
public static class Related
{
    public const string Guifan = "guifan";
    public const string Dezhou = "dezhou";
    public const string Lixiang = "lixiang";

    private static readonly object Lock = new();
    private static Dictionary<int, GuifanItem> _gf;
    private static Dictionary<int, DezhouPriceItem> _dz;
    private static Dictionary<int, LixiangRow> _lx;
    private static string _stamp;

    public static string Label(string lib) => lib switch
    {
        Guifan => "项目技术规范",
        Dezhou => "德州市物价",
        Lixiang => "立项指南",
        _ => lib ?? "",
    };

    private static void Ensure()
    {
        var stamp = Stamp();
        if (_gf != null && _dz != null && _lx != null && _stamp == stamp) return;
        lock (Lock)
        {
            if (_gf != null && _dz != null && _lx != null && _stamp == stamp) return;
            _gf = App.State.Guifan.Items.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
            _dz = App.State.Dezhou.Items.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
            // 指南按 Gid 建表：Id 只在区段内唯一，按 Id 建表会让 33 个区段的同号条目互相覆盖，
            // 指向「睡眠呼吸监测费」却弹出 s01 的「供小肠切取术」（两者 Id=11）——§9.25
            _lx = App.State.Lixiang.State.Sections.Values.SelectMany(s => s.Rows)
                .GroupBy(r => r.Gid).ToDictionary(g => g.Key, g => g.First());
            _stamp = stamp;
        }
    }

    /// <summary>与 CrossMap 同款数据戳：条数变了就重建，避免按旧 Id 指到别的条目。</summary>
    private static string Stamp()
    {
        var lx = App.State.Lixiang.State;
        var rows = 0;
        foreach (var s in lx.Sections.Values) rows += s.Rows.Count;
        return $"{lx.Sections.Count}/{rows}|{App.State.Dezhou.Items.Count}";
    }

    public static bool TryLoad(string lib, int id, out RelatedDoc doc)
    {
        Ensure();
        doc = null;
        switch (lib)
        {
            case Guifan:
                if (!_gf.TryGetValue(id, out var g)) return false;
                doc = new RelatedDoc
                {
                    Lib = Guifan,
                    LibLabel = Label(Guifan),
                    CodeChip = g.Code,
                    ExtraChip = g.Category,
                    ExtraChip2 = string.IsNullOrWhiteSpace(g.Unit) ? "" : "单位 " + g.Unit,
                    Name = Flat(g.Name),
                    Sub = string.Join(" · ", new[] { g.Chapter, g.Section, g.Group }
                        .Where(s => !string.IsNullOrWhiteSpace(s))),
                    Meters = GuifanView.BuildMeters(g.Difficulty, g.Risk, g.HrValue),
                    Blocks = GuifanView.BuildBlocks(g),
                };
                return true;

            case Dezhou:
                if (!_dz.TryGetValue(id, out var z)) return false;
                doc = new RelatedDoc
                {
                    Lib = Dezhou,
                    LibLabel = Label(Dezhou),
                    CodeChip = z.Code,
                    ExtraChip = z.Kind switch { "add" => "加收项", "ext" => "扩展项", _ => "主项" },
                    ExtraChip2 = string.IsNullOrWhiteSpace(z.Category) ? "" : z.Category,
                    Name = Flat(z.Name),
                    Sub = string.Join(" · ", new[]
                    {
                        "三级/二级/一级 " + string.Join(" / ", new[] { z.Price3, z.Price2, z.Price1 }
                            .Where(s => !string.IsNullOrWhiteSpace(s))),
                        z.Doc,
                    }.Where(s => !string.IsNullOrWhiteSpace(s))),
                    Blocks = DezhouPriceView.BuildBlocks(z),
                };
                return true;

            case Lixiang:
                if (!_lx.TryGetValue(id, out var r)) return false;
                doc = new RelatedDoc
                {
                    Lib = Lixiang,
                    LibLabel = Label(Lixiang),
                    CodeChip = string.IsNullOrWhiteSpace(r.Seq) ? "" : "序号 " + r.Seq,
                    ExtraChip = Flat(r.Category),
                    ExtraChip2 = string.IsNullOrWhiteSpace(r.Unit) ? "" : "计价单位 " + Flat(r.Unit),
                    Name = Flat(r.Name),
                    Blocks = LixiangView.BuildBlocks(r),
                };
                return true;

            default:
                return false;
        }
    }

    private static string Flat(string v)
    {
        var s = (v ?? "").Replace("\r", "").Replace("\n", " ");
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Trim();
    }
}
