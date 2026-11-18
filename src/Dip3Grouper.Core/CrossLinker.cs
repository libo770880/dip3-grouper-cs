using System.Text.RegularExpressions;

namespace Dip3Grouper;

/// <summary>跨库关联命中：Chip =「同名 / 相似」，Text = 名称 + 一行摘要。</summary>
public sealed class CrossLinkHit
{
    public string Chip { get; set; } = "";

    public string Text { get; set; } = "";

    /// <summary>命中条目所属库（guifan / dezhou / lixiang），用于点击跳转到对应页面</summary>
    public string Lib { get; set; } = "";

    /// <summary>命中条目 Id（立项指南是 Gid，跨区段唯一）</summary>
    public int Id { get; set; }
}

/// <summary>详情卡里的一整块关联（「关联·技术规范」等），标签 + 若干命中。</summary>
public sealed class CrossLinkGroup
{
    public string Label { get; set; } = "";
    public List<CrossLinkHit> Hits { get; set; } = new();
}

/// <summary>
/// 「德州物价 ↔ 立项指南 ↔ 项目技术规范」三库互相映射。
///
/// <para>原先只有桌面端有这份逻辑（<c>Dip3Grouper.Desktop/CrossMap.cs</c>），网页版的详情卡
/// 因此少了整段「关联·立项指南 / 关联·技术规范」。2026-10-30 搬到 Core，两端共用一份算法，
/// 桌面端只做 <c>LxBlock</c> 的转换。</para>
///
/// <para>事实依据（2026-10-03 全量测算）：
/// ① 德州物价主项 → 立项指南：名称精确命中 99.8%（德州落地的就是指南规范整合项目）；
/// ② 立项指南 ↔ 技术规范：国家在「规范整合」时普遍改名（心脏移植术 → 原位/异位心脏移植术…），
///    名称精确命中仅 1.2%，只能按字符二元组 Dice 相似度给「关联候选」（top1≥0.6 覆盖约 35%），
///    因此除同名直连外一律标注「相似」，供人工核对，不冒充官方映射。</para>
/// </summary>
public static class CrossLinker
{
    private sealed class Entry
    {
        public string Norm = "";
        public string Disp = "";
        public string Info = "";
        public int Id;
        public HashSet<string> Grams = new();
    }

    private sealed class Index
    {
        public string Lib = "";
        public List<Entry> Entries = new();
        public Dictionary<string, List<int>> Gram = new();      // 二元组倒排
        public Dictionary<string, List<int>> Exact = new();     // 同名直连（同名多义全收）
    }

    private static volatile Index _lx, _dz, _gf;
    private static string _stamp;
    private static AppState _owner;
    private static readonly object Lock = new();

    // ---------------- 对外 API ----------------

    /// <summary>立项指南行 → 关联的德州物价 / 技术规范</summary>
    public static List<CrossLinkGroup> ForLixiang(AppState state, string name)
    {
        Ensure(state);
        var groups = new List<CrossLinkGroup>();
        var dz = Query(_dz, name);
        if (dz.Count > 0) groups.Add(Blk("关联·德州物价", dz));
        var gf = Query(_gf, name);
        if (gf.Count > 0) groups.Add(Blk("关联·技术规范", gf));
        return groups;
    }

    /// <summary>德州物价行 → 关联的立项指南 / 技术规范</summary>
    public static List<CrossLinkGroup> ForDezhou(AppState state, string name)
    {
        Ensure(state);
        var groups = new List<CrossLinkGroup>();
        var lx = Query(_lx, name);
        if (lx.Count > 0) groups.Add(Blk("关联·立项指南", lx));
        var gf = Query(_gf, name);
        if (gf.Count > 0) groups.Add(Blk("关联·技术规范", gf));
        return groups;
    }

    /// <summary>技术规范条目 → 关联的立项指南 / 德州物价</summary>
    public static List<CrossLinkGroup> ForGuifan(AppState state, string name)
    {
        Ensure(state);
        var groups = new List<CrossLinkGroup>();
        var lx = Query(_lx, name);
        if (lx.Count > 0) groups.Add(Blk("关联·立项指南", lx));
        var dz = Query(_dz, name);
        if (dz.Count > 0) groups.Add(Blk("关联·德州物价", dz));
        return groups;
    }

