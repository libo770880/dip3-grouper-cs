using System.Windows;
using System.Windows.Controls;
using Dip3GrouperApp.Desktop.Views;

namespace Dip3GrouperApp.Desktop;

/// <summary>
/// 把任意 Button 变成「跨库关联链接」的附加行为：
/// 挂上后点击即读取 DataContext（LxLine）的 LinkLib / LinkId，打开对应库的详情弹窗。
/// 用附加属性是为了让模板放进共享 ResourceDictionary（那里没有代码后置可挂 Click 事件）。
/// </summary>
public static class RelatedLink
{
    public static readonly DependencyProperty EnableProperty = DependencyProperty.RegisterAttached(
        "Enable", typeof(bool), typeof(RelatedLink), new PropertyMetadata(false, OnEnableChanged));

    public static void SetEnable(DependencyObject o, bool v) => o.SetValue(EnableProperty, v);
    public static bool GetEnable(DependencyObject o) => (bool)o.GetValue(EnableProperty);

    private static void OnEnableChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not Button b) return;
        b.Click -= OnClick;
        if (e.NewValue is true) b.Click += OnClick;
    }

    private static void OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not LxLine ln) return;
        // 页面内动作优先（如详情卡「点此查看全部」定位使用说明面板）
        if (ln.Click != null)
        {
            e.Handled = true;
            ln.Click();
            return;
        }
        if (!ln.HasLink) return;
        e.Handled = true;
        RelatedWindow.Open(Window.GetWindow(fe), ln.LinkLib, ln.LinkId);
    }
}
