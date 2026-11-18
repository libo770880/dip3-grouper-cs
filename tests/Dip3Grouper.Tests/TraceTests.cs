using System.Text.Json;
using Dip3Grouper;
using Xunit;

namespace Dip3Grouper.Tests;

/// <summary>
/// 分组溯源测试：
/// ① 溯源结论必须与分组结果一致（命中层 = 结果层，行号 = 结果行号）——
///    溯源是排障依据，错了比没有更糟，故用全部 44 条既有用例跑一致性；
/// ② trace 不得进入 /api/group 的 JSON 契约（与 Node 版逐字节一致是对拍基线）；
/// ③ 未入组时每层都要给出「卡在哪一步」的依据。
/// </summary>
public class TraceTests
{
    private static readonly Dip3Config Cfg = Js.ReadJsonFile<Dip3Config>(AppPaths.ConfigFile)!;
    private static readonly List<ZongheRow> ZongheRows = Js.ReadJsonFile<ZongheFile>(AppPaths.ZongheFile)?.Rows ?? new List<ZongheRow>();

    private static Dip3Grouper NewGrouper(bool tryJiceng = true)
    {
        var g = new Dip3Grouper(Cfg, tryJiceng);
        g.SetZonghe(ZongheRows);
        return g;
    }

    private static Dip3Grouper NoJiceng() => NewGrouper(false);

    public static IEnumerable<object[]> Cases() => GroupingTests.Cases();

    [Theory]
    [MemberData(nameof(Cases))]
    public void 溯源与结果一致(string name, string kaseJson, string _)
    {
        var g = NewGrouper();
        var kase = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(kaseJson)!;
        var r = g.Group(Dip3Grouper.NormalizeCase(kase), withTrace: true);

        Assert.NotNull(r.Trace);
        Assert.NotEmpty(r.Trace.Steps);

        var hitSteps = r.Trace.Steps.Where(s => s.Status == "hit").ToList();
        if (r.Matched)
        {
            Assert.True(hitSteps.Count == 1, $"{name}: 命中层应恰为 1 个，实际 {hitSteps.Count}");
            var hit = hitSteps[0];
            Assert.Equal(r.GroupTypeCode, r.Trace.HitLayer);
            Assert.Equal(r.Seq, hit.Seq);
            Assert.False(string.IsNullOrEmpty(hit.Title), $"{name}: 命中层应给出结论");
            Assert.False(string.IsNullOrEmpty(hit.Name), $"{name}: 命中层应给出病种名");
        }
        else
        {
            Assert.True(hitSteps.Count == 0, $"{name}: 未入组却出现命中层");
            Assert.Null(r.Trace.HitLayer);
            Assert.False(string.IsNullOrEmpty(r.Reason), $"{name}: 未入组应给出原因");
        }

        // 层顺序必须与优先级一致（出现的子序列）
        var order = new List<int>();
        foreach (var s in r.Trace.Steps)
        {
            var idx = s.LayerCode switch
            {
                "EXCL" => 0, "XQ" => 1, "FZ" => 2, "BX" => 3, "JC" => 4, "JICENG" => 5, "ZH" => 6,
                _ => -1,
            };
            Assert.True(idx >= 0, $"{name}: 未知层代码 {s.LayerCode}");
            Assert.True(order.Count == 0 || idx > order[^1], $"{name}: 层顺序错乱 {string.Join(",", order)} → {s.LayerCode}");
            order.Add(idx);
        }
    }

    [Fact]
    public void trace_不进JSON契约()
    {
        var g = NewGrouper();
        var r = g.Group(Dip3Grouper.NormalizeCase(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"mainDiag":"C02.900","mainProc":"25.2x00x001"}""")!), withTrace: true);
        Assert.NotNull(r.Trace);

        var json = JsonSerializer.Serialize(r, Js.Http);
        Assert.DoesNotContain("trace", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("溯源", json, StringComparison.OrdinalIgnoreCase);

        // 对照：无 trace 请求时结果体与原有路径逐字节一致
        var plain = g.Group(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"mainDiag":"C02.900","mainProc":"25.2x00x001"}""")!);
        var saved = r.Trace;
        r.Trace = null;
        Assert.Equal(JsonSerializer.Serialize(plain, Js.Http), JsonSerializer.Serialize(r, Js.Http));
        r.Trace = saved;
    }

    [Fact]
    public void trace_默认关闭_不产生开销()
    {
        var g = NewGrouper();
        var r = g.Group(Dip3Grouper.NormalizeCase(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"mainDiag":"A01.000"}""")!));
        Assert.Null(r.Trace);
    }

    [Fact]
    public void 未入组_给出逐层卡点()
    {
        // 化疗主诊断 + 其他诊断给了阑尾炎 → 肿瘤层「其他诊断缺恶性肿瘤编码」
        var g = NewGrouper();
        var r = g.Group(Dip3Grouper.NormalizeCase(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """{"mainDiag":"Z51.100x002","mainProc":"99.2503","otherDiags":"K35.900"}""")!), withTrace: true);
        Assert.False(r.Matched);
        Assert.Equal(7, r.Trace.Steps.Count);

        var fz = r.Trace.Steps.First(s => s.LayerCode == "FZ");
        Assert.Equal("miss", fz.Status);
        Assert.Contains(fz.Facts, f => f.Tone == "warn" && f.Value.Contains("恶性肿瘤"));
    }

    [Fact]
    public void 排除判定_主诊断不纳入_后续层跳过()
    {
        var g = NoJiceng();
        var r = g.Group(Dip3Grouper.NormalizeCase(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"mainDiag":"A00.000"}""")!), withTrace: true);
        Assert.False(r.Matched);

        var ex = r.Trace.Steps.First(s => s.LayerCode == "EXCL");
        Assert.Equal("warn", ex.Status);
        Assert.All(r.Trace.Steps.Where(s => s.LayerCode is "FZ" or "BX" or "JC"),
            s => Assert.Equal("skip", s.Status));
    }

    [Fact]
    public void 主手术被排除_按保守治疗_溯源点明()
    {
        var g = NewGrouper();
        var r = g.Group(Dip3Grouper.NormalizeCase(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """{"mainDiag":"A09.901","mainProc":"44.1300x001"}""")!), withTrace: true);
        Assert.True(r.Matched);
        Assert.Equal("JC", r.Trace.HitLayer);

        var ex = r.Trace.Steps.First(s => s.LayerCode == "EXCL");
        Assert.Equal("warn", ex.Status);
        Assert.Contains(ex.Facts, f => f.Tone == "warn" && f.Value.Contains("保守治疗"));

        var jc = r.Trace.Steps.First(s => s.LayerCode == "JC");
        Assert.Equal("hit", jc.Status);
        Assert.Contains(jc.Facts, f => f.Value.Contains("保守治疗"));
    }

    [Fact]
    public void 结核区间行_溯源点明归入综合病组()
    {
        var g = NewGrouper();
        var r = g.Group(Dip3Grouper.NormalizeCase(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """{"mainDiag":"A15.000x010","mainProc":"31.9400"}""")!), withTrace: true);
        Assert.True(r.Matched);
        Assert.Equal("ZH", r.GroupTypeCode);

        var hit = r.Trace.Steps.First(s => s.Status == "hit");
        Assert.Equal("FZ", hit.LayerCode);            // 实际命中的是诊断辅助细分层
        Assert.Equal(1812, hit.Seq);
        Assert.Contains(hit.Facts, f => f.Label == "归入组别" && f.Value.Contains("综合病组"));
    }
}
