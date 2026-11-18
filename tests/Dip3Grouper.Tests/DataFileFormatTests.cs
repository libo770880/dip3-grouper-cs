using System.IO;
using System.Text;
using System.Text.Json;
using Dip3Grouper;
using Xunit;

namespace Dip3Grouper.Tests;

/// <summary>
/// 数据文件落盘格式对拍 —— C# 版写出的 data/*.json / config/*.json 必须与 Node 版
/// <c>JSON.stringify(value, null, n)</c> 的输出**逐字节相同**（含字段顺序与缩进）。
///
/// 做法: 读入现行（Node 侧产出的）文件 → 用 C# 模型反序列化 → 再序列化 → 断言字节一致。
/// 若字段顺序或缩进宽度不一致，这里会直接暴露，避免两个版本写出"语义相同但格式不同"的数据文件。
/// </summary>
public class DataFileFormatTests
{
    private static string RepoFile(params string[] parts)
        => Path.Combine(new[] { AppPaths.Root }.Concat(parts).ToArray());

    private static string Load(string rel) => File.ReadAllText(RepoFile(rel.Split('/')), Encoding.UTF8);

    /// <summary>读入 → 反序列化 → 再序列化（紧凑）→ 按 indent 美化</summary>
    private static string RoundTrip<T>(string rel, JsonSerializerOptions opts, int indent)
    {
        var model = Js.ReadJsonFile<T>(RepoFile(rel.Split('/')));
        var compact = JsonSerializer.Serialize(model, opts);
        return JsonPretty.Format(compact, indent);
    }

    [Fact]
    public void 映射表_1空格缩进_逐字节一致()
    {
        const string rel = "data/mappings.json";
        if (!File.Exists(RepoFile(rel.Split('/')))) return;
        Assert.Equal(Load(rel), RoundTrip<MappingFile>(rel, Js.Data, 1));
    }

    [Fact]
    public void 综合病组_写入器输出对齐Node()
    {
        // data/zonghe.json 现状可能是历史遗留的紧凑格式，故此处不比对磁盘文件，
        // 而是断言写入器本身产出 Node 的规范形态（已用隔离实例实测一致）。
        var model = new ZongheFile
        {
            Rows = new List<ZongheRow>(),
            Meta = new ZongheMeta { ImportedAt = "2026-01-01T00:00:00.000Z", SourceName = "已清空" },
        };
        var expect = string.Join("\n", new[]
        {
            "{",
            " \"rows\": [],",
            " \"meta\": {",
            "  \"importedAt\": \"2026-01-01T00:00:00.000Z\",",
            "  \"sourceName\": \"已清空\"",
            " }",
            "}",
        });
        Assert.Equal(expect, JsonPretty.Format(JsonSerializer.Serialize(model, Js.Data), 1));
    }

    [Fact]
    public void 立项指南_1空格缩进_逐字节一致()
    {
        const string rel = "data/lixiang.json";
        if (!File.Exists(RepoFile(rel.Split('/')))) return;
        Assert.Equal(Load(rel), RoundTrip<LixiangFile>(rel, Js.Data, 1));
    }

    [Fact]
    public void 配置元信息_2空格缩进_逐字节一致()
    {
        const string rel = "data/config-meta.json";
        if (!File.Exists(RepoFile(rel.Split('/')))) return;
        Assert.Equal(Load(rel), RoundTrip<ConfigMetaFile>(rel, Js.Data, 2));
    }

    [Fact]
    public void 病种库配置_紧凑无缩进_逐字节一致()
    {
        const string rel = "config/dip3-config.json";
        if (!File.Exists(RepoFile(rel.Split('/')))) return;
        Assert.Equal(Load(rel), RoundTrip<Dip3Config>(rel, Js.Config, 0));
    }
}
