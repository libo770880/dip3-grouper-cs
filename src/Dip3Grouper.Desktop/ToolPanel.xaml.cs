using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Dip3Grouper;

namespace Dip3GrouperApp.Desktop;

/// <summary>
/// 右侧「工具栏」抽屉 —— 一个壳，里面按卡片摆工具：
/// 现在有「病理开具指引」「基本物耗指引」「物价解读 AI」，右下留了一个预留位，
/// 以后加工具只往这里加一张卡 + 一个视图，顶部导航、抽屉开关、快捷键都不用动。
///
/// 工具本身仍然各写各的（病理在 <see cref="GuidePanel"/>、物耗在 <see cref="WuhaoPanel"/>、
/// AI 在 <see cref="PriceAiPanel"/>），数据与算法在 Core，网页端走同一份 JSON，
/// 这个壳只负责：列表 / 切换 / 返回 / 关闭。
/// </summary>
public partial class ToolPanel : UserControl
{
    /// <summary>工具 id：病理开具指引（以后新增工具在这里补一个常量）</summary>
    public const string ToolPathology = "pathology";

    /// <summary>工具 id：基本物耗指引</summary>
    public const string ToolWuhao = "wuhao";

    /// <summary>工具 id：物价解读 AI（模型地址 / 密钥由用户本机自配）</summary>
    public const string ToolPriceAi = "priceai";

    private AppState _st;          // XAML 构造时为 null，Init 里回落到 App.State
    private bool _ready;
    private string _tool;          // null = 工具栏首页

    /// <summary>收起抽屉（抽屉由 MainWindow 承载，面板只发信号）</summary>
    public event EventHandler CloseRequested;

    /// <summary>XAML 里直接挂到主窗口用（<see cref="App.State"/> 在 Loaded 时再取）</summary>
    public ToolPanel() : this(null) { }

    public ToolPanel(AppState st)
    {
        InitializeComponent();
        _st = st;

        // 工具自己的关闭 / 返回汇到这里：✕ 关抽屉，‹ 回工具栏首页
        Guide.CloseRequested += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        Guide.BackRequested += (_, _) => ShowHome();
        Guide.CanGoBack = true;

        Wuhao.CloseRequested += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        Wuhao.BackRequested += (_, _) => ShowHome();
        Wuhao.CanGoBack = true;

        PriceAi.CloseRequested += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        PriceAi.BackRequested += (_, _) => ShowHome();
        PriceAi.CanGoBack = true;

        Loaded += (_, _) => Init();
    }

    // ================= 初始化 =================

    private void Init()
    {
        if (_ready) return;
        _st ??= App.State;

        var z = _st.Zhijian;
        z.EnsureLoaded();
        TxtSub.Text = "开单速查 · 计价小工具";
        TxtPathDesc.Text = $"{z.Systems.Count} 个系统 · {z.Entries.Count} 条标本档位 · 德州二级/三级收费";

        var w = _st.Wuhao;
        w.EnsureLoaded();
        TxtWuhaoDesc.Text = $"{w.Items.Count} 条基本物耗 · {w.Edges.Count} 条边界判断 · {w.Meta.Cats} 类条款";

        var lx = _st.Lixiang;
        if (lx.State.Sections.Count == 0) lx.Load();
        var rows = lx.State.Sections.Values.Sum(s => s.Rows.Count);
        var jd = lx.State.Jiedu?.List?.Count ?? 0;
        TxtPriceAiDesc.Text = $"{lx.State.Sections.Count} 类指南 {rows} 条 · {jd} 条解读辅导 · 模型自配";
        _ready = true;
    }

    // ================= 工具切换 =================

    /// <summary>打开指定工具；<paramref name="tool"/> 为空则停在工具栏首页</summary>
    public void OpenTool(string tool)
    {
        Init();
        if (tool == ToolPathology) ShowGuide();
        else if (tool == ToolWuhao) ShowWuhao();
        else if (tool == ToolPriceAi) ShowPriceAi();
        else ShowHome();
    }

    private void ShowHome()
    {
        _tool = null;
        GuideView.Visibility = Visibility.Collapsed;
        WuhaoView.Visibility = Visibility.Collapsed;
        PriceAiView.Visibility = Visibility.Collapsed;
        HomeView.Visibility = Visibility.Visible;
        BtnPathology?.Focus();
    }

