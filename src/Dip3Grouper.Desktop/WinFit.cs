using System.Windows;

namespace Dip3GrouperApp.Desktop;

/// <summary>
/// 小屏适配（2026-10-21）：低配机器常是 1024×768 甚至更低，个别还开着 125% 缩放。
/// 窗口默认尺寸（主窗 1300×870 / 后台 1220×820）比屏幕大时，CenterScreen 居中会把
/// 顶部自绘标题栏整条推出屏幕外——用户看到的是「头部菜单导航不见了」（实机截图已复现）。
/// 在 SourceInitialized 时把尺寸钳进当前工作区、位置重居中即可。
/// </summary>
public static class WinFit
{
    /// <summary>
    /// 把窗口尺寸/位置钳进主屏工作区。尺寸只在超出时才缩（MinWidth/MinHeight 一并放低，
    /// 否则下限会把赋值顶回去）；位置只在超出可视范围时重居中，多屏正常摆放不受影响。
    /// </summary>
    public static void ClampToWorkArea(Window w)
    {
        var wa = SystemParameters.WorkArea;
        if (w.MinWidth > wa.Width) w.MinWidth = wa.Width;
        if (w.MinHeight > wa.Height) w.MinHeight = wa.Height;
        if (w.Width > wa.Width) w.Width = wa.Width;
        if (w.Height > wa.Height) w.Height = wa.Height;

        if (w.Left < wa.Left || w.Top < wa.Top ||
            w.Left + w.Width > wa.Right || w.Top + w.Height > wa.Bottom)
        {
            w.Left = wa.Left + Math.Max(0, (wa.Width - w.Width) / 2);
            w.Top = wa.Top + Math.Max(0, (wa.Height - w.Height) / 2);
        }
    }
}
