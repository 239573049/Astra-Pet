using System.ComponentModel;
using AstraPet.Core.Behaviors;

namespace AstraPet.Core;

/// <summary>
/// 宠物体系（design/pet-life/02、06 §1）：每分钟按 <see cref="LifeRules"/> 推进数值，订阅互动事件加减数值，
/// 穿过阈值时发事件（动作和台词交给触发规则），给 Director 提供随机链调制和解锁判断，按宠物存档。
/// 不直接调 Director 播动作。设置里关掉宠物体系时数值冻结，所有钩子撤掉，行为和 0.1.x 一样。
/// </summary>
public sealed class PetLife : PetBehavior
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(5);
    /// <summary>用户这么久没碰键鼠算不在电脑前（不计冷落）。</summary>
    public static readonly TimeSpan PresentWithin = TimeSpan.FromMinutes(5);

    private LifeData _data = new();
    private string? _directory;
    private PetProfile _packProfile = PetProfile.Default;
    private DateTimeOffset _lastTick;
    private readonly Queue<DateTimeOffset> _recentClicks = new();
    private DateTimeOffset _lastPetted;
    private bool _hooked;

    /// <summary>数值变了（控制台、状态浮层刷新）。</summary>
    public event Action? Changed;

    public bool Enabled => Context is not null && Settings.LifeEnabled;

    /// <summary>当前这只宠物的数值。宠物体系关着时也有（冻结的），界面自己看 <see cref="Enabled"/>。</summary>
    public LifeData Data => _data;

    public int Level => LifeRules.Level(_data.Xp);

    /// <summary>包里的性格 + 用户的覆盖。</summary>
    public PetTraits Traits
    {
        get
        {
            var traits = _packProfile.Traits;
            foreach (var (key, value) in _data.TraitOverrides) traits = traits.With(key, value);
            return traits;
        }
    }

    /// <summary>包里的档案，性格换成覆盖后的。</summary>
    public PetProfile Profile => _packProfile with { Traits = Traits };

    /// <summary>认识多少天（第一天是 1）。</summary>
    public int DaysTogether => Math.Max(1, (int)Math.Floor((Now - _data.AdoptedAt).TotalDays) + 1);

    protected override void OnStart()
    {
        Load(Context.Director.Library, announce: true);
        Context.Events.Published += OnEvent;
        Context.Director.LibraryChanged += OnLibraryChanged;
        Context.Director.StanceChanged += OnStanceChanged;
        Settings.PropertyChanged += OnSettingChanged;
        _lastTick = Now;
        Every(TickInterval, Tick);
        Every(SaveInterval, Save);
        ApplyHooks();
    }

    /// <summary>规则条件要看的状态（06 §3.1）。</summary>
    public TriggerState TriggerState() => new(
        Context.Director.Stance, Context.Director.Sleeping,
        Enabled ? Level : null,
        key => !Enabled ? null : key switch
        {
            "satiety" => _data.Satiety,
            "energy" => _data.Energy,
            "mood" => _data.Mood,
            _ => null,
        });

    // ───── 对外的操作 ─────

    /// <summary>喂一个食物（菜单、互动面板、拖食物）。结果通过事件告诉规则：pet.fed / pet.feedRefused。</summary>
    public LifeRules.FeedOutcome Feed(string foodId)
    {
        if (!Enabled) return LifeRules.FeedOutcome.Unknown;
        var before = _data.Clone();
        var outcome = LifeRules.Feed(_data, foodId, Now, Profile, out var liked);
        if (outcome == LifeRules.FeedOutcome.Ate)
            Context.Events.Publish(PetEvents.Fed, ("food", foodId), ("name", Foods.Find(foodId)!.Name),
                ("liked", liked switch { 1 => "liked", -1 => "disliked", _ => "normal" }));
        else
            Context.Events.Publish(PetEvents.FeedRefused, ("food", foodId), ("reason", outcome.ToString().ToLowerInvariant()));
        Commit(before);
        return outcome;
    }

    /// <summary>这个食物现在能不能喂，和给菜单显示的说明（冷却剩余、零食数量）。</summary>
    public (bool Available, string Note) FoodStatus(Food food)
    {
        if (food.Snack)
        {
            var count = _data.Snacks.GetValueOrDefault(food.Id);
            return (count > 0, count > 0 ? $"×{count}" : "完成番茄钟可得");
        }
        if (_data.Cooldowns.TryGetValue(food.Id, out var until) && until > Now)
            return (false, $"{Math.Ceiling((until - Now).TotalMinutes):0} 分钟后");
        return (true, "");
    }

    public void SetTrait(string key, double value)
    {
        _data.TraitOverrides[key] = Math.Clamp(value, 0, 100);
        if (Math.Abs(_packProfile.Traits[key] - value) < 0.5) _data.TraitOverrides.Remove(key);
        Changed?.Invoke();
    }

    public void ResetTraits()
    {
        _data.TraitOverrides.Clear();
        Changed?.Invoke();
    }

    public void SetNickname(string? nickname)
    {
        _data.Nickname = string.IsNullOrWhiteSpace(nickname) ? null : nickname.Trim()[..Math.Min(nickname.Trim().Length, 16)];
        Changed?.Invoke();
    }

    /// <summary>重置这只宠物的状态（数值和累计），不动动作覆盖和记忆。</summary>
    public void Reset()
    {
        _data = LifeData.New(Now);
        Save();
        Changed?.Invoke();
    }

    /// <summary>测试和控制台调试用：直接改数值（试玩里的「拉数值」，05 §1 ⑦）。</summary>
    public void Adjust(double? satiety = null, double? energy = null, double? mood = null)
    {
        var before = _data.Clone();
        if (satiety is { } s) _data.Satiety = Math.Clamp(s, 0, 100);
        if (energy is { } e) _data.Energy = Math.Clamp(e, 0, 100);
        if (mood is { } m) _data.Mood = Math.Clamp(m, -100, 100);
        Commit(before);
    }

    public void Save()
    {
        if (_directory is null) return;
        _data.SavedAt = Now;
        LifeStore.Save(_directory, _data, PetLog.Write);
    }

    // ───── 内部 ─────

    private void Load(PetLibrary library, bool announce)
    {
        _directory = library.OverlayDirectory;
        _packProfile = library.Profile;
        var loaded = _directory is null ? null : LifeStore.Load(_directory, PetLog.Write);
        if (loaded is null)
        {
            _data = LifeData.New(Now);
            return;
        }
        _data = loaded;
        var away = LifeRules.CatchUp(_data, Now, Traits);
        LifeRules.RollDay(_data, Now);
        if (announce && Settings.LifeEnabled && away >= LifeTuning.LongAway)
            Context.Ui.Post(() => Context.Events.Publish(PetEvents.Returned, ("days", ((int)away.TotalDays).ToString())));
    }

    private void OnLibraryChanged()
    {
        Save();   // 换宠物：存原来那只（按离线处理），读新那只
        Load(Context.Director.Library, announce: true);
        _lastTick = Now;
        Changed?.Invoke();
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PetSettings.LifeEnabled)) return;
        _lastTick = Now;   // 冻结期间不补算
        ApplyHooks();
        Changed?.Invoke();
    }

    private void OnStanceChanged(string stance)
    {
        if (stance == Stances.Wall) _data.Count(LifeStats.Climbs);
    }

    /// <summary>宠物体系开着时给 Director 装上随机链调制和解锁判断；关掉时撤掉，随机链和 0.1.4 完全一样。</summary>
    private void ApplyHooks()
    {
        var director = Context.Director;
        if (Settings.LifeEnabled)
        {
            director.AmbientWeight = Weight;
            director.Usable = a => a.UnlockBond <= Level;
            _hooked = true;
        }
        else if (_hooked)
        {
            director.AmbientWeight = null;
            director.Usable = null;
            _hooked = false;
        }
    }

    /// <summary>随机链权重调制（06 §3.3）。</summary>
    public double Weight(ActionDef a)
    {
        var w = 1.0;
        var moving = a.HasRole(Roles.Move) || a.HasRole(Roles.Climb);
        if (moving) w *= PetTraits.Factor(Traits.Energetic);
        if (_data.Energy < 30)
        {
            if (moving) w *= 0.3;
            if (a.HasRole(Roles.Idle)) w *= 1.5;
        }
        if (_data.Mood > 40 && a.Roles.Any(r => Roles.Emotions.Contains(r))) w *= 1.5;
        if (_data.Mood < -30)
        {
            if (a.HasRole(Roles.Happy)) w *= 0.3;
            if (a.HasRole(Roles.Sad)) w *= 2;
        }
        return w;
    }

    private void Tick()
    {
        var now = Now;
        var elapsed = now - _lastTick;
        _lastTick = now;
        if (!Enabled) return;
        if (elapsed <= TimeSpan.Zero) return;
        var before = _data.Clone();
        if (elapsed > TimeSpan.FromMinutes(10))
        {
            // 电脑睡眠、定时器被挂起：按离线补算，不当成一直醒着。
            _data.SavedAt = now - elapsed;
            LifeRules.CatchUp(_data, now, Traits);
        }
        else
        {
            var awake = !(Settings.PowerSavingActive || Settings.Paused || Context.Director.Sleeping);
            var present = Context.Monitor.IdleTime() is { } idle && idle < PresentWithin;
            LifeRules.Advance(_data, elapsed, awake, present, now, Traits, Settings.LifeSpeed);
        }
        Commit(before);
    }

    private void OnEvent(PetEvent e)
    {
        if (!Enabled) return;
        var before = _data.Clone();
        var traits = Traits;
        switch (e.Type)
        {
            case PetEvents.Clicked:
                while (_recentClicks.Count > 0 && Now - _recentClicks.Peek() > TimeSpan.FromMinutes(1)) _recentClicks.Dequeue();
                if (_recentClicks.Count >= 3) return;
                _recentClicks.Enqueue(Now);
                LifeRules.Interact(_data, LifeInteraction.Click, Now, traits);
                break;
            case PetEvents.Petted:
                if (Now - _lastPetted < TimeSpan.FromSeconds(30)) return;
                _lastPetted = Now;
                LifeRules.Interact(_data, LifeInteraction.Pet, Now, traits);
                break;
            case PetEvents.ChatReplied:
                LifeRules.Interact(_data, LifeInteraction.Chat, Now, traits);
                break;
            case PetEvents.PlayEnded:
                LifeRules.Interact(_data, LifeInteraction.Play, Now, traits);
                break;
            case PetEvents.FocusEnded:
                LifeRules.Interact(_data, LifeInteraction.Focus, Now, traits);
                _data.Count(LifeStats.FocusMinutes, Settings.PomodoroWork);
                if (LifeRules.AwardSnack(_data, Context.Random) is { } snack && Foods.Find(snack) is { } food)
                    Context.Events.Publish(PetEvents.SnackEarned, ("food", snack), ("name", food.Name), ("icon", food.Icon));
                break;
            case PetEvents.Landed when e["thrown"] == "true":
                LifeRules.Interact(_data, e["impact"] == "hard" ? LifeInteraction.ThrownHard : LifeInteraction.ThrownSoft, Now, traits);
                break;
            case PetEvents.Spammed:
                LifeRules.Interact(_data, LifeInteraction.Spammed, Now, traits);
                break;
            case PetEvents.Woken:
                LifeRules.Interact(_data, LifeInteraction.Woken, Now, traits);
                break;
            default:
                return;
        }
        Commit(before);
    }

    /// <summary>改完数值：发阈值事件、通知界面。</summary>
    private void Commit(LifeData before)
    {
        foreach (var crossing in LifeRules.Crossings(before, _data, Now))
            Context.Events.Publish(crossing.Event, crossing.Data);
        Changed?.Invoke();
    }

    public override void Dispose()
    {
        if (Context is not null)
        {
            Save();
            Context.Events.Published -= OnEvent;
            Context.Director.LibraryChanged -= OnLibraryChanged;
            Context.Director.StanceChanged -= OnStanceChanged;
            Settings.PropertyChanged -= OnSettingChanged;
            if (_hooked)
            {
                Context.Director.AmbientWeight = null;
                Context.Director.Usable = null;
            }
        }
        base.Dispose();
    }
}
