using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace Dip3Grouper;

/// <summary>
/// 与 JavaScript <c>JSON.stringify</c> 完全对齐的字符串转义器。
///
/// 只转义 <c>"</c> <c>\</c> 与 C0 控制字符（<c>\b \f \n \r \t</c> 用短转义，其余写 <c>\u00xx</c>），
/// 其余字符一律按原样输出 —— 包括中文与**补充平面字符**（如 U+2C317「𬌗」）。
///
/// 为什么不用内置编码器:
///   · <c>JavaScriptEncoder.UnsafeRelaxedJsonEscaping</c> 会把补充平面字符转义成代理对
///     <c>\uD870\uDF17</c>，与 Node 写出的原始 UTF-8 不一致；
///   · <c>JavaScriptEncoder.Create(UnicodeRanges.All)</c> 又把 ASCII 双引号写成 <c>\u0022</c>，
///     同样与 Node 不一致。
/// 两者都会让 C# 版写出的 data/*.json 与 Node 版出现"语义相同但字节不同"的假差异。
/// </summary>
public sealed class JsStringEncoder : JavaScriptEncoder
{
    public static readonly JsStringEncoder Instance = new();

    private JsStringEncoder() { }

    public override int MaxOutputCharactersPerInputCharacter => 6;

    public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
    {
        for (var i = 0; i < textLength; i++)
        {
            var c = text[i];
            if (c == '"' || c == '\\' || c < 0x20) return i;
        }
        return -1;
    }

    public override bool WillEncode(int unicodeScalar)
        => unicodeScalar == '"' || unicodeScalar == '\\' || unicodeScalar < 0x20;

    public override unsafe bool TryEncodeUnicodeScalar(int unicodeScalar, char* buffer, int bufferLength,
        out int numberOfCharactersWritten)
    {
        string s;
        switch (unicodeScalar)
        {
            case '"': s = "\\\""; break;
            case '\\': s = "\\\\"; break;
            case '\b': s = "\\b"; break;
            case '\f': s = "\\f"; break;
            case '\n': s = "\\n"; break;
            case '\r': s = "\\r"; break;
            case '\t': s = "\\t"; break;
            default:
                // 补充平面字符不应走到这里（WillEncode 对其返回 false，原样透传）
                if (unicodeScalar < 0x20) s = "\\u" + unicodeScalar.ToString("x4");
                else { numberOfCharactersWritten = 0; return false; }
                break;
        }
        if (s.Length > bufferLength) { numberOfCharactersWritten = 0; return false; }
        for (var i = 0; i < s.Length; i++) buffer[i] = s[i];
        numberOfCharactersWritten = s.Length;
        return true;
    }
}

/// <summary>
/// 工程根目录定位（数据目录 / 配置目录 / 前端目录）。
/// 优先 DIP3_HOME 环境变量；否则自 AppContext.BaseDirectory 向上查找同时含 config 与 web/index.html 的目录。
/// </summary>
public static class AppPaths
{
    private static string _root;

    public static string Root => _root ??= ResolveRoot();
    public static string DataDir => Path.Combine(Root, "data");
    public static string ConfigDir => Path.Combine(Root, "config");
    public static string WebDir => Path.Combine(Root, "web");
    public static string ToolsDir => Path.Combine(Root, "tools");
    public static string ConfigFile => Path.Combine(ConfigDir, "dip3-config.json");
    public static string MappingFile => Path.Combine(DataDir, "mappings.json");
    public static string ZongheFile => Path.Combine(DataDir, "zonghe.json");
    public static string LixiangFile => Path.Combine(DataDir, "lixiang.json");
    /// <summary>德州市物价（立项指南规范整合项目）</summary>
    public static string DezhouFile => Path.Combine(DataDir, "dezhou-price.json");
    /// <summary>德州市物价「常用收藏」（与主数据分离，重导物价不会丢收藏）</summary>
    public static string DezhouFavFile => Path.Combine(DataDir, "dezhou-favorites.json");
    /// <summary>
    /// 德州市物价「新项目征求意见」稿（尚未正式发布、暂未生效的批次）。
    /// 与主库 <see cref="DezhouFile"/> **完全分离**：征求意见阶段只写这里，
    /// 主库（现行项目 / 作废对照 / 旧版物价）零改动；正式发布后由后台动作合并进主库。
    /// </summary>
    public static string DezhouDraftFile => Path.Combine(DataDir, "dezhou-draft.json");
    /// <summary>全国医疗服务项目技术规范（2023 年版）</summary>
    public static string GuifanFile => Path.Combine(DataDir, "guifan.json");
    /// <summary>组织病理·送检指引（按疾病系统查标本、按蜡块档位定价）</summary>
    public static string ZhijianFile => Path.Combine(DataDir, "zhijian.json");
    /// <summary>基本物耗指引（哪些含在项目价内、哪些可零差率另收）</summary>
    public static string WuhaoFile => Path.Combine(DataDir, "wuhao.json");
    /// <summary>
    /// 基本物耗指引的「本机手工维护」覆盖层（wuhao.user.json）。
    /// 只在本机生效、**不进 DataCatalog 白名单**（绝不下辖发），与 wuhao.json 同目录但完全分离：
    /// 存的是增/改/删的差异集，载入时在内存里盖在 wuhao.json 之上。
    /// </summary>
    public static string WuhaoUserFile => Path.Combine(DataDir, "wuhao.user.json");

