using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dip3Grouper;

/// <summary>
/// 单个区段的导入报告（原 Web 侧匿名类型）。
/// 属性名与顺序对齐原实现，序列化输出 label/added/updated/errors/errorCount 逐字段一致。
/// </summary>
public sealed class AdminSectionReport
{
    public string Label { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public List<string> Errors { get; set; } = new();
    public int ErrorCount { get; set; }
}

/// <summary>映射表导入结果（供桌面端强类型使用；Web 侧仍自行拼装响应体）。</summary>
public sealed class MappingImportResult
{
    public int Parsed { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Total { get; set; }
    public MappingStats Stats { get; set; }
}

/// <summary>病种库结构导入结果。</summary>
public sealed class DiseasesImportResult
{
    public Dictionary<string, AdminSectionReport> Report { get; set; } = new();
    public Dictionary<string, SectionCount> Counts { get; set; }
}

/// <summary>立项指南导入结果。</summary>
public sealed class LixiangImportResult
{
    public Dictionary<string, LixiangSectionReport> Report { get; set; } = new();
    public Dictionary<string, SectionCount> Counts { get; set; }
    public int Total { get; set; }
}

/// <summary>
/// 立项指南逐 sheet 导入报告（原为两种形态的匿名类型，合并为一个可空字段的具名类型）。
///
/// 序列化兼容性依赖 <see cref="JsonIgnoreCondition.WhenWritingNull"/>：
///   · 成功分支 → label / tab / added / groupRows / guideUsage
///   · 跳过分支 → label / skipped
/// 空值字段被忽略，因此两种形态的 JSON 与原先逐字段一致。
/// </summary>
public sealed class LixiangSectionReport
{
    public string Label { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Tab { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Added { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? GroupRows { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? GuideUsage { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Skipped { get; set; }
}

/// <summary>立项指南类级使用说明保存结果（原匿名类型）。</summary>
public sealed class GuideUsageResult
{
    public bool Ok { get; set; } = true;
    public string Tab { get; set; }
    public int Length { get; set; }
}

/// <summary>德州物价 Excel 解析出的一行（字段与 <see cref="DezhouPriceItem"/> 对齐）。</summary>
public class DezhouImportRow
{
    public string Doc { get; set; }
    public string Exec { get; set; }
    public string Category { get; set; }
    public string Seq { get; set; }
    public string Code { get; set; }
    public string Kind { get; set; }
    public string Name { get; set; }
    public string ServiceOutput { get; set; }
    public string PriceComposition { get; set; }
    public string Unit { get; set; }
    public string Price3 { get; set; }
    public string Price2 { get; set; }
    public string Price1 { get; set; }
    /// <summary>扣减基本物耗价格（仅「一般治疗」类项目表有此列，正式版 25 号文起）。</summary>
    public string Deduct { get; set; }
    public string Insurance { get; set; }
    public string SelfPay { get; set; }
    public string Note { get; set; }
    public string Usage { get; set; }
}

/// <summary>德州物价导入结果。</summary>
public sealed class DezhouImportResult
{
    public int Parsed { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    /// <summary>编码/名称不合规而跳过的行数</summary>
    public int Skipped { get; set; }
    public int Total { get; set; }
}

/// <summary>解读辅导导入结果（原匿名类型）。</summary>
public sealed class JieduImportResult
{
    public int Total { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Issues { get; set; }
}

/// <summary>
/// 后台导入的**输入校验**错误。
/// 这类错误的文案可直接回给前端（Node 侧同样是不加前缀直接 sendJSON 的），
/// 与「处理过程失败」（需加 <c>XXX导入失败: </c> 前缀）区分开。
/// 抛这个类型可保证接口错误文案与 Node 版逐字一致。
/// </summary>
public sealed class AdminInputException : Exception
{
    public AdminInputException(string message) : base(message) { }
}

/// <summary>
/// 后台管理业务编排：病种库 / 立项指南 / 映射表 / 综合病组 的导入解析与写库。
///
/// 与 <see cref="GrouperService"/> 同理，这里是 **唯一实现**：
///   - Web 端（Dip3Grouper.Web）后台路由调用它
///   - 桌面端（Dip3Grouper.Desktop）后台页面直接调用它
/// 两端行为因此逐字节一致，不存在分叉。
/// </summary>
public static class AdminService
{
    // ==================================================================
    // 病种库
    // ==================================================================

    /// <summary>
    /// 病种库全量导入（对齐原 /api/admin/config）：
    /// 由 Excel 构建整份配置 → 旧文件另存 .bak.json → 原子写入 → 重载 → 记录元信息。
    ///
    /// 注意：元信息里的各区段条数取**导入文件自带的声明值**（config.Meta.Counts），
    /// 而非实际行数——与原实现一致；实际行数请用 <see cref="SectionCountsDict"/>。
    /// </summary>
    public static ConfigMetaFile ImportConfig(AppState S, byte[] content, string fileName)
    {
        var config = BuildConfig.Build(content, fileName);
        VersionStore.Create("导入前自动 · 整库覆盖", fileName, auto: true);   // 先留一份当前状态，随时可回滚
        if (File.Exists(AppPaths.ConfigFile))
            FileExt.CopyFileSafe(AppPaths.ConfigFile, Path.Combine(AppPaths.ConfigDir,
                Path.GetFileNameWithoutExtension(AppPaths.ConfigFile)
                + $"-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.bak.json"));
        Js.WriteJsonFile(AppPaths.ConfigFile, config, Js.Config, 0);
        S.ReloadConfig();

        var meta = new ConfigMetaFile
        {
            ImportedAt = Js.Iso(),
            SourceName = fileName,
            Counts = S.Config.Meta?.Counts?.ToDictionary(kv => kv.Key, kv => new SectionCount
            {
                Label = Diseases.Get(kv.Key)?.Label ?? kv.Key,
                Total = kv.Value,
            }),
        };
        MappingStore.SaveConfigMeta(meta);
        var total = Diseases.SectionCounts(S.Config).Values.Sum(x => x.Total);
        AuditLog.Write(AuditAction.Import, "病种库",
            $"整库导入 {fileName} · {Diseases.Sections.Count} 个区段 {total:N0} 条");
        return meta;
    }

    /// <summary>
    /// 病种库模板（onlyTab 为 null / 空 / 未识别的名称时导出全部区段的空模板，
    /// 与原实现一致：未知区段名不会报错，只是退化为全量导出）。
    /// </summary>
    public static (byte[] Bytes, string FileName) DiseasesTemplate(string onlyTab)
    {
        var tab = SheetTabOf(onlyTab);
        var samples = TemplateSamples();
        var sheets = new List<ExcelUtil.SheetSpec>();
        foreach (var sec in Diseases.Sections)
        {
            if (tab != null && sec.Tab != tab) continue;
            var aoa = new List<List<object>> { sec.Columns.Select(c => (object)c.Label).ToList() };
            foreach (var f in samples.TryGetValue(sec.Tab, out var s) ? s : new List<Dictionary<string, string>>())
                aoa.Add(sec.Columns.Select(c => (object)(f.TryGetValue(c.Key, out var v) ? v : "")).ToList());
            sheets.Add(new ExcelUtil.SheetSpec
            {
                Name = sec.Label,
                Aoa = aoa,
                ColWidths = ExcelUtil.WidthsFromPixels(sec.Columns.Select(c => c.Width ?? 120)),
            });
        }
        var fname = tab != null
            ? $"dip3-diseases-{tab}-template.xlsx"
            : "dip3-diseases-template.xlsx";
        return (ExcelUtil.BuildWorkbook(sheets), fname);
    }

    /// <summary>
    /// 病种库结构导入（对齐原 /api/admin/diseases-import）：
    /// 逐 sheet 按区段写库，replace 先清空区段，upsert 命中则更新。
    /// tabField 非空时强制指定区段（忽略 sheet 名）。
    /// </summary>
    public static DiseasesImportResult ImportDiseases(AppState S, byte[] content, string fileName,
        string tabField, string mode)
    {
        var wb = ExcelUtil.Open(content);
        var forcedTab = !string.IsNullOrEmpty(tabField) ? SheetTabOf(tabField) : null;
        if (!string.IsNullOrEmpty(tabField) && forcedTab == null)
            throw new AdminInputException($"未知的区段: {tabField}");

        VersionStore.Create("导入前自动 · 区段导入", fileName, auto: true);   // 导入前的状态留档

        var report = new Dictionary<string, AdminSectionReport>();
        var touchedExclusions = false;

        for (var si = 0; si < wb.NumberOfSheets; si++)
        {
            var shName = wb.GetSheetName(si);
            var tab = forcedTab ?? SheetTabOf(shName);
            if (string.IsNullOrEmpty(tab)) continue;
            if (report.ContainsKey(tab)) continue;
            var sec = Diseases.Get(tab);
            var aoa = ExcelUtil.ReadAoa(wb.GetSheetAt(si));
            if (aoa.Count == 0) continue;

            var headers = (aoa[0] ?? new List<string>()).Select(h => (h ?? "").Trim()).ToList();
            var colKey = headers.Select(h =>
            {
                var byLabel = sec.Columns.FirstOrDefault(c => c.Label == h);
                if (byLabel != null) return byLabel.Key;
                var byKey = sec.Columns.FirstOrDefault(c => c.Key == h);
                return byKey?.Key;
            }).ToList();
            if (!colKey.Any(k => k != null)) continue;

            if (mode == "replace") SetSectionRows(S.Config, tab, new List<object>());

            int added = 0, updated = 0;
            var errors = new List<string>();
            for (var i = 1; i < aoa.Count; i++)
            {
                var cells = aoa[i] ?? new List<string>();
                var flat = new Dictionary<string, string>();
                var hasValue = false;
                for (var ci = 0; ci < colKey.Count; ci++)
                {
                    var k = colKey[ci];
                    if (k == null) continue;
                    var v = ((ci < cells.Count ? cells[ci] : "") ?? "").Trim();
                    if (v.Length > 0) { flat[k] = v; hasValue = true; }
                }
                if (!hasValue) continue;

                var keyVal = flat.TryGetValue(sec.Key, out var kv) && kv.Length > 0 ? kv : null;
                var exists = keyVal != null && sec.GetRows(S.Config).Any(r => sec.KeyOf(r) == keyVal);
                if (mode == "upsert" && exists)
                {
                    var r = Diseases.UpdateRow(S.Config, tab, keyVal, flat);
                    if (!r.Ok) errors.Add($"第{i + 1}行: {r.Error}"); else updated++;
                }
                else
                {
                    var r = Diseases.AddRow(S.Config, tab, flat);
                    if (!r.Ok) errors.Add($"第{i + 1}行: {r.Error}"); else added++;
                }
            }
            if (tab is "excludedDiags" or "excludedProcs") touchedExclusions = true;
            report[tab] = new AdminSectionReport
            {
                Label = sec.Label,
                Added = added,
                Updated = updated,
                Errors = errors.Take(5).ToList(),
                ErrorCount = errors.Count,
            };
        }

        Diseases.SaveConfig(S.Config);
        S.ReloadConfig();
        if (touchedExclusions) S.Grouper.RefreshExclusions();

        var counts = SectionCountsDict(S.Config);
        MappingStore.SaveConfigMeta(new ConfigMetaFile
        {
            ImportedAt = Js.Iso(),
            SourceName = fileName,
            Counts = counts,
        });
        var touched = string.Join("、", report.Where(kv => kv.Value.Added + kv.Value.Updated > 0)
            .Select(kv => $"{kv.Value.Label} +{kv.Value.Added}/~{kv.Value.Updated}"));
        AuditLog.Write(AuditAction.Import, "病种库",
            $"区段导入 {fileName}（{(mode == "replace" ? "替换" : "追加更新")}）：{(touched.Length > 0 ? touched : "无变化")}",
            report.Values.Sum(v => v.ErrorCount) == 0,
            report.Values.Sum(v => v.ErrorCount) == 0 ? null : $"{report.Values.Sum(v => v.ErrorCount)} 行被跳过");
        return new DiseasesImportResult { Report = report, Counts = counts };
    }

    // ==================================================================
    // 立项指南
    // ==================================================================

    /// <summary>立项指南导入（对齐原 /api/admin/lixiang-import）。</summary>
    public static LixiangImportResult ImportLixiang(AppState S, byte[] content, string fileName,
        string tabField, string mode)
    {
        var wb = ExcelUtil.Open(content);
        var names = new List<string>();
        var aoas = new Dictionary<string, List<List<string>>>();
        for (var i = 0; i < wb.NumberOfSheets; i++)
        {
            var n = wb.GetSheetName(i);
            names.Add(n);
            aoas[n] = ExcelUtil.ReadAoa(wb.GetSheetAt(i));
        }
        var (sections, report) = S.Lixiang.ParseWorkbook(names, aoas);

        if (!string.IsNullOrEmpty(tabField))
        {
            var tabs = sections.Keys.ToList();
            if (tabs.Count == 0)
                throw new AdminInputException("未识别到表头（首行需含「项目名称」）");
            if (!S.Lixiang.State.Sections.TryGetValue(tabField, out var cur))
                throw new AdminInputException($"未知的区段: {tabField}");
            var sec = sections[tabs[0]];
            sections = new Dictionary<string, LixiangStore.ParsedSection>
            {
                [tabField] = new LixiangStore.ParsedSection
                {
                    Label = cur.Label,
                    Guide = !string.IsNullOrEmpty(cur.Guide) ? cur.Guide : sec.Guide,
                    Rows = sec.Rows,
                },
            };
        }

        S.Lixiang.ApplySections(sections, mode);
        S.Lixiang.Save();
        var total = S.Lixiang.State.Sections.Values.Sum(x => x.Rows.Count);
        S.Lixiang.State.Meta = new LixiangMeta
        {
            ImportedAt = Js.Iso(),
            SourceName = fileName,
            Sections = S.Lixiang.State.Sections.Count,
            Total = total,
        };
        S.Lixiang.Save();

        AuditLog.Write(AuditAction.Import, "立项指南",
            $"立项指南导入 {fileName}（{(mode == "replace" ? "替换" : "追加更新")}）："
            + $"{report.Count} 个区段 · 共 {total:N0} 个项目"
            + (string.IsNullOrEmpty(tabField) ? "" : $" · 指定区段 {tabField}"));

        return new LixiangImportResult
        {
            Report = report,
            Counts = S.Lixiang.SectionCounts(),
            Total = total,
        };
    }

    /// <summary>立项指南模板（有数据时导出各区段前 20 行作为样例）。</summary>
    public static (byte[] Bytes, string FileName) LixiangTemplate(AppState S, string onlyTab)
    {
        var headers = LixiangStore.Columns.Select(c => (object)c.Label).ToList();
        var sample = new Dictionary<string, string>
        {
            ["seq"] = "1",
            ["name"] = "示例项目名称",
            ["category"] = "",
            ["unit"] = "次",
            ["serviceOutput"] = "项目提供的服务内容说明。",
            ["priceComposition"] = "所定价格涵盖……的人力资源和基本物质资源消耗。",
            ["addItems"] = "01儿童\n11主任医师加收",
            ["extItems"] = "01人工智能辅助",
            ["usage"] = "本项目所称的“次”指：……",
        };
        var colWidths = ExcelUtil.WidthsFromPixels(LixiangStore.Columns.Select(c => c.Width ?? 120));
        var sheets = new List<ExcelUtil.SheetSpec>();
        var sections = S.Lixiang.State.Sections.Where(kv => onlyTab == null || kv.Key == onlyTab).ToList();

        if (sections.Count == 0)
        {
            sheets.Add(new ExcelUtil.SheetSpec
            {
                Name = "立项指南",
                Aoa = new List<List<object>>
                {
                    headers,
                    LixiangStore.Columns.Select(c => (object)(sample.TryGetValue(c.Key, out var v) ? v : "")).ToList(),
                },
                ColWidths = colWidths,
            });
        }
        else
        {
            foreach (var (_, sec) in sections)
            {
                var aoa = new List<List<object>> { headers };
                foreach (var r in sec.Rows.Take(20))
                {
                    aoa.Add(new List<object>
                    {
                        r.Seq, r.Name, r.Category, r.Unit, r.ServiceOutput,
                        r.PriceComposition, r.AddItems, r.ExtItems, r.Usage,
                    });
                }
                var label = sec.Label ?? "";
                sheets.Add(new ExcelUtil.SheetSpec
                {
                    Name = label.Length > 28 ? label[..28] : label,
                    Aoa = aoa,
                    ColWidths = colWidths,
                });
            }
        }
        var fname = onlyTab != null ? $"lixiang-{onlyTab}-template.xlsx" : "lixiang-template.xlsx";
        return (ExcelUtil.BuildWorkbook(sheets), fname);
    }

    /// <summary>
    /// 解读辅导文档解析：文档字节 → 纯文本 → 「期 → 问答」。
    /// 上传件会先落盘到 data/_jiedu_upload.* 供 DocToText 提取，结束后删除。
    /// </summary>
    public static (string Preface, List<JieduItem> List) ParseJieduDocument(byte[] content, string fileName)
    {
        var ext = Regex.Match(fileName ?? "", @"\.[a-z0-9]+$", RegexOptions.IgnoreCase) is { Success: true } m
            ? m.Value.ToLowerInvariant()
            : ".txt";
        var tmp = Path.Combine(AppPaths.DataDir, "_jiedu_upload" + ext);
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllBytes(tmp, content);
            var text = DocToText.Extract(content, ext);
            return LixiangStore.ParseJieduText(text);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 忽略清理失败 */ }
        }
    }

    /// <summary>解读辅导导入（解析 + 合并/替换入库），桌面端一步到位用。</summary>
    public static JieduImportResult ImportJiedu(AppState S, byte[] content, string fileName, string mode)
    {
        var (preface, list) = ParseJieduDocument(content, fileName);
        if (list.Count == 0)
            throw new InvalidOperationException("未解析到问答条目，请确认文件为解读辅导文档");
        var r = S.Lixiang.JieduImport(preface, list, mode, fileName);
        AuditLog.Write(AuditAction.Import, "立项指南",
            $"解读辅导导入 {fileName}（{(mode == "replace" ? "替换" : "追加更新")}）：新增 {r.Added} · 更新 {r.Updated} · 共 {r.Total} 条");
        return r;
    }

    // ==================================================================
    // 映射表 / 综合病组
    // ==================================================================

    /// <summary>映射表导入（对齐原 /api/admin/mapping-import）。</summary>
    public static MappingImportResult ImportMapping(AppState S, byte[] content, string fileName, string mode)
    {
        var rawRows = ReadMappingRows(content, fileName);
        var r = S.Mapping.ImportRows(rawRows, mode, fileName);
        AuditLog.Write(AuditAction.Import, "映射与灰码",
            $"映射表导入 {fileName}（{(mode == "replace" ? "替换" : "追加更新")}）：解析 {r.Parsed} · 新增 {r.Added} · 更新 {r.Updated} · 共 {r.Total}");
        return new MappingImportResult
        {
            Parsed = r.Parsed,
            Added = r.Added,
            Updated = r.Updated,
            Total = r.Total,
            Stats = S.Mapping.Stats(),
        };
    }

    /// <summary>综合病组导入（对齐原 /api/admin/zonghe-import），导入后同步给分组器。</summary>
    public static ZongheImportResult ImportZonghe(AppState S, byte[] content, string fileName, string mode)
    {
        var rawRows = ReadZongheRows(content);
        VersionStore.Create("导入前自动 · 综合病组", fileName, auto: true);
        var r = S.Zonghe.ImportRows(rawRows, mode, fileName);
        S.Grouper.SetZonghe(S.Zonghe.Rows);
        AuditLog.Write(AuditAction.Import, "病种库",
            $"综合病组导入 {fileName}（{(mode == "replace" ? "替换" : "追加更新")}）：解析 {r.Parsed} · 新增 {r.Added} · 更新 {r.Updated} · 共 {r.Total}");
        return r;
    }

    /// <summary>综合病组导入模板。</summary>
    public static byte[] ZongheTemplate() => ExcelUtil.BuildWorkbook(new[]
    {
        new ExcelUtil.SheetSpec
        {
            Name = "综合病组",
            Aoa = new List<List<object>>
            {
                new() { "病组编码", "病组名称", "诊断范围", "备注" },
                new() { "ZH-A15", "综合病组（呼吸道结核）", "A15-A16", "示例行，可删除" },
            },
        },
    });

    // ==================================================================
    // 德州市物价（国家立项指南在德州落地的规范整合项目）
    // ==================================================================

    /// <summary>表头别名 → 字段 key（我们的模板表头与医院公示附件的表头都能识别）。</summary>
    private static readonly Dictionary<string, string> DezhouFieldMap = new()
    {
        ["落地文号"] = "doc", ["文号"] = "doc", ["执行文件"] = "doc",
        ["执行日期"] = "exec", ["执行时间"] = "exec",
        ["类别"] = "category", ["项目类别"] = "category", ["分类"] = "category",
        ["原表序号"] = "seq", ["序号"] = "seq",
        ["项目编码"] = "code", ["编码"] = "code",
        ["项目性质"] = "kind", ["性质"] = "kind",
        ["项目名称"] = "name", ["名称"] = "name",
        ["服务产出"] = "serviceOutput", ["服务产出说明"] = "serviceOutput",
        ["价格构成"] = "priceComposition",
        ["计价单位"] = "unit", ["单位"] = "unit",
        ["三级价格"] = "price3", ["三级"] = "price3", ["三级医院价格"] = "price3", ["三级医疗机构价格"] = "price3",
        ["二级价格"] = "price2", ["二级"] = "price2", ["二级医院价格"] = "price2", ["二级医疗机构价格"] = "price2",
        ["一级价格"] = "price1", ["一级"] = "price1", ["一级医院价格"] = "price1", ["一级医疗机构价格"] = "price1",
        // 仅「一般治疗」类项目表有此列（正式版 25 号文起）
        ["扣减基本物耗价格"] = "deduct", ["扣减基本物耗"] = "deduct", ["扣减物耗"] = "deduct",
        ["是否医保支付"] = "insurance", ["医保支付"] = "insurance",
        ["自付比例"] = "selfPay", ["个人先行自付比例"] = "selfPay", ["先行自付比例"] = "selfPay",
        ["备注"] = "note", ["限定支付范围"] = "note", ["备注（限定支付范围）"] = "note",
        ["计价说明"] = "usage", ["说明"] = "usage",
    };

    /// <summary>
    /// 表头归一化：去掉空白与分隔符、去掉括号补充说明。
    /// 「医保支付（是/否）」→「医保支付」，「备注 / 限定支付范围」→「备注限定支付范围」。
    /// </summary>
    private static string NormDezhouHeader(string h)
    {
        var s = Regex.Replace(h ?? "", @"[\s\u3000/·、　]+", "");
        while (true)
        {
            var t = Regex.Replace(s, @"[（(][^（()）]*[)）]", "");
            if (t == s) break;
            s = t;
        }
        return s.Trim();
    }

    /// <summary>归一化别名表（长别名优先，避免「备注」抢走「限定支付范围」这类同值匹配）。</summary>
    private static readonly List<KeyValuePair<string, string>> DezhouAliasSorted =
        DezhouFieldMap
            .Select(kv => new KeyValuePair<string, string>(NormDezhouHeader(kv.Key), kv.Value))
            .Where(kv => kv.Key.Length > 0)
            .OrderByDescending(kv => kv.Key.Length)
            .ToList();

    /// <summary>按归一化表头匹配字段 key（先精确、再包含）。</summary>
    private static string MatchDezhouHeader(string raw)
    {
        var norm = NormDezhouHeader(raw);
        if (norm.Length == 0) return null;
        foreach (var kv in DezhouAliasSorted)
            if (norm.Contains(kv.Key)) return kv.Value;
        return null;
    }

    /// <summary>取一条德州物价记录的字段值（导出 / 模板 / 比对统一走这里，避免各处手抄列顺序）。</summary>
    public static string DezhouField(DezhouPriceItem r, string key) => key switch
    {
        "doc" => r.Doc,
        "exec" => r.Exec,
        "category" => r.Category,
        "seq" => r.Seq,
        "code" => r.Code,
        "kind" => r.Kind,
        "name" => r.Name,
        "serviceOutput" => r.ServiceOutput,
        "priceComposition" => r.PriceComposition,
        "unit" => r.Unit,
        "price3" => r.Price3,
        "price2" => r.Price2,
        "price1" => r.Price1,
        "deduct" => r.Deduct,
        "insurance" => r.Insurance,
        "selfPay" => r.SelfPay,
        "note" => r.Note,
        "usage" => r.Usage,
        _ => "",
    };

    /// <summary>德州物价导入模板（表头 + 一行示例）。</summary>
    public static (byte[] Bytes, string FileName) DezhouTemplate()
    {
        var cols = DezhouPriceStore.Columns;
        var widths = ExcelUtil.WidthsFromPixels(cols.Select(c => c.Width ?? 120));
        var sample = new Dictionary<string, string>
        {
            ["doc"] = "德医保发〔2026〕3号",
            ["exec"] = "2026-03-01",
            ["category"] = "骨骼肌肉系统类",
            ["seq"] = "1",
            ["code"] = "013315000010000",
            ["kind"] = "main",
            ["name"] = "示例项目名称",
            ["serviceOutput"] = "项目提供的服务内容说明。",
            ["priceComposition"] = "所定价格涵盖……的人力资源和基本物质资源消耗。",
            ["unit"] = "次",
            ["price3"] = "100",
            ["price2"] = "90",
            ["price1"] = "80",
            ["deduct"] = "2.5",
            ["insurance"] = "是",
            ["selfPay"] = "20%",
            ["note"] = "限工伤保险",
            ["usage"] = "本项目所称的“次”指：……",
        };
        return (ExcelUtil.BuildWorkbook(new[]
        {
            new ExcelUtil.SheetSpec
            {
                Name = "德州物价",
                Aoa = new List<List<object>>
                {
                    cols.Select(c => (object)c.Label).ToList(),
                    cols.Select(c => (object)(sample.TryGetValue(c.Key, out var v) ? v : "")).ToList(),
                },
                ColWidths = widths,
            },
        }), "dezhou-price-template.xlsx");
    }

    /// <summary>导出德州物价：按当前筛选条件导出（doc / category / kind 为空 = 不限）。</summary>
    public static (byte[] Bytes, string FileName) ExportDezhou(AppState S, string doc,
        string category = null, string kind = null, string q = null)
    {
        var cols = DezhouPriceStore.Columns;
        var widths = ExcelUtil.WidthsFromPixels(cols.Select(c => c.Width ?? 120));
        var list = S.Dezhou.Items
            .Where(i => DezhouPriceStore.Matches(i, doc ?? "", category ?? "", kind ?? "", q))
            .ToList();
        var aoa = new List<List<object>> { cols.Select(c => (object)c.Label).ToList() };
        aoa.AddRange(list.Select(r => cols.Select(c => (object)DezhouField(r, c.Key)).ToList()));

        var name = string.IsNullOrEmpty(doc) ? "dezhou-price.xlsx" : $"dezhou-{doc}.xlsx";
        return (ExcelUtil.BuildWorkbook(new[]
        {
            new ExcelUtil.SheetSpec
            {
                Name = string.IsNullOrEmpty(doc) ? "德州物价" : doc,
                Aoa = aoa,
                ColWidths = widths,
            },
        }), name);
    }

    /// <summary>
    /// 读取德州物价 Excel：逐 sheet 找表头行（前 5 行内同时含「项目编码」「项目名称」即认定），
    /// 表头别名见 <see cref="DezhouFieldMap"/>；无编码或名称的行跳过。
    /// </summary>
    public static List<DezhouImportRow> ReadDezhouRows(byte[] buffer)
    {
        var wb = ExcelUtil.Open(buffer);
        var rows = new List<DezhouImportRow>();
        var anyHeader = false;

        for (var s = 0; s < wb.NumberOfSheets; s++)
        {
            var aoa = ExcelUtil.ReadAoa(wb.GetSheetAt(s));
            if (aoa.Count == 0) continue;

            Dictionary<string, int> cols = null;
            var headerAt = -1;
            for (var i = 0; i < Math.Min(aoa.Count, 5); i++)
            {
                var row = aoa[i] ?? new List<string>();
                var found = new Dictionary<string, int>();
                for (var j = 0; j < row.Count; j++)
                {
                    var key = MatchDezhouHeader(row[j]);
                    if (key == null) continue;
                    if (!found.ContainsKey(key)) found[key] = j;
                }
                if (found.ContainsKey("code") && found.ContainsKey("name"))
                {
                    cols = found;
                    headerAt = i;
                    break;
                }
            }
            if (cols == null) continue;
            anyHeader = true;

            for (var i = headerAt + 1; i < aoa.Count; i++)
            {
                var arr = aoa[i] ?? new List<string>();
                string Get(string key) =>
                    cols.TryGetValue(key, out var j) && j < arr.Count ? (arr[j] ?? "").Trim() : "";
                var code = Get("code");
                var name = Get("name");
                if (code.Length == 0 && name.Length == 0) continue;
                rows.Add(new DezhouImportRow
                {
                    Doc = Get("doc"),
                    Exec = Get("exec"),
                    Category = Get("category"),
                    Seq = Get("seq"),
                    Code = code,
                    Kind = Get("kind"),
                    Name = name,
                    ServiceOutput = Get("serviceOutput"),
                    PriceComposition = Get("priceComposition"),
                    Unit = Get("unit"),
                    Price3 = Get("price3"),
                    Price2 = Get("price2"),
                    Price1 = Get("price1"),
                    Deduct = Get("deduct"),
                    Insurance = Get("insurance"),
                    SelfPay = Get("selfPay"),
                    Note = Get("note"),
                    Usage = Get("usage"),
                });
            }
        }

        if (!anyHeader)
            throw new AdminInputException("未识别到表头：需含有「项目编码」与「项目名称」两列，可先下载模板");
        return rows;
    }

    /// <summary>
    /// 德州物价导入。<paramref name="mode"/>：upsert = 按（文号, 编码）覆盖或新增；
    /// replace = 先清空该文号（<paramref name="docField"/>）再写入。
    /// </summary>
    public static DezhouImportResult ImportDezhou(AppState S, byte[] content, string fileName,
        string docField, string mode)
    {
        var rows = ReadDezhouRows(content);
        if (rows.Count == 0) throw new AdminInputException("未解析到有效数据行");

        var store = S.Dezhou;
        if (mode == "replace")
        {
            if (string.IsNullOrEmpty(docField))
                store.Items.Clear();
            else
                store.Items.RemoveAll(i => i.Doc == docField);
        }

        int added = 0, updated = 0, skipped = 0;
        foreach (var r in rows)
        {
            var doc = string.IsNullOrWhiteSpace(r.Doc) ? (docField ?? "") : r.Doc.Trim();
            if (doc.Length == 0 || r.Name.Length == 0 || !Regex.IsMatch(r.Code, @"^\d{11,15}[A-Za-z]?$"))
            {
                skipped++;
                continue;
            }
            var flat = new Dictionary<string, string>
            {
                ["doc"] = doc,
                ["exec"] = r.Exec,
                ["category"] = r.Category,
                ["seq"] = r.Seq,
                ["code"] = r.Code,
                ["kind"] = string.IsNullOrWhiteSpace(r.Kind) ? "main" : r.Kind,
                ["name"] = r.Name,
                ["serviceOutput"] = r.ServiceOutput,
                ["priceComposition"] = r.PriceComposition,
                ["unit"] = r.Unit,
                ["price3"] = r.Price3,
                ["price2"] = r.Price2,
                ["price1"] = r.Price1,
                ["deduct"] = r.Deduct,
                ["insurance"] = r.Insurance,
                ["selfPay"] = r.SelfPay,
                ["note"] = r.Note,
                ["usage"] = r.Usage,
            };

            var exist = store.Items.FirstOrDefault(i => i.Doc == doc && i.Code == r.Code);
            if (exist == null)
            {
                var a = store.AddRow(flat);
                if (!a.Ok) { skipped++; continue; }
                added++;
            }
            else
            {
                var u = store.UpdateRow(exist.Id.ToString(), flat);
                if (!u.Ok) { skipped++; continue; }
                updated++;
            }
        }

        store.SaveWithRecompute();
        AuditLog.Write(AuditAction.Import, "德州物价",
            $"物价导入 {fileName}（{(mode == "replace" ? "替换" : "追加更新")}"
            + (string.IsNullOrEmpty(docField) ? "" : " · " + docField) + $"）："
            + $"解析 {rows.Count} · 新增 {added} · 更新 {updated} · 跳过 {skipped} · 共 {store.Items.Count}");
        return new DezhouImportResult
        {
            Parsed = rows.Count,
            Added = added,
            Updated = updated,
            Skipped = skipped,
            Total = store.Items.Count,
        };
    }

    // ==================================================================
    // 解析 / 公共工具（Web 与桌面共用）
    // ==================================================================

    /// <summary>按表头名匹配映射表列（取值集合定义在 <see cref="GrouperService.MappingFieldMap"/>）。</summary>
    public static Dictionary<string, int> MatchMappingHeader(List<string> headerArr)
    {
        var cols = new Dictionary<string, int>();
        foreach (var (key, names) in GrouperService.MappingFieldMap)
        {
            for (var i = 0; i < headerArr.Count; i++)
            {
                if (names.Any(n => string.Equals(n, (headerArr[i] ?? "").Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    cols[key] = i;
                    break;
                }
            }
        }
        return cols;
    }

    /// <summary>
    /// 映射表 Excel 解析：前 5 行内找表头，灰度判定优先级
    /// （填充色 &gt; 「灰码」列 &gt; 医保编码为空）。
    /// </summary>
    public static List<MappingImportRow> ReadMappingRows(byte[] buffer, string originalName)
    {
        var isXlsx = Regex.IsMatch(originalName ?? "", @"\.xlsx$", RegexOptions.IgnoreCase);
        var wb = ExcelUtil.Open(buffer);
        var sheet = wb.GetSheetAt(0);
        var aoa = ExcelUtil.ReadAoa(sheet);
        var sheetName = wb.GetSheetName(0);

        var hIdx = -1;
        Dictionary<string, int> cols = null;
        for (var i = 0; i < Math.Min(aoa.Count, 5); i++)
        {
            var c = MatchMappingHeader(aoa[i] ?? new List<string>());
            if (c.ContainsKey("code")) { hIdx = i; cols = c; break; }
        }
        if (cols == null)
            throw new InvalidOperationException(
                $"未识别到编码列, 请确保表头包含以下之一: {string.Join(" / ", GrouperService.MappingFieldMap[0].Names)}");

        HashSet<int> grayRows = null;
        var rowOffset = sheet.FirstRowNum;
        if (isXlsx)
        {
            var letters = new List<string> { XlsxGray.ColLetter(cols["code"]) };
            if (cols.ContainsKey("insCode")) letters.Add(XlsxGray.ColLetter(cols["insCode"]));
            try { grayRows = XlsxGray.ParseGrayRows(buffer, letters, sheetName); }
            catch { grayRows = null; }
        }

        var rows = new List<MappingImportRow>();
        for (var i = hIdx + 1; i < aoa.Count; i++)
        {
            var arr = aoa[i] ?? new List<string>();
            var code = Cell(arr, cols["code"]).Trim();
            if (code.Length == 0) continue;
            var insCode = cols.ContainsKey("insCode") ? Cell(arr, cols["insCode"]).Trim() : "";
            var grayField = cols.ContainsKey("grayField") ? Cell(arr, cols["grayField"]).Trim() : "";

            bool gray;
            if (grayRows != null && grayRows.Contains(i + 1 + rowOffset)) gray = true;
            else if (grayField.Length > 0) gray = GrouperService.GrayTrueValues.Contains(grayField.ToLowerInvariant());
            else if (grayRows != null) gray = false;
            else gray = insCode.Length == 0;

            rows.Add(new MappingImportRow
            {
                Code = code,
                Name = cols.ContainsKey("name") ? Cell(arr, cols["name"]).Trim() : "",
                InsCode = insCode,
                InsName = cols.ContainsKey("insName") ? Cell(arr, cols["insName"]).Trim() : "",
                Gray = gray,
            });
        }
        if (rows.Count == 0) throw new InvalidOperationException("未解析到有效数据行");
        return rows;
    }

    /// <summary>综合病组 Excel 解析（前 5 行内找「病组编码 + 诊断范围」表头）。</summary>
    public static List<ZongheImportRow> ReadZongheRows(byte[] buffer)
    {
        var wb = ExcelUtil.Open(buffer);
        var aoa = ExcelUtil.ReadAoa(wb.GetSheetAt(0));
        if (aoa.Count == 0) throw new InvalidOperationException("工作表为空");

        Dictionary<string, int> cols = null;
        for (var i = 0; i < Math.Min(aoa.Count, 5); i++)
        {
            var c = new Dictionary<string, int>();
            var row = aoa[i] ?? new List<string>();
            foreach (var (key, names) in GrouperService.ZongheFieldMap)
            {
                for (var j = 0; j < row.Count; j++)
                {
                    if (names.Any(n => string.Equals(n, (row[j] ?? "").Trim(), StringComparison.OrdinalIgnoreCase)))
                    {
                        c[key] = j;
                        break;
                    }
                }
            }
            if (c.ContainsKey("code") && c.ContainsKey("ranges")) { cols = c; break; }
        }
        if (cols == null)
            throw new InvalidOperationException("未识别到表头, 需包含: 病组编码 / 诊断范围（如 A15-A16）, 可下载模板填写");

        var rows = new List<ZongheImportRow>();
        for (var i = 1; i < aoa.Count; i++)
        {
            var arr = aoa[i] ?? new List<string>();
            var code = Cell(arr, cols["code"]).Trim();
            if (code.Length == 0) continue;
            rows.Add(new ZongheImportRow
            {
                Code = code,
                Name = cols.ContainsKey("name") ? Cell(arr, cols["name"]).Trim() : "",
                Ranges = cols.ContainsKey("ranges") ? Cell(arr, cols["ranges"]).Trim() : "",
                Note = cols.ContainsKey("note") ? Cell(arr, cols["note"]).Trim() : "",
            });
        }
        if (rows.Count == 0) throw new InvalidOperationException("未解析到有效数据行");
        return rows;
    }

    private static string Cell(List<string> arr, int idx) => idx >= 0 && idx < arr.Count ? (arr[idx] ?? "") : "";

    /// <summary>整体替换某区段的行集合（导入 replace 模式用）。</summary>
    public static void SetSectionRows(Dip3Config config, string tab, List<object> rows)
    {
        switch (tab)
        {
            case "xianqi": config.Xianqi = rows.Cast<XianqiRow>().ToList(); break;
            case "bingxiang": config.Bingxiang = rows.Cast<BingxiangRow>().ToList(); break;
            case "fz_shaoshang": config.FzShaoshang = rows.Cast<ShaoshangRow>().ToList(); break;
            case "fz_zhongliu": config.FzZhongliu = rows.Cast<ZhongliuRow>().ToList(); break;
            case "fz_jiehe": config.FzJiehe = rows.Cast<JieheRow>().ToList(); break;
            case "jiehe_resist_codes": config.JieheResistCodes = rows.Cast<ResistCode>().ToList(); break;
            case "jichu": config.Jichu = rows.Cast<DiagRow>().ToList(); break;
            case "jiceng": config.Jiceng = rows.Cast<DiagRow>().ToList(); break;
            case "outlib": config.OutLib = rows.Cast<OutLibRow>().ToList(); break;
            case "excludedDiags": config.ExcludedDiags = rows.Cast<CodeName>().ToList(); break;
            case "excludedProcs": config.ExcludedProcs = rows.Cast<ExcludedProc>().ToList(); break;
        }
    }

    public static Dictionary<string, SectionCount> SectionCountsDict(Dip3Config config)
    {
        var d = new Dictionary<string, SectionCount>();
        foreach (var sec in Diseases.Sections)
            d[sec.Tab] = new SectionCount { Label = sec.Label, Total = sec.GetRows(config).Count };
        return d;
    }

    /// <summary>sheet 名 / 区段 key → 区段 tab（去空格、不区分大小写）。</summary>
    public static string SheetTabOf(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        static string Norm(string s) => (s ?? "").Trim().ToLowerInvariant().Replace(" ", "");
        foreach (var sec in Diseases.Sections)
            if (Norm(sec.Tab) == Norm(name) || Norm(sec.Label) == Norm(name)) return sec.Tab;
        return null;
    }

    /// <summary>各区段模板样例行（key = 区段 tab）。</summary>
    public static Dictionary<string, List<Dictionary<string, string>>> TemplateSamples() => new()
    {
        ["xianqi"] = new List<Dictionary<string, string>>
        {
            new() { ["seq"] = "1", ["name"] = "合并重症天使综合征", ["diag"] = "所有诊断", ["procs"] = "54.2100x001", ["ageMax"] = "", ["bw"] = "", ["note"] = "手术特征行" },
            new() { ["seq"] = "2", ["name"] = "极低出生体重儿", ["diag"] = "P07", ["procs"] = "", ["ageMax"] = "28", ["bw"] = "1000-1499", ["note"] = "新生儿体重档行" },
        },
        ["bingxiang"] = new List<Dictionary<string, string>>
        {
            new() { ["seq"] = "1", ["diag"] = "K80.0", ["diagName"] = "胆囊结石伴急性胆囊炎", ["procs"] = "51.0301", ["procName"] = "腹腔镜胆囊切除术", ["rel"] = "51.2201|51.2202", ["relNames"] = "相关手术甲|相关手术乙" },
        },
        ["fz_shaoshang"] = new List<Dictionary<string, string>>
        {
            new() { ["seq"] = "1", ["diag"] = "T20.2", ["degree"] = "二度", ["otherDiag"] = "T31.0", ["area"] = "面积<10%", ["procs"] = "86.2200x011", ["procName"] = "清创术", ["rel"] = "", ["relNames"] = "" },
        },
        ["fz_zhongliu"] = new List<Dictionary<string, string>>
        {
            new() { ["seq"] = "1", ["diag"] = "Z51.1x", ["diagName"] = "恶性肿瘤化学治疗疗程", ["otherDiag"] = "C88", ["procs"] = "41.0401+41.0402", ["procName"] = "造血干细胞移植" },
        },
        ["fz_jiehe"] = new List<Dictionary<string, string>>
        {
            new() { ["seq"] = "1", ["diag"] = "A15.0", ["diagName"] = "肺结核", ["procs"] = "", ["procName"] = "", ["resistant"] = "否", ["resistNote"] = "" },
        },
        ["jiehe_resist_codes"] = new List<Dictionary<string, string>>
        {
            new() { ["code"] = "A15.000x011", ["name"] = "耐药肺结核" },
        },
        ["jichu"] = new List<Dictionary<string, string>>
        {
            new() { ["seq"] = "1", ["diag"] = "T82.8", ["diagName"] = "假体装置并发症", ["procs"] = "", ["procName"] = "" },
        },
        ["jiceng"] = new List<Dictionary<string, string>>
        {
            new() { ["seq"] = "1", ["diag"] = "I10.x00", ["diagName"] = "原发性高血压", ["procs"] = "", ["procName"] = "" },
        },
        ["outlib"] = new List<Dictionary<string, string>>
        {
            new() { ["seq"] = "1", ["diagCode"] = "H65", ["diagName"] = "非化脓性中耳炎", ["procCode"] = "22.02", ["procName"] = "经自然孔的鼻窦抽吸或灌洗", ["suggest"] = "H65 ＋ 20.0100x005 鼓室置管术", ["note"] = "2027 年起不作核心病种" },
        },
        ["excludedDiags"] = new List<Dictionary<string, string>>
        {
            new() { ["code"] = "T29.100", ["name"] = "示例: 不纳入分组的诊断" },
        },
        ["excludedProcs"] = new List<Dictionary<string, string>>
        {
            new() { ["code"] = "86.2200x011", ["name"] = "示例: 不纳入分组的手术", ["note"] = "" },
        },
    };
}
