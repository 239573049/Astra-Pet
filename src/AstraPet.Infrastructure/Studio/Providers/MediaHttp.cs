using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AstraPet.Core.Studio;

namespace AstraPet.Infrastructure.Studio;

/// <summary>
/// 生成接口共用的 HTTP 细节：拼地址、鉴权头、合并 ExtraJson、把错误响应变成 <see cref="GenerationException"/>、
/// 给日志和任务记录用的脱敏（图片 base64 截断，05 §4）。密钥只出现在请求头里。
/// </summary>
internal static class MediaHttp
{
    public static readonly HttpClient Shared = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static string Url(MediaProvider provider, string path) => provider.BaseUrl.Trim().TrimEnd('/') + "/" + path.TrimStart('/');

    public static void Check(MediaProvider provider)
    {
        if (provider.BaseUrl.Trim().Length == 0) throw new GenerationException("没有填写接口地址");
        if (provider.ApiKey.Trim().Length == 0) throw new GenerationException("没有填写 API Key");
        if (provider.Model.Trim().Length == 0) throw new GenerationException("没有填写模型名");
    }

    public static HttpRequestMessage Request(HttpMethod method, MediaProvider provider, string path, HttpContent? content = null)
    {
        var message = new HttpRequestMessage(method, Url(provider, path)) { Content = content };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey.Trim());
        return message;
    }

    public static StringContent Json(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    /// <summary>把用户填的 ExtraJson（一个 JSON 对象）合并进请求体，同名字段覆盖。不是合法对象时报错，不悄悄忽略。</summary>
    public static void MergeExtra(JsonObject body, MediaProvider provider)
    {
        if (provider.ExtraJson.Trim().Length == 0) return;
        JsonNode? extra;
        try { extra = JsonNode.Parse(provider.ExtraJson); }
        catch (JsonException e) { throw new GenerationException($"「额外参数」不是合法的 JSON：{e.Message}"); }
        if (extra is not JsonObject obj) throw new GenerationException("「额外参数」必须是一个 JSON 对象 {…}");
        foreach (var (key, value) in obj) body[key] = value?.DeepClone();
    }

    public static async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpClient http, HttpRequestMessage message,
        TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var response = await http.SendAsync(message, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            return (response.StatusCode, body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GenerationException($"请求超时（{timeout.TotalSeconds:0} 秒）");
        }
        catch (HttpRequestException e)
        {
            throw new GenerationException($"连不上 {message.RequestUri?.Host}：{e.Message}");
        }
    }

    /// <summary>非 2xx 抛出，消息带服务端原文（error.message 优先）。</summary>
    public static JsonNode EnsureJson(HttpStatusCode status, string body)
    {
        var code = (int)status;
        if (code is < 200 or >= 300) throw new GenerationException($"HTTP {code}：{ErrorText(body)}", code);
        try
        {
            return JsonNode.Parse(body) ?? throw new GenerationException($"返回的不是 JSON：{Truncate(body)}", code);
        }
        catch (JsonException) { throw new GenerationException($"返回的不是 JSON：{Truncate(body)}", code); }
    }

    public static string ErrorText(string body)
    {
        try
        {
            var json = JsonNode.Parse(body);
            if (json?["error"] is JsonObject error)
            {
                var message = error["message"]?.GetValue<string>() ?? "";
                var code = error["code"]?.ToString() ?? "";
                return code.Length > 0 && message.Length > 0 ? $"{code}：{message}" : message.Length > 0 ? message : error.ToJsonString();
            }
            if (json?["error"] is JsonValue v) return v.ToString();
            if (json?["message"] is JsonValue m) return m.ToString();
        }
        catch (JsonException) { }
        return Truncate(body);
    }

    public static string Truncate(string text, int max = 500) => text.Length <= max ? text : text[..max] + "…";

    public static string DataUrl(byte[] image) => $"data:{MimeOf(image)};base64," + Convert.ToBase64String(image);

    public static string MimeOf(byte[] image) =>
        image is [0xFF, 0xD8, ..] ? "image/jpeg"
        : image is [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..] ? "image/webp"
        : "image/png";

    /// <summary>深拷贝请求体，把所有 data: URL 截成前 40 个字符（日志、任务记录用）。</summary>
    public static JsonNode Redacted(JsonNode body)
    {
        var copy = body.DeepClone();
        Walk(copy);
        return copy;

        static void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var key in obj.Select(p => p.Key).ToList())
                    {
                        if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith("data:", StringComparison.Ordinal))
                            obj[key] = s[..Math.Min(40, s.Length)] + "…(base64 省略)";
                        else Walk(obj[key]);
                    }
                    break;
                case JsonArray arr:
                    for (var i = 0; i < arr.Count; i++)
                    {
                        if (arr[i] is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith("data:", StringComparison.Ordinal))
                            arr[i] = s[..Math.Min(40, s.Length)] + "…(base64 省略)";
                        else Walk(arr[i]);
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// 测试连接的结论：401 / 403 是密钥不对；404、400、422 这种业务错误说明地址对、鉴权过了。
    /// </summary>
    public static string Verdict(HttpStatusCode status, string body, string okText)
    {
        var code = (int)status;
        return code switch
        {
            401 or 403 => throw new GenerationException($"密钥不对或没有权限（HTTP {code}）：{ErrorText(body)}", code),
            >= 200 and < 300 => okText,
            400 or 404 or 422 when LooksLikeApi(body) => okText,
            404 => throw new GenerationException("地址不对：服务端返回 404，检查一下接口地址（方舟是 …/api/v3，OpenAI 兼容接口一般以 /v1 结尾）", code),
            _ => throw new GenerationException($"HTTP {code}：{ErrorText(body)}", code),
        };

        static bool LooksLikeApi(string body)
        {
            try { return JsonNode.Parse(body) is JsonObject; }
            catch (JsonException) { return false; }
        }
    }
}
