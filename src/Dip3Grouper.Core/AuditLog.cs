using System.Text;

namespace Dip3Grouper;

/// <summary>「谁、在什么时候、对什么做了什么」——一条审计记录。</summary>
public sealed class AuditEntry
{
    public string Id { get; set; }

    /// <summary>发生时间（ISO，UTC）</summary>
    public string At { get; set; }

    /// <summary>操作者（后台账号；未启用登录或桌面端未登录时为「本机」）</summary>
    public string User { get; set; }

    /// <summary>来源：桌面端 / 网页端 / 系统</summary>
    public string Source { get; set; }

    /// <summary>动作类别（见 <see cref="AuditAction"/>）</summary>
    public string Action { get; set; }

    /// <summary>操作对象（病种库 / 立项指南 / 映射与灰码 / 德州物价 / 系统设置 / 数据 / 版本 …）</summary>
    public string Target { get; set; }

    /// <summary>详情描述</summary>
    public string Detail { get; set; }

    /// <summary>是否成功</summary>
    public bool Ok { get; set; } = true;

    /// <summary>失败原因等补充说明</summary>
    public string Note { get; set; }

    // ---- 界面直读 ----

    /// <summary>
    /// 界面显示用时间：当天的记录只显示「时:分:秒」，往前的才带日期。
    /// 审计页一屏几十行全带 <c>2026-10-30</c> 前缀，真正有用的时刻反而被挤到右边。
    /// 导出 CSV 用 <see cref="AtFullText"/>，不能省日期。
    /// </summary>
    public string AtText
    {
        get
        {
            if (!DateTimeOffset.TryParse(At, out var t)) return At ?? "";
            var l = t.ToLocalTime();
            return l.Date == DateTime.Today ? l.ToString("HH:mm:ss") : l.ToString("MM-dd HH:mm:ss");
        }
    }

    /// <summary>完整时间（导出 / 悬停提示用，永远带日期）</summary>
    public string AtFullText
        => DateTimeOffset.TryParse(At, out var t) ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : (At ?? "");

    public string ResultText => Ok ? "成功" : "失败";

    public string Summary => string.IsNullOrWhiteSpace(Detail) ? Target : Detail;
}

/// <summary>操作者身份（用于把「谁干的」带进 Core 层的写操作）。</summary>
public sealed class AuditActor
{
    public string User { get; set; }
    public string Source { get; set; }
}

/// <summary>审计动作类别常量。</summary>
public static class AuditAction
{
    public const string Login = "登录";
    public const string Logout = "退出";
    public const string Import = "导入";
    public const string Add = "新增";
    public const string Edit = "编辑";
    public const string Delete = "删除";
    public const string Save = "保存";
    public const string Update = "数据更新";
    public const string Version = "版本操作";
    public const string Setting = "修改设置";
    public const string Export = "导出";
}

/// <summary>查询条件。</summary>
public sealed class AuditFilter
{
    /// <summary>关键词（在动作 / 对象 / 详情 / 账号 / 备注里模糊匹配）</summary>
    public string Keyword { get; set; }

    /// <summary>动作类别精确匹配（空 = 不限）</summary>
    public string Action { get; set; }

    /// <summary>对象精确匹配（空 = 不限）</summary>
    public string Target { get; set; }

    /// <summary>只看失败（null = 不限）</summary>
    public bool? Ok { get; set; }

    /// <summary>最多返回条数</summary>
    public int Limit { get; set; } = 500;
}

/// <summary>审计日志的统计概览。</summary>
public sealed class AuditStats
{
    public int Total { get; set; }
    public int Today { get; set; }
    public int Failed { get; set; }
    public List<(string Action, int Count)> TopActions { get; set; } = new();
    public string LastAtText { get; set; } = "—";
}

