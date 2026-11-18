using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Dip3Grouper;

namespace Dip3GrouperApp.Desktop.Views.Admin;

/// <summary>统计卡上的一枚「标签 + 值」。</summary>
public sealed class AuditChip
{
    public string Label { get; set; }
    public string Value { get; set; }
}

/// <summary>
/// 审计日志：把后台的登录与写操作按时间列出来，支持按动作 / 对象 / 关键词筛选、
/// 只看失败、导出 CSV 与清空。
///
/// <para>日志写入发生在 Core 层（<see cref="AuditLog"/>），网页端与桌面端的操作都会汇总到
/// 同一份 <c>data/audit-log.json</c>：网页端靠请求作用域带上会话账号，
/// 桌面端在登录后设置全局身份。本页只做只读展示与筛选。</para>
/// </summary>
public partial class AdminAuditView : UserControl
{
    /// <summary>下拉里的「全部」哨兵值（等于它就不加筛选条件）</summary>
    private const string All = "（全部）";

    private bool _loaded;
    /// <summary>当前筛选命中的全量（分页在内存里切，日志最多 1000 条，够快）</summary>
    private List<AuditEntry> _rows = new();
    private int _page = 1;
    private int _pageSize = 100;
    private bool _building;

    public AdminAuditView()
    {
        InitializeComponent();
    }

    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        BuildFilters();
        BuildPageSizes();
        Load();
    }

    private void BuildFilters()
    {
        var actions = new List<string> { All };
        actions.AddRange(AuditLog.Actions());
        ActionSel.ItemsSource = actions;
        ActionSel.SelectedIndex = 0;

        var targets = new List<string> { All };
        targets.AddRange(AuditLog.Targets());
        TargetSel.ItemsSource = targets;
        TargetSel.SelectedIndex = 0;
    }

    // ==================================================================
    // 查询
    // ==================================================================

    private AuditFilter CurrentFilter() => new()
    {
        Keyword = TxtQuery.Text,
        Action = ActionSel.SelectedItem as string == All ? null : ActionSel.SelectedItem as string,
        Target = TargetSel.SelectedItem as string == All ? null : TargetSel.SelectedItem as string,
        Ok = ChkFailOnly.IsChecked == true ? false : null,
        Limit = 1000,
    };

    private void Load()
    {
        try
        {
            _rows = AuditLog.Query(CurrentFilter());
            _page = 1;
            RenderPage();
        }
        catch (Exception ex)
        {
            _rows = new List<AuditEntry>();
            Grid.ItemsSource = null;
            PageInfo.Text = "";
            TxtStatus.Text = "读取日志失败：" + ex.Message;
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, "B.Danger");
        }
    }

    /// <summary>只渲染当前页：152 条时全部铺出来还能滚，上千条就会明显卡（每行 7 个模板列）</summary>
    private void RenderPage()
    {
        var pages = Math.Max(1, (int)Math.Ceiling(_rows.Count / (double)_pageSize));
        if (_page > pages) _page = pages;
        if (_page < 1) _page = 1;

        Grid.ItemsSource = _rows.Skip((_page - 1) * _pageSize).Take(_pageSize).ToList();
        PageInfo.Text = $"第 {_page}/{pages} 页 · 共 {_rows.Count:N0} 条";
        BtnPrev.IsEnabled = _page > 1;
        BtnNext.IsEnabled = _page < pages;
        JumpBox.Text = _page.ToString();

        RenderStats(_rows);
        TxtStatus.Text = _rows.Count == 0
            ? "没有符合条件的记录。"
            : $"共 {_rows.Count:N0} 条" + (_rows.Count >= 1000 ? "（仅显示最近 1000 条，可用筛选缩小范围）" : "");
        TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, "B.Muted");
    }

    private void OnPrev(object sender, RoutedEventArgs e)
    {
        if (_page <= 1) return;
        _page--;
        RenderPage();
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        var pages = Math.Max(1, (int)Math.Ceiling(_rows.Count / (double)_pageSize));
        if (_page >= pages) return;
        _page++;
        RenderPage();
    }

    private void OnJumpKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (int.TryParse((JumpBox.Text ?? "").Trim(), out var p) && p > 0)
        {
            _page = p;
            RenderPage();
        }
        else
        {
            JumpBox.Text = _page.ToString();
        }
    }

    private void OnPageSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_building) return;
        if (!int.TryParse((PageSizeSel.SelectedItem as ComboBoxItem)?.Tag as string, out var n)) return;
        if (n <= 0 || n == _pageSize) return;
        _pageSize = n;
        _page = 1;
        RenderPage();
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

    private void RenderStats(List<AuditEntry> rows)
    {
        var st = AuditLog.Stats();
        var chips = new List<AuditChip>
        {
            new() { Label = "总记录", Value = st.Total.ToString("N0") },
            new() { Label = "今日", Value = st.Today.ToString("N0") },
            new() { Label = "失败", Value = st.Failed.ToString("N0") },
            new() { Label = "命中", Value = rows.Count.ToString("N0") },
            new() { Label = "最近一条", Value = st.LastAtText },
        };
        StatChips.ItemsSource = chips;
    }

    private void OnQuery(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        Load();
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        TxtQuery.Text = "";
        if (ActionSel.Items.Count > 0) ActionSel.SelectedIndex = 0;
        if (TargetSel.Items.Count > 0) TargetSel.SelectedIndex = 0;
        ChkFailOnly.IsChecked = false;
        Load();
    }

    // ==================================================================
    // 导出 / 清空
    // ==================================================================

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var rows = Grid.ItemsSource as List<AuditEntry> ?? new List<AuditEntry>();
        if (rows.Count == 0)
        {
            TxtStatus.Text = "当前没有可导出的记录。";
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, "B.Danger");
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出审计日志",
            FileName = $"审计日志-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            DefaultExt = ".csv",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            // 字节由 Core 统一出（BOM 只带一次），别再在写盘处套一层带前导码的编码器
            File.WriteAllBytes(dlg.FileName, AuditLog.ExportCsvBytes(rows));
            AuditLog.Write(AuditAction.Export, "审计日志", $"导出审计日志 {rows.Count} 条 → {Path.GetFileName(dlg.FileName)}");
            TxtStatus.Text = "已导出：" + dlg.FileName;
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, "B.Muted");
            BuildFilters();
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "导出失败：" + ex.Message;
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, "B.Danger");
        }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("确认清空全部审计日志？此操作不可恢复（建议先导出 CSV 留存）。",
                "清空确认", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

        AuditLog.Clear();
        AuditLog.Write(AuditAction.Delete, "审计日志", "清空审计日志");   // 清空这件事本身也要留痕
        BuildFilters();
        Load();
    }

    // ---- 供自检直读 ----

    internal int DiagRows => Grid.Items.Count;

    /// <summary>
    /// 诊断专用（<c>--shot-page admauditp</c>）：真实日志可能只有寥寥几条，看不出表格版式。
    /// 这里用一组**只在内存里**的假记录填充界面，覆盖统计 chips、各种动作 / 对象 / 来源、
    /// 失败行着色与超长详情换行。**不写盘、不写审计。**
    /// </summary>
    internal void DiagPreview()
    {
        EnsureLoaded();

        var now = DateTime.UtcNow;
        AuditEntry Make(int minsAgo, string source, string action, string target, string detail,
                        bool ok = true, string note = null)
        {
            var at = now.AddMinutes(-minsAgo);
            return new AuditEntry
            {
                Id = at.ToString("yyyyMMddHHmmssfff"), At = at.ToString("o"),
                User = "admin", Source = source, Action = action, Target = target,
                Detail = detail, Ok = ok, Note = note,
            };
        }

        var rows = new List<AuditEntry>
        {
            Make(1, "网页端", AuditAction.Login, "后台登录", "网页端登录成功：账号 admin"),
            Make(6, "网页端", AuditAction.Edit, "病种库", "新增 并项核心病种 记录 9552"),
            Make(9, "桌面端", AuditAction.Version, "版本", "新建病种库版本 20261011-214512：2026 版病种库定稿（10 个区段 · 14,153 条）"),
            Make(14, "桌面端", AuditAction.Update, "数据", "在线更新：病种库配置 / 导入信息 / 综合病组 共 3 个文件（指纹 174e68bb → 9c2a1d04）"),
            Make(21, "桌面端", AuditAction.Update, "客户端更新", "下载客户端 1.6.0 失败", false, "连接超时：更新源 10.0.0.12:3220 无响应"),
            Make(28, "网页端", AuditAction.Import, "映射与灰码", "导入医保编码映射表 映射表-2026Q4.xlsx：1 个区段 · 新增 412 行 · 更新 37 行 · 失败 0 行"),
            Make(35, "网页端", AuditAction.Setting, "系统设置", "软件名称改为「DIP 3.0 分组器（德州）」"),
            Make(43, "桌面端", AuditAction.Login, "后台登录", "桌面端登录失败：账号 admin", false, "口令不正确"),
            Make(52, "桌面端", AuditAction.Export, "审计日志", "导出审计日志 126 条 → 审计日志-20261011-2140.csv"),
            Make(61, "网页端", AuditAction.Delete, "德州物价", "删除物价项目 013315000010000（一次性使用无菌注射器）"),
        };

        TxtQuery.Text = "";
        if (ActionSel.Items.Count > 0) ActionSel.SelectedIndex = 0;
        if (TargetSel.Items.Count > 0) TargetSel.SelectedIndex = 0;
        ChkFailOnly.IsChecked = false;

        Grid.ItemsSource = rows;
        RenderStats(rows);
        TxtStatus.Text = "诊断预览：界面用内存假数据填充，未写盘、未写审计。";
        TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, "B.Muted");
    }
}
