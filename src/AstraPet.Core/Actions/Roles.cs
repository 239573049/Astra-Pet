namespace AstraPet.Core;

/// <summary>
/// 语义角色：程序按角色找动作（「拖拽时播哪个」「开心时播哪个」）。一个动作可以有多个角色。
/// 格式 1 的分类名就是角色名。设计见 design/pet-studio/01-action-model.md §2。
/// </summary>
public static class Roles
{
    public const string Idle = "idle";
    public const string Click = "click";
    public const string Drag = "drag";
    public const string Move = "move";
    public const string Turn = "turn";
    public const string Sing = "sing";
    public const string Greet = "greet";
    public const string Sleep = "sleep";
    public const string Think = "think";
    public const string Talk = "talk";
    public const string Celebrate = "celebrate";
    public const string Drink = "drink";
    public const string Stretch = "stretch";
    public const string Happy = "happy";
    public const string Shy = "shy";
    public const string Sad = "sad";
    public const string Angry = "angry";
    public const string Surprised = "surprised";
    public const string Confused = "confused";
    public const string Affection = "affection";
    // 宠物体系（design/pet-life）
    public const string Eat = "eat";
    public const string Hungry = "hungry";
    public const string Yawn = "yawn";
    public const string Dizzy = "dizzy";
    public const string Fall = "fall";
    public const string Land = "land";
    public const string Pounce = "pounce";
    public const string Climb = "climb";
    public const string Curious = "curious";

    /// <summary>每个都至少要有一个启用的动作，缺了不能加载（D14）。</summary>
    public static readonly IReadOnlyList<string> Required = [Idle, Click, Drag, Move, Turn];

    /// <summary>给 Agent 用的情绪角色，<c>play_action</c> 可以直接传这些名字。</summary>
    public static readonly IReadOnlyList<string> Emotions = [Happy, Shy, Sad, Angry, Surprised, Confused, Affection];

    /// <summary>
    /// 回退链：这个角色没有动作时依次找哪些角色。没列出来的角色（think、talk、sleep 和用户自定义的）没有回退，
    /// 找不到就什么都不播。click 是必需角色，所以以 click 结尾的链一定能找到。
    /// </summary>
    private static readonly Dictionary<string, string[]> Fallbacks = new()
    {
        [Sing] = [Happy, Click],
        [Greet] = [Happy, Click],
        [Celebrate] = [Happy, Click],
        [Affection] = [Happy, Click],
        [Drink] = [Click],
        [Stretch] = [Click],
        [Happy] = [Click],
        [Shy] = [Click],
        [Surprised] = [Click],
        [Confused] = [Click],
        [Angry] = [Click],
        [Sad] = [Idle],
        [Eat] = [Happy, Click],
        [Hungry] = [Sad, Idle],
        [Yawn] = [Stretch, Idle],
        [Dizzy] = [Surprised, Click],
        [Fall] = [Drag],
        [Land] = [Idle],
        [Pounce] = [Happy, Click],
        [Curious] = [Confused, Click],
    };

    /// <summary>
    /// 当前姿态下找不到时，会去别的姿态找（Director 先播过渡动作走过去）的角色：喂食、扑、睡觉这类「要换个地方才能做」的事。
    /// 其他角色（单击反应等）跨姿态时不自动切姿态，用当前姿态的待机代替（design/pet-life/04 §3.3）。
    /// </summary>
    public static readonly IReadOnlySet<string> Navigable = new HashSet<string> { Eat, Pounce, Sleep, Land };

    /// <summary>角色本身，然后是它的回退链。</summary>
    public static IEnumerable<string> Chain(string role)
    {
        yield return role;
        if (Fallbacks.TryGetValue(role, out var fallbacks))
            foreach (var r in fallbacks)
                yield return r;
    }

    /// <summary>
    /// 按动作的第一个角色给的默认随机链权重，是这个角色的「总权重」，由该角色的所有动作平分。
    /// 和 0.1.4 的随机链一致：待机 30 / 转向 10 / 动作 40 / 移动 20。
    /// </summary>
    public static double DefaultAmbient(string role, string stance) => stance == Stances.Ground
        ? DefaultAmbient(role)
        : role switch { Idle => 40, Climb => 30, _ => 0 };

    public static double DefaultAmbient(string role) => role switch
    {
        Idle => 30,
        Turn => 10,
        Click => 40,
        Move => 20,
        _ => 0,
    };
}
