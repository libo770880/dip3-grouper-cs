using System.Collections;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Dip3Grouper;
using Dip3GrouperApp.Desktop.Controls;
using GrouperEngine = global::Dip3Grouper.Dip3Grouper;
using Dip3GrouperApp.Desktop.Theming;

namespace Dip3GrouperApp.Desktop.Views;

/// <summary>单条分组（对齐 web/index.html 的 #panelSingle）。</summary>
public partial class SingleGroupView : UserControl
{
    /// <summary>detail 字段中文标签（对齐网页版 FIELD_ZH）</summary>
    private static readonly Dictionary<string, string> FieldZh = new()
    {
        ["mainDiag"] = "主要诊断",
        ["mainProc"] = "主要手术操作",
        ["conservative"] = "保守治疗（无手术）",
        ["relatedRequired"] = "需相关手术",
        ["rule"] = "分组规则",
        ["procAlts"] = "手术编码组合",
        ["subType"] = "病种类别",
        ["degree"] = "烧伤程度",
        ["area"] = "烧伤面积",
        ["otherDiagCodes"] = "其他诊断编码",
        ["diagCodes"] = "诊断编码范围",
        ["drugResistant"] = "耐药菌株",
        ["diagExcluded"] = "命中诊断排除",
        ["excludedProc"] = "命中手术排除",
        ["ageDays"] = "日龄",
        ["birthWeight"] = "出生体重（克）",
        ["birthWeightNote"] = "体重分档",
        ["category"] = "主诊断类目",
        ["range"] = "覆盖范围",
        ["viaExcluded"] = "命中排除表",
    };

    public SingleGroupView()
    {
        InitializeComponent();

        // 联动建议依赖主诊断当前值
        DiagOther.MainDiagProvider = () => DiagMain.Text;
        ProcMain.MainDiagProvider = () => DiagMain.Text;
        ProcOther.MainDiagProvider = () => DiagMain.Text;

        // 选中带医保编码的映射项后自动启用映射转换（对齐 acPick）
        foreach (var box in new[] { DiagMain, DiagOther, ProcMain, ProcOther })
        {
            box.Picked += row =>
            {
                if (!row.Gray && !string.IsNullOrEmpty(row.InsCode)) UseMapping.IsChecked = true;
            };
        }

        AgeUnit.ItemsSource = new[] { "岁", "月", "天" };
        AgeUnit.SelectedIndex = 0;

        RenderEmpty();
    }

    // ==================================================================
    // 分组
    // ==================================================================

    private void OnEnterGroup(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        DoGroup();
    }

    private void OnGroup(object sender, RoutedEventArgs e) => DoGroup();

    /// <summary>
    /// 「填入示例」：给第一次用的人一条能真正命中的病案（A09.901 未特指病因的胃肠炎 +
    /// 44.1300x001 胃镜检查），填完直接分组 —— 空态里只写「请填写」用户不知道填什么格式。
    /// </summary>
    private void OnFillSample(object sender, RoutedEventArgs e)
    {
        DiagMain.Text = "A09.901";
        ProcMain.Text = "44.1300x001";
        DiagOther.Text = "";
        ProcOther.Text = "";
        AgeBox.Text = "45";
        BirthWeight.Text = "";
        UseMapping.IsChecked = false;
        DoGroup();
    }

    /// <summary>一键清空：表单全部输入复位 + 结果区回到空态（方便连续录入下一条病案）。</summary>
    private void OnClear(object sender, RoutedEventArgs e)
    {
        DiagMain.Text = "";
        DiagOther.Text = "";
        ProcMain.Text = "";
        ProcOther.Text = "";
        AgeBox.Text = "";
        BirthWeight.Text = "";
        AgeUnit.SelectedIndex = 0;
        UseMapping.IsChecked = false;
        RenderEmpty();
        DiagMain.Focus();
    }

    /// <summary>
    /// 诊断专用（<c>--shot-page sg:主诊断[:主手术[:其他诊断]]</c>）：填入病案并立即分组，
    /// 用于离屏核对结果卡与「分组溯源」区块（无需人工输入）。
    /// </summary>
    internal void DiagGroup(string mainDiag, string mainProc = null, string otherDiags = null)
    {
        DiagMain.Text = mainDiag ?? "";
        ProcMain.Text = mainProc ?? "";
        DiagOther.Text = otherDiags ?? "";
        DoGroup();
    }

