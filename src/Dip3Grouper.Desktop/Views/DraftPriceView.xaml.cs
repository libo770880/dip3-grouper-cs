using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Dip3Grouper;

namespace Dip3GrouperApp.Desktop.Views;

/// <summary>「新项目征求意见」页「新增项目」视图的一行。</summary>
public class DraftRowVm
{
    public string Seq { get; set; }
    public string Code { get; set; }
    public string Name { get; set; }
    public string Category { get; set; }
    public string Unit { get; set; }
    public string Price3 { get; set; }
    public string Price2 { get; set; }
    public string Price1 { get; set; }
    public string Kind { get; set; }
    /// <summary>计价说明（本批附件没有单列的备注列，说明即最长的附注）</summary>
    public string Usage { get; set; }

    public string KindChip => Kind == "add" ? "加收项" : Kind == "ext" ? "扩展项" : "";
    public bool HasKindChip => Kind is "add" or "ext";
    public string PriceLine => $"{Nz(Price3)} / {Nz(Price2)} / {Nz(Price1)}";
    private static string Nz(string v) => string.IsNullOrWhiteSpace(v) ? "—" : v;

    private static string Fmt(string v) => string.IsNullOrWhiteSpace(v) ? "—" : v.Trim();

    public string Price3Text => Fmt(Price3);
    public string Price2Text => Fmt(Price2);
    public string Price1Text => Fmt(Price1);

    private List<LxBlock> _blocks;
    /// <summary>详情块懒构建（选中行才展开）</summary>
    public Func<List<LxBlock>> BlocksFactory { get; set; }
    public List<LxBlock> Blocks => _blocks ??= BlocksFactory?.Invoke() ?? new List<LxBlock>();
}

/// <summary>「废止对照」视图的一行：本批拟作废的原价格项目 → 对应新项目。</summary>
public class DraftCancelRowVm
{
    public string Category { get; set; }
    public string OldCode { get; set; }
    public string OldName { get; set; }
    public string NewCode { get; set; }
    public string NewName { get; set; }
    public string Note { get; set; }

    public string NewCodeText => string.IsNullOrEmpty(NewCode) ? "—" : NewCode;
    public string NewNameText => string.IsNullOrEmpty(NewName) ? "（直接废止，无替代项目）" : NewName;
    public string StatusText => string.IsNullOrEmpty(NewCode) ? "直接废止" : "有替代";
    public bool HasNew => !string.IsNullOrEmpty(NewCode);
}

/// <summary>「映射关系」视图的一行：通知附件映射表的原始两列。</summary>
public class DraftMapRowVm
{
    public string Seq { get; set; }
    public string Category { get; set; }
    public string NewCode { get; set; }
    public string NewName { get; set; }
    public string OldCode { get; set; }
    public string OldName { get; set; }
    public string Note { get; set; }

    public string NewCodeText => string.IsNullOrEmpty(NewCode) ? "—" : NewCode;
    public string OldCodeText => string.IsNullOrEmpty(OldCode) ? "—" : OldCode;
}

/// <summary>「新旧价格比对」视图的一行：新项目 ↔ 旧物价项目，带差额与涨幅。</summary>
public class DraftCompareRowVm
{
    public string Category { get; set; }
    public string NewCode { get; set; }
    public string NewName { get; set; }
    public string NewPrice3 { get; set; }
    public string NewPrice2 { get; set; }
    public string NewPrice1 { get; set; }
    public string OldCode { get; set; }
    public string OldName { get; set; }
    public string OldPrice3 { get; set; }
    public string OldPrice2 { get; set; }
    public string OldPrice1 { get; set; }
    /// <summary>legacy / cancel / none</summary>
    public string OldSource { get; set; } = "none";

    public double? Delta3 { get; set; }
    public double? Delta2 { get; set; }
    public double? Delta1 { get; set; }
    public double? Ratio3 { get; set; }

    private static string Nz(string v) => string.IsNullOrWhiteSpace(v) ? "—" : v;

    /// <summary>价格已由 Store 把加收项公式求值，这里只补空占位。</summary>
    private static string Fmt(string v) => string.IsNullOrWhiteSpace(v) ? "—" : v.Trim();

    public string NewCodeText => string.IsNullOrEmpty(NewCode) ? "—" : NewCode;
    public string NewPriceText => $"{Fmt(NewPrice3)}/{Fmt(NewPrice2)}/{Fmt(NewPrice1)}";
    /// <summary>完整价串（含公式原文，单元格提示用）。</summary>
    public string NewPriceTip => $"{Nz(NewPrice3)} / {Nz(NewPrice2)} / {Nz(NewPrice1)}";
    public string OldCodeText => string.IsNullOrEmpty(OldCode) ? "—" : OldCode;
    public string OldNameText => OldSource switch
    {
        "legacy" => Nz(OldName),
        "cancel" => string.IsNullOrWhiteSpace(OldName) ? "（作废对照，无旧价）" : OldName + "（无旧价）",
        _ => "（本批纯新增项目）",
    };
    /// <summary>旧价三档；无旧价一律「—」（仍占位，方便左右对照）。</summary>
    public string OldPriceText => OldSource == "legacy"
        ? $"{Nz(OldPrice3)}/{Nz(OldPrice2)}/{Nz(OldPrice1)}"
        : "—";

    /// <summary>三级价差额（带正负号）；无可比价显示「—」。</summary>
    public string Delta3Text => Sign(Delta3);
    public string Delta2Text => Sign(Delta2);
    public string Delta1Text => Sign(Delta1);

    private static string Sign(double? v)
        => v is null ? "—" : (v.Value > 0 ? "+" : "") + v.Value.ToString("0.##");

    /// <summary>按三级价算的涨幅文案。</summary>
    public string RatioText => Ratio3 is null ? "—"
        : (Ratio3.Value > 0 ? "+" : "") + Ratio3.Value.ToString("0.#") + "%";

    /// <summary>涨 / 跌 / 平（供取色；无旧价为空）。</summary>
    public string Trend => Delta3 is null ? "" : Delta3 > 0 ? "up" : Delta3 < 0 ? "down" : "flat";

    /// <summary>状态徽标文案。</summary>
    public string StatusText => OldSource switch
    {
        "legacy" => "有旧价可比",
        "cancel" => "仅作废对照",
        _ => "纯新增项目",
    };

    public bool HasOldPrice => OldSource == "legacy";
}