/// <summary>
/// 操作审计日志：把后台的写操作按时间追加到 <c>data/audit-log.json</c>，
/// 只增不改、环形封顶，支持按动作 / 对象 / 关键词检索与导出 CSV。
///
/// <para>「谁」的来源分两层：网页端在处理一次请求时用 <see cref="Scope"/> 把会话账号
/// 压进 <see cref="AsyncLocal{T}"/>（自动流进其内被 await 的 Core 调用）；
/// 桌面端是单用户程序，登录后用 <see cref="SetDefaultActor"/> 设一次全局身份即可。</para>
///
/// <para>日志文件属于**本机私有数据**：它是每台机器各自的操作记录，
/// 不进 <see cref="DataCatalog"/> 分发白名单，避免一次同步把别院的日志覆盖过来。</para>
/// </summary>
public static class AuditLog
{
    /// <summary>保留的最大条数（超出后丢弃最旧的），避免文件无限膨胀</summary>
    public const int MaxEntries = 3000;

    private static readonly object Gate = new();
    private static readonly AsyncLocal<AuditActor> _ambient = new();
    private static AuditActor _fallback = new() { User = "本机", Source = "桌面端" };
    private static List<AuditEntry> _cache;

    /// <summary>当前生效的操作者（可能为 null，由调用方兜底）</summary>
    public static AuditActor Ambient => _ambient.Value ?? _fallback;

    /// <summary>设置全局默认身份（桌面端登录后调用；Web 端不用，走 <see cref="Scope"/>）</summary>
    public static void SetDefaultActor(string user, string source)
    {
        _fallback = new AuditActor
        {
            User = string.IsNullOrWhiteSpace(user) ? "本机" : user.Trim(),
            Source = string.IsNullOrWhiteSpace(source) ? "桌面端" : source.Trim(),
        };
    }

    /// <summary>临时切换身份（网页端按请求作用域使用，<c>using</c> 结束自动还原）</summary>
    public static IDisposable Scope(string user, string source)
    {
        var prev = _ambient.Value;
        _ambient.Value = new AuditActor
        {
            User = string.IsNullOrWhiteSpace(user) ? "匿名" : user.Trim(),
            Source = string.IsNullOrWhiteSpace(source) ? "网页端" : source.Trim(),
        };
        return new Restore(prev);
    }

    private sealed class Restore : IDisposable
    {
        private readonly AuditActor _prev;
        public Restore(AuditActor prev) { _prev = prev; }
        public void Dispose() => _ambient.Value = _prev;
    }

    public static string File => Path.Combine(AppPaths.DataDir, "audit-log.json");

    // ==================================================================
    // 写入
    // ==================================================================

    /// <summary>
    /// 追加一条记录。写入失败绝不影响主流程（审计是旁路，不能因为写不了日志就让业务失败）。
    /// </summary>
    public static AuditEntry Write(string action, string target, string detail,
        bool ok = true, string note = null, string user = null, string source = null)
    {
        var actor = Ambient;
        var e = new AuditEntry
        {
            Id = DateTime.Now.ToString("yyyyMMddHHmmssfff"),
            At = Js.Iso(),
            User = string.IsNullOrWhiteSpace(user) ? (actor?.User ?? "本机") : user.Trim(),
            Source = string.IsNullOrWhiteSpace(source) ? (actor?.Source ?? "桌面端") : source.Trim(),
            Action = string.IsNullOrWhiteSpace(action) ? "操作" : action.Trim(),
            Target = (target ?? "").Trim(),
            Detail = (detail ?? "").Trim(),
            Ok = ok,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
        };

        if (e.Detail.Length == 0) e.Detail = e.Target;

        try
        {
            lock (Gate)
            {
                var list = Load();
                list.Add(e);
                if (list.Count > MaxEntries) list.RemoveRange(0, list.Count - MaxEntries);
                _cache = list;
                Save(list);
            }
        }
        catch { /* 审计写失败不阻断业务 */ }

        return e;
    }

    // ==================================================================
    // 读取
    // ==================================================================

    /// <summary>全部记录（新的在前）</summary>
    public static List<AuditEntry> All()
    {
        lock (Gate) return Load().OrderByDescending(e => e.Id ?? "", StringComparer.Ordinal).ToList();
    }

