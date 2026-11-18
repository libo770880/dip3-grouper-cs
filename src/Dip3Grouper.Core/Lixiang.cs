using System.Text.RegularExpressions;

namespace Dip3Grouper;

/// <summary>
/// 医疗服务价格立项指南库 + 解读辅导（对齐 src/lixiang.js）
/// 数据文件: data/lixiang.json
/// </summary>
/// <summary>
/// 立项指南检索结果。原为匿名类型，改为具名类型以便桌面客户端强类型使用；
/// 属性名与顺序不变，序列化输出与原先逐字段一致。
/// </summary>
public sealed class LixiangListResult
{
    public string Label { get; set; }
    public string Guide { get; set; }
    public string GuideUsage { get; set; }
    public List<ColumnDef> Columns { get; set; }
    public string KeyField { get; set; }
    public List<LixiangRow> Rows { get; set; }
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

public class LixiangStore
{
    public LixiangFile State { get; private set; } = new();

    // ---------- 列表/维护/查询统一列结构 ----------
    public static readonly List<ColumnDef> Columns = new()
    {
        new ColumnDef { Key = "seq", Label = "序号", Width = 56, Mono = true },
        new ColumnDef { Key = "name", Label = "项目名称", Width = 180 },
        new ColumnDef { Key = "category", Label = "项目类别", Width = 96 },
        new ColumnDef { Key = "unit", Label = "计价单位", Width = 76 },
        new ColumnDef { Key = "serviceOutput", Label = "服务产出", Width = 280 },
        new ColumnDef { Key = "priceComposition", Label = "价格构成", Width = 280 },
        new ColumnDef { Key = "addItems", Label = "加收项", Width = 150 },
        new ColumnDef { Key = "extItems", Label = "扩展项", Width = 150 },
        new ColumnDef { Key = "usage", Label = "使用说明（计价说明）", Width = 240 },
    };

    private static readonly Dictionary<string, string> HeaderAliases = new()
    {
        ["序号"] = "seq",
        ["项目名称"] = "name",
        ["项目类别"] = "category",
        ["类别"] = "category",
        ["服务产出"] = "serviceOutput",
        ["价格构成"] = "priceComposition",
        ["加收项"] = "addItems",
        ["扩展项"] = "extItems",
        ["计价单位"] = "unit",
        ["计价单元"] = "unit",
        ["计价说明"] = "usage",
        ["使用说明"] = "usage",
    };

    private static readonly string[] RowKeys =
        { "seq", "name", "category", "unit", "serviceOutput", "priceComposition", "addItems", "extItems", "usage" };

    public void Load()
    {
        if (File.Exists(AppPaths.LixiangFile))
        {
            try
            {
                var s = Js.ReadJsonFile<LixiangFile>(AppPaths.LixiangFile);
                State = s ?? new LixiangFile();
                State.Sections ??= new Dictionary<string, LixiangSection>();
                State.Meta ??= new LixiangMeta();
                State.Jiedu ??= new JieduStore();
                State.Jiedu.List ??= new List<JieduItem>();
                Reindex();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[lixiang] lixiang.json 解析失败，使用空库: {e.Message}");
                State = new LixiangFile();
            }
        }

        // 存量修复：老版本切答案时把下一题的题号留在了上一题答案末尾（正文里凭空多出一行「2.」）。
        // 源文件已不在手边，没法重新解析，只能按形态兜底清掉（幂等，清完回写一次）。
        // ⚠ 单独 try：写盘失败不能连累上面刚加载好的库（外层 catch 会把 State 清空）。
        try
        {
            if (State.Jiedu != null && NormalizeJiedu() > 0) Save();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[lixiang] 解读辅导存量修复失败（仅在内存中生效）: {e.Message}");
        }
    }

    /// <summary>
    /// 给每一行分配全局跳转键 <see cref="LixiangRow.Gid"/>（1..N，按区段顺序连续）。
    ///
    /// <para>Id 只在区段内唯一，跨库跳转必须用 Gid，否则会串到别的区段同号条目
    /// （§9.25）。加载 / 导入 / 增删改后都要重排——新增行会拿到新 Gid，
    /// CrossMap / Related 靠数据戳感知并重建索引，不需要各自维护失效逻辑。</para>
    /// </summary>
    public void Reindex()
    {
        var n = 0;
        foreach (var sec in State.Sections.Values)
            foreach (var r in sec.Rows) r.Gid = ++n;
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        Js.WriteJsonFile(AppPaths.LixiangFile, State);
    }

