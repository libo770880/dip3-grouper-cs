using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;

namespace Dip3Grouper;

/// <summary>「检查更新」结果里的一行：某个可分发文件在本机与服务器之间的差异。</summary>
public sealed class DataDiffItem
{
    public string Name { get; set; }
    public string Label { get; set; }
    public string Group { get; set; }
    public string RelPath { get; set; }

    public bool ExistsLocal { get; set; }
    public bool ExistsRemote { get; set; }
    public long LocalSize { get; set; }
    public long RemoteSize { get; set; }
    public string LocalSha { get; set; }
    public string RemoteSha { get; set; }

    /// <summary>服务器侧该文件的最后修改时间（ISO）</summary>
    public string UpdatedAt { get; set; }

    /// <summary>内容是否一致（两边都存在且 SHA-256 相同）</summary>
    public bool Same { get; set; }

    /// <summary>是否需要下载（服务器有、且与本机不一致）</summary>
    public bool NeedsUpdate => ExistsRemote && !Same;

    // ---- 界面直读的展示字段 ----

    public string StatusText => !ExistsRemote ? "服务器无"
        : !ExistsLocal ? "本地缺失"
        : Same ? "一致" : "需更新";

    public string LocalSizeText => ExistsLocal ? DataCatalog.SizeText(LocalSize) : "—";
    public string RemoteSizeText => ExistsRemote ? DataCatalog.SizeText(RemoteSize) : "—";

    public string UpdatedText
    {
        get
        {
            if (string.IsNullOrEmpty(UpdatedAt)) return "—";
            return DateTimeOffset.TryParse(UpdatedAt, out var t) ? t.ToLocalTime().ToString("MM-dd HH:mm") : UpdatedAt;
        }
    }
}

/// <summary>「检查更新」的整体结果。</summary>
public sealed class DataCheckResult
{
    public bool Ok { get; set; }
    public string Message { get; set; }
    public string Server { get; set; }
    public string ManifestAt { get; set; }
    public string CheckedAt { get; set; }
    public List<DataDiffItem> Items { get; set; } = new();

    /// <summary>待下载项</summary>
    public List<DataDiffItem> Pending => Items.Where(i => i.NeedsUpdate).ToList();

    public int PendingCount => Items.Count(i => i.NeedsUpdate);
    public long PendingBytes => Items.Where(i => i.NeedsUpdate).Sum(i => i.RemoteSize);

    public string PendingText => PendingCount == 0
        ? "全部一致"
        : $"{PendingCount} 个文件 · 约 {DataCatalog.SizeText(PendingBytes)}";
}

/// <summary>「立即更新」的执行结果。</summary>
public sealed class DataApplyResult
{
    public bool Ok { get; set; }
    public string Message { get; set; }

    /// <summary>本次的备份目录（无文件被覆盖时为 null）</summary>
    public string BackupDir { get; set; }

    public List<string> Applied { get; set; } = new();
    public List<string> Failed { get; set; } = new();
}

/// <summary>
/// 局域网「数据分发中心」的客户端。
///
/// <para>与服务器的全部约定只有两个只读接口：
/// <c>GET /api/data/manifest</c>（清单）与 <c>GET /api/data/file/{name}</c>（文件）。
/// 客户端不向服务器写任何东西 —— 没有双向同步，也就没有冲突可解。</para>
///
/// <para>更新分四步：下载到 <c>data/.staging/</c> → 逐个校验 SHA-256 →
/// 备份被覆盖的文件到 <c>data/_backup/&lt;时间戳&gt;/</c> → 原子上位替换。
/// 任何一步失败都不会留下半个文件；替换完成后再由调用方触发内存热重载。</para>
/// </summary>
public static class DataUpdateClient
{
    /// <summary>下载暂存目录（与业务数据同盘，保证替换是同卷原子操作）</summary>
    public static string StagingDir => Path.Combine(AppPaths.DataDir, ".staging");

    /// <summary>备份根目录，其下按时间戳分子目录</summary>
    public static string BackupRoot => Path.Combine(AppPaths.DataDir, "_backup");

    /// <summary>同一时刻只允许一个更新任务（避免两个页面/两次点击互相踩）</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    // ==================================================================
    // 地址处理
    // ==================================================================

    /// <summary>
    /// 归一化服务器地址：允许只填 <c>10.0.0.8:3210</c>，自动补 <c>http://</c> 并去掉尾斜杠。
    /// </summary>
    public static string Normalize(string url)
    {
        var u = (url ?? "").Trim();
        if (u.Length == 0) return "";
        if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            u = "http://" + u.TrimStart('/');
        return u.TrimEnd('/');
    }

