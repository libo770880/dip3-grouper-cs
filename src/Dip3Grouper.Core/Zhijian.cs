using System.Text.Json.Serialization;

namespace Dip3Grouper;

/// <summary>病理开具指引表头信息（来源 / 副标题 / 条目数）。</summary>
public sealed class ZhijianMeta
{
    public string Title { get; set; } = "病理开具指引";
    public string Sub { get; set; } = "";
    public string Source { get; set; } = "";
    public string Hotline { get; set; } = "";
    public int Count { get; set; }
}

/// <summary>
/// 计费口径里不随医院等级变化的公共参数：
/// 诊断费 10 个蜡块内只收 1 次、第 11 块起每块加收 5%、最多加收到第 30 块；
/// 化学染色费前 3 块每块减收 5 元。
/// </summary>
public sealed class ZhijianBase
{
    public int DiagCap { get; set; } = 10;
    public int DiagMax { get; set; } = 30;
    public int StainCutCount { get; set; } = 3;
    public string Note { get; set; } = "";
}

/// <summary>
/// 一个价格档（德州市二级 / 三级）。数值全部取自《德州市病理一二三级物价》表：
/// 诊断费 / 处理费 / 染色费都是「未减收」单价，减收额单列（DiagCut / StainCut），
/// 由 <see cref="ZhijianStore.Quote(ZhijianBase, ZhijianLevel, int, string)"/> 统一扣减，
/// 免得表里写一个数、代码里再写一个数对不上。
/// </summary>
public sealed class ZhijianLevel
{
    /// <summary>档位标识（2 / 3）</summary>
    public string Id { get; set; } = "2";
    public string Name { get; set; } = "二级";

    /// <summary>病理诊断费（未减收）</summary>
    public decimal Diag { get; set; }
    /// <summary>诊断费减收额（手机可查报告时收原价，不能查时减收）</summary>
    public decimal DiagCut { get; set; }

    /// <summary>病理标本处理费（组织病理-常规）/ 蜡块</summary>
    public decimal Proc { get; set; }
    /// <summary>病理标本处理费（组织病理-复杂）/ 蜡块</summary>
    public decimal ProcComplex { get; set; }

    /// <summary>病理样本化学染色检查费 / 蜡块（第 4 块起）</summary>
    public decimal Stain { get; set; }
    /// <summary>前 3 块每块减收额</summary>
    public decimal StainCut { get; set; }

    /// <summary>病理标本处理费（细胞病理）/ 张</summary>
    public decimal CellProc { get; set; }
    /// <summary>细胞病理蜡块制作费 / 个</summary>
    public decimal CellBlock { get; set; }

    /// <summary>病理样本免疫组织化学染色检查费（常规）/ 项（第 4 项起）</summary>
    public decimal Ihc { get; set; }
    public decimal IhcCut { get; set; }

    /// <summary>疑难病理会诊（10 张内）/ 次</summary>
    public decimal Consult { get; set; }
    /// <summary>疑难病理会诊超 10 张每张加收</summary>
    public decimal ConsultExtra { get; set; }

    public string Note { get; set; } = "";

    /// <summary>诊断费实收（减收后）</summary>
    [JsonIgnore] public decimal DiagNet => Diag - DiagCut;
    /// <summary>超 10 张每块加收 = 诊断费 × 5%</summary>
    [JsonIgnore] public decimal DiagStep => Math.Round(Diag * 0.05m, 2);
    /// <summary>染色费前 3 块单价（减收后）</summary>
    [JsonIgnore] public decimal StainCutUnit => Stain - StainCut;
    /// <summary>免疫组化前 3 项单价（减收后）</summary>
    [JsonIgnore] public decimal IhcCutUnit => Ihc - IhcCut;
}

/// <summary>专项项目卡片（细胞病理 / 细胞蜡块 / 免疫组化 / 会诊 / 基因检测）。</summary>
public sealed class ZhijianSpecial
{
    public string Title { get; set; } = "";
    /// <summary>二级价格口径</summary>
    public List<string> L2 { get; set; } = new();
    /// <summary>三级价格口径</summary>
    public List<string> L3 { get; set; } = new();
    public string Note { get; set; } = "";