    /// <summary>
    /// 「物价解读 AI」的本机私有配置（模型地址 / API Key / 地区）。
    /// 与 <see cref="WuhaoUserFile"/> 同理：只在本机生效、不进 DataCatalog 白名单，
    /// 发布脚本的 KEEPFILES 会保住它，重发布不会把用户自己填的密钥冲掉。
    /// </summary>
    public static string PriceAiFile => Path.Combine(DataDir, "price-ai.json");

    public static string ConfigMetaFile => Path.Combine(DataDir, "config-meta.json");

    /// <summary>前台消息中心（后台推送的通知 / 公告）</summary>
    public static string NoticeFile => Path.Combine(DataDir, "notice.json");

    /// <summary>「本机读过/关掉过哪些消息」的记录（本机私有，不参与数据分发）</summary>
    public static string NoticeReadFile => Path.Combine(DataDir, "notice-read.json");

    /// <summary>应用设置（软件名称 / 主题 / 后台账号口令）</summary>
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");

    /// <summary>解读辅导「上次查过国家医保局原文」的时间与水印（本机私有，不参与数据分发）</summary>
    public static string LixiangCheckFile => Path.Combine(DataDir, "lixiang-check.json");

    public static void SetRoot(string root)
    {
        if (!string.IsNullOrWhiteSpace(root)) _root = Path.GetFullPath(root);
    }

    private static string ResolveRoot()
    {
        var env = Environment.GetEnvironmentVariable("DIP3_HOME");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return Path.GetFullPath(env);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "web", "index.html")) &&
                Directory.Exists(Path.Combine(dir.FullName, "config")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }
}

