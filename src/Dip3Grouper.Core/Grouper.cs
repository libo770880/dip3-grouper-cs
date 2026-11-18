using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dip3Grouper;

/// <summary>
/// DIP 3.0 分组器引擎（对齐 src/grouper.js）
///
/// 分组流程（优先级从高到低）:
///   1. 先期分组（XQ）: 手术特征组合 / 新生儿出生体重档
///   2. 诊断辅助细分（FZ）: 烧伤、肿瘤、结核
///   3. 并项规则核心病种（BX）: 主诊断 + 主手术（+ 相关手术）
///   4. 基础规则核心病种（JC）: 主诊断 + 主手术 / 仅主诊断（保守治疗）
///   5. 基层病种（可选, 基层医疗机构场景）
///   6. 综合病组（ZH）兜底
/// </summary>
public class Dip3Grouper
{
    public static class Type
    {
        public const string XQ = "先期分组";
        public const string FZ = "诊断辅助细分";
        public const string BX = "并项规则核心病种";
        public const string JC = "基础规则核心病种";
        public const string JICENG = "基层病种";
        public const string ZH = "综合病组";
    }

    private static readonly Regex RangeRe = new(@"^([A-Z]\d{2})(?:\s*-\s*([A-Z]\d{2}))?$", RegexOptions.Compiled);
    private static readonly Regex CatPrefixRe = new(@"^([A-Z]\d{2})", RegexOptions.Compiled);

    public Dip3Config Cfg { get; set; }
    public bool TryJiceng { get; set; }
    public List<ZongheRow> Zonghe { get; private set; } = new();

    private HashSet<string> _exclDiagSet = new();
    private Dictionary<string, ExcludedProc> _exclProcMap = new();
    private List<(ZongheRow Row, List<(string Lo, string Hi)> Ranges)> _zongheParsed = new();

    /// <summary>目录库外病种索引（按诊断类目分桶）；配置实例变了就重建。</summary>
    private Dictionary<string, List<OutLibRow>> _outLibIndex;
    private Dip3Config _outLibCfg;
    /// <summary>当前索引里的清单条数（诊断 / 自检展示用）。</summary>
    public int OutLibRuleCount { get; private set; }

    public Dip3Grouper(Dip3Config config, bool tryJiceng = false)
    {
        Cfg = config;
        TryJiceng = tryJiceng;
        RefreshExclusions();
        RefreshOutLib();
    }

    /// <summary>热更新综合病组行（后台增删改后调用）</summary>
    public void SetZonghe(IEnumerable<ZongheRow> rows)
    {
        Zonghe = rows?.ToList() ?? new List<ZongheRow>();
        _zongheParsed = Zonghe.Select(row => (
            row,
            (row.Ranges ?? new List<string>()).Select(ParseRange).Where(r => r.HasValue).Select(r => r.Value).ToList()
        )).ToList();
    }

    /// <summary>排除表增删改后重建索引</summary>
    public void RefreshExclusions()
    {
        _exclDiagSet = new HashSet<string>((Cfg?.ExcludedDiags ?? new List<CodeName>()).Select(x => Matcher.NormalizeCode(x?.Code)));
        _exclProcMap = new Dictionary<string, ExcludedProc>();
        foreach (var x in Cfg?.ExcludedProcs ?? new List<ExcludedProc>())
        {
            var k = Matcher.NormalizeCode(x?.Code);
            if (!_exclProcMap.ContainsKey(k)) _exclProcMap[k] = x;
        }
    }

    public bool IsDiagExcluded(string diagCode) => _exclDiagSet.Contains(Matcher.NormalizeCode(diagCode));

    /// <summary>
    /// 重建「目录库外病种」索引（后台增删改清单后调用）。索引只按诊断类目分桶，
    /// 2109 行重建约 1ms；但不能每条病案都重建 —— 批量分组动辄上千条。
    /// </summary>
    public void RefreshOutLib()
    {
        _outLibCfg = Cfg;
        _outLibIndex = OutLibRules.BuildIndex(Cfg?.OutLib);
        OutLibRuleCount = _outLibIndex.Values.Sum(v => v.Count);
    }

    /// <summary>索引随配置失效自动重建（<see cref="Cfg"/> 被整体替换时，如数据分发落地）。</summary>
    private void EnsureOutLib()
    {
        if (_outLibIndex != null && ReferenceEquals(_outLibCfg, Cfg)) return;
        RefreshOutLib();
    }

    /// <summary>病案是否命中省局反馈的「目录库外病种」组合；未命中返回 null。</summary>
    public OutLibHit FindOutLib(Kase kase)
    {
        EnsureOutLib();
        return OutLibRules.Find(_outLibIndex, kase ?? new Kase());
    }

    public ExcludedProc FindExcludedProc(string procCode)
        => _exclProcMap.TryGetValue(Matcher.NormalizeCode(procCode), out var v) ? v : null;

    // ------------------------------------------------------------------
    // 工具
    // ------------------------------------------------------------------

    private static (string Lo, string Hi)? ParseRange(string r)
    {
        var s = (r ?? string.Empty).Trim().ToUpperInvariant();
        var m = RangeRe.Match(s);
        if (!m.Success) return null;
        return (m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : m.Groups[1].Value);
    }

