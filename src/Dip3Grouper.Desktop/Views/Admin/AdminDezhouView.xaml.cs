using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Dip3Grouper;
using Dip3GrouperApp.Desktop.Controls;
using Microsoft.Win32;

namespace Dip3GrouperApp.Desktop.Views.Admin;

/// <summary>后台「德州物价」表格的一行（含详情卡字段块）。</summary>
public sealed class DzAdminRowVm
{
    public DzAdminRowVm(DezhouPriceItem r)
    {
        Row = r;
        Blocks = new List<Block>
        {
            new("落地文件", string.IsNullOrWhiteSpace(r.Exec) ? r.Doc : $"{r.Doc}　执行日期 {r.Exec}"),
            new("服务产出", Flatten(r.ServiceOutput)),
            new("价格构成", Flatten(r.PriceComposition)),
            new("限定支付范围", Flatten(r.Note)),
            new("计价说明", Flatten(r.Usage)),
            new("医保支付", string.IsNullOrWhiteSpace(r.Insurance)
                ? "" : $"{r.Insurance}　自付比例 {r.SelfPay}"),
        }.Where(b => !string.IsNullOrWhiteSpace(b.Text)).ToList();
    }

    public DezhouPriceItem Row { get; }

    public string Code => Row.Code;
    public string Name => Flatten(Row.Name);
    public string Category => Row.Category;
    public string KindLabel => DezhouPriceStore.KindLabel(Row.Kind);
    public string Unit => Row.Unit;
    public string Price3 => Nz(Row.Price3);
    public string Price2 => Nz(Row.Price2);
    public string Price1 => Nz(Row.Price1);
    public string Insurance => Row.Insurance;
    public string SelfPay => Row.SelfPay;
    public string Doc => Row.Doc;
    public string PriceLine => $"{Nz(Row.Price3)} / {Nz(Row.Price2)} / {Nz(Row.Price1)}";
    public List<Block> Blocks { get; }

    private static string Nz(string v) => string.IsNullOrWhiteSpace(v) ? "—" : v;
    private static string Flatten(string v) => Regex.Replace(v ?? "", @"\n+", "；");

    public sealed record Block(string Label, string Text);
}

/// <summary>
/// 后台「德州物价」维护：国家立项指南在德州落地的规范整合项目（15 位编码 + 三级价格 + 医保支付）。
/// 支持筛选检索、增删改、Excel 导入/模板/导出；数据文件 data/dezhou-price.json。
/// </summary>
public partial class AdminDezhouView : UserControl
{
    private const int PageSize = 50;

    /// <summary>后端维护表列（第四字段 = 数值列，居中显示，与前台四视图口径一致）</summary>
    private static readonly (string Prop, string Header, double Width, bool Align, bool Mono)[] Cols =
    {
        ("Code", "项目编码", 150, false, true),
        ("Name", "项目名称", 0, false, false),
        ("Category", "类别", 112, false, false),
        ("KindLabel", "性质", 60, false, false),
        ("Unit", "计价单位", 68, false, false),
        ("Price3", "三级", 70, true, true),
        ("Price2", "二级", 70, true, true),
        ("Price1", "一级", 70, true, true),
        ("Insurance", "医保", 50, false, false),
        ("SelfPay", "自付", 56, true, true),
        ("Doc", "落地文号", 132, false, false),
    };

    private bool _loaded;
    private bool _building;
    private string _doc = "";
    private string _cat = "";
    private string _kind = "";
    private int _page = 1;

