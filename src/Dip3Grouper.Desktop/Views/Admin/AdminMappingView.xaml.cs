using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Dip3Grouper;
using Dip3GrouperApp.Desktop.Controls;
using Microsoft.Win32;

namespace Dip3GrouperApp.Desktop.Views.Admin;

/// <summary>
/// 映射与灰码：ICD-10 → 医保编码映射表（含灰码标记）。
/// 与网页版后台「映射与灰码」等价，直接调用 Core（不经 HTTP）。
/// 综合病组不在本页维护（后台已无该菜单，兜底层数据见 data/zonghe.json）。
/// </summary>
public partial class AdminMappingView : UserControl
{
    private const int PageSize = 50;

    private static readonly string[] MapKeys = { "code", "name", "insCode", "insName", "gray", "note" };
    private static readonly string[] MapLabels = { "ICD-10 编码", "诊断名称", "医保编码", "医保名称", "是否灰码（是 / 空）", "备注" };

    private bool _loaded;
    private int _mapPage = 1;

    public AdminMappingView()
    {
        InitializeComponent();
    }

    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        LoadMap(1);
    }

    // ==================================================================
    // 映射表
    // ==================================================================

    private bool? GrayFilterValue()
    {
        var tag = (GrayFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        return tag switch { "1" => true, "0" => false, _ => null };
    }

    private void OnGrayFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        LoadMap(1);
    }

    private void OnMapSearch(object sender, RoutedEventArgs e) => LoadMap(1);

    private void OnMapQueryKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        LoadMap(1);
    }

    private void OnMapPrev(object sender, RoutedEventArgs e) => LoadMap(_mapPage - 1);

    private void OnMapNext(object sender, RoutedEventArgs e) => LoadMap(_mapPage + 1);

    private void OnMapDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MGrid.SelectedItem != null) EditMap();
    }

    private void LoadMap(int page)
    {
        _mapPage = Math.Max(1, page);
        var q = MapQuery.Text?.Trim() ?? "";
        try
        {
            var r = App.State.Mapping.List(q, GrayFilterValue(), _mapPage, PageSize);
            MGrid.ItemsSource = r.Rows;

            var pages = Math.Max(1, (int)Math.Ceiling(r.Total / (double)r.PageSize));
            var st = App.State.Mapping.Stats();
            MapPageInfo.Text = $"第 {r.Page} / {pages} 页 · 命中 {r.Total:N0} 条";
            MapStatus.Text = r.Total == 0 ? "无匹配记录" : "";
            TxtMeta.Text = $"映射表 {st.Total:N0} 条 · 已映射 {st.Mapped:N0} · 灰码 {st.Gray:N0}" +
                           (string.IsNullOrWhiteSpace(st.SourceName) ? "" : $" · 来源 {st.SourceName}");

            MapPrev.IsEnabled = r.Page > 1;
            MapNext.IsEnabled = r.Page < pages;
        }
        catch (Exception ex)
        {
            MGrid.ItemsSource = null;
            MapStatus.Text = "加载失败：" + ex.Message;
        }
    }

    /// <summary>勾选/取消灰码（对齐 Web 的 mapping-gray）</summary>
    private void OnGrayToggle(object sender, RoutedEventArgs e)
    {
        var row = (sender as FrameworkElement)?.DataContext as MappingRow;
        if (row == null) return;
        var want = (sender as CheckBox)?.IsChecked == true;
        if (want == row.Gray) return;

        Run(msg => MapStatus.Text = msg, () =>
        {
            if (!App.State.Mapping.SetGray(row.Id, want)) throw new InvalidOperationException("记录不存在");
            AuditLog.Write(AuditAction.Edit, "映射与灰码",
                $"{(want ? "标记灰码" : "取消灰码")} {row.Code}（{row.Name}）");
            LoadMap(_mapPage);
            return $"已{(want ? "标记为灰码" : "取消灰码")}：{row.Code}";
        });
    }

    private void OnMapAdd(object sender, RoutedEventArgs e)
    {
        var flat = EditRow("新增映射记录", null);
        if (flat == null) return;

        Run(msg => MapStatus.Text = msg, () =>
        {
            var (err, id) = App.State.Mapping.Add(ToElement(flat));
            if (err != null) throw new InvalidOperationException(err);
            AuditLog.Write(AuditAction.Add, "映射与灰码", $"新增映射 #{id} {flat.GetValueOrDefault("code")}");
            LoadMap(_mapPage);
            return $"已新增 #{id}";
        });
    }

    private void OnMapEdit(object sender, RoutedEventArgs e) => EditMap();

    private void EditMap()
    {
        var row = MGrid.SelectedItem as MappingRow;
        if (row == null) { MapStatus.Text = "请先选中一行"; return; }

        var flat = EditRow($"编辑 · #{row.Id}", new Dictionary<string, string>
        {
            ["code"] = row.Code ?? "",
            ["name"] = row.Name ?? "",
            ["insCode"] = row.InsCode ?? "",
            ["insName"] = row.InsName ?? "",
            ["gray"] = row.Gray ? "是" : "",
            ["note"] = row.Note ?? "",
        });
        if (flat == null) return;

        Run(msg => MapStatus.Text = msg, () =>
        {
            if (!App.State.Mapping.Update(row.Id, ToElement(flat))) throw new InvalidOperationException("记录不存在");
            AuditLog.Write(AuditAction.Edit, "映射与灰码", $"编辑映射 #{row.Id} {row.Code}");
            LoadMap(_mapPage);
            return "已保存 #" + row.Id;
        });
    }

    private void OnMapDelete(object sender, RoutedEventArgs e)
    {
        var row = MGrid.SelectedItem as MappingRow;
        if (row == null) { MapStatus.Text = "请先选中一行"; return; }
        if (MessageBox.Show($"确认删除映射记录 {row.Code}（{row.Name}）？", "删除确认",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

        Run(msg => MapStatus.Text = msg, () =>
        {
            if (!App.State.Mapping.Remove(row.Id)) throw new InvalidOperationException("记录不存在");
            AuditLog.Write(AuditAction.Delete, "映射与灰码", $"删除映射 #{row.Id} {row.Code}（{row.Name}）");
            LoadMap(_mapPage);
            return "已删除 " + row.Code;
        });
    }

    private void OnMapImport(object sender, RoutedEventArgs e)
    {
        var path = PickOpen("选择映射表 Excel", "Excel 文件|*.xlsx;*.xls|所有文件|*.*");
        if (path == null) return;

        var mode = MessageBox.Show(
            "导入方式：\n\n" +
            "【是】追 加 —— 编码已存在则更新，其余新增\n" +
            "【否】替 换 —— 清空整张映射表后写入\n\n" +
            "取消则放弃本次导入。",
            "选择导入方式", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (mode == MessageBoxResult.Cancel) return;

        Run(msg => MapStatus.Text = msg, () =>
        {
            var r = AdminService.ImportMapping(App.State, File.ReadAllBytes(path), Path.GetFileName(path),
                mode == MessageBoxResult.Yes ? "append" : "replace");
            LoadMap(1);
            return $"导入完成 · 解析 {r.Parsed:N0} · 新增 {r.Added:N0} · 更新 {r.Updated:N0} · 共 {r.Total:N0} 条";
        });
    }

    /// <summary>映射表字段 → JSON 元素（gray 用布尔，Update 才认得出来）</summary>
    private static JsonElement ToElement(Dictionary<string, string> flat)
    {
        var payload = new Dictionary<string, object>
        {
            ["code"] = flat.TryGetValue("code", out var c) ? c : "",
            ["name"] = flat.TryGetValue("name", out var n) ? n : "",
            ["insCode"] = flat.TryGetValue("insCode", out var ic) ? ic : "",
            ["insName"] = flat.TryGetValue("insName", out var iname) ? iname : "",
            ["note"] = flat.TryGetValue("note", out var note) ? note : "",
            ["gray"] = IsYes(flat.TryGetValue("gray", out var g) ? g : ""),
        };
        return JsonSerializer.SerializeToElement(payload);
    }

    /// <summary>映射表行编辑对话框（字段固定为 6 列）</summary>
    private Dictionary<string, string> EditRow(string title, Dictionary<string, string> current)
    {
        var fields = MapKeys.Select((k, i) => new RowEditDialog.Field(
            k, MapLabels[i],
            current != null && current.TryGetValue(k, out var v) ? v : "",
            Multiline: k is "name" or "insName" or "note",
            ReadOnly: false)).ToList();
        return RowEditDialog.Edit(Window.GetWindow(this), title, fields);
    }

    private static bool IsYes(string s)
    {
        var t = (s ?? "").Trim().ToLowerInvariant();
        return GrouperService.GrayTrueValues.Contains(t);
    }

    // ==================================================================
    // 通用工具
    // ==================================================================

    private void Run(Action<string> report, Func<string> action)
    {
        try
        {
            report(action());
        }
        catch (Exception ex)
        {
            report("操作失败：" + ex.Message);
            MessageBox.Show(ex.Message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
            Title = "导出模板",
            FileName = defaultName,
            Filter = "Excel 文件|*.xlsx",
            DefaultExt = ".xlsx",
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }
}
