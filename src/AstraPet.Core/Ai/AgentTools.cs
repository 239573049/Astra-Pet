using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AstraPet.Core;

namespace AstraPet.Ai;

/// <summary>工具执行的结果，作为 <see cref="ToolResultPart"/> 写回历史。</summary>
public sealed record ToolResult(string Content, bool IsError = false)
{
    public static readonly ToolResult Done = new("已执行");
}

/// <summary>
/// 给内置 Agent 的一项能力（03 §1.1）。<c>play_action</c> 是第一个；以后有副作用的工具（开番茄钟、设提醒）
/// <see cref="IsPresentational"/> 为 false，执行前要过用户确认，并且角色包的人设和触发规则不能引用它们。
/// </summary>
public interface IAgentTool
{
    string Name { get; }

    /// <summary>名字、描述、JSON Schema；返回 null 表示当前不可用（比如没有可选动作）。</summary>
    ToolDefinition? Definition();

    /// <summary>只影响表现、没有副作用：模型已经说了话时，执行后不需要把结果再喂给模型。</summary>
    bool IsPresentational { get; }

    Task<ToolResult> InvokeAsync(JsonElement args, CancellationToken ct);
}

/// <summary>
/// <c>play_action</c>：让宠物做一个动作。动作清单写在工具描述里，id 用 enum 约束；
/// 只列启用的、Agent 选了有意义的动作（不列待机、拖拽、移动、转身、思考这些由系统控制的），最多 30 个，另加心情。
/// </summary>
public sealed class PlayActionTool(Func<ActionSet?> actions, Action<string> play, Func<ActionDef, bool>? usable = null) : IAgentTool
{
    public const string ToolName = "play_action";
    public const int MaxActions = 30;

    /// <summary>只有这些角色的动作不给 Agent 选。</summary>
    private static readonly HashSet<string> SystemRoles =
        [Roles.Idle, Roles.Drag, Roles.Move, Roles.Turn, Roles.Think, Roles.Sleep, Roles.Talk,
         Roles.Fall, Roles.Land, Roles.Climb, Roles.Hungry, Roles.Yawn, Roles.Eat];

    /// <summary>Agent 可以直接填的心情（程序按角色和回退链挑动作），和中文说明。</summary>
    public static readonly IReadOnlyList<(string Role, string Label)> Moods =
    [
        (Roles.Happy, "开心"), (Roles.Shy, "害羞"), (Roles.Sad, "难过"), (Roles.Surprised, "惊讶"),
        (Roles.Confused, "疑惑"), (Roles.Affection, "撒娇、喜欢"), (Roles.Angry, "生气"),
        (Roles.Greet, "打招呼"), (Roles.Celebrate, "庆祝"),
    ];

    public string Name => ToolName;
    public bool IsPresentational => true;

    /// <summary>给 Agent 选的动作（按包里的顺序）。</summary>
    /// <param name="usable">
    /// 现在能不能做（design/pet-life/06 §4.2）：在当前姿态（或能走过去），并且亲密度解锁了。null 时只看是不是地面上的普通动作。
    /// </param>
    public static IReadOnlyList<ActionDef> Choosable(ActionSet set, Func<ActionDef, bool>? usable = null) =>
        set.All.Where(a => a.Enabled && a.Transition is null && a.Roles.Any(r => !SystemRoles.Contains(r))
                           && (usable?.Invoke(a) ?? a.Stance == Stances.Ground))
            .Take(MaxActions).ToList();

    /// <summary>enum 里允许的全部值：动作 id + 心情。</summary>
    public IReadOnlyList<string> Allowed()
    {
        if (actions() is not { } set) return [];
        var ids = Choosable(set, usable).Select(a => a.Id).ToList();
        return ids.Concat(Moods.Select(m => m.Role).Where(m => !ids.Contains(m))).ToList();
    }

