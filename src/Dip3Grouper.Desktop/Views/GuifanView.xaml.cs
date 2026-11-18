using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Dip3Grouper;

namespace Dip3GrouperApp.Desktop.Views;

/// <summary>技术规范表格的一行。</summary>
public class GfRowVm
{
    public string Code { get; set; }
    public string Name { get; set; }
    public string Category { get; set; }
    public string Unit { get; set; }
    public string Difficulty { get; set; }
    public string Risk { get; set; }
    public string HrValue { get; set; }
    public string Bill { get; set; }

    private List<LxBlock> _blocks;

    /// <summary>
    /// 详情卡的字段块 + 跨库关联块：**懒构建**——只在行被选中（详情展开）时才算，
    /// 否则首页 50 行每行都要做十几次跨库相似度查询，白等一次跨库索引构建。
    /// </summary>
    public Func<List<LxBlock>> BlocksFactory { get; set; }
    public List<LxBlock> Blocks => _blocks ??= BlocksFactory?.Invoke() ?? new List<LxBlock>();

    /// <summary>详情卡里的「技术难度 / 风险程度 / 资源消耗」三条量表（规范分值均为 0~100）</summary>
    public List<GfMeter> Meters { get; set; }

    public bool HasUnit => !string.IsNullOrWhiteSpace(Unit);
    public string UnitChip => string.IsNullOrWhiteSpace(Unit) ? "" : "单位 " + Unit;

    private static string Nz(string v) => string.IsNullOrWhiteSpace(v) ? "—" : v;
}

/// <summary>详情卡量表行：标签 + 分值 + 彩色进度条（非数值时整行隐藏）。</summary>
public class GfMeter
{
    public string Label { get; set; }
    public string Text { get; set; }
    public double Value { get; set; }
    public Brush BarColor { get; set; }
}

/// <summary>
/// 全国医疗服务项目技术规范（2023 年版）查询页。
/// 数据来自 data/guifan.json（2162 页规范正文的大表解析结果，11 章 11,516 个项目）。
/// 列表按 类别 / 章 筛选 + 关键词检索（编码前缀 / 名称 / 内涵 / 耗材 / 拼音），
/// 选中行展开详情卡展示 17 个要素中的全部字段。
/// </summary>
public partial class GuifanView : UserControl
{
    private const int PageSize = 50;

    private static readonly (string Prop, string Header, double Width, bool Align, bool Mono)[] Cols =
    {
        ("Code", "项目编码", 92, false, true),
        ("Name", "项目名称", 0, false, false),
        ("Unit", "单位", 56, false, false),
        ("Difficulty", "技术难度", 80, true, true),
        ("Risk", "风险程度", 80, true, true),
        ("HrValue", "人力消耗相对值", 118, true, true),
        ("Bill", "收费票据分类", 100, false, false),
        ("Category", "类别", 64, false, false),
    };

    private bool _loaded;
    /// <summary>数据是否已首载（主窗口决定是否显示载入进度条）。</summary>
    internal bool DataReady => _loaded;
    private bool _building;
    private string _cat = "";
    private string _chap = "";
    private string _unit = "";
    private string _mat = "";
    private int _page = 1;

    /// <summary>键入防抖：1.15 万条全表扫描约 40ms/次，连续输入时只在停顿后跑一次</summary>
    private readonly Debounce _debounce;

    private DataGridColumn _nameCol;

    public GuifanView()
    {
        InitializeComponent();
        BuildColumns();
        // WPF DataGrid 的 star 列分配不可靠（会被压到最小宽 20，名称列整个消失），
        // 改为显式把「表格宽度 − 固定列合计」分给名称列
        GfGrid.SizeChanged += (_, _) => FitNameColumn();
        _debounce = new Debounce(280, () => Load(1));
    }

