using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace Dip3Grouper;

/// <summary>
/// 解读辅导「原文检查」。
///
/// <para>国家医保局的解读辅导是一份固定地址的 WPS 文件（每期往里追加，地址不变）：
/// <c>module/download/downfile.jsp?classid=0&amp;filename=976ce73a…wps</c>。
/// 正因为地址不变，软件可以把地址写死，用户不必再去官网找链接、下载、转换、导入——
/// 点一下按钮就能判断「官方有没有出新一期」。</para>
///
/// <para>判断依据是<b>文件字节的 SHA-256</b>：该下载接口不返回 ETag，Last-Modified 也没有，
/// Content-Length 相同不代表内容相同，只有哈希才算数。255 KB 的原文一次拉全量约 1~3 秒，
/// 比任何「增量探测」都简单可靠。</para>
///
/// <para>检查永远是「只读」的：<see cref="Compare"/> 只算差异，不写库；
/// 用户在弹窗里点「是」之后才走 <see cref="Apply"/>，且先备份整份 lixiang.json。
/// 这样即使新一期解析出问题，也只是一次可回滚的导入，而不是不可逆的覆盖。</para>
/// </summary>
public static class JieduSource
{
    /// <summary>国家医保局解读辅导原文（固定地址，文件内容随期次追加而变）</summary>
    public const string Url =
        "https://www.nhsa.gov.cn/module/download/downfile.jsp?classid=0&filename=976ce73a34c94bdab08920c94d2b5df6.wps";

    /// <summary>原文文件名（写进 lixiang.json 的 source 字段，界面上显示就是这个短名）</summary>
    public const string FileName = "976ce73a34c94bdab08920c94d2b5df6.wps";

