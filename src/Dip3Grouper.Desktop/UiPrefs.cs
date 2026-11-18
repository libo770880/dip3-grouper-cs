using System.Windows;
using Dip3Grouper;

namespace Dip3GrouperApp.Desktop;

/// <summary>
/// 界面密度偏好（紧凑 / 标准）。
///
/// <para>为什么用全局资源而不是逐表设 RowHeight：数据页有 4 张表（现行 / 作废 / 旧版 / 比对），
/// 加病种库、立项指南、审计日志，一处开关要同时改 8 张表；用一个 <c>DgRowHeight</c> 资源，
/// 表格 XAML 里写 <c>RowHeight="{DynamicResource DgRowHeight}"</c> 就能一起变。</para>
///
/// <para>选择落 <c>data/settings.json</c> 的 <c>compactRows</c>（本机私有键，不参与数据分发）。</para>
/// </summary>
public static class UiPrefs
{
    /// <summary>紧凑行高（1366×768 下一屏多出 3~4 行）</summary>
    public const double CompactHeight = 26;

    /// <summary>标准行高</summary>
    public const double NormalHeight = 34;

    /// <summary>当前是否紧凑</summary>
    public static bool Compact => SettingsStore.CompactRows;

    /// <summary>按钮上的文字（显示的是「当前状态」，点一下切到另一种）</summary>
    public static string Label => Compact ? "紧凑" : "标准";

    /// <summary>当前行高</summary>
    public static double RowHeight => Compact ? CompactHeight : NormalHeight;

    /// <summary>把偏好写进全局资源（App 启动时调用一次；切换时再调一次即可全表生效）</summary>
    public static void Apply()
    {
        if (Application.Current == null) return;
        Application.Current.Resources["DgRowHeight"] = RowHeight;
    }

    /// <summary>切换密度并落盘，返回切换后是否紧凑</summary>
    public static bool Toggle()
    {
        SettingsStore.SetCompactRows(!Compact);
        Apply();
        return Compact;
    }
}
