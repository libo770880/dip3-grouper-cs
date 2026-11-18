using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Dip3Grouper;

namespace Dip3GrouperApp.Desktop;

/// <summary>
/// 「病理开具指引」右侧抽屉面板 —— 开病理申请单时随手查：
/// 左列按疾病系统分组，中间是按蜡块档位排好的标本书，底下直接算钱
/// （诊断费 + 标本处理费 + 化学染色费，德州市二级 / 三级两档），
/// 双击一条即可复制成「常规病理 4蜡块｜宫颈活检（分装）、子宫次切」贴进病历 / 申请单。
///
/// 数据与算法都在 Core（<see cref="ZhijianStore"/>），网页端抽屉走同一份 JSON、同一套报价，
/// 两端档位与金额不允许出现差异。
/// </summary>
public partial class GuidePanel : UserControl
{
    private const string All = "全部";

    /// <summary>计价类型 → Core 里的模式名（顺序即下拉顺序）</summary>
    private static readonly (string Label, string Mode)[] Modes =
    {
        ("常规组织病理", ZhijianStore.ModeNormal),
        ("复杂组织病理", ZhijianStore.ModeComplex),
        ("细胞病理·按涂片", ZhijianStore.ModeCell),
        ("TCT·仅诊断+处理", ZhijianStore.ModeTct),
    };

    private AppState _st;   // XAML 构造时为 null，Init 里回落到 App.State
    private readonly List<ZhijianEntry> _view = new();
    private ZhijianEntry _pick;
    private bool _ready;

    /// <summary>关闭请求（抽屉由 MainWindow 承载，面板只发信号）</summary>
    public event EventHandler CloseRequested;

    /// <summary>返回工具栏首页（由 ToolPanel 承载时才有意义）</summary>
    public event EventHandler BackRequested;