    /// <summary>当前档位下的口径（网页 / 桌面都用这个取，避免两边各挑一份）</summary>
    public List<string> LinesFor(string levelId) =>
        levelId == "3" ? (L3 ?? new List<string>()) : (L2 ?? new List<string>());
}

/// <summary>一个开具条目：病种系统 → 标本适应证 → 蜡块档位。</summary>
public sealed class ZhijianItem
{
    /// <summary>常规 / 复杂（决定标本处理费单价）</summary>
    public string Kind { get; set; } = "常规";
    /// <summary>该标本类型建议蜡块数</summary>
    public int Wax { get; set; }
    /// <summary>适应证原文（顿号分隔）</summary>
    public string Sample { get; set; } = "";
}

/// <summary>一个疾病系统下的全部条目。</summary>
public sealed class ZhijianSystem
{
    public string Name { get; set; } = "";
    public List<ZhijianItem> Items { get; set; } = new();
}

public sealed class ZhijianFile
{
    public ZhijianMeta Meta { get; set; } = new();
    public ZhijianBase Base { get; set; } = new();
    public List<ZhijianLevel> Levels { get; set; } = new();
    public List<string> Rules { get; set; } = new();
    public List<ZhijianSpecial> Special { get; set; } = new();
    public List<ZhijianSystem> Systems { get; set; } = new();
}

/// <summary>扁平化后的条目（带所属系统，供列表 / 搜索使用）。</summary>
public sealed class ZhijianEntry
{
    public string System { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Wax { get; set; }
    public string Sample { get; set; } = "";
    /// <summary>档位文本，如「常规病理 4蜡块」</summary>
    public string Tier => $"{Kind}病理 {Wax}蜡块";
}

/// <summary>一行费用明细。</summary>
public sealed class ZhijianQuoteLine
{
    public string Name { get; set; } = "";
    public decimal Unit { get; set; }
    public decimal Qty { get; set; }
    public decimal Amount { get; set; }
    /// <summary>数量说明（如「前 3 块减收 5 元」「第 11~15 块加收 5%」）</summary>
    public string Note { get; set; } = "";
}

/// <summary>按蜡块（或涂片）数算出的收费明细。</summary>
public sealed class ZhijianQuote
{
    /// <summary>蜡块数 / 涂片数</summary>
    public int Count { get; set; }
    /// <summary>常规 / 复杂 / 细胞 / TCT</summary>
    public string Mode { get; set; } = "常规";
    public string LevelId { get; set; } = "2";
    public string LevelName { get; set; } = "二级";
    public bool Complex => Mode == "复杂";
    public decimal Proc { get; set; }
    public decimal Diag { get; set; }
    public decimal Stain { get; set; }
    public decimal Total { get; set; }
    public List<ZhijianQuoteLine> Lines { get; set; } = new();
}

/// <summary>
/// 「病理开具指引」库（data/zhijian.json）。
/// 标本 / 蜡块档位来自《组织病理·送检指引》海报（安谱 ADICON），
/// 价格口径重建为《德州市病理一二三级物价》（二级 / 三级两档），
/// 规则说明取自《关于病理科新物价收费的切换说明》。
/// 桌面端与网页端共用本类，避免两端档位 / 报价不一致。
/// </summary>
public sealed class ZhijianStore
{
    /// <summary>计价模式</summary>
    public const string ModeNormal = "常规";
    public const string ModeComplex = "复杂";
    public const string ModeCell = "细胞";
    public const string ModeTct = "TCT";

    public ZhijianMeta Meta { get; private set; } = new();
    public ZhijianBase Base { get; private set; } = new();
    public List<ZhijianLevel> Levels { get; private set; } = new();
    public List<string> Rules { get; private set; } = new();
    public List<ZhijianSpecial> Special { get; private set; } = new();
    public List<ZhijianSystem> Systems { get; private set; } = new();
    /// <summary>全部条目扁平列表（按海报「自上而下、先左后右」的顺序）</summary>
    public List<ZhijianEntry> Entries { get; private set; } = new();
    public bool Loaded { get; private set; }

