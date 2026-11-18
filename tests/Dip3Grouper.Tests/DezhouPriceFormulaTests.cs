using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Dip3Grouper;
using Xunit;

namespace Dip3Grouper.Tests;

/// <summary>
/// 德州物价「加收项公式价」换算规则的单元测试。
///
/// 背景：征求意见稿里加收项（儿童加收、内穿刺加收…）的价格列是 Excel 公式
/// <c>ROUND(G42*0.2,0)</c> = 主项同档价 × 加收比例、四舍五入取整。数据里公式原样保留，
/// 但**前台表格 / 详情卡 / 新旧比对 / 导出**必须显示金额——否则物价人员看到的是机器文本。
/// 主项定位规则：加收项编码 = 主项编码前 12 位 + 000。
/// </summary>
public class DezhouPriceFormulaTests
{
    private static DezhouPriceItem Item(string code, string name, string p3, string p2 = "", string p1 = "",
        string kind = "main") => new()
    {
        Code = code, Name = name, Price3 = p3, Price2 = p2, Price1 = p1, Kind = kind,
    };

    /// <summary>用合成条目建库（不落盘）：主项 1000/950/870 + 三个加收项。</summary>
    private static DezhouPriceStore NewStore()
    {
        var s = new DezhouPriceStore();
        s.Items.Clear();
        s.Items.AddRange(new[]
        {
            Item("013310000010000", "无创胆管外引流费", "1000", "950", "870"),
            Item("013310000010001", "无创胆管外引流费-儿童（加收）",
                "ROUND(G42*0.2,0)", "ROUND(H42*0.2,0)", "ROUND(I42*0.2,0)", "add"),
            Item("013310000010002", "无创胆管外引流费-自主定价（加收）", "自主定价", "", "", "add"),
            // 主项缺失：只能在别的编码段里找不到 000 主项
            Item("099990000010001", "无主项加收", "ROUND(G9*0.2,0)", "", "", "add"),
        });
        return s;
    }

    [Fact]
    public void 加收项公式价_按主项同档价乘比例换算()
    {
        var s = NewStore();
        var it = s.Items.First(i => i.Code == "013310000010001");

        Assert.Equal("200", s.ResolvePrice(it, "price3"));   // 1000 × 0.2
        Assert.Equal("190", s.ResolvePrice(it, "price2"));   // 950 × 0.2
        Assert.Equal("174", s.ResolvePrice(it, "price1"));   // 870 × 0.2
        Assert.Equal(("200", "190", "174"), s.ResolvePrices(it));

        // 展示口径：一律金额，不得残留公式
        Assert.Equal("200", s.PriceOf(it, "price3"));
        Assert.False(DezhouPriceStore.IsFormulaPrice(s.PriceOf(it, "price3")));
    }

    [Fact]
    public void 四舍五入_采用远离零的银行家反向规则()
    {
        var s = NewStore();
        // 主项三级价 1005 × 0.2 = 201（整数，不涉及半值）；这里专测 .5 边界：主项 55 × 0.5 = 27.5 → 28
        s.Items.Clear();
        s.Items.Add(Item("013310000020000", "主项", "55"));
        s.Items.Add(Item("013310000020001", "半值加收", "ROUND(G2*0.5,0)", "", "", "add"));

        Assert.Equal("28", s.ResolvePrice(s.Items[1], "price3"));
    }

    [Fact]
    public void 普通价格与文本价_原样返回且不带换算说明()
    {
        var s = NewStore();
        var main = s.Items[0];
        var text = s.Items.First(i => i.Code == "013310000010002");

        Assert.Equal("1000", s.PriceOf(main, "price3"));
        Assert.Null(s.PriceNote(main, "price3"));

        Assert.Equal("自主定价", s.PriceOf(text, "price3"));
        Assert.Null(s.PriceNote(text, "price3"));
    }

