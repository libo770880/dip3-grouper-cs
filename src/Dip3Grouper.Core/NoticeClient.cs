using System.Net.Http;

namespace Dip3Grouper;

/// <summary>
/// 前台看到的一条消息。<b>只有展示需要的字段</b> —— 服务端 <c>/api/notice</c> 刻意不回
/// 生效排期与发布开关（草稿、排期属于运营内部信息）。
/// </summary>
public sealed class NoticeView
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";

    /// <summary><c>info</c> 一般 / <c>warn</c> 提醒 / <c>danger</c> 重要</summary>
    public string Level { get; set; } = "info";

    /// <summary>钉住的消息在导航栏下方常驻横幅显示</summary>
    public bool Pinned { get; set; }

    public string UpdatedAt { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string Author { get; set; } = "";

    /// <summary>级别对应的中文名（界面直接显示）</summary>
    public string LevelText => Level switch
    {
        "warn" => "提醒",
        "danger" => "重要",
        _ => "一般",
    };

    /// <summary>时间的紧凑显示（<c>MM-dd HH:mm</c>；解析失败原样返回）</summary>
    public string TimeText
    {
        get
        {
            var raw = string.IsNullOrWhiteSpace(UpdatedAt) ? CreatedAt : UpdatedAt;
            return DateTime.TryParse(raw, out var t) ? t.ToString("MM-dd HH:mm") : (raw ?? "");
        }
    }
}

/// <summary>服务端 <c>GET /api/notice</c> 的原始应答。</summary>
public sealed class NoticeFeed
{
    public int Rev { get; set; }
    public string ServerTime { get; set; } = "";
    public List<NoticeView> Items { get; set; } = new();
}

/// <summary>一次拉取的结果，供界面直接显示（网络异常不抛，翻译成人话）。</summary>
public sealed class NoticeFetchResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public string Server { get; set; } = "";
    public int Rev { get; set; }
    public List<NoticeView> Items { get; set; } = new();
}

/// <summary>
/// 从局域网数据源拉取「通知公告」——桌面客户端的消息提示走这条路。
///
/// <para>与网页前台用的是同一个接口（<c>GET /api/notice</c>）：免登录、只回「已发布且在生效期内」的消息。
/// 配了共享口令时自动带上 <c>?token=</c>（服务端当前不校验这个接口，带上也不会出错，
/// 将来若把它纳入口令保护则客户端不用改）。</para>
/// </summary>
public static class NoticeClient
{
    /// <summary>拉取当前生效的消息（只读，不落盘）。建议包在 try 里，或直接用 <see cref="FetchAsync"/> 的宽容版本。</summary>
    public static async Task<NoticeFeed> GetAsync(string baseUrl, CancellationToken ct = default)
    {
        var root = DataUpdateClient.Normalize(baseUrl);
        if (root.Length == 0) throw new InvalidOperationException("请先填写数据源地址。");

        using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        h.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Dip3Grouper-Desktop");
        var text = await h.GetStringAsync(DataUpdateClient.WithToken(root + "/api/notice"), ct);
        return Js.FromJson<NoticeFeed>(text) ?? new NoticeFeed();
    }

    /// <summary>拉取并翻译错误；网络不通是最常见的正常情况，所以不抛异常。</summary>
    public static async Task<NoticeFetchResult> FetchAsync(string baseUrl, CancellationToken ct = default)
    {
        var res = new NoticeFetchResult { Server = DataUpdateClient.Normalize(baseUrl) };
        if (res.Server.Length == 0)
        {
            res.Message = "未配置数据源地址。";
            return res;
        }
        try
        {
            var feed = await GetAsync(baseUrl, ct);
            res.Rev = feed.Rev;
            res.Items = (feed.Items ?? new List<NoticeView>())
                .Where(n => n != null && !string.IsNullOrWhiteSpace(n.Id))
                .ToList();
            res.Ok = true;
        }
        catch (Exception e)
        {
            res.Message = DataUpdateClient.Friendly(e);
        }
        return res;
    }
}
