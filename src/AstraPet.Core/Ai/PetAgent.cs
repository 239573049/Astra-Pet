using System.Text.Json;
using System.Text.Json.Nodes;
using AstraPet.Core;

namespace AstraPet.Ai;

/// <summary>一轮对话的结果。<see cref="Acted"/>：这一轮调用过 play_action（宠物已经在做动作了，不用再补随机反应）。</summary>
public sealed record AgentReply(string Text, bool Acted, bool UsedTools);

/// <summary>
/// 宠物的对话智能体：拼分层系统提示词（<see cref="SystemPrompt"/>），带最近几轮上下文和工具发给当前模型，
/// 模型调用工具时执行并按需再请求一轮（03 §1.4）。
/// 记忆是一组短句，存在 <see cref="PetSettings.Memories"/> 里随设置一起落盘；开了自动记忆时，
/// 每轮对话后再发一次请求，让模型从这轮里挑出值得长期记住的用户信息。
/// 在 UI 线程上调用，await 之后回到 UI 线程，所以可以直接改设置里的集合。
/// </summary>
public sealed class PetAgent
{
    public const int MaxMemories = 100;
    /// <summary>一轮对话最多请求几次（防止模型无限调工具）。</summary>
    public const int MaxRounds = 3;
    /// <summary>模型只调了工具、几轮下来都没说话时的兜底。</summary>
    public const string FallbackReply = "嗯嗯～";

    private readonly PetSettings _settings;
    private readonly ILlmClient _llm;
    private readonly TimeProvider _time;
    /// <summary>按轮存：一轮是用户的一句话加上之后的全部消息（含工具调用和结果），裁剪时整轮去掉，不拆开调用和结果。</summary>
    private readonly List<List<ChatMessage>> _turns = new();

    public PetAgent(PetSettings settings, ILlmClient llm, TimeProvider? time = null)
    {
        _settings = settings;
        _llm = llm;
        _time = time ?? TimeProvider.System;
        PlayAction = new PlayActionTool(() => Actions(), id => ActionRequested?.Invoke(id), a => Usable?.Invoke(a) ?? a.Stance == Stances.Ground);
        Tools = [PlayAction];
    }

    /// <summary>当前角色的人设（换宠物时由外面换掉）。</summary>
    public Func<PetPersona> Persona { get; set; } = () => PetPersona.Generic("Astra");

    /// <summary>当前宠物的动作；null 时没有 play_action。</summary>
    public Func<ActionSet?> Actions { get; set; } = () => null;

    /// <summary>这个动作现在能不能让 Agent 选（当前姿态、亲密度解锁）；null 只给地面上的。</summary>
    public Func<ActionDef, bool>? Usable { get; set; }

    /// <summary>【现在】里时间之后的几行（番茄钟、听歌、刚才的交互）。</summary>
    public Func<IReadOnlyList<string>> Situation { get; set; } = () => [];

    public PlayActionTool PlayAction { get; }
    public IReadOnlyList<IAgentTool> Tools { get; }

    /// <summary>智能体开始思考 / 思考结束。</summary>
    public event Action<bool>? Busy;

    /// <summary>模型调用了 play_action，参数是动作 id 或心情。收到就播，不等文字。</summary>
    public event Action<string>? ActionRequested;

    /// <summary>记忆有新增，参数是新记住的句子。</summary>
    public event Action<IReadOnlyList<string>>? Remembered;

    public IReadOnlyList<ChatMessage> History => _turns.SelectMany(t => t).ToList();

    public bool IsConfigured => _settings.ActiveProvider is { } p
                                && !string.IsNullOrWhiteSpace(p.BaseUrl) && !string.IsNullOrWhiteSpace(p.Model);

    public void ClearHistory() => _turns.Clear();

    public async Task<AgentReply> AskAsync(string text, CancellationToken ct = default)
    {
        var provider = _settings.ActiveProvider ?? throw new LlmException("还没有配置模型，请在控制台「智能」页添加");
        var useTools = provider.UsesTools;

        Busy?.Invoke(true);
        (AgentReply Reply, List<ChatMessage> Turn) result;
        try
        {
            try
            {
                result = await RunAsync(provider, text, useTools, ct);
            }
            catch (LlmException e) when (useTools && provider.ToolSupport == ToolSupport.Auto && e.LooksLikeToolsUnsupported)
            {
                // 03 §1.5：带工具被拒，记住这个模型不支持，去掉工具重试一次。
                PetLog.Write($"{provider.Name} · {provider.Model} 好像不支持工具调用，之后不再带工具: {e.Message}");
                provider.NoToolsModel = provider.Model;
                result = await RunAsync(provider, text, false, ct);
            }
        }
        finally
        {
            Busy?.Invoke(false);
        }

        _turns.Add(result.Turn);
        var keep = Math.Max(1, _settings.ContextTurns);
        if (_turns.Count > keep) _turns.RemoveRange(0, _turns.Count - keep);

        if (_settings.MemoryEnabled && _settings.AutoMemory)
            _ = ExtractMemoriesAsync(provider, text, result.Reply.Text);
        return result.Reply;
    }