    public AdminDezhouView()
    {
        InitializeComponent();
        BuildColumns();
        Grid.SizeChanged += (_, _) => FitNameColumn();
    }

    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        BuildDocs();
        BuildCats();
        BuildKinds();
        UpdateStat();
        Load(1);
    }

    // ==================================================================
    // 下拉与统计
    // ==================================================================

    private void BuildDocs()
    {
        _building = true;
        var keep = _doc;
        var byDoc = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var i in App.State.Dezhou.Items)
            byDoc[i.Doc] = byDoc.TryGetValue(i.Doc, out var n) ? n + 1 : 1;

        DocSel.Items.Clear();
        DocSel.Items.Add(new ComboBoxItem { Content = $"全部批次（{App.State.Dezhou.Items.Count:N0}）", Tag = "" });
        foreach (var b in App.State.Dezhou.Batches())
        {
            byDoc.TryGetValue(b.Doc, out var n);
            DocSel.Items.Add(new ComboBoxItem { Content = $"{b.Doc}（{n:N0}条）", Tag = b.Doc });
        }
        DocSel.SelectedIndex = IndexOfTag(DocSel, keep);
        _doc = (DocSel.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        _building = false;
    }

    private void BuildCats()
    {
        _building = true;
        var keep = _cat;
        CatSel.Items.Clear();
        CatSel.Items.Add(new ComboBoxItem { Content = "全部类别", Tag = "" });
        foreach (var c in App.State.Dezhou.Categories(_doc))
            CatSel.Items.Add(new ComboBoxItem { Content = c, Tag = c });
        CatSel.SelectedIndex = IndexOfTag(CatSel, keep);
        _cat = (CatSel.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        _building = false;
    }

    private void BuildKinds()
    {
        _building = true;
        var keep = _kind;
        KindSel.Items.Clear();
        KindSel.Items.Add(new ComboBoxItem { Content = "全部项目", Tag = "" });
        KindSel.Items.Add(new ComboBoxItem { Content = "仅主项目", Tag = "main" });
        KindSel.Items.Add(new ComboBoxItem { Content = "仅加收项", Tag = "add" });
        KindSel.Items.Add(new ComboBoxItem { Content = "仅扩展项", Tag = "ext" });
        KindSel.SelectedIndex = IndexOfTag(KindSel, keep);
        _kind = (KindSel.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        _building = false;
    }

    /// <summary>在下拉中按 Tag 找回原选中项（找不到则回到第一项）。</summary>
    private static int IndexOfTag(ComboBox cb, string tag)
    {
        for (var i = 0; i < cb.Items.Count; i++)
            if ((cb.Items[i] as ComboBoxItem)?.Tag as string == tag) return i;
        return 0;
    }

    private void UpdateStat()
    {
        var s = App.State.Dezhou.Stat();
        StatText.Text = s.Total == 0
            ? "尚未导入德州物价数据"
            : $"共 {s.Total:N0} 条 · 主项目 {s.Main:N0} · 加收项 {s.Add:N0} · 扩展项 {s.Ext:N0} · 医保支付 {s.Insured:N0} · {s.BatchCount} 批 / {s.CategoryCount} 类";
        var meta = App.State.Dezhou.Meta;
        var srcFull = string.IsNullOrWhiteSpace(meta?.Source) ? "" : meta.Source;
        TxtMeta.Text = srcFull.Length == 0 ? "" : $"导入 {ShortTime(meta.ImportedAt)}";
        System.Windows.Controls.ToolTipService.SetToolTip(TxtMeta,
            srcFull.Length == 0 ? "" : $"来源 {srcFull}\n导入 {meta.ImportedAt}");
        System.Windows.Controls.ToolTipService.SetToolTip(StatText, StatText.Text);
    }

    // ==================================================================
    // 筛选与列表
    // ==================================================================

    private void OnDocChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_building || !_loaded) return;
        _doc = (DocSel.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        BuildCats();
        Load(1);
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_building || !_loaded) return;
        _cat = (CatSel.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        _kind = (KindSel.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        Load(1);
    }

    private void OnSearch(object sender, RoutedEventArgs e) => Load(1);

    private void OnQueryKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Load(1);
    }

    private void OnPrev(object sender, RoutedEventArgs e) => Load(_page - 1);

    private void OnNext(object sender, RoutedEventArgs e) => Load(_page + 1);

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Grid.SelectedItem != null) EditSelected();
    }

    private void Load(int page)
    {
        _page = Math.Max(1, page);
        var q = QueryBox.Text?.Trim() ?? "";
        try
        {
            var r = App.State.Dezhou.ListRows(_doc, _cat, _kind, q, _page, PageSize);
            Grid.ItemsSource = r.Items.Select(i => new DzAdminRowVm(i)).ToList();

            var pages = Math.Max(1, (int)Math.Ceiling(r.Total / (double)r.PageSize));
            PageInfo.Text = $"{r.Label} · 第 {r.Page} / {pages} 页 · 共 {r.Total:N0} 条";
            BtnPrev.IsEnabled = r.Page > 1;
            BtnNext.IsEnabled = r.Page < pages;

            StatusText.Text = App.State.Dezhou.Items.Count == 0
                ? "未导入 data/dezhou-price.json（可「导入 Excel」或「下载模板」）"
                : r.Total == 0 ? "无匹配记录" : "";
        }
        catch (Exception ex)
        {
            Grid.ItemsSource = null;
            PageInfo.Text = "";
            StatusText.Text = "加载失败：" + ex.Message;
        }
        UpdateStat();
    }

    // ==================================================================
    // 增删改
    // ==================================================================

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var batches = App.State.Dezhou.Batches();
        var cur = new DezhouPriceItem
        {
            Doc = _doc.Length > 0 ? _doc : batches.FirstOrDefault()?.Doc ?? "",
            Category = _cat,
            Kind = "main",
        };
        var r0 = batches.FirstOrDefault(b => b.Doc == cur.Doc);
        if (r0 != null) cur.Exec = r0.Exec;

        var flat = RowEditDialog.Edit(Window.GetWindow(this), "新增德州物价项目", BuildFields(cur));
        if (flat == null) return;

        Run(() =>
        {
            if (App.State.Dezhou.CodeExists(flat["code"], 0, flat["doc"]))
                throw new InvalidOperationException($"该文号下已存在编码 {flat["code"]}，请改用「编辑」或更换编码");
            var r = App.State.Dezhou.AddRow(flat);
            if (!r.Ok) throw new InvalidOperationException(r.Error);
            App.State.Dezhou.SaveWithRecompute();
            AuditLog.Write(AuditAction.Add, "德州物价", $"新增物价项目 #{r.Row} {flat["code"]} {flat["name"]}");
            RefreshAll();
            return $"已新增 #{r.Row} · {flat["name"]}";
        });
    }

    private void OnEdit(object sender, RoutedEventArgs e) => EditSelected();

    private void EditSelected()
    {
        var vm = Grid.SelectedItem as DzAdminRowVm;
        if (vm == null) { StatusText.Text = "请先选中一行"; return; }

        var flat = RowEditDialog.Edit(Window.GetWindow(this), $"编辑 · {vm.Row.Code}", BuildFields(vm.Row));
        if (flat == null) return;

        Run(() =>
        {
            if (App.State.Dezhou.CodeExists(flat["code"], vm.Row.Id, flat["doc"]))
                throw new InvalidOperationException($"该文号下已存在编码 {flat["code"]}");
            var r = App.State.Dezhou.UpdateRow(vm.Row.Id.ToString(), flat);
            if (!r.Ok) throw new InvalidOperationException(r.Error);
            App.State.Dezhou.SaveWithRecompute();
            AuditLog.Write(AuditAction.Edit, "德州物价", $"编辑物价项目 #{r.Row} {flat["code"]}");
            RefreshAll();
            return $"已保存 #{r.Row} · {flat["name"]}";
        });
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        var vm = Grid.SelectedItem as DzAdminRowVm;
        if (vm == null) { StatusText.Text = "请先选中一行"; return; }
        if (MessageBox.Show($"确认删除「{vm.Name}」（{vm.Code}）？", "删除确认",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

        Run(() =>
        {
            var r = App.State.Dezhou.RemoveRow(vm.Row.Id.ToString());
            if (!r.Ok) throw new InvalidOperationException(r.Error);
            App.State.Dezhou.SaveWithRecompute();
            AuditLog.Write(AuditAction.Delete, "德州物价", $"删除物价项目 {vm.Code}（{vm.Name}）");
            RefreshAll();
            return $"已删除 {vm.Code} · {vm.Name}";
        });
    }

    // ==================================================================
    // 导入 / 模板 / 导出
    // ==================================================================

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var path = PickOpen("选择德州物价 Excel", "Excel 文件|*.xlsx;*.xls|所有文件|*.*");
        if (path == null) return;
        var mode = PickMode();
        if (mode == null) return;

        Run(() =>
        {
            var r = AdminService.ImportDezhou(App.State, File.ReadAllBytes(path), Path.GetFileName(path),
                _doc, mode);
            RefreshAll();
            var extra = r.Skipped > 0 ? $" · 跳过 {r.Skipped} 行（编码/名称为空或不合规）" : "";
            return $"导入完成 · 解析 {r.Parsed:N0} 行 · 新增 {r.Added:N0} · 更新 {r.Updated:N0}{extra} · 共 {r.Total:N0} 条";
        });
    }

    private void OnTemplate(object sender, RoutedEventArgs e)
    {
        var (bytes, defaultName) = AdminService.DezhouTemplate();
        var path = PickSave(defaultName);
        if (path == null) return;
        Run(() =>
        {
            File.WriteAllBytes(path, bytes);
            return "模板已导出：" + Path.GetFileName(path);
        });
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var (bytes, defaultName) = AdminService.ExportDezhou(App.State, _doc, _cat, _kind, QueryBox.Text?.Trim());
        var path = PickSave(defaultName);
        if (path == null) return;
        Run(() =>
        {
            File.WriteAllBytes(path, bytes);
            var scope = _doc.Length > 0 ? _doc : "全部批次";
            return $"已导出：{Path.GetFileName(path)}（{scope}）";
        });
    }

    // ==================================================================
    // 编辑字段
    // ==================================================================

    private List<RowEditDialog.Field> BuildFields(DezhouPriceItem cur)
    {
        var d = App.State.Dezhou;
        var docOptions = MergeOptions(new List<string> { cur.Doc }, d.DistinctDocs());
        var catOptions = MergeOptions(new List<string> { cur.Category }, d.DistinctCategories(cur.Doc));
        var unitOptions = MergeOptions(new List<string> { cur.Unit }, d.DistinctUnits());

        RowEditDialog.Field F(string key, string label, IReadOnlyList<string> options = null, bool multiline = false)
            => new(key, label, AdminService.DezhouField(cur, key), multiline, false, options);

        return new List<RowEditDialog.Field>
        {
            F("doc", "落地文号（可输入新文号）", docOptions),
            F("exec", "执行日期（如 2026-03-01）"),
            F("category", "类别（可下拉检索 / 输入新类别）", catOptions),
            F("seq", "原表序号（加收项、扩展项可留空）"),
            F("code", "项目编码（11–15 位数字，美容整形类可带 1 位字母后缀）"),
            F("kind", "项目性质（main=主项目 / add=加收项 / ext=扩展项）", DezhouPriceStore.Kinds),
            F("name", "项目名称"),
            F("unit", "计价单位（可下拉检索）", unitOptions),
            F("price3", "三级价格"),
            F("price2", "二级价格"),
            F("price1", "一级价格"),
            F("insurance", "医保支付（是 / 否）", new[] { "是", "否" }),
            F("selfPay", "自付比例（如 20%）"),
            F("serviceOutput", "服务产出", null, true),
            F("priceComposition", "价格构成", null, true),
            F("note", "备注 / 限定支付范围", null, true),
            F("usage", "计价说明", null, true),
        };
    }

    /// <summary>ISO 时间 → 本地「yyyy-MM-dd HH:mm」。</summary>
    private static string ShortTime(string iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "—";
        return DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
            ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : iso;
    }

    /// <summary>合并候选值（首项为当前值，去重保序）。</summary>
    private static List<string> MergeOptions(List<string> first, List<string> rest)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var v in (first ?? new List<string>()).Concat(rest ?? new List<string>()))
        {
            var t = (v ?? "").Trim();
            if (t.Length == 0 || !seen.Add(t)) continue;
            list.Add(t);
        }
        return list;
    }

    // ==================================================================
    // 通用
    // ==================================================================

    private void RefreshAll()
    {
        BuildDocs();
        BuildCats();
        BuildKinds();
        UpdateStat();
        Load(_page);
    }

    private void Run(Func<string> action) => AdminUi.Run(action, StatusText);

    private string PickMode()
    {
        var scope = _doc.Length > 0
            ? _doc
            : "全部批次（注意：会先清空整个德州物价库再写入）";
        var r = MessageBox.Show(
            $"导入范围：{scope}\n\n" +
            "【是】合并更新 —— 按（文号, 编码）覆盖，其余新增\n" +
            "【否】替 换 —— 先清空上述范围再写入\n\n" +
            "取消则放弃本次导入。",
            "选择导入方式", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return r switch
        {
            MessageBoxResult.Yes => "upsert",
            MessageBoxResult.No => "replace",
            _ => null,
        };
    }

    private static string PickOpen(string title, string filter)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private static string PickSave(string defaultName)
    {
        var dlg = new SaveFileDialog
        {
            Title = "导出",
            FileName = defaultName,
            Filter = "Excel 文件|*.xlsx",
            DefaultExt = ".xlsx",
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
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
                // 价格 / 自付这类短数值居中；必须 Stretch，否则 TextBlock 按内容宽自适应、对齐不生效
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
            Grid.Columns.Add(col);
        }
        FitNameColumn();
    }

    private DataGridColumn _nameCol;

    /// <summary>名称列吃掉全部剩余宽度（WPF star 列分配不可靠，见前台同款修复）</summary>
    private void FitNameColumn()
    {
        if (_nameCol == null || Grid.ActualWidth <= 0) return;
        var fixedSum = Cols.Where(c => c.Width > 0).Sum(c => c.Width) + 30;
        var w = Grid.ActualWidth - fixedSum;
        _nameCol.Width = new DataGridLength(Math.Max(120, w));
    }
}
