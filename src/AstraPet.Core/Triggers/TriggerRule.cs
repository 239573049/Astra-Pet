using System.Text.Json.Serialization;

namespace AstraPet.Core;

/// <summary>
/// 一条触发规则：收到 <see cref="On"/> 事件、条件满足时，请求播 <see cref="Do"/> 并可选说一句话。
/// 存在设置（全局规则）和覆盖层（只对某只宠物）里，JSON 字段名和 design/pet-studio/02-triggers.md §3.1 一致。
/// </summary>
public sealed class TriggerRule
{
    /// <summary>说话时从设置里取台词的占位符。</summary>
    public const string ClickLinesToken = "$ClickLines";
    public const string PettingLinesToken = "$PettingLines";
    /// <summary><see cref="Do"/> 取事件数据里的 action（Agent 的 play_action）。</summary>
    public const string EventActionToken = "$action";
    public const string None = "none";

    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary>事件类型；末尾 * 通配：pomodoro.*。</summary>
    [JsonPropertyName("on")] public string On { get; set; } = "";
    /// <summary>动作 id、role:&lt;角色&gt;、$action 或 none。</summary>
    [JsonPropertyName("do")] public string Do { get; set; } = None;
    [JsonPropertyName("say")] public List<string>? Say { get; set; }
    /// <summary>说的话是否朗读（还要用户开了朗读提醒）。</summary>
    [JsonPropertyName("speak")] public bool Speak { get; set; } = true;
    [JsonPropertyName("chance")] public double Chance { get; set; } = 1;
    /// <summary>秒。</summary>
    [JsonPropertyName("cooldown")] public double Cooldown { get; set; }
    [JsonPropertyName("priority")] public string Priority { get; set; } = "system";
    /// <summary>循环播放，直到收到其中一个事件。</summary>
    [JsonPropertyName("until")] public List<string>? Until { get; set; }
    [JsonPropertyName("when")] public TriggerWhen? When { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    /// <summary>命中后同一事件的后续规则不再执行（design/pet-life/06 §3.2，比如睡着时单击只醒来）。</summary>
    [JsonPropertyName("final")] public bool Final { get; set; }

    /// <summary><see cref="Do"/> 的前缀：走到某个姿态（design/pet-life/04 §3.3）。</summary>
    public const string StancePrefix = "stance:";
    /// <summary>说话时展开成这一级亲密度的解锁台词。</summary>
    public const string BondLinesToken = "$BondLines";

    [JsonIgnore]
    public ActionPriority PriorityValue => Priority switch
    {
        "ambient" => ActionPriority.Ambient,
        "agent" => ActionPriority.Agent,
        "user" => ActionPriority.User,
        _ => ActionPriority.System,
    };

    public bool Matches(string eventType) =>
        On.EndsWith('*') ? eventType.StartsWith(On[..^1], StringComparison.Ordinal) : On == eventType;

    public TriggerRule Clone() => new()
    {
        Id = Id, On = On, Do = Do, Say = Say?.ToList(), Speak = Speak, Chance = Chance, Cooldown = Cooldown,
        Priority = Priority, Until = Until?.ToList(), When = When?.Clone(), Enabled = Enabled, Final = Final,
    };
}

/// <summary>规则的条件，全部可选，AND 关系。</summary>
public sealed class TriggerWhen
{
    /// <summary>本地时间 [起, 止)，可以跨午夜：[22, 6]。</summary>
    [JsonPropertyName("hours")] public int[]? Hours { get; set; }
    [JsonPropertyName("powerSaving")] public bool? PowerSaving { get; set; }
    [JsonPropertyName("music")] public bool? Music { get; set; }
    /// <summary>某个布尔设置为真时才生效（见 <see cref="TriggerConditions.Settings"/>）。包里的规则不能用。</summary>
    [JsonPropertyName("setting")] public string? Setting { get; set; }
    /// <summary>事件数据逐项相等。</summary>
    [JsonPropertyName("event")] public Dictionary<string, string>? Event { get; set; }
    /// <summary>数值落在 [低, 高)：satiety / energy 0..100，mood −100..100（design/pet-life/06 §3.1）。宠物体系关着时不成立。</summary>
    [JsonPropertyName("need")] public Dictionary<string, double[]>? Need { get; set; }
    /// <summary>亲密度等级在 [低, 高]。宠物体系关着时不成立。</summary>
    [JsonPropertyName("bond")] public int[]? Bond { get; set; }
    /// <summary>当前姿态是其中之一。</summary>
    [JsonPropertyName("stance")] public List<string>? Stance { get; set; }
    /// <summary>放置方式：ground / free。</summary>
    [JsonPropertyName("placement")] public string? Placement { get; set; }
    /// <summary>在睡觉（当前姿态的待机带 sleep 角色）。</summary>
    [JsonPropertyName("sleeping")] public bool? Sleeping { get; set; }

