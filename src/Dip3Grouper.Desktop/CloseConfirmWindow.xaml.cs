using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dip3Grouper;

namespace Dip3GrouperApp.Desktop;

/// <summary>
/// 点窗口关闭按钮时的确认框：让用户当场选「最小化到托盘」还是「退出程序」，
/// 并可用「记住我的选择」把结果写进设置（之后不再询问）。
///
/// 设计要点：
///   ① 三个出口都有明确语义 —— ✕ / Esc / 取消 = 什么都不做（窗口不关）；
///      最小化到托盘 = 收进托盘继续跑（通知公告照收）；退出程序 = 结束进程。
///   ② 默认按钮落在「最小化到托盘」：回车最坏结果是留在后台，不会误杀会话。
///   ③ 返回途径只有 Choice 一个字段，调用方读完即弃；不做 MessageBox 那套编号约定。
/// </summary>
public partial class CloseConfirmWindow : Window
{
    /// <summary>用户在确认框里的选择（Cancel = 什么都不做）</summary>
    public enum CloseChoice { Cancel, HideToTray, Exit }

    /// <summary>本次选择；未做选择时是 Cancel</summary>
    public CloseChoice Choice { get; private set; } = CloseChoice.Cancel;

    /// <summary>是否勾了「记住我的选择，以后不再询问」</summary>
    public bool Remember => ChkRemember.IsChecked == true;

    /// <param name="appName">显示在说明里的软件名称（跟随服务端公共设置）</param>
    public CloseConfirmWindow(string appName = null)
    {
        InitializeComponent();
        var name = string.IsNullOrWhiteSpace(appName) ? "程序" : appName.Trim();
        TxtAskSub.Text = $"最小化到托盘后 {name} 继续在后台运行，通知公告照常接收；"
                         + "双击托盘图标可随时唤回。选择「退出程序」则立即结束。";
    }

    // ==================================================================
    // 三个出口
    // ==================================================================

    /// <summary>最小化到托盘（默认按钮）</summary>
    private void OnTray(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.HideToTray;
        DialogResult = true;
    }

    /// <summary>退出程序</summary>
    private void OnExit(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.Exit;
        DialogResult = true;
    }

    /// <summary>标题栏 ✕ 与 Alt+F4：等同取消（Esc 由「取消」按钮的 IsCancel 兜住）</summary>
    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.Cancel;
        DialogResult = false;
    }

    private void OnHeadDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    // ==================================================================
    // 离屏渲染取样（诊断用：--shot-close &lt;输出.png&gt;）
    // ==================================================================

    /// <summary>
    /// 不显示窗口，直接按实际尺寸布局后渲染成 PNG，用于无需人工点击地核对版式。
    /// 返回 null 表示成功，否则返回错误说明。
    /// </summary>
    public static string Shot(string outPath)
    {
        var w = new CloseConfirmWindow(SettingsStore.Current.AppName);
        if (w.Content is not FrameworkElement root) return "窗口内容为空";

        // SizeToContent=Height：先按目标宽度量一遍才知道实际高度
        root.Measure(new Size(w.Width, double.PositiveInfinity));
        var size = new Size(w.Width, Math.Ceiling(root.DesiredSize.Height));
        root.Arrange(new Rect(size));
        root.UpdateLayout();

        var bmp = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(outPath)) enc.Save(fs);
        return null;
    }
}
