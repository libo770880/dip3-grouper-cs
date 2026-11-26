using System.IO;
using System.Windows;
using System.Windows.Threading;
using Dip3Grouper;
using Dip3GrouperApp.Desktop.Theming;

namespace Dip3GrouperApp.Desktop;

/// <summary>
/// 桌面客户端入口。
/// 与 Web 版的差异：不启动 HTTP 服务，界面为原生 WPF，直接调用 Dip3Grouper.Core，
/// 因此不需要 Node 运行时，也不占用任何端口。
///
/// 命令行：Dip3Grouper.Desktop [--jiceng] [--home &lt;工程根&gt;] [--selftest]
///   --selftest 不打开窗口，跑一遍数据链路 + 全部页面构造自检，日志写到
///              %TEMP%\dip3-desktop-selftest.log，全部通过时退出码 0。
///
/// 启动顺序：解析参数 → 定位数据目录 → 载入设置并套用主题 → 显示启动页 →
///           后台线程初始化数据（进度回传启动页）→ 显示主窗口 → 关闭启动页。
/// 顺序不能乱：先关启动页再开主窗口会因 ShutdownMode=OnLastWindowClose 直接退出进程。
/// </summary>
public partial class App : Application
{
    /// <summary>应用级共享状态（与 Web 版同一实现，见 Core/AppState.cs）</summary>
    public static AppState State { get; private set; }

    public static bool Jiceng { get; private set; }

    /// <summary>设置变更（软件名称 / 主题 / 登录凭据）后触发，宿主窗口据此刷新标题与品牌区</summary>
    public static event Action SettingsChanged;

    public static void NotifySettingsChanged() => SettingsChanged?.Invoke();

    /// <summary>
    /// 诊断 / 自检模式收尾：关掉所有残留窗口 → Shutdown → 2.5 秒兜底强制退出。
    /// <para>为什么不能只调 <see cref="Application.Shutdown(int)"/>：诊断窗是 MainWindow 时，
    /// 关闭会走「最小化到托盘 / 退出」确认框或 HideToTray，进程就挂在那儿等人点 —— 这正是
    /// 「跑一次测试弹一个框、还得手工关」的根因。这里先标记诊断窗口再关，最后兜底 Exit 保证进程一定走。</para>
    /// </summary>
    public static void DiagFinish(int code)
    {
        // 先改成「显式退出」：否则关掉最后一个窗口时 WPF 会自行 Shutdown(0)，
        // 自检失败也报成功（退出码被吃掉），CI 就查不出来了。
        Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            foreach (var w in Current.Windows.Cast<Window>().ToList())
            {
                if (w is MainWindow m) m.EnableDiagMode();
                try { w.Close(); } catch { /* 已在关闭流程里，忽略 */ }
            }
        }
        catch { /* 视觉树已拆，忽略 */ }
        try { Console.Out.Flush(); } catch { }
        Current.Shutdown(code);

