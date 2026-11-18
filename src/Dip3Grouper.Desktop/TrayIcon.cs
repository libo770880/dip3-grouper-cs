using System.Windows;
using Dip3Grouper;
using WinForms = System.Windows.Forms;
using SD = System.Drawing;

namespace Dip3GrouperApp.Desktop;

/// <summary>托盘菜单条目模型（自检断言与真实菜单共用，保证「菜单里有什么」只有一份定义）。</summary>
public sealed record TrayItem(string Text, bool IsSeparator = false, bool IsToggle = false);

/// <summary>
/// 系统托盘图标 + 右键菜单。
///
/// 职责：
///   ① 点窗口关闭按钮「最小化到托盘」后，从这里唤回主界面 / 打开后台管理 / 彻底退出；
///   ② 菜单每次打开时按 settings.json 重建，「关闭时最小化到托盘」勾选态永远实时；
///   ③ 首次收进托盘弹一次气泡，避免用户以为程序已退出又双击 exe 起了第二个实例。
///
/// 图标：优先取嵌入资源 Assets/app.ico（与 exe 图标同一份），失败时退回 exe 关联图标，
///       再失败用系统默认——托盘功能本身不能因为图标问题挂掉。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const string PackUri = "pack://application:,,,/Assets/app.ico";

    private readonly Window _host;
    private WinForms.NotifyIcon _ni;
    private WinForms.ContextMenuStrip _menu;
    private bool _tipShown;
    private bool _disposed;

    public TrayIcon(Window host)
    {
        _host = host;
        _ni = new WinForms.NotifyIcon
        {
            Text = Truncate(_host.Title),
            Visible = true,
            Icon = LoadIcon(),
        };
        _ni.DoubleClick += (_, _) => ShowMain();
        _ni.ContextMenuStrip = _menu = BuildMenu();
        _menu.Opening += (_, _) => RefreshDynamic();
    }

    // ==================================================================
    // 菜单定义（模型 ↔ 实际菜单一一对应）
    // ==================================================================

    /// <summary>菜单条目（closeToTray 只影响勾选开关的初始态，条目集合不变）</summary>
    internal static List<TrayItem> MenuModel() => new()
    {
        new("打开主界面"),
        new("最小化到托盘"),
        new("打开后台管理"),
        new("-", IsSeparator: true),
        new("关闭时最小化到托盘", IsToggle: true),
        new("-", IsSeparator: true),
        new("退出程序"),
    };

    private WinForms.ContextMenuStrip BuildMenu()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.ShowImageMargin = false;          // 纯文字，紧凑
        menu.RenderMode = WinForms.ToolStripRenderMode.System;

        foreach (var it in MenuModel())
        {
            if (it.IsSeparator)
            {
                menu.Items.Add(new WinForms.ToolStripSeparator());
                continue;
            }
            var item = new WinForms.ToolStripMenuItem(it.Text)
            {
                Font = new SD.Font("Microsoft YaHei UI", 9.5f),
            };
            if (it.IsToggle)
            {
                item.CheckOnClick = true;
                item.Checked = SettingsStore.CloseToTray;
                // 勾选即视为「已经明确表态」：连「关闭时先询问」一起关掉，
                // 否则用户会觉得"我在这里设过了，怎么点关闭还弹窗问"
                item.Click += (_, _) => SettingsStore.SetCloseChoice(item.Checked);
            }
            else
            {
                item.Click += (_, _) => OnMenu(it.Text);
            }
            menu.Items.Add(item);
        }
        return menu;
    }

    /// <summary>菜单打开前刷新动态态：勾选开关跟随设置，悬停文本跟随窗口标题</summary>
    private void RefreshDynamic()
    {
        if (_disposed) return;
        _ni.Text = Truncate(_host.Title);
        foreach (var it in _menu.Items.OfType<WinForms.ToolStripMenuItem>())
            if (it.CheckOnClick) it.Checked = SettingsStore.CloseToTray;
    }

    private void OnMenu(string text)
    {
        switch (text)
        {
            case "打开主界面": ShowMain(); break;
            case "最小化到托盘": HideToTray(); break;
            case "打开后台管理": (_host as MainWindow)?.OpenAdminFromTray(); break;
            case "退出程序": (_host as MainWindow)?.ExitFromTray(); break;
        }
    }

    // ==================================================================
    // 行为
    // ==================================================================

    /// <summary>从托盘唤回主界面（还原最小化态并抢回前台）</summary>
    public void ShowMain()
    {
        if (_disposed) return;
        _host.Show();
        if (_host.WindowState == WindowState.Minimized) _host.WindowState = WindowState.Normal;
        _host.Activate();
    }

    /// <summary>收进托盘：隐藏窗口；第一次附带气泡说明，之后静默</summary>
    public void HideToTray()
    {
        if (_disposed) return;
        _host.Hide();
        if (_tipShown) return;
        _tipShown = true;
        try
        {
            _ni.ShowBalloonTip(2200, Truncate(_host.Title),
                "程序已最小化到系统托盘：双击图标重新打开，右键菜单可退出。", WinForms.ToolTipIcon.Info);
        }
        catch { /* 气泡失败无所谓 */ }
    }

    /// <summary>
    /// 弹一次托盘气泡（不改窗口显隐）。用于「启动后静默检查到数据可更新」这类提示。
    /// </summary>
    public void Balloon(string text)
    {
        if (_disposed) return;
        try
        {
            _ni.ShowBalloonTip(4000, Truncate(_host.Title), text ?? "", WinForms.ToolTipIcon.Info);
        }
        catch { /* 气泡失败无所谓 */ }
    }

    /// <summary>彻底退出前调用；移除托盘图标（否则图标残留在托盘直到鼠标划过）</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_menu != null) { _menu.Dispose(); _menu = null; }
        if (_ni != null)
        {
            _ni.Visible = false;
            _ni.Icon?.Dispose();
            _ni.Dispose();
            _ni = null;
        }
    }

    // ==================================================================
    // 图标
    // ==================================================================

    /// <summary>托盘用 32×32 帧即可（DPI 高时 Shell 会自己放大相邻帧）</summary>
    private static SD.Icon LoadIcon()
    {
        try
        {
            using var s = Application.GetResourceStream(new Uri(PackUri))?.Stream;
            if (s != null) return new SD.Icon(s, new SD.Size(32, 32));
        }
        catch { /* 落到下面的兜底 */ }
        try { return SD.Icon.ExtractAssociatedIcon(Environment.ProcessPath); }
        catch { return SD.SystemIcons.Application; }
    }

    /// <summary>NotifyIcon 悬停文本上限 63 字符</summary>
    private static string Truncate(string s) => s is { Length: > 63 } t ? t[..63] : s ?? "";
}
