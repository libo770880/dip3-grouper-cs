using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Animation;
using Dip3Grouper;

namespace Dip3GrouperApp.Desktop;

/// <summary>
/// 启动画面：品牌区 + 进度条 + 阶段文案 + 百分比。
///
/// 由 <see cref="App"/> 驱动：先 <c>Show()</c> 让它真正上屏渲染，再在后台线程跑
/// <see cref="AppState.Init"/>，进度经 Dispatcher 回传；数据就绪且满足最短停留时长后，
/// 先显示主窗口再关闭本窗口（顺序不能反，否则 ShutdownMode=OnLastWindowClose 会直接退出进程）。
/// </summary>
public partial class SplashWindow : Window
{
    /// <summary>Track 未完成布局时的兜底宽度</summary>
    private const double FallbackTrack = 374;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _scale = FallbackTrack;
    private double _cur;
    private int _lastPct = -1;

    /// <summary>最短停留时长（毫秒）：数据载入很快时也让进度条走完，避免"闪一下就没了"</summary>
    public int MinVisibleMs { get; init; } = 1000;

    public SplashWindow()
    {
        InitializeComponent();

        var s = SettingsStore.Current;
        TxtLogo.Text = SettingsStore.LogoText;
        TxtAppName.Text = s.AppName;
        TxtSubName.Text = string.IsNullOrWhiteSpace(s.AppSubName) ? "DIP 医保分组 · 桌面客户端" : s.AppSubName;
        TxtFooter.Text = "数据目录：" + AppPaths.Root;

        Opacity = 0;
        Loaded += (_, _) =>
        {
            RefreshScale();
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)));
        };
        SizeChanged += (_, _) => RefreshScale();
    }

    /// <summary>上报进度（0~100，只增不减）；可从任意线程调用</summary>
    public void Report(int percent, string stage)
    {
        if (Dispatcher.CheckAccess()) Apply(percent, stage);
        else Dispatcher.Invoke(() => Apply(percent, stage));
    }

    /// <summary>等满最短停留时长，让进度条动画完整播完</summary>
    public async Task WaitMinimumAsync()
    {
        var left = MinVisibleMs - (int)_clock.ElapsedMilliseconds;
        if (left > 0) await Task.Delay(left);
    }

    private void Apply(int percent, string stage)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (percent < _lastPct) percent = _lastPct;
        _lastPct = percent;

        if (!string.IsNullOrEmpty(stage)) TxtStage.Text = stage;
        TxtPct.Text = percent + "%";

        var target = _scale * percent / 100.0;
        var from = _cur;
        _cur = target;

        // 动画会接管 Width，写值前必须先清掉上一次的动画，否则新值会被忽略
        Fill.BeginAnimation(WidthProperty, null);
        Fill.Width = from;
        if (Math.Abs(target - from) < 0.5)
        {
            Fill.Width = target;
            return;
        }
        Fill.BeginAnimation(WidthProperty, new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    /// <summary>按实际布局宽度校准进度条满格长度</summary>
    private void RefreshScale()
    {
        if (Track.ActualWidth > 1) _scale = Track.ActualWidth;
    }
}
