using System.Text;

namespace Dip3Grouper;

/// <summary>一条命中的目录库外病种（供桌面端渲染预警）。</summary>
public sealed class OutLibHit
{
    public string DiagCode { get; set; } = "";
    public string DiagName { get; set; } = "";
    public string ProcCode { get; set; } = "";
    public string ProcName { get; set; } = "";
    public string Suggest { get; set; } = "";
    public string Note { get; set; } = "";
    /// <summary>序号（清单原表顺序，便于回查纸质清单）</summary>
    public int Seq { get; set; }

    /// <summary>清单键，如「H65-22.0200」；无手术行只显示诊断码。</summary>
    public string Label => string.IsNullOrEmpty(ProcCode) ? DiagCode : $"{DiagCode}-{ProcCode}";

    /// <summary>是否保守行（清单里只由诊断定义，不含手术）</summary>
    public bool Conservative => string.IsNullOrEmpty(ProcCode);

    /// <summary>一句话摘要：「H65 非化脓性中耳炎 ＋ 22.02 经自然孔的鼻窦抽吸或灌洗」</summary>
    public string Combo =>
        (string.IsNullOrEmpty(ProcCode)
            ? $"{DiagCode} {DiagName}（保守治疗，无手术）"
            : $"{DiagCode} {DiagName} ＋ {ProcCode} {ProcName}").Trim();
}

/// <summary>
/// 目录库外病种（省局反馈清单）的识别与规避建议。
///
/// <para><b>业务背景</b>：省局反馈一批「诊断 + 手术」组合，因全省结算量 &lt; 120 例，
/// 2027 年起不再作为 DIP 核心病种。病案首页若仍按这些组合填报，分组只能落到综合病组，
/// 直接影响结算与考核。工具侧要做两件事：</para>
/// <list type="number">
///   <item>分组时**认得出**这些组合并给出预警（<see cref="Find"/>）；</item>
///   <item>给出**同诊断在目录库内的可入组组合**供病案室核对填报（<see cref="Suggest"/>）。</item>
/// </list>
///
/// <para><b>为什么建议只取「并项 / 基础 / 基层」三层</b>：这三层只依赖 主诊断 + 主手术，
/// 病案室照着改就能入组；烧伤 / 肿瘤 / 结核三层还要求其他诊断（面积码、恶性肿瘤码）或耐药状态，
/// 把它们的行当建议等于告诉用户「改成这个就能入组」，而实际改完仍然入不了组。</para>
/// </summary>
public static class OutLibRules
{
    // ==================================================================
    // 编码归一
    // ==================================================================

    private static string N(string s) => (s ?? "").Trim().ToUpperInvariant();

    /// <summary>
    /// 手术码规范形：去掉小数点后无意义的尾零，并把 x 扩展位单独保留。
    /// <para>省局清单里写「22.02」「99.1」，病案首页常写「22.0200」「99.1000」；
    /// <see cref="Matcher.MatchProc"/> 是「精确 / 含 x 才前缀」的严格语义，
    /// 直接拿来比会漏掉绝大多数病例（实测 2109 行里只能命中 8 行）。
    /// 这里只做尾零归一，不做前缀吞并：22.02 仍不等于 22.03。</para>
    /// </summary>
    public static string Canon(string code)
    {
        var c = N(code);
        if (c.Length == 0) return "";
        var xi = c.IndexOf('X');
        var head = xi >= 0 ? c[..xi] : c;
        var tail = xi >= 0 ? c[xi..] : "";
        var dot = head.IndexOf('.');
        if (dot >= 0)
        {
            var intPart = head[..dot];
            var dec = head[(dot + 1)..].TrimEnd('0');
            head = dec.Length > 0 ? $"{intPart}.{dec}" : intPart;
        }
        return head + tail;
    }

    /// <summary>
    /// 清单码 ↔ 病案码的宽松匹配。
    /// <para>① 完全相同；② 尾零归一后相同（22.02 = 22.0200）；
    /// ③ 清单码是不带 x 的主码、病案码是其 x 扩展（04.9900 ← 04.9900x001）。</para>
    /// </summary>
    public static bool MatchProcLoose(string listCode, string caseCode)
    {
        var a = N(listCode);
        var b = N(caseCode);
        if (a.Length == 0 || b.Length == 0) return false;
        if (a == b) return true;
        if (Canon(a) == Canon(b)) return true;
        // 主码 + x 扩展：清单只写到主码时，病案的 xNNN 扩展要认回来
        if (!a.Contains('X') && b.StartsWith(a, StringComparison.Ordinal))
        {
            var next = b.Length > a.Length ? b[a.Length] : '\0';
            if (next is 'X' or '0' or '\0') return true;
        }
        return false;
    }

