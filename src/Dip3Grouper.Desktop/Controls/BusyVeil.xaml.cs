using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Dip3GrouperApp.Desktop.Controls;

/// <summary>
/// 页面级「正在取数 / 正在换视图」反馈：**整页渐显**，不再是中间弹一张进度卡。
///
/// <para>为什么改：分段页签、查询、翻页点下去要等 150~400ms（主线程同步重建下拉、重排 50 行），
/// 这段时间界面完全不刷新。原先的做法是弹一张白卡把整页蒙住，再放沙漏和进度条 —— 反馈是有了，
/// 但它是「多出来的一层东西」：挡住正在换的内容，还要为了「最短停留」硬留 180ms，
/// 等于给一次点击叠了两段等待。用户的诉求很直接：要的是**页面在换**，不是**弹窗在转**。</para>
///
/// <para>现在的做法是三步，全部作用在页面自身：</para>
/// <list type="number">
/// <item>点下去立刻把同级的页面内容压到 <see cref="DimOpacity"/>（文字对比度掉下去，像换了半拍气）；</item>
/// <item>顶部 3px 进度线按预估时长推到 92%（超过预估就改成呼吸脉冲，表示「还在干」）；</item>
/// <item>数据到位后把内容用 220ms <c>CubicEase(EaseOut)</c> 淡回 1.0 —— 这就是「渐显」。</item>
/// </list>
///
/// <para>为什么淡出量的是「文字对比度」而不是整页亮度：浅色主题下白底压透明度，整体亮度几乎不动
/// （白 255 压到 38% 叠在 #F7F7F9 上还是 250 上下），肉眼看到的其实是**黑字变浅灰**。
/// 自检因此统计「深色像素数量」，而不是平均亮度 —— 后者在这里几乎量不出差别。</para>
///
/// <para>用法（放在页面根 Grid 里，与要淡出的内容同级）：</para>
/// <code>
/// &lt;ctl:BusyVeil x:Name="Busy" Grid.Row="0" Grid.RowSpan="4" Panel.ZIndex="50" /&gt;
///
/// Busy.Show("正在载入旧版物价对码表…", "整理状态与对应新编码", 280);
/// Dispatcher.BeginInvoke(new Action(() =&gt; { Work(); Busy.HideAfter(); }),
///                        DispatcherPriority.Render);
/// </code>
///
/// <para>为什么仍要 <c>DispatcherPriority.Render</c>：它排在所有渲染之后，等回调执行时
/// 「内容已经变淡」这一帧才真正画在屏幕上，这时再让主线程去忙，用户看到的是「页面暗下去了，活正在干」。</para>
///
/// <para>文案（caption / hint）不再显示在页面上：渐显模式下页面上任何浮层都会把界面拽回「弹窗」，
/// 而一张只写「正在载入…」的浮条比进度线更像噪音。文案保留为**可读状态**，
/// 供 <c>--veil-trace</c> 与自检核对「亮起的是不是对应动作」。完成后的结果文案仍由各页状态栏照常输出。</para>
/// </summary>
public partial class BusyVeil : UserControl
{
    /// <summary>最短停留（ms）：淡出至少要让人看见一次，否则一闪而过更像闪屏故障。</summary>
    public const int DefaultMinMs = 180;

    /// <summary>淡回时长（ms）。220ms 是「能看出在过渡」又不拖沓的上限。</summary>
    public const int FadeInMs = 220;

    /// <summary>取数期间页面内容的不透明度。再低会把表格压成一片糊。</summary>
    public const double DimOpacity = 0.38;

    /// <summary>定量段最多推进到 92%：留出余量，避免「100% 了还没好」的观感。</summary>
    private const double EstimateCeiling = 0.92;

    /// <summary>定量段封顶时长（ms）：再慢的活也不让进度线几乎不动。</summary>
    private const int EstimateCapMs = 900;

    private long _shownAt;
    private DispatcherTimer _timer;

    /// <summary>超时切「呼吸」的定时器（定量段跑完那一刻触发）。</summary>
    private DispatcherTimer _spill;

    private Stopwatch _work = new();
    private int _estimate;
    private bool _spilled;

    /// <summary>兄弟元素的**真实原不透明度**。只记一次，之后所有淡入淡出都以它为准。</summary>
    private readonly Dictionary<UIElement, double> _orig = new();

    /// <summary>当前处于「已压淡」状态的兄弟元素。</summary>
    private readonly HashSet<UIElement> _dimmed = new();

    /// <summary>淡回的兜底定时器（窗口最小化时动画回调可能不来）。</summary>
    private DispatcherTimer _fadeGuard;

    /// <summary>
    /// 全局开关：自检与离屏截图流程会把它置 false。
    /// 渐显会把整页压淡，截图与「确定性渲染」都不希望这层效果出现。
    /// </summary>
    public static bool Enabled = true;

    public BusyVeil()
    {
        InitializeComponent();
        // 页面被卸载（切走 / 关窗）时若还压着淡，兄弟元素会永远停在 0.38 —— 必须无条件还原
        Unloaded += (_, _) =>
        {
            _timer?.Stop();
            _spill?.Stop();
            _fadeGuard?.Stop();
            _work.Stop();
            FadeBack(animate: false);
            _orig.Clear();
        };
    }

