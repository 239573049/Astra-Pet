using System.Text.Json;

namespace AstraPet.Core;

/// <summary>五个性格特质，0～100，默认 50（design/pet-life/01 §2）。</summary>
public sealed record PetTraits(double Energetic = 50, double Clingy = 50, double Greedy = 50, double Bold = 50, double Curious = 50)
{
    public static readonly IReadOnlyList<(string Key, string Label, string High, string Low)> All =
    [
        ("energetic", "活泼", "很活泼", "爱发呆"),
        ("clingy", "黏人", "很黏人", "挺独立"),
        ("greedy", "贪吃", "很贪吃", "吃得少"),
        ("bold", "胆量", "胆子大", "胆子有点小"),
        ("curious", "好奇", "很好奇", "不太好奇"),
    ];

    public double this[string key] => key switch
    {
        "energetic" => Energetic,
        "clingy" => Clingy,
        "greedy" => Greedy,
        "bold" => Bold,
        "curious" => Curious,
        _ => 50,
    };

    /// <summary>特质换成倍率：0 → 0.5×，50 → 1×，100 → 1.5×。</summary>
    public static double Factor(double trait) => 0.5 + Math.Clamp(trait, 0, 100) / 100;

    public PetTraits With(string key, double value)
    {
        value = Math.Clamp(value, 0, 100);
        return key switch
        {
            "energetic" => this with { Energetic = value },
            "clingy" => this with { Clingy = value },
            "greedy" => this with { Greedy = value },
            "bold" => this with { Bold = value },
            "curious" => this with { Curious = value },
            _ => this,
        };
    }

    /// <summary>给系统提示词的一句话：只写偏离默认值明显（≤30 或 ≥70）的特质。</summary>
    public string Describe()
    {
        var parts = All.Select(t => this[t.Key] >= 70 ? t.High : this[t.Key] <= 30 ? t.Low : null).Where(p => p is not null).ToList();
        return parts.Count == 0 ? "" : string.Join("、", parts!);
    }
}

/// <summary>
/// 身体：画布坐标（640×360、角色朝左）。默认值是小t 的经验值，和工作台合成首尾帧的规则一致（头顶 y≈60、脚底 y≈330）。
/// </summary>
public sealed record PetBodyMetrics
{
    /// <summary>站姿的不透明包围盒。</summary>
    public double X0 { get; init; } = 214;
    public double Y0 { get; init; } = 60;
    public double X1 { get; init; } = 426;
    public double Y1 { get; init; } = 330;
    public double HeadRatio { get; init; } = 0.30;
    public double FeetRatio { get; init; } = 0.15;
    /// <summary>站在地面上时贴着地面的那条线。</summary>
    public double FeetY { get; init; } = 330;
    /// <summary>嘴的位置：喂食时食物飞到这里。</summary>
    public double MouthX { get; init; } = 290;
    public double MouthY { get; init; } = 136;
    /// <summary>
    /// 贴墙姿态下接触墙的竖线（朝左时在左边）。默认值是工作台的版式：贴墙关键帧左缘在画面 22% 处，
    /// 按画面锁定映射到画布是 320 − 0.28 × 1280 × (270 / 468) ≈ 113（design/pet-life/05 §1 ③⑥）。
    /// </summary>
    public double WallContactX { get; init; } = 113;
    /// <summary>贴墙姿态下身体最低点，上下墙时用它对齐地面。关键帧最低点对齐 85% 地面线，映射到画布就是 330。</summary>
    public double WallFeetY { get; init; } = 330;
    /// <summary>倒挂姿态下贴着天花板的横线。关键帧顶边对齐画面 12%，映射到画布约 27。</summary>
    public double CeilingContactY { get; init; } = 27;

    public double Width => X1 - X0;
    public double Height => Y1 - Y0;
}

/// <summary>物理参数（design/pet-life/01 §4）。</summary>
public sealed record PetPhysics(double Weight = 1, double Bounce = 0.25, double WalkSpeed = 1, double ClimbSpeed = 1, bool Fragile = false);

/// <summary>口味：喜欢 / 讨厌的内置食物 id，食量。</summary>
public sealed record PetDiet(IReadOnlyList<string> Likes, IReadOnlyList<string> Dislikes, double Appetite = 1)
{
    public static readonly PetDiet Default = new([], []);
}

/// <summary>
/// 宠物档案：pet.json 的 profile（design/pet-life/01）。全部可选，缺了用默认值；数值超范围夹到范围内，不拒绝加载。
/// </summary>
public sealed record PetProfile
{
    public static readonly PetProfile Default = new();

    public string Species { get; init; } = "humanoid";
    public PetTraits Traits { get; init; } = new();
    public PetBodyMetrics Body { get; init; } = new();
    public PetPhysics Physics { get; init; } = new();
    public PetDiet Diet { get; init; } = PetDiet.Default;

    public static readonly IReadOnlyList<string> SpeciesList = ["humanoid", "quadruped", "flying", "blob", "other"];

