using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;

namespace Dip3GrouperApp.Desktop;

/// <summary>
/// GitHub Release 检查更新。
/// 原理：启动后（以及点「检查更新」时）请求
/// https://api.github.com/repos/&lt;owner&gt;/&lt;repo&gt;/releases/latest，
/// 把返回的 tag_name（如 v1.0.1）与本程序内置版本号比较，更新则弹提示、
/// 「打开下载页」用系统默认浏览器跳到 Release 页面下载新包。
/// 仓库是公开的，因此整个流程不需要任何 Token / 登录。
/// 发布新版本的固定动作：改 <see cref="CurrentVersion"/> → publish-desktop.bat 重打包 →
/// zip 安装目录 → GitHub 新建 Release（tag 用 vX.Y.Z）→ 上传 zip 资产。
/// 检查失败（无网 / 被墙 / 限流）一律静默或提示失败，绝不影响主功能。
/// </summary>
internal static class UpdateChecker
{
    /// <summary>本程序当前版本号（发布新包时必须同步改这里）</summary>
    public const string CurrentVersion = "1.0.1";

    private const string RepoOwner = "libo770880";
    private const string RepoName = "dip3-grouper-cs";
    private const string LatestApi = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
    private const string ReleasesPage = $"https://github.com/{RepoOwner}/{RepoName}/releases";

    private static readonly HttpClient Http = new()
    {
        // 检查更新不能拖慢启动：8 秒拿不到就当没有
        Timeout = TimeSpan.FromSeconds(8),
    };

    static UpdateChecker()
    {
        // GitHub API 要求所有请求带 User-Agent，否则 403
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("DIP3-Desktop-Updater/" + CurrentVersion);
    }

    public sealed record ReleaseInfo(string Tag, string Name, string Url, string Notes, string PublishedAt);

    /// <summary>取最新 Release 信息；无 Release 或网络失败返回 null（静默）。</summary>
    public static async Task<ReleaseInfo> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync(LatestApi, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag)) return null;
            return new ReleaseInfo(
                tag.Trim(),
                root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                root.TryGetProperty("html_url", out var u) ? u.GetString() ?? ReleasesPage : ReleasesPage,
                root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                root.TryGetProperty("published_at", out var p) ? p.GetString() ?? "" : "");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 语义化版本比较：latestTag 是否比 <see cref="CurrentVersion"/> 新。
    /// 接受 v1.2.3 / 1.2.3 / 1.2 三种写法；逐段比数字，段数不足补 0；解析不了返回 false。
    /// </summary>
    public static bool IsNewer(string latestTag)
    {
        var cur = ParseVersion(CurrentVersion);
        var lat = ParseVersion(latestTag);
        if (cur is null || lat is null) return false;
        for (var i = 0; i < Math.Max(cur.Length, lat.Length); i++)
        {
            var a = i < cur.Length ? cur[i] : 0;
            var b = i < lat.Length ? lat[i] : 0;
            if (a != b) return b > a;
        }
        return false;
    }

    private static int[] ParseVersion(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s[1..];
        var dot = s.IndexOfAny(['-', '+']);
        if (dot >= 0) s = s[..dot];
        var parts = s.Split('.');
        if (parts.Length is < 1 or > 4) return null;
        var nums = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out var n) || n < 0) return null;
            nums[i] = n;
        }
        return nums;
    }

    /// <summary>
    /// 启动后的静默检查：有新版才打扰用户（弹一个选择框），其余情况（最新 / 无网）一声不吭。
    /// </summary>
    public static async void CheckOnStartup(Window owner)
    {
        var rel = await CheckAsync().ConfigureAwait(true);
        if (rel is null || !IsNewer(rel.Tag)) return;
        if (owner is null || !owner.IsLoaded) return;
        Prompt(owner, rel, silentWhenLatest: true);
    }

    /// <summary>
    /// 手动「检查更新」：把结果明确告诉用户（已是最新也报），失败也报。
    /// 返回给按钮刷新提示条用的短文案。
    /// </summary>
    public static async Task<string> CheckManualAsync(Window owner)
    {
        var rel = await CheckAsync().ConfigureAwait(true);
        if (rel is null)
        {
            MessageBox.Show(owner, "检查更新失败：连不上 GitHub。\n\n请检查网络后重试；不影响软件其它功能。",
                "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            return "检查失败";
        }
        if (!IsNewer(rel.Tag))
        {
            MessageBox.Show(owner, $"已是最新版本 v{CurrentVersion}。\n\n（GitHub 最新 Release：{rel.Tag}）",
                "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
            return "已是最新";
        }
        Prompt(owner, rel, silentWhenLatest: false);
        return "发现新版 " + rel.Tag;
    }

    private static void Prompt(Window owner, ReleaseInfo rel, bool silentWhenLatest)
    {
        var notes = string.IsNullOrWhiteSpace(rel.Notes) ? "" :
            (rel.Notes.Length > 600 ? rel.Notes[..600] + "…" : rel.Notes);
        var msg = $"发现新版本 {rel.Tag}（当前 v{CurrentVersion}）\n" +
                  $"发布时间：{(string.IsNullOrWhiteSpace(rel.PublishedAt) ? "未知" : rel.PublishedAt)}\n\n" +
                  (string.IsNullOrEmpty(notes) ? "" : notes + "\n\n") +
                  "是否打开浏览器前往下载页？";
        var go = MessageBox.Show(owner, msg, "软件更新",
            MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No);
        if (go == MessageBoxResult.Yes) OpenReleasesPage(rel.Url);
    }

    private static void OpenReleasesPage(string url)
    {
        try
        {
            // UseShellExecute=true：走系统默认浏览器，不需要额外依赖
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开浏览器失败：" + ex.Message + $"\n\n请手动访问：{ReleasesPage}",
                "软件更新", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
