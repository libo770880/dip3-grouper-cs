using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Dip3Grouper;
using Dip3GrouperApp.Desktop.Controls;
using Microsoft.Win32;

namespace Dip3GrouperApp.Desktop.Views.Admin;

/// <summary>
/// 病种库管理：区段浏览 / 增删改 / 整库导入 / 区段导入 / 模板导出。
/// 与网页版后台「病种库管理」等价，直接调用 Core（不经 HTTP）。
/// </summary>
public partial class AdminDiseasesView : UserControl
{
    private const int PageSize = 50;

    private bool _loaded;
    private bool _building;
    private string _tab = "bingxiang";
    private int _page = 1;

    public AdminDiseasesView()
    {
        InitializeComponent();
    }

    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        BuildTabs();
        Load(1);
    }

    // ==================================================================
    // 区段胶囊
    // ==================================================================

    private void BuildTabs()
    {
        var counts = Diseases.SectionCounts(App.State.Config);
        TabsPanel.Children.Clear();
        _building = true;
        foreach (var (tab, sec) in counts)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = sec.Label, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock
            {
                Text = sec.Total.ToString("N0"),
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Opacity = 0.6,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });

            var rb = new RadioButton
            {
                GroupName = "AdminDiseaseTabs",
                Style = (Style)FindResource("Pill"),
                Tag = tab,
                IsChecked = tab == _tab,
                Content = sp,
            };
            rb.Checked += OnTabChecked;
            TabsPanel.Children.Add(rb);
        }
        _building = false;

        var meta = MappingStore.LoadConfigMeta();
        TxtMeta.Text = string.IsNullOrWhiteSpace(meta.SourceName)
            ? "尚未导入过病种库"
            : $"来源 {meta.SourceName} · {meta.ImportedAt}";
    }

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        if (_building || !_loaded) return;
        var tab = (sender as RadioButton)?.Tag as string;
        if (string.IsNullOrEmpty(tab) || tab == _tab) return;
        _tab = tab;
        Load(1);
    }

    // ==================================================================
    // 列表
    // ==================================================================

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
            var r = Diseases.ListRows(App.State.Config, _tab, q, _page, PageSize);
            FlatGrid.BuildColumns(Grid, r.Columns, this);
            Grid.ItemsSource = r.Rows;

            var pages = Math.Max(1, (int)Math.Ceiling(r.Total / (double)r.PageSize));
            PageInfo.Text = $"{r.Label} · 第 {r.Page} / {pages} 页 · 共 {r.Total:N0} 条";
            BtnPrev.IsEnabled = r.Page > 1;
            BtnNext.IsEnabled = r.Page < pages;
            StatusText.Text = r.Total == 0 ? "无匹配记录" : "";
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

    private void Reload() => Load(_page);

    private Dictionary<string, object> SelectedRow() => Grid.SelectedItem as Dictionary<string, object>;

    // ==================================================================
    // 增 / 改 / 删
    // ==================================================================

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var sec = Diseases.Get(_tab);
        if (sec == null) return;
        var fields = FlatGrid.ToEditFields(sec.Columns, null);
        var flat = RowEditDialog.Edit(Window.GetWindow(this), $"新增 · {sec.Label}", fields);
        if (flat == null) return;

        try
        {
            var r = Diseases.AddRow(App.State.Config, _tab, flat);
            if (!r.Ok) { MessageBox.Show(r.Error, "新增失败", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            SaveAndRefresh(AuditAction.Add, $"新增 {sec.Label} 记录 {r.Row}");
            StatusText.Text = "已新增，主键 " + r.Row;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "新增失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnEdit(object sender, RoutedEventArgs e) => EditSelected();

    private void EditSelected()
    {
        var row = SelectedRow();
        var sec = Diseases.Get(_tab);
        if (row == null || sec == null) { StatusText.Text = "请先选中一行"; return; }

        var key = FlatGrid.Format(row.TryGetValue(sec.Key, out var kv) ? kv : null);
        var fields = FlatGrid.ToEditFields(sec.Columns, row);
        var flat = RowEditDialog.Edit(Window.GetWindow(this), $"编辑 · {sec.Label}", fields);
        if (flat == null) return;

        try
        {
            var r = Diseases.UpdateRow(App.State.Config, _tab, key, flat);
            if (!r.Ok) { MessageBox.Show(r.Error, "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            SaveAndRefresh(AuditAction.Edit, $"编辑 {sec.Label} 记录 {key}");
            StatusText.Text = "已保存 " + key;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        var row = SelectedRow();
        var sec = Diseases.Get(_tab);
        if (row == null || sec == null) { StatusText.Text = "请先选中一行"; return; }

        var key = FlatGrid.Format(row.TryGetValue(sec.Key, out var kv) ? kv : null);
        var label = row.TryGetValue("name", out var nm) ? FlatGrid.Format(nm) : "";
        if (MessageBox.Show($"确认删除 {sec.Label} 的 {key} {(label.Length > 0 ? "（" + label + "）" : "")}？",
                "删除确认", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

        try
        {
            var r = Diseases.RemoveRow(App.State.Config, _tab, key);
            if (!r.Ok) { MessageBox.Show(r.Error, "删除失败", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            SaveAndRefresh(AuditAction.Delete, $"删除 {sec.Label} 记录 {key}");
            StatusText.Text = "已删除 " + key;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "删除失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 落盘 + 排除表刷新 + 胶囊条数与列表刷新（对齐 Web 侧写操作后续动作）。
    /// 落盘**之前**先给病种库留一个自动版本快照（内容没变则自动跳过），
    /// 这样「版本管理」里看到的历史就是历次改动前的真实状态，随时可回滚。
    /// </summary>
    private void SaveAndRefresh(string action, string detail)
    {
        VersionStore.Create("保存前自动 · 后台编辑", detail, auto: true);
        Diseases.SaveConfig(App.State.Config);
        if (_tab is "excludedDiags" or "excludedProcs") App.State.Grouper.RefreshExclusions();
        AuditLog.Write(action, "病种库", detail);
        BuildTabs();
        Load(_page);
    }

    // ==================================================================
    // 导入 / 模板
    // ==================================================================

    /// <summary>整库导入：一整份 xlsx 覆盖 config/dip3-config.json。</summary>
    private void OnImportConfig(object sender, RoutedEventArgs e)
    {
        var path = PickOpen("选择病种库 Excel（整库）", "Excel 文件|*.xlsx;*.xls|所有文件|*.*");
        if (path == null) return;
        if (MessageBox.Show(
                "将用所选文件整份覆盖当前病种库配置。\n\n" +
                "旧配置会自动备份为 config/dip3-config-<时间戳>.bak.json。\n\n确认继续？",
                "整库导入确认", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

        Run(() =>
        {
            var meta = AdminService.ImportConfig(App.State, File.ReadAllBytes(path), Path.GetFileName(path));
            _page = 1;
            BuildTabs();
            Load(1);
            return $"整库导入完成 · 来源 {meta.SourceName}";
        });
    }

    /// <summary>区段导入：按表头匹配列写入当前区段，可选「追加更新 / 替换」。</summary>
    private void OnImportSection(object sender, RoutedEventArgs e)
    {
        var sec = Diseases.Get(_tab);
        var path = PickOpen($"选择「{sec?.Label}」的 Excel", "Excel 文件|*.xlsx;*.xls|所有文件|*.*");
        if (path == null) return;

        var mode = PickMode(sec?.Label ?? _tab);
        if (mode == null) return;

        Run(() =>
        {
            var r = AdminService.ImportDiseases(App.State, File.ReadAllBytes(path), Path.GetFileName(path),
                _tab, mode);
            BuildTabs();
            Load(_page);
            var rep = r.Report.TryGetValue(_tab, out var one) ? one : null;
            return rep == null
                ? "该文件未识别出与当前区段匹配的表头"
                : $"{rep.Label}：新增 {rep.Added} · 更新 {rep.Updated} · 失败 {rep.ErrorCount}";
        });
    }

    private void OnTemplate(object sender, RoutedEventArgs e)
    {
        var sec = Diseases.Get(_tab);
        var (bytes, defaultName) = AdminService.DiseasesTemplate(_tab);
        var path = PickSave(defaultName);
        if (path == null) return;
        Run(() =>
        {
            File.WriteAllBytes(path, bytes);
            return $"模板已导出：{Path.GetFileName(path)}（区段 {sec?.Label}）";
        });
    }

    // ==================================================================
    // 通用小工具
    // ==================================================================

    /// <summary>执行一段可能失败的操作并把结果/异常写到状态条（实现在 AdminUi）。</summary>
    private void Run(Func<string> action) => AdminUi.Run(action, StatusText);

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

    /// <summary>追加更新 / 替换 二选一；返回 null 表示取消。</summary>
    private static string PickMode(string what)
    {
        var r = MessageBox.Show(
            $"导入方式（{what}）：\n\n" +
            "【是】追加更新 —— 主键已存在的行覆盖，其余新增\n" +
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
}
