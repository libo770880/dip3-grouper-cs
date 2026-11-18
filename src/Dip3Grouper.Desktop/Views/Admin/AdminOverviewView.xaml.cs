using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dip3Grouper;
using Dip3GrouperApp.Desktop.Theming;

namespace Dip3GrouperApp.Desktop.Views.Admin;

/// <summary>后台概览：病种库 / 映射表 / 综合病组 / 立项指南 / 德州物价 / 技术规范 的数据规模与导入来源。</summary>
public partial class AdminOverviewView : UserControl
{
    private bool _loaded;

    /// <summary>
    /// 病种库内置综合病组条数 —— 结核表里的区间行（分组第 2 层），
    /// 与 data/zonghe.json 的追加兜底层（第 6 层）分开统计。
    /// </summary>
    private static int BuiltInCount()
    {
        try
        {
            return (App.State.Config?.FzJiehe ?? new List<JieheRow>())
                .Count(r => (r.DiagCodes ?? new List<string>()).Any(d => (d ?? "").Contains('-')));
        }
        catch
        {
            return 0;
        }
    }

    public AdminOverviewView()
    {
        InitializeComponent();
    }

    /// <summary>首次进入该页时加载（由 AdminWindow 调用）</summary>
    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        Refresh();
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    private void OnReloadConfig(object sender, RoutedEventArgs e)
    {
        try
        {
            App.State.ReloadConfig();
            App.State.Mapping.Load();
            App.State.Zonghe.Load();
            App.State.Lixiang.Load();
            Refresh();
            TxtStatus.Text = "配置与数据已重新载入";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "重载失败：" + ex.Message;
        }
    }