        // 兜底：还有窗口（例如某个模态框）没关掉时，别让进程僵在那儿等人工点击
        var guard = new Thread(() =>
        {
            Thread.Sleep(2500);
            Environment.Exit(code);
        }) { IsBackground = true, Name = "diag-exit" };
        guard.Start();
    }

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        // 表格密度跟随本机偏好（紧凑 / 标准）：必须在建窗之前写进全局资源，
        // 否则首屏会先按 XAML 默认 26 画一遍、切换后再跳一次。
        UiPrefs.Apply();
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            ShowFatal(ex);
        };

        // 系统注销 / 关机：直接放行退出。此时弹关闭确认框会把关机流程卡住（Windows 只给几秒）
        SessionEnding += (_, _) => (MainWindow as MainWindow)?.PrepareSystemShutdown();

        var selftest = false;
        string importDezhou = null;
        string importDezhouDraft = null;
        string importDezhouUsage = null;
        string importGuifan = null;
        string shotRelated = null;
        string shotClose = null;
        try
        {
            var home = ParseArgs(e.Args, out var jiceng, out selftest, out importDezhou, out importDezhouDraft,
                out importDezhouUsage, out importGuifan, out shotRelated, out shotClose);
            Jiceng = jiceng;
            ResolveDataRoot(home);
            // 命令行导入模式会往控制台打中文报告，Windows 控制台默认 GBK 会花屏
            if (importDezhou != null || importDezhouDraft != null || importDezhouUsage != null || importGuifan != null)
                ConsoleSetup.UseUtf8();

            // 主题必须在任何窗口出现之前套用，否则启动页会先闪一下默认配色
            ThemeManager.Apply(SettingsStore.Current.Theme);
        }
        catch (Exception ex)
        {
            ShowFatal(ex);
            DiagFinish(1);
            return;
        }

        // 诊断模式：把关闭确认框离屏渲染成 PNG（不开窗、不干扰桌面），
        // 用法 --shot-close <输出.png>
        if (!string.IsNullOrWhiteSpace(shotClose))
        {
            try
            {
                var code = CloseConfirmWindow.Shot(shotClose);
                Console.WriteLine("[shot-close] " + (code ?? "OK"));
                DiagFinish(string.IsNullOrEmpty(code) ? 0 : 2);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[shot-close] FAIL " + ex.Message);
                DiagFinish(2);
            }
            return;
        }

        // 诊断模式：把关联详情弹窗离屏渲染成 PNG（不开窗、不干扰桌面），
        // 用法 --shot-related <库:guifan|dezhou|lixiang> <Id> <输出.png>
        if (!string.IsNullOrWhiteSpace(shotRelated))
        {
            try
            {
                State = new AppState();
                State.Init(Jiceng);
                var parts = shotRelated.Split(':');
                var code = RelatedWindow.Shot(parts[0], int.Parse(parts[1]), e.Args[^1]);
                Console.WriteLine("[shot-related] " + code);
                DiagFinish(string.IsNullOrEmpty(code) ? 0 : 2);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[shot-related] FAIL " + ex.Message);
                DiagFinish(2);
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(importGuifan))
        {
            try
            {
                var n = new GuifanStore().ImportRaw(importGuifan,
                    "《全国医疗服务项目技术规范（2023年版）》（国家卫健委 / 国家中医药局 / 国家疾控局）");
                Console.WriteLine($"[guifan] 已导入 {n} 条 → {AppPaths.GuifanFile}");
                DiagFinish(0);
            }
            catch (Exception ex)
            {
                ShowFatal(ex);
                DiagFinish(1);
            }
            return;
        }

        // 新项目征求意见稿（通知附件 xlsx 直读）→ data/dezhou-draft.json
        // 用法：--import-dezhou-draft <附件.xlsx> [--label 批次显示名] [--doc 正式文号] [--draft-note 说明]
        // 注意：与 --import-dezhou（主库）**完全分离**，主库三份数据零改动。
        if (!string.IsNullOrWhiteSpace(importDezhouDraft))
        {
            try
            {
                var label = ArgValue(e.Args, "--label") ?? "德州市物价新项目（征求意见稿）";
                var doc = ArgValue(e.Args, "--doc") ?? "";
                var note = ArgValue(e.Args, "--draft-note") ?? "";
                var file = DezhouDraftParser.Parse(importDezhouDraft, label, doc, note);
                var store = new DezhouDraftStore();
                var (ni, nc, nm) = store.Import(file, file.Meta.Source, file.Meta.FileName);
                Console.WriteLine($"[dezhou-draft] 已导入 新增 {ni} 项 / 拟作废 {nc} 条 / 映射 {nm} 行" +
                                  $"（{file.Meta.Batches.Count} 个类别）→ {AppPaths.DezhouDraftFile}");
                foreach (var b in file.Meta.Batches)
                    Console.WriteLine($"    {b.Scope,-24} 新增 {b.Mains,4} 主项 + {b.Adds,3} 加收 + {b.Exts,3} 扩展 = {b.Rows,4}");
                DiagFinish(0);
            }
            catch (Exception ex)
            {
                ShowFatal(ex);
                DiagFinish(1);
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(importDezhou))
        {
            try
            {
                var n = new DezhouPriceStore().ImportRaw(importDezhou,
                    "德州市医疗保障局 立项指南规范整合文件（德医保发〔2025〕33号/37号、〔2026〕3号/11号）");
                Console.WriteLine($"[dezhou] 已导入 {n} 条 → {AppPaths.DezhouFile}");
                DiagFinish(0);
            }
            catch (Exception ex)
            {
                ShowFatal(ex);
                DiagFinish(1);
            }
            return;
        }

        // 类别使用说明（.tmp/dezhou/usage.json）合并进物价库 → data/dezhou-price.json
        if (!string.IsNullOrWhiteSpace(importDezhouUsage))
        {
            try
            {
                var z = new DezhouPriceStore();
                var (cats, items) = z.ImportUsage(importDezhouUsage);
                Console.WriteLine($"[dezhou] 已写入类别使用说明：{cats} 个类别 / {items} 条 → {AppPaths.DezhouFile}");
                DiagFinish(0);
            }
            catch (Exception ex)
            {
                ShowFatal(ex);
                DiagFinish(1);
            }
            return;
        }

        // 诊断模式：更新落库往返（--probe-jiedu-apply <临时根目录>）
        // 在临时副本上验证「备份 → 合并 → 再比对无差异」，绝不碰真实 data/lixiang.json
        if (e.Args.Contains("--probe-jiedu-apply"))
        {
            try
            {
                var tmpRoot = e.Args[Array.IndexOf(e.Args, "--probe-jiedu-apply") + 1];
                var srcFile = AppPaths.LixiangFile;
                Directory.CreateDirectory(Path.Combine(tmpRoot, "data"));
                File.Copy(srcFile, Path.Combine(tmpRoot, "data", "lixiang.json"), overwrite: true);
                AppPaths.SetRoot(tmpRoot);

                var store = new LixiangStore();
                store.Load();
                var before = store.State.Jiedu.List.Count;
                // 模拟「本地少一条」：Import 之后必须被原文补回来
                store.State.Jiedu.List.RemoveAll(x => x.Issue == 13 && x.No == 1);

                var raw = await JieduSource.DownloadAsync();
                var r1 = JieduSource.Compare(store, raw);
                var bak = JieduSource.Apply(store, r1);
                var r2 = JieduSource.Compare(store, raw);

                Console.WriteLine($"[probe-jiedu-apply] before={before} / after={store.State.Jiedu.List.Count} " +
                                  $"added={r1.Added.Count} updated={r1.Updated.Count} backup={(string.IsNullOrEmpty(bak) ? "none" : "ok")}");
                Console.WriteLine($"[probe-jiedu-apply] recheck added={r2.Added.Count} updated={r2.Updated.Count} " +
                                  $"removed={r2.Removed.Count} hashMatch={store.State.Jiedu.SourceHash == r2.RemoteHash}");
                DiagFinish(r2.Added.Count == 0 && r2.Updated.Count == 0 && store.State.Jiedu.List.Count == before ? 0 : 3);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[probe-jiedu-apply] FAIL " + ex.Message);
                DiagFinish(2);
            }
            return;
        }

        // 诊断模式：核对解读辅导原文（--probe-jiedu）：下载 → 解析 → 与本地比对，只打印不写库
        if (e.Args.Contains("--probe-jiedu"))
        {
            try
            {
                State = new AppState();
                State.Init(Jiceng);
                var raw = await JieduSource.DownloadAsync();
                var r = JieduSource.Compare(State.Lixiang, raw);
                Console.WriteLine($"[probe-jiedu] remote {r.RemoteIssues.Count} issues / {r.RemoteTotal} items, " +
                                  $"local {r.LocalIssues.Count} / {r.LocalTotal}");
                Console.WriteLine($"[probe-jiedu] sha256={r.RemoteHash} size={r.RemoteSize} " +
                                  $"added={r.Added.Count} updated={r.Updated.Count} removed={r.Removed.Count} hasUpdate={r.HasUpdate}");
                if (r.NewIssues.Count > 0)
                    Console.WriteLine("[probe-jiedu] new issues: " + string.Join(",", r.NewIssues));
                DiagFinish(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[probe-jiedu] FAIL " + ex.Message);
                DiagFinish(2);
            }
            return;
        }

        // 诊断模式：后台概览页离屏渲染（--probe-overview <输出.png>）
        if (e.Args.Contains("--probe-overview"))
        {
            try
            {
                State = new AppState();
                State.Init(Jiceng);
                Probe.Overview(e.Args[^1]);
                Console.WriteLine("[probe] overview OK");
                DiagFinish(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[probe] overview FAIL " + ex);
                DiagFinish(2);
            }
            return;
        }

        // 诊断模式：后台任一页离屏渲染（--probe-admin <页名> <输出.png>）
        if (e.Args.Contains("--probe-admin"))
        {
            try
            {
                State = new AppState();
                State.Init(Jiceng);
                var which = e.Args[Array.IndexOf(e.Args, "--probe-admin") + 1];
                Probe.AdminPage(which, e.Args[^1]);
                Console.WriteLine("[probe-admin] ok");
                DiagFinish(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[probe-admin] FAIL " + ex);
                DiagFinish(2);
            }
            return;
        }

        // 诊断模式：量化首开卡顿（--probe-nav）：等启动预热跑完，依次点开数据页签并打印耗时
        if (e.Args.Contains("--probe-nav"))
        {
            try
            {
                Jiceng = e.Args.Contains("--jiceng");
                State = new AppState();
                State.Init(Jiceng);
                var navOut = e.Args.FirstOrDefault(a => a.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
                Probe.NavTiming(navOut);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[probe-nav] FAIL " + ex);
                DiagFinish(2);
            }
            return;
        }

        // 诊断模式：连点回归（--probe-rapid）——预热未完成时毫秒级换页，每页都必须出行
        if (e.Args.Contains("--probe-rapid"))
        {
            try
            {
                Jiceng = e.Args.Contains("--jiceng");
                State = new AppState();
                State.Init(Jiceng);
                Probe.RapidSwitch();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[rapid] FAIL " + ex);
                DiagFinish(2);
            }
            return;
        }

        // 诊断模式：早点击复现（--probe-early gf|dz|lx <输出.png>）
        if (e.Args.Contains("--probe-early"))
        {
            try
            {
                Jiceng = e.Args.Contains("--jiceng");
                State = new AppState();
                State.Init(Jiceng);
                WarmupAsync(State);          // 与正常启动一致：后台预热并发在跑
                var which = e.Args[Array.IndexOf(e.Args, "--probe-early") + 1];
                Probe.EarlyClick(which, e.Args[^1]);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[probe-early] FAIL " + ex);
                DiagFinish(2);
            }
            return;
        }

        // 诊断模式：真实 MainWindow 路径复现（--probe-main gf|dz|lx <输出.png>[@宽x高]）
        if (e.Args.Contains("--probe-main"))
        {
            try
            {
                Jiceng = e.Args.Contains("--jiceng");
                State = new AppState();
                State.Init(Jiceng);
                var which = e.Args[Array.IndexOf(e.Args, "--probe-main") + 1];
                Probe.MainWindowTab(which, e.Args[^1]);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[probe-main] FAIL " + ex);
                DiagFinish(2);
            }
            return;
        }

        // 诊断模式：还原「折叠 → 启动预热 → 点开页签」，检查首屏是否真的出行（--probe-tab lx|dz|gf <输出.png>）
        if (e.Args.Contains("--probe-tab"))
        {
            try
            {
                State = new AppState();
                State.Init(Jiceng);
                var which = e.Args[Array.IndexOf(e.Args, "--probe-tab") + 1];
                Probe.TabOpen(which, e.Args[^1]);
                DiagFinish(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[probe] FAIL " + ex);
                DiagFinish(2);
            }
            return;
        }

        // 诊断模式：三个数据查询页离屏渲染成 PNG（--shot-page lx|dz|gf <输出.png>）
        var shotPage = e.Args.Contains("--shot-page");
        if (shotPage)
        {
            try
            {
                State = new AppState();
                State.Init(Jiceng);
                var which = e.Args[Array.IndexOf(e.Args, "--shot-page") + 1];
                var code = Bench.ShotPage(which, e.Args[^1]);
                Console.WriteLine("[shot-page] " + code);
                DiagFinish(string.IsNullOrEmpty(code) ? 0 : 2);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[shot-page] FAIL " + ex.Message);
                Console.WriteLine(ex.StackTrace);
                DiagFinish(2);
            }
            return;
        }

        // 诊断模式：前台主题选择离屏渲染（--shot-theme <主题id> <输出前缀>，产出 -bar / -panel 两张）
        if (e.Args.Contains("--shot-theme"))
        {
            try
            {
                State = new AppState();
                State.Init(Jiceng);
                var i = Array.IndexOf(e.Args, "--shot-theme");
                Console.WriteLine("[shot-theme] " + Bench.ShotTheme(e.Args[i + 1], e.Args[^1]));
                DiagFinish(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[shot-theme] FAIL " + ex.Message);
                Console.WriteLine(ex.StackTrace);
                DiagFinish(2);
            }
            return;
        }

        // 诊断模式：右键菜单离屏渲染成 PNG（--shot-menu dz|lx|gf <输出.png>，核对菜单样式与条目）
        if (e.Args.Contains("--shot-menu"))
        {
            try
            {
                State = new AppState();
                State.Init(Jiceng);
                var which = e.Args[Array.IndexOf(e.Args, "--shot-menu") + 1];
                Bench.ShotMenu(which, e.Args[^1]);
                Console.WriteLine("[shot-menu] ok");
                DiagFinish(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[shot-menu] FAIL " + ex);
                DiagFinish(2);
            }
            return;
        }

        // 诊断模式：动画时钟探针（--clock-test <输出目录>，验证「先赋基值再 BeginAnimation」是否吃掉补间）
        if (e.Args.Contains("--clock-test"))
        {
            try
            {
                ClockTest.Run(e.Args[^1]);
                DiagFinish(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[clock-test] FAIL " + ex);
                DiagFinish(2);
            }
            return;
        }

        // 诊断模式：实机验证「点分段页签会亮进度遮罩」（--veil-trace <输出目录>）
        if (e.Args.Contains("--veil-trace"))
        {
            try
            {
                State = new AppState();       // 页面构造要读物价库，必须先初始化（同 --shot-page）
                State.Init(Jiceng);
                var log = Bench.VeilTrace(e.Args[^1]);
                Console.WriteLine("[veil-trace] " + log);
                DiagFinish(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[veil-trace] FAIL " + ex.Message);
                Console.WriteLine(ex.StackTrace);
                DiagFinish(1);
            }
            return;
        }

        if (e.Args.Contains("--seg-trace"))
        {
            try
            {
                State = new AppState();
                State.Init(Jiceng);
                var code = Bench.SegTrace(e.Args[^1]);
                DiagFinish(string.IsNullOrEmpty(code) ? 0 : 2);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[seg-trace] FAIL " + ex);
                DiagFinish(2);
            }
            return;
        }

        // 诊断模式：分段白块滑动采样（--seg-demo <输出目录>，核对切换是否有过渡动画）

        if (e.Args.Contains("--seg-demo"))
        {
            try
            {
                ConsoleSetup.UseUtf8();   // 报告里有中文，Windows 控制台默认 GBK 会花屏
                State = new AppState();
                State.Init(Jiceng);
                var code = Bench.SegDemo(e.Args[^1]);
                DiagFinish(string.IsNullOrEmpty(code) ? 0 : 2);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[seg-demo] FAIL " + ex);
                DiagFinish(2);
            }
            return;
        }

        // 诊断模式：性能采样（页面载入 + 检索各阶段耗时打控制台）
        if (e.Args.Contains("--bench"))
        {
            try
            {
                Jiceng = e.Args.Contains("--jiceng");
                State = new AppState();
                State.Init(Jiceng);
                Bench.Run(State);
                DiagFinish(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[bench] FAIL " + ex);
                DiagFinish(2);
            }
            return;
        }

        if (selftest)
        {
            try
            {
                State = new AppState();
                State.Init(Jiceng);
            }
            catch (Exception ex)
            {
                ShowFatal(ex);
                DiagFinish(1);
                return;
            }
            DiagFinish(SelfTest.Run());
            return;
        }

        // 诊断模式：只开后台登录窗（不载入数据），并把 WPF 实际收到的按键写进日志。
        // 用于排查"输入法/键盘通道导致回车不提交"这类只在实机出现的问题。
        if (e.Args.Contains("--login"))
        {
            var path = Path.Combine(Path.GetTempPath(), "dip3-login-trace.log");
            try { File.WriteAllText(path, "== login probe " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ==\n"); } catch { }
            LoginWindow.Trace = m =>
            {
                try { File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + m + "\n"); } catch { }
            };
            var lw = new LoginWindow();
            MainWindow = lw;
            lw.Show();
            return;
        }

        await RunWithSplashAsync();
    }

    /// <summary>诊断：正常启动并停在指定页（--show dz|lx|gf），供外部探针做鼠标级验证。</summary>
    private static void ShowForProbe(MainWindow main, string page)
    {
        main.Show();
        main.WindowState = WindowState.Normal;
        main.Activate();
        main.Topmost = true;
        main.Topmost = false;
        main.DiagOpenPage(page);
    }

    /// <summary>带启动进度的正常启动路径</summary>
    private async Task RunWithSplashAsync()
    {
        var splash = new SplashWindow();
        Application.Current.MainWindow = splash;   // 避免初始化期间"无窗口"被判定为退出
        splash.Show();

        try
        {
            State = await Task.Run(() =>
            {
                var state = new AppState();
                // 数据初始化是纯 CPU/IO，放后台线程跑；进度同步回 UI 线程刷新启动页
                state.Init(Jiceng, (pct, stage) => Dispatcher.Invoke(() => splash.Report(pct, stage)));
                return state;
            });
        }
        catch (Exception ex)
        {
            splash.Close();
            ShowFatal(ex);
            Shutdown(1);
            return;
        }

        splash.Report(100, "启动完成");
        await splash.WaitMinimumAsync();

        var main = new MainWindow();
        Current.MainWindow = main;
        main.Show();
        splash.Close();

        // 诊断：--show dz|lx|gf —— 停在指定页，供外部探针做真鼠标级验证
        var argv = Environment.GetCommandLineArgs();
        var showIdx = Array.IndexOf(argv, "--show");
        if (showIdx >= 0 && showIdx + 1 < argv.Length)
            _ = main.Dispatcher.BeginInvoke(new Action(() => main.DiagOpenPage(argv[showIdx + 1])),
                System.Windows.Threading.DispatcherPriority.Loaded);

        // 启动后静默检查 GitHub Release 新版本（8 秒超时，无网 / 最新版都不打扰）
        UpdateChecker.CheckOnStartup(main);

        // 解读辅导原文检查：地址固定，启动后静默核对一次（12 小时节流），
        // 官方出了新一期才提示，且同一份原文只提示一次
        _ = AutoCheckJieduAsync(main);

        WarmupAsync(State);
    }

    /// <summary>
    /// 启动后静默核对国家医保局解读辅导原文：
    ///   · 地址是固定的，所以软件可以自己查，用户不用去官网翻链接；
    ///   · 12 小时内查过就不重复下载（255 KB，避免每次开软件都占带宽）；
    ///   · 只有「条目真的新增/修订」才提示，且同一份原文只提示一次；任何失败都静默。
    /// </summary>
    private static async Task AutoCheckJieduAsync(MainWindow main)
    {
        try
        {
            if (!Views.LixiangView.NeedAutoCheck()) return;
            await Task.Delay(TimeSpan.FromSeconds(6)).ConfigureAwait(true);   // 让启动预热先跑完
            if (main is null || !main.IsLoaded) return;
            await main.ViewLx.CheckJieduAsync(manual: false).ConfigureAwait(true);
        }
        catch
        {
            // 后台检查失败不能影响任何主功能
        }
    }

    /// <summary>
    /// 启动后空闲预热（后台线程，失败不影响使用）：
    ///  ① 技术规范 JSON（10.6MB / 1.15 万条，解析约 240ms）——不预热的话首次点开该页要干等；
    ///  ② 拼音缓存——首次按拼音检索要现转 1.15 万条名称（约 700ms）；
    ///  ③ 跨库关联索引——首次点开任一详情卡要现建（约 150ms）。
    /// 三者都是只读数据，预热后各页面打开即是「内存命中」。
    /// </summary>
    private static void WarmupAsync(AppState state)
    {
        Task.Run(() =>
        {
            try
            {
                state.Guifan.EnsureLoaded();
                PinyinHelper.WarmAll(state.Guifan.Items.SelectMany(i => new[] { i.Name, i.Category, i.Group }));
                PinyinHelper.WarmAll(state.Dezhou.Items.SelectMany(i => new[] { i.Name, i.Category, i.Unit }));
                PinyinHelper.WarmAll(state.Lixiang.State.Sections.Values
                    .SelectMany(s => s.Rows).SelectMany(r => new[] { r.Name, r.Category, r.Unit }));
                CrossMap.Warm();
            }
            catch
            {
                // 预热失败只是「回到未预热」的行为，不影响功能
            }
        });
    }

    /// <summary>取 <c>--name value</c> 形式的参数值；未给出返回 null。</summary>
    private static string ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>解析命令行；--home 显式指定数据根目录，--selftest 进入自检模式，--import-* 导入解析件，--shot-* 离屏截图</summary>
    private static string ParseArgs(string[] args, out bool jiceng, out bool selftest,
                                    out string importDezhou, out string importDezhouDraft,
                                    out string importDezhouUsage,
                                    out string importGuifan, out string shotRelated, out string shotClose)
    {
        jiceng = false;
        selftest = false;
        importDezhou = null;
        importDezhouDraft = null;
        importDezhouUsage = null;
        importGuifan = null;
        shotRelated = null;
        shotClose = null;
        string home = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--jiceng") { jiceng = true; continue; }
            if (args[i] == "--selftest") { selftest = true; continue; }
            if (args[i] == "--import-dezhou" && i + 1 < args.Length) { importDezhou = args[++i]; continue; }
            if (args[i] == "--import-dezhou-draft" && i + 1 < args.Length) { importDezhouDraft = args[++i]; continue; }
            if (args[i] == "--import-dezhou-usage" && i + 1 < args.Length) { importDezhouUsage = args[++i]; continue; }
            if (args[i] == "--import-guifan" && i + 1 < args.Length) { importGuifan = args[++i]; continue; }
            if (args[i] == "--shot-related" && i + 1 < args.Length) { shotRelated = args[++i]; continue; }
            if (args[i] == "--shot-close" && i + 1 < args.Length) { shotClose = args[++i]; continue; }
            if (args[i] == "--home" && i + 1 < args.Length) home = args[++i];
        }
        return home;
    }

    /// <summary>
    /// 定位数据根目录（含 data/、config/）。优先级：
    ///   ① --home 参数  ② DIP3_HOME 环境变量  ③ exe 同目录（绿色版布局）  ④ Core 的向上查找
    /// </summary>
    private static void ResolveDataRoot(string home)
    {
        if (!string.IsNullOrWhiteSpace(home) && Directory.Exists(home))
        {
            AppPaths.SetRoot(home);
            return;
        }
        var env = Environment.GetEnvironmentVariable("DIP3_HOME");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
        {
            AppPaths.SetRoot(env);
            return;
        }
        var baseDir = AppContext.BaseDirectory;
        if (Directory.Exists(Path.Combine(baseDir, "config")) ||
            Directory.Exists(Path.Combine(baseDir, "data")))
        {
            AppPaths.SetRoot(baseDir);
        }
        // 否则交给 AppPaths 默认逻辑（自 BaseDirectory 向上查找含 web/index.html + config 的目录）
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        MessageBox.Show(
            $"操作过程中出现未预期的错误：\n\n{e.Exception?.Message}\n\n" +
            "可以重试；若反复出现，请把此信息反馈给维护人员。",
            SettingsStore.Current.AppName ?? "DIP 3.0 分组器", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void ShowFatal(Exception ex)
    {
        MessageBox.Show(
            $"启动失败：{ex?.Message}\n\n" +
            "请确认程序目录下存在 data/ 与 config/ 两个子目录（绿色版应整体解压后使用），\n" +
            "或通过 --home <目录> 指定数据目录。",
            "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