    [Fact]
    public void 主项缺失时_展示为破折号而不是公式()
    {
        var s = NewStore();
        var orphan = s.Items.First(i => i.Code == "099990000010001");

        Assert.Equal("—", s.PriceOf(orphan, "price3"));
        var note = s.PriceNote(orphan, "price3");
        Assert.NotNull(note);
        Assert.Contains("未能匹配主项", note);
    }

    [Fact]
    public void 公式价悬停说明_讲清主项_比例与结果()
    {
        var s = NewStore();
        var it = s.Items.First(i => i.Code == "013310000010001");
        var note = s.PriceNote(it, "price3");

        Assert.NotNull(note);
        Assert.Contains("013310000010000", note);
        Assert.Contains("20%", note);
        Assert.Contains("→ 200", note);
        Assert.Contains("已自动换算", note);
    }

    [Fact]
    public void 主项自身也是公式时_没有更上层主项即视为缺失()
    {
        // 编码规则决定「加收项 → 主项」只有一层（前 12 位 + 000 的主项自己不再有主项），
        // 因此公式套公式在真实数据里不会出现；真出现时按「主项缺失」处理，绝不把公式当金额。
        var s = NewStore();
        s.Items.Clear();
        s.Items.Add(Item("013310000040000", "主项", "ROUND(G1*0.5,0)"));
        s.Items.Add(Item("013310000040001", "加收", "ROUND(G2*0.5,0)", "", "", "add"));

        Assert.Equal("—", s.PriceOf(s.Items[1], "price3"));
        Assert.Contains("未能匹配主项", s.PriceNote(s.Items[1], "price3"));
    }

    [Fact]
    public void 真实数据_公式价若存在必须全部可换算且比对行无公式残留()
    {
        // 德医保发〔2026〕25号 正式版已把 379 个公式格替换成固定金额（暂定 24 号文批次整批撤下），
        // 真实数据的公式价数量可以从「有」变成「0」——这条断言必须允许 0，
        // 但同时守住"只要还有一个公式格，就必须全部换算成金额、比对行不得残留公式"。
        var s = new DezhouPriceStore();
        var formula = s.Items.Where(i => DezhouPriceStore.IsFormulaPrice(i.Price3)
                                         || DezhouPriceStore.IsFormulaPrice(i.Price2)
                                         || DezhouPriceStore.IsFormulaPrice(i.Price1)).ToList();

        foreach (var it in formula)
        {
            Assert.NotNull(s.BaseItemOf(it));
            foreach (var key in new[] { "price3", "price2", "price1" })
            {
                var shown = s.PriceOf(it, key);
                Assert.False(DezhouPriceStore.IsFormulaPrice(shown));
                // 主项缺失时展示破折号，其余必须是纯数字金额
                Assert.True(Regex.IsMatch(shown ?? "", @"^(\d+|—)$"), $"{it.Code}/{key} 展示值异常「{shown}」");
            }
        }

        var leftover = s.CompareRows()
            .Count(r => DezhouPriceStore.IsFormulaPrice(r.NewPrice3)
                        || DezhouPriceStore.IsFormulaPrice(r.NewPrice2)
                        || DezhouPriceStore.IsFormulaPrice(r.NewPrice1));
        Assert.Equal(0, leftover);

        // 真实数据的价格格不得出现公式文本（空白 / 数字 / 「自主定价」这类文字口径均可）
        var anyFormulaShown = s.Items.Any(i => new[] { "price3", "price2", "price1" }
            .Any(k => DezhouPriceStore.IsFormulaPrice(s.PriceOf(i, k))));
        Assert.False(anyFormulaShown, "PriceOf 把公式文本直接暴露给了前台");
    }

    // ==================================================================
    // 医院级别筛选（三级 / 二级 / 一级 = 价格档位，不是三条独立记录）
    // ==================================================================

