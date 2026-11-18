namespace Dip3Grouper;

/// <summary>德州物价查询结果。</summary>
public sealed class DezhouListResult
{
    public string Label { get; set; } = "德州市物价";
    public List<DezhouPriceItem> Items { get; set; } = new();
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

/// <summary>德州物价库统计。</summary>
public sealed class DezhouStat
{
    public int Total { get; set; }
    public int Main { get; set; }
    public int Add { get; set; }
    public int Ext { get; set; }
    /// <summary>医保支付为「是」的条数</summary>
    public int Insured { get; set; }
    public int BatchCount { get; set; }
    public int CategoryCount { get; set; }
}

/// <summary>
/// 德州市物价 —— 国家立项指南在德州落地的「规范整合」项目。
/// 数据文件 <c>data/dezhou-price.json</c>，由德州各批规范整合通知的附件项目表解析而来，
/// 含 15 位项目编码、三级价格、医保支付与自付比例。
/// </summary>
public class DezhouPriceStore
{
    public List<DezhouPriceItem> Items { get; private set; } = new();
    public DezhouPriceMeta Meta { get; private set; } = new();
    /// <summary>作废项目对照行（批次映射关系表：作废原项目 → 对应新项目）。</summary>
    public List<DezhouCancelItem> Cancels { get; private set; } = new();
    /// <summary>旧版物价（45号文对码表，规范整合前的旧价格项目）。</summary>
    public List<DezhouLegacyItem> Legacy { get; private set; } = new();

    /// <summary>旧码 → 现行标准（作废对照联查索引，懒构建；值含「编码 名称」串，多对多用「；」连接）。</summary>
    private Dictionary<string, string> _cancelIndex;

    /// <summary>
    /// 已作废旧码集合（只判「是否作废」，**不要求有新项目替代**）。
    /// 与 <see cref="_cancelIndex"/> 分开维护：索引只收「有替代」的行（供现行标准列），
    /// 但「直接废止·无替代项目」同样是作废，必须计入判定，否则旧版物价会误标「现行」。
    /// </summary>
    private HashSet<string> _canceledSet;

    /// <summary>
    /// 同族补判索引：（基础码, 归一化名称）→ 作废对照行（懒构建）。
    /// <para>映射关系表引的是省对码，可能与本地对码表的字母后缀错位：
    /// 对照写 <c>311400012</c>，旧表里同名项目是 <c>311400012a</c>（或反向）。
    /// 编码直接命不中的行按「同基础码 + 同名」补判，否则旧版物价会误标「现行」。</para>
    /// </summary>
    private Dictionary<(string, string), DezhouCancelItem> _cancelFamily;

    public DezhouPriceStore() => Load();

    public void Load()
    {
        try
        {
            var d = Js.ReadJsonFile<DezhouPriceFile>(AppPaths.DezhouFile);
            if (d != null)
            {
                Items = d.Items ?? new List<DezhouPriceItem>();
                Meta = d.Meta ?? new DezhouPriceMeta();
                Cancels = d.Cancels ?? new List<DezhouCancelItem>();
                Legacy = d.Legacy ?? new List<DezhouLegacyItem>();
                _cancelIndex = null;
                _canceledSet = null;
                _cancelFamily = null;
                _cancelMap = null;
                InvalidateCompare();
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[dezhou] dezhou-price.json 解析失败，使用空库: {e.Message}");
            Items = new List<DezhouPriceItem>();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        Js.WriteJsonFile(AppPaths.DezhouFile, new DezhouPriceFile
        {
            Meta = Meta,
            Items = Items,
            Cancels = Cancels,
            Legacy = Legacy,
        });
    }

    /// <summary>从解析中间产物导入并规范化落盘（落盘字节格式由写盘器统一保证）。</summary>
    public int ImportRaw(string rawPath, string source)
    {
        var d = Js.ReadJsonFile<DezhouPriceFile>(rawPath)
                ?? throw new InvalidOperationException($"解析文件读取失败: {rawPath}");
        // 类别使用说明不在项目表里（来自通知 PDF 的「使用说明」条款），重新导入项目表时按
        // (文号, 类别) 原样保留，避免一次重导就把已抽取 / 后台维护过的说明清空。
        var keep = UsageMap();
        Items = d.Items ?? new List<DezhouPriceItem>();
        Meta = d.Meta ?? new DezhouPriceMeta();
        Meta.Source = source;
        Meta.ImportedAt = Js.Iso();
        Meta.Total = Items.Count;
        // 重导项目表不动已合并的作废对照与旧版物价（三者是不同来源，各自独立维护）
        if (d.Cancels is { Count: > 0 }) Cancels = d.Cancels;
        if (d.Legacy is { Count: > 0 }) Legacy = d.Legacy;
        _cancelIndex = null;
        _canceledSet = null;
        _cancelFamily = null;
        _cancelMap = null;
        InvalidateCompare();
        ApplyUsage(keep);
        Save();
        return Items.Count;
    }

    /// <summary>导入「作废项目对照」（.tmp/dezhou/cancels-clean.json：{total, cancels:[…]}），返回导入行数。</summary>
    public int ImportCancels(string path)
    {
        var d = Js.ReadJsonFile<DezhouCancelFile>(path)
                ?? throw new InvalidOperationException($"作废对照文件读取失败: {path}");
        // 「划线保留」是人眼识别 PDF 删除线后补的标记，解析产物里没有这个键 ——
        // 整表重导时按 (文号, 旧编码) 从旧表搬回去，否则一次重导就把不作废判定打回作废。
        var keepKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in Cancels)
            if (c.Keep) keepKeys.Add(KeepKey(c.Doc, c.OldCode));
        Cancels = d.Cancels ?? new List<DezhouCancelItem>();
        if (keepKeys.Count > 0)
            foreach (var c in Cancels)
                if (keepKeys.Contains(KeepKey(c.Doc, c.OldCode))) c.Keep = true;
        _cancelIndex = null;
        _canceledSet = null;
        _cancelFamily = null;
        _cancelMap = null;
        InvalidateCompare();
        Save();
        return Cancels.Count;
    }

    /// <summary>保留标记的比对键：同一文号下同一旧编码视为同一行。</summary>
    private static string KeepKey(string doc, string oldCode)
        => ((doc ?? "").Trim()) + "|" + ((oldCode ?? "").Trim());

    /// <summary>导入「旧版物价」（.tmp/dezhou/legacy.json：{total, legacy:[…]}），返回导入行数。</summary>
    public int ImportLegacy(string path)
    {
        var d = Js.ReadJsonFile<DezhouLegacyFile>(path)
                ?? throw new InvalidOperationException($"旧版物价文件读取失败: {path}");
        Legacy = d.Legacy ?? new List<DezhouLegacyItem>();
        InvalidateCompare();   // 旧价表变了，比对行必须重建（否则拿到的是旧表算出的差额）
        Save();
        return Legacy.Count;
    }

    /// <summary>当前 meta 中 (文号, 类别) → 使用说明条目的映射（只含非空项）。</summary>
    private Dictionary<string, List<string>> UsageMap()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var b in Meta?.Batches ?? new List<DezhouBatch>())
            foreach (var c in b.Categories ?? new List<DezhouCategory>())
                if (c.Usage is { Count: > 0 }) map[UsageKey(b.Doc, c.Name)] = c.Usage;
        return map;
    }

    private static string UsageKey(string doc, string category)
        => (doc ?? "").Trim() + "\u0001" + (category ?? "").Trim();

