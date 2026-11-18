using NPOI.SS.UserModel;

namespace Dip3Grouper;

/// <summary>
/// 德州市物价「新项目征求意见」稿 xlsx 解析器。
///
/// <para><b>源文件形态</b>：一份通知（征求意见稿）的附件工作簿，按「类别 × 三张表」组织，
/// 共 3N 个工作表（本批 9 类 → 27 张）：</para>
/// <list type="bullet">
///   <item><c>附件N&lt;类别&gt;</c> —— 拟新增项目表（序号 / 项目编码 / 项目名称 / 服务产出 /
///         价格构成 / 计价单位 / 三级价格 / 二级价格 / 一级价格 / [扣减基本物耗价格] / 计价说明）</item>
///   <item><c>附件N&lt;类别&gt;废止</c> —— 拟作废原项目表（序号 / 项目编码 / 项目名称 / 项目内涵 /
///         除外内容 / 计价单位 / 三级价格（元）/ 二级价格（元）/ 一级价格（元）/ 说明 / [备注]）</item>
///   <item><c>附件N&lt;类别&gt;映射</c> —— 映射关系表（两段表头；左 = 新立项指南项目，
///         右 = 映射原价格项目，即作废侧）</item>
/// </list>
///
/// <para><b>Sheet 归类规则</b>：按名后缀区分——以「废止」结尾 = 废止表，以「映射」结尾 = 映射表，
/// 其余 = 新增项目表。类别名从 sheet 名去掉 <c>附件N</c> 前缀与后缀得到（如
/// 「附件10消化废止」→ 类别「消化」）。因此不依赖附件编号的连续性，缺哪一类都不影响其余。</para>
///
/// <para><b>表头定位</b>：第 1 行是「附件N」角标、第 2 行是表名、第 3 行（映射表为第 3~4 行）
/// 才是列头。为避免个别文件行数错位，这里统一「向下扫描找到含 `序号` 的行」作为表头行，
/// 数据自表头行下一行开始——比写死行号稳。</para>
/// </summary>
public static class DezhouDraftParser
{
    /// <summary>一张已识别的表。</summary>
    private sealed class SheetInfo
    {
        public string Name;
        public Kind Kind;
        /// <summary>去掉附件前缀与「废止 / 映射」后缀后的类别名</summary>
        public string Category;
        /// <summary>附件编号（仅用于日志，允许缺失）</summary>
        public string Annex;
    }

    private enum Kind { New, Cancel, Map }