    public ToolDefinition? Definition()
    {
        if (actions() is not { } set) return null;
        var list = Choosable(set, usable);
        var sb = new StringBuilder("让你自己在用户的桌面上做一个动作，配合你说的话和心情。每次回复最多调用一次，并且照常用文字回复用户；不需要动作时不调用。");
        if (list.Count > 0)
        {
            sb.Append("\n可用动作：");
            foreach (var a in list)
            {
                sb.Append("\n- ").Append(a.Id).Append('：').Append(a.Description.Length > 0 ? a.Description : a.DisplayName);
                var moods = a.Roles.Select(r => Moods.FirstOrDefault(m => m.Role == r).Label).Where(l => l is not null).ToList();
                if (moods.Count > 0) sb.Append('（').Append(string.Join("、", moods)).Append('）');
            }
        }
        sb.Append("\n也可以填心情 ").Append(string.Join("、", Moods.Select(m => m.Role)))
          .Append("，程序会挑一个合适的动作。");

        var allowed = new JsonArray();
        foreach (var id in Allowed()) allowed.Add((JsonNode)JsonValue.Create(id)!);
        var parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["id"] = new JsonObject { ["type"] = "string", ["enum"] = allowed, ["description"] = "动作 id 或心情" },
            },
            ["required"] = new JsonArray("id"),
            ["additionalProperties"] = false,
        };
        return new ToolDefinition(ToolName, sb.ToString(), parameters);
    }

    public Task<ToolResult> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var id = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("id", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!.Trim()
            : "";
        if (!Allowed().Contains(id))
            return Task.FromResult(new ToolResult($"没有这个动作：{id}", IsError: true));
        play(id);
        return Task.FromResult(ToolResult.Done);
    }
}

/// <summary>
/// <c>remember</c>：让宠物自己往长期记忆里记一条关于用户的信息、改一条或删一条。
/// 用户说「记住…」「忘掉…」时用；宠物自己从聊天里发现值得记的事也可以用（主动记忆）。
/// 没开记忆时不提供。执行后不需要再把结果喂回模型（它已经在同一次回复里说了话），所以算「表现类」。
/// </summary>
public sealed class MemoryTool(Func<bool> enabled, Func<string, string?, MemoryChange> remember, Func<string, string?> forget) : IAgentTool
{
    public const string ToolName = "remember";

    public string Name => ToolName;
    public bool IsPresentational => true;

    public ToolDefinition? Definition()
    {
        if (!enabled()) return null;
        var parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["action"] = new JsonObject
                {
                    ["type"] = "string", ["enum"] = new JsonArray("remember", "forget"),
                    ["description"] = "remember 记下或更新；forget 删掉一条",
                },
                ["text"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "remember：以「用户」开头的一句简短陈述，如「用户养了一只橘猫叫团子」；forget：要删的那句（照抄已记住的原句）",
                },
                ["replaces"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "可选。要改写某条已记住的内容时填那条原句（照抄），text 是改写后的样子：信息有变化（搬家、换工作），或者只想忘掉那条里的一部分、留下其余的",
                },
            },
            ["required"] = new JsonArray("action", "text"),
            ["additionalProperties"] = false,
        };
        return new ToolDefinition(ToolName,
            "把关于用户的一条信息记进你的长期记忆，或删掉一条。用户让你记住、别忘了、忘掉什么时一定要调用；" +
            "你自己从聊天里发现用户重要又长期有效的信息（喜好、习惯、身份、重要的人和日子、目标、近期计划）时也可以主动记。" +
            "不要记一次性的闲聊、已经记得的内容，也不要记密码、证件号、银行卡号、住址门牌号、健康诊断这类敏感信息。" +
            "怎么记由你决定：一句记一件事还是几件相关的事合成一句、要不要改写或合并已有的，都按你觉得以后最好用的方式来。" +
            "用户只想忘掉某条里的一部分时，用 remember + replaces 把那条改写成剩下的内容，而不是整条删掉。" +
            "调用后照常用文字回复，可以自然地提一句记住了。", parameters);
    }

    public Task<ToolResult> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        string? Arg(string name) =>
            args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()!.Trim() is { Length: > 0 } s ? s : null
                : null;
        var text = Arg("text");
        if (text is null) return Task.FromResult(new ToolResult("text 不能为空", IsError: true));
        if (Arg("action") == "forget")
            return Task.FromResult(forget(text) is { } removed
                ? new ToolResult($"已忘掉：{removed}")
                : new ToolResult("没找到这条（或者有好几条对得上）。要删请照抄【关于用户，你记得】里的原句"));
        return Task.FromResult(remember(text, Arg("replaces")) switch
        {
            MemoryChange.Added => new ToolResult($"已记住：{text}"),
            MemoryChange.Replaced => new ToolResult($"已更新：{text}"),
            _ => new ToolResult("这条已经记得了"),
        });
    }
}

/// <summary>记一条记忆的结果。</summary>
public enum MemoryChange { Unchanged, Added, Replaced }
