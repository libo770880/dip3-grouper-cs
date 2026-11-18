using System.Windows;
using System.Windows.Media;

namespace Dip3GrouperApp.Desktop.Theming;

/// <summary>一套主题的完整色板。</summary>
internal sealed class ThemeDef
{
    public string Id { get; init; }
    public string Name { get; init; }
    public string Hint { get; init; }
    public bool Dark { get; init; }
    public string LogoA { get; init; }
    public string LogoB { get; init; }
    public string LogoC { get; init; }
    public Dictionary<string, string> Map { get; init; }

    /// <summary>取某个语义键的十六进制值</summary>
    public string Hex(string key) => Map.TryGetValue(key, out var v) ? v : "#000000";

    /// <summary>取某个语义键的 Color（用于预览色块）</summary>
    public Color Col(string key) => (Color)ColorConverter.ConvertFromString(Hex(key));

    public SolidColorBrush Brush(string key)
    {
        var b = new SolidColorBrush(Col(key));
        b.Freeze();
        return b;
    }
}

/// <summary>
/// 主题目录：五套配色。每套都必须给全 <see cref="Keys"/> 里的每个语义键，
/// 缺键会让 DynamicResource 静默回落成 null（表现为文字消失），
/// 故 <c>--selftest</c> 里有一项专门校验键齐全。
/// </summary>
internal static class ThemeCatalog
{
    /// <summary>全部语义键（与 Themes/Colors.xaml 的 B.* 一一对应）</summary>
    internal static readonly string[] Keys =
    {
        "Bg", "Card", "Border", "BorderStrong", "Top", "SoftBg", "SoftBorder", "SegBg",
        "Text", "Muted", "Faint", "InfoText", "WarnText",
        "Accent", "AccentHover", "AccentSoft", "White",
        "Ok", "Warn", "Danger", "DangerSoft", "DangerHover", "Deduct", "DeductBg",
        "InfoBg", "InfoBorder", "WarnBg",
        "RowAlt", "RowHover", "RowSelected", "ScrollThumb", "HoverBg", "FocusBg",
        "Sidebar", "SidebarHead", "SidebarText", "SidebarTextFaint", "SidebarHover",
        // 「征求意见稿」标记（前台新项目征求意见页）：底色 / 文字 / 角标底 / 角标边框
        "DraftBg", "DraftFg", "DraftBadge", "DraftBorder",
    };

