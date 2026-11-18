namespace Dip3Grouper;

/// <summary>
/// 「物价解读 AI」的本机私有配置（data/price-ai.json）。
///
/// <para><b>安全边界</b>：模型地址、密钥、地区都是<b>每台机器各自</b>的设置，
/// 必须留在 <see cref="DataCatalog.PrivateRelPaths"/> 里、绝不进 <c>Specs</c> 分发白名单；
/// 发布脚本也把它列进 KEEPFILES，重发布不会把用户填的密钥冲掉。</para>
///
/// <para>密钥为了能直接调用 API 只能以明文保存在本机文件里（不像后台口令可以存哈希），
/// 因此界面用 PasswordBox、状态条只显示遮盖值，日志与异常信息一律不带密钥。</para>
/// </summary>
public sealed class PriceAiSettings
{
    /// <summary>服务商预设 id（只影响界面下拉，调用一律按 BaseUrl/Model 走）</summary>
    public string Provider { get; set; } = "custom";

    /// <summary>OpenAI 兼容接口根地址，如 https://api.deepseek.com/v1 （不含 /chat/completions）</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>API Key（本机明文，绝不下发 / 绝不写日志）</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>模型名，如 deepseek-flash / qwen-plus（以服务商控制台实际名称为准）</summary>
    public string Model { get; set; } = "";

    /// <summary>解读适用的地区（影响提示词口径与是否注入本地落地价）</summary>
    public string Region { get; set; } = PriceAiSettingsStore.DefaultRegion;

    /// <summary>每轮最多引用几条立项指南条目</summary>
    public int TopK { get; set; } = 6;

    /// <summary>每轮最多引用几条解读辅导问答</summary>
    public int JieduK { get; set; } = 4;

    /// <summary>每轮最多引用几条地区落地价项目</summary>
    public int PriceK { get; set; } = 3;

    /// <summary>随请求带上的历史轮数（0 = 单轮问答）</summary>
    public int HistoryTurns { get; set; } = 4;

    /// <summary>采样温度：政策解读取低值，避免自由发挥</summary>
    public double Temperature { get; set; } = 0.1;

    /// <summary>单次回答最大 token 数</summary>
    public int MaxTokens { get; set; } = 1800;

    /// <summary>请求超时（秒）</summary>
    public int TimeoutSec { get; set; } = 90;

    /// <summary>最后保存时间（ISO）</summary>
    public string UpdatedAt { get; set; } = "";

    /// <summary>仅供自检 / 测试用：克隆一份，避免改到全局单例</summary>
    public PriceAiSettings Clone() => (PriceAiSettings)MemberwiseClone();
}

/// <summary>服务商预设（只提供常用值，最终以服务商文档为准，全部字段都可手改）。</summary>
public sealed class PriceAiProvider
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>界面上的一句话提示（密钥从哪拿、模型名要不要改）</summary>
    public string Hint { get; set; } = "";
}

/// <summary>配置的读写、归一化与校验。</summary>
public static class PriceAiSettingsStore
{
    public const string DefaultRegion = "山东省德州市";

    /// <summary>本机私有配置文件路径</summary>
    public static string FilePath => AppPaths.PriceAiFile;

    /// <summary>常用 OpenAI 兼容服务商。Model 只是建议值——控制台改了名字在这里改一下即可。</summary>
    public static readonly IReadOnlyList<PriceAiProvider> Providers = new[]
    {
        new PriceAiProvider { Id = "deepseek", Name = "DeepSeek", BaseUrl = "https://api.deepseek.com/v1",
            Model = "deepseek-flash", Hint = "密钥在 platform.deepseek.com 申请。旧名 deepseek-chat 已停用，以控制台模型名为准。" },
        new PriceAiProvider { Id = "dashscope", Name = "阿里云百炼（通义千问）", BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1",
            Model = "qwen-plus", Hint = "必须带 /compatible-mode/v1；模型名按百炼控制台填写。" },
        new PriceAiProvider { Id = "hunyuan", Name = "腾讯混元", BaseUrl = "https://api.hunyuan.cloud.tencent.com/v1",
            Model = "hunyuan-turbos-latest", Hint = "在腾讯云控制台开通混元大模型后创建 API Key。" },
        new PriceAiProvider { Id = "zhipu", Name = "智谱 GLM", BaseUrl = "https://open.bigmodel.cn/api/paas/v4",
            Model = "glm-4.6", Hint = "注意路径是 /api/paas/v4，不是 /v1。" },
        new PriceAiProvider { Id = "moonshot", Name = "月之暗面 Kimi", BaseUrl = "https://api.moonshot.cn/v1",
            Model = "moonshot-v1-8k", Hint = "模型名与上下文长度按控制台实际可用型号填写。" },
        new PriceAiProvider { Id = "ollama", Name = "本机 / 内网模型（Ollama 等）", BaseUrl = "http://127.0.0.1:11434/v1",
            Model = "qwen2.5:7b", Hint = "内网自建不用密钥可留空；数据不出院，适合病历相关咨询。地址可改成院内推理机。" },
        new PriceAiProvider { Id = "custom", Name = "自定义（OpenAI 兼容）", BaseUrl = "", Model = "",
            Hint = "填完整接口根地址，程序自动补 /chat/completions。也支持已含该路径的完整地址。" },
    };