    // ==================================================================
    // 索引
    // ==================================================================

    /// <summary>
    /// 按诊断前 3 位（ICD-10 类目）分桶。清单 2109 行、单桶几十行，
    /// 比全表扫（每条病案 2109 次诊断匹配）快两个数量级 —— 批量分组动辄上千条。
    /// </summary>
    public static Dictionary<string, List<OutLibRow>> BuildIndex(IEnumerable<OutLibRow> rows)
    {
        var map = new Dictionary<string, List<OutLibRow>>(StringComparer.Ordinal);
        foreach (var r in rows ?? Enumerable.Empty<OutLibRow>())
        {
            var code = N(r?.DiagCode);
            if (code.Length < 3) continue;
            var key = code[..3];
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<OutLibRow>();
            list.Add(r);
        }
        return map;
    }

    /// <summary>
    /// 病案是否命中清单。传规范化后的病案（<see cref="Dip3Grouper.Normalize"/>）。
    /// <para>命中规则与病种库一致：诊断按 ICD-10 前缀匹配；手术按
    /// <see cref="MatchProcLoose"/>；清单行为空手术码时，只有「无主手术」的病案才算命中。</para>
    /// </summary>
    public static OutLibHit Find(Dictionary<string, List<OutLibRow>> index, Kase kase)
    {
        if (index == null || index.Count == 0 || kase == null) return null;
        var diag = N(kase.MainDiag);
        if (diag.Length < 3) return null;
        if (!index.TryGetValue(diag[..3], out var bucket)) return null;

        OutLibRow best = null;
        foreach (var r in bucket)
        {
            if (!Matcher.MatchDiag(r.DiagCode, diag)) continue;
            var hasProc = !string.IsNullOrEmpty(N(r.ProcCode));
            if (hasProc)
            {
                if (string.IsNullOrEmpty(kase.MainProc)) continue;
                if (!MatchProcLoose(r.ProcCode, kase.MainProc)) continue;
            }
            else if (!string.IsNullOrEmpty(kase.MainProc))
            {
                // 清单行是「仅诊断」病种：病案带了主手术就是另一个病种，不算命中
                continue;
            }
            // 同诊断多条命中时取诊断码更精确的那条（H65.0 优于 H65）
            if (best == null || N(r.DiagCode).Length > N(best.DiagCode).Length) best = r;
        }
        if (best == null) return null;
        return new OutLibHit
        {
            DiagCode = best.DiagCode,
            DiagName = best.DiagName,
            ProcCode = best.ProcCode,
            ProcName = best.ProcName,
            Suggest = best.Suggest,
            Note = best.Note,
            Seq = best.Seq,
        };
    }

    // ==================================================================
    // 规避建议
    // ==================================================================