    private void DoGroup()
    {
        var mainDiag = (DiagMain.Text ?? "").Trim();
        var mainProc = (ProcMain.Text ?? "").Trim();
        if (mainDiag.Length == 0 && mainProc.Length == 0)
        {
            MessageBox.Show("请至少填写主要诊断编码或主要手术操作编码", "DIP 3.0 分组器",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var payload = new Dictionary<string, object>();
        if (mainDiag.Length > 0) payload["mainDiag"] = mainDiag;
        var od = (DiagOther.Text ?? "").Trim();
        if (od.Length > 0) payload["otherDiags"] = od;
        if (mainProc.Length > 0) payload["mainProc"] = mainProc;
        var op = (ProcOther.Text ?? "").Trim();
        if (op.Length > 0) payload["otherProcs"] = op;
        var age = (AgeBox.Text ?? "").Trim();
        if (age.Length > 0)
        {
            payload["age"] = age;
            payload["ageUnit"] = AgeUnit.SelectedItem as string ?? "岁";
        }
        var bw = (BirthWeight.Text ?? "").Trim();
        if (bw.Length > 0) payload["birthWeight"] = bw;

        var wasEnabled = BtnGroup.IsEnabled;
        BtnGroup.IsEnabled = false;
        BtnGroup.Content = "分组中…";
        try
        {
            var kase = GrouperEngine.ReadCase(GrouperService.ToJsonDict(payload));
            var result = GrouperService.Group(App.State, kase, UseMapping.IsChecked == true, withTrace: true);
            ResultHost.Content = BuildResult(result);
        }
        catch (Exception ex)
        {
            ResultHost.Content = BuildError(ex.Message);
        }
        finally
        {
            BtnGroup.IsEnabled = wasEnabled;
            BtnGroup.Content = "分 组";
        }
    }

    // ==================================================================
    // 结果渲染
    // ==================================================================

    private void RenderEmpty()
    {
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        // 结果卡现在占满剩余高度：把空态提示在视口内垂直居中
        if (ResultHost.Parent is ScrollViewer sv)
            sp.SetBinding(HeightProperty, new System.Windows.Data.Binding("ViewportHeight") { Source = sv });
        sp.Children.Add(new TextBlock
        {
            Text = "📋",
            FontSize = 34,
            Opacity = 0.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12),
        });
        sp.Children.Add(new TextBlock
        {
            Text = "填写左侧信息后点击「分 组」，或点「填入示例」先看一条结果",
            FontSize = 13.5,
            HorizontalAlignment = HorizontalAlignment.Center,
        }.ThFg("B.Muted"));
        ResultHost.Content = sp;
    }

    private UIElement BuildError(string message)
    {
        var sp = new StackPanel();
        sp.Children.Add(Badge("NONE", "请求错误"));
        sp.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        }.ThFg("B.Warn"));
        return sp;
    }

    private UIElement BuildResult(GroupResult r)
    {
        var sp = new StackPanel();

        if (!r.Matched)
        {
            sp.Children.Add(Badge("NONE", "未入组"));
            sp.Children.Add(new TextBlock
            {
                Text = r.Reason ?? "",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0),
            });
            var outLibMiss = BuildOutLib(r);
            if (outLibMiss != null) sp.Children.Add(outLibMiss);
            var missTrace = BuildTrace(r);
            if (missTrace != null) sp.Children.Add(missTrace);
            sp.Children.Add(BuildDebug(r));
            return sp;
        }

        // 头部：类型徽章 + 病种序号
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Badge(r.GroupTypeCode, r.GroupType ?? ""));
        head.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = $"病种序号 #{r.Seq}",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
            }.ThFg("B.Accent"),
        }.ThBg("B.AccentSoft"));
        sp.Children.Add(head);

        sp.Children.Add(new TextBlock
        {
            Text = r.Name ?? "",
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        });

        sp.Children.Add(BuildKv(r));
        var outLib = BuildOutLib(r);
        if (outLib != null) sp.Children.Add(outLib);
        var chain = BuildChain(r);
        if (chain != null) sp.Children.Add(chain);
        var trace = BuildTrace(r);
        if (trace != null) sp.Children.Add(trace);
        sp.Children.Add(BuildDebug(r));
        return sp;
    }

    // ==================================================================
    // 目录库外病种预警（省局反馈清单）
    // ==================================================================

    /// <summary>
    /// 命中「目录库外病种」时的预警卡：说明这个组合为什么不能用，并给出同诊断在目录库内
    /// 的可入组组合。放在病种名下方 / 未入组原因下方 —— 位置固定，用户不必去翻回溯原因。
    /// </summary>
    private UIElement BuildOutLib(GroupResult r)
    {
        var hit = r.OutLib;
        if (hit == null) return null;

        var box = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 12, 0, 0),
            Child = new StackPanel(),
        }.ThBg("B.WarnBg").ThBorder("B.Warn");
        var sp = (StackPanel)box.Child;

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock
        {
            Text = "⚠ 目录库外病种",
            FontSize = 13,
            FontWeight = FontWeights.Bold,
        }.ThFg("B.WarnText"));
        head.Children.Add(new TextBlock
        {
            Text = "省局反馈清单 · 2027 年起不作 DIP 核心病种",
            FontSize = 11.5,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        }.ThFg("B.WarnText"));
        sp.Children.Add(head);

        sp.Children.Add(new TextBlock
        {
            Text = hit.Combo,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 7, 0, 0),
        }.ThFg("B.WarnText"));

        // 原因写在卡片里而不是读 Note：Note 是病种库表格用的短备注（列宽 280px 会被截断），
        // 卡片有整行宽度，把「为什么不能用、后果是什么」一次说清。
        sp.Children.Add(new TextBlock
        {
            Text = "省局反馈：全省结算量低于 120 例，2027 年起不再作为 DIP 核心病种。"
                 + "首页仍按该组合填报的病例只能入综合病组，影响结算与考核。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Opacity = 0.9,
        }.ThFg("B.WarnText"));

        var items = (hit.Suggest ?? "").Split('；')
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (items.Count > 0)
        {
            var tip = new TextBlock
            {
                Text = "同诊断在目录库内的可入组组合（核对首页填报用，非改码依据）",
                FontSize = 11.5,
                Margin = new Thickness(0, 9, 0, 0),
                Opacity = 0.85,
            }.ThFg("B.WarnText");
            sp.Children.Add(tip);
            foreach (var s in items)
                sp.Children.Add(new TextBlock
                {
                    Text = "· " + s,
                    FontSize = 12.5,
                    FontFamily = (FontFamily)FindResource("Font.Mono"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0),
                });
        }
        else
        {
            sp.Children.Add(new TextBlock
            {
                Text = "目录库内没有同诊断的核心病种，该病例只能走综合病组 —— 请先核对主要诊断编码本身。",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 7, 0, 0),
            }.ThFg("B.WarnText"));
        }

        sp.Children.Add(new TextBlock
        {
            Text = $"清单序号 #{hit.Seq} · 键 {hit.Label}",
            FontSize = 11,
            Margin = new Thickness(0, 8, 0, 0),
            Opacity = 0.75,
        }.ThFg("B.WarnText"));
        return box;
    }

    // ==================================================================
    // 分组溯源（逐层判定过程）
    // ==================================================================

    /// <summary>
    /// 「分组溯源」区块：把分组器逐层的判定过程画出来，回答「为什么分到这个病种 / 为什么没入组」。
    /// 层顺序即优先级顺序（排除判定 → 先期 → 诊断辅助细分 → 并项 → 基础 → 基层 → 综合病组）。
    /// </summary>
    private UIElement BuildTrace(GroupResult r)
    {
        var tr = r.Trace;
        if (tr == null || tr.Steps.Count == 0) return null;

        var box = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 14, 0, 0),
            Child = new StackPanel(),
        }.ThBg("B.SoftBg").ThBorder("B.SoftBorder");
        var host = (StackPanel)box.Child;

        var hit = tr.Steps.FirstOrDefault(s => s.Status == "hit");
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 9, 12, 4) };
        head.Children.Add(new TextBlock { Text = "分组溯源", FontSize = 13, FontWeight = FontWeights.SemiBold });
        head.Children.Add(new TextBlock
        {
            Text = hit != null
                ? $"按优先级共 {tr.Steps.Count} 层 · 命中于「{hit.Layer}」第 {hit.Seq} 行"
                : $"按优先级共 {tr.Steps.Count} 层 · 全部未命中",
            FontSize = 12,
            Margin = new Thickness(9, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        }.ThFg("B.Muted"));
        host.Children.Add(head);

        foreach (var s in tr.Steps) host.Children.Add(TraceStepRow(s));
        return box;
    }

    private UIElement TraceStepRow(GroupTraceStep s)
    {
        var (label, toneKey) = s.Status switch
        {
            "hit" => ("命中", "B.Accent"),
            "warn" => ("影响", "B.Warn"),
            "skip" => ("跳过", "B.Faint"),
            "ok" => ("无影响", "B.Muted"),
            _ => ("未命中", "B.Muted"),
        };
        // 命中行的左侧色条用浅色强调，排除命中用警示色，其余用中性分隔色
        var barKey = s.Status switch
        {
            "hit" => "B.Accent",
            "warn" => "B.Warn",
            _ => "B.BorderStrong",
        };

        var body = new StackPanel { Margin = new Thickness(10, 0, 12, 0) };

        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 1) };
        head.Children.Add(new TextBlock
        {
            Text = s.Layer,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
        }.ThFg(s.Status is "hit" or "warn" ? "B.Text" : "B.Muted"));
        head.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(7, 1, 7, 1),
            Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = label, FontSize = 11.5, FontWeight = FontWeights.SemiBold }.ThFg(toneKey),
        }.ThBg(s.Status switch { "hit" => "B.AccentSoft", "warn" => "B.WarnBg", _ => "B.Bg" }).ThBorder("B.Border"));
        if (s.RuleCount > 0)
            head.Children.Add(new TextBlock
            {
                Text = $"{s.RuleCount} 条规则",
                FontSize = 11.5,
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            }.ThFg("B.Faint"));
        body.Children.Add(head);

        if (!string.IsNullOrEmpty(s.Title))
            body.Children.Add(new TextBlock
            {
                Text = s.Title,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 0),
            }.ThFg(s.Status switch { "hit" => "B.Accent", "warn" => "B.Warn", _ => "B.Muted" }));

        foreach (var f in s.Facts) body.Children.Add(TraceFactRow(f));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var bar = new Border { CornerRadius = new CornerRadius(3, 0, 0, 3) }.ThBg(barKey);
        Grid.SetColumn(bar, 0);
        Grid.SetColumn(body, 1);
        grid.Children.Add(bar);
        grid.Children.Add(body);

        return new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 0, 0, 3),
            Child = grid,
        }.ThBorder("B.Border");
    }

    private UIElement TraceFactRow(GroupTraceFact f)
    {
        var g = new Grid { Margin = new Thickness(0, 1, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var lab = new TextBlock
        {
            Text = f.Label,
            FontSize = 11.5,
            Padding = new Thickness(0, 2, 8, 2),
            TextWrapping = TextWrapping.Wrap,
        }.ThFg("B.Faint");
        var val = new TextBlock
        {
            Text = f.Value,
            FontSize = 12,
            Padding = new Thickness(0, 2, 0, 2),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = (FontFamily)FindResource("Font.Mono"),
        }.ThFg(f.Tone == "warn" ? "B.Warn" : f.Tone == "ok" ? "B.Ok" : "B.Text");
        Grid.SetColumn(val, 1);
        g.Children.Add(lab);
        g.Children.Add(val);
        return g;
    }

    /// <summary>明细键值网格（左标签 118px + 右值，虚线分隔）</summary>
    private UIElement BuildKv(GroupResult r)
    {
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var row = 0;
        foreach (var (k, v) in r.Detail ?? new Dictionary<string, object>())
        {
            if (k == "procName" || k == "dipDiagCode") continue;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = FieldZh.TryGetValue(k, out var zh) ? zh : k,
                FontSize = 13,
                Padding = new Thickness(0, 6, 0, 6),
                VerticalAlignment = VerticalAlignment.Top,
            }.ThFg("B.Muted");

            var value = new TextBlock
            {
                Text = FmtVal(v),
                FontFamily = (FontFamily)FindResource("Font.Mono"),
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(0, 6, 0, 6),
            };

            var lb = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = label }.ThBorder("B.Border");
            var vb = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = value }.ThBorder("B.Border");
            Grid.SetRow(lb, row); Grid.SetColumn(lb, 0);
            Grid.SetRow(vb, row); Grid.SetColumn(vb, 1);
            grid.Children.Add(lb);
            grid.Children.Add(vb);
            row++;
        }
        return grid;
    }

    /// <summary>主诊断映射链（启用映射时三段链，否则两段链）</summary>
    private UIElement BuildChain(GroupResult r)
    {
        var detail = r.Detail ?? new Dictionary<string, object>();
        detail.TryGetValue("mainDiag", out var mdRaw);
        detail.TryGetValue("dipDiagCode", out var dipCode);
        var md = mdRaw as string ?? "";

        if (r.Translate != null)
        {
            var t = r.Translate.TryGetValue("mainDiag", out var tv) && tv is Dictionary<string, object> td
                ? td : new Dictionary<string, object>();
            var others = r.Translate.TryGetValue("otherDiags", out var ov) && ov is IEnumerable oe
                ? oe.Cast<object>().ToList() : new List<object>();

            string Str(IDictionary<string, object> d, string key)
                => d != null && d.TryGetValue(key, out var v) ? v as string ?? "" : "";

            var from = Str(t, "from");
            var to = Str(t, "to");
            var grayMain = t.TryGetValue("gray", out var gv) && gv is bool gb && gb;
            var chain = $"{(!string.IsNullOrEmpty(from) ? from : md)} → {(!string.IsNullOrEmpty(to) ? to : (!string.IsNullOrEmpty(from) ? from : md))} → {(!string.IsNullOrEmpty(dipCode as string) ? dipCode : "—")}";

            var grayCount = (grayMain ? 1 : 0) + others.Count(x =>
                x is Dictionary<string, object> d && d.TryGetValue("gray", out var v2) && v2 is bool b2 && b2);

            var box = NewChainBox();
            var inner = (StackPanel)box.Child;
            inner.Children.Add(KvLine("主诊断映射", $"{(grayMain ? "⚠ 命中灰码，保留原码　" : "")}{chain}"));
            if (grayCount > 0)
                inner.Children.Add(KvLine("灰码提示", $"⚠ 其他诊断中有 {grayCount} 个灰码已保留原码"));
            return box;
        }

        if (!string.IsNullOrEmpty(dipCode as string))
        {
            var box = NewChainBox();
            ((StackPanel)box.Child).Children.Add(KvLine("主诊断映射", $"{md} → {dipCode}"));
            return box;
        }
        return null;
    }

    private Border NewChainBox() => new Border
    {
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(12, 9, 12, 9),
        Margin = new Thickness(0, 14, 0, 0),
        Child = new StackPanel(),
    }.ThBg("B.InfoBg").ThBorder("B.InfoBorder");

    private UIElement KvLine(string k, string v)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = new TextBlock { Text = k, FontSize = 12.5, Padding = new Thickness(0, 3, 0, 3) }.ThFg("B.Muted");
        var val = new TextBlock
        {
            Text = v,
            FontFamily = (FontFamily)FindResource("Font.Mono"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(0, 3, 0, 3),
        };
        Grid.SetColumn(val, 1);
        g.Children.Add(l);
        g.Children.Add(val);
        return g;
    }

    /// <summary>技术明细（调试用，JSON）</summary>
    private UIElement BuildDebug(GroupResult r)
    {
        var text = new TextBox
        {
            Text = JsonSerializer.Serialize(r, Js.Http),
            Style = (Style)FindResource("Tb.Multi"),
            IsReadOnly = true,
            FontFamily = (FontFamily)FindResource("Font.Mono"),
            FontSize = 11.5,
            MaxHeight = 260,
            Margin = new Thickness(0, 8, 0, 0),
        };
        return new Expander
        {
            Header = "技术明细（调试用）",
            FontSize = 12.5,
            Margin = new Thickness(0, 16, 0, 0),
            Content = text,
        }.ThFg("B.Muted");
    }

    private UIElement Badge(string code, string text)
    {
        var key = "Badge." + (string.IsNullOrEmpty(code) ? "NONE" : code);
        var brush = TryFindResource(key) as Brush ?? (Brush)FindResource("Badge.NONE");
        var badge = new Border
        {
            Background = brush,
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(12, 3, 12, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 6,
                ShadowDepth = 1,
                Direction = 270,
                Color = Colors.Black,
                Opacity = 0.28,
            },
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
            },
        };
        return badge;
    }

    private static string FmtVal(object v)
    {
        switch (v)
        {
            case null: return "";
            case bool b: return b ? "是" : "否";
            case string s: return s;
            case JsonElement je: return je.ValueKind == JsonValueKind.String ? je.GetString() ?? "" : je.ToString();
            case IEnumerable en: return string.Join("；", en.Cast<object>().Select(FmtVal));
            default: return v.ToString() ?? "";
        }
    }
}
