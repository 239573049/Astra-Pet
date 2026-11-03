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
/// 发一次非流式对话请求，返回模型的文本。实现在 Infrastructure（三种协议的 HTTP 调用）；测试里用假的。
/// 失败抛 <see cref="LlmException"/>，信息给人看。
/// </summary>
public interface ILlmClient
{
    Task<string> CompleteAsync(LlmProvider provider, ChatRequest request, TimeSpan timeout, CancellationToken ct = default);
}