    /// <summary>
    /// 算「同诊断在目录库内的可入组组合」。导入时落盘到 <see cref="OutLibRow.Suggest"/>，
    /// 后台可手工改写；运行时只读不重算（批量分组不会为此多扫 5000 行）。
    ///
    /// <para><b>排序为什么不是「配置里的先后」</b>：M17 膝关节病在基础库里有几十行，
    /// 按配置顺序取前 3 条会给出「周围神经破坏术 / 烧灼术」，与被预警的「腓骨截骨术」
    /// 差着十万八千里 —— 病案室看了只会觉得工具在胡说。这里按两级排序：
    /// ① 诊断码完全相同优先于前缀/类目码命中；② 手术码类目相同（77.27 → 77.xx）优先。
    /// 同为骨科的截骨术排在前面，才对得上「同一台手术该怎么编」这个真实问题。</para>
    /// </summary>
    public static string Suggest(Dip3Config cfg, string diagCode, string procCode, int max = 3)
    {
        if (cfg == null) return "";
        var diag = N(diagCode);
        if (diag.Length == 0) return "";
        var procCat = ProcCategory(procCode);

        var cands = new List<(int DiagRank, int ProcRank, string Text)>();
        void Take(string rowDiag, List<string> procs, string procName)
        {
            var diagRank = N(rowDiag) == diag ? 0 : 1;
            var names = (procName ?? "").Split('|').Select(x => x.Trim()).ToList();
            if (procs == null || procs.Count == 0)
            {
                cands.Add((diagRank, 9, $"{rowDiag} 保守治疗（无手术）"));
                return;
            }
            for (var i = 0; i < procs.Count; i++)
            {
                var p = N(procs[i]);
                if (p.Length == 0) continue;
                // 被预警的那个组合本身不能当建议（否则等于「建议继续这么填」）
                if (MatchProcLoose(procCode, p)) continue;
                var nm = i < names.Count && names[i].Length > 0 ? names[i] : (names.Count > 0 ? names[0] : "");
                // 手术类目是 ICD-9 两位大类（77.27 与 77.25 都归「77」）
                var sameCat = procCat.Length >= 2 && ProcCategory(p) == procCat ? 0 : 1;
                cands.Add((diagRank, sameCat, $"{rowDiag} ＋ {procs[i]} {nm}".Trim()));
            }
        }

        foreach (var r in cfg.Bingxiang ?? new List<BingxiangRow>())
        {
            var hit = (r.DiagCodes ?? new List<string>()).FirstOrDefault(d => Matcher.MatchDiag(d, diag));
            if (hit != null) Take(hit, r.ProcCodes, r.ProcName);
        }
        foreach (var r in cfg.Jichu ?? new List<DiagRow>())
        {
            var hit = (r.DiagCodes ?? new List<string>()).FirstOrDefault(d => Matcher.MatchDiag(d, diag));
            if (hit != null) Take(hit, r.ProcCodes, r.ProcName);
        }
        foreach (var r in cfg.Jiceng ?? new List<DiagRow>())
        {
            var hit = (r.DiagCodes ?? new List<string>()).FirstOrDefault(d => Matcher.MatchDiag(d, diag));
            if (hit != null) Take(hit, r.ProcCodes, r.ProcName);
        }

        var outp = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in cands.OrderBy(c => c.DiagRank).ThenBy(c => c.ProcRank))
        {
            if (!seen.Add(c.Text)) continue;
            outp.Add(c.Text);
            if (outp.Count >= Math.Max(1, max)) break;
        }
        return string.Join("；", outp);
    }

    /// <summary>手术码类目（ICD-9 前 3 位，如 77.27 → 77.）；不足 3 位返回空。</summary>
    private static string ProcCategory(string code)
    {
        var c = N(code);
        var dot = c.IndexOf('.');
        var head = dot >= 0 ? c[..dot] : c;
        return head.Length >= 2 ? head.PadLeft(2, '0') : "";
    }

    /// <summary>
    /// 行级备注（病种库表格里显示的那一列）。
    /// <para>刻意写得短：病种库列宽上限 280px、单元格是省略号截断，
    /// 「2027 年起不作核心病种（全省结算量 &lt;120 例）」这种写法在表里只看得到前半句。
    /// 完整来龙去脉放在分组结果卡与 README，表格里只留「这条为什么不能用」的结论。</para>
    /// </summary>
    public static string NoteFor(string suggest)
        => string.IsNullOrEmpty(suggest)
            ? "2027 年起不作核心病种；无同诊断病种"
            : "2027 年起不作核心病种";

    /// <summary>诊断行 + 手术行拼成清单键（导入时写日志用）。</summary>
    public static string KeyOf(string diagCode, string procCode)
    {
        var d = N(diagCode);
        var p = N(procCode);
        return p.Length == 0 ? d : $"{d}-{p}";
    }

    /// <summary>把清单行格式化成一行文本（CLI 输出 / 自检断言用）。</summary>
    public static string Describe(OutLibRow r)
    {
        var sb = new StringBuilder(KeyOf(r.DiagCode, r.ProcCode));
        if (!string.IsNullOrEmpty(r.DiagName)) sb.Append(' ').Append(r.DiagName);
        if (!string.IsNullOrEmpty(r.ProcName)) sb.Append(" ＋ ").Append(r.ProcName);
        return sb.ToString();
    }
}
