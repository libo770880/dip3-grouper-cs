using System.Text.Json;
using System.Text.Json.Serialization;
using GrouperEngine = global::Dip3Grouper.Dip3Grouper;

namespace Dip3Grouper;

/// <summary>
/// 业务编排层：分组 / 批量分组 / 联想检索 / 模板生成。
///
/// 原实现散落在 Dip3Grouper.Web 的 WebRouter 中，为让桌面客户端直接调用（不经 HTTP）
/// 且保证两端行为完全一致，统一上提到 Core —— 唯一实现，Web 与桌面共用。
/// </summary>
public static class GrouperService
{
    // ==================================================================
    // 常量：导入列名映射
    // ==================================================================

    public static readonly string[] GrayTrueValues = { "是", "1", "true", "灰码", "y", "yes" };

    /// <summary>批量分组输入列名 → 字段</summary>
    public static readonly (string Key, string[] Names)[] BatchFieldMap =
    {
        ("caseId", new[] { "病案号", "ID", "id", "caseId", "编号" }),
        ("mainDiag", new[] { "主要诊断编码", "主要诊断", "mainDiag" }),
        ("otherDiags", new[] { "其他诊断编码", "其他诊断", "otherDiags" }),
        ("mainProc", new[] { "主要手术操作编码", "主要手术操作", "主要手术", "mainProc" }),
        ("otherProcs", new[] { "其他手术操作编码", "其他手术操作", "其他手术", "otherProcs" }),
        ("age", new[] { "年龄", "age" }),
        ("ageUnit", new[] { "年龄单位", "ageUnit" }),
        ("ageDays", new[] { "年龄天数", "ageDays" }),
        ("birthWeight", new[] { "出生体重", "出生体重(g)", "出生体重克", "birthWeight" }),
    };

    /// <summary>映射表列名 → 字段</summary>
    public static readonly (string Key, string[] Names)[] MappingFieldMap =
    {
        ("code", new[] { "ICD-10编码", "ICD10编码", "国临版编码", "国临版诊断编码", "诊断编码", "ICD编码", "院内编码", "编码", "icd10Code", "code" }),
        ("name", new[] { "ICD-10名称", "ICD10名称", "国临版名称", "诊断名称", "院内名称", "名称", "icd10Name", "name" }),
        ("insCode", new[] { "医保编码", "医保版2.0编码", "医保版编码", "医保2.0编码", "医保诊断编码", "映射编码", "医保ICD编码", "标准编码", "insCode", "insuranceCode" }),
        ("insName", new[] { "医保名称", "医保版2.0名称", "医保版名称", "医保2.0名称", "医保诊断名称", "映射名称", "标准名称", "insName", "insuranceName" }),
        ("grayField", new[] { "灰码", "是否灰码", "gray" }),
    };

    /// <summary>综合病组列名 → 字段</summary>
    public static readonly (string Key, string[] Names)[] ZongheFieldMap =
    {
        ("code", new[] { "病组编码", "综合病组编码", "病种编码", "编码", "code" }),
        ("name", new[] { "病组名称", "综合病组名称", "名称", "name" }),
        ("ranges", new[] { "诊断范围", "诊断编码范围", "范围", "诊断范围编码", "ranges" }),
        ("note", new[] { "备注", "说明", "note" }),
    };

    private static string T(string s) => (s ?? "").Trim();

    // ==================================================================
    // 单条分组
    // ==================================================================

    /// <summary>
    /// 单条分组（对齐 WebRouter /api/group）：可选映射转换 → 分组 → 补全名称。
    /// <paramref name="withTrace"/> 为 true 时同时产出逐层溯源（<see cref="GroupResult.Trace"/>）；
    /// Web 端 <c>/api/group</c> 走默认值 false，保持与 Node 版逐字节一致。
    /// </summary>
    public static GroupResult Group(AppState S, Kase kase, bool useMapping, bool withTrace = false)
    {
        Dictionary<string, object> translateInfo = null;
        if (useMapping && S.Mapping.Rows.Count > 0) translateInfo = S.ApplyMapping(kase);
        var result = S.Grouper.Group(kase, withTrace);
        S.WithDetailNames(result);
        if (translateInfo != null) result.Translate = translateInfo;
        return result;
    }

    // ==================================================================
    // 批量分组
    // ==================================================================

