using System.Text.Json;
using AstraPet.Core;

namespace AstraPet.Ai;

/// <summary>
/// 角色人设：格式 2 的 pet.json 里的 <c>persona</c>（07 §2），进系统提示词的【你的性格】一段（03 §3.3）。
/// 角色包里的人设是第三方文本，每个字段和总长都有上限，超了截断。
/// </summary>
public sealed record PetPersona
{
    public const int MaxField = 200;
    public const int MaxTotal = 800;
    public const int MaxListItems = 8;

    public string Name { get; init; } = "";
    public string Identity { get; init; } = "";
    public string Personality { get; init; } = "";
    public string Speech { get; init; } = "";
    public IReadOnlyList<string> Likes { get; init; } = [];
    public IReadOnlyList<string> Catchphrases { get; init; } = [];
    public string Greeting { get; init; } = "";

    /// <summary>格式 1 的角色（包里没有人设）用的通用人设：名字取角色显示名，性格是 0.1.4 默认提示词的意思。</summary>
    public static PetPersona Generic(string name) => new()
    {
        Name = string.IsNullOrWhiteSpace(name) ? "Astra" : name.Trim(),
        Personality = "温暖、俏皮，关心用户",
        Speech = "说话简短、温暖，带一点俏皮",
    };

    /// <summary>从 pet.json 的 persona 读；没有或不是对象时返回 null。</summary>
    public static PetPersona? Parse(JsonElement manifest)
    {
        if (manifest.ValueKind != JsonValueKind.Object
            || !manifest.TryGetProperty("persona", out var p) || p.ValueKind != JsonValueKind.Object)
            return null;
        var persona = new PetPersona
        {
            Name = Field(ActionManifest.Str(p, "name"), 40),
            Identity = Field(ActionManifest.Str(p, "identity")),
            Personality = Field(ActionManifest.Str(p, "personality")),
            Speech = Field(ActionManifest.Str(p, "speech")),
            Likes = List(ActionManifest.Strings(p, "likes")),
            Catchphrases = List(ActionManifest.Strings(p, "catchphrases")),
            Greeting = Field(ActionManifest.Str(p, "greeting")),
        };
        return persona.Name.Length == 0 ? null : persona.Capped();
    }

    /// <summary>总长超过 <see cref="MaxTotal"/> 时从后往前砍：口头禅、喜好、说话风格……名字不动。</summary>
    private PetPersona Capped()
    {
        var p = this;
        while (p.Length > MaxTotal)
        {
            if (p.Catchphrases.Count > 0) p = p with { Catchphrases = p.Catchphrases.Take(p.Catchphrases.Count - 1).ToList() };
            else if (p.Likes.Count > 0) p = p with { Likes = p.Likes.Take(p.Likes.Count - 1).ToList() };
            else if (p.Greeting.Length > 0) p = p with { Greeting = "" };
            else if (p.Speech.Length > 0) p = p with { Speech = "" };
            else if (p.Identity.Length > 0) p = p with { Identity = "" };
            else p = p with { Personality = p.Personality[..Math.Max(0, MaxTotal - p.Name.Length)] };
        }
        return p;
    }

    public int Length => Name.Length + Identity.Length + Personality.Length + Speech.Length + Greeting.Length
                         + Likes.Sum(l => l.Length) + Catchphrases.Sum(c => c.Length);

    private static string Field(string text, int max = MaxField) =>
        text.ReplaceLineEndings(" ") is var t && t.Length <= max ? t : t[..max];

    private static IReadOnlyList<string> List(IEnumerable<string> items) =>
        items.Select(i => Field(i, 40)).Where(i => i.Length > 0).Take(MaxListItems).ToList();
}
