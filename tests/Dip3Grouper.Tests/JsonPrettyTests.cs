using Dip3Grouper;
using Xunit;

namespace Dip3Grouper.Tests;

/// <summary>
/// JsonPretty —— 对齐 JavaScript <c>JSON.stringify(value, null, n)</c> 的缩进重排。
/// 数据文件落盘格式需逐字节一致，否则 Node 版与 C# 版写出的 data/*.json 会出现
/// 无语义差异但体积/可读性不同的"假差异"。
/// </summary>
public class JsonPrettyTests
{
    [Fact]
    public void 对象_1空格缩进()
    {
        Assert.Equal("{\n \"a\": 1,\n \"b\": 2\n}", JsonPretty.Format("""{"a":1,"b":2}""", 1));
    }

    [Fact]
    public void 嵌套对象_逐层递增缩进()
    {
        var expect = string.Join("\n", new[]
        {
            "{",
            " \"sections\": {",
            "  \"s01\": {",
            "   \"label\": \"器官移植\"",
            "  }",
            " }",
            "}",
        });
        Assert.Equal(expect, JsonPretty.Format("""{"sections":{"s01":{"label":"器官移植"}}}""", 1));
    }

    [Fact]
    public void 数组_元素换行缩进()
    {
        Assert.Equal("{\n \"a\": [\n  1,\n  2\n ]\n}", JsonPretty.Format("""{"a":[1,2]}""", 1));
    }

    [Fact]
    public void 空对象与空数组_不换行()
    {
        Assert.Equal("{\n \"a\": {},\n \"b\": []\n}", JsonPretty.Format("""{"a":{},"b":[]}""", 1));
        Assert.Equal("{}", JsonPretty.Format("{}", 1));
        Assert.Equal("[]", JsonPretty.Format("[]", 1));
    }

    [Fact]
    public void 数组内嵌套对象()
    {
        var expect = string.Join("\n", new[]
        {
            "[",
            " {",
            "  \"a\": 1",
            " },",
            " {",
            "  \"a\": 2",
            " }",
            "]",
        });
        Assert.Equal(expect, JsonPretty.Format("""[{"a":1},{"a":2}]""", 1));
    }

    [Fact]
    public void 二维数组()
    {
        Assert.Equal("[\n [\n  1\n ],\n [\n  2\n ]\n]", JsonPretty.Format("[[1],[2]]", 1));
    }

    [Fact]
    public void 缩进2空格_用于config_meta()
    {
        Assert.Equal("{\n  \"a\": 1\n}", JsonPretty.Format("""{"a":1}""", 2));
    }

    [Fact]
    public void 转义与Unicode_原样透传()
    {
        // 字符串内容不应被改写（含 \" \\ \n 转义与中文字面量）
        var compact = "{\"a\":\"x\\\"y\\\\z\\n中\",\"b\":null,\"c\":true}";
        var expect = "{\n \"a\": \"x\\\"y\\\\z\\n中\",\n \"b\": null,\n \"c\": true\n}";
        Assert.Equal(expect, JsonPretty.Format(compact, 1));
    }

    [Fact]
    public void 科学计数与小数_原样透传()
    {
        Assert.Equal("[\n 1.5,\n 1e+30,\n -0.25\n]", JsonPretty.Format("[1.5,1e+30,-0.25]", 1));
    }

    [Fact]
    public void 顶层标量()
    {
        Assert.Equal("5", JsonPretty.Format("5", 1));
        Assert.Equal("null", JsonPretty.Format("null", 1));
    }

    [Fact]
    public void 重排后仍是等价JSON()
    {
        const string compact = """{"sections":{"s01":{"label":"器官移植","rows":[{"id":1,"name":"a"},{"id":2,"name":"b"}],"empty":[],"nil":null}},"n":0}""";
        var pretty = JsonPretty.Format(compact, 1);
        Assert.Equal(compact, System.Text.Json.JsonSerializer.Serialize(
            System.Text.Json.JsonDocument.Parse(pretty).RootElement,
            new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    [Fact]
    public void indent为零_保持紧凑()
    {
        Assert.Equal("""{"a":1}""", JsonPretty.Format("""{"a":1}""", 0));
    }

    // ------------------------------------------------------------------
    // JsStringEncoder: 必须与 JavaScript JSON.stringify 的转义策略一致
    // ------------------------------------------------------------------
    private static string Enc(string s)
        => System.Text.Json.JsonSerializer.Serialize(s,
            new System.Text.Json.JsonSerializerOptions { Encoder = JsStringEncoder.Instance });

    [Fact]
    public void 编码器_补充平面字符不转义()
    {
        // U+2C317「𬌗」：Node 写原始 UTF-8；UnsafaRelaxed 会写成 \uD870\uDF17，必须避免
        Assert.Equal("\"错𬌗畸形\"", Enc("错𬌗畸形"));
    }

    [Fact]
    public void 编码器_引号与反斜杠用短转义()
    {
        Assert.Equal("\"a\\\"b\\\\c\"", Enc("a\"b\\c"));
    }

    [Fact]
    public void 编码器_控制字符()
    {
        Assert.Equal("\"\\b\\f\\n\\r\\t\"", Enc("\b\f\n\r\t"));
        Assert.Equal("\"\\u0001\\u001f\"", Enc("\u0001\u001f"));
    }

    [Fact]
    public void 编码器_DEL等非控制字符不转义()
    {
        Assert.Equal("\"\u007f\u2028\u2029\"", Enc("\u007f\u2028\u2029"));
    }
}
