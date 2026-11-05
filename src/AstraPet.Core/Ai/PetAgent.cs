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
        Memory = new MemoryTool(() => _settings.MemoryEnabled, RememberByTool, Forget);
        Tools = [PlayAction, Memory];
    }

    /// <summary>当前角色的人设（换宠物时由外面换掉）。</summary>
    public Func<PetPersona> Persona { get; set; } = () => PetPersona.Generic("Astra");
    /// <summary>角色包自带的提示词（.md）；null 表示没有，用结构化人设。</summary>
    public Func<string?> PackPrompt { get; set; } = () => null;

    /// <summary>当前宠物的动作；null 时没有 play_action。</summary>
    public Func<ActionSet?> Actions { get; set; } = () => null;

    /// <summary>这个动作现在能不能让 Agent 选（当前姿态、亲密度解锁）；null 只给地面上的。</summary>
    public Func<ActionDef, bool>? Usable { get; set; }

    /// <summary>【现在】里时间之后的几行（番茄钟、听歌、刚才的交互）。</summary>
    public Func<IReadOnlyList<string>> Situation { get; set; } = () => [];

    public PlayActionTool PlayAction { get; }
    public MemoryTool Memory { get; }
    public IReadOnlyList<IAgentTool> Tools { get; }

    /// <summary>智能体开始思考 / 思考结束。</summary>
    public event Action<bool>? Busy;

    /// <summary>模型调用了 play_action，参数是动作 id 或心情。收到就播，不等文字。</summary>
    public event Action<string>? ActionRequested;

    /// <summary>记忆有新增，参数是新记住的句子。</summary>
    public event Action<IReadOnlyList<string>>? Remembered;

    public IReadOnlyList<ChatMessage> History => _turns.SelectMany(t => t).ToList();

    /// <summary>压缩掉的较早对话的摘要（写进系统提示词）；还没压缩过为 null。</summary>
    public string? Summary => _summary;

    /// <summary>上下文压缩完成（摘要更新了）。</summary>
    public event Action? Compacted;

    public bool IsConfigured => _settings.ActiveProvider is { } p
                                && !string.IsNullOrWhiteSpace(p.BaseUrl) && !string.IsNullOrWhiteSpace(p.Model);

    public void ClearHistory()
    {
        _turns.Clear();
        _summary = null;
        _lastAt = null;
        _generation++;   // 正在跑的压缩结果作废
        HistoryChanged?.Invoke();
    }

    // ───── 持久化 ─────

    private DateTimeOffset? _lastAt;

    /// <summary>对话变了（多了一轮、压缩完、清空）：外面据此存盘。</summary>
    public event Action? HistoryChanged;

    /// <summary>最后一次聊天的时间（重启后从快照里恢复）。</summary>
    public DateTimeOffset? LastChatAt => _lastAt;

    /// <summary>上次聊天隔了这么久，提示词里提一句，免得模型接着几小时前的话头像刚说完一样。</summary>
    public static readonly TimeSpan MentionGapAfter = TimeSpan.FromMinutes(30);

    public ConversationSnapshot Snapshot() => new(_turns.Select(t => (IReadOnlyList<ChatMessage>)t.ToList()).ToList(), _summary, _lastAt);

    /// <summary>换上一份保存的对话（启动、换宠物时）。null 等于清空。不触发 <see cref="HistoryChanged"/>。</summary>
    public void Restore(ConversationSnapshot? snapshot)
    {
        _generation++;
        _turns.Clear();
        _turns.AddRange((snapshot?.Turns ?? []).Select(t => t.ToList()));
        _summary = snapshot?.Summary;
        _lastAt = snapshot?.LastAt;
    }

    /// <summary>「上次聊天是 3 小时前」；没聊过或隔得不久返回 null。</summary>
    internal string? GapLine()
    {
        if (_lastAt is not { } last || _turns.Count == 0 && _summary is null) return null;
        var gap = _time.GetUtcNow() - last;
        if (gap < MentionGapAfter) return null;
        var text = gap.TotalDays >= 1 ? $"{(int)gap.TotalDays} 天" : gap.TotalHours >= 1 ? $"{(int)gap.TotalHours} 小时" : $"{(int)gap.TotalMinutes} 分钟";
        return $"上次和用户聊天是 {text}前（之前的对话是那时候的，接着聊时注意时间已经过去了）";
    }

    /// <summary>主动开口时，模型这次不想说话就只回这个。</summary>
    public const string SilentToken = "[SILENT]";

    public Task<AgentReply> AskAsync(string text, CancellationToken ct = default) => AskAsync(text, null, ct);

    /// <param name="onPartial">流式：每收到一段就用到目前为止的整段回复回调一次（null 不流式）。</param>
    public async Task<AgentReply> AskAsync(string text, Action<string>? onPartial, CancellationToken ct = default) =>
        (await AskCoreAsync(text, proactive: false, onPartial, ct))!;   // 非主动的一轮总有回复（空回复已换成兜底或抛异常）

    /// <summary>
    /// 主动开口：用户没在说话，告诉模型起因（<paramref name="cue"/>），让它按自己的性格决定说不说、说什么。
    /// 不想说返回 null（这一轮不进上下文）；说了的话，起因和回复都进上下文，用户接着回话时模型知道刚才自己说了什么。
    /// 不冒「…」、不提取记忆（这一轮没有用户说的话）。
    /// </summary>
    /// <param name="mustSpeak">问候、提醒这类一定要说到的：不给「不说」的选项。</param>
    public Task<AgentReply?> SpeakUpAsync(string cue, bool mustSpeak = false, Action<string>? onPartial = null, CancellationToken ct = default) =>
        AskCoreAsync(ProactivePrompt(cue, mustSpeak), proactive: true, onPartial, ct);

    /// <summary>流式回来的半截回复可能是 <see cref="SilentToken"/> 的开头，这时先别显示。</summary>
    public static bool MaybeSilent(string partial)
    {
        var t = partial.Trim();
        return t.Length == 0 || SilentToken.StartsWith(t, StringComparison.OrdinalIgnoreCase)
                             || t.Contains(SilentToken, StringComparison.OrdinalIgnoreCase);
    }

    internal static string ProactivePrompt(string cue, bool mustSpeak = false) => mustSpeak
        ? "【主动开口】（这条不是用户发的消息，是系统告诉你的：现在需要你主动跟用户说一句。）\n" +
          $"起因：{cue}\n" +
          "用你自己的性格和口吻把这件事说出来，就像你自己想起来的一样，一两句就好；" +
          "不要提「系统」「起因」，别和刚才说过的话重复。"
        : "【主动开口】（这条不是用户发的消息，是系统告诉你的：用户现在没在和你说话，你可以自己开口。）\n" +
          $"起因：{cue}\n" +
          "按你的性格决定要不要开口、说什么：话多活泼的可以多说两句，内向、高冷或犯懒的可以少开口甚至不理人。\n" +
          "开口的话，就像你自己突然想说的一样，自然地说一句；不要提「系统」「起因」，不要每次都提问，也别重复刚才说过的话。\n" +
          $"这次不想说话，就只回复 {SilentToken}。";

    private async Task<AgentReply?> AskCoreAsync(string text, bool proactive, Action<string>? onPartial, CancellationToken ct)
    {
        var provider = _settings.ActiveProvider ?? throw new LlmException("还没有配置模型，请在控制台「智能」页添加");
        var useTools = provider.UsesTools;

        if (!proactive) Busy?.Invoke(true);
        (AgentReply Reply, List<ChatMessage> Turn) result;
        try
        {
            try
            {
                result = await RunWithOverflowRetryAsync(provider, text, useTools, proactive, onPartial, ct);
            }
            catch (LlmException e) when (useTools && provider.ToolSupport == ToolSupport.Auto && e.LooksLikeToolsUnsupported)
            {
                // 03 §1.5：带工具被拒，记住这个模型不支持，去掉工具重试一次。
                PetLog.Write($"{provider.Name} · {provider.Model} 好像不支持工具调用，之后不再带工具: {e.Message}");
                provider.NoToolsModel = provider.Model;
                result = await RunWithOverflowRetryAsync(provider, text, false, proactive, onPartial, ct);
            }
        }
        finally
        {
            if (!proactive) Busy?.Invoke(false);
        }

        if (proactive && IsSilent(result.Reply.Text))
            return result.Reply.Acted ? result.Reply with { Text = "" } : null;

        _turns.Add(result.Turn);
        _lastAt = _time.GetUtcNow();
        TrimContext(provider);
        HistoryChanged?.Invoke();

        if (!proactive && _settings.MemoryEnabled)
        {
            // 用户明确让记 / 忘，但这轮没用上 remember 工具（模型不支持工具、或者它忘了调）：马上提取一次，不管开没开自动记忆。
            var explicitAsk = AsksToRemember(text) && !result.Turn.Any(m => m.ToolCalls.Any(c => c.Name == MemoryTool.ToolName));
            if (explicitAsk || _settings.AutoMemory)
                _ = ExtractMemoriesAsync(provider, text, result.Reply.Text, explicitAsk);
        }
        return result.Reply;
    }

    /// <summary>
    /// 接口说上下文超长：从错误里学到真实窗口（有的话），校准往大里调，立刻把历史压到一半以下再试一次。
    /// 压下来的几轮照常交给后台并进摘要。
    /// </summary>
    private async Task<(AgentReply, List<ChatMessage>)> RunWithOverflowRetryAsync(
        LlmProvider provider, string text, bool useTools, bool proactive, Action<string>? onPartial, CancellationToken ct)
    {
        try
        {
            return await RunAsync(provider, text, useTools, proactive, onPartial, ct);
        }
        catch (LlmException e) when (e.LooksLikeContextOverflow && _turns.Count > 0)
        {
            if (e.ReportedContextWindow is { } reported) _learnedWindows[provider.Model] = reported;
            _calibration = Math.Min(MaxCalibration, _calibration * 1.5);
            var budget = Budget(provider);
            PetLog.Write($"上下文超长，先压缩再重试（{budget}）: {e.Message}");
            Trim(provider, budget, targetTokens: budget.HistoryTokens / 2, targetTurns: _turns.Count / 2, reason: "接口报超长");
            return await RunAsync(provider, text, useTools, proactive, onPartial, ct);
        }
    }

    /// <summary>回复是空的或只有 <see cref="SilentToken"/>（模型可能顺手加了标点、换行）。</summary>
    internal static bool IsSilent(string reply) =>
        reply.Trim().Trim('。', '.', '…', ' ', '\n').Length == 0 || reply.Contains(SilentToken, StringComparison.OrdinalIgnoreCase);

    private async Task<(AgentReply, List<ChatMessage>)> RunAsync(LlmProvider provider, string text, bool useTools, bool proactive,
        Action<string>? onPartial, CancellationToken ct)
    {
        var tools = new List<(IAgentTool Tool, ToolDefinition Def)>();
        if (useTools)
            foreach (var tool in Tools)
                if (tool.Definition() is { } def)
                    tools.Add((tool, def));

        var system = BuildSystemPrompt(canAct: tools.Any(t => t.Tool == PlayAction), canRemember: tools.Any(t => t.Tool == Memory));
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
                new ChatRequest(system, [.. context, .. turn], null, _settings.MaxTokens, defs)
                {
                    // 前几轮（调工具那几轮）说过的话加上这一轮正在收的，拼成整段回复。
                    OnText = onPartial is null ? null : partial => onPartial(string.Join("\n", [.. texts, partial]).Trim()),
                }, timeout, ct);
            if (round == 1) Calibrate(response.InputTokens, system, [.. context, .. turn], defs);

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
        if (reply.Length == 0 && !proactive)
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
    public string BuildSystemPrompt(bool? canAct = null, bool? canRemember = null)
    {
        canAct ??= _settings.ActiveProvider is { UsesTools: true } && PlayAction.Definition() is not null;
        canRemember ??= _settings.ActiveProvider is { UsesTools: true } && Memory.Definition() is not null;
        return SystemPrompt.Build(new PromptInputs
        {
            Persona = Persona(),
            PackPrompt = PackPrompt(),
            CustomPersona = _settings.PersonaMode == PersonaMode.Custom ? _settings.PersonaCustom : null,
            Notes = _settings.PersonaNotes,
            Memories = _settings.MemoryEnabled ? _settings.Memories.Select(m => m.Text).ToList() : [],
            CanAct = canAct.Value,
            CanRemember = canRemember.Value,
            ActionExample = ExampleAction(),
            Now = _time.GetLocalNow().DateTime,
            Situation = GapLine() is { } gap ? [gap, .. Situation()] : Situation(),
            Summary = _settings.ContextTurns > 0 ? _summary : null,
        });
    }

    /// <summary>示例里的庆祝动作：有 celebrate 角色的动作用它的 id，否则写心情。</summary>
    private string ExampleAction() =>
        Actions() is { } set && PlayActionTool.Choosable(set).FirstOrDefault(a => a.HasRole(Roles.Celebrate)) is { } a
            ? a.Id
            : Roles.Celebrate;

    // ───── 上下文压缩 ─────

    private string? _summary;
    private int _generation;
    private Task _compaction = Task.CompletedTask;

    /// <summary>这次会话里从「超长」错误学到的窗口，按模型 id 记（比模型库的更可信）。</summary>
    private readonly Dictionary<string, int> _learnedWindows = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>真实 token ÷ 估算 token，按接口返回的 usage 慢慢校准；估算偏少时调大，偏多时调小。</summary>
    private double _calibration = 1.0;
    private const double MinCalibration = 0.5;
    private const double MaxCalibration = 3.0;

    /// <summary>最近一次算出的预算（控制台、日志看）。</summary>
    public ContextBudget? LastBudget { get; private set; }

    /// <summary>当前历史估计占多少 token（已乘校准系数）。</summary>
    public int HistoryTokens => Scaled(TokenEstimate.Of(_turns.SelectMany(t => t)));

    private int Scaled(int estimate) => (int)Math.Ceiling(estimate * _calibration);

    /// <summary>当前模型的窗口：报错学到的 → 模型库 → 不知道。</summary>
    public int? ContextWindow(LlmProvider provider) =>
        _learnedWindows.TryGetValue(provider.Model, out var learned) ? learned : _llm.ContextWindow(provider);

    public ContextBudget Budget(LlmProvider provider)
    {
        var system = Scaled(TokenEstimate.Of(BuildSystemPrompt(canAct: provider.UsesTools)));
        return LastBudget = ContextBudget.For(ContextWindow(provider), system, _settings.MaxTokens);
    }

    private void Calibrate(int? actual, string system, IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition>? tools)
    {
        if (actual is not > 0) return;
        var estimate = TokenEstimate.Of(system) + TokenEstimate.Of(messages)
                       + (tools?.Sum(t => TokenEstimate.Of(t.Name) + TokenEstimate.Of(t.Description) + TokenEstimate.Of(t.Parameters.ToString())) ?? 0);
        if (estimate < 200) return;   // 太短的请求固定开销占比大，不拿来校准
        var ratio = Math.Clamp((double)actual.Value / estimate, MinCalibration, MaxCalibration);
        _calibration = Math.Clamp(_calibration * 0.7 + ratio * 0.3, MinCalibration, MaxCalibration);
    }

    /// <summary>
    /// 每轮之后看要不要裁上下文，两个上限任一超了就裁：
    /// <list type="bullet">
    /// <item>轮数超过 <see cref="PetSettings.ContextTurns"/>（默认不限，只有测试会设）；</item>
    /// <item>历史 token 超过按模型上下文窗口算出的预算（<see cref="ContextBudget"/>）。</item>
    /// </list>
    /// 开了压缩时一次裁到上限的一半左右，攒成一批交给模型并进摘要，免得每说一句就多一次请求；
    /// 没开压缩就只裁到刚好不超。最近一轮总是留着。
    /// </summary>
    private void TrimContext(LlmProvider provider)
    {
        var budget = Budget(provider);
        var turnLimit = Math.Max(1, _settings.ContextTurns);
        var used = HistoryTokens;
        var overTurns = _turns.Count > turnLimit;
        var overTokens = used > budget.HistoryTokens;
        if (!overTurns && !overTokens) return;

        if (!Compacting)
        {
            Trim(provider, budget, budget.HistoryTokens, turnLimit, reason: null);
            return;
        }
        var targetTurns = overTurns ? turnLimit - turnLimit / 2 : _turns.Count;
        var targetTokens = overTokens ? budget.HistoryTokens / 2 : budget.HistoryTokens;
        Trim(provider, budget, targetTokens, targetTurns,
            reason: overTokens ? $"历史约 {used} token，超过预算 {budget.HistoryTokens}" : $"超过 {turnLimit} 轮");
    }

    private bool Compacting => _settings.CompactContext && _settings.ContextTurns > 0;

    /// <summary>从最早的一轮开始去掉，直到轮数和 token 都不超过目标（至少留一轮）；开了压缩就把去掉的交给后台并进摘要。</summary>
    private void Trim(LlmProvider provider, ContextBudget budget, int targetTokens, int targetTurns, string? reason)
    {
        var used = HistoryTokens;
        var drop = 0;
        while (_turns.Count - drop > 1 && (_turns.Count - drop > Math.Max(1, targetTurns) || used > targetTokens))
        {
            used -= Scaled(TokenEstimate.Of(_turns[drop]));
            drop++;
        }
        if (drop == 0) return;
        var dropped = _turns.GetRange(0, drop);
        _turns.RemoveRange(0, drop);
        if (!Compacting) return;

        PetLog.Write($"压缩上下文：{reason}，并掉最早的 {drop} 轮（{budget}）");
        var generation = _generation;
        var previous = _compaction;
        _compaction = CompactAsync(provider, dropped, budget, previous, generation);
    }

    /// <summary>等正在跑的压缩做完（测试、退出前用）。</summary>
    public Task WaitForCompactionAsync() => _compaction;

    private async Task CompactAsync(LlmProvider provider, List<List<ChatMessage>> dropped, ContextBudget budget, Task previous, int generation)
    {
        // 一次只跑一个压缩，后面的接着前面的摘要往下并。
        try { await previous; } catch { /* 前一个自己写过日志 */ }
        if (generation != _generation) return;

        var withMemory = _settings.MemoryEnabled && _settings.CompactMemory;
        var prompt = CompactionPrompt(_summary, Transcript(dropped, budget.CompactionInputTokens),
            withMemory ? _settings.Memories.Select(m => m.Text).ToList() : null, budget.SummaryChars);
        try
        {
            var raw = (await _llm.CompleteAsync(provider,
                new ChatRequest(null, [ChatMessage.User(prompt)], 0.2, Math.Max(_settings.MaxTokens, 600)),
                TimeSpan.FromSeconds(_settings.TimeoutSeconds))).Text;
            if (generation != _generation) return;
            var (summary, items) = ParseCompaction(raw);
            if (summary is not null)
            {
                var max = budget.SummaryChars * 2;
                _summary = summary.Length <= max ? summary : summary[..max];
                PetLog.Write($"上下文已压缩：{dropped.Count} 轮并进摘要（{_summary.Length} 字）");
                Compacted?.Invoke();
                HistoryChanged?.Invoke();
            }
            if (withMemory) ApplyMemories(items);
        }
        catch (Exception e)
        {
            PetLog.Write($"压缩上下文失败（这几轮就直接丢掉了）: {e.Message}");
        }
    }

    /// <summary>
    /// 把要压缩的几轮写成「用户：… / 你：…」。工具调用不写；主动开口的那条写成一句说明。
    /// 超过 <paramref name="maxTokens"/> 时先把每条长消息截短，还超就只留后面的（越近越重要）。
    /// </summary>
    internal static string Transcript(IEnumerable<List<ChatMessage>> turns, int maxTokens = int.MaxValue)
    {
        var lines = new List<string>();
        foreach (var message in turns.SelectMany(t => t))
        {
            var text = message.Text.Trim();
            if (text.Length == 0) continue;
            if (message.Role == "user")
                lines.Add(text.StartsWith("【主动开口】", StringComparison.Ordinal) ? "（你主动找用户说话）" : "用户：" + text);
            else
                lines.Add("你：" + text);
        }
        if (lines.Sum(TokenEstimate.Of) > maxTokens)
            lines = lines.Select(l => l.Length > 400 ? l[..400] + "…" : l).ToList();
        var total = lines.Sum(TokenEstimate.Of);
        var skip = 0;
        while (total > maxTokens && skip < lines.Count - 1) total -= TokenEstimate.Of(lines[skip++]);
        return (skip > 0 ? "（更早的部分太长，略）\n" : "") + string.Join("\n", lines.Skip(skip));
    }

    internal static string CompactionPrompt(string? summary, string transcript, IReadOnlyList<string>? memories, int summaryChars = 300)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("你在帮一只桌面宠物整理它和用户较早的聊天记录，这些对话马上要从上下文里移走。");
        sb.AppendLine("1. summary：把「之前的摘要」和下面这段对话合并成一份新的摘要。用第二人称称呼宠物（「你」）、称用户为「用户」，");
        sb.AppendLine("   记下聊过的话题、用户提到的近况和计划、你答应过用户的事、还没聊完的话头；不要记寒暄。");
        sb.AppendLine($"   不超过 {summaryChars} 字；摘要太长时，越早、越不重要的内容越该删。");
        if (memories is not null)
        {
            sb.AppendLine("2. memories：从这段对话里找出值得长期记住的「关于用户本人」的信息。");
            sb.AppendLine(MemoryRules);
        }
        sb.AppendLine(memories is not null
            ? "只输出一个 JSON 对象：{\"summary\": \"…\", \"memories\": [ … ]}"
            : "只输出一个 JSON 对象：{\"summary\": \"…\"}");
        sb.AppendLine();
        if (memories is not null)
        {
            sb.AppendLine("已记住：");
            sb.AppendLine(memories.Count == 0 ? "（无）" : string.Join("\n", memories.Select(m => "- " + m)));
            sb.AppendLine();
        }
        sb.AppendLine("之前的摘要：");
        sb.AppendLine(string.IsNullOrWhiteSpace(summary) ? "（无）" : summary);
        sb.AppendLine();
        sb.AppendLine("这段对话：");
        sb.Append(transcript);
        return sb.ToString();
    }

    /// <summary>取第一个 { 到最后一个 } 之间的 JSON 对象；summary 为空时返回 null（保留旧摘要）。</summary>
    internal static (string? Summary, IReadOnlyList<(string? Replace, string Text)> Memories) ParseCompaction(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return (null, []);
        try
        {
            if (JsonNode.Parse(raw[start..(end + 1)]) is not JsonObject o) return (null, []);
            var summary = o["summary"] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
            return (summary, o["memories"] is JsonArray a ? ParseItems(a) : []);
        }
        catch (JsonException)
        {
            return (null, []);
        }
    }

    /// <summary>用户这句话是不是在让宠物记住或忘掉什么。</summary>
    internal static bool AsksToRemember(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(text,
            @"记住|记一下|记下来|记着|别忘|不要忘|忘掉|忘了吧|别记|不用记|remember|forget|don't forget",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>remember 工具记一条。工具记的算宠物自己记的（Auto）；它会在回复里自己说，不再冒「我记住啦」。</summary>
    private MemoryChange RememberByTool(string text, string? replaces)
    {
        var existed = replaces is not null && _settings.Memories.Any(m => string.Equals(m.Text, replaces.Trim(), StringComparison.OrdinalIgnoreCase));
        var added = replaces is null ? AddMemory(text, auto: true) : ReplaceMemory(replaces, text);
        if (added) PetLog.Write($"宠物记住了: {text}");
        return !added ? MemoryChange.Unchanged : existed ? MemoryChange.Replaced : MemoryChange.Added;
    }

    /// <summary>
    /// 删掉一条记忆：先找一模一样的；模型没照抄原句时，只有恰好一条和它互相包含才删，有好几条对得上就不猜（告诉模型没找到，让它照抄原句再来）。
    /// 返回删掉的那句，没找到为 null。
    /// </summary>
    public string? Forget(string text)
    {
        text = text.Trim();
        var item = _settings.Memories.FirstOrDefault(m => string.Equals(m.Text, text, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            var near = _settings.Memories.Where(m => m.Text.Contains(text, StringComparison.OrdinalIgnoreCase)
                                                     || text.Contains(m.Text, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            item = near.Count == 1 ? near[0] : null;
        }
        if (item is null) return null;
        _settings.Memories.Remove(item);
        PetLog.Write($"忘掉了: {item.Text}");
        return item.Text;
    }

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

    /// <summary>提取记忆的规则：每轮提取和压缩上下文时共用。</summary>
    internal const string MemoryRules =
        "要记的：兴趣爱好、喜好厌恶、习惯、身份、重要的人和日子、目标。\n" +
        "只记用户明确说过的事实，不要推测；闲聊、一次性的请求、已经记住且没有变化的内容都不要。\n" +
        "不要记密码、证件号、银行卡号、住址门牌号、健康诊断这类敏感信息。\n" +
        "每条写成一句简短的中文陈述，以「用户」开头。新信息直接写成字符串；" +
        "如果它更新了某条已记住的内容（比如搬家、换工作），写成 {\"replace\": \"已记住的原句\", \"with\": \"新的那句\"}。";

    private void ApplyMemories(IReadOnlyList<(string? Replace, string Text)> items)
    {
        var added = new List<string>();
        foreach (var (replace, text) in items)
        {
            if (text.Length == 0)
            {
                if (replace is not null) Forget(replace);
                continue;
            }
            if (replace is null ? AddMemory(text, auto: true) : ReplaceMemory(replace, text))
                added.Add(text);
        }
        if (added.Count == 0) return;
        PetLog.Write($"记住了: {string.Join("；", added)}");
        Remembered?.Invoke(added);
    }

    private async Task ExtractMemoriesAsync(LlmProvider provider, string userText, string reply, bool explicitAsk = false)
    {
        var known = string.Join("\n", _settings.Memories.Select(m => "- " + m.Text));
        var prompt =
            "从下面这轮对话里，找出值得长期记住的「关于用户本人」的信息。\n" +
            MemoryRules + "\n" +
            (explicitAsk
                ? "这次用户明确要求记住或忘掉某件事：照TA说的记，不受「只记长期信息」的限制（敏感信息仍然不记）；" +
                  "要忘掉的写成 {\"forget\": \"已记住的原句\"}。\n"
                : "") +
            "只输出 JSON 数组，没有就输出 []。\n\n" +
            $"已记住：\n{(known.Length == 0 ? "（无）" : known)}\n\n" +
            $"用户：{userText}\n宠物：{reply}";
        try
        {
            var raw = (await _llm.CompleteAsync(provider,
                new ChatRequest(null, [ChatMessage.User(prompt)], 0, 256),
                TimeSpan.FromSeconds(_settings.TimeoutSeconds))).Text;
            ApplyMemories(ParseList(raw));
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
            return JsonNode.Parse(raw[start..(end + 1)]) is JsonArray array ? ParseItems(array) : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<(string? Replace, string Text)> ParseItems(JsonArray array)
    {
        var items = new List<(string?, string)>();
        foreach (var node in array)
        {
            if (node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                items.Add((null, s));
            else if (node is JsonObject o && Str(o, "with") is { } with)
                items.Add((Str(o, "replace"), with));
            else if (node is JsonObject f && Str(f, "forget") is { } forget)
                items.Add((forget, ""));   // 文字为空 = 删掉 Replace 那条
        }
        return items;

        static string? Str(JsonObject o, string key) =>
            o[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
    }
}
