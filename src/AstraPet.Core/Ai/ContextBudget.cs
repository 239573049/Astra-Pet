namespace AstraPet.Ai;

/// <summary>
/// 粗估 token 数（没有各家的分词器）：中日韩字符、全角符号约 1 个 token 一个字，其余约 3.5 个字符一个 token，
/// 每条消息另加一点开销。偏保守；真实用量由接口返回的 usage 校准（<see cref="PetAgent"/>）。
/// </summary>
public static class TokenEstimate
{
    private const int MessageOverhead = 4;

    public static int Of(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var wide = 0;
        var narrow = 0;
        foreach (var c in text)
        {
            if (c >= 0x2E80 && !char.IsLowSurrogate(c)) wide++;
            else narrow++;
        }
        return wide + (int)Math.Ceiling(narrow / 3.5);
    }

    public static int Of(ChatMessage message)
    {
        var n = MessageOverhead;
        foreach (var part in message.Parts)
            n += part switch
            {
                TextPart t => Of(t.Text),
                ToolCallPart c => Of(c.Name) + Of(c.ArgumentsJson) + MessageOverhead,
                ToolResultPart r => Of(r.Content) + MessageOverhead,
                _ => 0,
            };
        return n;
    }

    public static int Of(IEnumerable<ChatMessage> messages) => messages.Sum(Of);
}

/// <summary>
/// 按模型的上下文窗口算出这次能给对话历史用多少 token、摘要写多长。
/// <list type="bullet">
/// <item>窗口来自模型库（models.dev）、或接口报「超长」时从错误信息里学到的；都不知道按 <see cref="UnknownWindow"/> 估。</item>
/// <item>只用窗口的 <see cref="UsableRatio"/>，余下给估算误差、工具定义、模型思考。</item>
/// <item>历史再封顶 <see cref="MaxHistoryTokens"/>：桌宠聊天用不着把几十万 token 的历史每轮都带上，贵且慢，旧的进摘要就够了。</item>
/// </list>
/// </summary>
public sealed record ContextBudget(int Window, bool Known, int HistoryTokens, int SummaryChars)
{
    public const int UnknownWindow = 32_000;
    public const double UsableRatio = 0.75;
    public const int MaxHistoryTokens = 24_000;
    /// <summary>窗口再小也至少留给历史这么多（大概两三轮），否则压缩一直触发、什么都带不上。</summary>
    public const int MinHistoryTokens = 600;

    public static ContextBudget For(int? window, int systemTokens, int maxOutput)
    {
        var known = window is > 0;
        var w = known ? window!.Value : UnknownWindow;
        var usable = (int)(w * UsableRatio) - systemTokens - maxOutput;
        var history = Math.Clamp(usable, MinHistoryTokens, MaxHistoryTokens);
        var summary = w switch
        {
            <= 8_000 => 150,
            <= 32_000 => 300,
            <= 128_000 => 450,
            _ => 600,
        };
        return new ContextBudget(w, known, history, summary);
    }

    /// <summary>给压缩请求里的对话记录留多少 token：窗口一半，再扣掉提示词和摘要输出。</summary>
    public int CompactionInputTokens => Math.Max(800, Window / 2 - 1500);

    public override string ToString() =>
        $"窗口 {Window}{(Known ? "" : "（未知，按默认）")}，历史预算 {HistoryTokens}，摘要 {SummaryChars} 字";
}