    public static PetProfile Parse(JsonElement? manifest, Action<string>? log = null)
    {
        if (manifest is not { ValueKind: JsonValueKind.Object } m || !m.TryGetProperty("profile", out var p) || p.ValueKind != JsonValueKind.Object)
            return Default;
        try
        {
            var species = ActionManifest.Str(p, "species");
            var physicsDefaults = DefaultPhysics(SpeciesList.Contains(species) ? species : "humanoid");
            return new PetProfile
            {
                Species = SpeciesList.Contains(species) ? species : "humanoid",
                Traits = ParseTraits(p),
                Body = ParseBody(p, log),
                Physics = ParsePhysics(p, physicsDefaults),
                Diet = ParseDiet(p),
            };
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            log?.Invoke($"档案写错了，用默认值：{e.Message}");
            return Default;
        }
    }

    public static PetPhysics DefaultPhysics(string species) => species switch
    {
        "flying" => new PetPhysics(Weight: 0.5, Bounce: 0.2),
        "blob" => new PetPhysics(Bounce: 0.6),
        "quadruped" => new PetPhysics(Weight: 1.2),
        _ => new PetPhysics(),
    };

    private static double Num(JsonElement e, string name, double fallback, double min, double max) =>
        e.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? Math.Clamp(d, min, max) : fallback;

    private static PetTraits ParseTraits(JsonElement p)
    {
        if (!p.TryGetProperty("traits", out var t) || t.ValueKind != JsonValueKind.Object) return new PetTraits();
        return new PetTraits(Num(t, "energetic", 50, 0, 100), Num(t, "clingy", 50, 0, 100), Num(t, "greedy", 50, 0, 100),
            Num(t, "bold", 50, 0, 100), Num(t, "curious", 50, 0, 100));
    }

    private static PetBodyMetrics ParseBody(JsonElement p, Action<string>? log)
    {
        var d = new PetBodyMetrics();
        if (!p.TryGetProperty("body", out var b) || b.ValueKind != JsonValueKind.Object) return d;
        var body = d;
        if (b.TryGetProperty("bounds", out var bounds) && bounds.ValueKind == JsonValueKind.Array && bounds.GetArrayLength() == 4)
        {
            var v = bounds.EnumerateArray().Select(x => x.TryGetDouble(out var n) ? n : double.NaN).ToArray();
            var area = (v[2] - v[0]) * (v[3] - v[1]);
            if (v.All(double.IsFinite) && v[0] >= 0 && v[1] >= 0 && v[2] <= PetClip.Width && v[3] <= PetClip.Height
                && v[2] > v[0] && v[3] > v[1] && area >= PetClip.Width * PetClip.Height * 0.05 && area <= PetClip.Width * PetClip.Height * 0.95)
                body = body with { X0 = v[0], Y0 = v[1], X1 = v[2], Y1 = v[3], FeetY = v[3] };
            else
                log?.Invoke("档案里的 body.bounds 不合理，忽略");
        }
        body = body with
        {
            HeadRatio = Num(b, "headRatio", body.HeadRatio, 0.05, 0.6),
            FeetRatio = Num(b, "feetRatio", body.FeetRatio, 0.05, 0.5),
            FeetY = Num(b, "feetY", body.FeetY, 1, PetClip.Height),
        };
        if (b.TryGetProperty("mouth", out var mouth) && mouth.ValueKind == JsonValueKind.Array && mouth.GetArrayLength() == 2
            && mouth[0].TryGetDouble(out var mx) && mouth[1].TryGetDouble(out var my))
            body = body with { MouthX = Math.Clamp(mx, 0, PetClip.Width), MouthY = Math.Clamp(my, 0, PetClip.Height) };
        if (b.TryGetProperty("ceiling", out var ceiling) && ceiling.ValueKind == JsonValueKind.Object)
            body = body with { CeilingContactY = Num(ceiling, "contactY", body.CeilingContactY, 0, PetClip.Height) };
        if (b.TryGetProperty("wall", out var wall) && wall.ValueKind == JsonValueKind.Object)
            body = body with
            {
                WallContactX = Num(wall, "contactX", body.WallContactX, 0, PetClip.Width),
                WallFeetY = Num(wall, "feetY", body.WallFeetY, 1, PetClip.Height),
            };
        return body;
    }

    private static PetPhysics ParsePhysics(JsonElement p, PetPhysics d)
    {
        if (!p.TryGetProperty("physics", out var f) || f.ValueKind != JsonValueKind.Object) return d;
        return new PetPhysics(
            Num(f, "weight", d.Weight, 0.3, 3), Num(f, "bounce", d.Bounce, 0, 0.8),
            Num(f, "walkSpeed", d.WalkSpeed, 0.3, 3), Num(f, "climbSpeed", d.ClimbSpeed, 0.3, 3),
            f.TryGetProperty("fragile", out var fr) ? fr.ValueKind == JsonValueKind.True : d.Fragile);
    }

    private static PetDiet ParseDiet(JsonElement p)
    {
        if (!p.TryGetProperty("diet", out var d) || d.ValueKind != JsonValueKind.Object) return PetDiet.Default;
        // 不认识的食物 id 忽略：第三方包可能用了新版程序的食物。
        var likes = ActionManifest.Strings(d, "likes").Where(Foods.Exists).Distinct().ToList();
        var dislikes = ActionManifest.Strings(d, "dislikes").Where(f => Foods.Exists(f) && !likes.Contains(f)).Distinct().ToList();
        return new PetDiet(likes, dislikes, Num(d, "appetite", 1, 0.3, 3));
    }
}
