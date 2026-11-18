using System.Globalization;

namespace Dip3Grouper;

/// <summary>推送到前台的一条消息（通知 / 公告）。</summary>
public sealed class Notice
{
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    /// <summary>正文，允许换行（前台按原样保留空行与缩进）。</summary>
    public string Body { get; set; } = "";

    /// <summary>级别：<c>info</c> 一般（蓝） / <c>warn</c> 提醒（橙） / <c>danger</c> 重要（红）。</summary>
    public string Level { get; set; } = NoticeStore.LevelInfo;

    /// <summary>已发布：前台可见。关掉即草稿，只有后台能看见。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>钉住：在**前台导航栏下方常驻横幅**显示（不钉的只进「消息中心」抽屉）。</summary>
    public bool Pinned { get; set; }

    /// <summary>生效开始（本地时间 <c>yyyy-MM-ddTHH:mm</c>，空 = 立即生效）。</summary>
    public string StartAt { get; set; } = "";

    /// <summary>失效时间（本地时间，空 = 长期有效）。</summary>
    public string EndAt { get; set; } = "";

    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";

    /// <summary>发布者（取后台登录账号；关闭登录校验时为空）。</summary>
    public string Author { get; set; } = "";
}

public sealed class NoticeFile
{
    public string UpdatedAt { get; set; }

    /// <summary>版本号：每次落盘 +1。前台拿它做「有没有变化」的轻量判断，不必逐条比对。</summary>
    public int Rev { get; set; }

    public List<Notice> Items { get; set; } = new();
}

/// <summary>
/// 前台消息中心的数据源（<c>data/notice.json</c>）。
///
/// <para>它是**运营数据**：后台编辑发布，前台（局域网内各科室浏览器）轮询 <c>/api/notice</c> 取回。
/// 三条硬规则：</para>
/// <list type="bullet">
///   <item><b>草稿不外泄</b>：<see cref="Enabled"/>=false 的记录只出现在后台接口里，
///         前台接口只回「已发布 <b>且</b> 处于生效期内」的，连生效时间也不外泄；</item>
///   <item><b>生效期用本地时间</b>：存 <c>yyyy-MM-ddTHH:mm</c> 原样（<c>datetime-local</c> 的原生格式），
///         不做时区换算 —— 排班通知本来就是「本地墙上的钟」语义，跨时区反而会错；</item>
///   <item><b>版本号单调递增</b>：任何落盘都让 <see cref="Rev"/> 加一，前台据此判断是否需要重绘。</item>
/// </list>
/// </summary>
public sealed class NoticeStore
{
    public const string LevelInfo = "info";
    public const string LevelWarn = "warn";
    public const string LevelDanger = "danger";

    public const int MaxTitle = 60;
    public const int MaxBody = 2000;

    /// <summary>时间字段格式（与浏览器 <c>datetime-local</c> 的取值一致）</summary>
    public const string TimeFormat = "yyyy-MM-ddTHH:mm";

    private readonly object _gate = new();

    /// <summary>全部消息（含草稿），按后台列表顺序：钉住优先、最近更新在前。</summary>
    public List<Notice> Items { get; private set; } = new();

    public int Rev { get; private set; }

    public NoticeStore() => Load();

    // ------------------------------------------------------------------
    // 载入 / 落盘
    // ------------------------------------------------------------------

    public void Load()
    {
        NoticeFile f = null;
        try
        {
            f = Js.ReadJsonFile<NoticeFile>(AppPaths.NoticeFile);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[notice] notice.json 解析失败，按空消息处理: {e.Message}");
        }

        var items = (f?.Items ?? new List<Notice>())
            .Where(n => n != null && !string.IsNullOrWhiteSpace(n.Id))
            .Select(Normalize)
            .ToList();

        lock (_gate)
        {
            Items = items;
            Rev = f?.Rev ?? 0;
        }
    }