    /// <summary>名称列吃掉全部剩余宽度（保底 120，其余列固定紧凑宽）</summary>
    private void FitNameColumn()
    {
        if (_nameCol == null || GfGrid.ActualWidth <= 0) return;
        var fixedSum = Cols.Where(c => c.Width > 0).Sum(c => c.Width) + 30;   // + 纵向滚动条/边框
        var w = GfGrid.ActualWidth - fixedSum;
        _nameCol.Width = new DataGridLength(Math.Max(120, w));
    }

    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        BuildCats();
        BuildChaps();
        BuildUnits();
        BuildMaterials();
        UpdateStat();
        Load(1);
    }

    /// <summary>
    /// 启动空闲预热：载入数据并强制布局一次，把「首次进入本页」的元素树构建 + 列生成成本
    /// 提前到启动后空闲时段消化（实测首屏 50 行首次布局 $约 0.5s$，预热后点开页签几乎瞬时）。
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

    private void BuildCats()
    {
        _building = true;
        CatSel.Items.Clear();
        CatSel.Items.Add(new ComboBoxItem { Content = "全部类别", Tag = "" });
        foreach (var c in App.State.Guifan.Categories())
            CatSel.Items.Add(new ComboBoxItem { Content = c, Tag = c });
        CatSel.SelectedIndex = 0;
        _building = false;
    }

    /// <summary>章下拉：随类别联动（类别为空则列全部章）。</summary>
    private void BuildChaps()
    {
        _building = true;
        ChapSel.Items.Clear();
        ChapSel.Items.Add(new ComboBoxItem { Content = "全部章", Tag = "" });
        foreach (var ch in App.State.Guifan.Chapters())
        {
            if (_cat.Length > 0 && ch.Category != _cat) continue;
            ChapSel.Items.Add(new ComboBoxItem
            {
                Content = $"{ch.Name}（{ch.Items:N0}）",
                Tag = ch.Code,
            });
        }
        ChapSel.SelectedIndex = 0;
        _building = false;
    }

    /// <summary>计量单位下拉：按出现次数降序（常用单位排前面），标签带条数</summary>
    private void BuildUnits()
    {
        _building = true;
        UnitSel.Items.Clear();
        UnitSel.Items.Add(new ComboBoxItem { Content = "全部单位", Tag = "" });
        foreach (var kv in App.State.Guifan.UnitCounts())
            UnitSel.Items.Add(new ComboBoxItem { Content = $"{kv.Key}（{kv.Value:N0}）", Tag = kv.Key });
        UnitSel.SelectedIndex = 0;
        _building = false;
    }

    private void BuildMaterials()
    {
        _building = true;
        MatSel.Items.Clear();
        MatSel.Items.Add(new ComboBoxItem { Content = "耗材不限", Tag = "" });
        MatSel.Items.Add(new ComboBoxItem { Content = "含必需耗材", Tag = "req" });
        MatSel.Items.Add(new ComboBoxItem { Content = "无必需耗材", Tag = "none" });
        MatSel.SelectedIndex = 0;
        _building = false;
    }

    private void UpdateStat()
    {
        var s = App.State.Guifan.Stat();
        StatText.Text = s.Total == 0
            ? "尚未导入 data/guifan.json"
            : $"共 {s.Total:N0} 个项目 · {s.ChapterCount} 章 / {s.CategoryCount} 类 · 含耗材 {s.WithMaterial:N0}";
    }

    private void OnCatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_building || !_loaded) return;
        _cat = (CatSel.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        BuildChaps();
        _chap = "";
        Load(1);
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_building || !_loaded) return;
        _chap = (ChapSel.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        _unit = (UnitSel.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        _mat = (MatSel.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        Load(1);
    }

    /// <summary>键入即防抖检索（拼音全拼 / 首字母同样即时生效）</summary>
    private void OnQueryText(object sender, TextChangedEventArgs e)
    {
        if (_building || !_loaded) return;
        _debounce.Poke();
    }

    private void OnSearch(object sender, RoutedEventArgs e)
    {
        _debounce.Cancel();               // 手动查询/回车立即执行，不再等防抖
        Load(1);
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _debounce.Cancel();
        _building = true;                 // 复位各下拉时不触发多次检索
        GfQuery.Text = "";
        _cat = ""; _chap = ""; _unit = ""; _mat = "";
        BuildChaps();
        BuildUnits();
        BuildMaterials();
        CatSel.SelectedIndex = 0;
        ChapSel.SelectedIndex = 0;
        UnitSel.SelectedIndex = 0;
        MatSel.SelectedIndex = 0;
        _building = false;
        GfStatus.Text = "";
        Load(1);
        GfQuery.Focus();
    }

    private void OnQueryKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _debounce.Cancel();
        Load(1);
    }

    private void OnPrev(object sender, RoutedEventArgs e) => Load(_page - 1);

    private void OnNext(object sender, RoutedEventArgs e) => Load(_page + 1);

    private void Load(int page)
    {
        _page = Math.Max(1, page);
        var q = GfQuery.Text?.Trim() ?? "";
        try
        {
            var r = App.State.Guifan.ListRows(_cat, _chap, q, _page, PageSize, _unit, _mat);
            GfGrid.ItemsSource = r.Items.Select(ToVm).ToList();

            var pages = Math.Max(1, (int)Math.Ceiling(r.Total / (double)r.PageSize));
            GfPageInfo.Text = $"{r.Label} · 第 {r.Page}/{pages} 页 · 共 {r.Total:N0} 条";
            GfPrev.IsEnabled = r.Page > 1;
            GfNext.IsEnabled = r.Page < pages;

            GfStatus.Text = App.State.Guifan.Items.Count == 0
                ? "未导入 data/guifan.json"
                : r.Total == 0 ? "无匹配记录" : "";
        }
        catch (Exception ex)
        {
            GfGrid.ItemsSource = null;
            GfPageInfo.Text = "";
            GfStatus.Text = "加载失败：" + ex.Message;
        }
    }

    private static readonly Brush MeterBlue = new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5));
    private static readonly Brush MeterOrange = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0C));
    private static readonly Brush MeterGreen = new SolidColorBrush(Color.FromRgb(0x2E, 0xA4, 0x4F));

    private static GfRowVm ToVm(GuifanItem it) => new()
    {
        Code = it.Code,
        Name = Flatten(it.Name),
        Category = it.Category,
        Unit = it.Unit,
        Difficulty = it.Difficulty,
        Risk = it.Risk,
        HrValue = it.HrValue,
        Bill = it.Bill,
        Meters = BuildMeters(it.Difficulty, it.Risk, it.HrValue),
        BlocksFactory = () => BuildBlocks(it),
    };

    /// <summary>技术难度 / 风险程度 / 人力消耗相对值 → 三条量表；规范分值满值 100。（关联弹窗复用）</summary>
    internal static List<GfMeter> BuildMeters(string diff, string risk, string hr)
    {
        var list = new List<GfMeter>();
        void Add(string label, string raw, Brush color)
        {
            if (!double.TryParse((raw ?? "").Trim(), out var v)) return;
            if (v < 0) v = 0;
            if (v > 100) v = 100;
            list.Add(new GfMeter
            {
                Label = label,
                Text = v.ToString("0.00"),
                Value = v,
                BarColor = color,
            });
        }
        Add("技术难度", diff, MeterBlue);
        Add("风险程度", risk, MeterOrange);
        Add("资源消耗", hr, MeterGreen);
        return list;
    }

    private static string Flatten(string v) => Regex.Replace(v ?? "", @"\s*\n\s*", "");

    /// <summary>详情字段块（末段为跨库关联块）。（关联弹窗复用）</summary>
    internal static List<LxBlock> BuildBlocks(GuifanItem it)
    {
        var blocks = new List<LxBlock>();
        void Add(string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            blocks.Add(new LxBlock
            {
                Label = label,
                Lines = new List<LxLine> { new() { Text = Flatten(value) } },
            });
        }
        Add("英文名称", it.En);
        Add("项目内涵", it.Content);
        Add("必需耗材", it.Required);
        Add("可选耗材", it.Optional);
        Add("低值耗材分档", it.LowGrade);
        Add("人力消耗及耗时", it.Manpower);
        Add("计量单位", it.Unit);
        Add("特殊情况调整系数", it.Factor);
        Add("说明", it.Note);
        Add("收费票据分类", it.Bill);
        Add("会计科目分类", it.Account);
        Add("病案首页费用分类", it.CaseFee);
        var where = string.Join(" · ", new[] { it.Chapter, it.Section, it.Group }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        if (where.Length > 0) Add("所属章节", $"{where}　（PDF 第 {it.Page} 页）");
        blocks.AddRange(CrossMap.ForGuifan(it.Name));
        return blocks;
    }

    private void BuildColumns()
    {
        var mono = (FontFamily)FindResource("Font.Mono");
        var baseCell = (Style)FindResource("DgCell");

        foreach (var (prop, header, width, align, isMono) in Cols)
        {
            var binding = new Binding(prop);

            var textStyle = new Style(typeof(TextBlock));
            textStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            if (align)
            {
                // 数值列居中（先 Stretch，TextAlignment 才能在列宽内生效）
                textStyle.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
                textStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            }
            if (isMono)
            {
                textStyle.Setters.Add(new Setter(TextBlock.FontFamilyProperty, mono));
                textStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, 12.0));
            }

            var cellStyle = new Style(typeof(DataGridCell), baseCell);
            if (align) cellStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            cellStyle.Setters.Add(new Setter(ToolTipService.ToolTipProperty, binding));

            var col = new DataGridTextColumn
            {
                Header = header,
                Width = width <= 0
                    ? new DataGridLength(120)
                    : new DataGridLength(width),
                Binding = binding,
                ElementStyle = textStyle,
                CellStyle = cellStyle,
                // 左对齐列也要显式挂 DgHead：列级 HeaderStyle 会覆盖 DataGrid.ColumnHeaderStyle，
                // 留 null 会让这些列掉回默认表头模板（白底），与灰蓝表头拼成两段（2026-10-27）
                HeaderStyle = (Style)FindResource(align ? "DgHeadCenter" : "DgHead"),
            };
            if (width <= 0) _nameCol = col;
            GfGrid.Columns.Add(col);
        }
        FitNameColumn();
    }
}
