using System.Text.Json;
using Dip3Grouper;
using Xunit;

namespace Dip3Grouper.Tests;

/// <summary>
/// 分组器测试 —— 逐条移植 Node 版 test/run.js 的 44 条断言。
/// 每个用例: (名称, 病案 JSON, 期望子集 JSON)，期望子集支持 matched / groupType / groupTypeCode / seq / name / detail.*。
/// </summary>
public class GroupingTests
{
    // 配置与综合病组只加载一次（对齐 Node createGrouper 自动挂载 data/zonghe.json）
    private static readonly Dip3Config Cfg = Js.ReadJsonFile<Dip3Config>(AppPaths.ConfigFile)!;
    private static readonly List<ZongheRow> ZongheRows = Js.ReadJsonFile<ZongheFile>(AppPaths.ZongheFile)?.Rows ?? new List<ZongheRow>();

    private static Dip3Grouper NewGrouper(bool tryJiceng = true, Dip3Config cfg = null)
    {
        var g = new Dip3Grouper(cfg ?? Cfg, tryJiceng);
        g.SetZonghe(ZongheRows);
        return g;
    }

    private static Dip3Grouper NoJiceng() => NewGrouper(false);

    // ------------------------------------------------------------------
    // 用例表
    // ------------------------------------------------------------------
    public static IEnumerable<object[]> Cases()
    {
        // 一、先期分组
        yield return C("ECMO 单码主手术", """{"mainProc":"39.6500x001"}""", """{"matched":true,"groupTypeCode":"XQ","seq":11}""");
        yield return C("呼吸机≥96h+CRRT 组合码", """{"mainProc":"96.7201","otherProcs":"39.9500x007"}""", """{"matched":true,"groupTypeCode":"XQ","seq":13}""");
        yield return C("呼吸机≥96h 单独（不误入组合行）", """{"mainProc":"96.7201"}""", """{"matched":true,"groupTypeCode":"XQ","seq":14}""");
        yield return C("心脏移植", """{"mainDiag":"I25.900","mainProc":"37.5100x001"}""", """{"matched":true,"groupTypeCode":"XQ","seq":2}""");
        yield return C("新生儿 极低出生体重 1200g", """{"mainDiag":"P07.100","otherDiags":"P05.900","ageDays":5,"birthWeight":1200}""", """{"matched":true,"groupTypeCode":"XQ","seq":17}""");
        yield return C("新生儿 低出生体重 2300g", """{"mainDiag":"P07.100","ageDays":28,"birthWeight":2300}""", """{"matched":true,"groupTypeCode":"XQ","seq":19}""");
        yield return C("体重≥2500g/年龄>28天 不入体重档→落基础规则P07保守行", """{"mainDiag":"P07.100","ageDays":28,"birthWeight":2600}""", """{"matched":true,"groupTypeCode":"JC","seq":4478}""");
        yield return C("年龄>28天 不入体重档→落基础规则P07保守行", """{"mainDiag":"P07.100","ageDays":40,"birthWeight":1200}""", """{"matched":true,"groupTypeCode":"JC","seq":4478}""");

        // 二、诊断辅助细分
        yield return C("肿瘤: Z51.1 单纯静脉化疗", """{"mainDiag":"Z51.100x002","otherDiags":"C34.900","mainProc":"99.2503"}""", """{"matched":true,"groupTypeCode":"FZ","seq":1770}""");
        yield return C("肿瘤: 化疗+靶向 取最长组合", """{"mainDiag":"Z51.100x002","otherDiags":"C34.900","mainProc":"99.2503","otherProcs":"99.2800x006"}""", """{"matched":true,"groupTypeCode":"FZ","seq":1772}""");
        yield return C("肿瘤: 其他诊断无恶性肿瘤不入", """{"mainDiag":"Z51.100x002","otherDiags":"K35.900","mainProc":"99.2503"}""", """{"matched":false}""");
        yield return C("烧伤: 二度+面积<10%+清创", """{"mainDiag":"T20.200","otherDiags":"T31.000","mainProc":"86.2200x011"}""", """{"matched":true,"groupTypeCode":"FZ","seq":1746}""");
        yield return C("烧伤: 二度+面积<10%+皮片移植", """{"mainDiag":"T20.200","otherDiags":"T31.000","mainProc":"86.6201"}""", """{"matched":true,"groupTypeCode":"FZ","seq":1747}""");
        yield return C("烧伤: 面积10-29%档不同行", """{"mainDiag":"T20.200","otherDiags":"T31.100","mainProc":"86.6201"}""", """{"matched":true,"groupTypeCode":"FZ","seq":1750}""");
        yield return C("结核区间行(A15-A16)=综合病组: 非耐药", """{"mainDiag":"A15.000x002","mainProc":"31.9400"}""", """{"matched":true,"groupTypeCode":"ZH","seq":1813}""");
        yield return C("结核区间行: 其他诊断含U84.300→耐药", """{"mainDiag":"A15.000x002","mainProc":"31.9400","otherDiags":"U84.300"}""", """{"matched":true,"groupTypeCode":"ZH","seq":1812}""");
        yield return C("结核区间行: 主诊断为附表耐药拓展码→耐药", """{"mainDiag":"A15.000x010","mainProc":"31.9400"}""", """{"matched":true,"groupTypeCode":"ZH","seq":1812}""");

        // 三、并项规则
        yield return C("舌癌部分切除+颈淋巴清扫(相关手术命中)", """{"mainDiag":"C02.900","mainProc":"25.2x00x001","otherProcs":"40.5901"}""", """{"matched":true,"groupTypeCode":"BX","seq":51}""");
        yield return C("舌癌部分切除无相关手术→入无相关行", """{"mainDiag":"C02.900","mainProc":"25.2x00x001"}""", """{"matched":true,"groupTypeCode":"BX","seq":52}""");
        yield return C("放疗+化疗并项(相关手术优先于无相关行)", """{"mainDiag":"Z51.000x001","mainProc":"92.2400x005","otherProcs":"99.2503"}""", """{"matched":true,"groupTypeCode":"BX","seq":1733}""");
        yield return C("单纯放疗→落基础规则", """{"mainDiag":"Z51.000x001","mainProc":"92.2400x005"}""", """{"matched":true,"groupTypeCode":"JC","seq":5095}""");
        yield return C("胃肠炎+胃镜检查(胃镜被排除→保守行)", """{"mainDiag":"A09.901","mainProc":"44.1300x001"}""", """{"matched":true,"groupTypeCode":"JC","seq":1868,"detail":{"conservative":true}}""");

        // 四、基础规则
        yield return C("伤寒保守治疗（仅主诊断）", """{"mainDiag":"A01.000"}""", """{"matched":true,"groupTypeCode":"JC","seq":1858,"name":"伤寒"}""");
        yield return C("主手术不在基础规则→综合病种", """{"mainDiag":"A01.000","mainProc":"86.3x03"}""", """{"matched":false}""");

        // 五、不纳入分组
        yield return C("霍乱主诊断不纳入", """{"mainDiag":"A00.000"}""", """{"matched":false}""");
        yield return C("胃肠炎主诊断A09.900在排除表→基层兜底(tryJiceng)", """{"mainDiag":"A09.900"}""", """{"matched":true,"groupTypeCode":"JICENG","seq":1}""");
        yield return C("主手术不纳入→按保守治疗入组(胃镜被排除)", """{"mainDiag":"A09.901","mainProc":"44.1300x001"}""", """{"matched":true,"groupTypeCode":"JC","seq":1868,"detail":{"conservative":true}}""");
        yield return C("舌恶性肿瘤+胃镜被排除→保守行", """{"mainDiag":"C02.900","mainProc":"44.1300x001"}""", """{"matched":true,"groupTypeCode":"JC","seq":1970}""");

        // 八、结核类（区间行=综合病组, 单三位码=核心病种行）
        yield return C("结核区间码: A15 + 气管注入(非耐药)", """{"mainDiag":"A15.000x001","mainProc":"31.9400"}""", """{"matched":true,"groupTypeCode":"ZH","seq":1813}""");
        yield return C("结核区间码: A16 区间下半段命中", """{"mainDiag":"A16.500x001","mainProc":"32.0100x004","otherDiags":"U84.300"}""", """{"matched":true,"groupTypeCode":"ZH","seq":1814}""");
        yield return C("结核区间码: A15 保守行外手术不误入", """{"mainDiag":"A15.000x001","mainProc":"99.9999"}""", """{"matched":false}""");
        yield return C("A17 神经系统结核 保守行(无主手术,非耐药)", """{"mainDiag":"A17.900"}""", """{"matched":true,"groupTypeCode":"FZ","seq":1841}""");
        yield return C("A17 神经系统结核 保守行 耐药(U84.300)", """{"mainDiag":"A17.900","otherDiags":"U84.300"}""", """{"matched":true,"groupTypeCode":"FZ","seq":1840}""");
        yield return C("A17 + 腰椎穿刺手术行", """{"mainDiag":"A17.900","mainProc":"03.3101"}""", """{"matched":true,"groupTypeCode":"FZ","seq":1839}""");
        yield return C("A18 其他器官结核 保守行", """{"mainDiag":"A18.900"}""", """{"matched":true,"groupTypeCode":"FZ","seq":1853}""");
        yield return C("A19 粟粒性结核 保守行", """{"mainDiag":"A19.900"}""", """{"matched":true,"groupTypeCode":"FZ","seq":1857}""");
        yield return C("A15 区间保守行(无主手术)→综合病组", """{"mainDiag":"A15.000x001"}""", """{"matched":true,"groupTypeCode":"ZH","seq":1837}""");
    }

