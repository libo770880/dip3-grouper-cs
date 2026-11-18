using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Dip3Grouper;
using Dip3GrouperApp.Desktop.Controls;

namespace Dip3GrouperApp.Desktop.Views;

/// <summary>病种库查询（对齐 web/index.html 的 #panelLib）。</summary>
public partial class DiseaseLibView : UserControl
{
    private int _pageSize = 50;
    /// <summary>建下拉 / 设初值时挡住 SelectionChanged（否则会在初始化时触发一次 Load）</summary>
    private bool _building;
    private const string DefaultTab = "bingxiang";

    private bool _loaded;
    /// <summary>数据是否已首载（主窗口决定是否显示载入进度条）。</summary>
    internal bool DataReady => _loaded;
    private string _tab = DefaultTab;
    private int _page = 1;
    /// <summary>序号承接提示的缓存（按区段算一次首行 seq，翻页不重复整表 Flatten）</summary>
    private string _seqHintTab;
    private int _seqHintFirst = 1;

    /// <summary>
    /// 用户手动拖过的列宽，按区段分开记（tab → 列 key → 像素）。
    /// <para>为什么要记：每次 Load 都会重建列，重建就把 WPF 自动转成像素的手拖宽度抹平了。
    /// 不记的话，「拖宽诊断名称 → 翻页」立刻弹回原样，用户会以为拖动根本不生效。</para>
    /// <para>只记用户真拖过的列，没拖过的列继续保持 Star，跟着窗口宽度伸缩。</para>
    /// </summary>
    private readonly Dictionary<string, Dictionary<string, double>> _colWidths = new(StringComparer.Ordinal);

    /// <summary>上次建列用的签名：列没变就不重建，保住用户的手拖结果与滚动位置</summary>
    private string _colSig;

    /// <summary>当前可伸缩列（建列时由 FlatGrid 返回），窗口缩放时按新宽度重新摊分</summary>
    private List<FlatGrid.FlexCol> _flex = new();

