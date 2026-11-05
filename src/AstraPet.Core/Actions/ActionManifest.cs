using System.Text.Json;

namespace AstraPet.Core;

/// <summary>
/// 从素材得到动作：格式 1（分类文件夹）按文件合成，格式 2 读 pet.json 的 actions。
/// 设计见 design/pet-studio/01-action-model.md §3、§5 和 07-package-format.md §2。
/// </summary>
public static class ActionManifest
{
    public const int MaxDescription = 60;
    public const int MaxLine = 60;
    public const int MaxLines = 20;

    /// <summary>
    /// 格式 1：每个片段合成一个动作，id 是 <c>&lt;分类&gt;-&lt;序号&gt;</c>（按文件名排序，从 1 开始，所以稳定），
    /// 名字是文件名，角色是分类。随机链权重按分类的总权重平分给其中的动作，和 0.1.4「先抽分类、再等概率抽片段」等价。
    /// </summary>
    public static List<ActionDef> FromCategories(
        IEnumerable<(string Category, IReadOnlyList<PetClip> Clips)> categories,
        IReadOnlyDictionary<string, Gait> gaits,
        IReadOnlySet<string> noMirror)
    {
        var actions = new List<ActionDef>();
        foreach (var (category, clips) in categories)
        {
            for (var i = 0; i < clips.Count; i++)
            {
                var clip = clips[i];
                actions.Add(new ActionDef
                {
                    Id = $"{category}-{i + 1}",
                    Name = clip.Name,
                    Roles = [category],
                    Clips = [clip],
                    Mirror = !noMirror.Contains(clip.Name),
                    Ambient = Roles.DefaultAmbient(category) / clips.Count,
                    Gait = gaits.GetValueOrDefault(clip.Name),
                });
            }
        }
        return actions;
    }

    /// <summary>
    /// 格式 2：解析 pet.json 里的 actions。片段路径相对 <paramref name="root"/>，不能跑出去。
    /// 单个动作写错了跳过并记日志，不让整只宠物加载失败；必需角色是否齐全由调用方检查。
    /// </summary>
    public static List<ActionDef> Parse(JsonElement actions, string root, IClipDecoder decoder, Action<string>? log = null)
    {
        var result = new List<ActionDef>();
        var ambientGiven = new List<bool>();
        if (actions.ValueKind != JsonValueKind.Array) return result;

        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var seen = new HashSet<string>();
        foreach (var item in actions.EnumerateArray())
        {
            try
            {
                if (item.ValueKind != JsonValueKind.Object) throw new FormatException("不是对象");
                var id = Str(item, "id");
                if (!ActionDef.IsValidId(id)) throw new FormatException($"id 不合法：「{id}」");
                if (!seen.Add(id)) throw new FormatException($"id 重复：{id}");

                var transition = ParseTransition(item);
                var roles = Strings(item, "roles").Where(ActionDef.IsValidId).Distinct().ToList();
                var kind = Str(item, "kind") switch { "loop" => ActionKind.Loop, "phased" => ActionKind.Phased, _ => ActionKind.OneShot };
                if (roles.Count == 0 && transition is null) throw new FormatException($"{id} 没有角色");

                PetClip Probe(string rel)
                {
                    var full = Path.GetFullPath(Path.Combine(root, rel));
                    if (!full.StartsWith(fullRoot, StringComparison.Ordinal) || rel.Contains('\\'))
                        throw new FormatException($"{id} 的片段路径不合法：{rel}");
                    if (!File.Exists(full)) throw new FormatException($"{id} 的片段不存在：{rel}");
                    var clip = decoder.Probe(full, roles.Count > 0 ? roles[0] : id);
                    log?.Invoke($"{id}/{clip.Name}: {clip.Fps:0.##} fps, 约 {clip.EstimatedFrames} 帧");
                    return clip;
                }

                var stance = Str(item, "stance") is { Length: > 0 } st ? st : Stances.Ground;
                if (!Stances.IsValid(stance)) throw new FormatException($"{id} 的姿态不合法：{stance}");
                var hasAmbient = item.TryGetProperty("ambient", out var amb) && amb.TryGetDouble(out _);
                var (lines, bondLines) = ParseLines(item);
                var baseDef = new ActionDef
                {
                    Id = id,
                    Name = Str(item, "name"),
                    Description = Clip(Str(item, "description"), MaxDescription),
                    Roles = roles,
                    Kind = kind == ActionKind.Phased ? ActionKind.OneShot : kind,
                    Mirror = !(item.TryGetProperty("mirror", out var m) && m.ValueKind == JsonValueKind.False),
                    Ambient = hasAmbient ? Math.Max(0, amb.GetDouble()) : 0,
                    Cooldown = TimeSpan.FromSeconds(item.TryGetProperty("cooldown", out var cd) && cd.TryGetDouble(out var s) ? Math.Max(0, s) : 0),
                    Interrupt = Str(item, "interrupt") switch { "user" => InterruptPolicy.User, "never" => InterruptPolicy.Never, _ => InterruptPolicy.Any },
                    Lines = lines,
                    BondLines = bondLines,
                    Gait = item.TryGetProperty("gait", out var g) ? ParseGait(g) : null,
                    Stance = transition?.From ?? stance,
                    Transition = transition,
                    ContactX = item.TryGetProperty("anchor", out var an) && an.ValueKind == JsonValueKind.Object
                               && an.TryGetProperty("contactX", out var cx) && cx.TryGetDouble(out var cxv) ? cxv : null,
                    UnlockBond = item.TryGetProperty("unlock", out var un) && un.ValueKind == JsonValueKind.Object
                                 && un.TryGetProperty("bond", out var ub) && ub.TryGetInt32(out var ubv) ? Math.Clamp(ubv, 1, 10) : 1,
                };

                if (kind == ActionKind.Phased)
                {
                    // 分段动作是语法糖：展开成「进入」过渡、以动作 id 为名的姿态里的循环（这个姿态的待机）、「退出」过渡（04 §3.2）。
                    if (!item.TryGetProperty("phases", out var ph) || ph.ValueKind != JsonValueKind.Object)
                        throw new FormatException($"{id} 是分段动作但没有 phases");
                    var enter = Probe(Str(ph, "enter"));
                    var loop = Probe(Str(ph, "loop"));
                    var exit = Probe(Str(ph, "exit"));
                    var from = baseDef.Stance;
                    result.Add(baseDef with
                    {
                        Id = Trim(id, "-in"), Clips = [enter], Transition = new StanceTransition(from, id), Stance = from,
                        Name = baseDef.DisplayName + "（进入）",
                    });
                    ambientGiven.Add(hasAmbient);
                    result.Add(baseDef with
                    {
                        Clips = [loop], Stance = id, Transition = null, Roles = roles.Prepend(Roles.Idle).Distinct().ToList(),
                        Ambient = 30, Gait = null,
                    });
                    ambientGiven.Add(true);
                    result.Add(baseDef with
                    {
                        Id = Trim(id, "-out"), Clips = [exit], Stance = id, Transition = new StanceTransition(id, from), Roles = [],
                        Ambient = 4, Gait = null, Lines = [], BondLines = [], Name = baseDef.DisplayName + "（退出）",
                    });
                    ambientGiven.Add(true);
                    continue;
                }

                var clips = Strings(item, "clips").Select(Probe).ToList();
                if (clips.Count == 0) throw new FormatException($"{id} 没有片段");
                result.Add(baseDef with { Clips = clips });
                ambientGiven.Add(hasAmbient);
            }
            catch (Exception e) when (e is FormatException or InvalidOperationException or IOException)
            {
                log?.Invoke($"跳过动作：{e.Message}");
            }
        }

        // 没写 ambient 的动作用默认值：按（姿态，第一个角色）的总权重，由同一组里没写的动作平分。过渡动作默认 0。
        static string Key(ActionDef a) => a.Stance + "/" + (a.Roles.Count > 0 ? a.Roles[0] : "");
        var counts = result.Where((a, i) => !ambientGiven[i] && a.Transition is null && a.Roles.Count > 0)
            .GroupBy(Key).ToDictionary(g => g.Key, g => g.Count());
        for (var i = 0; i < result.Count; i++)
            if (!ambientGiven[i])
                result[i] = result[i] with
                {
                    Ambient = result[i].Transition is null && result[i].Roles.Count > 0
                        ? Roles.DefaultAmbient(result[i].Roles[0], result[i].Stance) / counts[Key(result[i])]
                        : 0,
                };
        return result;
    }