    private readonly object _gate = new();

    public void EnsureLoaded()
    {
        if (Loaded) return;
        lock (_gate)
        {
            if (Loaded) return;
            Load();
        }
    }

    public void Load()
    {
        var d = Js.ReadJsonFile<ZhijianFile>(AppPaths.ZhijianFile);
        if (d == null)
        {
            Meta = new ZhijianMeta();
            Base = new ZhijianBase();
            Levels = new List<ZhijianLevel>();
            Rules = new List<string>();
            Special = new List<ZhijianSpecial>();
            Systems = new List<ZhijianSystem>();
            Entries = new List<ZhijianEntry>();
            Loaded = true;
            return;
        }

        Meta = d.Meta ?? new ZhijianMeta();
        Base = d.Base ?? new ZhijianBase();
        Levels = d.Levels is { Count: > 0 } ? d.Levels : new List<ZhijianLevel> { new() };
        Rules = d.Rules ?? new List<string>();
        Special = d.Special ?? new List<ZhijianSpecial>();
        Systems = d.Systems ?? new List<ZhijianSystem>();
        Entries = Systems
            .SelectMany(s => (s.Items ?? new List<ZhijianItem>()).Select(i => new ZhijianEntry
            {
                System = s.Name ?? "",
                Kind = string.IsNullOrWhiteSpace(i.Kind) ? "常规" : i.Kind,
                Wax = i.Wax,
                Sample = i.Sample ?? "",
            }))
            .ToList();
        Loaded = true;
    }

    /// <summary>按 Id 取价格档；找不到时回落到第一档（二级），绝不返回 null。</summary>
    public ZhijianLevel Level(string id)
    {
        EnsureLoaded();
        if (Levels.Count == 0) return new ZhijianLevel();
        if (!string.IsNullOrWhiteSpace(id))
        {
            var hit = Levels.FirstOrDefault(l => l.Id == id);
            if (hit != null) return hit;
        }
        return Levels[0];
    }

    /// <summary>关键词检索：命中系统名 / 适应证 / 档位文本任一即算命中；空关键词返回全部。</summary>
    public List<ZhijianEntry> Search(string q, string system = null, string kind = null)
    {
        EnsureLoaded();
        IEnumerable<ZhijianEntry> src = Entries;
        if (!string.IsNullOrWhiteSpace(system) && system != "全部")
            src = src.Where(e => e.System == system);
        if (!string.IsNullOrWhiteSpace(kind) && kind != "全部")
            src = src.Where(e => e.Kind == kind);

        var kw = (q ?? "").Trim();
        if (kw.Length == 0) return src.ToList();

        var parts = kw.Split(new[] { ' ', '\t', '，', ',' }, StringSplitOptions.RemoveEmptyEntries);
        return src.Where(e => parts.All(p =>
        {
            var s = e.System + " " + e.Sample + " " + e.Tier;
            return s.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0;
        })).ToList();
    }

    /// <summary>按蜡块数出报价明细（诊断费 + 标本处理费 + 化学染色费）。</summary>
    public ZhijianQuote Quote(int count, bool complex, string levelId) =>
        Quote(count, complex ? ModeComplex : ModeNormal, levelId);

    /// <summary>按蜡块 / 涂片数与计价模式出报价。</summary>
    public ZhijianQuote Quote(int count, string mode, string levelId)
    {
        EnsureLoaded();
        return Quote(Base, Level(levelId), count, mode);
    }