    private async Task<(AgentReply, List<ChatMessage>)> RunAsync(LlmProvider provider, string text, bool useTools, CancellationToken ct)
    {
        var tools = new List<(IAgentTool Tool, ToolDefinition Def)>();
        if (useTools)
            foreach (var tool in Tools)
                if (tool.Definition() is { } def)
                    tools.Add((tool, def));

        var system = BuildSystemPrompt(canAct: tools.Any(t => t.Tool == PlayAction));
        var context = _turns.Skip(Math.Max(0, _turns.Count - _settings.ContextTurns)).SelectMany(t => t).ToList();
        if (_settings.ContextTurns == 0) context.Clear();
        if (tools.Count == 0) context = StripTools(context);

        var turn = new List<ChatMessage> { ChatMessage.User(text) };
        var texts = new List<string>();
        var acted = false;
        var timeout = TimeSpan.FromSeconds(_settings.TimeoutSeconds);
        var defs = tools.Count > 0 ? tools.Select(t => t.Def).ToList() : null;

        for (var round = 1; round <= MaxRounds; round++)
        {
            var response = await _llm.CompleteAsync(provider,
                new ChatRequest(system, [.. context, .. turn], _settings.Temperature, _settings.MaxTokens, defs), timeout, ct);

            var calls = defs is null ? [] : response.ToolCalls;
            var spoken = response.Text;
            var parts = new List<ChatPart>();
            if (spoken.Length > 0) parts.Add(new TextPart(spoken));
            parts.AddRange(calls);
            if (parts.Count == 0) break;
            turn.Add(new ChatMessage("assistant", parts));
            if (spoken.Length > 0) texts.Add(spoken);
            if (calls.Count == 0) break;

            var results = new List<ChatPart>();
            var presentationalOnly = true;
            foreach (var call in calls)
            {
                var tool = tools.FirstOrDefault(t => t.Def.Name == call.Name).Tool;
                ToolResult r;
                if (tool is null)
                    r = new ToolResult($"没有这个工具：{call.Name}", IsError: true);
                else if (tool == PlayAction && acted)
                    r = new ToolResult("已忽略：每次回复最多做一个动作");
                else
                    r = await InvokeAsync(tool, call, ct);
                if (tool == PlayAction && !r.IsError) acted = true;
                if (r.IsError) PetLog.Write($"工具 {call.Name}({call.ArgumentsJson}) 失败: {r.Content}");
                presentationalOnly &= tool?.IsPresentational ?? true;
                results.Add(new ToolResultPart(call.CallId, r.Content, r.IsError));
            }
            turn.Add(new ChatMessage("user", results));

            // 常见情况：一次回复里既说了话又调了纯表现的工具，结果只写进历史，不再请求。
            if (spoken.Length > 0 && presentationalOnly) break;
        }

        var reply = string.Join("\n", texts).Trim();
        if (reply.Length == 0)
        {
            if (defs is null) throw new LlmException("模型没有返回文本");
            reply = FallbackReply;
        }
        return (new AgentReply(reply, acted, defs is not null), turn);
    }

