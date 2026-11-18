using System.Text;
using System.Text.RegularExpressions;

namespace Dip3Grouper;

/// <summary>一条可被模型引用的证据。Tag（G1/J1/P1）是回答里必须回引的编号。</summary>
public sealed class PriceAiEvidence
{
    /// <summary>G1 / J1 / P1</summary>
    public string Tag { get; set; } = "";

    /// <summary>guide / jiedu / price</summary>
    public string Kind { get; set; } = "";

    /// <summary>来源名，如「立项指南 · 骨骼肌肉系统类」「解读辅导 第 3 期」「德州市落地价」</summary>
    public string Source { get; set; } = "";

    /// <summary>条目名 / 问答标题</summary>
    public string Title { get; set; } = "";

    /// <summary>送进模型的条文正文（已截断）</summary>
    public string Text { get; set; } = "";

    /// <summary>检索得分（仅用于排序，界面可显示）</summary>
    public int Score { get; set; }

    /// <summary>引用面板用的一行摘要</summary>
    public string OneLine => $"[{Tag}] {Source} · {Title}";
}

/// <summary>一轮对话（只存角色与文本，不落盘）。</summary>
public sealed class PriceAiTurn
{
    public string Role { get; set; } = "";   // user / assistant
    public string Content { get; set; } = "";
}

/// <summary>一次提问组装好的全部上下文。</summary>
public sealed class PriceAiAsk
{
    public string Question { get; set; } = "";
    public string Region { get; set; } = "";
    public List<PriceAiEvidence> Evidences { get; set; } = new();
    public string SystemPrompt { get; set; } = "";
    public string UserPrompt { get; set; } = "";
    public string Status { get; set; } = "";
    public bool HasLocalPrice { get; set; }

    /// <summary>
    /// 本轮没有找到与问题里项目名完全一致的条目，[G] 证据均为近似条文。
    /// 提示词会据此强制模型先声明不确定性，避免「拿子宫内翻复位费的规定回答骨折复位」。
    /// </summary>
    public bool FuzzyOnly { get; set; }

    public int GuideCount { get; set; }
    public int JieduCount { get; set; }
    public int PriceCount { get; set; }

    /// <summary>证据区文本（界面「本轮引用」可直接展示）</summary>
    public string EvidenceText => string.Join("\n\n", Evidences.Select(e => $"[{e.Tag}] {e.Source} · {e.Title}\n{e.Text}"));
}

/// <summary>
/// 「物价解读 AI」的本地检索与提示词装配（RAG）。
///
/// <para><b>为什么必须走本地检索</b>：直接把问题丢给大模型，它会凭常识编出文号、价格和比例。
/// 这里先从本机三份真实资料里检索出带编号的证据，再在 SystemPrompt 里规定
/// 「只能引用这些编号、证据里没有的金额一律不得给出、证据不足就明说没检索到」。</para>
///
/// <para>三份资料：① <c>data/lixiang.json</c> 立项指南（40 类 / 2627 行）；
/// ② 同文件里的解读辅导（13 期 / 224 条问答）；③ <c>data/dezhou-price.json</c> 德州市落地价
/// —— 只有地区命中「德州」时才注入，否则明确告知模型本地区价格表未收录、禁止报金额。</para>
///
/// <para>纯函数为主，不碰网络、不写盘，便于自检直接断言。</para>
/// </summary>
public static class PriceAiKnowledge
{
    /// <summary>单条证据正文上限，防止长条文把上下文撑爆</summary>
    private const int MaxEvidenceChars = 900;

    /// <summary>证据总长上限（约 6~8k 汉字，留足回答空间）</summary>
    private const int MaxTotalChars = 12000;

    /// <summary>低于此分数视为无关，宁可少给证据也不给噪音</summary>
    private const int MinScore = 6;

    /// <summary>本地落地价库目前只覆盖德州市，地区不命中就不注入价格证据</summary>
    public static bool LocalPriceAvailable(string region)
        => (region ?? "").Contains("德州", StringComparison.Ordinal);

