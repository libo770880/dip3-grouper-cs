using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Dip3GrouperApp.Desktop.Controls;
using Dip3GrouperApp.Desktop.Views;

namespace Dip3GrouperApp.Desktop;

/// <summary>
/// 表格行 / 详情卡 / 使用说明条的右键菜单。
///
/// 用附加属性而不是 XAML 里的 ContextMenu：条目要按「点中的是哪一行」动态生成
/// （收藏态、可复制的字段、该行的跨库关联各不相同），而共享资源字典里没有代码后置可挂事件。
/// 右键一律「先选中该行再弹菜单」，与资源管理器直觉一致。
/// </summary>
public static class RowMenu
{
    public static readonly DependencyProperty EnableProperty = DependencyProperty.RegisterAttached(
        "Enable", typeof(bool), typeof(RowMenu), new PropertyMetadata(false, OnEnableChanged));

    public static void SetEnable(DependencyObject o, bool v) => o.SetValue(EnableProperty, v);
    public static bool GetEnable(DependencyObject o) => (bool)o.GetValue(EnableProperty);

    private static void OnEnableChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not FrameworkElement fe) return;
        fe.PreviewMouseRightButtonUp -= OnRightUp;
        if (e.NewValue is true) fe.PreviewMouseRightButtonUp += OnRightUp;
    }

    private static void OnRightUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;

        object item = fe.DataContext;
        DataGridRow row = null;
        DataGridCell cell = null;
        if (fe is DataGrid g)
        {
            var src = e.OriginalSource as DependencyObject;
            row = ItemsControl.ContainerFromElement(g, src) as DataGridRow;
            if (row == null) return;              // 表头 / 空白区：不弹菜单，交回框架
            cell = Ancestor<DataGridCell>(src);
            item = row.Item;
            g.SelectedItem = item;                // 右键即选中
        }
        if (item == null) return;

        var menu = Build(item, fe, row ?? fe, cell);
        if (menu == null) return;

        menu.PlacementTarget = fe;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>从命中元素向上找最近的指定祖先（右键命中的是单元格里的 TextBlock，不是单元格本身）。</summary>
    private static T Ancestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (var cur = d; cur != null; cur = VisualTreeHelper.GetParent(cur))
            if (cur is T t) return t;
        return null;
    }

    // ==================================================================
    // 菜单构建
    // ==================================================================

    /// <summary>按行数据生成菜单；无可选项时返回 null（调用方不弹）。</summary>
    internal static ContextMenu Build(object item, FrameworkElement owner, FrameworkElement hitRoot,
        DataGridCell hitCell = null)
    {
        var menu = new ContextMenu();
        menu.Style = Application.Current?.TryFindResource("Ctx.Menu") as Style;

        void Sep() => menu.Items.Add(new Separator { Style = Application.Current?.TryFindResource("Ctx.Sep") as Style });

        void Add(string header, Action act)
        {
            var mi = new MenuItem { Header = header };
            var st = Application.Current?.TryFindResource("Ctx.Item") as Style;
            if (st != null) mi.Style = st;
            mi.Click += (_, _) => act();
            menu.Items.Add(mi);
        }

        var sel = SelectedText(hitRoot);
        if (!string.IsNullOrEmpty(sel)) Add("复制选中文字", () => Copy(sel, "已复制选中文字"));

        switch (item)
        {
            case DzRowVm v:
                Add(v.IsFav ? "移出常用收藏" : "加入常用收藏 ★", () =>
                {
                    var now = DezhouPriceView.ToggleFav(v);
                    Flash(now ? "已加入常用收藏" : "已移出常用收藏");
                });
                Sep();
                Add("复制项目编码", () => Copy(v.Code, "已复制项目编码"));
                Add("复制项目名称", () => Copy(v.Name, "已复制项目名称"));
                Add("复制本行（可直接粘进 Excel）", () => Copy(RowText(v), "已复制本行"));
                Add("复制全部详情", () => Copy(DetailText(v), "已复制全部详情"));
                break;

            case LxRowVm v:
                Add("复制项目名称", () => Copy(v.Name, "已复制项目名称"));
                Add("复制计价说明", () => Copy(v.Usage, "已复制计价说明"));
                Add("复制本行（可直接粘进 Excel）", () => Copy(RowText(v), "已复制本行"));
                Add("复制全部详情", () => Copy(DetailText(v), "已复制全部详情"));
                break;

            case GfRowVm v:
                Add("复制项目编码", () => Copy(v.Code, "已复制项目编码"));
                Add("复制项目名称", () => Copy(v.Name, "已复制项目名称"));
                Add("复制本行（可直接粘进 Excel）", () => Copy(RowText(v), "已复制本行"));
                Add("复制全部详情", () => Copy(DetailText(v), "已复制全部详情"));
                break;

            case JdRowVm v:
                Add("复制问题", () => Copy(v.Q, "已复制问题"));
                Add("复制答案", () => Copy(v.A, "已复制答案"));
                Add("复制问答（问题 + 答案）", () => Copy(DetailText(v), "已复制问答"));
                break;

            case RelatedDoc d:
                Add("复制名称", () => Copy(d.Name, "已复制名称"));
                Add("复制全部详情", () => Copy(DetailText(d), "已复制全部详情"));
                break;

            case UsageVm u:
                Add("复制本条使用说明", () => Copy(u.Text, "已复制使用说明"));
                Add("复制全部详情", () => Copy(DetailText(u), "已复制使用说明"));
                break;

            // 病种库查询：行是「列定义 + 扁平字典」，菜单按当前区段的列动态生成
            // （各区段字段完全不同：病种 / 先期 / 烧伤 / 结核…，写死字段必然对不上）
            case IDictionary<string, object> d:
                AddDiseaseRow(d, owner as DataGrid ?? hitRoot as DataGrid, hitCell, Add, Sep);
                break;
        }

        AddLinks(menu, item, owner, Add, Sep);
        return menu.Items.Count == 0 ? null : menu;
    }

    /// <summary>
    /// 病种库行的菜单。列顺序 / 列名从所属 DataGrid 反推 —— 这张表的列是按区段动态建的
    /// （病种 / 先期 / 烧伤 / 结核…字段完全不同），写死字段名必然和当前区段对不上。
    /// </summary>
    private static void AddDiseaseRow(IDictionary<string, object> row, DataGrid grid, DataGridCell hitCell,
        Action<string, Action> add, Action sep)
    {
        var cols = DiseaseCols(grid, row);

        string Val(string key) => row.TryGetValue(key, out var v) ? FlatGrid.Format(v) : "";

        // 右键落在哪个单元格就先给「复制该格」——不用先整行粘出去再在 Excel 里拆
        var hitKey = hitCell?.Column == null ? "" : ColumnKey(hitCell.Column);
        if (hitKey.Length > 0)
        {
            var cellVal = Val(hitKey);
            if (cellVal.Length > 0) add("复制该单元格内容", () => Copy(cellVal, "已复制单元格"));
        }

        add("复制本行（可直接粘进 Excel）",
            () => Copy(DiseaseRowText(row, grid, withHeader: false), "已复制本行"));
        add("复制本行（带列名）",
            () => Copy(DiseaseRowText(row, grid, withHeader: true), "已复制本行（含列名）"));

        // 编码 / 名称 / 序号类字段各给一项：核对病种时被单独拿去比对的就是这几个
        var picked = cols.Where(c => IsKeyLike(c.Key, c.Label)).Take(4).ToList();
        if (picked.Count > 0) sep();
        foreach (var (label, key) in picked)
        {
            var v = Val(key);
            if (v.Length == 0) continue;
            var l = label;
            add($"复制 · {l}", () => Copy(v, "已复制 " + l));
        }
    }

    /// <summary>病种库行的列（标签 + 字典键）：优先按所属 DataGrid 的列顺序。</summary>
    private static List<(string Label, string Key)> DiseaseCols(DataGrid grid, IDictionary<string, object> row)
    {
        var cols = new List<(string Label, string Key)>();
        if (grid != null)
        {
            foreach (var c in grid.Columns)
            {
                var key = ColumnKey(c);
                // 表头为了排版可能带换行（「手术编码」/「（空=保守行）」），
                // 复制到剪贴板 / Excel 时必须还原成单行，否则一个列名会占两行、列数全错位
                var label = (c.Header as string ?? key).Replace("\n", "");
                if (key.Length > 0) cols.Add((label, key));
            }
        }
        if (cols.Count == 0) cols.AddRange(row.Keys.Select(k => (k, k)));
        return cols;
    }

    /// <summary>
    /// 病种库整行文本（制表符分隔，与表格列序一致）。<paramref name="withHeader"/> = 第一行放列名，
    /// 贴进 Excel 后不用再回忆哪列是哪列。自检直接断言这个函数，免得为验证格式去动剪贴板。
    /// </summary>
    internal static string DiseaseRowText(IDictionary<string, object> row, DataGrid grid, bool withHeader)
    {
        var cols = DiseaseCols(grid, row);
        string Val(string key) => row.TryGetValue(key, out var v) ? FlatGrid.Format(v) : "";
        var body = string.Join("\t", cols.Select(c => Val(c.Key)));
        return withHeader ? string.Join("\t", cols.Select(c => c.Label)) + "\n" + body : body;
    }

    /// <summary>列绑定的字典键（FlatGrid 用索引器绑定 <c>[key]</c>）。</summary>
    private static string ColumnKey(DataGridColumn c)
    {
        if (c is DataGridTextColumn t && t.Binding is Binding b
            && b.Path?.Path is string p && p.Length > 2 && p[0] == '[' && p[^1] == ']')
            return p[1..^1];
        return c.SortMemberPath ?? "";
    }

    private static bool IsKeyLike(string key, string label)
    {
        var s = ((key ?? "") + " " + (label ?? "")).ToLowerInvariant();
        return s.Contains("code") || s.Contains("编码") || s.Contains("序号") || s.Contains("seq")
            || s.Contains("name") || s.Contains("名称") || s.Contains("诊断") || s.Contains("手术");
    }

    /// <summary>该行的跨库关联条目：一项一条菜单（避免子菜单模板的额外复杂度）。</summary>
    private static void AddLinks(ContextMenu menu, object item, FrameworkElement owner,
                                 Action<string, Action> add, Action sep)
    {
        var links = BlocksOf(item).SelectMany(b => b.Lines ?? new List<LxLine>())
            .Where(l => l.HasLink).Take(5).ToList();
        if (links.Count == 0) return;

        sep();
        foreach (var l in links)
        {
            var ln = l;
            add($"打开关联 · {Trim(ln.Text, 30)}",
                () => RelatedWindow.Open(Window.GetWindow(owner), ln.LinkLib, ln.LinkId));
        }
    }

    private static List<LxBlock> BlocksOf(object item) => item switch
    {
        DzRowVm v => v.Blocks,
        LxRowVm v => v.Blocks,
        GfRowVm v => v.Blocks,
        RelatedDoc d => d.Blocks,
        _ => new List<LxBlock>(),
    };

    // ==================================================================
    // 复制文本生成（自检直接断言这两个函数）
    // ==================================================================

    /// <summary>整行文本（制表符分隔，直接粘进 Excel 即一列一格）。</summary>
    internal static string RowText(object item) => item switch
    {
        DzRowVm v => string.Join("\t", new[]
        {
            v.Code, v.Name, v.Category, v.Unit,
            Nz(v.Price3), Nz(v.Price2), Nz(v.Price1), v.Insurance, v.SelfPay, v.Doc,
        }),
        LxRowVm v => string.Join("\t", new[]
        {
            v.Seq, v.Name, v.Category, v.Unit, v.AddItems, v.ExtItems, v.Usage,
        }),
        GfRowVm v => string.Join("\t", new[]
        {
            v.Code, v.Name, v.Category, v.Unit, v.Difficulty, v.Risk, v.HrValue, v.Bill,
        }),
        JdRowVm v => string.Join("\t", new[] { $"第{v.Issue}期", "第 " + v.No + " 题", v.Q }),
        _ => "",
    };

    /// <summary>详情全文（标题行 + 全部字段块，含类别使用说明与跨库关联）。</summary>
    internal static string DetailText(object item)
    {
        var sb = new StringBuilder();
        switch (item)
        {
            case DzRowVm v:
                sb.AppendLine($"项目编码：{v.Code}");
                sb.AppendLine($"项目名称：{v.Name}");
                sb.AppendLine($"类别：{v.Category}　计价单位：{v.Unit}");
                sb.AppendLine($"价格（三级 / 二级 / 一级）：{Nz(v.Price3)} / {Nz(v.Price2)} / {Nz(v.Price1)}");
                sb.AppendLine($"医保支付：{v.Insurance}　自付比例：{v.SelfPay}");
                sb.AppendLine($"落地文号：{v.Doc}");
                sb.Append(BlocksText(v.Blocks));
                break;

            case LxRowVm v:
                sb.AppendLine($"序号：{v.Seq}");
                sb.AppendLine($"项目名称：{v.Name}");
                sb.AppendLine($"项目类别：{v.Category}　计价单位：{v.Unit}");
                sb.Append(BlocksText(v.Blocks));
                break;

            case GfRowVm v:
                sb.AppendLine($"项目编码：{v.Code}");
                sb.AppendLine($"项目名称：{v.Name}");
                sb.AppendLine($"类别：{v.Category}　单位：{v.Unit}");
                sb.AppendLine($"技术难度：{v.Difficulty}　风险程度：{v.Risk}　人力消耗相对值：{v.HrValue}");
                sb.Append(BlocksText(v.Blocks));
                break;

            case JdRowVm v:
                sb.AppendLine($"第 {v.Issue} 期 · 第 {v.No} 题");
                sb.AppendLine(v.Q);
                sb.AppendLine();
                sb.AppendLine(v.A);
                break;

            case RelatedDoc d:
                sb.AppendLine(d.Name);
                if (!string.IsNullOrWhiteSpace(d.Sub)) sb.AppendLine(d.Sub);
                foreach (var m in d.Meters ?? new List<GfMeter>())
                    sb.AppendLine($"{m.Label}：{m.Text}");
                sb.Append(BlocksText(d.Blocks));
                break;

            case UsageVm u:
                return (u.Text ?? "").Trim();

            default:
                return "";
        }
        return sb.ToString().TrimEnd();
    }

    private static string BlocksText(List<LxBlock> blocks)
    {
        var sb = new StringBuilder();
        foreach (var b in blocks ?? new List<LxBlock>())
        {
            var vals = (b?.Lines ?? new List<LxLine>())
                .Select(l => (l.Text ?? "").Trim()).Where(t => t.Length > 0).ToList();
            if (vals.Count == 0) continue;
            sb.AppendLine();
            sb.AppendLine($"{b.Label}：{vals[0]}");
            for (var i = 1; i < vals.Count; i++) sb.AppendLine("　　" + vals[i]);
        }
        return sb.ToString();
    }

    private static string Nz(string v) => string.IsNullOrWhiteSpace(v) ? "—" : v;

    private static string Trim(string s, int n)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= n ? s : s[..n] + "…";
    }

    // ==================================================================
    // 剪贴板 / 轻提示
    // ==================================================================

    /// <summary>写剪贴板（剪贴板被其他进程占用时重试，避免偶发 CLIPBRD_E_CANT_OPEN）。</summary>
    internal static bool Copy(string text, string hint = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        for (var i = 0; i < 3; i++)
        {
            try
            {
                Clipboard.SetText(text);
                if (!string.IsNullOrEmpty(hint)) Flash(hint);
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[copy] 第 {i + 1} 次写剪贴板失败：{ex.Message}");
                System.Threading.Thread.Sleep(40);
            }
        }
        Flash("复制失败：剪贴板被占用");
        return false;
    }

    private static Popup _tip;

    /// <summary>鼠标位置的一行轻提示（复制 / 收藏后的即时反馈），1.1 秒后自动消失。</summary>
    internal static void Flash(string text)
    {
        // 自检 / 命令行模式没有主窗口，直接跳过（不弹任何界面）
        if (Application.Current?.MainWindow == null) return;
        try
        {
            if (_tip != null) _tip.IsOpen = false;

            var tb = new TextBlock
            {
                Text = text,
                FontSize = 12.5,
                FontFamily = Application.Current.TryFindResource("Font.UI") as FontFamily,
                Foreground = Application.Current.TryFindResource("B.White") as Brush ?? Brushes.White,
            };
            var bd = new Border
            {
                Child = tb,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 7, 12, 7),
                Background = Application.Current.TryFindResource("B.Sidebar") as Brush ?? Brushes.Black,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 14, ShadowDepth = 2, Direction = 270, Color = Colors.Black, Opacity = 0.28,
                },
            };
            var p = new Popup
            {
                Child = bd,
                Placement = PlacementMode.MousePoint,
                AllowsTransparency = true,
                StaysOpen = true,
                IsHitTestVisible = false,
            };
            p.IsOpen = true;
            _tip = p;

            var t = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1100),
            };
            t.Tick += (_, _) =>
            {
                t.Stop();
                try { p.IsOpen = false; } catch { /* 关闭失败无所谓 */ }
            };
            t.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[flash] " + ex.Message);
        }
    }

    /// <summary>命中区域内已被选中的文本（用于「复制选中文字」）。</summary>
    private static string SelectedText(DependencyObject root)
    {
        if (Keyboard.FocusedElement is TextBox ftb && ftb.SelectionLength > 0) return ftb.SelectedText;
        return FindSelection(root);
    }

    private static string FindSelection(DependencyObject d)
    {
        if (d is not Visual) return null;
        var n = VisualTreeHelper.GetChildrenCount(d);
        for (var i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is TextBox tb && tb.SelectionLength > 0) return tb.SelectedText;
            var r = FindSelection(c);
            if (r != null) return r;
        }
        return null;
    }
}
