using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Dip3Grouper;
using Dip3GrouperApp.Desktop.Theming;

namespace Dip3GrouperApp.Desktop.Views.Admin;

/// <summary>主题色卡的一项（预览用画刷取自该主题自身，与当前生效主题无关）。</summary>
public sealed class ThemeCardVm : INotifyPropertyChanged
{
    public string Id { get; init; }
    public string Name { get; init; }
    public string Hint { get; init; }
    public Brush BgBrush { get; init; }
    public Brush CardBrush { get; init; }
    public Brush AccentBrush { get; init; }

    private bool _selected;
    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;
}

/// <summary>
/// 系统设置：软件名称 / 单位副名 / 界面主题 / 后台登录账号口令。
/// 对应 data/settings.json，保存后立即生效（主题点击即应用并落盘）。
/// </summary>
public partial class AdminSettingsView : UserControl
{
    private readonly List<ThemeCardVm> _themes = new();
    private bool _loaded;

    public AdminSettingsView()
    {
        InitializeComponent();

        // 前台标题栏也能换肤了：这张卡必须跟着勾，否则两处显示不一致。
        // 用 -= 再 += 是为了「后台窗口反复隐藏/显示」时不叠加订阅。
        Loaded += (_, _) =>
        {
            ThemeManager.Changed -= OnThemeChanged;
            ThemeManager.Changed += OnThemeChanged;
            SyncTheme(ThemeManager.CurrentId);
        };
        Unloaded += (_, _) => ThemeManager.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged(string id) => SyncTheme(id);

    /// <summary>首次进入时载入（与其它后台页一致的懒加载约定）</summary>
    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        foreach (var t in ThemeCatalog.All)
        {
            _themes.Add(new ThemeCardVm
            {
                Id = t.Id,
                Name = t.Name,
                Hint = t.Hint,
                BgBrush = t.Brush("Bg"),
                CardBrush = t.Brush("Card"),
                AccentBrush = t.Brush("Accent"),
            });
        }
        ThemeHost.ItemsSource = _themes;

        TxtSettingsPath.Text = AppPaths.SettingsFile;
        LoadForm();
        LoadParams();
    }

    // ==================================================================
    // 载入 / 回填
    // ==================================================================

    private void LoadForm()
    {
        var s = SettingsStore.Current;
        TxtAppName.Text = s.AppName ?? SettingsStore.DefaultAppName;
        TxtSubName.Text = s.AppSubName ?? "";
        ChkLogin.IsChecked = SettingsStore.LoginEnabled;
        ChkAsk.IsChecked = SettingsStore.CloseAsk;
        ChkTray.IsChecked = SettingsStore.CloseToTray;
        TxtAdminUser.Text = s.AdminUser ?? SettingsStore.DefaultUser;
        TxtNewPass.Clear();
        TxtNewPass2.Clear();
        SyncTheme(ThemeManager.CurrentId);
        RefreshPassHint();
        RefreshFollowHint();
        SetBasicStatus(null);
        SetAccountStatus(null);
        TxtTrayStatus.Text = "";     // 载入态不显示上一次操作的「已保存」文案
    }

    /// <summary>顶部提示：本机设置是否会被服务端覆盖（跟随开关在「数据更新」页）</summary>
    private void RefreshFollowHint()
    {
        var follow = SettingsStore.HasDataSource && SettingsStore.FollowServerSettings;
        TxtTip.Text = follow
            ? "软件名称 / 界面主题 / 后台登录 / 立项指南参数 —— 保存后立即生效"
              + "（已开启「跟随服务端公共设置」：软件名称、副名与立项参数每次启动会被数据源覆盖，"
              + "可在「数据更新」页关闭）"
            : "软件名称 / 界面主题 / 后台登录 / 立项指南参数 —— 保存后立即生效";
    }

    private void RefreshPassHint()
    {
        TxtPassHint.Text = SettingsStore.IsDefaultPassword
            ? $"当前口令仍是出厂默认值（{SettingsStore.DefaultUser} / {SettingsStore.DefaultPassword}），"
              + "建议立刻改成自家口令。口令以 PBKDF2 哈希保存，程序内不保留明文，忘记后只能删除 "
              + "data/settings.json 重置。"
            : "口令已自定义。密码框留空即表示不做修改；改动后立即生效，下次打开后台需用新口令登录。";
    }

