namespace Dip3Grouper;

/// <summary>征求意见稿「新增项目」列表结果（分页）。</summary>
public sealed class DezhouDraftListResult
{
    public string Label { get; set; } = "新项目征求意见";
    public List<DezhouPriceItem> Items { get; set; } = new();
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

/// <summary>征求意见稿统计（页面顶部黄条与分段徽标用）。</summary>
public sealed class DezhouDraftStat
{
    /// <summary>拟新增项目总数</summary>
    public int Items { get; set; }
    public int Mains { get; set; }
    public int Adds { get; set; }
    public int Exts { get; set; }
    /// <summary>拟作废原项目条数</summary>
    public int Cancels { get; set; }
    /// <summary>映射关系行数</summary>
    public int Maps { get; set; }
    /// <summary>涉及类别数</summary>
    public int Categories { get; set; }
    /// <summary>作废行涉及的既有批次文号</summary>
    public List<string> Refs { get; set; } = new();
    /// <summary>状态：draft / active</summary>
    public string Status { get; set; } = "draft";
    /// <summary>是否存在数据（无数据时前台显示空态）</summary>
    public bool HasData { get; set; }
}

/// <summary>
/// 德州市物价「新项目征求意见」稿存储（<c>data/dezhou-draft.json</c>）。
///
/// <para><b>为什么要独立于主库</b>：本批（消化系统等 9 类）尚在征求意见阶段，价格未生效、
/// 文号未下发、作废范围未最终确定。若直接并入 <see cref="DezhouPriceStore"/> 的
/// <c>data/dezhou-price.json</c>，会同时污染三件事：① 一线按未生效价格计价；
/// ② 旧版物价页会把「拟作废」的旧项目误标为已作废；③ 正式发布后文号变更需要全库改字符串。
/// 因此这里**只读不写的独立文件**，正式发布时再由后台动作显式并入主库。</para>
///
/// <para><b>与主库的字段语义对应</b>：
/// <c>Items</c> → 主库 Items（status 恒 draft）；
/// <c>Cancels</c> → 主库 Cancels（Pending 恒 true，不参与主库作废判定）；
/// <c>Maps</c> → 主库 Cancels 的「映射关系表」原始形态。</para>
/// </summary>
public class DezhouDraftStore
{
    public DezhouDraftMeta Meta { get; private set; } = new();
    public List<DezhouPriceItem> Items { get; private set; } = new();
    /// <summary>拟作废的原价格项目（<see cref="DezhouCancelItem"/> 复用主库模型，Pending 语义由本类单独维护）</summary>
    public List<DezhouCancelItem> Cancels { get; private set; } = new();
    public List<DezhouDraftMapRow> Maps { get; private set; } = new();

    /// <summary>旧码 → 映射关系行（懒构建索引，供详情卡「关联映射」用）</summary>
    private Dictionary<string, List<DezhouDraftMapRow>> _byOld;
    /// <summary>新码 → 映射关系行（懒构建索引，供详情卡「关联映射」用）</summary>
    private Dictionary<string, List<DezhouDraftMapRow>> _byNew;

    public DezhouDraftStore() => Load();

