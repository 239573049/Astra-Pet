namespace AstraPet.Core;

/// <summary>
/// 姿态：宠物现在以什么方式待在哪（design/pet-life/04 §3）。动作只能在自己的姿态下播，换姿态靠过渡动作。
/// 姿态名是字符串：除了下面这些，分段动作（kind: phased）展开后会用动作 id 当姿态名（比如 nap）。
/// </summary>
public static class Stances
{
    /// <summary>站在一个水平表面上。所有没写 stance 的动作都属于它。</summary>
    public const string Ground = "ground";
    /// <summary>贴在屏幕左 / 右边上。</summary>
    public const string Wall = "wall";
    public const string Sit = "sit";
    public const string Lie = "lie";
    public const string Ceiling = "ceiling";

    /// <summary>物理姿态：下落 / 被甩飞，位置由物理积分决定，不由随机链进入。过渡动作可以以它为终点（松手）。</summary>
    public const string Air = "air";

    public static bool IsValid(string? name) => ActionDef.IsValidId(name);
}
