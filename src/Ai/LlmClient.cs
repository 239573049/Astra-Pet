using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AstraPet.Core;

namespace AstraPet.Ai;

/// <summary>一条对话消息。Role 只有 user 和 assistant，系统提示词单独传。</summary>
public sealed record ChatMessage(string Role, string Content);

/// <summary>一次请求的参数。</summary>
public sealed record ChatRequest(
    string? System,
    IReadOnlyList<ChatMessage> Messages,
    double Temperature,
    int MaxTokens);

public sealed class LlmException(string message) : Exception(message);

/// <summary>
/// 三种协议的非流式调用：OpenAI Chat Completions、OpenAI Responses、Anthropic Messages。
/// 只拼 JSON 发 HTTP，不依赖任何 SDK，这样兼容接口（DeepSeek、通义、Ollama 等）填个地址就能用。
/// </summary>
public static class LlmClient
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static async Task<string> CompleteAsync(LlmProvider provider, ChatRequest request, TimeSpan timeout,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provider.BaseUrl)) throw new LlmException("没有填写接口地址");
        if (string.IsNullOrWhiteSpace(provider.Model)) throw new LlmException("没有填写模型名");

        using var message = provider.Protocol switch
        {
            LlmProtocol.Responses => BuildResponses(provider, request),
            LlmProtocol.Anthropic => BuildAnthropic(provider, request),
            _ => BuildChatCompletions(provider, request),
        };

        var body = await SendAsync(message, timeout, ct);
        JsonNode? json;
        try { json = JsonNode.Parse(body); }
        catch (JsonException) { throw new LlmException($"返回的不是 JSON: {Truncate(body)}"); }

        var text = provider.Protocol switch
        {
            LlmProtocol.Responses => ParseResponses(json),
            LlmProtocol.Anthropic => ParseAnthropic(json),
            _ => ParseChatCompletions(json),
        };
        if (string.IsNullOrWhiteSpace(text))
            throw new LlmException($"模型没有返回文本: {Truncate(body)}");
        return text.Trim();
    }

    /// <summary>发出请求、统一处理超时、连接失败和非 2xx，返回响应体文本。</summary>
    private static async Task<string> SendAsync(HttpRequestMessage message, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(message, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmException($"请求超时（{timeout.TotalSeconds:0} 秒）");
        }
        catch (HttpRequestException e)
        {
            throw new LlmException($"连接失败: {e.Message}");
        }

        using (response)
        {
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new LlmException($"请求超时（{timeout.TotalSeconds:0} 秒）");
            }
            if (!response.IsSuccessStatusCode)
                throw new LlmException($"HTTP {(int)response.StatusCode}: {ErrorMessage(body)}");
            return body;
        }
    }

    /// <summary>
    /// 向接口要模型列表：OpenAI 系是 GET {地址}/models，Anthropic 是 GET {地址}/v1/models，
    /// 都返回 {"data":[{"id":...}]}。Gemini 的兼容接口会带 "models/" 前缀，这里去掉。
    /// 不是每个接口都开放这个端点，失败时抛 <see cref="LlmException"/>，由界面提示手动填写。
    /// </summary>
    public static async Task<IReadOnlyList<string>> ListModelsAsync(LlmProvider provider, TimeSpan timeout,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provider.BaseUrl)) throw new LlmException("没有填写接口地址");

        HttpRequestMessage request;
        if (provider.Protocol == LlmProtocol.Anthropic)
        {
            request = new HttpRequestMessage(HttpMethod.Get, AnthropicUrl(provider.BaseUrl, "models") + "?limit=1000");
            request.Headers.Add("x-api-key", provider.ApiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
        }
        else
        {
            request = new HttpRequestMessage(HttpMethod.Get, Join(provider.BaseUrl, "models"));
            Bearer(request, provider.ApiKey);
        }

        using (request)
        {
            var body = await SendAsync(request, timeout, ct);
            JsonNode? json;
            try { json = JsonNode.Parse(body); }
            catch (JsonException) { throw new LlmException($"返回的不是 JSON: {Truncate(body)}"); }

            // 绝大多数是 {"data":[{"id":..}]}；少数（Ollama 原生等）是 {"models":[{"name":..}]}。
            var items = json?["data"]?.AsArray() ?? json?["models"]?.AsArray()
                ?? (json as JsonArray) ?? throw new LlmException($"没有识别到模型列表: {Truncate(body)}");
            var ids = new List<string>();
            foreach (var item in items)
            {
                var id = item is JsonValue v && v.TryGetValue<string>(out var s) ? s
                    : item?["id"]?.GetValue<string>() ?? item?["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id)) continue;
                ids.Add(id.StartsWith("models/", StringComparison.Ordinal) ? id["models/".Length..] : id);
            }
            if (ids.Count == 0) throw new LlmException("接口返回了空的模型列表");
            return ids.Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>发一条「Say 1」，返回模型回复和耗时。用于设置页的测试按钮。</summary>
    public static async Task<(string Reply, TimeSpan Elapsed)> TestAsync(LlmProvider provider, int maxTokens,
        TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        var reply = await CompleteAsync(provider,
            new ChatRequest(null, [new ChatMessage("user", "Say 1")], 0, maxTokens), timeout);
        return (reply, watch.Elapsed);
    }

    // ───── OpenAI Chat Completions ─────

    private static HttpRequestMessage BuildChatCompletions(LlmProvider p, ChatRequest r)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(r.System))
            messages.Add((JsonNode)new JsonObject { ["role"] = "system", ["content"] = r.System });
        foreach (var m in r.Messages)
            messages.Add((JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content });

        var info = ModelCatalog.Current.Find(p);
        var body = new JsonObject
        {
            ["model"] = p.Model,
            ["messages"] = messages,
            ["max_tokens"] = MaxTokens(r.MaxTokens, info),
        };
        Temperature(body, r.Temperature, info);
        var request = Post(Join(p.BaseUrl, "chat/completions"), body);
        Bearer(request, p.ApiKey);
        return request;
    }

    private static string? ParseChatCompletions(JsonNode? json) =>
        json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();

    // ───── OpenAI Responses ─────

    private static HttpRequestMessage BuildResponses(LlmProvider p, ChatRequest r)
    {
        var input = new JsonArray();
        foreach (var m in r.Messages)
            input.Add((JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content });

        var info = ModelCatalog.Current.Find(p);
        var body = new JsonObject
        {
            ["model"] = p.Model,
            ["input"] = input,
            ["max_output_tokens"] = Math.Max(16, MaxTokens(r.MaxTokens, info)),
            ["store"] = false,
        };
        Temperature(body, r.Temperature, info);
        if (!string.IsNullOrWhiteSpace(r.System))
            body["instructions"] = r.System;
        var request = Post(Join(p.BaseUrl, "responses"), body);
        Bearer(request, p.ApiKey);
        return request;
    }

    /// <summary>
    /// 原始 JSON 里没有 SDK 的 output_text 便捷字段，要从 output[] 里找 type=message 的项，
    /// 再拼它 content[] 里 type=output_text 的文本。
    /// </summary>
    private static string? ParseResponses(JsonNode? json)
    {
        if (json?["output_text"] is JsonValue direct) return direct.GetValue<string>();
        var sb = new StringBuilder();
        foreach (var item in json?["output"]?.AsArray() ?? [])
        {
            if (item?["type"]?.GetValue<string>() != "message") continue;
            foreach (var part in item["content"]?.AsArray() ?? [])
                if (part?["type"]?.GetValue<string>() == "output_text")
                    sb.Append(part["text"]?.GetValue<string>());
        }
        return sb.ToString();
    }

    // ───── Anthropic Messages ─────

    private static HttpRequestMessage BuildAnthropic(LlmProvider p, ChatRequest r)
    {
        var messages = new JsonArray();
        foreach (var m in r.Messages)
            messages.Add((JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content });

        var info = ModelCatalog.Current.Find(p);
        var body = new JsonObject
        {
            ["model"] = p.Model,
            ["messages"] = messages,
            ["max_tokens"] = MaxTokens(r.MaxTokens, info),
        };
        Temperature(body, Math.Min(1, r.Temperature), info);   // Anthropic 的 temperature 上限是 1
        if (!string.IsNullOrWhiteSpace(r.System))
            body["system"] = r.System;

        var request = Post(AnthropicUrl(p.BaseUrl, "messages"), body);
        request.Headers.Add("x-api-key", p.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        return request;
    }

    /// <summary>地址可以填 https://api.anthropic.com 或带 /v1 的形式。</summary>
    private static string AnthropicUrl(string baseUrl, string resource)
    {
        baseUrl = baseUrl.TrimEnd('/');
        return baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? $"{baseUrl}/{resource}"
            : $"{baseUrl}/v1/{resource}";
    }

    private static string? ParseAnthropic(JsonNode? json)
    {
        var sb = new StringBuilder();
        foreach (var part in json?["content"]?.AsArray() ?? [])
            if (part?["type"]?.GetValue<string>() == "text")
                sb.Append(part["text"]?.GetValue<string>());
        return sb.ToString();
    }

    // ───── 公共 ─────

    /// <summary>
    /// 模型库（models.dev）里写明不接受 temperature 的模型（推理模型常见）就不发这个字段，
    /// 发了会被接口直接拒绝；库里没有这个模型时照常发。
    /// </summary>
    private static void Temperature(JsonObject body, double value, ModelInfo? info)
    {
        if (info?.Temperature != false) body["temperature"] = value;
    }

    /// <summary>max_tokens 超过模型的最大输出时接口会报错，按模型库里的上限截断。</summary>
    private static int MaxTokens(int requested, ModelInfo? info) =>
        info is { MaxOutput: > 0 } ? Math.Min(requested, info.MaxOutput) : requested;

    private static HttpRequestMessage Post(string url, JsonNode body) => new(HttpMethod.Post, url)
    {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
    };

    private static void Bearer(HttpRequestMessage request, string apiKey)
    {
        // 本地模型（Ollama 等）可以不填 Key。
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
    }

    private static string Join(string baseUrl, string path) => baseUrl.TrimEnd('/') + "/" + path;

    /// <summary>三家的错误体都是 {"error": {"message": ...}} 或 {"error": "..."}，取不到就截断原文。</summary>
    private static string ErrorMessage(string body)
    {
        try
        {
            // 对非对象节点用索引器会抛异常，所以先确认类型：根可能是数组或字符串，error 可能是对象也可能是字符串。
            var error = (JsonNode.Parse(body) as JsonObject)?["error"];
            if (error is JsonObject { } obj && obj["message"] is JsonValue message && message.TryGetValue<string>(out var m))
                return m;
            if (error is JsonValue text && text.TryGetValue<string>(out var s)) return s;
        }
        catch (JsonException) { }
        return Truncate(body);
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