    /// <summary>
    /// 载入征求意见稿。文件不存在时静默留空（前台显示空态），不抛异常——
    /// 未收到征求意见稿的机器不该因此打不开页面。
    /// </summary>
    public void Load()
    {
        try
        {
            var path = AppPaths.DezhouDraftFile;
            if (!File.Exists(path))
            {
                Meta = new DezhouDraftMeta();
                Items = new List<DezhouPriceItem>();
                Cancels = new List<DezhouCancelItem>();
                Maps = new List<DezhouDraftMapRow>();
                _byOld = _byNew = null;
                _compare = null;
                _byCode = null;
                return;
            }
            var d = Js.ReadJsonFile<DezhouDraftFile>(path);
            Meta = d?.Meta ?? new DezhouDraftMeta();
            Items = d?.Items ?? new List<DezhouPriceItem>();
            Cancels = d?.Cancels ?? new List<DezhouCancelItem>();
            Maps = d?.Maps ?? new List<DezhouDraftMapRow>();
            _byOld = _byNew = null;
            _compare = null;
            _byCode = null;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[dezhou-draft] dezhou-draft.json 解析失败，使用空库: {e.Message}");
            Meta = new DezhouDraftMeta();
            Items = new List<DezhouPriceItem>();
            Cancels = new List<DezhouCancelItem>();
            Maps = new List<DezhouDraftMapRow>();
            _compare = null;
            _byCode = null;
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        Js.WriteJsonFile(AppPaths.DezhouDraftFile, new DezhouDraftFile
        {
            Meta = Meta,
            Items = Items,
            Cancels = Cancels,
            Maps = Maps,
        });
    }

    /// <summary>导入解析产物（<c>--import-dezhou-draft</c> 用），落盘并返回各项条数。</summary>
    public (int Items, int Cancels, int Maps) Import(DezhouDraftFile file, string source, string fileName)
    {
        if (file == null) throw new InvalidOperationException("征求意见稿解析产物为空");
        Meta = file.Meta ?? new DezhouDraftMeta();
        Items = file.Items ?? new List<DezhouPriceItem>();
        Cancels = file.Cancels ?? new List<DezhouCancelItem>();
        Maps = file.Maps ?? new List<DezhouDraftMapRow>();
        Meta.Source = source;
        Meta.FileName = fileName;
        Meta.ImportedAt = Js.Iso();
        Meta.Status = "draft";
        // 一致性兜底：导入的每一行都必须带草稿与待作废语义，避免解析侧遗漏
        foreach (var it in Items) it.Status = "draft";
        foreach (var c in Cancels) c.Pending = true;
        foreach (var m in Maps) m.Pending = true;
        _byOld = _byNew = null;
        _compare = null;
        _byCode = null;
        Save();
        return (Items.Count, Cancels.Count, Maps.Count);
    }

    public bool HasData => Items.Count > 0 || Cancels.Count > 0 || Maps.Count > 0;

    /// <summary>各类别名称（按 Items 中出现的顺序去重）。</summary>
    public List<string> Categories()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var it in Items)
        {
            var c = (it.Category ?? "").Trim();
            if (c.Length == 0 || !seen.Add(c)) continue;
            list.Add(c);
        }
        return list;
    }

    // ==================================================================
    // 类别级「使用说明」
    //   与主库 dezhou-price.json 同款结构：meta.batches[].categories[].usage
    //   （逐条对应通知附件表末「使用说明：」的编号条款）。
    //   注意与 DezhouPriceItem.Usage 区分：后者是单条项目的「计价说明」。
    // ==================================================================

    /// <summary>某类别的使用说明条目；找不到返回空集合。</summary>
    public List<string> UsageOf(string category)
    {
        var c = (category ?? "").Trim();
        if (c.Length == 0) return new List<string>();
        foreach (var b in Meta?.Batches ?? new List<DezhouBatch>())
            foreach (var cat in b.Categories ?? new List<DezhouCategory>())
                if (cat.Name == c) return cat.Usage ?? new List<string>();
        return new List<string>();
    }

    /// <summary>全部类别的使用说明（类别, 条目），只含有说明的类别，保序。</summary>
    public List<(string Category, List<string> Usage)> UsageAll()
    {
        var outList = new List<(string, List<string>)>();
        foreach (var b in Meta?.Batches ?? new List<DezhouBatch>())
            foreach (var cat in b.Categories ?? new List<DezhouCategory>())
                if ((cat.Usage ?? new List<string>()).Count > 0)
                    outList.Add((cat.Name, cat.Usage));
        return outList;
    }

    /// <summary>使用说明覆盖情况：有说明的类别数 / 类别总数 / 条目总数。</summary>
    public (int Covered, int Categories, int Entries) UsageStat()
    {
        int covered = 0, cats = 0, n = 0;
        foreach (var b in Meta?.Batches ?? new List<DezhouBatch>())
            foreach (var cat in b.Categories ?? new List<DezhouCategory>())
            {
                cats++;
                var k = (cat.Usage ?? new List<string>()).Count;
                if (k > 0) covered++;
                n += k;
            }
        return (covered, cats, n);
    }

