using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Dip3Grouper;

namespace Dip3GrouperApp.Desktop.Views;

/// <summary>详情卡里的一行（可带两位编号徽章）</summary>
public class LxLine
{
    public string Chip { get; set; }
    public string Text { get; set; }
    public bool HasChip => !string.IsNullOrEmpty(Chip);

    /// <summary>关联跳转目标库：guifan / dezhou / lixiang（空 = 普通文本行）</summary>
    public string LinkLib { get; set; }

    /// <summary>关联跳转目标条目 Id（三库 Id 均唯一）</summary>
    public int LinkId { get; set; }

    public bool HasLink => LinkId > 0 && !string.IsNullOrEmpty(LinkLib);

    /// <summary>页面内动作（优先于关联跳转）：非空时该行渲染为可点击链接，点击直接执行</summary>
    public Action Click { get; set; }

    /// <summary>是否渲染为链接（关联跳转 或 页面内动作）</summary>
    public bool IsLinkable => HasLink || Click != null;
    public bool IsPlain => !IsLinkable;
}

/// <summary>详情卡里的一个字段块</summary>
public class LxBlock
{
    public string Label { get; set; }
    public List<LxLine> Lines { get; set; }
}

/// <summary>价格项目表格的一行</summary>
public class LxRowVm
{
    public int Gid { get; set; }
    public string Seq { get; set; }
    public string Name { get; set; }
    public string Category { get; set; }
    public string Unit { get; set; }
    public string AddItems { get; set; }
    public string ExtItems { get; set; }
    public string Usage { get; set; }

    /// <summary>所属区段全名（s01 器官移植…），仅用于「区段」列与悬停提示</summary>
    public string Section { get; set; } = "";

    /// <summary>
    /// 「区段」列显示值：只在区段第一行写出来，其余行留空。
    /// 2,627 行里序号会在每个区段重新从 1 开始（s01 的 12 条后紧接 s02 的 1），
    /// 不标出区段边界，用户会以为序号重复 / 数据重复。
    /// </summary>
    public string SectionText { get; set; } = "";

    private List<LxBlock> _blocks;

    /// <summary>详情块懒构建（只在行选中、详情展开时才算跨库关联）</summary>
    public Func<List<LxBlock>> BlocksFactory { get; set; }
    public List<LxBlock> Blocks => _blocks ??= BlocksFactory?.Invoke() ?? new List<LxBlock>();
    public string SeqChip => string.IsNullOrEmpty(Seq) ? "—" : "#" + Seq;
}

/// <summary>解读辅导表格的一行</summary>
public class JdRowVm
{
    public int Issue { get; set; }
    public int No { get; set; }
    public string Q { get; set; }
    public string A { get; set; }
    public string IssueText => $"第{Issue}期";
    public string IssueChip => $"第{Issue}期 · {No}";
}

/// <summary>立项指南查询（对齐 web/index.html 的 #panelLx：价格项目 + 解读辅导）。</summary>
public partial class LixiangView : UserControl
{
    private int _pageSize = 50;
    private static readonly string[] DetailKeys = { "seq", "name", "category", "unit", "addItems", "extItems", "usage" };
    private static readonly Dictionary<string, string> KeyToProp = new()
    {
        ["seq"] = "Seq",
        ["name"] = "Name",
        ["category"] = "Category",
        ["unit"] = "Unit",
        ["addItems"] = "AddItems",
        ["extItems"] = "ExtItems",
        ["usage"] = "Usage",
    };

    private bool _loaded;
    /// <summary>数据是否已首载（主窗口决定是否显示载入进度条）。</summary>
    internal bool DataReady => _loaded;
    private bool _building;
    private bool _jdReady;
    private string _tab = "";
    private string _cat = "";
    private string _flag = "";
    private int _page = 1;

    /// <summary>键入防抖：停顿 280ms 才跑一次全表扫描</summary>
    private readonly Debounce _debounce;

    /// <summary>已构建的列签名：列结构没变就不重建 DataGrid 列（每次检索重建列很费）</summary>
    private string _colSig;

    private DataGridColumn _lxNameCol;

    public LixiangView()
    {
        InitializeComponent();
        // WPF DataGrid 的 star 列分配不可靠（会被压到最小宽 20，名称列整个消失），
        // 改为显式把「表格宽度 − 固定列合计」分给名称列
        LxGrid.SizeChanged += (_, _) => FitLxNameColumn();
        // 解读辅导同理：关掉横向滚动后行详情才能拿到有限宽度（见 FitJdQColumn 注释），
        // 于是「解读问题」列必须自己算宽，不能留给 star
        JdGrid.SizeChanged += (_, _) => FitJdQColumn();
        _debounce = new Debounce(280, () => LoadLx(1));
    }

