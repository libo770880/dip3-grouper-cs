using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Dip3Grouper;

/// <summary>服务器发布的客户端版本信息（来自 <c>GET /api/app/version</c>）。</summary>
public sealed class AppVersionInfo
{
    /// <summary>版本号，形如 1.6.0</summary>
    public string Version { get; set; }

    /// <summary>发布通道（stable / beta），仅作展示</summary>
    public string Channel { get; set; }

    /// <summary>安装包地址：可为相对路径（走服务器 /api/app/package）或绝对 URL</summary>
    public string Url { get; set; }

    /// <summary>安装包大小（字节，0 = 未知）</summary>
    public long Size { get; set; }

    /// <summary>安装包 SHA-256（小写十六进制；为空则跳过校验）</summary>
    public string Sha256 { get; set; }

    /// <summary>更新说明（多行）</summary>
    public string Notes { get; set; }

    /// <summary>发布时间（ISO）</summary>
    public string PublishedAt { get; set; }

    /// <summary>是否强制更新（低于最低版本时必须更新才能继续用）</summary>
    public bool Mandatory { get; set; }

    /// <summary>最低可运行版本；本机低于它时必须更新</summary>
    public string MinVersion { get; set; }

    public string SizeText => Size > 0 ? DataCatalog.SizeText(Size) : "—";

    public string PublishedText
        => DateTimeOffset.TryParse(PublishedAt, out var t) ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "—";
}

/// <summary>一次「检查客户端更新」的结果。</summary>
public sealed class AppUpdateCheck
{
    public bool Ok { get; set; }
    public string Message { get; set; }
    public string Server { get; set; }
    public string CurrentVersion { get; set; }

    /// <summary>服务器上的版本信息（服务器未发布时为 null）</summary>
    public AppVersionInfo Info { get; set; }

    /// <summary>是否有可用更新</summary>
    public bool HasUpdate { get; set; }

    /// <summary>是否强制更新</summary>
    public bool Mandatory { get; set; }

    public string RemoteVersion => Info?.Version ?? "—";
}

/// <summary>下载结果。</summary>
public sealed class AppUpdateDownload
{
    public bool Ok { get; set; }
    public string Message { get; set; }
    public string FilePath { get; set; }
    public long Size { get; set; }
}

/// <summary>一次「应用更新」的执行计划（解包后的暂存目录 + 待替换的目标目录 + 重启脚本）。</summary>
public sealed class AppUpdatePlan
{
    public string Version { get; set; }
    public string StageDir { get; set; }
    public string AppDir { get; set; }
    public string ExePath { get; set; }
    public string ScriptPath { get; set; }
    public string BackupDir { get; set; }
    public int FileCount { get; set; }

    public string Summary => $"待替换 {FileCount} 个文件 → {AppDir}";
}

/// <summary>本机客户端版本号与版本比较。</summary>
public static class AppVersion
{
    /// <summary>读不到程序集版本时的兜底</summary>
    public const string Fallback = "1.5.0";

    private static string _cur;