    // 疑问词 / 泛词：2-gram 切片会把它们切出来，若不排掉会命中所有条文
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "的", "了", "吗", "呢", "是", "在", "有", "和", "与", "或", "能", "不", "可", "以", "为",
        "把", "被", "对", "从", "到", "按", "该", "这", "那", "我", "你", "他", "它", "们",
        "如何", "怎么", "什么", "哪些", "多少", "是否", "可以", "不能", "能不", "可不", "不可",
        "一下", "一个", "一次", "请", "问", "帮我", "我想", "咨询", "一下", "还有", "另外",
        "关于", "以及", "并且", "但是", "如果", "需要", "要不要", "有没有", "怎么", "怎样",
        "医院", "收费", "物价", "规定", "解读", "标准", "问题", "回答", "说明", "情况",
        "什么", "为何", "为啥", "哪里", "哪个", "几", "元", "钱", "价格", "费用",
        // 这些是提问里的万能词，几乎不会出现在「项目名称」里，却会在长条文里反复出现。
        // 不排掉的话「换药项目的价格构成包含哪些内容」会被一堆长说明挤到后面去。
        "项目", "内容", "包含", "哪些", "构成", "相关", "方面", "主要", "其中",
        "进行", "使用", "以及", "可以", "应该", "是否", "还有", "一些", "怎样",
    };

    private static readonly Regex TokenSplit = new(@"[^\u4e00-\u9fffA-Za-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex HasLatin = new(@"[A-Za-z0-9]", RegexOptions.Compiled);

    /// <summary>
    /// 从自然语言问题里抽检索词：中文按连续块整体 + 2/3-gram 切片，编码/英文整块保留。
    /// 长词权重更高，因此「基本物耗」比「基本」「物耗」更能把相关条目顶上来。
    /// </summary>
    public static List<(string Word, int Weight)> Keywords(string question)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        void Add(string w, int weight)
        {
            if (w.Length < 2 || StopWords.Contains(w)) return;
            w = w.ToUpperInvariant();
            if (!map.ContainsKey(w) || map[w] < weight) map[w] = weight;
        }

        foreach (var raw in TokenSplit.Split(question ?? ""))
        {
            var token = LixiangStore.NormName(raw);
            if (token.Length < 2) continue;

            if (HasLatin.IsMatch(token)) { Add(token, 4); continue; }

            // 整块（长度 > 8 的长问句不整体入库，只靠切片）
            if (token.Length <= 8) Add(token, token.Length >= 4 ? 4 : 3);

            // 2/3/4-gram：应对「换药包能不能另收」这类混在句子里的项目名。
            // 4-gram 权重给到 3，让「手法复位」这种连续长词比零散 2 字更能区分条目。
            for (var i = 0; i + 2 <= token.Length; i++) Add(token.Substring(i, 2), 1);
            if (token.Length >= 4)
                for (var i = 0; i + 3 <= token.Length; i++) Add(token.Substring(i, 3), 2);
            if (token.Length >= 5)
                for (var i = 0; i + 4 <= token.Length; i++) Add(token.Substring(i, 4), 3);
        }

        return map.Select(kv => (kv.Key, kv.Value)).OrderByDescending(x => x.Value).ToList();
    }

    private static int FieldScore(string field, List<(string Word, int Weight)> keys, int weight)
    {
        if (string.IsNullOrEmpty(field)) return 0;
        var norm = LixiangStore.NormName(field).ToUpperInvariant();
        var s = 0;
        foreach (var k in keys)
            if (norm.Contains(k.Word, StringComparison.Ordinal)) s += weight * k.Weight;
        return s;
    }

    private static string Cut(string s, int max = MaxEvidenceChars)
    {
        var v = (s ?? "").Trim();
        return v.Length <= max ? v : v[..max] + "…（原文过长已截断）";
    }

    private static string Field(string label, string value)
        => string.IsNullOrWhiteSpace(value) ? "" : $"{label}：{value.Trim()}; ";

    /// <summary>
    /// 检索立项指南条目（全类别），返回按相关度排序的候选。
    /// <paramref name="exact"/> 为问题中出现的、与项目全名相同的串时给大额加成。
    /// </summary>
    public static List<PriceAiEvidence> SearchGuides(AppState st, string question, int topK,
                                                     List<(string Word, int Weight)> keys = null)
    {
        keys ??= Keywords(question);
        var qNorm = LixiangStore.NormName(question ?? "");
        var list = new List<PriceAiEvidence>();

        foreach (var (_, sec) in st.Lixiang.State.Sections)
        {
            foreach (var r in sec.Rows)
            {
                // 项目名命中优先：问「换药」就该先给名称里带「换药」的条目，
                // 而不是让「价格构成/使用说明」很长的无关项目靠通用词刷分排到前面。
                // 正文累加封顶 30，避免长条文把名称命中的条目压下去。
                var nameScore = FieldScore(r.Name, keys, 12) * 10;
                var bodyScore = Math.Min(30,
                      FieldScore(r.Usage, keys, 4)
                    + FieldScore(r.ServiceOutput, keys, 3)
                    + FieldScore(r.PriceComposition, keys, 3)
                    + FieldScore(r.AddItems, keys, 3)
                    + FieldScore(r.ExtItems, keys, 3)
                    + FieldScore(r.Category, keys, 1)
                    + FieldScore(r.Unit, keys, 1));
                var score = nameScore + bodyScore;

                // 问题里直接写出了项目全名 —— 这是最确定的命中，必须置顶
                var name = LixiangStore.NormName(r.Name);
                if (name.Length >= 3 && qNorm.Contains(name, StringComparison.Ordinal)) score += 120;

                if (score < MinScore) continue;
                var text = Cut(
                    Field("项目类别", string.IsNullOrWhiteSpace(r.Category) ? sec.Label : r.Category)
                  + Field("计价单位", r.Unit)
                  + Field("服务产出", r.ServiceOutput)
                  + Field("价格构成", r.PriceComposition)
                  + Field("加收项", r.AddItems)
                  + Field("扩展项", r.ExtItems)
                  + Field("计价说明 / 使用说明", r.Usage));
                list.Add(new PriceAiEvidence
                {
                    Kind = "guide", Source = $"立项指南 · {sec.Label}", Title = r.Name,
                    Text = text, Score = score,
                });
            }
        }
        return list.OrderByDescending(e => e.Score).ThenBy(e => e.Title).Take(Math.Max(1, topK)).ToList();
    }

    /// <summary>检索解读辅导问答（13 期 224 条官方口径）。</summary>
    public static List<PriceAiEvidence> SearchJiedu(AppState st, string question, int topK,
                                                    List<(string Word, int Weight)> keys = null)
    {
        if (topK <= 0) return new List<PriceAiEvidence>();
        keys ??= Keywords(question);
        var list = new List<PriceAiEvidence>();
        foreach (var it in st.Lixiang.State.Jiedu?.List ?? new List<JieduItem>())
        {
            // 问答同样「问题命中优先」，答案正文只做次级排序且封顶
            var score = FieldScore(it.Q, keys, 10) * 10 + Math.Min(30, FieldScore(it.A, keys, 3));
            if (score < MinScore) continue;
            list.Add(new PriceAiEvidence
            {
                Kind = "jiedu", Source = $"解读辅导 第 {it.Issue} 期 · 第 {it.No} 问",
                Title = Cut(it.Q, 90), Text = Cut("问：" + it.Q + "\n答：" + it.A), Score = score,
            });
        }
        return list.OrderByDescending(e => e.Score).Take(topK).ToList();
    }

    /// <summary>
    /// 检索地区落地价。本地只收录德州市数据，其它地区一律返回空表
    /// （由 <see cref="Build"/> 在提示词里说明「本地区未收录，禁止报金额」）。
    /// </summary>
    public static List<PriceAiEvidence> SearchPrices(AppState st, string question, string region, int topK,
                                                     List<(string Word, int Weight)> keys = null)
    {
        if (topK <= 0 || !LocalPriceAvailable(region)) return new List<PriceAiEvidence>();
        keys ??= Keywords(question);
        var list = new List<PriceAiEvidence>();
        foreach (var it in st.Dezhou.Items)
        {
            var score = FieldScore(it.Name, keys, 12) * 10
                      + Math.Min(30,
                          FieldScore(it.Usage, keys, 4)
                        + FieldScore(it.ServiceOutput, keys, 3)
                        + FieldScore(it.PriceComposition, keys, 2)
                        + FieldScore(it.Note, keys, 3)
                        + FieldScore(it.Category, keys, 1));
            if (score < MinScore) continue;
            var (p3, p2, p1) = st.Dezhou.ResolvePrices(it);
            var text = Cut(
                Field("项目编码", it.Code) + Field("落地文号", it.Doc) + Field("执行日期", it.Exec)
              + Field("项目类别", it.Category) + Field("计价单位", it.Unit)
              + Field("三级价格(元)", p3) + Field("二级价格(元)", p2) + Field("一级价格(元)", p1)
              + Field("医保支付", it.Insurance) + Field("自付比例", it.SelfPay)
              + Field("计价说明", it.Usage) + Field("备注 / 限定支付范围", it.Note));
            list.Add(new PriceAiEvidence
            {
                Kind = "price", Source = $"德州市落地价 · {it.Doc}", Title = it.Name,
                Text = text, Score = score,
            });
        }
        return list.OrderByDescending(e => e.Score).Take(topK).ToList();
    }

    /// <summary>最长公共子串长度（归一化后的短串，O(n·m) 但 n、m 都不到 30 字）。</summary>
    private static int LongestCommon(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        var best = 0;
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                cur[j] = a[i - 1] == b[j - 1] ? prev[j - 1] + 1 : 0;
                if (cur[j] > best) best = cur[j];
            }
            (prev, cur) = (cur, prev);
            Array.Clear(cur, 0, cur.Length);
        }
        return best;
    }

    /// <summary>
    /// 证据里是否存在与问题中项目名**完全一致 / 主干一致**的立项指南条目。
    /// 没有就说明只找到近似条文 —— 「骨折手法复位术」在本库里不存在，
    /// 字面最像的是「子宫内翻手法复位费」，绝不能把它的规定张冠李戴。
    /// </summary>
    public static bool ExactProjectHit(string question, List<PriceAiEvidence> evidences)
    {
        var q = LixiangStore.NormName(question ?? "");
        if (q.Length < 3) return false;
        foreach (var e in evidences.Where(x => x.Kind == "guide"))
        {
            var n = LixiangStore.NormName(e.Title);
            if (n.Length < 3) continue;
            if (q.Contains(n, StringComparison.Ordinal)) return true;
            // 80% 主干覆盖：只命中「手法复位」这种通用四字，不足以把
            // 「包皮手法复位费」认成用户问的「骨折手法复位术」。
            if (LongestCommon(n, q) >= Math.Max(4, (int)Math.Ceiling(n.Length * 0.8))) return true;
        }
        return false;
    }

    /// <summary>组装 SystemPrompt：地区口径 + 证据 + 硬性引用规则。</summary>
    public static string BuildSystemPrompt(string region, List<PriceAiEvidence> evidences,
                                           bool hasLocalPrice, bool fuzzyOnly)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是「医疗服务价格项目立项指南」的物价规则解读助手，服务对象是中国医院物价/医保办工作人员。");
        sb.AppendLine($"当前咨询地区：{region}。");
        sb.AppendLine();
        sb.AppendLine("【唯一依据】下方【证据】区是本机资料库按本轮问题检索出的真实条文：");
        sb.AppendLine("- [G] 国家医疗服务价格项目立项指南（各类别的项目名称、计价单位、服务产出、价格构成、加收/扩展项、计价说明）");
        sb.AppendLine("- [J] 立项指南解读辅导（官方问答口径）");
        sb.AppendLine(hasLocalPrice
            ? "- [P] 山东省德州市落地价格表（本地资料库，含项目编码、三档价格、医保支付与限定支付范围）"
            : "- [P] 本轮没有：本地资料库尚未收录「" + region + "」的落地价格表。");
        sb.AppendLine();
        sb.AppendLine("【硬性规则】");
        sb.AppendLine("1. 每个结论后必须回引证据编号，如 [G1]、[J2]；只能引用本轮出现过的编号，不得杜撰。");
        sb.AppendLine("2. 证据里没有写的金额、项目编码、文号、自付比例、执行日期，一律不得给出；不要凭常识补全省市价格。");
        sb.AppendLine("3. 区分「计价规则」（能否另收、计价单位、加收/扩展项、价格构成、基本物耗）与「具体价格」。"
                     + (hasLocalPrice ? "" : " 本地区落地价未收录时，只讲规则，并明确说明本地价格表未收录、不报金额。"));
        sb.AppendLine("4. 证据之间口径不一致时，按 [G] 立项指南 > [J] 解读辅导 > [P] 地区落地价 说明差异，不要自行裁决，也不要把不同层级混成一句结论。");
        sb.AppendLine("5. 证据不足以回答时，直接回答「本机资料库未检索到相关条文」，并建议改用完整项目名到「立项指南」页核对；严禁生成看似合理的规定。");
        sb.AppendLine("6. 简体中文作答：先给结论，再列依据（带编号），最后写「需注意」；不复述问题、不写客套话、不输出 Markdown 表格。");
        sb.AppendLine("7. 本模块只做政策条文解读，不替代医院最终收费决策与医保结算结果。");
        sb.AppendLine();
        if (fuzzyOnly)
        {
            sb.AppendLine("【本轮检索提示 · 重要】本机资料库里**没有**与问题中的项目名完全一致的项目，"
                         + "下面的 [G] 证据只是字面相近的其它项目条文。");
            sb.AppendLine("回答开头必须明确说明「未检索到该项目的同名条目，以下为近似条文」，"
                         + "严禁把近似条文的计价单位、价格构成或说明当成该项目的规定；"
                         + "并建议用户核对完整项目名称后重新提问。");
            sb.AppendLine();
        }
        sb.AppendLine("【证据】");
        if (evidences.Count == 0)
        {
            sb.AppendLine("（本轮未检索到相关条文。请按规则 5 回答，不要依据常识作答。）");
        }
        else
        {
            foreach (var e in evidences)
            {
                sb.AppendLine($"[{e.Tag}] {e.Source} · {e.Title}");
                sb.AppendLine(e.Text);
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 一次提问的完整装配：检索 → 编号 → 裁剪 → SystemPrompt / UserPrompt / 状态文案。
    /// </summary>
    public static PriceAiAsk Build(AppState st, string question, PriceAiSettings cfg,
                                   List<PriceAiTurn> history = null)
    {
        cfg = PriceAiSettingsStore.Normalize(cfg);
        question = (question ?? "").Trim();
        var region = cfg.Region;

        // 独立自检 / 冷启动兜底：库为空时补一次载入（正常启动路径 App.State.Init 已加载，这里几乎不会触发）
        if (st.Lixiang.State.Sections.Count == 0) st.Lixiang.Load();
        if (st.Dezhou.Items.Count == 0) st.Dezhou.Load();

        var keys = Keywords(question);
        var hasLocal = LocalPriceAvailable(region);
        // 候选池比送进模型的条数大：判断「有没有同名项目」要看全池，
        // 否则真命中的条目只要排在 TopK 之外就会被误标成「仅近似条文」。
        var guidePool = SearchGuides(st, question, Math.Max(cfg.TopK, 24), keys);
        var guides = guidePool.Take(cfg.TopK).ToList();
        var jiedu = SearchJiedu(st, question, cfg.JieduK, keys);
        var prices = SearchPrices(st, question, region, cfg.PriceK, keys);

        // 统一编号 + 总长度预算：先按 G/J/P 顺序排，超预算就整条丢弃（不截断半条造成语义破碎）
        var all = new List<PriceAiEvidence>();
        var g = 0; var j = 0; var p = 0;
        var used = 0;
        foreach (var e in guides.Concat(jiedu).Concat(prices))
        {
            var tag = e.Kind switch { "guide" => "G" + (++g), "jiedu" => "J" + (++j), _ => "P" + (++p) };
            e.Tag = tag;
            if (used + e.Text.Length > MaxTotalChars) continue;
            used += e.Text.Length;
            all.Add(e);
        }

        var fuzzy = all.Count > 0 && !ExactProjectHit(question, guidePool);
        var ask = new PriceAiAsk
        {
            Question = question,
            Region = region,
            Evidences = all,
            HasLocalPrice = hasLocal,
            FuzzyOnly = fuzzy,
            GuideCount = all.Count(x => x.Kind == "guide"),
            JieduCount = all.Count(x => x.Kind == "jiedu"),
            PriceCount = all.Count(x => x.Kind == "price"),
            SystemPrompt = BuildSystemPrompt(region, all, hasLocal, fuzzy),
            UserPrompt = "请依据上述证据回答下列问题：\n" + question,
        };
        ask.Status = all.Count == 0
            ? $"未检索到相关条文 · 地区 {region}"
            : $"引用 指南 {ask.GuideCount} · 解读 {ask.JieduCount} · 落地价 {ask.PriceCount} · 地区 {region}";
        if (fuzzy) ask.Status += " · 未找到完全同名项目（近似条文）";
        if (!hasLocal) ask.Status += "（本地价格表未收录，不报金额）";
        return ask;
    }

    /// <summary>
    /// 组装发给模型的 messages：每轮都用**本轮最新检索结果**当 system，
    /// 再带最近 N 轮对话（只带文本，不带旧证据，避免上下文里出现过期编号）。
    /// </summary>
    public static List<PriceAiMessage> Messages(PriceAiAsk ask, List<PriceAiTurn> history, int turns)
    {
        var list = new List<PriceAiMessage> { new() { Role = "system", Content = ask.SystemPrompt } };
        if (turns > 0 && history != null)
        {
            // 只取成对的最近 N 轮，避免孤立的 user/assistant 造成角色错乱
            var pairs = new List<PriceAiTurn>();
            for (var i = 0; i + 1 < history.Count; i += 2)
                if (history[i].Role == "user" && history[i + 1].Role == "assistant")
                    pairs.AddRange(new[] { history[i], history[i + 1] });
            foreach (var t in pairs.Skip(Math.Max(0, pairs.Count - turns * 2)))
                list.Add(new PriceAiMessage { Role = t.Role, Content = t.Content });
        }
        list.Add(new PriceAiMessage { Role = "user", Content = ask.UserPrompt });
        return list;
    }
}
