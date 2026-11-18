using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Dip3Grouper;

namespace Dip3GrouperApp.Desktop.Controls;

/// <summary>联想下拉的一行（含显示用计算属性）。</summary>
public class AcRow : INotifyPropertyChanged
{
    public string Code { get; set; }
    public string Name { get; set; }
    public string InsCode { get; set; }
    public string InsName { get; set; }
    public bool Gray { get; set; }
    public bool Linked { get; set; }
    public List<GrouperService.DipHit> Dip { get; set; }

    private bool _on;
    /// <summary>键盘/鼠标当前高亮</summary>
    public bool IsOn
    {
        get => _on;
        set { if (_on == value) return; _on = value; Raise(); }
    }

    public bool HasMap => !string.IsNullOrEmpty(InsCode) || !string.IsNullOrEmpty(InsName);
    public string MapText =>
        $"医保版2.0: {(string.IsNullOrEmpty(InsCode) ? "—" : InsCode)}" +
        (!string.IsNullOrEmpty(InsName) && InsName != Name ? " " + InsName : "");

    public bool HasDip => Dip != null && Dip.Count > 0;
    public string DipText => HasDip
        ? "DIP病种: " + string.Join(" · ", Dip.Select(d => $"[{d.Type}]#{d.Seq} {d.Name}"))
        : "";