    private static string Trim(string id, string suffix) => (id.Length + suffix.Length <= 32 ? id : id[..(32 - suffix.Length)]) + suffix;

    /// <summary>{"from": "ground", "to": "wall"}；没有或写错返回 null。</summary>
    private static StanceTransition? ParseTransition(JsonElement item)
    {
        if (!item.TryGetProperty("transition", out var t) || t.ValueKind != JsonValueKind.Object) return null;
        var from = Str(t, "from");
        var to = Str(t, "to");
        if (!Stances.IsValid(from) || !Stances.IsValid(to) || from == to)
            throw new FormatException($"过渡写错了：{from} → {to}");
        return new StanceTransition(from, to);
    }

    /// <summary>lines 里的字符串是不限等级的台词，{"bond": n, "text": "…"} 是要亲密度 n 才说的。</summary>
    private static (IReadOnlyList<string> Lines, IReadOnlyList<GatedLine> BondLines) ParseLines(JsonElement item)
    {
        var plain = new List<string>();
        var gated = new List<GatedLine>();
        if (item.TryGetProperty("lines", out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var x in v.EnumerateArray())
            {
                if (x.ValueKind == JsonValueKind.String) plain.Add(x.GetString()!);
                else if (x.ValueKind == JsonValueKind.Object && Str(x, "text") is { Length: > 0 } text)
                {
                    var bond = x.TryGetProperty("bond", out var b) && b.TryGetInt32(out var bv) ? Math.Clamp(bv, 1, 10) : 1;
                    if (bond <= 1) plain.Add(text);
                    else if (gated.Count < MaxLines) gated.Add(new GatedLine(Clip(text, MaxLine), bond));
                }
            }
        return (CleanLines(plain), gated);
    }

    /// <summary>{"stride": 240, "curve": [0, 0.01, …], "axis": "y"}；没有 stride 返回 null。</summary>
    public static Gait? ParseGait(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("stride", out var stride) || !stride.TryGetDouble(out var s))
            return null;
        var curve = e.TryGetProperty("curve", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Number).Select(v => v.GetDouble()).ToArray()
            : null;
        var axis = Str(e, "axis") == "y" ? GaitAxis.Y : GaitAxis.X;
        return new Gait(s, curve, axis);
    }

    public static IReadOnlyList<string> CleanLines(IEnumerable<string> lines) =>
        lines.Select(l => Clip(l.Trim(), MaxLine)).Where(l => l.Length > 0).Take(MaxLines).ToList();

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];

    internal static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";

    internal static IEnumerable<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).ToList()
            : [];
}