    /// <summary>带上共享口令（未配置则原样返回）。公共设置同步复用同一份口令。</summary>
    internal static string WithToken(string url)
    {
        var t = (SettingsStore.Current.DataShareToken ?? "").Trim();
        if (t.Length == 0) return url;
        return url + (url.Contains('?') ? "&" : "?") + "token=" + Uri.EscapeDataString(t);
    }

    private static HttpClient Make(int timeoutSeconds)
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        h.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Dip3Grouper-Desktop");
        return h;
    }

    /// <summary>把网络异常翻译成维护人员看得懂的话</summary>
    internal static string Friendly(Exception e)
    {
        if (e is TaskCanceledException || e is OperationCanceledException) return "连接超时：对方没有响应，请确认服务是否在运行。";
        if (e is HttpRequestException he)
        {
            return he.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "共享口令不正确，被服务器拒绝。",
                HttpStatusCode.NotFound => "服务器上没有这个接口，请确认对方跑的是本程序的分发服务。",
                _ => "连不上数据源：" + he.Message,
            };
        }
        return e.Message;
    }

    // ==================================================================
    // 检查更新
    // ==================================================================

    public static async Task<DataManifest> FetchManifestAsync(string baseUrl, CancellationToken ct = default)
    {
        var root = Normalize(baseUrl);
        if (root.Length == 0) throw new InvalidOperationException("请先填写数据源地址。");
        using var h = Make(15);
        var text = await h.GetStringAsync(WithToken(root + "/api/data/manifest"), ct);
        var m = Js.FromJson<DataManifest>(text);
        if (m?.Files == null) throw new InvalidOperationException("服务器返回的清单格式不正确。");
        return m;
    }

    /// <summary>比对服务器清单与本地文件，得到差异明细。</summary>
    public static async Task<DataCheckResult> CheckAsync(string baseUrl, CancellationToken ct = default)
    {
        var res = new DataCheckResult { Server = Normalize(baseUrl), CheckedAt = Js.Iso() };
        if (res.Server.Length == 0)
        {
            res.Ok = false;
            res.Message = "请先填写数据源地址。";
            return res;
        }

        try
        {
            var manifest = await FetchManifestAsync(baseUrl, ct);
            res.ManifestAt = manifest.GeneratedAt;

            var remote = new Dictionary<string, DataFileInfo>(StringComparer.Ordinal);
            foreach (var f in manifest.Files)
                if (!string.IsNullOrEmpty(f?.Name)) remote[f.Name] = f;

            foreach (var spec in DataCatalog.Specs)
            {
                var local = DataCatalog.Describe(spec);
                remote.TryGetValue(spec.Name, out var rf);
                var hasRemote = rf != null && rf.Exists;

                var item = new DataDiffItem
                {
                    Name = spec.Name,
                    Label = spec.Label,
                    Group = spec.Group,
                    RelPath = spec.RelPath,
                    ExistsLocal = local.Exists,
                    ExistsRemote = hasRemote,
                    LocalSize = local.Size,
                    RemoteSize = hasRemote ? rf.Size : 0,
                    LocalSha = local.Sha256,
                    RemoteSha = hasRemote ? rf.Sha256 : null,
                    UpdatedAt = hasRemote ? rf.UpdatedAt : null,
                };
                item.Same = item.ExistsLocal && hasRemote &&
                            string.Equals(item.LocalSha, item.RemoteSha, StringComparison.OrdinalIgnoreCase);
                res.Items.Add(item);
            }

            res.Ok = true;
            res.Message = res.PendingCount == 0
                ? "本地数据已是最新。"
                : $"发现 {res.PendingCount} 个文件需要更新，共约 {DataCatalog.SizeText(res.PendingBytes)}。";
        }
        catch (Exception e)
        {
            res.Ok = false;
            res.Message = Friendly(e);
        }
        return res;
    }

    // ==================================================================
    // 立即更新
    // ==================================================================

    /// <summary>
    /// 下载并安装差异文件。执行完毕后由调用方触发 <c>AppState.ReloadAll()</c>。
    /// </summary>
    public static async Task<DataApplyResult> ApplyAsync(string baseUrl, IReadOnlyList<DataDiffItem> items,
        Action<int, string> progress = null, CancellationToken ct = default)
    {
        var result = new DataApplyResult();
        var pending = (items ?? Array.Empty<DataDiffItem>()).Where(i => i.NeedsUpdate).ToList();
        if (pending.Count == 0)
        {
            result.Ok = true;
            result.Message = "没有需要更新的文件。";
            return result;
        }

        var root = Normalize(baseUrl);
        if (root.Length == 0)
        {
            result.Message = "请先填写数据源地址。";
            return result;
        }

        await Gate.WaitAsync(ct);
        try
        {
            ResetStaging();

            // ---- 第一步：全部下载到暂存区并逐个校验 ----
            var staged = new List<(DataFileSpec Spec, string Path)>();
            using (var h = Make(180))
            {
                for (var i = 0; i < pending.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var item = pending[i];
                    var spec = DataCatalog.Find(item.Name);
                    if (spec == null) continue;

                    var basePct = i * 60 / pending.Count;
                    progress?.Invoke(basePct, $"正在下载 {item.Label}（{i + 1}/{pending.Count}）…");

                    var stagePath = Path.Combine(StagingDir, spec.Name);
                    await DownloadAsync(h, WithToken(root + "/api/data/file/" + Uri.EscapeDataString(spec.Name)),
                        stagePath, (got, total) =>
                        {
                            var frac = total > 0 ? got * 1.0 / total : 0;
                            progress?.Invoke(basePct + (int)(frac * 60 / pending.Count),
                                $"正在下载 {item.Label}（{i + 1}/{pending.Count}）…");
                        }, ct);

                    var sha = DataCatalog.Sha256(stagePath);
                    if (!string.Equals(sha, item.RemoteSha, StringComparison.OrdinalIgnoreCase))
                    {
                        ResetStaging();
                        result.Message = $"{item.Label} 下载校验失败（SHA-256 不一致），已放弃本次更新，本地数据未改动。";
                        return result;
                    }
                    staged.Add((spec, stagePath));
                }
            }

            progress?.Invoke(70, "正在备份并替换…");
            var install = Install(staged, p => progress?.Invoke(70 + p * 30 / 100, $"正在替换… {p}%"));
            result.Applied = install.Applied;
            result.Failed = install.Failed;
            result.BackupDir = install.BackupDir;

            progress?.Invoke(100, "更新完成");
            result.Ok = result.Failed.Count == 0;
            result.Message = result.Ok
                ? $"已更新 {result.Applied.Count} 个文件" + (result.BackupDir != null ? "，原文件已备份。" : "。")
                : $"成功 {result.Applied.Count} 个，失败 {result.Failed.Count} 个：" + string.Join("；", result.Failed);
            AuditLog.Write(AuditAction.Update, "数据", $"在线更新 {result.Applied.Count} 个文件（源 {root}）",
                result.Ok, result.Ok ? null : string.Join("；", result.Failed));
            return result;
        }
        catch (Exception e)
        {
            result.Ok = false;
            result.Message = Friendly(e);
            return result;
        }
        finally
        {
            ResetStaging();
            Gate.Release();
        }
    }

    private static async Task DownloadAsync(HttpClient h, string url, string dest,
        Action<long, long> onProgress, CancellationToken ct)
    {
        using var resp = await h.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1;

        Directory.CreateDirectory(Path.GetDirectoryName(dest));
        var part = dest + ".part";
        try
        {
            using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var src = await resp.Content.ReadAsStreamAsync(ct))
            {
                var buf = new byte[1 << 20];
                long got = 0;
                int n;
                while ((n = await src.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, n), ct);
                    got += n;
                    onProgress?.Invoke(got, total);
                }
            }
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(part, dest);
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { /* 尽力而为 */ }
        }
    }

    // ==================================================================
    // 安装（备份 + 原子上位）—— 网络更新与更新包导入共用
    // ==================================================================

    private sealed class InstallOutcome
    {
        public List<string> Applied { get; } = new();
        public List<string> Failed { get; } = new();
        public string BackupDir { get; set; }
    }

    /// <summary>执行安装；<paramref name="progress"/> 只回报百分比，文案由调用方决定。</summary>
    private static InstallOutcome Install(List<(DataFileSpec Spec, string Path)> staged, Action<int> progress)
    {
        var outcome = new InstallOutcome();
        if (staged.Count == 0) return outcome;

        // 时间戳备份目录：同一个「更新包」里的多个文件落在同一目录，便于整体回滚。
        // 秒级时间戳在本机回环（更新→立即回滚）场景会撞名：同名 Install 会用「还原前的内容」
        // 覆盖掉上一轮备份 —— 回滚就从备份里拷回了错误的内容（自检第 72 项偶发失败的根因）。
        // 因此已存在时追加序号，保证每次 Install 各用自己的目录。
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupDir = Path.Combine(BackupRoot, stamp);
        for (var n = 1; Directory.Exists(backupDir); n++)
            backupDir = Path.Combine(BackupRoot, $"{stamp}-{n}");
        var backedUp = 0;

        for (var i = 0; i < staged.Count; i++)
        {
            var (spec, stagedPath) = staged[i];
            try
            {
                var target = DataCatalog.LocalPath(spec);

                if (File.Exists(target))
                {
                    var bak = Path.Combine(backupDir, spec.RelPath.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(bak));
                    File.Copy(target, bak, true);
                    backedUp++;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target));
                var tmp = target + ".new";
                File.Copy(stagedPath, tmp, true);
                MoveWithRetry(tmp, target);
                outcome.Applied.Add(spec.Label);
            }
            catch (Exception e)
            {
                outcome.Failed.Add(spec.Label + "（" + e.Message + "）");
            }
            progress?.Invoke((i + 1) * 100 / staged.Count);
        }

        if (backedUp > 0) outcome.BackupDir = backupDir;
        else if (Directory.Exists(backupDir)) { try { Directory.Delete(backupDir, true); } catch { } }

        return outcome;
    }

    /// <summary>
    /// 同卷替换（<c>MoveFileEx</c> + REPLACE_EXISTING），失败重试若干次 ——
    /// 杀软实时扫描 / 文件被短暂占用时会抛出 IOException，等一下即可成功。
    /// </summary>
    internal static void MoveWithRetry(string src, string dst, int tries = 8)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                File.Move(src, dst, overwrite: true);
                return;
            }
            catch (Exception e) when ((e is IOException || e is UnauthorizedAccessException) && i < tries - 1)
            {
                Thread.Sleep(150 * (i + 1));
            }
        }
    }

    private static void ResetStaging()
    {
        try
        {
            if (Directory.Exists(StagingDir)) Directory.Delete(StagingDir, true);
        }
        catch { /* 删不掉不影响下一次（下载时会覆盖同名文件） */ }
    }

    // ==================================================================
    // 回滚
    // ==================================================================

    /// <summary>历次备份目录（新的在前）</summary>
    public static List<string> BackupDirs()
    {
        if (!Directory.Exists(BackupRoot)) return new List<string>();
        return Directory.GetDirectories(BackupRoot)
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>把某个备份目录里的文件原样还原回去（同样走备份 + 原子替换）。</summary>
    public static DataApplyResult Restore(string backupDir)
    {
        var result = new DataApplyResult();
        if (string.IsNullOrEmpty(backupDir) || !Directory.Exists(backupDir))
        {
            result.Message = "备份目录不存在。";
            return result;
        }

        var staged = new List<(DataFileSpec Spec, string Path)>();
        foreach (var spec in DataCatalog.Specs)
        {
            var src = Path.Combine(backupDir, spec.RelPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(src)) staged.Add((spec, src));
        }
        if (staged.Count == 0)
        {
            result.Message = "该备份目录里没有可还原的文件。";
            return result;
        }

        var outcome = Install(staged, null);
        result.Applied = outcome.Applied;
        result.Failed = outcome.Failed;
        result.BackupDir = outcome.BackupDir;
        result.Ok = outcome.Failed.Count == 0;
        result.Message = result.Ok
            ? $"已还原 {result.Applied.Count} 个文件（还原前的版本也已备份）。"
            : $"还原成功 {result.Applied.Count} 个，失败 {result.Failed.Count} 个：" + string.Join("；", result.Failed);
        AuditLog.Write(AuditAction.Update, "数据",
            $"还原备份点 {Path.GetFileName(backupDir)}（{result.Applied.Count} 个文件）",
            result.Ok, result.Ok ? null : string.Join("；", result.Failed));
        return result;
    }

    // ==================================================================
    // 更新包（离线兜底：把服务器上的当前数据打成一个 zip 带走）
    // ==================================================================

    /// <summary>
    /// 导出更新包（<c>manifest.json</c> + <c>files/&lt;name&gt;</c>）。
    /// 无网络的机器用「导入更新包」即可获得与在线更新完全一致的结果。
    /// </summary>
    public static string ExportPackage(string outFile, Action<int, string> progress = null)
    {
        var dir = Path.GetDirectoryName(outFile);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var part = outFile + ".part";
        try { if (File.Exists(part)) File.Delete(part); } catch { }

        using (var zip = ZipFile.Open(part, ZipArchiveMode.Create))
        {
            var manifest = DataCatalog.BuildManifest();
            var entry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
            using (var s = entry.Open())
            {
                var bytes = Encoding.UTF8.GetBytes(Js.ToJson(manifest));
                s.Write(bytes, 0, bytes.Length);
            }

            for (var i = 0; i < DataCatalog.Specs.Count; i++)
            {
                var spec = DataCatalog.Specs[i];
                var path = DataCatalog.LocalPath(spec);
                progress?.Invoke(i * 95 / DataCatalog.Specs.Count, $"正在打包 {spec.Label}…");
                if (File.Exists(path)) zip.CreateEntryFromFile(path, "files/" + spec.Name, CompressionLevel.Optimal);
            }
        }

        if (File.Exists(outFile)) File.Delete(outFile);
        File.Move(part, outFile);
        progress?.Invoke(100, "导出完成");
        return outFile;
    }

    /// <summary>导入更新包（解包 → 校验 → 备份 → 替换），与在线更新共用同一套安装逻辑。</summary>
    public static DataApplyResult ImportPackage(string zipFile, Action<int, string> progress = null)
    {
        var result = new DataApplyResult();
        if (string.IsNullOrEmpty(zipFile) || !File.Exists(zipFile))
        {
            result.Message = "更新包文件不存在。";
            return result;
        }

        try
        {
            ResetStaging();
            using var zip = ZipFile.OpenRead(zipFile);

            var metaEntry = zip.GetEntry("manifest.json");
            if (metaEntry == null)
            {
                result.Message = "这不是本程序导出的更新包（缺少 manifest.json）。";
                return result;
            }

            DataManifest manifest;
            using (var s = metaEntry.Open())
            using (var sr = new StreamReader(s, Encoding.UTF8))
                manifest = Js.FromJson<DataManifest>(sr.ReadToEnd());

            if (manifest?.Files == null)
            {
                result.Message = "更新包内的清单无法解析。";
                return result;
            }

            progress?.Invoke(10, "正在解包并校验…");
            var staged = new List<(DataFileSpec Spec, string Path)>();
            foreach (var spec in DataCatalog.Specs)
            {
                var rec = manifest.Files.FirstOrDefault(f => string.Equals(f?.Name, spec.Name, StringComparison.Ordinal));
                if (rec == null || !rec.Exists) continue;

                var entry = zip.GetEntry("files/" + spec.Name);
                if (entry == null)
                {
                    result.Message = $"更新包里缺少「{spec.Label}」。";
                    return result;
                }

                var stagePath = Path.Combine(StagingDir, spec.Name);
                Directory.CreateDirectory(StagingDir);
                entry.ExtractToFile(stagePath, true);

                var sha = DataCatalog.Sha256(stagePath);
                if (!string.Equals(sha, rec.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    result.Message = $"「{spec.Label}」校验失败（SHA-256 不一致），已放弃导入，本地数据未改动。";
                    return result;
                }

                // 与本机一致的文件直接跳过，避免无谓覆盖与备份
                var local = DataCatalog.Describe(spec);
                if (local.Exists && string.Equals(local.Sha256, sha, StringComparison.OrdinalIgnoreCase)) continue;
                staged.Add((spec, stagePath));
            }

            if (staged.Count == 0)
            {
                result.Ok = true;
                result.Message = "更新包里的数据与本地一致，无需导入。";
                return result;
            }

            progress?.Invoke(50, "正在备份并替换…");
            var outcome = Install(staged, p => progress?.Invoke(50 + p / 2, null));
            result.Applied = outcome.Applied;
            result.Failed = outcome.Failed;
            result.BackupDir = outcome.BackupDir;
            result.Ok = outcome.Failed.Count == 0;
            result.Message = result.Ok
                ? $"已导入 {result.Applied.Count} 个文件。"
                : $"成功 {result.Applied.Count} 个，失败 {result.Failed.Count} 个：" + string.Join("；", result.Failed);
            progress?.Invoke(100, "导入完成");
            AuditLog.Write(AuditAction.Update, "数据",
                $"导入离线更新包 {Path.GetFileName(zipFile)}（{result.Applied.Count} 个文件）",
                result.Ok, result.Ok ? null : string.Join("；", result.Failed));
            return result;
        }
        catch (Exception e)
        {
            result.Ok = false;
            result.Message = "导入失败：" + e.Message;
            AuditLog.Write(AuditAction.Update, "数据", $"导入离线更新包 {Path.GetFileName(zipFile)}",
                false, e.Message);
            return result;
        }
        finally
        {
            ResetStaging();
        }
    }
}