    /// <summary>
    /// 当前程序版本：取入口程序集的 <c>AssemblyInformationalVersion</c>
    /// （由 csproj 的 <c>&lt;Version&gt;</c> 决定），读不到时退回 <see cref="Fallback"/>。
    /// </summary>
    public static string Current
    {
        get
        {
            if (_cur != null) return _cur;
            var v = "";
            try
            {
                var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(info)) v = info.Split('+')[0].Trim();
            }
            catch { /* 反射失败无所谓 */ }
            if (v.Length == 0 || v == "1.0.0") v = Fallback;
            _cur = v;
            return v;
        }
    }

    /// <summary>数值化版本（忽略非数字后缀），如 "1.6.0-beta.2" → [1,6,0]</summary>
    private static int[] Parts(string v)
    {
        var head = (v ?? "").Trim().Split('-', '+')[0];
        var nums = new List<int>();
        foreach (var seg in head.Split('.'))
        {
            var digits = new string(seg.TakeWhile(char.IsAsciiDigit).ToArray());
            nums.Add(digits.Length > 0 && int.TryParse(digits, out var n) ? n : 0);
        }
        while (nums.Count < 3) nums.Add(0);
        return nums.ToArray();
    }

    /// <summary>版本比较：a &gt; b 返回正数，相等 0，a &lt; b 负数。空值视为 -1（永远更新）。</summary>
    public static int Compare(string a, string b)
    {
        var va = string.IsNullOrWhiteSpace(a) ? "-1" : a;
        var vb = string.IsNullOrWhiteSpace(b) ? "-1" : b;
        var pa = Parts(va);
        var pb = Parts(vb);
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var x = i < pa.Length ? pa[i] : 0;
            var y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    /// <summary>是否为合法版本号（至少三段数字，或两段亦可）</summary>
    public static bool LooksValid(string v)
        => !string.IsNullOrWhiteSpace(v) && Parts(v).Length >= 2 && v.Trim().Any(char.IsAsciiDigit);
}

/// <summary>
/// 客户端自动更新：从局域网更新源检查版本、下载安装包、校验 SHA-256、解包并替换程序目录，
/// 最后用一段纯 ASCII 的批处理在程序退出后完成替换与重启。
///
/// <para>与「数据更新」（<see cref="DataUpdateClient"/>，只换 <c>data/</c> 与 <c>config/</c> 里的业务数据）
/// 是两件不同的事：这里换的是**程序本体**，所以必须先把进程关掉才能覆盖 exe/dll ——
/// 这正是要把替换动作交给独立批处理的原因（进程自己不能替换正在使用的文件）。</para>
///
/// <para>服务器侧只需两个只读接口：<c>GET /api/app/version</c>（版本信息）与
/// <c>GET /api/app/package</c>（安装包 zip）；管理员把新版发布的 zip 与清单放到
/// <c>data/app-update/</c> 即可，不需要额外的分发服务。</para>
/// </summary>
public static class AppUpdateClient
{
    /// <summary>服务器侧发布目录</summary>
    public static string ServerDir => Path.Combine(AppPaths.DataDir, "app-update");
    public static string ServerManifest => Path.Combine(ServerDir, "manifest.json");
    public static string ServerPackage => Path.Combine(ServerDir, "package.zip");

    /// <summary>本机暂存目录（与程序同盘，保证替换是同卷操作）</summary>
    public static string UpdateDir => Path.Combine(AppPaths.DataDir, "_update");
    public static string StageDir => Path.Combine(UpdateDir, "stage");

    /// <summary>程序目录（绿色版解压出来的那个目录）</summary>
    public static string AppDir
    {
        get
        {
            if (!string.IsNullOrEmpty(AppDirOverride)) return AppDirOverride;
            var p = Environment.ProcessPath;
            var dir = string.IsNullOrEmpty(p) ? AppContext.BaseDirectory : Path.GetDirectoryName(p);
            return string.IsNullOrEmpty(dir) ? AppContext.BaseDirectory : dir;
        }
    }

    /// <summary>
    /// 覆盖「程序目录」（默认 = 当前进程所在目录）。
    /// 仅供自动化自检使用：在临时目录里验证替换计划，绝不碰真实安装目录。
    /// </summary>
    public static string AppDirOverride { get; set; }

    /// <summary>主程序文件名（更新包里第一个命中的就是它）</summary>
    public const string MainExeName = "Dip3Grouper.Desktop.exe";

    // ==================================================================
    // 服务器侧：生成清单（发布方调用）
    // ==================================================================