    /// <summary>官网栏目页，下载失败时给用户手动下载的入口</summary>
    public const string PortalUrl = "https://www.nhsa.gov.cn/";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        // 医保局对无 UA 的请求会拦；Referer 也按浏览器行为带上
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        c.DefaultRequestHeaders.Referrer = new Uri(PortalUrl);
        return c;
    }

    /// <summary>下载原文。失败抛 <see cref="JieduSourceException"/>（消息已经翻译成人话）。</summary>
    public static async Task<byte[]> DownloadAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync(Url, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new JieduSourceException($"官方服务器返回 {(int)resp.StatusCode} {resp.ReasonPhrase}");
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes is null || bytes.Length < 1024)
                throw new JieduSourceException("下载到的文件异常（内容不足 1 KB），可能被网络网关拦成了提示页");
            return bytes;
        }
        catch (JieduSourceException) { throw; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new JieduSourceException("连接超时（60 秒），当前网络访问不了国家医保局");
        }
        catch (HttpRequestException e)
        {
            throw new JieduSourceException("连不上国家医保局：" + e.Message);
        }
        catch (Exception e)
        {
            throw new JieduSourceException("下载失败：" + e.Message);
        }
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// 把下载到的字节解析成条目，并与库里现有解读比对。
    /// <para>比对键是（期次, 题号）。文本比较前统一去掉所有空白/换行——
    /// 原文排版换行位置随 WPS 版本会漂，逐字节比会把「只换了个行」误报成「内容变了」。</para>
    /// </summary>
    public static JieduCheckResult Compare(LixiangStore store, byte[] raw)
    {
        var text = DocToText.Extract(raw, ".wps");
        var (preface, items) = LixiangStore.ParseJieduText(text);
        var r = CompareItems(store.State.Jiedu?.List, items, preface);
        r.RemoteHash = Sha256(raw);
        r.RemoteSize = raw.LongLength;
        return r;
    }

    /// <summary>
    /// 条目层面的比对（不碰文件、不碰网络，自检可直接喂数据）。
    /// <para>比对键是（期次, 题号）。文本比较前统一去掉所有空白/换行——
    /// 原文排版换行位置随 WPS 版本会漂，逐字节比会把「只换了个行」误报成「内容变了」。</para>
    /// <para>「原文少了 N 条」只提示、不删除、也不单独触发更新：本地可能有人工补充的条目，
    /// 且国家局原文只追加不重写旧期，真出现删除更可能是解析抖动。</para>
    /// </summary>
    public static JieduCheckResult CompareItems(List<JieduItem> local, List<JieduItem> remote, string preface)
    {
        var loc = new Dictionary<(int, int), JieduItem>();
        foreach (var it in local ?? new List<JieduItem>()) loc[(it.Issue, it.No)] = it;   // 重复键后者覆盖，与 JieduImport 一致

        var rem = new Dictionary<(int, int), JieduItem>();
        foreach (var it in remote ?? new List<JieduItem>()) rem[(it.Issue, it.No)] = it;

        var r = new JieduCheckResult
        {
            Ok = true,
            RemoteTotal = rem.Count,
            LocalTotal = loc.Count,
            RemoteIssues = rem.Keys.Select(k => k.Item1).Distinct().OrderBy(x => x).ToList(),
            LocalIssues = loc.Keys.Select(k => k.Item1).Distinct().OrderBy(x => x).ToList(),
            Preface = preface,
            Items = rem.Values.OrderBy(x => x.Issue).ThenBy(x => x.No).ToList(),
        };
        r.NewIssues = r.RemoteIssues.Except(r.LocalIssues).ToList();

        foreach (var (k, v) in rem)
        {
            if (!loc.TryGetValue(k, out var old)) r.Added.Add(v);
            else if (Norm(old.Q) != Norm(v.Q) || Norm(old.A) != Norm(v.A)) r.Updated.Add(v);
        }
        foreach (var (k, v) in loc)
            if (!rem.ContainsKey(k)) r.Removed.Add(v);

        r.HasUpdate = r.Added.Count > 0 || r.Updated.Count > 0;
        return r;
    }

    /// <summary>比较用归一化：去掉全部空白与换行（不改变落库内容）。</summary>
    private static string Norm(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (!char.IsWhiteSpace(c)) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>增量集合：只有新增的和内容确实变化的条目才进导入管线（纯函数，自检可直接喂）。</summary>
    public static List<JieduItem> BuildDelta(JieduCheckResult r)
    {
        var delta = new List<JieduItem>(r.Added.Count + r.Updated.Count);
        delta.AddRange(r.Added);
        delta.AddRange(r.Updated);
        return delta;
    }

    /// <summary>
    /// 增量应用检查结果：先备份整份 lixiang.json，然后<b>只写入</b>「新增 + 修订」的条目，
    /// 其余条目（含人工补充、人工修订过的同号条目）一个字节都不动。
    /// <para>不做全量重导入：全量会把每条都过一遍 merge（内容相同的也被“更新”一遍），
    /// 前言、来源等元数据也全部重置——增量语义下这些都不该发生。
    /// 前言只在归一化后确实不同时才替换（官方改了前言说明才跟着变）。</para>
    /// <para>Removed 的条目<b>不删</b>，只在提示里告知。</para>
    /// </summary>
    /// <returns>备份文件路径（失败为空串）。</returns>
    public static string Apply(LixiangStore store, JieduCheckResult r)
    {
        var backup = BackupLixiang();

        // 增量集合：只有新增的和内容确实变化的条目才进导入管线
        var delta = BuildDelta(r);
        if (delta.Count > 0)
            store.JieduImport("", delta, "merge", FileName);   // 前言传空 = 保留现有前言

        var jd = store.State.Jiedu;
        jd.SourceHash = r.RemoteHash;
        jd.SourceSize = r.RemoteSize;
        if (string.IsNullOrEmpty(jd.Source)) jd.Source = FileName;

        // 官方确实改了前言才更新（比较前去掉空白，排版换行漂移不算变化）
        if (!string.IsNullOrWhiteSpace(r.Preface) &&
            Norm(jd.Preface ?? "") != Norm(r.Preface))
            jd.Preface = r.Preface;

        store.Save();
        return backup;
    }

    /// <summary>
    /// 内容一致（0 新增 0 修订）但本地没记指纹 / 指纹过期时补记一次。
    /// 这样「这份解读是不是官方原文」有据可查；幂等，同一份原文只写一次盘。
    /// </summary>
    /// <returns>是否真的写了盘。</returns>
    public static bool SyncFingerprint(LixiangStore store, JieduCheckResult r)
    {
        var jd = store?.State?.Jiedu;
        if (jd == null || string.IsNullOrEmpty(r?.RemoteHash)) return false;
        if (jd.SourceHash == r.RemoteHash) return false;

        jd.SourceHash = r.RemoteHash;
        jd.SourceSize = r.RemoteSize;
        if (string.IsNullOrEmpty(jd.Source)) jd.Source = FileName;
        try { store.Save(); return true; }
        catch (Exception e)
        {
            Console.Error.WriteLine("[jiedu] 回写原文指纹失败（仅影响记录，不影响数据）: " + e.Message);
            return false;
        }
    }

    /// <summary>把当前 lixiang.json 复制到 data/_backup/lixiang-yyyyMMdd-HHmmss.json，只留最近 10 份。</summary>
    public static string BackupLixiang()
    {
        try
        {
            if (!File.Exists(AppPaths.LixiangFile)) return "";
            var dir = Path.Combine(AppPaths.DataDir, "_backup");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"lixiang-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(AppPaths.LixiangFile, path, overwrite: true);

            foreach (var old in Directory.GetFiles(dir, "lixiang-*.json")
                         .OrderByDescending(f => f).Skip(10))
            {
                try { File.Delete(old); } catch { /* 备份清不掉不影响本次 */ }
            }
            return path;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("[jiedu] 备份 lixiang.json 失败: " + e.Message);
            return "";
        }
    }

    // ------------------------------------------------------------------
    // 检查记录：启动静默检查靠它节流，避免每次开软件都拉 255 KB
    // ------------------------------------------------------------------

    public static JieduCheckRecord LoadRecord()
    {
        try
        {
            return File.Exists(AppPaths.LixiangCheckFile)
                ? Js.ReadJsonFile<JieduCheckRecord>(AppPaths.LixiangCheckFile) ?? new JieduCheckRecord()
                : new JieduCheckRecord();
        }
        catch { return new JieduCheckRecord(); }
    }

    public static void SaveRecord(JieduCheckRecord rec)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            Js.WriteJsonFile(AppPaths.LixiangCheckFile, rec);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("[jiedu] 写检查记录失败: " + e.Message);
        }
    }

    public static bool ShouldAutoCheck(JieduCheckRecord rec, int hours)
    {
        if (rec is null || string.IsNullOrWhiteSpace(rec.CheckedAt)) return true;
        return !DateTimeOffset.TryParse(rec.CheckedAt, out var t)
               || (DateTimeOffset.UtcNow - t) > TimeSpan.FromHours(hours);
    }
}