    private void OnOpenDataDir(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.Root) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "打开失败：" + ex.Message;
        }
    }

    // ==================================================================
    // 统计
    // ==================================================================

    private void Refresh()
    {
        try
        {
            // 只显示数据目录名（生产机上就是安装目录），完整路径放悬停提示：
            // 总览页是给科室看的，直接铺一串 C://Users//... 既晦涩又容易被截图外传。
            var rootName = Path.GetFileName(AppPaths.Root.TrimEnd(Path.DirectorySeparatorChar));
            TxtRoot.Text = "数据目录：" + (rootName.Length > 0 ? rootName : AppPaths.Root);
            TxtRoot.ToolTip = AppPaths.Root;

            var meta = MappingStore.LoadConfigMeta();
            var map = App.State.Mapping.Stats();
            var zh = App.State.Zonghe.Stats();
            var sections = Diseases.SectionCounts(App.State.Config);
            var lxCounts = App.State.Lixiang.SectionCounts();
            var issues = App.State.Lixiang.JieduIssues();

            var configTotal = sections.Values.Sum(s => s.Total);
            var lxTotal = lxCounts.Values.Sum(s => s.Total);
            var jdTotal = issues.Sum(i => i.Total);

            CardHost.Children.Clear();
            CardHost.Children.Add(Card("病种库", sections.Count == 0 ? "尚未导入" : sections.Count + " 个区段", new[]
            {
                ("总条数", configTotal.ToString("N0")),
                ("导入时间", ShortTime(meta.ImportedAt)),
                ("数据来源", meta.SourceName ?? "—"),
                ("配置文件", "config/dip3-config.json"),
            }));
            CardHost.Children.Add(Card("映射表（ICD-10 → 医保）", CountText(map.Total), new[]
            {
                ("已映射", map.Mapped.ToString("N0")),
                ("灰码", map.Gray.ToString("N0")),
                ("导入时间", ShortTime(map.ImportedAt)),
                ("数据来源", map.SourceName ?? "—"),
            }));
            // 综合病组两层：病种库内置（结核区间行，分组第 2 层）+ data/zonghe.json 追加兜底层（第 6 层）
            var zhBuiltIn = BuiltInCount();
            CardHost.Children.Add(Card("综合病组", CountText(zhBuiltIn + zh.Total), new[]
            {
                ("病种库内置", zhBuiltIn.ToString("N0") + " 条（结核区间行）"),
                ("追加兜底层", zh.Total.ToString("N0") + " 条"),
                ("多范围病组", zh.MultiRange.ToString("N0")),
                ("兜底层导入时间", ShortTime(zh.ImportedAt)),
                ("兜底层数据来源", zh.SourceName ?? "—"),
                ("兜底层数据文件", "data/zonghe.json"),
            }));
            CardHost.Children.Add(Card("立项指南", lxTotal == 0 ? "尚未导入" : lxTotal.ToString("N0") + " 个项目", new[]
            {
                ("覆盖区段", lxCounts.Count + " 个"),
                ("解读辅导", issues.Count + " 期 · " + jdTotal.ToString("N0") + " 条"),
                ("导入时间", ShortTime(App.State.Lixiang.State.Meta?.ImportedAt)),
                ("数据文件", "data/lixiang.json"),
            }));

            var dzStat = App.State.Dezhou.Stat();
            CardHost.Children.Add(Card("德州物价", CountText(dzStat.Total), new[]
            {
                ("项目构成", $"主项 {dzStat.Main:N0} · 加收 {dzStat.Add:N0} · 扩展 {dzStat.Ext:N0}"),
                ("医保支付", dzStat.Insured.ToString("N0") + " 条"),
                ("覆盖批次", dzStat.BatchCount + " 批 · " + dzStat.CategoryCount + " 类"),
                ("导入时间", ShortTime(App.State.Dezhou.Meta.ImportedAt)),
                ("数据来源", string.IsNullOrWhiteSpace(App.State.Dezhou.Meta.Source) ? "—" : App.State.Dezhou.Meta.Source),
                ("数据文件", "data/dezhou-price.json"),
            }));

            var gfStat = App.State.Guifan.Stat();
            CardHost.Children.Add(Card("技术规范", gfStat.Total == 0 ? "尚未导入" : gfStat.Total.ToString("N0") + " 个项目", new[]
            {
                ("章节结构", gfStat.ChapterCount + " 章 / " + gfStat.CategoryCount + " 类"),
                ("含耗材项目", gfStat.WithMaterial.ToString("N0")),
                ("导入时间", ShortTime(App.State.Guifan.Meta.ImportedAt)),
                ("数据来源", string.IsNullOrWhiteSpace(App.State.Guifan.Meta.Source) ? "2023 年版规范 PDF" : App.State.Guifan.Meta.Source),
                ("数据文件", "data/guifan.json"),
            }));

            SectionChips.ItemsSource = sections.Select(kv => new ChipVm
            {
                Label = kv.Value.Label ?? kv.Key,
                Total = kv.Value.Total.ToString("N0"),
            }).ToList();

            LixiangChips.ItemsSource = lxCounts
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new ChipVm
                {
                    Label = kv.Value.Label ?? kv.Key,
                    Total = kv.Value.Total.ToString("N0"),
                }).ToList();

            TxtStatus.Text = "";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "统计加载失败：" + ex.Message;
        }
    }

    private sealed class ChipVm
    {
        public string Label { get; set; }
        public string Total { get; set; }
    }

    /// <summary>ISO 时间 → 本地 yyyy-MM-dd HH:mm</summary>
    /// <summary>0 条不要显示成「0 条」——那看起来像加载失败，明确写「尚未导入」</summary>
    private static string CountText(int n) => n == 0 ? "尚未导入" : n.ToString("N0") + " 条";

    private static string ShortTime(string iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "—";
        return DateTimeOffset.TryParse(iso, out var t)
            ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : iso;
    }

    /// <summary>生成一张统计卡：大数值 + 键值对明细（三列布局下的紧凑尺寸）。</summary>
    private static Border Card(string title, string headline, (string Key, string Val)[] rows)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
        }.ThFg("B.Muted"));
        stack.Children.Add(new TextBlock
        {
            Text = headline,
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 1, 0, 8),
        }.ThFg("B.Text"));
        foreach (var (k, v) in rows)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var key = new TextBlock
            {
                Text = k,
                FontSize = 11,
            }.ThFg("B.Muted");
            var val = new TextBlock
            {
                Text = v,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
            }.ThFg("B.Text");
            ToolTipService.SetToolTip(val, v);
            Grid.SetColumn(val, 1);
            g.Children.Add(key);
            g.Children.Add(val);
            stack.Children.Add(g);
        }

        return new Border
        {
            Style = (Style)Application.Current.FindResource("Card"),
            Margin = new Thickness(0, 0, 10, 10),
            Padding = new Thickness(14, 11, 14, 12),
            Child = stack,
        };
    }
}