    public event PropertyChangedEventHandler PropertyChanged;
    private void Raise([CallerMemberName] string n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>
/// 诊断 / 手术编码联想输入框。
/// 行为对齐网页版 acAttach + acFetch + acFetchLinked：
///   输入 180ms 防抖检索；空输入且「手术」或「多值」时按主诊断带出联动建议；
///   上下键移动高亮、Enter 选中、Esc 关闭；多值输入只替换最后一段并用 ; 分隔。
/// </summary>
public partial class SuggestBox : UserControl
{
    private static readonly string[] Separators = { "|", ";", "；" };

    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private List<AcRow> _rows = new();
    private int _idx = -1;
    private int _total;

    /// <summary>
    /// 程序性回填（选中下拉项 / 外部赋 Text）期间的静音开关。
    /// 不回填后立刻再检索一次 —— 否则选完 180ms 后下拉又弹出来，
    /// 用户得先把它按掉才能接着输入，表现为「选完还要再点一次」。
    /// </summary>
    private bool _mute;

    /// <summary>宿主提供主诊断当前值（用于联动建议）</summary>
    public Func<string> MainDiagProvider { get; set; }

    /// <summary>选中某项后触发</summary>
    public event Action<AcRow> Picked;

    public SuggestBox()
    {
        InitializeComponent();
        _debounce.Tick += (_, _) => { _debounce.Stop(); Search(); };
        // TextChanged 已在 XAML 里挂过，这里不能再挂一次（重复触发会让防抖计时器反复重启）
        IsEnabledChanged += (_, _) => { if (!IsEnabled) Close(); };
    }

    // ==================================================================
    // 依赖属性
    // ==================================================================

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(SuggestBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnTextPropChanged));

    private static void OnTextPropChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (SuggestBox)d;
        var v = e.NewValue as string ?? "";
        if (box.Input.Text == v) return;
        var was = box._mute;
        box._mute = true;          // 外部赋值（清空 / 预填）不该弹出下拉
        try { box.Input.Text = v; }
        finally { box._mute = was; }
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(SuggestBox), new PropertyMetadata(""));

    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    /// <summary>"diag" 诊断 / "proc" 手术</summary>
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(string), typeof(SuggestBox), new PropertyMetadata("diag"));

    public string Kind
    {
        get => (string)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>多值输入（其他诊断 / 其他手术）：只替换最后一段</summary>
    public static readonly DependencyProperty MultiProperty = DependencyProperty.Register(
        nameof(Multi), typeof(bool), typeof(SuggestBox), new PropertyMetadata(false));

    public bool Multi
    {
        get => (bool)GetValue(MultiProperty);
        set => SetValue(MultiProperty, value);
    }

    public new void Focus() => Input.Focus();

    // ==================================================================
    // 事件
    // ==================================================================

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (Text != Input.Text) Text = Input.Text;
        UpdateEcho();
        _debounce.Stop();
        DiagWouldSearch = !_mute;
        if (_mute) return;
        _debounce.Start();
    }

    /// <summary>
    /// 第一次点进已有内容的单选输入框时全选：接着敲键就是整体替换。
    /// 少了这一步，点进去只是落个光标，用户得再点一次或手动删干净才输得进去。
    /// </summary>
    private void OnInputMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Multi || Input.IsKeyboardFocusWithin) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Input.IsKeyboardFocusWithin) Input.SelectAll();
        }), DispatcherPriority.Input);
    }

    /// <summary>右侧回显：当前编码对应的名称（只在单值、短文本、能查到名称时显示）。</summary>
    private void UpdateEcho()
    {
        var t = (Input.Text ?? "").Trim();
        var name = "";
        if (!Multi && t.Length > 0 && t.Length <= 16 && App.State != null)
            name = GrouperService.NameOf(App.State, t, Kind == "proc");
        Echo.Text = name ?? "";
        Echo.Visibility = string.IsNullOrEmpty(Echo.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // 聚焦且为空时直接带出主诊断联动建议
        if (string.IsNullOrWhiteSpace(Input.Text) && (Kind == "proc" || Multi)) LinkedSearch();
    }

    private void OnLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // 点击下拉项时 Input 会先失焦，故延迟判断鼠标是否已在下拉内
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!Pop.IsMouseOver) Close();
        }), DispatcherPriority.Input);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!Pop.IsOpen) return;
        switch (e.Key)
        {
            case Key.Down:
            case Key.Up:
                e.Handled = true;
                if (_rows.Count == 0) return;
                _idx = (_idx + (e.Key == Key.Down ? 1 : -1) + _rows.Count) % _rows.Count;
                ApplyHighlight();
                break;
            case Key.Enter:
                if (_idx >= 0 && _idx < _rows.Count) Pick(_rows[_idx]);
                e.Handled = true;
                break;
            case Key.Escape:
                e.Handled = true;
                Close();
                break;
        }
    }

    private void OnItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AcRow row)
        {
            e.Handled = true;
            Pick(row);
        }
    }

    // ==================================================================
    // 检索
    // ==================================================================

    private string QueryOf()
    {
        var t = Input.Text ?? "";
        if (!Multi) return t.Trim();
        var segs = t.Split(Separators, StringSplitOptions.None);
        return (segs.Length > 0 ? segs[^1] : "").Trim();
    }

    private void Search()
    {
        var q = QueryOf();
        if (q.Length == 0)
        {
            if (Kind == "proc" || Multi) { LinkedSearch(); return; }
            Close();
            return;
        }
        var S = App.State;
        _total = 0;
        if (Kind == "proc")
        {
            Show(GrouperService.SuggestProc(S, q, 30).Select(ToRow).ToList());
        }
        else
        {
            _total = GrouperService.SuggestDiagTotal(S, q);
            Show(GrouperService.SuggestDiag(S, q, 30).Select(ToRow).ToList());
        }
    }

    private void LinkedSearch()
    {
        var md = (MainDiagProvider?.Invoke() ?? "").Trim();
        if (md.Length == 0) { Close(); return; }
        var r = GrouperService.SuggestLinked(App.State, md);
        var src = Kind == "proc" ? r.Procs : r.OtherDiags;

        // 已录入的码不再重复建议
        var used = (Input.Text ?? "").Split(Separators, StringSplitOptions.None)
            .Select(s => s.Trim().ToUpperInvariant())
            .Where(s => s.Length > 0)
            .ToHashSet();
        _total = 0;
        Show(src.Where(it => !used.Contains((it.Code ?? "").ToUpperInvariant()))
            .Take(30).Select(ToRow).ToList());
    }

    private static AcRow ToRow(GrouperService.SuggestHit h) => new()
    {
        Code = h.Code,
        Name = h.Name,
        InsCode = h.InsCode,
        InsName = h.InsName,
        Gray = h.Gray,
        Linked = h.Linked,
        Dip = h.Dip,
    };

    private void Show(List<AcRow> rows)
    {
        if (rows.Count == 0) { Close(); return; }
        _rows = rows;
        _idx = 0;
        ApplyHighlight();
        Items.ItemsSource = null;
        Items.ItemsSource = _rows;
        Pop.Width = Math.Max(Input.ActualWidth, 280);
        Pop.IsOpen = true;

        // 中文名称检索命中较多时给出提示（对齐网页版 ac-more）
        var more = _total > _rows.Count;
        MoreHint.Text = more
            ? $"共匹配 {_total} 条，仅显示前 {_rows.Count} 条 —— 请继续输入更具体的关键词"
            : "";
        MoreHint.Visibility = more ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyHighlight()
    {
        for (var i = 0; i < _rows.Count; i++) _rows[i].IsOn = i == _idx;
        if (_idx >= 0 && _idx < _rows.Count &&
            Items.ItemContainerGenerator.ContainerFromIndex(_idx) is FrameworkElement fe)
        {
            fe.BringIntoView();
        }
    }

    private void Pick(AcRow row)
    {
        if (row == null) return;
        _debounce.Stop();
        var was = _mute;
        _mute = true;
        try
        {
            Close();
            ReplaceCurrent(row.Code);
            Picked?.Invoke(row);
        }
        finally { _mute = was; }

        Keyboard.Focus(Input);
        // 单选字段选中后全选：接着键入就是重新检索，不用先清空再点一次
        if (Multi) Input.CaretIndex = Input.Text?.Length ?? 0;
        else Input.SelectAll();
        UpdateEcho();
    }

    private void ReplaceCurrent(string code)
    {
        if (!Multi) { Text = code; Input.Text = code; return; }
        var parts = (Input.Text ?? "").Split(Separators, StringSplitOptions.None).ToList();
        parts[^1] = code;
        var kept = new List<string>();
        for (var i = 0; i < parts.Count; i++)
        {
            if (parts[i].Length == 0 && i == parts.Count - 1) continue;
            kept.Add(parts[i]);
        }
        var v = string.Join(";", kept) + ";";
        Text = v;
        Input.Text = v;
    }

    private void Close()
    {
        Pop.IsOpen = false;
        foreach (var r in _rows) r.IsOn = false;
        _idx = -1;
        _rows = new List<AcRow>();
    }

    // ==================================================================
    // 自检钩子（离屏自检没有消息循环，等不到 180ms 防抖，只能直接观察状态）
    // ==================================================================

    /// <summary>最近一次文本变化是否会真的触发检索（程序性回填时为 false）。</summary>
    internal bool DiagWouldSearch { get; private set; } = true;

    internal bool DiagPopupOpen => Pop.IsOpen;

    /// <summary>右侧名称回显的当前内容（未显示时为空串）。</summary>
    internal string DiagEcho => Echo.Visibility == Visibility.Visible ? (Echo.Text ?? "") : "";

    /// <summary>水印相对输入框的偏移（同模板同 Padding 时应为 0）。</summary>
    internal Point DiagWatermarkOffset => Ph.TranslatePoint(new Point(0, 0), Input);

    internal bool DiagWatermarkIsTextBox => Ph is TextBox;

    internal double DiagWatermarkPadding => Ph.Padding.Left;

    /// <summary>模拟用户键入（不静音，会走正常检索链路）。</summary>
    internal void DiagType(string text) => Input.Text = text ?? "";

    /// <summary>模拟从下拉里选一项（编码直接给，避免依赖数据库）。</summary>
    internal void DiagPick(string code) => Pick(new AcRow { Code = code });
}
