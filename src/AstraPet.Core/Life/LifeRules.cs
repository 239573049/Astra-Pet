namespace AstraPet.Core;

/// <summary>
/// 数值平衡的全部常量（design/pet-life/02 §3）。调平衡只改这里；不开放给角色包改。速率都是「每小时」。
/// </summary>
public static class LifeTuning
{
    public const double SatietyDecay = 6, SatietyDecayResting = 2;
    public const double EnergyDecay = 5, EnergyNightExtra = 5, EnergyRecover = 25;
    /// <summary>心情每小时缩小与基线差距的比例。</summary>
    public const double MoodReturn = 0.30;
    public const double MoodBase = 10;
    public const double HungryBelow = 30, HungryRecoverAt = 40, StarvingBelow = 10;
    public const double SleepyBelow = 20, RestedAt = 60;
    public const double FullAt = 95;
    public static readonly TimeSpan NeglectAfter = TimeSpan.FromHours(2);
    public const double NeglectPerHour = 5, NeglectMax = 30;
    public const double DailyXpCap = 60;
    public const int DailyChatXpTurns = 20;
    /// <summary>离线补算：饱腹最低降到这里（本来就更低的不动）；离线超过这么久算「好久不见」。</summary>
    public const double OfflineSatietyFloor = 35;
    public static readonly TimeSpan LongAway = TimeSpan.FromDays(1);
    public static readonly TimeSpan StarvingRepeat = TimeSpan.FromMinutes(30);
    public const int MaxSnacks = 10;

    /// <summary>每级要的累计经验，下标 0 是 1 级。</summary>
    public static readonly double[] LevelXp = [0, 60, 180, 400, 700, 1100, 1600, 2200, 3000, 4000];

    public static readonly string[] LevelNames =
        ["初次见面", "认识了", "熟悉", "朋友", "好朋友", "亲近", "很亲近", "挚友", "家人", "最重要的人"];
}

/// <summary>一次互动对数值的影响（02 §3.2 的表）。</summary>
public enum LifeInteraction { Click, Pet, Chat, Play, Focus, ThrownSoft, ThrownHard, Spammed, Woken }

/// <summary>数值穿过阈值时要发的事件（不是低于阈值期间一直发）。</summary>
public sealed record LifeCrossing(string Event, params (string Key, string Value)[] Data);

/// <summary>
/// 数值规则，全是纯函数（只改传进来的 <see cref="LifeData"/>），单元测试直接测（design/pet-life/02、06 §1）。
/// </summary>
public static class LifeRules
{
    public static int Level(double xp)
    {
        var level = 1;
        for (var i = 1; i < LifeTuning.LevelXp.Length; i++)
            if (xp >= LifeTuning.LevelXp[i]) level = i + 1;
        return level;
    }

    public static string LevelName(int level) => LifeTuning.LevelNames[Math.Clamp(level, 1, 10) - 1];

    /// <summary>下一级要的累计经验；满级返回 null。</summary>
    public static double? NextLevelXp(int level) => level >= 10 ? null : LifeTuning.LevelXp[level];

    /// <summary>心情基线及其组成（控制台「为什么」那一行）。</summary>
    public static (double Value, IReadOnlyList<(string Label, double Delta)> Parts) MoodBaseline(LifeData d, PetTraits traits)
    {
        var parts = new List<(string, double)> { ("基线", LifeTuning.MoodBase) };
        if (d.Satiety < LifeTuning.StarvingBelow) parts.Add(("很饿", -40));
        else if (d.Satiety < LifeTuning.HungryBelow) parts.Add(("有点饿", -20));
        if (d.Energy < LifeTuning.SleepyBelow) parts.Add(("很困", -15));
        if (d.NeglectHours > 0)
        {
            var neglect = Math.Min(LifeTuning.NeglectMax, LifeTuning.NeglectPerHour * d.NeglectHours * PetTraits.Factor(traits.Clingy));
            parts.Add(("被冷落", -neglect));
        }
        return (parts.Sum(p => p.Item2), parts);
    }

    /// <summary>
    /// 自然变化：过了 <paramref name="elapsed"/>。<paramref name="awake"/> 为假（睡觉 / 省电 / 暂停）时消耗慢、精力恢复；
    /// <paramref name="userPresent"/> 为假（空闲检测说用户不在，或者平台不支持）时不计冷落。<paramref name="speed"/> 是控制台的变化速度。
    /// </summary>
    public static void Advance(LifeData d, TimeSpan elapsed, bool awake, bool userPresent, DateTimeOffset localNow, PetTraits traits, double speed)
    {
        var h = Math.Max(0, elapsed.TotalHours) * speed;
        if (h <= 0) return;
        RollDay(d, localNow);

        d.Satiety = Clamp100(d.Satiety - h * (awake ? LifeTuning.SatietyDecay * PetTraits.Factor(traits.Greedy) : LifeTuning.SatietyDecayResting));
        if (awake)
        {
            var night = localNow.Hour >= 22 || localNow.Hour < 6;
            d.Energy = Clamp100(d.Energy - h * (LifeTuning.EnergyDecay * PetTraits.Factor(traits.Energetic) + (night ? LifeTuning.EnergyNightExtra : 0)));
        }
        else d.Energy = Clamp100(d.Energy + h * LifeTuning.EnergyRecover);

        if (userPresent && localNow - d.LastInteractionAt > LifeTuning.NeglectAfter)
            d.NeglectHours += Math.Max(0, elapsed.TotalHours);

        var baseline = MoodBaseline(d, traits).Value;
        var keep = Math.Pow(1 - LifeTuning.MoodReturn, h);
        d.Mood = Math.Clamp(baseline + (d.Mood - baseline) * keep, -100, 100);
    }

