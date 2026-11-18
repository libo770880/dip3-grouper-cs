using System.Windows;
using System.Windows.Controls;

namespace Dip3GrouperApp.Desktop.Theming;

/// <summary>
/// 主题资源的代码侧取用助手。
///
/// 页面里手写 <c>FindResource("B.Xxx")</c> 拿到的是**具体画刷对象**，换肤后不会更新；
/// 想让它跟着主题走，必须改用 <c>SetResourceReference</c>（等价于 XAML 的 DynamicResource）。
/// 本类把常用控件属性的写法收敛成一行链式调用。
/// </summary>
internal static class ThemeExt
{
    /// <summary>TextBlock 前景 → 主题资源</summary>
    public static TextBlock ThFg(this TextBlock el, string key)
    {
        el.SetResourceReference(TextBlock.ForegroundProperty, key);
        return el;
    }

    /// <summary>Border 背景 → 主题资源</summary>
    public static Border ThBg(this Border el, string key)
    {
        el.SetResourceReference(Border.BackgroundProperty, key);
        return el;
    }

    /// <summary>Border 描边 → 主题资源</summary>
    public static Border ThBorder(this Border el, string key)
    {
        el.SetResourceReference(Border.BorderBrushProperty, key);
        return el;
    }

    /// <summary>任意 Control（TextBox / Expander / Button …）前景 → 主题资源</summary>
    public static T ThFg<T>(this T el, string key) where T : Control
    {
        el.SetResourceReference(Control.ForegroundProperty, key);
        return el;
    }

    /// <summary>任意 Control 背景 → 主题资源</summary>
    public static T ThBg<T>(this T el, string key) where T : Control
    {
        el.SetResourceReference(Control.BackgroundProperty, key);
        return el;
    }

    /// <summary>通用：把元素的某个依赖属性挂到主题资源上</summary>
    public static T ThRef<T>(this T el, DependencyProperty prop, string key) where T : FrameworkElement
    {
        el.SetResourceReference(prop, key);
        return el;
    }
}