    private void SyncTheme(string id)
    {
        foreach (var t in _themes) t.IsSelected = t.Id == id;
    }

    // ==================================================================
    // 基本设置
    // ==================================================================

    private void OnPickTheme(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border card || card.Tag is not string id) return;
        if (ThemeManager.CurrentId == id) return;

        ThemeManager.Apply(id);
        SettingsStore.Current.Theme = id;
        SettingsStore.Save();
        SyncTheme(id);
        AuditLog.Write(AuditAction.Setting, "系统设置", $"切换界面主题为「{ThemeCatalog.Get(id).Name}」");
        SetBasicStatus($"已切换到「{ThemeCatalog.Get(id).Name}」并保存", true);
    }

    private void OnSaveBasic(object sender, RoutedEventArgs e)
    {
        var name = (TxtAppName.Text ?? "").Trim();
        if (name.Length == 0)
        {
            SetBasicStatus("软件名称不能为空", false);
            TxtAppName.Focus();
            return;
        }

        SettingsStore.Current.AppName = name;
        SettingsStore.SetSubName(TxtSubName.Text);
        SettingsStore.Current.Theme = ThemeManager.CurrentId;
        SettingsStore.Save();

        AuditLog.Write(AuditAction.Setting, "系统设置",
            $"修改软件名称「{name}」" + (string.IsNullOrWhiteSpace(TxtSubName.Text) ? "" : $" / 副名「{TxtSubName.Text.Trim()}」"));
        SetBasicStatus("已保存，窗口标题与品牌区已同步更新", true);
        App.NotifySettingsChanged();
    }

    private void OnRevertName(object sender, RoutedEventArgs e)
    {
        TxtAppName.Text = SettingsStore.DefaultAppName;
        TxtSubName.Clear();
        SetBasicStatus("已恢复默认名称，点「保存设置」生效", true);
    }

    // ==================================================================
    // 窗口与托盘
    // ==================================================================

    /// <summary>「点关闭先询问」开关：切换即落盘</summary>
    private void OnToggleAsk(object sender, RoutedEventArgs e)
    {
        var on = ChkAsk.IsChecked == true;
        SettingsStore.SetCloseAsk(on);
        TxtTrayStatus.Text = on
            ? "已保存：点关闭按钮会先弹窗问一次「最小化到托盘还是退出程序」。"
            : "已保存：点关闭按钮不再询问，直接按下面这项执行。";
    }

    /// <summary>「点关闭最小化到托盘」开关：切换即落盘（托盘菜单里也有同一开关）</summary>
    private void OnToggleTray(object sender, RoutedEventArgs e)
    {
        var on = ChkTray.IsChecked == true;
        SettingsStore.SetCloseToTray(on);
        TxtTrayStatus.Text = on
            ? "已保存：不问时点关闭按钮将最小化到托盘，双击托盘图标可唤回。"
            : "已保存：不问时点关闭按钮将直接退出程序。";
    }

    // ==================================================================
    // 账号安全
    // ==================================================================

    private void OnSaveAccount(object sender, RoutedEventArgs e)
    {
        var user = (TxtAdminUser.Text ?? "").Trim();
        if (user.Length == 0)
        {
            SetAccountStatus("登录账号不能为空", false);
            TxtAdminUser.Focus();
            return;
        }

        var p1 = TxtNewPass.Password ?? "";
        var p2 = TxtNewPass2.Password ?? "";
        if (p1.Length > 0 || p2.Length > 0)
        {
            if (p1.Length < 6)
            {
                SetAccountStatus("新密码至少 6 位", false);
                TxtNewPass.Focus();
                return;
            }
            if (p1 != p2)
            {
                SetAccountStatus("两次输入的新密码不一致", false);
                TxtNewPass2.Focus();
                return;
            }
        }

        SettingsStore.Current.LoginEnabled = ChkLogin.IsChecked == true;
        SettingsStore.SetUser(user);
        var changedPass = p1.Length > 0;
        if (changedPass) SettingsStore.SetPassword(p1);
        SettingsStore.Save();

        TxtNewPass.Clear();
        TxtNewPass2.Clear();
        RefreshPassHint();
        AuditLog.Write(AuditAction.Setting, "系统设置",
            $"修改后台账号「{user}」" + (changedPass ? "并更新了登录口令" : "")
            + (ChkLogin.IsChecked == true ? "" : " · 已关闭登录校验"));
        SetAccountStatus(changedPass ? "已保存，登录口令已更新" : "已保存", true);
    }

    // ==================================================================
    // 立项指南参数
    // ==================================================================

    /// <summary>多行文本 → 列表（每行一项，去空白）</summary>
    private static List<string> LinesToList(string text)
        => (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    /// <summary>列表 → 多行文本</summary>
    private static string ListToLines(List<string> list)
        => list == null || list.Count == 0 ? "" : string.Join("\n", list);

    private void LoadParams()
    {
        var s = SettingsStore.Current;
        // 未配置时预填现有数据去重值，便于直接在此基础上增删
        TxtCategories.Text = ListToLines(s.LixiangCategories is { Count: > 0 }
            ? s.LixiangCategories
            : App.State.Lixiang.DistinctCategories());
        TxtUnits.Text = ListToLines(s.LixiangUnits is { Count: > 0 }
            ? s.LixiangUnits : App.State.Lixiang.DistinctUnits());
        SetParamStatus(null);
    }

    private void OnSaveParams(object sender, RoutedEventArgs e)
    {
        var s = SettingsStore.Current;
        s.LixiangCategories = SettingsStore.CleanList(LinesToList(TxtCategories.Text)) is { Count: > 0 } c ? c : null;
        s.LixiangUnits = SettingsStore.CleanList(LinesToList(TxtUnits.Text)) is { Count: > 0 } u ? u : null;
        SettingsStore.Save();

        AuditLog.Write(AuditAction.Setting, "系统设置",
            $"修改立项指南参数表：类别 {s.LixiangCategories?.Count ?? 0} 项 · 单位 {s.LixiangUnits?.Count ?? 0} 项");
        LoadParams();
        SetParamStatus($"已保存：类别 {s.LixiangCategories?.Count ?? 0} 项 · 单位 {s.LixiangUnits?.Count ?? 0} 项" +
                       (s.LixiangCategories == null || s.LixiangUnits == null ? "（空表项自动取现有数据）" : ""), true);
    }

    private void OnExtractParams(object sender, RoutedEventArgs e)
    {
        TxtCategories.Text = ListToLines(App.State.Lixiang.DistinctCategories());
        TxtUnits.Text = ListToLines(App.State.Lixiang.DistinctUnits());
        SetParamStatus("已从现有数据提取，点「保存参数」生效", true);
    }

    private void SetParamStatus(string text, bool ok = true)
    {
        TxtParamStatus.Text = text ?? "";
        TxtParamStatus.SetResourceReference(TextBlock.ForegroundProperty, ok ? "B.Ok" : "B.Danger");
    }

    // ==================================================================
    // 配置文件
    // ==================================================================

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(AppPaths.SettingsFile);
            if (System.IO.Directory.Exists(dir)) Process.Start("explorer.exe", $"\"{dir}\"");
        }
        catch (Exception ex)
        {
            SetBasicStatus("打开目录失败：" + ex.Message, false);
        }
    }

    private void OnReloadSettings(object sender, RoutedEventArgs e)
    {
        SettingsStore.Reload();
        ThemeManager.Apply(SettingsStore.Current.Theme);
        LoadForm();
        LoadParams();
        App.NotifySettingsChanged();
        SetBasicStatus("已重新读取配置文件", true);
    }

    // ==================================================================
    // 状态提示
    // ==================================================================

    private void SetBasicStatus(string text, bool ok = true)
    {
        TxtBasicStatus.Text = text ?? "";
        TxtBasicStatus.SetResourceReference(TextBlock.ForegroundProperty, ok ? "B.Ok" : "B.Danger");
    }

    private void SetAccountStatus(string text, bool ok = true)
    {
        TxtAccountStatus.Text = text ?? "";
        TxtAccountStatus.SetResourceReference(TextBlock.ForegroundProperty, ok ? "B.Ok" : "B.Danger");
    }
}
