using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Input;
using Dip3Grouper;
using Dip3GrouperApp.Desktop.Controls;
using Microsoft.Win32;

namespace Dip3GrouperApp.Desktop.Views.Admin;

/// <summary>
/// 立项指南管理：价格项目（区段增删改 / 区段导入 / 模板 / 类别使用说明）+ 解读辅导（导入 / 删期）。
/// 与网页版后台「立项指南」等价，直接调用 Core（不经 HTTP）。
/// </summary>
public partial class AdminLixiangView : UserControl
{
    private const int PageSize = 50;

    private bool _loaded;
    private bool _building;
    private string _tab;
    private int _page = 1;

    public AdminLixiangView()
    {
        InitializeComponent();
    }

    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        BuildSections();
        Load(1);
    }

    /// <summary>当前视图是否为「价格项目」（解读辅导要重新载入问答并建列表，耗时更长）。</summary>
    private bool _itemsView = true;

    private void OnViewChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        var items = SegItems.IsChecked == true;
        if (items == _itemsView) return;
        _itemsView = items;

        // 与前台立项指南页同一套做法：先亮遮罩让出一帧，再同步换视图。
        Busy.Show(items ? "正在载入价格项目…" : "正在载入解读辅导…",
                  items ? "重建区段与类别筛选" : "重新载入问答列表");
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                ItemsView.Visibility = items ? Visibility.Visible : Visibility.Collapsed;
                JieduView.Visibility = items ? Visibility.Collapsed : Visibility.Visible;
                if (!items) LoadJd();
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

    private void BuildSections()
    {
        _building = true;
        var counts = App.State.Lixiang.SectionCounts();
        SectionSel.Items.Clear();
        foreach (var kv in counts.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var it = new ComboBoxItem
            {
                Content = $"{kv.Value.Label ?? kv.Key}（{kv.Value.Total:N0}）",
                Tag = kv.Key,
            };
            SectionSel.Items.Add(it);
            if (_tab == null) _tab = kv.Key;
        }
        var idx = 0;
        for (var i = 0; i < SectionSel.Items.Count; i++)
            if ((SectionSel.Items[i] as ComboBoxItem)?.Tag as string == _tab) { idx = i; break; }
        SectionSel.SelectedIndex = idx;
        _building = false;

        var meta = App.State.Lixiang.State.Meta;
        TxtMeta.Text = string.IsNullOrWhiteSpace(meta?.SourceName)
            ? "尚未导入过立项指南"
            : $"来源 {meta.SourceName} · {meta.ImportedAt}";
        RefreshUsageBox();
    }

    private void RefreshUsageBox()
    {
        var usage = CurrentSection()?.GuideUsage ?? "";
        UsageText.Text = usage;
        // 限高后若溢出会出滚动条，给个字数提示
        UsageMore.Text = usage.Length > 90 ? $"共 {usage.Length} 字 · 可滚动查看" : "";
        UsageBox.Visibility = usage.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private LixiangSection CurrentSection()
        => _tab != null && App.State.Lixiang.State.Sections.TryGetValue(_tab, out var s) ? s : null;

    private void OnSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_building || !_loaded) return;
        var tab = (SectionSel.SelectedItem as ComboBoxItem)?.Tag as string;
        if (string.IsNullOrEmpty(tab) || tab == _tab) return;
        _tab = tab;
        RefreshUsageBox();
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
        if (_tab == null) return;
        var q = QueryBox.Text?.Trim() ?? "";
        try
        {
            var r = App.State.Lixiang.ListRows(_tab, q, _page, PageSize);
            Grid.ItemsSource = r.Rows.Select(row => new ItemVm(row)).ToList();

            var pages = Math.Max(1, (int)Math.Ceiling(r.Total / (double)r.PageSize));
            PageInfo.Text = $"{r.Label} · 第 {r.Page} / {pages} 页 · 共 {r.Total:N0} 条";
            BtnPrev.IsEnabled = r.Page > 1;
            BtnNext.IsEnabled = r.Page < pages;
            StatusText.Text = r.Total == 0 ? "该区段暂无项目" : "";
        }
        catch (Exception ex)
        {
            Grid.ItemsSource = null;
            PageInfo.Text = "";
            StatusText.Text = "加载失败：" + ex.Message;
        }
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (_tab == null) return;
        var flat = RowEditDialog.Edit(Window.GetWindow(this), "新增立项指南项目",
            BuildFields(new Dictionary<string, string>()));
        if (flat == null) return;

        Run(() =>
        {
            var r = App.State.Lixiang.AddRow(_tab, flat);
            if (!r.Ok) throw new InvalidOperationException(r.Error);
            App.State.Lixiang.Save();
            AuditLog.Write(AuditAction.Add, "立项指南", $"新增 {_tab} 项目 #{r.Row} {flat.GetValueOrDefault("name")}");
            BuildSections();
            Load(_page);
            return "已新增（内部编号 #" + r.Row + "）";
        });
    }

    private void OnEdit(object sender, RoutedEventArgs e) => EditSelected();

    private void EditSelected()
    {
        var vm = Grid.SelectedItem as ItemVm;
        if (vm == null || _tab == null) { StatusText.Text = "请先选中一行"; return; }

        var flat = RowEditDialog.Edit(Window.GetWindow(this), $"编辑 · #{vm.Row.Id}",
            BuildFields(ToFlat(vm.Row)));
        if (flat == null) return;

        Run(() =>
        {
            var r = App.State.Lixiang.UpdateRow(_tab, vm.Row.Id.ToString(), flat);
            if (!r.Ok) throw new InvalidOperationException(r.Error);
            App.State.Lixiang.Save();
            AuditLog.Write(AuditAction.Edit, "立项指南", $"编辑 {_tab} 项目 #{vm.Row.Id}");
            Load(_page);
            return "已保存 #" + vm.Row.Id;
        });
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        var vm = Grid.SelectedItem as ItemVm;
        if (vm == null || _tab == null) { StatusText.Text = "请先选中一行"; return; }
        if (MessageBox.Show($"确认删除「{vm.Row.Name}」（#{vm.Row.Id}）？", "删除确认",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

        Run(() =>
        {
            var r = App.State.Lixiang.RemoveRow(_tab, vm.Row.Id.ToString());
            if (!r.Ok) throw new InvalidOperationException(r.Error);
            App.State.Lixiang.Save();
            AuditLog.Write(AuditAction.Delete, "立项指南", $"删除 {_tab} 项目 #{vm.Row.Id}（{vm.Row.Name}）");
            BuildSections();
            Load(_page);
            return "已删除 #" + vm.Row.Id;
        });
    }

    private void OnEditGuideUsage(object sender, RoutedEventArgs e)
    {
        var sec = CurrentSection();
        if (sec == null) return;
        var cur = sec.GuideUsage ?? "";
        var flat = RowEditDialog.Edit(Window.GetWindow(this), $"类别使用说明 · {sec.Label}",
            new[]
            {
                new RowEditDialog.Field("guideUsage",
                    "本类使用说明（类别级，作用于整个区段的所有项目）", cur, true, false),
            });
        if (flat == null) return;

        Run(() =>
        {
            App.State.Lixiang.SetGuideUsage(_tab, flat["guideUsage"]);
            AuditLog.Write(AuditAction.Edit, "立项指南",
                $"保存 {sec.Label} 类别使用说明（{flat["guideUsage"].Length} 字）");
            RefreshUsageBox();
            return $"类别使用说明已保存（{flat["guideUsage"].Length} 字）";
        });
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var sec = CurrentSection();
        var path = PickOpen($"选择「{sec?.Label}」立项指南 Excel", "Excel 文件|*.xlsx;*.xls|所有文件|*.*");
        if (path == null) return;
        var mode = PickMode(sec?.Label ?? _tab);
        if (mode == null) return;

        Run(() =>
        {
            var r = AdminService.ImportLixiang(App.State, File.ReadAllBytes(path), Path.GetFileName(path),
                _tab, mode);
            BuildSections();
            Load(1);
            var rep = r.Report.Values.FirstOrDefault();
            if (rep == null)
                return $"导入完成 · 区段 {r.Counts.Count} 个 · 共 {r.Total:N0} 个项目";
            if (!string.IsNullOrEmpty(rep.Skipped))
                return $"未导入：{rep.Label} — {rep.Skipped}";
            return $"导入完成 · {rep.Label}：新增 {rep.Added} 行 · 共 {r.Total:N0} 个项目";
        });
    }

    private void OnTemplate(object sender, RoutedEventArgs e)
    {
        var (bytes, defaultName) = AdminService.LixiangTemplate(App.State, _tab);
        var path = PickSave(defaultName);
        if (path == null) return;
        Run(() =>
        {
            File.WriteAllBytes(path, bytes);
            return "模板已导出：" + Path.GetFileName(path);
        });
    }

    // ==================================================================
    // 解读辅导
    // ==================================================================

    private void LoadJd()
    {
        var issue = (IssueSel.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        var q = JdQuery.Text?.Trim() ?? "";
        try
        {
            var r = App.State.Lixiang.JieduList(issue, q);

            _building = true;
            var cur = (IssueSel.SelectedItem as ComboBoxItem)?.Tag as string;
            IssueSel.Items.Clear();
            IssueSel.Items.Add(new ComboBoxItem { Content = "全部期次", Tag = "" });
            foreach (var it in r.Issues)
                IssueSel.Items.Add(new ComboBoxItem
                {
                    Content = $"第{it.Issue}期（{it.Total}条）",
                    Tag = it.Issue.ToString(),
                });
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

            JdPageInfo.Text = $"共 {r.Issues.Count} 期 · 命中 {r.Total:N0} 条问答";
            JdStatus.Text = r.Total == 0 ? "无匹配条目" : "";
        }
        catch (Exception ex)
        {
            JdGrid.ItemsSource = null;
            JdStatus.Text = "加载失败：" + ex.Message;
        }
    }

    private void OnIssueChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_building || !_loaded) return;
        LoadJd();
    }

    private void OnJdSearch(object sender, RoutedEventArgs e) => LoadJd();

    private void OnJdQueryKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        LoadJd();
    }

    private void OnJieduImport(object sender, RoutedEventArgs e)
    {
        var path = PickOpen("选择解读辅导文档", "解读文档|*.doc;*.docx;*.txt|所有文件|*.*");
        if (path == null) return;

        var mode = MessageBox.Show(
            "导入方式：\n\n" +
            "【是】合并 —— 按（期, 题号）更新，其余保留\n" +
            "【否】替换 —— 清空全部解读后导入\n\n" +
            "取消则放弃本次导入。",
            "选择导入方式", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (mode == MessageBoxResult.Cancel) return;

        Run(() =>
        {
            var r = AdminService.ImportJiedu(App.State, File.ReadAllBytes(path), Path.GetFileName(path),
                mode == MessageBoxResult.Yes ? "merge" : "replace");
            LoadJd();
            return $"解读导入完成 · 新增 {r.Added} · 更新 {r.Updated} · 共 {r.Total:N0} 条 / {r.Issues} 期";
        });
    }

    private void OnJieduDeleteIssue(object sender, RoutedEventArgs e)
    {
        var issue = (IssueSel.SelectedItem as ComboBoxItem)?.Tag as string;
        if (string.IsNullOrEmpty(issue)) { JdStatus.Text = "请先在下拉框中选择要删除的期次"; return; }
        if (MessageBox.Show($"确认删除第 {issue} 期的全部解读条目？", "删除确认",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

        Run(() =>
        {
            var r = App.State.Lixiang.JieduRemoveIssue(issue);
            AuditLog.Write(AuditAction.Delete, "立项指南", $"删除第 {issue} 期解读 {r.Removed} 条");
            LoadJd();
            return $"已删除第 {issue} 期 {r.Removed} 条，剩余 {r.Total:N0} 条";
        });
    }

    // ==================================================================
    // 通用工具
    // ==================================================================

    private void Run(Func<string> action) => AdminUi.Run(action, StatusText);

    /// <summary>
    /// 字段构造：
    /// 项目类别 = 可编辑下拉（候选 = 参数表 ∪ 全部区段名 ∪ 数据已有值），留空默认取所属区段名；
    /// 计价单位 = 可编辑下拉（候选 = 参数表 ∪ 数据已有值）。
    /// 两者都允许自由输入，新增类别/单位不需要改代码或配置文件。
    /// </summary>
    private List<RowEditDialog.Field> BuildFields(Dictionary<string, string> flat)
    {
        var s = SettingsStore.Current;
        var secLabel = App.State.Lixiang.SectionCounts().TryGetValue(_tab, out var sc)
            ? (sc.Label ?? "").Trim() : "";
        var unitOptions = MergeOptions(s.LixiangUnits, App.State.Lixiang.DistinctUnits());
        var catOptions = MergeOptions(
            MergeOptions(s.LixiangCategories, App.State.Lixiang.SectionLabels()),
            App.State.Lixiang.DistinctCategories());

        return LixiangStore.Columns.Select(c =>
        {
            var value = flat.TryGetValue(c.Key, out var v) ? v : "";
            // 新增（无值）时类别预填当前区段名，作为默认值
            if (c.Key == "category" && string.IsNullOrWhiteSpace(value)) value = secLabel;
            return new RowEditDialog.Field(
                c.Key,
                c.Label + (c.Key == "seq" ? "（留空自动取最大值+1）"
                         : c.Key == "category" ? "（可下拉检索，留空默认取所属区段名）" : ""),
                value,
                Multiline: c.Key is "serviceOutput" or "priceComposition" or "usage" or "addItems" or "extItems",
                ReadOnly: c.Key is "seq",
                Options: c.Key == "unit" ? unitOptions
                       : c.Key == "category" ? catOptions
                       : null);
        }).ToList();
    }

    /// <summary>合并参数表与数据提取值（配置在前，去重保序）</summary>
    private static List<string> MergeOptions(List<string> configured, List<string> fromData)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var v in (configured ?? new List<string>()).Concat(fromData ?? new List<string>()))
        {
            var t = (v ?? "").Trim();
            if (t.Length == 0 || !seen.Add(t)) continue;
            list.Add(t);
        }
        return list;
    }

    private static Dictionary<string, string> ToFlat(LixiangRow r) => new()
    {
        ["seq"] = r.Seq ?? "",
        ["name"] = r.Name ?? "",
        ["category"] = r.Category ?? "",
        ["unit"] = r.Unit ?? "",
        ["serviceOutput"] = r.ServiceOutput ?? "",
        ["priceComposition"] = r.PriceComposition ?? "",
        ["addItems"] = r.AddItems ?? "",
        ["extItems"] = r.ExtItems ?? "",
        ["usage"] = r.Usage ?? "",
    };

    private static string PickOpen(string title, string filter)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private static string PickSave(string defaultName)
    {
        var dlg = new SaveFileDialog
        {
            Title = "导出模板",
            FileName = defaultName,
            Filter = "Excel 文件|*.xlsx",
            DefaultExt = ".xlsx",
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private static string PickMode(string what)
    {
        var r = MessageBox.Show(
            $"导入方式（{what}）：\n\n" +
            "【是】追加更新 —— 同名项目覆盖，其余新增\n" +
            "【否】替 换 —— 先清空该区段再写入\n\n" +
            "取消则放弃本次导入。",
            "选择导入方式", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return r switch
        {
            MessageBoxResult.Yes => "upsert",
            MessageBoxResult.No => "replace",
            _ => null,
        };
    }

    // ==================================================================
    // 视图模型
    // ==================================================================

    /// <summary>价格项目行（含详情卡用的字段块）</summary>
    public sealed class ItemVm
    {
        public ItemVm(LixiangRow r)
        {
            Row = r;
            Blocks = new List<Block>
            {
                new("服务产出", r.ServiceOutput),
                new("价格构成", r.PriceComposition),
                new("加收项", r.AddItems),
                new("扩展项", r.ExtItems),
                new("使用说明", r.Usage),
            }.Where(b => !string.IsNullOrWhiteSpace(b.Text)).ToList();
        }

        public LixiangRow Row { get; }
        public string Seq => Row.Seq;
        public string Name => Row.Name;
        public string Category => Row.Category;
        public string Unit => Row.Unit;
        public string Usage => Row.Usage;
        public string SeqChip => string.IsNullOrWhiteSpace(Row.Seq) ? "#" + Row.Id : "序号 " + Row.Seq;
        public List<Block> Blocks { get; }

        public sealed record Block(string Label, string Text);
    }

    /// <summary>解读辅导行</summary>
    public sealed class JdRowVm
    {
        public int Issue { get; set; }
        public int No { get; set; }
        public string Q { get; set; }
        public string A { get; set; }
        public string IssueText => $"第{Issue}期";
        public string IssueChip => $"第{Issue}期 · {No}";
    }
}