    private static Dictionary<string, string> D(params string[] kv)
    {
        var d = new Dictionary<string, string>(kv.Length / 2);
        for (var i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
        return d;
    }

    public static readonly List<ThemeDef> All = new()
    {
        // ---------------------------------------------------------------
        // 1. 经典蓝 —— 默认主题，稳重专业，适合政务/医保场景
        // ---------------------------------------------------------------
        new ThemeDef
        {
            Id = "azure", Name = "经典蓝", Hint = "稳重专业 · 默认",
            LogoA = "#0e3a8c", LogoB = "#1d6fe0", LogoC = "#2f8fe6",
            Map = D(
                "Bg", "#eef2f8", "Card", "#ffffff", "Border", "#e3e8ef", "BorderStrong", "#d3d9e3",
                "Top", "#ffffff", "SoftBg", "#f7f9fc", "SoftBorder", "#e3eefb", "SegBg", "#e9eef6",
                "Text", "#17212e", "Muted", "#5b6b81", "Faint", "#9aa3b2",
                "InfoText", "#33455e", "WarnText", "#92400e",
                "Accent", "#1d6fe0", "AccentHover", "#1558b8", "AccentSoft", "#e8f1fd", "White", "#ffffff",
                "Ok", "#16a34a", "Warn", "#d97706", "Danger", "#dc2626",
                "DangerSoft", "#f3c5c5", "DangerHover", "#e11d48",
                "InfoBg", "#f0f7ff", "InfoBorder", "#d6e8fb", "WarnBg", "#fef3c7",
                "Deduct", "#0f766e", "DeductBg", "#e6f4f1",
                "DraftBg", "#fffbea", "DraftFg", "#7c4a03", "DraftBadge", "#fde68a", "DraftBorder", "#f5d78e",
                "RowAlt", "#f8fafc", "RowHover", "#eef4fe", "RowSelected", "#dfeafb",
                "ScrollThumb", "#c4cdd9", "HoverBg", "#eef1f6", "FocusBg", "#fffdf5",
                "Sidebar", "#1b2436", "SidebarHead", "#0f1728", "SidebarText", "#aab6cc",
                "SidebarTextFaint", "#7d8aa5", "SidebarHover", "#2a3550"),
        },

        // ---------------------------------------------------------------
        // 2. 青碧 —— 医疗清新，偏冷青绿，长时间阅读不易疲劳
        // ---------------------------------------------------------------
        new ThemeDef
        {
            Id = "jade", Name = "青碧", Hint = "医疗清新",
            LogoA = "#08524a", LogoB = "#0f8a7e", LogoC = "#2fb3a2",
            Map = D(
                "Bg", "#edf5f3", "Card", "#ffffff", "Border", "#dde9e6", "BorderStrong", "#c9dbd7",
                "Top", "#ffffff", "SoftBg", "#f4faf8", "SoftBorder", "#d8ebe6", "SegBg", "#e2efec",
                "Text", "#12241f", "Muted", "#527068", "Faint", "#93aca5",
                "InfoText", "#2c4f48", "WarnText", "#8a5406",
                "Accent", "#0f8a7e", "AccentHover", "#0a6b62", "AccentSoft", "#e3f4f1", "White", "#ffffff",
                "Ok", "#15a06a", "Warn", "#cf7a12", "Danger", "#d0453f",
                "DangerSoft", "#f3cfcd", "DangerHover", "#d93a4e",
                "InfoBg", "#eefaf7", "InfoBorder", "#c9e9e2", "WarnBg", "#fdf1d8",
                "Deduct", "#0b6f66", "DeductBg", "#ddf2ee",
                "DraftBg", "#fffceb", "DraftFg", "#79500a", "DraftBadge", "#fbe3ac", "DraftBorder", "#efd296",
                "RowAlt", "#f6fbf9", "RowHover", "#e8f5f2", "RowSelected", "#d5efea",
                "ScrollThumb", "#bcd2cd", "HoverBg", "#e7f1ee", "FocusBg", "#fbfdf6",
                "Sidebar", "#10312d", "SidebarHead", "#08211e", "SidebarText", "#a4c4bd",
                "SidebarTextFaint", "#6f8d86", "SidebarHover", "#1c4a44"),
        },

        // ---------------------------------------------------------------
        // 3. 靛紫 —— 现代科技，适合大屏演示
        // ---------------------------------------------------------------
        new ThemeDef
        {
            Id = "indigo", Name = "靛紫", Hint = "现代科技",
            LogoA = "#2b1f96", LogoB = "#4f46e5", LogoC = "#7b6ff0",
            Map = D(
                "Bg", "#eff0fa", "Card", "#ffffff", "Border", "#e0e2f2", "BorderStrong", "#cdd0e8",
                "Top", "#ffffff", "SoftBg", "#f7f7fd", "SoftBorder", "#e2e3fa", "SegBg", "#e8e9f7",
                "Text", "#191b2e", "Muted", "#606580", "Faint", "#9ba0bb",
                "InfoText", "#383d70", "WarnText", "#8f5a06",
                "Accent", "#4f46e5", "AccentHover", "#4338ca", "AccentSoft", "#ecebfe", "White", "#ffffff",
                "Ok", "#17a558", "Warn", "#d4870c", "Danger", "#dc2b41",
                "DangerSoft", "#f4c9cf", "DangerHover", "#e11d48",
                "InfoBg", "#f0f0fe", "InfoBorder", "#d6d7fb", "WarnBg", "#fdf0d9",
                "Deduct", "#0e7490", "DeductBg", "#e0f2f7",
                "DraftBg", "#fffbec", "DraftFg", "#7b4a10", "DraftBadge", "#fbe0b0", "DraftBorder", "#efd09a",
                "RowAlt", "#f8f8fe", "RowHover", "#eef0fe", "RowSelected", "#dfe1fd",
                "ScrollThumb", "#c3c6dd", "HoverBg", "#ecedf8", "FocusBg", "#fcfcff",
                "Sidebar", "#191a35", "SidebarHead", "#0f1029", "SidebarText", "#adafcc",
                "SidebarTextFaint", "#75779a", "SidebarHover", "#2b2c52"),
        },

        // ---------------------------------------------------------------
        // 4. 朱砂 —— 温润中医风，暖褐底 + 朱红强调
        // ---------------------------------------------------------------
        new ThemeDef
        {
            Id = "crimson", Name = "朱砂", Hint = "温润中医",
            LogoA = "#6d1b14", LogoB = "#b4322a", LogoC = "#d9614a",
            Map = D(
                "Bg", "#f8f2ee", "Card", "#fffdfc", "Border", "#ecdfd8", "BorderStrong", "#dcc9bf",
                "Top", "#fffdfc", "SoftBg", "#fdf8f5", "SoftBorder", "#f0ddd5", "SegBg", "#f1e6e0",
                "Text", "#2b1a15", "Muted", "#836a5f", "Faint", "#b09a8e",
                "InfoText", "#5c362d", "WarnText", "#85520a",
                "Accent", "#b4322a", "AccentHover", "#93251e", "AccentSoft", "#fbeae7", "White", "#ffffff",
                "Ok", "#4f8a3d", "Warn", "#c07a15", "Danger", "#c0392b",
                "DangerSoft", "#eec3bd", "DangerHover", "#d6314b",
                "InfoBg", "#fdf0ec", "InfoBorder", "#f3d8d0", "WarnBg", "#fbebd2",
                "Deduct", "#2f7a5c", "DeductBg", "#e7f2ea",
                "DraftBg", "#fffbea", "DraftFg", "#7d4609", "DraftBadge", "#fbdfae", "DraftBorder", "#eecf97",
                "RowAlt", "#fdf9f7", "RowHover", "#fbeeea", "RowSelected", "#f7dcd6",
                "ScrollThumb", "#d3bdb3", "HoverBg", "#f2e7e1", "FocusBg", "#fffdf6",
                "Sidebar", "#2c1a16", "SidebarHead", "#1d110e", "SidebarText", "#c6aca1",
                "SidebarTextFaint", "#8e7367", "SidebarHover", "#452722"),
        },

        // ---------------------------------------------------------------
        // 5. 石墨 —— 深色模式，夜间值守/大屏值班室
        // ---------------------------------------------------------------
        new ThemeDef
        {
            Id = "graphite", Name = "石墨（深色）", Hint = "夜间护眼", Dark = true,
            LogoA = "#1b3f7a", LogoB = "#4c8dff", LogoC = "#7fb0ff",
            Map = D(
                "Bg", "#16191f", "Card", "#1f232b", "Border", "#2b303a", "BorderStrong", "#3a4150",
                "Top", "#1a1e25", "SoftBg", "#21252d", "SoftBorder", "#333a47", "SegBg", "#252a34",
                "Text", "#e7ebf2", "Muted", "#9aa5b8", "Faint", "#6e7a8e",
                "InfoText", "#b9c9e2", "WarnText", "#e8c877",
                "Accent", "#4c8dff", "AccentHover", "#74a6ff", "AccentSoft", "#202e45", "White", "#ffffff",
                "Ok", "#3ecf8e", "Warn", "#e0a53c", "Danger", "#f0616d",
                "DangerSoft", "#4d2a31", "DangerHover", "#e11d48",
                "InfoBg", "#1d2839", "InfoBorder", "#2c3d59", "WarnBg", "#382d14",
                "Deduct", "#2dd4bf", "DeductBg", "#16302d",
                "DraftBg", "#2e2610", "DraftFg", "#f3d99a", "DraftBadge", "#5c4611", "DraftBorder", "#6b5316",
                "RowAlt", "#1b1f26", "RowHover", "#252b36", "RowSelected", "#2b3a52",
                "ScrollThumb", "#4a5260", "HoverBg", "#262c38", "FocusBg", "#1e2b3f",
                "Sidebar", "#12151b", "SidebarHead", "#0d1015", "SidebarText", "#a7b2c5",
                "SidebarTextFaint", "#6b7688", "SidebarHover", "#232936"),
        },
    };

    public const string DefaultId = "azure";

    public static ThemeDef Get(string id)
        => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))
           ?? All.FirstOrDefault(t => t.Id == DefaultId) ?? All[0];
}

