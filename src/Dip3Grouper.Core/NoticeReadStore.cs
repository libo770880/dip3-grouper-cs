namespace Dip3Grouper;

/// <summary><c>data/notice-read.json</c> 的落盘结构。</summary>
public sealed class NoticeReadFile
{
    public string UpdatedAt { get; set; }

    /// <summary>已读的消息 id（打开消息面板即视为读完）</summary>
    public List<string> Read { get; set; } = new();

    /// <summary>本机隐藏的横幅消息 id（点横幅上的 ✕；不影响别人，也不删服务端数据）</summary>
    public List<string> Hidden { get; set; } = new();
}

/// <summary>
/// 「这条消息我读过没有 / 我关掉过没有」的本机记忆（<c>data/notice-read.json</c>）。
///
/// <para>它是**本机私有**的：每个人读没读过只跟自己那台机器有关，服务端不关心，
/// 因此这个文件不进数据分发白名单，也不会被下发或覆盖。</para>
///
/// <para>文件会随消息不断增删而膨胀，所以每次拉取到新列表后调用 <see cref="KeepOnly"/>
/// 把已经不在服务端列表里的 id 清掉。</para>
/// </summary>
public sealed class NoticeReadStore
{
    private readonly HashSet<string> _read = new(StringComparer.Ordinal);
    private readonly HashSet<string> _hidden = new(StringComparer.Ordinal);

    public NoticeReadStore() => Load();

    public void Load()
    {
        NoticeReadFile f = null;
        try { f = Js.ReadJsonFile<NoticeReadFile>(AppPaths.NoticeReadFile); }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[notice-read] 已读记录解析失败，按全部未读处理: {e.Message}");
        }

        _read.Clear();
        _hidden.Clear();
        foreach (var id in f?.Read ?? new List<string>()) if (!string.IsNullOrWhiteSpace(id)) _read.Add(id.Trim());
        foreach (var id in f?.Hidden ?? new List<string>()) if (!string.IsNullOrWhiteSpace(id)) _hidden.Add(id.Trim());
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        Js.WriteJsonFile(AppPaths.NoticeReadFile, new NoticeReadFile
        {
            UpdatedAt = Js.Iso(),
            Read = _read.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            Hidden = _hidden.OrderBy(x => x, StringComparer.Ordinal).ToList(),
        });
    }

    public bool IsRead(string id) => _read.Contains((id ?? "").Trim());

    public bool IsHidden(string id) => _hidden.Contains((id ?? "").Trim());

    /// <summary>未读条数（<paramref name="items"/> 为服务端当前生效列表）</summary>
    public int UnreadCount(IEnumerable<NoticeView> items)
        => (items ?? Enumerable.Empty<NoticeView>()).Count(n => !IsRead(n.Id));

    /// <summary>未读的那些（保持原顺序）</summary>
    public List<NoticeView> Unread(IEnumerable<NoticeView> items)
        => (items ?? Enumerable.Empty<NoticeView>()).Where(n => !IsRead(n.Id)).ToList();

    /// <summary>标记为已读；返回是否真的有变化（没变化就不落盘）。</summary>
    public bool MarkRead(IEnumerable<string> ids)
    {
        var changed = false;
        foreach (var raw in ids ?? Enumerable.Empty<string>())
        {
            var id = (raw ?? "").Trim();
            if (id.Length == 0) continue;
            if (_read.Add(id)) changed = true;
        }
        if (changed) Save();
        return changed;
    }

    /// <summary>把当前列表里除 <paramref name="keepId"/> 外的全部标为已读（并隐藏对应横幅）</summary>
    public bool MarkAllReadExcept(IEnumerable<NoticeView> items, string keepId)
    {
        var keep = (keepId ?? "").Trim();
        var changed = false;
        foreach (var n in items ?? Enumerable.Empty<NoticeView>())
        {
            if (string.Equals(n.Id, keep, StringComparison.Ordinal)) continue;
            if (_read.Add(n.Id)) changed = true;
        }
        if (changed) Save();
        return changed;
    }

    /// <summary>本机不再显示某条消息的横幅（点 ✕）。返回是否真的有变化。</summary>
    public bool Hide(string id)
    {
        var k = (id ?? "").Trim();
        if (k.Length == 0 || !_hidden.Add(k)) return false;
        Save();
        return true;
    }

    /// <summary>清掉已经不在服务端列表里的 id，避免文件无限增长。返回是否发生了清理。</summary>
    public bool KeepOnly(IEnumerable<string> liveIds)
    {
        var live = new HashSet<string>((liveIds ?? Enumerable.Empty<string>()).Select(x => (x ?? "").Trim()),
            StringComparer.Ordinal);
        var n = _read.Count(x => !live.Contains(x)) + _hidden.Count(x => !live.Contains(x));
        if (n == 0) return false;
        _read.RemoveWhere(x => !live.Contains(x));
        _hidden.RemoveWhere(x => !live.Contains(x));
        Save();
        return true;
    }

    /// <summary>全部标记为已读（消息面板的「全部已读」按钮）</summary>
    public bool MarkAllRead(IEnumerable<NoticeView> items)
        => MarkAllReadExcept(items, null);
}