    private static DezhouPriceStore LevelStore()
    {
        var s = new DezhouPriceStore();
        s.Items.Clear();
        s.Items.AddRange(new[]
        {
            Item("000000000010000", "三档齐全", "100", "90", "80"),
            Item("000000000020000", "仅一级有价", "", "", "70"),
            Item("000000000030000", "各级同价", "50", "50", "50"),
            Item("000000000040000", "三档皆空", "", "", ""),
            Item("000000000050000", "文字价", "自主定价", "自主定价", "自主定价"),
        });
        return s;
    }

    [Theory]
    [InlineData("3", "000000000010000,000000000030000,000000000050000")]
    [InlineData("2", "000000000010000,000000000030000,000000000050000")]
    [InlineData("1", "000000000010000,000000000020000,000000000030000,000000000050000")]
    [InlineData("eq", "000000000030000,000000000050000")]
    [InlineData("", "000000000010000,000000000020000,000000000030000,000000000040000,000000000050000")]
    public void 医院级别筛选_只保留该档有价的项目(string level, string expectCodes)
    {
        var s = LevelStore();
        var got = s.ListRows("", "", "", "", 1, 50, "", null, level);
        Assert.Equal(expectCodes, string.Join(",", got.Items.Select(i => i.Code)));
    }

    [Fact]
    public void 医院级别取价_按档取且回落到三级()
    {
        var it = Item("000000000010000", "x", "100", "90", "80");
        var s = LevelStore();
        Assert.Equal("90", s.PriceAtLevel(it, "2"));
        Assert.Equal("80", s.PriceAtLevel(it, "1"));
        Assert.Equal("100", s.PriceAtLevel(it, ""));      // 不限 / 同价 → 三级价
        Assert.Equal("100", s.PriceAtLevel(it, "eq"));
        Assert.Equal("三级", DezhouPriceStore.LevelName("3"));
        Assert.Equal("", DezhouPriceStore.LevelName(""));
    }

    [Fact]
    public void 真实数据_级别筛选与三档价自洽()
    {
        var s = new DezhouPriceStore();
        var all = s.ListRows("", "", "", "", 1, 1).Total;
        Assert.True(all > 0, "真实物价库为空，跳过意义不大");
        foreach (var (lv, pick) in new (string, Func<DezhouPriceItem, string>)[]
                 {
                     ("3", i => i.Price3), ("2", i => i.Price2), ("1", i => i.Price1),
                 })
        {
            var hits = s.Items.Count(i => DezhouPriceStore.HasLevel(i, lv));
            var rows = s.ListRows("", "", "", "", 1, 1, "", null, lv).Total;
            Assert.Equal(hits, rows);
            Assert.DoesNotContain(s.Items.Where(i => DezhouPriceStore.HasLevel(i, lv)),
                i => (pick(i) ?? "").Trim().Length == 0);
        }
        // 三档皆空的行只能出现在「不限」里：受限级别下的行数必须严格小于全量
        var blank = s.Items.Count(i => !DezhouPriceStore.HasLevel(i, "3")
                                    && !DezhouPriceStore.HasLevel(i, "2")
                                    && !DezhouPriceStore.HasLevel(i, "1"));
        if (blank > 0)
            foreach (var lv in new[] { "3", "2", "1" })
                Assert.True(s.ListRows("", "", "", "", 1, 1, "", null, lv).Total < all,
                    $"存在 {blank} 条三档皆空的行，级别 {lv} 却未过滤掉任何行");
    }

    // ==================================================================
    // 空价加收项：比例写在「计价说明」里（2026-10-27 新增）
    //   25 号文「一般治疗」的 49 条「-儿童（加收）」源表三档价全空，
    //   只在计价说明写「加收比例20%。」。以前这些行前台一直显示「—」，
    //   物价人员以为漏维护。现在求值时按说明里的比例 × 主项同档价兜底。
    // ==================================================================

    private static DezhouPriceItem NoteAdd(string code, string name, string note) => new()
    {
        Code = code, Name = name, Kind = "add", Usage = note,
    };