    /// <summary>按库名分派（Web 接口用）。lib 取 lixiang / dezhou / guifan，其余返回空表。</summary>
    public static List<CrossLinkGroup> For(AppState state, string lib, string name)
        => lib switch
        {
            "lixiang" => ForLixiang(state, name),
            "dezhou" => ForDezhou(state, name),
            "guifan" => ForGuifan(state, name),
            _ => new List<CrossLinkGroup>(),
        };

    /// <summary>
    /// 预热索引（后台线程调用）：首次点开任一详情卡时才建索引会有 150ms 左右的停顿
    /// （规范库还要先读 11 MB 文件），启动后空闲时先建好，之后查关联是纯内存命中。
    /// </summary>
    public static void Warm(AppState state) => Ensure(state);

    /// <summary>三个索引的条目数（自检 / 诊断用）。</summary>
    public static (int Lx, int Dz, int Gf) Sizes(AppState state)
    {
        Ensure(state);
        return (_lx.Entries.Count, _dz.Entries.Count, _gf.Entries.Count);
    }

    // ---------------- 索引构建 ----------------

    /// <summary>
    /// 当前数据戳（区段数 / 指南行数 / 物价条数）。任一项变化 → 索引重建。
    ///
    /// <para>索引里存的是条目 <b>Id</b>，而 Id 会随导入、增删、替换而变化（指南的 Gid
    /// 更是每次 Reindex 整段重排）。原先「三个索引非空就直接返回」，导入新数据后
    /// 仍按旧 Id 指路，等于点 A 看 B。这里用计数当廉价指纹，绝大多数脏读都能拦住。</para>
    ///
    /// <para>不把技术规范条数算进指纹：取 <c>state.Guifan</c> 会强制加载 11 MB 文件，
    /// 而规范库只在 ReloadAll 里刷新，那条路径同时也会换掉指南 / 物价。</para>
    /// </summary>
    private static string Stamp(AppState state)
    {
        var lx = state.Lixiang.State;
        var rows = 0;
        foreach (var s in lx.Sections.Values) rows += s.Rows.Count;
        return $"{lx.Sections.Count}/{rows}|{state.Dezhou.Items.Count}";
    }

    private static void Ensure(AppState state)
    {
        var stamp = Stamp(state);
        // owner 也参与判定：同一个进程里换了 AppState 实例（单测 / 后台重载）时索引必须重建，
        // 否则会拿旧实例的 Id 去新实例里跳转。
        if (_lx != null && _dz != null && _gf != null && _stamp == stamp && ReferenceEquals(_owner, state)) return;
        lock (Lock)
        {
            if (_lx != null && _dz != null && _gf != null && _stamp == stamp && ReferenceEquals(_owner, state)) return;

            var lx = new Index { Lib = "lixiang" };
            foreach (var (tab, sec) in state.Lixiang.State.Sections)
            {
                var secLabel = string.IsNullOrWhiteSpace(sec.Label) ? tab : sec.Label;
                foreach (var r in sec.Rows)
                {
                    var disp = Flat(r.Name);
                    if (disp.Length == 0) continue;
                    var info = string.Join(" · ", new[]
                    {
                        r.Category, string.IsNullOrWhiteSpace(r.Unit) ? "" : "计价单位 " + Flat(r.Unit), secLabel,
                    }.Where(s => !string.IsNullOrWhiteSpace(s)));
                    // 跳转键用 Gid 而不是 Id：Id 只在区段内唯一，跨区段会串号（§9.25）
                    Add(lx, disp, info, r.Gid);
                }
            }

            var dz = new Index { Lib = "dezhou" };
            foreach (var it in state.Dezhou.Items)
            {
                var disp = Flat(it.Name);
                if (disp.Length == 0) continue;
                var kind = it.Kind switch
                {
                    "add" => "加收项",
                    "ext" => "扩展项",
                    _ => "主项",
                };
                var info = string.Join(" · ", new[]
                {
                    it.Code, P3(it), it.Doc, kind,
                }.Where(s => !string.IsNullOrWhiteSpace(s)));
                Add(dz, disp, info, it.Id);
            }

            var gf = new Index { Lib = "guifan" };
            foreach (var it in state.Guifan.Items)
            {
                var disp = Flat(it.Name);
                if (disp.Length == 0) continue;
                var score = it.Difficulty is { Length: > 0 } d && it.Risk is { Length: > 0 } r && it.HrValue is { Length: > 0 } h
                    ? $"难{d}/险{r}/耗{h}"
                    : "";
                var info = string.Join(" · ", new[]
                {
                    it.Code, it.Chapter, score,
                }.Where(s => !string.IsNullOrWhiteSpace(s)));
                Add(gf, disp, info, it.Id);
            }

            _lx = lx; _dz = dz; _gf = gf;
            _stamp = stamp;
            _owner = state;
        }
    }