    /// <summary>
    /// 解析工作簿 → 征求意见稿数据结构。
    /// </summary>
    /// <param name="xlsxPath">通知附件 xlsx 路径</param>
    /// <param name="label">批次显示名（如「消化系统等 9 类医疗服务价格项目（征求意见稿）」）</param>
    /// <param name="doc">正式文号（征求意见阶段可传空）</param>
    /// <param name="note">页面顶部说明文字（可传空，使用默认文案）</param>
    public static DezhouDraftFile Parse(string xlsxPath, string label, string doc = "", string note = "")
    {
        if (!File.Exists(xlsxPath)) throw new FileNotFoundException($"找不到工作簿：{xlsxPath}", xlsxPath);
        var bytes = File.ReadAllBytes(xlsxPath);
        using var wb = ExcelUtil.Open(bytes);

        var sheets = new List<SheetInfo>();
        for (var i = 0; i < wb.NumberOfSheets; i++)
        {
            var name = wb.GetSheetName(i) ?? "";
            var info = Classify(name);
            if (info != null) sheets.Add(info);
        }
        if (sheets.Count == 0)
            throw new InvalidOperationException("未在工作簿中识别到任何「新增项目 / 废止 / 映射」表，请检查 sheet 命名");

        var items = new List<DezhouPriceItem>();
        var cancels = new List<DezhouCancelItem>();
        var maps = new List<DezhouDraftMapRow>();
        var batches = new List<DezhouBatch>();
        var warns = new List<string>();

        foreach (var group in sheets.GroupBy(s => s.Category, StringComparer.Ordinal)
                                    .OrderBy(g => g.Min(s => AnnexOrder(s.Annex))))        {
            var cat = group.Key;
            var newSheet = group.FirstOrDefault(s => s.Kind == Kind.New);
            var cancelSheet = group.FirstOrDefault(s => s.Kind == Kind.Cancel);
            var mapSheet = group.FirstOrDefault(s => s.Kind == Kind.Map);

            var catNew = newSheet == null ? new List<DezhouPriceItem>()
                : ParseNew(wb.GetSheet(newSheet.Name), cat, doc);
            var catCancel = cancelSheet == null ? new List<DezhouCancelItem>()
                : ParseCancel(wb.GetSheet(cancelSheet.Name), cat, doc);
            var catMap = mapSheet == null ? new List<DezhouDraftMapRow>()
                : ParseMap(wb.GetSheet(mapSheet.Name), cat);

            // 映射表是「新码 ↔ 旧码」的权威对照：用它给废止行补 NewCode / NewName。
            // 废止表本身只有旧项目侧，没有新项目列，必须靠映射表补全。
            var mapByOld = new Dictionary<string, DezhouDraftMapRow>(StringComparer.Ordinal);
            foreach (var m in catMap)
            {
                if (string.IsNullOrWhiteSpace(m.OldCode)) continue;
                if (!mapByOld.ContainsKey(m.OldCode)) mapByOld[m.OldCode] = m;
            }
            foreach (var c in catCancel)
            {
                if (string.IsNullOrWhiteSpace(c.OldCode)) continue;
                if (mapByOld.TryGetValue(c.OldCode, out var m))
                {
                    c.NewCode = m.NewCode;
                    c.NewName = m.NewName;
                }
            }

            items.AddRange(catNew);
            cancels.AddRange(catCancel);
            maps.AddRange(catMap);

            var unMatched = catCancel.Count(c => string.IsNullOrEmpty(c.NewCode));
            if (unMatched > 0) warns.Add($"{cat}：{unMatched} 条作废行在映射表中无对应新项目（直接废止）");

            batches.Add(new DezhouBatch
            {
                Doc = string.IsNullOrWhiteSpace(doc) ? label : doc,
                Exec = "",
                Prov = "",
                Scope = cat,
                Expect = catNew.Count(i => i.Kind == "main"),
                Rows = catNew.Count,
                Mains = catNew.Count(i => i.Kind == "main"),
                Adds = catNew.Count(i => i.Kind == "add"),
                Exts = catNew.Count(i => i.Kind == "ext"),
                Categories = new List<DezhouCategory>
                {
                    new DezhouCategory
                    {
                        Name = cat,
                        Items = catNew.Count,
                        Main = catNew.Count(i => i.Kind == "main"),
                        Add = catNew.Count(i => i.Kind == "add"),
                        Ext = catNew.Count(i => i.Kind == "ext"),
                    },
                },
            });
        }

        // 序号与 Id 统一
        var id = 1;
        foreach (var it in items) it.Id = id++;
        foreach (var it in items) it.Status = "draft";
        foreach (var c in cancels) c.Pending = true;
        foreach (var m in maps) m.Pending = true;

        var refs = cancels.Select(c => (c.Doc ?? "").Trim())
                          .Where(d => d.Length > 0)
                          .Distinct(StringComparer.Ordinal)
                          .ToList();

        var file = new DezhouDraftFile
        {
            Meta = new DezhouDraftMeta
            {
                Doc = doc ?? "",
                Label = string.IsNullOrWhiteSpace(label) ? "新项目征求意见" : label,
                Status = "draft",
                Note = string.IsNullOrWhiteSpace(note) ? DefaultNote : note,
                Refs = refs,
                Source = "德州市医疗保障局 立项指南规范整合文件（征求意见稿）",
                FileName = Path.GetFileName(xlsxPath),
                Batches = batches,
            },
            Items = items,
            Cancels = cancels,
            Maps = maps,
        };
        file.Meta.Note = string.IsNullOrWhiteSpace(note)
            ? DefaultNote.Replace("{n}", items.Count.ToString())
                          .Replace("{c}", cancels.Count.ToString())
            : note;
        if (warns.Count > 0) Console.Error.WriteLine("[dezhou-draft] 解析提示：\n  " + string.Join("\n  ", warns));
        return file;
    }

