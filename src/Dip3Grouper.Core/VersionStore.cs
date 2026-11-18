using System.Text;

namespace Dip3Grouper;

/// <summary>某个版本里的一份文件（记录大小与哈希，便于快速判断是否变动）。</summary>
public sealed class VersionFile
{
    public string Name { get; set; }
    public string Label { get; set; }
    public string RelPath { get; set; }
    public bool Exists { get; set; }
    public long Size { get; set; }
    public string Sha256 { get; set; }

    public string SizeText => Exists ? DataCatalog.SizeText(Size) : "—";
    public string StatusText => Exists ? "已保存" : "缺失";
}

/// <summary>一个病种库版本（快照）的元信息，落盘为 &lt;版本目录&gt;/version.json。</summary>
public sealed class VersionEntry
{
    /// <summary>版本号（目录名），形如 20261011-213045，同一秒内重名会追加 -2 -3</summary>
    public string Id { get; set; }

    /// <summary>创建时间（ISO，UTC）</summary>
    public string At { get; set; }

    /// <summary>创建者（后台账号；桌面端未登录时为「本机」）</summary>
    public string User { get; set; }

    /// <summary>触发来源：手动创建 / 导入前自动 / 保存前自动 / 更新前自动</summary>
    public string Source { get; set; }

    /// <summary>是否为自动快照（自动快照在界面上以次要样式呈现）</summary>
    public bool Auto { get; set; }

    /// <summary>备注（手动创建时可填）</summary>
    public string Note { get; set; }

    /// <summary>各区段行数（tab → 行数）</summary>
    public Dictionary<string, int> Counts { get; set; } = new();

    /// <summary>总行数</summary>
    public int Total { get; set; }

    /// <summary>当时的分组器版本</summary>
    public string AppVersion { get; set; }

    /// <summary>该版本包含的文件</summary>
    public List<VersionFile> Files { get; set; } = new();

    // ---- 界面直读 ----

    public string AtText
        => DateTimeOffset.TryParse(At, out var t) ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : (At ?? "");

    public string Title => string.IsNullOrWhiteSpace(Note) ? Source : Note;

    public string CountsText => $"{Counts.Count} 个区段 · {Total:N0} 条";

    /// <summary>类型（自动 / 手动）</summary>
    public string KindText => Auto ? "自动" : "手动";

    /// <summary>快照总大小（各文件之和）</summary>
    public string SizeText => DataCatalog.SizeText(Files.Where(f => f.Exists).Sum(f => f.Size));

    /// <summary>快照的「指纹」：病种库配置文件的 SHA-256（用于识别内容是否真的变了）</summary>
    public string Fingerprint { get; set; }

    /// <summary>指纹短码（界面显示）</summary>
    public string ShortHash => string.IsNullOrEmpty(Fingerprint) ? "—" : Fingerprint[..Math.Min(8, Fingerprint.Length)];
}

/// <summary>差异比对里某个区段的增删改统计。</summary>
public sealed class VersionDiffSection
{
    public string Tab { get; set; }
    public string Label { get; set; }
    public int OldCount { get; set; }
    public int NewCount { get; set; }

    /// <summary>新增行的主键（最多存若干条，供界面展示）</summary>
    public List<string> Added { get; set; } = new();
    public List<string> Removed { get; set; } = new();
    public List<string> Changed { get; set; } = new();

    public int AddedCount { get; set; }
    public int RemovedCount { get; set; }
    public int ChangedCount { get; set; }

    public bool Same => AddedCount == 0 && RemovedCount == 0 && ChangedCount == 0;

    public string Summary
    {
        get
        {
            if (Same) return "无变化";
            var parts = new List<string>();
            if (AddedCount > 0) parts.Add($"+{AddedCount}");
            if (RemovedCount > 0) parts.Add($"-{RemovedCount}");
            if (ChangedCount > 0) parts.Add($"~{ChangedCount}");
            return string.Join(" / ", parts);
        }
    }

    public string CountText => $"{OldCount:N0} → {NewCount:N0}";

    /// <summary>变更明细（主键样例，长列表截断）</summary>
    public string SampleText
    {
        get
        {
            var parts = new List<string>();
            if (Added.Count > 0) parts.Add("新增 " + Keys(Added, AddedCount));
            if (Removed.Count > 0) parts.Add("删除 " + Keys(Removed, RemovedCount));
            if (Changed.Count > 0) parts.Add("修改 " + Keys(Changed, ChangedCount));
            return parts.Count == 0 ? "—" : string.Join("；", parts);
        }
    }

