using System.Text.RegularExpressions;

namespace Dip3Grouper;

/// <summary>
/// DIP 3.0 编码匹配工具（对齐 src/matcher.js）
/// - 诊断编码: 支持前缀匹配（配置码 A09.0 匹配 A09.000x001）、范围匹配（C00-C75）
/// - 手术编码: 支持精确/前缀匹配（配置码 33.6x00 匹配 33.6x00x001）
/// </summary>
public static class Matcher
{
    private static readonly Regex CatRe = new(@"^[A-Z]\d{2}$", RegexOptions.Compiled);

    public static string NormalizeCode(string code) => (code ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>判断病案诊断码是否命中配置码（ICD-10 层级前缀匹配）。</summary>
    public static bool MatchDiag(string configCode, string caseCode)
    {
        var c = NormalizeCode(configCode);
        if (c.EndsWith("+", StringComparison.Ordinal)) c = c[..^1]; // M47.0+ 剑标码去掉+
        var d = NormalizeCode(caseCode);
        if (c.Length == 0 || d.Length == 0) return false;
        if (c == "所有诊断") return true;
        if (c.Contains('-'))
        {
            var parts = c.Split('-');
            if (parts.Length < 2) return false;
            var lo = parts[0].Trim();
            var hi = parts[1].Trim();
            if (!CatRe.IsMatch(lo) || !CatRe.IsMatch(hi)) return false;
            var cat = d.Length >= 3 ? d[..3] : d;
            return CatRe.IsMatch(cat)
                && string.CompareOrdinal(cat, lo) >= 0
                && string.CompareOrdinal(cat, hi) <= 0;
        }
        return d == c || d.StartsWith(c, StringComparison.Ordinal);
    }

    /// <summary>
    /// 判断病案手术码是否命中配置码。
    /// 医保版 ICD-9 手术编码规则:
    ///   - 纯数字完整码（如 92.2400）: 精确匹配；扩展码单独成行枚举，不得被基础码吞并。
    ///   - 含 x 占位符的码（如 25.2x00）: 允许命中其 xNNN 扩展（25.2x00x001）。
    /// </summary>
    public static bool MatchProc(string configCode, string caseCode)
    {
        var c = NormalizeCode(configCode);
        var p = NormalizeCode(caseCode);
        if (c.Length == 0 || p.Length == 0) return false;
        if (c == p) return true;
        return c.Contains('X') && p.StartsWith(c, StringComparison.Ordinal);
    }

    public static bool AnyDiagMatch(string configCode, IEnumerable<string> caseCodes)
        => caseCodes != null && caseCodes.Any(d => MatchDiag(configCode, d));

    public static bool AnyProcMatch(string configCode, IEnumerable<string> caseCodes)
        => caseCodes != null && caseCodes.Any(p => MatchProc(configCode, p));

    /// <summary>组合编码判定: 组合内所有编码均需在病案手术列表中出现。</summary>
    public static bool ComboMatch(IEnumerable<string> comboCodes, IEnumerable<string> caseCodes)
    {
        var list = caseCodes as IList<string> ?? (caseCodes ?? Array.Empty<string>()).ToList();
        return (comboCodes ?? Array.Empty<string>()).All(cc => AnyProcMatch(cc, list));
    }

    /// <summary>配置码列表中任一命中病案诊断。</summary>
    public static bool MatchDiagAny(IEnumerable<string> configCodes, string caseCode)
        => (configCodes ?? Array.Empty<string>()).Any(c => MatchDiag(c, caseCode));

    /// <summary>配置码列表中第一个命中病案诊断的配置码（用于展示 DIP 3.0 病种库编码），无命中返回 null。</summary>
    public static string MatchDiagCodeAny(IEnumerable<string> configCodes, string caseCode)
    {
        foreach (var c in configCodes ?? Array.Empty<string>())
            if (MatchDiag(c, caseCode)) return c;
        return null;
    }

    /// <summary>配置码列表 × 病案诊断列表 交叉任一命中。</summary>
    public static bool AnyDiagAny(IEnumerable<string> configCodes, IEnumerable<string> caseCodes)
    {
        var conf = (configCodes ?? Array.Empty<string>()).ToList();
        return (caseCodes ?? Array.Empty<string>()).Any(d => MatchDiagAny(conf, d));
    }
}
