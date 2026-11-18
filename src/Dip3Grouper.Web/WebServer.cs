using System.Net.Sockets;
using Dip3Grouper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dip3Grouper.Web;

/// <summary>
/// Web 服务的构建逻辑。
///
/// 两个宿主共用这一份：
///   1) 命令行 / 服务托管：Dip3Grouper.Web.exe（Program.cs，一屏控制台）；
///   2) 绿色包里的「软件版」：Dip3Grouper.WebShell.exe（托盘 + 小面板，无黑框）。
///
/// 之所以抽出来而不是让壳去启动子进程：单进程即单份数据、单份日志，
/// 关窗口就是真退出，不会留下孤儿 Dip3Grouper.Web.exe 占着 3210 端口。
/// </summary>
public static class WebServer
{
    /// <summary>默认端口，与桌面端、发布脚本、使用说明保持一致。</summary>
    public const int DefaultPort = 3210;

    /// <summary>构建（未启动）HTTP 宿主。</summary>
    public static IHost Build(int port, AppState state)
    {
        return new HostBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Warning);
            })
            .ConfigureWebHostDefaults(web =>
            {
                // 监听 0.0.0.0：科室局域网内的其它电脑要能访问 http://<本机IP>:3210
                web.UseUrls($"http://0.0.0.0:{port}");
                web.ConfigureServices(services =>
                {
                    services.Configure<FormOptions>(o =>
                    {
                        o.MultipartBodyLengthLimit = long.MaxValue;
                        o.ValueLengthLimit = int.MaxValue;
                        o.MultipartHeadersLengthLimit = int.MaxValue;
                    });
                });
                web.Configure(app => app.Run(ctx => WebRouter.Handle(ctx, state)));
            })
            .Build();
    }

    /// <summary>
    /// 该端口上是否已经有实例在应答。
    /// 托盘壳重复双击、或用户先跑了命令行版时，直接复用已有服务，不重复占端口。
    /// </summary>
    public static bool IsAlive(int port, int timeoutMs = 700)
    {
        try
        {
            using var c = new TcpClient();
            var task = c.ConnectAsync("127.0.0.1", port);
            if (!task.Wait(timeoutMs)) return false;
            return c.Connected;
        }
        catch { return false; }
    }
}