    /// <summary>
    /// 「解读问题」列吃掉剩余宽度。
    ///
    /// 为什么要关横向滚动：DataGrid 允许横向滚动时，虚拟化面板会按「无限宽」测量每一行，
    /// 展开详情卡里的长回答就按单行撑开——实测 1460 宽的表格里整行量出 3429，
    /// 底部多出一条横条，正文反而被推得很远。关掉之后详情卡拿到有限宽度、正常折行。
    /// 代价是 star 列算不出宽度（会塌成 20px），所以宽度在这里显式分配，与上方 LxGrid 同一套做法。
    /// </summary>
    private void FitJdQColumn()
    {
        if (JdQCol == null || JdGrid.ActualWidth <= 0) return;
        // 固定列 92 + 72，另留 30 给竖滚动条与边框
        var fixedSum = JdGrid.Columns.Where(c => c != JdQCol).Sum(c => c.ActualWidth) + 30;
        JdQCol.Width = new DataGridLength(Math.Max(160, JdGrid.ActualWidth - fixedSum));
    }

    /// <summary>名称列吃掉全部剩余宽度（保底 120，其余列固定紧凑宽）</summary>
    private void FitLxNameColumn()
    {
        if (_lxNameCol == null || LxGrid.ActualWidth <= 0) return;
        var fixedSum = LxGrid.Columns.Where(c => c != _lxNameCol).Sum(c => c.ActualWidth) + 30;
        var w = LxGrid.ActualWidth - fixedSum;
        _lxNameCol.Width = new DataGridLength(Math.Max(120, w));
    }

    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        BuildSections();
        BuildCats();
        BuildFlags();
        BuildLxPageSizes();
        LoadLx(1);
    }

    /// <summary>
    /// 启动空闲预热：载入数据并强制布局一次，把「首次进入本页」的元素树构建成本提前消化
    /// （实测首屏 50 行首次布局约 0.5s，预热后点开页签几乎瞬时）。
    /// 页面在页签容器里可能仍是 Collapsed，手工 Measure/Arrange 不受 Visibility 影响。
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

    /// <summary>类别下拉（跨区段去重，选「全部指南」时也能按类别筛）</summary>
    private void BuildCats()
    {
        _building = true;
        CatSel.Items.Clear();
        CatSel.Items.Add(new ComboBoxItem { Content = "全部类别", Tag = "" });
        foreach (var c in App.State.Lixiang.Categories())
            CatSel.Items.Add(new ComboBoxItem { Content = c, Tag = c });
        CatSel.SelectedIndex = 0;
        _building = false;
    }

    private void BuildFlags()
    {
        _building = true;
        FlagSel.Items.Clear();
        FlagSel.Items.Add(new ComboBoxItem { Content = "不限加收", Tag = "" });
        FlagSel.Items.Add(new ComboBoxItem { Content = "含加收项", Tag = "add" });
        FlagSel.Items.Add(new ComboBoxItem { Content = "含扩展项", Tag = "ext" });
        FlagSel.SelectedIndex = 0;
        _building = false;
    }

    /// <summary>类别 / 加收扩展筛选变化</summary>
    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_building || !_loaded) return;
        _cat = (CatSel.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        _flag = (FlagSel.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        LoadLx(1);
    }

    /// <summary>键入即防抖检索（拼音全拼 / 首字母同样即时生效）</summary>
    private void OnQueryText(object sender, TextChangedEventArgs e)
    {
        if (_building || !_loaded) return;
        _debounce.Poke();
    }

    /// <summary>当前视图是否为「价格项目」（解读辅导首次进入要解析问答并建列表，耗时更长）。</summary>
    private bool _itemsView = true;

    private void OnViewChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var items = SegItems.IsChecked == true;
        if (items == _itemsView) return;
        _itemsView = items;

        // 先亮遮罩并把一帧让出去（DispatcherPriority.Render 排在所有渲染之后，
        // 回调时遮罩已真正画在屏幕上），再同步换视图 —— 否则这 300~600ms 是纯静止画面。
        Busy.Show(items ? "正在载入价格项目…" : "正在载入解读辅导…",
                  items ? "重建区段与类别筛选" : "解析问答并建列表");
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                ItemsView.Visibility = items ? Visibility.Visible : Visibility.Collapsed;
                JieduView.Visibility = items ? Visibility.Collapsed : Visibility.Visible;
                if (!items) EnsureJiedu();
            }
            finally
            {
                Busy.HideAfter();
            }
        }), DispatcherPriority.Render);
    }

    // ==================================================================
    // 价格项目
    // ==================================================================

    /// <summary>类别使用说明全文：右键「复制全文」（正文不可编辑，故由菜单负责整段复制）</summary>
    private void OnCopyUsage(object sender, RoutedEventArgs e) => RowMenu.Copy(UsageText.Text, "已复制使用说明");

    private void OnSelectUsage(object sender, RoutedEventArgs e)
    {
        UsageText.Focus();
        UsageText.SelectAll();
    }

    private void BuildSections()
    {
        _building = true;
        var counts = App.State.Lixiang.SectionCounts();
        var grand = counts.Values.Sum(x => x.Total);

        SectionSel.Items.Clear();
        SectionSel.Items.Add(new ComboBoxItem { Content = $"全部指南（{grand:N0}）", Tag = "" });
        foreach (var (tab, sec) in counts)
            SectionSel.Items.Add(new ComboBoxItem { Content = $"{sec.Label}（{sec.Total:N0}）", Tag = tab });
        SectionSel.SelectedIndex = 0;
        _building = false;
    }

    private void OnSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_building || !_loaded) return;
        _tab = (SectionSel.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        LoadLx(1);
    }

    /// <summary>诊断 / 自检用：当前列表首行项目名（核对定位是否落到目标行）。</summary>
    internal string FirstRowName => LxGrid.Items.Count > 0
        ? (LxGrid.Items[0] as LxRowVm)?.Name ?? ""
        : "";

    /// <summary>诊断 / 自检用：当前选中的区段 tab。</summary>
    internal string CurrentTab => _tab;

    /// <summary>
    /// 外部跳转：定位到某区段里的某个项目（物价「新旧比对 / 现行项目」的「立项指南」链接）。
    /// <para>为什么用「区段 + 关键词」而不是行号：立项指南没有编码列，行在区段内的位次
    /// 会随后台维护（增删改）变化，存行号等于存一个会失效的坐标；
    /// 而区段 + 项目名是稳定的，检索框按名称精确命中后把该行选中即可。</para>
    /// </summary>
    /// <returns>定位成功返回 true；条目已不存在（数据更新过）返回 false。</returns>
    public bool Locate(string tab, string name)
    {
        EnsureLoaded();
        var n = (name ?? "").Trim();
        if (n.Length == 0) return false;

        // 区段下拉按 tab 命中（找不到就退回「全部指南」，宁可宽一点也别定位失败）
        _building = true;
        SectionSel.SelectedIndex = 0;
        for (var i = 0; i < SectionSel.Items.Count; i++)
        {
            if ((SectionSel.Items[i] as ComboBoxItem)?.Tag as string == (tab ?? ""))
            {
                SectionSel.SelectedIndex = i;
                break;
            }
        }
        LxQuery.Text = n;
        _tab = (SectionSel.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        _cat = "";
        _flag = "";
        CatSel.SelectedIndex = 0;
        FlagSel.SelectedIndex = 0;
        _building = false;

        LoadLx(1);
        if (LxGrid.Items.Count == 0) return false;

        LxGrid.SelectedIndex = 0;
        LxGrid.ScrollIntoView(LxGrid.Items[0]);
        LxStatus.Text = $"已定位：{(SectionSel.SelectedItem as ComboBoxItem)?.Content} · {n}";
        return true;
    }

    private void OnLxSearch(object sender, RoutedEventArgs e)
    {
        _debounce.Cancel();
        LoadLx(1);
    }

    /// <summary>清空关键词并恢复当前区段默认列表（不切区段、不动分页状态）。</summary>
    private void OnLxClear(object sender, RoutedEventArgs e)
    {
        _debounce.Cancel();
        _building = true;                 // 复位各下拉时不触发多次检索
        LxQuery.Text = "";
        _tab = ""; _cat = ""; _flag = "";
        SectionSel.SelectedIndex = 0;
        CatSel.SelectedIndex = 0;
        FlagSel.SelectedIndex = 0;
        _building = false;
        LxStatus.Text = "";
        LoadLx(1);
        LxQuery.Focus();
    }

    private void OnLxQueryKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _debounce.Cancel();
        LoadLx(1);
    }

    private void OnLxPrev(object sender, RoutedEventArgs e) => LoadLx(_page - 1);

    private void OnLxNext(object sender, RoutedEventArgs e) => LoadLx(_page + 1);

    private void OnLxJumpKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (int.TryParse((LxJump.Text ?? "").Trim(), out var p) && p > 0) LoadLx(p);
        else LxJump.Text = _page.ToString();
    }

    private void OnLxPageSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_building) return;
        if (!int.TryParse((LxPageSize.SelectedItem as ComboBoxItem)?.Tag as string, out var n)) return;
        if (n <= 0 || n == _pageSize) return;
        _pageSize = n;
        LoadLx(1);
    }

    private void BuildLxPageSizes()
    {
        _building = true;
        LxPageSize.Items.Clear();
        foreach (var n in new[] { 50, 100, 200 })
            LxPageSize.Items.Add(new ComboBoxItem
            {
                Content = n + " 条",
                Tag = n.ToString(),
                IsSelected = n == _pageSize,
            });
        _building = false;
    }

    private void LoadLx(int page)
    {
        _page = Math.Max(1, page);
        var q = LxQuery.Text?.Trim() ?? "";
        try
        {
            var r = App.State.Lixiang.ListRows(_tab, q, _page, _pageSize, _cat, _flag);

            // 类别级使用说明（仅选中具体区段时展示）
            if (!string.IsNullOrEmpty(r.GuideUsage))
            {
                UsageText.Text = r.GuideUsage;
                UsageHead.Text = "本类使用说明（点击展开）";
                UsageBox.Visibility = Visibility.Visible;
            }
            else
            {
                UsageBox.Visibility = Visibility.Collapsed;
            }

            // 列结构签名不变就不重建（每次检索重建 DataGrid 列会明显发涩）
            var sig = string.Join("|", r.Columns.Select(c => c.Key + ":" + c.Label + ":" + c.Width));
            if (sig != _colSig)
            {
                BuildLxColumns(r.Columns);
                _colSig = sig;
            }
            LxGrid.ItemsSource = r.Rows.Select(ToLxVm).ToList();
            MarkSections(LxGrid.ItemsSource as IEnumerable<LxRowVm>);

            var pages = Math.Max(1, (int)Math.Ceiling(r.Total / (double)r.PageSize));
            LxPageInfo.Text = $"{r.Label} · 第 {r.Page}/{pages} 页 · 共 {r.Total:N0} 条";
            LxPrev.IsEnabled = r.Page > 1;
            LxNext.IsEnabled = r.Page < pages;
            LxJump.Text = r.Page.ToString();
            LxStatus.Text = r.Total == 0 ? "无匹配记录" : "";
        }
        catch (Exception ex)
        {
            LxGrid.Columns.Clear();
            LxGrid.ItemsSource = null;
            LxPageInfo.Text = "";
            LxStatus.Text = "加载失败：" + ex.Message;
        }
    }

    private static LxRowVm ToLxVm(LixiangRow row) => new()
    {
        Gid = row.Gid,
        Seq = row.Seq,
        Name = row.Name,
        // 表格内换行以 ；呈现（对齐网页版 replace(/\n+/g, '；')）
        Category = Flatten(row.Category),
        Unit = Flatten(row.Unit),
        AddItems = Flatten(row.AddItems),
        ExtItems = Flatten(row.ExtItems),
        Usage = Flatten(row.Usage),
        BlocksFactory = () => BuildBlocks(row),
    };

    private static string Flatten(string v) => Regex.Replace(v ?? "", @"\n+", "；");

    /// <summary>
    /// 给每行标出所属区段，并只在区段首行写出「区段名」（其余行留空）。
    /// 立项指南 2,627 行的序号是**段内序号**：s01 的 12 条之后，下一条又是「1」（s02 的第一条）。
    /// 界面上不标边界，看起来就是「序号重复、数据重复」。用 Gid 反查区段，不依赖序号。
    /// </summary>
    private static void MarkSections(IEnumerable<LxRowVm> rows)
    {
        if (rows == null) return;
        var map = new Dictionary<int, string>();
        foreach (var sec in App.State.Lixiang.State.Sections.Values)
            foreach (var r in sec.Rows) map[r.Gid] = sec.Label;

        var last = "";
        foreach (var vm in rows)
        {
            vm.Section = map.TryGetValue(vm.Gid, out var label) ? label : "";
            // 首页第一行也写出区段名（否则用户不知道这页从哪个区段开始）
            if (vm.Section.Length > 0 && vm.Section != last)
            {
                vm.SectionText = vm.Section;
                last = vm.Section;
            }
            else
            {
                vm.SectionText = "";
            }
        }
    }

    /// <summary>详情字段块（末段为跨库关联块）。（关联弹窗复用）</summary>
    internal static List<LxBlock> BuildBlocks(LixiangRow row)
    {
        var blocks = new List<LxBlock>();
        void Add(string label, string value, bool withChips)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            blocks.Add(new LxBlock { Label = label, Lines = SplitLines(value, withChips) });
        }
        Add("项目类别", row.Category, false);
        Add("服务产出", row.ServiceOutput, false);
        Add("价格构成", row.PriceComposition, false);
        Add("加收项", row.AddItems, true);
        Add("扩展项", row.ExtItems, true);
        Add("使用说明（计价说明）", row.Usage, false);
        blocks.AddRange(CrossMap.ForLixiang(row.Name));
        return blocks;
    }

    /// <summary>多行字段逐行渲染；"01床旁X线摄影" 这类两位编号前缀渲染为徽章</summary>
    private static List<LxLine> SplitLines(string value, bool withChips)
    {
        return Regex.Split(value, @"\n+")
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Select(s =>
            {
                if (withChips)
                {
                    var m = Regex.Match(s, @"^(\d{2})(?=\D|$)");
                    if (m.Success)
                        return new LxLine { Chip = m.Groups[1].Value, Text = s[m.Groups[1].Value.Length..] };
                }
                return new LxLine { Text = s };
            })
            .ToList();
    }

    private void BuildLxColumns(List<ColumnDef> columns)
    {
        LxGrid.Columns.Clear();
        var mono = (FontFamily)FindResource("Font.Mono");
        var baseCell = (Style)FindResource("DgCell");

        // 「区段」列：只在该区段第一行显示区段名，用来划分「序号重新从 1 开始」的边界。
        // 放在序号左边；宽度 96 从名称列里让出来（FitLxNameColumn 会按剩余宽度重算）。
        var secText = new Style(typeof(TextBlock));
        secText.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        secText.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        secText.Setters.Add(new Setter(TextBlock.FontSizeProperty, 11.5));
        var secCell = new Style(typeof(DataGridCell), baseCell);
        secCell.Setters.Add(new Setter(ToolTipService.ToolTipProperty, new Binding("Section")));
        LxGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "区段",
            Width = new DataGridLength(112),
            Binding = new Binding("SectionText"),
            ElementStyle = secText,
            CellStyle = secCell,
            HeaderStyle = (Style)Application.Current.FindResource("DgHead"),
        });

        foreach (var c in columns.Where(c => DetailKeys.Contains(c.Key)))
        {
            var prop = KeyToProp[c.Key];
            var binding = new Binding(prop);

            var textStyle = new Style(typeof(TextBlock));
            textStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            if (c.Key == "seq")
            {
                textStyle.Setters.Add(new Setter(TextBlock.FontFamilyProperty, mono));
                textStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, 12.0));
            }

            var cellStyle = new Style(typeof(DataGridCell), baseCell);
            cellStyle.Setters.Add(new Setter(ToolTipService.ToolTipProperty, binding));

            var col = new DataGridTextColumn
            {
                Header = c.Label,
                Width = c.Key == "name"
                    ? new DataGridLength(120)
                    : new DataGridLength(Math.Max(70, c.Width ?? 120)),
                Binding = binding,
                ElementStyle = textStyle,
                CellStyle = cellStyle,
            };
            if (c.Key == "name") _lxNameCol = col;
            LxGrid.Columns.Add(col);
        }
        FitLxNameColumn();
    }

    // ==================================================================
    // 解读辅导
    // ==================================================================

    private void EnsureJiedu()
    {
        LoadJd();
        _jdReady = true;
    }

    /// <summary>
    /// 诊断用：直接切到「解读辅导」并按需展开某一行详情（离屏核对详情卡版式）。
    /// 走 OnViewChanged 那条路要等 IsLoaded + Dispatcher 回调，离屏渲染等不到。
    /// </summary>
    internal void DiagJiedu(string q, int sel)
    {
        SegItems.IsChecked = false;
        SegJiedu.IsChecked = true;
        _itemsView = false;
        ItemsView.Visibility = Visibility.Collapsed;
        JieduView.Visibility = Visibility.Visible;
        EnsureJiedu();
        if (!string.IsNullOrEmpty(q)) { JdQuery.Text = q; LoadJd(); }
        if (sel >= 0 && JdGrid.Items.Count > sel) JdGrid.SelectedIndex = sel;
    }

    /// <summary>诊断用：读取表格横向滚动条的真实尺寸关系（Extent 大于 Viewport 才会出现横条）。</summary>
    internal (double Vp, double Ext, double ColQ, Visibility Hbar, double Details) DiagJieduSize()
    {
        JdGrid.UpdateLayout();
        var sv = FindScroll(JdGrid);
        var row = JdGrid.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow;
        var detailW = double.NaN;
        if (row != null)
        {
            var dp = FindChild<System.Windows.Controls.Primitives.DataGridDetailsPresenter>(row);
            if (dp != null) detailW = dp.ActualWidth;
        }
        return (sv?.ViewportWidth ?? 0, sv?.ExtentWidth ?? 0, JdQCol?.ActualWidth ?? 0,
                sv?.ComputedHorizontalScrollBarVisibility ?? Visibility.Visible, detailW);
    }

    internal string DiagJieduMetrics()
    {
        var m = DiagJieduSize();
        return $"viewport={m.Vp:0.#} extent={m.Ext:0.#} hbar={m.Hbar} grid={JdGrid.ActualWidth:0.#} "
             + $"details={m.Details:0.#} cols=[{string.Join(",", JdGrid.Columns.Select(c => c.ActualWidth.ToString("0.#")))}]";
    }

    /// <summary>诊断用：价格项目页展开首行详情（核对同一种行详情卡是否也被撑宽）。</summary>
    internal void DiagItemsRow(int sel)
    {
        EnsureLoaded();
        if (sel >= 0 && LxGrid.Items.Count > sel) LxGrid.SelectedIndex = sel;
    }

    internal string DiagItemsMetrics()
    {
        LxGrid.UpdateLayout();
        var sv = FindScroll(LxGrid);
        return $"viewport={sv?.ViewportWidth:0.#} extent={sv?.ExtentWidth:0.#} "
             + $"hbar={sv?.ComputedHorizontalScrollBarVisibility} grid={LxGrid.ActualWidth:0.#}";
    }

    private static ScrollViewer FindScroll(DependencyObject root)
        => FindChild<ScrollViewer>(root);

    private static T FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        if (root == null) return null;
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            var r = FindChild<T>(c);
            if (r != null) return r;
        }
        return null;
    }

    private void OnIssueChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_building || !_loaded || !_jdReady) return;
        LoadJd();
    }

    private void OnJdSearch(object sender, RoutedEventArgs e) => LoadJd();

    /// <summary>清空关键词并恢复全部期次列表（不动期次下拉的当前选择）。</summary>
    private void OnJdClear(object sender, RoutedEventArgs e)
    {
        JdQuery.Text = "";
        JdStatus.Text = "";
        LoadJd();
        JdQuery.Focus();
    }

    private void OnJdQueryKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        LoadJd();
    }

    private void LoadJd()
    {
        var issue = (IssueSel.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        var q = JdQuery.Text?.Trim() ?? "";
        try
        {
            var r = App.State.Lixiang.JieduList(issue, q);

            // 期次下拉（保留当前选择）
            _building = true;
            var cur = (IssueSel.SelectedItem as ComboBoxItem)?.Tag as string;
            IssueSel.Items.Clear();
            IssueSel.Items.Add(new ComboBoxItem { Content = "全部期次", Tag = "" });
            foreach (var it in r.Issues)
                IssueSel.Items.Add(new ComboBoxItem { Content = $"第{it.Issue}期（{it.Total}条）", Tag = it.Issue.ToString() });
            var idx = 0;
            for (var i = 0; i < IssueSel.Items.Count; i++)
                if ((IssueSel.Items[i] as ComboBoxItem)?.Tag as string == cur) { idx = i; break; }
            IssueSel.SelectedIndex = idx;
            _building = false;

            JdGrid.ItemsSource = r.Items.Select(it => new JdRowVm
            {
                Issue = it.Issue,
                No = it.No,
                Q = it.Q,
                A = it.A,
            }).ToList();

            JdPageInfo.Text = $"共 {r.Issues.Count} 期 · 命中 {r.Total} 条问答";
            JdStatus.Text = r.Total == 0 ? "无匹配的解读条目" : "";
            FitJdQColumn();   // 条数变化会带出/收走竖滚动条，宽度要跟着重算
        }
        catch (Exception ex)
        {
            JdGrid.ItemsSource = null;
            JdStatus.Text = "加载失败：" + ex.Message;
        }
    }
}