/// <summary>JSON 序列化选项与读写工具（对齐 Node JSON.stringify 的输出风格：中文不转义、camelCase）。</summary>
public static class Js
{
    /// <summary>反序列化：宽容大小写、允许尾逗号。</summary>
    public static readonly JsonSerializerOptions Read = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>HTTP 响应 / 通用写入：保留 null。</summary>
    public static readonly JsonSerializerOptions Http = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        WriteIndented = false,
        Encoder = JsStringEncoder.Instance,
    };

    /// <summary>数据文件写入（保留 null，紧凑）。</summary>
    public static readonly JsonSerializerOptions Data = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Encoder = JsStringEncoder.Instance,
    };

    /// <summary>病种库配置写入：null 字段省略（对齐 Node 中 undefined 被丢弃的行为）。</summary>
    public static readonly JsonSerializerOptions Config = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Encoder = JsStringEncoder.Instance,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Iso() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    public static string ToJson(object o) => JsonSerializer.Serialize(o, Http);

    public static T FromJson<T>(string json) => JsonSerializer.Deserialize<T>(json, Read);

    /// <summary>把任意值转为独立持有的 JsonElement（脱离原 document 生命周期）。测试工程与分组入口使用。</summary>
    public static JsonElement ToJsonElement(object value)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value, Http));
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// 读 JSON 文件并反序列化。文件不存在返回默认值。
    ///
    /// <para>两处细节都是为了「刚写完 / 刚被替换就立刻重读」这个场景：</para>
    /// <list type="bullet">
    ///   <item>以 <see cref="FileShare.ReadWrite"/> 打开 —— 写入方（<c>File.WriteAllText</c>）只允许
    ///         读共享，用更宽松的共享模式打开可避免绝大多数共享冲突；</item>
    ///   <item>瞬时 IO 异常重试 4 次 —— 杀软实时扫描、网盘同步、Windows 索引会短暂抓住刚生成的文件，
    ///         此时读失败会**静默**退化成空库（或默认设置），一次抖动不该造成这种后果。数据分发中心
    ///         「替换文件 → 立刻 ReloadAll」正是最容易撞上这个窗口的路径。</item>
    /// </list>
    /// </summary>
    public static T ReadJsonFile<T>(string path, int tries = 4)
    {
        if (!System.IO.File.Exists(path)) return default;
        for (var i = 0; ; i++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                return FromJson<T>(sr.ReadToEnd());
            }
            catch (Exception e) when ((e is IOException || e is UnauthorizedAccessException) && i < tries - 1)
            {
                Thread.Sleep(80 * (i + 1));
            }
        }
    }

    /// <summary>
    /// 写数据文件。
    /// <paramref name="indent"/> 逐字节对齐 Node 侧 <c>JSON.stringify(value, null, n)</c> 的缩进宽度：
    ///   data/mappings.json · data/zonghe.json · data/lixiang.json → 1
    ///   data/config-meta.json                                      → 2
    ///   config/dip3-config.json                                    → 0（紧凑，Node 侧就不带缩进参数）
    /// （.NET 8 的 Utf8JsonWriter 只能输出 2 空格缩进，IndentSize 需 .NET 9，故由 JsonPretty 自行重排。）
    /// </summary>
    public static void WriteJsonFile(string path, object o, JsonSerializerOptions opt = null, int indent = 1)
    {
        var compact = JsonSerializer.Serialize(o, opt ?? Data);
        var json = indent > 0 ? JsonPretty.Format(compact, indent) : compact;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // 写侧与读侧同样带瞬时容错：Defender / 索引服务会短暂独占刚落盘的文件，
        // File.Replace / Move 撞上就是 IOException。自检曾因此偶发「写成功却读回旧值」级别的抖动，
        // 与 ReadJsonFile 同一套退避策略（4 次、80ms 递增）。
        for (var i = 0; ; i++)
        {
            try
            {
                var tmp = path + ".tmp";
                System.IO.File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (System.IO.File.Exists(path)) System.IO.File.Replace(tmp, path, null);
                else System.IO.File.Move(tmp, path);
                return;
            }
            catch (Exception e) when ((e is IOException || e is UnauthorizedAccessException) && i < 3)
            {
                Thread.Sleep(80 * (i + 1));
            }
        }
    }
}

/// <summary>文件辅助（原在 Web 侧，导入备份逻辑上提到 Core 后一并移入）。</summary>
public static class FileExt
{
    /// <summary>覆盖复制；失败不抛出（备份属于尽力而为，不应阻断导入主流程）。</summary>
    public static void CopyFileSafe(string src, string dst)
    {
        try { System.IO.File.Copy(src, dst, overwrite: true); } catch { }
    }
}

/// <summary>
/// 把紧凑 JSON 重排为指定缩进宽度的美化格式，逐字节对齐 JavaScript 的
/// <c>JSON.stringify(value, null, n)</c>：
///   · 每层容器缩进 n 个空格；属性/数组元素前换行；逗号后换行
///   · 属性名后是 <c>": "</c>（冒号+一个空格）
///   · 空对象/空数组写作 <c>{}</c> / <c>[]</c>（不换行）
///   · 换行统一为 LF
///   · 字符串/数字/字面量按原始 token 字节原样透传，不重新转义
/// </summary>
public static class JsonPretty
{
    /// <summary>字符串字面量生成（与数据文件一致的转义策略：中文不转义、不转义 HTML 敏感字符）。</summary>
    private static readonly JsonSerializerOptions StrOpts = new()
    {
        Encoder = JsStringEncoder.Instance,
    };

