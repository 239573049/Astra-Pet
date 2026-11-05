using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AstraPet.Core;

namespace AstraPet.Ai;

/// <summary>
/// 流式请求（SSE）：三种协议各自的事件格式，边收边把文字交给 <see cref="ChatRequest.OnText"/>，
/// 最后拼成和非流式一样的 <see cref="ChatResponse"/>（文字、工具调用、usage）。
/// 网关忽略了 stream、直接回 JSON 的，按普通响应解析。
/// </summary>
public sealed partial class LlmClient
{
    /// <summary>流式被拒过的接口（地址 + 模型），之后直接走普通请求。</summary>
    private static readonly ConcurrentDictionary<string, byte> NoStreamingSet = new();
    private static ICollection<string> NoStreaming => NoStreamingSet.Keys;
    private static string StreamKey(LlmProvider p) => $"{p.BaseUrl}|{p.Model}";

    private static async Task<ChatResponse> StreamAsync(LlmProvider provider, ChatRequest request, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var message = Build(provider, request);
        message.Headers.Accept.ParseAdd("text/event-stream");

        try
        {
            using var response = await Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cts.Token);
                throw new LlmException($"HTTP {(int)response.StatusCode}: {ErrorMessage(error)}", (int)response.StatusCode);
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!mediaType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
            {
                // 网关没理 stream，整段 JSON 回来了。
                var body = await response.Content.ReadAsStringAsync(cts.Token);
                JsonNode? json;
                try { json = JsonNode.Parse(body); }
                catch (JsonException) { throw new LlmException($"返回的不是 JSON: {Truncate(body)}"); }
                var whole = Parse(provider.Protocol, json);
                if (whole.Text.Length == 0 && whole.ToolCalls.Count == 0) throw new LlmException($"模型没有返回文本: {Truncate(body)}");
                request.OnText!(whole.Text);
                return whole;
            }

            var acc = new StreamAccumulator(provider.Protocol, request.OnText!);
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var data = new StringBuilder();
            while (await reader.ReadLineAsync(cts.Token) is { } line)
            {
                if (line.Length == 0)
                {
                    if (data.Length > 0 && acc.Feed(data.ToString())) break;
                    data.Clear();
                    continue;
                }
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0) data.Append('\n');
                    data.Append(line.AsSpan(5).TrimStart());
                }
            }
            if (data.Length > 0) acc.Feed(data.ToString());

            var result = acc.Result();
            if (result.Text.Length == 0 && result.ToolCalls.Count == 0) throw new LlmException("模型没有返回文本（流式）");
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmException($"请求超时（{timeout.TotalSeconds:0} 秒）");
        }
        catch (HttpRequestException e)
        {
            throw new LlmException($"连接失败: {e.Message}");
        }
        catch (IOException e)
        {
            throw new LlmException($"连接中断: {e.Message}");
        }
    }

    /// <summary>把 SSE 的 data 一条条喂进来，攒出文字、工具调用、usage。</summary>
    internal sealed class StreamAccumulator(LlmProtocol protocol, Action<string> onText)
    {
        private readonly StringBuilder _text = new();
        /// <summary>工具调用按序号攒：id、名字、参数 JSON 片段。</summary>
        private readonly SortedDictionary<int, (string Id, string Name, StringBuilder Args)> _calls = new();
        private string? _stop;
        private int? _inputTokens;
        private ChatResponse? _final;

        /// <summary>处理一条 data；返回 true 表示流结束了。</summary>
        public bool Feed(string data)
        {
            if (data == "[DONE]") return true;
            JsonNode? json;
            try { json = JsonNode.Parse(data); }
            catch (JsonException) { return false; }   // 心跳、注释之类
            if (json is not JsonObject o) return false;
            if (o["error"] is { } error)
                throw new LlmException(Str(error["message"]) ?? Str(error) ?? error.ToJsonString());

            return protocol switch
            {
                LlmProtocol.Responses => FeedResponses(o),
                LlmProtocol.Anthropic => FeedAnthropic(o),
                _ => FeedChatCompletions(o),
            };
        }

        private void AppendText(string? delta)
        {
            if (string.IsNullOrEmpty(delta)) return;
            _text.Append(delta);
            var now = _text.ToString().Trim();
            if (now.Length > 0) onText(now);
        }

        private bool FeedChatCompletions(JsonObject o)
        {
            if (Int(o["usage"]?["prompt_tokens"]) is { } usage) _inputTokens = usage;
            // 最后一块（只有 usage）的 choices 是空数组。
            if (o["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not { } choice) return false;
            var delta = choice["delta"];
            if (delta?["content"] is JsonValue c && c.TryGetValue<string>(out var text)) AppendText(text);
            foreach (var call in delta?["tool_calls"] as JsonArray ?? [])
            {
                var index = Int(call?["index"]) ?? (call?["index"] is JsonValue iv && iv.TryGetValue<int>(out var i0) ? i0 : _calls.Count);
                var fn = call?["function"];
                var (id, name, args) = _calls.TryGetValue(index, out var existing) ? existing : ("", "", new StringBuilder());
                if (Str(call?["id"]) is { Length: > 0 } newId) id = newId;
                if (Str(fn?["name"]) is { Length: > 0 } newName) name = newName;
                if (Str(fn?["arguments"]) is { } part) args.Append(part);
                _calls[index] = (id, name, args);
            }
            if (Str(choice["finish_reason"]) is { } stop) _stop = stop;
            return false;
        }

        private bool FeedResponses(JsonObject o)
        {
            switch (Str(o["type"]))
            {
                case "response.output_text.delta":
                    AppendText(Str(o["delta"]));
                    break;
                case "response.completed":
                case "response.incomplete":
                    if (o["response"] is { } r)
                    {
                        var full = ParseResponses(r);
                        _final = new ChatResponse(full.Parts, full.StopReason) { InputTokens = Int(r["usage"]?["input_tokens"]) };
                        if (full.Text.Length > 0 && full.Text != _text.ToString().Trim()) onText(full.Text);
                    }
                    return true;
                case "response.failed":
                    throw new LlmException(Str(o["response"]?["error"]?["message"]) ?? "请求失败（流式）");
            }
            return false;
        }

        private bool FeedAnthropic(JsonObject o)
        {
            switch (Str(o["type"]))
            {
                case "message_start":
                    var usage = o["message"]?["usage"];
                    if (Int(usage?["input_tokens"]) is { } input)
                        _inputTokens = input + (Int(usage?["cache_read_input_tokens"]) ?? 0) + (Int(usage?["cache_creation_input_tokens"]) ?? 0);
                    break;
                case "content_block_start":
                    var block = o["content_block"];
                    if (Str(block?["type"]) == "tool_use")
                        _calls[Int(o["index"]) ?? _calls.Count] = (Str(block?["id"]) ?? "", Str(block?["name"]) ?? "", new StringBuilder());
                    else if (Str(block?["text"]) is { Length: > 0 } initial)
                        AppendText(initial);
                    break;
                case "content_block_delta":
                    var delta = o["delta"];
                    switch (Str(delta?["type"]))
                    {
                        case "text_delta":
                            AppendText(Str(delta?["text"]));
                            break;
                        case "input_json_delta":
                            if (_calls.TryGetValue(Int(o["index"]) ?? -1, out var call)) call.Args.Append(Str(delta?["partial_json"]));
                            break;
                    }
                    break;
                case "message_delta":
                    if (Str(o["delta"]?["stop_reason"]) is { } stop) _stop = stop;
                    break;
                case "message_stop":
                    return true;
            }
            return false;
        }

        public ChatResponse Result()
        {
            if (_final is not null) return _final;
            var parts = new List<ChatPart>();
            if (_text.ToString().Trim() is { Length: > 0 } text) parts.Add(new TextPart(text));
            var n = 0;
            foreach (var (_, (id, name, args)) in _calls)
            {
                n++;
                if (name.Length == 0) continue;
                var json = args.ToString();
                parts.Add(new ToolCallPart(id.Length > 0 ? id : $"call_{n}", name, string.IsNullOrWhiteSpace(json) ? "{}" : json));
            }
            return new ChatResponse(parts, _stop) { InputTokens = _inputTokens };
        }
    }
}