    private static async Task<ToolResult> InvokeAsync(IAgentTool tool, ToolCallPart call, CancellationToken ct)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson); }
        catch (JsonException) { return new ToolResult("参数不是合法的 JSON", IsError: true); }
        using (doc)
        {
            try { return await tool.InvokeAsync(doc.RootElement, ct); }
            catch (Exception e) when (e is not OperationCanceledException) { return new ToolResult(e.Message, IsError: true); }
        }
    }

    /// <summary>不带工具请求时，历史里的工具调用和结果去掉（有的接口见到没定义的工具会报错），只留文字。</summary>
    private static List<ChatMessage> StripTools(List<ChatMessage> messages) =>
        messages.Where(m => m.Parts.Any(p => p is TextPart))
            .Select(m => m.Parts.All(p => p is TextPart) ? m : new ChatMessage(m.Role, m.Parts.OfType<TextPart>().ToList()))
            .ToList();

    /// <summary>
    /// 分层系统提示词。<paramref name="canAct"/> 为 null 时按当前提供商是否带工具判断（控制台预览用）。
    /// </summary>
    public string BuildSystemPrompt(bool? canAct = null)
    {
        canAct ??= _settings.ActiveProvider is { UsesTools: true } && PlayAction.Definition() is not null;
        return SystemPrompt.Build(new PromptInputs
        {
            Persona = Persona(),
            CustomPersona = _settings.PersonaMode == PersonaMode.Custom ? _settings.PersonaCustom : null,
            Notes = _settings.PersonaNotes,
            Memories = _settings.MemoryEnabled ? _settings.Memories.Select(m => m.Text).ToList() : [],
            CanAct = canAct.Value,
            ActionExample = ExampleAction(),
            Now = _time.GetLocalNow().DateTime,
            Situation = Situation(),
        });
    }

    /// <summary>示例里的庆祝动作：有 celebrate 角色的动作用它的 id，否则写心情。</summary>
    private string ExampleAction() =>
        Actions() is { } set && PlayActionTool.Choosable(set).FirstOrDefault(a => a.HasRole(Roles.Celebrate)) is { } a
            ? a.Id
            : Roles.Celebrate;

    public bool AddMemory(string text, bool auto)
    {
        text = text.Trim();
        if (text.Length == 0) return false;
        if (_settings.Memories.Any(m => string.Equals(m.Text, text, StringComparison.OrdinalIgnoreCase)))
            return false;
        _settings.Memories.Add(new MemoryItem { Text = text, Auto = auto });
        while (_settings.Memories.Count > MaxMemories)
            _settings.Memories.RemoveAt(0);
        return true;
    }

    private async Task ExtractMemoriesAsync(LlmProvider provider, string userText, string reply)
    {
        var known = string.Join("\n", _settings.Memories.Select(m => "- " + m.Text));
        var prompt =
            "从下面这轮对话里，找出值得长期记住的「关于用户本人」的信息：兴趣爱好、喜好厌恶、习惯、身份、重要的人和日子、目标。\n" +
            "只记用户明确说过的事实，不要推测；闲聊、一次性的请求、已经记住且没有变化的内容都不要。\n" +
            "不要记密码、证件号、银行卡号、住址门牌号、健康诊断这类敏感信息。\n" +
            "每条写成一句简短的中文陈述，以「用户」开头。只输出 JSON 数组，没有就输出 []。\n" +
            "新信息直接写成字符串；如果它更新了某条已记住的内容（比如搬家、换工作），写成 {\"replace\": \"已记住的原句\", \"with\": \"新的那句\"}。\n\n" +
            $"已记住：\n{(known.Length == 0 ? "（无）" : known)}\n\n" +
            $"用户：{userText}\n宠物：{reply}";
        try
        {
            var raw = (await _llm.CompleteAsync(provider,
                new ChatRequest(null, [ChatMessage.User(prompt)], 0, 256),
                TimeSpan.FromSeconds(_settings.TimeoutSeconds))).Text;
            var added = new List<string>();
            foreach (var (replace, text) in ParseList(raw))
                if (replace is null ? AddMemory(text, auto: true) : ReplaceMemory(replace, text))
                    added.Add(text);
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

    /// <summary>
    /// 把一条已记住的内容换成新的（03 §3.7）：删掉原句，新句加到最后（算最新的，裁剪时最后才丢）。
    /// 找不到原句时当新信息加；新句已经有了就只删原句。返回是否加了新句。
    /// </summary>
    public bool ReplaceMemory(string old, string text)
    {
        var existing = _settings.Memories.FirstOrDefault(m => string.Equals(m.Text, old.Trim(), StringComparison.OrdinalIgnoreCase));
        if (existing is not null) _settings.Memories.Remove(existing);
        return AddMemory(text, auto: true);
    }

    /// <summary>
    /// 模型可能把 JSON 包在代码块或前后加话，取第一个 [ 到最后一个 ] 之间的部分。
    /// 每项是新句（字符串，Replace 为 null）或 {"replace": 原句, "with": 新句}。
    /// </summary>
    internal static IReadOnlyList<(string? Replace, string Text)> ParseList(string raw)
    {
        var start = raw.IndexOf('[');
        var end = raw.LastIndexOf(']');
        if (start < 0 || end <= start) return [];
        try
        {
            if (JsonNode.Parse(raw[start..(end + 1)]) is not JsonArray array) return [];
            var items = new List<(string?, string)>();
            foreach (var node in array)
            {
                if (node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                    items.Add((null, s));
                else if (node is JsonObject o && Str(o, "with") is { } with)
                    items.Add((Str(o, "replace"), with));
            }
            return items;
        }
        catch (JsonException)
        {
            return [];
        }

        static string? Str(JsonObject o, string key) =>
            o[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
    }
}
