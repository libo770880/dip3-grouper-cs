using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dip3Grouper;

namespace Dip3Grouper.Cli;

/// <summary>
/// DIP 3.0 分组器命令行（C# 版）—— 对应 Node 版 bin/cli.js、bin/batch.js、tools/build-config.js、tools/patch-config.js、tools/doc2txt.py
///
/// 用法:
///   Dip3Grouper.Cli group '{"主要诊断编码":"Z51.100x002","其他诊断编码":"C34.900","主要手术操作编码":"99.2503"}'
///   Dip3Grouper.Cli group --file case.json [--jiceng]
///   Dip3Grouper.Cli batch --input cases.xlsx [--output results.xlsx] [--jiceng]
///   Dip3Grouper.Cli build-config <xlsx路径> [输出json路径]
///   Dip3Grouper.Cli patch-config <xlsx路径>
///   Dip3Grouper.Cli doc2txt <doc|docx|txt路径> [输出txt路径]
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions OutOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static int Main(string[] argv)
    {
        ConsoleSetup.UseUtf8();

        var opts = new Dictionary<string, string>();
        var flags = new HashSet<string>();
        var pos = new List<string>();
        for (var i = 0; i < argv.Length; i++)
        {
            var a = argv[i];
            string Next() => i + 1 < argv.Length ? argv[++i] : "";
            switch (a)
            {
                case "--jiceng": flags.Add("jiceng"); break;
                case "--dry": flags.Add("dry"); break;
                case "--file": case "-f": case "--input": case "-i": opts["input"] = Next(); break;
                case "--output": case "-o": opts["output"] = Next(); break;
                case "--config": case "-c": opts["config"] = Next(); break;
                default: pos.Add(a); break;
            }
        }

        var cmd = pos.Count > 0 ? pos[0] : "help";
        try
        {
            return cmd switch
            {
                "group" => CmdGroup(opts, flags, pos),
                "batch" => CmdBatch(opts, flags),
                "merge-draft" => CmdMergeDraft(opts, flags),
                "build-config" => CmdBuildConfig(pos, opts),
                "patch-config" => CmdPatchConfig(pos),
                "import-outlib" => CmdImportOutLib(pos, flags),
                "doc2txt" => CmdDoc2Txt(pos, opts),
                _ => PrintHelp(cmd),
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"执行失败: {e.Message}");
            return 1;
        }
    }

    // ------------------------------------------------------------------
    // 分组器构造（自动挂载 data/zonghe.json 综合病组层，对齐 Node createGrouper）
    // ------------------------------------------------------------------

    private static Dip3Grouper MakeGrouper(Dictionary<string, string> opts, HashSet<string> flags)
    {
        var path = opts.TryGetValue("config", out var p) && p.Length > 0 ? p : AppPaths.ConfigFile;
        var cfg = Js.ReadJsonFile<Dip3Config>(path) ?? throw new InvalidOperationException($"配置读取失败: {path}");
        var g = new Dip3Grouper(cfg, flags.Contains("jiceng"));
        try
        {
            var zh = Js.ReadJsonFile<ZongheFile>(AppPaths.ZongheFile);
            g.SetZonghe(zh?.Rows ?? new List<ZongheRow>());
        }
        catch { /* 无综合病组文件时该层不生效 */ }
        return g;
    }

    // ------------------------------------------------------------------
    // merge-draft: 「新项目征求意见」稿转正并入德州物价主库（先备份；动作幂等，重跑无副作用）
    // ------------------------------------------------------------------

    private static int CmdMergeDraft(Dictionary<string, string> opts, HashSet<string> flags)
    {
        var dry = flags.Contains("dry");
        // 征求意见稿（24 号暂定）已于 2026-10 定稿为德医保发〔2026〕25号 正式发文版，
        // 若再走转正流程，默认落到正式文号上，避免又造出一个「暂定」批次。
        var doc = opts.TryGetValue("doc", out var d) && d.Trim().Length > 0
            ? d.Trim() : "德医保发〔2026〕25号";
        var exec = opts.TryGetValue("exec", out var e) ? e.Trim() : "";

        var main = new DezhouPriceStore();
        var draft = new DezhouDraftStore();
        if (draft.Items.Count == 0)
        {
            Console.Error.WriteLine("征求意见稿为空，无可转正数据。");
            return 1;
        }
        if (draft.Meta.Status == "active")
            Console.WriteLine("提示：征求意见稿已是 active（可能已转正过），本次按幂等重跑处理。");

        // 预演：与 MergeInto 相同的去重口径，先报数不写盘
        var existCodes = new HashSet<string>(main.Items.Select(i => (i.Code ?? "").Trim()), StringComparer.Ordinal);
        var newItems = draft.Items
            .Where(i => (i.Code ?? "").Trim().Length > 0 && existCodes.Add((i.Code ?? "").Trim()))
            .ToList();
        var knownPairs = new HashSet<string>(
            main.Cancels.Select(c => (c.OldCode ?? "").Trim() + "\u0001" + (c.NewCode ?? "").Trim()),
            StringComparer.Ordinal);
        var newCancels = draft.Cancels
            .Where(c => (c.OldCode ?? "").Trim().Length > 0
                        && knownPairs.Add((c.OldCode ?? "").Trim() + "\u0001" + (c.NewCode ?? "").Trim()))
            .ToList();

        Console.WriteLine($"征求意见稿：拟新增 {draft.Items.Count}（与主库同码 {draft.Items.Count - newItems.Count}，不覆盖）");
        Console.WriteLine($"           拟作废 {draft.Cancels.Count}（与主库重复 {draft.Cancels.Count - newCancels.Count}，去重后追加）");
        Console.WriteLine($"           映射关系 {draft.Maps.Count} 行");
        Console.WriteLine($"文号 = {doc}    执行日期 = {(exec.Length > 0 ? exec : "（空，待正式文件）")}");

        if (dry)
        {
            Console.WriteLine("[dry] 预演结束，未写入任何文件。确认执行请去掉 --dry。");
            return 0;
        }

        // 备份两个数据文件（主库 + 草稿）后再动手
        var bakDir = Path.Combine(AppPaths.DataDir, "_backup",
            "merge-draft-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(bakDir);
        foreach (var f in new[] { AppPaths.DezhouFile, AppPaths.DezhouDraftFile })
            if (File.Exists(f)) File.Copy(f, Path.Combine(bakDir, Path.GetFileName(f)), true);
        Console.WriteLine("已备份 → " + bakDir);

        draft.Meta.Doc = doc;
        if (exec.Length > 0) draft.Meta.Exec = exec;
        var (items, cancels, maps) = draft.MergeInto(main);

        Console.WriteLine($"合并完成：新增项目 {items}、作废对照追加 {cancels}、映射 {maps}");
        Console.WriteLine($"主库现状：items {main.Items.Count}、cancels {main.Cancels.Count}、批次 {main.Meta.Batches.Count}");

        // 合并后校验：新批次 doc 计数 / 作废 Pending / 草稿状态
        // 该批次行数恒等于草稿 items 数（首次=新增数；幂等重跑=已存在数），两种路径同一条校验
        var byDoc = main.Items.Count(i => (i.Doc ?? "").Trim() == doc);
        var expected = draft.Items.Count(i => (i.Code ?? "").Trim().Length > 0);
        var batch = main.Meta.Batches.FirstOrDefault(b => (b.Doc ?? "").Trim() == doc);
        Console.WriteLine($"校验：doc=「{doc}」项目 {byDoc} 条（批次 rows={batch?.Rows ?? -1}）；" +
                          $"草稿状态={draft.Meta.Status}");
        if (byDoc != expected)
        {
            Console.Error.WriteLine($"失败：doc 计数 {byDoc} != 预期 {expected}，请核对备份 {bakDir}");
            return 1;
        }
        if (batch == null || batch.Rows != draft.Meta.Batches.Sum(b => b.Rows))
        {
            Console.Error.WriteLine("失败：主库批次汇总 rows 与草稿不一致，请核对备份。");
            return 1;
        }
        Console.WriteLine("转正完成。正式文号下发后：全局替换「" + doc + "」并补 exec 执行日期即可。");
        if (exec.Length == 0)
            Console.WriteLine("提示：本次未给 --exec，批次与行内执行日期为空，拿到执行日期后需再补（不必重导）。");
        return 0;
    }

    // ------------------------------------------------------------------
    // group: 单条分组（对齐 bin/cli.js）
    // ------------------------------------------------------------------

    private static int CmdGroup(Dictionary<string, string> opts, HashSet<string> flags, List<string> pos)
    {
        string raw;
        if (opts.TryGetValue("input", out var f) && f.Length > 0) raw = System.IO.File.ReadAllText(f, Encoding.UTF8);
        else if (pos.Count > 1) raw = pos[1];
        else { Console.Error.WriteLine("缺少病案数据: 传 JSON 字符串或 --file <路径>"); return 1; }

        Dictionary<string, JsonElement> kase;
        try
        {
            kase = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(raw) ?? new Dictionary<string, JsonElement>();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("病案 JSON 解析失败: " + e.Message);
            return 1;
        }

        var result = MakeGrouper(opts, flags).Group(kase);
        Console.WriteLine(JsonSerializer.Serialize(result, OutOpts));
        return result.Matched ? 0 : 2;
    }

    // ------------------------------------------------------------------
    // batch: 批量分组（对齐 bin/batch.js）
    // ------------------------------------------------------------------

    private static readonly (string Key, string[] Names)[] FieldMap =
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

    private static int CmdBatch(Dictionary<string, string> opts, HashSet<string> flags)
    {
        if (!opts.TryGetValue("input", out var input) || input.Length == 0)
        {
            Console.Error.WriteLine("用法: Dip3Grouper.Cli batch --input <病案.xlsx> --output <结果.xlsx> [--jiceng] [--config <配置>]");
            return 1;
        }
        var output = opts.TryGetValue("output", out var o) && o.Length > 0
            ? o
            : System.Text.RegularExpressions.Regex.Replace(input, @"\.(xlsx|xls|csv)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase) + "-分组结果.xlsx";

        var cases = ReadCaseFile(input);
        if (cases.Count == 0) { Console.Error.WriteLine("输入文件中没有数据行"); return 1; }

        var grouper = MakeGrouper(opts, flags);
        var aoa = new List<List<object>>
        {
            new() { "序号", "病案号", "主要诊断编码", "主要手术操作编码", "入组", "组别", "组别代码", "病种序号", "分组名称", "未入组原因", "匹配明细", "目录库外病种" },
        };
        var byType = new Dictionary<string, int>();
        var matchedCount = 0;
        for (var i = 0; i < cases.Count; i++)
        {
            var kase = Dip3Grouper.ReadCase(cases[i]);
            var r = grouper.Group(kase);
            if (r.Matched)
            {
                matchedCount++;
                var t = r.GroupType ?? "";
                byType[t] = byType.TryGetValue(t, out var c) ? c + 1 : 1;
            }
            aoa.Add(new List<object>
            {
                i + 1,
                kase.CaseId ?? "",
                kase.MainDiag ?? "",
                kase.MainProc ?? "",
                r.Matched ? "是" : "否",
                r.GroupType ?? "",
                r.GroupTypeCode ?? "",
                r.Seq.HasValue ? r.Seq.Value : "",
                r.Name ?? "",
                r.Reason ?? "",
                JsonSerializer.Serialize(r.Detail ?? new Dictionary<string, object>(), Js.Data),
                r.OutLib == null ? "" : $"{r.OutLib.Label}（2027 起不作核心病种）",
            });
        }

        var bytes = ExcelUtil.BuildWorkbook(new[]
        {
            new ExcelUtil.SheetSpec { Name = "分组结果", Aoa = aoa, ColWidths = new[] { 6, 12, 16, 20, 6, 7, 10, 10, 26, 30, 60, 34 } },
        });
        System.IO.File.WriteAllBytes(output, bytes);

        Console.WriteLine($"分组完成: 共 {aoa.Count - 1} 条, 入组 {matchedCount} 条, 未入组 {aoa.Count - 1 - matchedCount} 条");
        foreach (var kv in byType) Console.WriteLine($"  {kv.Key}: {kv.Value}");
        Console.WriteLine($"结果文件: {output}");
        return 0;
    }

    /// <summary>读取病案表（xlsx/xls 用 NPOI；csv 用内置解析）。列名自动映射，中英文均可。</summary>
    private static List<Dictionary<string, JsonElement>> ReadCaseFile(string path)
    {
        var outRows = new List<Dictionary<string, JsonElement>>();
        if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            var lines = System.IO.File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length == 0) return outRows;
            var headers = SplitCsv(lines[0]);
            for (var li = 1; li < lines.Length; li++)
            {
                if (lines[li].Trim().Length == 0) continue;
                var cells = SplitCsv(lines[li]);
                var row = new Dictionary<string, string>();
                for (var ci = 0; ci < headers.Count && ci < cells.Count; ci++)
                    if (!string.IsNullOrWhiteSpace(cells[ci])) row[headers[ci].Trim()] = cells[ci];
                var mapped = MapRow(row);
                if (mapped.Count > 0) outRows.Add(mapped);
            }
            return outRows;
        }

        var wb = ExcelUtil.Open(System.IO.File.ReadAllBytes(path));
        foreach (var o in ExcelUtil.ReadObjects(wb.GetSheetAt(0)))
        {
            var mapped = MapRow(o);
            if (mapped.Count > 0) outRows.Add(mapped);
        }
        return outRows;
    }

    private static Dictionary<string, JsonElement> MapRow(IDictionary<string, string> row)
    {
        var src = new Dictionary<string, string>();
        foreach (var (key, names) in FieldMap)
        {
            foreach (var n in names)
            {
                if (row.TryGetValue(n, out var v) && !string.IsNullOrEmpty(v)) { src[key] = v; break; }
            }
        }
        var json = JsonSerializer.Serialize(src);
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? new Dictionary<string, JsonElement>();
    }

    private static List<string> SplitCsv(string line)
    {
        var outList = new List<string>();
        var sb = new StringBuilder();
        var inQuote = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuote)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuote = false;
                }
                else sb.Append(ch);
            }
            else if (ch == '"') inQuote = true;
            else if (ch == ',') { outList.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        outList.Add(sb.ToString());
        return outList;
    }

    // ------------------------------------------------------------------
    // build-config / patch-config / doc2txt
    // ------------------------------------------------------------------

    private static int CmdBuildConfig(List<string> pos, Dictionary<string, string> opts)
    {
        if (pos.Count < 2) { Console.WriteLine("用法: Dip3Grouper.Cli build-config <xlsx路径> [输出json路径]"); return 1; }
        var input = pos[1];
        var output = pos.Count > 2 ? pos[2] : AppPaths.ConfigFile;
        var cfg = BuildConfig.Build(System.IO.File.ReadAllBytes(input), input);
        var json = JsonSerializer.Serialize(cfg, Js.Config);
        System.IO.File.WriteAllText(output, json, new UTF8Encoding(false));
        Console.WriteLine($"转换完成: {output}");
        Console.WriteLine(JsonSerializer.Serialize(cfg.Meta?.Counts ?? new Dictionary<string, int>(), OutOpts));
        return 0;
    }

    private static int CmdPatchConfig(List<string> pos)
    {
        if (pos.Count < 2) { Console.WriteLine("用法: Dip3Grouper.Cli patch-config <xlsx路径>"); return 1; }
        var input = pos[1];
        var cfg = Js.ReadJsonFile<Dip3Config>(AppPaths.ConfigFile) ?? throw new InvalidOperationException("现有配置读取失败");
        var fresh = BuildConfig.Build(System.IO.File.ReadAllBytes(input), input);

        // 1. 烧伤 relNames（按 seq 对齐合并，保留后台手工编辑）
        var patched = 0;
        foreach (var row in cfg.FzShaoshang)
        {
            var src = fresh.FzShaoshang.FirstOrDefault(r => r.Seq == row.Seq);
            if (src != null && src.RelNames is { Count: > 0 } && (row.RelNames == null || row.RelNames.Count == 0))
            {
                row.RelNames = src.RelNames;
                patched++;
            }
        }

        // 2. 结核耐药附表 [{code,name}]
        cfg.JieheResistCodes = fresh.JieheResistCodes ?? new List<ResistCode>();

        Js.WriteJsonFile(AppPaths.ConfigFile, cfg, Js.Config, 0);
        Console.WriteLine($"补齐完成: 烧伤 relNames {patched} 行, 结核耐药附表 {cfg.JieheResistCodes.Count} 条");
        return 0;
    }

    // ------------------------------------------------------------------
    // import-outlib: 省局反馈「目录库外病种清单」→ 病种库 outlib 区段
    // ------------------------------------------------------------------

    /// <summary>
    /// 把省局反馈的《目录库外部分病种清单》导入 <c>config/dip3-config.json</c> 的 <c>outlib</c> 区段。
    ///
    /// <para>原表结构：病种（诊断码-手术码复合键）/ 主要诊断编码 / 主要诊断名称 / 手术操作编码 /
    /// 手术操作名称。**不使用复合键列**：它只是「诊断码-手术码」的拼接，拆列后自己拼更可靠，
    /// 且原表存在「P58.9-」这种尾随横线的写法。</para>
    ///
    /// <para>每条自动算「同诊断在目录库内的可入组组合」落盘成 <c>suggest</c>：
    /// 病案室要的不是「这条不能用」，而是「同诊断该编成什么才能入组」。</para>
    /// </summary>
    private static int CmdImportOutLib(List<string> pos, HashSet<string> flags)
    {
        if (pos.Count < 2)
        {
            Console.WriteLine("用法: Dip3Grouper.Cli import-outlib <省局清单xlsx> [--dry]");
            return 1;
        }
        var input = pos[1];
        var cfg = Js.ReadJsonFile<Dip3Config>(AppPaths.ConfigFile)
                  ?? throw new InvalidOperationException("现有配置读取失败");

        var wb = ExcelUtil.Open(System.IO.File.ReadAllBytes(input));
        var sheet = wb.GetSheetAt(0);
        var rows = ExcelUtil.ReadObjects(sheet);

        string Pick(Dictionary<string, string> o, params string[] names)
        {
            foreach (var n in names)
            {
                if (o.TryGetValue(n, out var v) && (v ?? "").Trim().Length > 0) return v.Trim();
            }
            return "";
        }

        var list = new List<OutLibRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var dup = 0;
        foreach (var o in rows)
        {
            var diag = Pick(o, "主要诊断编码", "诊断编码", "mainDiag");
            if (diag.Length == 0) continue;
            var proc = Pick(o, "手术操作编码", "主要手术操作编码", "手术编码", "mainProc");
            if (!seen.Add(OutLibRules.KeyOf(diag, proc))) { dup++; continue; }
            var suggest = OutLibRules.Suggest(cfg, diag, proc);
            list.Add(new OutLibRow
            {
                Seq = list.Count + 1,
                DiagCode = diag,
                DiagName = Pick(o, "主要诊断名称", "诊断名称"),
                ProcCode = proc,
                ProcName = Pick(o, "手术操作名称", "主要手术操作名称", "手术名称"),
                Suggest = suggest,
                Note = OutLibRules.NoteFor(suggest),
            });
        }

        var withProc = list.Count(r => !string.IsNullOrEmpty(r.ProcCode));
        var withSuggest = list.Count(r => !string.IsNullOrEmpty(r.Suggest));
        var diagCount = list.Select(r => (r.DiagCode ?? "").ToUpperInvariant()).Distinct().Count();
        Console.WriteLine($"读取 {rows.Count} 行 → 有效 {list.Count} 条（重复跳过 {dup}）");
        Console.WriteLine($"其中 含手术 {withProc} 条 · 仅诊断 {list.Count - withProc} 条 · 涉及诊断 {diagCount} 个");
        Console.WriteLine($"算得「目录库内可入组组合」{withSuggest} 条，无同诊断病种 {list.Count - withSuggest} 条");

        if (flags.Contains("dry"))
        {
            Console.WriteLine("--dry：未写盘。抽样：");
            foreach (var r in list.Take(5)) Console.WriteLine("  " + OutLibRules.Describe(r) + "  ⇒  " + r.Suggest);
            return 0;
        }

        cfg.OutLib = list;
        cfg.Meta ??= new ConfigMeta();
        cfg.Meta.Counts ??= new Dictionary<string, int>();
        cfg.Meta.Counts["outlib"] = list.Count;
        Diseases.SaveConfig(cfg);
        Console.WriteLine($"已写入 {AppPaths.ConfigFile} 的 outlib 区段（{list.Count} 条）");
        return 0;
    }

    private static int CmdDoc2Txt(List<string> pos, Dictionary<string, string> opts)
    {
        if (pos.Count < 2) { Console.WriteLine("用法: Dip3Grouper.Cli doc2txt <doc|docx|txt路径> [输出txt路径]"); return 1; }
        var input = pos[1];
        var ext = Path.GetExtension(input).ToLowerInvariant();
        var text = DocToText.Extract(System.IO.File.ReadAllBytes(input), ext);
        if (pos.Count > 2) { System.IO.File.WriteAllText(pos[2], text, new UTF8Encoding(false)); Console.WriteLine($"已写出: {pos[2]} ({text.Length} 字)"); }
        else Console.WriteLine(text);
        return 0;
    }

    private static int PrintHelp(string cmd)
    {
        if (cmd != "help" && cmd.Length > 0) Console.Error.WriteLine($"未知命令: {cmd}");
        Console.WriteLine(@"DIP 3.0 分组器（C# 版）

用法:
  Dip3Grouper.Cli group '<JSON病案>'          单条分组（未入组退出码 2）
  Dip3Grouper.Cli group --file case.json      从文件读病案
  Dip3Grouper.Cli batch --input <xlsx> [--output <xlsx>]
                                              批量分组，结果写 Excel
  Dip3Grouper.Cli build-config <xlsx> [json]  由国家分组配置 Excel 生成 config/dip3-config.json
  Dip3Grouper.Cli patch-config <xlsx>         原位补齐现有配置缺失字段（保留后台手工编辑）
  Dip3Grouper.Cli import-outlib <xlsx> [--dry]
                                              导入省局反馈「目录库外病种清单」到病种库 outlib 区段
                                              （自动算同诊断的目录库内可入组组合）
  Dip3Grouper.Cli merge-draft [--dry]         「新项目征求意见稿」库转正并入德州物价主库
                                              [--doc 文号]（默认德医保发〔2026〕25号，即正式发文文号）
                                              [--exec 执行日期]（25 号已定 2026-11-01）；自动备份到 data/_backup/
  Dip3Grouper.Cli doc2txt <doc|docx|txt>      文档转纯文本（.doc 走内置 piece table 解析）

选项:
  --jiceng              核心病种未入组时尝试基层病种
  --config <路径>       指定配置 JSON（默认 config/dip3-config.json）
  --file/-f, --input/-i 输入文件
  --output/-o           输出文件

病案字段（中英文均可）:
  主要诊断编码 mainDiag / 其他诊断编码 otherDiags（| 分隔可多个）
  主要手术操作编码 mainProc / 其他手术操作编码 otherProcs
  年龄 age + 年龄单位 ageUnit（岁/月/天/小时）或 年龄天数 ageDays
  出生体重 birthWeight（克）

示例:
  Dip3Grouper.Cli group ""{\""主要诊断编码\"":\""A09.000\"",\""主要手术操作编码\"":\""44.1300x001\""}""
  Dip3Grouper.Cli group ""{\""主要诊断编码\"":\""P07.100\"",\""年龄天数\"":5,\""出生体重\"":1200}""
");
        return cmd == "help" ? 0 : 1;
    }
}