    /// <summary>纯函数版本：便于自检直接对德州物价表逐行核对（二级 / 三级 / 细胞 / TCT）。</summary>
    public static ZhijianQuote Quote(ZhijianBase b, ZhijianLevel lv, int count, string mode)
    {
        b ??= new ZhijianBase();
        lv ??= new ZhijianLevel();
        var m = string.IsNullOrWhiteSpace(mode) ? ModeNormal : mode;
        if (count < 0) count = 0;

        var q = new ZhijianQuote
        {
            Count = count, Mode = m, LevelId = lv.Id, LevelName = lv.Name,
        };

        var unit = m switch
        {
            ModeComplex => lv.ProcComplex,
            ModeCell or ModeTct => lv.CellProc,
            _ => lv.Proc,
        };
        var procName = m switch
        {
            ModeComplex => "病理标本处理费（组织病理-复杂）",
            ModeCell or ModeTct => "病理标本处理费（细胞病理）",
            _ => "病理标本处理费（组织病理-常规）",
        };

        var proc = new ZhijianQuoteLine
        {
            Name = procName, Unit = unit, Qty = count, Amount = Math.Round(unit * count, 2),
            Note = m is ModeCell or ModeTct ? "按玻片张数" : "按蜡块数",
        };
        q.Lines.Add(proc);

        // TCT 只收诊断费 + 细胞病理处理费，不收化学染色费（德州物价表 50503145894 口径）
        if (m != ModeTct)
        {
            var cut = Math.Min(count, Math.Max(0, b.StainCutCount));
            if (cut > 0)
                q.Lines.Add(new ZhijianQuoteLine
                {
                    Name = "病理样本化学染色检查费-已减收",
                    Unit = lv.StainCutUnit, Qty = cut, Amount = Math.Round(lv.StainCutUnit * cut, 2),
                    Note = $"前 {cut} 块每块减收 {lv.StainCut:0.##} 元",
                });
            if (count > cut)
                q.Lines.Add(new ZhijianQuoteLine
                {
                    Name = "病理样本化学染色检查费",
                    Unit = lv.Stain, Qty = count - cut, Amount = Math.Round(lv.Stain * (count - cut), 2),
                    Note = $"{cut + 1} 块起按原价",
                });
        }

        q.Lines.Add(new ZhijianQuoteLine
        {
            Name = "病理诊断费-已减收", Unit = lv.DiagNet, Qty = 1, Amount = lv.DiagNet,
            Note = $"{Math.Max(1, b.DiagCap)} 个蜡块内只收 1 次（减收 {lv.DiagCut:0.##} 元）",
        });

        if (count > b.DiagCap)
        {
            var extra = Math.Min(count, b.DiagMax) - b.DiagCap;   // 最多加收到第 30 块
            if (extra > 0)
                q.Lines.Add(new ZhijianQuoteLine
                {
                    Name = "病理诊断费-超过 10 张加收", Unit = lv.DiagStep, Qty = extra,
                    Amount = Math.Round(lv.DiagStep * extra, 2),
                    Note = $"第 {b.DiagCap + 1}~{Math.Min(count, b.DiagMax)} 块，每块加收 5%",
                });
        }

        q.Proc = q.Lines.Where(x => x.Name.StartsWith("病理标本处理费")).Sum(x => x.Amount);
        q.Stain = q.Lines.Where(x => x.Name.Contains("化学染色")).Sum(x => x.Amount);
        q.Diag = q.Lines.Where(x => x.Name.StartsWith("病理诊断费")).Sum(x => x.Amount);
        q.Total = Math.Round(q.Lines.Sum(x => x.Amount), 2);
        return q;
    }

    /// <summary>
    /// 蜡块速算（三大规则的算术化）：
    /// ① 1 个瓶子/袋子 = 1 个蜡块；1 个蜡块最多放 4 块组织，超出按每 4 块进位；
    /// ② 多部位按各自瓶袋分开算；
    /// ③ 体积超过 2cm×2cm×2cm 的，每超出 1cm 加 1 块。
    /// </summary>
    /// <param name="bags">瓶子/袋子数</param>
    /// <param name="piecesPerBag">每袋组织块数（默认 1）</param>
    /// <param name="maxCm">最大标本边长 cm（≤2 不加）</param>
    public static int WaxCount(int bags, int piecesPerBag, int maxCm)
    {
        if (bags <= 0) return 0;
        var per = Math.Max(1, piecesPerBag);
        var wax = bags * ((per + 3) / 4);
        if (maxCm > 2) wax += maxCm - 2;
        return wax;
    }
}
