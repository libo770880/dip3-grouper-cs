using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Dip3Grouper;
using Dip3GrouperApp.Desktop.Theming;

namespace Dip3GrouperApp.Desktop;

/// <summary>主窗口：前台四个页签（单条分组 / 批量分组 / 病种库查询 / 立项指南查询）+ 后台管理入口。</summary>
public partial class MainWindow : Window
{
    private AdminWindow _admin;
    /// <summary>工具栏抽屉是否展开（面板本身是单例控件，挂在 GuideLayer 里）</summary>
    private bool _guideOpen;
    /// <summary>工具栏抽屉宽度，与 MainWindow.xaml 里 GuideDrawer 的 Width 一致</summary>
    private const double GuideWidth = 782;
    private TrayIcon _tray;
    private bool _exiting;   // 托盘「退出程序」/ 系统关机置位后：不再弹确认框、不再转托盘

    // ---- 通知公告（服务端后台发布 → 本机轮询取回）----
    /// <summary>轮询周期：绿色版形态下不做长连接，60 秒一次足够，进程重启也不会掉线</summary>
    private static readonly TimeSpan NoticeInterval = TimeSpan.FromSeconds(60);

    private NoticeReadStore _noticeRead;
    private DispatcherTimer _noticeTimer;
    private List<NoticeView> _noticeItems = new();

    /// <summary>本次会话已弹过托盘气泡的消息 id（同一条只打扰一次）</summary>
    private readonly HashSet<string> _noticeToasted = new(StringComparer.Ordinal);

    /// <summary>打开面板那一刻的未读集合 —— 打开即已读，但仍让这几条保持高亮，便于一眼看出「新的」</summary>
    private readonly HashSet<string> _noticeFresh = new(StringComparer.Ordinal);

    /// <summary>当前横幅对应的消息 id（点 ✕ 时要知道隐藏谁）</summary>
    private string _noticeBarId = "";

    /// <summary>拉取失败的说明（面板底部显示；不影响正常使用）</summary>
    private string _noticeStatus = "";