    private static string Keys(List<string> sample, int total)
    {
        var head = string.Join("、", sample);
        return total > sample.Count ? $"{head} … 等 {total} 项" : head;
    }
}

/// <summary>两个版本（或版本与当前）之间的差异。</summary>
public sealed class VersionDiff
{
    public string LeftId { get; set; }
    public string RightId { get; set; }
    public string LeftAt { get; set; }
    public string RightAt { get; set; }

    public List<VersionDiffSection> Sections { get; set; } = new();

    /// <summary>非病种库配置文件的文件级差异（导入信息 / 综合病组）</summary>
    public List<VersionFileDiff> Files { get; set; } = new();

    public int AddedTotal { get; set; }
    public int RemovedTotal { get; set; }
    public int ChangedTotal { get; set; }

    public bool Same => AddedTotal == 0 && RemovedTotal == 0 && ChangedTotal == 0
                        && Files.All(f => f.Same);

    public string SummaryText => Same
        ? "两个版本完全一致"
        : $"新增 {AddedTotal} · 删除 {RemovedTotal} · 修改 {ChangedTotal}";
}

/// <summary>单个文件的版本间差异。</summary>
public sealed class VersionFileDiff
{
    public string Label { get; set; }
    public string RelPath { get; set; }
    public bool InLeft { get; set; }
    public bool InRight { get; set; }
    public string LeftSha { get; set; }
    public string RightSha { get; set; }

    public bool Same => InLeft == InRight &&
                        string.Equals(LeftSha, RightSha, StringComparison.OrdinalIgnoreCase);

    public string StatusText => !InRight ? "已消失" : !InLeft ? "新增" : Same ? "一致" : "已修改";
}

/// <summary>
/// 病种库版本库：把病种库那一组文件（配置 / 导入信息 / 综合病组）按时间整份快照，
/// 支持历史查看、两版差异比对与一键回滚。
///
/// <para>与 <see cref="DataUpdateClient"/> 的 <c>data/_backup/</c> 是**两件事**：
/// 那个备份是「替换前顺手留一手」，只服务于一次更新/还原；这里是**一等公民的历史版本**，
/// 带来源与备注，可长期留存、可比较、可随时回滚到任意历史状态。</para>
///
/// <para>目录结构与更新包备份一致（保留 <c>config/</c> <c>data/</c> 相对层级），
/// 因此 <see cref="Restore"/> 与更新包的安装逻辑同构：先备份被覆盖的文件，再原子上位替换。</para>
/// </summary>
public static class VersionStore
{
    /// <summary>版本库根目录</summary>
    public static string Root => Path.Combine(AppPaths.DataDir, "_versions");

    /// <summary>版本快照覆盖的文件 —— 直接复用数据分发的「病种库」分组定义，两处不会走偏</summary>
    public static IReadOnlyList<DataFileSpec> Files { get; } =
        DataCatalog.Specs.Where(s => s.Group == "病种库").ToList();

    /// <summary>差异列表里每个区段最多展示几条主键（避免界面被长列表拖垮）</summary>
    private const int KeySampleMax = 40;

    /// <summary>自动快照去重窗口：同一内容不重复建版本</summary>
    private static readonly object Gate = new();

    public static string DirOf(string id) => Path.Combine(Root, id ?? "");

    // ==================================================================
    // 读取
    // ==================================================================