    private static string P3(DezhouPriceItem it)
    {
        var ps = new[] { it.Price3, it.Price2, it.Price1 }.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        return ps.Count == 0 ? "" : string.Join("/", ps) + " 元";
    }

    private static void Add(Index idx, string disp, string info, int id)
    {
        var norm = Norm(disp);
        var e = new Entry { Norm = norm, Disp = disp, Info = info, Id = id, Grams = Grams(norm) };
        var at = idx.Entries.Count;
        idx.Entries.Add(e);
        if (!idx.Exact.TryGetValue(norm, out var ex)) idx.Exact[norm] = ex = new List<int>();
        ex.Add(at);
        foreach (var g in e.Grams)
        {
            if (!idx.Gram.TryGetValue(g, out var list)) idx.Gram[g] = list = new List<int>();
            list.Add(at);
        }
    }

    // ---------------- 检索：同名直连优先，其次 Dice ≥ 0.62 的 top3 ----------------

    private static List<CrossLinkHit> Query(Index idx, string name)
    {
        var norm = Norm(name);
        if (norm.Length < 3) return new List<CrossLinkHit>();
        if (idx.Exact.TryGetValue(norm, out var exact))
            return exact.Take(3)
                .Select(i => new CrossLinkHit
                {
                    Chip = "同名", Text = Line(idx.Entries[i]), Lib = idx.Lib, Id = idx.Entries[i].Id,
                })
                .ToList();

        var grams = Grams(norm);
        var cand = new Dictionary<int, int>();
        foreach (var g in grams)
            if (idx.Gram.TryGetValue(g, out var list))
                foreach (var i in list)
                    cand[i] = cand.TryGetValue(i, out var c) ? c + 1 : 1;

        var scored = new List<(int I, double S)>();
        foreach (var (i, overlap) in cand)
        {
            var e = idx.Entries[i];
            var s = 2.0 * overlap / (grams.Count + e.Grams.Count);
            if (s >= 0.62) scored.Add((i, s));
        }

        return scored.OrderByDescending(x => x.S)
            .ThenBy(x => idx.Entries[x.I].Disp.Length)
            .Take(3)
            .Select(x => new CrossLinkHit
            {
                Chip = "相似", Text = Line(idx.Entries[x.I]), Lib = idx.Lib, Id = idx.Entries[x.I].Id,
            })
            .ToList();
    }

    private static string Line(Entry e) => string.IsNullOrEmpty(e.Info) ? e.Disp : $"{e.Disp}　{e.Info}";

    private static CrossLinkGroup Blk(string label, List<CrossLinkHit> hits)
        => new() { Label = label, Hits = hits };

    // ---------------- 工具 ----------------

    private static string Norm(string s)
        => Regex.Replace(s ?? "", @"\s+", "").Replace("（", "(").Replace("）", ")");

    private static string Flat(string s)
        => Regex.Replace(s ?? "", @"\s*\n\s*", "").Trim();

    private static HashSet<string> Grams(string s)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (s.Length == 1) set.Add(s);
        else for (var i = 0; i < s.Length - 1; i++) set.Add(s.Substring(i, 2));
        return set;
    }
}
