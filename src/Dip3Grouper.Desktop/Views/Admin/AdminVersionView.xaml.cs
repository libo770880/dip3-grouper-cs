using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Dip3Grouper;

namespace Dip3GrouperApp.Desktop.Views.Admin;

/// <summary>
/// 版本管理：病种库的历史快照、两版差异比对与一键回滚。
///
/// <para>快照在**每次改动之前**自动生成（整库导入 / 区段导入 / 综合病组导入 / 后台逐行增删改），
/// 内容与上一版一致时不再重复留档，因此历史列表就是「病种库历次真正的状态」。
/// 手动点「新建版本快照」可额外打一个带备注的里程碑版本（例如「2026 版定稿」）。</para>
/// </summary>
public partial class AdminVersionView : UserControl
{
    private bool _loaded;

    /// <summary>最近一次比较结果（导出用）</summary>
    private VersionDiff _lastDiff;

    public AdminVersionView()
    {
        InitializeComponent();
    }

    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        LoadCurrent();
        RefreshList();
    }

    // ==================================================================
    // 载入
    // ==================================================================

    private void LoadCurrent()
    {
        try
        {
            var counts = Diseases.SectionCounts(App.State.Config);
            var total = counts.Values.Sum(x => x.Total);
            TxtCurrent.Text = $"{counts.Count} 个区段 · {total:N0} 条　（配置 config/dip3-config.json）";

            var fp = VersionStore.CurrentFingerprint();
            TxtFingerprint.Text = fp == null ? "配置文件不存在" : fp;

            SyncButtonState();
        }
        catch (Exception ex)
        {
            SetStatus(TxtStatus, "读取当前病种库失败：" + ex.Message, false);
        }
    }

    private void RefreshList()
    {
        try
        {
            var list = VersionStore.List();
            Grid.ItemsSource = list;
            if (list.Count > 0 && Grid.SelectedIndex < 0) Grid.SelectedIndex = 0;
            TxtListStatus.Text = list.Count == 0
                ? "还没有任何历史版本，改动病种库或点「新建版本快照」即可生成。"
                : $"共 {list.Count} 个版本（新 → 旧）";
        }
        catch (Exception ex)
        {
            SetStatus(TxtListStatus, "读取版本列表失败：" + ex.Message, false);
        }
    }

    private void SyncButtonState()
    {
        var has = Grid.SelectedItem is VersionEntry;
        BtnRestore.IsEnabled = has;
        BtnDelete.IsEnabled = has;
        BtnCompareCur.IsEnabled = has;
        BtnPrev.IsEnabled = has;
        BtnExport.IsEnabled = _lastDiff != null;
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        LoadCurrent();
        RefreshList();
        SetStatus(TxtStatus, "已刷新。", true);
    }

    private void OnSelect(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded && !_loaded) return;
        _lastDiff = null;
        SyncButtonState();
        if (Grid.SelectedItem is VersionEntry ve)
            SetStatus(TxtListStatus, $"已选中 {ve.Id}（{ve.AtText} · {ve.Title}）", true);
    }

    // ==================================================================
    // 新建
    // ==================================================================

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        var note = TxtNote.Text?.Trim() ?? "";
        try
        {
            var entry = VersionStore.Create(string.IsNullOrEmpty(note) ? "手动创建" : note, note, auto: false);
            if (note.Length == 0) TxtNote.Text = "";
            TxtStatus.Text = "";
            RefreshList();
            Grid.SelectedItem = (Grid.ItemsSource as List<VersionEntry>)?.FirstOrDefault(x => x.Id == entry.Id);
            SetStatus(TxtStatus, $"已创建版本 {entry.Id}", true);
        }
        catch (Exception ex)
        {
            SetStatus(TxtStatus, "创建失败：" + ex.Message, false);
        }
    }

    // ==================================================================
    // 比对
    // ==================================================================

    private void OnCompareCurrent(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not VersionEntry ve) return;
        try
        {
            _lastDiff = VersionStore.DiffWithCurrent(ve.Id);
            ShowDiff(_lastDiff, $"{ve.Id}（{ve.AtText}）　→　当前磁盘内容");
        }
        catch (Exception ex)
        {
            SetStatus(TxtListStatus, "比较失败：" + ex.Message, false);
        }
    }

    private void OnComparePrev(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not VersionEntry ve) return;
        var list = Grid.ItemsSource as List<VersionEntry>;
        var idx = list?.FindIndex(x => x.Id == ve.Id) ?? -1;
        if (list == null || idx < 0 || idx + 1 >= list.Count)
        {
            SetStatus(TxtListStatus, "这是最早的版本，没有更早的可比版本。", false);
            return;
        }
        var older = list[idx + 1];
        try
        {
            _lastDiff = VersionStore.Diff(older.Id, ve.Id);
            ShowDiff(_lastDiff, $"{older.Id}（{older.AtText}）　→　{ve.Id}（{ve.AtText}）");
        }
        catch (Exception ex)
        {
            SetStatus(TxtListStatus, "比较失败：" + ex.Message, false);
        }
    }

    private void ShowDiff(VersionDiff d, string header)
    {
        GridDiff.ItemsSource = d.Sections;
        TxtDiffHead.Text = $"{header}　·　{d.SummaryText}";
        SetStatus(TxtListStatus, d.Same ? "两个版本完全一致。" : d.SummaryText, true);
        SyncButtonState();
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (_lastDiff == null)
        {
            SetStatus(TxtListStatus, "请先做一次比较。", false);
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出差异报告",
            FileName = $"病种库版本差异-{_lastDiff.LeftId}-{_lastDiff.RightId}.txt",
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            DefaultExt = ".txt",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, VersionStore.DiffReportText(_lastDiff), new UTF8Encoding(true));
            AuditLog.Write(AuditAction.Export, "版本", $"导出病种库版本差异报告 {Path.GetFileName(dlg.FileName)}");
            SetStatus(TxtListStatus, "已导出：" + dlg.FileName, true);
        }
        catch (Exception ex)
        {
            SetStatus(TxtListStatus, "导出失败：" + ex.Message, false);
        }
    }

    // ==================================================================
    // 回滚 / 删除
    // ==================================================================

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not VersionEntry ve) return;
        if (MessageBox.Show(
                $"将把病种库回滚到 {ve.Id}（{ve.AtText}）：\n{ve.Title} · {ve.CountsText}\n\n" +
                "当前内容会先备份到 data/_backup/，回滚后立即可用。\n\n确认回滚？",
                "回滚确认", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

        try
        {
            var res = VersionStore.Restore(ve.Id);
            if (res.Applied.Count > 0) App.State.ReloadAll();
            SetStatus(TxtListStatus, res.Message, res.Ok);
            LoadCurrent();
            RefreshList();
        }
        catch (Exception ex)
        {
            SetStatus(TxtListStatus, "回滚失败：" + ex.Message, false);
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not VersionEntry ve) return;
        if (MessageBox.Show($"确认删除版本 {ve.Id}（{ve.AtText}）？此操作不可恢复。",
                "删除确认", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

        var ok = VersionStore.Delete(ve.Id);
        SetStatus(TxtListStatus, ok ? "已删除 " + ve.Id : "删除失败，文件可能被占用。", ok);
        RefreshList();
    }

    private void OnOpenDir(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(VersionStore.Root);
            Process.Start("explorer.exe", $"\"{VersionStore.Root}\"");
        }
        catch (Exception ex)
        {
            SetStatus(TxtStatus, "打开目录失败：" + ex.Message, false);
        }
    }

    // ==================================================================
    // 内部
    // ==================================================================

    private void SetStatus(TextBlock target, string text, bool ok)
    {
        target.Text = text ?? "";
        target.SetResourceReference(TextBlock.ForegroundProperty, ok ? "B.Muted" : "B.Danger");
    }

    // ---- 供自检直读 / 驱动 ----

    internal int DiagVersionCount => Grid.Items.Count;

    internal VersionEntry DiagSelected => Grid.SelectedItem as VersionEntry;

    internal void DiagSelectFirst()
    {
        if (Grid.Items.Count > 0) Grid.SelectedIndex = 0;
    }

    internal int DiagDiffRows => GridDiff.Items.Count;

    /// <summary>
    /// 诊断专用（<c>--shot-page admverp</c>）：真实库里可能还没有任何历史版本，
    /// 拿一组**只在内存里**的假版本与假差异填充界面，供离屏截图核对表格版式
    /// （列宽、行高、明细换行、按钮启用态）。**不写盘、不写审计。**
    /// </summary>
    internal void DiagPreview()
    {
        EnsureLoaded();

        var files = VersionStore.Files.Select(s => new VersionFile
        {
            Name = s.Name, Label = s.Label, RelPath = s.RelPath,
            Exists = true, Size = 187_392, Sha256 = new string('a', 64),
        }).ToList();

        var real = Diseases.SectionCounts(App.State.Config);
        var now = DateTime.UtcNow;
        var fp = "174e68bb9c0d4a5f6e7b8c9d0e1f2a3b4c5d6e7f8091a2b3c4d5e6f708192a3b";

        VersionEntry Make(int minsAgo, string source, bool auto, string note, int delta)
        {
            var counts = real.ToDictionary(kv => kv.Key, kv => kv.Value.Total);
            if (delta != 0) counts["bingxiang"] = Math.Max(0, counts["bingxiang"] + delta);
            return new VersionEntry
            {
                Id = now.AddMinutes(-minsAgo).ToLocalTime().ToString("yyyyMMdd-HHmmss"),
                At = now.AddMinutes(-minsAgo).ToString("o"),
                User = "admin", Source = source, Auto = auto, Note = note,
                Counts = counts, Total = counts.Values.Sum(),
                AppVersion = AppVersion.Current, Files = files, Fingerprint = fp,
            };
        }

        var list = new List<VersionEntry>
        {
            Make(38, "手动创建", false, "2026 版病种库定稿", 0),
            Make(76, "保存前自动 · 后台编辑", true, null, -1),
            Make(1_140, "导入前自动 · 整库覆盖", true, null, -7),
        };

        // 差异：并项核心病种 +3/-1、基础核心病种 ~2，其余无变化
        var diff = new VersionDiff
        {
            LeftId = list[1].Id, RightId = list[0].Id,
            LeftAt = list[1].AtText, RightAt = list[0].AtText,
        };
        foreach (var s in Diseases.Sections)
        {
            var n = real.TryGetValue(s.Tab, out var sc) ? sc.Total : 0;
            var sec = new VersionDiffSection { Tab = s.Tab, Label = s.Label, OldCount = n, NewCount = n };
            if (s.Tab == "bingxiang")
            {
                sec.AddedCount = 3; sec.RemovedCount = 1; sec.NewCount = n + 2;
                sec.Added = new List<string> { "9552", "9601", "9610" };
                sec.Removed = new List<string> { "8413" };
            }
            else if (s.Tab == "jichu")
            {
                sec.ChangedCount = 2;
                sec.Changed = new List<string> { "K80.001", "I63.501" };
            }
            diff.Sections.Add(sec);
        }
        diff.AddedTotal = diff.Sections.Sum(x => x.AddedCount);
        diff.RemovedTotal = diff.Sections.Sum(x => x.RemovedCount);
        diff.ChangedTotal = diff.Sections.Sum(x => x.ChangedCount);
        diff.Files = VersionStore.Files.Select(f => new VersionFileDiff
        {
            Label = f.Label, RelPath = f.RelPath, InLeft = true, InRight = true,
            LeftSha = new string('a', 64), RightSha = new string('a', 64),
        }).ToList();

        Grid.ItemsSource = list;
        Grid.SelectedIndex = 0;                       // 会触发 OnSelect（清掉 _lastDiff），故比较放在其后
        ShowDiff(diff, "诊断预览（内存假数据，未落盘）");
    }
}
