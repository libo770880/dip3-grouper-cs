using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Dip3Grouper;

/// <summary>
/// 拼音检索辅助（对齐 src/pym.js）：为中文文本生成全拼/首字母索引，支持 ASCII 关键词的拼音匹配。
/// - 全拼包含匹配: 「lanwei」→ 阑尾炎 yes
/// - 首字母包含匹配: 「lwy」→ 阑尾炎(lwy) yes
/// 依赖 ToolGood.Words 的 Pinyin / FirstPinyin 组件（已按 pinyin-pro 对拍一致）。
/// </summary>
public static class PinyinHelper
{
    public sealed class PyKeys
    {
        public string Full { get; init; }
        public string First { get; init; }
    }

    private static readonly ConcurrentDictionary<string, PyKeys> Cache = new();
    private static readonly Regex NonAscii = new(@"[^\x00-\x7F]", RegexOptions.Compiled);
    private static readonly Regex Strip = new(@"[^a-z0-9]", RegexOptions.Compiled);

    /// <summary>返回 {full, first} 或 null（纯 ASCII / 转换失败）。</summary>
    public static PyKeys PyKeysOf(string text)
    {
        var s = (text ?? string.Empty).Trim();
        if (s.Length == 0 || !NonAscii.IsMatch(s)) return null;
        if (Cache.TryGetValue(s, out var cached)) return cached;

        PyKeys hit = null;
        try
        {
            var fullRaw = ToolGood.Words.Pinyin.WordsHelper.GetPinyin(s, false);
            var firstRaw = ToolGood.Words.FirstPinyin.WordsHelper.GetFirstPinyin(s);
            var full = Strip.Replace((fullRaw ?? string.Empty).Replace(" ", string.Empty).ToLowerInvariant(), string.Empty);
            var first = Strip.Replace((firstRaw ?? string.Empty).Replace(" ", string.Empty).ToLowerInvariant(), string.Empty);
            if (full.Length > 0) hit = new PyKeys { Full = full, First = first };
        }
        catch
        {
            hit = null; // 转换失败按无拼音处理
        }

        if (Cache.Count > 60000) Cache.Clear(); // 简单防膨胀
        Cache[s] = hit;
        return hit;
    }

    /// <summary>ASCII 关键词是否命中拼音（全拼包含 / 首字母包含）；中文关键词或过短返回 false。</summary>
    public static bool PyMatch(string text, string kw)
    {
        var k = (kw ?? string.Empty).Trim().ToLowerInvariant();
        if (k.Length == 0 || k.Length < 2 || NonAscii.IsMatch(k)) return false;
        var py = PyKeysOf(text);
        if (py == null) return false;
        return py.Full.Contains(k, StringComparison.Ordinal)
            || (py.First.Length >= k.Length && py.First.Contains(k, StringComparison.Ordinal));
    }

    /// <summary>多字段版本：任一字段的拼音命中即算命中（避免为拼一个组合串而反复转换）。</summary>
    public static bool PyMatchAny(IEnumerable<string> texts, string kw)
        => texts.Any(t => PyMatch(t, kw));

    /// <summary>当前缓存条目数（诊断用）</summary>
    public static int CacheCount => Cache.Count;

    /// <summary>预热拼音缓存（应在后台线程调用）：首次按拼音检索时逐条实时转换会明显卡顿，
    /// 提前把常用字段转换一遍，之后检索只走缓存。</summary>
    public static void WarmAll(IEnumerable<string> texts)
    {
        foreach (var t in texts)
            if (!string.IsNullOrWhiteSpace(t)) PyKeysOf(t);
    }
}
