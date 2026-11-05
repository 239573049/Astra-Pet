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