    /// <summary>全部版本（新的在前）。目录损坏的条目会被跳过，不影响其余版本可用。</summary>
    public static List<VersionEntry> List()
    {
        var list = new List<VersionEntry>();
        if (!Directory.Exists(Root)) return list;
        foreach (var dir in Directory.GetDirectories(Root))
        {
            var e = Read(dir);
            if (e != null) list.Add(e);
        }
        return list.OrderByDescending(e => e.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>读取某个版本目录的元信息；目录里没有 version.json 时按文件现场推断（兼容手工拷入的目录）。</summary>
    public static VersionEntry Read(string dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        var meta = Path.Combine(dir, "version.json");
        VersionEntry e = null;
        try { e = Js.ReadJsonFile<VersionEntry>(meta); }
        catch { /* 元信息损坏则按文件推断 */ }

        e ??= new VersionEntry();
        if (string.IsNullOrWhiteSpace(e.Id)) e.Id = Path.GetFileName(dir);
        if (e.Files == null || e.Files.Count == 0) e.Files = DescribeDir(dir);
        if (e.Fingerprint == null)
            e.Fingerprint = e.Files.FirstOrDefault(f => f.Name == "dip3-config")?.Sha256;
        if (string.IsNullOrWhiteSpace(e.Source)) e.Source = "历史版本";
        return e;
    }

    public static VersionEntry Find(string id)
        => string.IsNullOrEmpty(id) ? null : Read(DirOf(id));

    /// <summary>逐文件描述版本目录里的内容</summary>
    private static List<VersionFile> DescribeDir(string dir)
    {
        var list = new List<VersionFile>();
        foreach (var spec in Files)
        {
            var p = Path.Combine(dir, spec.RelPath.Replace('/', Path.DirectorySeparatorChar));
            var fi = File.Exists(p) ? new FileInfo(p) : null;
            list.Add(new VersionFile
            {
                Name = spec.Name,
                Label = spec.Label,
                RelPath = spec.RelPath,
                Exists = fi != null,
                Size = fi?.Length ?? 0,
                Sha256 = fi == null ? null : DataCatalog.Sha256(p),
            });
        }
        return list;
    }

    /// <summary>当前（磁盘上）病种库文件的描述</summary>
    public static List<VersionFile> DescribeCurrent() => DescribeDir(AppPaths.Root);

    /// <summary>当前病种库配置的指纹（config/dip3-config.json 的 SHA-256）</summary>
    public static string CurrentFingerprint()
        => DataCatalog.Sha256(AppPaths.ConfigFile);

    // ==================================================================
    // 创建
    // ==================================================================

    /// <summary>
    /// 建一个版本快照。<paramref name="auto"/> = true 时若与最新版本内容一致则不重复建，
    /// 返回 null（调用方据此决定是否提示）。
    /// </summary>
    public static VersionEntry Create(string source, string note = null, bool auto = false, string user = null)
    {
        lock (Gate)
        {
            var fp = CurrentFingerprint();
            var existing = List();
            if (auto && fp != null && existing.Count > 0 &&
                string.Equals(existing[0].Fingerprint, fp, StringComparison.OrdinalIgnoreCase))
                return null;   // 内容没变，不产生无意义的历史版本

            var id = UniqueId();
            var dir = DirOf(id);
            Directory.CreateDirectory(dir);

            var files = new List<VersionFile>();
            foreach (var spec in Files)
            {
                var src = DataCatalog.LocalPath(spec);
                var dst = Path.Combine(dir, spec.RelPath.Replace('/', Path.DirectorySeparatorChar));
                var exists = File.Exists(src);
                if (exists)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(src, dst, true);
                }
                var fi = exists ? new FileInfo(src) : null;
                files.Add(new VersionFile
                {
                    Name = spec.Name,
                    Label = spec.Label,
                    RelPath = spec.RelPath,
                    Exists = exists,
                    Size = fi?.Length ?? 0,
                    Sha256 = exists ? DataCatalog.Sha256(src) : null,
                });
            }

            var counts = SectionCountsNow();
            var entry = new VersionEntry
            {
                Id = id,
                At = Js.Iso(),
                User = string.IsNullOrWhiteSpace(user) ? (AuditLog.Ambient?.User ?? "本机") : user,
                Source = source ?? "手动创建",
                Auto = auto,
                Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                Counts = counts,
                Total = counts.Values.Sum(v => v),
                AppVersion = AppVersion.Current,
                Files = files,
                Fingerprint = fp,
            };
            Js.WriteJsonFile(Path.Combine(dir, "version.json"), entry, indent: 2);
            // 自动快照是「每次改动前的例行留档」，不进审计（否则一次导入会记两条）；
            // 手动打版是管理员的显式动作，必须留痕。
            if (!auto)
                AuditLog.Write(AuditAction.Version, "版本",
                    $"新建病种库版本 {entry.Id}：{entry.Title}（{entry.CountsText}）");
            return entry;
        }
    }

    /// <summary>删除一个版本（不可恢复）。</summary>
    public static bool Delete(string id)
    {
        lock (Gate)
        {
            var dir = DirOf(id);
            if (string.IsNullOrEmpty(id) || !Directory.Exists(dir)) return false;
            try
            {
                Directory.Delete(dir, true);
                AuditLog.Write(AuditAction.Version, "版本", $"删除病种库版本 {id}");
                return true;
            }
            catch
            {
                AuditLog.Write(AuditAction.Version, "版本", $"删除病种库版本 {id}", false, "文件可能被占用");
                return false;
            }
        }
    }

    /// <summary>时间戳版本号；同秒重名时追加序号。</summary>
    private static string UniqueId()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var id = stamp;
        for (var n = 1; Directory.Exists(DirOf(id)); n++) id = $"{stamp}-{n}";
        return id;
    }

