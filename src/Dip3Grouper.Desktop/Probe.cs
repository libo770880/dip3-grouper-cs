using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Dip3GrouperApp.Desktop.Views;
using Dip3GrouperApp.Desktop.Views.Admin;

namespace Dip3GrouperApp.Desktop;

/// <summary>
/// 诊断（命令行 --probe-tab lx|dz|gf &lt;输出.png&gt;）：
/// 还原「页面在页签容器里初始折叠 → 启动空闲预热 → 用户点开页签」这条真实路径，
/// 检查表格首屏到底有没有生成行 —— 用于复现/防回归「打开空白，点查询才出来」。
/// </summary>
public static class Probe
{
    /// <summary>
    /// 连点回归（--probe-rapid）：启动预热跑完前，毫秒级连点「德州物价 → 立项指南 → 技术规范 → 德州物价」，
    /// 每页都必须真的出行。防回归「载入排队被后续导航顶掉，_visited 已记但数据没载 → 页面永久空白」。
    /// </summary>
    public static void RapidSwitch()
    {
        MainWindow.FadeMs = 0;
        MainWindow.LoadBarEnabled = false;
        MainWindow.TraceNav = true;
        var win = new MainWindow { Width = 1440, Height = 900 };
        win.MakeInvisibleForDiag();   // 离屏：不抢焦点、不进任务栏、移出可视区，关窗不弹确认
        win.Show();

        var steps = new Queue<(int AtMs, string What, Action Act)>();
        // 200ms 就动手：此时启动预热（ApplicationIdle）多半还没轮到，正好撞车
        steps.Enqueue((200, "dz", () => { Console.WriteLine("[rapid] → dz"); win.NavDz.IsChecked = true; }));
        steps.Enqueue((215, "lx", () => { Console.WriteLine("[rapid] → lx"); win.NavLx.IsChecked = true; }));
        steps.Enqueue((230, "gf", () => { Console.WriteLine("[rapid] → gf"); win.NavGf.IsChecked = true; }));
        steps.Enqueue((245, "dz", () => { Console.WriteLine("[rapid] → dz（回）"); win.NavDz.IsChecked = true; }));
        Action verify = () =>
        {
            int dz = win.ViewDz.DzGrid.Items.Count, lx = win.ViewLx.LxGrid.Items.Count,
                gf = win.ViewGf.GfGrid.Items.Count, lib = win.ViewLib.Grid.Items.Count;
            Console.WriteLine($"[rapid] 行数 dz={dz} lx={lx} gf={gf} lib={lib}");
            var bad = new List<string>();
            if (dz == 0) bad.Add("德州物价");
            if (lx == 0) bad.Add("立项指南");
            if (gf == 0) bad.Add("技术规范");
            Console.WriteLine(bad.Count == 0
                ? "[rapid] PASS 连点后每页都有数据"
                : "[rapid] FAIL 空白页：" + string.Join("、", bad));
            Application.Current.Shutdown(bad.Count == 0 ? 0 : 1);
        };
        steps.Enqueue((6000, "核对", verify));
        var start = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) =>
        {
            while (steps.Count > 0 && (DateTime.UtcNow - start).TotalMilliseconds >= steps.Peek().AtMs)
            {
                var (_, _, act) = steps.Dequeue();
                try { act(); } catch (Exception ex) { Console.WriteLine("[rapid] FAIL " + ex.Message); }
            }
            if (steps.Count == 0) timer.Stop();
        };
        timer.Start();
    }

    /// <summary>
    /// 首开耗时量测（--probe-nav）：真实 MainWindow 启动 → 等启动预热把四页布局做完 →
    /// 依次点开「德州物价 / 立项指南 / 再回德州物价」，把 `[nav]` 计时打到控制台。
    /// 用来验证「首次点开还卡不卡」，以及第二次进入是否真的是 0 成本。
    /// </summary>
    public static void NavTiming(string outPath = null)
    {
        // 给了输出路径就保留淡入与进度条，并抓一张「首开瞬间」的图（验证进度条真的露脸）
        if (outPath == null) { MainWindow.FadeMs = 0; MainWindow.LoadBarEnabled = false; }
        MainWindow.TraceNav = true;
        var win = new MainWindow { Width = 1440, Height = 900 };
        win.MakeInvisibleForDiag();   // 离屏：不抢焦点、不进任务栏、移出可视区，关窗不弹确认
        var d = win.Dispatcher;
        win.Show();

        var steps = new Queue<(int AtMs, string What, Action Act)>();
        steps.Enqueue((9000, "点击 德州物价", () =>
        {
            Console.WriteLine("[probe-nav] → 德州物价");
            win.NavDz.IsChecked = true;
        }));
        if (outPath != null)
        {
            steps.Enqueue((9130, "抓首开瞬间", () =>
            {
                Console.WriteLine($"[probe-nav] bar vis={win.LoadTrack.Visibility} " +
                                  $"w={win.LoadTrack.ActualWidth:F0} h={win.LoadTrack.ActualHeight:F0} " +
                                  $"slideX={win.LoadSlideX.X:F0} opacity={(win.ViewDz.Opacity):F2}");
                Capture(win, outPath);
                Console.WriteLine("[probe-nav] 首开截图 " + outPath);
            }));
            // 再抓一张「收尾中」的帧：滑块应已跑到右端并开始淡出（证明不是半路消失）
            var endPath = outPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                ? outPath[..^4] + "_end.png"
                : outPath + "_end.png";
            steps.Enqueue((9740, "抓收尾帧", () =>
            {
                Console.WriteLine($"[probe-nav] bar(收尾) vis={win.LoadTrack.Visibility} " +
                                  $"slideX={win.LoadSlideX.X:F0} trackOpacity={win.LoadTrack.Opacity:F2}");
                Capture(win, endPath);
                Console.WriteLine("[probe-nav] 收尾截图 " + endPath);
            }));
        }
        steps.Enqueue((10500, "点击 立项指南", () => { Console.WriteLine("[probe-nav] → 立项指南"); win.NavLx.IsChecked = true; }));
        steps.Enqueue((12000, "回到 德州物价", () => { Console.WriteLine("[probe-nav] → 德州物价（第二次）"); win.NavDz.IsChecked = true; }));
        steps.Enqueue((13000, "结束", () => { Console.WriteLine("[probe-nav] DONE"); App.DiagFinish(0); }));
        var start = DateTime.UtcNow;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            if (steps.Count == 0) { timer.Stop(); return; }
            if ((DateTime.UtcNow - start).TotalMilliseconds < steps.Peek().AtMs) return;
            var (_, _, act) = steps.Dequeue();
            try { act(); } catch (Exception ex) { Console.WriteLine("[probe-nav] FAIL " + ex.Message); }
        };
        timer.Start();
    }

    /// <summary>
    /// 早点击复现（--probe-early gf &lt;输出.png&gt;）：窗口一渲染出来立刻点开页签
    /// （Loaded 优先级，先于 ApplicationIdle 的启动预热），还原「用户手快」路径。
    /// </summary>
    public static void EarlyClick(string nav, string outPath)
    {
        MainWindow.FadeMs = 0;        // 离屏截图确定性：关闭淡入（否则可能截到半透明帧）
        MainWindow.LoadBarEnabled = false;   // 同理：别让进度条挂在截图里
        MainWindow.TraceNav = true;          // 打出 [nav] / [prewarm] 时序
        var win = new MainWindow { Width = 1440, Height = 900 };
        win.MakeInvisibleForDiag();   // 离屏：不抢焦点、不进任务栏、移出可视区，关窗不弹确认
        var d = win.Dispatcher;
        win.Show();

        d.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            Console.WriteLine("[probe] 窗口渲染即点开页签（早于启动预热）");
            switch (nav)
            {
                case "dz": win.NavDz.IsChecked = true; break;
                case "lx": win.NavLx.IsChecked = true; break;
                default: win.NavGf.IsChecked = true; break;
            }

            d.BeginInvoke(DispatcherPriority.SystemIdle, new Action(() =>
            {
                // 看**点开的那个**表，而不是固定看技术规范（导航到 dz 时看 gf 永远是空的）
                var (grid, stat, pageFoot, tag) = nav switch
                {
                    "dz" => (win.ViewDz.DzGrid, "", win.ViewDz.DzPageInfo.Text, "德州物价"),
                    "lx" => (win.ViewLx.LxGrid, "", "", "立项指南"),
                    _ => (win.ViewGf.GfGrid, win.ViewGf.StatText.Text, win.ViewGf.GfPageInfo.Text, "技术规范"),
                };
                Console.WriteLine($"[probe] {tag} 早点击结果：Items={grid.Items.Count} " +
                                  $"首行容器={(grid.ItemContainerGenerator.ContainerFromIndex(0) == null ? "null(未生成)" : "已生成")} " +
                                  $"Stat={stat} 页脚={pageFoot}");
                DumpRows(win, "早点击后");
                Console.WriteLine($"[probe-nav] bar vis={win.LoadTrack.Visibility} " +
                                  $"w={win.LoadTrack.ActualWidth:F0} h={win.LoadTrack.ActualHeight:F0} " +
                                  $"slideX={win.LoadSlideX.X:F0} opacity={(win.ViewDz.Opacity):F2}");
                Capture(win, outPath);
                App.DiagFinish(0);
            }));
        }));
    }

    /// <summary>
    /// 真实路径复现（--probe-main gf|dz|lx &lt;输出.png&gt;[@宽x高]）：
    /// 起一个真 MainWindow（默认停在「单条分组」）→ 等启动空闲预热三页跑完 →
    /// 再点开目标页签 → 检查表格首屏是否真的出行，并截图。
    /// 输出路径可带 @宽x高 后缀（如 out.png@1024x728）模拟小屏，核对紧凑导航。
    /// </summary>
    public static void MainWindowTab(string nav, string outPath)
    {
        // @宽x高 后缀：模拟小屏（低配机 1024×768 / 125% 缩放 ≈ 819×582 DIP）
        double w = 1440, h = 900;
        var at = outPath.LastIndexOf('@');
        if (at > 0)
        {
            var dim = outPath[(at + 1)..].ToLowerInvariant();
            var parts = dim.Split('x', '×', 'X');
            if (parts.Length == 2 && double.TryParse(parts[0], out var pw) && double.TryParse(parts[1], out var ph))
            {
                (w, h) = (pw, ph);
                outPath = outPath[..at];
            }
        }
        MainWindow.FadeMs = 0;        // 离屏截图确定性：关闭淡入（否则可能截到半透明帧）
        MainWindow.LoadBarEnabled = false;   // 同理：别让进度条挂在截图里
        var win = new MainWindow { Width = w, Height = h };
        win.MakeInvisibleForDiag();   // 离屏：不抢焦点、不进任务栏、移出可视区，关窗不弹确认
        // 模拟真实小屏 clamp 的结果：WinFit 会把 MinWidth 降进工作区，离屏本机屏幕大
        // 触发不了，这里手动对齐，否则 MinWidth=1060 会把小尺寸顶回去。
        if (w < win.MinWidth) win.MinWidth = w;
        if (h < win.MinHeight) win.MinHeight = h;
        win.Show();

        // 与 PrewarmDataPages 同样的优先级排队：等它跑完（ApplicationIdle 之后是 SystemIdle）
        var d = win.Dispatcher;
        d.BeginInvoke(DispatcherPriority.SystemIdle, new Action(() =>
        {
            Console.WriteLine("[probe] 空闲预热已跑完");
            DumpRows(win, "预热后（尚未点开页签）");

            // 用户点开页签
            switch (nav)
            {
                case "dz": win.NavDz.IsChecked = true; break;
                case "lx": win.NavLx.IsChecked = true; break;
                case "jd": win.NavLx.IsChecked = true; break;
                case "jdu": win.NavLx.IsChecked = true; break;
                default: win.NavGf.IsChecked = true; break;
            }

            d.BeginInvoke(DispatcherPriority.SystemIdle, new Action(() =>
            {
                // jd = 立项指南 → 解读辅导视图（截图核对工具栏按钮 / 更新提示条版式）
                // jdu = 同上，并挂一条「发现新版」提示，用于核对提示条
                if (nav is "jd" or "jdu") win.ViewLx.DiagJiedu("", -1, nav == "jdu");
                DumpRows(win, "点开页签后");

                // more = 标题栏「更多」下拉（核对菜单项版式；面板是独立 Popup，需单独渲染）
                if (nav == "more")
                {
                    win.DiagOpenMore();
                    var t0 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
                    t0.Tick += (_, _) =>
                    {
                        t0.Stop();
                        Capture(win, outPath);
                        if (win.MorePanel is FrameworkElement mp)
                            Capture(mp, System.IO.Path.Combine(
                                System.IO.Path.GetDirectoryName(outPath) ?? ".",
                                System.IO.Path.GetFileNameWithoutExtension(outPath) + "-menu.png"));
                        App.DiagFinish(0);
                    };
                    t0.Start();
                    return;
                }
                Console.WriteLine($"[probe-nav] bar vis={win.LoadTrack.Visibility} " +
                                  $"w={win.LoadTrack.ActualWidth:F0} h={win.LoadTrack.ActualHeight:F0} " +
                                  $"slideX={win.LoadSlideX.X:F0} opacity={(win.ViewDz.Opacity):F2}");
                // 解读辅导要走一次 Busy 压淡渐显，立刻截会截到全白；等动画收尾再取样
                if (nav is "jd" or "jdu")
                {
                    var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                    t.Tick += (_, _) =>
                    {
                        t.Stop();
                        Capture(win, outPath);
                        App.DiagFinish(0);
                    };
                    t.Start();
                    return;
                }
                Capture(win, outPath);
                App.DiagFinish(0);
            }));
        }));
    }

    private static void DumpRows(MainWindow win, string tag)
    {
        var gv = win.ViewGf;
        var grid = gv.GfGrid;
        Console.WriteLine($"[probe] {tag}：ViewGf.Visibility={gv.Visibility} IsVisible={gv.IsVisible} " +
                          $"ViewGf.ActualSize={gv.ActualWidth:0}×{gv.ActualHeight:0}");
        Console.WriteLine($"[probe] {tag}：Items={grid.Items.Count} 首行容器=" +
                          $"{(grid.ItemContainerGenerator.ContainerFromIndex(0) == null ? "null(未生成)" : "已生成")} " +
                          $"表格={grid.ActualWidth:0}×{grid.ActualHeight:0} " +
                          $"Stat={gv.StatText.Text}");
        Console.WriteLine($"[probe] {tag}：页脚={gv.GfPageInfo.Text} 状态={gv.GfStatus.Text}");
    }

    private static void Capture(FrameworkElement root, string outPath)
    {
        // 离屏窗口为了不抢视觉把 Opacity 压到 0.01，RenderTargetBitmap 会把这个透明度一起渲染进去，
        // 不还原就截出一张全白。截完立刻恢复，不影响窗口本身。
        var oldOpacity = root.Opacity;
        root.Opacity = 1;
        try
        {
            var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(outPath);
            enc.Save(fs);
        }
        finally
        {
            root.Opacity = oldOpacity;
        }
    }

    /// <summary>后台概览页离屏取样（--probe-overview &lt;输出.png&gt;）：核对统计卡版式。</summary>
    public static void Overview(string outPath)
    {
        var v = new AdminOverviewView();
        var size = new Size(1500, 940);
        var host = new Border
        {
            Background = (Brush)Application.Current.Resources["B.Bg"],
            Child = v,
            Width = size.Width,
            Height = size.Height,
        };
        v.EnsureLoaded();
        host.Measure(size);
        host.Arrange(new Rect(size));
        host.UpdateLayout();
        Capture(host, outPath);
    }

    /// <summary>
    /// 后台任一页离屏取样（--probe-admin &lt;页名&gt; &lt;输出.png&gt;）：
    /// 后台页要登录后才看得到，改版式时用它比「点进去再截图」快得多。
    /// 页名：overview / diseases / lixiang / mapping / dezhou / data / settings。
    /// </summary>
    public static void AdminPage(string which, string outPath)
    {
        var size = new Size(1500, 940);
        FrameworkElement view = which switch
        {
            "diseases" or "病种库" => new AdminDiseasesView(),
            "lixiang" or "立项指南" => new AdminLixiangView(),
            "mapping" or "映射" => new AdminMappingView(),
            "dezhou" or "物价" => new AdminDezhouView(),
            "data" or "数据更新" => new AdminDataView(),
            "settings" or "设置" => new AdminSettingsView(),
            _ => new AdminOverviewView(),
        };

        switch (view)
        {
            case AdminOverviewView v: v.EnsureLoaded(); break;
            case AdminDiseasesView v: v.EnsureLoaded(); break;
            case AdminLixiangView v: v.EnsureLoaded(); break;
            case AdminMappingView v: v.EnsureLoaded(); break;
            case AdminDezhouView v: v.EnsureLoaded(); break;
            case AdminDataView v: v.EnsureLoaded(); break;
            case AdminSettingsView v: v.EnsureLoaded(); break;
        }

        var host = new Border
        {
            Background = (Brush)Application.Current.Resources["B.Bg"],
            Child = view,
            Width = size.Width,
            Height = size.Height,
        };
        host.Measure(size);
        host.Arrange(new Rect(size));
        host.UpdateLayout();
        Capture(host, outPath);
    }

    public static string TabOpen(string which, string outPath)
    {
        var size = new Size(1200, 700);

        FrameworkElement view = which switch
        {
            "dz" => new DezhouPriceView(),
            "lx" => new LixiangView(),
            _ => new GuifanView(),
        };
        view.Visibility = Visibility.Collapsed;        // 启动时不在当前页签
        var host = new Grid();
        host.Children.Add(view);
        host.Measure(size);
        host.Arrange(new Rect(size));
        host.UpdateLayout();

        // ---- 与 MainWindow.PrewarmDataPages 完全一致的空闲预热 ----
        switch (view)
        {
            case GuifanView v: v.Prewarm(size.Width - 40, size.Height - 60); break;
            case DezhouPriceView v: v.Prewarm(size.Width - 40, size.Height - 60); break;
            case LixiangView v: v.Prewarm(size.Width - 40, size.Height - 60); break;
        }
        Console.WriteLine($"[probe] 预热后：IsVisible={view.IsVisible} MeasureValid={view.IsMeasureValid} " +
                          $"ArrangeValid={view.IsArrangeValid} RenderSize={view.RenderSize}");

        // ---- 用户点开该页签 ----
        view.Visibility = Visibility.Visible;
        host.Measure(size);
        host.Arrange(new Rect(size));
        host.UpdateLayout();
        // 再给一帧（真实运行时 Tab 切换后还会走一次布局）
        host.Measure(size);
        host.Arrange(new Rect(size));
        host.UpdateLayout();

        DataGrid grid = view switch
        {
            GuifanView v => v.GfGrid,
            DezhouPriceView v => v.DzGrid,
            LixiangView v => v.LxGrid,
            _ => null,
        };

        var first = grid?.ItemContainerGenerator.ContainerFromIndex(0);
        Console.WriteLine($"[probe] 切页后：Items={grid?.Items.Count} 首行容器={(first == null ? "null(未生成)" : "已生成")} " +
                          $"表格 ActualSize={grid?.ActualWidth:0}×{grid?.ActualHeight:0} " +
                          $"MeasureValid={grid?.IsMeasureValid} ArrangeValid={grid?.IsArrangeValid}");

        // ---- 渲染成 PNG 直观看 ----
        var host2 = new Border
        {
            Background = (Brush)Application.Current.Resources["B.Bg"],
            Child = view,
            Width = size.Width,
            Height = size.Height,
        };
        host2.Measure(size);
        host2.Arrange(new Rect(size));
        host2.UpdateLayout();

        var bmp = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(host2);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(outPath)) enc.Save(fs);
        return null;
    }
}