    public void Save()
    {
        int rev;
        List<Notice> snapshot;
        lock (_gate)
        {
            Rev++;
            rev = Rev;
            snapshot = Items.ToList();
        }
        Directory.CreateDirectory(AppPaths.DataDir);
        // 缩进对齐 data/ 下其余业务文件（Node 侧 JSON.stringify(v, null, 1)）
        Js.WriteJsonFile(AppPaths.NoticeFile, new NoticeFile
        {
            UpdatedAt = Js.Iso(),
            Rev = rev,
            Items = snapshot,
        });
    }

    // ------------------------------------------------------------------
    // 查询
    // ------------------------------------------------------------------

    /// <summary>当前生效中的消息（已发布 + 在生效期内），钉住的排前面，其余按最近更新在前。</summary>
    public List<Notice> Active(DateTime now)
    {
        List<Notice> all;
        lock (_gate) all = Items.ToList();
        return all.Where(n => IsActive(n, now))
                  .OrderByDescending(n => n.Pinned)
                  .ThenByDescending(n => n.UpdatedAt ?? "", StringComparer.Ordinal)
                  .ToList();
    }

    /// <summary>后台列表：全部消息（含草稿），钉住优先、最近更新在前。</summary>
    public List<Notice> All()
    {
        lock (_gate)
            return Items.OrderByDescending(n => n.Pinned)
                        .ThenByDescending(n => n.UpdatedAt ?? "", StringComparer.Ordinal)
                        .ToList();
    }

    /// <summary>
    /// 前台可见的字段集（**刻意不含** StartAt/EndAt/Enabled）：草稿与排期属运营内部信息，
    /// 没必要让每个浏览器都拿到；前台只需要「显示什么」。
    /// </summary>
    public List<object> ActiveForFront(DateTime now)
        => Active(now).Select(n => (object)new
        {
            id = n.Id,
            title = n.Title,
            body = n.Body,
            level = n.Level,
            pinned = n.Pinned,
            updatedAt = n.UpdatedAt,
            createdAt = n.CreatedAt,
            author = n.Author,
        }).ToList();

    public Notice Find(string id)
    {
        var k = (id ?? "").Trim();
        if (k.Length == 0) return null;
        lock (_gate) return Items.FirstOrDefault(n => n.Id == k);
    }

    /// <summary>是否处于生效期内（已发布 + 未到失效 + 已过开始）。</summary>
    public static bool IsActive(Notice n, DateTime now)
    {
        if (n == null || !n.Enabled) return false;
        if (ParseTime(n.StartAt) is { } s && now < s) return false;
        if (ParseTime(n.EndAt) is { } e && now > e) return false;
        return true;
    }

    /// <summary>解析 <c>yyyy-MM-ddTHH:mm</c>；空 / 非法返回 null（空 = 不限，非法已在保存时拦下）。</summary>
    public static DateTime? ParseTime(string s)
    {
        var t = (s ?? "").Trim();
        if (t.Length == 0) return null;
        if (DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) return dt;
        return null;
    }

    // ------------------------------------------------------------------
    // 维护
    // ------------------------------------------------------------------

    /// <summary>
    /// 新增（<paramref name="id"/> 为空）或更新一条消息。返回 (错误, 记录)；错误非空时记录无意义。
    /// </summary>
    public (string Err, Notice Item) Upsert(string id, string title, string body, string level, bool pinned,
        string startAt, string endAt, bool enabled, string author)
    {
        var t = (title ?? "").Trim();
        var b = (body ?? "").Replace("\r\n", "\n").Trim();
        var lv = NormalizeLevel(level);
        var sa = (startAt ?? "").Trim();
        var ea = (endAt ?? "").Trim();

        if (t.Length == 0 && b.Length == 0) return ("标题与正文不能都为空", null);
        if (t.Length > MaxTitle) return ($"标题最长 {MaxTitle} 字（当前 {t.Length} 字）", null);
        if (b.Length > MaxBody) return ($"正文最长 {MaxBody} 字（当前 {b.Length} 字）", null);

        if (sa.Length > 0 && ParseTime(sa) == null) return ("生效开始时间格式不正确", null);
        if (ea.Length > 0 && ParseTime(ea) == null) return ("失效时间格式不正确", null);
        if (sa.Length > 0 && ea.Length > 0 && ParseTime(ea) < ParseTime(sa))
            return ("失效时间不能早于生效开始时间", null);

        // 标题留空时用正文首行兜底，列表里不至于出现无名记录
        if (t.Length == 0) t = FirstLine(b, MaxTitle);

        var key = (id ?? "").Trim();
        var now = DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture);