    public MainWindow()
    {
        InitializeComponent();

        ApplyBranding();
        InitTheme();

        StateChanged += (_, _) => UpdateMaxIcon();
        UpdateMaxIcon();

        // 小屏适配：尺寸钳进工作区（1024×768 低配机 + 高 DPI 缩放下，默认 1300×870
        // 居中会把顶部标题栏整条推出屏幕外）；宽度不足时导航切紧凑模式。
        SizeChanged += (_, _) => ApplyCompactNav();

        // Ctrl+G：任何页面都能直达「工具栏 → 病理开具指引」（开病历时随手一按，不用先切页）
        PreviewKeyDown += OnMainKey;
        if (ToolHost != null) ToolHost.CloseRequested += OnGuideCloseRequested;

        // 后台改完软件名称/单位后要立刻反映到标题与品牌区
        App.SettingsChanged += ApplyBranding;
        App.SettingsChanged += OnAppSettingsChanged;

        // 版本号只维护 UpdateChecker.CurrentVersion 一处：菜单项提示与右侧版本号都从常量取
        if (UpdateChecker.CurrentVersion != null)
            TxtUpdate.Text = "v" + UpdateChecker.CurrentVersion;
        BtnUpdate.ToolTip = $"检查更新（当前版本 v{UpdateChecker.CurrentVersion}）：到 GitHub Release 下载新版本";

        // 「更多」下拉：点按钮toggle；因为 StaysOpen=False 时点按钮会先关面板，
        // 记下关闭时刻，250ms 内的再次点击视为「刚关掉」，不再重开（否则点一下关不掉）
        MorePopup.Opened += (_, _) => PositionMorePopup();
        MorePopup.Closed += (_, _) => _moreClosedAt = DateTime.UtcNow;
        SizeChanged += (_, _) => { if (MorePopup.IsOpen) PositionMorePopup(); };
        Closed += (_, _) =>
        {
            App.SettingsChanged -= ApplyBranding;
            App.SettingsChanged -= OnAppSettingsChanged;
            ThemeManager.Changed -= OnThemeApplied;
            _noticeTimer?.Stop();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WinFit.ClampToWorkArea(this);
    }

    /// <summary>设置变化时顺带重取一次消息（数据源地址可能刚被配上）</summary>
    private void OnAppSettingsChanged() => RefreshNotices(false);

    /// <summary>按设置刷新窗口标题与品牌区（软件名称 / 单位副名 / 基层兜底状态）</summary>
    private void ApplyBranding()
    {
        var s = SettingsStore.Current;
        Title = s.AppName;
        TxtLogo.Text = SettingsStore.LogoText;
        TxtAppName.Text = s.AppName;
        TxtSubtitle.Text = string.IsNullOrWhiteSpace(s.AppSubName)
            ? (App.Jiceng ? "基层病种兜底：开启" : "基层病种兜底：关闭")
            : s.AppSubName;
        TxtSubtitle.ToolTip = $"数据目录：{AppPaths.Root}";
        TxtDensity.Text = UiPrefs.Label;
        BtnDensity.ToolTip = $"表格行高：{UiPrefs.Label}（当前 {UiPrefs.RowHeight:F0}px，点一下切到"
                             + (UiPrefs.Compact ? "标准" : "紧凑") + "）";
        ApplyCompactNav();   // 改名后按当前宽度重新决定紧凑档（未显示时内部有守卫，直接跳过）
    }

    // ==================================================================
    // 小屏紧凑导航（2026-10-21）：头部 6 个页签 + 品牌 + 右侧操作区约 1160 DIP 宽，
    // 窗口不够宽时整条溢出被裁。按宽度分两档收缩，拉大窗口自动还原。
    // ==================================================================

    /// <summary>当前紧凑档：0 标准 / 1 紧凑 / 2 极紧凑（-1 = 尚未算过）。</summary>
    private int _navCompact = -1;

    private void ApplyCompactNav()
    {
        if (ActualWidth <= 0) return;   // 离屏构造 / 未显示时不判断
        int mode = ActualWidth < 900 ? 2 : ActualWidth < 1180 ? 1 : 0;
        if (mode == _navCompact) return;
        _navCompact = mode;

        // 档 1：副标题收起、页签边距 18→6；档 2：只留 logo、页签再缩字号
        TxtSubtitle.Visibility = mode == 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtAppName.Visibility = mode == 2 ? Visibility.Collapsed : Visibility.Visible;

        double pad = mode == 0 ? 18 : mode == 1 ? 6 : 5;
        double size = mode == 2 ? 11.5 : 14;
        foreach (var rb in new[] { NavSingle, NavBatch, NavLib, NavLx, NavDz, NavGf })
        {
            rb.Padding = new Thickness(pad, 0, pad, 0);
            rb.FontSize = size;
        }
        // 右侧操作区已收进「更多」下拉（2026-11-24），菜单项不随窗口宽度伸缩，这里不需要再调
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        ShowView();
        PrewarmDataPages();
        EnsureTray();   // 主窗口真正显示后再挂托盘（自检离屏构造不弹托盘图标）
        InitNotices();  // 通知公告轮询（无数据源时只把入口置灰，不发请求）
        CheckDataUpdateQuietly();
    }

    // ==================================================================
    // 界面主题（前台直接换肤，2026-10-28）
    //
    // 以前只能进「后台管理 → 系统设置」换肤，普通用户不会去找。这里把同一份
    // ThemeCatalog 搬到标题栏：点开就是 5 张色卡，选中即生效、即落盘。
    //
    // 色卡里的三块色（底色/卡片/强调）取的是**各主题自己的颜色**，故意不用
    // DynamicResource —— 它们要展示"那套主题长什么样"，必须固定；卡片外框与
    // 文字才是当前主题色。深色主题下这一层因此是深底浅字，不会白块闪烁。
    // ==================================================================

    private readonly List<Views.Admin.ThemeCardVm> _themeCards = new();

    /// <summary>诊断/自检用：配色浮层的根元素（Popup 未打开时不在视觉树里，需单独渲染）</summary>
    internal FrameworkElement ThemePanel => ThemePopup.Child as FrameworkElement;

    /// <summary>诊断/自检用：5 张主题卡</summary>
    internal IReadOnlyList<Views.Admin.ThemeCardVm> ThemeCards => _themeCards;

    /// <summary>诊断/自检用：当前勾选的主题 id</summary>
    internal string SelectedThemeId
        => _themeCards.FirstOrDefault(c => c.IsSelected)?.Id ?? "";

    private void InitTheme()
    {
        foreach (var t in ThemeCatalog.All)
        {
            _themeCards.Add(new Views.Admin.ThemeCardVm
            {
                Id = t.Id,
                Name = t.Name,
                Hint = t.Hint,
                BgBrush = t.Brush("Bg"),
                CardBrush = t.Brush("Card"),
                AccentBrush = t.Brush("Accent"),
            });
        }
        ThemeHost.ItemsSource = _themeCards;

        ThemePopup.PlacementTarget = BtnMore;
        ThemePopup.Opened += (_, _) => PositionThemePopup();
        SizeChanged += (_, _) => { if (ThemePopup.IsOpen) PositionThemePopup(); };

        // 自己点选与后台改主题都走 ThemeManager.Changed 这一条路，选中态只有一处维护
        ThemeManager.Changed += OnThemeApplied;
        SyncThemeCards(ThemeManager.CurrentId);
    }

    /// <summary>「更多」下拉：行高 / 主题 / 检查更新 / 后台管理（2026-11-24 从标题栏一排按钮收进来）</summary>
    private DateTime _moreClosedAt = DateTime.MinValue;

    private void OnToggleMore(object sender, RoutedEventArgs e)
    {
        if (MorePopup.IsOpen) { MorePopup.IsOpen = false; return; }
        // 面板因「点到按钮算外部点击」先关了一次，这里别再立刻弹回来
        if ((DateTime.UtcNow - _moreClosedAt).TotalMilliseconds < 250) return;
        MorePopup.IsOpen = true;
    }

    /// <summary>诊断/自检用：下拉面板根元素（Popup 未打开时不在视觉树里，需单独渲染）</summary>
    internal FrameworkElement MorePanel => MorePopup.Child as FrameworkElement;

    /// <summary>诊断用：直接打开下拉（不模拟点击，避免 Popup 抢焦）</summary>
    internal void DiagOpenMore()
    {
        PositionMorePopup();
        MorePopup.IsOpen = true;
    }

    /// <summary>菜单右边缘与「更多」按钮对齐；越出窗口右侧时夹回来</summary>
    private void PositionMorePopup()
    {
        if (MorePopup.Child is not FrameworkElement fe) return;
        var w = fe.Width + fe.Margin.Left + fe.Margin.Right;
        var off = -(w - BtnMore.ActualWidth - 4);
        var left = BtnMore.TranslatePoint(new Point(0, 0), this).X + off;
        if (left + w > ActualWidth - 8) off -= (left + w) - (ActualWidth - 8);
        if (left < 8) off += 8 - left;
        MorePopup.HorizontalOffset = off;
    }

    private void OnToggleTheme(object sender, RoutedEventArgs e)
    {
        if (ThemePopup.IsOpen) { ThemePopup.IsOpen = false; return; }
        MorePopup.IsOpen = false;      // 从「更多」里点主题：先收菜单，再出配色面板
        ThemePopup.IsOpen = true;
    }

    /// <summary>
    /// 手动检查更新：期间按钮置灰防连点，结果（新版弹窗 / 已最新 / 失败）用弹窗明确告知。
    /// 启动时的静默检查在 App.RunWithSplashAsync 里，两处共用 UpdateChecker。
    /// </summary>
    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        MorePopup.IsOpen = false;
        BtnUpdate.IsEnabled = false;
        TxtUpdate.Text = "检查中…";
        try
        {
            await UpdateChecker.CheckManualAsync(this);
        }
        finally
        {
            TxtUpdate.Text = "v" + UpdateChecker.CurrentVersion;   // 右侧恢复成版本号（结果已用弹窗告知）
            BtnUpdate.IsEnabled = true;
        }
    }

    /// <summary>紧凑 / 标准行高切换：写全局资源即可让所有绑定 DgRowHeight 的表同步变，无需逐页刷新</summary>
    private void OnToggleDensity(object sender, RoutedEventArgs e)
    {
        var compact = UiPrefs.Toggle();
        TxtDensity.Text = UiPrefs.Label;
        BtnDensity.ToolTip = $"表格行高：{UiPrefs.Label}（当前 {UiPrefs.RowHeight:F0}px，点一下切到"
                             + (compact ? "标准" : "紧凑") + "）";
    }

    /// <summary>面板右边缘对齐主题按钮；越出窗口左侧时夹回来（与通知面板同一套算法）</summary>
    private void PositionThemePopup()
    {
        if (ThemePopup.Child is not FrameworkElement fe) return;
        var w = fe.Width + fe.Margin.Left + fe.Margin.Right;
        var off = -(w - BtnMore.ActualWidth - 4);
        var left = BtnMore.TranslatePoint(new Point(0, 0), this).X + off;
        if (left < 8) off += 8 - left;
        ThemePopup.HorizontalOffset = off;
    }