    public TriggerWhen Clone() => new()
    {
        Hours = Hours?.ToArray(), PowerSaving = PowerSaving, Music = Music, Setting = Setting,
        Event = Event is null ? null : new Dictionary<string, string>(Event),
        Need = Need?.ToDictionary(p => p.Key, p => p.Value.ToArray()), Bond = Bond?.ToArray(), Stance = Stance?.ToList(),
        Placement = Placement, Sleeping = Sleeping,
    };
}

/// <summary>规则条件要看的宠物状态。<see cref="BondLevel"/> 和 <see cref="Need"/> 在宠物体系关着时为 null。</summary>
public sealed record TriggerState(string Stance, bool Sleeping, int? BondLevel, Func<string, double?> Need);

public static class TriggerConditions
{
    /// <summary>规则能引用的设置开关。不用反射，NativeAOT 下也能用。</summary>
    public static readonly IReadOnlyDictionary<string, Func<PetSettings, bool>> Settings = new Dictionary<string, Func<PetSettings, bool>>
    {
        [nameof(PetSettings.ClickReact)] = s => s.ClickReact,
        [nameof(PetSettings.ClickBubble)] = s => s.ClickBubble,
        [nameof(PetSettings.Petting)] = s => s.Petting,
        [nameof(PetSettings.DailyGreeting)] = s => s.DailyGreeting,
        [nameof(PetSettings.HourlyChime)] = s => s.HourlyChime,
        [nameof(PetSettings.MusicDetect)] = s => s.MusicDetect,
        [nameof(PetSettings.StayStill)] = s => s.StayStill,
        [nameof(PetSettings.AllowClimb)] = s => s.AllowClimb && s.GroundMode,
        [nameof(PetSettings.LifeEnabled)] = s => s.LifeEnabled,
        ["NeedBubbles"] = s => s.NeedHints == "bubble",
    };

    public static bool Hold(TriggerWhen? when, PetEvent e, PetSettings settings, DateTimeOffset localNow) =>
        Hold(when, e, settings, localNow, null);

    /// <param name="state">宠物现在的状态（数值、姿态）；null 时和数值、姿态有关的条件都不成立。</param>
    public static bool Hold(TriggerWhen? when, PetEvent e, PetSettings settings, DateTimeOffset localNow, TriggerState? state)
    {
        if (when is null) return true;
        if (when.Placement is { Length: > 0 } placement && settings.Placement != placement) return false;
        if (when.Stance is { Count: > 0 } stances && (state is null || !stances.Contains(state.Stance))) return false;
        if (when.Sleeping is { } sleeping && (state?.Sleeping ?? false) != sleeping) return false;
        if (when.Bond is [var lo, var hi] && (state?.BondLevel is not { } level || level < lo || level > hi)) return false;
        if (when.Need is { Count: > 0 } needs)
            foreach (var (key, range) in needs)
                if (range is not [var min, var max] || state?.Need(key) is not { } value || value < min || value >= max)
                    return false;
        if (when.Hours is [var from, var to] && !InHours(localNow.Hour, from, to)) return false;
        if (when.PowerSaving is { } saving && settings.PowerSavingActive != saving) return false;
        if (when.Music is { } music && settings.MusicPlaying != music) return false;
        if (when.Setting is { Length: > 0 } name
            && !(Settings.TryGetValue(name, out var get) && get(settings))) return false;
        if (when.Event is { } data)
            foreach (var (key, value) in data)
                if (e[key] != value) return false;
        return true;
    }

    /// <summary>[from, to)，from &gt; to 时跨午夜；from == to 表示全天。</summary>
    public static bool InHours(int hour, int from, int to) =>
        from == to || (from < to ? hour >= from && hour < to : hour >= from || hour < to);
}