    private static object[] C(string name, string kase, string expect) => new object[] { name, kase, expect };

    [Theory]
    [MemberData(nameof(Cases))]
    public void 分组用例(string name, string kaseJson, string expectJson)
    {
        var g = NewGrouper();
        var kase = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(kaseJson)!;
        var r = g.Group(kase);
        AssertSubset(name, r, expectJson);
    }

    private static void AssertSubset(string name, GroupResult r, string expectJson)
    {
        var exp = JsonDocument.Parse(expectJson).RootElement;
        foreach (var p in exp.EnumerateObject())
        {
            switch (p.Name)
            {
                case "matched":
                    Assert.True(p.Value.GetBoolean() == r.Matched,
                        $"{name}: matched 期望 {p.Value.GetBoolean()}, 实际 {r.Matched}");
                    break;
                case "groupTypeCode":
                    Assert.True(p.Value.GetString() == r.GroupTypeCode,
                        $"{name}: groupTypeCode 期望 {p.Value.GetString()}, 实际 {r.GroupTypeCode}");
                    break;
                case "groupType":
                    Assert.True(p.Value.GetString() == r.GroupType,
                        $"{name}: groupType 期望 {p.Value.GetString()}, 实际 {r.GroupType}");
                    break;
                case "seq":
                    Assert.True(p.Value.GetInt32() == r.Seq,
                        $"{name}: seq 期望 {p.Value.GetInt32()}, 实际 {(r.Seq.HasValue ? r.Seq.Value.ToString() : "null")}");
                    break;
                case "name":
                    Assert.True(p.Value.GetString() == r.Name,
                        $"{name}: name 期望 {p.Value.GetString()}, 实际 {r.Name}");
                    break;
                case "detail":
                    foreach (var dp in p.Value.EnumerateObject())
                    {
                        Assert.True(r.Detail.TryGetValue(dp.Name, out var actual),
                            $"{name}: detail.{dp.Name} 不存在于实际明细 {JsonSerializer.Serialize(r.Detail, Js.Data)}");
                        var actualJson = actual is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(actual, Js.Data);
                        Assert.True(dp.Value.GetRawText() == actualJson,
                            $"{name}: detail.{dp.Name} 期望 {dp.Value.GetRawText()}, 实际 {actualJson}");
                    }
                    break;
            }
        }
    }