    private static Dictionary<string, int> SectionCountsNow()
    {
        var cfg = Js.ReadJsonFile<Dip3Config>(AppPaths.ConfigFile);
        if (cfg == null) return new Dictionary<string, int>();
        return Diseases.SectionCounts(cfg).ToDictionary(kv => kv.Key, kv => kv.Value.Total);
    }

    // ==================================================================
    // 回滚
    // ==================================================================

    /// <summary>
    /// 把某个版本的文件原样还原回工程根。还原前会把当前文件备份到
    /// <c>data/_backup/&lt;时间戳&gt;/</c>（与数据更新共用同一套备份目录）。
    /// 完成后由调用方触发 <c>App.State.ReloadAll()</c>。
    /// </summary>
    public static DataApplyResult Restore(string id)
    {
        var result = new DataApplyResult();
        var dir = DirOf(id);
        if (string.IsNullOrEmpty(id) || !Directory.Exists(dir))
        {
            result.Message = "版本不存在。";
            return result;
        }

        var staged = new List<(DataFileSpec Spec, string Path)>();
        foreach (var spec in Files)
        {
            var src = Path.Combine(dir, spec.RelPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(src)) staged.Add((spec, src));
        }
        if (staged.Count == 0)
        {
            result.Message = "该版本里没有可还原的文件。";
            return result;
        }

        var outcome = Install(staged);
        result.Applied = outcome.Applied;
        result.Failed = outcome.Failed;
        result.BackupDir = outcome.BackupDir;
        result.Ok = outcome.Failed.Count == 0;
        result.Message = result.Ok
            ? $"已回滚到 {id}（{result.Applied.Count} 个文件；回滚前的版本也已备份）。"
            : $"回滚成功 {result.Applied.Count} 个，失败 {result.Failed.Count} 个：" + string.Join("；", result.Failed);
        AuditLog.Write(AuditAction.Version, "版本",
            $"回滚病种库到 {id}（{result.Applied.Count} 个文件）",
            result.Ok, result.Ok ? null : string.Join("；", result.Failed));
        return result;
    }

    private sealed class InstallOutcome
    {
        public List<string> Applied { get; } = new();
        public List<string> Failed { get; } = new();
        public string BackupDir { get; set; }
    }

    /// <summary>备份 + 原子上位替换（与数据更新的 Install 同构，供版本回滚单独使用）。</summary>
    private static InstallOutcome Install(List<(DataFileSpec Spec, string Path)> staged)
    {
        var outcome = new InstallOutcome();
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupDir = Path.Combine(DataUpdateClient.BackupRoot, stamp);
        for (var n = 1; Directory.Exists(backupDir); n++)
            backupDir = Path.Combine(DataUpdateClient.BackupRoot, $"{stamp}-{n}");
        var backedUp = 0;

        foreach (var (spec, stagedPath) in staged)
        {
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
                DataUpdateClient.MoveWithRetry(tmp, target);
                outcome.Applied.Add(spec.Label);
            }
            catch (Exception e)
            {
                outcome.Failed.Add(spec.Label + "（" + e.Message + "）");
            }
        }

