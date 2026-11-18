using System;
using System.Text;
using Dip3Grouper;
using Xunit;

namespace Dip3Grouper.Tests;

/// <summary>
/// 立项指南（表格导入/解析）与文档转文本的单元测试。
/// 重点覆盖:
///   1. 模板「下载 → 填写 → 导入」往返 —— sheet 名为区段标签时必须复用既有 tab，不得新建 x_* 区段
///      （原 Node 版在此处把 40 个区段翻倍成 80 个，见 tools/compare-write-api.mjs 对拍记录）
///   2. doc2txt / DocToText 的换行与控制字符清洗语义
/// </summary>
public class LixiangTests
{
    private static readonly LixiangStore Store = NewStore();

    private static LixiangStore NewStore()
    {
        var s = new LixiangStore();
        s.Load();
        return s;
    }

    private static List<List<string>> SampleAoa() => new()
    {
        LixiangStore.Columns.Select(c => c.Label).ToList(),
        new List<string> { "1", "往返导入测试项目", "", "次", "", "", "", "", "" },
    };

    // ------------------------------------------------------------------
    // 一、sheet 名 → tab 回落
    // ------------------------------------------------------------------
    [Fact]
    public void 模板往返导入_标签式sheet名_应复用既有tab()
    {
        var firstTab = Store.State.Sections.Keys.First();
        var label = Store.State.Sections[firstTab].Label;

        var (sections, report) = Store.ParseWorkbook(new[] { label },
            new Dictionary<string, List<List<string>>> { [label] = SampleAoa() });

        Assert.Equal(new[] { firstTab }, sections.Keys);
        Assert.Equal(label, sections[firstTab].Label);
        Assert.Single(sections[firstTab].Rows);
        Assert.True(report.ContainsKey(label));
    }

    [Fact]
    public void 模板往返导入_全量40区段_不产生新区段()
    {
        // 模拟「下载全量模板 → 原样导入」：区段数必须保持不变
        var before = Store.State.Sections.Count;
        var names = new List<string>();
        var aoas = new Dictionary<string, List<List<string>>>();
        foreach (var sec in Store.State.Sections.Values)
        {
            names.Add(sec.Label);
            aoas[sec.Label] = SampleAoa();
        }

        var (sections, _) = Store.ParseWorkbook(names, aoas);

        Assert.Equal(before, sections.Count);
        // 全部应回落到既有 tab（无 x_* 前缀键）
        Assert.DoesNotContain(sections.Keys, k => k.StartsWith("x_"));
    }

    [Fact]
    public void 序号前缀sheet名_仍按序号映射()
    {
        var name = "01-任意标签立项指南项目";
        var (sections, _) = Store.ParseWorkbook(new[] { name },
            new Dictionary<string, List<List<string>>> { [name] = SampleAoa() });

        Assert.Equal(new[] { "s01" }, sections.Keys);
        Assert.Equal("任意标签", sections["s01"].Label);
    }

    [Fact]
    public void 未知标签sheet名_走x_兜底()
    {
        var name = "不存在的区段XYZ";
        var (sections, _) = Store.ParseWorkbook(new[] { name },
            new Dictionary<string, List<List<string>>> { [name] = SampleAoa() });

        Assert.Equal(new[] { "x_不存在的区段XYZ" }, sections.Keys);
    }

    // ------------------------------------------------------------------
    // 二、文档转文本
    // ------------------------------------------------------------------
    [Fact]
    public void DocToText_txt_CRLF归一与BOM剥离()
    {
        var raw = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes("第一行\r\n第二行\r第三行")).ToArray();

        var text = DocToText.Extract(raw, ".txt");

        // \r → \n，BOM 不残留
        Assert.Equal("第一行\n\n第二行\n第三行", text);
        Assert.DoesNotContain("\r", text);
        // 注意: string.StartsWith 默认文化敏感比较，U+FEFF 属"可忽略字符"，
        // 会与任意字符串前缀相等，故必须用 Ordinal。
        Assert.False(text.StartsWith("\uFEFF", StringComparison.Ordinal));
    }

    [Fact]
    public void DocToText_控制字符清洗_保留换行与制表()
    {
        // 0x07 → \t，0x0b → \n，其余 C0 控制符（除 \t\n）应被删除
        // 注意: C# 的 \x 转义会贪婪吸收后续十六进制字符（"\x07B" 会被解析成 U+07B），
        // 故此处一律使用定长 \u 转义。
        var raw = Encoding.UTF8.GetBytes("A\u0007B\u000bC\u0001D\u0002E\nF");

        var text = DocToText.Extract(raw, ".txt");

        Assert.Equal("A\tB\nCDE\nF", text);
    }

    [Fact]
    public void DocToText_未知扩展名按二进制doc处理_非法输入应抛错()
    {
        Assert.ThrowsAny<Exception>(() => DocToText.Extract(Encoding.UTF8.GetBytes("not a doc"), ".doc"));
    }

    // ------------------------------------------------------------------
    // 三、解读辅导文本解析
    // ------------------------------------------------------------------
    [Fact]
    public void ParseJieduText_按期分块_题号与答案切分正确()
    {
        var text = string.Join("\n", new[]
        {
            "前言说明段落。",
            "医疗服务价格项目立项指南解读辅导（第1期）",
            "1.第一个问题是什么？",
            "这是第一个答案。",
            "2.第二个问题是什么？",
            "这是第二个答案。",
        });

        var (preface, list) = LixiangStore.ParseJieduText(text);

        Assert.Contains("前言说明段落", preface);
        Assert.Equal(2, list.Count);
        Assert.Equal(1, list[0].Issue);
        Assert.Equal(1, list[0].No);
        Assert.Equal("第一个问题是什么？", list[0].Q);
        Assert.Equal("第二个问题是什么？", list[1].Q);
        Assert.Equal("这是第二个答案。", list[1].A);

        // 已知（Node/C# 一致的）边界行为: 非末题的答案尾部会多带下一题题号的
        // 前若干字符（此处为 "\n2."）。这是原 Node 版 slice 边界算法的固有结果，
        // C# 侧刻意保持逐字一致（见 tools/compare-write-api.mjs 对拍），未作"修正"，
        // 以免改变既有 data/lixiang.json 中已导入的 224 条问答内容。
        Assert.Equal("这是第一个答案。\n2.", list[0].A);
        Assert.StartsWith("这是第一个答案。", list[0].A);
    }

    [Fact]
    public void ParseJieduText_无期号_全部归入前言且无条目()
    {
        var (preface, list) = LixiangStore.ParseJieduText("只有一段没有期号的文本。");
        Assert.Equal("只有一段没有期号的文本。", preface);
        Assert.Empty(list);
    }
}