    /// <summary>按条件检索（结果新的在前，条数受 <see cref="AuditFilter.Limit"/> 限制）</summary>
    public static List<AuditEntry> Query(AuditFilter f)
    {
        f ??= new AuditFilter();
        var kw = (f.Keyword ?? "").Trim();
        IEnumerable<AuditEntry> q = All();

        if (!string.IsNullOrEmpty(f.Action)) q = q.Where(e => e.Action == f.Action);
        if (!string.IsNullOrEmpty(f.Target)) q = q.Where(e => e.Target == f.Target);
        if (f.Ok.HasValue) q = q.Where(e => e.Ok == f.Ok.Value);
        if (kw.Length > 0)
        {
            q = q.Where(e =>
                Contains(e.Action, kw) || Contains(e.Target, kw) || Contains(e.Detail, kw) ||
                Contains(e.User, kw) || Contains(e.Note, kw) || Contains(e.Source, kw));
        }

        var limit = f.Limit <= 0 ? 500 : f.Limit;
        return q.Take(limit).ToList();
    }

    /// <summary>所有出现过的动作类别（供界面下拉）</summary>
    public static List<string> Actions()
        => All().Select(e => e.Action).Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal).ToList();

    /// <summary>所有出现过的对象（供界面下拉）</summary>
    public static List<string> Targets()
        => All().Select(e => e.Target).Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal).ToList();

    public static AuditStats Stats()
    {
        var all = All();
        var today = DateTime.Now.Date;
        return new AuditStats
        {
            Total = all.Count,
            Today = all.Count(e => DateTimeOffset.TryParse(e.At, out var t) && t.ToLocalTime().Date == today),
            Failed = all.Count(e => !e.Ok),
            TopActions = all.GroupBy(e => e.Action)
                .Select(g => (Action: g.Key, Count: g.Count()))
                .OrderByDescending(x => x.Count).Take(6).ToList(),
            LastAtText = all.Count > 0 ? all[0].AtFullText : "—",
        };
    }

    /// <summary>清空全部记录</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            _cache = new List<AuditEntry>();
            Save(_cache);
        }
    }

    /// <summary>丢弃内存缓存、下次访问重新读盘（日志文件被外部修改后用它刷新）</summary>
    public static void Reload()
    {
        lock (Gate) _cache = null;
    }

    // ==================================================================
    // 导出
    // ==================================================================

    /// <summary>
    /// 导出为 CSV 的字节（UTF-8，BOM 在 <see cref="ExportCsv"/> 的内容里带且只带一次）。
    /// 写盘一律走这里：用 <c>UTF8Encoding(true)</c> 会再补一个前导码，文件头变成两个 BOM。
    /// </summary>
    public static byte[] ExportCsvBytes(IEnumerable<AuditEntry> entries)
        => new UTF8Encoding(false).GetBytes(ExportCsv(entries));

    /// <summary>导出为 CSV（带 UTF-8 BOM，双击用 Excel 打开不乱码）。</summary>
    public static string ExportCsv(IEnumerable<AuditEntry> entries)
    {
        var sb = new StringBuilder();
        sb.Append('\uFEFF');
        sb.AppendLine("时间,账号,来源,动作,对象,详情,结果,备注");
        foreach (var e in entries ?? Enumerable.Empty<AuditEntry>())
        {
            sb.Append(Csv(e.AtFullText)).Append(',')
              .Append(Csv(e.User)).Append(',')
              .Append(Csv(e.Source)).Append(',')
              .Append(Csv(e.Action)).Append(',')
              .Append(Csv(e.Target)).Append(',')
              .Append(Csv(e.Detail)).Append(',')
              .Append(Csv(e.ResultText)).Append(',')
              .Append(Csv(e.Note)).AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>CSV 字段转义：含逗号/引号/换行时用双引号包裹并把内部引号翻倍。</summary>
    private static string Csv(string s)
    {
        var v = s ?? "";
        if (v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return v;
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }

    // ==================================================================
    // 内部
    // ==================================================================

    private static bool Contains(string hay, string needle)
        => (hay ?? "").IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    private static List<AuditEntry> Load()
    {
        if (_cache != null) return new List<AuditEntry>(_cache);
        List<AuditEntry> list = null;
        try { list = Js.ReadJsonFile<List<AuditEntry>>(File); }
        catch { /* 文件损坏时从空开始，不让审计把程序拖死 */ }
        _cache = list ?? new List<AuditEntry>();
        return new List<AuditEntry>(_cache);
    }

    private static void Save(List<AuditEntry> list)
        => Js.WriteJsonFile(File, list, indent: 1);
}
