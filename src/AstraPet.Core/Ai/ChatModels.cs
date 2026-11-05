using System.Text.Json.Nodes;
using AstraPet.Core;

namespace AstraPet.Ai;

/// <summary>消息里的一块内容：文字、工具调用或工具结果。设计见 design/pet-studio/03-agent-and-prompt.md §1.2。</summary>
public abstract record ChatPart;

public sealed record TextPart(string Text) : ChatPart;

/// <summary>模型发起的一次工具调用。<see cref="ArgumentsJson"/> 是参数对象的 JSON 文本。</summary>
public sealed record ToolCallPart(string CallId, string Name, string ArgumentsJson) : ChatPart;

/// <summary>工具的执行结果，放在 role 为 user 的消息里（Anthropic 的约定），各协议的序列化再按自己的格式拆开。</summary>
public sealed record ToolResultPart(string CallId, string Content, bool IsError = false) : ChatPart;

/// <summary>一条对话消息。Role 只有 user 和 assistant，系统提示词单独传。</summary>
public sealed record ChatMessage(string Role, IReadOnlyList<ChatPart> Parts)
{
    public static ChatMessage User(string text) => new("user", [new TextPart(text)]);
    public static ChatMessage Assistant(string text) => new("assistant", [new TextPart(text)]);

    /// <summary>全部文字块拼起来，对话窗口和气泡只显示这个。</summary>
    public string Text => string.Concat(Parts.OfType<TextPart>().Select(p => p.Text));

    public IEnumerable<ToolCallPart> ToolCalls => Parts.OfType<ToolCallPart>();
    public IEnumerable<ToolResultPart> ToolResults => Parts.OfType<ToolResultPart>();
}

/// <summary>给模型的一个工具：名字、说明、参数的 JSON Schema。</summary>
public sealed record ToolDefinition(string Name, string Description, JsonObject Parameters);

/// <summary>一次请求的参数。<see cref="Tools"/> 为 null 或空时不带工具，和以前一样。</summary>
public sealed record ChatRequest(
    string? System,
    IReadOnlyList<ChatMessage> Messages,
    double Temperature,
    int MaxTokens,
    IReadOnlyList<ToolDefinition>? Tools = null)
{
    public bool HasTools => Tools is { Count: > 0 };
}

/// <summary>模型的回复。<see cref="StopReason"/> 是各协议原样的停止原因，可能为 null。</summary>
public sealed record ChatResponse(IReadOnlyList<ChatPart> Parts, string? StopReason = null)
{
    public static ChatResponse FromText(string text) => new([new TextPart(text)]);

    public string Text => string.Concat(Parts.OfType<TextPart>().Select(p => p.Text)).Trim();
    public IReadOnlyList<ToolCallPart> ToolCalls => Parts.OfType<ToolCallPart>().ToList();
}

/// <summary>调用失败，信息给人看。<see cref="Status"/> 是 HTTP 状态码（不是 HTTP 错误时为 null）。</summary>
public sealed class LlmException(string message, int? status = null) : Exception(message)
{
    public int? Status { get; } = status;

    /// <summary>
    /// 看起来是「这个模型 / 网关不支持工具」的拒绝：4xx，且错误信息里提到 tool / function。
    /// <see cref="ToolSupport.Auto"/> 时据此去掉工具重试一次（03 §1.5）。
    /// </summary>
    public bool LooksLikeToolsUnsupported =>
        Status is >= 400 and < 500 and not 401 and not 403 and not 429
        && (Message.Contains("tool", StringComparison.OrdinalIgnoreCase)
            || Message.Contains("function", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 发一次非流式对话请求。实现在 Infrastructure（三种协议的 HTTP 调用）；测试里用假的。
/// 失败抛 <see cref="LlmException"/>。
/// </summary>
public interface ILlmClient
{
    Task<ChatResponse> CompleteAsync(LlmProvider provider, ChatRequest request, TimeSpan timeout, CancellationToken ct = default);
}
