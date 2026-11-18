using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Dip3Grouper;

namespace Dip3GrouperApp.Desktop;

/// <summary>
/// 「基本物耗指引」面板 —— 开单 / 收费前查一个耗材到底能不能另收：
/// ① 判定：边打边判，命中基本物耗即「不可另收」；
/// ② 不可收费：36 类指南基本物耗清单，按归类分组；
/// ③ 可收费：边界清单（可另收 / 不可另收 / 需核对）与依据；
/// ④ 口径依据：六条规则 + 36 类条款原文，逐字可核。
///
/// 数据在 Core（<see cref="WuhaoStore"/>），网页端共用同一份 data/wuhao.json。
/// </summary>
public partial class WuhaoPanel : UserControl
{
    private const string All = "全部";

    private AppState _st;
    private WuhaoStore _w;
    private readonly Debounce _judgeDelay;
    private readonly Debounce _basicDelay;
    private bool _ready;

    /// <summary>收起抽屉</summary>
    public event EventHandler CloseRequested;

    /// <summary>返回工具栏首页</summary>
    public event EventHandler BackRequested;

    /// <summary>挂在工具栏里时显示「‹ 返回」，单独截图时关掉</summary>
    public bool CanGoBack
    {
        get => BtnBack.Visibility == Visibility.Visible;
        set => BtnBack.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public WuhaoPanel() : this(null) { }

    public WuhaoPanel(AppState st)
    {
        InitializeComponent();
        _st = st;
        _judgeDelay = new Debounce(140, () => RunJudge(TxtQuery.Text));
        _basicDelay = new Debounce(120, ApplyBasicFilter);
        ItemGrid.RowHeight = double.NaN;   // 说明列会折行，行高交给内容
        EdgeGrid.RowHeight = double.NaN;
        Loaded += (_, _) => Init();
    }

    // ================= 初始化 =================

    private void Init()
    {
        if (_ready) return;
        _st ??= App.State;
        _w = _st.Wuhao;
        _w.EnsureLoaded();

        TabBasic.Content = $"不可收费 {_w.Items.Count}";
        TabCharge.Content = $"可收费 {_w.Edges.Count}";
        TabRule.Content = "口径依据";
        TxtSub.Text = $"{_w.Meta.Cats} 类指南条款 · {_w.Items.Count} 条基本物耗 · {_w.Edges.Count} 条边界判断";

        // Id 与 Name 必须分开存：条目里的 group 是 id（g1…g10），分组列表显示的是 Name（「病理 · 检验」）。
        // 早先用 Name 去筛 id，结果点任何分组都是 0 条，看着像「点开空白」。
        var rows = new List<GroupRow> { new("", All, _w.Items.Count.ToString()) };
        foreach (var g in _w.Groups)
        {
            var n = _w.Items.Count(i => i.Group == g.Id);
            if (n == 0) continue;
            rows.Add(new GroupRow(g.Id, g.Name, n.ToString()));
        }
        GroupList.ItemsSource = rows;
        GroupList.SelectedIndex = 0;

        EdgeSel.ItemsSource = new[] { "全部", "可另收", "可另收（减收）", "不可另收", "需核对" };
        EdgeSel.SelectedIndex = 0;

        RuleList.ItemsSource = _w.Rules;
        SourceList.ItemsSource = _w.Sources;
        TxtSrcTitle.Text = $"指南条款原文（{_w.Sources.Count} 类）";
        TxtFoot.Text = $"{_w.Meta.Source}。{_w.Meta.Note}";

        // 手工维护页的下拉框：分组按 id 值、显示名；结论用固定四档（与边界清单同一套词）
        CbMGroup.ItemsSource = _w.Groups.Select(g => new GroupRow(g.Id, g.Name, "")).ToList();
        CbEVerdict.ItemsSource = new[] { "可另收", "可另收（减收）", "不可另收", "需核对" };
        TxtMaintSub.Text =
            "只写 data/wuhao.user.json 的差异集（增 / 改 / 删），不改 data/wuhao.json，" +
            "也不进数据分发白名单。保存后本机立即生效。";

        _ready = true;
        ApplyBasicFilter();
        ApplyEdgeFilter();
        RunJudge("");
        TxtStatus.Text = "Ctrl+F 检索 · Esc 收起";
    }

    // ================= 页签 =================

    private void OnTab(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var tag = (sender as RadioButton)?.Tag as string ?? "judge";
        ViewJudge.Visibility = tag == "judge" ? Visibility.Visible : Visibility.Collapsed;
        ViewBasic.Visibility = tag == "basic" ? Visibility.Visible : Visibility.Collapsed;
        ViewCharge.Visibility = tag == "charge" ? Visibility.Visible : Visibility.Collapsed;
        ViewRule.Visibility = tag == "rule" ? Visibility.Visible : Visibility.Collapsed;
        ViewMaint.Visibility = Visibility.Collapsed;   // 顶部页签与手工维护互斥
    }

    // ================= ① 判定 =================

    private void OnQueryText(object sender, TextChangedEventArgs e)
    {
        TxtHint.Visibility = string.IsNullOrEmpty(TxtQuery.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (!_ready) return;
        _judgeDelay.Poke();
    }

    private void OnQueryKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _judgeDelay.Cancel();
        RunJudge(TxtQuery.Text);
        e.Handled = true;
    }

    private void OnChip(object sender, RoutedEventArgs e)
    {
        var q = (sender as Button)?.Tag as string ?? "";
        TxtQuery.Text = q;
        TxtQuery.CaretIndex = q.Length;
        TxtQuery.Focus();
        _judgeDelay.Cancel();
        RunJudge(q);
    }

    private void RunJudge(string q)
    {
        if (_w == null) return;
        var v = _w.Judge(q);

        TxtVerdict.Text = v.Head;
        TxtBasis.Text = v.Basis;
        TxtNote.Text = v.Note;
        TxtNote.Visibility = string.IsNullOrWhiteSpace(v.Note) ? Visibility.Collapsed : Visibility.Visible;

        var (badge, badgeBg, bg, border) = v.Tone switch
        {
            WuhaoStore.ToneNo => ("不可另收", "B.Danger", "B.DangerSoft", "B.Danger"),
            WuhaoStore.ToneYes => ("可另收", "B.Accent", "B.AccentSoft", "B.Accent"),
            WuhaoStore.ToneDeduct => ("可另收·减收", "B.Deduct", "B.DeductBg", "B.Deduct"),
            WuhaoStore.ToneCheck => ("需核对", "B.Warn", "B.WarnBg", "B.Warn"),
            _ => ("清单外", "B.Muted", "B.InfoBg", "B.InfoBorder"),
        };
        TxtBadge.Text = badge;
        Badge.Background = Res(badgeBg);
        ResultCard.Background = Res(bg);
        ResultCard.BorderBrush = Res(border);
        TxtVerdict.Foreground = v.Tone == WuhaoStore.ToneNo ? Res("B.Danger") : Res("B.Text");

        var near = v.Near ?? new List<WuhaoItem>();
        NearBox.Visibility = near.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NearList.ItemsSource = near;
    }

    private static Brush Res(string key) => (Brush)Application.Current.TryFindResource(key);

    // ================= ② 不可收费 =================

    private void OnBasicQuery(object sender, TextChangedEventArgs e)
    {
        TxtBasicHint.Visibility = string.IsNullOrEmpty(TxtBasicQuery.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (!_ready) return;
        _basicDelay.Poke();
    }

    private void OnGroupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        ApplyBasicFilter();
    }

    private void ApplyBasicFilter()
    {
        if (_w == null) return;
        var g = (GroupList.SelectedItem as GroupRow)?.Id;
        var gName = (GroupList.SelectedItem as GroupRow)?.Name;
        var rows = _w.Search(TxtBasicQuery.Text, string.IsNullOrEmpty(g) ? null : g);
        ItemGrid.ItemsSource = rows;
        TxtBasicCount.Text = string.IsNullOrEmpty(g)
            ? $"{rows.Count} 条 · 双击判定"
            : $"{rows.Count} 条 · {gName} · 双击判定";
        BtnEdgeHint.Visibility = Visibility.Collapsed;
        if (rows.Count > 0) ItemGrid.SelectedIndex = 0;
        else
        {
            // 清单里没有，但边界清单里可能有（留置针、避光输液器就是这种）。
            // 只说「没有匹配」会让人以为整个工具不认识它，所以顺手把边界条目的结论抬出来。
            var kw = (TxtBasicQuery.Text ?? "").Trim();
            var edges = _w.EdgeHit(kw);
            if (edges.Count > 0)
            {
                TxtItemName.Text = $"基本物耗清单里没有，边界清单有 {edges.Count} 条";
                TxtItemBasis.Text = string.Join("\n", edges.Take(3)
                    .Select(e => $"{e.Name} → {e.Verdict}：{e.Basis}"));
                TxtEdgeHint.Text = $"去「可收费」页看这 {edges.Count} 条 →";
                BtnEdgeHint.Visibility = Visibility.Visible;
            }
            else
            {
                TxtItemName.Text = string.IsNullOrEmpty(g) || string.IsNullOrEmpty(kw)
                    ? "没有匹配的条目"
                    : $"「{gName}」里没有匹配的条目";
                TxtItemBasis.Text = string.IsNullOrEmpty(kw)
                    ? "换个分组，或左上角切回「全部」。"
                    : "换个词试试：纱布 / 棉签 / 试剂 / 引流；或左上角切到「全部」。";
            }
        }
    }

    /// <summary>从「不可收费」的空结果跳到「可收费」页，按判定词过滤。</summary>
    private void OnGotoEdge(object sender, RoutedEventArgs e)
    {
        var kw = (TxtBasicQuery.Text ?? "").Trim();
        TabCharge.IsChecked = true;
        ViewJudge.Visibility = Visibility.Collapsed;
        ViewBasic.Visibility = Visibility.Collapsed;
        ViewCharge.Visibility = Visibility.Visible;
        ViewRule.Visibility = Visibility.Collapsed;

        var edges = _w.EdgeHit(kw);
        EdgeSel.SelectedIndex = 0;          // 先归位筛选框（会触发一次全量刷新）
        EdgeGrid.ItemsSource = edges;       // 再用这个词的结果盖回去
        TxtEdgeCount.Text = edges.Count > 0
            ? $"「{kw}」对上 {edges.Count} 条 · " + string.Join("、", edges.Select(x => x.Verdict).Distinct())
            : "没有对上的边界条目";
        if (edges.Count > 0) EdgeGrid.SelectedIndex = 0;
    }

    private void OnItemChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ItemGrid.SelectedItem is not WuhaoItem it) return;
        TxtItemName.Text = $"{it.Name}　·　{_w.GroupName(it.Group)}　·　{it.CountText}";
        var cats = it.Cats ?? new List<string>();
        var srcs = _w.Sources.Where(s => cats.Contains(s.Cat)).ToList();
        var sb = new System.Text.StringBuilder();
        sb.Append(it.Note);
        sb.Append('\n').Append($"出现在 {it.Count}/36 类：{it.CatText}");
        // 「扩增内容」：详情区直接带出条款原文，省得再去「口径依据」页一条条翻
        foreach (var s in srcs.Take(2))
        {
            var c = s.Clause ?? "";
            if (c.Length > 160) c = c[..160] + "…";
            sb.Append('\n').Append($"【{s.Cat}】{c}");
        }
        if (srcs.Count > 2) sb.Append('\n').Append($"（另有 {srcs.Count - 2} 类条款见「口径依据」页）");
        TxtItemBasis.Text = sb.ToString();
        TxtItemBasis.ToolTip = srcs.Count == 0 ? null : string.Join("\n\n", srcs.Select(s => $"【{s.Cat}】{s.Clause}"));
    }

    /// <summary>双击某条 → 跳到「判定」页，直接把这条判一遍（原本文案写了「双击判定」却没接事件）。</summary>
    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!_ready) return;
        // 只认行内的双击：点表头、空白、滚动条不跳
        if (ItemsControl.ContainerFromElement(ItemGrid, e.OriginalSource as DependencyObject) is not DataGridRow)
            return;
        if (ItemGrid.SelectedItem is WuhaoItem it) GotoJudge(it.Name);
    }

    /// <summary>键盘同样能走：选中后按 Enter。</summary>
    private void OnItemKey(object sender, KeyEventArgs e)
    {
        if (!_ready || e.Key != Key.Enter) return;
        if (ItemGrid.SelectedItem is WuhaoItem it)
        {
            e.Handled = true;
            GotoJudge(it.Name);
        }
    }

    private void GotoJudge(string name)
    {
        TabJudge.IsChecked = true;
        ViewJudge.Visibility = Visibility.Visible;
        ViewBasic.Visibility = Visibility.Collapsed;
        ViewCharge.Visibility = Visibility.Collapsed;
        ViewRule.Visibility = Visibility.Collapsed;
        TxtQuery.Text = name;
        _judgeDelay.Cancel();
        RunJudge(name);
        TxtQuery.Focus();
        TxtQuery.SelectAll();
    }

    // ================= ③ 可收费 =================

    private void OnEdgeFilter(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        ApplyEdgeFilter();
    }

    private void ApplyEdgeFilter()
    {
        if (_w == null) return;
        var v = EdgeSel.SelectedItem as string;
        var rows = _w.EdgeList(v);
        EdgeGrid.ItemsSource = rows;
        var yes = rows.Count(x => x.Verdict == "可另收");
        var dd = rows.Count(x => x.Verdict == "可另收（减收）");
        var no = rows.Count(x => x.Verdict == "不可另收");
        var ck = rows.Count(x => x.Verdict == "需核对");
        TxtEdgeCount.Text = dd > 0
            ? $"{rows.Count} 条：可另收 {yes} · 可另收减收 {dd} · 不可另收 {no} · 需核对 {ck}"
            : $"{rows.Count} 条：可另收 {yes} · 不可另收 {no} · 需核对 {ck}";
    }

    private void OnEdgeChanged(object sender, SelectionChangedEventArgs e) { }

    // ================= 键盘 / 焦点 =================

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnBack(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    public bool HandleKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseRequested?.Invoke(this, EventArgs.Empty); return true; }
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FocusSearch();
            return true;
        }
        return false;
    }

    public void FocusSearch()
    {
        if (!_ready) Init();
        if (TabBasic.IsChecked == true) { TxtBasicQuery.Focus(); TxtBasicQuery.SelectAll(); }
        else { TabJudge.IsChecked = true; TxtQuery.Focus(); TxtQuery.SelectAll(); }
    }

    // ================= ⑤ 手工维护（本机覆盖层） =================
    //
    // 三层数据必须分清，混在一起就会写坏基础库：
    //   data/wuhao.json       基础库（随分发下发，只读）
    //   data/wuhao.user.json  本机差异集（增 / 改 / 删，**不进 DataCatalog**，绝不下发）
    //   本面板的行             基础库 + 差异集的行内副本 —— 编辑只改副本，点保存才落地

    private readonly List<MaintRow> _maintItemRows = new();
    private readonly List<MaintRow> _maintEdgeRows = new();
    private MaintRow _maintSel;
    /// <summary>填充编辑框时置位，挡住 TextChanged 回写（否则一选中就把空值写进副本）</summary>
    private bool _maintFilling;
    /// <summary>别名体检结果（只读，不写任何文件）。</summary>
    private readonly List<AliasIssue> _aliasIssues = new();
    private int _aliasRealConflicts;

    private sealed class AliasIssue
    {
        public string Kind { get; set; } = "";
        public string Alias { get; set; } = "";
        public string Owner { get; set; } = "";
        public string Other { get; set; } = "";
        public string Note { get; set; } = "";
    }

    /// <summary>维护表的一行：Base* 是基础库原值（null = 本机新增），Item/Edge 是可编辑副本。</summary>
    private sealed class MaintRow : System.ComponentModel.INotifyPropertyChanged
    {
        public bool IsEdge;
        public WuhaoItem BaseItem;
        public WuhaoEdge BaseEdge;
        public WuhaoItem Item;
        public WuhaoEdge Edge;
        public bool Deleted;

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        /// <summary>整行刷新（属性名传空串 = 全部失效），免得每改一个字就重建表格丢选中。</summary>
        public void Raise() => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(""));

        public string Name => IsEdge ? (Edge?.Name ?? "") : (Item?.Name ?? "");
        public string VerdictText => IsEdge ? (Edge?.Verdict ?? "") : $"{Item?.Count ?? 0} 类";
        /// <summary>覆盖列：本机新增 / 本机改过 / 待删。空串 = 原样来自基础库。</summary>
        public string CoverText
        {
            get
            {
                if (Deleted) return "待删";
                if (IsEdge)
                {
                    if (BaseEdge == null) return "新增";
                    return SameEdge(Edge, BaseEdge) ? "" : "已改";
                }
                if (BaseItem == null) return "新增";
                return SameItem(Item, BaseItem) ? "" : "已改";
            }
        }
    }

    private void OnMaintToggle(object sender, RoutedEventArgs e)
    {
        if (!_ready) Init();
        var on = ViewMaint.Visibility != Visibility.Visible;
        if (on)
        {
            // 顶部页签全部取消勾选：不然会出现「判定」高亮却显示维护页的矛盾画面
            TabJudge.IsChecked = false;
            TabBasic.IsChecked = false;
            TabCharge.IsChecked = false;
            TabRule.IsChecked = false;
            ViewJudge.Visibility = Visibility.Collapsed;
            ViewBasic.Visibility = Visibility.Collapsed;
            ViewCharge.Visibility = Visibility.Collapsed;
            ViewRule.Visibility = Visibility.Collapsed;
            ViewMaint.Visibility = Visibility.Visible;
            BuildMaintRows();
            ApplyMaintTab();
        }
        else
        {
            ViewMaint.Visibility = Visibility.Collapsed;
            TabJudge.IsChecked = true;   // 触发 OnTab，回到判定页
        }
    }

    private void OnMaintTab(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        ApplyMaintTab();
    }

    private void ApplyMaintTab()
    {
        var isItem = TabMaintItem.IsChecked == true;
        _maintSel = null;
        ClearMaintEditor();
        MaintGrid.ItemsSource = isItem
            ? (System.Collections.IEnumerable)_maintItemRows
            : _maintEdgeRows;
        PanelMGroup.Visibility = isItem ? Visibility.Visible : Visibility.Collapsed;
        PanelMItem.Visibility = isItem ? Visibility.Visible : Visibility.Collapsed;
        PanelMEdge.Visibility = isItem ? Visibility.Collapsed : Visibility.Visible;
        RefreshMaintCount();
        if (MaintGrid.Items.Count > 0) MaintGrid.SelectedIndex = 0;
    }

    /// <summary>用「基础库 + 差异集」重建两页的行；被删的条目也要出现（标「待删」），否则没法反悔。</summary>
    private void BuildMaintRows()
    {
        _maintItemRows.Clear();
        _maintEdgeRows.Clear();

        var baseFile = Js.ReadJsonFile<WuhaoFile>(AppPaths.WuhaoFile);
        var saved = Js.ReadJsonFile<WuhaoUserFile>(AppPaths.WuhaoUserFile) ?? new WuhaoUserFile();
        var baseItems = baseFile?.Items ?? _w.Items;
        var baseEdges = baseFile?.Edges ?? _w.Edges;

        foreach (var b in baseItems)
        {
            var u = saved.Items.FirstOrDefault(x => x.Id == b.Id);
            _maintItemRows.Add(new MaintRow
            {
                BaseItem = b,
                Item = CloneItem(u ?? b),
                Deleted = saved.DeletedItems.Contains(b.Id),
            });
        }
        // 本机新增的放最前面：它们是这次维护最该复核的内容，混在 130 多行末尾等于看不见
        var at = 0;
        foreach (var u in saved.Items.Where(x => baseItems.All(b => b.Id != x.Id)))
            _maintItemRows.Insert(at++, new MaintRow { BaseItem = null, Item = CloneItem(u) });

        foreach (var b in baseEdges)
        {
            var u = saved.Edges.FirstOrDefault(x => x.Name == b.Name);
            _maintEdgeRows.Add(new MaintRow
            {
                IsEdge = true,
                BaseEdge = b,
                Edge = CloneEdge(u ?? b),
                Deleted = saved.DeletedEdges.Contains(b.Name),
            });
        }
        var atEdge = 0;
        foreach (var u in saved.Edges.Where(x => baseEdges.All(b => b.Name != x.Name)))
            _maintEdgeRows.Insert(atEdge++, new MaintRow { IsEdge = true, BaseEdge = null, Edge = CloneEdge(u) });
    }

    private void OnMaintSelection(object sender, SelectionChangedEventArgs e)
    {
        if (MaintGrid.SelectedItem is not MaintRow r) return;
        _maintSel = r;
        FillMaintEditor(r);
    }

    private void FillMaintEditor(MaintRow r)
    {
        _maintFilling = true;
        try
        {
            PanelMGroup.Visibility = r.IsEdge ? Visibility.Collapsed : Visibility.Visible;
            PanelMItem.Visibility = r.IsEdge ? Visibility.Collapsed : Visibility.Visible;
            PanelMEdge.Visibility = r.IsEdge ? Visibility.Visible : Visibility.Collapsed;

            EdtEName.Text = r.Name;
            if (r.IsEdge)
            {
                CbEVerdict.SelectedItem = r.Edge.Verdict;
                EdtECat.Text = r.Edge.Cat;
                EdtEBasis.Text = r.Edge.Basis;
                EdtENote.Text = r.Edge.Note;
                TxtEditTitle.Text = r.Deleted
                    ? $"待删除 · {r.Name}（再点「删除」可恢复，保存后才真正移除）"
                    : r.BaseEdge == null ? "本机新增 · 边界条目" : "基础库边界条目 · 本机可改";
            }
            else
            {
                CbMGroup.SelectedValue = r.Item.Group;
                EdtMAlias.Text = string.Join("、", r.Item.Alias ?? new List<string>());
                EdtMNote.Text = r.Item.Note;
                TxtEditTitle.Text = r.Deleted
                    ? $"待删除 · {r.Name}（再点「删除」可恢复，保存后才真正移除）"
                    : r.BaseItem == null ? "本机新增 · 清单条目" : "基础库条目 · 本机可改";
            }
        }
        finally { _maintFilling = false; }
    }

    private void ClearMaintEditor()
    {
        _maintFilling = true;
        try
        {
            EdtEName.Text = "";
            EdtMAlias.Text = "";
            EdtMNote.Text = "";
            EdtECat.Text = "";
            EdtEBasis.Text = "";
            EdtENote.Text = "";
            CbMGroup.SelectedIndex = -1;
            CbEVerdict.SelectedIndex = -1;
            TxtEditTitle.Text = "选中左侧条目后可修改";
        }
        finally { _maintFilling = false; }
    }

    /// <summary>编辑框 → 行内副本。只改副本，不动 WuhaoStore，所以取消/重置都能干净回退。</summary>
    private void OnMaintField(object sender, RoutedEventArgs e)
    {
        if (_maintFilling || _maintSel == null) return;
        var r = _maintSel;
        if (r.IsEdge)
        {
            r.Edge.Name = (EdtEName.Text ?? "").Trim();
            r.Edge.Verdict = CbEVerdict.SelectedItem as string ?? "需核对";
            r.Edge.Cat = (EdtECat.Text ?? "").Trim();
            r.Edge.Basis = (EdtEBasis.Text ?? "").Trim();
            r.Edge.Note = (EdtENote.Text ?? "").Trim();
        }
        else
        {
            r.Item.Name = (EdtEName.Text ?? "").Trim();
            r.Item.Group = CbMGroup.SelectedValue as string ?? r.Item.Group;
            r.Item.Alias = SplitAlias(EdtMAlias.Text);
            r.Item.Note = (EdtMNote.Text ?? "").Trim();
        }
        r.Raise();
        RefreshMaintCount();
    }

    /// <summary>删除 / 恢复（同一个按钮来回切）。真正移除发生在「保存到本机」之后。</summary>
    private void OnMaintDelete(object sender, RoutedEventArgs e)
    {
        if (_maintSel == null) return;
        _maintSel.Deleted = !_maintSel.Deleted;
        _maintSel.Raise();
        FillMaintEditor(_maintSel);
        RefreshMaintCount();
    }

    private void OnMaintNew(object sender, RoutedEventArgs e)
    {
        if (TabMaintItem.IsChecked == true)
        {
            var row = new MaintRow
            {
                Item = new WuhaoItem
                {
                    Id = NextUserId(),
                    Name = "",
                    Group = _w.Groups.FirstOrDefault()?.Id ?? "",
                    Alias = new List<string>(),
                    Cats = new List<string>(),
                    Note = "",
                },
            };
            _maintItemRows.Insert(0, row);
            MaintGrid.ItemsSource = _maintItemRows;
            MaintGrid.SelectedItem = row;
        }
        else
        {
            var row = new MaintRow
            {
                IsEdge = true,
                Edge = new WuhaoEdge { Name = "", Cat = "", Verdict = "需核对", Basis = "", Note = "" },
            };
            _maintEdgeRows.Insert(0, row);
            MaintGrid.ItemsSource = _maintEdgeRows;
            MaintGrid.SelectedItem = row;
        }
        MaintGrid.ScrollIntoView(MaintGrid.SelectedItem);
        EdtEName.Focus();
        EdtEName.SelectAll();
        RefreshMaintCount();
    }

    /// <summary>撤销未保存的编辑：直接从磁盘重读（基础库 + 已保存的差异集）。</summary>
    private void OnMaintUndo(object sender, RoutedEventArgs e)
    {
        _w.Reload();
        BuildMaintRows();
        ApplyMaintTab();
        RefreshCounts();
        TxtStatus.Text = "已撤销未保存的编辑";
    }

    /// <summary>清空差异集（含以前保存过的），回到纯基础库。</summary>
    private void OnMaintReset(object sender, RoutedEventArgs e)
    {
        var ok = MessageBox.Show(
            "清空全部本机维护？\n\n只会删除 data/wuhao.user.json 里的差异集，" +
            "data/wuhao.json 与分发数据都不受影响。",
            "重置全部改动", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (ok != MessageBoxResult.OK) return;

        _w.Save(new WuhaoUserFile());
        BuildMaintRows();
        ApplyMaintTab();
        RefreshCounts();
        TxtStatus.Text = "已重置：本机维护清空，回到基础库";
    }

    /// <summary>
    /// 保存差异集：与基础库逐字段比，只写真正变了的部分 ——
    /// 改回原样的条目不写、删掉本机新增的条目不写删除标记，文件永远是最小差异。
    /// </summary>
    private void OnMaintSave(object sender, RoutedEventArgs e)
    {
        var blank = _maintItemRows.Concat(_maintEdgeRows)
            .FirstOrDefault(x => !x.Deleted && string.IsNullOrWhiteSpace(x.Name));
        if (blank != null)
        {
            MessageBox.Show("还有条目没写名称（或名称只剩空格），补全后再保存。",
                "保存到本机", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var user = new WuhaoUserFile();

        foreach (var r in _maintItemRows)
        {
            if (r.Deleted)
            {
                if (r.BaseItem != null) user.DeletedItems.Add(r.BaseItem.Id);
                continue;
            }
            if (r.BaseItem == null) user.Items.Add(r.Item);                    // 本机新增
            else if (!SameItem(r.Item, r.BaseItem)) user.Items.Add(r.Item);    // 本机修改
        }

        foreach (var r in _maintEdgeRows)
        {
            if (r.Deleted)
            {
                if (r.BaseEdge != null) user.DeletedEdges.Add(r.BaseEdge.Name);
                continue;
            }
            if (r.BaseEdge == null) { user.Edges.Add(r.Edge); continue; }

            if (!string.Equals(r.Edge.Name, r.BaseEdge.Name, StringComparison.Ordinal))
            {
                // 边界条目按 Name 匹配：改名 = 删旧 + 增新，否则旧名称会一直留在清单里
                user.DeletedEdges.Add(r.BaseEdge.Name);
                user.Edges.Add(r.Edge);
                continue;
            }
            if (!SameEdge(r.Edge, r.BaseEdge)) user.Edges.Add(r.Edge);
        }

        _w.Save(user);          // 写 data/wuhao.user.json，并在内存里立即生效
        BuildMaintRows();
        ApplyMaintTab();
        RefreshCounts();

        var add = user.Items.Count + user.Edges.Count;
        var del = user.DeletedItems.Count + user.DeletedEdges.Count;
        TxtStatus.Text = del == 0
            ? $"已保存：本机新增 / 覆盖 {add} 条，已生效"
            : $"已保存：新增 / 覆盖 {add} 条 · 删除 {del} 条，已生效";
    }

    private void RefreshMaintCount()
    {
        var rows = TabMaintItem.IsChecked == true ? _maintItemRows : _maintEdgeRows;
        var all = _maintItemRows.Concat(_maintEdgeRows).ToList();
        var add = all.Count(x => !x.Deleted && (x.IsEdge ? x.BaseEdge : (object)x.BaseItem) == null);
        var del = all.Count(x => x.Deleted);
        var edit = all.Count(x => !x.Deleted &&
            (x.IsEdge ? x.BaseEdge != null && !SameEdge(x.Edge, x.BaseEdge)
                      : x.BaseItem != null && !SameItem(x.Item, x.BaseItem)));
        TxtMaintCount.Text = $"{rows.Count - del} 条生效 · 新增 {add} · 已改 {edit} · 待删 {del}";
        RefreshAliasAudit();
    }

    // ================= 别名体检（只读） =================

    /// <summary>对齐 Core WuhaoStore.Same 的归一化口径。</summary>
    private static string NormKey(string s) => (s ?? "")
        .Replace(" ", "").Replace("（", "(").Replace("）", ")").Replace("／", "/")
        .ToLowerInvariant();

    /// <summary>
    /// 用「基础库 + 本机差异集」的生效条目重算别名问题。只检查、不改数据。
    /// 真冲突（跨条目撞名 / 别名互撞 / 同名条目）会让按钮标红；自名别名与撞边界条目只是提示。
    /// </summary>
    private void RefreshAliasAudit()
    {
        _aliasIssues.Clear();
        var items = _maintItemRows.Where(r => !r.Deleted && r.Item != null)
                                  .Select(r => r.Item).ToList();

        var names = new Dictionary<string, List<int>>();
        var aliases = new Dictionary<string, List<(int idx, string raw)>>();
        for (var i = 0; i < items.Count; i++)
        {
            var nk = NormKey(items[i].Name);
            if (nk.Length > 0)
            {
                if (!names.TryGetValue(nk, out var nl)) names[nk] = nl = new List<int>();
                nl.Add(i);
            }
            foreach (var a in items[i].Alias ?? new List<string>())
            {
                var ak = NormKey(a);
                if (ak.Length == 0) continue;
                if (!aliases.TryGetValue(ak, out var al)) aliases[ak] = al = new List<(int, string)>();
                al.Add((i, a));
            }
        }

        var edgeNames = new Dictionary<string, WuhaoEdge>();
        foreach (var e in _w?.Edges ?? new List<WuhaoEdge>())
        {
            var ek = NormKey(e.Name);
            if (ek.Length > 0 && !edgeNames.ContainsKey(ek)) edgeNames[ek] = e;
        }

        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            foreach (var a in it.Alias ?? new List<string>())
            {
                var ak = NormKey(a);
                if (ak.Length == 0) continue;
                if (ak == NormKey(it.Name))
                {
                    _aliasIssues.Add(new AliasIssue
                    {
                        Kind = "冗余自名别名", Alias = a, Owner = it.Name, Other = "—",
                        Note = "别名与主名相同；HitItem 每条先比主名，判定不受影响，可从源表精简",
                    });
                }
                if (names.TryGetValue(ak, out var owners))
                {
                    foreach (var j in owners.Where(j => j != i))
                        _aliasIssues.Add(new AliasIssue
                        {
                            Kind = "跨条目撞名", Alias = a, Owner = it.Name, Other = items[j].Name,
                            Note = $"清单第 {Math.Min(i, j) + 1} 行（{(i < j ? "本条别名" : "对方主名")}）先命中，归属与说明会串",
                        });
                }
                if (aliases.TryGetValue(ak, out var users))
                {
                    foreach (var j in users.Where(u => u.idx > i).Select(u => u.idx).Distinct())
                        _aliasIssues.Add(new AliasIssue
                        {
                            Kind = "别名互撞", Alias = a, Owner = it.Name, Other = items[j].Name,
                            Note = "两条目共用同一别名，靠清单顺序决定命中，判定归属不唯一",
                        });
                }
                if (edgeNames.TryGetValue(ak, out var edge))
                    _aliasIssues.Add(new AliasIssue
                    {
                        Kind = "别名撞边界", Alias = a, Owner = it.Name, Other = edge.Name,
                        Note = $"边界结论「{edge.Verdict}」；Judge ② 阶段会与清单命中比具体度，需人工核对",
                    });
            }
        }
        foreach (var kv in names.Where(kv => kv.Value.Count > 1))
            _aliasIssues.Add(new AliasIssue
            {
                Kind = "同名条目", Alias = items[kv.Value[0]].Name,
                Owner = items[kv.Value[0]].Name, Other = $"共 {kv.Value.Count} 条",
                Note = "主名重复，判定归属不唯一",
            });

        // 真冲突必须置顶：否则会被 100 条「冗余自名别名」埋到表格末尾，等于看不见。
        static int Rank(string kind) => kind switch
        {
            "跨条目撞名" or "别名互撞" or "同名条目" => 0,
            "别名撞边界" => 1,
            _ => 2,
        };
        _aliasIssues.Sort((a, b) => Rank(a.Kind).CompareTo(Rank(b.Kind)));

        _aliasRealConflicts = _aliasIssues.Count(x =>
            x.Kind == "跨条目撞名" || x.Kind == "别名互撞" || x.Kind == "同名条目");
        var redundant = _aliasIssues.Count(x => x.Kind == "冗余自名别名");
        var edgeHits = _aliasIssues.Count(x => x.Kind == "别名撞边界");
        BtnAliasAudit.Content = $"别名体检 · 冗余 {redundant} · 冲突 {_aliasRealConflicts} · 边界 {edgeHits}";
        BtnAliasAudit.SetResourceReference(Control.ForegroundProperty,
            _aliasRealConflicts > 0 ? "B.Danger" : "B.Muted");
        BtnAliasAudit.ToolTip = _aliasRealConflicts > 0
            ? $"发现 {_aliasRealConflicts} 处真冲突，点击查看明细（只读）"
            : "只读检查：冗余自名别名不影响判定；真冲突会让清单顺序决定命中";
    }

    private void OnAliasAudit(object sender, RoutedEventArgs e)
    {
        if (!_ready) Init();
        if (_maintItemRows.Count == 0) BuildMaintRows();

        var win = new Window
        {
            Title = "基本物耗清单 · 别名体检（只读）",
            Width = 790, Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
        };
        win.SetResourceReference(Control.BackgroundProperty, "B.Bg");
        win.Content = BuildAliasAuditGrid(win);
        win.ShowDialog();
    }

    /// <summary>体检明细视图。host 为 null 时用于离屏截图（去掉「关闭」按钮），不弹窗、不阻塞。</summary>
    private FrameworkElement BuildAliasAuditGrid(Window host)
    {
        if (_maintItemRows.Count == 0) BuildMaintRows();
        RefreshAliasAudit();
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new TextBlock
        {
            Margin = new Thickness(14, 12, 14, 8), FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Text = $"生效 {_maintItemRows.Count(r => !r.Deleted)} 条 · 冗余自名别名 " +
                   $"{_aliasIssues.Count(x => x.Kind == "冗余自名别名")} · 真冲突 {_aliasRealConflicts} · " +
                   $"别名撞边界 {_aliasIssues.Count(x => x.Kind == "别名撞边界")}\n" +
                   "判定顺序：HitItem 按清单顺序逐条走，每条先比主名再比别名，第一个命中即返回；" +
                   "真冲突会让顺序靠前者胜出。本页只读，不会改动 wuhao.json 或本机差异集。",
        };
        head.SetResourceReference(TextBlock.ForegroundProperty, "B.Text");
        Grid.SetRow(head, 0);
        root.Children.Add(head);

        var dg = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, Margin = new Thickness(14, 0, 14, 8),
            RowHeaderWidth = 0, HeadersVisibility = DataGridHeadersVisibility.Column,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        if (TryFindResource("Dg.ReadOnly") is Style dgStyle) dg.Style = dgStyle;
        dg.SetResourceReference(Control.ForegroundProperty, "B.Text");
        // 固定列宽合计 730px，正好落在 782px 抽屉内；星号列会按长说明文本撑宽把最后一列挤出可视区。
        dg.Columns.Add(new DataGridTextColumn { Header = "类型", Binding = new System.Windows.Data.Binding(nameof(AliasIssue.Kind)), Width = 100 });
        dg.Columns.Add(new DataGridTextColumn { Header = "别名 / 名称", Binding = new System.Windows.Data.Binding(nameof(AliasIssue.Alias)), Width = 130 });
        dg.Columns.Add(new DataGridTextColumn { Header = "所属条目", Binding = new System.Windows.Data.Binding(nameof(AliasIssue.Owner)), Width = 145 });
        dg.Columns.Add(new DataGridTextColumn { Header = "冲突对象", Binding = new System.Windows.Data.Binding(nameof(AliasIssue.Other)), Width = 120 });
        var noteCol = new DataGridTextColumn
        {
            Header = "说明", Binding = new System.Windows.Data.Binding(nameof(AliasIssue.Note)), Width = 235,
        };
        var noteStyle = new Style(typeof(TextBlock));
        noteStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        noteStyle.Setters.Add(new Setter(TextBlock.ToolTipProperty,
            new System.Windows.Data.Binding(nameof(AliasIssue.Note))));
        noteCol.ElementStyle = noteStyle;
        dg.Columns.Add(noteCol);
        dg.ItemsSource = _aliasIssues.Count > 0 ? _aliasIssues.ToList() : new List<AliasIssue>();
        Grid.SetRow(dg, 1);
        root.Children.Add(dg);

        if (host != null)
        {
            var close = new Button
            {
                Content = "关闭", Width = 90, Height = 28, Margin = new Thickness(0, 0, 14, 12),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            if (TryFindResource("Btn.Primary") is Style btnStyle) close.Style = btnStyle;
            close.Click += (_, _) => host.Close();
            Grid.SetRow(close, 2);
            root.Children.Add(close);
        }

        root.SetResourceReference(Control.BackgroundProperty, "B.Bg");
        return root;
    }

    /// <summary>保存后把「不可收费 / 可收费」的条数、分组、判定结果一起刷新。</summary>
    private void RefreshCounts()
    {
        TabBasic.Content = $"不可收费 {_w.Items.Count}";
        TabCharge.Content = $"可收费 {_w.Edges.Count}";
        TxtSub.Text = $"{_w.Meta.Cats} 类指南条款 · {_w.Items.Count} 条基本物耗 · {_w.Edges.Count} 条边界判断";

        var keep = (GroupList.SelectedItem as GroupRow)?.Id;
        var rows = new List<GroupRow> { new("", All, _w.Items.Count.ToString()) };
        foreach (var g in _w.Groups)
        {
            var n = _w.Items.Count(i => i.Group == g.Id);
            if (n == 0) continue;
            rows.Add(new GroupRow(g.Id, g.Name, n.ToString()));
        }
        GroupList.ItemsSource = rows;
        GroupList.SelectedItem = rows.FirstOrDefault(x => x.Id == keep) ?? rows[0];

        ApplyBasicFilter();
        ApplyEdgeFilter();
        RunJudge(TxtQuery.Text);
    }

    private string NextUserId()
    {
        var used = _maintItemRows.Where(x => x.Item?.Id != null).Select(x => x.Item.Id).ToHashSet();
        for (var i = 1; i < 10000; i++)
        {
            var id = "u" + i;
            if (!used.Contains(id)) return id;
        }
        return "u" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    private static WuhaoItem CloneItem(WuhaoItem s) => new()
    {
        Id = s.Id ?? "",
        Name = s.Name ?? "",
        Group = s.Group ?? "",
        Alias = new List<string>(s.Alias ?? new List<string>()),
        Cats = new List<string>(s.Cats ?? new List<string>()),
        Count = s.Count,
        Note = s.Note ?? "",
    };

    private static WuhaoEdge CloneEdge(WuhaoEdge s) => new()
    {
        Name = s.Name ?? "",
        Cat = s.Cat ?? "",
        Verdict = s.Verdict ?? "需核对",
        Basis = s.Basis ?? "",
        Note = s.Note ?? "",
    };

    private static List<string> SplitAlias(string s) => (s ?? "")
        .Split(new[] { '、', '，', ',', '；', ';', '/', '／', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Trim())
        .Where(x => x.Length > 0)
        .Distinct()
        .ToList();

    /// <summary>只比用户能改的字段：Cats / Count 是数据侧算出来的，面板不动它们。</summary>
    private static bool SameItem(WuhaoItem a, WuhaoItem b)
        => a != null && b != null
           && string.Equals(a.Name, b.Name, StringComparison.Ordinal)
           && string.Equals(a.Group, b.Group, StringComparison.Ordinal)
           && string.Equals(a.Note, b.Note, StringComparison.Ordinal)
           && (a.Alias ?? new List<string>()).SequenceEqual(b.Alias ?? new List<string>(), StringComparer.Ordinal);

    private static bool SameEdge(WuhaoEdge a, WuhaoEdge b)
        => a != null && b != null
           && string.Equals(a.Name, b.Name, StringComparison.Ordinal)
           && string.Equals(a.Cat, b.Cat, StringComparison.Ordinal)
           && string.Equals(a.Verdict, b.Verdict, StringComparison.Ordinal)
           && string.Equals(a.Basis, b.Basis, StringComparison.Ordinal)
           && string.Equals(a.Note, b.Note, StringComparison.Ordinal);

    // ================= 离屏截图 =================

    /// <summary>离屏截图专用：tab = judge / basic / charge / rule，query 为判定词或清单关键词</summary>
    internal FrameworkElement DiagRoot(string tab, string query)
    {
        Init();
        // wuhao:aliaudit —— 离屏截图别名体检明细（不弹窗、不阻塞）
        if (tab == "aliaudit") return BuildAliasAuditGrid(null);
        Diag(tab, query);
        return this;
    }

    internal void Diag(string tab, string query)
    {
        if (!_ready) Init();
        switch (tab)
        {
            case "basic": TabBasic.IsChecked = true; break;
            case "charge": TabCharge.IsChecked = true; break;
            case "rule": TabRule.IsChecked = true; break;
            case "maint":
                // 截图 / 自检用：直接落到手工维护页；query = edge 时切到边界条目
                TabJudge.IsChecked = false;
                TabBasic.IsChecked = false;
                TabCharge.IsChecked = false;
                TabRule.IsChecked = false;
                ViewJudge.Visibility = Visibility.Collapsed;
                ViewBasic.Visibility = Visibility.Collapsed;
                ViewCharge.Visibility = Visibility.Collapsed;
                ViewRule.Visibility = Visibility.Collapsed;
                ViewMaint.Visibility = Visibility.Visible;
                BuildMaintRows();
                ApplyMaintTab();
                if (query == "edge")
                {
                    TabMaintEdge.IsChecked = true;
                    ApplyMaintTab();
                }
                return;
            default: TabJudge.IsChecked = true; break;
        }
        if (string.IsNullOrEmpty(query)) return;
        if (tab == "basic")
        {
            // 截图 / 自检用：dbl@玻片 模拟「双击这条」——验证 GotoJudge 真的把判定页拉起来了
            if (query.StartsWith("dbl@", StringComparison.Ordinal))
            {
                var row = _w.Search(query[4..], null).FirstOrDefault();
                if (row != null)
                {
                    ItemGrid.SelectedItem = row;
                    GotoJudge(row.Name);
                }
                return;
            }
            // 截图 / 自检用：g + 分组序号（g7）直接选中该分组，验证「点分组不再空白」
            if (query.Length >= 2 && query[0] == 'g' && int.TryParse(query[1..], out var gi)
                && gi >= 0 && gi < GroupList.Items.Count)
            {
                GroupList.SelectedIndex = gi;
                TxtBasicQuery.Text = "";
            }
            else TxtBasicQuery.Text = query;
            ApplyBasicFilter();
        }
        else if (tab == "charge")
        {
            // 截图用：直接把结论筛选框拨到指定值（可另收 / 可另收（减收）/ 不可另收 / 需核对）
            if (EdgeSel.Items.Contains(query)) EdgeSel.SelectedItem = query;
        }
        else if (tab == "judge")
        {
            TxtQuery.Text = query;
            _judgeDelay.Cancel();
            RunJudge(query);
        }
    }

    private sealed class GroupRow
    {
        public GroupRow(string id, string name, string count) { Id = id; Name = name; Count = count; }
        /// <summary>分组 id（g1…g10）；「全部」为空串。</summary>
        public string Id { get; }
        public string Name { get; }
        public string Count { get; }
    }
}
