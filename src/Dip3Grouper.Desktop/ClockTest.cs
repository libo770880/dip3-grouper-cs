// 临时验证（诊断时钟，不参与产品）：同样是「窗口 + Margin 动画」，
// 在 CompositionTarget.Rendering 回调里读 Margin.Left，看时钟到底有没有被推进。
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Dip3GrouperApp.Desktop;

internal static class ClockTest
{
    public static void Run(string outDir)
    {
        System.IO.Directory.CreateDirectory(outDir);
        var log = new System.IO.StreamWriter(System.IO.Path.Combine(outDir, "clock.log"), false) { AutoFlush = true };
        var sw = Stopwatch.StartNew();
        void W(string m) => log.WriteLine($"[{sw.ElapsedMilliseconds,6}ms] {m}");

        var slide = new Border { Width = 78, Height = 28, Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left };
        var host = new Grid { Width = 400, Height = 40 };
        host.Children.Add(slide);
        var win = new Window
        {
            Content = host, Width = 460, Height = 120,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            Left = -32000, Top = -32000, Opacity = 0.02, Topmost = false,
        };
        win.Show();

        double Read() => slide.GetValue(FrameworkElement.MarginProperty) is Thickness t ? t.Left : -1;
        void Pump(int ms)
        {
            var until = Environment.TickCount64 + Math.Max(1, ms);
            while (Environment.TickCount64 < until)
            {
                var f = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => f.Continue = false));
                Dispatcher.PushFrame(f);
                System.Threading.Thread.Sleep(1);
            }
        }

        Pump(400);
        slide.Margin = new Thickness(2, 2, 0, 2);   // 基值 = 起点
        Pump(100);
        W($"起始 cur={Read():0.0}");

        var tm = new ThicknessAnimation
        {
            From = new Thickness(2, 2, 0, 2),
            To = new Thickness(248, 2, 0, 2),
            Duration = new Duration(TimeSpan.FromMilliseconds(600)),
            DecelerationRatio = 0.65,
        };
        tm.Completed += (_, _) => W("Completed 事件触发");

        // 模拟 Segment.Arrange 的顺序：先赋基值 = 终点，再 BeginAnimation
        slide.Margin = new Thickness(248, 2, 0, 2);
        slide.BeginAnimation(FrameworkElement.MarginProperty, tm);
        W($"BeginAnimation 后立即 cur={Read():0.0}");

        var frames = 0;
        EventHandler onFrame = (_, _) =>
        {
            frames++;
            if (frames % 6 == 1) W($"渲染帧 #{frames} cur={Read():0.0} hasAnim={slide.HasAnimatedProperties}");
        };
        CompositionTarget.Rendering += onFrame;
        var end = Environment.TickCount64 + 900;
        while (Environment.TickCount64 < end) Pump(8);
        CompositionTarget.Rendering -= onFrame;
        W($"结束 cur={Read():0.0} 渲染帧数={frames}");

        // 再测「只挂时钟、不赋基值」：From 显式给，基值保持起点
        slide.BeginAnimation(FrameworkElement.MarginProperty, null);
        slide.Margin = new Thickness(2, 2, 0, 2);
        Pump(50);
        var tm2 = new ThicknessAnimation
        {
            From = new Thickness(2, 2, 0, 2),
            To = new Thickness(248, 2, 0, 2),
            Duration = new Duration(TimeSpan.FromMilliseconds(600)),
        };
        slide.BeginAnimation(FrameworkElement.MarginProperty, tm2);
        Pump(200);
        W($"不赋基值：200ms 后 cur={Read():0.0}（应在 2~248 之间）");
        Pump(600);
        W($"不赋基值：800ms 后 cur={Read():0.0}（应≈248）");

        win.Close();
        log.Dispose();
        Console.WriteLine("[clock-test] 写入 " + log);
        Console.WriteLine("[clock-test] 渲染帧数=" + frames);
    }
}