    public bool IsBusy => Visibility == Visibility.Visible;

    /// <summary>当前文案（不在界面上渲染，供诊断 / 自检核对「是哪一处入口触发的」）。</summary>
    public string CaptionText { get; private set; } = "";

    /// <summary>当前提示行（同上）。</summary>
    public string HintText { get; private set; } = "";

    /// <summary>进度线当前所示百分比（0~100；呼吸阶段返回 -1）。</summary>
    public int PercentShown => _spilled ? -1 : (int)Math.Round(BarScale.ScaleX * 100);

    /// <summary>进度线是否已切到「还在干」的呼吸态（诊断用）。</summary>
    public bool Spilled => _spilled;

    /// <summary>本次压淡了几个兄弟元素（诊断用：0 表示控件没挂到内容同级的 Panel 里）。</summary>
    internal int DimmedCount => _dimmed.Count;

    /// <summary>压淡时的目标不透明度（诊断用）。</summary>
    internal static double TargetOpacity => DimOpacity;

    /// <summary>亮起渐显。caption 写「在等什么」，hint 补充「正在做什么」。</summary>
    /// <param name="estimateMs">预估耗时（ms）。≤0 表示无法预估，直接走呼吸态。</param>
    public void Show(string caption, string hint = "", int estimateMs = 0)
    {
        CaptionText = caption ?? "";
        HintText = hint ?? "";

        _shownAt = Environment.TickCount64;
        _work.Restart();
        _spilled = false;
        _estimate = Math.Clamp(estimateMs, 0, EstimateCapMs);

        if (!Enabled)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        _timer?.Stop();
        _spill?.Stop();
        Visibility = Visibility.Visible;
        DimSiblings();
        ResetBar();
        StartEstimate();
    }

    /// <summary>立刻收起：还原内容不透明度，摘掉所有定时器与动画（不摘会一直空转占 CPU）。</summary>
    public void Hide()
    {
        _timer?.Stop();
        _spill?.Stop();
        _work.Stop();
        ResetBar();
        Visibility = Visibility.Collapsed;
        FadeBack(animate: true);
    }

