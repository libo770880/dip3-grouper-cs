namespace Dip3Grouper;

/// <summary>一条「常用物价」收藏（以 15 位项目编码为键）。</summary>
public sealed class DezhouFavorite
{
    public string Code { get; set; } = "";
    /// <summary>收藏时的落地文号（仅用于展示，不作为键）</summary>
    public string Doc { get; set; } = "";
    /// <summary>收藏时的项目名称（仅用于展示，改价改名不影响收藏关系）</summary>
    public string Name { get; set; } = "";
    public string AddedAt { get; set; } = "";
}

public sealed class DezhouFavoriteFile
{
    public string UpdatedAt { get; set; }
    public List<DezhouFavorite> Items { get; set; } = new();
}

/// <summary>
/// 「常用物价」收藏。
///
/// 存放在 <c>data/dezhou-favorites.json</c>，与物价主数据 <c>data/dezhou-price.json</c> 分离：
/// 重新导入 / 调整物价数据不会影响已收藏的条目（收藏只认编码，名称与文号仅作展示）。
/// 最近收藏排在最前，便于「刚收藏的立刻能找到」。
/// </summary>
public sealed class DezhouFavoritesStore
{
    private readonly object _gate = new();
    private HashSet<string> _codes = new(StringComparer.Ordinal);

    public List<DezhouFavorite> Items { get; private set; } = new();

    public DezhouFavoritesStore() => Load();

    public int Count
    {
        get { lock (_gate) return Items.Count; }
    }

    public void Load()
    {
        try
        {
            var f = Js.ReadJsonFile<DezhouFavoriteFile>(AppPaths.DezhouFavFile);
            Items = f?.Items ?? new List<DezhouFavorite>();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[dezhou] dezhou-favorites.json 解析失败，按空收藏处理: {e.Message}");
            Items = new List<DezhouFavorite>();
        }
        Items = Items.Where(i => !string.IsNullOrWhiteSpace(i.Code)).ToList();
        lock (_gate) _codes = ToSet(Items);
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        Js.WriteJsonFile(AppPaths.DezhouFavFile,
            new DezhouFavoriteFile { UpdatedAt = Js.Iso(), Items = Items });
    }

    /// <summary>是否已收藏。</summary>
    public bool Has(string code)
    {
        var c = (code ?? "").Trim();
        lock (_gate) return c.Length > 0 && _codes.Contains(c);
    }

    /// <summary>当前收藏编码集合的副本（供筛选使用，遍历时不持有锁）。</summary>
    public HashSet<string> CodeSet()
    {
        lock (_gate) return new HashSet<string>(_codes, StringComparer.Ordinal);
    }

    /// <summary>切换收藏状态并落盘；返回切换后「是否已收藏」。</summary>
    public bool Toggle(string code, string doc = "", string name = "")
    {
        var c = (code ?? "").Trim();
        if (c.Length == 0) return false;

        bool now;
        lock (_gate)
        {
            var cur = Items.FirstOrDefault(i => i.Code == c);
            if (cur != null)
            {
                Items.Remove(cur);
                _codes.Remove(c);
                now = false;
            }
            else
            {
                Items.Insert(0, new DezhouFavorite
                {
                    Code = c,
                    Doc = doc ?? "",
                    Name = name ?? "",
                    AddedAt = Js.Iso(),
                });
                _codes.Add(c);
                now = true;
            }
        }
        Save();
        return now;
    }

    /// <summary>移出收藏（未收藏时为空操作）；返回是否发生了移除。</summary>
    public bool Remove(string code)
    {
        var c = (code ?? "").Trim();
        bool removed = false;
        lock (_gate)
        {
            var cur = Items.FirstOrDefault(i => i.Code == c);
            if (cur != null)
            {
                Items.Remove(cur);
                _codes.Remove(c);
                removed = true;
            }
        }
        if (removed) Save();
        return removed;
    }

    /// <summary>清空收藏并落盘；返回清掉的数量。</summary>
    public int Clear()
    {
        int n;
        lock (_gate)
        {
            n = Items.Count;
            Items = new List<DezhouFavorite>();
            _codes = new HashSet<string>(StringComparer.Ordinal);
        }
        if (n > 0) Save();
        return n;
    }

    private static HashSet<string> ToSet(IEnumerable<DezhouFavorite> items)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var i in items)
        {
            var c = (i.Code ?? "").Trim();
            if (c.Length > 0) set.Add(c);
        }
        return set;
    }

    /// <summary>
    /// 仅诊断用（桌面端 --shot-page dzfav 离屏截图）：只注入内存、绝不落盘。
    /// Core 与桌面端跨程序集，故开为 public —— 正常业务请走 <see cref="Toggle"/> / <see cref="Remove"/>。
    /// </summary>
    public void DiagInject(IEnumerable<(string Code, string Doc, string Name)> items)
    {
        lock (_gate)
        {
            Items = items.Select(x => new DezhouFavorite
            {
                Code = x.Code ?? "",
                Doc = x.Doc ?? "",
                Name = x.Name ?? "",
                AddedAt = Js.Iso(),
            }).ToList();
            _codes = ToSet(Items);
        }
    }
}
