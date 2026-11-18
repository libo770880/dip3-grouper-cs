namespace Dip3Grouper;

/// <summary>技术规范查询结果。</summary>
public sealed class GuifanListResult
{
    public string Label { get; set; } = "全国医疗服务项目技术规范";
    public List<GuifanItem> Items { get; set; } = new();
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

/// <summary>技术规范库统计。</summary>
public sealed class GuifanStat
{
    public int Total { get; set; }
    public int ChapterCount { get; set; }
    public int CategoryCount { get; set; }
    /// <summary>有「必需耗材」描述的项目数</summary>
    public int WithMaterial { get; set; }
    /// <summary>有「技术难度」分值的项目数</summary>
    public int WithScore { get; set; }
}

/// <summary>
/// 《全国医疗服务项目技术规范（2023 年版）》查询库。
/// 数据文件 <c>data/guifan.json</c>，由国家卫健委 / 国家中医药局 / 国家疾控局 2023 年版规范正文
/// （2162 页、17 要素大表）解析而来，共 11 章（含辅助操作）11516 个项目。
/// 载入较慢（约 10 MB），因此在 <see cref="AppState"/> 中是首次使用时才实例化。
/// </summary>
public class GuifanStore
{
    public List<GuifanItem> Items { get; private set; } = new();
    public GuifanMeta Meta { get; private set; } = new();
    public bool Loaded { get; private set; }

    private readonly object _gate = new();

    /// <summary>首次访问时载入（可在后台线程预热；并发触发只会真正载入一次）</summary>
    public void EnsureLoaded()
    {
        if (Loaded) return;
        lock (_gate)
        {
            if (Loaded) return;
            Load();
        }
    }

    public void Load()
    {
        try
        {
            var d = Js.ReadJsonFile<GuifanFile>(AppPaths.GuifanFile);
            if (d != null)
            {
                Items = d.Items ?? new List<GuifanItem>();
                Meta = d.Meta ?? new GuifanMeta();
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[guifan] guifan.json 解析失败，使用空库: {e.Message}");
            Items = new List<GuifanItem>();
        }
        // ⚠ 必须在解析完成后才置位：后台预热线程与 UI 线程会并发 EnsureLoaded，
        //   若先置位，另一方会拿到「已加载」标记却读到空的 Items（曾导致技术规范页打开空白）
        Loaded = true;
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        Js.WriteJsonFile(AppPaths.GuifanFile, new GuifanFile { Meta = Meta, Items = Items });
    }

    /// <summary>从解析中间产物（.tmp/guifan/raw.json）导入并规范化落盘。</summary>
    public int ImportRaw(string rawPath, string source)
    {
        var d = Js.ReadJsonFile<GuifanFile>(rawPath)
                ?? throw new InvalidOperationException($"解析文件读取失败: {rawPath}");
        Items = d.Items ?? new List<GuifanItem>();
        Meta = d.Meta ?? new GuifanMeta();
        Meta.Source = source;
        Meta.ImportedAt = Js.Iso();
        Meta.Total = Items.Count;
        Save();
        Loaded = true;
        return Items.Count;
    }

    // ==================================================================
    // 检索
    // ==================================================================

    public List<string> Categories()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var it in Items)
        {
            var v = (it.Category ?? "").Trim();
            if (v.Length == 0 || !seen.Add(v)) continue;
            list.Add(v);
        }
        return list;
    }

    /// <summary>章列表（保序，取自数据本身，附带实际项目数）。</summary>
    public List<GuifanChapter> Chapters()
    {
        var list = new List<GuifanChapter>();
        var idx = new Dictionary<string, GuifanChapter>(StringComparer.Ordinal);
        foreach (var it in Items)
        {
            var key = (it.Chapter ?? "").Trim();
            if (key.Length == 0) continue;
            if (!idx.TryGetValue(key, out var c))
            {
                c = new GuifanChapter { Code = key.Split(' ')[0], Name = key, Category = it.Category };
                idx[key] = c;
                list.Add(c);
            }
            c.Items++;
        }
        return list;
    }

