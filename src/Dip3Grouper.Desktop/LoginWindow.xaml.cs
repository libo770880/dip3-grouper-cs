using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Dip3Grouper;

namespace Dip3GrouperApp.Desktop;

/// <summary>
/// 后台管理登录窗（无边框圆角，品牌头可拖动窗口）。
///
/// 口令在 Core 侧以 PBKDF2 哈希存储（<see cref="SettingsStore"/>），这里只做校验；
/// 若在「系统设置」中关闭了登录开关，则根本不会走到这个窗口。
///
/// 「敲回车就能登录」由三条互不依赖的通道共同保证（见 <see cref="SubmitFromKey"/>）：
///   ① 两个输入框各自的 PreviewKeyDown（焦点在框内）
///   ② 窗口级 PreviewKeyDown（焦点停在按钮/关闭图标等任意位置）
///   ③ <c>BtnLogin.IsDefault="True"</c>（WPF 原生默认按钮）
/// 其中①②还额外认「输入法吃掉的那一下回车」（Key.ImeProcessed）。
/// </summary>
public partial class LoginWindow : Window
{
    private int _failed;

    /// <summary>上一次提交的时间戳（TickCount）；用于把"同一次按键走了多条通道"去重</summary>
    private int _lastSubmitTick = Environment.TickCount - 1000;

    /// <summary>诊断入口（<c>--login</c> 模式挂上）：把键盘通道收到的原始按键写进日志</summary>
    internal static Action<string> Trace;

    private static void Log(string m) => Trace?.Invoke(m);

    private static string ImeKey(KeyEventArgs e)
        => e.Key == Key.ImeProcessed ? e.ImeProcessedKey.ToString() : "-";

    /// <summary>自检用：已经进入过登录校验的次数（不暴露任何口令信息）</summary>
    internal int LoginAttempts { get; private set; }

    /// <summary>自检用：已通过校验并提交（窗口内部已置为"已完成"）</summary>
    internal bool Submitted { get; private set; }

    /// <summary>自检用：走一遍键盘提交通道（等价于按下回车）</summary>
    internal void SubmitForTest() => SubmitFromKey(null);

    public LoginWindow()
    {
        InitializeComponent();

        var s = SettingsStore.Current;
        TxtLogo.Text = SettingsStore.LogoText;
        TxtAppName.Text = s.AppName;
        TxtUser.Text = s.AdminUser ?? SettingsStore.DefaultUser;

        TxtHint.Text = SettingsStore.IsDefaultPassword
            ? $"当前为出厂口令：账号 {SettingsStore.DefaultUser} ／ 密码 {SettingsStore.DefaultPassword}。\n"
              + "登录后请在「系统设置 · 账号安全」中尽快修改。"
            : "如需重置口令，可删除程序目录下的 data/settings.json（会恢复为出厂账号）。";

        PreviewKeyDown += (_, e) =>
        {
            Log($"window key={e.Key} imeKey={ImeKey(e)}");
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Dismiss(false);
                return;
            }
            SubmitFromKey(e);
        };

        Loaded += (_, _) =>
        {
            if (TxtUser.Text.Length == 0) TxtUser.Focus();
            else TxtPass.Focus();
            Log($"loaded focused={(Keyboard.FocusedElement as FrameworkElement)?.Name} " +
                $"imeEnabled={InputMethod.GetIsInputMethodEnabled(TxtPass)}");
        };
    }

    // ==================================================================
    // 窗口交互
    // ==================================================================

    private void OnHeadDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnFieldKey(object sender, KeyEventArgs e)
    {
        Log($"field key={e.Key} imeKey={ImeKey(e)} passLen={TxtPass.Password.Length} " +
            $"focus={(Keyboard.FocusedElement as FrameworkElement)?.Name}");
        SubmitFromKey(e);
    }

    /// <summary>
    /// 键盘通道统一入口：回车提交登录。
    ///
    /// 为什么不止挂输入框上一个 PreviewKeyDown：
    ///   · 焦点不在输入框（例如停在"登录"按钮、关闭图标上）时，输入框收不到事件；
    ///   · 输入法开着时，回车有可能先被 IME 拿去做"候选上屏"，WPF 只能把它作为
    ///     <see cref="Key.ImeProcessed"/> 交出来。这一路也要认，并推迟一拍再读密码框，
    ///     让上屏结果先落进去 —— 这样"输入法状态下敲一次回车"就能同时完成上屏 + 登录。
    /// </summary>
    private void SubmitFromKey(KeyEventArgs e)
    {
        if (e == null || e.Key == Key.Enter)
        {
            if (e != null) e.Handled = true;
            DoLogin();
            return;
        }
        if (e.Key == Key.ImeProcessed && e.ImeProcessedKey == Key.Enter)
        {
            e.Handled = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (TxtPass.Password.Length > 0) DoLogin();
            }), DispatcherPriority.Background);
        }
    }

    private void OnLogin(object sender, RoutedEventArgs e) => DoLogin();

    private void OnCancel(object sender, RoutedEventArgs e) => Dismiss(false);

    private void Dismiss(bool ok)
    {
        // DialogResult 只在 ShowDialog 打开的窗口上可写；非模态时退回 Close()
        Submitted = ok;
        try { DialogResult = ok; }
        catch (InvalidOperationException) { Close(); }
    }

    // ==================================================================
    // 校验
    // ==================================================================

    private void DoLogin()
    {
        // 同一次按键可能经由（窗口级 / 输入框级 / 默认按钮）多条通道进来，200ms 内只认一次
        var now = Environment.TickCount;
        if (unchecked(now - _lastSubmitTick) < 200) return;
        _lastSubmitTick = now;

        var user = TxtUser.Text?.Trim() ?? "";
        var pass = TxtPass.Password ?? "";
        LoginAttempts++;
        Log($"DoLogin user='{user}' passLen={pass.Length}");

        if (user.Length == 0) { Fail("请输入登录账号。"); TxtUser.Focus(); return; }
        if (pass.Length == 0) { Fail("请输入登录密码。"); TxtPass.Focus(); return; }

        if (!SettingsStore.VerifyLogin(user, pass))
        {
            _failed++;
            AuditLog.Write(AuditAction.Login, "后台登录", $"桌面端登录失败：账号 {user}", false,
                null, user, "桌面端");
            TxtPass.Clear();
            TxtPass.Focus();
            Fail(_failed >= 3
                ? "账号或密码不正确。若确实忘记，可删除程序目录下的 data/settings.json 重置为出厂账号。"
                : "账号或密码不正确，请重新输入。");
            return;
        }

        // 登录成功：把身份设为全局默认操作者，之后所有后台写操作都会带上这个账号
        AuditLog.SetDefaultActor(user, "桌面端");
        AuditLog.Write(AuditAction.Login, "后台登录", $"桌面端登录成功（账号 {user}）", true, null, user, "桌面端");
        Dismiss(true);
    }

    private void Fail(string message)
    {
        TxtError.Text = message;
        TxtError.Visibility = Visibility.Visible;
        TxtHint.Visibility = Visibility.Collapsed;
    }
}