    /// <summary>全部区段 tab → {label, total}</summary>
    public Dictionary<string, SectionCount> SectionCounts()
        => State.Sections.ToDictionary(
            kv => kv.Key,
            kv => new SectionCount { Label = kv.Value.Label, Total = kv.Value.Rows.Count });

    /// <summary>全部区段 Label（即立项指南「项目类别」的标准候选，保序去重）</summary>
    public List<string> SectionLabels()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var sec in State.Sections.Values)
        {
            var v = (sec.Label ?? "").Trim();
            if (v.Length == 0 || !seen.Add(v)) continue;
            list.Add(v);
        }
        return list;
    }

    /// <summary>现有数据中出现过的不重复「项目类别」（保序，空值剔除）</summary>
    public List<string> DistinctCategories() => DistinctValues(r => r.Category);

    /// <summary>现有数据中出现过的不重复「计价单位」（保序，空值剔除）</summary>
    public List<string> DistinctUnits() => DistinctValues(r => r.Unit);

    private List<string> DistinctValues(Func<LixiangRow, string> sel)
    {
        List<string> outList = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sec in State.Sections.Values)
            foreach (var r in sec.Rows)
            {
                var v = (sel(r) ?? "").Trim();
                if (v.Length == 0 || !seen.Add(v)) continue;
                outList ??= new List<string>();
                outList.Add(v);
            }
        return outList ?? new List<string>();
    }

    // ==================================================================
    // 按项目名称反查立项指南（供物价「新旧比对 / 现行项目」跳转）
    // ==================================================================

    private Dictionary<string, (string Tab, LixiangRow Row)> _byName;
    private string _byNameStamp;

    /// <summary>
    /// 名称 → 立项指南条目（跨全部区段，同名取首条）。
    /// <para>为什么按名称：立项指南原表没有国家编码列（只有序号 / 名称 / 类别 / 服务产出…），
    /// 编码是各省落地时才赋的。物价库的项目名与立项指南项目名一一对应，
    /// 是唯一稳定的连接键；加收项 / 扩展项在原表里写作「主项-子项」，
    /// 落到物价库成为独立项目，所以主项名也要能反查（见 <see cref="FindGuide"/>）。</para>
    /// </summary>
    private Dictionary<string, (string Tab, LixiangRow Row)> ByName()
    {
        // 数据只在后台维护时变，用「区段数 + 总行数」当版本戳，避免每次比价都重建几万条
        var stamp = $"{State.Sections.Count}:{State.Sections.Values.Sum(s => s.Rows.Count)}";
        if (_byName != null && _byNameStamp == stamp) return _byName;

        var map = new Dictionary<string, (string Tab, LixiangRow Row)>(StringComparer.Ordinal);
        foreach (var (tab, sec) in State.Sections)
            foreach (var r in sec.Rows)
            {
                var n = NormName(r.Name);
                if (n.Length > 0 && !map.ContainsKey(n)) map[n] = (tab, r);
            }
        _byName = map;
        _byNameStamp = stamp;
        return map;
    }

    /// <summary>名称归一：去空白 + 全角括号转半角（原表与落地表括注写法不统一，不归一会有大量假未命中）。</summary>
    public static string NormName(string s) => (s ?? "")
        .Replace("（", "(").Replace("）", ")")
        .Replace("\u3000", "").Replace(" ", "").Trim();

    /// <summary>
    /// 按物价项目名找立项指南条目。先精确匹配，再退到「去掉子项后缀」的主项名
    /// （加收项 / 扩展项在立项指南里挂在主项下，本身不是独立行）。
    /// </summary>
    /// <returns>命中的区段 tab + 行；未命中返回 null。</returns>
    public (string Tab, LixiangRow Row)? FindGuide(string itemName)
    {
        var map = ByName();
        var n = NormName(itemName);
        if (n.Length == 0) return null;
        if (map.TryGetValue(n, out var hit)) return hit;

        var main = NormName(MainName(itemName));
        if (main.Length > 0 && main != n && map.TryGetValue(main, out var h2)) return h2;
        return null;
    }

    /// <summary>
    /// 取主项名：物价侧加收 / 扩展项形如「心脏移植术-儿童手术(加收)」，
    /// 主项就是 <c>-</c> 之前那段。没有后缀的原样返回。
    /// </summary>
    public static string MainName(string s)
    {
        var v = (s ?? "").Trim();
        var i = v.IndexOf('-');
        if (i > 0) v = v[..i];
        v = Regex.Replace(v, @"\((加收|扩展)\)$", "");
        return v.Trim();
    }

    /// <summary>立项指南条目摘要：类别 / 单位，用于悬停提示。</summary>
    public static string GuideTip(LixiangRow r)
    {
        if (r == null) return "";
        var parts = new List<string>();
        var cat = (r.Category ?? "").Trim();
        var unit = (r.Unit ?? "").Trim();
        if (cat.Length > 0) parts.Add(cat);
        if (unit.Length > 0) parts.Add("单位 " + unit);
        return parts.Count == 0 ? "" : string.Join(" · ", parts);
    }

    /// <summary>检索 + 分页（tab 为空 = 全部区段联合检索）</summary>
    /// <summary>
    /// 检索 + 分页。tab 为空表示全部区段；category 为空不限类别；
    /// flag："" 不限 / "add" 含加收项 / "ext" 含扩展项；
    /// 关键词支持全拼与首字母（xzyz → 心脏移植），覆盖名称 / 类别 / 单位 / 服务产出 / 计价说明等。
    /// </summary>
    public LixiangListResult ListRows(string tab, string q = "", int page = 1, int pageSize = 50,
                                      string category = "", string flag = "")
    {
        category ??= "";
        flag ??= "";
        var kw = (q ?? "").Trim().ToUpperInvariant();
        IEnumerable<KeyValuePair<string, LixiangSection>> tabs;
        if (!string.IsNullOrEmpty(tab))
        {
            if (!State.Sections.TryGetValue(tab, out var sec0)) throw new InvalidOperationException($"未知的立项指南区段: {tab}");
            tabs = new[] { new KeyValuePair<string, LixiangSection>(tab, sec0) };
        }
        else
        {
            tabs = State.Sections.AsEnumerable();
        }

        // 类别为空的行在列表里以所属区段名呈现，因此过滤要按「呈现后的类别」判断
        string CatOf(LixiangRow r, string secLabel) => string.IsNullOrEmpty(r.Category) ? secLabel : r.Category;

        bool Hit(LixiangRow r) => kw.Length == 0 ||
            new[] { r.Seq, r.Name, r.Category, r.Unit, r.ServiceOutput, r.PriceComposition, r.AddItems, r.ExtItems, r.Usage }
                .Any(v => (v ?? "").ToUpperInvariant().Contains(kw)) ||
            PinyinHelper.PyMatchAny(new[] { r.Name, r.Category, r.Unit }, kw);

        var rows = new List<LixiangRow>();
        foreach (var (_, sec) in tabs)
        {
            foreach (var r in sec.Rows)
            {
                if (flag == "add" && string.IsNullOrWhiteSpace(r.AddItems)) continue;
                if (flag == "ext" && string.IsNullOrWhiteSpace(r.ExtItems)) continue;
                if (category.Length > 0 && CatOf(r, sec.Label) != category) continue;
                if (!Hit(r)) continue;
                // 一律浅拷贝再填「区段」：直接改库里的实例会把 section 带进 lixiang.json，
                // 2627 行白占空间，而且改过一次就再也回不到「没这个键」的干净形态。
                rows.Add(new LixiangRow
                {
                    Seq = r.Seq, Name = r.Name,
                    Category = string.IsNullOrEmpty(r.Category) ? sec.Label : r.Category,
                    Unit = r.Unit, ServiceOutput = r.ServiceOutput, PriceComposition = r.PriceComposition,
                    AddItems = r.AddItems, ExtItems = r.ExtItems, Usage = r.Usage,
                    Id = r.Id, Gid = r.Gid, Section = sec.Label,
                });
            }
        }

        var total = rows.Count;
        page = Math.Max(1, page);
        pageSize = Math.Min(200, Math.Max(5, pageSize));
        var start = (page - 1) * pageSize;

        var label = "全部指南";
        var guide = "";
        var guideUsage = "";
        if (!string.IsNullOrEmpty(tab))
        {
            var s = State.Sections[tab];
            label = s.Label;
            guide = s.Guide ?? "";
            guideUsage = s.GuideUsage ?? "";
        }

        return new LixiangListResult
        {
            Label = label,
            Guide = guide,
            GuideUsage = guideUsage,
            Columns = Columns,
            KeyField = "id",
            Rows = rows.Skip(start).Take(pageSize).ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize,
        };
    }

    /// <summary>
    /// 类别候选（保序去重，含由所属区段名兜底的行）——供前台「类别」下拉使用。
    /// </summary>
    public List<string> Categories()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var (_, sec) in State.Sections)
        {
            foreach (var r in sec.Rows)
            {
                var v = (string.IsNullOrEmpty(r.Category) ? sec.Label : r.Category)?.Trim() ?? "";
                if (v.Length == 0 || !seen.Add(v)) continue;
                list.Add(v);
            }
        }
        return list;
    }

    /// <summary>
    /// 类别取值：留空则默认用所属区段名（方便新增），填了就按填的存——
    /// 不强制等于区段名，新增类别时无需改代码/配置。
    /// </summary>
    public static string ResolveCategory(string submitted, string sectionLabel)
        => string.IsNullOrWhiteSpace(submitted) ? (sectionLabel ?? "").Trim() : submitted.Trim();

    public ParseResult AddRow(string tab, Dictionary<string, string> flat)
    {
        if (!State.Sections.TryGetValue(tab ?? "", out var sec)) return new ParseResult { Error = $"未知的立项指南区段: {tab}" };
        var row = NormalizeRow(flat);
        if (!row.Ok) return row;
        var r = (LixiangRow)row.Row;
        r.Id = sec.NextId > 0 ? sec.NextId : 1;
        r.Category = ResolveCategory(r.Category, sec.Label);
        sec.NextId = r.Id + 1;
        sec.Rows.Add(r);
        Reindex();
        return new ParseResult { Row = r.Id };
    }

    public ParseResult UpdateRow(string tab, string id, Dictionary<string, string> flat)
    {
        if (!State.Sections.TryGetValue(tab ?? "", out var sec)) return new ParseResult { Error = $"未知的立项指南区段: {tab}" };
        var row = sec.Rows.FirstOrDefault(r => r.Id.ToString() == (id ?? ""));
        if (row == null) return new ParseResult { Error = $"未找到记录 #{id}" };
        var next = NormalizeRow(flat);
        if (!next.Ok) return next;
        var n = (LixiangRow)next.Row;
        n.Id = row.Id;
        n.Category = ResolveCategory(n.Category, sec.Label);
        var idx = sec.Rows.IndexOf(row);
        sec.Rows[idx] = n;
        Reindex();                       // 整行被替换成新实例，Gid 必须重新分配
        return new ParseResult { Row = n.Id };
    }

    public ParseResult RemoveRow(string tab, string id)
    {
        if (!State.Sections.TryGetValue(tab ?? "", out var sec)) return new ParseResult { Error = $"未知的立项指南区段: {tab}" };
        var idx = sec.Rows.FindIndex(r => r.Id.ToString() == (id ?? ""));
        if (idx < 0) return new ParseResult { Error = $"未找到记录 #{id}" };
        sec.Rows.RemoveAt(idx);
        Reindex();                       // 删掉一行后，后面所有行的 Gid 都变了
        return new ParseResult { Row = true };
    }

    private static ParseResult NormalizeRow(Dictionary<string, string> flat)
    {
        var row = new LixiangRow();
        foreach (var k in RowKeys)
        {
            var v = flat != null && flat.TryGetValue(k, out var x) ? (x ?? "").Trim() : "";
            switch (k)
            {
                case "seq": row.Seq = v; break;
                case "name": row.Name = v; break;
                case "category": row.Category = v; break;
                case "unit": row.Unit = v; break;
                case "serviceOutput": row.ServiceOutput = v; break;
                case "priceComposition": row.PriceComposition = v; break;
                case "addItems": row.AddItems = v; break;
                case "extItems": row.ExtItems = v; break;
                case "usage": row.Usage = v; break;
            }
        }
        if (row.Name.Length == 0) return new ParseResult { Error = "项目名称不能为空" };
        return new ParseResult { Row = row };
    }

    // ==================================================================
    // 工作簿解析
    // ==================================================================

    public class ParsedSection
    {
        public string Label { get; set; }
        public string Guide { get; set; }
        public string GuideUsage { get; set; }
        public List<LixiangRow> Rows { get; set; } = new();
    }

    private static readonly Regex SheetNameRe = new(@"^(\d+)\s*[-－]\s*(.+?)(?:立项指南项目?|立项指南)?$", RegexOptions.Compiled);
    private static readonly Regex TabSafeRe = new(@"[^A-Za-z0-9_\u4e00-\u9fa5]", RegexOptions.Compiled);
    private static readonly Regex TrailGuideRe = new(@"(?:立项指南项目?|立项指南)$", RegexOptions.Compiled);

    /// <summary>从工作簿解析立项指南（aoas: sheet 名 → 二维字符串数组）。</summary>
    public (Dictionary<string, ParsedSection> Sections, Dictionary<string, LixiangSectionReport> Report) ParseWorkbook(
        IEnumerable<string> sheetNames, Dictionary<string, List<List<string>>> aoas)
    {
        var outSections = new Dictionary<string, ParsedSection>();
        var report = new Dictionary<string, LixiangSectionReport>();

        foreach (var shNameRaw in sheetNames)
        {
            var shName = shNameRaw ?? "";
            if (!aoas.TryGetValue(shName, out var aoa) || aoa == null || aoa.Count < 2) continue;

            var m = SheetNameRe.Match(shName.Trim());
            string tab, label;
            if (m.Success)
            {
                tab = "s" + int.Parse(m.Groups[1].Value).ToString("D2");
                label = m.Groups[2].Value;
            }
            else
            {
                // 回退: 模板导出的 sheet 名就是「区段标签」本身, 按标签反查既有 tab,
                // 否则会落到 x_* 分支新建区段, 导致「下载模板→导入」把区段翻倍。
                var rawName = shName.Trim();
                var stripped = TrailGuideRe.Replace(rawName, "").Trim();
                (string Key, LixiangSection Sec)? hit = null;
                foreach (var kv in State.Sections)
                {
                    if (string.Equals((kv.Value?.Label ?? "").Trim(), rawName, StringComparison.Ordinal)) { hit = (kv.Key, kv.Value); break; }
                }
                if (hit == null)
                {
                    foreach (var kv in State.Sections)
                    {
                        if (string.Equals((kv.Value?.Label ?? "").Trim(), stripped, StringComparison.Ordinal)) { hit = (kv.Key, kv.Value); break; }
                    }
                }
                if (hit != null)
                {
                    tab = hit.Value.Key;
                    label = hit.Value.Sec.Label;
                }
                else
                {
                    tab = "x_" + TabSafeRe.Replace(rawName, "");
                    label = rawName;
                }
            }
            var guide = ((aoa[0] != null && aoa[0].Count > 0 ? aoa[0][0] : "") ?? "").Trim();
            if (guide.Length == 0) guide = label;

            var headerIdx = -1;
            for (var i = 0; i < Math.Min(5, aoa.Count); i++)
            {
                if ((aoa[i] ?? new List<string>()).Any(h => (h ?? "").Trim() == "项目名称")) { headerIdx = i; break; }
            }
            if (headerIdx < 0)
            {
                report[shName] = new LixiangSectionReport { Label = label, Skipped = "未找到表头行" };
                continue;
            }
            var colKeys = (aoa[headerIdx] ?? new List<string>())
                .Select(h => HeaderAliases.TryGetValue((h ?? "").Trim(), out var k) ? k : null).ToList();
            if (!colKeys.Contains("name"))
            {
                report[shName] = new LixiangSectionReport { Label = label, Skipped = "表头缺少「项目名称」" };
                continue;
            }

            var rows = new List<LixiangRow>();
            var category = "";
            var added = 0;
            var groupRows = 0;
            var guideUsage = "";

            for (var i = headerIdx + 1; i < aoa.Count; i++)
            {
                var cells = aoa[i] ?? new List<string>();
                var flat = new Dictionary<string, string>();
                var hasValue = false;
                for (var ci = 0; ci < colKeys.Count; ci++)
                {
                    var k = colKeys[ci];
                    if (k == null) continue;
                    var v = (ci < cells.Count ? cells[ci] : "") ?? "";
                    v = v.Trim();
                    if (v.Length > 0) { flat[k] = v; hasValue = true; }
                }
                if (!hasValue) continue;

                var firstText = cells.Select(c => (c ?? "").Trim()).FirstOrDefault(v => v.Length > 0) ?? "";
                if (firstText.StartsWith("使用说明", StringComparison.Ordinal))
                {
                    guideUsage = Regex.Replace(firstText, @"^使用说明[:：]\s*", "");
                    for (var j = i + 1; j < aoa.Count; j++)
                    {
                        var cont = (aoa[j] ?? new List<string>()).Select(c => (c ?? "").Trim()).FirstOrDefault(v => v.Length > 0);
                        if (!string.IsNullOrEmpty(cont)) guideUsage += "\n" + cont;
                    }
                    break;
                }

                var onlySeq = flat.TryGetValue("seq", out var sq) && sq.Length > 0
                    && flat.Count == 1 && !Regex.IsMatch(sq, @"^\d+$");
                if (onlySeq) { category = sq; groupRows++; continue; }

                var merged = new Dictionary<string, string> { ["category"] = category };
                foreach (var (kk, vv) in flat) merged[kk] = vv;
                var row = NormalizeRow(merged);
                if (!row.Ok) continue;
                var r = (LixiangRow)row.Row;
                r.Id = rows.Count + 1;
                rows.Add(r);
                added++;
            }

            if (outSections.TryGetValue(tab, out var exist) && exist.Rows.Count >= rows.Count) continue;
            outSections[tab] = new ParsedSection { Label = label, Guide = guide, GuideUsage = guideUsage, Rows = rows };
            report[shName] = new LixiangSectionReport
            {
                Label = label,
                Tab = tab,
                Added = added,
                GroupRows = groupRows,
                GuideUsage = guideUsage.Length,
            };
        }
        return (outSections, report);
    }

    /// <summary>应用解析结果: mode=replace 覆盖同名区段 / upsert 追加+按项目名称去重更新</summary>
    public void ApplySections(Dictionary<string, ParsedSection> parsed, string mode)
    {
        foreach (var (tab, sec) in parsed)
        {
            if (!State.Sections.TryGetValue(tab, out var cur) || mode == "replace")
            {
                State.Sections[tab] = new LixiangSection
                {
                    Label = sec.Label,
                    Guide = sec.Guide,
                    GuideUsage = sec.GuideUsage ?? "",
                    NextId = 1,
                    Rows = sec.Rows,
                };
            }
            else
            {
                if (!string.IsNullOrEmpty(sec.GuideUsage)) cur.GuideUsage = sec.GuideUsage;
                var byName = new Dictionary<string, LixiangRow>();
                foreach (var r in cur.Rows) byName[r.Name ?? ""] = r;
                foreach (var row in sec.Rows)
                {
                    if (byName.TryGetValue(row.Name ?? "", out var exist))
                    {
                        var keepId = exist.Id;
                        var idx = cur.Rows.IndexOf(exist);
                        row.Id = keepId;
                        cur.Rows[idx] = row;
                        byName[row.Name ?? ""] = row;
                    }
                    else
                    {
                        row.Id = cur.NextId > 0 ? cur.NextId : 1;
                        cur.NextId = row.Id + 1;
                        cur.Rows.Add(row);
                        byName[row.Name ?? ""] = row;
                    }
                }
            }
            var s = State.Sections[tab];
            s.NextId = (s.Rows.Count == 0 ? 0 : s.Rows.Max(r => r.Id)) + 1;
        }
        Reindex();   // 导入会整段换行 / 追加行，跳转键必须与内容一起重建
    }

    /// <summary>设置某区段的类级使用说明（后台维护）</summary>
    public GuideUsageResult SetGuideUsage(string tab, string usage)
    {
        if (!State.Sections.TryGetValue(tab ?? "", out var sec)) throw new InvalidOperationException($"未知的立项指南区段: {tab}");
        sec.GuideUsage = (usage ?? "").Trim();
        Save();
        return new GuideUsageResult { Ok = true, Tab = tab, Length = sec.GuideUsage.Length };
    }

    // ==================================================================
    // 解读辅导
    // ==================================================================

    private static readonly Regex PageRe = new(@"PAGE\s*\\\* MERGEFORMAT\s*\d*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TrailingWsRe = new(@"[ \t]+\n", RegexOptions.Compiled);
    private static readonly Regex HeaderRe = new(@"医疗服务价格项目立项指南解读辅导\s*\n?\s*（第(\d+)期）", RegexOptions.Compiled);
    // 题面行：题号 + 单行文本，以问号收尾。问号必须同时收全角「？」与半角「?」——
    // 2026-11-18 发现国家局解读原文里第 5 期第 4、11 两问以半角「?」收尾，
    // 旧正则只认全角导致整题被吞进上一题答案（224 → 实际 226 条）。
    private static readonly Regex QRe = new(@"(?:^|\n)\s*(\d{1,3})[\.、．]\s*([^\n]{2,100}[？?])\s*\n?", RegexOptions.Compiled);
    private static readonly Regex MultiNlRe = new(@"\n{2,}", RegexOptions.Compiled);

    /// <summary>
    /// 答案尾部「光秃秃的一个题号」：`…项目。\n2.`。
    ///
    /// <para>2026-10-30 修：切答案时用「下一题匹配结束位置 - 题面长度 - 1」推边界，那个 `-1`
    /// 假设题号前只有一个 `\n`；实际「上一段正文 \n 2. 题面」里 `（?&gt;^|\n）\s*` 可能只吃到 1 个
    /// 换行，算式就多留一个字符，把下一题的 `2.` 留在本题答案末尾。224 条里 211 条中招
    /// （每期最后一题切到正文尾，所以那 13 条没事）。现在边界直接取「下一题题号数字的下标」，
    /// 与换行个数无关；存量数据由 <see cref="NormalizeJiedu"/> 兜底清掉。</para>
    /// </summary>
    private static readonly Regex DanglingNoRe = new(@"\n\s*\d{1,3}\s*[\.、．]\s*$", RegexOptions.Compiled);

    /// <summary>看一条答案末尾是否挂着下一题的题号（自检 / 修复共用）。</summary>
    public static bool HasDanglingNo(string a) => !string.IsNullOrEmpty(a) && DanglingNoRe.IsMatch(a);

    /// <summary>
    /// 清掉存量答案尾部的游离题号（幂等，返回改动条数）。
    /// 没有源文件时无法重新解析，只能按形态兜底：末尾是「换行 + 1~3 位数字 + 句点」的一定是切歪的题号。
    /// </summary>
    public int NormalizeJiedu()
    {
        var n = 0;
        foreach (var it in State.Jiedu?.List ?? new List<JieduItem>())
        {
            var a = it.A;
            if (string.IsNullOrEmpty(a)) continue;
            var fixedA = a;
            for (var guard = 0; guard < 4; guard++)   // 连续粘了多个也能一次清掉
            {
                var next = DanglingNoRe.Replace(fixedA, "");
                if (next == fixedA) break;
                fixedA = next;
            }
            fixedA = fixedA.TrimEnd();
            if (fixedA == a) continue;
            it.A = fixedA;
            n++;
        }
        return n;
    }

    /// <summary>从解读辅导全文解析「期 → 问答」条目。</summary>
    public static (string Preface, List<JieduItem> List) ParseJieduText(string text)
    {
        var t = (text ?? "");
        t = t.Replace("\r\n", "\n").Replace("\r", "\n");
        t = PageRe.Replace(t, "");
        t = TrailingWsRe.Replace(t, "\n");

        var marks = new List<(int Issue, int Start, int ContentStart)>();
        foreach (Match mm in HeaderRe.Matches(t))
            marks.Add((int.Parse(mm.Groups[1].Value), mm.Index, mm.Index + mm.Length));

        var preface = marks.Count > 0 ? t[..marks[0].Start].Trim() : t.Trim();
        var list = new List<JieduItem>();

        for (var idx = 0; idx < marks.Count; idx++)
        {
            var mk = marks[idx];
            var bodyEnd = idx + 1 < marks.Count ? marks[idx + 1].Start : t.Length;
            var body = t.Substring(mk.ContentStart, bodyEnd - mk.ContentStart);

            var qs = new List<(int No, string Q, int At, int NumAt)>();
            foreach (Match qm in QRe.Matches(body))
                qs.Add((int.Parse(qm.Groups[1].Value), qm.Groups[2].Value.Trim(),
                        qm.Index + qm.Length, qm.Groups[1].Index));   // NumAt = 题号数字自身的位置

            for (var i = 0; i < qs.Count; i++)
            {
                var qa = qs[i];
                // 答案的右边界 = 下一题「题号第一位数字」的位置，而不是「匹配结束 - 题面长度 - 1」。
                // 后者隐含「题号前恰好一个换行」，正文与题号之间若多一个换行 / 空格就会把下一题的
                // 「2.」留在答案末尾（2026-09 首次导入 224 条时 211 条中招，见 DanglingNoRe 的注释）。
                var end = i + 1 < qs.Count ? qs[i + 1].NumAt : body.Length;
                if (end < qa.At) end = qa.At;
                var a = body.Substring(qa.At, end - qa.At).Trim();
                a = DanglingNoRe.Replace(a, "").TrimEnd();   // 双保险：万一还有别的切法漏进来
                list.Add(new JieduItem
                {
                    Issue = mk.Issue,
                    No = qa.No,
                    Q = qa.Q,
                    A = MultiNlRe.Replace(a, "\n"),
                });
            }
        }
        return (preface, list);
    }

    /// <summary>导入解读: mode=merge 按（期,题号）合并更新 / replace 清空后导入</summary>
    public JieduImportResult JieduImport(string preface, List<JieduItem> items, string mode, string source)
    {
        var cur = mode == "replace" ? new List<JieduItem>() : State.Jiedu.List.ToList();
        static string K(JieduItem it) => $"{it.Issue}#{it.No}";
        var byKey = new Dictionary<string, JieduItem>();
        foreach (var it in cur) byKey[K(it)] = it;   // 重复键 → 后者覆盖（对齐 Map 语义）

        int added = 0, updated = 0;
        foreach (var it in items)
        {
            var k = K(it);
            if (byKey.TryGetValue(k, out var exist))
            {
                var idx = cur.IndexOf(exist);
                cur[idx] = it;
                byKey[k] = it;
                updated++;
            }
            else
            {
                byKey[k] = it;
                cur.Add(it);
                added++;
            }
        }
        cur = cur.OrderBy(x => x.Issue).ThenBy(x => x.No).ToList();

        State.Jiedu = new JieduStore
        {
            List = cur,
            Preface = !string.IsNullOrEmpty(preface) ? preface : (State.Jiedu.Preface ?? ""),
            Source = !string.IsNullOrEmpty(source) ? source : (State.Jiedu.Source ?? ""),
            ImportedAt = Js.Iso(),
        };
        Save();
        return new JieduImportResult
        {
            Total = cur.Count,
            Added = added,
            Updated = updated,
            Issues = JieduIssues().Count,
        };
    }

    public List<JieduIssueCount> JieduIssues()
    {
        var map = new Dictionary<int, int>();
        foreach (var it in State.Jiedu.List) map[it.Issue] = map.TryGetValue(it.Issue, out var c) ? c + 1 : 1;
        return map.OrderBy(kv => kv.Key).Select(kv => new JieduIssueCount { Issue = kv.Key, Total = kv.Value }).ToList();
    }

    public JieduListResult JieduList(string issue, string q)
    {
        var nIssue = int.TryParse(issue, out var n) ? n : 0;
        var kw = (q ?? "").Trim().ToUpperInvariant();
        var items = State.Jiedu.List.AsEnumerable();
        if (nIssue != 0) items = items.Where(it => it.Issue == nIssue);
        if (kw.Length > 0)
            items = items.Where(it => (it.Q + "\n" + it.A).ToUpperInvariant().Contains(kw) || PinyinHelper.PyMatch(it.Q, kw));
        var list = items.ToList();
        return new JieduListResult { Issues = JieduIssues(), Total = list.Count, Items = list };
    }

    public JieduRemoveResult JieduRemoveIssue(string issue)
    {
        var n = int.TryParse(issue, out var x) ? x : 0;
        var before = State.Jiedu.List.Count;
        State.Jiedu.List = State.Jiedu.List.Where(it => it.Issue != n).ToList();
        Save();
        return new JieduRemoveResult { Removed = before - State.Jiedu.List.Count, Total = State.Jiedu.List.Count };
    }
}

/// <summary>解读辅导期次计数（原匿名类型）</summary>
public sealed class JieduIssueCount
{
    public int Issue { get; set; }
    public int Total { get; set; }
}

/// <summary>解读辅导检索结果（原匿名类型）</summary>
public sealed class JieduListResult
{
    public List<JieduIssueCount> Issues { get; set; }
    public int Total { get; set; }
    public List<JieduItem> Items { get; set; }
}

/// <summary>解读辅导期次删除结果（原匿名类型）</summary>
public sealed class JieduRemoveResult
{
    public int Removed { get; set; }
    public int Total { get; set; }
}
