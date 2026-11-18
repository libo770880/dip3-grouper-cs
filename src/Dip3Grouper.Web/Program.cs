using Dip3Grouper;
using Dip3Grouper.Web;

// =====================================================================
// DIP 3.0 分组器（C# 版）Web 服务 —— 命令行 / 服务托管入口
//   启动: Dip3Grouper.Web [--port 3210] [--jiceng] [--home <工程根>]
//   页面: http://localhost:3210        后台: http://localhost:3210/admin
//
// 绿色包里普通用户双击的是 Dip3Grouper.WebShell.exe（托盘 + 面板，无黑框）；
// 这个 exe 留给运维：做 Windows 服务、写计划任务、或者需要在控制台看日志时用。
// =====================================================================

var opts = new Dictionary<string, string>();
var flags = new HashSet<string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--jiceng") { flags.Add("jiceng"); continue; }
    if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
        opts[args[i][2..]] = args[++i];
}
if (opts.TryGetValue("home", out var home)) AppPaths.SetRoot(home);
var port = opts.TryGetValue("port", out var pv) && int.TryParse(pv, out var pn) && pn > 0 ? pn : WebServer.DefaultPort;
var jiceng = flags.Contains("jiceng");

var state = new AppState();
state.Init(jiceng);

var host = WebServer.Build(port, state);

ConsoleSetup.UseUtf8();
Console.WriteLine($"DIP 3.0 分组器（C# 版）Web 界面已启动: http://localhost:{port}");
Console.WriteLine($"后台管理: http://localhost:{port}/admin");
// 基层兜底是分组口径的开关，默认关闭；只有真开了才占一行提示，免得每台机器都看到这句废话
if (jiceng) Console.WriteLine("基层病种兜底: 开");

host.Run();
