using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dip3Grouper;

public class ColumnDef
{
    public string Key { get; set; }
    public string Label { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? Width { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? Mono { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? Immutable { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    [System.Text.Json.Serialization.JsonPropertyName("bool")]
    public bool? BoolFlag { get; set; }
}

public class ParseResult
{
    public object Row { get; set; }
    public string Error { get; set; }
    public bool Ok => Error == null;
}

/// <summary>病种库某区段的定义（对齐 src/diseases.js 的 SECTIONS）。</summary>
public abstract class SectionDef
{
    public abstract string Tab { get; }
    public abstract string Label { get; }
    public abstract string Key { get; }
    public abstract List<ColumnDef> Columns { get; }

    public abstract List<object> GetRows(Dip3Config c);
    public abstract Dictionary<string, object> Flatten(object row);
    public abstract ParseResult Parse(Dictionary<string, string> flat, object oldRow);
    public abstract string KeyOf(object row);
    public abstract void AddRow(Dip3Config c, object row);
    public abstract void SetAt(Dip3Config c, int idx, object row);
    public abstract void RemoveAt(Dip3Config c, int idx);
    public abstract void ReplaceAll(Dip3Config c, List<object> rows);

    protected string F(Dictionary<string, string> f, string key)
        => f != null && f.TryGetValue(key, out var v) ? (v ?? "").Trim() : "";
}

public abstract class SectionDef<T> : SectionDef where T : class
{
    protected abstract List<T> Backing(Dip3Config c);
    protected abstract Dictionary<string, object> FlattenRow(T r);
    protected abstract ParseResult ParseRow(Dictionary<string, string> f, T old);
    protected abstract string KeyOfRow(T r);

    public override List<object> GetRows(Dip3Config c) => Backing(c).Cast<object>().ToList();
    public override Dictionary<string, object> Flatten(object row) => FlattenRow((T)row);
    public override ParseResult Parse(Dictionary<string, string> flat, object oldRow) => ParseRow(flat, oldRow as T);
    public override string KeyOf(object row) => KeyOfRow((T)row);
    public override void AddRow(Dip3Config c, object row) => Backing(c).Add((T)row);
    public override void SetAt(Dip3Config c, int idx, object row) => Backing(c)[idx] = (T)row;
    public override void RemoveAt(Dip3Config c, int idx) => Backing(c).RemoveAt(idx);
    public override void ReplaceAll(Dip3Config c, List<object> rows)
    {
        var list = Backing(c);
        list.Clear();
        list.AddRange(rows.Cast<T>());
    }
}

/// <summary>
/// 病种库区段列表结果。原为匿名类型，改为具名类型以便桌面客户端强类型使用；
/// 属性名与顺序不变，序列化输出与原先逐字段一致。
/// </summary>
public sealed class SectionListResult
{
    public string Label { get; set; }
    public string KeyField { get; set; }
    public List<ColumnDef> Columns { get; set; }
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<Dictionary<string, object>> Rows { get; set; }
}

public static class Diseases
{
    // ---------- 分隔工具（对齐 JS 的 splitList / joinList / parseRel / fmtRel） ----------
    private static readonly Regex SplitListRe = new(@"[|;；、]", RegexOptions.Compiled);

    public static List<string> SplitList(string s)
    {
        var outList = new List<string>();
        foreach (var part in SplitListRe.Split(s ?? ""))
        {
            var t = part.Trim();
            if (t.Length > 0) outList.Add(t);
        }
        return outList;
    }

    public static string JoinList(IEnumerable<string> a) => string.Join("|", a ?? Enumerable.Empty<string>());

    public static List<List<string>> ParseRel(string s)
    {
        var outList = new List<List<string>>();
        foreach (var alt in (s ?? "").Split('|'))
        {
            var one = alt.Split('+').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            if (one.Count > 0) outList.Add(one);
        }
        return outList;
    }

    public static string FmtRel(List<List<string>> rel)
        => string.Join("|", (rel ?? new List<List<string>>()).Select(a => string.Join("+", a)));

    private static ParseResult ReqList(string v, string name)
    {
        var arr = SplitList(v);
        if (arr.Count == 0) return new ParseResult { Error = $"{name}不能为空" };
        return new ParseResult { Row = arr };
    }

    private static readonly Regex BwRe = new(@"^(\d+)\s*-\s*(\d+)$", RegexOptions.Compiled);

    // ==================================================================
    // 各 SectionDef 实现
    // ==================================================================

    private class XianqiDef : SectionDef<XianqiRow>
    {
        public override string Tab => "xianqi";
        public override string Label => "先期分组";
        public override string Key => "seq";
        public override List<ColumnDef> Columns { get; } = new()
        {
            new ColumnDef { Key = "seq", Label = "序号", Width = 56, Immutable = true },
            new ColumnDef { Key = "name", Label = "分组名称", Width = 170 },
            new ColumnDef { Key = "diag", Label = "诊断编码", Width = 190, Mono = true },
            new ColumnDef { Key = "procs", Label = "手术操作（备选 | 组合 +）", Width = 190, Mono = true },
            new ColumnDef { Key = "ageMax", Label = "年龄上限(天)", Width = 88, Mono = true },
            new ColumnDef { Key = "bw", Label = "出生体重(克)", Width = 104, Mono = true },
            new ColumnDef { Key = "note", Label = "备注", Width = 130 },
        };
        protected override List<XianqiRow> Backing(Dip3Config c) => c.Xianqi;
        protected override string KeyOfRow(XianqiRow r) => r.Seq.ToString();
        protected override Dictionary<string, object> FlattenRow(XianqiRow r) => new()
        {
            ["seq"] = r.Seq,
            ["name"] = r.Name ?? "",
            ["diag"] = JoinList(r.DiagCodes),
            ["procs"] = string.Join("|", (r.ProcAlts ?? new()).Select(a => string.Join("+", a))),
            ["ageMax"] = r.AgeDaysMax.HasValue ? r.AgeDaysMax.Value : "",
            ["bw"] = r.BirthWeightMin.HasValue ? $"{r.BirthWeightMin}-{r.BirthWeightMax}" : "",
            ["note"] = r.Note ?? "",
        };
        protected override ParseResult ParseRow(Dictionary<string, string> f, XianqiRow old)
        {
            var row = new XianqiRow
            {
                Seq = ParseInt(F(f, "seq")),
                Name = F(f, "name"),
                DiagCodes = SplitList(F(f, "diag")),
                ProcAlts = new(),
                AgeDaysMax = null,
                BirthWeightMin = null,
                BirthWeightMax = null,
                Note = F(f, "note"),
            };
            var ageMax = F(f, "ageMax");
            var bw = F(f, "bw");
            if (ageMax.Length > 0 || bw.Length > 0)
            {
                if (ageMax.Length > 0)
                {
                    var n = CaseParse.Number(ageMax);
                    if (n == null || n < 0) return new ParseResult { Error = "年龄上限须为非负数字" };
                    row.AgeDaysMax = (int)n.Value;
                }
                if (bw.Length > 0)
                {
                    var m = BwRe.Match(bw);
                    if (!m.Success) return new ParseResult { Error = "出生体重区间格式应为 0-999" };
                    row.BirthWeightMin = int.Parse(m.Groups[1].Value);
                    row.BirthWeightMax = int.Parse(m.Groups[2].Value);
                    row.BirthWeightNote = $"出生体重{m.Groups[1].Value}-{m.Groups[2].Value}克";
                }
                if (row.DiagCodes.Count == 0) row.DiagCodes = new List<string> { "P07" };
            }
            else
            {
                var procs = ParseRel(F(f, "procs"));
                if (procs.Count == 0) return new ParseResult { Error = "手术操作不能为空（新生儿体重行请填年龄上限/体重区间）" };
                row.ProcAlts = procs;
                if (row.DiagCodes.Count == 0) row.DiagCodes = new List<string> { "所有诊断" };
            }
            return new ParseResult { Row = row };
        }
    }

    private class BingxiangDef : SectionDef<BingxiangRow>
    {
        public override string Tab => "bingxiang";
        public override string Label => "并项核心病种";
        public override string Key => "seq";
        public override List<ColumnDef> Columns { get; } = new()
        {
            new ColumnDef { Key = "seq", Label = "序号", Width = 56, Immutable = true },
            new ColumnDef { Key = "diag", Label = "主要诊断编码", Width = 130, Mono = true },
            new ColumnDef { Key = "diagName", Label = "诊断名称", Width = 150 },
            new ColumnDef { Key = "procs", Label = "主要手术编码", Width = 160, Mono = true },
            new ColumnDef { Key = "procName", Label = "手术名称", Width = 170 },
            new ColumnDef { Key = "rel", Label = "相关手术编码（备选 | 组合 +）", Width = 160, Mono = true },
            new ColumnDef { Key = "relNames", Label = "相关手术名称（备选 | 组合 +）", Width = 170 },
        };
        protected override List<BingxiangRow> Backing(Dip3Config c) => c.Bingxiang;
        protected override string KeyOfRow(BingxiangRow r) => r.Seq.ToString();
        protected override Dictionary<string, object> FlattenRow(BingxiangRow r) => new()
        {
            ["seq"] = r.Seq,
            ["diag"] = JoinList(r.DiagCodes),
            ["diagName"] = r.DiagName ?? "",
            ["procs"] = JoinList(r.ProcCodes),
            ["procName"] = r.ProcName ?? "",
            ["rel"] = FmtRel(r.RelProcs),
            ["relNames"] = string.Join("|", r.RelNames ?? new()),
        };
        protected override ParseResult ParseRow(Dictionary<string, string> f, BingxiangRow old)
        {
            var diag = ReqList(F(f, "diag"), "主要诊断编码");
            if (!diag.Ok) return diag;
            var procs = ReqList(F(f, "procs"), "主要手术编码");
            if (!procs.Ok) return procs;
            var relNames = SplitList(F(f, "relNames"));
            return new ParseResult
            {
                Row = new BingxiangRow
                {
                    Seq = ParseInt(F(f, "seq")),
                    DiagCodes = (List<string>)diag.Row,
                    DiagName = F(f, "diagName"),
                    ProcCodes = (List<string>)procs.Row,
                    ProcName = F(f, "procName"),
                    RelProcs = ParseRel(F(f, "rel")),
                    RelNames = relNames.Count > 0 ? relNames : (old?.RelNames ?? new List<string>()),
                }
            };
        }
    }

    private class ShaoshangDef : SectionDef<ShaoshangRow>
    {
        public override string Tab => "fz_shaoshang";
        public override string Label => "辅助细分-烧伤";
        public override string Key => "seq";
        public override List<ColumnDef> Columns { get; } = new()
        {
            new ColumnDef { Key = "seq", Label = "序号", Width = 56, Immutable = true },
            new ColumnDef { Key = "diag", Label = "主要诊断编码", Width = 170, Mono = true },
            new ColumnDef { Key = "degree", Label = "程度", Width = 100 },
            new ColumnDef { Key = "otherDiag", Label = "其他诊断(面积码)", Width = 92, Mono = true },
            new ColumnDef { Key = "area", Label = "面积", Width = 92 },
            new ColumnDef { Key = "procs", Label = "手术编码", Width = 160, Mono = true },
            new ColumnDef { Key = "procName", Label = "手术名称", Width = 190 },
            new ColumnDef { Key = "rel", Label = "相关手术编码（备选 | 组合 +）", Width = 110, Mono = true },
            new ColumnDef { Key = "relNames", Label = "相关手术名称（备选 | 组合 +）", Width = 130 },
        };
        protected override List<ShaoshangRow> Backing(Dip3Config c) => c.FzShaoshang;
        protected override string KeyOfRow(ShaoshangRow r) => r.Seq.ToString();
        protected override Dictionary<string, object> FlattenRow(ShaoshangRow r) => new()
        {
            ["seq"] = r.Seq,
            ["diag"] = JoinList(r.DiagCodes),
            ["degree"] = r.Degree ?? "",
            ["otherDiag"] = JoinList(r.OtherDiagCodes),
            ["area"] = r.Area ?? "",
            ["procs"] = JoinList(r.ProcCodes),
            ["procName"] = r.ProcName ?? "",
            ["rel"] = FmtRel(r.RelProcs),
            ["relNames"] = string.Join("|", r.RelNames ?? new()),
        };
        protected override ParseResult ParseRow(Dictionary<string, string> f, ShaoshangRow old)
        {
            var diag = ReqList(F(f, "diag"), "主要诊断编码");
            if (!diag.Ok) return diag;
            var other = ReqList(F(f, "otherDiag"), "其他诊断(面积码)");
            if (!other.Ok) return other;
            var relNames = SplitList(F(f, "relNames"));
            return new ParseResult
            {
                Row = new ShaoshangRow
                {
                    Seq = ParseInt(F(f, "seq")),
                    DiagCodes = (List<string>)diag.Row,
                    Degree = F(f, "degree"),
                    OtherDiagCodes = (List<string>)other.Row,
                    Area = F(f, "area"),
                    ProcCodes = SplitList(F(f, "procs")),
                    ProcName = F(f, "procName"),
                    RelProcs = ParseRel(F(f, "rel")),
                    RelNames = relNames.Count > 0 ? relNames : (old?.RelNames ?? new List<string>()),
                }
            };
        }
    }

    private class ZhongliuDef : SectionDef<ZhongliuRow>
    {
        public override string Tab => "fz_zhongliu";
        public override string Label => "辅助细分-肿瘤";
        public override string Key => "seq";
        public override List<ColumnDef> Columns { get; } = new()
        {
            new ColumnDef { Key = "seq", Label = "序号", Width = 56, Immutable = true },
            new ColumnDef { Key = "diag", Label = "主要诊断编码", Width = 84, Mono = true },
            new ColumnDef { Key = "diagName", Label = "诊断名称", Width = 140 },
            new ColumnDef { Key = "otherDiag", Label = "其他诊断(恶性肿瘤)", Width = 100, Mono = true },
            new ColumnDef { Key = "procs", Label = "手术组合编码", Width = 170, Mono = true },
            new ColumnDef { Key = "procName", Label = "手术组合名称", Width = 220 },
        };
        protected override List<ZhongliuRow> Backing(Dip3Config c) => c.FzZhongliu;
        protected override string KeyOfRow(ZhongliuRow r) => r.Seq.ToString();
        protected override Dictionary<string, object> FlattenRow(ZhongliuRow r) => new()
        {
            ["seq"] = r.Seq,
            ["diag"] = JoinList(r.DiagCodes),
            ["diagName"] = r.DiagName ?? "",
            ["otherDiag"] = JoinList(r.OtherDiagCodes),
            ["procs"] = JoinList(r.ProcAlt),
            ["procName"] = r.ProcName ?? "",
        };
        protected override ParseResult ParseRow(Dictionary<string, string> f, ZhongliuRow old)
        {
            var diag = ReqList(F(f, "diag"), "主要诊断编码");
            if (!diag.Ok) return diag;
            var other = ReqList(F(f, "otherDiag"), "其他诊断(恶性肿瘤)");
            if (!other.Ok) return other;
            return new ParseResult
            {
                Row = new ZhongliuRow
                {
                    Seq = ParseInt(F(f, "seq")),
                    DiagCodes = (List<string>)diag.Row,
                    DiagName = F(f, "diagName"),
                    OtherDiagCodes = (List<string>)other.Row,
                    ProcAlt = SplitList(F(f, "procs")),
                    ProcName = F(f, "procName"),
                }
            };
        }
    }

    private class JieheDef : SectionDef<JieheRow>
    {
        public override string Tab => "fz_jiehe";
        public override string Label => "辅助细分-结核";
        public override string Key => "seq";
        public override List<ColumnDef> Columns { get; } = new()
        {
            new ColumnDef { Key = "seq", Label = "序号", Width = 56, Immutable = true },
            new ColumnDef { Key = "diag", Label = "主要诊断编码", Width = 110, Mono = true },
            new ColumnDef { Key = "diagName", Label = "诊断名称", Width = 140 },
            new ColumnDef { Key = "procs", Label = "手术编码（空=保守行）", Width = 100, Mono = true },
            new ColumnDef { Key = "procName", Label = "手术名称", Width = 160 },
            new ColumnDef { Key = "resistant", Label = "耐药", Width = 56, BoolFlag = true },
            new ColumnDef { Key = "resistNote", Label = "是否耐药说明", Width = 240 },
        };
        protected override List<JieheRow> Backing(Dip3Config c) => c.FzJiehe;
        protected override string KeyOfRow(JieheRow r) => r.Seq.ToString();
        protected override Dictionary<string, object> FlattenRow(JieheRow r) => new()
        {
            ["seq"] = r.Seq,
            ["diag"] = JoinList(r.DiagCodes),
            ["diagName"] = r.DiagName ?? "",
            ["procs"] = JoinList(r.ProcCodes),
            ["procName"] = r.ProcName ?? "",
            ["resistant"] = r.DrugResistant ? "是" : "否",
            ["resistNote"] = r.ResistNote ?? "",
        };
        protected override ParseResult ParseRow(Dictionary<string, string> f, JieheRow old)
        {
            var diag = ReqList(F(f, "diag"), "主要诊断编码");
            if (!diag.Ok) return diag;
            return new ParseResult
            {
                Row = new JieheRow
                {
                    Seq = ParseInt(F(f, "seq")),
                    DiagCodes = (List<string>)diag.Row,
                    DiagName = F(f, "diagName"),
                    ProcCodes = SplitList(F(f, "procs")),
                    ProcName = F(f, "procName"),
                    DrugResistant = F(f, "resistant") == "是",
                    ResistNote = F(f, "resistNote"),
                }
            };
        }
    }

    private class ResistCodesDef : SectionDef<ResistCode>
    {
        public override string Tab => "jiehe_resist_codes";
        public override string Label => "结核耐药诊断附表";
        public override string Key => "code";
        public override List<ColumnDef> Columns { get; } = new()
        {
            new ColumnDef { Key = "code", Label = "主要诊断编码", Width = 150, Immutable = true, Mono = true },
            new ColumnDef { Key = "name", Label = "主要诊断名称" },
        };
        protected override List<ResistCode> Backing(Dip3Config c) => c.JieheResistCodes;
        protected override string KeyOfRow(ResistCode r) => r.Code ?? "";
        protected override Dictionary<string, object> FlattenRow(ResistCode r) => new()
        {
            ["code"] = r.Code ?? "",
            ["name"] = r.Name ?? "",
        };
        protected override ParseResult ParseRow(Dictionary<string, string> f, ResistCode old)
        {
            var code = F(f, "code");
            if (code.Length == 0) return new ParseResult { Error = "编码必填" };
            return new ParseResult { Row = new ResistCode { Code = code, Name = F(f, "name") } };
        }
    }

    private class DiagRowDef : SectionDef<DiagRow>
    {
        private readonly string _tab;
        private readonly string _label;
        private readonly Func<Dip3Config, List<DiagRow>> _backing;

        public DiagRowDef(string tab, string label, Func<Dip3Config, List<DiagRow>> backing)
        {
            _tab = tab; _label = label; _backing = backing;
        }
        public override string Tab => _tab;
        public override string Label => _label;
        public override string Key => "seq";
        public override List<ColumnDef> Columns { get; } = new()
        {
            new ColumnDef { Key = "seq", Label = "序号", Width = 56, Immutable = true },
            new ColumnDef { Key = "diag", Label = "主要诊断编码", Width = 130, Mono = true },
            new ColumnDef { Key = "diagName", Label = "诊断名称" },
            new ColumnDef { Key = "procs", Label = "手术编码（空=保守行）", Width = 140, Mono = true },
            new ColumnDef { Key = "procName", Label = "手术名称" },
        };
        protected override List<DiagRow> Backing(Dip3Config c) => _backing(c);
        protected override string KeyOfRow(DiagRow r) => r.Seq.ToString();
        protected override Dictionary<string, object> FlattenRow(DiagRow r) => new()
        {
            ["seq"] = r.Seq,
            ["diag"] = JoinList(r.DiagCodes),
            ["diagName"] = r.DiagName ?? "",
            ["procs"] = JoinList(r.ProcCodes),
            ["procName"] = r.ProcName ?? "",
        };
        protected override ParseResult ParseRow(Dictionary<string, string> f, DiagRow old)
        {
            var diag = ReqList(F(f, "diag"), "主要诊断编码");
            if (!diag.Ok) return diag;
            return new ParseResult
            {
                Row = new DiagRow
                {
                    Seq = ParseInt(F(f, "seq")),
                    DiagCodes = (List<string>)diag.Row,
                    DiagName = F(f, "diagName"),
                    ProcCodes = SplitList(F(f, "procs")),
                    ProcName = F(f, "procName"),
                }
            };
        }
    }

    /// <summary>
    /// 目录库外病种（省局反馈清单）。这一区段的行**不参与**分组命中，
    /// 只在分组结果里做「组合不可用」的预警；维护它的目的是让病案室能查、能改建议组合。
    /// </summary>
    private class OutLibDef : SectionDef<OutLibRow>
    {
        public override string Tab => "outlib";
        public override string Label => "目录库外病种（省局反馈）";
        public override string Key => "seq";
        public override List<ColumnDef> Columns { get; } = new()
        {
            new ColumnDef { Key = "seq", Label = "序号", Width = 56, Immutable = true },
            new ColumnDef { Key = "diagCode", Label = "主要诊断编码", Width = 112, Mono = true },
            new ColumnDef { Key = "diagName", Label = "诊断名称", Width = 168 },
            new ColumnDef { Key = "procCode", Label = "手术操作编码", Width = 120, Mono = true },
            new ColumnDef { Key = "procName", Label = "手术操作名称", Width = 180 },
            new ColumnDef { Key = "suggest", Label = "目录库内可入组组合（参考）", Width = 280 },
            new ColumnDef { Key = "note", Label = "备注", Width = 220 },
        };
        protected override List<OutLibRow> Backing(Dip3Config c) => c.OutLib ??= new List<OutLibRow>();
        protected override string KeyOfRow(OutLibRow r) => r.Seq.ToString();
        protected override Dictionary<string, object> FlattenRow(OutLibRow r) => new()
        {
            ["seq"] = r.Seq,
            ["diagCode"] = r.DiagCode ?? "",
            ["diagName"] = r.DiagName ?? "",
            ["procCode"] = r.ProcCode ?? "",
            ["procName"] = r.ProcName ?? "",
            ["suggest"] = r.Suggest ?? "",
            ["note"] = r.Note ?? "",
        };
        protected override ParseResult ParseRow(Dictionary<string, string> f, OutLibRow old)
        {
            var diag = F(f, "diagCode");
            if (diag.Length == 0) return new ParseResult { Error = "主要诊断编码不能为空" };
            var proc = F(f, "procCode");
            // 建议列 / 备注留空时沿用旧值：后台只改一个名称不该把算好的建议清掉。
            // 新增行的建议由导入命令（import-outlib）算好写入，此处不重算 ——
            // SectionDef.Parse 拿不到 Dip3Config，硬算需要全局单例，不值得。
            var suggest = F(f, "suggest");
            if (suggest.Length == 0 && old != null) suggest = old.Suggest ?? "";
            var note = F(f, "note");
            if (note.Length == 0 && old != null) note = old.Note ?? "";
            if (note.Length == 0) note = OutLibRules.NoteFor(suggest);
            return new ParseResult
            {
                Row = new OutLibRow
                {
                    Seq = ParseInt(F(f, "seq")),
                    DiagCode = diag,
                    DiagName = F(f, "diagName"),
                    ProcCode = proc,
                    ProcName = F(f, "procName"),
                    Suggest = suggest,
                    Note = note,
                }
            };
        }
    }

    private class ExcludedDiagsDef : SectionDef<CodeName>    {
        public override string Tab => "excludedDiags";
        public override string Label => "不纳入诊断";
        public override string Key => "code";
        public override List<ColumnDef> Columns { get; } = new()
        {
            new ColumnDef { Key = "code", Label = "诊断编码", Width = 140, Immutable = true, Mono = true },
            new ColumnDef { Key = "name", Label = "诊断名称" },
        };
        protected override List<CodeName> Backing(Dip3Config c) => c.ExcludedDiags;
        protected override string KeyOfRow(CodeName r) => r.Code ?? "";
        protected override Dictionary<string, object> FlattenRow(CodeName r) => new()
        {
            ["code"] = r.Code ?? "",
            ["name"] = r.Name ?? "",
        };
        protected override ParseResult ParseRow(Dictionary<string, string> f, CodeName old)
        {
            var code = F(f, "code");
            if (code.Length == 0) return new ParseResult { Error = "编码必填" };
            return new ParseResult { Row = new CodeName { Code = code, Name = F(f, "name") } };
        }
    }

    private class ExcludedProcsDef : SectionDef<ExcludedProc>
    {
        public override string Tab => "excludedProcs";
        public override string Label => "不纳入手术";
        public override string Key => "code";
        public override List<ColumnDef> Columns { get; } = new()
        {
            new ColumnDef { Key = "code", Label = "手术编码", Width = 150, Immutable = true, Mono = true },
            new ColumnDef { Key = "name", Label = "手术名称" },
            new ColumnDef { Key = "note", Label = "备注", Width = 130 },
        };
        protected override List<ExcludedProc> Backing(Dip3Config c) => c.ExcludedProcs;
        protected override string KeyOfRow(ExcludedProc r) => r.Code ?? "";
        protected override Dictionary<string, object> FlattenRow(ExcludedProc r) => new()
        {
            ["code"] = r.Code ?? "",
            ["name"] = r.Name ?? "",
            ["note"] = r.Note ?? "",
        };
        protected override ParseResult ParseRow(Dictionary<string, string> f, ExcludedProc old)
        {
            var code = F(f, "code");
            if (code.Length == 0) return new ParseResult { Error = "编码必填" };
            return new ParseResult { Row = new ExcludedProc { Code = code, Name = F(f, "name"), Note = F(f, "note") } };
        }
    }

    private static int ParseInt(string v)
    {
        var n = CaseParse.Number(v);
        return n.HasValue && CaseParse.IsFinite(n.Value) ? (int)Math.Truncate(n.Value) : 0;
    }

    /// <summary>区段定义表（顺序即界面 tab 顺序）。</summary>
    public static readonly List<SectionDef> Sections = new()
    {
        new XianqiDef(),
        new BingxiangDef(),
        new ShaoshangDef(),
        new ZhongliuDef(),
        new JieheDef(),
        new ResistCodesDef(),
        new DiagRowDef("jichu", "基础核心病种", c => c.Jichu),
        new DiagRowDef("jiceng", "基层病种", c => c.Jiceng),
        new OutLibDef(),
        new ExcludedDiagsDef(),
        new ExcludedProcsDef(),
    };

    public static Dictionary<string, SectionDef> ByTab { get; } =
        Sections.ToDictionary(s => s.Tab, s => s);

    public static SectionDef Get(string tab) => ByTab.TryGetValue(tab ?? "", out var s) ? s : null;

    /// <summary>列表: 扁平化 + 搜索 + 分页</summary>
    public static SectionListResult ListRows(Dip3Config config, string tab, string q = "", int page = 1, int pageSize = 50)
    {
        var sec = Get(tab) ?? throw new InvalidOperationException($"未知病种库区段: {tab}");
        var flats = sec.GetRows(config).Select(sec.Flatten).ToList();
        var kw = (q ?? "").Trim().ToLowerInvariant();
        if (kw.Length > 0)
        {
            var nameKeys = sec.Columns.Select(c => c.Key).Where(k => k.ToLowerInvariant().Contains("name")).ToList();
            flats = flats.Where(f =>
                f.Values.Any(v => JsonText.Str(v).ToLowerInvariant().Contains(kw)) ||
                nameKeys.Any(k => f.TryGetValue(k, out var nv) && PinyinHelper.PyMatch(JsonText.Str(nv), kw))
            ).ToList();

            // 目录库外病种：清单里写 22.02，病案首页写 22.0200，纯 Contains 搜不到 ——
            // 而「拿病案的实际编码来查这条在不在清单里」正是这个区段最常用的用法。
            // 手术码再按「尾零归一」比一次，两种写法都能命中。
            if (sec.Tab == "outlib")
            {
                var canon = OutLibRules.Canon(kw);
                if (canon.Length > 0)
                {
                    var have = new HashSet<string>(flats.Select(f => JsonText.Str(f.GetValueOrDefault("seq"))),
                        StringComparer.Ordinal);
                    foreach (var f in sec.GetRows(config).Select(sec.Flatten))
                    {
                        if (!f.TryGetValue("procCode", out var pv)) continue;
                        if (OutLibRules.Canon(JsonText.Str(pv)) != canon) continue;
                        if (!have.Add(JsonText.Str(f.GetValueOrDefault("seq")))) continue;
                        flats.Add(f);
                    }
                }
            }
        }
        var total = flats.Count;
        page = Math.Max(1, page);
        pageSize = Math.Min(Math.Max(1, pageSize), 200);
        var start = (page - 1) * pageSize;
        return new SectionListResult
        {
            Label = sec.Label,
            KeyField = sec.Key,
            Columns = sec.Columns,
            Total = total,
            Page = page,
            PageSize = pageSize,
            Rows = flats.Skip(start).Take(pageSize).ToList(),
        };
    }

    /// <summary>新增: seq 区段自动取最大序号+1, code 区段查重</summary>
    public static ParseResult AddRow(Dip3Config config, string tab, Dictionary<string, string> flat)
    {
        var sec = Get(tab);
        if (sec == null) return new ParseResult { Error = $"未知病种库区段: {tab}" };
        var rows = sec.GetRows(config);
        flat ??= new Dictionary<string, string>();
        if (sec.Key == "seq")
        {
            var seq = ParseInt(flat.TryGetValue("seq", out var sv) ? sv : "");
            var maxSeq = rows.Count == 0 ? 0 : rows.Max(r => ParseInt(sec.KeyOf(r)));
            // 对齐 Node: 未填序号或序号已存在 → 取最大序号+1
            if (seq == 0 || rows.Any(r => ParseInt(sec.KeyOf(r)) == seq)) seq = maxSeq + 1;
            flat["seq"] = seq.ToString();
        }
        else
        {
            var code = (flat.TryGetValue("code", out var cv) ? cv : "").Trim();
            if (code.Length == 0) return new ParseResult { Error = "编码必填" };
            if (rows.Any(r => sec.KeyOf(r) == code)) return new ParseResult { Error = $"编码 {code} 已存在" };
            flat["code"] = code;
        }
        var parsed = sec.Parse(flat, null);
        if (!parsed.Ok) return parsed;
        sec.AddRow(config, parsed.Row);
        var keyStr = sec.KeyOf(parsed.Row);
        object keyVal = sec.Key == "seq" && int.TryParse(keyStr, out var kn) ? kn : keyStr;
        return new ParseResult { Row = keyVal };
    }

    /// <summary>编辑: 按 key 定位, key 字段不可变</summary>
    public static ParseResult UpdateRow(Dip3Config config, string tab, string key, Dictionary<string, string> flat)
    {
        var sec = Get(tab);
        if (sec == null) return new ParseResult { Error = $"未知病种库区段: {tab}" };
        var rows = sec.GetRows(config);
        var idx = rows.FindIndex(r => sec.KeyOf(r) == (key ?? ""));
        if (idx < 0) return new ParseResult { Error = "记录不存在" };
        var merged = new Dictionary<string, string>(flat ?? new Dictionary<string, string>()) { [sec.Key] = key };
        var parsed = sec.Parse(merged, rows[idx]);
        if (!parsed.Ok) return parsed;
        sec.SetAt(config, idx, parsed.Row);
        return new ParseResult { Row = sec.KeyOf(parsed.Row) };
    }

    public static ParseResult RemoveRow(Dip3Config config, string tab, string key)
    {
        var sec = Get(tab);
        if (sec == null) return new ParseResult { Error = $"未知病种库区段: {tab}" };
        var rows = sec.GetRows(config);
        var idx = rows.FindIndex(r => sec.KeyOf(r) == (key ?? ""));
        if (idx < 0) return new ParseResult { Error = "记录不存在" };
        sec.RemoveAt(config, idx);
        return new ParseResult { Row = true };
    }

    /// <summary>配置持久化（临时文件 + 原子替换）；Node 侧 JSON.stringify(config) 无缩进</summary>
    public static void SaveConfig(Dip3Config config)
        => Js.WriteJsonFile(AppPaths.ConfigFile, config, Js.Config, 0);

    public static Dictionary<string, SectionCount> SectionCounts(Dip3Config config)
        => Sections.ToDictionary(s => s.Tab, s => new SectionCount { Label = s.Label, Total = s.GetRows(config).Count });
}

public static class JsonText
{
    public static string Str(object v)
    {
        if (v == null) return "";
        if (v is string s) return s;
        if (v is bool b) return b ? "true" : "false";
        return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }
}

/// <summary>字符串 → 数值（对齐 JS Number()）。</summary>
public static class CaseParse
{
    public static double? Number(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        return double.TryParse(s.Trim(), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
    }
    public static bool IsFinite(double n) => !double.IsNaN(n) && !double.IsInfinity(n);
}