    /// <summary>批量分组结果行（序列化后与既有 Web JSON 契约一致）</summary>
    public sealed class BatchItem
    {
        public int Seq { get; set; }
        public string CaseId { get; set; }
        public string MainDiag { get; set; }
        public string MainProc { get; set; }
        public bool Matched { get; set; }
        public string GroupType { get; set; }
        public string GroupTypeCode { get; set; }
        public object DipSeq { get; set; }
        public string Name { get; set; }
        public string Reason { get; set; }
        public Dictionary<string, object> Detail { get; set; }
        /// <summary>仅在启用映射时出现 —— 未启用时该键整体不输出（对齐原实现）</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, object> Translate { get; set; }

        /// <summary>
        /// 命中「目录库外病种」时的预警（省局反馈清单）。
        /// <b>不参与序列化</b>：<c>/api/batch</c> 的每行键集合是 Node 对拍基线，
        /// 多一个键就破；桌面端直接读属性即可。
        /// </summary>
        [JsonIgnore]
        public OutLibHit OutLib { get; set; }

        /// <summary>结果表 / 界面用的短文案（空 = 未命中清单）。</summary>
        [JsonIgnore]
        public string OutLibText => OutLib == null ? "" : $"{OutLib.Label}（2027 起不作核心病种）";
    }

    public sealed class BatchSummary
    {
        public int Total { get; set; }
        public int Matched { get; set; }
        public Dictionary<string, int> ByType { get; set; } = new();
        /// <summary>命中目录库外病种（省局清单）的病例数。不参与序列化，理由同 <see cref="BatchItem.OutLib"/>。</summary>
        [JsonIgnore]
        public int OutLibCases { get; set; }
    }

    public sealed class BatchResult
    {
        public List<BatchItem> Rows { get; set; } = new();
        public BatchSummary Summary { get; set; } = new();
        /// <summary>结果 Excel 字节（对齐 Web 版 /api/download 的产物）</summary>
        public byte[] Xlsx { get; set; }
    }

    /// <summary>批量分组：逐行分组 → 汇总 → 生成结果 Excel。</summary>
    public static BatchResult Batch(AppState S, IEnumerable<Dictionary<string, JsonElement>> rawRows, bool useMapping)
    {
        var src = (rawRows ?? Enumerable.Empty<Dictionary<string, JsonElement>>()).ToList();
        var rows = new List<BatchItem>();
        for (var i = 0; i < src.Count; i++)
        {
            var kase = GrouperEngine.ReadCase(src[i]);
            Dictionary<string, object> tr = null;
            if (useMapping) tr = S.ApplyMapping(kase);
            var r = S.Grouper.Group(kase);
            rows.Add(new BatchItem
            {
                Seq = i + 1,
                CaseId = kase.CaseId ?? "",
                MainDiag = kase.MainDiag ?? "",
                MainProc = kase.MainProc ?? "",
                Matched = r.Matched,
                GroupType = r.GroupType ?? "",
                GroupTypeCode = r.GroupTypeCode ?? "",
                DipSeq = r.Seq.HasValue ? r.Seq.Value : (object)"",
                Name = r.Name ?? "",
                Reason = r.Reason ?? "",
                Detail = r.Detail ?? new Dictionary<string, object>(),
                Translate = tr,
                OutLib = r.OutLib,
            });
        }
        return new BatchResult
        {
            Rows = rows,
            Summary = Summarize(rows),
            Xlsx = ResultsToXlsx(rows, useMapping),
        };
    }

    public static BatchSummary Summarize(List<BatchItem> rows)
    {
        var byType = new Dictionary<string, int>();
        var matched = 0;
        foreach (var r in rows)
        {
            if (!r.Matched) continue;
            matched++;
            var t = r.GroupType;
            if (string.IsNullOrEmpty(t)) continue;
            byType[t] = byType.TryGetValue(t, out var c) ? c + 1 : 1;
        }
        return new BatchSummary
        {
            Total = rows.Count,
            Matched = matched,
            ByType = byType,
            OutLibCases = rows.Count(r => r.OutLib != null),
        };
    }