    public static string Format(string compact, int indentSize)
    {
        if (string.IsNullOrEmpty(compact) || indentSize <= 0) return compact;

        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(compact), new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        var sb = new StringBuilder(compact.Length + compact.Length / 3);
        var hasItems = new Stack<bool>();   // 每个已打开的容器"是否已有元素"
        var depth = 0;                      // 已打开的容器数（根容器为 1）
        var expectValue = false;            // 刚写过属性名，下一个 token 是它的值

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                {
                    // 属性名标志着一个「条目」的开始
                    if (hasItems.Peek()) sb.Append(',');
                    else { hasItems.Pop(); hasItems.Push(true); }
                    sb.Append('\n').Append(' ', depth * indentSize);
                    // 注意: PropertyName / String 的 ValueSpan 不含引号，故用序列化器生成字面量
                    sb.Append(JsonSerializer.Serialize(reader.GetString(), StrOpts));
                    sb.Append(": ");
                    expectValue = true;
                    break;
                }

                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                {
                    if (!expectValue && depth > 0) BeginArrayElement(sb, hasItems, depth, indentSize);
                    expectValue = false;
                    sb.Append(reader.TokenType == JsonTokenType.StartObject ? '{' : '[');
                    depth++;
                    hasItems.Push(false);
                    break;
                }

                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                {
                    var had = hasItems.Pop();
                    depth--;
                    if (had) sb.Append('\n').Append(' ', depth * indentSize);
                    sb.Append(reader.TokenType == JsonTokenType.EndObject ? '}' : ']');
                    expectValue = false;
                    break;
                }

                case JsonTokenType.String:
                {
                    if (!expectValue && depth > 0) BeginArrayElement(sb, hasItems, depth, indentSize);
                    expectValue = false;
                    sb.Append(JsonSerializer.Serialize(reader.GetString(), StrOpts));
                    break;
                }

                default:
                {
                    // 数字 / true / false / null：原始字面量直接透传
                    if (!expectValue && depth > 0) BeginArrayElement(sb, hasItems, depth, indentSize);
                    expectValue = false;
                    AppendRaw(sb, in reader);
                    break;
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>数组元素：按需补逗号 + 换行缩进，并把所属容器标记为"已有元素"。</summary>
    private static void BeginArrayElement(StringBuilder sb, Stack<bool> hasItems, int depth, int indentSize)
    {
        if (hasItems.Peek()) sb.Append(',');
        else { hasItems.Pop(); hasItems.Push(true); }
        sb.Append('\n').Append(' ', depth * indentSize);
    }

    /// <summary>原样透传 token 的 UTF-8 字节（仅用于数字与 true/false/null 字面量）。</summary>
    private static void AppendRaw(StringBuilder sb, in Utf8JsonReader r)
    {
        if (r.HasValueSequence)
        {
            foreach (var seg in r.ValueSequence) sb.Append(Encoding.UTF8.GetString(seg.Span));
        }
        else
        {
            sb.Append(Encoding.UTF8.GetString(r.ValueSpan));
        }
    }
}

/// <summary>JsonElement → JS 值语义转换（对齐 String(v) / Number(v)）。</summary>
public static class CaseValue
{
    public static string String(JsonElement? e)
    {
        if (e == null) return "";
        var v = e.Value;
        return v.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => "",
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => v.GetRawText(),
        };
    }

    /// <summary>对齐 JS Number(v)；不可解析返回 null（对应 NaN）。</summary>
    public static double? Number(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Number:
                return e.TryGetDouble(out var d) ? d : null;
            case JsonValueKind.True: return 1;
            case JsonValueKind.False: return 0;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined: return 0;
            case JsonValueKind.String:
                var s = e.GetString();
                if (string.IsNullOrWhiteSpace(s)) return 0;
                return double.TryParse(s.Trim(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : null;
            default:
                return null;
        }
    }

    /// <summary>对齐 JS Number.isFinite。</summary>
    public static bool IsFinite(double n) => !double.IsNaN(n) && !double.IsInfinity(n);
}

/// <summary>控制台初始化（在无控制台句柄的场景下必须容错）。</summary>
public static class ConsoleSetup
{
    /// <summary>
    /// 尽量把控制台输出切到 UTF-8 以正确显示中文。
    /// 当进程以"无控制台"方式运行（输出被重定向到文件、作为 Windows 服务/nssm/托盘托管启动）时，
    /// 设置 OutputEncoding 会抛 IOException("句柄无效") 并导致进程启动即崩溃，故必须容错。
    /// </summary>
    public static void UseUtf8()
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
        catch { /* 无控制台句柄（重定向/服务托管）时忽略，不影响功能 */ }
    }
}
