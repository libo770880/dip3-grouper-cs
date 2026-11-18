using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Dip3GrouperApp.Desktop;

/// <summary>
/// 明细卡（RowDetails）的收起行为。
///
/// 表格用的是 <c>RowDetailsVisibilityMode=VisibleWhenSelected</c>：选中即展开，
/// 而收起只能靠「改选另一行」——想顺眼看看别的行，得先点别的行、再点回来，很别扭。
/// 这里补两条通路：
/// ① 点已展开的明细卡（或该行单元格）→ 收起；再点一次 → 展开；
/// ② 卡片右上角一个「收起」链接，点它只收当前这一行。
/// 明细卡里的可选文本、星标、链接按钮一律不参与切换，免得妨碍「选中复制」。
/// </summary>
public static class DetailToggle
{
    // ==================================================================
    // ① DataGrid：点行 / 点卡片背景 → 收起或展开
    // ==================================================================

    public static readonly DependencyProperty EnableProperty = DependencyProperty.RegisterAttached(
        "Enable", typeof(bool), typeof(DetailToggle), new PropertyMetadata(false, OnEnableChanged));

    public static void SetEnable(DependencyObject o, bool v) => o.SetValue(EnableProperty, v);
    public static bool GetEnable(DependencyObject o) => (bool)o.GetValue(EnableProperty);

    private static void OnEnableChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not DataGrid g) return;
        g.PreviewMouseLeftButtonDown -= OnDown;
        if (e.NewValue is true) g.PreviewMouseLeftButtonDown += OnDown;
        // 不必自己维护「哪些行被收起过」：DataGrid 每次选中变化都会按 VisibleWhenSelected
        // 重算各行 DetailsVisibility，覆盖掉我们的临时值——正好就是想要的效果。
    }

    private static void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid g || e.OriginalSource is not DependencyObject src) return;
        if (TryToggle(g, src)) e.Handled = true;
    }

    /// <summary>
    /// 判定这次左键点击要不要切换明细卡。抽成静态方法，便于离屏自检直接喂元素进去。
    /// 点在可选文本 / 星标 / 链接按钮上不做切换，交给控件自己处理。
    /// </summary>
    internal static bool TryToggle(DataGrid g, DependencyObject src)
    {
        var row = RowOf(src);
        if (row == null || !row.IsSelected) return false;   // 未选中的行：先让 DataGrid 正常选中（选中即展开）

        var open = row.DetailsVisibility != Visibility.Collapsed;
        row.DetailsVisibility = open ? Visibility.Collapsed : Visibility.Visible;
        if (!open) g.ScrollIntoView(row.Item);              // 重新展开时把卡片带回视野
        return true;
    }

    // ==================================================================
    // ② 卡片头部的「收起」链接
    // ==================================================================

    public static readonly DependencyProperty CollapseProperty = DependencyProperty.RegisterAttached(
        "Collapse", typeof(bool), typeof(DetailToggle), new PropertyMetadata(false, OnCollapseChanged));

    public static void SetCollapse(DependencyObject o, bool v) => o.SetValue(CollapseProperty, v);
    public static bool GetCollapse(DependencyObject o) => (bool)o.GetValue(CollapseProperty);

    private static void OnCollapseChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not ButtonBase b) return;
        b.Click -= OnCollapseClick;
        if (e.NewValue is true) b.Click += OnCollapseClick;
    }

    private static void OnCollapseClick(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject src) return;
        var row = RowOf(src, stopAtInteractive: false);      // 起点就是按钮本身，别把自己当成「不归我管」
        if (row == null) return;
        row.DetailsVisibility = Visibility.Collapsed;
        e.Handled = true;                                    // 别让外层再当成一次普通行点击
    }

    // ==================================================================
    // 公共：从点击元素向上找所属行
    // ==================================================================

    /// <summary>
    /// 向上找到所属 <see cref="DataGridRow"/>；
    /// 途中先遇到文本框 / 按钮（星标、关联链接、收起）则返回 null，表示这次点击不归本行为管。
    /// </summary>
    private static DataGridRow RowOf(DependencyObject src, bool stopAtInteractive = true)
    {
        for (var d = src; d != null; d = ParentOf(d))
        {
            if (d is DataGridRow row) return row;
            if (d is DataGrid) return null;                  // 走到表头等表格本身，说明不在任何一行里
            if (stopAtInteractive && (d is ButtonBase || d is TextBoxBase)) return null;
        }
        return null;
    }

    private static DependencyObject ParentOf(DependencyObject d) => d switch
    {
        Visual or Visual3D => VisualTreeHelper.GetParent(d),
        FrameworkContentElement fce => fce.Parent,
        _ => LogicalTreeHelper.GetParent(d),
    };
}
