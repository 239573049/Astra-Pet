using AstraPet.Core;
using AstraPet.Pet;

namespace AstraPet.ControlPanel;

/// <summary>「状态」页里的一个性格滑块。</summary>
public sealed class TraitItem(string key, string label, Func<double> get, Action<double> set, Func<bool> overridden) : Observable
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public double Value
    {
        get => get();
        set { set(Math.Round(value)); Raise(nameof(Value)); Raise(nameof(ValueText)); }
    }
    public string ValueText => $"{Value:0}" + (overridden() ? "（已改）" : "");
    public void Refresh() { Raise(nameof(Value)); Raise(nameof(ValueText)); }
}

/// <summary>「状态」页里的一个食物按钮。</summary>
public sealed record FoodItem(string Id, string Label, bool Enabled, string Note);

/// <summary>
/// 控制台的「状态」页和「位置」页（design/pet-life/06 §5）：当前宠物的数值、亲密度、性格、口味、累计，宠物体系的开关；
/// 放置方式、爬墙、甩，以及几个试玩按钮。
/// </summary>
public sealed class LifeViewModel : Observable, IDisposable
{
    private readonly PetCompanion _companion;
    private readonly PetDirector _director;
    private readonly PetWindow _pet;
    private bool _confirmReset;
    private readonly AstraPet.Platform.PlatformCapabilities _capabilities;
    private readonly string _hotkey;

    public bool CanPerch => _capabilities.WindowSurfaces;
    public string PerchNote => CanPerch
        ? "宠物可以落在、走在其他应用窗口的顶边上；窗口移动它跟着动，窗口关了它就掉下来。只读窗口的位置，不读标题和内容。"
        : _capabilities.NoteFor(nameof(AstraPet.Platform.PlatformCapabilities.WindowSurfaces)) ?? "当前平台读不到其他窗口的位置。";
    public bool CanSummon => _capabilities.GlobalHotkey;
    public string SummonNote => CanSummon
        ? $"在任何程序里按 {_hotkey}，它会跑到光标下面来（也会重新显示出来）。"
        : _capabilities.NoteFor(nameof(AstraPet.Platform.PlatformCapabilities.GlobalHotkey)) ?? "当前平台不支持全局快捷键。";

    public LifeViewModel(PetCompanion companion, PetDirector director, PetWindow pet, PetSettings settings,
        AstraPet.Platform.PlatformCapabilities? capabilities = null, string hotkey = "")
    {
        _capabilities = capabilities ?? new AstraPet.Platform.PlatformCapabilities();
        _hotkey = hotkey;
        _companion = companion;
        _director = director;
        _pet = pet;
        Settings = settings;
        Traits = PetTraits.All.Select(t => new TraitItem(t.Key, t.Label,
            () => Life.Traits[t.Key], v => Life.SetTrait(t.Key, v), () => Life.Data.TraitOverrides.ContainsKey(t.Key))).ToList();
        _life = Life;
        _life.Changed += Refresh;
        director.LibraryChanged += Refresh;
        director.StanceChanged += OnStanceChanged;
        settings.PropertyChanged += OnSettingChanged;
    }

    private readonly PetLife _life;

    private void OnStanceChanged(string _) => Raise(nameof(StanceText));

