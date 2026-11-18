using System.Collections.Concurrent;
using System.Text.Json;
using Dip3Grouper;

// 本文件位于 namespace Dip3Grouper 内，而分组器类名也叫 Dip3Grouper，
// 直接写简单名会产生「命名空间 / 类型」二义性，故用别名指向类型本身。
using GrouperEngine = global::Dip3Grouper.Dip3Grouper;

namespace Dip3Grouper;

public class ProcLibItem
{
    public string Code { get; set; }
    public string Name { get; set; }
}

/// <summary>
/// 应用级共享状态（对齐 bin/web.js 的模块级单例）。
/// 原属 Dip3Grouper.Web，为让桌面客户端与 Web 端共用同一份实现而移入 Core。
/// </summary>
public class AppState
{
    public bool Jiceng { get; set; }
    public Dip3Config Config { get; set; }
    public GrouperEngine Grouper { get; set; }
    public MappingStore Mapping { get; } = new();
    public ZongheStore Zonghe { get; } = new();
    public LixiangStore Lixiang { get; } = new();
    /// <summary>德州市物价（立项指南规范整合项目）</summary>
    public DezhouPriceStore Dezhou { get; } = new();
    /// <summary>德州市物价「常用收藏」（data/dezhou-favorites.json）</summary>
    public DezhouFavoritesStore DezhouFav { get; } = new();
    /// <summary>
    /// 德州市物价「新项目征求意见」稿（data/dezhou-draft.json）—— 尚未正式发布、未生效的批次。
    /// 独立于 <see cref="Dezhou"/>，征求意见阶段前台只读展示、不参与任何计价与作废判定。
    /// </summary>
    public DezhouDraftStore DezhouDraft { get; } = new();

    /// <summary>
    /// 前台消息中心（data/notice.json）：后台在本机编辑发布，局域网内的前台页面轮询取回。
    /// 它是运营数据而非业务数据，因此**不进** <see cref="DataCatalog"/> 白名单，也不参与 ReloadAll。
    /// </summary>
    public NoticeStore Notice { get; } = new();

    private GuifanStore _guifan;
    private readonly object _gfGate = new();

    /// <summary>
    /// 全国医疗服务项目技术规范（2023 年版）—— 数据约 11 MB，首次使用时才载入，
    /// 避免给「从不打开该页面」的启动路径平白增加读盘与反序列化开销。
    /// 载入可并发触发（内部加锁，只会真正载入一次），因此可以放后台线程预热。
    /// </summary>
    public GuifanStore Guifan
    {
        get
        {
            GuifanStore store;
            lock (_gfGate) store = _guifan ??= new GuifanStore();
            store.EnsureLoaded();
            return store;
        }
    }
    /// <summary>
    /// 组织病理·送检指引（data/zhijian.json）—— 按疾病系统查标本、按蜡块档位定价。
    /// 体量只有几 KB，随初始化直接载入；桌面端弹窗与网页端面板共用本实例。
    /// </summary>
    public ZhijianStore Zhijian { get; } = new();

    /// <summary>
    /// 基本物耗指引（data/wuhao.json）—— 哪些耗材含在项目价内不能收、哪些可零差率另收。
    /// 数据来自 36 类立项指南「使用说明」的基本物耗条款，桌面端与网页端共用本实例。
    /// </summary>
    public WuhaoStore Wuhao { get; } = new();

    public List<ProcLibItem> ProcLib { get; private set; } = new();
    public ConcurrentDictionary<string, byte[]> Downloads { get; } = new();

    /// <summary>
    /// 初始化全部运行期数据。
    /// <paramref name="progress"/> 为可选的进度回传（百分比, 阶段文案），
    /// 桌面端用它驱动启动画面；Web 端不传，行为与之前完全一致。
    /// </summary>
    public void Init(bool jiceng, Action<int, string> progress = null)
    {
        Jiceng = jiceng;
        progress?.Invoke(8, "正在载入立项指南数据…");
        Lixiang.Load();
        progress?.Invoke(20, "正在载入德州市物价…");
        Dezhou.Load();
        DezhouFav.Load();
        // 征求意见稿的「新旧价格比对」要用旧版物价（45 号对码表）当比对基准，
        // 数据层之间不互相持有引用，由这里注入数据源（见 DezhouDraftStore.LegacySource）。
        DezhouDraft.LegacySource = () => Dezhou.Legacy;
        DezhouDraft.Load();
        Zhijian.Load();                    // 送检指引只有几 KB，随初始化一并载入
        Wuhao.Load();                      // 基本物耗清单同量级，一并载入
        progress?.Invoke(34, "正在载入病种库配置…");
        Config = Js.ReadJsonFile<Dip3Config>(AppPaths.ConfigFile) ?? new Dip3Config();
        progress?.Invoke(60, "正在初始化分组引擎…");
        Grouper = new GrouperEngine(Config, jiceng);
        progress?.Invoke(76, "正在接入综合病组规则…");
        Grouper.SetZonghe(Zonghe.Rows);
        progress?.Invoke(90, "正在构建手术编码库…");
        BuildProcLib();
        progress?.Invoke(100, "准备就绪");
    }