    /// <summary>把使用说明贴回当前的批次 / 类别结构上（类别以「文号 + 名称」定位）。</summary>
    private void ApplyUsage(Dictionary<string, List<string>> map)
    {
        if (map == null || map.Count == 0) return;
        foreach (var b in Meta.Batches ?? new List<DezhouBatch>())
            foreach (var c in b.Categories ?? new List<DezhouCategory>())
                if (map.TryGetValue(UsageKey(b.Doc, c.Name), out var u)) c.Usage = u;
    }

    /// <summary>
    /// 导入「类别使用说明」（.tmp/dezhou/usage.json：{文号: {类别: [条目…]}}）。
    /// 只写入项目数据中真实存在的 (文号, 类别)；返回写入的类别数与条目数。
    /// </summary>
    public (int Categories, int Items) ImportUsage(string path)
    {
        var map = Js.ReadJsonFile<Dictionary<string, Dictionary<string, List<string>>>>(path)
                  ?? throw new InvalidOperationException($"使用说明文件读取失败: {path}");
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var it in Items) known.Add(UsageKey(it.Doc, it.Category));

        var cats = 0;
        var n = 0;
        foreach (var b in Meta.Batches ?? new List<DezhouBatch>())
            foreach (var c in b.Categories ?? new List<DezhouCategory>())
            {
                if (!known.Contains(UsageKey(b.Doc, c.Name))) continue;
                var list = map.TryGetValue(b.Doc ?? "", out var byCat) && byCat.TryGetValue(c.Name ?? "", out var u)
                    ? u.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList()
                    : new List<string>();
                if (list.Count == 0) continue;
                c.Usage = list;
                cats++;
                n += list.Count;
            }
        Save();
        return (cats, n);
    }

    public List<DezhouBatch> Batches() => Meta.Batches ?? new List<DezhouBatch>();

    /// <summary>某个 (文号, 类别) 的使用说明条目；doc 为空时按类别名跨批次命中；无则返回空表。</summary>
    public List<string> UsageOf(string doc, string category)
    {
        var d = (doc ?? "").Trim();
        var c = (category ?? "").Trim();
        if (c.Length == 0) return new List<string>();
        foreach (var b in Meta.Batches ?? new List<DezhouBatch>())
        {
            if (d.Length > 0 && b.Doc != d) continue;
            foreach (var cat in b.Categories ?? new List<DezhouCategory>())
                if (cat.Name == c) return cat.Usage ?? new List<string>();
        }
        return new List<string>();
    }

    /// <summary>
    /// 设置某 (文号, 类别) 的「类别使用说明」条目（后台手工维护用）。
    /// 传空集合即清空该类别说明；返回写入后的条目数。
    /// 批次结构里找不到该类别时先 <see cref="Recompute"/> 重建一次再试（元数据可能滞后于项目数据）。
    /// </summary>
    public int SetUsage(string doc, string category, IEnumerable<string> lines)
    {
        var d = (doc ?? "").Trim();
        var c = (category ?? "").Trim();
        if (c.Length == 0) return 0;

        var list = (lines ?? Array.Empty<string>())
            .Select(x => (x ?? "").Trim())
            .Where(x => x.Length > 0)
            .ToList();

        if (!TrySetUsage(d, c, list))
        {
            Recompute();
            if (!TrySetUsage(d, c, list)) return 0;   // 类别在数据里不存在：不落盘、不新增空类别
        }
        Save();
        return list.Count;
    }

    private bool TrySetUsage(string doc, string category, List<string> list)
    {
        var hit = false;
        foreach (var b in Meta.Batches ?? new List<DezhouBatch>())
        {
            if (b.Doc != doc) continue;
            foreach (var cat in b.Categories ?? new List<DezhouCategory>())
            {
                if (cat.Name != category) continue;
                cat.Usage = list;
                hit = true;
            }
        }
        return hit;
    }

    /// <summary>使用说明覆盖情况：有说明的类别数 / 类别总数与条目总数。</summary>
    public (int Covered, int Categories, int Entries) UsageStat()
    {
        var covered = 0;
        var cats = 0;
        var n = 0;
        foreach (var b in Meta.Batches ?? new List<DezhouBatch>())
            foreach (var c in b.Categories ?? new List<DezhouCategory>())
            {
                cats++;
                var k = (c.Usage ?? new List<string>()).Count;
                if (k > 0) covered++;
                n += k;
            }
        return (covered, cats, n);
    }

    /// <summary>某文号下所有类别的使用说明（供「按文号汇总查看」）。</summary>
    public List<(string Category, List<string> Usage)> UsageOfDoc(string doc)
    {
        var outList = new List<(string, List<string>)>();
        foreach (var b in Meta.Batches ?? new List<DezhouBatch>())
        {
            if (!string.IsNullOrEmpty(doc) && b.Doc != doc) continue;
            foreach (var c in b.Categories ?? new List<DezhouCategory>())
                if ((c.Usage ?? new List<string>()).Count > 0) outList.Add((c.Name, c.Usage));
        }
        return outList;
    }

    /// <summary>
    /// 全部批次的使用说明（文号, 类别, 条目），供前台「使用说明」面板在未筛选时全量展示。
    /// 只含有说明的类别，保序（按批次 → 类别）。
    /// </summary>
    public List<(string Doc, string Category, List<string> Usage)> UsageAll()
    {
        var outList = new List<(string, string, List<string>)>();
        foreach (var b in Meta.Batches ?? new List<DezhouBatch>())
            foreach (var c in b.Categories ?? new List<DezhouCategory>())
                if ((c.Usage ?? new List<string>()).Count > 0)
                    outList.Add((b.Doc, c.Name, c.Usage));
        return outList;
    }

    /// <summary>某文号下的类别列表（doc 为空 = 全部批次，保序去重）。</summary>
    public List<string> Categories(string doc)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var it in Items)
        {
            if (doc.Length > 0 && it.Doc != doc) continue;
            var v = (it.Category ?? "").Trim();
            if (v.Length == 0 || !seen.Add(v)) continue;
            list.Add(v);
        }
        return list;
    }

    public DezhouStat Stat()
    {
        var s = new DezhouStat { Total = Items.Count };
        var cats = new HashSet<string>(StringComparer.Ordinal);
        var docs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var it in Items)
        {
            switch (it.Kind)
            {
                case "add": s.Add++; break;
                case "ext": s.Ext++; break;
                default: s.Main++; break;
            }
            if (it.Insurance == "是") s.Insured++;
            if (!string.IsNullOrEmpty(it.Category)) cats.Add(it.Category);
            if (!string.IsNullOrEmpty(it.Doc)) docs.Add(it.Doc);
        }
        s.CategoryCount = cats.Count;
        s.BatchCount = docs.Count;
        return s;
    }

    /// <summary>单条是否满足筛选条件（doc / category / kind 为空表示不限）。</summary>
    /// <summary>
    /// 单条是否满足筛选条件（空串表示不限）。
    /// insurance："" 不限 / "是" 医保支付 / "否" 非医保支付。
    /// level："" 不限 / "3"三级 / "2"二级 / "1"一级 / "eq"三档同价（见 <see cref="HasLevel"/>）。
    /// </summary>
    public static bool Matches(DezhouPriceItem it, string doc, string category, string kind, string q,
                               string insurance = "", string level = "")
    {
        if (!string.IsNullOrEmpty(doc) && it.Doc != doc) return false;
        if (!string.IsNullOrEmpty(category) && it.Category != category) return false;
        if (!string.IsNullOrEmpty(kind) && it.Kind != kind) return false;
        if (!string.IsNullOrEmpty(insurance) && (it.Insurance ?? "").Trim() != insurance) return false;
        if (!string.IsNullOrEmpty(level) && !HasLevel(it, level)) return false;

        var kw = (q ?? "").Trim().ToUpperInvariant();
        if (kw.Length == 0) return true;
        if ((it.Code ?? "").StartsWith(kw, StringComparison.Ordinal)) return true;
        return new[] { it.Name, it.Category, it.Doc, it.Unit, it.Note, it.Usage, it.ServiceOutput }
                   .Any(v => (v ?? "").Contains(kw, StringComparison.OrdinalIgnoreCase))
               || PinyinHelper.PyMatchAny(new[] { it.Name, it.Category, it.Unit }, kw);
    }

    /// <summary>
    /// 该级别是否有价。三级 / 二级 / 一级是**价格档位**（同一项目按医院等级定价），不是三条独立记录：
    /// 项目在某档为空 = 该级别不开展 / 不设价（51 条三档全空、另有零星空档）。
    /// 「eq」= 三档同价（各级别同价的项目，与具体级别无关）。
    /// </summary>
    public static bool HasLevel(DezhouPriceItem it, string level)
    {
        var p3 = (it?.Price3 ?? "").Trim();
        var p2 = (it?.Price2 ?? "").Trim();
        var p1 = (it?.Price1 ?? "").Trim();
        return level switch
        {
            "3" => p3.Length > 0,
            "2" => p2.Length > 0,
            "1" => p1.Length > 0,
            "eq" => p3.Length > 0 && p3 == p2 && p2 == p1,
            _ => true,
        };
    }

    /// <summary>
    /// 取该级别的价格（展示与导出用）。level 为空 / "eq" / 未知 → 回落到三级价。
    /// 走 <see cref="PriceOf"/>，公式价换算与「主项缺失显示 —」的口径与三档价列一致。
    /// </summary>
    public string PriceAtLevel(DezhouPriceItem it, string level)
    {
        var col = level switch { "2" => "price2", "1" => "price1", _ => "price3" };
        return PriceOf(it, col);
    }

    /// <summary>级别中文名（"3" → 三级，用于状态条 / 导出表头）。</summary>
    public static string LevelName(string level) => level switch
    {
        "3" => "三级", "2" => "二级", "1" => "一级", "eq" => "同价", _ => "",
    };

    /// <summary>
    /// 检索 + 分页。doc/category/kind/insurance/level 为空表示不限；
    /// 关键词命中编码前缀、名称、服务产出、计价说明等，支持全拼与首字母（xzyz → 心脏移植）。
    /// <paramref name="filter"/> 为可选的额外行过滤（如「仅看收藏」，由调用方注入收藏集合，Core 不依赖收藏存储）。
    /// </summary>
    public DezhouListResult ListRows(string doc, string category, string kind, string q, int page, int pageSize,
                                     string insurance = "", Func<DezhouPriceItem, bool> filter = null,
                                     string level = "")
    {
        doc ??= "";
        category ??= "";
        kind ??= "";
        insurance ??= "";
        level ??= "";

        IEnumerable<DezhouPriceItem> seq = Items.Where(it => Matches(it, doc, category, kind, q, insurance, level));
        if (filter != null) seq = seq.Where(filter);
        var rows = seq.ToList();
        var total = rows.Count;
        page = Math.Max(1, page);
        pageSize = Math.Min(200, Math.Max(5, pageSize));
        var start = (page - 1) * pageSize;

        var label = doc.Length > 0 ? doc : "全部批次";
        if (category.Length > 0) label += " · " + category;
        if (insurance.Length > 0) label += " · 医保支付" + insurance;
        if (level.Length > 0) label += " · " + LevelName(level) + "医院";

        return new DezhouListResult
        {
            Label = label,
            Items = rows.Skip(start).Take(pageSize).ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize,
        };
    }

    /// <summary>按 15 位编码精确定位。</summary>
    public DezhouPriceItem ByCode(string code)
        => Items.FirstOrDefault(i => i.Code == (code ?? "").Trim());

    // ==================================================================
    // 加收项公式求值（与 DezhouDraftStore 同一套规则，两边展示口径必须一致）
    //   征求意见稿里「儿童（加收）」等加收项的价格列是 **Excel 公式**
    //   （形如 ROUND(G42*0.2,0)：主项同档价 × 加收比例，四舍五入取整）。
    //   公式是源表事实，数据里原样保留；但**前台表格、比对、导出**一律换算成钱数，
    //   不能让物价人员看到「ROUND(G42*0.2,0)」这种机器文本。
    //   主项定位规则：加收项编码 = 主项编码的前 12 位 + 3 位序号（主项为 000）。
    //
    //   第二种形态（2026-10-27 补）：**价格列留空、比例写在「计价说明」里**。
    //   25 号文「一般治疗」的 49 条「-儿童（加收）」就是这种：源表 G/H/I 三档全空，
    //   计价说明写「加收比例20%。」。只认 ROUND 的话这些行会一直显示「—」，
    //   物价人员会以为漏维护。所以求值时再兜一层：价格为空 → 去计价说明扒比例。
    // ==================================================================

    /// <summary>编码 → 条目（懒构建；随 <see cref="InvalidateCompare"/> 失效）。</summary>
    private Dictionary<string, DezhouPriceItem> _codeMap;

    private Dictionary<string, DezhouPriceItem> CodeMap()
    {
        if (_codeMap != null) return _codeMap;
        var d = new Dictionary<string, DezhouPriceItem>(StringComparer.Ordinal);
        foreach (var it in Items)
        {
            var k = (it.Code ?? "").Trim();
            if (k.Length > 0) d[k] = it;
        }
        return _codeMap = d;
    }

    /// <summary>加收项对应的主项（编码前 12 位 + 000）；找不到或自身即主项时返回 null。</summary>
    public DezhouPriceItem BaseItemOf(DezhouPriceItem it)
    {
        var code = (it?.Code ?? "").Trim();
        if (code.Length < 15) return null;
        var baseCode = code[..12] + "000";
        if (string.Equals(baseCode, code, StringComparison.Ordinal)) return null;
        return CodeMap().TryGetValue(baseCode, out var b) && !ReferenceEquals(b, it) ? b : null;
    }

    /// <summary>
    /// 这一档价格是否「待换算」：未求值的 Excel 公式串，或空价但计价说明给了加收比例。
    /// （空价又没比例 = 本来就没价，不算待换算。）
    /// </summary>
    public bool IsPending(DezhouPriceItem it, string v)
        => (v ?? "").Trim().Length == 0 ? NoteRatio(it) != null : IsFormulaPrice(v);

    /// <summary>
    /// 价格串是否还是未求值的 Excel 公式（展示层用来决定是否加「已换算」提示）。
    /// 只认公式形态；空价加收项见 <see cref="IsPending"/>。
    /// </summary>
    public static bool IsFormulaPrice(string v)
        => (v ?? "").Trim().StartsWith("ROUND", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 从「计价说明」里扒加收比例，返回小数系数（20% → 0.2）；没有则 null。
    /// 25 号文一般治疗类的加收项价格列是空的，比例只写在这一栏。
    /// </summary>
    public static double? NoteRatio(DezhouPriceItem it)
    {
        var note = (it?.Usage ?? "").Trim();
        if (note.Length == 0) return null;
        var m = NoteRatioRx.Match(note);
        if (!m.Success) return null;
        return double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var pct) ? pct / 100.0 : null;
    }

    private static readonly System.Text.RegularExpressions.Regex NoteRatioRx =
        new(@"加收比例\s*(\d+(?:\.\d+)?)\s*[%％]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>一档价格列名（price3/price2/price1）→ 条目上的原始串。</summary>
    private static string RawPrice(DezhouPriceItem it, string col) => col switch
    {
        "price3" => it?.Price3, "price2" => it?.Price2, "price1" => it?.Price1, _ => null,
    };

    /// <summary>
    /// 把一档价格串求值：数字原样返回；<c>ROUND(&lt;列&gt;&lt;行&gt;*k,0)</c> 按主项同档价 × k 计算。
    /// 形态不认识或主项取不到数时原样返回（调用方用 <see cref="PriceOf"/> 做展示兜底）。
    /// </summary>
    public string ResolvePrice(DezhouPriceItem it, string col) => ResolvePrice(it, col, 0);

    private string ResolvePrice(DezhouPriceItem it, string col, int depth)
    {
        var raw = RawPrice(it, col);
        var v = (raw ?? "").Trim();

        // 形态二：价格列留空、比例在计价说明里（25 号文一般治疗类儿童加收）
        if (v.Length == 0) return ResolveByNote(it, col, depth) ?? "";

        if (!IsFormulaPrice(v)) return raw;                      // 纯数字 / 文本原样
        if (depth >= 3) return raw;                              // 防异常数据自引用
        var m = FormulaRx.Match(v);
        if (!m.Success) return raw;
        var colName = m.Groups[1].Value.ToUpperInvariant() switch
        {
            "G" => "price3", "H" => "price2", "I" => "price1", _ => null,
        };
        if (colName is null) return raw;
        if (!double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var factor)) return raw;
        return TimesBase(it, colName, factor, depth) ?? raw;
    }

    /// <summary>
    /// 空价加收项：按计价说明里的「加收比例 N%」× 主项同档价算。
    /// 不是加收项、说明里没有比例、或主项取不到价 —— 一律返回 null（原样留空，显示「—」）。
    /// </summary>
    private string ResolveByNote(DezhouPriceItem it, string col, int depth)
    {
        if (depth > 3) return null;
        var ratio = NoteRatio(it);
        if (ratio is null) return null;
        return TimesBase(it, col, ratio.Value, depth);
    }

    /// <summary>
    /// 主项同档价 × 系数。
    /// <para>正常情况四舍五入取整（与源表 <c>ROUND(...,0)</c> 和医保价格「元」口径一致）。</para>
    /// <para>⚠ 取整后会变成 0 的必须保留小数：低单价项目的加收额不足 1 元（如胃肠灌注费 2 元 × 20%
    /// = 0.4），照整数取整等于把加收直接抹平，表格里出现「+0 元」比空着更误导。
    /// 这种时候给两位小数（0.4 / 0.2），既不丢钱也不会凭空造出 1 元的加收额。</para>
    /// 主项缺失或本身算不出时返回 null。
    /// </summary>
    private string TimesBase(DezhouPriceItem it, string col, double factor, int depth)
    {
        var b = BaseItemOf(it);
        if (b is null) return null;
        var bBase = ResolvePrice(b, col, depth + 1);             // 主项本身也可能是公式
        if (!double.TryParse((bBase ?? "").Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var bv)) return null;
        var raw = bv * factor;
        var rounded = (int)Math.Round(raw, MidpointRounding.AwayFromZero);
        if (rounded > 0 || raw <= 0)
            return rounded.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Math.Round(raw, 2, MidpointRounding.AwayFromZero)
            .ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static readonly System.Text.RegularExpressions.Regex FormulaRx =
        new(@"^ROUND\(\s*([A-Za-z])\d+\s*\*\s*([\d.]+)\s*,\s*\d+\s*\)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>三档价全部求值（数字原样、公式算值、文本保留）。</summary>
    public (string P3, string P2, string P1) ResolvePrices(DezhouPriceItem it)
        => (ResolvePrice(it, "price3"), ResolvePrice(it, "price2"), ResolvePrice(it, "price1"));

    /// <summary>
    /// 展示用取价：列名 price3/price2/price1（其余列不处理）。公式换算后仍是公式 = 主项缺失，
    /// 返回「—」而不是把公式甩给用户。
    /// </summary>
    public string PriceOf(DezhouPriceItem it, string key)
    {
        if (key is not ("price3" or "price2" or "price1")) return AdminService.DezhouField(it, key);
        var v = (ResolvePrice(it, key) ?? "").Trim();
        // 本该有价（有公式 / 有加收比例）却算不出 → 显示「—」提示核对主项；
        // 本来就没价（既无公式又无比例）→ 原样留空，别给「—」制造「漏维护」的错觉。
        if (v.Length == 0) return IsPending(it, v) ? "—" : "";
        return IsFormulaPrice(v) ? "—" : v;
    }

    /// <summary>
    /// 公式价 / 空价加收项的悬停说明（「主项 × 比例 = 金额」）；普通数字价返回 null（不加气泡）。
    /// </summary>
    public string PriceNote(DezhouPriceItem it, string key)
    {
        var raw = (RawPrice(it, key) ?? "").Trim();
        var byNote = raw.Length == 0 && NoteRatio(it) != null;    // 形态二：比例写在计价说明
        if (!byNote && !IsFormulaPrice(raw)) return null;

        var b = BaseItemOf(it);
        var resolved = ResolvePrice(it, key);
        var label = key switch { "price3" => "三级价", "price2" => "二级价", _ => "一级价" };

        if (byNote)
        {
            var pct0 = (NoteRatio(it)!.Value * 100)
                .ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "%";
            if (b is null || IsPending(it, resolved))
                return $"加收项：计价说明写「加收比例{pct0}」，但未能匹配主项（{it?.Code} 前 12 位 + 000）的价格";
            return $"加收项：主项 {b.Code} 的{label} {RawPrice(b, key)} × {pct0} → {resolved}"
                 + "（按计价说明的加收比例自动换算）";
        }

        var m = FormulaRx.Match(raw);
        if (b is null || IsPending(it, resolved))
            return $"加收项：原表公式 {raw}，未能匹配主项（{it?.Code} 前 12 位 + 000）的价格";
        var factor = m.Success ? m.Groups[2].Value : "?";
        var pct = double.TryParse(factor, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var f0)
            ? (f0 * 100).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "%"
            : factor;
        var baseVal = RawPrice(b, key) ?? "";
        return $"加收项：主项 {b.Code} 的{label} {baseVal} × {pct} → {resolved}"
             + $"（原表公式 {raw}，已自动换算）";
    }

    /// <summary>本批是否含公式价（导出、自检用来提示「已换算为金额」）。</summary>
    public bool HasFormulaPrices
    {
        get
        {
            foreach (var it in Items)
                if (IsFormulaPrice(it.Price3) || IsFormulaPrice(it.Price2) || IsFormulaPrice(it.Price1))
                    return true;
            return false;
        }
    }

    // ==================================================================
    // 作废项目对照（各批次映射关系表）
    // ==================================================================

    /// <summary>作废对照行筛选（doc/category 为空表示不限）；关键词命中编码前缀与各文本列，支持拼音。</summary>
    public static bool CancelMatches(DezhouCancelItem it, string doc, string category, string q)
    {
        if (!string.IsNullOrEmpty(doc) && it.Doc != doc) return false;
        if (!string.IsNullOrEmpty(category) && it.Category != category) return false;

        var kw = (q ?? "").Trim().ToUpperInvariant();
        if (kw.Length == 0) return true;
        // 编码后缀是小写字母（310701001g / 311400012a），关键词统一转大写后
        // 用 Ordinal 前缀比对会一条都搜不到 —— 编码前缀一律忽略大小写。
        if ((it.OldCode ?? "").StartsWith(kw, StringComparison.OrdinalIgnoreCase)) return true;
        if ((it.NewCode ?? "").StartsWith(kw, StringComparison.OrdinalIgnoreCase)) return true;
        return new[] { it.OldName, it.NewName, it.Category, it.Doc, it.Note }
                   .Any(v => (v ?? "").Contains(kw, StringComparison.OrdinalIgnoreCase))
               || PinyinHelper.PyMatchAny(new[] { it.OldName, it.NewName, it.Category }, kw);
    }

    /// <summary>作废对照行数（按筛选），供页面状态栏与分页。</summary>
    public List<DezhouCancelItem> CancelRows(string doc, string category, string q)
        => Cancels.Where(it => CancelMatches(it, doc, category, q)).ToList();

    /// <summary>作废对照的类别列表（doc 为空 = 全部批次，保序去重）。</summary>
    public List<string> CancelCategories(string doc)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var it in Cancels)
        {
            if (doc.Length > 0 && it.Doc != doc) continue;
            var v = (it.Category ?? "").Trim();
            if (v.Length == 0 || !seen.Add(v)) continue;
            list.Add(v);
        }
        return list;
    }

    /// <summary>作废对照统计：总行数 / 有新项目替代 / 直接废止无替代 / 批次数 / 类别数 / 其中保留（划线不作废）。</summary>
    public (int Total, int Mapped, int Dead, int Batches, int Categories, int Kept) CancelStat()
    {
        var docs = new HashSet<string>(StringComparer.Ordinal);
        var cats = new HashSet<string>(StringComparer.Ordinal);
        var mapped = 0;
        var kept = 0;
        foreach (var it in Cancels)
        {
            if (!string.IsNullOrEmpty(it.Doc)) docs.Add(it.Doc);
            if (!string.IsNullOrEmpty(it.Category)) cats.Add(it.Category);
            if (!string.IsNullOrEmpty(it.NewCode)) mapped++;
            if (it.Keep) kept++;
        }
        return (Cancels.Count, mapped, Cancels.Count - mapped, docs.Count, cats.Count, kept);
    }

    // ==================================================================
    // 旧版物价（45号文对码表）× 作废对照联查
    // ==================================================================

    /// <summary>旧码 → 现行标准串（「编码 名称」，多对多用「；」连接；懒构建一次）。</summary>
    private Dictionary<string, string> CancelIndex()
    {
        if (_cancelIndex != null) return _cancelIndex;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in Cancels)
        {
            // Pending = 征求意见阶段的「拟作废」对照，尚未真作废，不能参与判定。
            // Keep = 通知废止表里划横线的保留项目（如 25 号文 311400031 / PCAA0101），
            // 原文划线即不作废，同样不参与判定，否则旧版物价会被误标「已作废」。
            // 主库历史数据没有 pending / keep 键，反序列化恒为 false，此处行为与既有完全一致。
            if (c.Pending || c.Keep) continue;
            if (string.IsNullOrEmpty(c.OldCode) || string.IsNullOrEmpty(c.NewCode)) continue;
            var std = $"{c.NewCode} {FlattenName(c.NewName)}";
            if (map.TryGetValue(c.OldCode, out var prev))
            {
                if (!prev.Contains(std, StringComparison.Ordinal)) map[c.OldCode] = prev + "；" + std;
            }
            else map[c.OldCode] = std;
        }
        _cancelIndex = map;
        return map;
    }

    private static string FlattenName(string v) => (v ?? "").Replace("\n", "；").Replace("\r", "");

    /// <summary>已作废旧码集合（懒构建；含「直接废止·无替代项」的行）。</summary>
    private HashSet<string> CanceledSet()
    {
        if (_canceledSet != null) return _canceledSet;
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in Cancels)
        {
            if (c.Pending || c.Keep) continue;   // 拟作废 / 划线保留都不算作废
            var code = (c.OldCode ?? "").Trim();
            if (code.Length == 0) continue;
            set.Add(code);                     // 有没有 NewCode 都是作废
        }
        _canceledSet = set;
        return set;
    }

    /// <summary>某旧码是否已作废（出现在任一批次映射关系表的作废侧，含直接废止）。</summary>
    public bool LegacyCanceled(string code) => CanceledSet().Contains((code ?? "").Trim());

    /// <summary>
    /// 某旧版物价行是否已作废（整行判定：编码直接命中，或同基础码同名补判）。
    /// <para>映射关系表引的是省对码，与本地对码表的字母后缀可能错位（<c>311400012</c> ↔ <c>311400012a</c>），
    /// 只按编码比对会漏判 —— 这类行在旧版物价里挂着「现行」，正是误标来源。同基础码 + 同名才补判，
    /// 跨码同名（如不同医疗机构级别的「知名专家」）不会误伤。</para>
    /// </summary>
    public bool LegacyCanceled(DezhouLegacyItem it)
        => FamilyCancelOf(it) != null || LegacyCanceled(it?.Code);

    /// <summary>同族补判：编码没直接命中时，按（基础码, 归一化名称）回查作废对照；未命中返回 null。</summary>
    private DezhouCancelItem FamilyCancelOf(DezhouLegacyItem it)
    {
        var code = (it?.Code ?? "").Trim();
        var name = NormText(it?.Name);
        if (code.Length == 0 || name.Length == 0) return null;
        if (CanceledSet().Contains(code)) return null;   // 直接命中走主判定，不走补判
        return CancelFamily().TryGetValue((BaseCode(code), name), out var c) ? c : null;
    }

    /// <summary>同族补判索引（懒构建；只收非拟作废、非划线保留的行）。</summary>
    private Dictionary<(string, string), DezhouCancelItem> CancelFamily()
    {
        if (_cancelFamily != null) return _cancelFamily;
        var map = new Dictionary<(string, string), DezhouCancelItem>();
        foreach (var c in Cancels)
        {
            if (c.Pending || c.Keep) continue;           // 拟作废 / 划线保留都不参与判定
            var code = (c.OldCode ?? "").Trim();
            var name = NormText(c.OldName);
            if (code.Length == 0 || name.Length == 0) continue;
            var key = (BaseCode(code), name);
            if (!map.ContainsKey(key)) map[key] = c;
        }
        _cancelFamily = map;
        return map;
    }

    /// <summary>基础码：剥掉对码表为区分同名条目追加的小写字母后缀（<c>311400012a</c> → <c>311400012</c>）。</summary>
    private static string BaseCode(string code)
    {
        var c = (code ?? "").Trim();
        while (c.Length > 0 && c[^1] >= 'a' && c[^1] <= 'z') c = c[..^1];
        return c;
    }

    /// <summary>
    /// 名称归一化：NFKC（全角括号 / 全角字母转半角）+ 只留字母数字与汉字。
    /// <para>映射表用全角括号、对码表用半角括号是常态（<c>普通透视（数字化加收）</c> ↔
    /// <c>普通透视(数字化加收)</c>），标点差异不该挡住同一条目；只去空白不够。</para>
    /// </summary>
    private static string NormText(string v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        var s = v.Normalize(System.Text.NormalizationForm.FormKC);
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);   // 汉字归 Letter，保留
        return sb.ToString();
    }

    /// <summary>某旧码的现行标准（对应新项目「编码 名称」串；未作废或无替代返回空串）。</summary>
    public string LegacyCurrentStd(string code)
        => CancelIndex().TryGetValue((code ?? "").Trim(), out var s) ? s : "";

    /// <summary>某旧版物价行的现行标准（编码直接命中或同族补判；未作废或无替代返回空串）。</summary>
    public string LegacyCurrentStd(DezhouLegacyItem it)
    {
        var s = LegacyCurrentStd(it?.Code);
        if (s.Length > 0) return s;
        var c = FamilyCancelOf(it);
        return c == null ? "" : LegacyCurrentStd(c.OldCode);
    }

    /// <summary>旧码 → 对应新项目明细（懒构建；同一新码去重保序）。</summary>
    private Dictionary<string, List<DezhouCancelItem>> _cancelMap;

    /// <summary>某旧码对应的现行新项目明细（前台「现行标准」列的链接数据源；未作废或无替代返回空列表）。</summary>
    public List<DezhouCancelItem> CancelOf(string oldCode)
    {
        if (_cancelMap == null)
        {
            var map = new Dictionary<string, List<DezhouCancelItem>>(StringComparer.Ordinal);
            foreach (var c in Cancels)
            {
                if (c.Pending || c.Keep) continue;   // 拟作废 / 划线保留都不参与「现行标准」联查
                if (string.IsNullOrEmpty(c.OldCode) || string.IsNullOrEmpty(c.NewCode)) continue;
                if (!map.TryGetValue(c.OldCode, out var list))
                    map[c.OldCode] = list = new List<DezhouCancelItem>();
                // 同一旧码→同一新码（不同批次重复公布）只保留一条
                if (!list.Any(x => x.NewCode == c.NewCode)) list.Add(c);
            }
            _cancelMap = map;
        }
        return _cancelMap.TryGetValue((oldCode ?? "").Trim(), out var l) ? l : new List<DezhouCancelItem>();
    }

    /// <summary>某旧版物价行对应的现行新项目明细（编码直接命中或同族补判；未作废或无替代返回空列表）。</summary>
    public List<DezhouCancelItem> CancelOf(DezhouLegacyItem it)
    {
        var direct = CancelOf(it?.Code);
        if (direct.Count > 0) return direct;
        var c = FamilyCancelOf(it);
        return c == null ? new List<DezhouCancelItem>() : CancelOf(c.OldCode);
    }

    /// <summary>旧版物价行筛选（category/status 为空表示不限）；status："dead" 已作废 / "live" 现行 / "" 全部。</summary>
    public bool LegacyMatches(DezhouLegacyItem it, string category, string status, string q)
    {
        if (!string.IsNullOrEmpty(category) && it.Category != category) return false;

        // 整行判定：编码直接命中或同基础码同名补判（见 LegacyCanceled(DezhouLegacyItem)）
        var canceled = LegacyCanceled(it);
        switch (status ?? "")
        {
            case "dead" when !canceled:
            case "live" when canceled:
                return false;
        }

        var kw = (q ?? "").Trim().ToUpperInvariant();
        if (kw.Length == 0) return true;
        // 同上：旧码后缀小写，前缀比对必须忽略大小写
        if ((it.Code ?? "").StartsWith(kw, StringComparison.OrdinalIgnoreCase)) return true;
        if ((it.NatCode ?? "").StartsWith(kw, StringComparison.OrdinalIgnoreCase)) return true;
        return new[] { it.Name, it.NatName, it.Category, it.DwCode, it.Content, it.Note, it.LimitPay }
                   .Any(v => (v ?? "").Contains(kw, StringComparison.OrdinalIgnoreCase))
               || PinyinHelper.PyMatchAny(new[] { it.Name, it.NatName, it.Category }, kw);
    }

    /// <summary>旧版物价行数（按筛选）。</summary>
    public List<DezhouLegacyItem> LegacyRows(string category, string status, string q)
        => Legacy.Where(it => LegacyMatches(it, category, status, q)).ToList();

    /// <summary>旧版物价的归集口径列表（保序去重）。</summary>
    public List<string> LegacyCategories()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var it in Legacy)
        {
            var v = (it.Category ?? "").Trim();
            if (v.Length == 0 || !seen.Add(v)) continue;
            list.Add(v);
        }
        return list;
    }

    /// <summary>旧版物价统计：总行数 / 已作废 / 现行 / 归集口径数。</summary>
    public (int Total, int Dead, int Live, int Categories) LegacyStat()
    {
        var cats = new HashSet<string>(StringComparer.Ordinal);
        var dead = 0;
        foreach (var it in Legacy)
        {
            if (!string.IsNullOrEmpty(it.Category)) cats.Add(it.Category);
            if (LegacyCanceled(it)) dead++;
        }
        return (Legacy.Count, dead, Legacy.Count - dead, cats.Count);
    }

    // ==================================================================
    // 新旧物价比对（现行项目 ↔ 旧版物价，经作废对照反查旧码）
    // ==================================================================

    private List<DezhouDraftCompareRow> _compare;
    private Dictionary<string, DezhouLegacyItem> _legacyByCode;
    /// <summary>新编码 → 旧码列表（作废对照反查，一对多保留全部；拟作废 Pending=true 的行不参与）。</summary>
    private Dictionary<string, List<string>> _oldByNew;

    /// <summary>
    /// 全部比对行（懒构建，按新编码排序；一个新项目对应多个旧项目时一行一个旧项目）。
    ///
    /// <para>取数路径：现行项目编码 → 作废对照反查旧码 → 旧版物价（45 号对码表）取旧价。
    /// 旧码命中旧版物价 = <c>legacy</c>（可比价）；只在作废对照里出现 = <c>cancel</c>（无旧价）；
    /// 查无任何旧码 = <c>none</c>（纯新增，规范整合时没有对应旧项目）。</para>
    /// </summary>
    public List<DezhouDraftCompareRow> CompareRows()
    {
        if (_compare != null) return _compare;
        BuildCompareIndex();

        var rows = new List<DezhouDraftCompareRow>();
        foreach (var it in Items)
        {
            var newCode = (it.Code ?? "").Trim();
            if (newCode.Length == 0) continue;
            var olds = _oldByNew.TryGetValue(newCode, out var l) ? l : null;

            var row = new DezhouDraftCompareRow
            {
                NewCode = newCode,
                NewName = it.Name ?? "",
                Category = it.Category ?? "",
                // 加收项原表是 ROUND(主项*0.2,0)，先换算成钱，否则差额与涨幅一律算不出
                NewPrice3 = ResolvePrice(it, "price3"),
                NewPrice2 = ResolvePrice(it, "price2"),
                NewPrice1 = ResolvePrice(it, "price1"),
                NewKind = string.IsNullOrEmpty(it.Kind) ? "main" : it.Kind,
                OldSource = "none",
            };

            if (olds == null)
            {
                rows.Add(row);   // 纯新增：作废对照里查无旧项目
                continue;
            }
            foreach (var oc in olds)
            {
                var r = new DezhouDraftCompareRow
                {
                    NewCode = row.NewCode, NewName = row.NewName, Category = row.Category,
                    NewPrice3 = row.NewPrice3, NewPrice2 = row.NewPrice2, NewPrice1 = row.NewPrice1,
                    NewKind = row.NewKind,
                    OldCode = oc,
                    OldSource = "cancel",   // 先按「仅作废对照」，命中旧版物价再升级
                };
                if (_legacyByCode.TryGetValue(oc, out var lg))
                {
                    r.OldSource = "legacy";
                    r.OldName = lg.Name ?? "";
                    r.OldCategory = lg.Category ?? "";
                    r.OldPrice3 = lg.Price3 ?? "";
                    r.OldPrice2 = lg.Price2 ?? "";
                    r.OldPrice1 = lg.Price1 ?? "";
                    r.OldInsurance = lg.Insurance ?? "";
                    r.OldSelfPay = lg.SelfPay ?? "";
                }
                DezhouDraftStore.FillDelta(r);   // 差额与涨幅（价格解析不出时留 null）
                rows.Add(r);
            }
        }

        rows.Sort((a, b) => string.CompareOrdinal(a.NewCode, b.NewCode));
        _compare = rows;
        return _compare;
    }

    /// <summary>比对反查索引：旧码 → 旧版物价条目；新码 → 旧码列表（作废对照里 NewCode 非空且非拟作废的行）。</summary>
    private void BuildCompareIndex()
    {
        if (_legacyByCode != null && _oldByNew != null) return;
        var lg = new Dictionary<string, DezhouLegacyItem>(StringComparer.Ordinal);
        foreach (var li in Legacy)
        {
            var k = (li.Code ?? "").Trim();
            if (k.Length > 0 && !lg.ContainsKey(k)) lg[k] = li;   // 同码重复只留首条
        }
        var ob = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var c in Cancels)
        {
            if (c.Pending) continue;   // 征求意见的拟作废行还不是旧价基准
            // Keep（划线保留）刻意不跳过：原文把这条写进了映射关系表，新旧价并列比对
            // 仍是有效参考（旧项目没作废、还在执行，正好能看出新旧两套价的差）。
            var oc = (c.OldCode ?? "").Trim();
            var nc = (c.NewCode ?? "").Trim();
            if (oc.Length == 0 || nc.Length == 0) continue;
            if (!ob.TryGetValue(nc, out var l)) ob[nc] = l = new List<string>();
            if (!l.Contains(oc, StringComparer.Ordinal)) l.Add(oc);
        }
        _legacyByCode = lg;
        _oldByNew = ob;
    }

    /// <summary>比对行筛选：类别 + 关键词（两侧编码 / 名称 / 类别）+ 范围（"" 全部 / priced / cancel / none）。</summary>
    public static bool CompareMatches(DezhouDraftCompareRow r, string category, string q, string scope)
    {
        if (!string.IsNullOrEmpty(category) &&
            !string.Equals((r.Category ?? "").Trim(), category.Trim(), StringComparison.Ordinal))
            return false;
        if (scope == "priced" && r.OldSource != "legacy") return false;
        if (scope == "cancel" && r.OldSource != "cancel") return false;
        if (scope == "none" && r.OldSource != "none") return false;
        if (string.IsNullOrWhiteSpace(q)) return true;
        var needle = q.Trim();
        return Contains(r.NewCode, needle) || Contains(r.NewName, needle)
            || Contains(r.OldCode, needle) || Contains(r.OldName, needle) || Contains(r.Category, needle);

        static bool Contains(string f, string n)
            => !string.IsNullOrEmpty(f) && f.Contains(n, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>按筛选条件列出比对行。</summary>
    public List<DezhouDraftCompareRow> ListCompare(string category, string q, string scope)
        => CompareRows().Where(r => CompareMatches(r, category, q, scope)).ToList();

    /// <summary>比对统计：总行 / 有旧价可比 / 仅作废对照 / 纯新增 / 降 / 涨 / 平（均按三级价）。</summary>
    public (int Total, int Priced, int CancelOnly, int BrandNew, int Down, int Up, int Flat) CompareStat()
        => CompareStat("3");

    /// <summary>
    /// 比对统计（按指定价格档位算涨 / 跌 / 平）。
    /// <para>level：<c>3</c> 三级（默认，等价于旧行为）/ <c>2</c> 二级 / <c>1</c> 一级 / <c>eq</c> 同价（取三级）。
    /// 涨跌必须与表格当前展示的那一档一致 —— 切到二级价看，统计条还在数三级价的涨跌，就是两个口径打架。</para>
    /// </summary>
    public (int Total, int Priced, int CancelOnly, int BrandNew, int Down, int Up, int Flat) CompareStat(string level)
    {
        var all = CompareRows();
        var priced = all.Where(r => r.OldSource == "legacy").ToList();
        var deltas = priced.Select(r => PickDelta(r, level)).ToList();
        return (all.Count,
                priced.Count,
                all.Count(r => r.OldSource == "cancel"),
                all.Count(r => r.OldSource == "none"),
                deltas.Count(d => d is < 0),
                deltas.Count(d => d is > 0),
                deltas.Count(d => d is 0));
    }

    /// <summary>按档位取差额：3/eq → Delta3，2 → Delta2，1 → Delta1；未知档位按三级。</summary>
    internal static double? PickDelta(DezhouDraftCompareRow r, string level) => level switch
    {
        "2" => r.Delta2,
        "1" => r.Delta1,
        _ => r.Delta3,
    };

    /// <summary>比对 / 编码索引随数据变化失效（Load / ImportRaw / ImportCancels / ImportLegacy / Recompute 五处调用）。</summary>
    private void InvalidateCompare()
    {
        _compare = null;
        _legacyByCode = null;
        _oldByNew = null;
        _codeMap = null;      // 加收项公式求值靠它定位主项，条目一改必须重建
    }

    // ==================================================================
    // 后台维护（增删改 + 统计重算）
    // ==================================================================

    /// <summary>维护/编辑框与 Excel 模板共用的列结构（顺序即界面与表头顺序）。</summary>
    public static readonly List<ColumnDef> Columns = new()
    {
        new ColumnDef { Key = "doc", Label = "落地文号", Width = 150 },
        new ColumnDef { Key = "exec", Label = "执行日期", Width = 96, Mono = true },
        new ColumnDef { Key = "category", Label = "类别", Width = 124 },
        new ColumnDef { Key = "seq", Label = "原表序号", Width = 72, Mono = true },
        new ColumnDef { Key = "code", Label = "项目编码", Width = 152, Mono = true },
        new ColumnDef { Key = "kind", Label = "项目性质（main/add/ext）", Width = 130 },
        new ColumnDef { Key = "name", Label = "项目名称", Width = 200 },
        new ColumnDef { Key = "serviceOutput", Label = "服务产出", Width = 280 },
        new ColumnDef { Key = "priceComposition", Label = "价格构成", Width = 280 },
        new ColumnDef { Key = "unit", Label = "计价单位", Width = 78 },
        new ColumnDef { Key = "price3", Label = "三级价格", Width = 84, Mono = true },
        new ColumnDef { Key = "price2", Label = "二级价格", Width = 84, Mono = true },
        new ColumnDef { Key = "price1", Label = "一级价格", Width = 84, Mono = true },
        // 仅「一般治疗」类有值（注射 / 清创缝合 / 换药 / 导尿 / 膀胱冲洗），其余批次空着
        new ColumnDef { Key = "deduct", Label = "扣减基本物耗价格", Width = 118, Mono = true },
        new ColumnDef { Key = "insurance", Label = "医保支付（是/否）", Width = 104 },
        new ColumnDef { Key = "selfPay", Label = "自付比例", Width = 84, Mono = true },
        new ColumnDef { Key = "note", Label = "备注 / 限定支付范围", Width = 240 },
        new ColumnDef { Key = "usage", Label = "计价说明", Width = 280 },
    };

    private static readonly string[] RowKeys =
    {
        "doc", "exec", "category", "seq", "code", "kind", "name", "serviceOutput",
        "priceComposition", "unit", "price3", "price2", "price1", "deduct", "insurance",
        "selfPay", "note", "usage",
    };

    /// <summary>项目性质取值（main=主项目 / add=加收项 / ext=扩展项）。</summary>
    public static readonly string[] Kinds = { "main", "add", "ext" };

    public static string KindLabel(string kind) => kind switch
    {
        "add" => "加收项",
        "ext" => "扩展项",
        _ => "主项目",
    };

    public int NextId()
    {
        var max = 0;
        foreach (var i in Items) if (i.Id > max) max = i.Id;
        return max + 1;
    }

    public ParseResult AddRow(Dictionary<string, string> flat)
    {
        var r = NormalizeRow(flat);
        if (!r.Ok) return r;
        var it = (DezhouPriceItem)r.Row;
        it.Id = NextId();
        Items.Add(it);
        return new ParseResult { Row = it.Id };
    }

    public ParseResult UpdateRow(string id, Dictionary<string, string> flat)
    {
        var cur = Items.FirstOrDefault(i => i.Id.ToString() == (id ?? "").Trim());
        if (cur == null) return new ParseResult { Error = $"未找到记录 #{id}" };
        var r = NormalizeRow(flat);
        if (!r.Ok) return r;
        var n = (DezhouPriceItem)r.Row;
        n.Id = cur.Id;
        Items[Items.IndexOf(cur)] = n;
        return new ParseResult { Row = n.Id };
    }

    public ParseResult RemoveRow(string id)
    {
        var cur = Items.FirstOrDefault(i => i.Id.ToString() == (id ?? "").Trim());
        if (cur == null) return new ParseResult { Error = $"未找到记录 #{id}" };
        Items.Remove(cur);
        return new ParseResult { Row = true };
    }

    /// <summary>同一文号内是否已存在该编码（编辑时用 exceptId 排除自身）；doc 为空 = 查全部批次。</summary>
    public bool CodeExists(string code, int exceptId, string doc = null)
    {
        var c = (code ?? "").Trim();
        return Items.Any(i => i.Id != exceptId && i.Code == c
                              && (string.IsNullOrEmpty(doc) || i.Doc == doc));
    }

    private static ParseResult NormalizeRow(Dictionary<string, string> flat)
    {
        var it = new DezhouPriceItem();
        foreach (var k in RowKeys)
        {
            var v = flat != null && flat.TryGetValue(k, out var x) ? (x ?? "").Trim() : "";
            switch (k)
            {
                case "doc": it.Doc = v; break;
                case "exec": it.Exec = v; break;
                case "category": it.Category = v; break;
                case "seq": it.Seq = v; break;
                case "code": it.Code = v; break;
                case "kind": it.Kind = v.Length == 0 ? "main" : v.ToLowerInvariant(); break;
                case "name": it.Name = v; break;
                case "serviceOutput": it.ServiceOutput = v; break;
                case "priceComposition": it.PriceComposition = v; break;
                case "unit": it.Unit = v; break;
                case "price3": it.Price3 = v; break;
                case "price2": it.Price2 = v; break;
                case "price1": it.Price1 = v; break;
                case "deduct": it.Deduct = v; break;
                case "insurance": it.Insurance = v; break;
                case "selfPay": it.SelfPay = v; break;
                case "note": it.Note = v; break;
                case "usage": it.Usage = v; break;
            }
        }
        if (it.Name.Length == 0) return new ParseResult { Error = "项目名称不能为空" };
        if (!System.Text.RegularExpressions.Regex.IsMatch(it.Code, @"^\d{11,15}[A-Za-z]?$"))
            return new ParseResult { Error = $"项目编码需为 11–15 位数字（美容整形类可带 1 位字母后缀），当前「{it.Code}」" };
        if (!Kinds.Contains(it.Kind))
            return new ParseResult { Error = $"项目性质需为 main / add / ext，当前「{it.Kind}」" };
        return new ParseResult { Row = it };
    }

    /// <summary>
    /// 重算统计：总数 + 各批次的行数/主项/加收/扩展/类别明细。
    /// 批次顺序与既有元数据（省级文号、涉及类别、官方公布数）保留，新文号按出现顺序补建。
    /// </summary>
    public void Recompute()
    {
        Meta ??= new DezhouPriceMeta();
        Meta.Total = Items.Count;
        InvalidateCompare();   // 后台改过条目（价格/编码），比对缓存必须重建

        // 类别使用说明是「类别级」的人工/抽取产物，重算统计时必须原样保留（按文号 + 类别名定位）
        var keepUsage = UsageMap();

        var byDoc = Items.GroupBy(i => (i.Doc ?? "").Trim()).ToDictionary(g => g.Key, g => g.ToList());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var batches = new List<DezhouBatch>();

        foreach (var b in Meta.Batches ?? new List<DezhouBatch>())
        {
            var key = (b.Doc ?? "").Trim();
            if (key.Length == 0 || !seen.Add(key)) continue;
            var list = byDoc.TryGetValue(key, out var l) ? l : new List<DezhouPriceItem>();
            if (list.Count == 0) continue;   // 空批次不保留：批次列表始终反映实际数据
            FillBatch(b, list, keepUsage);
            batches.Add(b);
        }
        foreach (var kv in byDoc)
        {
            if (kv.Key.Length == 0 || !seen.Add(kv.Key)) continue;
            var b = new DezhouBatch
            {
                Doc = kv.Key,
                Exec = kv.Value.Select(x => x.Exec).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e)) ?? "",
            };
            FillBatch(b, kv.Value, keepUsage);
            batches.Add(b);
        }
        Meta.Batches = batches;
    }

    private static void FillBatch(DezhouBatch b, List<DezhouPriceItem> list,
                                  Dictionary<string, List<string>> keepUsage = null)
    {
        b.Rows = list.Count;
        b.Mains = list.Count(x => x.Kind != "add" && x.Kind != "ext");
        b.Adds = list.Count(x => x.Kind == "add");
        b.Exts = list.Count(x => x.Kind == "ext");
        b.Categories = list
            .GroupBy(x => (x.Category ?? "").Trim())
            .Where(g => g.Key.Length > 0)
            .Select(g => new DezhouCategory
            {
                Name = g.Key,
                Items = g.Count(),
                Main = g.Count(x => x.Kind != "add" && x.Kind != "ext"),
                Add = g.Count(x => x.Kind == "add"),
                Ext = g.Count(x => x.Kind == "ext"),
                Usage = keepUsage != null && keepUsage.TryGetValue(UsageKey(b.Doc, g.Key), out var u)
                    ? u
                    : new List<string>(),
            })
            .ToList();
    }

    /// <summary>改完数据后「保存并重算」。</summary>
    public void SaveWithRecompute()
    {
        Recompute();
        Save();
    }

    // ---------- 候选值（供可编辑下拉使用） ----------

    public List<string> DistinctDocs() => Distinct(i => i.Doc);

    public List<string> DistinctUnits() => Distinct(i => i.Unit);

    public List<string> DistinctCategories(string doc)
        => Distinct(i => i.Category, doc);

    private List<string> Distinct(Func<DezhouPriceItem, string> pick, string doc = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var it in Items)
        {
            if (doc != null && it.Doc != doc) continue;
            var v = (pick(it) ?? "").Trim();
            if (v.Length == 0 || !seen.Add(v)) continue;
            list.Add(v);
        }
        return list;
    }
}