/// <summary>
/// 前台「新项目征求意见」页 —— 尚未正式发布、暂未生效的批次（本批：消化系统等 9 类）。
///
/// <para><b>与「德州物价」页的关系</b>：完全独立的两份数据。<see cref="DezhouPriceView"/> 读
/// <c>data/dezhou-price.json</c>（现行项目 / 作废对照 / 旧版物价，已生效）；本页读
/// <c>data/dezhou-draft.json</c>（拟新增 / 拟作废 / 映射关系，未生效）。本页**只读**，
/// 不参与任何计价与作废判定，主库数据不会被这里影响。</para>
///
/// <para><b>视觉约定</b>：所有行的底色用 <c>B.DraftBg</c>，名称列尾部挂「征求意见稿」胶囊角标，
/// 详情卡里再挂一枚「未生效」。四处取色一律 <c>DynamicResource</c>，换肤跟随（README §8.12）。</para>
/// </summary>
public partial class DraftPriceView : UserControl
{
    /// <summary>本页的四个视图（public：供自检与离屏截图按名切换）。</summary>
    public enum ViewKind { New, Cancel, Map, Compare }

    private ViewKind _view = ViewKind.New;
    private bool _loaded;
    private bool _building;

    private int _page = 1;
    private const int PageSize = 50;
    private int _total;
    private string _query = "";

    private DataGridColumn _nameCol;
    private DataGridColumn _cancelNameCol;
    private DataGridColumn _mapNameCol;
    private DataGridColumn _cmpNewNameCol;
    private DataGridColumn _cmpOldNameCol;

    /// <summary>各视图的排序状态（列属性名 + 方向；null = 未排序）。</summary>
    private readonly Dictionary<ViewKind, (string Prop, ListSortDirection Dir)> _sorts = new();

    /// <summary>排序键：价格列按数值排（公式/解析不出排最后），其余按字符串排。</summary>
    private static object SortKey(string prop, string raw)
        => prop is "NewPriceText" or "OldPriceText" or "Price3Text" or "Price2Text" or "Price1Text"
            ? NumOf(raw)
            : raw ?? "";

    /// <summary>宽松取数（与 DezhouDraftStore.TryNum 同规则；解析不出返回 null → 排最后）。</summary>
    private static double? NumOf(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        if (v.IndexOf("ROUND", StringComparison.OrdinalIgnoreCase) >= 0) return null;
        var sb = new System.Text.StringBuilder();
        var started = false;
        foreach (var c in v)
        {
            if (char.IsAsciiDigit(c) || (c == '.' && started)) { sb.Append(c); started = true; }
            else if (started) break;
        }
        return sb.Length > 0 && double.TryParse(sb.ToString(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var x) ? x : null;
    }

    /// <summary>就地排序：null 键永远排最后（两方向一致），其余按方向翻转。</summary>
    private static void ApplySort<T>(List<T> rows, Func<T, object> key, ListSortDirection? dir)
    {
        if (dir == null) return;
        rows.Sort((a, b) =>
        {
            var ka = key(a); var kb = key(b);
            if (ka == null && kb == null) return 0;
            if (ka == null) return 1;
            if (kb == null) return -1;
            var r = ((IComparable)ka).CompareTo(kb);
            return dir == ListSortDirection.Descending ? -r : r;
        });
    }

    /// <summary>表头点击排序：翻转方向（首点升序），清其余列箭头，整表排序后回到第 1 页。</summary>
    private void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var grid = (DataGrid)sender;
        var dir = e.Column.SortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending : ListSortDirection.Ascending;
        foreach (var c in grid.Columns) c.SortDirection = null;
        e.Column.SortDirection = dir;
        _sorts[_view] = (e.Column.SortMemberPath, dir);
        Load(1);
    }

    /// <summary>恢复某视图上次点过的列箭头（切视图回来箭头还在）。</summary>
    private void RestoreSortIndicator(DataGrid grid, ViewKind kind)
    {
        foreach (var c in grid.Columns) c.SortDirection = null;
        if (_sorts.TryGetValue(kind, out var s))
            foreach (var c in grid.Columns)
                if (c.SortMemberPath == s.Prop) { c.SortDirection = s.Dir; break; }
    }

    private readonly System.Windows.Threading.DispatcherTimer _debounce = new()
    { Interval = TimeSpan.FromMilliseconds(280) };

    public DraftPriceView()
    {
        InitializeComponent();
        _debounce.Tick += (_, _) => { _debounce.Stop(); OnFilterChanged(null, null); };
        Loaded += (_, _) => { EnsureLoaded(); FitColumns(); };
        SizeChanged += (_, _) => FitColumns();
    }

    private static DezhouDraftStore Store => App.State.DezhouDraft;

    /// <summary>使用说明面板当前是否展开（收起后不再占高度）</summary>
    private bool _usageOpen = true;