    /// <summary>头部是否显示「‹ 返回」（挂在工具栏里时打开，单独截图/测试时关掉）</summary>
    public bool CanGoBack
    {
        get => BtnBack.Visibility == Visibility.Visible;
        set => BtnBack.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>XAML 里直接挂到主窗口用（<see cref="App.State"/> 在 Loaded 时再取）</summary>
    public GuidePanel() : this(null) { }

    public GuidePanel(AppState st)
    {
        InitializeComponent();
        _st = st;
        // 标本名称经常是两行：行高交给内容自己定（DataGrid.RowHeight 是 double，
        // XAML 里写不了 Auto，只能在这里置 NaN）。固定 31px 会把第二行裁掉。
        ItemGrid.RowHeight = double.NaN;
        Loaded += (_, _) => Init();
    }

    // ================= 初始化 =================

    private void Init()
    {
        if (_ready) return;
        _st ??= App.State;
        var z = _st.Zhijian;
        z.EnsureLoaded();

        TxtTitle.Text = string.IsNullOrWhiteSpace(z.Meta.Title) ? "病理开具指引" : z.Meta.Title;
        var level = LevelId() == "3" ? z.Level("3") : z.Level("2");
        TxtSub.Text = $"{z.Meta.Sub} · {z.Meta.Count} 条 · {level.Name}价（诊断 {level.DiagNet:0.##} / " +
                      $"处理 {level.Proc:0.##} / 染色 {level.StainCutUnit:0.##}·{level.Stain:0.##}）";
        TxtSource.Text = $"来源：{z.Meta.Source}";
        TxtRules.Text = string.Join("\n", z.Rules.Select((r, i) => $"（{i + 1}）{r}"));
        PaintSpec();

        // 疾病系统（带条目数）—— 顺序照海报，不自作主张排序
        var rows = new List<SysRow> { new() { Name = All, Count = z.Entries.Count } };
        rows.AddRange(z.Systems.Select(s => new SysRow
        {
            Name = s.Name, Count = z.Entries.Count(x => x.System == s.Name),
        }));
        SysList.ItemsSource = rows;
        SysList.SelectedIndex = 0;

        KindSel.ItemsSource = new List<string> { All, "常规", "复杂" };
        KindSel.SelectedIndex = 0;

        ModeSel.ItemsSource = Modes.Select(m => m.Label).ToList();
        ModeSel.SelectedIndex = 0;

        _ready = true;
        ApplyFilter();
        if (ItemGrid.Items.Count > 0) ItemGrid.SelectedIndex = 0;
        else UpdateQuote();
    }

    private sealed class SysRow
    {
        public string Name { get; set; }
        public int Count { get; set; }
    }

    private string LevelId() => RdoLv3?.IsChecked == true ? "3" : "2";
    private string Mode() => Modes[Math.Max(0, ModeSel.SelectedIndex)].Mode;
    private bool CellMode => Mode() is ZhijianStore.ModeCell or ZhijianStore.ModeTct;

    // ================= 筛选 =================

    private void OnQueryText(object sender, TextChangedEventArgs e)
    {
        if (TxtHint != null)
            TxtHint.Visibility = string.IsNullOrEmpty(TxtQuery.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (_ready) ApplyFilter();
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) ApplyFilter();
    }

    private void OnSystemChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) ApplyFilter();
    }

    private void ApplyFilter()
    {
        var sys = (SysList.SelectedItem as SysRow)?.Name ?? All;
        var kind = KindSel.SelectedItem as string ?? All;
        var q = TxtQuery.Text;

        var rows = _st.Zhijian.Search(q, sys, kind);
        _view.Clear();
        _view.AddRange(rows);
        ItemGrid.ItemsSource = null;
        ItemGrid.ItemsSource = _view;

        TxtStatus.Text = rows.Count == _st.Zhijian.Entries.Count
            ? $"共 {rows.Count} 条 · 双击复制"
            : $"{rows.Count} / {_st.Zhijian.Entries.Count} 条";

        if (rows.Count > 0) ItemGrid.SelectedIndex = 0;
        else
        {
            _pick = null;
            TxtPick.Text = "没有匹配的条目";
            TxtPickSample.Text = "换个关键词试试，例如「宫颈」「ESD」「甲状腺」";
            UpdateQuote();
        }
    }

    // ================= 选中条目 =================

    private void OnEntryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        _pick = ItemGrid.SelectedItem as ZhijianEntry;
        if (_pick == null) return;

        TxtPick.Text = $"{_pick.System} · {_pick.Tier}";
        TxtPickSample.Text = _pick.Sample;

        // 档位里写的就是这类标本的典型蜡块数，选中即预填，再按实际取材微调
        ModeSel.SelectedIndex = _pick.Kind == "复杂" ? 1 : 0;
        TxtWax.Text = _pick.Wax.ToString();
        UpdateQuote();
    }

    private void OnEntryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_pick == null) return;
        var text = $"{_pick.Tier}｜{_pick.Sample}";
        try { Clipboard.SetText(text); } catch { /* 剪贴板被别的程序占用，忽略 */ }
        TxtStatus.Text = "已复制到剪贴板";
    }

    // ================= 报价 =================

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || LblCount == null) return;
        LblCount.Text = CellMode ? "涂片数" : "蜡块数";
        UpdateQuote();
    }

    private void OnLevelChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var z = _st.Zhijian;
        var level = LevelId() == "3" ? z.Level("3") : z.Level("2");
        TxtSub.Text = $"{z.Meta.Sub} · {z.Meta.Count} 条 · {level.Name}价（诊断 {level.DiagNet:0.##} / " +
                      $"处理 {level.Proc:0.##} / 染色 {level.StainCutUnit:0.##}·{level.Stain:0.##}）";
        PaintSpec();
        UpdateQuote();
    }

    private void OnWaxText(object sender, TextChangedEventArgs e)
    {
        if (_ready) UpdateQuote();
    }

    private void OnWaxMinus(object sender, RoutedEventArgs e) => StepWax(-1);
    private void OnWaxPlus(object sender, RoutedEventArgs e) => StepWax(1);

    private void StepWax(int delta)
    {
        var n = Math.Clamp(WaxValue() + delta, 0, 60);
        TxtWax.Text = n.ToString();
        TxtWax.CaretIndex = TxtWax.Text.Length;
    }

    private int WaxValue() =>
        int.TryParse((TxtWax.Text ?? "").Trim(), out var n) ? Math.Max(0, Math.Min(60, n)) : 0;

    private void UpdateQuote()
    {
        if (TxtTotal == null) return;
        var q = _st.Zhijian.Quote(WaxValue(), Mode(), LevelId());

        PriceList.ItemsSource = q.Lines.Select(l => new PriceRow
        {
            Name = l.Name,
            Note = l.Note,
            Qty = l.Qty == 1 ? "×1" : $"×{l.Qty:0.##} · {l.Unit:0.##}",
            Amount = "¥" + l.Amount.ToString("0.##"),
        }).ToList();
        TxtTotal.Text = "¥" + q.Total.ToString("0.##");
    }

    private sealed class PriceRow
    {
        public string Name { get; set; }
        public string Note { get; set; }
        public string Qty { get; set; }
        public string Amount { get; set; }
    }

    // ================= 蜡块速算 =================

    /// <summary>速算：四个常用场景点一下直接填蜡块数（说明里的案例就是这四种）。</summary>
    private void OnQuickPick(object sender, RoutedEventArgs e)
    {
        var items = new (string Label, int Wax)[]
        {
            ("1 袋 1 块 → 1 个蜡块", ZhijianStore.WaxCount(1, 1, 2)),
            ("4 个点位 4 袋 → 4 个蜡块", ZhijianStore.WaxCount(4, 1, 2)),
            ("2 袋共 7 块（5+2）→ 3 个蜡块", ZhijianStore.WaxCount(1, 5, 2) + ZhijianStore.WaxCount(1, 2, 2)),
            ("1 袋 9cm 肿物 → 8 个蜡块", ZhijianStore.WaxCount(1, 1, 9)),
        };

        var menu = new ContextMenu
        {
            PlacementTarget = sender as UIElement,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
        };
        foreach (var (label, wax) in items)
        {
            var mi = new MenuItem { Header = $"{label} · ¥{_st.Zhijian.Quote(wax, Mode(), LevelId()).Total:0.##}" };
            if (TryFindResource("Ctx.Item") is Style st) mi.Style = st;
            mi.Click += (_, _) => SetWax(wax);
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    private void SetWax(int wax)
    {
        if (wax <= 0) return;
        TxtWax.Text = wax.ToString();
        UpdateQuote();
    }

    // ================= 其它 =================

    private void OnToggleRules(object sender, RoutedEventArgs e)
    {
        var show = TxtRules.Visibility != Visibility.Visible;
        TxtRules.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        BtnRules.Content = show ? "蜡块核定规则 ▴" : "蜡块核定规则 ▾";
        if (show)
        {
            TxtSpec.Visibility = Visibility.Collapsed;
            BtnSpec.Content = "专项价目 ▾";
        }
    }

    private void OnToggleSpec(object sender, RoutedEventArgs e)
    {
        var show = TxtSpec.Visibility != Visibility.Visible;
        TxtSpec.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        BtnSpec.Content = show ? "专项价目 ▴" : "专项价目 ▾";
        if (show)
        {
            TxtRules.Visibility = Visibility.Collapsed;
            BtnRules.Content = "蜡块核定规则 ▾";
        }
    }

    /// <summary>专项口径按当前价格档取（二级 / 三级各一套金额）</summary>
    private void PaintSpec()
    {
        if (TxtSpec == null) return;
        var z = _st?.Zhijian;
        if (z == null) return;
        var lines = new List<string>();
        foreach (var sp in z.Special)
        {
            lines.Add($"【{sp.Title}】");
            lines.AddRange(sp.LinesFor(LevelId()).Select((l, i) => $"  {i + 1}. {l}"));
            if (!string.IsNullOrWhiteSpace(sp.Note)) lines.Add($"  注：{sp.Note}");
        }
        TxtSpec.Text = string.Join("\n", lines);
    }

    /// <summary>抽屉拉开时把光标放进检索框（开单场景多半是「直接搜个器官」）</summary>
    public void FocusSearch()
    {
        if (!_ready) Init();
        TxtQuery.Focus();
        TxtQuery.SelectAll();
    }

    /// <summary>头部 ✕：面板不自己销毁，只告诉宿主收起抽屉</summary>
    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>头部 ‹：回到工具栏首页（不掉抽屉，医生还想看别的工具）</summary>
    private void OnBack(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>面板内的 Esc 收起抽屉；Ctrl+F 定位检索框（MainWindow 也转发这两个键）</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseRequested?.Invoke(this, EventArgs.Empty); return true; }
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            TxtQuery.Focus();
            TxtQuery.SelectAll();
            return true;
        }
        return false;
    }

    /// <summary>
    /// 离屏截图专用：不依赖 Loaded 事件，直接把内容根元素交出去渲染（同其它页的 --shot-page）。
    /// </summary>
    internal FrameworkElement DiagRoot(string system, string query, string mode = null, int? wax = null,
                                       string level = null)
    {
        Init();
        Diag(system, query, mode, wax, level);
        return this;
    }

    /// <summary>离屏截图专用：直接切到某个系统 / 关键词 / 档位，并选中首条。</summary>
    internal void Diag(string system, string query, string mode = null, int? wax = null, string level = null)
    {
        if (!_ready) Init();
        if (level == "3") RdoLv3.IsChecked = true;
        if (!string.IsNullOrEmpty(system))
        {
            foreach (var it in SysList.ItemsSource as IEnumerable<SysRow> ?? Array.Empty<SysRow>())
                if (it.Name == system) { SysList.SelectedItem = it; break; }
        }
        if (!string.IsNullOrEmpty(query)) TxtQuery.Text = query;
        ApplyFilter();
        if (ItemGrid.Items.Count > 0) ItemGrid.SelectedIndex = 0;
        // 选中行会按条目自带档位（常规/复杂）回填，所以计价类型要放在选中之后再切
        if (!string.IsNullOrEmpty(mode))
        {
            var i = Array.FindIndex(Modes, m => m.Mode == mode);
            if (i >= 0) ModeSel.SelectedIndex = i;
        }
        if (wax is > 0) TxtWax.Text = wax.Value.ToString();
        UpdateQuote();
    }
}
