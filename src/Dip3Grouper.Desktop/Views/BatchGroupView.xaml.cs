using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dip3Grouper;
using Microsoft.Win32;
using Dip3GrouperApp.Desktop.Theming;

namespace Dip3GrouperApp.Desktop.Views;

/// <summary>批量分组结果表格的一行（含展示用计算属性）。</summary>
public class BatchRowVm : INotifyPropertyChanged
{
    public int Seq { get; set; }
    public string CaseId { get; set; }
    public string MainDiag { get; set; }
    public string MainProc { get; set; }
    public bool Matched { get; set; }
    public string GroupType { get; set; }
    public string GroupTypeCode { get; set; }
    public string DipSeq { get; set; }
    public string NameOrReason { get; set; }
    /// <summary>命中省局「目录库外病种」清单时的键（如 H65-22.02）；空 = 未命中</summary>
    public string OutLibLabel { get; set; } = "";
    /// <summary>完整组合，供 Tooltip 展示</summary>
    public string OutLibTip { get; set; } = "";

    public string MatchedText => Matched ? "是" : "否";

    public string OutLibText => string.IsNullOrEmpty(OutLibLabel) ? "—" : OutLibLabel;

    public Brush OutLibBrush => string.IsNullOrEmpty(OutLibLabel) ? Res("B.Faint") : Res("B.Warn");

    public Brush MatchedBrush => Matched
        ? Res("B.Ok")
        : Res("B.Warn");

    public string TypeText => string.IsNullOrEmpty(GroupType) ? "—" : GroupType;

    public Brush TypeBrush
    {
        get
        {
            var key = "Badge." + (string.IsNullOrEmpty(GroupTypeCode) ? "NONE" : GroupTypeCode);
            return Application.Current.TryFindResource(key) as Brush ?? Res("Badge.NONE");
        }
    }

    private static Brush Res(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;

    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>
    /// 换肤后重算颜色。上面的画刷属性是「取的那一刻」定死的，绑定不会自己重取，
    /// 必须显式发一次变更通知，否则深色主题下这几列还留着浅色主题的橙/绿。
    /// </summary>
    public void RefreshBrushes()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OutLibBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MatchedBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TypeBrush)));
    }
}

/// <summary>批量分组（对齐 web/index.html 的 #panelBatch）。</summary>
public partial class BatchGroupView : UserControl
{
    private GrouperService.BatchResult _last;

    public BatchGroupView()
    {
        InitializeComponent();
        BatchText.TextChanged += (_, _) => UpdateHint();
        UpdateHint();
    }

    private void UpdateHint()
        => PasteHint.Visibility = string.IsNullOrEmpty(BatchText.Text) ? Visibility.Visible : Visibility.Collapsed;

    // ==================================================================
    // 上传 Excel
    // ==================================================================

    private void OnUpload(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择病案 Excel",
            Filter = "表格文件|*.xlsx;*.xls;*.csv|所有文件|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        SetError(null);
        try
        {
            var bytes = File.ReadAllBytes(dlg.FileName);
            var rows = GrouperService.ReadSheetRows(bytes);
            var useMapping = UseMapping.IsChecked == true && App.State.Mapping.Rows.Count > 0;
            ShowResult(GrouperService.Batch(App.State, rows, useMapping), Path.GetFileName(dlg.FileName));
        }
        catch (Exception ex)
        {
            SetError("文件解析失败：" + ex.Message);
        }
    }

    // ==================================================================
    // 粘贴表格
    // ==================================================================

    private void OnBatchText(object sender, RoutedEventArgs e)
    {
        var text = (BatchText.Text ?? "").Trim();
        if (text.Length == 0)
        {
            MessageBox.Show("请先粘贴病案数据", "DIP 3.0 分组器", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SetError(null);
        try
        {
            var rows = ParseText(text);
            var useMapping = UseMapping.IsChecked == true && App.State.Mapping.Rows.Count > 0;
            ShowResult(GrouperService.Batch(App.State, rows, useMapping), $"粘贴表格 · {rows.Count} 行");
        }
        catch (Exception ex)
        {
            SetError("文本解析失败：" + ex.Message);
        }
    }

    /// <summary>支持两种格式：每行一个 JSON；或首行列名的 TSV（可直接从 Excel 复制）。</summary>
    private static List<Dictionary<string, JsonElement>> ParseText(string text)
    {
        var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
        if (lines.Count == 0) throw new InvalidOperationException("没有有效内容");

        if (lines[0].StartsWith("{"))
        {
            var list = new List<Dictionary<string, JsonElement>>();
            foreach (var line in lines)
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("JSON 行必须是对象");
                list.Add(doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()));
            }
            return list;
        }

        var headers = lines[0].Split('\t').Select(h => h.Trim()).ToList();
        var rows = new List<Dictionary<string, object>>();
        foreach (var line in lines.Skip(1))
        {
            var cells = line.Split('\t');
            var row = new Dictionary<string, object>();
            for (var i = 0; i < headers.Count; i++)
                row[headers[i]] = i < cells.Length ? cells[i].Trim() : "";
            rows.Add(row);
        }
        return rows.Select(GrouperService.ToJsonDict).ToList();
    }