    /// <summary>重新加载病种库配置（后台导入后调用）</summary>
    public void ReloadConfig()
    {
        Config = Js.ReadJsonFile<Dip3Config>(AppPaths.ConfigFile) ?? new Dip3Config();
        Grouper.Cfg = Config;
        Grouper.RefreshExclusions();
        Grouper.RefreshOutLib();
        BuildProcLib();
    }

    /// <summary>
    /// 重新载入**全部**数据文件（局域网「数据分发中心」更新落地后调用），免重启。
    ///
    /// <para>注意三点：
    /// ① 技术规范（11 MB）只在「已经载入过」时才刷新 —— 更新完就为了没用过的页面
    ///    平白多读一次盘不划算，若它尚未载入，下次打开页面自然会读到新文件；
    /// ② 综合病组重载后必须重新喂给分组引擎（<see cref="GrouperEngine.SetZonghe"/>），
    ///    否则引擎里还是旧规则；
    /// ③ 本机私有数据（data/settings.json、data/dezhou-favorites.json）**不参与**，
    ///    它们不属于分发白名单，这里也一律不动。</para>
    /// </summary>
    public void ReloadAll(Action<int, string> progress = null)
    {
        progress?.Invoke(10, "正在重载立项指南…");
        Lixiang.Load();

        progress?.Invoke(30, "正在重载德州市物价…");
        Dezhou.Load();
        DezhouDraft.InvalidateCompare();   // 旧版物价换了，比对缓存必须一起失效
        DezhouDraft.Load();

        progress?.Invoke(40, "正在重载送检指引…");
        Zhijian.Load();
        Wuhao.Load();

        progress?.Invoke(45, "正在重载医保编码映射…");
        Mapping.Load();

        progress?.Invoke(55, "正在重载综合病组规则…");
        Zonghe.Load();
        Grouper.SetZonghe(Zonghe.Rows);

        progress?.Invoke(70, "正在重载病种库配置…");
        ReloadConfig();

        progress?.Invoke(85, "正在重载国家技术规范…");
        lock (_gfGate)
        {
            // 仅刷新已载入的实例；未载入则留给首次访问按需载入
            if (_guifan is { Loaded: true }) _guifan.Load();
        }

        progress?.Invoke(100, "数据已全部重载");
    }

    /// <summary>手术编码库: 汇总各区段 procCodes/procAlt + procName（病种库导入后重建）</summary>
    public void BuildProcLib()
    {
        var map = new Dictionary<string, ProcLibItem>();
        void Feed(IEnumerable<string> codes, string procName)
        {
            var names = (procName ?? "").Split('|').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            var list = codes.ToList();
            for (var i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (string.IsNullOrEmpty(c) || map.ContainsKey(c)) continue;
                map[c] = new ProcLibItem { Code = c, Name = i < names.Count ? names[i] : (names.Count > 0 ? names[0] : "") };
            }
        }
        // 先期分组行的手术码字段名为 procAlts（Node 版 buildProcLib 只读 procCodes/procAlt，故此处不贡献）
        foreach (var r in Config.Bingxiang) Feed(r.ProcCodes, r.ProcName);
        foreach (var r in Config.Jichu) Feed(r.ProcCodes, r.ProcName);
        foreach (var r in Config.Jiceng) Feed(r.ProcCodes, r.ProcName);
        foreach (var r in Config.FzShaoshang) Feed(r.ProcCodes, r.ProcName);
        foreach (var r in Config.FzZhongliu) Feed(r.ProcAlt, r.ProcName);
        ProcLib = map.Values.OrderBy(p => p.Code, StringComparer.Ordinal).ToList();
    }

