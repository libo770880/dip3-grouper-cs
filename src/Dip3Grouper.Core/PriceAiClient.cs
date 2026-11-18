using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Dip3Grouper;

/// <summary>发给模型的一条消息（OpenAI Chat Completions 协议）。</summary>
public sealed class PriceAiMessage
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
}

/// <summary>一次模型调用的结果。网络/协议异常一律翻译成可显示的人话，不往外抛。</summary>
public sealed class PriceAiReply
{
    public bool Ok { get; set; }
    public string Content { get; set; } = "";
    public string Message { get; set; } = "";
    public string Model { get; set; } = "";
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public long ElapsedMs { get; set; }
}

/// <summary>
/// OpenAI 兼容的 Chat Completions 客户端（非流式）。
///
/// <para>只认一套协议：<c>POST {BaseUrl}/chat/completions</c> + <c>Authorization: Bearer</c>，
/// 因此 DeepSeek / 百炼 / 混元 / 智谱 / Kimi / 院内 Ollama 都能接，用户自己填地址和密钥。</para>
///
/// <para><b>隐私</b>：请求只带用户当轮输入的问题与本机检索出的政策条文，不上传任何病历、
/// 不上传统计数据；密钥只出现在 HTTP 头，绝不写日志、绝不进异常文案。</para>
/// </summary>
public static class PriceAiClient
{
    /// <summary>请求体构造（纯函数，自检可直接断言字段）。密钥不在这里。</summary>
    public static string BuildRequestBody(PriceAiSettings cfg, List<PriceAiMessage> messages, int? maxTokens = null)
    {
        cfg = PriceAiSettingsStore.Normalize(cfg);
        var payload = new Dictionary<string, object>
        {
            ["model"] = cfg.Model,
            ["messages"] = (messages ?? new List<PriceAiMessage>())
                .Select(m => new Dictionary<string, object> { ["role"] = m.Role, ["content"] = m.Content })
                .ToList(),
            ["temperature"] = cfg.Temperature,
            ["max_tokens"] = maxTokens ?? cfg.MaxTokens,
            ["stream"] = false,
        };
        return JsonSerializer.Serialize(payload, Js.Http);
    }

    /// <summary>把响应正文里可能回显的密钥抹掉（异常信息也要过这一道）。</summary>
    public static string Redact(string text, PriceAiSettings cfg)
    {
        var t = text ?? "";
        var key = (cfg?.ApiKey ?? "").Trim();
        if (key.Length >= 6) t = t.Replace(key, "***API-KEY***", StringComparison.OrdinalIgnoreCase);
        return t;
    }

    /// <summary>
    /// 解析响应（纯函数）。兼容 <c>content</c> 为 null 但带 <c>reasoning_content</c> 的思考型模型。
    /// 非 2xx 时按状态码给出可操作的中文提示。
    /// </summary>
    public static PriceAiReply ParseResponse(string json, int statusCode, PriceAiSettings cfg = null)
    {
        var reply = new PriceAiReply();
        var safe = Redact(json ?? "", cfg);

        if (statusCode < 200 || statusCode >= 300)
        {
            var detail = TryError(safe);
            reply.Message = statusCode switch
            {
                401 or 403 => "API Key 无效或没有该模型权限（HTTP " + statusCode + "）。请到「设置」核对密钥与账号权限。",
                404 => "接口地址不对（HTTP 404）。请填到 /v1 这一层，程序会自动补 /chat/completions；"
                     + "百炼要带 /compatible-mode/v1、智谱是 /api/paas/v4。",
                429 => "请求被限流或额度不足（HTTP 429）。稍后再试，或检查服务商余额。",
                >= 500 => "模型服务端暂时不可用（HTTP " + statusCode + "）。",
                _ => "模型接口返回 HTTP " + statusCode + "。",
            };
            if (detail.Length > 0) reply.Message += " 服务商：" + detail;
            return reply;
        }

        try
        {
            using var doc = JsonDocument.Parse(json ?? "");
            var root = doc.RootElement;
            if (root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
                reply.Model = m.GetString() ?? "";

            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var pv)) reply.PromptTokens = pv;
                if (usage.TryGetProperty("completion_tokens", out var ct) && ct.TryGetInt32(out var cv)) reply.CompletionTokens = cv;
            }

            var content = "";
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                var msg = choices[0].GetProperty("message");
                if (msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    content = c.GetString() ?? "";
                if (content.Length == 0 && msg.TryGetProperty("reasoning_content", out var rc)
                    && rc.ValueKind == JsonValueKind.String) content = rc.GetString() ?? "";
            }