    /// <summary>
    /// 依据 <c>data/app-update/</c> 里的实际文件生成/刷新清单。
    /// 发布方只需把 zip 放进去，清单里的大小与哈希由本方法来算，避免手抄出错。
    /// </summary>
    public static AppVersionInfo BuildServerManifest(string version, string notes = null, bool mandatory = false,
        string minVersion = null, string channel = "stable")
    {
        var info = new AppVersionInfo
        {
            Version = version,
            Channel = channel,
            Url = "app/package",
            Notes = notes,
            PublishedAt = Js.Iso(),
            Mandatory = mandatory,
            MinVersion = minVersion,
            Size = File.Exists(ServerPackage) ? new FileInfo(ServerPackage).Length : 0,
            Sha256 = File.Exists(ServerPackage) ? DataCatalog.Sha256(ServerPackage) : null,
        };
        Directory.CreateDirectory(ServerDir);
        Js.WriteJsonFile(ServerManifest, info, indent: 2);
        return info;
    }

    /// <summary>服务器侧当前版本信息（无发布文件时返回 null）</summary>
    public static AppVersionInfo ServerInfo()
    {
        if (!File.Exists(ServerManifest)) return null;
        try
        {
            var info = Js.ReadJsonFile<AppVersionInfo>(ServerManifest);
            if (info == null || string.IsNullOrWhiteSpace(info.Version)) return null;
            // 大小与哈希以磁盘上的包为准（清单可能是手工写的或包被替换过）
            if (File.Exists(ServerPackage))
            {
                info.Size = new FileInfo(ServerPackage).Length;
                var sha = DataCatalog.Sha256(ServerPackage);
                if (!string.IsNullOrWhiteSpace(sha)) info.Sha256 = sha;
            }
            if (string.IsNullOrWhiteSpace(info.Url)) info.Url = "app/package";
            return info;
        }
        catch { return null; }
    }

    // ==================================================================
    // 客户端：检查 / 下载 / 解包 / 重启
    // ==================================================================