    public DiseaseLibView()
    {
        InitializeComponent();
        // 监听表头拖拽结束：Thumb 的 TemplatedParent 就是表头，能直接拿到被拖的那一列。
        // 用 AddHandler(..., handledEventsToo: true) 是因为 Thumb 自己会吃掉事件。
        Grid.AddHandler(Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(OnColResized), true);
        // 列宽是算出来的一次性像素值，窗口变宽/变窄后必须重算，否则拉伸窗口不会跟着铺满
        Grid.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged) FlatGrid.FillFlex(Grid, _flex);
        };
    }

    /// <summary>首次进入该页时加载数据（由 MainWindow 调用）</summary>
    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        BuildTabs();
        BuildPageSizes();
        Load(1);
    }

    /// <summary>
    /// 启动空闲预热：本页数据量最大（1.1 万+ 行），首次渲染的模板实例化 / JIT 成本最高，
    /// 必须在启动空闲期提前消化，否则用户启动后点本页签会明显卡顿。
    /// </summary>
    public void Prewarm(double w, double h)
    {
        EnsureLoaded();
        if (w <= 0) w = 1200;
        if (h <= 0) h = 700;
        Measure(new Size(w, h));
        Arrange(new Rect(0, 0, w, h));
        UpdateLayout();
    }

    // ==================================================================
    // 区段胶囊
    // ==================================================================

    private void BuildTabs()
    {
        var counts = Diseases.SectionCounts(App.State.Config);
        TabsPanel.Children.Clear();
        foreach (var (tab, sec) in counts)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock
            {
                Text = sec.Label,
                VerticalAlignment = VerticalAlignment.Center,
            });
            sp.Children.Add(new TextBlock
            {
                Text = sec.Total.ToString("N0"),
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Opacity = 0.6,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });

            // 注意：IsChecked 在订阅 Checked 之前赋值，避免构造期触发加载
            var rb = new RadioButton
            {
                GroupName = "DiseaseLibTabs",
                Style = (Style)FindResource("Pill"),
                Tag = tab,
                IsChecked = tab == _tab,
                Content = sp,
            };
            rb.Checked += OnTabChecked;
            TabsPanel.Children.Add(rb);
        }
    }

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        var tab = (sender as RadioButton)?.Tag as string;
        if (string.IsNullOrEmpty(tab) || tab == _tab) return;
        _tab = tab;
        Load(1);
    }

    // ==================================================================
    // 加载
    // ==================================================================

    private void OnSearch(object sender, RoutedEventArgs e) => Load(1);

    /// <summary>清空关键词并恢复当前区段默认列表（不切区段、不动分页状态）。</summary>
    private void OnClear(object sender, RoutedEventArgs e)
    {
        QueryBox.Text = "";
        StatusText.Text = "";
        Load(1);
        QueryBox.Focus();
    }

    private void OnQueryKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Load(1);
    }

    private void OnPrev(object sender, RoutedEventArgs e) => Load(_page - 1);

    private void OnNext(object sender, RoutedEventArgs e) => Load(_page + 1);

    private void OnJumpKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (int.TryParse((JumpBox.Text ?? "").Trim(), out var p) && p > 0) Load(p);
        else JumpBox.Text = _page.ToString();
    }

    private void OnPageSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_building) return;
        if (!int.TryParse((PageSizeSel.SelectedItem as ComboBoxItem)?.Tag as string, out var n)) return;
        if (n <= 0 || n == _pageSize) return;
        _pageSize = n;
        Load(1);
    }

    private void BuildPageSizes()
    {
        _building = true;
        PageSizeSel.Items.Clear();
        foreach (var n in new[] { 50, 100, 200 })
            PageSizeSel.Items.Add(new ComboBoxItem
            {
                Content = n + " 条",
                Tag = n.ToString(),
                IsSelected = n == _pageSize,
            });
        _building = false;
    }

    private void Load(int page)
    {
        _page = Math.Max(1, page);
        var q = QueryBox.Text?.Trim() ?? "";
        try
        {
            var r = Diseases.ListRows(App.State.Config, _tab, q, _page, _pageSize);
            BuildColumns(r.Columns, r.Rows);
            Grid.ItemsSource = r.Rows;

            var pages = Math.Max(1, (int)Math.Ceiling(r.Total / (double)r.PageSize));
            PageInfo.Text = $"{r.Label} · 第 {r.Page}/{pages} 页 · 共 {r.Total:N0} 条";
            BtnPrev.IsEnabled = r.Page > 1;
            BtnNext.IsEnabled = r.Page < pages;
            JumpBox.Text = r.Page.ToString();
            StatusText.Text = r.Total == 0 ? "无匹配记录" : "";
            UpdateSeqHint();
        }
        catch (Exception ex)
        {
            Grid.Columns.Clear();
            Grid.ItemsSource = null;
            PageInfo.Text = "";
            BtnPrev.IsEnabled = false;
            BtnNext.IsEnabled = false;
            StatusText.Text = "加载失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 按区段列定义动态建列。
    /// <para>列签名没变就<b>不重建</b> —— 重建会 Clear 掉 Columns，用户刚拖好的宽度和横向滚动位置一起丢。
    /// 翻页、重查、清空都不改列定义，只有切区段才需要真的重建。</para>
    /// </summary>
    private void BuildColumns(List<ColumnDef> columns, IList<Dictionary<string, object>> rows)
    {
        var sig = _tab + "|" + string.Join(";",
            columns.Select(c => $"{c.Key}:{c.Label}:{c.Width}"));
        if (sig == _colSig) return;
        _colSig = sig;

        _colWidths.TryGetValue(_tab, out var saved);
        _flex = FlatGrid.BuildColumns(Grid, columns, this, rows, saved);
        // 建列时表格可能还没量出宽度（首次进入 / 预排版），能算就先算一次，
        // 算不了也没关系 —— 随后的 SizeChanged 会补上。
        FlatGrid.FillFlex(Grid, _flex);
    }

    /// <summary>
    /// 记下用户拖过的列宽。sender 是表头模板里的 Thumb，
    /// TemplatedParent 即 DataGridColumnHeader，其 Column 就是刚被拖宽的那一列。
    /// </summary>
    private void OnColResized(object sender, DragCompletedEventArgs e)
    {
        if (sender is not Thumb th || th.TemplatedParent is not DataGridColumnHeader header) return;
        var col = header.Column;
        if (col == null || string.IsNullOrEmpty(col.SortMemberPath)) return;

        // 拖完这一列的 Width 已被 WPF 从 Star 转成 Absolute，取它比 ActualWidth 稳
        var w = col.Width.IsAbsolute ? col.Width.Value : col.ActualWidth;
        if (w <= 0) return;

        if (!_colWidths.TryGetValue(_tab, out var map))
            _colWidths[_tab] = map = new Dictionary<string, double>(StringComparer.Ordinal);
        map[col.SortMemberPath] = w;

        // 拖过的列立刻退出自动分配，否则窗口一缩放就被 FillFlex 算回去，用户的动作白做
        _flex.RemoveAll(f => f.Column == col);
    }

    /// <summary>把当前区段的手拖列宽全部忘掉（表头双击 / 右键「恢复默认列宽」用）</summary>
    internal void ResetColumnWidths()
    {
        _colWidths.Remove(_tab);
        _colSig = null;
        Load(_page);
    }

    /// <summary>诊断用：布局完成后重算一次列宽（离屏截图不派发 SizeChanged）</summary>
    internal void DiagFillFlex() => FlatGrid.FillFlex(Grid, _flex);

    /// <summary>
    /// 「序号不从 1 开始」不是漏数据：并顶核心病种 / 辅助细分等区段的 seq 承接上游区段
    /// （先期分组占 1–19），本段第一行是 20。不解释一句，用户会以为丢了 19 条。
    /// 起点按区段缓存，翻页不重复整表 Flatten。
    /// </summary>
    private void UpdateSeqHint()
    {
        if (_seqHintTab != _tab)
        {
            _seqHintTab = _tab;
            _seqHintFirst = 1;
            try
            {
                var one = Diseases.ListRows(App.State.Config, _tab, "", 1, 1);
                if (one.Rows.Count > 0 && one.Rows[0].TryGetValue("seq", out var v))
                    _seqHintFirst = Math.Max(1, Convert.ToInt32(v));
            }
            catch { _seqHintFirst = 1; }
        }
        if (_seqHintFirst <= 1)
        {
            SeqHint.Visibility = Visibility.Collapsed;
            SeqHint.Text = "";
            return;
        }
        SeqHint.Text = $"ⓘ 序号承接前序区段（1–{_seqHintFirst - 1}），本区段从 {_seqHintFirst} 起";
        SeqHint.ToolTip = "DIP 3.0 病种库的序号按区段连续编号，切到「先期分组」可看到前 19 条";
        SeqHint.Visibility = Visibility.Visible;
    }

    // ==================================================================
    // 诊断用（离屏截图 / 自检）
    // ==================================================================

    /// <summary>
    /// 直接切到指定区段。**不依赖 RadioButton.Checked 事件** —— 离屏渲染不跑消息循环，
    /// 事件派发不可靠；这里显式同步胶囊选中态再走一次 Load，与用户点击后的状态一致。
    /// </summary>
    internal void DiagTab(string tab)
    {
        EnsureLoaded();
        if (!Diseases.ByTab.ContainsKey(tab ?? "")) return;
        _tab = tab;
        foreach (var rb in TabsPanel.Children.OfType<RadioButton>())
            rb.IsChecked = (rb.Tag as string) == _tab;
        Load(1);
    }

    /// <summary>当前区段（自检断言用）</summary>
    internal string CurrentTab => _tab;

    /// <summary>当前页行数（自检断言用）</summary>
    internal int RowCount => Grid.ItemsSource is System.Collections.IEnumerable en
        ? en.Cast<object>().Count() : 0;

    /// <summary>当前页状态栏文案（自检断言用：「无匹配记录」等）</summary>
    internal string StatusForTest => StatusText.Text ?? "";
}