    /// <summary>分组结果 → 结果 Excel（列顺序与表头文案保持与原实现一致）。</summary>
    public static byte[] ResultsToXlsx(List<BatchItem> rows, bool useMapping)
    {
        var headers = new List<string>
        {
            "序号", "病案号", "主要诊断编码", "主要手术操作编码", "入组", "组别",
            "组别代码", "病种序号", "分组名称", "未入组原因", "匹配明细", "目录库外病种",
        };
        if (useMapping) headers.Add("主诊断映射");

        var aoa = new List<List<object>> { headers.Cast<object>().ToList() };
        foreach (var r in rows)
        {
            var line = new List<object>
            {
                r.Seq,
                r.CaseId ?? "",
                r.MainDiag ?? "",
                r.MainProc ?? "",
                r.Matched ? "是" : "否",
                r.GroupType ?? "",
                r.GroupTypeCode ?? "",
                r.DipSeq ?? "",
                r.Name ?? "",
                r.Reason ?? "",
                JsonSerializer.Serialize(r.Detail ?? new Dictionary<string, object>(), Js.Http),
                r.OutLibText,
            };
            if (useMapping)
            {
                var text = "";
                if (r.Translate != null
                    && r.Translate.TryGetValue("mainDiag", out var mdv) && mdv is Dictionary<string, object> md)
                {
                    var gray = md.TryGetValue("gray", out var g) && g is bool gb && gb;
                    var matched = md.TryGetValue("matched", out var m) && m is bool mb && mb;
                    text = gray ? "灰码(未映射)"
                        : matched ? $"{md.GetValueOrDefault("from")} → {md.GetValueOrDefault("to")}"
                        : "未命中映射";
                }
                line.Add(text);
            }
            aoa.Add(line);
        }
        return ExcelUtil.BuildWorkbook(new[]
        {
            new ExcelUtil.SheetSpec { Name = "分组结果", Aoa = aoa },
        });
    }

    /// <summary>批量分组模板（客户端可另存为文件；Web 端直接下发）。</summary>
    public static byte[] BuildBatchTemplate()
    {
        var headers = new List<object> { "病案号", "主要诊断编码", "其他诊断编码", "主要手术操作编码", "其他手术操作编码", "年龄", "年龄单位", "出生体重" };
        var samples = new List<List<object>>
        {
            new() { "MZ001", "T20.600", "T31.000x001", "", "", "", "", "" },
            new() { "MZ002", "T20.600", "T31.400", "86.6903", "", "", "", "" },
            new() { "MZ003", "I74.306", "", "", "", "", "", "" },
            new() { "MZ004", "A15.0", "", "", "", "", "", "" },
            new() { "MZ005", "P07.100", "", "", "", "5", "天", "1200" },
        };
        var notes = new List<List<object>>
        {
            new() { "列名", "必填", "说明" },
            new() { "病案号", "否", "自定义标识，原样带回结果表" },
            new() { "主要诊断编码", "与主手术至少一项", "ICD-10 国临版编码或医保版2.0编码，如 T20.600；启用映射时自动转医保码" },
            new() { "其他诊断编码", "否", "多个用 | ; ，分隔，如 T31.000x001;T32.1；烧伤类需含面积码 T31/T32" },
            new() { "主要手术操作编码", "与主诊断至少一项", "ICD-9-CM3 编码，如 86.6903" },
            new() { "其他手术操作编码", "否", "多个用 | ; ，分隔" },
            new() { "年龄 / 年龄单位", "新生儿必填", "年龄单位：天/月/岁；先期分组新生儿条件需 年龄<29天 且 出生体重" },
            new() { "出生体重", "新生儿必填", "单位克，如 1200" },
            new() { "", "", "" },
            new() { "说明", "", "仅填第一行表头即可被识别；CSV 亦支持（UTF-8）；留空列可不填；示例行可直接删除" },
        };
        return ExcelUtil.BuildWorkbook(new[]
        {
            new ExcelUtil.SheetSpec { Name = "批量分组", Aoa = new List<List<object>> { headers }.Concat(samples).ToList(), ColWidths = new[] { 12, 16, 22, 20, 20, 8, 10, 10 } },
            new ExcelUtil.SheetSpec { Name = "填写说明", Aoa = notes, ColWidths = new[] { 18, 16, 80 } },
        });
    }

    /// <summary>病案 Excel → 分组入参行（列名按 BatchFieldMap 归一化）。</summary>
    public static List<Dictionary<string, JsonElement>> ReadSheetRows(byte[] buffer)
    {
        var wb = ExcelUtil.Open(buffer);
        var objs = ExcelUtil.ReadObjects(wb.GetSheetAt(0));
        var outRows = new List<Dictionary<string, JsonElement>>();
        foreach (var o in objs)
        {
            var mapped = new Dictionary<string, object>();
            foreach (var (key, names) in BatchFieldMap)
            {
                foreach (var n in names)
                {
                    if (o.TryGetValue(n, out var v) && v != null && v != "")
                    {
                        mapped[key] = v;
                        break;
                    }
                }
            }
            outRows.Add(ToJsonDict(mapped));
        }
        return outRows;
    }