    private static string CategoryOf(string code)
    {
        var c = Matcher.NormalizeCode(code);
        var m = CatPrefixRe.Match(c);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static double? ToAgeDays(double age, string ageUnit)
    {
        var n = age;
        switch ((ageUnit ?? string.Empty).Trim())
        {
            case "天":
            case "日": return n;
            case "小时": return Math.Floor(n / 24);
            case "月": return Math.Floor(n * 30);
            case "岁":
            case "年": return Math.Floor(n * 365);
            default: return n >= 1 ? Math.Floor(n * 365) : Math.Floor(n);
        }
    }

    /// <summary>规范化病案入参（支持中英文列名）: 读取 + 大写规范化。</summary>
    public static Kase NormalizeCase(IDictionary<string, JsonElement> input) => Normalize(ReadCase(input));

    /// <summary>
    /// 读取病案入参（不做大写规范化）。对齐 Node 端 <c>grouper.group</c> 之前的原始 kase 语义，
    /// 便于 applyMapping 保留原始大小写（translate.from）与批量结果回显原始编码。
    /// </summary>
    public static Kase ReadCase(IDictionary<string, JsonElement> input)
    {
        input ??= new Dictionary<string, JsonElement>();

        static bool HasValue(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => false,
            JsonValueKind.String => e.GetString() != "",
            _ => true,
        };

        JsonElement? Pick(params string[] keys)
        {
            foreach (var k in keys)
                if (input.TryGetValue(k, out var v) && HasValue(v)) return v;
            return null;
        }

        List<string> ToList(JsonElement? v)
        {
            var outList = new List<string>();
            if (v == null) return outList;
            var e = v.Value;
            if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in e.EnumerateArray())
                {
                    var s = CaseValue.String(item).Trim();
                    if (s.Length > 0) outList.Add(s);
                }
                return outList;
            }
            var raw = CaseValue.String(e);
            foreach (var part in Regex.Split(raw, @"[|;；,，、\n]"))
            {
                var s = part.Trim();
                if (s.Length > 0) outList.Add(s);
            }
            return outList;
        }

        var ageRaw = Pick("ageDays", "年龄天数", "age", "年龄");
        double? ageDays = null;
        if (ageRaw != null)
        {
            if (Pick("ageDays", "年龄天数") != null)
            {
                var n = CaseValue.Number(ageRaw.Value);
                ageDays = n;
            }
            else
            {
                var n = CaseValue.Number(ageRaw.Value);
                ageDays = n.HasValue
                    ? ToAgeDays(n.Value, CaseValue.String(Pick("ageUnit", "年龄单位") ?? default))
                    : null;
            }
        }

        var bw = Pick("birthWeight", "出生体重", "出生体重(g)", "出生体重克");
        double? birthWeight = null;
        if (bw != null)
        {
            var n = CaseValue.Number(bw.Value);
            if (n.HasValue) birthWeight = n;
        }