/// <summary>
/// 运行时换肤。
///
/// 做法：把 App.Resources 顶层的 <c>B.*</c> / <c>Color.*</c> 条目**整体替换**成新画刷。
/// 这要求所有用色处写的是 <c>DynamicResource</c>（顶层字典优先于合并字典，替换即刻生效）；
/// 若写成 StaticResource，XAML 载入时已固化成旧对象，换肤不会跟着变 —— 见 Themes/Colors.xaml 顶部说明。
/// </summary>
internal static class ThemeManager
{
    /// <summary>当前生效的主题 id</summary>
    public static string CurrentId { get; private set; } = ThemeCatalog.DefaultId;

    /// <summary>主题切换后触发（参数为新主题 id），供已生成的动态元素刷新用</summary>
    public static event Action<string> Changed;

    /// <summary>应用主题；<paramref name="id"/> 未知时回落到默认主题</summary>
    public static void Apply(string id)
    {
        var th = ThemeCatalog.Get(id);
        var res = Application.Current?.Resources;
        if (res == null)
        {
            CurrentId = th.Id;
            return;
        }

        foreach (var kv in th.Map)
        {
            var color = (Color)ColorConverter.ConvertFromString(kv.Value);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            res["B." + kv.Key] = brush;      // 覆盖合并字典里的 B.Xxx
            res["Color." + kv.Key] = color;  // 供渐变/动画使用
        }

        res["B.LogoBox"] = BuildLogo(th);

        CurrentId = th.Id;
        Changed?.Invoke(th.Id);
    }

    /// <summary>品牌方块的三段渐变</summary>
    private static LinearGradientBrush BuildLogo(ThemeDef th)
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        b.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(th.LogoA), 0));
        b.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(th.LogoB), 0.6));
        b.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(th.LogoC), 1));
        b.Freeze();
        return b;
    }
}