    private static DezhouPriceStore NoteStore()
    {
        var s = new DezhouPriceStore();
        s.Items.Clear();
        s.Items.AddRange(new[]
        {
            Item("011203000020000", "采血费（静脉）", "6", "6", "5.5"),
            NoteAdd("011203000020001", "采血费（静脉）-儿童（加收）", "加收比例20%。"),
            // 说明里没有比例：不是加收公式，保持空（不该凭空造出价格）
            NoteAdd("011203000020002", "采血费（静脉）-其他（加收）", "本项目所称的“次”指：一次完整成功的采血过程。"),
            // 主项不存在：有比例也算不出来
            NoteAdd("099990000010001", "无主项加收", "加收比例20%。"),
        });
        return s;
    }

    [Fact]
    public void 空价加收项_按计价说明的比例乘主项同档价()
    {
        var s = NoteStore();
        var it = s.Items.First(i => i.Code == "011203000020001");

        // 6×0.2=1.2→1；6×0.2→1；5.5×0.2=1.1→1
        Assert.Equal("1", s.ResolvePrice(it, "price3"));
        Assert.Equal("1", s.ResolvePrice(it, "price2"));
        Assert.Equal("1", s.ResolvePrice(it, "price1"));

        // 展示口径：必须是金额，不能还是空 / 「—」
        Assert.Equal("1", s.PriceOf(it, "price3"));
        Assert.False(s.IsPending(it, s.PriceOf(it, "price3")));
        Assert.True(s.IsPending(it, ""));            // 原始串仍是空的 → 待换算态
        Assert.NotNull(s.PriceNote(it, "price3"));   // 要有「主项 × 比例」的悬停说明
        Assert.Contains("20%", s.PriceNote(it, "price3"));
    }

    [Fact]
    public void 空价加收项_说明无比例时保持空不造价()
    {
        var s = NoteStore();
        var it = s.Items.First(i => i.Code == "011203000020002");
        Assert.Equal("", s.ResolvePrice(it, "price3"));
        Assert.Equal("", s.PriceOf(it, "price3"));
        Assert.False(s.IsPending(it, ""));           // 本来就没价，不算待换算
        Assert.Null(s.PriceNote(it, "price3"));      // 不加气泡
    }

    [Fact]
    public void 空价加收项_主项缺失时不给假价()
    {
        var s = NoteStore();
        var it = s.Items.First(i => i.Code == "099990000010001");

        // 比例识别出来了，但主项（099990000010000）不在库里 → 算不出，必须留空
        Assert.NotNull(DezhouPriceStore.NoteRatio(it));
        Assert.Equal("", s.ResolvePrice(it, "price3"));

        // 展示层给「—」提示人工核对，而不是甩一个假价或空白
        Assert.Equal("—", s.PriceOf(it, "price3"));
        Assert.Contains("未能匹配主项", s.PriceNote(it, "price3"));
    }

    [Fact]
    public void 空价加收项_真实数据里25号文儿童加收全部算出价()
    {
        var s = new DezhouPriceStore();
        var doc25 = s.Items.Where(i => (i.Doc ?? "").Contains("25号")).ToList();
        if (doc25.Count == 0) return;   // 没导入 25 号文时无意义

        var blankAdds = doc25.Where(i => i.Kind == "add"
                                        && (i.Price3 ?? "").Trim() == ""
                                        && DezhouPriceStore.NoteRatio(i) != null).ToList();
        foreach (var it in blankAdds)
        {
            // 三档价要么都有值，要么三档都算不出（主项缺失）—— 不允许「一半有价」的半成品
            var (p3, p2, p1) = s.ResolvePrices(it);
            var got = new[] { p3, p2, p1 }.Count(x => (x ?? "").Trim().Length > 0);
            Assert.True(got == 0 || got == 3,
                $"{it.Code} {it.Name} 只算出 {got}/3 档（{p3}|{p2}|{p1}）");
        }
    }
}