    /// <summary>作废行涉及的既有批次文号（从 <see cref="DezhouCancelItem.Doc"/> 收集，顺序稳定）。</summary>
    public List<string> Refs()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        // 优先用 meta 显式声明的引用（人工维护更准），再补数据里实际出现的
        foreach (var d in Meta?.Refs ?? new List<string>())
        {
            var s = (d ?? "").Trim();
            if (s.Length > 0 && seen.Add(s)) list.Add(s);
        }
        foreach (var c in Cancels)
        {
            var s = (c.Doc ?? "").Trim();
            if (s.Length > 0 && seen.Add(s)) list.Add(s);
        }
        return list;
    }

    public DezhouDraftStat Stat()
    {
        var st = new DezhouDraftStat
        {
            Items = Items.Count,
            Mains = Items.Count(i => i.Kind == "main"),
            Adds = Items.Count(i => i.Kind == "add"),
            Exts = Items.Count(i => i.Kind == "ext"),
            Cancels = Cancels.Count,
            Maps = Maps.Count,
            Categories = Categories().Count,
            Refs = Refs(),
            Status = string.IsNullOrEmpty(Meta?.Status) ? "draft" : Meta.Status,
            HasData = HasData,
        };
        return st;
    }

    // ==================================================================
    // 新增项目：筛选 / 检索 / 分页
    // ==================================================================

    /// <summary>单行匹配：类别 + 性质 + 关键词（编码 / 名称 / 服务产出 / 计价说明）。</summary>
    public static bool ItemMatches(DezhouPriceItem it, string category, string kind, string q)
    {
        if (!string.IsNullOrEmpty(category) && category != "（全部）" &&
            !string.Equals((it.Category ?? "").Trim(), category, StringComparison.Ordinal))
            return false;
        if (!string.IsNullOrEmpty(kind) && kind != "（全部）" && it.Kind != kind) return false;
        return MatchText(q, it.Code, it.Name, it.ServiceOutput, it.Usage, it.Unit, it.PriceComposition);
    }

    /// <summary>新增项目分页列表。</summary>
    public DezhouDraftListResult ListItems(string category, string kind, string q, int page, int pageSize)
    {
        page = Math.Max(1, page);
        pageSize = pageSize <= 0 ? 50 : pageSize;
        var hit = Items.Where(i => ItemMatches(i, category, kind, q)).ToList();
        return new DezhouDraftListResult
        {
            Label = string.IsNullOrWhiteSpace(Meta?.Label) ? "新项目征求意见" : Meta.Label,
            Total = hit.Count,
            Page = page,
            PageSize = pageSize,
            Items = hit.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
        };
    }

    /// <summary>新增项目全量筛选（导出用，不分页）。</summary>
    public List<DezhouPriceItem> AllItems(string category, string kind, string q)
        => Items.Where(i => ItemMatches(i, category, kind, q)).ToList();

    // ==================================================================
    // 加收项公式求值
    //   征求意见稿里「儿童（加收）」等加收项的价格列是 Excel 公式
    //   （形如 ROUND(G42*0.2,0)：主项三级价 × 加收比例，四舍五入取整）。
    //   前台不该显示「公式」二字，要显示算出来的钱数。
    //   主项定位规则：加收项编码 = 主项编码的前 12 位 + 3 位序号（主项为 000）。
    // ==================================================================

    /// <summary>主项缓存（懒构建）：编码 → 条目。</summary>
    private Dictionary<string, DezhouPriceItem> _byCode;

    private Dictionary<string, DezhouPriceItem> ByCode()
    {
        if (_byCode != null) return _byCode;
        var d = new Dictionary<string, DezhouPriceItem>(StringComparer.Ordinal);
        foreach (var it in Items) if (!string.IsNullOrEmpty(it.Code)) d[it.Code] = it;
        return _byCode = d;
    }

    /// <summary>加收项对应的主项（编码前 12 位 + 000）；找不到或自身即主项时返回 null。</summary>
    public DezhouPriceItem BaseItemOf(DezhouPriceItem it)
    {
        var code = it?.Code ?? "";
        if (code.Length < 15) return null;
        var baseCode = code[..12] + "000";
        if (string.Equals(baseCode, code, StringComparison.Ordinal)) return null;
        return ByCode().TryGetValue(baseCode, out var b) && b != it ? b : null;
    }

    /// <summary>把一档价格串求值：数字原样返回；<c>ROUND(&lt;列&gt;&lt;行&gt;*k,0)</c> 按主项同档价 × k 计算。</summary>
    public string ResolvePrice(DezhouPriceItem it, string col)
    {
        var raw = col switch
        {
            "price3" => it?.Price3, "price2" => it?.Price2, "price1" => it?.Price1, _ => null,
        };
        var v = (raw ?? "").Trim();
        if (v.Length == 0) return raw ?? "";
        if (!v.StartsWith("ROUND", StringComparison.OrdinalIgnoreCase)) return raw;   // 纯数字/文本原样
        var m = System.Text.RegularExpressions.Regex.Match(v,
            @"^ROUND\(\s*([A-Za-z])\d+\s*\*\s*([\d.]+)\s*,\s*\d+\s*\)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return raw;                                                  // 形态不认识，保留原文
        var colName = m.Groups[1].Value.ToUpperInvariant() switch
        {
            "G" => "price3", "H" => "price2", "I" => "price1", _ => null,
        };
        if (colName is null) return raw;
        var factor = double.TryParse(m.Groups[2].Value,
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
            out var f) ? f : (double?)null;
        if (factor is null) return raw;
        var b = BaseItemOf(it);
        if (b is null) return raw;
        var bBase = colName switch
        {
            "price3" => b.Price3, "price2" => b.Price2, _ => b.Price1,
        };
        if (!double.TryParse((bBase ?? "").Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var bv)) return raw;
        var result = (int)Math.Round(bv * factor.Value, MidpointRounding.AwayFromZero);
        return result.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>三档价全部求值（数字原样、公式算值、文本保留），价格列展示统一走它。</summary>
    public (string P3, string P2, string P1) ResolvePrices(DezhouPriceItem it)
        => (ResolvePrice(it, "price3"), ResolvePrice(it, "price2"), ResolvePrice(it, "price1"));

    /// <summary>比对行的新价补算：稿子里加收项价为公式，求值后才能与旧价比差额。</summary>
    private void ResolveCompareNew(DezhouDraftCompareRow r)
    {
        if (r is null || string.IsNullOrEmpty(r.NewCode)) return;
        if (!ByCode().TryGetValue(r.NewCode, out var it)) return;
        r.NewPrice3 = ResolvePrice(it, "price3");
        r.NewPrice2 = ResolvePrice(it, "price2");
        r.NewPrice1 = ResolvePrice(it, "price1");
        FillDelta(r);
    }

    // ==================================================================
    // 废止对照：筛选 / 检索
    // ==================================================================

    /// <summary>废止行匹配：类别 + 关键词（旧码 / 旧名 / 新码 / 新名）。</summary>
    public static bool CancelMatches(DezhouCancelItem it, string category, string q)
    {
        if (!string.IsNullOrEmpty(category) && category != "（全部）" &&
            !string.Equals((it.Category ?? "").Trim(), category, StringComparison.Ordinal))
            return false;
        return MatchText(q, it.OldCode, it.OldName, it.NewCode, it.NewName, it.Note);
    }

    public List<DezhouCancelItem> ListCancels(string category, string q)
        => Cancels.Where(c => CancelMatches(c, category, q)).ToList();

    public List<string> CancelCategories()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var c in Cancels)
        {
            var s = (c.Category ?? "").Trim();
            if (s.Length == 0 || !seen.Add(s)) continue;
            list.Add(s);
        }
        return list;
    }

    // ==================================================================
    // 映射关系：筛选 / 检索 / 索引
    // ==================================================================

    public static bool MapMatches(DezhouDraftMapRow it, string category, string q)
    {
        if (!string.IsNullOrEmpty(category) && category != "（全部）" &&
            !string.Equals((it.Category ?? "").Trim(), category, StringComparison.Ordinal))
            return false;
        return MatchText(q, it.NewCode, it.NewName, it.OldCode, it.OldName, it.Note);
    }

    public List<DezhouDraftMapRow> ListMaps(string category, string q)
        => Maps.Where(m => MapMatches(m, category, q)).ToList();

    private void BuildIndex()
    {
        if (_byOld != null && _byNew != null) return;
        var byOld = new Dictionary<string, List<DezhouDraftMapRow>>(StringComparer.Ordinal);
        var byNew = new Dictionary<string, List<DezhouDraftMapRow>>(StringComparer.Ordinal);
        void Push(Dictionary<string, List<DezhouDraftMapRow>> map, string key, DezhouDraftMapRow row)
        {
            var k = (key ?? "").Trim();
            if (k.Length == 0) return;
            if (!map.TryGetValue(k, out var list)) map[k] = list = new List<DezhouDraftMapRow>();
            list.Add(row);
        }
        foreach (var m in Maps)
        {
            Push(byOld, m.OldCode, m);
            Push(byNew, m.NewCode, m);
        }
        _byOld = byOld;
        _byNew = byNew;
    }

    /// <summary>按旧编码查映射关系行（可能多对多）。</summary>
    public List<DezhouDraftMapRow> MapsOfOld(string oldCode)
    {
        BuildIndex();
        return _byOld.TryGetValue((oldCode ?? "").Trim(), out var list)
            ? list : new List<DezhouDraftMapRow>();
    }

    /// <summary>按新编码查映射关系行（可能多对多）。</summary>
    public List<DezhouDraftMapRow> MapsOfNew(string newCode)
    {
        BuildIndex();
        return _byNew.TryGetValue((newCode ?? "").Trim(), out var list)
            ? list : new List<DezhouDraftMapRow>();
    }

    /// <summary>废止对照联查：按旧编码取作废行（供旧版物价页「拟作废」提示用；不参与主库判定）。</summary>
    public List<DezhouCancelItem> CancelsOfOld(string oldCode)
    {
        var k = (oldCode ?? "").Trim();
        return k.Length == 0
            ? new List<DezhouCancelItem>()
            : Cancels.Where(c => string.Equals((c.OldCode ?? "").Trim(), k, StringComparison.Ordinal)).ToList();
    }

    // ==================================================================
    // 新旧价格比对（征求意见稿新项目 ↔ 旧版物价 / 作废对照）
    // ==================================================================

    private List<DezhouDraftCompareRow> _compare;
    private Dictionary<string, string> _legacyByCode;

    /// <summary>
    /// 全部比对行（懒构建，按新编码排序）。
    ///
    /// <para>取数顺序：每条拟新增项目先取它的旧项目编码（优先用映射关系表的 oldCode，
    /// 没有则回落到作废对照的 oldCode），再按该旧码去 **旧版物价** 里查价格；
    /// 旧版物价查不到时退化为「仅作废对照」（<c>cancel</c>，无旧价），
    /// 连旧码都没有的项目标成 <c>none</c>（纯新增，无旧项目可对照）。</para>
    /// </summary>
    public List<DezhouDraftCompareRow> Compare()
    {
        if (_compare != null) return _compare;
        BuildLegacyIndex();

        var rows = new List<DezhouDraftCompareRow>();
        foreach (var it in Items)
        {
            var newCode = (it.Code ?? "").Trim();
            if (newCode.Length == 0) continue;

            // 旧码来源：映射关系表优先（它保留了两侧完整名称），作废对照兜底
            var map = MapsOfNew(newCode).FirstOrDefault();
            var oldCode = (map?.OldCode ?? "").Trim();
            var oldName = map?.OldName ?? "";
            if (oldCode.Length == 0)
            {
                var c = Cancels.FirstOrDefault(x =>
                    string.Equals((x.NewCode ?? "").Trim(), newCode, StringComparison.Ordinal));
                if (c != null) { oldCode = (c.OldCode ?? "").Trim(); oldName = c.OldName ?? ""; }
            }

            var row = new DezhouDraftCompareRow
            {
                NewCode = newCode,
                NewName = it.Name ?? "",
                Category = it.Category ?? "",
                NewPrice3 = it.Price3 ?? "",
                NewPrice2 = it.Price2 ?? "",
                NewPrice1 = it.Price1 ?? "",
                NewKind = string.IsNullOrEmpty(it.Kind) ? "main" : it.Kind,
                OldCode = oldCode,
                OldName = oldName,
                OldSource = oldCode.Length == 0 ? "none" : "cancel",
            };

            if (oldCode.Length > 0 && _legacyByCode.TryGetValue(oldCode, out var lg))
            {
                var li = LegacyOf(lg);
                row.OldSource = "legacy";
                row.OldName = string.IsNullOrWhiteSpace(oldName) ? li.Name : oldName;
                row.OldCategory = li.Category ?? "";
                row.OldPrice3 = li.Price3 ?? "";
                row.OldPrice2 = li.Price2 ?? "";
                row.OldPrice1 = li.Price1 ?? "";
                row.OldInsurance = li.Insurance ?? "";
                row.OldSelfPay = li.SelfPay ?? "";
            }

            ResolveCompareNew(row);   // 加收项稿子价是公式，求值后再算差额
            rows.Add(row);
        }

        rows.Sort((a, b) => string.CompareOrdinal(a.NewCode, b.NewCode));
        _compare = rows;
        return _compare;
    }

    /// <summary>旧版物价索引：旧码 → 「旧库存档 id」定位串（用主库共享的旧版物价列表）。</summary>
    private void BuildLegacyIndex()
    {
        if (_legacyByCode != null) return;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var li in LegacyItems())
        {
            var k = (li.Code ?? "").Trim();
            if (k.Length > 0 && !map.ContainsKey(k)) map[k] = k;  // 同码重复只留首条
        }
        _legacyByCode = map;
    }

    /// <summary>把索引里的键还原成旧版物价条目（索引只存编码，条目另取）。</summary>
    private DezhouLegacyItem LegacyOf(string code)
    {
        var k = (code ?? "").Trim();
        for (var i = 0; i < _legacyList.Count; i++)
            if (string.Equals((_legacyList[i].Code ?? "").Trim(), k, StringComparison.Ordinal))
                return _legacyList[i];
        return new DezhouLegacyItem();
    }

    private List<DezhouLegacyItem> _legacyList = new();

    /// <summary>旧版物价条目来源：主库 <c>dezhou-price.json</c> 的 legacy 段（45 号对码表）。</summary>
    private List<DezhouLegacyItem> LegacyItems()
    {
        if (_legacyList.Count > 0) return _legacyList;
        if (LegacySource != null) _legacyList = LegacySource() ?? new List<DezhouLegacyItem>();
        return _legacyList;
    }

    /// <summary>
    /// 旧版物价数据源（由外层注入 —— Core 的数据层之间不互相持有引用）。
    /// <see cref="AppState.Init"/> 里设为 <c>() =&gt; Dezhou.Legacy</c>；
    /// 未注入时比对页退化为「仅作废对照」（旧价格列留空）。
    /// </summary>
    public Func<List<DezhouLegacyItem>> LegacySource { get; set; }

    /// <summary>旧版物价数据更新后清缓存（由 AppState.ReloadAll 调用）。</summary>
    public void InvalidateCompare()
    {
        _compare = null;
        _byCode = null;
        _legacyByCode = null;
        _legacyList = new List<DezhouLegacyItem>();
    }

    /// <summary>算三级价差额与三级价涨幅（数字解析不出时留 null，界面显示「—」）。
    /// internal：主库「新旧物价比对」（DezhouPriceStore.CompareRows）共用同一套规则。</summary>
    internal static void FillDelta(DezhouDraftCompareRow r)
    {
        r.Delta3 = Diff(r.NewPrice3, r.OldPrice3);
        r.Delta2 = Diff(r.NewPrice2, r.OldPrice2);
        r.Delta1 = Diff(r.NewPrice1, r.OldPrice1);
        // 三档涨幅一次算全：新旧比对可以切三级 / 二级 / 一级口径，
        // 只算三级的话切档时要么重算全表、要么显示错档的涨幅。
        if (r.Delta3 is { } d3 && TryNum(r.OldPrice3, out var o3) && o3 > 0)
            r.Ratio3 = Math.Round(d3 / o3 * 100.0, 1);
        if (r.Delta2 is { } d2 && TryNum(r.OldPrice2, out var o2) && o2 > 0)
            r.Ratio2 = Math.Round(d2 / o2 * 100.0, 1);
        if (r.Delta1 is { } d1 && TryNum(r.OldPrice1, out var o1) && o1 > 0)
            r.Ratio1 = Math.Round(d1 / o1 * 100.0, 1);
    }

    private static double? Diff(string neu, string old)
        => TryNum(neu, out var n) && TryNum(old, out var o) ? Math.Round(n - o, 2) : null;

    /// <summary>宽松取数：价格串可能带「元」「/次」或全角符号，抠出第一段数字即可。（internal 供主库比对复用）</summary>
    internal static bool TryNum(string v, out double val)
    {
        val = 0;
        if (string.IsNullOrWhiteSpace(v)) return false;
        var sb = new System.Text.StringBuilder();
        var started = false;
        foreach (var c in v)
        {
            if (char.IsAsciiDigit(c) || (c == '.' && started))
            {
                sb.Append(c); started = true;
            }
            else if (started) break;
        }
        return sb.Length > 0 && double.TryParse(sb.ToString(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out val);
    }

    /// <summary>比对行筛选：类别 + 关键词（两侧编码 / 名称）+ 有无旧价。</summary>
    public static bool CompareMatches(DezhouDraftCompareRow r, string category, string q, string scope)
    {
        if (!string.IsNullOrEmpty(category) && category != "（全部）" &&
            !string.Equals((r.Category ?? "").Trim(), category, StringComparison.Ordinal))
            return false;
        // scope："" 全部 / "priced" 有旧价可比 / "new" 无旧项目
        if (scope == "priced" && r.OldSource != "legacy") return false;
        if (scope == "new" && r.OldSource != "none") return false;
        return MatchText(q, r.NewCode, r.NewName, r.OldCode, r.OldName, r.Category);
    }

    public List<DezhouDraftCompareRow> ListCompare(string category, string q, string scope)
        => Compare().Where(r => CompareMatches(r, category, q, scope)).ToList();

    /// <summary>比对统计：总行数 / 有旧价可比 / 仅作废对照 / 纯新增 / 降价 / 涨价 / 持平。</summary>
    public (int Total, int Priced, int CancelOnly, int BrandNew, int Down, int Up, int Flat) CompareStat()
    {
        var all = Compare();
        var priced = all.Where(r => r.OldSource == "legacy").ToList();
        return (all.Count,
                priced.Count,
                all.Count(r => r.OldSource == "cancel"),
                all.Count(r => r.OldSource == "none"),
                priced.Count(r => r.Delta3 is < 0),
                priced.Count(r => r.Delta3 is > 0),
                priced.Count(r => r.Delta3 is 0));
    }

    // ==================================================================
    // 关键词匹配（编码 / 名称 / 描述）
    // ==================================================================

    private static bool MatchText(string q, params string[] fields)
    {
        if (string.IsNullOrWhiteSpace(q)) return true;
        var needle = q.Trim();
        foreach (var f in fields)
        {
            if (string.IsNullOrEmpty(f)) continue;
            if (f.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ==================================================================
    // 供后台 / 主库并库动作使用（本期前台只读，此处预留合并入口）
    // ==================================================================

    /// <summary>
    /// 把征求意见稿合并进主库（**仅供正式发布后的转正动作调用**，前台不调用；
    /// 本期由 Cli `merge-draft` 在备份后调用）。说明：草稿行以追加方式并入，
    /// 主库既有的同编码行不覆盖、作废对照按 (旧码,新码) 去重 —— 整个动作幂等，重跑无副作用。
    /// 文号 / 执行日期取自草稿 meta（转正前由调用方填好；暂定文号带「（暂定）」便于日后全文替换）。
    /// </summary>
    public (int Items, int Cancels, int Maps) MergeInto(DezhouPriceStore main)
    {
        if (main == null) throw new ArgumentNullException(nameof(main));

        // 主库批次归属完全靠 items.doc 过滤，草稿行内 doc/exec 为空时用 meta 兜底
        var doc = (Meta?.Doc ?? "").Trim();
        var exec = (Meta?.Exec ?? "").Trim();

        var exist = new HashSet<string>(main.Items.Select(i => (i.Code ?? "").Trim()), StringComparer.Ordinal);
        var added = 0;
        foreach (var it in Items)
        {
            var code = (it.Code ?? "").Trim();
            if (code.Length == 0 || !exist.Add(code)) continue;
            added++;
            main.AddRow(FlatOf(it, doc, exec));
        }
        // 作废对照以 (旧码, 新码) 去重后追加；行内 doc 为空时用 meta 兜底 ——
        // 作废对照页的批次下拉 / 类别筛选同样只认行内 doc，空 doc 行会「看不见」
        var known = new Dictionary<string, DezhouCancelItem>(StringComparer.Ordinal);
        foreach (var c in main.Cancels)
            known[(c.OldCode ?? "").Trim() + "\u0001" + (c.NewCode ?? "").Trim()] = c;
        var cancelled = 0;
        foreach (var c in Cancels)
        {
            var key = (c.OldCode ?? "").Trim() + "\u0001" + (c.NewCode ?? "").Trim();
            if ((c.OldCode ?? "").Trim().Length == 0) continue;
            if (known.TryGetValue(key, out var existRow))
            {
                // 幂等重跑时修复历史账：同 (旧码,新码) 已存在但 doc/exec 空的行补上
                if (string.IsNullOrWhiteSpace(existRow.Doc) && doc.Length > 0) existRow.Doc = doc;
                if (string.IsNullOrWhiteSpace(existRow.Exec) && exec.Length > 0) existRow.Exec = exec;
                continue;
            }
            cancelled++;
            c.Pending = false;                       // 转正：旧项目正式作废
            if (string.IsNullOrWhiteSpace(c.Doc) && doc.Length > 0) c.Doc = doc;
            if (string.IsNullOrWhiteSpace(c.Exec) && exec.Length > 0) c.Exec = exec;
            known[key] = c;
            main.Cancels.Add(c);
        }
        foreach (var m in Maps) m.Pending = false;

        // 批次汇总：把草稿的 9 个子批（含各类别使用说明）聚合成一个主库批次
        AppendBatch(main, doc, exec);

        Meta.Status = "active";
        // 注意：Refs 的语义是「本批作废涉及的既有批次文号」，不塞自身文号（自身文号在 Meta.Doc / 黄条标题里）
        Meta.Note = $"已于 {DateTime.Now:yyyy-MM-dd} 并入德州物价主库（文号暂定「{doc}」，"
                  + "正式文号下发后全文替换）。本页保留作档案视图，数据以主库为准。";
        Save();
        main.SaveWithRecompute();
        return (added, cancelled, Maps.Count);
    }

    /// <summary>把草稿 9 个子批（含类别级使用说明）聚合成一个主库批次（按 doc 幂等）。</summary>
    private void AppendBatch(DezhouPriceStore main, string doc, string exec)
    {
        if (doc.Length == 0) return;
        var subs = Meta?.Batches ?? new List<DezhouBatch>();
        var batch = new DezhouBatch
        {
            Doc = doc,
            Exec = exec,
            Prov = "",
            Scope = string.Join("、", subs.Select(b => b.Scope).Where(s => !string.IsNullOrWhiteSpace(s))),
            Expect = subs.Sum(b => b.Expect),
            Rows = subs.Sum(b => b.Rows),
            Mains = subs.Sum(b => b.Mains),
            Adds = subs.Sum(b => b.Adds),
            Exts = subs.Sum(b => b.Exts),
            Categories = subs.SelectMany(b => b.Categories ?? new List<DezhouCategory>()).ToList(),
        };
        main.Meta.Batches ??= new List<DezhouBatch>();
        main.Meta.Batches.RemoveAll(b => (b.Doc ?? "").Trim() == doc);   // 重跑不重复
        main.Meta.Batches.Add(batch);
    }

    private static Dictionary<string, string> FlatOf(DezhouPriceItem it, string doc, string exec)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var col in DezhouPriceStore.Columns)
        {
            var v = col.Key switch
            {
                // 行内为空时用草稿 meta 的文号/执行日期兜底（主库批次/类别筛选靠 doc）
                "doc" => string.IsNullOrWhiteSpace(it.Doc) ? doc : it.Doc,
                "exec" => string.IsNullOrWhiteSpace(it.Exec) ? exec : it.Exec,
                "category" => it.Category,
                "seq" => it.Seq,
                "code" => it.Code,
                "kind" => it.Kind,
                "name" => it.Name,
                "serviceOutput" => it.ServiceOutput,
                "priceComposition" => it.PriceComposition,
                "unit" => it.Unit,
                "price3" => it.Price3,
                "price2" => it.Price2,
                "price1" => it.Price1,
                "deduct" => it.Deduct,
                "insurance" => it.Insurance,
                "selfPay" => it.SelfPay,
                "note" => it.Note,
                "usage" => it.Usage,
                _ => "",
            };
            map[col.Key] = v ?? "";
        }
        return map;
    }
}
