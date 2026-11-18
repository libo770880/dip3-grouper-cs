using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Dip3Grouper;

/// <summary>
/// 网页后台的登录会话（Web 版专有；桌面端走 LoginWindow，不用这里）。
///
/// 设计取舍：
/// <list type="bullet">
///   <item>**纯内存、不落盘**。令牌一旦写进文件就会跟着绿色版目录被拷走、被分发，
///         反而制造新的泄漏面；进程重启后所有人重新登录一次也无所谓。</item>
///   <item>只守「改动数据」的接口，浏览/查询/模板/导出始终免登录——医院里多数人只是想看数据，
///         让他们先登录一次纯属添堵。</item>
///   <item>口令本身仍由 <see cref="SettingsStore"/> 校验（PBKDF2-SHA256 / 10 万次迭代），
///         与桌面端共用 settings.json，改一处两端一致。</item>
///   <item>登录失败按来源 IP 限速：连续 5 次失败锁 60 秒，成功即清零。
///         按 IP 而非全局计数，避免有人误输几次就把管理员一起锁在门外。</item>
/// </list>
/// </summary>
public static class WebSession
{
    /// <summary>会话 Cookie 名（HttpOnly，浏览器自动携带）</summary>
    public const string CookieName = "dip3_admin";

    /// <summary>会话令牌请求头名（给 curl / 脚本用，免得手工拼 Cookie）</summary>
    public const string HeaderName = "X-DIP3-Admin";

    /// <summary>会话有效期</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(8);

    // 下面三项是策略参数，写成可设置的静态属性（而非 const）是为了让自检能把时间调小，
    // 从而真正验证「锁定到期后计数归零」这条分支——否则只能靠等 60 秒。

    /// <summary>触发锁定前允许的连续失败次数</summary>
    public static int MaxFails { get; set; } = 5;

    /// <summary>锁定时长</summary>
    public static TimeSpan LockTime { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>失败计数冷静期：距上次失败超过这个时间就重新给满额度</summary>
    public static TimeSpan FailWindow { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>恢复出厂策略参数（自检改过之后调用）</summary>
    public static void ResetPolicy()
    {
        MaxFails = 5;
        LockTime = TimeSpan.FromSeconds(60);
        FailWindow = TimeSpan.FromMinutes(10);
    }

    private static readonly ConcurrentDictionary<string, DateTime> _sessions = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Attempt> _attempts = new(StringComparer.Ordinal);
    private static readonly object _attemptLock = new();

    private sealed class Attempt
    {
        public int Fails;
        public DateTime LastFail;    // UTC
        public DateTime LockUntil;   // UTC；MinValue = 未锁定
    }

    /// <summary>签发一个新会话，返回令牌。</summary>
    public static string Create()
    {
        Prune();
        // 32 字节 = 256 位，会话期间不可猜；十六进制编码便于放进 Cookie
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _sessions[token] = DateTime.UtcNow.Add(Ttl);
        return token;
    }

    /// <summary>令牌是否仍然有效（不存在 / 已过期都算无效，过期项顺手清掉）。</summary>
    public static bool Valid(string token)
    {
        var t = (token ?? "").Trim();
        if (t.Length == 0) return false;
        if (!_sessions.TryGetValue(t, out var exp)) return false;
        if (exp > DateTime.UtcNow) return true;
        _sessions.TryRemove(t, out _);
        return false;
    }

    /// <summary>注销单个令牌（退出登录）。</summary>
    public static void Drop(string token)
    {
        var t = (token ?? "").Trim();
        if (t.Length > 0) _sessions.TryRemove(t, out _);
    }

    /// <summary>
    /// 只保留给定令牌，清掉其余会话。改口令时调用：让刚刚改口令的这个人留在原地，
    /// 其他人（可能正拿着旧口令开的会话）立刻掉线。
    /// </summary>
    public static void KeepOnly(string token)
    {
        var keep = (token ?? "").Trim();
        foreach (var k in _sessions.Keys)
            if (!string.Equals(k, keep, StringComparison.Ordinal)) _sessions.TryRemove(k, out _);
    }

    /// <summary>清空全部会话（关闭登录校验时调用，避免留下无主会话）。</summary>
    public static void Clear()
    {
        _sessions.Clear();
        lock (_attemptLock) _attempts.Clear();
    }

    /// <summary>当前有效会话数（自检与诊断用）。</summary>
    public static int ActiveCount
    {
        get { Prune(); return _sessions.Count; }
    }

    // ==================================================================
    // 失败限速
    // ==================================================================

    /// <summary>该来源剩余锁定秒数（0 = 未锁定，可继续尝试）。</summary>
    public static int LockRemaining(string source)
    {
        var key = Key(source);
        lock (_attemptLock)
        {
            if (!_attempts.TryGetValue(key, out var a)) return 0;
            var left = (a.LockUntil - DateTime.UtcNow).TotalSeconds;
            return left > 0 ? (int)Math.Ceiling(left) : 0;
        }
    }

    /// <summary>
    /// 记一次失败；达到阈值即锁定。
    ///
    /// 注意：锁定到期后必须把计数归零（以及距上次失败超过冷静期也归零），
    /// 否则计数会一直停在阈值上——之后**每一次**尝试都立刻触发新锁定，
    /// 等于输错 5 次就永久锁死，管理员自己都进不来。
    /// </summary>
    public static void NoteFail(string source)
    {
        var key = Key(source);
        var now = DateTime.UtcNow;
        lock (_attemptLock)
        {
            if (!_attempts.TryGetValue(key, out var a)) _attempts[key] = a = new Attempt();

            var lockExpired = a.LockUntil != DateTime.MinValue && a.LockUntil <= now;
            var cooled = a.LastFail != DateTime.MinValue && (now - a.LastFail) > FailWindow;
            if (lockExpired || cooled) a.Fails = 0;

            a.Fails++;
            a.LastFail = now;
            if (a.Fails >= MaxFails) a.LockUntil = now.Add(LockTime);
        }
    }

    /// <summary>登录成功：清零该来源的失败记录。</summary>
    public static void NoteOk(string source)
    {
        var key = Key(source);
        lock (_attemptLock) _attempts.Remove(key);
    }

    /// <summary>该来源已连续失败次数（诊断用）。</summary>
    public static int FailCount(string source)
    {
        var key = Key(source);
        lock (_attemptLock) return _attempts.TryGetValue(key, out var a) ? a.Fails : 0;
    }

    private static string Key(string source)
        => string.IsNullOrWhiteSpace(source) ? "?" : source.Trim();

    /// <summary>清掉已过期的会话，避免长时间运行后字典无限增长。</summary>
    private static void Prune()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _sessions)
            if (kv.Value <= now) _sessions.TryRemove(kv.Key, out _);
    }
}
