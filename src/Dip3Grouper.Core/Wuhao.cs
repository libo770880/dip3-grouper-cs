using System.Text.Json.Serialization;

namespace Dip3Grouper;

/// <summary>基本物耗指引表头。</summary>
public sealed class WuhaoMeta
{
    public string Title { get; set; } = "基本物耗指引";
    public string Sub { get; set; } = "";
    public string Source { get; set; } = "";
    public string Updated { get; set; } = "";
    public int Count { get; set; }
    public int Cats { get; set; }
    public int EdgeCount { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>一条判断口径（25 号文与各类指南的共同规则）。</summary>
public sealed class WuhaoRule
{
    public string No { get; set; } = "";
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>物耗分组（消杀 / 敷料 / 注射 / 病理检验 / 口腔 …）。</summary>
public sealed class WuhaoGroup
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Desc { get; set; } = "";
    /// <summary>该分组下的条目数（载入时按 items 实算，网页端左侧要显示）。</summary>
    public int Count { get; set; }
}

/// <summary>一条基本物耗（不可单独收费）。Category 为列出它的指南类别名。</summary>
public sealed class WuhaoItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public List<string> Alias { get; set; } = new();
    public List<string> Cats { get; set; } = new();
    public int Count { get; set; }
    public string Note { get; set; } = "";

    [JsonIgnore] public string CatText => Cats is { Count: > 0 } ? string.Join("、", Cats) : "（未匹配到具体类别）";
    [JsonIgnore] public string CountText => $"{Count}/36 类";
}

/// <summary>边界清单：常见耗材到底能不能另收。</summary>
public sealed class WuhaoEdge
{
    public string Name { get; set; } = "";
    public string Cat { get; set; } = "";
    /// <summary>可另收 / 不可另收 / 需核对</summary>
    public string Verdict { get; set; } = "";
    public string Basis { get; set; } = "";
    public string Note { get; set; } = "";
}

/// <summary>一类指南的基本物耗条款原文（用于「口径依据」逐字核对）。</summary>
public sealed class WuhaoSource
{
    public string Cat { get; set; } = "";
    public string Key { get; set; } = "";
    public string Clause { get; set; } = "";
}

public sealed class WuhaoFile
{
    public WuhaoMeta Meta { get; set; } = new();
    public List<WuhaoRule> Rules { get; set; } = new();
    public List<WuhaoGroup> Groups { get; set; } = new();
    public List<WuhaoItem> Items { get; set; } = new();
    public List<WuhaoEdge> Edges { get; set; } = new();
    public List<WuhaoSource> Sources { get; set; } = new();
}

/// <summary>
/// 本机「手工维护」覆盖层（data/wuhao.user.json）。
/// 只存**增 / 改 / 删**的差异集，载入时盖在 wuhao.json 之上生效，
/// 不改 wuhao.json 本身、**不进 DataCatalog 白名单**（不随分发下发）。
/// </summary>
public sealed class WuhaoUserFile
{
    /// <summary>新增或修改的清单条目（Id 与 wuhao.json 相同则覆盖，否则新增）。</summary>
    public List<WuhaoItem> Items { get; set; } = new();
    /// <summary>新增或修改的边界条目（Name 相同则覆盖）。</summary>
    public List<WuhaoEdge> Edges { get; set; } = new();
    /// <summary>要从清单里删掉的条目 Id。</summary>
    public List<string> DeletedItems { get; set; } = new();
    /// <summary>要从边界清单里删掉的条目 Name。</summary>
    public List<string> DeletedEdges { get; set; } = new();
    /// <summary>最近一次保存时间（ISO 8601）。</summary>
    public string Updated { get; set; } = "";
}

/// <summary>判定结果（顶部「判定器」用）：命中基本物耗 / 命中边界清单 / 清单外。</summary>
public sealed class WuhaoVerdict
{
    /// <summary>no = 不可另收；yes = 可另收；check = 需核对；miss = 清单外</summary>
    public string Tone { get; set; } = "miss";
    public string Head { get; set; } = "";
    public string Sub { get; set; } = "";
    public string Basis { get; set; } = "";
    public string Note { get; set; } = "";
    public WuhaoItem Item { get; set; }
    public WuhaoEdge Edge { get; set; }
    public List<WuhaoItem> Near { get; set; } = new();
}

/// <summary>
/// 「基本物耗指引」库（data/wuhao.json）。数据来自德州市已导入的 36 类立项指南
/// 「使用说明」基本物耗条款 + 德医保发〔2026〕25 号通知正文，桌面端与网页端共用。
/// </summary>
public sealed class WuhaoStore
{
    public const string ToneNo = "no";
    public const string ToneYes = "yes";
    public const string ToneCheck = "check";
    public const string ToneMiss = "miss";
    /// <summary>可另收，但项目价必须同步减收（特殊功能耗材）</summary>
    public const string ToneDeduct = "deduct";