/// <summary>一次检查的结果。UI 只读它，不直接碰原文。</summary>
public sealed class JieduCheckResult
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";

    /// <summary>原文是否与上次导入的字节不同（仅信息用途；是否有条目变化看 <see cref="HasUpdate"/>）</summary>
    public bool HashChanged { get; set; }

    /// <summary>条目层面确实有新增/修订 —— 决定要不要提示用户</summary>
    public bool HasUpdate { get; set; }

    public string RemoteHash { get; set; } = "";
    public long RemoteSize { get; set; }
    public int RemoteTotal { get; set; }
    /// <summary>本地现有条数（提示里对比用）</summary>
    public int LocalTotal { get; set; }
    public List<int> RemoteIssues { get; set; } = new();
    /// <summary>本地现有期次（用于算出「哪些期是新的」）</summary>
    public List<int> LocalIssues { get; set; } = new();
    /// <summary>远端有、本地完全没有的期次</summary>
    public List<int> NewIssues { get; set; } = new();

    public List<JieduItem> Added { get; set; } = new();
    public List<JieduItem> Updated { get; set; } = new();
    public List<JieduItem> Removed { get; set; } = new();

    public string Preface { get; set; } = "";

    /// <summary>解析出的全部条目（Apply 用；不进提示文案）</summary>
    public List<JieduItem> Items { get; set; } = new();
}

/// <summary>上次检查的水印（data/lixiang-check.json，本机私有）</summary>
public sealed class JieduCheckRecord
{
    public string CheckedAt { get; set; }
    public string RemoteHash { get; set; }
    public bool HasUpdate { get; set; }
    public int RemoteTotal { get; set; }
    public int RemoteIssues { get; set; }
    /// <summary>本次「有新版本」提示是否已经给用户看过（同一份原文不重复弹）</summary>
    public string PromptedHash { get; set; }
}

/// <summary>下载失败：Message 已翻译成用户能看懂的话，界面直接显示。</summary>
public sealed class JieduSourceException : Exception
{
    public JieduSourceException(string message) : base(message) { }
}