    /// <summary>object 值字典 → JsonElement 字典（复用分组器的入参读取）。</summary>
    public static Dictionary<string, JsonElement> ToJsonDict(Dictionary<string, object> o)
    {
        var json = JsonSerializer.Serialize(o, Js.Http);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    // ==================================================================
    // 联想检索
    // ==================================================================

    public sealed class DipHit
    {
        public string Type { get; set; }
        public int Seq { get; set; }
        public string Name { get; set; }
    }

    public sealed class SuggestHit
    {
        public string Code { get; set; }
        public string Name { get; set; }
        public string InsCode { get; set; }
        public string InsName { get; set; }
        public bool Gray { get; set; }
        public bool Linked { get; set; }
        public List<DipHit> Dip { get; set; }
    }

    public sealed class LinkedSuggest
    {
        public string MainDiag { get; set; }
        public List<SuggestHit> OtherDiags { get; set; } = new();
        public List<SuggestHit> Procs { get; set; } = new();
    }

    /// <summary>诊断联想：编码前缀优先，其次名称/拼音；灰码不给 DIP 病种预览。</summary>
    public static List<SuggestHit> SuggestDiag(AppState S, string q, int limit = 10)
    {
        return S.Mapping.Suggest(q, limit).Select(r => new SuggestHit
        {
            Code = r.Code,
            Name = r.Name,
            InsCode = r.InsCode,
            InsName = r.InsName,
            Gray = r.Gray,
            Dip = !r.Gray && !string.IsNullOrEmpty(r.InsCode) ? S.DipPreview(r.InsCode) : new List<DipHit>(),
        }).ToList();
    }

    /// <summary>中文检索时前端需展示「共 N 条」提示，故单独给出计数。</summary>
    public static int SuggestDiagTotal(AppState S, string q) => S.Mapping.CountName(q);

    /// <summary>
    /// 按编码取名称（诊断查映射表、手术查手术库），用于输入框右侧回显。
    /// 找不到返回空串 —— 调用方据此决定要不要显示。
    /// </summary>
    public static string NameOf(AppState S, string code, bool proc = false)
    {
        var c = T(code).ToUpperInvariant();
        if (c.Length == 0) return "";
        if (!proc) return S.Mapping.DisplayNameOfCode(c);
        foreach (var p in S.ProcLib)
            if (string.Equals((p.Code ?? "").Trim().ToUpperInvariant(), c, StringComparison.Ordinal))
                return p.Name ?? "";
        return "";
    }

    /// <summary>手术联想：编码前缀优先，其次名称/拼音。</summary>
    public static List<SuggestHit> SuggestProc(AppState S, string q, int limit = 10)
    {
        var kw = T(q).ToUpperInvariant();
        if (kw.Length == 0) return new List<SuggestHit>();
        var byCode = S.ProcLib.Where(p => (p.Code ?? "").ToUpperInvariant().StartsWith(kw, StringComparison.Ordinal)).Take(limit).ToList();
        var byName = S.ProcLib.Where(p => !(p.Code ?? "").ToUpperInvariant().StartsWith(kw, StringComparison.Ordinal)
            && (((p.Name ?? "").ToUpperInvariant().Contains(kw, StringComparison.Ordinal)) || PinyinHelper.PyMatch(p.Name, kw))).Take(limit).ToList();
        return byCode.Concat(byName).Take(limit)
            .Select(p => new SuggestHit { Code = p.Code, Name = p.Name }).ToList();
    }

    /// <summary>主诊断联动建议：由主诊断命中的病种行带出其他诊断（面积码等）与相关手术。</summary>
    public static LinkedSuggest SuggestLinked(AppState S, string mainDiagRaw, int limit = 12)
    {
        var raw = T(mainDiagRaw);
        MappingStore.TranslateResult tr = null;
        if (S.Mapping.Rows.Count > 0 && raw.Length > 0) tr = S.Mapping.Translate(raw);
        var md = !string.IsNullOrEmpty(tr?.To) ? tr.To : raw;

        var odMap = new Dictionary<string, SuggestHit>();
        var procMap = new Dictionary<string, SuggestHit>();
        if (md.Length > 0)
        {
            string LookupName(string oc, out string insCode, out string insName, out bool gray)
            {
                var row = S.Mapping.Rows.FirstOrDefault(r => r.Code == oc || r.InsCode == oc)
                          ?? S.Mapping.Rows.FirstOrDefault(r => (r.InsCode ?? "").StartsWith(oc, StringComparison.Ordinal)
                                                             || (r.Code ?? "").StartsWith(oc, StringComparison.Ordinal));
                insCode = row?.InsCode ?? "";
                insName = row?.InsName ?? "";
                gray = row?.Gray ?? false;
                return row?.Name ?? "";
            }

            foreach (var key in new[] { "fz_shaoshang", "fz_zhongliu", "fz_jiehe", "bingxiang", "jichu", "jiceng" })
            {
                foreach (var g in SectionsOf(S, key))
                {
                    if (!(g.DiagCodes ?? new List<string>()).Any(d => Matcher.MatchDiag(d, md))) continue;

                    foreach (var oc in g.OtherDiagCodes ?? new List<string>())
                    {
                        if ((oc ?? "").Contains('-')) continue;
                        if (odMap.ContainsKey(oc)) continue;
                        var nm = LookupName(oc, out var insCode, out var insName, out var gray);
                        odMap[oc] = new SuggestHit
                        {
                            Code = oc,
                            Name = nm,
                            InsCode = insCode.Length > 0 ? insCode : oc,
                            InsName = insName,
                            Gray = gray,
                            Linked = true,
                        };
                    }

                    var names = (g.ProcName ?? "").Split('|').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                    var codes = (g.ProcCodes ?? new List<string>()).Concat(g.ProcAlt ?? new List<string>()).ToList();
                    for (var i = 0; i < codes.Count; i++)
                    {
                        var c = codes[i];
                        if (string.IsNullOrEmpty(c) || procMap.ContainsKey(c)) continue;
                        procMap[c] = new SuggestHit
                        {
                            Code = c,
                            Name = i < names.Count ? names[i] : (names.Count > 0 ? names[0] : ""),
                            Linked = true,
                        };
                    }
                }
            }
        }
        return new LinkedSuggest
        {
            MainDiag = md,
            OtherDiags = odMap.Values.Take(limit).ToList(),
            Procs = procMap.Values.Take(limit).ToList(),
        };
    }

    // ==================================================================
    // 病种库区段（供联动建议遍历）
    // ==================================================================

    public sealed class SectionRow
    {
        public List<string> DiagCodes { get; set; }
        public List<string> OtherDiagCodes { get; set; }
        public List<string> ProcCodes { get; set; }
        public List<string> ProcAlt { get; set; }
        public string ProcName { get; set; }
    }

    public static List<SectionRow> SectionsOf(AppState S, string key)
    {
        var list = new List<SectionRow>();
        switch (key)
        {
            case "fz_shaoshang":
                foreach (var r in S.Config.FzShaoshang)
                    list.Add(new SectionRow { DiagCodes = r.DiagCodes, OtherDiagCodes = r.OtherDiagCodes, ProcCodes = r.ProcCodes, ProcName = r.ProcName });
                break;
            case "fz_zhongliu":
                foreach (var r in S.Config.FzZhongliu)
                    list.Add(new SectionRow { DiagCodes = r.DiagCodes, OtherDiagCodes = r.OtherDiagCodes, ProcAlt = r.ProcAlt, ProcName = r.ProcName });
                break;
            case "fz_jiehe":
                foreach (var r in S.Config.FzJiehe)
                    list.Add(new SectionRow { DiagCodes = r.DiagCodes, ProcCodes = r.ProcCodes, ProcName = r.ProcName });
                break;
            case "bingxiang":
                foreach (var r in S.Config.Bingxiang)
                    list.Add(new SectionRow { DiagCodes = r.DiagCodes, ProcCodes = r.ProcCodes, ProcName = r.ProcName });
                break;
            case "jichu":
                foreach (var r in S.Config.Jichu)
                    list.Add(new SectionRow { DiagCodes = r.DiagCodes, ProcCodes = r.ProcCodes, ProcName = r.ProcName });
                break;
            case "jiceng":
                foreach (var r in S.Config.Jiceng)
                    list.Add(new SectionRow { DiagCodes = r.DiagCodes, ProcCodes = r.ProcCodes, ProcName = r.ProcName });
                break;
        }
        return list;
    }
}