        if (backedUp > 0) outcome.BackupDir = backupDir;
        else if (Directory.Exists(backupDir)) { try { Directory.Delete(backupDir, true); } catch { } }
        return outcome;
    }

    // ==================================================================
    // 差异比对
    // ==================================================================

    /// <summary>版本 ↔ 版本</summary>
    public static VersionDiff Diff(string leftId, string rightId)
    {
        var left = Find(leftId);
        var right = Find(rightId);
        if (left == null || right == null)
            throw new InvalidOperationException("版本不存在。");
        return Compare(LoadConfig(DirOf(leftId)), LoadConfig(DirOf(rightId)), left, right);
    }

    /// <summary>版本 ↔ 当前（磁盘上的实时内容）</summary>
    public static VersionDiff DiffWithCurrent(string id)
    {
        var left = Find(id);
        if (left == null) throw new InvalidOperationException("版本不存在。");
        var right = new VersionEntry
        {
            Id = "当前",
            At = Js.Iso(),
            Source = "当前",
            Counts = SectionCountsNow(),
            Files = DescribeCurrent(),
        };
        right.Total = right.Counts.Values.Sum(v => v);
        right.Fingerprint = CurrentFingerprint();
        return Compare(LoadConfig(DirOf(id)), Js.ReadJsonFile<Dip3Config>(AppPaths.ConfigFile), left, right);
    }

    private static Dip3Config LoadConfig(string dir)
    {
        var p = Path.Combine(dir, "config", "dip3-config.json");
        return File.Exists(p) ? Js.ReadJsonFile<Dip3Config>(p) : null;
    }

    private static VersionDiff Compare(Dip3Config oldCfg, Dip3Config newCfg, VersionEntry left, VersionEntry right)
    {
        var diff = new VersionDiff
        {
            LeftId = left.Id,
            RightId = right.Id,
            LeftAt = left.At,
            RightAt = right.At,
        };

        foreach (var sec in Diseases.Sections)
        {
            var a = MapOf(sec, oldCfg);
            var b = MapOf(sec, newCfg);
            var item = new VersionDiffSection
            {
                Tab = sec.Tab,
                Label = sec.Label,
                OldCount = a.Count,
                NewCount = b.Count,
            };
            foreach (var kv in b)
            {
                if (!a.TryGetValue(kv.Key, out var oldVal)) { item.AddedCount++; Sample(item.Added, kv.Key); }
                else if (!string.Equals(oldVal, kv.Value, StringComparison.Ordinal)) { item.ChangedCount++; Sample(item.Changed, kv.Key); }
            }
            foreach (var kv in a)
                if (!b.ContainsKey(kv.Key)) { item.RemovedCount++; Sample(item.Removed, kv.Key); }

            diff.AddedTotal += item.AddedCount;
            diff.RemovedTotal += item.RemovedCount;
            diff.ChangedTotal += item.ChangedCount;
            diff.Sections.Add(item);
        }

        // 非配置文件的文件级差异（导入信息 / 综合病组）
        foreach (var spec in Files)
        {
            if (spec.Name == "dip3-config") continue;   // 已在区段级比对
            var l = left.Files.FirstOrDefault(f => f.Name == spec.Name);
            var r = right.Files.FirstOrDefault(f => f.Name == spec.Name);
            diff.Files.Add(new VersionFileDiff
            {
                Label = spec.Label,
                RelPath = spec.RelPath,
                InLeft = l?.Exists ?? false,
                InRight = r?.Exists ?? false,
                LeftSha = l?.Sha256,
                RightSha = r?.Sha256,
            });
        }
        return diff;
    }

    /// <summary>区段行 → 「主键 ⇒ 规范化内容」映射；内容用序列化后的扁平字典表示，可比对也便于展示。</summary>
    private static Dictionary<string, string> MapOf(SectionDef sec, Dip3Config cfg)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (cfg == null) return map;
        foreach (var row in sec.GetRows(cfg))
        {
            var key = sec.KeyOf(row) ?? "";
            if (key.Length == 0) continue;
            map[key] = Js.ToJson(sec.Flatten(row));
        }
        return map;
    }

    private static void Sample(List<string> list, string key)
    {
        if (list.Count < KeySampleMax) list.Add(key);
    }

    // ==================================================================
    // 导出差异报告
    // ==================================================================

    /// <summary>把差异导出为文本报告（可直接贴进培训材料）。</summary>
    public static string DiffReportText(VersionDiff d)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"病种库版本差异　{d.LeftId}（{Local(d.LeftAt)}）　→　{d.RightId}（{Local(d.RightAt)}）");
        sb.AppendLine($"合计：新增 {d.AddedTotal} · 删除 {d.RemovedTotal} · 修改 {d.ChangedTotal}");
        sb.AppendLine();
        foreach (var s in d.Sections)
        {
            if (s.Same) continue;
            sb.AppendLine($"[{s.Label}] {s.CountText}　{s.Summary}");
            if (s.Added.Count > 0) sb.AppendLine("  新增：" + string.Join("、", s.Added)
                + (s.AddedCount > s.Added.Count ? $" 等 {s.AddedCount} 项" : ""));
            if (s.Removed.Count > 0) sb.AppendLine("  删除：" + string.Join("、", s.Removed)
                + (s.RemovedCount > s.Removed.Count ? $" 等 {s.RemovedCount} 项" : ""));
            if (s.Changed.Count > 0) sb.AppendLine("  修改：" + string.Join("、", s.Changed)
                + (s.ChangedCount > s.Changed.Count ? $" 等 {s.ChangedCount} 项" : ""));
        }
        foreach (var f in d.Files.Where(f => !f.Same))
            sb.AppendLine($"[{f.Label}] {f.StatusText}（{f.RelPath}）");
        if (d.Same) sb.AppendLine("(两个版本完全一致)");
        return sb.ToString();
    }

    private static string Local(string iso)
        => DateTimeOffset.TryParse(iso, out var t) ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : (iso ?? "");
}