    private void OnPickTheme(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border card || card.Tag is not string id) return;
        if (ThemeManager.CurrentId == id) return;
        ApplyThemeChoice(id);
    }

    private void OnResetTheme(object sender, RoutedEventArgs e)
    {
        if (ThemeManager.CurrentId == ThemeCatalog.DefaultId) return;
        ApplyThemeChoice(ThemeCatalog.DefaultId);
    }

    /// <summary>换肤 + 落盘 + 审计。写盘失败不回滚：当前会话已经换过去了，宁可下次启动回到旧值。</summary>
    private void ApplyThemeChoice(string id)
    {
        ThemeManager.Apply(id);
        SettingsStore.Current.Theme = id;
        SettingsStore.Save();
        AuditLog.Write(AuditAction.Setting, "界面主题",
            $"前台切换界面主题为「{ThemeCatalog.Get(id).Name}」");
    }

    /// <summary>自检用：与点击色卡走完全相同的路径（换肤 + 落盘 + 审计 + 选中态）</summary>
    internal void PickThemeForTest(string id) => ApplyThemeChoice(id);

    private void OnThemeApplied(string id)
    {
        SyncThemeCards(id);

        // 这两处的颜色是"建元素时取好"的，不跟 DynamicResource 自动变，得显式重算：
        //  · 批量结果表的行画刷（BatchRowVm 的计算属性）
        //  · 通知面板（Foreground/Background 由 Brush() 静态取色）
        ViewBatch.RepaintForTheme();
        if (NoticePopup.IsOpen) UpdateNoticeUi();
    }

    private void SyncThemeCards(string id)
    {
        foreach (var c in _themeCards) c.IsSelected = c.Id == id;
        var th = ThemeCatalog.Get(id);
        TxtThemeCur.Text = th.Id == ThemeCatalog.DefaultId
            ? $"当前：{th.Name}（默认）"
            : $"当前：{th.Name}";
    }

    // ==================================================================
    // 通知公告
    // ==================================================================

    /// <summary>挂上消息入口：定位弹出面板、拉一次、起定时器。</summary>
    private void InitNotices()
    {
        _noticeRead ??= new NoticeReadStore();
        NoticePopup.PlacementTarget = BtnNotice;
        NoticePopup.Opened += (_, _) =>
        {
            PositionNoticePopup();
            // 打开面板 = 已读（角标清零），但把这一批记下来用于高亮，方便看出哪几条是新的
            _noticeFresh.Clear();
            foreach (var n in _noticeRead.Unread(_noticeItems)) _noticeFresh.Add(n.Id);
            _noticeRead.MarkAllRead(_noticeItems);
            UpdateNoticeUi();
        };
        SizeChanged += (_, _) => { if (NoticePopup.IsOpen) PositionNoticePopup(); };

        UpdateNoticeUi();          // 先按「未配置数据源」的样子画一次，避免闪
        RefreshNotices(true);

        _noticeTimer = new DispatcherTimer { Interval = NoticeInterval };
        _noticeTimer.Tick += (_, _) => RefreshNotices(true);
        _noticeTimer.Start();
    }

    /// <summary>面板右边缘对齐铃铛按钮右边缘；越出窗口左侧时夹回来。</summary>
    private void PositionNoticePopup()
    {
        if (NoticePopup.Child is not FrameworkElement fe) return;
        var w = fe.Width + fe.Margin.Left + fe.Margin.Right;
        var off = -(w - BtnNotice.ActualWidth - 4);
        var left = BtnNotice.TranslatePoint(new Point(0, 0), this).X + off;
        if (left < 8) off += 8 - left;
        NoticePopup.HorizontalOffset = off;
    }

    /// <param name="notifyNew">发现新的未读消息时是否弹一次托盘气泡（打开面板/刷新时用 false）</param>
    private async void RefreshNotices(bool notifyNew)
    {
        if (!SettingsStore.HasDataSource)
        {
            _noticeItems = new List<NoticeView>();
            _noticeStatus = "未配置数据源地址。到「后台管理 → 数据更新」里填写服务器地址后即可接收通知。";
            UpdateNoticeUi();
            return;
        }

        try
        {
            var res = await NoticeClient.FetchAsync(SettingsStore.DataSourceUrl);
            if (!res.Ok)
            {
                _noticeStatus = "取消息失败：" + res.Message;
                UpdateNoticeUi();
                return;
            }

            _noticeRead.KeepOnly(res.Items.Select(n => n.Id));   // 服务端已删的消息，本机记录也清掉
            _noticeItems = res.Items;
            _noticeStatus = "";

            var unread = _noticeRead.Unread(_noticeItems);
            UpdateNoticeUi();

            if (!notifyNew) return;
            var fresh = unread.Where(n => _noticeToasted.Add(n.Id)).ToList();
            if (fresh.Count == 0) return;
            _tray?.Balloon(fresh.Count == 1
                ? "新通知：" + fresh[0].Title
                : $"收到 {fresh.Count} 条新通知，最新：{fresh[0].Title}");
        }
        catch (Exception ex)
        {
            // 读盘/解析异常都不该让界面炸掉：记一句状态，保持上一轮的内容
            _noticeStatus = "取消息失败：" + ex.Message;
            UpdateNoticeUi();
        }
    }

    /// <summary>按当前数据重画入口：角标、横幅、面板（面板开着才重画）。</summary>
    private void UpdateNoticeUi()
    {
        var unread = _noticeRead?.Unread(_noticeItems) ?? new List<NoticeView>();
        var n = unread.Count;

        if (n > 0)
        {
            TxtBadge.Text = n > 99 ? "99+" : n.ToString();
            NoticeBadge.Visibility = Visibility.Visible;
        }
        else
        {
            NoticeBadge.Visibility = Visibility.Collapsed;
        }

        var online = SettingsStore.HasDataSource;
        TxtBell.Opacity = online ? 1 : 0.45;
        BtnNotice.ToolTip = !online ? "通知公告：未配置数据源地址"
            : n > 0 ? $"通知公告：{n} 条未读"
            : "通知公告";

        // 钉住的消息 → 常驻横幅（本机点过 ✕ 的不再出现）
        var bar = _noticeItems.FirstOrDefault(x => x.Pinned && !_noticeRead.IsHidden(x.Id));
        if (bar == null)
        {
            _noticeBarId = "";
            NoticeBar.Visibility = Visibility.Collapsed;
        }
        else
        {
            _noticeBarId = bar.Id;
            TxtBarTag.Text = bar.LevelText;
            NoticeBarTag.Background = LevelBrush(bar.Level);
            TxtBarText.Text = string.IsNullOrWhiteSpace(bar.Title) ? FirstLine(bar.Body) : bar.Title;
            NoticeBar.Visibility = Visibility.Visible;
        }

        if (NoticePopup.IsOpen) RenderNoticeList();

        TxtNoticeFoot.Text = _noticeStatus.Length > 0
            ? _noticeStatus
            : $"最后刷新 {DateTime.Now:HH:mm} · 共 {_noticeItems.Count} 条"
              + (online ? "" : " · 未配置数据源");
    }

    /// <summary>重画消息列表。</summary>
    private void RenderNoticeList()
    {
        NoticeList.Children.Clear();
        TxtNoticeHead.Text = _noticeItems.Count == 0 ? "通知公告" : $"通知公告 · {_noticeItems.Count} 条";

        if (_noticeItems.Count == 0)
        {
            NoticeList.Children.Add(new TextBlock
            {
                Text = _noticeStatus.Length > 0 ? _noticeStatus : "暂无通知。",
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 8, 2, 8),
                Foreground = Brush("B.Muted", Brushes.Gray),
            });
            return;
        }

        foreach (var n in _noticeItems)
            NoticeList.Children.Add(BuildNoticeRow(n, _noticeFresh.Contains(n.Id) || !_noticeRead.IsRead(n.Id)));
    }

    /// <summary>一条消息（级别色标 + 标题 + 时间 + 正文三行；点一下展开全文）。</summary>
    private FrameworkElement BuildNoticeRow(NoticeView n, bool unread)
    {
        const double bodyLine = 19;
        var body = new TextBlock
        {
            Text = n.Body ?? "",
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = bodyLine,
            MaxHeight = 3 * bodyLine,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 5, 0, 0),
            Foreground = Brush("B.InfoText", Brushes.Black),
        };

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tag = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Background = LevelBrush(n.Level),
            Child = new TextBlock
            {
                Text = n.LevelText,
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("B.White", Brushes.White),
            },
        };
        Grid.SetColumn(tag, 0);

        var title = new TextBlock
        {
            Text = n.Title,
            FontSize = 13,
            FontWeight = unread ? FontWeights.Bold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
            Foreground = Brush(unread ? "B.Text" : "B.Muted", Brushes.Black),
        };
        Grid.SetColumn(title, 1);

        var time = new TextBlock
        {
            Text = n.TimeText,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("B.Faint", Brushes.Gray),
        };
        Grid.SetColumn(time, 2);

        head.Children.Add(tag);
        head.Children.Add(title);
        head.Children.Add(time);

        var stack = new StackPanel();
        stack.Children.Add(head);
        if (!string.IsNullOrWhiteSpace(n.Body)) stack.Children.Add(body);

        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 9, 10, 9),
            Margin = new Thickness(0, 0, 0, 8),
            Background = Brush(unread ? "B.InfoBg" : "B.SoftBg", Brushes.White),
            BorderBrush = Brush("B.Border", Brushes.LightGray),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Child = stack,
        };

        if (!string.IsNullOrWhiteSpace(n.Body))
        {
            card.MouseLeftButtonUp += (_, _) =>
            {
                var expand = !double.IsPositiveInfinity(body.MaxHeight);
                body.MaxHeight = expand ? double.PositiveInfinity : 3 * bodyLine;
                body.TextTrimming = expand ? TextTrimming.None : TextTrimming.CharacterEllipsis;
            };
        }
        return card;
    }

    private static Brush LevelBrush(string level) => level switch
    {
        NoticeStore.LevelDanger => Brush("B.Danger", Brushes.IndianRed),
        NoticeStore.LevelWarn => Brush("B.Warn", Brushes.Orange),
        _ => Brush("B.Accent", Brushes.SteelBlue),
    };

    /// <summary>取主题画刷（主题切换后每次重取，避免留下旧主题的颜色）</summary>
    private static Brush Brush(string key, Brush fallback)
        => Application.Current?.TryFindResource(key) as Brush ?? fallback;

    /// <summary>标题为空时用正文首行兜底（与后台列表一致）</summary>
    private static string FirstLine(string text)
        => (text ?? "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "(无标题)";

    private void OnToggleNotice(object sender, RoutedEventArgs e)
    {
        if (!SettingsStore.HasDataSource)
        {
            MessageBox.Show(this,
                "尚未配置数据源地址，收不到服务端推送的通知。\n\n"
                + "请打开「后台管理 → 数据更新 → 数据源配置」，填写服务端地址"
                + "（例如 http://10.0.0.8:3210），保存后即可接收；\n"
                + "若服务端设了共享口令，把口令一并填上。",
                "通知公告", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (NoticePopup.IsOpen)
        {
            NoticePopup.IsOpen = false;
            return;
        }
        NoticePopup.IsOpen = true;   // Opened 里会刷新与标记已读
    }

    private void OnNoticeReload(object sender, RoutedEventArgs e) => RefreshNotices(false);

    private void OnNoticeAllRead(object sender, RoutedEventArgs e)
    {
        _noticeRead?.MarkAllRead(_noticeItems);
        _noticeFresh.Clear();
        UpdateNoticeUi();
    }

    private void OnBarView(object sender, RoutedEventArgs e)
    {
        if (!SettingsStore.HasDataSource) { OnToggleNotice(sender, e); return; }
        NoticePopup.IsOpen = true;
    }

    /// <summary>横幅上的 ✕：只让本机不再显示这条（不动服务端数据，也不影响别人）</summary>
    private void OnBarHide(object sender, RoutedEventArgs e)
    {
        if (_noticeBarId.Length == 0) return;
        _noticeRead?.Hide(_noticeBarId);
        UpdateNoticeUi();
    }

    /// <summary>
    /// 启动后静默跑一次「跟服务端对齐」：先同步公共设置（软件名称 / 副名 / 立项指南参数），
    /// 再检查业务数据差异。
    ///
    /// <para>两者都只提示、不自动改数据 —— 替换数据文件属于需要人确认的动作，托盘气泡点进去
    /// 「后台管理 → 数据更新」由用户自己决定何时更新。公共设置相反：它只是展示类配置，
    /// 管理员在服务端改完就该全院跟着变，所以直接落地并提示一句。</para>
    /// </summary>
    private async void CheckDataUpdateQuietly()
    {
        if (!SettingsStore.HasDataSource) return;

        if (SettingsStore.FollowServerSettings)
        {
            try
            {
                var sr = await PublicSettingsClient.SyncAsync(SettingsStore.DataSourceUrl);
                if (sr is { Ok: true, Changed: true })
                {
                    App.NotifySettingsChanged();   // 窗口标题 / 品牌区立刻跟随
                    _tray?.Balloon("已按服务端更新本机设置：" + string.Join("、", sr.Applied));
                }
            }
            catch
            {
                // 静默失败：对方没开机、网线没插都是常态
            }
        }

        if (!SettingsStore.AutoCheckUpdate) return;
        try
        {
            var res = await DataUpdateClient.CheckAsync(SettingsStore.DataSourceUrl);
            if (res is { Ok: true, PendingCount: > 0 })
                _tray?.Balloon($"发现 {res.PendingCount} 个数据文件可更新（约 {DataCatalog.SizeText(res.PendingBytes)}），"
                               + "可在「后台管理 → 数据更新」中查看。");

            // 顺手看一眼程序本体有没有新版（同一数据源地址，接口不同）
            var app = await AppUpdateClient.CheckAsync(SettingsStore.DataSourceUrl);
            if (app is { Ok: true, HasUpdate: true })
                _tray?.Balloon($"发现新版本 {app.RemoteVersion}（当前 {AppVersion.Current}）"
                               + (app.Mandatory ? "，此为必需更新。" : "，")
                               + "可在「后台管理 → 数据更新 → 客户端更新」中升级。");
        }
        catch
        {
            // 静默检查失败不打扰用户（对方没开机、网线没插都是常态）
        }
    }

    /// <summary>
    /// 启动后按「空闲优先级」分片预热数据查询三页（立项指南 / 德州物价 / 技术规范）：
    /// 载入数据 + 强制布局一次，把首次进入页面的首屏 50 行元素树构建成本提前消化。
    /// 分三片而不是一次做完，避免启动后一口气卡住界面（Idle 优先级下用户操作永远优先）。
    /// </summary>
    private void PrewarmDataPages()
    {
        // 内容区尺寸（扣掉左侧导航条与内边距），拿不到就用兜底值
        var w = ActualWidth > 240 ? ActualWidth - 240 : 1100;
        var h = ActualHeight > 200 ? ActualHeight - 150 : 700;

        // 数据量大的页签最优先预热（病种库 1.1 万+ 行最重），首次点击才不会卡顿；
        // 预热本身在 UI 线程同步跑（每页约 0.5~2s），排在 ApplicationIdle 用户基本无感
        var queue = new Queue<Action>(new Action[]
        {
            () => { ViewLib.EnsureLoaded(); WarmPage(ViewLib, w, h, "病种库"); },
            () => { ViewDz.EnsureLoaded();  WarmPage(ViewDz,  w, h, "德州物价"); },
            () => { ViewLx.EnsureLoaded();  WarmPage(ViewLx,  w, h, "立项指南"); },
            () => { ViewGf.EnsureLoaded();  WarmPage(ViewGf,  w, h, "技术规范"); },
        });

        void Step()
        {
            // 用户已经动手点页签了：剩下的预热就是在给自己添堵——每片 0.4~0.6s 的
            // UI 线程占用会在他浏览时再卡两下（「打开后还在卡」的真实来源），立即收工。
            if (_warmAbort) return;
            if (queue.Count == 0) return;
            try { queue.Dequeue()(); } catch { /* 预热失败不影响功能，真正打开时照常加载 */ }
            if (queue.Count > 0)
                Dispatcher.BeginInvoke(new Action(Step), DispatcherPriority.ApplicationIdle);
        }

        Dispatcher.BeginInvoke(new Action(Step), DispatcherPriority.ApplicationIdle);
    }

    /// <summary>用户首次点开任一数据页 → 放弃剩余预热（见 <see cref="PrewarmDataPages"/>）。</summary>
    private bool _warmAbort;

    /// <summary>
    /// 预热一页 = 载数据 + **真的做一次布局**。
    ///
    /// <para>⚠ 关键坑：`Visibility=Collapsed` 的元素 `Measure()` 直接返回 0×0，布局树根本不下传，
    /// DataGrid 一行都不会生成——所以此前只调 `Measure/Arrange` 的预热等于没做，用户首次点开
    /// 还是要现场消化首屏 50 行的模板实例化 + 布局（德州物价 ~1.6s）。
    /// 这里临时置 Visible + Opacity=0 + 不接鼠标，尺寸给足，布局走完再还原可见性：
    /// 页面就在同一个 Grid 单元里叠一下，用户完全看不见，也点不到。</para>
    /// </summary>
    private static void WarmPage(FrameworkElement page, double w, double h, string tag)
    {
        if (page == null || page.Visibility == Visibility.Visible) return;   // 正在看的页不用预热
        var sw = System.Diagnostics.Stopwatch.StartNew();
        page.Visibility = Visibility.Visible;
        page.Opacity = 0;
        page.IsHitTestVisible = false;
        try
        {
            page.Measure(new Size(w, h));
            page.Arrange(new Rect(0, 0, w, h));
            page.UpdateLayout();
        }
        finally
        {
            page.Visibility = Visibility.Collapsed;
            page.Opacity = 1;
            page.IsHitTestVisible = true;
        }
        if (TraceNav) Console.WriteLine($"[prewarm] {tag} {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>诊断开关（--trace-nav）：把预热与页签切换耗时打到控制台，便于量化首开卡顿。</summary>
    internal static bool TraceNav;

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        ShowView();
    }

    /// <summary>诊断用（--show）：直接切到指定页（dz=德州物价 / lx=立项 / gf=规范），供外部探针做鼠标级验证。</summary>
    internal void DiagOpenPage(string page)
    {
        // --show dz:slow —— 白块滑动放慢到 3s，供外部截图探针逐帧核对补间
        Console.WriteLine("[show] page=" + page);
        // jd = 立项指南页的「解读辅导」视图（外部探针/人工核对检查更新入口时用）
        if (string.Equals(page, "jd", StringComparison.OrdinalIgnoreCase))
        {
            NavLx.IsChecked = true;
            ShowView();
            ViewLx.DiagJiedu("", -1);
            return;
        }
        if ((page ?? "").EndsWith(":slow", StringComparison.OrdinalIgnoreCase))
        {
            Controls.Segment.SlideMsOverride = 3000;
            page = page[..^5];
            Console.WriteLine("[show] slow -> " + page + " ms=" + Controls.Segment.SlideMsOverride);
        }
        var rb = (page ?? "").ToLowerInvariant() switch
        {
            "lx" => NavLx,
            "gf" => NavGf,
            "lib" => NavLib,
            "batch" => NavBatch,
            "single" => NavSingle,
            _ => NavDz,
        };
        rb.IsChecked = true;
        ShowView();
    }

    /// <summary>
    /// 跨页跳转：从物价页的「立项指南」链接切到立项指南页并定位条目。
    /// <para>先切页签再定位：立项指南页在 <c>Collapsed</c> 状态下也能检索（布局不受可见性影响），
    /// 但用户看到的是切过去的**结果页**，顺序反了会先看见旧列表再被刷新成目标行。</para>
    /// </summary>
    /// <returns>定位成功返回 true（条目已从指南库消失时返回 false，由调用方提示）。</returns>
    internal bool OpenLixiangAt(string tab, string name)
    {
        NavLx.IsChecked = true;
        // 自检 / 探针窗口没有走到 OnContentRendered，页签互斥组可能还没把其它按钮取消选中；
        // 显式清掉再 ShowView，否则会同时显示两页（或目标页仍是 Collapsed）。
        foreach (var rb in new[] { NavSingle, NavBatch, NavLib, NavDz, NavGf })
            if (rb != null) rb.IsChecked = false;
        NavLx.IsChecked = true;
        ShowView();
        return ViewLx.Locate(tab, name);
    }

    /// <summary>自检 / 探针专用：物价页实例（验证跨页跳转要拿真实宿主窗口）。</summary>
    internal Views.DezhouPriceView DzViewForTest => ViewDz;

    /// <summary>自检 / 探针专用：立项指南页实例。</summary>
    internal Views.LixiangView LxViewForTest => ViewLx;

    /// <summary>
    /// 诊断 / 自检窗口标记：关闭时**不问**「最小化到托盘还是退出」、也不收进托盘，直接退出。
    /// 不设这个标记时，探针关窗会撞上 <see cref="OnClosing"/> 的确认框 —— 离屏跑一遍就弹一个框，
    /// 没人点它就永远挂着，进程也退不掉（只能去任务管理器/托盘手动关）。
    /// </summary>
    private bool _diagWindow;

    /// <summary>把本窗口标记为诊断窗口（见 <see cref="_diagWindow"/>）。必须在 Show 之前调用。</summary>
    internal void EnableDiagMode() => _diagWindow = true;

    /// <summary>
    /// 诊断窗口统一形状：不抢焦点、不进任务栏、**移出可视区**、几乎全透明。
    /// 位置也要真的挪走 —— 只靠 Opacity=0.02 时窗口仍会在桌面左上角闪一下。
    /// </summary>
    internal void MakeInvisibleForDiag()
    {
        _diagWindow = true;
        WindowStyle = WindowStyle.None;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;
        Opacity = 0.01;
        Topmost = false;
    }

    // ==================================================================
    // 页签进入过渡（2026-10-25）：内容淡入 + 顶部渐变进度条
    // ==================================================================

    /// <summary>淡入时长（ms）。离屏探针 / 自检置 0 关闭动画，保证截图确定性。</summary>
    internal static int FadeMs = 160;

    /// <summary>是否显示顶部载入进度条（离屏探针置 false，免得截图里挂着一条动画中的条）。</summary>
    internal static bool LoadBarEnabled = true;

    /// <summary>ShowView 重入序号：进度条载入延迟执行期间用户又点了别的页签时作废旧一轮。</summary>
    private int _showSeq;

    /// <summary>已进入过的页签（只有首次进入才需要现场建界面 / 亮进度条）。</summary>
    private readonly HashSet<string> _visited = new(StringComparer.Ordinal);

    /// <summary>进度条最短显示时长（ms）：首开若被预热消化到只剩几十毫秒，也要让人看见一眼。</summary>
    /// <summary>进度条最短显示时长：数据页首开只要 30ms，条一闪就没等于没做。</summary>
    private const int MinBarMs = 480;

    private DispatcherTimer _barStopTimer;

    private void ShowView()
    {
        var single = NavSingle.IsChecked == true;
        var batch = NavBatch.IsChecked == true;
        var lib = NavLib.IsChecked == true;
        var lx = NavLx.IsChecked == true;
        var dz = NavDz.IsChecked == true;
        var gf = NavGf.IsChecked == true;

        ViewSingle.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
        ViewBatch.Visibility = batch ? Visibility.Visible : Visibility.Collapsed;
        ViewLib.Visibility = lib ? Visibility.Visible : Visibility.Collapsed;
        ViewLx.Visibility = lx ? Visibility.Visible : Visibility.Collapsed;
        ViewDz.Visibility = dz ? Visibility.Visible : Visibility.Collapsed;
        ViewGf.Visibility = gf ? Visibility.Visible : Visibility.Collapsed;

        // 进入过渡：目标页内容淡入（Opacity 动画由渲染线程合成，载入卡 UI 线程时也在动）
        UIElement target = single ? ViewSingle
            : batch ? ViewBatch
            : lib ? ViewLib
            : lx ? ViewLx
            : dz ? ViewDz
            : gf ? ViewGf
            : null;
        if (target != null) FadeIn(target);

        var key = single ? "single" : batch ? "batch" : lib ? "lib" : lx ? "lx" : dz ? "dz" : gf ? "gf" : "";
        if (key.Length == 0) return;

        // 已经进过的页：界面（含首屏行）早已建好，切回来只是可见性切换，不用再亮进度条
        if (!_visited.Add(key))
        {
            if (TraceNav) Console.WriteLine($"[nav] {key} cached");
            return;
        }

        // 单条 / 批量分组没有大数据页，首次进入也不需要进度条
        if (!(lib || lx || dz || gf)) return;

        // ⚠ 轮次序号只能在「确认是首次进入」之后推进。
        // 一次导航可能触发两遍 ShowView（RadioButton 换组时 Unchecked / Checked 各来一次），
        // 若在方法开头就 ++，第二遍会先把这个序号顶掉，第一遍排进队列的载入任务随即被
        // `seq != _showSeq` 判为过期 —— 页面切过去了、数据永远不载，用户看到的就是一整页空白。
        var seq = ++_showSeq;

        // 用户手已经不闲着了：立刻收掉后台预热队列，别在他的浏览间隙里再塞几百毫秒的布局
        if (!_warmAbort)
        {
            _warmAbort = true;
            if (TraceNav) Console.WriteLine("[prewarm] abort (用户已点开数据页)");
        }

        // 首次进入：先亮进度条，让出一帧（Background 低于 Render，渲染先跑）再干活，干完收起。
        // 数据页的 EnsureLoaded 必须在 UI 线程跑（要填表格），这里只是把「显示条」插到载入前。
        StartLoadBar();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            // 等待期间用户切了页签：**数据该载还得载**（EnsureLoaded 幂等，页面已 Collapsed 的
            // UpdateLayout 是 0 成本），只有进度条归新一轮管。
            // 早先这里直接 return，等于把「已记入 _visited 但从未载数据」的页留成永久空白。
            var current = seq == _showSeq;
            long loadMs = 0, layoutMs = 0;
            try
            {
                var l = System.Diagnostics.Stopwatch.StartNew();
                if (lib) ViewLib.EnsureLoaded();
                if (lx) ViewLx.EnsureLoaded();
                if (dz) ViewDz.EnsureLoaded();
                if (gf) ViewGf.EnsureLoaded();
                loadMs = l.ElapsedMilliseconds;

                // 预热若已把首屏渲染做掉，这一步几乎为零；没预热到（如启动预热还没轮到）就在这里补
                var lay = System.Diagnostics.Stopwatch.StartNew();
                target?.UpdateLayout();
                layoutMs = lay.ElapsedMilliseconds;
            }
            finally
            {
                if (TraceNav)
                    Console.WriteLine($"[nav] {key} load {loadMs}ms layout {layoutMs}ms " +
                                      $"total {sw.ElapsedMilliseconds}ms{(current ? "" : " (已被后续导航接管)")}");
                if (current)
                {
                    var rest = MinBarMs - (int)sw.ElapsedMilliseconds;
                    if (rest > 0) StopLoadBarAfter(rest, seq);
                    else StopLoadBar();
                }
            }
        }), DispatcherPriority.Background);
    }

    /// <summary>进度条至少再亮 <paramref name="ms"/> 毫秒（期间用户又切页签则放弃，交给新一轮）。</summary>
    private void StopLoadBarAfter(int ms, int seq)
    {
        _barStopTimer?.Stop();
        _barStopTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        _barStopTimer.Tick += (_, _) =>
        {
            _barStopTimer.Stop();
            if (seq == _showSeq) StopLoadBar();
        };
        _barStopTimer.Start();
    }

    /// <summary>内容淡入：基值保持 1，动画 0→1 覆盖期间（结束后自然回到基值）。</summary>
    private static void FadeIn(UIElement el)
    {
        if (FadeMs <= 0) { el.Opacity = 1; return; }
        el.Opacity = 1;
        el.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(
            0, 1, TimeSpan.FromMilliseconds(FadeMs))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
            },
        });
    }

    /// <summary>渐变滑块开始循环滑动（终点用窗口宽，进度条首帧 ActualWidth 还没算出来）。</summary>
    private void StartLoadBar()
    {
        if (!LoadBarEnabled) return;
        if (TraceNav) Console.WriteLine("[bar] start");
        // 上一轮的淡出可能还没跑完：清掉动画、复位不透明度，否则这轮会从半透明里淡出来
        LoadTrack.BeginAnimation(UIElement.OpacityProperty, null);
        LoadTrack.Opacity = 1;
        LoadTrack.Visibility = Visibility.Visible;
        var w = ActualWidth > 300 ? ActualWidth - 280 : 1000;
        // 从 0 起跑（而不是从 -宽度）：条纹一出现就在视野里，否则前 200ms 整条还在屏幕左侧外，
        // 看起来像「没显示」。900ms 一圈，够慢能看清、够快不拖沓。
        var anim = new System.Windows.Media.Animation.DoubleAnimation(
            0, w, TimeSpan.FromMilliseconds(900))
        {
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
        };
        LoadSlideX.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    /// <summary>
    /// 收尾：滑块先跑到右端（180ms），再淡出（140ms）才收起。
    /// <para>直接「半路消失」看着像卡断——用户会以为加载视觉根本没工作。跑完 + 淡出才是「加载完成」的语义。</para>
    /// </summary>
    private void StopLoadBar()
    {
        if (LoadTrack.Visibility != Visibility.Visible)
        {
            if (TraceNav) Console.WriteLine("[bar] stop(noop)");
            return;
        }
        var end = ActualWidth > 300 ? ActualWidth - 40 : 1000;
        var run = new System.Windows.Media.Animation.DoubleAnimation(
            end, TimeSpan.FromMilliseconds(180));
        run.Completed += (_, _) =>
        {
            var fade = new System.Windows.Media.Animation.DoubleAnimation(
                0, TimeSpan.FromMilliseconds(140));
            fade.Completed += (_, _) =>
            {
                LoadTrack.BeginAnimation(UIElement.OpacityProperty, null);
                LoadTrack.Opacity = 1;
                LoadTrack.Visibility = Visibility.Collapsed;
                LoadSlideX.BeginAnimation(TranslateTransform.XProperty, null);
                LoadSlideX.X = 0;
                if (TraceNav) Console.WriteLine("[bar] stop");
            };
            LoadTrack.BeginAnimation(UIElement.OpacityProperty, fade);
        };
        LoadSlideX.BeginAnimation(TranslateTransform.XProperty, run);
    }

    // ==================================================================
    // 窗口按钮
    // ==================================================================

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // ==================================================================
    // 关闭按钮 → 询问「最小化到托盘」还是「退出程序」
    // ==================================================================

    /// <summary>点关闭按钮的三种去向</summary>
    internal enum CloseRoute { Ask, HideToTray, Exit }

    /// <summary>
    /// 关闭去向的纯决策（不碰 UI、不落盘，自检可直接断言）：
    ///   ① 已在退出流程（托盘「退出程序」/ 系统关机）→ 直接退出，不再打扰；
    ///   ② 开着「关闭时先询问」→ 交给确认框；
    ///   ③ 已经记住选择 → 按记住的行为执行。
    /// </summary>
    internal static CloseRoute ResolveClose(bool exiting, bool askEnabled, bool closeToTray)
        => exiting ? CloseRoute.Exit
            : askEnabled ? CloseRoute.Ask
            : closeToTray ? CloseRoute.HideToTray
            : CloseRoute.Exit;

    /// <summary>本次关闭的去向</summary>
    internal CloseRoute CloseRouteNow
        => ResolveClose(_exiting, SettingsStore.CloseAsk, SettingsStore.CloseToTray);

    /// <summary>点关闭是否应该转「最小化到托盘」（退出置位后永远 false）</summary>
    internal bool ShouldHideOnClose => CloseRouteNow == CloseRoute.HideToTray;

    /// <summary>主窗口显示后挂上托盘图标（重复调用安全）</summary>
    private void EnsureTray() => _tray ??= new TrayIcon(this);

    /// <summary>
    /// 确认框已问过一次、需要直接执行时置位（否则 Close() 会二次进入询问）。
    /// 一次性的：OnClosing 读完立刻复位。
    /// </summary>
    private CloseRoute _forcedRoute = CloseRoute.Ask;

    protected override void OnClosing(CancelEventArgs e)
    {
        // 诊断 / 自检窗口：不问、不进托盘，直接关。否则每跑一次探针就弹一次确认框。
        var route = _diagWindow ? CloseRoute.Exit
            : _forcedRoute != CloseRoute.Ask ? _forcedRoute : CloseRouteNow;
        _forcedRoute = CloseRoute.Ask;

        if (route == CloseRoute.Ask)
        {
            // 拦下本次关闭，把「询问」延后一拍再弹：此刻窗口正处于关闭流程里
            //（_isClosing = true，e.Cancel 要等 OnClosing 返回后才生效），
            // 在这个状态下 ShowDialog(Owner = this) 会触发 WPF 的
            // 「在窗口关闭期间，无法将可见性设置为可见，也无法调用 Show、ShowDialog…」异常。
            // 等这条关闭消息走完、关闭被彻底取消后再问，就是合法状态。
            e.Cancel = true;
            Dispatcher.BeginInvoke(AskThenClose, DispatcherPriority.Background);
            return;
        }

        // 「最小化到托盘」：隐藏窗口保住会话；彻底退出走托盘菜单「退出程序」
        if (route == CloseRoute.HideToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _tray?.Dispose();
        _tray = null;
        base.OnClosing(e);
    }

    /// <summary>
    /// 延后一拍的「先问再关」：由 OnClosing 经 Dispatcher 调度，此时关闭已被彻底取消，
    /// 窗口脱离了关闭流程，ShowDialog(Owner = this) 才是合法调用。
    /// </summary>
    private void AskThenClose()
    {
        if (!IsLoaded || _exiting) return;   // 调度期间窗口没了 / 已转退出流程：不问

        var ask = new CloseConfirmWindow(SettingsStore.Current.AppName) { Owner = this };
        try
        {
            ask.ShowDialog();
        }
        catch (InvalidOperationException)
        {
            // 兜底：极端时序下（如询问期间又被系统请求关闭）仍可能撞上关闭限制，
            // 此时不再打扰用户，按「记住的行为」执行，避免对话框套对话框。
            if (SettingsStore.CloseToTray) { HideToTray(); return; }
            _exiting = true;
            Close();
            return;
        }

        if (ask.Choice == CloseConfirmWindow.CloseChoice.Cancel) return;   // 什么都不做，窗口照旧
        if (ask.Remember) SettingsStore.SetCloseChoice(ask.Choice == CloseConfirmWindow.CloseChoice.HideToTray);

        _forcedRoute = ask.Choice == CloseConfirmWindow.CloseChoice.HideToTray
            ? CloseRoute.HideToTray
            : CloseRoute.Exit;
        Close();                             // 带着结论重新走一遍关闭流程
    }

    /// <summary>系统注销 / 关机：跳过关闭确认直接放行（弹确认框会把关机流程卡住）</summary>
    internal void PrepareSystemShutdown() => _exiting = true;

    /// <summary>点关闭 / 托盘「最小化到托盘」：主窗口与后台窗口一起收进托盘</summary>
    internal void HideToTray()
    {
        EnsureTray();
        if (_admin is { IsVisible: true }) _admin.Hide();
        _tray.HideToTray();
    }

    /// <summary>托盘菜单「退出程序」：放行关闭流程并结束应用</summary>
    internal void ExitFromTray()
    {
        _exiting = true;
        Close();
    }

    /// <summary>托盘菜单「打开后台管理」：与标题栏按钮同一条路径（含登录校验）</summary>
    internal void OpenAdminFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        OnOpenAdmin(null, null);
    }

    private void UpdateMaxIcon()
    {
        if (MaxIcon == null) return;
        MaxIcon.Data = Geometry.Parse(WindowState == WindowState.Maximized
            ? "M 3 0.6 L 10.4 0.6 L 10.4 8 M 0.6 3 L 8 3 L 8 10.4 L 0.6 10.4 Z"
            : "M 0.6 0.6 L 10.4 0.6 L 10.4 10.4 L 0.6 10.4 Z");
        BtnMax.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
    }

    // ==================================================================
    // 后台管理
    // ==================================================================

    // ==================================================================
    // 工具栏（右侧竖排标签 → 抽屉：病理开具指引 + 预留位）
    // —— 标签点开停在工具列表，Ctrl+G 直接进病理；Esc 收起。
    // ==================================================================

    /// <summary>面板自己发的关闭请求（头部 ✕）</summary>
    private void OnGuideCloseRequested(object sender, EventArgs e) => CloseGuide();

    private void OnMainKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.G && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            ToggleGuide(true);   // 快捷键就是为了查病理，直接落到工具里，少点一次
            return;
        }
        // 抽屉展开时，Esc 收起 / Ctrl+F 定位检索框（面板内部按键优先处理）
        if (_guideOpen && ToolHost != null && ToolHost.HandleKey(e)) e.Handled = true;
    }

    /// <summary>侧边竖排标签：点开停在工具栏首页（先看有哪些工具）</summary>
    private void OnOpenGuide(object sender, RoutedEventArgs e) => ToggleGuide(false);

    private void OnCloseGuide(object sender, MouseButtonEventArgs e) => CloseGuide();

    private void OnCloseGuide(object sender, RoutedEventArgs e) => CloseGuide();

    private void ToggleGuide(bool deep)
    {
        if (_guideOpen) CloseGuide();
        else OpenGuide(deep);
    }

    /// <param name="deep">true = 直接进病理开具指引；false = 停在工具栏首页</param>
    private void OpenGuide(bool deep)
    {
        if (_guideOpen) return;
        _guideOpen = true;
        GuideLayer.Visibility = Visibility.Visible;
        GuideTab.Visibility = Visibility.Collapsed;

        // 先赋基值、再 BeginAnimation：滑块从右边缘整幅宽度之外滑进来
        GuideX.BeginAnimation(TranslateTransform.XProperty, null);
        GuideX.X = GuideWidth;
        GuideX.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(GuideWidth, 0,
            TimeSpan.FromMilliseconds(190))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });

        ToolHost?.OpenTool(deep ? ToolPanel.ToolPathology : null);
        ToolHost?.FocusSearch();
    }

    private void CloseGuide()
    {
        if (!_guideOpen) return;
        _guideOpen = false;

        var anim = new DoubleAnimation(GuideX.X, GuideWidth, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        anim.Completed += (_, _) =>
        {
            // 展开态被再次点击时动画可能已被替换：以 _guideOpen 为准，别把新开的抽屉关掉
            if (_guideOpen) return;
            GuideX.BeginAnimation(TranslateTransform.XProperty, null);
            GuideX.X = GuideWidth;
            GuideLayer.Visibility = Visibility.Collapsed;
            GuideTab.Visibility = Visibility.Visible;
        };
        GuideX.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private void OnOpenAdmin(object sender, RoutedEventArgs e)
    {
        MorePopup.IsOpen = false;
        if (_admin == null)
        {
            // 登录开关与凭据都在「后台管理 · 系统设置」里维护（data/settings.json）
            if (SettingsStore.LoginEnabled)
            {
                var login = new LoginWindow { Owner = this };
                if (login.ShowDialog() != true) return;
            }

            _admin = new AdminWindow { Owner = this };
            _admin.Closed += (_, _) => _admin = null;
            _admin.Show();
            return;
        }
        if (_admin.WindowState == WindowState.Minimized) _admin.WindowState = WindowState.Normal;
        _admin.Activate();
    }
}