    /// <summary>
    /// 一次互动：改数值、加经验（受每日上限）。返回加了多少经验。冷却由调用方（PetLife）管。
    /// </summary>
    public static double Interact(LifeData d, LifeInteraction kind, DateTimeOffset localNow, PetTraits traits)
    {
        RollDay(d, localNow);
        double mood = 0, xp = 0, satiety = 0, energy = 0;
        var active = true;
        switch (kind)
        {
            case LifeInteraction.Click: mood = 2; xp = 1; d.Count(LifeStats.Clicked); break;
            case LifeInteraction.Pet: mood = 6; xp = 3; d.Count(LifeStats.Petted); break;
            case LifeInteraction.Chat:
                mood = 3;
                d.Count(LifeStats.Chats);
                if (d.TodayChats < LifeTuning.DailyChatXpTurns) { d.TodayChats++; xp = 4; }
                break;
            case LifeInteraction.Play: mood = 12; xp = 6; satiety = -3; energy = -8; d.Count(LifeStats.Played); break;
            case LifeInteraction.Focus: mood = 5; xp = 8; active = false; break;
            case LifeInteraction.ThrownSoft: mood = traits.Bold >= 50 ? 5 : -5; active = false; d.Count(LifeStats.Thrown); break;
            case LifeInteraction.ThrownHard: mood = traits.Bold >= 50 ? 0 : -12; active = false; d.Count(LifeStats.Thrown); break;
            case LifeInteraction.Spammed: mood = -8; active = false; break;
            case LifeInteraction.Woken: mood = -6; active = false; break;
        }
        d.Mood = Math.Clamp(d.Mood + mood, -100, 100);
        d.Satiety = Clamp100(d.Satiety + satiety);
        d.Energy = Clamp100(d.Energy + energy);
        if (active) Touch(d, localNow);
        return AddXp(d, xp);
    }

    public enum FeedOutcome { Ate, Full, Cooldown, NoSnack, Unknown }

    /// <summary>
    /// 喂食。<paramref name="liked"/> 返回 1 喜欢 / 0 普通 / −1 讨厌。吃不下（饱腹 ≥ 95）、冷却中、零食不够时什么都不改。
    /// </summary>
    public static FeedOutcome Feed(LifeData d, string foodId, DateTimeOffset localNow, PetProfile profile, out int liked)
    {
        liked = 0;
        if (Foods.Find(foodId) is not { } food) return FeedOutcome.Unknown;
        if (food.Snack && d.Snacks.GetValueOrDefault(foodId) <= 0) return FeedOutcome.NoSnack;
        if (!food.Snack && d.Cooldowns.TryGetValue(foodId, out var until) && until > localNow) return FeedOutcome.Cooldown;
        if (d.Satiety >= LifeTuning.FullAt) return FeedOutcome.Full;

        RollDay(d, localNow);
        liked = profile.Diet.Likes.Contains(foodId) ? 1 : profile.Diet.Dislikes.Contains(foodId) ? -1 : 0;
        var greedy = PetTraits.Factor(profile.Traits.Greedy);
        d.Satiety = Clamp100(d.Satiety + food.Satiety / profile.Diet.Appetite);
        d.Energy = Clamp100(d.Energy + 3 + food.Energy);
        var mood = liked switch { 1 => 8 * 1.5 * greedy, -1 => -10, _ => 8 * greedy } + food.Mood;
        d.Mood = Math.Clamp(d.Mood + mood, -100, 100);
        if (food.Snack)
        {
            d.Snacks[foodId] = d.Snacks[foodId] - 1;
            if (d.Snacks[foodId] <= 0) d.Snacks.Remove(foodId);
        }
        else d.Cooldowns[foodId] = localNow + food.Cooldown;
        d.Count(LifeStats.Fed);
        Touch(d, localNow);
        AddXp(d, 5);
        return FeedOutcome.Ate;
    }

    /// <summary>完成一轮专注：随机得一份零食（攒满 10 份不再加）。返回得到的零食 id，没得到返回 null。</summary>
    public static string? AwardSnack(LifeData d, Random random)
    {
        if (d.SnackCount >= LifeTuning.MaxSnacks) return null;
        var snacks = Foods.Snacks.ToList();
        var food = snacks[random.Next(snacks.Count)];
        d.Snacks[food.Id] = d.Snacks.GetValueOrDefault(food.Id) + 1;
        return food.Id;
    }

