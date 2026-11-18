using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Dip3GrouperApp.Desktop.Controls;

/// <summary>
/// 分段按钮的「滑动白块」驱动（样式见 Themes/Controls.xaml 的 Segment.Bar / Segment.Slide）。
///
/// 为什么要一层附加属性：白块要能**滑动**，就不能由每个 RadioButton 自己画背景
/// （「谁被选中谁变白」只能瞬切）。于是把白块做成容器里一张独立的 Border，
/// 位置与宽度由这里算出后跑动画。XAML 侧只写两个附加属性即可：
///
/// <code>
/// &lt;Border Style="{StaticResource Segment.Bar}" local:Segment.Track="True"&gt;
///   &lt;Grid&gt;
///     &lt;Border Style="{StaticResource Segment.Slide}" /&gt;
///     &lt;StackPanel Orientation="Horizontal"&gt; ... RadioButton ... &lt;/StackPanel&gt;
///   &lt;/Grid&gt;
/// &lt;/Border&gt;
/// </code>
///
/// 选中项自动取容器内第一个 IsChecked 的 RadioButton；集合里任何一个切换选中，
/// 都会重新排布。不需要在后台代码里额外调用。
/// </summary>
public static class Segment
{
    /// <summary>动画时长。180ms 是 Fluent「标准」档，比 250ms 利落、比 120ms 不突兀。</summary>
    internal const double SlideMs = 180;



    /// <summary>
    /// 动画时长的外部覆盖（只给诊断用，正常运行为 0 = 用 <see cref="SlideMs"/>）。
    /// 180ms 的动画用 17ms 粒度的截图采样只能抓到一两帧中间态，很难证明「确实在补间」；
    /// 诊断模式把它拉到 3000ms，就能逐帧看清白块是如何滑过去的。
    /// </summary>
    internal static double SlideMsOverride;


    /// <summary>白块相对标签区的内缩：左右各 2px，避免圆角被轨道圆角切掉一点。</summary>
    private const double Inset = 2;


    /// <summary>
    /// 待启动滑动的轨道。Checked 时立刻钉住起点并入队；随后每一次 LayoutUpdated
    /// 都先看标签区是否已稳定（字重切换引起的重新布局结束），稳定了才真正启动动画。
    ///
    /// <para>为什么不在 Checked 里直接开动画：那一刻标签还是旧宽度。也不靠 Dispatcher
    /// 排队：实测 Loaded 优先级要等 300ms 才轮到，期间 LayoutUpdated 已经把白块
    /// 「按新布局一次性搬到位」，动画再无距离可滑 —— 这就是先前表现为硬切的真因。</para>
    /// </summary>
    private static readonly System.Collections.Generic.HashSet<Border> Pending = new();

    /// <summary>每条轨道「本帧量到的标签总宽」与「上一帧量到的」，用于判断字重引起的重排是否收敛。</summary>
    private static readonly System.Collections.Generic.Dictionary<Border, double> LastSpan = new();

    /// <summary>待滑动轨道已等待的布局轮数（给一次余量，吃掉字重换位那轮的抖动）。</summary>
    private static readonly System.Collections.Generic.Dictionary<Border, int> Waited = new();
    /// <summary>诊断：最近一次 <see cref="Measure"/> 拿到的动画基值（--seg-demo 用）。</summary>
    public static double Base = double.NaN;

    /// <summary>
    /// 诊断：排布过程全量留痕（<c>--seg-trace</c> 用）。只在诊断时打开，
    /// 正常运行时为 null，一句判断即返回，不影响布局性能。
    /// </summary>
    internal static Action<string> Trace;

    /// <summary>诊断：把一条排布过程写进 <see cref="Trace"/>（未开启时零成本）。</summary>
    private static void T(string msg) => Trace?.Invoke(msg);

    /// <summary>挂在 Segment.Bar 上，开启白块驱动。值无所谓，写 True 只为可读。</summary>
    public static readonly DependencyProperty TrackProperty =
        DependencyProperty.RegisterAttached("Track", typeof(bool), typeof(Segment),
            new PropertyMetadata(false, OnTrackChanged));

    public static bool GetTrack(DependencyObject d) => (bool)d.GetValue(TrackProperty);
    public static void SetTrack(DependencyObject d, bool value) => d.SetValue(TrackProperty, value);