    /// <summary>页面顶部默认说明文案（{n} / {c} 会被替换为项目数与作废数）。</summary>
    public const string DefaultNote =
        "本批共 {n} 项拟新增、{c} 项拟作废，正在征求意见，尚未生效。价格以正式发布文件为准。";

    // ==================================================================
    // Sheet 归类
    // ==================================================================

    private static SheetInfo Classify(string rawName)
    {
        var name = (rawName ?? "").Trim();
        if (name.Length == 0) return null;
        const string prefix = "附件";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) return null;

        var rest = name[prefix.Length..];
        var i = 0;
        while (i < rest.Length && char.IsDigit(rest[i])) i++;
        var annex = rest[..i];
        var body = rest[i..].Trim();
        if (body.Length == 0) return null;

        var kind = Kind.New;
        if (body.EndsWith("废止", StringComparison.Ordinal))
        {
            kind = Kind.Cancel;
            body = body[..^"废止".Length];
        }
        else if (body.EndsWith("映射", StringComparison.Ordinal))
        {
            kind = Kind.Map;
            body = body[..^"映射".Length];
        }
        body = body.Trim();
        if (body.Length == 0) return null;

        return new SheetInfo { Name = rawName, Kind = kind, Category = NormalizeCat(body), Annex = annex };
    }

    /// <summary>
    /// 类别名归一化。
    ///
    /// <para>同一附件里「新增项目表」与「废止 / 映射表」的 sheet 命名**不统一**，实测本批即有两处：
    /// 「附件8<b>药学类</b>」对「附件17<b>药学</b>废止」、「附件6<b>病理类</b>」对「附件15<b>病理</b>废止」。
    /// 若按原名 GroupBy，会把同一类别拆成两组（一组有项目、一组只剩废止行），
    /// 表现为「多出两个 0 项的类别」。这里统一去掉尾部的「类」字再分组。</para>
    /// </summary>
    private static string NormalizeCat(string body)
    {
        var s = (body ?? "").Trim();
        return s.EndsWith("类", StringComparison.Ordinal) && s.Length > 1 ? s[..^1] : s;
    }

    private static int AnnexOrder(string annex)
        => int.TryParse(annex, out var n) ? n : 9999;

    // ==================================================================
    // 表头定位
    // ==================================================================

    /// <summary>
    /// 向下扫描找到表头行（含「序号」的那一行），返回 (表头行索引, 该行文本列表)。
    /// 找不到返回 (-1, null)。比写死第 3 行稳——个别附件会多插一行说明。
    /// </summary>
    private static (int Row, List<string> Cells) FindHeader(List<List<string>> aoa)
    {
        for (var r = 0; r < Math.Min(aoa.Count, 8); r++)
        {
            var row = aoa[r];
            if (row.Any(c => (c ?? "").Trim() == "序号")) return (r, row);
        }
        return (-1, null);
    }

    /// <summary>按表头名定位列号（归一化 + 包含匹配，避免列顺序变动 / 列名带换行或单位后缀）。</summary>
    private static int ColOf(List<string> header, params string[] aliases)
    {
        string Norm(string s) => (s ?? "").Replace("\n", "").Replace("\r", "")
            .Replace(" ", "").Replace("（", "(").Replace("）", ")").Replace("／", "/");
        if (header == null) return -1;
        // 先精确匹配，再退化为包含匹配（别名按长度降序，长的更具体）
        foreach (var a in aliases.OrderByDescending(x => x.Length))
        {
            var na = Norm(a);
            for (var i = 0; i < header.Count; i++)
                if (Norm(header[i]) == na) return i;
        }
        foreach (var a in aliases.OrderByDescending(x => x.Length))
        {
            var na = Norm(a);
            for (var i = 0; i < header.Count; i++)
            {
                var h = Norm(header[i]);
                if (h.Length > 0 && h.Contains(na, StringComparison.Ordinal)) return i;
            }
        }
        return -1;
    }

    private static string Cut(List<string> row, int idx)
        => idx >= 0 && idx < row.Count ? (row[idx] ?? "").Trim() : "";

    // ==================================================================
    // 三类表各自的解析
    // ==================================================================

    /// <summary>拟新增项目表。</summary>
    private static List<DezhouPriceItem> ParseNew(ISheet sheet, string category, string doc)
    {
        var list = new List<DezhouPriceItem>();
        var aoa = ExcelUtil.ReadAoa(sheet);
        var (hr, header) = FindHeader(aoa);
        if (hr < 0) return list;

        var cSeq = ColOf(header, "序号");
        var cCode = ColOf(header, "项目编码", "编码");
        var cName = ColOf(header, "项目名称", "名称");
        var cOut = ColOf(header, "服务产出");
        var cComp = ColOf(header, "价格构成");
        var cUnit = ColOf(header, "计价单位", "单位");
        var cP3 = ColOf(header, "三级价格");
        var cP2 = ColOf(header, "二级价格");
        var cP1 = ColOf(header, "一级价格");
        var cUsage = ColOf(header, "计价说明", "说明");
        // 正式版（2026-10-27 起，德医保发〔2026〕25号）比征求意见稿多 4 列：
        // 是否医保支付 / 自付比例 / 限定支付范围；「一般治疗」类另有「扣减基本物耗价格」。
        // 征求意见稿没有这些列 → ColOf 返回 -1 → 取空串，与旧行为完全一致。
        var cIns = ColOf(header, "是否医保支付");
        var cSelf = ColOf(header, "自付比例");
        var cLimit = ColOf(header, "限定支付范围");
        var cDeduct = ColOf(header, "扣减基本物耗");

        for (var r = hr + 1; r < aoa.Count; r++)
        {
            var row = aoa[r];
            var code = Cut(row, cCode);
            if (code.Length == 0) continue;
            // 结束行保护：出现过「使用说明：」这类说明段则停止
            if (code.StartsWith("使用说明", StringComparison.Ordinal)) break;
            if (!code.All(ch => char.IsLetterOrDigit(ch))) continue;

            list.Add(new DezhouPriceItem
            {
                Doc = doc ?? "",
                Exec = "",
                Category = category,
                Seq = Cut(row, cSeq),
                Code = code,
                Kind = KindOf(code),
                Name = Cut(row, cName),
                ServiceOutput = Cut(row, cOut),
                PriceComposition = Cut(row, cComp),
                Unit = Cut(row, cUnit),
                Price3 = Cut(row, cP3),
                Price2 = Cut(row, cP2),
                Price1 = Cut(row, cP1),
                Insurance = Cut(row, cIns),
                SelfPay = Cut(row, cSelf),
                Note = Cut(row, cLimit),
                Deduct = Cut(row, cDeduct),
                Usage = Cut(row, cUsage),
                Status = "draft",
            });
        }
        return list;
    }

    /// <summary>拟作废原项目表。</summary>
    private static List<DezhouCancelItem> ParseCancel(ISheet sheet, string category, string doc)
    {
        var list = new List<DezhouCancelItem>();
        var aoa = ExcelUtil.ReadAoa(sheet);
        var (hr, header) = FindHeader(aoa);
        if (hr < 0) return list;

        var cCode = ColOf(header, "项目编码", "编码");
        var cName = ColOf(header, "项目名称", "名称");
        var cNote = ColOf(header, "备注");
        var cUsage = ColOf(header, "说明");

        for (var r = hr + 1; r < aoa.Count; r++)
        {
            var row = aoa[r];
            var code = Cut(row, cCode);
            if (code.Length == 0) continue;
            if (code.StartsWith("使用说明", StringComparison.Ordinal)) break;
            if (!code.All(ch => char.IsLetterOrDigit(ch))) continue;

            var note = Cut(row, cNote);
            if (note.Length == 0) note = Cut(row, cUsage);
            list.Add(new DezhouCancelItem
            {
                Doc = doc ?? "",
                Exec = "",
                Category = category,
                OldCode = code,
                OldName = Cut(row, cName),
                NewCode = "",
                NewName = "",
                Note = note,
                Pending = true,
            });
        }
        return list;
    }

    /// <summary>映射关系表（两段式表头：左半 = 新项目，右半 = 拟作废的原项目）。</summary>
    private static List<DezhouDraftMapRow> ParseMap(ISheet sheet, string category)
    {
        var list = new List<DezhouDraftMapRow>();
        var aoa = ExcelUtil.ReadAoa(sheet);
        var (hr, header) = FindHeader(aoa);
        if (hr < 0) return list;

        // 表头行形如：序号 | 项目编码 | 项目名称 | [项目]编码 | 项目名称
        // 前三个「项目编码 / 项目名称」属新项目侧，之后的同名列为原项目侧。
        // 同名无法用 ColOf 区分，故按「第 2 个同名列表头」定位。
        var codeCols = new List<int>();
        var nameCols = new List<int>();
        for (var i = 0; i < header.Count; i++)
        {
            var h = (header[i] ?? "").Replace("\n", "").Replace("\r", "").Trim();
            if (h.Contains("编码", StringComparison.Ordinal)) codeCols.Add(i);
            else if (h.Contains("名称", StringComparison.Ordinal)) nameCols.Add(i);
        }
        if (codeCols.Count == 0) return list;

        var cNewCode = codeCols[0];
        var cName1 = nameCols.Count > 0 ? nameCols[0] : -1;
        var cOldCode = codeCols.Count > 1 ? codeCols[^1] : -1;
        var cName2 = nameCols.Count > 1 ? nameCols[^1] : -1;
        var cSeq = ColOf(header, "序号");
        var cNote = ColOf(header, "备注", "说明");

        // 映射表中「新码」纵向合并（续行留空），需向下继承；旧码每行独立。
        var carryNewCode = "";
        var carryNewName = "";
        for (var r = hr + 1; r < aoa.Count; r++)
        {
            var row = aoa[r];
            var newCode = Cut(row, cNewCode);
            var newName = Cut(row, cName1);
            if (newCode.Length > 0) { carryNewCode = newCode; carryNewName = newName; }
            else if (newName.Length > 0) { carryNewName = newName; }

            var oldCode = Cut(row, cOldCode);
            var oldName = Cut(row, cName2);
            if (oldCode.Length == 0 && oldName.Length == 0) continue;
            if (oldCode.StartsWith("使用说明", StringComparison.Ordinal)) break;

            list.Add(new DezhouDraftMapRow
            {
                Seq = Cut(row, cSeq),
                NewCode = newCode.Length > 0 ? newCode : carryNewCode,
                NewName = newCode.Length > 0 ? newName : carryNewName,
                OldCode = oldCode,
                OldName = oldName,
                Category = category,
                Note = Cut(row, cNote),
                Pending = true,
            });
        }
        return list;
    }

    /// <summary>
    /// 由 15 位编码判定项目性质：第 12–13 位非 00 = 扩展项，第 14–15 位非 00 = 加收项，其余 = 主项目。
    /// （与 <see cref="DezhouPriceStore"/> 既有口径一致。）
    /// </summary>
    public static string KindOf(string code)
    {
        var c = (code ?? "").Trim();
        if (c.Length < 15) return "main";
        var a = c.Substring(11, 2);
        var b = c.Substring(13, 2);
        if (b != "00") return "add";
        if (a != "00") return "ext";
        return "main";
    }
}
