namespace AstraPet.Core;

/// <summary>oneshot 播一遍；loop 循环到被打断或结束条件；phased 进入 → 循环 → 退出。</summary>
public enum ActionKind { OneShot, Loop, Phased }

/// <summary>谁能打断这个动作。拖拽总能打断。</summary>
public enum InterruptPolicy { Any, User, Never }

/// <summary>Pack：角色包里的，只能禁用；User：用户新增的，可以删。</summary>
public enum ActionOrigin { Pack, User }

/// <summary>分段动作的三段。</summary>
public sealed record ActionPhases(PetClip Enter, PetClip Loop, PetClip Exit);

/// <summary>过渡动作：播完之后姿态从 <see cref="From"/> 变成 <see cref="To"/>（design/pet-life/04 §3）。</summary>
public sealed record StanceTransition(string From, string To);

/// <summary>
/// 一句台词，<see cref="Bond"/> 是要求的最低亲密度等级（1 表示不限）。
/// pet.json 里可以写成字符串，也可以写成 {"bond": 5, "text": "…"}（design/pet-life/02 §5）。
/// </summary>
public sealed record GatedLine(string Text, int Bond = 1);

/// <summary>
/// 一个动作：稳定的 id + 一个或多个片段（播放时随机抽一个）+ 给人和 Agent 看的说明 + 播放属性。
/// 不可变，是角色包定义和用户覆盖层合并后的结果。设计见 design/pet-studio/01-action-model.md §3。
/// </summary>
public sealed record ActionDef
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";
    /// <summary>给 Agent 看的一句话说明。格式 1 合成的动作是空的。</summary>
    public string Description { get; init; } = "";
    public IReadOnlyList<string> Roles { get; init; } = [];
    public IReadOnlyList<PetClip> Clips { get; init; } = [];
    public ActionKind Kind { get; init; } = ActionKind.OneShot;
    /// <summary>朝右时能不能镜像；带文字的片段是 false。</summary>
    public bool Mirror { get; init; } = true;
    /// <summary>随机链权重，0 表示空闲时不会自己播，只能被显式触发。</summary>
    public double Ambient { get; init; }
    /// <summary>同一动作两次自动触发的最小间隔；用户直接操作不受限。</summary>
    public TimeSpan Cooldown { get; init; }
    public InterruptPolicy Interrupt { get; init; } = InterruptPolicy.Any;
    /// <summary>播放时随机冒一句的台词，可以为空。</summary>
    public IReadOnlyList<string> Lines { get; init; } = [];
    /// <summary>只有 move 角色的动作有：窗口跟着走的步幅和曲线。</summary>
    public Gait? Gait { get; init; }
    public ActionPhases? Phases { get; init; }
    /// <summary>只能在这个姿态下播（design/pet-life/04 §3）。默认站在地面上。</summary>
    public string Stance { get; init; } = Stances.Ground;
    /// <summary>不为空时这是一个过渡动作：从 From 姿态开始，播完变成 To 姿态。</summary>
    public StanceTransition? Transition { get; init; }
    /// <summary>贴墙时接触墙的那条竖线（画布坐标，朝左）；null 用档案里的默认值。</summary>
    public double? ContactX { get; init; }
    /// <summary>需要的亲密度等级，没到之前不进随机链、不给 Agent 选（design/pet-life/02 §5）。1 表示不限。</summary>
    public int UnlockBond { get; init; } = 1;
    /// <summary>只在亲密度够了才说的台词（不限等级的在 <see cref="Lines"/> 里）。</summary>
    public IReadOnlyList<GatedLine> BondLines { get; init; } = [];
    public ActionOrigin Origin { get; init; } = ActionOrigin.Pack;
    public bool Enabled { get; init; } = true;

    public bool HasRole(string role) => Roles.Contains(role);

    /// <summary>当前能说的台词：不限等级的 + 亲密度够了的。</summary>
    public IReadOnlyList<string> LinesFor(int bondLevel) =>
        BondLines.Count == 0 ? Lines : Lines.Concat(BondLines.Where(l => l.Bond <= bondLevel).Select(l => l.Text)).ToList();

    /// <summary>显示用的名字：没名字就用 id。</summary>
    public string DisplayName => Name.Length > 0 ? Name : Id;

    public PetClip PickClip(Random random) => Clips[random.Next(Clips.Count)];

    /// <summary>动作 id 和角色名的规则：[a-z0-9-]，1～32 个字符，不以 - 开头。</summary>
    public static bool IsValidId(string? id)
    {
        if (id is not { Length: > 0 and <= 32 } || id[0] == '-')
            return false;
        foreach (var c in id)
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
                return false;
        return true;
    }
}