            if (content.Length == 0)
            {
                reply.Message = "模型返回了空内容（可能是模型名不对、内容被过滤或上下文超限）。";
                return reply;
            }
            reply.Ok = true;
            reply.Content = content.Trim();
            return reply;
        }
        catch (Exception e)
        {
            reply.Message = "模型应答无法解析：" + e.Message + "。原始片段：" + safe[..Math.Min(180, safe.Length)];
            return reply;
        }
    }

    private static string TryError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.String) return Trim(err.GetString());
                if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m)
                    && m.ValueKind == JsonValueKind.String) return Trim(m.GetString());
            }
            if (root.TryGetProperty("message", out var m2) && m2.ValueKind == JsonValueKind.String)
                return Trim(m2.GetString());
        }
        catch { /* 非 JSON 错误页，返回空即可 */ }
        return "";
    }

    private static string Trim(string s)
    {
        var v = (s ?? "").Trim();
        return v.Length <= 200 ? v : v[..200] + "…";
    }

    /// <summary>发起一次对话。任何失败都返回 <c>Ok=false</c> 的中文说明，不抛异常、不带密钥。</summary>
    public static async Task<PriceAiReply> ChatAsync(PriceAiSettings cfg, List<PriceAiMessage> messages,
                                                     CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            cfg = PriceAiSettingsStore.Normalize(cfg);
            var check = PriceAiSettingsStore.Validate(cfg);
            if (!check.Ok) return new PriceAiReply { Message = check.Message, ElapsedMs = sw.ElapsedMilliseconds };

            var endpoint = PriceAiSettingsStore.Endpoint(cfg.BaseUrl);
            var body = BuildRequestBody(cfg, messages);

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(cfg.TimeoutSec) };
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            if (cfg.ApiKey.Length > 0)
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + cfg.ApiKey);

            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var reply = ParseResponse(text, (int)resp.StatusCode, cfg);
            reply.ElapsedMs = sw.ElapsedMilliseconds;
            return reply;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new PriceAiReply { Message = "已取消本次解读。", ElapsedMs = sw.ElapsedMilliseconds };
        }
        catch (TaskCanceledException)
        {
            return new PriceAiReply
            {
                Message = $"请求超时（{cfg?.TimeoutSec ?? 90} 秒）。可在「设置」里调大超时，或换一个小一点的模型。",
                ElapsedMs = sw.ElapsedMilliseconds,
            };
        }
        catch (HttpRequestException e)
        {
            return new PriceAiReply
            {
                Message = "连不上模型接口：" + Redact(e.Message, cfg)
                        + $"。请检查地址 {PriceAiSettingsStore.Endpoint(cfg?.BaseUrl)}、网络与院内代理。",
                ElapsedMs = sw.ElapsedMilliseconds,
            };
        }
        catch (Exception e)
        {
            return new PriceAiReply { Message = "调用失败：" + Redact(e.Message, cfg), ElapsedMs = sw.ElapsedMilliseconds };
        }
    }

    /// <summary>
    /// 连通性自检：发一条极短的固定问句，确认地址 / 密钥 / 模型名三者都对。
    /// 不走立项指南检索，也不写任何业务数据。
    /// </summary>
    public static async Task<PriceAiReply> TestAsync(PriceAiSettings cfg, CancellationToken ct = default)
    {
        var messages = new List<PriceAiMessage>
        {
            new() { Role = "system", Content = "你是一个连通性测试助手，只按用户要求回复两个字。" },
            new() { Role = "user", Content = "只回复两个字：正常" },
        };
        // 复用同一个发送通道，但单独构造请求体以压到 32 token
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            cfg = PriceAiSettingsStore.Normalize(cfg);
            var check = PriceAiSettingsStore.Validate(cfg);
            if (!check.Ok) return new PriceAiReply { Message = check.Message };

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Min(cfg.TimeoutSec, 30)) };
            using var req = new HttpRequestMessage(HttpMethod.Post, PriceAiSettingsStore.Endpoint(cfg.BaseUrl));
            req.Content = new StringContent(BuildRequestBody(cfg, messages, 32), Encoding.UTF8, "application/json");
            if (cfg.ApiKey.Length > 0)
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + cfg.ApiKey);
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var r = ParseResponse(text, (int)resp.StatusCode, cfg);
            r.ElapsedMs = sw.ElapsedMilliseconds;
            if (r.Ok) r.Message = $"连接正常 · 模型 {r.Model} · {r.ElapsedMs} ms · 回复「{Trim(r.Content)}」";
            return r;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new PriceAiReply { Message = "已取消测试。" };
        }
        catch (Exception e)
        {
            return new PriceAiReply { Message = "测试失败：" + Redact(e.Message, cfg), ElapsedMs = sw.ElapsedMilliseconds };
        }
    }
}