    private void OnSettingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PetSettings.LifeEnabled) or nameof(PetSettings.LifeSpeed) or nameof(PetSettings.Placement)
            or nameof(PetSettings.AllowClimb))
            Refresh();
    }

    /// <summary>控制台释放时退订：宠物、设置都比控制台活得久，不退订会把整个控制台拴在内存里。</summary>
    public void Dispose()
    {
        _life.Changed -= Refresh;
        _director.LibraryChanged -= Refresh;
        _director.StanceChanged -= OnStanceChanged;
        Settings.PropertyChanged -= OnSettingChanged;
    }

    public PetSettings Settings { get; }
    private PetLife Life => _companion.Life;
    private LifeData Data => Life.Data;

    public bool Enabled => Settings.LifeEnabled;
    public string PetName => _director.Library.Name;

    public string Nickname
    {
        get => Data.Nickname ?? "";
        set { Life.SetNickname(value); Raise(nameof(Nickname)); }
    }

    public string Summary => $"{PetName} · 认识 {Life.DaysTogether} 天";
    public string BondHearts
    {
        get
        {
            var full = Life.Level;
            return new string('♥', full) + new string('♡', 10 - full);
        }
    }
    public string BondText => $"Lv.{Life.Level} {LifeRules.LevelName(Life.Level)}";
    public double BondProgress
    {
        get
        {
            var level = Life.Level;
            if (LifeRules.NextLevelXp(level) is not { } next) return 1;
            var from = LifeTuning.LevelXp[level - 1];
            return Math.Clamp((Data.Xp - from) / (next - from), 0, 1);
        }
    }
    public string BondXpText => LifeRules.NextLevelXp(Life.Level) is { } next
        ? $"{Data.Xp:0} / {next:0}　今天 +{Data.TodayXp:0} / {LifeTuning.DailyXpCap:0}"
        : $"已满级　今天 +{Data.TodayXp:0}";

    public double Satiety => Data.Satiety;
    public string SatietyText => $"{LifeRules.SatietyText(Data.Satiety)} · {Data.Satiety:0}";
    public double Energy => Data.Energy;
    public string EnergyText => $"{LifeRules.EnergyText(Data.Energy)} · {Data.Energy:0}";
    public string MoodText => $"{LifeRules.MoodText(Data.Mood)}（{Data.Mood:+0;-0;0}）";
    /// <summary>心情基线的组成：数值可解释（设计原则「数值可见」）。</summary>
    public string MoodWhy
    {
        get
        {
            var (value, parts) = LifeRules.MoodBaseline(Data, Life.Traits);
            return "会慢慢回到 " + value.ToString("+0;-0;0") + "：" + string.Join(" · ", parts.Select(p => $"{p.Label} {p.Delta:+0;-0;0}"));
        }
    }

    public string SnackText => Data.SnackCount == 0
        ? "还没有零食：完成一轮番茄钟专注会得到一份。"
        : "零食：" + string.Join("  ", Data.Snacks.Select(s => $"{Foods.Find(s.Key)?.Icon}{Foods.Find(s.Key)?.Name} ×{s.Value}"));

    public IReadOnlyList<FoodItem> FoodItems => Foods.All.Select(f =>
    {
        var (ok, note) = Life.FoodStatus(f);
        var star = Life.Profile.Diet.Likes.Contains(f.Id) ? "★" : "";
        return new FoodItem(f.Id, $"{f.Icon} {f.Name}{star}", ok && Enabled, note);
    }).ToList();

    public IReadOnlyList<TraitItem> Traits { get; }

    public string DietText
    {
        get
        {
            var diet = Life.Profile.Diet;
            string Names(IEnumerable<string> ids) => string.Join("、", ids.Select(id => Foods.Find(id)?.Name ?? id));
            var parts = new List<string>();
            if (diet.Likes.Count > 0) parts.Add("喜欢：" + Names(diet.Likes));
            if (diet.Dislikes.Count > 0) parts.Add("讨厌：" + Names(diet.Dislikes));
            return parts.Count == 0 ? "这只宠物的角色包没写口味，吃什么都一样开心。" : string.Join("　", parts);
        }
    }

    public string StatsText =>
        $"被摸 {Data.Stat(LifeStats.Petted)} 次 · 吃了 {Data.Stat(LifeStats.Fed)} 顿 · 聊了 {Data.Stat(LifeStats.Chats)} 轮 · " +
        $"一起专注 {Data.Stat(LifeStats.FocusMinutes) / 60.0:0.#} 小时 · 被甩 {Data.Stat(LifeStats.Thrown)} 次 · 爬墙 {Data.Stat(LifeStats.Climbs)} 次";

    /// <summary>变化速度的分段选择用字符串。</summary>
    public string Speed
    {
        get => Settings.LifeSpeed switch { 0.5 => "slow", 2 => "fast", _ => "normal" };
        set => Settings.LifeSpeed = value switch { "slow" => 0.5, "fast" => 2, _ => 1 };
    }

    public string ResetLabel => _confirmReset ? "再点一次确认重置" : "重置这只宠物的状态";

    // ───── 位置页 ─────

    public bool GroundMode => Settings.GroundMode;
    public bool CanClimb => _director.Library.Stances.Available.Contains(Stances.Wall);
    public string ClimbNote => CanClimb
        ? "走到屏幕边上时会按性格和精力决定要不要爬上去；墙上待一会儿会自己下来或松手掉下去。"
        : "这只宠物还没有贴墙动作（贴墙待机、攀爬、上墙、下墙），可以在角色工作台里生成。" +
          (_director.Library.PrunedStances.Count > 0 ? "\n" + string.Join("\n", _director.Library.PrunedStances) : "");
    public string StanceText => _director.Stance switch
    {
        Stances.Ground => "站在地上",
        Stances.Wall => "扒在墙上",
        Stances.Air => "在空中",
        _ when _director.Sleeping => "在睡觉",
        var s => s,
    };
    public bool CanSleep => _director.Library.Actions.All.Any(a => a.Enabled && a.HasRole(Roles.Sleep));

    // ───── 操作 ─────

    public void Feed(string id) => _companion.Feed(id);
    public void ResetTraits() => Life.ResetTraits();

    public void ResetLife()
    {
        if (!_confirmReset)
        {
            _confirmReset = true;
            Raise(nameof(ResetLabel));
            return;
        }
        _confirmReset = false;
        Life.Reset();
        Raise(nameof(ResetLabel));
    }

    public void GoClimb()
    {
        if (!_pet.Body.GoClimb()) _companion.Say("我还不会爬墙…", speak: false);
    }

    public void Drop() => _pet.Body.Toss(0, -900);
    public void Toss() => _pet.Body.Toss(_director.Direction * 1600, -1400);
    public void StartPlay() { if (_companion.Play.Playing) _companion.Play.Stop(); else _companion.Play.Start(); }
    public void Summon() => _companion.Summon.Summon();
    public void Sleep() => _companion.Events.Publish(_director.Sleeping ? PetEvents.Woken : PetEvents.SleepRequested);

    public void Refresh()
    {
        foreach (var name in new[]
                 {
                     nameof(Enabled), nameof(PetName), nameof(Nickname), nameof(Summary), nameof(BondHearts), nameof(BondText),
                     nameof(BondProgress), nameof(BondXpText), nameof(Satiety), nameof(SatietyText), nameof(Energy), nameof(EnergyText),
                     nameof(MoodText), nameof(MoodWhy), nameof(SnackText), nameof(FoodItems), nameof(DietText), nameof(StatsText),
                     nameof(Speed), nameof(GroundMode), nameof(CanClimb), nameof(ClimbNote), nameof(StanceText), nameof(CanSleep),
                 })
            Raise(name);
        foreach (var t in Traits) t.Refresh();
    }
}
