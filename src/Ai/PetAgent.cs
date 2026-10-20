using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AstraPet.Core;

namespace AstraPet.Ai;

/// <summary>
/// 宠物的对话智能体：拼系统提示词（用户设定 + 记忆 + 当前时间），带最近几轮上下文发给当前模型。
/// 记忆是一组短句，存在 <see cref="PetSettings.Memories"/> 里随设置一起落盘；开了自动记忆时，
/// 每轮对话后再发一次请求，让模型从这轮里挑出值得长期记住的用户信息。
/// 在 UI 线程上调用，await 之后回到 UI 线程，所以可以直接改设置里的集合。
/// </summary>
public sealed class PetAgent(PetSettings settings)
{
    public const int MaxMemories = 100;

    private readonly List<ChatMessage> _history = new();

    /// <summary>智能体开始思考 / 思考结束，宠物窗口用来显示「…」气泡。</summary>
    public event Action<bool>? Busy;

    /// <summary>记忆有新增，参数是新记住的句子。</summary>
    public event Action<IReadOnlyList<string>>? Remembered;

    public IReadOnlyList<ChatMessage> History => _history;

    public bool IsConfigured => settings.ActiveProvider is { } p
                                && !string.IsNullOrWhiteSpace(p.BaseUrl) && !string.IsNullOrWhiteSpace(p.Model);

    public void ClearHistory() => _history.Clear();

    public async Task<string> AskAsync(string text, CancellationToken ct = default)
    {
        var provider = settings.ActiveProvider ?? throw new LlmException("还没有配置模型，请在控制台「智能」页添加");
        var turns = settings.ContextTurns;
        var context = _history.Skip(Math.Max(0, _history.Count - turns * 2)).ToList();
        context.Add(new ChatMessage("user", text));

        Busy?.Invoke(true);
        string reply;
        try
        {
            reply = await LlmClient.CompleteAsync(provider,
                new ChatRequest(BuildSystemPrompt(), context, settings.Temperature, settings.MaxTokens),
                TimeSpan.FromSeconds(settings.TimeoutSeconds), ct);
        }
        finally
        {
            Busy?.Invoke(false);
        }

        _history.Add(new ChatMessage("user", text));
        _history.Add(new ChatMessage("assistant", reply));
        // 历史只留够用的部分，避免长时间运行越攒越多。
        var keep = Math.Max(2, turns * 2);
        if (_history.Count > keep * 2) _history.RemoveRange(0, _history.Count - keep);

        if (settings.MemoryEnabled && settings.AutoMemory)
            _ = ExtractMemoriesAsync(provider, text, reply);
        return reply;
    }

    /// <summary>系统提示词 = 用户设定 + 记忆 + 当前时间。控制台的预览也用它。</summary>
    public string BuildSystemPrompt()
    {
        var sb = new StringBuilder(settings.SystemPrompt.Trim());
        if (settings.MemoryEnabled && settings.Memories.Count > 0)
        {
            sb.AppendLine().AppendLine().AppendLine("关于用户，你记得这些：");
            foreach (var m in settings.Memories)
                sb.Append("- ").AppendLine(m.Text);
        }
        sb.AppendLine().Append($"现在是 {DateTime.Now:yyyy-MM-dd dddd HH:mm}。");
        return sb.ToString();
    }

    public bool AddMemory(string text, bool auto)
    {
        text = text.Trim();
        if (text.Length == 0) return false;
        if (settings.Memories.Any(m => string.Equals(m.Text, text, StringComparison.OrdinalIgnoreCase)))
            return false;
        settings.Memories.Add(new MemoryItem { Text = text, Auto = auto });
        while (settings.Memories.Count > MaxMemories)
            settings.Memories.RemoveAt(0);
        return true;
    }

    private async Task ExtractMemoriesAsync(LlmProvider provider, string userText, string reply)
    {
        var known = string.Join("\n", settings.Memories.Select(m => "- " + m.Text));
        var prompt =
            "从下面这轮对话里，找出值得长期记住的「关于用户本人」的信息：兴趣爱好、喜好厌恶、习惯、身份、重要的人和日子、目标。\n" +
            "只记用户明确说过的事实，不要推测；闲聊、一次性的请求、已经记住的内容都不要。\n" +
            "每条写成一句简短的中文陈述，以「用户」开头。只输出 JSON 字符串数组，没有就输出 []。\n\n" +
            $"已记住：\n{(known.Length == 0 ? "（无）" : known)}\n\n" +
            $"用户：{userText}\n宠物：{reply}";
        try
        {
            var raw = await LlmClient.CompleteAsync(provider,
                new ChatRequest(null, [new ChatMessage("user", prompt)], 0, 256),
                TimeSpan.FromSeconds(settings.TimeoutSeconds));
            var added = ParseList(raw).Where(t => AddMemory(t, auto: true)).ToList();
            if (added.Count > 0)
            {
                PetLog.Write($"记住了: {string.Join("；", added)}");
                Remembered?.Invoke(added);
            }
        }
        catch (Exception e)
        {
            PetLog.Write($"提取记忆失败: {e.Message}");
        }
    }

    /// <summary>模型可能把 JSON 包在代码块或前后加话，取第一个 [ 到最后一个 ] 之间的部分。</summary>
    private static IEnumerable<string> ParseList(string raw)
    {
        var start = raw.IndexOf('[');
        var end = raw.LastIndexOf(']');
        if (start < 0 || end <= start) return [];
        try
        {
            return JsonNode.Parse(raw[start..(end + 1)]) is JsonArray array
                ? array.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
                       .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToArray()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