    public WuhaoMeta Meta { get; private set; } = new();
    public List<WuhaoRule> Rules { get; private set; } = new();
    public List<WuhaoGroup> Groups { get; private set; } = new();
    public List<WuhaoItem> Items { get; private set; } = new();
    public List<WuhaoEdge> Edges { get; private set; } = new();
    public List<WuhaoSource> Sources { get; private set; } = new();
    public bool Loaded { get; private set; }

    /// <summary>当前已生效的本机维护差异集（读写的 data/wuhao.user.json 内容）。</summary>
    public WuhaoUserFile User { get; private set; } = new();

    private readonly object _gate = new();

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
        var d = Js.ReadJsonFile<WuhaoFile>(AppPaths.WuhaoFile);
        if (d == null)
        {
            Meta = new WuhaoMeta();
            Rules = new List<WuhaoRule>();
            Groups = new List<WuhaoGroup>();
            Items = new List<WuhaoItem>();
            Edges = new List<WuhaoEdge>();
            Sources = new List<WuhaoSource>();
            User = new WuhaoUserFile();
            Loaded = true;
            return;
        }

        Meta = d.Meta ?? new WuhaoMeta();
        Rules = d.Rules ?? new List<WuhaoRule>();
        Groups = d.Groups ?? new List<WuhaoGroup>();
        Items = d.Items ?? new List<WuhaoItem>();
        Edges = d.Edges ?? new List<WuhaoEdge>();
        Sources = d.Sources ?? new List<WuhaoSource>();
        ApplyUserCover();
        // 分组条数按 items 实算：JSON 里不存这个字段，少一份能写错的数据。
        foreach (var g in Groups) g.Count = Items.Count(i => i.Group == g.Id);
        Loaded = true;
    }

    /// <summary>
    /// 把本机维护差异集（data/wuhao.user.json）盖在基础库之上：先删、再改、后增。
    /// 顺序很重要：删除列表里的对象才会被真正移除，否则覆盖会把它"救回来"。
    /// </summary>
    private void ApplyUserCover()
    {
        User = Js.ReadJsonFile<WuhaoUserFile>(AppPaths.WuhaoUserFile) ?? new WuhaoUserFile();

        // ① 删除（含基础库里的行）
        Items.RemoveAll(i => User.DeletedItems.Contains(i.Id));
        Edges.RemoveAll(e => User.DeletedEdges.Contains(e.Name));

        // ② 覆盖（与基础库同 Id / 同 Name 的直接替换）
        foreach (var it in User.Items.Where(x => x.Id != null))
        {
            var idx = Items.FindIndex(x => x.Id == it.Id);
            if (idx >= 0) Items[idx] = it;
        }
        // ③ 新增（Id 在基础库里不存在）
        foreach (var it in User.Items.Where(x => x.Id != null && Items.All(y => y.Id != x.Id)))
            Items.Add(it);

        foreach (var ed in User.Edges.Where(x => x.Name != null))
        {
            var idx = Edges.FindIndex(x => x.Name == ed.Name);
            if (idx >= 0) Edges[idx] = ed;
        }
        foreach (var ed in User.Edges.Where(x => x.Name != null && Edges.All(y => y.Name != x.Name)))
            Edges.Add(ed);
    }

    /// <summary>保存本机维护差异集：写 data/wuhao.user.json，并在内存里立即生效。</summary>
    /// 返回写入条数（含删除项），供界面提示。</summary>
    public int Save(WuhaoUserFile user)
    {
        lock (_gate)
        {
            User = user ?? new WuhaoUserFile();
            User.Updated = Js.Iso();
            var total = User.Items.Count + User.Edges.Count
                        + User.DeletedItems.Count + User.DeletedEdges.Count;
            Js.WriteJsonFile(AppPaths.WuhaoUserFile, User, Js.Config, 2);
            Load();   // 重载基础库 + 应用新覆盖，内存与磁盘逐字节一致
            return total;
        }
    }