    private static HttpClient Make(int timeoutSeconds)
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        h.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Dip3Grouper-Desktop");
        return h;
    }

    /// <summary>检查客户端更新（不落盘、不改动任何文件）。</summary>
    public static async Task<AppUpdateCheck> CheckAsync(string baseUrl, CancellationToken ct = default)
    {
        var res = new AppUpdateCheck
        {
            Server = DataUpdateClient.Normalize(baseUrl),
            CurrentVersion = AppVersion.Current,
        };
        if (res.Server.Length == 0)
        {
            res.Message = "请先填写更新源地址。";
            return res;
        }

        try
        {
            using var h = Make(15);
            var url = DataUpdateClient.WithToken(res.Server + "/api/app/version");
            var text = await h.GetStringAsync(url, ct);
            var info = Js.FromJson<AppVersionInfo>(text);
            if (info == null || string.IsNullOrWhiteSpace(info.Version))
            {
                // 更新源在跑，只是管理员还没发布新版 —— 这是正常状态，不是错误
                res.Ok = true;
                res.HasUpdate = false;
                res.Message = $"更新源尚未发布客户端版本（本机 {AppVersion.Current}）。";
                return res;
            }

            res.Info = info;
            res.Ok = true;
            res.HasUpdate = AppVersion.Compare(info.Version, AppVersion.Current) > 0;
            res.Mandatory = res.HasUpdate &&
                            (info.Mandatory || (AppVersion.LooksValid(info.MinVersion)
                                                && AppVersion.Compare(AppVersion.Current, info.MinVersion) < 0));

            res.Message = res.HasUpdate
                ? $"发现新版本 {info.Version}（当前 {AppVersion.Current}）"
                  + (res.Mandatory ? "，此版本为必需更新。" : "。")
                : $"已是最新版本（{AppVersion.Current}）。";
        }
        catch (Exception e)
        {
            // 更新源没开这个接口时给一句人话，而不是把 404 直接丢给用户
            res.Ok = false;
            res.Message = e is HttpRequestException he && he.StatusCode == HttpStatusCode.NotFound
                ? "更新源上没有客户端更新接口，可能对方跑的是旧版本。"
                : DataUpdateClient.Friendly(e);
        }
        return res;
    }

    /// <summary>下载安装包到本机暂存目录并校验 SHA-256。</summary>
    public static async Task<AppUpdateDownload> DownloadAsync(string baseUrl, AppVersionInfo info,
        Action<int, string> progress = null, CancellationToken ct = default)
    {
        var res = new AppUpdateDownload();
        if (info == null || string.IsNullOrWhiteSpace(info.Version))
        {
            res.Message = "没有可下载的版本信息。";
            return res;
        }

        var root = DataUpdateClient.Normalize(baseUrl);
        var url = string.IsNullOrWhiteSpace(info.Url) ? "app/package"
            : info.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
              info.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? info.Url
                : root + "/api/" + info.Url.TrimStart('/');

        try
        {
            Directory.CreateDirectory(UpdateDir);
            var dest = Path.Combine(UpdateDir, $"app-{info.Version}.zip");
            progress?.Invoke(2, "正在连接更新源…");

            using var h = Make(600);
            using var resp = await h.GetAsync(DataUpdateClient.WithToken(url),
                HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? info.Size;

            var part = dest + ".part";
            long got = 0;
            using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var src = await resp.Content.ReadAsStreamAsync(ct))
            {
                var buf = new byte[1 << 20];
                int n;
                while ((n = await src.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, n), ct);
                    got += n;
                    var pct = total > 0 ? (int)(got * 90 / total) : 0;
                    progress?.Invoke(Math.Max(2, Math.Min(92, pct)), $"正在下载 {DataCatalog.SizeText(got)}…");
                }
            }
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(part, dest);

            if (!string.IsNullOrWhiteSpace(info.Sha256))
            {
                progress?.Invoke(95, "正在校验安装包…");
                var sha = Sha256(dest);
                if (!string.Equals(sha, info.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(dest); } catch { }
                    res.Message = "安装包校验失败（SHA-256 不一致），已删除下载内容。";
                    return res;
                }
            }

            res.Ok = true;
            res.FilePath = dest;
            res.Size = new FileInfo(dest).Length;
            res.Message = $"已下载 {DataCatalog.SizeText(res.Size)}。";
            progress?.Invoke(100, "下载完成");
        }
        catch (Exception e)
        {
            res.Message = DataUpdateClient.Friendly(e);
        }
        return res;
    }

    /// <summary>
    /// 解包安装包到暂存目录，生成重启脚本，返回执行计划。
    /// 本方法**不改动程序目录**，只是把替换这件事准备妥当。
    /// </summary>
    public static AppUpdatePlan PrepareApply(string zipPath, string version)
    {
        if (string.IsNullOrEmpty(zipPath) || !File.Exists(zipPath))
            throw new InvalidOperationException("安装包不存在。");

        ResetDir(StageDir);
        Directory.CreateDirectory(StageDir);
        ZipFile.ExtractToDirectory(zipPath, StageDir, true);

        // 安装包可能把文件放在一个顶层目录里（Windows 资源管理器右键「压缩」就是这种），
        // 此时把该目录当作真正的源根。
        var sourceRoot = StageDir;
        var entries = Directory.GetFileSystemEntries(StageDir);
        if (entries.Length == 1 && Directory.Exists(entries[0]) && !File.Exists(entries[0]))
        {
            var inner = entries[0];
            if (File.Exists(Path.Combine(inner, MainExeName))) sourceRoot = inner;
        }

        var exe = FindMainExe(sourceRoot);
        if (exe == null)
            throw new InvalidOperationException($"安装包里没有找到 {MainExeName}，不是本程序的更新包。");

        var appDir = AppDir;
        var targetExe = Path.Combine(appDir, Path.GetFileName(exe));

        // 备份：把当前程序目录整份另存一份（同盘，随时可切回）
        var backup = Path.Combine(UpdateDir, "backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        try
        {
            Directory.CreateDirectory(backup);
            CopyTree(appDir, backup);
        }
        catch { backup = null; }

        var script = Path.Combine(UpdateDir, "apply-update.cmd");
        File.WriteAllText(script, UpdaterScript(), new ASCIIEncoding());

        var plan = new AppUpdatePlan
        {
            Version = version,
            StageDir = sourceRoot,
            AppDir = appDir,
            ExePath = targetExe,
            ScriptPath = script,
            BackupDir = backup,
            FileCount = Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories).Length,
        };
        return plan;
    }

    /// <summary>
    /// 从本地 zip 直接安装（离线兜底）：解包 → 生成计划。与在线下载后调用 <see cref="PrepareApply"/> 等价。
    /// </summary>
    public static AppUpdatePlan ApplyLocalPackage(string zipPath)
        => PrepareApply(zipPath, Path.GetFileNameWithoutExtension(zipPath) ?? "本地包");

    /// <summary>
    /// 启动替换脚本并让调用方随后退出程序。脚本会等待本进程退出，再覆盖程序目录并重启。
    /// </summary>
    public static void Launch(AppUpdatePlan plan, int waitPid)
    {
        if (plan == null) throw new InvalidOperationException("没有可执行的更新计划。");
        var args = $"/c \"\"{plan.ScriptPath}\" \"{waitPid}\" \"{plan.StageDir}\" \"{plan.AppDir}\" \"{plan.ExePath}\"\"";
        var psi = new ProcessStartInfo("cmd.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = plan.AppDir,
        };
        Process.Start(psi);
    }

    // ==================================================================
    // 替换脚本
    // ==================================================================

    /// <summary>
    /// 生成替换脚本。**必须是纯 ASCII**：cmd.exe 按 OEM 代码页读批处理文件，
    /// 任何中文都会变成乱码并被当成命令执行。可能含中文的路径一律由命令行参数传入
    /// （<c>cmd /c script.cmd "&lt;pid&gt;" "&lt;暂存&gt;" "&lt;程序目录&gt;" "&lt;exe&gt;"</c>），
    /// 走 CreateProcessW 的宽字符参数通道，不受代码页影响。
    /// </summary>
    public static string UpdaterScript() =>
        "@echo off\r\n" +
        "rem DIP 3.0 grouper - client self-update helper (ASCII only)\r\n" +
        "setlocal\r\n" +
        "set \"PID=%~1\"\r\n" +
        "set \"STAGE=%~2\"\r\n" +
        "set \"APP=%~3\"\r\n" +
        "set \"EXE=%~4\"\r\n" +
        "if \"%PID%\"==\"\" goto copy\r\n" +
        ":wait\r\n" +
        "tasklist /FI \"PID eq %PID%\" /NH 2>nul | find \"%PID%\" >nul\r\n" +
        "if not errorlevel 1 (\r\n" +
        "  ping -n 2 127.0.0.1 >nul\r\n" +
        "  goto wait\r\n" +
        ")\r\n" +
        ":copy\r\n" +
        "if exist \"%STAGE%\\\" xcopy /E /Y /I /Q \"%STAGE%\\*\" \"%APP%\\\" >nul\r\n" +
        "if exist \"%EXE%\" start \"\" \"%EXE%\"\r\n" +
        "if exist \"%STAGE%\\\" rd /S /Q \"%STAGE%\" >nul 2>nul\r\n" +
        "(goto) 2>nul & del \"%~f0\"\r\n";

    // ==================================================================
    // 内部工具
    // ==================================================================

    /// <summary>在目录里找主程序（先找根目录，再递归）。</summary>
    internal static string FindMainExe(string root)
    {
        if (!Directory.Exists(root)) return null;
        var direct = Path.Combine(root, MainExeName);
        if (File.Exists(direct)) return direct;
        try
        {
            var hit = Directory.GetFiles(root, MainExeName, SearchOption.AllDirectories).FirstOrDefault();
            return hit;
        }
        catch { return null; }
    }

    private static string Sha256(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    private static void ResetDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* 删不掉就地覆盖 */ }
    }

    /// <summary>递归复制目录（不删除目标已有内容）。</summary>
    private static void CopyTree(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, f);
            var target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            try { File.Copy(f, target, true); } catch { /* 正在被占用的文件跳过 */ }
        }
    }
}