        Notice item;
        lock (_gate)
        {
            if (key.Length == 0)
            {
                item = new Notice { Id = NewId(), CreatedAt = now };
                Items.Add(item);
            }
            else
            {
                item = Items.FirstOrDefault(n => n.Id == key);
                if (item == null) return ("消息不存在（可能已被其他管理员删除）", null);
            }
            item.Title = t;
            item.Body = b;
            item.Level = lv;
            item.Pinned = pinned;
            item.StartAt = sa;
            item.EndAt = ea;
            item.Enabled = enabled;
            item.UpdatedAt = now;
            if (!string.IsNullOrWhiteSpace(author)) item.Author = (author ?? "").Trim();
        }
        Save();
        return (null, item);
    }

    public bool Remove(string id)
    {
        bool removed;
        lock (_gate)
        {
            var cur = Items.FirstOrDefault(n => n.Id == (id ?? "").Trim());
            removed = cur != null;
            if (removed) Items.Remove(cur);
        }
        if (removed) Save();
        return removed;
    }

    /// <summary>发布 / 停用（草稿）。</summary>
    public bool SetEnabled(string id, bool on)
    {
        Notice item;
        lock (_gate) item = Items.FirstOrDefault(n => n.Id == (id ?? "").Trim());
        if (item == null) return false;
        if (item.Enabled == on) return true;   // 幂等：不产生无意义的版本号变化
        lock (_gate) item.Enabled = on;
        Save();
        return true;
    }

    /// <summary>清掉全部草稿（已停用的）；返回清掉的数量。</summary>
    public int ClearDrafts()
    {
        int n;
        lock (_gate)
        {
            var drafts = Items.Where(x => !x.Enabled).ToList();
            n = drafts.Count;
            foreach (var d in drafts) Items.Remove(d);
        }
        if (n > 0) Save();
        return n;
    }

    /// <summary>后台统计：总数 / 生效中 / 草稿 / 已过期 / 钉住。</summary>
    public object Stats()
    {
        List<Notice> all;
        lock (_gate) all = Items.ToList();
        var now = DateTime.Now;
        bool expired(Notice n) => n.Enabled && ParseTime(n.EndAt) is { } e && now > e;
        return new
        {
            total = all.Count,
            active = all.Count(n => IsActive(n, now)),
            draft = all.Count(n => !n.Enabled),
            expired = all.Count(expired),
            pinned = all.Count(n => n.Pinned && IsActive(n, now)),
            scheduled = all.Count(n => n.Enabled && ParseTime(n.StartAt) is { } s && now < s),
        };
    }

    // ------------------------------------------------------------------

    /// <summary>8 位十六进制短 id（够用且便于对着日志念）。</summary>
    public static string NewId()
        => Guid.NewGuid().ToString("N")[..8];

    public static string NormalizeLevel(string level)
    {
        var v = (level ?? "").Trim().ToLowerInvariant();
        return v switch
        {
            LevelWarn => LevelWarn,
            LevelDanger => LevelDanger,
            _ => LevelInfo,
        };
    }

    private static Notice Normalize(Notice n)
    {
        n.Id = (n.Id ?? "").Trim();
        n.Title = (n.Title ?? "").Trim();
        n.Body = (n.Body ?? "").Replace("\r\n", "\n").Trim();
        n.Level = NormalizeLevel(n.Level);
        n.StartAt = (n.StartAt ?? "").Trim();
        n.EndAt = (n.EndAt ?? "").Trim();
        n.Author = n.Author ?? "";
        n.CreatedAt = n.CreatedAt ?? "";
        n.UpdatedAt = n.UpdatedAt ?? "";
        return n;
    }

    private static string FirstLine(string text, int max)
    {
        var line = (text ?? "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        return line.Length > max ? line[..max] : line;
    }
}