    private static void OnTrackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border bar || e.NewValue is not true) return;

        // ⚠ 必须 handledEventsToo: true。RadioButton 的 Checked 事件在它自己的
        //   ToggleButton 控件模板里已经被标记为 Handled（模板内的视觉状态切换用掉了），
        //   普通 AddHandler 挂在祖先上**收不到**——症状是鼠标点得动、文字变色，白块不动，
        //   而且极难察觉（没有异常，只是回调永远不触发）。
        bar.AddHandler(ToggleButton.CheckedEvent,
            new RoutedEventHandler(OnAnyChecked), handledEventsToo: true);

        // Loaded 覆盖正常运行路径；IsVisibleChanged 兜住「离屏渲染 / 首次布局已完成」的情形
        //（--shot-page 走的是 Measure/Arrange/UpdateLayout 手动布局，Loaded 不会触发，
        //  只靠 Loaded 的话截图里白块根本不会出现）。
        bar.Loaded += (_, _) => Schedule(bar, animate: false);
        bar.IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true) Schedule(bar, animate: false);
        };
        // 尺寸变化（窗口缩放 / 字体或换行导致标签变宽）白块必须重算，否则会停在旧位置。
        // 用 LayoutUpdated 而不是 SizeChanged：后者在纯布局期偶尔不触发。
        // ⚠ LayoutUpdated 是**每个元素、每一帧**都触发，Arrange 里必须先做
        //   「位置没变就直接返回」，否则布局帧会被自己的动画不断喂新帧（CPU 空转）。
        // ⚠ 也不能直接改基值：LayoutUpdated 会在动画播放期间触发（Checked 改了文字字重
        //   → 重新布局），同步改 Margin 基值等于把动画一帧掐死 → 看起来就是硬切。
        //   第一参数是标签区是否还在动（字重切换需要重新布局一帧），第二参数是
        //   「基值已被动画改过」——后者成立时只校正不重启动画。
        bar.LayoutUpdated += (_, _) =>
        {
            if (!bar.IsVisible || bar.ActualWidth <= 1) return;


            if (Pending.Contains(bar))
            {
                // 等两轮布局：Checked 换了字重（Normal→SemiBold），标签宽度会变一两个像素，
                // 那一轮量到的终点还不是最终位置。等宽度连续两轮不变再启动，白块才不会
                // 滑到「差一像素」的地方再被 LayoutUpdated 拽一下。
                var span = LabelsSpan(bar);
                LastSpan.TryGetValue(bar, out var prev);
                LastSpan[bar] = span;
                Waited.TryGetValue(bar, out var n);
                Waited[bar] = n + 1;

                if (n < 1 && Math.Abs(span - prev) > 0.05) { T($"布局：等字重收敛 span={span:0.0} prev={prev:0.0}"); return; }

                if (n < 1) { T($"布局：钉起点(n={n}) span={span:0.0}"); Arrange(bar, animate: false, force: true, keepSliding: true); return; }

                Pending.Remove(bar);
                Waited.Remove(bar);

                // 启动补间前把「白块当前真实位置」打出来：若这里就已是终点，
                // 说明起点在某一步被提前搬走了，动画自然无从滑起。
                var c0 = slideOf(bar);
                if (c0 != null)
                {
                    var cm = c0.GetValue(FrameworkElement.MarginProperty) is Thickness t ? t.Left : double.NaN;
                    var cb = c0.GetAnimationBaseValue(FrameworkElement.MarginProperty) is Thickness tb ? tb.Left : double.NaN;
                    T($"布局：启动补间 cur={cm:0.0} base={cb:0.0} span={span:0.0}");
                }
                Arrange(bar, animate: true, force: false);
                return;
            }

            Arrange(bar, animate: false, force: false);
        };
    }

    private static void OnAnyChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not Border bar) return;

        T($"Checked 事件：当前选中={CheckedText(bar)}");

        // 立刻按**旧布局**把起点钉死在「原选中项」上：Checked 一返回，字重就换了，
        // 布局随后更新；若等到那时才读起点，读到的已是终点，动画没有距离可走。
        Arrange(bar, animate: false, force: false, want: PrevOf(bar, e));

        // 动画等布局稳定后由 LayoutUpdated 启动（见 Pending 说明）。
        Pending.Add(bar);
        Waited[bar] = 0;
        LastSpan.TryGetValue(bar, out var l0);
        LastSpan[bar] = l0;
    }

    /// <summary>
    /// 事件发生**之前**选中的那一项（= 白块当前的落点）。
    /// <c>RadioButton.Checked</c> 触发时源项与同组旧项都已改好 IsChecked，
    /// 所以不能靠 IsChecked 反推；改为「白块现在压在谁的横向范围里」。
    /// 找不到（首次布局）就返回 null，等于不钉起点。
    /// </summary>
    private static RadioButton PrevOf(Border bar, RoutedEventArgs e)
    {
        if (bar.Child is not Panel host || host.Children.Count < 2) return null;
        if (host.Children[0] is not Border slide) return null;
        if (host.Children[1] is not Panel labels) return null;
        var x = slide.GetValue(FrameworkElement.MarginProperty) is Thickness m ? m.Left : slide.Margin.Left;
        var probe = x - Inset + 1;
        var acc = 0.0;
        foreach (var c in labels.Children)
        {
            if (c is not FrameworkElement fe) continue;
            if (probe >= acc && probe < acc + fe.ActualWidth) return c as RadioButton;
            acc += fe.ActualWidth;
        }
        return null;
    }

    /// <summary>标签区所有标签的实际总宽（字重切换会让它变一两个像素，是判断「是否还在重排」的信号）。</summary>
    private static double LabelsSpan(Border bar)
    {
        if (bar.Child is not Panel host || host.Children.Count < 2 || host.Children[1] is not Panel labels)
            return -1;
        var sum = 0.0;
        foreach (var c in labels.Children)
            if (c is FrameworkElement fe) sum += fe.ActualWidth;
        return sum;
    }

    /// <summary>轨道里的白块（约定：容器第 0 个子元素）。诊断留痕用。</summary>
    private static Border slideOf(Border bar)
    {
        if (bar?.Child is Panel host && host.Children.Count > 0 && host.Children[0] is Border s) return s;
        return null;
    }

    /// <summary>诊断留痕：当前选中项的文字，用来确认 Checked 事件与真实选中是否同步。</summary>
    private static string CheckedText(Border bar)
    {
        if (bar?.Child is not Panel host || host.Children.Count < 2 || host.Children[1] is not Panel labels)
            return "(结构不符)";
        var names = new System.Collections.Generic.List<string>();
        foreach (var c in labels.Children)
            if (c is RadioButton rb && rb.IsChecked == true)
                names.Add((rb.Content as string) ?? "?");
        return names.Count == 0 ? "(无)" : string.Join("/", names);
    }

    /// <summary>诊断留痕：目标标签的文字。</summary>
    private static string TargetName(RadioButton rb) => (rb?.Content as string) ?? "(?)";

    /// <summary>
    /// 用 Loaded 派发而不是直接排布：Checked 事件可能在布局完成前触发，
    /// 那时 ActualWidth 全是 0，白块会落成一条 0 宽的线。
    ///
    /// 优先级用 Loaded（不是 Input）：输入优先级会在**每一帧**动画里重入，
    /// Dispatcher 队列被动画帧塞满，界面反而卡顿。
    /// </summary>
    private static void Schedule(Border bar, bool animate)
    {
        bar.Dispatcher.BeginInvoke(new Action(() => Arrange(bar, animate, force: false)),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 同步排布（不排队、无动画）。给「不跑消息循环的渲染」用：
    /// <c>--shot-page</c> 走 Measure/Arrange/UpdateLayout 手动布局，Dispatcher 从不泵消息，
    /// <see cref="Schedule"/> 排进去的回调永远不执行 —— 截图里白块就会整块消失。
    /// 页面在 EnsureLoaded 之后调一次即可。
    /// </summary>
    public static void Sync(Border bar)
    {
        if (bar == null) return;
        Arrange(bar, animate: false, force: false);
    }

    /// <summary>按视觉树找到容器内所有分段轨道并同步排布。</summary>
    public static void SyncAll(DependencyObject root)
    {
        if (root == null) return;
        if (root is Border b && GetTrack(b)) Sync(b);
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++) SyncAll(VisualTreeHelper.GetChild(root, i));
    }

    /// <summary>按视觉树找到容器内**第一条**分段轨道（诊断 / 自检用）。</summary>
    public static Border Find(DependencyObject root)
    {
        if (root == null) return null;
        if (root is Border b && GetTrack(b)) return b;
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var hit = Find(VisualTreeHelper.GetChild(root, i));
            if (hit != null) return hit;
        }
        return null;
    }

    /// <summary>
    /// 量出白块当前的位置（左边界 / 宽度，坐标系与容器内容区一致）。
    /// 诊断专用：动画若真的在补间，连续采样会看到 Left 落在一串中间值上；
    /// 若是硬切，前后只会出现「起点 / 终点」两个值。
    ///
    /// 走 <c>GetValue</c>：动画活跃时它返回**当前时刻的插值结果**（AnimationStorage 不缓存，
    /// 每次取值按时钟现在的时刻重算），所以不需要有人 tick 渲染线程也能看到中间态。
    /// </summary>
    public static bool Measure(Border bar, out double left, out double width)
    {
        left = width = double.NaN;
        if (bar?.Child is not Panel host || host.Children.Count < 1) return false;
        if (host.Children[0] is not Border slide) return false;
        left = slide.GetValue(FrameworkElement.MarginProperty) is Thickness m ? m.Left : slide.Margin.Left;
        width = slide.GetValue(FrameworkElement.WidthProperty) is double w && !double.IsNaN(w) ? w : slide.Width;
        // 顺带把「时钟求值」也带出来：GetValue 在时钟没被 tick 时返回基值，看不出补间
        if (slide.GetAnimationBaseValue(FrameworkElement.MarginProperty) is Thickness b) Base = b.Left;
        return true;
    }

    /// <summary>
    /// 同步触发一次「带动画」的排布（诊断 / 自检用）：正常运行时由 Checked 事件排队触发，
    /// 不泵消息的环境（离屏渲染、控制台探针）拿不到，这里给一个直接入口。
    /// </summary>
    public static void ArrangeNow(Border bar)
    {
        if (bar == null) return;
        Arrange(bar, animate: true, force: false);
    }

    /// <summary>
    /// <paramref name="animate"/>：是否跑补间（Checked 切换才需要）。
    /// <paramref name="force"/>：忽略「已在位」短路，强制按当前布局落一次位（不插值）。
    ///
    /// <para>⚠ 判断「已在位」和「首帧无动画」**必须用动画当前的插值**（<c>GetValue</c>），
    /// 不能用 <c>slide.Margin</c>（那是基值）。动画一启动基值就已经是终点，
    /// 用基值判断会让每一帧 LayoutUpdated 都以为「已经在位」而把时钟摘掉，
    /// 以及第二帧以为「白块 ActualWidth 还是 0」而重设基值 —— 两种都会把动画掐死在起点，
    /// 表现就是「点了之后白块直接跳过去，没有滑动」。</para>
    /// </summary>
    private static void Arrange(Border bar, bool animate, bool force, RadioButton want = null,
        bool keepSliding = false)
    {
        if (bar.ActualWidth < 1) return;   // 尚未布局
        if (bar.Child is not Panel host) return;

        // 约定：第 0 个是白块（Segment.Slide），第 1 个是标签区
        if (host.Children.Count < 2) return;
        if (host.Children[0] is not Border slide) return;
        if (host.Children[1] is not Panel labels) return;

        var target = want;
        if (target == null)
        {
            foreach (var child in labels.Children)
            {
                if (child is RadioButton rb && rb.IsChecked == true) { target = rb; break; }
            }
        }
        if (target == null) return;        // 还没选中任何一项


        var x = 0.0;
        for (var i = 0; i < labels.Children.Count; i++)
        {
            if (ReferenceEquals(labels.Children[i], target)) break;
            if (labels.Children[i] is FrameworkElement fe) x += fe.ActualWidth;
        }

        var left = x + Inset;
        var width = Math.Max(0, target.ActualWidth - Inset * 2);

        if (keepSliding) return;   // 标签还在动：白块原地不动，等布局稳定后一次性滑动

        var cur = slide.GetValue(FrameworkElement.MarginProperty) is Thickness cm ? cm.Left : slide.Margin.Left;
        var curW = slide.GetValue(FrameworkElement.WidthProperty) is double cw && !double.IsNaN(cw)
            ? cw : slide.Width;

        T($"排布 target={TargetName(target)} left={left:0.0} w={width:0.0} cur={cur:0.0} curW={curW:0.0} animate={animate} force={force}");

        // 已经在位就立刻返回：这是 LayoutUpdated 每帧回调的主要出口，
        // 少这一句会让「排布 → 触发布局 → 再排布」自激，白块每帧重设属性、CPU 空转。
        var placed = Math.Abs(cur - left) < 0.5 && Math.Abs(curW - width) < 0.5;

        // ⚠ 已经在滑向同一目标时，绝不调用 SlideDone —— 它会把动画时钟摘掉。
        //   LayoutUpdated 每帧都来一次，而这个判断用的是**插值后的当前值**：
        //   滑到一半时 cur≠target，本来就不该进 placed 分支；
        //   但若动画已收尾（基值=目标，时钟还没摘），重复摘一次也无害。
        if (!force && placed)
        {
            T("  已在位 → 摘时钟返回");
            SlideDone(slide);
            return;
        }

        // 首次（或未开动画 / 布局未成形）：直接落位，不做过渡 —— 页面刚打开就滑一下很怪
        var noTween = !animate || width < 0.5 || double.IsNaN(curW);
        if (noTween || force)
        {
            T($"  落位(无补间) left={left:0.0} w={width:0.0} animate={animate} force={force} cur={cur:0.0} curW={curW:0.0}");
            slide.BeginAnimation(FrameworkElement.MarginProperty, null);
            slide.BeginAnimation(FrameworkElement.WidthProperty, null);
            slide.Margin = new Thickness(left, Inset, 0, Inset);
            slide.Width = width;
            return;
        }

        // 同一目标已经在滑：别重复挂时钟（LayoutUpdated 期间可能又触发一次 Checked 排布）
        if (slide.HasAnimatedProperties && Math.Abs(slide.Margin.Left - left) < 0.5 &&
            Math.Abs(slide.Width - width) < 0.5)
        {
            T("  已在滑向同一目标 → 不重挂");
            return;
        }

        // ⚠ 只改 Margin.Left：用 ThicknessAnimation 整体插值会让左右边距同时动，
        //   中途宽度「呼吸」。宽度单独用 DoubleAnimation 补间。
        var ease = new Duration(TimeSpan.FromMilliseconds(SlideMsOverride > 0 ? SlideMsOverride : SlideMs));

        T($"  ** 启动补间 From={cur:0.0} → To={left:0.0}（时长 {ease.TimeSpan.TotalMilliseconds:0}ms）");
        // ⚠ 必须显式给 **From**。Timeline 默认从属性基值起算，而下面为了让
        //   FillBehavior=Stop 收尾不闪回，会先把基值赋成终点 —— 那样动画
        //   From=To=终点，任何时刻取出来都是终点，肉眼看就是「没有滑动」。
        //   （这一条是本次排查的最后一个坑：anim=True 却 cur=终点。）
        var tm = new ThicknessAnimation
        {
            To = new Thickness(left, Inset, 0, Inset),
            From = new Thickness(cur, Inset, 0, Inset),
            Duration = ease,
            // FillBehavior 用 HoldEnd（默认），收尾后属性停在终点；
            // Completed 里再显式摘时钟并写基值，避免下次判定「还在滑」。

            DecelerationRatio = 0.65,
        };
        var wm = new DoubleAnimation
        {
            To = width,
            From = curW,
            Duration = ease,
            DecelerationRatio = 0.65,
        };

        // FillBehavior=Stop：动画结束后属性回到「未动画值」，必须同时把基值改掉，
        // 否则下一帧会闪回旧位置（经典 WPF 坑）。
        slide.Margin = new Thickness(left, Inset, 0, Inset);
        slide.BeginAnimation(FrameworkElement.MarginProperty, tm);

        slide.Width = width;
        slide.BeginAnimation(FrameworkElement.WidthProperty, wm);

        // 动画结束把时钟摘掉（见 SlideDone 说明）。只摘一次，避免 Completed 反复排队。
        tm.Completed += (_, _) => SlideDone(slide);
    }

    /// <summary>
    /// 动画走完把动画时钟摘掉。FillBehavior=Stop 会保留 AnimationClock 记录
    /// （<c>HasAnimatedProperties</c> 恒为 true），但属性值已落回基值；
    /// 不及时摘掉的话，后续判断容易误以为「还在滑」，另外时钟也占内存。
    /// 摘之前必须确认基值已是目标位（上面已赋值），否则会闪回旧位置。
    /// </summary>
    private static void SlideDone(Border slide)
    {
        slide.BeginAnimation(FrameworkElement.MarginProperty, null);
        slide.BeginAnimation(FrameworkElement.WidthProperty, null);
    }
}