    private void ShowGuide()
    {
        _tool = ToolPathology;
        HomeView.Visibility = Visibility.Collapsed;
        WuhaoView.Visibility = Visibility.Collapsed;
        PriceAiView.Visibility = Visibility.Collapsed;
        GuideView.Visibility = Visibility.Visible;
        Guide.FocusSearch();
    }

    private void ShowWuhao()
    {
        _tool = ToolWuhao;
        HomeView.Visibility = Visibility.Collapsed;
        GuideView.Visibility = Visibility.Collapsed;
        PriceAiView.Visibility = Visibility.Collapsed;
        WuhaoView.Visibility = Visibility.Visible;
        Wuhao.FocusSearch();
    }

    private void ShowPriceAi()
    {
        _tool = ToolPriceAi;
        HomeView.Visibility = Visibility.Collapsed;
        GuideView.Visibility = Visibility.Collapsed;
        WuhaoView.Visibility = Visibility.Collapsed;
        PriceAiView.Visibility = Visibility.Visible;
        PriceAi.FocusSearch();
    }

    private void OnPickPathology(object sender, RoutedEventArgs e) => ShowGuide();

    private void OnPickWuhao(object sender, RoutedEventArgs e) => ShowWuhao();

    private void OnPickPriceAi(object sender, RoutedEventArgs e) => ShowPriceAi();

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    // ================= 键盘 =================

    /// <summary>抽屉展开时由 MainWindow 转发：Esc 收起抽屉、Ctrl+F 定位检索框</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        // 物价 AI 的设置面板打开时，Esc 先只关设置；再按一次才收起抽屉
        if (e.Key == Key.Escape)
        {
            if (_tool == ToolPriceAi && PriceAi.HandleKey(e)) return true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return true;
        }
        if (_tool == ToolPathology) return Guide.HandleKey(e);
        if (_tool == ToolWuhao) return Wuhao.HandleKey(e);
        if (_tool == ToolPriceAi) return PriceAi.HandleKey(e);
        return false;
    }

    /// <summary>抽屉拉开时把光标放到该去的地方：首页停在病理卡上，进工具后落到检索框</summary>
    public void FocusSearch()
    {
        if (!_ready) Init();
        if (_tool == ToolPathology) Guide.FocusSearch();
        else if (_tool == ToolWuhao) Wuhao.FocusSearch();
        else if (_tool == ToolPriceAi) PriceAi.FocusSearch();
        else BtnPathology?.Focus();
    }

    // ================= 离屏截图 =================

    /// <summary>
    /// 离屏截图专用：tool 为空渲染工具栏首页；pathology 透传给 GuidePanel
    /// （guide:系统:关键词:计价模式:价格档）；wuhao 透传给 WuhaoPanel（wuhao:页签:关键词）；
    /// priceai 透传给 PriceAiPanel（priceai:set 设置面板、priceai:ask:关键词 真实本地检索，均不发网络请求）。
    /// </summary>
    internal FrameworkElement DiagRoot(string tool, string system, string query, string mode = null,
                                       int? wax = null, string level = null)
    {
        Init();
        if (string.IsNullOrEmpty(tool))
        {
            ShowHome();
            return this;
        }
        if (tool == ToolWuhao)
        {
            // wuhao:aliaudit —— 别名体检明细本身就是根视觉，不套工具栏外壳（离屏截图用）
            if (system == "aliaudit") return Wuhao.DiagRoot("aliaudit", query);
            _tool = ToolWuhao;
            HomeView.Visibility = Visibility.Collapsed;
            GuideView.Visibility = Visibility.Collapsed;
            PriceAiView.Visibility = Visibility.Collapsed;
            WuhaoView.Visibility = Visibility.Visible;
            Wuhao.Diag(system, query);
            return this;
        }
        if (tool == ToolPriceAi)
        {
            _tool = ToolPriceAi;
            HomeView.Visibility = Visibility.Collapsed;
            GuideView.Visibility = Visibility.Collapsed;
            WuhaoView.Visibility = Visibility.Collapsed;
            PriceAiView.Visibility = Visibility.Visible;
            PriceAi.Diag(system, query);
            return this;
        }
        _tool = ToolPathology;
        HomeView.Visibility = Visibility.Collapsed;
        WuhaoView.Visibility = Visibility.Collapsed;
        PriceAiView.Visibility = Visibility.Collapsed;
        GuideView.Visibility = Visibility.Visible;
        Guide.Diag(system, query, mode, wax, level);
        return this;
    }
}