    private static PriceAiSettings _cur;

    /// <summary>当前配置（首次访问自动载入并补齐默认值）</summary>
    public static PriceAiSettings Current => _cur ??= Load();

    /// <summary>按 id 找预设，找不到返回自定义</summary>
    public static PriceAiProvider FindProvider(string id)
    {
        foreach (var p in Providers)
            if (string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) return p;
        return Providers[^1];
    }

    /// <summary>
    /// 补齐默认值、裁掉越界值。**只做数值与空白归一，不改用户填的地址 / 密钥 / 模型**，
    /// 免得一个手滑的默认值把用户配好的内网模型换掉。
    /// </summary>
    public static PriceAiSettings Normalize(PriceAiSettings s)
    {
        s ??= new PriceAiSettings();
        s.Provider = string.IsNullOrWhiteSpace(s.Provider) ? "custom" : s.Provider.Trim();
        s.BaseUrl = (s.BaseUrl ?? "").Trim();
        s.ApiKey = (s.ApiKey ?? "").Trim();
        s.Model = (s.Model ?? "").Trim();
        s.Region = string.IsNullOrWhiteSpace(s.Region) ? DefaultRegion : s.Region.Trim();
        s.TopK = Math.Clamp(s.TopK, 1, 12);
        s.JieduK = Math.Clamp(s.JieduK, 0, 8);
        s.PriceK = Math.Clamp(s.PriceK, 0, 8);
        s.HistoryTurns = Math.Clamp(s.HistoryTurns, 0, 10);
        s.Temperature = double.IsNaN(s.Temperature) ? 0.1 : Math.Clamp(s.Temperature, 0, 1);
        s.MaxTokens = Math.Clamp(s.MaxTokens, 256, 8192);
        s.TimeoutSec = Math.Clamp(s.TimeoutSec, 10, 300);
        s.UpdatedAt ??= "";
        return s;
    }

    /// <summary>读配置；文件不存在 / 损坏时退回默认（不能因此让工具栏打不开）。</summary>
    /// <param name="path">可传临时路径供自检用；留空走 <see cref="FilePath"/>。</param>
    public static PriceAiSettings Load(string path = null)
    {
        PriceAiSettings raw = null;
        try { raw = Js.ReadJsonFile<PriceAiSettings>(path ?? FilePath); }
        catch (Exception e) { Console.Error.WriteLine($"[price-ai] 配置读取失败，使用默认值: {e.Message}"); }
        return Normalize(raw ?? new PriceAiSettings());
    }

    /// <summary>
    /// 原子写配置。传自定义路径时只落盘、不动内存单例（自检用）；
    /// 走默认路径时同步刷新 <see cref="Current"/>，界面改完立即生效。
    /// </summary>
    public static void Save(PriceAiSettings s, string path = null)
    {
        s = Normalize(s ?? new PriceAiSettings());
        s.UpdatedAt = Js.Iso();
        Js.WriteJsonFile(path ?? FilePath, s, Js.Config, 2);
        if (path == null) _cur = s;
    }

    /// <summary>仅供自检：清内存单例，强制下次重新读盘。</summary>
    public static void ResetCache() => _cur = null;

    /// <summary>密钥遮盖显示：只留前 3 后 2，中间固定 6 个点，绝不回显全文。</summary>
    public static string MaskKey(string key)
    {
        var k = (key ?? "").Trim();
        if (k.Length == 0) return "未填写";
        if (k.Length <= 6) return "••••••";
        return k[..3] + "••••••" + k[^2..];
    }

    /// <summary>是否可以发起请求（地址 + 模型必填；密钥允许内网自建留空）。</summary>
    public static bool IsReady(PriceAiSettings s)
        => s != null && s.BaseUrl.Length > 0 && s.Model.Length > 0;

    /// <summary>提交前校验，返回可直接显示给人看的错误文案。</summary>
    public static (bool Ok, string Message) Validate(PriceAiSettings s)
    {
        s = Normalize(s);
        if (s.BaseUrl.Length == 0) return (false, "请填写接口地址（Base URL）。");
        if (!Uri.TryCreate(s.BaseUrl, UriKind.Absolute, out var u) ||
            (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
            return (false, "接口地址必须是 http:// 或 https:// 开头的完整地址。");
        if (s.Model.Length == 0) return (false, "请填写模型名称。");
        if (s.Region.Length == 0) return (false, "请填写适用地区。");
        return (true, "");
    }

    /// <summary>
    /// 拼出 chat/completions 完整地址：容忍用户填根地址、带 /v1、或已经填了完整路径。
    /// 末尾的 / 统一裁掉，避免出现 //chat/completions。
    /// </summary>
    public static string Endpoint(string baseUrl)
    {
        var b = (baseUrl ?? "").Trim().TrimEnd('/');
        if (b.Length == 0) return "";
        if (b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return b;
        return b + "/chat/completions";
    }

    /// <summary>只给界面展示用：地址不含密钥，可安全写日志。</summary>
    public static string SafeDescribe(PriceAiSettings s)
    {
        s = Normalize(s);
        return $"{s.Region} · {s.Provider} · {s.Model} · {Endpoint(s.BaseUrl)} · 密钥 {MaskKey(s.ApiKey)}";
    }
}