    /// <summary>丢弃界面上的编辑，直接从磁盘重载（基础库 + 现有维护）。</summary>
    public void Reload()
    {
        lock (_gate) { Load(); }
    }

    /// <summary>按分组取条目；group 为空 / 全部时返回全部。</summary>
    public List<WuhaoItem> ByGroup(string group)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(group) || group == "全部") return Items;
        return Items.Where(i => i.Group == group).ToList();
    }

    public string GroupName(string id)
    {
        EnsureLoaded();
        return Groups.FirstOrDefault(g => g.Id == id)?.Name ?? id;
    }

    /// <summary>关键词检索（名称 / 别名 / 说明 / 指南类别）。</summary>
    public List<WuhaoItem> Search(string q, string group = null)
    {
        EnsureLoaded();
        var src = ByGroup(group);
        var kw = (q ?? "").Trim();
        if (kw.Length == 0) return src;

        var parts = kw.Split(new[] { ' ', '\t', '，', ',' }, StringSplitOptions.RemoveEmptyEntries);
        return src.Where(i => parts.All(p =>
        {
            var s = i.Name + " " + string.Join(" ", i.Alias) + " " + i.Note + " " + string.Join(" ", i.Cats);
            return s.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0;
        })).ToList();
    }

    /// <summary>边界清单里能对上这个词的条目（供「不可收费」页搜不到时给条出路）。</summary>
    public List<WuhaoEdge> EdgeHit(string q)
    {
        EnsureLoaded();
        var kw = (q ?? "").Trim().Replace(" ", "");
        if (kw.Length < 2) return new List<WuhaoEdge>();
        return Edges.Where(e =>
        {
            var nm = e.Name.Replace(" ", "");
            if (nm.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (kw.IndexOf(nm, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            // 「留置针 / 静脉留置针」这种一格多名的写法，逐段比一次
            return nm.Split('/', '、').Any(p => p.Length >= 2 &&
                (p.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 kw.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0));
        }).ToList();
    }

    /// <summary>边界清单筛选；verdict 为空 / 全部时返回全部。</summary>
    public List<WuhaoEdge> EdgeList(string verdict = null)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(verdict) || verdict == "全部") return Edges;
        return Edges.Where(e => e.Verdict == verdict).ToList();
    }

    /// <summary>
    /// 判定一个耗材能不能另收：
    /// ① 先看是否在基本物耗清单里（命中即不可另收）；
    /// ② 再看边界清单（可另收 / 不可另收 / 需核对）；
    /// ③ 都不命中 → 清单外，原则上可零差率另收，但要过「除外内容 / 材料目录 / 耗材编码」三关。
    /// </summary>
    public WuhaoVerdict Judge(string q)
    {
        EnsureLoaded();
        var kw = (q ?? "").Trim().Replace(" ", "");
        var v = new WuhaoVerdict { Sub = kw };

        if (kw.Length == 0)
        {
            v.Tone = ToneMiss;
            v.Head = "输入耗材名称开始判定";
            v.Basis = "例：纱布、一次性输液器、钢板、耦合剂、留置针";
            v.Note = "判断依据：36 类立项指南「使用说明」的基本物耗条款 + 德医保发〔2026〕25 号。";
            return v;
        }

        // ① 基本物耗清单。hitWord 是真正对上的那个叫法（可能是别名），用来提示「按哪个名字判的」。
        var hit = HitItem(kw, out var hitWord);
        var exact = hit != null && hitWord != null && Same(hitWord, kw);

        // ①½ 纯型号 / 修饰词单独查（「精密」「避光」「一次性」）：
        //     它可能指好几种东西（精密输液器？精密过滤器？），硬判「不可收」或「可收」都会错。
        //     早先落到「清单外 · 原则上可另收」，等于告诉一线「能收」，这是最危险的一格。
        if (!exact && IsModifier(kw))
        {
            v.Tone = ToneCheck;
            v.Head = "名称不全 · 请写耗材全称";
            v.Basis = $"「{kw}」只是型号 / 修饰词，不能单独判定。型号会影响收费口径：" +
                      "普通输液器属基本物耗、含在项目价里；避光、精密过滤等特殊功能型可以另收，但要同步减收项目价。";
            v.Note = "把品类一起写上再查，例：「精密输液器」「避光输液器」「一次性导尿包」。" +
                     "只写型号时，任何「可以收」的结论都不可信。";
            v.Near = NearItems(kw, null);
            return v;
        }

        // ② 边界清单：先取出来，好和「基本物耗命中」比谁更具体。
        //    例：「弹力绷带」既命中清单里的「绷带」（2 字），又命中边界清单的「医用弹力绷带」（4 字），
        //    后者是专门写过的口径，应当由它说了算；「一次性输液器」两边都判不可另收，取哪边都一样。
        //    一格多名的写法（「三腔导尿管 / 三腔气囊导尿管」）必须逐段比，否则写全一个叫法就落回宽条目。
        WuhaoEdge edge0 = null;
        var edgeLen = 0;
        var edgeExact = false;
        foreach (var e in Edges)
        {
            foreach (var w in EdgeWords(e))
            {
                if (Same(w, kw))
                {
                    edge0 = e; edgeLen = w.Length; edgeExact = true;
                    continue;
                }
                var len = w.Contains(kw) ? kw.Length
                        : kw.Contains(w) ? w.Length : 0;
                if (len > edgeLen) { edge0 = e; edgeLen = len; }
            }
        }

        if (hit != null && !edgeExact && edgeLen <= MatchedLen(hit, kw) + 1)
        {
            // 输入只是条目名的一截（「精密」对上别名「精密输液器」）时，标题里把话说明白，
            // 别让人以为系统凭空认了个东西；结论仍按该条目走（保守：判不可另收）。
            var partial = hitWord != null && kw.Length < hitWord.Length;
            v.Tone = ToneNo;
            v.Item = hit;
            v.Head = partial
                ? $"不可单独收费 · 按「{hitWord}」理解"
                : "不可单独收费 · 已含在项目价内";
            v.Sub = hit.Name;
            v.Basis = $"「{hit.Name}」列入 {hit.Count} 类指南的基本物耗清单（{hit.CatText}），成本已计入项目价格。";
            var body = string.IsNullOrWhiteSpace(hit.Note)
                ? "基本物耗成本计入项目价格，不得向患者另收，也不得让患者外购。"
                : hit.Note;
            v.Note = partial
                ? $"你输入的是「{kw}」，清单里对应的是「{hitWord}」；如果实际用的不是它，请写全称再查。\n" + body
                : body;
            v.Near = NearItems(kw, hit);
            return v;
        }

        // ② 边界清单
        var edge = edge0;
        if (edge != null)
        {
            v.Edge = edge;
            v.Tone = edge.Verdict switch
            {
                "可另收" => ToneYes,
                "可另收（减收）" => ToneDeduct,
                "不可另收" => ToneNo,
                _ => ToneCheck,
            };
            v.Head = edge.Verdict switch
            {
                "可另收" => "可另收 · 按实际采购价零差率",
                "可另收（减收）" => "可另收 · 但项目价须同步减收",
                "不可另收" => "不可另收 · 已含在项目价内",
                _ => "需核对 · 看目录与本院耗材编码",
            };
            v.Sub = edge.Name;
            v.Basis = edge.Basis;
            v.Note = edge.Note;
            return v;
        }

        // ③ 短词 / 型号词没命中：不能给「可以收」的结论。
        //    「精密」「避光」「一次性」这类单独输，既可能是名称没写全，也可能真的不在清单里，
        //    两种情况下随口说「可以收」都会害人，一律判「需核对」并让人补全。
        if (kw.Length <= 3 || IsModifier(kw))
        {
            v.Tone = ToneCheck;
            v.Head = "未收录 · 先把全称补全";
            v.Basis = $"「{kw}」没有命中 {Items.Count} 条基本物耗清单，也没有命中边界清单。" +
                      "这么短的词多半是型号或修饰语，单查会漏掉真正的条目（例如「精密」→「精密输液器」）。";
            v.Note = "两种可能：① 名称不全 —— 补全再查；② 确实没收录 —— 按三关核对：" +
                     "原项目「除外内容」是否列明 / 省·市「可另收费一次性材料目录」是否在列 / 本院耗材编码是否可用。" +
                     "「未收录」不等于「可以收费」。";
            v.Near = NearItems(kw, null);
            return v;
        }

        // ④ 清单外（名称已经写全，确实不在这两份清单里）
        v.Tone = ToneMiss;
        v.Head = "清单外 · 先过三关再谈收费";
        v.Basis = $"「{kw}」没有出现在 {Items.Count} 条基本物耗清单里，按 36 类指南不属于「基本物耗」；" +
                  "但「不在清单」不等于「可以直接收」。";
        v.Note = "过三关：① 原项目「除外内容」是否列明；② 省/市「可另收费的一次性材料目录」是否在列；" +
                 "③ 本院耗材编码是否可用。三关都过，才按实际采购价零差率另收。";
        v.Near = NearItems(kw, null);
        return v;
    }

    /// <summary>边界条目的各种叫法：整串 + 「/」「、」分隔的每一段（「三腔导尿管 / 三腔气囊导尿管」）。</summary>
    private static IEnumerable<string> EdgeWords(WuhaoEdge e)
    {
        var all = (e.Name ?? "").Replace(" ", "");
        if (all.Length >= 2) yield return all;
        foreach (var p in (e.Name ?? "").Split(new[] { '/', '、', '／' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = p.Trim().Replace(" ", "");
            if (t.Length >= 2 && t != all) yield return t;
        }
    }

    /// <summary>型号 / 修饰词：单独查没有意义，必须跟品类一起写（精密输液器、避光输液器）。</summary>
    private static bool IsModifier(string kw)    {
        if (string.IsNullOrEmpty(kw)) return false;
        var t = kw.Replace("型", "").Replace("式", "");
        return Modifiers.Any(m => t == m || (t.Length <= 4 && m.Contains(t) && t.Length >= 2));
    }

    private static readonly string[] Modifiers =
    {
        "精密", "避光", "一次性", "医用", "普通", "加强", "进口", "国产", "专用", "微量",
        "带针", "无针", "安全", "防针刺", "高值", "低值", "无菌", "灭菌",
    };

    /// <summary>命中清单里的哪一条；word 回传真正对上的叫法（名称或别名）。</summary>
    private WuhaoItem HitItem(string kw, out string word)
    {
        word = null;

        // 完全相等（名称优先，其次别名）
        foreach (var i in Items)
        {
            if (Same(i.Name, kw)) { word = i.Name; return i; }
            var ea = i.Alias.FirstOrDefault(a => Same(a, kw));
            if (ea != null) { word = ea; return i; }
        }

        // 输入是别名的一截（「精密」→「精密输液器」「避光」→「避光输液器」）：
        // 取最短的别名，越短越贴近输入，避免「各类消杀用品」抢走「碘伏」这类具体词。
        WuhaoItem best = null;
        var bestLen = int.MaxValue;
        foreach (var i in Items)
        {
            foreach (var a in i.Alias.Append(i.Name))
            {
                if (a.Length < 2) continue;
                if (a.Contains(kw) && a.Length < bestLen) { best = i; bestLen = a.Length; word = a; }
            }
        }
        if (best != null) return best;

        // 反过来：输入里带着清单条目（如「一次性使用输液器」命中「输液器」）：
        // 这次取最长的别名，越具体越准。
        bestLen = 0;
        foreach (var i in Items)
        {
            foreach (var a in i.Alias.Append(i.Name))
            {
                if (a.Length < 2) continue;
                if (kw.Contains(a) && a.Length > bestLen) { best = i; bestLen = a.Length; word = a; }
            }
        }
        return best;
    }

    /// <summary>命中该条目时实际对上的字数（用于和边界清单比「谁更具体」）。</summary>
    private static int MatchedLen(WuhaoItem it, string kw)
    {
        var len = 0;
        foreach (var a in it.Alias.Append(it.Name))
        {
            if (a.Length < 2) continue;
            if (Same(a, kw)) return Math.Max(len, a.Length + 10);   // 完全相等，优先级最高
            if (a.Contains(kw)) len = Math.Max(len, kw.Length);
            else if (kw.Contains(a)) len = Math.Max(len, a.Length);
        }
        return len;
    }

    private List<WuhaoItem> NearItems(string kw, WuhaoItem except)
    {
        var list = new List<WuhaoItem>();
        foreach (var i in Items)
        {
            if (ReferenceEquals(i, except)) continue;
            var s = i.Name + string.Join("", i.Alias);
            if (kw.Length >= 2 && s.Contains(kw.Substring(0, 2))) list.Add(i);
            if (list.Count >= 4) break;
        }
        return list;
    }

    private static bool Same(string a, string b)
    {
        static string Norm(string s) => (s ?? "").Replace(" ", "").Replace("（", "(").Replace("）", ")").Replace("／", "/");
        return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
    }
}