    /// <summary>
    /// 离线补算（02 §4 方案 B）：按睡眠速率降饱腹但不低于 35（本来更低的不动）；精力按恢复速率回；心情回到基线。
    /// 系统时间往回调按 0，超过 365 天按 365 天。返回离线时长。
    /// </summary>
    public static TimeSpan CatchUp(LifeData d, DateTimeOffset now, PetTraits traits)
    {
        var away = now - d.SavedAt;
        if (away < TimeSpan.Zero) away = TimeSpan.Zero;
        if (away > TimeSpan.FromDays(365)) away = TimeSpan.FromDays(365);
        var h = away.TotalHours;
        if (h <= 0) return away;
        if (d.Satiety > LifeTuning.OfflineSatietyFloor)
            d.Satiety = Math.Max(LifeTuning.OfflineSatietyFloor, d.Satiety - LifeTuning.SatietyDecayResting * h);
        d.Energy = Clamp100(d.Energy + LifeTuning.EnergyRecover * h);
        d.NeglectHours = 0;   // 不在的时候不算冷落
        if (h >= 1) d.Mood = MoodBaseline(d, traits).Value;
        d.SavedAt = now;
        return away;
    }

    /// <summary>
    /// 比较前后两份数值，返回穿过阈值的事件（02 §3.3），并更新 <paramref name="d"/> 里的阈值状态。带回差：饿了要回到 40 以上才算吃饱。
    /// </summary>
    public static List<LifeCrossing> Crossings(LifeData before, LifeData d, DateTimeOffset now)
    {
        var events = new List<LifeCrossing>();
        if (!d.Hungry && d.Satiety < LifeTuning.HungryBelow)
        {
            d.Hungry = true;
            events.Add(new(PetEvents.Hungry));
        }
        else if (d.Hungry && d.Satiety >= LifeTuning.HungryRecoverAt)
        {
            d.Hungry = false;
            d.StarvingAt = null;
            events.Add(new(PetEvents.Satisfied));
        }
        if (d.Satiety < LifeTuning.StarvingBelow && (d.StarvingAt is not { } at || now - at >= LifeTuning.StarvingRepeat))
        {
            d.StarvingAt = now;
            events.Add(new(PetEvents.Starving));
        }

        if (!d.Sleepy && d.Energy < LifeTuning.SleepyBelow)
        {
            d.Sleepy = true;
            events.Add(new(PetEvents.Sleepy));
        }
        else if (d.Sleepy && d.Energy >= LifeTuning.RestedAt)
        {
            d.Sleepy = false;
            events.Add(new(PetEvents.Rested));
        }

        if (MoodBand(before.Mood) != MoodBand(d.Mood))
            events.Add(new(PetEvents.MoodChanged, ("mood", MoodBand(d.Mood))));

        var (was, now2) = (Level(before.Xp), Level(d.Xp));
        if (now2 > was) events.Add(new(PetEvents.LevelUp, ("level", now2.ToString())));
        return events;
    }

    /// <summary>心情档位：low（&lt; −30）/ normal / happy（&gt; 40）。</summary>
    public static string MoodBand(double mood) => mood < -30 ? "low" : mood > 40 ? "happy" : "normal";

    public static string MoodText(double mood) => mood switch
    {
        < -50 => "很低落",
        < -30 => "低落",
        < 0 => "有点闷",
        <= 40 => "平常",
        <= 70 => "开心",
        _ => "超开心",
    };

    public static string SatietyText(double s) => s switch
    {
        < LifeTuning.StarvingBelow => "很饿",
        < LifeTuning.HungryBelow => "有点饿",
        < 70 => "不饿",
        _ => "很饱",
    };

    public static string EnergyText(double e) => e switch
    {
        < LifeTuning.SleepyBelow => "很困",
        < 50 => "有点累",
        _ => "精力充沛",
    };

    private static void Touch(LifeData d, DateTimeOffset now)
    {
        d.LastInteractionAt = now;
        d.NeglectHours = 0;
    }

    private static double AddXp(LifeData d, double xp)
    {
        if (xp <= 0) return 0;
        var room = Math.Max(0, LifeTuning.DailyXpCap - d.TodayXp);
        var gained = Math.Min(room, xp);
        d.TodayXp += gained;
        d.Xp += gained;
        return gained;
    }

    /// <summary>跨天：今日经验和今日对话轮数清零。</summary>
    public static void RollDay(LifeData d, DateTimeOffset localNow)
    {
        var today = localNow.ToString("yyyy-MM-dd");
        if (d.Today == today) return;
        d.Today = today;
        d.TodayXp = 0;
        d.TodayChats = 0;
    }

    private static double Clamp100(double v) => Math.Clamp(v, 0, 100);
}
