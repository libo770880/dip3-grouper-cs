using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Dip3Grouper;
using Dip3Grouper.Web;

namespace Dip3GrouperApp.Shell;

/// <summary>
/// 「软件版」入口：双击直接起数据服务，开浏览器，托盘常驻。
///
///   Dip3Grouper.WebShell.exe [--port 3210] [--jiceng] [--no-browser] [--home &lt;工程根&gt;]
///
/// 同一个端口上已有实例（例如运维已经跑着命令行版）时，不再抢端口，
/// 直接把页面开出来给用户，然后自己退出。
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var port = WebServer.DefaultPort;
        var jiceng = false;
        var autoOpen = true;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--jiceng":
                    jiceng = true;
                    break;
                case "--no-browser":
                    autoOpen = false;
                    break;
                case "--port" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var p) && p > 0) port = p;
                    break;
                case "--home" when i + 1 < args.Length:
                    AppPaths.SetRoot(args[++i]);
                    break;
            }
        }

        try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { /* manifest 已声明时忽略 */ }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 壳是 WinExe，没有控制台可以看堆栈。任何未处理异常都落盘到 data\_logs\shell-error.log，
        // 否则用户只会看到"窗口闪一下就没了"，运维也无从查起。
        Application.ThreadException += (_, e) => Shell.LogCrash(e.Exception, "UI 线程");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Shell.LogCrash(e.ExceptionObject as Exception, "后台线程");

        // 单实例：双击第二次不要再起一个进程抢 3210；已有服务则直接把页面开出来
        using var mutex = new Mutex(true, @"Local\Dip3Grouper.WebShell", out var isFirst);
        if (!isFirst || WebServer.IsAlive(port))
        {
            // 用户是"再点一次图标"才走到这里，所以只要没显式关掉自动打开，就把页面亮出来，
            // 免得他以为程序没反应。--no-browser 时保持安静（脚本/冒烟测试用）。
            if (autoOpen) Shell.OpenBrowser($"http://localhost:{port}");
            return;
        }

        Application.Run(new ServerForm(port, jiceng, autoOpen));
    }
}

/// <summary>壳里零散的窗口级工具。</summary>
internal static class Shell
{
    public static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* 没有默认浏览器时静默失败，地址在面板上可以手动复制 */ }
    }

    public static void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    /// <summary>临时排查用：把关键节点写进 data\_logs\shell-debug.log。</summary>
    public static void LogLine(string text)
    {
        try
        {
            var dir = Path.Combine(AppPaths.Root, "data", "_logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "shell-debug.log"),
                $"[{DateTime.Now:HH:mm:ss}] {text}\r\n", System.Text.Encoding.UTF8);
        }
        catch { }
    }

    /// <summary>未处理异常落盘，便于远程排查（壳没有控制台）。</summary>
    public static void LogCrash(Exception ex, string where)
    {
        try
        {
            var dir = Path.Combine(AppPaths.Root, "data", "_logs");
            Directory.CreateDirectory(dir);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}: {ex?.GetType().Name} {ex?.Message}\r\n{ex?.StackTrace}\r\n";
            File.AppendAllText(Path.Combine(dir, "shell-error.log"), line, System.Text.Encoding.UTF8);
        }
        catch { }
    }

    /// <summary>一条可分享的局域网入口。</summary>
    public sealed class LanEndpoint
    {
        public string Url;
        public string Ip;
        public string Adapter;   // 网卡显示名（"WLAN" / "以太网" / "VMware Network Adapter VMnet8"）
        public string Kind;      // 面向用户的中文类型：以太网 / 无线 / 虚拟网卡 / 其他
    }

    /// <summary>
    /// 局域网地址：面板上要让同事照抄的那一条。
    ///
    /// 不能用"第一块已连接网卡" —— 装了 VMware / VirtualBox / Hyper-V 的机器上，
    /// 枚举顺序常把 VMnet1(192.168.x.1)、vEthernet(172.x) 排在真正的 Wi-Fi 前面，
    /// 抄给别人一定连不上。这里按「有默认网关 &gt; 有线/无线 &gt; 私有网段」打分，
    /// 虚拟网卡直接扣到底，再把 169.254 自动地址排除。
    /// </summary>
    public static string LanUrl(int port) => LanEndpoints(port).FirstOrDefault()?.Url ?? $"http://localhost:{port}";

    /// <summary>按可信度排序的局域网候选（最多 6 条），第一条就是面板上展示的地址。</summary>
    public static List<LanEndpoint> LanEndpoints(int port)
    {
        var scored = new List<(int Score, LanEndpoint Ep)>();
        var seen = new HashSet<string>();

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                var virtualCard = IsVirtualCard(ni);
                var props = ni.GetIPProperties();
                var hasGateway = props.GatewayAddresses.Any(g =>
                    g.Address != null &&
                    g.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !g.Address.Equals(IPAddress.Any));

                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = ua.Address.ToString();
                    if (ip.StartsWith("127.") || !seen.Add(ip)) continue;

                    var score = 0;
                    if (hasGateway) score += 60;
                    score += ni.NetworkInterfaceType switch
                    {
                        NetworkInterfaceType.Ethernet => 40,
                        NetworkInterfaceType.GigabitEthernet => 40,
                        NetworkInterfaceType.FastEthernetT => 40,
                        NetworkInterfaceType.Wireless80211 => 35,
                        _ => 0,
                    };
                    if (IsPrivate(ip)) score += 25;
                    if (ip.StartsWith("169.254.")) score -= 200;   // 没拿到 DHCP，等于不可用
                    if (virtualCard) score -= 120;

                    scored.Add((score, new LanEndpoint
                    {
                        Url = $"http://{ip}:{port}",
                        Ip = ip,
                        Adapter = ni.Name,
                        Kind = virtualCard ? "虚拟网卡" : KindOf(ni.NetworkInterfaceType),
                    }));
                }
            }
        }
        catch { /* 取不到网卡信息时退回 localhost，面板仍可用 */ }

        return scored
            .OrderByDescending(x => x.Score)
            .Select(x => x.Ep)
            .Take(6)
            .ToList();
    }

    private static string KindOf(NetworkInterfaceType t) => t switch
    {
        NetworkInterfaceType.Wireless80211 => "无线 Wi-Fi",
        NetworkInterfaceType.Ethernet => "以太网",
        NetworkInterfaceType.GigabitEthernet => "以太网",
        NetworkInterfaceType.FastEthernetT => "以太网",
        _ => "其他网卡",
    };

    private static bool IsPrivate(string ip)
    {
        if (ip.StartsWith("10.")) return true;
        if (ip.StartsWith("192.168.")) return true;
        if (ip.StartsWith("172."))
        {
            var p = ip.Split('.');
            if (p.Length > 1 && int.TryParse(p[1], out var b) && b >= 16 && b <= 31) return true;
        }
        return false;
    }

    /// <summary>虚拟/旁路网卡：VMware、VirtualBox、Hyper-V、各类 VPN 与抓包驱动。</summary>
    private static bool IsVirtualCard(NetworkInterface ni)
    {
        var hay = (ni.Name + " " + ni.Description).ToLowerInvariant();
        foreach (var k in VirtualHints)
            if (hay.Contains(k)) return true;
        return false;
    }

    private static readonly string[] VirtualHints =
    {
        "vmware", "virtualbox", "vbox", "hyper-v", "vethernet", "virtual", "tap-windows",
        "tap adapter", "tun", "wintun", "wireguard", "openvpn", "zerotier", "tailscale",
        "radmin", "npcap", "loopback", "bluetooth", "docker", "vmnet", "虚拟",
    };
}