        return new Kase
        {
            MainDiag = CaseValue.String(Pick("mainDiag", "主要诊断编码", "主要诊断") ?? default),
            OtherDiags = ToList(Pick("otherDiags", "其他诊断编码", "其他诊断")),
            MainProc = CaseValue.String(Pick("mainProc", "主要手术操作编码", "主要手术") ?? default),
            OtherProcs = ToList(Pick("otherProcs", "其他手术操作编码", "其他手术")),
            AgeDays = ageDays,
            BirthWeight = birthWeight,
            CaseId = Pick("caseId", "病案号", "ID", "id") is { } cid ? CaseValue.String(cid) : null,
        };
    }

    /// <summary>规范化: trim + 大写 + 过滤空项（对齐 Node normalizeCode / toList）。</summary>
    public static Kase Normalize(Kase k) => new()
    {
        MainDiag = Matcher.NormalizeCode(k.MainDiag),
        OtherDiags = (k.OtherDiags ?? new List<string>()).Select(Matcher.NormalizeCode).ToList(),
        MainProc = Matcher.NormalizeCode(k.MainProc),
        OtherProcs = (k.OtherProcs ?? new List<string>()).Select(Matcher.NormalizeCode).ToList(),
        AgeDays = k.AgeDays,
        BirthWeight = k.BirthWeight,
        CaseId = k.CaseId,
    };

    // ------------------------------------------------------------------
    // 主入口
    // ------------------------------------------------------------------

    public GroupResult Group(IDictionary<string, JsonElement> input)
    {
        var kase = ReadCase(input);
        return Group(kase);
    }

    public GroupResult Group(Kase kase) => Group(kase, false);

    /// <summary>
    /// 分组主入口。<paramref name="withTrace"/> 为 true 时额外产出
    /// <see cref="GroupResult.Trace"/>（逐层判定过程，供「分组溯源」界面回溯根因）；
    /// 关闭时行为与开销与既有实现完全一致（批量分组走这条路径）。
    /// </summary>
    public GroupResult Group(Kase kase, bool withTrace) => GroupCore(kase, withTrace ? new GroupTrace() : null);

    private GroupResult GroupCore(Kase kase, GroupTrace tr)
    {
        // 对齐 Node: group(input) 内部 normalizeCase(input)，不改写调用方传入的对象
        kase = Normalize(kase);
        var result = GroupLayers(kase, tr);
        // 目录库外病种是「事后旁注」：不改变分组走向，只在结果上挂预警。
        // 挂在最外层而不是各层 return 处 —— 六个命中分支 + 未入组分支都要带，
        // 逐处补等于给未来新增的分组层留一个「忘了加」的口子。
        result.OutLib = FindOutLib(kase);
        return result;
    }

    /// <summary>分组主流程（入参已规范化）。</summary>
    private GroupResult GroupLayers(Kase kase, GroupTrace tr)
    {
        var allProcs = new List<string>();
        if (!string.IsNullOrEmpty(kase.MainProc)) allProcs.Add(kase.MainProc);
        allProcs.AddRange(kase.OtherProcs.Where(p => !string.IsNullOrEmpty(p)));

        // ---- 0. 排除判定 ----
        var diagExcluded = IsDiagExcluded(kase.MainDiag);
        var exclProcInfo = !string.IsNullOrEmpty(kase.MainProc) ? FindExcludedProc(kase.MainProc) : null;
        var effectiveMainProc = exclProcInfo != null ? "" : kase.MainProc; // 主手术不纳入 → 按保守治疗
        var eff = kase.Clone();
        eff.MainProc = effectiveMainProc;

        if (tr != null)
        {
            var ex = tr.Add("排除表判定", "EXCL");
            ex.RuleCount = (Cfg?.ExcludedDiags?.Count ?? 0) + (Cfg?.ExcludedProcs?.Count ?? 0);
            ex.Fact("主要诊断", string.IsNullOrEmpty(kase.MainDiag) ? "（未填）"
                    : diagExcluded ? $"命中诊断排除表（{(Cfg?.ExcludedDiags?.Count ?? 0)} 条）" : "未命中排除表",
                diagExcluded ? "warn" : "ok");
            if (!string.IsNullOrEmpty(kase.MainProc))
                ex.Fact("主要手术", exclProcInfo != null
                        ? $"命中手术排除表 → 按保守治疗（{(string.IsNullOrEmpty(exclProcInfo.Note) ? "不纳入主手术" : exclProcInfo.Note)}）"
                        : "未命中排除表",
                    exclProcInfo != null ? "warn" : "ok");
            // 「warn」= 排除表命中并对分组产生影响（与分组命中 hit 区分，溯源 UI 用警示色）
            ex.Set(diagExcluded || exclProcInfo != null ? "warn" : "ok",
                diagExcluded ? "主诊断不纳入分组"
                : exclProcInfo != null ? "主手术不纳入分组，改按保守治疗判定"
                : "无排除命中，正常进入分层判定");
        }

        // ---- 1. 先期分组 ----
        var xqStep = tr?.Add("先期分组", "XQ");
        var xq = MatchXianqi(kase, allProcs, xqStep);
        if (xq != null) return Pack(xq, Type.XQ, "XQ", tr);

        // ---- 2. 诊断辅助细分 ----
        if (!diagExcluded)
        {
            var fzStep = tr?.Add("诊断辅助细分", "FZ");
            var fz = MatchFuzhu(eff, allProcs, fzStep);
            if (fz != null)
            {
                // 结核表中诊断编码为区间（如 A15-A16）的行属于综合病组
                return fz.Zonghe ? Pack(fz, Type.ZH, "ZH", tr) : Pack(fz, Type.FZ, "FZ", tr);
            }
        }
        else
        {
            tr?.Add("诊断辅助细分", "FZ").Set("skip", "主诊断命中排除表，本层跳过");
        }

        // ---- 3. 并项规则核心病种 ----
        if (!diagExcluded)
        {
            var bxStep = tr?.Add("并项规则", "BX");
            var bx = MatchBingxiang(eff, allProcs, bxStep);
            if (bx != null) return Pack(bx, Type.BX, "BX", tr);
        }
        else
        {
            tr?.Add("并项规则", "BX").Set("skip", "主诊断命中排除表，本层跳过");
        }

        // ---- 4. 基础规则核心病种 ----
        if (!diagExcluded)
        {
            var jcStep = tr?.Add("基础规则", "JC");
            var jc = MatchJichu(eff, jcStep);
            if (jc != null) return Pack(jc, Type.JC, "JC", tr);
        }
        else
        {
            tr?.Add("基础规则", "JC").Set("skip", "主诊断命中排除表，本层跳过");
        }

        // ---- 5. 基层病种（可选兜底） ----
        if (TryJiceng)
        {
            var jcbStep = tr?.Add("基层病种", "JICENG");
            var jcb = MatchJiceng(eff, jcbStep);
            if (jcb != null) return Pack(jcb, Type.JICENG, "JICENG", tr);
        }
        else
        {
            tr?.Add("基层病种", "JICENG").Set("skip", "基层病种兜底未启用（启动参数 --jiceng）");
        }

        // ---- 6. 综合病组 ----
        var zhStep = tr?.Add("综合病组", "ZH");
        var zh = MatchZonghe(kase, zhStep);
        if (zh != null) return Pack(zh, Type.ZH, "ZH", tr);

        // ---- 未入组原因 ----
        string reason;
        if (diagExcluded) reason = "主要诊断不纳入分组（按综合病种）";
        else if (exclProcInfo != null) reason = $"主要手术不纳入分组（{(string.IsNullOrEmpty(exclProcInfo.Note) ? "按保守治疗入组" : exclProcInfo.Note)}），未命中保守治疗病种";
        else if (string.IsNullOrEmpty(eff.MainProc) && !string.IsNullOrEmpty(kase.MainProc)) reason = "主手术被排除后按保守治疗仍未入组";
        else if (!string.IsNullOrEmpty(kase.MainDiag)) reason = "未命中核心病种（按综合病种）";
        else reason = "缺少主要诊断";

        var detail = new Dictionary<string, object>
        {
            ["mainDiag"] = kase.MainDiag ?? "",
            ["mainProc"] = kase.MainProc ?? "",
            ["diagExcluded"] = diagExcluded,
        };
        if (exclProcInfo != null) detail["excludedProc"] = exclProcInfo;

        return new GroupResult
        {
            Matched = false,
            GroupType = null,
            GroupTypeCode = null,
            Seq = null,
            Name = null,
            Detail = detail,
            Reason = reason,
            Trace = tr,
        };
    }

    // ------------------------------------------------------------------
    // 1. 先期分组
    // ------------------------------------------------------------------

    private Hit MatchXianqi(Kase kase, List<string> allProcs, GroupTraceStep st)
    {
        var rows = Cfg.Xianqi ?? new List<XianqiRow>();
        if (st != null) st.RuleCount = rows.Count;
        var hasAge = kase.AgeDays.HasValue;
        var hasBw = kase.BirthWeight.HasValue;

        // 溯源计数（不参与判定，仅用于「未命中」时给出卡在哪一步）
        var weightRows = 0; var weightDiagOk = 0;
        var featRows = 0; var featCombos = 0;
        var weightHit = false; var featHit = false;

        foreach (var row in rows)
        {
            // 新生儿体重档: 诊断含P07（不限定主要诊断）+ 年龄<29天 + 出生体重区间
            if (row.AgeDaysMax.HasValue)
            {
                if (st != null) weightRows++;
                var diagList = new List<string>();
                if (!string.IsNullOrEmpty(kase.MainDiag)) diagList.Add(kase.MainDiag);
                diagList.AddRange(kase.OtherDiags);

                var diagOk = diagList.Any(d => Matcher.MatchDiag("P07", d));
                var ageOk = hasAge && kase.AgeDays.Value <= row.AgeDaysMax.Value;
                if (st != null && diagOk && ageOk) weightDiagOk++;

                if (ageOk
                    && hasBw
                    && row.BirthWeightMin.HasValue && row.BirthWeightMax.HasValue
                    && kase.BirthWeight.Value >= row.BirthWeightMin.Value
                    && kase.BirthWeight.Value <= row.BirthWeightMax.Value
                    && diagOk)
                {
                    var d = new Dictionary<string, object>
                    {
                        ["rule"] = "新生儿出生体重分组",
                        ["ageDays"] = kase.AgeDays.Value,
                        ["birthWeight"] = kase.BirthWeight.Value,
                    };
                    if (!string.IsNullOrEmpty(row.BirthWeightNote)) d["birthWeightNote"] = row.BirthWeightNote;
                    if (st != null)
                    {
                        weightHit = true;
                        st.Fact("命中规则", "新生儿出生体重分档", "ok")
                          .Fact("日龄", $"{kase.AgeDays.Value} 天 ≤ 阈值 {row.AgeDaysMax.Value} 天")
                          .Fact("出生体重", $"{kase.BirthWeight.Value} g ∈ [{row.BirthWeightMin}–{row.BirthWeightMax}] g");
                        if (!string.IsNullOrEmpty(row.BirthWeightNote))
                            st.Fact("体重分档", row.BirthWeightNote);
                    }
                    return new Hit { Row = row, Seq = row.Seq, Name = row.Name, Detail = d };
                }
                continue;
            }

            // 手术特征分组: 备选列表任一命中（单码须为主手术; +组合须全部出现且主手术为其中之一）
            if (row.ProcAlts == null || row.ProcAlts.Count == 0) continue;
            if (st != null) featRows++;
            ProcAltHit hitInfo = null;
            var hit = row.ProcAlts.Any(alt =>
            {
                if (alt == null || alt.Count == 0) return false;
                if (alt.Count == 1)
                {
                    var ok = !string.IsNullOrEmpty(kase.MainProc) && Matcher.MatchProc(alt[0], kase.MainProc);
                    if (ok && hitInfo == null) hitInfo = new ProcAltHit { Alt = alt, IsCombo = false };
                    return ok;
                }
                var allIn = alt.All(pc => Matcher.AnyProcMatch(pc, allProcs));
                var mainIn = !string.IsNullOrEmpty(kase.MainProc) && alt.Any(pc => Matcher.MatchProc(pc, kase.MainProc));
                if (allIn && mainIn && hitInfo == null) hitInfo = new ProcAltHit { Alt = alt, IsCombo = true };
                return allIn && mainIn;
            });
            if (hit)
            {
                if (st != null)
                {
                    featHit = true;
                    st.Fact("命中规则", hitInfo is { IsCombo: true } ? "手术操作组合" : "主要手术操作", "ok")
                      .Fact("命中组合", string.Join(" + ", hitInfo?.Alt ?? new List<string>()));
                    if (!string.IsNullOrEmpty(row.Name)) st.Fact("规则行", $"{row.Name}（序号 {row.Seq}）");
                }
                return new Hit
                {
                    Row = row,
                    Seq = row.Seq,
                    Name = row.Name,
                    Detail = new Dictionary<string, object>
                    {
                        ["rule"] = row.ProcAlts.Any(a => a != null && a.Count > 1) ? "手术操作组合" : "主要手术操作",
                        ["procAlts"] = row.ProcAlts,
                    },
                };
            }
            if (st != null)
            {
                // 组合行：统计「缺哪个码」，这是最常见的未命中原因
                foreach (var alt in row.ProcAlts)
                {
                    if (alt == null || alt.Count < 2) continue;
                    var missing = alt.Where(pc => !Matcher.AnyProcMatch(pc, allProcs)).ToList();
                    if (missing.Count == 0) continue;
                    featCombos++;
                    if (featCombos <= 1)
                        st.Fact("最接近的组合", $"{string.Join(" + ", alt)}（缺 {string.Join("、", missing)}）", "warn");
                }
            }
        }

        if (st != null && !weightHit && !featHit)
        {
            if (weightRows > 0)
                st.Fact("体重档规则", $"{weightRows} 条 · 诊断与年龄同时满足 {weightDiagOk} 条",
                    weightDiagOk > 0 ? "ok" : "mute");
            if (weightRows > 0 && (!hasAge || !hasBw))
                st.Fact("缺项", $"未填写{(hasAge ? "" : "年龄")}{(!hasAge && !hasBw ? "、" : "")}{(hasBw ? "" : "出生体重")}，体重档无法判定", "warn");
            if (featRows > 0)
                st.Fact("手术特征规则", $"{featRows} 条 · 主手术命中 0 条",
                    string.IsNullOrEmpty(kase.MainProc) ? "warn" : "mute");
            if (featRows > 0 && string.IsNullOrEmpty(kase.MainProc))
                st.Fact("缺项", "未填写主要手术操作", "warn");
            if (weightRows == 0 && featRows == 0)
                st.Fact("规则", "配置中无可用先期分组行", "warn");
        }
        return null;
    }

    private sealed class ProcAltHit
    {
        public List<string> Alt { get; set; }
        public bool IsCombo { get; set; }
    }

    // ------------------------------------------------------------------
    // 2. 诊断辅助细分（烧伤/肿瘤/结核）
    // ------------------------------------------------------------------

    private Hit MatchFuzhu(Kase kase, List<string> allProcs, GroupTraceStep st)
    {
        var sRows = Cfg.FzShaoshang ?? new List<ShaoshangRow>();
        var zRows = Cfg.FzZhongliu ?? new List<ZhongliuRow>();
        var jRows = Cfg.FzJiehe ?? new List<JieheRow>();
        if (st != null) st.RuleCount = sRows.Count + zRows.Count + jRows.Count;

        // 溯源计数
        int sDiag = 0, sOther = 0, sProc = 0;
        int zDiag = 0, zOther = 0, zProc = 0;
        int jDiag = 0, jProc = 0, jResist = 0;

        // (a) 烧伤
        foreach (var row in sRows)
        {
            var dip = Matcher.MatchDiagCodeAny(row.DiagCodes, kase.MainDiag);
            if (dip == null) continue;
            if (st != null) sDiag++;
            if (!row.OtherDiagCodes.Any(oc => Matcher.AnyDiagMatch(oc, kase.OtherDiags))) continue;
            if (st != null) sOther++;
            if (!ProcCondOk(row.ProcCodes, row.RelProcs, kase)) continue;
            if (st != null)
            {
                sProc++;
                st.Fact("命中类别", "烧伤类", "ok")
                  .Fact("主诊断", $"{kase.MainDiag} → 命中 {dip}")
                  .Fact("程度 / 面积", $"{row.Degree}｜{row.Area}")
                  .Fact("其他诊断", string.Join("；", row.OtherDiagCodes))
                  .Fact("手术条件", row.ProcCodes.Count > 0 ? $"命中 {kase.MainProc}" : "保守治疗（无手术要求）");
            }
            return new Hit
            {
                Row = row,
                Seq = row.Seq,
                Name = $"烧伤（{row.Degree}｜{row.Area}）",
                Detail = new Dictionary<string, object>
                {
                    ["subType"] = "烧伤类",
                    ["mainDiag"] = kase.MainDiag,
                    ["dipDiagCode"] = dip,
                    ["degree"] = row.Degree,
                    ["area"] = row.Area,
                    ["otherDiagCodes"] = row.OtherDiagCodes,
                },
            };
        }

        // (b) 肿瘤: 组合越长越特异, 取最长命中
        ZhongliuRow best = null;
        string bestDip = null;
        foreach (var row in zRows)
        {
            var dip = Matcher.MatchDiagCodeAny(row.DiagCodes, kase.MainDiag);
            if (dip == null) continue;
            if (st != null) zDiag++;
            if (!row.OtherDiagCodes.Any(oc => Matcher.AnyDiagMatch(oc, kase.OtherDiags))) continue;
            if (st != null) zOther++;
            if (row.ProcAlt.Count == 0)
            {
                if (best == null || best.ProcAlt.Count == 0) { best = row; bestDip = dip; }
                continue;
            }
            if (Matcher.ComboMatch(row.ProcAlt, allProcs))
            {
                if (st != null) zProc++;
                if (best == null || row.ProcAlt.Count > best.ProcAlt.Count) { best = row; bestDip = dip; }
            }
        }
        if (best != null)
        {
            if (st != null)
            {
                st.Fact("命中类别", "肿瘤类", "ok")
                  .Fact("主诊断", $"{kase.MainDiag} → 命中 {bestDip}")
                  .Fact("病种", $"{best.DiagName}（{best.ProcName}）")
                  .Fact("其他诊断", "含恶性肿瘤编码")
                  .Fact("手术组合", best.ProcAlt.Count > 0 ? string.Join(" + ", best.ProcAlt) : "保守治疗（无手术要求）");
            }
            return new Hit
            {
                Row = best,
                Seq = best.Seq,
                Name = $"{best.DiagName}（{best.ProcName}）",
                Detail = new Dictionary<string, object>
                {
                    ["subType"] = "肿瘤类",
                    ["mainDiag"] = kase.MainDiag,
                    ["dipDiagCode"] = bestDip,
                    ["procAlt"] = best.ProcAlt,
                    ["procName"] = best.ProcName,
                },
            };
        }

        // (c) 结核
        var resistCodes = (Cfg.JieheResistCodes ?? new List<ResistCode>()).Select(c => c?.Code).ToList();
        var isResist = Matcher.AnyDiagAny(resistCodes, new List<string> { kase.MainDiag })
            || kase.OtherDiags.Any(d => Matcher.MatchDiag("U84.300", d));

        foreach (var row in jRows)
        {
            var dip = Matcher.MatchDiagCodeAny(row.DiagCodes, kase.MainDiag);
            if (dip == null) continue;
            if (st != null) jDiag++;
            if (!ProcCondOk(row.ProcCodes, null, kase)) continue;
            if (st != null) jProc++;
            if (row.DrugResistant != isResist) continue;
            if (st != null)
            {
                jResist++;
                var isRangeHit = row.DiagCodes.Any(d => (d ?? "").Contains('-'));
                st.Fact("命中类别", isRangeHit ? "结核类（区间行 → 综合病组）" : "结核类", "ok")
                  .Fact("主诊断", $"{kase.MainDiag} → 命中 {dip}")
                  .Fact("病种", row.DiagName)
                  .Fact("手术条件", row.ProcCodes.Count > 0 ? $"命中 {kase.MainProc}" : "保守治疗（无手术要求）")
                  .Fact("耐药状态", isResist ? "耐药" : "非耐药");
                if (!string.IsNullOrEmpty(row.ResistNote)) st.Fact("耐药说明", row.ResistNote);
            }
            var isRange = row.DiagCodes.Any(d => (d ?? "").Contains('-'));
            var procLabel = row.ProcCodes.Count > 0 ? row.ProcName : "保守治疗";
            return new Hit
            {
                Row = row,
                Seq = row.Seq,
                Zonghe = isRange,
                Name = $"{row.DiagName}（{procLabel}｜{(row.DrugResistant ? "耐药" : "非耐药")}）",
                Detail = new Dictionary<string, object>
                {
                    ["subType"] = "结核类",
                    ["diagCodes"] = row.DiagCodes,
                    ["mainDiag"] = kase.MainDiag,
                    ["dipDiagCode"] = dip,
                    ["mainProc"] = kase.MainProc ?? "",
                    ["drugResistant"] = isResist,
                    ["resistNote"] = row.ResistNote ?? "",
                },
            };
        }

        if (st != null)
        {
            st.Fact($"烧伤（{sRows.Count} 条）", $"诊断命中 {sDiag} · 其他诊断满足 {sOther}", sOther > 0 ? "ok" : "mute");
            st.Fact($"肿瘤（{zRows.Count} 条）", $"诊断命中 {zDiag} · 其他诊断满足 {zOther} · 手术组合满足 {zProc}", zProc > 0 ? "ok" : "mute");
            st.Fact($"结核（{jRows.Count} 条）", $"诊断命中 {jDiag} · 手术条件满足 {jProc} · 耐药状态匹配 {jResist}", jResist > 0 ? "ok" : "mute");
            if (sDiag > 0 && sOther == 0)
                st.Fact("烧伤提示", "主诊断命中烧伤编码，但其他诊断缺少面积码（T31/T32）", "warn");
            if (zDiag > 0 && zOther == 0)
                st.Fact("肿瘤提示", "主诊断命中肿瘤编码，但其他诊断缺少恶性肿瘤编码", "warn");
            if (jDiag > 0 && jProc == 0)
                st.Fact("结核提示", "主诊断命中结核编码，但手术条件不满足", "warn");
            if (jDiag > 0 && jProc > 0 && jResist == 0)
                st.Fact("结核提示", $"耐药状态与规则不符（本病例判定为{(isResist ? "耐药" : "非耐药")}）", "warn");
        }
        return null;
    }

    /// <summary>手术条件判定: 行主手术为空 → 保守治疗(有效主手术为空); 否则主手术须命中; 相关手术须在其他手术中。</summary>
    private static bool ProcCondOk(List<string> procCodes, List<List<string>> relProcs, Kase kase)
    {
        if (procCodes == null || procCodes.Count == 0)
            return string.IsNullOrEmpty(kase.MainProc);
        if (string.IsNullOrEmpty(kase.MainProc) || !procCodes.Any(pc => Matcher.MatchProc(pc, kase.MainProc))) return false;
        if (relProcs != null && relProcs.Count > 0)
        {
            var ok = relProcs.Any(alt => Matcher.ComboMatch(alt, kase.OtherProcs));
            if (!ok) return false;
        }
        return true;
    }

    // ------------------------------------------------------------------
    // 3. 并项规则
    // ------------------------------------------------------------------

    private Hit MatchBingxiang(Kase kase, List<string> allProcs, GroupTraceStep st)
    {
        var rows = Cfg.Bingxiang ?? new List<BingxiangRow>();
        if (st != null) st.RuleCount = rows.Count;
        BingxiangRow bestRel = null;   // 命中相关手术要求的行（更特异）, 序号最小优先
        BingxiangRow firstNoRel = null; // 未要求相关手术的行（兜底）
        int diagRows = 0, procRows = 0, relRows = 0, relNeeded = 0;
        foreach (var row in rows)
        {
            if (!Matcher.MatchDiagAny(row.DiagCodes, kase.MainDiag)) continue;
            if (st != null) diagRows++;
            if (string.IsNullOrEmpty(kase.MainProc) || !row.ProcCodes.Any(pc => Matcher.MatchProc(pc, kase.MainProc))) continue;
            if (st != null) procRows++;
            var hasRel = row.RelProcs != null && row.RelProcs.Count > 0;
            if (hasRel)
            {
                if (st != null) relNeeded++;
                var relOk = row.RelProcs.Any(alt => Matcher.ComboMatch(alt, kase.OtherProcs));
                if (relOk && st != null) relRows++;
                if (relOk && (bestRel == null || row.Seq < bestRel.Seq)) bestRel = row;
            }
            else if (firstNoRel == null) firstNoRel = row;
        }
        var row2 = bestRel ?? firstNoRel;
        if (row2 == null)
        {
            if (st != null)
            {
                st.Fact($"规则 {rows.Count} 条", $"主诊断命中 {diagRows} 条", diagRows > 0 ? "ok" : "mute");
                st.Fact("其中主手术命中", $"{procRows} 条", procRows > 0 ? "warn" : "mute");
                if (relNeeded > 0)
                    st.Fact("相关手术要求", $"{relNeeded} 条需相关手术，满足 {relRows} 条", relRows > 0 ? "warn" : "mute");
                if (string.IsNullOrEmpty(kase.MainProc) && diagRows > 0)
                    st.Fact("缺项", "并项规则必须有主手术，本病例未填写", "warn");
                else if (diagRows > 0 && procRows == 0)
                    st.Fact("卡点", "主诊断命中但主手术不在该病种手术范围内", "warn");
            }
            return null;
        }
        if (st != null)
        {
            st.Fact("命中行", $"序号 {row2.Seq}", "ok")
              .Fact("主诊断", $"{kase.MainDiag} → 命中 {Matcher.MatchDiagCodeAny(row2.DiagCodes, kase.MainDiag)}")
              .Fact("主手术", kase.MainProc)
              .Fact("相关手术", bestRel != null
                    ? $"已满足（{string.Join("；", bestRel.RelProcs.FirstOrDefault(a => Matcher.ComboMatch(a, kase.OtherProcs)) ?? new List<string>())}）"
                    : "该行不要求相关手术");
        }
        return new Hit
        {
            Row = row2,
            Seq = row2.Seq,
            Name = RowName(row2.DiagName, row2.ProcCodes, row2.ProcName, kase),
            Detail = new Dictionary<string, object>
            {
                ["mainDiag"] = kase.MainDiag,
                ["dipDiagCode"] = Matcher.MatchDiagCodeAny(row2.DiagCodes, kase.MainDiag),
                ["mainProc"] = kase.MainProc,
                ["relatedRequired"] = bestRel != null,
            },
        };
    }

    // ------------------------------------------------------------------
    // 4. 基础规则
    // ------------------------------------------------------------------

    private Hit MatchJichu(Kase kase, GroupTraceStep st)
    {
        var rows = Cfg.Jichu ?? new List<DiagRow>();
        if (st != null) st.RuleCount = rows.Count;
        DiagRow procRow = null, conservativeRow = null;
        int diagRows = 0, procRows = 0, consRows = 0;
        foreach (var row in rows)
        {
            if (!Matcher.MatchDiagAny(row.DiagCodes, kase.MainDiag)) continue;
            if (st != null) diagRows++;
            if (row.ProcCodes.Count > 0)
            {
                if (st != null) procRows++;
                if (!string.IsNullOrEmpty(kase.MainProc) && row.ProcCodes.Any(pc => Matcher.MatchProc(pc, kase.MainProc)))
                {
                    if (procRow == null || row.Seq < procRow.Seq) procRow = row;
                }
            }
            else
            {
                if (st != null) consRows++;
                if (conservativeRow == null) conservativeRow = row;
            }
        }
        var row2 = procRow ?? (string.IsNullOrEmpty(kase.MainProc) ? conservativeRow : null);
        WriteDiagLayerTrace(st, kase, row2, diagRows, procRows, consRows, "基础规则");
        if (row2 == null) return null;
        return new Hit
        {
            Row = row2,
            Seq = row2.Seq,
            Name = RowName(row2.DiagName, row2.ProcCodes, row2.ProcName, kase),
            Detail = new Dictionary<string, object>
            {
                ["mainDiag"] = kase.MainDiag,
                ["dipDiagCode"] = Matcher.MatchDiagCodeAny(row2.DiagCodes, kase.MainDiag),
                ["mainProc"] = kase.MainProc ?? "",
                ["conservative"] = string.IsNullOrEmpty(kase.MainProc),
            },
        };
    }

    /// <summary>基础规则 / 基层病种共用的溯源写法（两层判定结构一致）。</summary>
    private static void WriteDiagLayerTrace(GroupTraceStep st, Kase kase, DiagRow hit,
        int diagRows, int procRows, int consRows, string layer)
    {
        if (st == null) return;
        if (hit != null)
        {
            var conservative = hit.ProcCodes.Count == 0;
            st.Fact("命中行", $"序号 {hit.Seq}", "ok")
              .Fact("主诊断", $"{kase.MainDiag} → 命中 {Matcher.MatchDiagCodeAny(hit.DiagCodes, kase.MainDiag)}")
              .Fact("入组方式", conservative ? "保守治疗（该行无手术要求）" : $"主手术命中 {kase.MainProc}")
              .Fact("病种", string.IsNullOrEmpty(hit.DiagName) ? (hit.ProcName ?? "") : hit.DiagName);
            return;
        }
        st.Fact($"规则 {st.RuleCount} 条", $"主诊断命中 {diagRows} 条", diagRows > 0 ? "ok" : "mute");
        st.Fact("有手术要求的行", $"诊断命中 {procRows} 条 · 主手术命中 0 条", procRows > 0 ? "warn" : "mute");
        st.Fact("保守治疗行", $"{consRows} 条", "mute");
        if (string.IsNullOrEmpty(kase.MainProc))
            st.Fact("判定方式", "未填写主手术 → 按保守治疗行匹配", "mute");
        else if (procRows > 0)
            st.Fact("卡点", "主诊断与手术范围有交集，但主手术不在其中", "warn");
    }

    // ------------------------------------------------------------------
    // 5. 基层病种
    // ------------------------------------------------------------------

    private Hit MatchJiceng(Kase kase, GroupTraceStep st)
    {
        var rows = Cfg.Jiceng ?? new List<DiagRow>();
        if (st != null) st.RuleCount = rows.Count;
        DiagRow procRow = null, conservativeRow = null;
        int diagRows = 0, procRows = 0, consRows = 0;
        foreach (var row in rows)
        {
            if (!Matcher.MatchDiagAny(row.DiagCodes, kase.MainDiag)) continue;
            if (st != null) diagRows++;
            if (row.ProcCodes.Count > 0)
            {
                if (st != null) procRows++;
                if (!string.IsNullOrEmpty(kase.MainProc) && row.ProcCodes.Any(pc => Matcher.MatchProc(pc, kase.MainProc)))
                {
                    if (procRow == null || row.Seq < procRow.Seq) procRow = row;
                }
            }
            else
            {
                if (st != null) consRows++;
                if (conservativeRow == null) conservativeRow = row;
            }
        }
        var row2 = procRow ?? (string.IsNullOrEmpty(kase.MainProc) ? conservativeRow : null);
        WriteDiagLayerTrace(st, kase, row2, diagRows, procRows, consRows, "基层病种");
        if (row2 == null) return null;
        return new Hit
        {
            Row = row2,
            Seq = row2.Seq,
            Name = RowName(row2.DiagName, row2.ProcCodes, row2.ProcName, kase),
            Detail = new Dictionary<string, object>
            {
                ["mainDiag"] = kase.MainDiag,
                ["dipDiagCode"] = Matcher.MatchDiagCodeAny(row2.DiagCodes, kase.MainDiag),
                ["mainProc"] = kase.MainProc,
            },
        };
    }

    // ------------------------------------------------------------------
    // 6. 综合病组
    // ------------------------------------------------------------------

    private Hit MatchZonghe(Kase kase, GroupTraceStep st)
    {
        if (st != null) st.RuleCount = _zongheParsed.Count;
        var cat = CategoryOf(kase.MainDiag);
        if (cat == null)
        {
            st?.Fact("主诊断", string.IsNullOrEmpty(kase.MainDiag) ? "（未填）" : kase.MainDiag, "warn")
               .Fact("卡点", "无法从主诊断解析出类目前缀（如 A15）", "warn");
            return null;
        }
        foreach (var (row, ranges) in _zongheParsed)
        {
            var hitRange = ranges.FirstOrDefault(r =>
                string.CompareOrdinal(cat, r.Lo) >= 0 && string.CompareOrdinal(cat, r.Hi) <= 0);
            if (hitRange.Lo != null)
            {
                var label = hitRange.Lo == hitRange.Hi ? hitRange.Lo : $"{hitRange.Lo}-{hitRange.Hi}";
                var d = new Dictionary<string, object>
                {
                    ["mainDiag"] = kase.MainDiag,
                    ["dipDiagCode"] = label,
                    ["category"] = cat,
                    ["range"] = label,
                };
                if (IsDiagExcluded(kase.MainDiag)) d["viaExcluded"] = true;
                st?.Fact("命中行", $"序号 {row.Seq}", "ok")
                   .Fact("主诊断类目", cat)
                   .Fact("覆盖范围", label)
                   .Fact("病组", row.Name ?? "");
                return new Hit { Row = row, Seq = row.Seq, Name = row.Name, Detail = d };
            }
        }
        st?.Fact("主诊断类目", cat, "ok")
           .Fact("覆盖范围", $"已比对 {_zongheParsed.Count} 条综合病组，无覆盖该类目的行", "warn");
        return null;
    }

    // ------------------------------------------------------------------

    private static string RowName(string diagName, List<string> procCodes, string procName, Kase kase)
    {
        if (procCodes == null || procCodes.Count == 0) return diagName;
        var name = "";
        if (!string.IsNullOrEmpty(kase.MainProc))
        {
            var idx = procCodes.FindIndex(pc => Matcher.MatchProc(pc, kase.MainProc));
            var names = (procName ?? "").Split('|').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (idx >= 0 && idx < names.Count) name = names[idx];
            else if (names.Count > 0) name = names[0];
        }
        return name.Length > 0 ? $"{diagName}（{name}）" : diagName;
    }

    private static GroupResult Pack(Hit hit, string groupType, string typeCode, GroupTrace tr)
    {
        if (tr != null)
        {
            // 命中层的步骤是最后加入的一条；补上序号 / 名称 / 状态
            var last = tr.Steps.Count > 0 ? tr.Steps[^1] : null;
            if (last != null)
            {
                last.Status = "hit";
                last.Seq = hit.Seq;
                last.Name = hit.Name;
                last.Title = $"命中第 {hit.Seq} 行 · {hit.Name}";
                // 结核区间行在「诊断辅助细分」命中但归入「综合病组」，此处点明归口
                if (last.LayerCode != typeCode)
                    last.Fact("归入组别", groupType);
            }
            tr.HitLayer = typeCode;
        }
        return new GroupResult
        {
            Matched = true,
            GroupType = groupType,
            GroupTypeCode = typeCode,
            Seq = hit.Seq,
            Name = hit.Name,
            Detail = hit.Detail,
            Trace = tr,
        };
    }

    private class Hit
    {
        public object Row { get; set; }
        public int? Seq { get; set; }
        public string Name { get; set; }
        public bool Zonghe { get; set; }
        public Dictionary<string, object> Detail { get; set; } = new();
    }
}