    /// <summary>
    /// 到「最短停留」时间就收起；还没到就补一个定时器再收。
    /// 淡出至少要让人看清一次 —— 哪怕活只干了 30ms，也不能让页面闪一下又弹回来。
    /// </summary>
    public void HideAfter(int minMs = DefaultMinMs)
    {
        if (Visibility != Visibility.Visible) { Hide(); return; }

        var rest = minMs - (int)(Environment.TickCount64 - _shownAt);
        if (rest <= 0) { Hide(); return; }

        _timer?.Stop();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(rest) };
        _timer.Tick += (_, _) => { _timer.Stop(); Hide(); };
        _timer.Start();
    }

    // ==================================================================
    // 渐显：压淡 / 淡回
    // ==================================================================

    /// <summary>
    /// 把「与遮罩同级的页面内容」整体压淡。
    /// <para>为什么按同级元素找，而不是要求各页显式传一个内容宿主：遮罩本来就挂在页面根 Grid 里，
    /// 兄弟就是全部可见内容（页签、工具栏、表格、分页条）。这样四个页面一行 XAML 都不用改。</para>
    /// <para>先摘掉残留动画再取基值：快速连点两次页签时，上一次的淡回可能正跑到一半，
    /// 直接读 <c>Opacity</c> 会拿到动画中间值（0.6 之类），还原时就把页面永久压暗了。</para>
    /// </summary>
    private void DimSiblings()
    {
        _fadeGuard?.Stop();

        if (Parent is not Panel panel) return;
        foreach (UIElement child in panel.Children)
        {
            if (ReferenceEquals(child, this)) continue;
            // 摘下上一次还没跑完的淡回动画（快速连点两次页签时会撞上）
            child.BeginAnimation(UIElement.OpacityProperty, null);

            // 原值只在这里记一次：每次淡出都重新读一遍的话，
            // 第二次读到的就是上一轮压下去的 0.38，页面会越点越灰、再也回不来。
            if (!_orig.TryGetValue(child, out var baseOp))
            {
                baseOp = child.Opacity;
                _orig[child] = baseOp;
            }
            if (baseOp <= 0.01) continue;      // 本来就看不见的兄弟（备用表格等）不参与，也就不用还原
            _dimmed.Add(child);
            child.Opacity = Math.Min(baseOp, DimOpacity);
        }
    }

    /// <summary>
    /// 淡回原不透明度。
    /// <para>有没有渲染循环决定「用动画还是直接写值」：离屏截图 / 自检里没有消息循环，
    /// 动画时钟不推进，挂上动画就永远停在 0.38 —— 截图会是一片灰，自检会误判「没恢复」。
    /// 所以没有 <see cref="PresentationSource"/> 时直接写值。</para>
    /// </summary>
    private void FadeBack(bool animate)
    {
        if (_dimmed.Count == 0) return;

        var pending = new List<UIElement>(_dimmed);
        _dimmed.Clear();
        _fadeGuard?.Stop();

        var animateNow = animate && PresentationSource.FromVisual(this) != null;
        foreach (var el in pending)
        {
            var target = _orig.TryGetValue(el, out var v) ? v : 1.0;
            if (animateNow)
            {
                var a = new DoubleAnimation(target, TimeSpan.FromMilliseconds(FadeInMs))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    // ⚠ 必须是 Stop 而不是默认的 HoldEnd：HoldEnd 只是把 1.0「压在」属性上，
                    // 动画一摘属性立刻弹回底色值（0.38）。Stop + 回调里写底色，值才真正落回元素上。
                    FillBehavior = FillBehavior.Stop,
                };
                a.Completed += (_, _) => Settle(el, target);
                el.BeginAnimation(UIElement.OpacityProperty, a);
            }
            else
            {
                Settle(el, target);
            }
        }

        if (!animateNow) return;

        // 窗口最小化 / 无渲染帧时 Completed 不一定来 —— 兜一个定时器补写底色值，
        // 否则用户切回来会看到一页发灰的表格（比没有渐显糟糕得多）。
        _fadeGuard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FadeInMs + 160) };
        _fadeGuard.Tick += (_, _) =>
        {
            _fadeGuard.Stop();
            foreach (var el in pending)
                Settle(el, _orig.TryGetValue(el, out var v) ? v : 1.0);
        };
        _fadeGuard.Start();
    }

    /// <summary>把底色值真正写回元素。已经被新一轮压淡的元素要放过（否则会把它顶回不透明）。</summary>
    private void Settle(UIElement el, double target)
    {
        if (_dimmed.Contains(el)) return;
        el.BeginAnimation(UIElement.OpacityProperty, null);
        el.Opacity = target;
    }

    /// <summary>诊断 / 自检专用：跳过动画立刻还原（离屏路径截图前后都要用）。</summary>
    internal void FinishNow()
    {
        _timer?.Stop();
        _spill?.Stop();
        _work.Stop();
        ResetBar();
        Visibility = Visibility.Collapsed;
        FadeBack(animate: false);
    }

    // ==================================================================
    // 顶部进度线：定量 → 呼吸
    // ==================================================================

    private void ResetBar()
    {
        BarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        BarScale.ScaleX = 0;
        Bar.BeginAnimation(OpacityProperty, null);
        Bar.Opacity = 1;
    }

    /// <summary>
    /// 第一段：按预估时长在 <c>_estimate</c> 内推进到 92%，用 <c>QuadraticEase(EaseOut)</c>
    /// —— 前快后慢，符合「多数活比预期快」的心理预期。
    /// 预估时长耗尽仍未结束 → <see cref="Spill"/>。
    /// </summary>
    private void StartEstimate()
    {
        if (_estimate <= 0) { Spill(); return; }

        var anim = new DoubleAnimation(0.04, EstimateCeiling, TimeSpan.FromMilliseconds(_estimate))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        BarScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);

        _spill?.Stop();
        _spill = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_estimate + 60) };
        _spill.Tick += (_, _) => { _spill.Stop(); Spill(); };
        _spill.Start();
    }

    /// <summary>
    /// 第二段：预估时长跑完还没好 —— 线停在 92% 改成呼吸（1.0 ↔ 0.35 往复）。
    /// <para>为什么不再换一条往复滑动的滑块：那是「进度条」的做法，在只有 3px 的页面级细线上
    /// 看起来像是线断了。呼吸的不透明度变化更安静，且明确表达「还在动，没卡死」。</para>
    /// </summary>
    private void Spill()
    {
        if (_spilled) return;
        _spilled = true;

        BarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        BarScale.ScaleX = EstimateCeiling;

        var pulse = new DoubleAnimation(1.0, 0.35, TimeSpan.FromMilliseconds(780))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            AutoReverse = true,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Bar.BeginAnimation(OpacityProperty, pulse);
    }

    /// <summary>
    /// 诊断专用（离屏截图 <c>--shot-page</c> 路径）：把渐显状态定格在一个可见的中间姿态。
    /// 离屏渲染不跑消息循环 —— 动画时钟不推进，<c>BeginAnimation</c> 刚挂上时当前帧仍取基值，
    /// 直接截会得到「页面完全没变淡、进度线 0」的假象。
    /// 这里摘掉时钟再直接写值，截图才看得出「内容压淡 + 顶部线推进到 62%」。
    /// </summary>
    /// <param name="spill">true 定格「超过预估、线在呼吸」的姿态。</param>
    internal void DiagFreeze(string caption, string hint, bool spill = false)
    {
        // 截图路径全局关掉了渐显（否则所有页面截图都会被压淡），这里单次打开
        var was = Enabled;
        Enabled = true;
        Show(caption, hint, 400);
        Enabled = was;

        _spill?.Stop();
        if (spill)
        {
            Spill();
            _spill?.Stop();
            Bar.BeginAnimation(OpacityProperty, null);
            Bar.Opacity = 0.62;               // 呼吸到中间那一帧
        }
        else
        {
            BarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            BarScale.ScaleX = 0.62;
        }
        UpdateLayout();
    }
}
