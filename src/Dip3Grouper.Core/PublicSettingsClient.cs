using System.Net;
using System.Net.Http;

namespace Dip3Grouper;

/// <summary>
/// 服务端「公共设置」快照 —— 可以在局域网内共享、且不含任何敏感信息的那一部分。
///
/// <para>对应 <c>GET /api/settings/public</c>。刻意只有展示类配置：
/// 软件名称、副名、品牌方块文字、立项指南参数表。
/// 后台口令哈希、界面主题、托盘开关、数据源地址、常用收藏都不在这里，
/// 也永远不会通过这条路下发（否则服务端一次改动就能把全院各机的登录口令覆盖掉）。</para>
/// </summary>
public sealed class PublicSettingsSnapshot
{
    public string AppName { get; set; }
    public string AppSubName { get; set; }
    public string LogoText { get; set; }
    public List<string> LixiangCategories { get; set; }
    public List<string> LixiangUnits { get; set; }
}

/// <summary>一次「同步公共设置」的结果，供界面直接显示。</summary>
public sealed class PublicSettingsResult
{
    public bool Ok { get; set; }

    /// <summary>本次是否真的改动了本机设置</summary>
    public bool Changed { get; set; }

    public string Message { get; set; }
    public string Server { get; set; }
    public string At { get; set; }

    /// <summary>被改动的字段中文名（软件名称 / 单位副名 / 项目类别参数 / 计价单位参数）</summary>
    public List<string> Applied { get; set; } = new();
}

/// <summary>
/// 从局域网数据源拉取「公共设置」并落到本机 <c>data/settings.json</c>。
///
/// <para>设计原则：<b>服务端有值才下发</b>。服务端某个字段为空表示"管理员没在这台服务上配"，
/// 此时保留本机自己的设置不动 —— 这样「本机没配就自动取现有数据」的语义在客户端也成立，
/// 服务端也不会因为漏填一项就把全院客户端的配置清空。</para>
///
/// <para>只有一处例外需要知道：如果管理员在服务端把副名从"A"改成"B"，所有开启跟随的客户端
/// 下次启动就会跟着变成"B"；但把服务端副名清空并不会让客户端跟着清空（需要手工在客户端改）。</para>
/// </summary>
public static class PublicSettingsClient
{
    /// <summary>拉取服务端公共设置（只读，不落盘）。</summary>
    public static async Task<PublicSettingsSnapshot> FetchAsync(string baseUrl, CancellationToken ct = default)
    {
        var root = DataUpdateClient.Normalize(baseUrl);
        if (root.Length == 0) throw new InvalidOperationException("请先填写数据源地址。");

        using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        h.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Dip3Grouper-Desktop");
        var text = await h.GetStringAsync(DataUpdateClient.WithToken(root + "/api/settings/public"), ct);
        var snap = Js.FromJson<PublicSettingsSnapshot>(text);
        if (snap == null) throw new InvalidOperationException("服务器返回的设置格式不正确。");
        return snap;
    }

    /// <summary>拉取 + 合并进本机设置。网络异常不抛出，翻译成人话放进 <see cref="PublicSettingsResult.Message"/>。</summary>
    public static async Task<PublicSettingsResult> SyncAsync(string baseUrl, CancellationToken ct = default)
    {
        var res = new PublicSettingsResult { Server = DataUpdateClient.Normalize(baseUrl), At = Js.Iso() };
        if (res.Server.Length == 0)
        {
            res.Message = "请先填写数据源地址。";
            return res;
        }

        try
        {
            var snap = await FetchAsync(baseUrl, ct);
            res.Applied = Apply(snap);
            res.Changed = res.Applied.Count > 0;
            res.Ok = true;
            if (res.Changed)
            {
                SettingsStore.Current.LastSettingsAt = res.At;
                SettingsStore.Save();
                res.Message = "已应用服务端设置：" + string.Join("、", res.Applied);
            }
            else
            {
                res.Message = "本机设置已与服务端一致。";
            }
        }
        catch (Exception e)
        {
            res.Message = DataUpdateClient.Friendly(e);
        }
        return res;
    }

    /// <summary>
    /// 把快照合并进 <see cref="SettingsStore.Current"/>（**不落盘**，由调用方决定何时 Save）。
    /// 返回被改动的字段中文名；空列表 = 没有任何变化。
    /// </summary>
    public static List<string> Apply(PublicSettingsSnapshot s)
    {
        var labels = new List<string>();
        if (s == null) return labels;
        var cur = SettingsStore.Current;

        var name = (s.AppName ?? "").Trim();
        if (name.Length > 0 && !string.Equals(cur.AppName ?? "", name, StringComparison.Ordinal))
        {
            cur.AppName = name;
            labels.Add("软件名称");
        }

        var sub = (s.AppSubName ?? "").Trim();
        if (sub.Length > 0 && !string.Equals(cur.AppSubName ?? "", sub, StringComparison.Ordinal))
        {
            cur.AppSubName = sub;
            labels.Add("单位副名");
        }

        var cats = SettingsStore.CleanList(s.LixiangCategories);
        if (cats is { Count: > 0 } && !SameList(cur.LixiangCategories, cats))
        {
            cur.LixiangCategories = cats;
            labels.Add("项目类别参数");
        }

        var units = SettingsStore.CleanList(s.LixiangUnits);
        if (units is { Count: > 0 } && !SameList(cur.LixiangUnits, units))
        {
            cur.LixiangUnits = units;
            labels.Add("计价单位参数");
        }

        return labels;
    }

    /// <summary>最近一次同步时间的中文描述（未同步过返回空串）</summary>
    public static string LastSyncText()
    {
        var at = SettingsStore.Current.LastSettingsAt;
        if (string.IsNullOrEmpty(at)) return "";
        return DateTimeOffset.TryParse(at, out var t) ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : at;
    }

    private static bool SameList(List<string> a, List<string> b)
        => a != null && a.Count == b.Count && a.SequenceEqual(b, StringComparer.Ordinal);
}