    // ==================================================================
    // 载入
    // ==================================================================

    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        BuildCats();
        BuildKinds();
        BuildScopes();
        BuildColumns();
        RenderBanner();
        // 三张表的 Visibility 必须按当前视图显式同步一次：
        // 首屏那次 OnViewChanged 被 _loaded 挡掉了，这里补上（否则切到本页会是三表叠在一起）。
        OnViewChanged(null, null);
        UpdateUsagePanel();
    }

    /// <summary>启动空闲预热（与其它页一致的入口，把首屏元素树成本提前消化）。</summary>
    public void Prewarm(double w, double h)
    {
        EnsureLoaded();
        if (w <= 0) w = 1200;
        if (h <= 0) h = 700;
        Measure(new Size(w, h));
        Arrange(new Rect(0, 0, w, h));
        UpdateLayout();
    }

    /// <summary>顶部黄条：批次名 + 说明 + 引用文号；无数据时整条隐藏。</summary>
    private void RenderBanner()
    {
        var st = Store.Stat();
        if (!st.HasData)
        {
            DraftBanner.Visibility = Visibility.Collapsed;
            return;
        }
        DraftBanner.Visibility = Visibility.Visible;
        var label = string.IsNullOrWhiteSpace(Store.Meta?.Label) ? "德州市物价新项目" : Store.Meta.Label;
        var doc = (Store.Meta?.Doc ?? "").Trim();
        TxtDraftTitle.Text = doc.Length > 0 ? $"{label}（{doc}）" : label;
        var note = (Store.Meta?.Note ?? "").Trim();
        if (note.Length == 0)
            note = $"本批共 {st.Items:N0} 项拟新增、{st.Cancels:N0} 项拟作废，正在征求意见，尚未生效。价格以正式发布文件为准。";
        TxtDraftNote.Text = note;
        var refs = st.Refs;
        TxtDraftRefs.Text = refs.Count > 0
            ? "本批作废项目涉及：\n" + string.Join("\n", refs)
            : "";
        TxtDraftRefs.Visibility = refs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ==================================================================
    // 下拉与列
    // ==================================================================

    private void BuildCats()
    {
        _building = true;
        CatSel.Items.Clear();
        CatSel.Items.Add(new ComboBoxItem { Content = "全部类别", Tag = "" });
        foreach (var c in Store.Categories())
            CatSel.Items.Add(new ComboBoxItem
            {
                Content = $"{c}（{Store.Items.Count(i => i.Category == c):N0}）",
                Tag = c,
            });
        CatSel.SelectedIndex = 0;
        _building = false;
    }

    private void BuildKinds()
    {
        _building = true;
        KindSel.Items.Clear();
        KindSel.Items.Add(new ComboBoxItem { Content = "性质不限", Tag = "" });
        KindSel.Items.Add(new ComboBoxItem { Content = "主项目", Tag = "main" });
        KindSel.Items.Add(new ComboBoxItem { Content = "加收项", Tag = "add" });
        KindSel.Items.Add(new ComboBoxItem { Content = "扩展项", Tag = "ext" });
        KindSel.SelectedIndex = 0;
        _building = false;
    }

    /// <summary>比对范围下拉（只在「新旧价格比对」视图可见）。</summary>
    private void BuildScopes()
    {
        _building = true;
        ScopeSel.Items.Clear();
        ScopeSel.Items.Add(new ComboBoxItem { Content = "范围不限", Tag = "" });
        ScopeSel.Items.Add(new ComboBoxItem { Content = "有旧价可比", Tag = "priced" });
        ScopeSel.Items.Add(new ComboBoxItem { Content = "纯新增项目", Tag = "new" });
        ScopeSel.SelectedIndex = 0;
        _building = false;
    }

    /// <summary>新增项目视图的列（名称列吃剩余宽度）。</summary>
    private static readonly (string Prop, string Header, double Width, bool Align, bool Mono)[] NewCols =
    {
        ("Seq", "序号", 48, true, true),
        ("Code", "项目编码", 132, false, true),
        ("Name", "项目名称", 0, false, false),
        ("Category", "类别", 96, false, false),
        ("Unit", "计价单位", 66, false, false),
        ("Price3Text", "三级价格", 70, true, true),
        ("Price2Text", "二级价格", 70, true, true),
        ("Price1Text", "一级价格", 70, true, true),
    };

    private static readonly (string Prop, string Header, double Width, bool Align, bool Mono)[] CancelCols =
    {
        ("Category", "类别", 96, false, false),
        ("OldCode", "拟作废编码", 108, false, true),
        ("OldName", "拟作废项目名称", 0, false, false),
        ("NewCodeText", "对应新编码", 130, false, true),
        ("NewNameText", "对应新项目", 210, false, false),
        ("StatusText", "处理方式", 76, false, false),
    };

    private static readonly (string Prop, string Header, double Width, bool Align, bool Mono)[] MapCols =
    {
        ("Seq", "序号", 48, true, true),
        ("Category", "类别", 92, false, false),
        ("NewCodeText", "新项目编码", 132, false, true),
        ("NewName", "新项目名称", 196, false, false),
        ("OldCodeText", "原项目编码", 104, false, true),
        ("OldName", "原项目名称", 0, false, false),
    };

    /// <summary>
    /// 新旧价格比对：左半新项目、中间旧物价、右侧差额与涨幅。
    /// 固定列尽量窄，把宽度让给两个名称列；新价列提示里保留公式原文。
    /// </summary>
    private static readonly (string Prop, string Header, double Width, bool Align, bool Mono)[] CompareCols =
    {
        ("NewCodeText", "新编码", 124, false, true),
        ("NewName", "新项目名称", 0, false, false),
        ("NewPriceText", "新价(三/二/一)", 118, true, true),
        ("OldCodeText", "旧编码", 88, false, true),
        ("OldNameText", "旧项目名称", 0, false, false),
        ("OldPriceText", "旧价(三/二/一)", 118, true, true),
        ("Delta3Text", "差额(三级)", 96, true, true),
        ("RatioText", "涨幅", 58, true, true),
        ("StatusText", "比对状态", 96, false, false),
    };

    /// <summary>给列的文字样式追加「灰显占位」触发器（取色走语义键）。</summary>
    private static void FgTrigger(Style st, string prop, string value, string colorKey)
        => st.Triggers.Add(new DataTrigger
        {
            Binding = new Binding(prop),
            Value = value,
            Setters = { new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension(colorKey)) },
        });

    private void BuildColumns()
    {
        var mono = (FontFamily)FindResource("Font.Mono");
        var baseCell = (Style)FindResource("DgCell");

        foreach (var (prop, header, width, align, isMono) in NewCols)
        {
            // 价格一律是「算好的钱」（Store 已把加收项的公式求值），无需再缩显
            var col = MakeTextCol(prop, header, width, align, isMono, mono, baseCell);
            if (width <= 0) _nameCol = col;
            DraftGrid.Columns.Add(col);
        }
        foreach (var (prop, header, width, align, isMono) in CancelCols)
        {
            var col = MakeTextCol(prop, header, width, align, isMono, mono, baseCell);
            if (width <= 0) _cancelNameCol = col;
            CancelGrid.Columns.Add(col);
        }
        foreach (var (prop, header, width, align, isMono) in MapCols)
        {
            var col = MakeTextCol(prop, header, width, align, isMono, mono, baseCell);
            if (width <= 0) _mapNameCol = col;
            MapGrid.Columns.Add(col);
        }
        foreach (var (prop, header, width, align, isMono) in CompareCols)
        {
            // 涨跌列（差额三级 / 涨幅）按行级 Trend 着色：涨红跌绿（中国行情惯例），
            // 取色走语义键 B.Danger / B.Ok，换肤跟随；其余列走普通文本列
            var col = prop is "Delta3Text" or "RatioText"
                ? MakeTrendCol(prop, header, width, mono, baseCell)
                : MakeTextCol(prop, header, width, align, isMono, mono, baseCell,
                      st =>
                      {
                          // 无旧价的占位灰显（B.Muted），「仅作废对照」用警示色 —— 换肤跟随
                          if (prop == "OldNameText" || prop == "OldPriceText")
                              FgTrigger(st, "HasOldPrice", "False", "B.Muted");
                          if (prop == "StatusText")
                          {
                              FgTrigger(st, "OldSource", "cancel", "B.Warn");
                              FgTrigger(st, "OldSource", "none", "B.Muted");
                          }
                      });
            if (width <= 0) _cmpNewNameCol = col;
            if (prop == "OldNameText") _cmpOldNameCol = col;
            CompareGrid.Columns.Add(col);
        }
        // 新价列的完整价串（含公式原文）放提示里
        foreach (var c in CompareGrid.Columns)
            if (c is DataGridTextColumn t && c.SortMemberPath == "NewPriceText")
            {
                t.CellStyle = new Style(typeof(DataGridCell), baseCell)
                {
                    Setters =
                    {
                        new Setter(ToolTipService.ToolTipProperty, new Binding("NewPriceTip")),
                        new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Right),
                    },
                };
            }
    }

    /// <summary>造文本列。第四参数 = 数值列：价格 / 序号这类短数值**居中**（与物价四视图口径一致）。</summary>
    private static DataGridTextColumn MakeTextCol(string prop, string header, double width,
        bool align, bool isMono, FontFamily mono, Style baseCell, Action<Style> decorate = null)
    {
        var binding = new Binding(prop);
        var textStyle = new Style(typeof(TextBlock));
        textStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        if (align)
        {
            // 先撑满单元格，TextAlignment 才在列宽内生效
            textStyle.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
            textStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
        }
        if (isMono)
        {
            textStyle.Setters.Add(new Setter(TextBlock.FontFamilyProperty, mono));
            textStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, 12.0));
        }
        decorate?.Invoke(textStyle);
        var cellStyle = new Style(typeof(DataGridCell), baseCell);
        if (align) cellStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        cellStyle.Setters.Add(new Setter(ToolTipService.ToolTipProperty, binding));
        return new DataGridTextColumn
        {
            Header = header,
            Width = width <= 0 ? new DataGridLength(120) : new DataGridLength(width),
            Binding = binding,
            ElementStyle = textStyle,
            CellStyle = cellStyle,
            // 左对齐列也要显式挂 DgHead：列级 HeaderStyle 会覆盖 DataGrid.ColumnHeaderStyle，
            // 留 null 会让这些列掉回默认表头模板（白底），与灰蓝表头拼成两段（2026-10-27）
            HeaderStyle = (Style)Application.Current.FindResource(
                align ? "DgHeadCenter" : "DgHead"),
        };
    }

    /// <summary>
    /// 涨跌列：按 <see cref="DraftCompareRowVm.Trend"/>（up/down/flat）着色 ——
    /// 涨 = <c>B.Danger</c>（红）、跌 = <c>B.Ok</c>（绿）、平/无 = 默认文字色。
    /// 取色一律 DynamicResource（README §8.12），三态 TextBlock 前景色各自独立设置。
    /// </summary>
    private static DataGridTextColumn MakeTrendCol(string prop, string header, double width,
        FontFamily mono, Style baseCell)
    {
        var binding = new Binding(prop);
        var textStyle = new Style(typeof(TextBlock));
        textStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        textStyle.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Right));
        textStyle.Setters.Add(new Setter(TextBlock.FontFamilyProperty, mono));
        textStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, 12.0));
        textStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
        textStyle.Triggers.Add(new DataTrigger
        {
            Binding = new Binding("Trend"),
            Value = "up",
            Setters = { new Setter(TextBlock.ForegroundProperty,
                new DynamicResourceExtension("B.Danger")) },
        });
        textStyle.Triggers.Add(new DataTrigger
        {
            Binding = new Binding("Trend"),
            Value = "down",
            Setters = { new Setter(TextBlock.ForegroundProperty,
                new DynamicResourceExtension("B.Ok")) },
        });
        return new DataGridTextColumn
        {
            Header = header,
            Width = new DataGridLength(width),
            Binding = binding,
            ElementStyle = textStyle,
            CellStyle = new Style(typeof(DataGridCell), baseCell)
            {
                Setters = { new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Right) },
            },
        };
    }

    /// <summary>
    /// 名称列吃掉剩余宽度。
    /// ⚠ 不能依赖 star 列：WPF 会把 star 列压到最小宽 20（名称列整个消失，全宽也一样），
    /// 必须在此显式计算 —— 与德州物价页同一处理（README「已修 UI 缺陷」）。
    /// </summary>
    private void FitColumns()
    {
        Fit(DraftGrid, _nameCol, NewCols);
        Fit(CancelGrid, _cancelNameCol, CancelCols);
        Fit(MapGrid, _mapNameCol, MapCols);
        // 比对表两个名称列（新 / 旧）都吃剩余宽，按 1.2 : 1 分（新名称普遍更长）
        Fit2(CompareGrid, _cmpNewNameCol, _cmpOldNameCol, CompareCols, 1.2);
    }

    private static void Fit(DataGrid grid, DataGridColumn nameCol,
        (string Prop, string Header, double Width, bool Align, bool Mono)[] cols)
    {
        if (nameCol == null || grid.ActualWidth <= 0) return;
        var fixedSum = cols.Where(c => c.Width > 0).Sum(c => c.Width) + 30;
        var w = grid.ActualWidth - fixedSum;
        nameCol.Width = new DataGridLength(Math.Max(80, w));
    }

    /// <summary>两个弹性列分剩余宽（比对表：新名称略宽于旧名称）。</summary>
    private static void Fit2(DataGrid grid, DataGridColumn a, DataGridColumn b,
        (string Prop, string Header, double Width, bool Align, bool Mono)[] cols, double ratio)
    {
        if (a == null || b == null || grid.ActualWidth <= 0) return;
        var fixedSum = cols.Where(c => c.Width > 0).Sum(c => c.Width) + 30;
        var w = Math.Max(200, grid.ActualWidth - fixedSum);
        var wa = w * ratio / (ratio + 1);
        a.Width = new DataGridLength(Math.Max(110, wa));
        b.Width = new DataGridLength(Math.Max(90, w - wa));
    }

    // ==================================================================
    // 视图切换与筛选
    // ==================================================================
    // 类别级「使用说明」面板（对齐主库物价页，与当前类别筛选联动）
    // ==================================================================

    /// <summary>
    /// 面板内容：选了某个类别 → 该类别说明；未选 → 全部类别（带「类别」小标题分组）。
    /// 征求意见稿的说明逐条来自通知各附件表末的「使用说明：」编号条款。
    /// </summary>
    private void UpdateUsagePanel()
    {
        if (!_loaded || UsageCard is null) return;
        var cat = SelCat;                       // "" / "（全部）" = 未选
        var rows = new List<UsageVm>();

        if (!string.IsNullOrEmpty(cat) && cat != "（全部）")
        {
            var list = Store.UsageOf(cat);
            for (var i = 0; i < list.Count; i++)
                rows.Add(new UsageVm { No = i + 1 + ".", Text = list[i] });
            UsageTitle.Text = $"「{cat}」类 使用说明（{list.Count} 条）";
        }
        else
        {
            var all = Store.UsageAll();
            foreach (var (name, list) in all)
                for (var i = 0; i < list.Count; i++)
                    rows.Add(new UsageVm
                    {
                        No = i + 1 + ".",
                        Group = i == 0 ? name : "",
                        Text = list[i],
                    });
            UsageTitle.Text = all.Count == 0
                ? "本稿未附带类别使用说明"
                : $"全部 {all.Count} 个类别 使用说明（{rows.Count} 条）· 选择类别可只看该类";
        }

        if (rows.Count == 0)
        {
            UsageCard.Visibility = Visibility.Collapsed;
            UsageList.ItemsSource = null;
            return;
        }
        UsageCard.Visibility = Visibility.Visible;
        UsageList.ItemsSource = rows;
        ApplyUsageOpenState();
    }

    private void ApplyUsageOpenState()
    {
        UsageBody.Visibility = _usageOpen ? Visibility.Visible : Visibility.Collapsed;
        UsageToggle.Content = _usageOpen ? "收起" : "展开";
    }

    private void OnToggleUsage(object sender, RoutedEventArgs e)
    {
        _usageOpen = !_usageOpen;
        ApplyUsageOpenState();
    }

    // ==================================================================

    private void OnViewChanged(object sender, RoutedEventArgs e)
    {
        // 注意：XAML 里 SegNew 的 IsChecked="True" 会在 InitializeComponent 期间就触发本方法，
        // 那时 DraftGrid / CancelGrid / MapGrid 还没赋值完成 —— 必须先用 _loaded 挡掉，
        // 真正的首屏渲染由 EnsureLoaded() 里显式调用 OnViewChanged 完成。
        if (_building || !_loaded) return;
        var next = SegCmp.IsChecked == true ? ViewKind.Compare
              : SegCancel.IsChecked == true ? ViewKind.Cancel
              : SegMap.IsChecked == true ? ViewKind.Map
              : ViewKind.New;
        if (next == _view) return;
        // 切视图要重建筛选 + 重排 50 行（实测 200~400ms），期间主线程不刷新。
        // 先亮遮罩并让出一帧，再同步干活 —— 否则用户看到的是「点了没反应」。
        Busy.Show(BusyLabel(next), BusyHint(next));
        Dispatcher.BeginInvoke(new Action(() => SwitchView(next)), DispatcherPriority.Render);
    }

    /// <summary>真正切视图（已由 <see cref="OnViewChanged"/> 让出一帧，遮罩已画在屏幕上）。</summary>
    private void SwitchView(ViewKind next)
    {
        try
        {
        _view = next;
        // 性质筛选只在「新增项目」视图可用；比对视图改用「比对范围」筛选
        KindSel.Visibility = _view == ViewKind.New ? Visibility.Visible : Visibility.Collapsed;
        ScopeSel.Visibility = _view == ViewKind.Compare ? Visibility.Visible : Visibility.Collapsed;
        DraftGrid.Visibility = _view == ViewKind.New ? Visibility.Visible : Visibility.Collapsed;
        CancelGrid.Visibility = _view == ViewKind.Cancel ? Visibility.Visible : Visibility.Collapsed;
        MapGrid.Visibility = _view == ViewKind.Map ? Visibility.Visible : Visibility.Collapsed;
        CompareGrid.Visibility = _view == ViewKind.Compare ? Visibility.Visible : Visibility.Collapsed;
        // 恢复该视图上次点过的排序箭头（数据重载会清，切回视图时补回）
        RestoreSortIndicator(DraftGrid, ViewKind.New);
        RestoreSortIndicator(CancelGrid, ViewKind.Cancel);
        RestoreSortIndicator(MapGrid, ViewKind.Map);
        RestoreSortIndicator(CompareGrid, ViewKind.Compare);
        Load(1);
        FitColumns();
        UpdateUsagePanel();
        }
        finally
        {
            Busy.HideAfter();
        }
    }

    private static string BusyLabel(ViewKind v) => v switch
    {
        ViewKind.Cancel => "正在载入拟作废对照…",
        ViewKind.Map => "正在整理映射关系表…",
        ViewKind.Compare => "正在比对新增与旧版价格…",
        _ => "正在载入新增项目…",
    };

    private static string BusyHint(ViewKind v) => v switch
    {
        ViewKind.Cancel => "配对原价格项目",
        ViewKind.Map => "还原通知附件两列结构",
        ViewKind.Compare => "逐条计算差额与涨跌幅",
        _ => "重建类别与服务产出",
    };

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_building || !_loaded) return;
        Load(1);
        UpdateUsagePanel();
    }

    private void OnQueryText(object sender, TextChangedEventArgs e)
    {
        if (_building || !_loaded) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnQueryKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _debounce.Stop();
        OnFilterChanged(null, null);
    }

    private void OnSearch(object sender, RoutedEventArgs e)
    {
        _debounce.Stop();
        OnFilterChanged(null, null);
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _building = true;
        DzQuery.Text = "";
        CatSel.SelectedIndex = 0;
        KindSel.SelectedIndex = 0;
        ScopeSel.SelectedIndex = 0;
        _building = false;
        Load(1);
    }

    private void OnPrev(object sender, RoutedEventArgs e) => Load(_page - 1);
    private void OnNext(object sender, RoutedEventArgs e) => Load(_page + 1);

    private string SelCat
    {
        get
        {
            var tag = (CatSel.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            return tag;
        }
    }

    private string SelKind
    {
        get
        {
            var tag = (KindSel.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            return tag;
        }
    }

    private string Q => _query;

    /// <summary>比对范围筛选："" 全部 / priced 有旧价可比 / new 纯新增项目。</summary>
    private string SelScope
        => (ScopeSel.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    // ==================================================================
    // 载入数据
    // ==================================================================

    private void Load(int page)
    {
        if (!_loaded) return;
        _query = (DzQuery.Text ?? "").Trim();
        if (_view != ViewKind.New && _view == ViewKind.New) KindSel.SelectedIndex = 0;

        switch (_view)
        {
            case ViewKind.Cancel: LoadCancel(page); return;
            case ViewKind.Map: LoadMap(page); return;
            case ViewKind.Compare: LoadCompare(page); return;
            default: LoadNew(page); return;
        }
    }

    private void LoadNew(int page)
    {
        // 排序必须整表进行（分页下只排当前页会错乱），故本地取全量再切页
        var rows = Store.AllItems(SelCat, SelKind, Q);
        _sorts.TryGetValue(ViewKind.New, out var s);
        ApplySort(rows, it => SortKey(s.Prop, s.Prop switch
        {
            "Seq" => it.Seq, "Code" => it.Code, "Name" => it.Name,
            "Category" => it.Category, "Unit" => it.Unit,
            "Price3Text" => it.Price3, "Price2Text" => it.Price2, "Price1Text" => it.Price1,
            _ => it.Code,
        }), s.Dir);

        _total = rows.Count;
        page = Math.Max(1, Math.Min(page, Math.Max(1, (int)Math.Ceiling(_total / (double)PageSize))));
        _page = page;
        DraftGrid.ItemsSource = rows.Skip((page - 1) * PageSize).Take(PageSize).Select(ToNewVm).ToList();
        UpdatePager();
        var st = Store.Stat();
        DzStatus.Text = st.HasData
            ? $"拟新增 {st.Items:N0} 项（主项 {st.Mains:N0} · 加收 {st.Adds:N0} · 扩展 {st.Exts:N0}）"
            : "尚未导入征求意见稿";
    }

    private void LoadCancel(int page)
    {
        var all = Store.ListCancels(SelCat, Q);
        _sorts.TryGetValue(ViewKind.Cancel, out var s);
        ApplySort(all, c => SortKey(s.Prop, s.Prop switch
        {
            "Category" => c.Category, "OldCode" => c.OldCode, "OldName" => c.OldName,
            "NewCodeText" => c.NewCode, "NewNameText" => c.NewName,
            _ => c.OldCode,
        }), s.Dir);
        _total = all.Count;
        if (page < 1) page = 1;
        var maxPage = Math.Max(1, (int)Math.Ceiling(_total / (double)PageSize));
        if (page > maxPage) page = maxPage;
        _page = page;
        CancelGrid.ItemsSource = all.Skip((page - 1) * PageSize).Take(PageSize)
            .Select(c => new DraftCancelRowVm
            {
                Category = c.Category,
                OldCode = c.OldCode,
                OldName = c.OldName,
                NewCode = c.NewCode,
                NewName = c.NewName,
                Note = c.Note,
            }).ToList();
        UpdatePager();
        var noNew = all.Count(c => string.IsNullOrEmpty(c.NewCode));
        DzStatus.Text = $"拟作废 {_total:N0} 条（其中 {noNew:N0} 条直接废止、无替代项目）";
    }

    private void LoadMap(int page)
    {
        var all = Store.ListMaps(SelCat, Q);
        _sorts.TryGetValue(ViewKind.Map, out var s);
        ApplySort(all, m => SortKey(s.Prop, s.Prop switch
        {
            "Seq" => m.Seq, "Category" => m.Category,
            "NewCodeText" => m.NewCode, "NewName" => m.NewName,
            "OldCodeText" => m.OldCode, "OldName" => m.OldName,
            _ => m.NewCode,
        }), s.Dir);
        _total = all.Count;
        if (page < 1) page = 1;
        var maxPage = Math.Max(1, (int)Math.Ceiling(_total / (double)PageSize));
        if (page > maxPage) page = maxPage;
        _page = page;
        MapGrid.ItemsSource = all.Skip((page - 1) * PageSize).Take(PageSize)
            .Select(m => new DraftMapRowVm
            {
                Seq = m.Seq,
                Category = m.Category,
                NewCode = m.NewCode,
                NewName = m.NewName,
                OldCode = m.OldCode,
                OldName = m.OldName,
                Note = m.Note,
            }).ToList();
        UpdatePager();
        DzStatus.Text = $"映射关系 {_total:N0} 行";
    }

    private void LoadCompare(int page)
    {
        var all = Store.ListCompare(SelCat, Q, SelScope);
        _sorts.TryGetValue(ViewKind.Compare, out var s);
        ApplySort(all, r => s.Prop switch
        {
            // 差额 / 涨幅按原始数值排（null 排最后），状态按来源排
            "Delta3Text" => r.Delta3, "RatioText" => r.Ratio3,
            "StatusText" => r.OldSource,
            "NewPriceText" => NumOf(r.NewPrice3),
            "OldPriceText" => r.OldSource == "legacy" ? NumOf(r.OldPrice3) : null,
            _ => SortKey(s.Prop, s.Prop switch
            {
                "NewCodeText" => r.NewCode, "NewName" => r.NewName,
                "OldCodeText" => r.OldCode, "OldNameText" => r.OldName,
                _ => r.NewCode,
            }),
        }, s.Dir);
        _total = all.Count;
        if (page < 1) page = 1;
        var maxPage = Math.Max(1, (int)Math.Ceiling(_total / (double)PageSize));
        if (page > maxPage) page = maxPage;
        _page = page;
        CompareGrid.ItemsSource = all.Skip((page - 1) * PageSize).Take(PageSize)
            .Select(r => new DraftCompareRowVm
            {
                Category = r.Category,
                NewCode = r.NewCode,
                NewName = r.NewName,
                NewPrice3 = r.NewPrice3,
                NewPrice2 = r.NewPrice2,
                NewPrice1 = r.NewPrice1,
                OldCode = r.OldCode,
                OldName = r.OldName,
                OldPrice3 = r.OldPrice3,
                OldPrice2 = r.OldPrice2,
                OldPrice1 = r.OldPrice1,
                OldSource = r.OldSource,
                Delta3 = r.Delta3,
                Delta2 = r.Delta2,
                Delta1 = r.Delta1,
                Ratio3 = r.Ratio3,
            }).ToList();
        UpdatePager();
        var (total, priced, cancelOnly, brandNew, down, up, flat) = Store.CompareStat();
        var noNum = Store.Compare().Count(r => r.OldSource == "legacy" && r.Delta3 is null);
        DzStatus.Text =
            $"共 {total:N0} 项拟新增 · 有旧价可比 {priced:N0}（涨 {up:N0} / 跌 {down:N0} / 平 {flat:N0}"
            + (noNum > 0 ? $" / 旧价非数字 {noNum:N0}" : "") + "）"
            + $" · 仅作废对照 {cancelOnly:N0} · 纯新增 {brandNew:N0}";
    }

    private void UpdatePager()
    {
        var maxPage = Math.Max(1, (int)Math.Ceiling(_total / (double)PageSize));
        DzPageInfo.Text = $"{_page} / {maxPage} 页 · 共 {_total:N0} 条";
        DzPrev.IsEnabled = _page > 1;
        DzNext.IsEnabled = _page < maxPage;
    }

    // ==================================================================
    // 行 → 视图模型
    // ==================================================================

    private DraftRowVm ToNewVm(DezhouPriceItem it)
    {
        // 加收项价格在稿子里是 Excel 公式（ROUND(主项*0.2,0)）——这里换成算出来的钱
        var (p3, p2, p1) = Store.ResolvePrices(it);
        var vm = new DraftRowVm
        {
            Seq = it.Seq,
            Code = it.Code,
            Name = it.Name,
            Category = it.Category,
            Unit = it.Unit,
            Price3 = p3,
            Price2 = p2,
            Price1 = p1,
            Kind = it.Kind,
            Usage = it.Usage,
        };
        vm.BlocksFactory = () => BuildBlocks(it);
        return vm;
    }

    /// <summary>详情卡内容块：服务产出 / 价格构成 / 计价说明 + 类别使用说明摘录 + 映射关系（若映射表里有）。</summary>
    private List<LxBlock> BuildBlocks(DezhouPriceItem it)
    {
        var blocks = new List<LxBlock>
        {
            TextBlockOf("服务产出", it.ServiceOutput),
            TextBlockOf("价格构成", it.PriceComposition),
            TextBlockOf("计价说明", it.Usage),
        };

        // 该类别的「使用说明」摘录（类别级通用规则）：只放前 2 条，全文见页面上方「使用说明」面板
        var cat = Store.UsageOf(it.Category);
        if (cat.Count > 0)
        {
            var take = cat.Take(2).Select((t, i) => new LxLine { Text = $"{i + 1}. {t}" }).ToList();
            if (cat.Count > 2)
            {
                var hint = new LxLine { Text = $"…… 本类使用说明共 {cat.Count} 条 · 点此定位查看全部 ▸" };
                hint.Click = () => FocusUsage(it.Category);
                take.Add(hint);
            }
            blocks.Add(new LxBlock { Label = $"{it.Category} · 使用说明", Lines = take });
        }

        var maps = Store.MapsOfNew(it.Code);
        if (maps.Count > 0)
            blocks.Add(new LxBlock
            {
                Label = $"映射原价格项目（{maps.Count}）",
                Lines = maps.Select(m => new LxLine
                {
                    Text = $"{m.OldCode}  {m.OldName}",
                }).ToList(),
            });

        return blocks.Where(b => b.Lines is { Count: > 0 }).ToList();
    }

    /// <summary>
    /// 详情卡「点此定位查看全部」：面板聚焦到该类别并展开，不改表格筛选、不打断浏览。
    /// </summary>
    private void FocusUsage(string cat)
    {
        foreach (ComboBoxItem i in CatSel.Items)
            if ((i.Tag as string ?? "") == (cat ?? "")) { CatSel.SelectedItem = i; break; }
        _usageOpen = true;
        UpdateUsagePanel();
        UsageCard.BringIntoView();
        DzStatus.Text = $"「使用说明」已定位：{cat}（改类别下拉可退出定位）";
    }

    /// <summary>单段文本块（<see cref="LxBlock"/> 只有 Label + Lines，文本行放在 Lines 里）。</summary>
    private static LxBlock TextBlockOf(string label, string text)
        => new()
        {
            Label = label,
            Lines = string.IsNullOrWhiteSpace(text)
                ? new List<LxLine>()
                : new List<LxLine> { new() { Text = text.Trim() } },
        };

    // ==================================================================
    // 导出
    // ==================================================================

    private void OnExport(object sender, RoutedEventArgs e)
    {
        try
        {
            var (name, sheets) = _view switch
            {
                ViewKind.Cancel => ("征求意见稿-废止对照", BuildCancelSheets()),
                ViewKind.Map => ("征求意见稿-映射关系", BuildMapSheets()),
                ViewKind.Compare => ("征求意见稿-新旧价格比对", BuildCompareSheets()),
                _ => ("征求意见稿-拟新增项目", BuildNewSheets()),
            };
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = name + ".xlsx",
                Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
            };
            if (dlg.ShowDialog() != true) return;
            var bytes = ExcelUtil.BuildWorkbook(sheets);
            File.WriteAllBytes(dlg.FileName, bytes);
            MessageBox.Show($"已导出到：\n{dlg.FileName}", "导出完成",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("导出失败：" + ex.Message, "导出", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>把「表头 + 数据行」拼成 <see cref="ExcelUtil.SheetSpec"/>（首行即表头）。</summary>
    private static ExcelUtil.SheetSpec Sheet(string name, List<string> header, List<List<string>> rows)
    {
        var aoa = new List<List<object>> { header.Cast<object>().ToList() };
        foreach (var r in rows) aoa.Add(r.Cast<object>().ToList());
        return new ExcelUtil.SheetSpec { Name = name, Aoa = aoa };
    }

    private List<ExcelUtil.SheetSpec> BuildNewSheets()
    {
        var rows = Store.AllItems(SelCat, SelKind, Q);
        return new List<ExcelUtil.SheetSpec>
        {
            Sheet("拟新增项目",
                new List<string> { "序号", "项目编码", "项目名称", "类别", "项目性质", "计价单位", "三级价格", "二级价格", "一级价格", "计价说明" },
                rows.Select(i => new List<string>
                {
                    i.Seq, i.Code, i.Name, i.Category, DezhouPriceStore.KindLabel(i.Kind),
                    i.Unit, Store.ResolvePrice(i, "price3"), Store.ResolvePrice(i, "price2"),
                    Store.ResolvePrice(i, "price1"), i.Usage,
                }).ToList()),
        };
    }

    private List<ExcelUtil.SheetSpec> BuildCancelSheets()
    {
        var rows = Store.ListCancels(SelCat, Q);
        return new List<ExcelUtil.SheetSpec>
        {
            Sheet("废止对照",
                new List<string> { "类别", "拟作废编码", "拟作废项目名称", "对应新编码", "对应新项目", "备注" },
                rows.Select(c => new List<string>
                {
                    c.Category, c.OldCode, c.OldName, c.NewCode, c.NewName, c.Note,
                }).ToList()),
        };
    }

    private List<ExcelUtil.SheetSpec> BuildMapSheets()
    {
        var rows = Store.ListMaps(SelCat, Q);
        return new List<ExcelUtil.SheetSpec>
        {
            Sheet("映射关系",
                new List<string> { "序号", "类别", "新项目编码", "新项目名称", "原项目编码", "原项目名称", "备注" },
                rows.Select(m => new List<string>
                {
                    m.Seq, m.Category, m.NewCode, m.NewName, m.OldCode, m.OldName, m.Note,
                }).ToList()),
        };
    }

    private List<ExcelUtil.SheetSpec> BuildCompareSheets()
    {
        var rows = Store.ListCompare(SelCat, Q, SelScope);
        var (total, priced, cancelOnly, brandNew, down, up, flat) = Store.CompareStat();
        var head = new List<string>
        {
            "类别", "新编码", "新项目名称", "新三级价", "新二级价", "新一级价",
            "旧编码", "旧项目名称", "旧三级价", "旧二级价", "旧一级价",
            "差额(三级)", "涨幅(三级)", "比对状态",
        };
        var data = rows.Select(r => new List<string>
        {
            r.Category, r.NewCode, r.NewName, r.NewPrice3, r.NewPrice2, r.NewPrice1,
            r.OldCode, r.OldSource == "none" ? "" : r.OldName,
            r.OldSource == "legacy" ? r.OldPrice3 : "",
            r.OldSource == "legacy" ? r.OldPrice2 : "",
            r.OldSource == "legacy" ? r.OldPrice1 : "",
            r.Delta3 is null ? "" : r.Delta3.Value.ToString("0.##"),
            r.Ratio3 is null ? "" : r.Ratio3.Value.ToString("0.#") + "%",
            r.OldSource == "legacy" ? "有旧价可比"
                : r.OldSource == "cancel" ? "仅作废对照" : "纯新增项目",
        }).ToList();
        // 汇总单独一张表：不能塞进数据区。以前是 aoa.Insert(1, 汇总行)，
        // 表头下第一条就是汇总，所有数据整体下移一行 —— 筛选、排序、透视全部错行。
        var summary = new List<List<object>>
        {
            new() { "指标", "数值" },
            new() { "拟新增项目合计", total },
            new() { "有旧价可比", priced },
            new() { "　其中 涨价", up },
            new() { "　其中 降价", down },
            new() { "　其中 持平", flat },
            new() { "仅作废对照", cancelOnly },
            new() { "纯新增项目", brandNew },
            new() { "导出口径", $"类别「{(SelCat.Length == 0 ? "全部" : SelCat)}」· "
                                 + $"范围「{(SelScope.Length == 0 ? "全部" : SelScope == "priced" ? "有旧价可比" : "纯新增项目")}」· "
                                 + $"关键词「{(Q.Length == 0 ? "无" : Q)}」· 本表 {data.Count} 行" },
        };
        return new List<ExcelUtil.SheetSpec>
        {
            Sheet("新旧价格比对", head, data),
            new() { Name = "比对汇总", Aoa = summary, ColWidths = ExcelUtil.WidthsFromPixels(new[] { 180, 420 }) },
        };
    }

    // ==================================================================
    // 供自检 / 离屏截图直读
    // ==================================================================

    internal int DiagRows => _view switch
    {
        ViewKind.Cancel => CancelGrid.Items.Count,
        ViewKind.Map => MapGrid.Items.Count,
        ViewKind.Compare => CompareGrid.Items.Count,
        _ => DraftGrid.Items.Count,
    };

    internal int DiagTotal => _total;
    internal string DiagStatus => DzStatus.Text;

    /// <summary>
    /// 诊断 / 自检：按当前视图走 <see cref="OnExport"/> 同一条构造路径，直接拿到待写盘的工作表，
    /// 让自检能逐表解析产物（表头宽度、行序、是否错行），不必真的弹「另存为」。
    /// </summary>
    internal List<ExcelUtil.SheetSpec> DiagExportSheets() => _view switch
    {
        ViewKind.Cancel => BuildCancelSheets(),
        ViewKind.Map => BuildMapSheets(),
        ViewKind.Compare => BuildCompareSheets(),
        _ => BuildNewSheets(),
    };

    /// <summary>
    /// 诊断 / 自检：导出按钮与状态文字是否同一行。
    /// 判据用**垂直中心**对齐（按钮 32px、文字 20px，两者 Top 天然差 6px，比 Top 会误判错行）。
    /// </summary>
    internal (double BtnTop, double StatusTop, double BtnLeft, bool SameRow) DiagExportLayout()
    {
        var b = ExpBtn.TransformToAncestor(this).Transform(new Point(0, 0));
        var s = DzStatus.TransformToAncestor(this).Transform(new Point(0, 0));
        var bc = b.Y + ExpBtn.ActualHeight / 2;
        var sc = s.Y + DzStatus.ActualHeight / 2;
        return (b.Y, s.Y, b.X, Math.Abs(bc - sc) <= 2);
    }

    /// <summary>诊断 / 自检：比对统计（与导出的「比对汇总」表逐项对账）。</summary>
    internal (int Total, int Priced, int CancelOnly, int BrandNew, int Down, int Up, int Flat) DiagCompareStat()
        => Store.CompareStat();
    internal bool DiagBannerShown => DraftBanner.Visibility == Visibility.Visible;
    internal string DiagBannerText => TxtDraftTitle.Text + " | " + TxtDraftNote.Text
                                        + " | " + TxtDraftRefs.Text;

    /// <summary>诊断专用：切到指定视图（--shot-page draftcancel / draftmap / draftcmp）</summary>
    internal void DiagView(ViewKind view)
    {
        _building = true;
        SegNew.IsChecked = view == ViewKind.New;
        SegCancel.IsChecked = view == ViewKind.Cancel;
        SegMap.IsChecked = view == ViewKind.Map;
        SegCmp.IsChecked = view == ViewKind.Compare;
        _building = false;
        // ⚠ 这里必须**同步**切，不能走 OnViewChanged 的「让一帧再干活」通道：
        // 离屏自检 / 截图路径不跑消息循环，BeginInvoke(Render) 排的回调永远不会执行，
        // 结果就是「视图切了、表格 0 行」。诊断入口一律走同步。
        if (view != _view) SwitchView(view);
    }

    internal void DiagCancel() => DiagView(ViewKind.Cancel);
    internal void DiagMap() => DiagView(ViewKind.Map);
    internal void DiagCompare() => DiagView(ViewKind.Compare);

    /// <summary>诊断专用：切到比对视图并按指定列排序（模拟点表头，--shot-page draftcmps:涨幅）。</summary>
    internal void DiagCompareSort(string header)
    {
        DiagView(ViewKind.Compare);
        var col = CompareGrid.Columns.FirstOrDefault(c =>
            string.Equals((string)c.Header, header, StringComparison.Ordinal));
        if (col == null) return;
        OnSorting(CompareGrid, new DataGridSortingEventArgs(col));
    }

    /// <summary>诊断专用：切到比对视图并选中指定比对范围（priced / new / ""）。</summary>
    internal void DiagCompareScope(string scope)
    {
        DiagView(ViewKind.Compare);
        _building = true;
        foreach (ComboBoxItem it in ScopeSel.Items)
            if ((it.Tag as string ?? "") == scope) { ScopeSel.SelectedItem = it; break; }
        _building = false;
        Load(1);
    }

    internal void DiagQuery(string q)
    {
        EnsureLoaded();
        _building = true;
        DzQuery.Text = q ?? "";
        _building = false;
        Load(1);
    }

    internal void DiagSelectCat(string cat)
    {
        EnsureLoaded();
        _building = true;
        foreach (ComboBoxItem i in CatSel.Items)
            if ((i.Tag as string ?? "") == (cat ?? "")) { CatSel.SelectedItem = i; break; }
        _building = false;
        Load(1);
    }

    /// <summary>
    /// 诊断专用（--shot-page draftdetail:编码）：选中指定编码的那一行展开详情卡，
    /// 再模拟点详情卡里的「点此定位查看全部」链接，核对使用说明面板是否定位到该类别。
    /// </summary>
    internal void DiagDetailUsage(string code)
    {
        EnsureLoaded();
        var needle = (code ?? "").Trim();
        if (needle.Length > 0) DiagQuery(needle);
        var vm = DraftGrid.Items.OfType<DraftRowVm>().FirstOrDefault();
        if (vm is null) { DzStatus.Text = "诊断：未找到行 " + needle; return; }
        DraftGrid.SelectedItem = vm;
        var blocks = vm.Blocks;
        var hint = blocks.SelectMany(b => b.Lines ?? new List<LxLine>())
                         .FirstOrDefault(l => l.Click != null);
        if (hint is null) { DzStatus.Text = $"诊断：{vm.Code} 详情卡无定位链接"; return; }
        hint.Click();
    }

    /// <summary>诊断断言用：使用说明面板是否可见 / 标题 / 条目数。</summary>
    internal bool DiagUsageShown => UsageCard.Visibility == Visibility.Visible;
    internal string DiagUsageTitle => UsageTitle.Text;
    internal int DiagUsageCount => (UsageList.ItemsSource as System.Collections.ICollection)?.Count ?? 0;

    internal int DiagNewCount => DraftGrid.Items.Count;

    /// <summary>离屏诊断用：四个视图的表格（核对列对齐 / 表头样式）</summary>
    internal System.Windows.Controls.DataGrid[] DraftGrids => new[] { DraftGrid, CancelGrid, MapGrid, CompareGrid };
}