    // ==================================================================
    // 模板 / 结果导出
    // ==================================================================

    private void OnDownloadTemplate(object sender, RoutedEventArgs e)
        => Save(GrouperService.BuildBatchTemplate(), "dip3-batch-template.xlsx", "保存批量分组模板");

    private void OnSaveResult(object sender, RoutedEventArgs e)
    {
        if (_last?.Xlsx == null) return;
        Save(_last.Xlsx, "dip3-result.xlsx", "保存分组结果");
    }

    private static void Save(byte[] bytes, string defaultName, string title)
    {
        var dlg = new SaveFileDialog
        {
            Title = title,
            FileName = defaultName,
            Filter = "Excel 工作簿|*.xlsx",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllBytes(dlg.FileName, bytes);
            MessageBox.Show($"已保存：\n{dlg.FileName}", "DIP 3.0 分组器",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存失败：" + ex.Message, "DIP 3.0 分组器",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ==================================================================
    // 展示
    // ==================================================================

    /// <summary>换肤后让现有结果行重新取色（不重跑分组、不重置滚动位置）。</summary>
    public void RepaintForTheme()
    {
        if (Grid.ItemsSource is not IEnumerable<BatchRowVm> rows) return;
        foreach (var r in rows) r.RefreshBrushes();
    }

    private void ShowResult(GrouperService.BatchResult result, string source)    {
        _last = result;

        var rows = result.Rows.Select(r => new BatchRowVm
        {
            Seq = r.Seq,
            CaseId = r.CaseId,
            MainDiag = r.MainDiag,
            MainProc = r.MainProc,
            Matched = r.Matched,
            GroupType = r.GroupType,
            GroupTypeCode = r.GroupTypeCode,
            DipSeq = r.DipSeq?.ToString() ?? "",
            NameOrReason = r.Matched ? r.Name : r.Reason,
            OutLibLabel = r.OutLib == null ? "" : r.OutLib.Label,
            OutLibTip = r.OutLib == null
                ? ""
                : $"{r.OutLib.Combo}\n2027 年起不作核心病种（省局反馈清单 #{r.OutLib.Seq}）"
                  + (string.IsNullOrEmpty(r.OutLib.Suggest) ? "" : $"\n可入组组合：{r.OutLib.Suggest}"),
        }).ToList();

        Grid.ItemsSource = rows;
        Grid.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnSaveResult.Visibility = Visibility.Visible;
        TxtSource.Text = source;
        SetError(null);
        BuildSummary(result.Summary);
    }

    private void BuildSummary(GrouperService.BatchSummary s)
    {
        SummaryPanel.Children.Clear();

        // colorKey 传资源键而非画刷对象：摘要条每次分组都会重建，但绑定动态资源后
        // 即便重建前切了主题，颜色也一定是当前主题的。
        void Add(string head, string text, string colorKey)
        {
            if (SummaryPanel.Children.Count > 0)
            {
                SummaryPanel.Children.Add(new Border
                {
                    Width = 1,
                    Height = 14,
                    Margin = new Thickness(14, 0, 14, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                }.ThBg("B.Border"));
            }
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock
            {
                Text = head,
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
            }.ThFg("B.Muted"));
            sp.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 12.5,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            }.ThFg(colorKey));
            SummaryPanel.Children.Add(sp);
        }

        Add("共", s.Total.ToString(), "B.Text");
        Add("入组", s.Matched.ToString(), "B.Ok");
        Add("未入组", (s.Total - s.Matched).ToString(), "B.Warn");
        // 只有真命中才占位：绝大多数批量没有这条清单的病例，常驻一个「0」会让人以为哪出错了
        if (s.OutLibCases > 0) Add("目录库外", s.OutLibCases.ToString(), "B.Warn");

        if (s.ByType.Count > 0)
        {
            var text = string.Join("｜", s.ByType.Select(kv => $"{kv.Key} {kv.Value}"));
            Add("", text, "B.Muted");
        }
        SummaryBar.Visibility = Visibility.Visible;
    }

    private void SetError(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            BatchErr.Visibility = Visibility.Collapsed;
            BatchErr.Text = "";
            return;
        }
        BatchErr.Text = message;
        BatchErr.Visibility = Visibility.Visible;
    }

    // ==================================================================
    // 诊断用（离屏截图 / 自检）
    // ==================================================================

    /// <summary>直接跑一批 TSV 文本（与「粘贴表格分组」同一条路径，只是不弹框）。</summary>
    internal void DiagBatch(string tsv)
    {
        var rows = ParseText(tsv ?? "");
        ShowResult(GrouperService.Batch(App.State, rows, UseMapping.IsChecked == true), "离屏样例");
    }

    /// <summary>结果表当前行（自检断言用）</summary>
    internal IEnumerable<BatchRowVm> RowsForTest => Grid.Items.Cast<BatchRowVm>();

    /// <summary>摘要条文案（自检断言用）</summary>
    internal string SummaryText => string.Join(" ",
        SummaryPanel.Children.OfType<StackPanel>()
            .Select(sp => string.Concat(sp.Children.OfType<TextBlock>().Select(t => t.Text))));
}