    // ------------------------------------------------------------------
    // 六、基层病种
    // ------------------------------------------------------------------

    [Fact(DisplayName = "核心未入→基层兜底 (JICENG#1)")]
    public void 基层兜底()
    {
        // 构造无核心库的分组器, 验证基层兜底路径
        var cfg = CloneConfigWithoutJichu();
        var g = NewGrouper(true, cfg);
        var rj = g.Group(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"mainDiag":"A09.900"}""")!);
        Assert.True(rj.Matched,
            $"实际: {JsonSerializer.Serialize(rj, Js.Data)} | jichu={cfg.Jichu.Count} jiceng={cfg.Jiceng.Count} exclDiag={cfg.ExcludedDiags.Count} bx={cfg.Bingxiang.Count}");
        Assert.Equal("JICENG", rj.GroupTypeCode);
        Assert.Equal(1, rj.Seq);
    }

    [Fact(DisplayName = "关闭基层兜底后未入组")]
    public void 关闭基层兜底()
    {
        var r2 = NoJiceng().Group(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"mainDiag":"A09.900"}""")!);
        Assert.False(r2.Matched);
    }

    private static Dip3Config CloneConfigWithoutJichu()
    {
        // 通过 JSON 往返克隆，避免改动共享的静态配置（对齐 Node test/run.js 的 { ...config, jichu: [] }）
        var json = JsonSerializer.Serialize(Cfg, Js.Config);
        var cfg = Js.FromJson<Dip3Config>(json)!;
        cfg.Jichu = new List<DiagRow>();
        return cfg;
    }

    // ------------------------------------------------------------------
    // 七、综合病组（核心未入组 → 三位类目范围兜底）
    // ------------------------------------------------------------------

    /// <summary>动态取一个核心病种未覆盖的三位类目</summary>
    private static string FreeCategory()
    {
        var used = new HashSet<string>();
        foreach (var rows in new IEnumerable<string>[] { Cfg.Bingxiang.SelectMany(r => r.DiagCodes), Cfg.Jichu.SelectMany(r => r.DiagCodes), Cfg.Jiceng.SelectMany(r => r.DiagCodes) })
            foreach (var d in rows)
            {
                var m = System.Text.RegularExpressions.Regex.Match(d ?? "", "^([A-Z]\\d{2})");
                if (m.Success) used.Add(m.Groups[1].Value);
            }
        for (var i = 0; i < 26 * 100; i++)
        {
            var c = ((char)('A' + i % 26)) + (i / 26).ToString("00");
            if (System.Text.RegularExpressions.Regex.IsMatch(c, "^[A-Z]\\d{2}$") && !used.Contains(c)) return c;
        }
        return "Y99";
    }

    [Fact(DisplayName = "七、综合病组: 核心可入组时不落综合病组 / 未覆盖类目兜底 / 排除诊断 / 区间保守行 / 热更新清空")]
    public void 综合病组()
    {
        var g = NewGrouper();
        var zhCat = FreeCategory();

        g.SetZonghe(new List<ZongheRow>
        {
            new() { Seq = 9001, Code = "ZH-A15", Name = "综合病组（呼吸道结核）", Ranges = new List<string> { "A15-A16" } },
            new() { Seq = 9002, Code = $"ZH-{zhCat}", Name = $"综合病组（{zhCat}）", Ranges = new List<string> { zhCat } },
        });

        // 核心可入组时不落综合病组: 动态取一条基础规则保守行
        var consRow = Cfg.Jichu.First(r =>
            r.ProcCodes.Count == 0
            && System.Text.RegularExpressions.Regex.IsMatch(r.DiagCodes[0], "^[A-Z]\\d{2}(\\.\\d)?$")
            && r.DiagCodes[0][..3] != "A15" && r.DiagCodes[0][..3] != "A16"
            && r.DiagCodes[0][..3] != zhCat);
        var rzhCore = g.Group(new Dictionary<string, JsonElement> { ["mainDiag"] = Js.ToJsonElement(consRow.DiagCodes[0]) });
        Assert.True(rzhCore.GroupTypeCode != "ZH",
            $"核心可入组时不落综合病组: 实际 {rzhCore.GroupTypeCode}#{rzhCore.Seq}");

        // 未覆盖类目 → 综合病组兜底
        var rzh = g.Group(new Dictionary<string, JsonElement> { ["mainDiag"] = Js.ToJsonElement($"{zhCat}.000x001") });
        Assert.True(rzh.Matched);
        Assert.Equal("ZH", rzh.GroupTypeCode);
        Assert.Equal(9002, rzh.Seq);
        Assert.Equal(zhCat, rzh.Detail["category"]?.ToString());

        // 排除诊断（A00.000 霍乱）也落入综合病组
        var rzhEx = g.Group(new Dictionary<string, JsonElement> { ["mainDiag"] = Js.ToJsonElement("A00.000") });
        Assert.True(rzhEx.Matched);
        Assert.Equal("ZH", rzhEx.GroupTypeCode);
        Assert.True(rzhEx.Detail.TryGetValue("viaExcluded", out var via) && via is true);

        // 范围区间命中: A16 落入病种库区间行 A15-A16（无主手术 → 保守行 1837, 综合病组）
        var rzhRange = NoJiceng().Group(new Dictionary<string, JsonElement> { ["mainDiag"] = Js.ToJsonElement("A16.000x001") });
        Assert.Equal("ZH", rzhRange.GroupTypeCode);
        Assert.Equal(1837, rzhRange.Seq);
        var diagCodes = Assert.IsType<List<string>>(rzhRange.Detail["diagCodes"]);
        Assert.Equal(new List<string> { "A15-A16" }, diagCodes);

        // 热更新: 清空综合病组后未覆盖类目不再入组
        g.SetZonghe(new List<ZongheRow>());
        var rzhEmpty = g.Group(new Dictionary<string, JsonElement> { ["mainDiag"] = Js.ToJsonElement($"{zhCat}.000x001") });
        Assert.False(rzhEmpty.Matched);
    }
}