    /// <summary>诊断映射转换: 主诊断/其他诊断经映射表转医保编码, 灰码保留原码并标记。</summary>
    public Dictionary<string, object> ApplyMapping(Kase kase)
    {
        var info = new Dictionary<string, object>
        {
            ["enabled"] = true,
            ["mainDiag"] = null,
            ["otherDiags"] = new List<object>(),
        };
        var t = Mapping.Translate(kase.MainDiag);
        info["mainDiag"] = MappingStore.TranslateToDict(t);
        if (!string.IsNullOrEmpty(t.To)) kase.MainDiag = t.To;

        var others = new List<object>();
        kase.OtherDiags = kase.OtherDiags.Select(d =>
        {
            var tr = Mapping.Translate(d);
            others.Add(MappingStore.TranslateToDict(tr));
            return string.IsNullOrEmpty(tr.To) ? d : tr.To;
        }).ToList();
        info["otherDiags"] = others;
        return info;
    }

    /// <summary>医保码对照 DIP 分组库预览: 每区段取首条命中（对齐 bin/web.js dipPreview）。</summary>
    public List<GrouperService.DipHit> DipPreview(string insCode)
    {
        var groups = new List<(string Type, List<object> Rows)>
        {
            ("并项", Config.Bingxiang.Cast<object>().ToList()),
            ("基础", Config.Jichu.Cast<object>().ToList()),
            ("基层", Config.Jiceng.Cast<object>().ToList()),
            ("烧伤", Config.FzShaoshang.Cast<object>().ToList()),
            ("肿瘤", Config.FzZhongliu.Cast<object>().ToList()),
            ("结核", Config.FzJiehe.Cast<object>().ToList()),
        };
        var hits = new List<GrouperService.DipHit>();
        foreach (var (type, rows) in groups)
        {
            foreach (var r in rows)
            {
                if (!TryDiagRow(r, out var diagCodes, out var seq, out var label)) continue;
                if ((diagCodes ?? new List<string>()).Any(d => Matcher.MatchDiag(d, insCode)))
                {
                    hits.Add(new GrouperService.DipHit { Type = type, Seq = seq, Name = label ?? "" });
                    break;
                }
            }
            if (hits.Count >= 4) break;
        }
        return hits;
    }

    private static bool TryDiagRow(object r, out List<string> diagCodes, out int seq, out string label)
    {
        switch (r)
        {
            case BingxiangRow x: diagCodes = x.DiagCodes; seq = x.Seq; label = FirstNonEmpty(x.DiagName, x.ProcName, null); return true;
            case DiagRow x: diagCodes = x.DiagCodes; seq = x.Seq; label = FirstNonEmpty(x.DiagName, x.ProcName, null); return true;
            case ShaoshangRow x: diagCodes = x.DiagCodes; seq = x.Seq; label = FirstNonEmpty(null, x.ProcName, null); return true;
            case ZhongliuRow x: diagCodes = x.DiagCodes; seq = x.Seq; label = FirstNonEmpty(x.DiagName, x.ProcName, null); return true;
            case JieheRow x: diagCodes = x.DiagCodes; seq = x.Seq; label = FirstNonEmpty(x.DiagName, x.ProcName, null); return true;
            default: diagCodes = null; seq = 0; label = null; return false;
        }
    }

    private static string FirstNonEmpty(params string[] vals)
    {
        foreach (var v in vals) if (!string.IsNullOrEmpty(v)) return v;
        return "";
    }

    /// <summary>结果卡名称补全: detail 中主要诊断/主要手术操作 显示为 名称（编码）</summary>
    public void WithDetailNames(GroupResult result)
    {
        var d = result?.Detail;
        if (d == null) return;
        string ProcNameOf(string code)
        {
            if (string.IsNullOrEmpty(code)) return "";
            var c = code.ToUpperInvariant();
            var hit = ProcLib.FirstOrDefault(p => (p.Code ?? "").ToUpperInvariant() == c);
            return hit?.Name ?? "";
        }
        string Fmt(string code, string name) => string.IsNullOrEmpty(name) ? code : $"{name}（{code}）";

        if (d.TryGetValue("mainDiag", out var md) && md is string mds && mds.Length > 0)
            d["mainDiag"] = Fmt(mds, Mapping.NameOfCode(mds));
        if (d.TryGetValue("mainProc", out var mp) && mp is string mps && mps.Length > 0)
            d["mainProc"] = Fmt(mps, ProcNameOf(mps));
    }
}
