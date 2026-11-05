using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AstraPet.Core;

namespace AstraPet.Ai;

/// <summary>
/// 三种协议的非流式调用：OpenAI Chat Completions、OpenAI Responses、Anthropic Messages。
/// 只拼 JSON 发 HTTP，不依赖任何 SDK，这样兼容接口（DeepSeek、通义、Ollama 等）填个地址就能用。
/// </summary>
public sealed class LlmClient : ILlmClient
{
    /// <summary>对话智能体用的实例（实现 <see cref="ILlmClient"/>）；设置页的测试、拉模型列表直接用静态方法。</summary>
    public static readonly LlmClient Instance = new();

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private LlmClient() { }

    Task<ChatResponse> ILlmClient.CompleteAsync(LlmProvider provider, ChatRequest request, TimeSpan timeout, CancellationToken ct) =>
        CompleteAsync(provider, request, timeout, ct);

    public static async Task<ChatResponse> CompleteAsync(LlmProvider provider, ChatRequest request, TimeSpan timeout,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provider.BaseUrl)) throw new LlmException("没有填写接口地址");
        if (string.IsNullOrWhiteSpace(provider.Model)) throw new LlmException("没有填写模型名");

        using var message = Build(provider, request);
        var body = await SendAsync(message, timeout, ct);
        JsonNode? json;
        try { json = JsonNode.Parse(body); }
        catch (JsonException) { throw new LlmException($"返回的不是 JSON: {Truncate(body)}"); }

        var response = Parse(provider.Protocol, json);
        if (response.Text.Length == 0 && response.ToolCalls.Count == 0)
            throw new LlmException($"模型没有返回文本: {Truncate(body)}");
        return response;
    }

    internal static HttpRequestMessage Build(LlmProvider provider, ChatRequest request) => provider.Protocol switch
    {
        LlmProtocol.Responses => BuildResponses(provider, request),
        LlmProtocol.Anthropic => BuildAnthropic(provider, request),
        _ => BuildChatCompletions(provider, request),
    };

    /// <summary>请求体（测试用）。</summary>
    internal static JsonObject Body(LlmProvider provider, ChatRequest request) => provider.Protocol switch
    {
        LlmProtocol.Responses => ResponsesBody(provider, request),
        LlmProtocol.Anthropic => AnthropicBody(provider, request),
        _ => ChatCompletionsBody(provider, request),
    };

    internal static ChatResponse Parse(LlmProtocol protocol, JsonNode? json) => protocol switch
    {
        LlmProtocol.Responses => ParseResponses(json),
        LlmProtocol.Anthropic => ParseAnthropic(json),
        _ => ParseChatCompletions(json),
    };

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
                throw new LlmException($"HTTP {(int)response.StatusCode}: {ErrorMessage(body)}", (int)response.StatusCode);
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
            new ChatRequest(null, [ChatMessage.User("Say 1")], 0, maxTokens), timeout);
        return (reply.Text, watch.Elapsed);
    }

    /// <summary>
    /// 测一次带工具的请求（03 §1.5）：接口接受工具定义就算支持；<c>Called</c> 表示模型这次真的调用了。
    /// 被以「不支持 tools」的理由拒绝时返回 Supported = false；其他错误照常抛。
    /// </summary>
    public static async Task<(bool Supported, bool Called)> TestToolsAsync(LlmProvider provider, int maxTokens, TimeSpan timeout)
    {
        var ping = new ToolDefinition("ping", "测试用的工具，被要求时调用它。",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() });
        try
        {
            var reply = await CompleteAsync(provider,
                new ChatRequest(null, [ChatMessage.User("请调用 ping 工具，不用说话。")], 0, maxTokens, [ping]), timeout);
            return (true, reply.ToolCalls.Any(c => c.Name == "ping"));
        }
        catch (LlmException e) when (e.LooksLikeToolsUnsupported)
        {
            return (false, false);
        }
    }

    // ───── OpenAI Chat Completions ─────

    private static HttpRequestMessage BuildChatCompletions(LlmProvider p, ChatRequest r)
    {
        var request = Post(Join(p.BaseUrl, "chat/completions"), ChatCompletionsBody(p, r));
        Bearer(request, p.ApiKey);
        return request;
    }

    /// <summary>
    /// 工具结果拆成单独的 role: tool 消息；assistant 的调用放 tool_calls。
    /// 不发 parallel_tool_calls：不少兼容网关不认这个字段，多个调用由 PetAgent 只执行第一个。
    /// </summary>
    private static JsonObject ChatCompletionsBody(LlmProvider p, ChatRequest r)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(r.System))
            messages.Add((JsonNode)new JsonObject { ["role"] = "system", ["content"] = r.System });
        foreach (var m in r.Messages)
        {
            if (m.Role == "assistant")
            {
                var msg = new JsonObject { ["role"] = "assistant", ["content"] = m.Text.Length > 0 ? m.Text : null };
                var calls = new JsonArray();
                foreach (var c in m.ToolCalls)
                    calls.Add((JsonNode)new JsonObject
                    {
                        ["id"] = c.CallId,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.ArgumentsJson },
                    });
                if (calls.Count > 0) msg["tool_calls"] = calls;
                messages.Add((JsonNode)msg);
                continue;
            }
            foreach (var result in m.ToolResults)
                messages.Add((JsonNode)new JsonObject { ["role"] = "tool", ["tool_call_id"] = result.CallId, ["content"] = result.Content });
            if (m.Text.Length > 0)
                messages.Add((JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Text });
        }

        var info = ModelCatalog.Current.Find(p);
        var body = new JsonObject
        {
            ["model"] = p.Model,
            ["messages"] = messages,
            ["max_tokens"] = MaxTokens(r.MaxTokens, info),
        };
        Temperature(body, r.Temperature, info);
        if (r.HasTools)
        {
            var tools = new JsonArray();
            foreach (var t in r.Tools!)
                tools.Add((JsonNode)new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Parameters.DeepClone(),
                    },
                });
            body["tools"] = tools;
            body["tool_choice"] = "auto";
        }
        return body;
    }

    private static ChatResponse ParseChatCompletions(JsonNode? json)
    {
        var choice = json?["choices"]?[0];
        var message = choice?["message"];
        var parts = new List<ChatPart>();
        if (Text(message?["content"]) is { Length: > 0 } text) parts.Add(new TextPart(text));
        var index = 0;
        foreach (var call in message?["tool_calls"] as JsonArray ?? [])
        {
            index++;
            var fn = call?["function"];
            if (Str(fn?["name"]) is not { Length: > 0 } name) continue;
            parts.Add(new ToolCallPart(Str(call?["id"]) is { Length: > 0 } id ? id : $"call_{index}", name, Arguments(fn?["arguments"])));
        }
        return new ChatResponse(parts, Str(choice?["finish_reason"]));
    }

    // ───── OpenAI Responses ─────

    private static HttpRequestMessage BuildResponses(LlmProvider p, ChatRequest r)
    {
        var request = Post(Join(p.BaseUrl, "responses"), ResponsesBody(p, r));
        Bearer(request, p.ApiKey);
        return request;
    }

    /// <summary>工具调用和结果是 input 里单独的 function_call / function_call_output 项。</summary>
    private static JsonObject ResponsesBody(LlmProvider p, ChatRequest r)
    {
        var input = new JsonArray();
        foreach (var m in r.Messages)
        {
            foreach (var result in m.ToolResults)
                input.Add((JsonNode)new JsonObject { ["type"] = "function_call_output", ["call_id"] = result.CallId, ["output"] = result.Content });
            if (m.Text.Length > 0)
                input.Add((JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Text });
            foreach (var c in m.ToolCalls)
                input.Add((JsonNode)new JsonObject
                {
                    ["type"] = "function_call", ["call_id"] = c.CallId, ["name"] = c.Name, ["arguments"] = c.ArgumentsJson,
                });
        }

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
        if (r.HasTools)
        {
            var tools = new JsonArray();
            foreach (var t in r.Tools!)
                tools.Add((JsonNode)new JsonObject
                {
                    ["type"] = "function", ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Parameters.DeepClone(),
                });
            body["tools"] = tools;
            body["tool_choice"] = "auto";
            body["parallel_tool_calls"] = false;
        }
        return body;
    }

    /// <summary>
    /// 原始 JSON 里没有 SDK 的 output_text 便捷字段（有的网关会给），要从 output[] 里找 type=message 的项，
    /// 再拼它 content[] 里 type=output_text 的文本；type=function_call 的项是工具调用。
    /// </summary>
    private static ChatResponse ParseResponses(JsonNode? json)
    {
        var parts = new List<ChatPart>();
        var sb = new StringBuilder();
        foreach (var item in json?["output"] as JsonArray ?? [])
        {
            switch (Str(item?["type"]))
            {
                case "message":
                    foreach (var part in item!["content"] as JsonArray ?? [])
                        if (Str(part?["type"]) == "output_text")
                            sb.Append(Str(part!["text"]));
                    break;
                case "function_call" when Str(item!["name"]) is { Length: > 0 } name:
                    parts.Add(new ToolCallPart(Str(item["call_id"]) ?? Str(item["id"]) ?? $"call_{parts.Count + 1}", name,
                        Arguments(item["arguments"])));
                    break;
            }
        }
        if (sb.Length == 0 && Str(json?["output_text"]) is { } direct) sb.Append(direct);
        if (sb.ToString().Trim() is { Length: > 0 } text) parts.Insert(0, new TextPart(text));
        return new ChatResponse(parts, Str(json?["status"]));
    }

    // ───── Anthropic Messages ─────

    private static HttpRequestMessage BuildAnthropic(LlmProvider p, ChatRequest r)
    {
        var request = Post(AnthropicUrl(p.BaseUrl, "messages"), AnthropicBody(p, r));
        request.Headers.Add("x-api-key", p.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        return request;
    }

    /// <summary>
    /// 内容块：text、tool_use（input 是对象）、tool_result（在 user 消息里，要排在文字前面）。
    /// 相邻的同角色消息合并成一条（上一轮结尾的工具结果和这一轮用户说的话）。纯文字的消息仍然发字符串。
    /// </summary>
    private static JsonObject AnthropicBody(LlmProvider p, ChatRequest r)
    {
        var merged = new List<(string Role, List<ChatPart> Parts)>();
        foreach (var m in r.Messages)
        {
            if (merged.Count > 0 && merged[^1].Role == m.Role) merged[^1].Parts.AddRange(m.Parts);
            else merged.Add((m.Role, m.Parts.ToList()));
        }

        var messages = new JsonArray();
        foreach (var (role, parts) in merged)
        {
            if (parts.All(x => x is TextPart))
            {
                messages.Add((JsonNode)new JsonObject { ["role"] = role, ["content"] = string.Concat(parts.OfType<TextPart>().Select(x => x.Text)) });
                continue;
            }
            var content = new JsonArray();
            foreach (var result in parts.OfType<ToolResultPart>())
            {
                var block = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = result.CallId, ["content"] = result.Content };
                if (result.IsError) block["is_error"] = true;
                content.Add((JsonNode)block);
            }
            foreach (var part in parts)
            {
                switch (part)
                {
                    case TextPart { Text.Length: > 0 } t:
                        content.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = t.Text });
                        break;
                    case ToolCallPart c:
                        content.Add((JsonNode)new JsonObject
                        {
                            ["type"] = "tool_use", ["id"] = c.CallId, ["name"] = c.Name, ["input"] = ArgumentsObject(c.ArgumentsJson),
                        });
                        break;
                }
            }
            messages.Add((JsonNode)new JsonObject { ["role"] = role, ["content"] = content });
        }

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
        if (r.HasTools)
        {
            var tools = new JsonArray();
            foreach (var t in r.Tools!)
                tools.Add((JsonNode)new JsonObject
                {
                    ["name"] = t.Name, ["description"] = t.Description, ["input_schema"] = t.Parameters.DeepClone(),
                });
            body["tools"] = tools;
            body["tool_choice"] = new JsonObject { ["type"] = "auto", ["disable_parallel_tool_use"] = true };
        }
        return body;
    }

    /// <summary>地址可以填 https://api.anthropic.com 或带 /v1 的形式。</summary>
    private static string AnthropicUrl(string baseUrl, string resource)
    {
        baseUrl = baseUrl.TrimEnd('/');
        return baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? $"{baseUrl}/{resource}"
            : $"{baseUrl}/v1/{resource}";
    }

    private static ChatResponse ParseAnthropic(JsonNode? json)
    {
        var parts = new List<ChatPart>();
        var sb = new StringBuilder();
        foreach (var block in json?["content"] as JsonArray ?? [])
        {
            switch (Str(block?["type"]))
            {
                case "text":
                    sb.Append(Str(block!["text"]));
                    break;
                case "tool_use" when Str(block!["name"]) is { Length: > 0 } name:
                    parts.Add(new ToolCallPart(Str(block["id"]) ?? $"toolu_{parts.Count + 1}", name, Arguments(block["input"])));
                    break;
            }
        }
        if (sb.ToString().Trim() is { Length: > 0 } text) parts.Insert(0, new TextPart(text));
        return new ChatResponse(parts, Str(json?["stop_reason"]));
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

    /// <summary>字符串节点的值；不是字符串（null、对象、数字）时返回 null，不抛异常。</summary>
    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>消息的 content：一般是字符串，少数网关给 [{type: text, text}] 数组。</summary>
    private static string? Text(JsonNode? node)
    {
        if (Str(node) is { } s) return s.Trim();
        if (node is not JsonArray array) return null;
        var sb = new StringBuilder();
        foreach (var part in array)
            if (Str(part?["text"]) is { } t) sb.Append(t);
        return sb.ToString().Trim();
    }

    /// <summary>工具参数：标准是 JSON 字符串，一些网关直接给对象，两种都接受，统一成 JSON 文本。</summary>
    private static string Arguments(JsonNode? node) => node switch
    {
        null => "{}",
        JsonValue v when v.TryGetValue<string>(out var s) => string.IsNullOrWhiteSpace(s) ? "{}" : s,
        _ => node.ToJsonString(),
    };

    /// <summary>Anthropic 的 tool_use.input 要对象：解析失败或不是对象时给空对象。</summary>
    private static JsonObject ArgumentsObject(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
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