    public GuifanStat Stat()
    {
        var s = new GuifanStat { Total = Items.Count };
        var chaps = new HashSet<string>(StringComparer.Ordinal);
        var cats = new HashSet<string>(StringComparer.Ordinal);
        foreach (var it in Items)
        {
            if (!string.IsNullOrEmpty(it.Chapter)) chaps.Add(it.Chapter);
            if (!string.IsNullOrEmpty(it.Category)) cats.Add(it.Category);
            if (!string.IsNullOrWhiteSpace(it.Required)) s.WithMaterial++;
            if (!string.IsNullOrWhiteSpace(it.Difficulty)) s.WithScore++;
        }
        s.ChapterCount = chaps.Count;
        s.CategoryCount = cats.Count;
        return s;
    }

    /// <summary>
    /// 单条是否满足筛选条件（空串表示该条件不限）。
    /// material："" 不限 / "req" 有必需耗材 / "none" 无必需耗材。
    /// </summary>
    public static bool Matches(GuifanItem it, string category, string chapter, string q,
                              string unit = "", string material = "")
    {
        if (!string.IsNullOrEmpty(category) && it.Category != category) return false;
        if (!string.IsNullOrEmpty(chapter) && !(it.Chapter ?? "").StartsWith(chapter, StringComparison.Ordinal))
            return false;
        if (!string.IsNullOrEmpty(unit) && (it.Unit ?? "").Trim() != unit) return false;
        if (material == "req" && string.IsNullOrWhiteSpace(it.Required)) return false;
        if (material == "none" && !string.IsNullOrWhiteSpace(it.Required)) return false;

        var kw = (q ?? "").Trim();
        if (kw.Length == 0) return true;
        if ((it.Code ?? "").StartsWith(kw, StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var v in new[] { it.Name, it.En, it.Content, it.Unit, it.Note,
                                  it.Required, it.Optional, it.Bill, it.Account, it.Group })
        {
            if ((v ?? "").Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;
        }
        // 拼音（全拼 / 首字母）：只对短名称类字段做，内涵等长文本不做拼音以免拖慢
        return PinyinHelper.PyMatchAny(new[] { it.Name, it.Category, it.Group }, kw);
    }

    /// <summary>
    /// 检索 + 分页。各条件为空表示不限；关键词命中编码前缀、名称、内涵、耗材、票据分类等，
    /// 支持全拼与首字母（如 xzyz → 心脏移植）；material 见 <see cref="Matches"/>。
    /// </summary>
    public GuifanListResult ListRows(string category, string chapter, string q, int page, int pageSize,
                                     string unit = "", string material = "")
    {
        category ??= "";
        chapter ??= "";
        unit ??= "";
        material ??= "";

        var rows = Items.Where(it => Matches(it, category, chapter, q, unit, material)).ToList();
        var total = rows.Count;
        page = Math.Max(1, page);
        pageSize = Math.Min(200, Math.Max(5, pageSize));
        var start = (page - 1) * pageSize;

        var label = category.Length > 0 ? category : "全部类别";
        if (chapter.Length > 0) label += " · " + chapter;
        if (unit.Length > 0) label += " · 单位 " + unit;
        if (material.Length > 0) label += material == "req" ? " · 含必需耗材" : " · 无必需耗材";

        return new GuifanListResult
        {
            Label = label,
            Items = rows.Skip(start).Take(pageSize).ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize,
        };
    }

    /// <summary>按 8 位编码精确定位。</summary>
    public GuifanItem ByCode(string code)
        => Items.FirstOrDefault(i => string.Equals(i.Code, (code ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>计量单位候选值（保序去重）。</summary>
    public List<string> DistinctUnits() => Distinct(i => i.Unit);

    /// <summary>计量单位及出现次数（按次数降序）——供前台下拉把常用单位排在前面并显示条数。</summary>
    public List<KeyValuePair<string, int>> UnitCounts()
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var it in Items)
        {
            var v = (it.Unit ?? "").Trim();
            if (v.Length == 0) continue;
            map[v] = map.TryGetValue(v, out var c) ? c + 1 : 1;
        }
        return map.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
    }

    private List<string> Distinct(Func<GuifanItem, string> pick)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var it in Items)
        {
            var v = (pick(it) ?? "").Trim();
            if (v.Length == 0 || !seen.Add(v)) continue;
            list.Add(v);
        }
        return list;
    }
}
