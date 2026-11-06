using System.Text.Json;
using System.Text.Json.Nodes;
using AstraPet.Ai;
using AstraPet.Core;
using AstraPet.Core.Behaviors;
using Microsoft.Extensions.Time.Testing;

namespace AstraPet.Tests;

/// <summary>
/// 一个格式 2 的角色包：五个必需角色 + 吃东西 + 墙组（贴墙待机、攀爬、上墙、下墙、松手）+ 分段的打瞌睡。
/// 可以去掉其中几个，看姿态图怎么处理残缺的组。
/// </summary>
internal sealed class StanceLibrary : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "astra-stance-" + Guid.NewGuid().ToString("N"));

    public StanceLibrary(IEnumerable<string>? omit = null, JsonObject? profile = null, bool ceiling = false, bool land = false)
    {
        var skip = (omit ?? []).ToHashSet();
        var actions = new JsonArray();
        void Add(string id, JsonObject def, params string[] clips)
        {
            if (skip.Contains(id)) return;
            def["id"] = id;
            if (!def.ContainsKey("phases")) def["clips"] = new JsonArray(clips.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray());
            actions.Add((JsonNode)def);
            foreach (var c in clips) Touch(c);
        }
        Add("breathe", new() { ["roles"] = new JsonArray("idle") }, "clips/breathe.webm");
        Add("wave", new() { ["roles"] = new JsonArray("click", "happy") }, "clips/wave.webm");
        Add("lifted", new() { ["roles"] = new JsonArray("drag") }, "clips/lifted.webm");
        Add("run", new() { ["roles"] = new JsonArray("move"), ["gait"] = new JsonObject { ["stride"] = 240 } }, "clips/run.webm");
        Add("look", new() { ["roles"] = new JsonArray("turn") }, "clips/look.webm");
        Add("munch", new() { ["roles"] = new JsonArray("eat") }, "clips/munch.webm");
        Add("wall-idle", new() { ["roles"] = new JsonArray("idle"), ["stance"] = "wall" }, "clips/wall-idle.webm");
        Add("climb", new() { ["roles"] = new JsonArray("climb"), ["stance"] = "wall", ["kind"] = "oneshot",
                             ["gait"] = new JsonObject { ["stride"] = 100, ["axis"] = "y" } }, "clips/climb.webm");
        Add("wall-enter", new() { ["transition"] = new JsonObject { ["from"] = "ground", ["to"] = "wall" } }, "clips/wall-enter.webm");
        Add("wall-exit", new() { ["transition"] = new JsonObject { ["from"] = "wall", ["to"] = "ground" } }, "clips/wall-exit.webm");
        Add("let-go", new() { ["transition"] = new JsonObject { ["from"] = "wall", ["to"] = "air" } }, "clips/let-go.webm");
        if (land) Add("land", new() { ["roles"] = new JsonArray("land") }, "clips/land.webm");
        if (ceiling)
        {
            Add("ceiling-idle", new() { ["roles"] = new JsonArray("idle"), ["stance"] = "ceiling" }, "clips/ceiling-idle.webm");
            Add("ceiling-crawl", new() { ["roles"] = new JsonArray("climb"), ["stance"] = "ceiling",
                                         ["gait"] = new JsonObject { ["stride"] = 200 } }, "clips/ceiling-crawl.webm");
            Add("ceiling-enter", new() { ["transition"] = new JsonObject { ["from"] = "wall", ["to"] = "ceiling" } }, "clips/ceiling-enter.webm");
            Add("ceiling-drop", new() { ["transition"] = new JsonObject { ["from"] = "ceiling", ["to"] = "air" } }, "clips/ceiling-drop.webm");
        }
        if (!skip.Contains("nap"))
        {
            actions.Add((JsonNode)new JsonObject
            {
                ["id"] = "nap", ["roles"] = new JsonArray("sleep"), ["kind"] = "phased",
                ["phases"] = new JsonObject { ["enter"] = "clips/nap-in.webm", ["loop"] = "clips/nap.webm", ["exit"] = "clips/nap-out.webm" },
            });
            Touch("clips/nap-in.webm");
            Touch("clips/nap.webm");
            Touch("clips/nap-out.webm");
        }
        var manifest = new JsonObject { ["format"] = 2, ["id"] = "stance-test", ["name"] = "测试", ["actions"] = actions };
        if (profile is not null) manifest["profile"] = profile;
        File.WriteAllText(Path.Combine(Dir, "pet.json"), manifest.ToJsonString());
    }

    private void Touch(string rel)
    {
        var path = Path.Combine(Dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
    }

    public PetLibrary Load(string? overlay = null, FakeDecoder? decoder = null) =>
        PetLibrary.Load(Dir, decoder ?? new FakeDecoder(), overlayDirectory: overlay);

    public void Dispose()
    {
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }
}

public class StanceGraphTests : IDisposable
{
    private readonly List<StanceLibrary> _libs = new();

    private PetLibrary Load(params string[] omit)
    {
        var lib = new StanceLibrary(omit);
        _libs.Add(lib);
        return lib.Load();
    }

    public void Dispose()
    {
        foreach (var l in _libs) l.Dispose();
    }

    [Fact]
    public void Complete_wall_group_is_available_and_phased_actions_expand()
    {
        var lib = Load();
        Assert.Contains(Stances.Wall, lib.Stances.Available);
        Assert.Contains("nap", lib.Stances.Available);
        var enter = lib.Actions.Find("nap-in")!;
        Assert.Equal(new StanceTransition(Stances.Ground, "nap"), enter.Transition);
        Assert.Equal("nap", lib.Actions.Find("nap")!.Stance);
        Assert.True(lib.Actions.Find("nap")!.HasRole(Roles.Idle));
        Assert.Equal(new StanceTransition("nap", Stances.Ground), lib.Actions.Find("nap-out")!.Transition);
        Assert.Equal(GaitAxis.Y, lib.Actions.Find("climb")!.Gait!.Axis);
    }

    [Fact]
    public void Wall_group_without_entry_is_pruned()
    {
        var lib = Load("wall-enter");
        Assert.DoesNotContain(Stances.Wall, lib.Stances.Available);
        Assert.Null(lib.Actions.Find("wall-idle"));
        Assert.Null(lib.Actions.Find("climb"));
        Assert.NotNull(lib.Actions.Find("breathe"));
    }

    [Fact]
    public void Wall_without_way_down_is_pruned_but_letting_go_is_enough()
    {
        Assert.DoesNotContain(Stances.Wall, Load("wall-exit", "let-go").Stances.Available);
        Assert.Contains(Stances.Wall, Load("wall-exit").Stances.Available);
    }

    [Fact]
    public void Wall_without_idle_is_pruned()
    {
        var lib = Load("wall-idle");
        Assert.DoesNotContain(Stances.Wall, lib.Stances.Available);
        Assert.Null(lib.Actions.Find("climb"));
    }

    [Fact]
    public void Path_goes_through_transitions()
    {
        var lib = Load();
        Assert.Equal(["wall-enter"], lib.Stances.Path(Stances.Ground, Stances.Wall)!.Select(a => a.Id));
        Assert.Equal(["wall-exit"], lib.Stances.Path(Stances.Wall, Stances.Ground)!.Select(a => a.Id));
        Assert.Equal(["wall-exit", "nap-in"], lib.Stances.Path(Stances.Wall, "nap")!.Select(a => a.Id));
        Assert.Empty(lib.Stances.Path(Stances.Ground, Stances.Ground)!);
        // 下墙不能用时（离地面太远），只能松手。
        Assert.Null(lib.Stances.Path(Stances.Wall, Stances.Ground, t => t.Transition!.To != Stances.Ground));
        Assert.Equal(["let-go"], lib.Stances.Path(Stances.Wall, Stances.Air)!.Select(a => a.Id));
    }

    [Fact]
    public void Required_roles_only_count_ground_actions()
    {
        var lib = Load();
        Assert.Empty(lib.Actions.MissingRequired());
        // 墙上的 idle 不算地面的 idle。
        Assert.Equal(["breathe"], lib.Actions.WithRole(Roles.Idle).Select(a => a.Id));
        Assert.Equal(["wall-idle"], lib.Actions.WithRole(Roles.Idle, Stances.Wall).Select(a => a.Id));
    }
}

public class StanceDirectorTests : IDisposable
{
    private readonly StanceLibrary _dir = new();
    private readonly PetSettings _settings = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero));
    private readonly PetDirector _director;

    public StanceDirectorTests()
    {
        _director = new PetDirector(_dir.Load(), _settings, new Random(3), _time);
        _director.Start();
    }

    public void Dispose() => _dir.Dispose();

    private void PlayOut()
    {
        for (var i = 0; i < 3; i++) _director.Tick();
    }

    private ActionDef A(string id) => _director.Library.Actions.Find(id)!;

    [Fact]
    public void Requesting_a_wall_action_plays_the_transition_first()
    {
        Assert.Equal(PlayResult.Played, _director.Request(new ActionRequest(A("climb"), ActionPriority.User, "test")));
        Assert.Equal("wall-enter", _director.CurrentAction.Id);
        Assert.Equal(Stances.Ground, _director.Stance);
        Assert.Equal(Stances.Wall, _director.NextStance);
        PlayOut();
        _director.Tick();
        Assert.Equal(Stances.Wall, _director.Stance);
        Assert.Equal("climb", _director.CurrentAction.Id);
    }

    [Fact]
    public void Can_enter_hook_blocks_the_wall()
    {
        _director.CanEnter = s => s != Stances.Wall;
        Assert.Equal(PlayResult.Rejected, _director.RequestStance(Stances.Wall, ActionPriority.User, "test"));
        Assert.Equal(PlayResult.Rejected, _director.Request(new ActionRequest(A("climb"), ActionPriority.User, "test")));
    }

    [Fact]
    public void On_the_wall_ambient_only_picks_wall_actions_and_clicks_do_not_climb_down()
    {
        _director.RequestStance(Stances.Wall, ActionPriority.User, "test");
        PlayOut();
        _director.Tick();
        Assert.Equal(Stances.Wall, _director.Stance);
        for (var i = 0; i < 40; i++)
        {
            PlayOut();
            _director.Tick();
            Assert.True(_director.CurrentAction.Stance == Stances.Wall || _director.CurrentAction.Transition?.From == Stances.Wall,
                $"墙上播了 {_director.CurrentAction.Id}");
            if (_director.Stance != Stances.Wall) break;   // 随机抽到了下墙 / 松手
        }
        // 单击反应：墙上没有 click，不自动下墙。
        var walled = new PetDirector(_director.Library, _settings, new Random(1), _time);
        walled.Start();
        walled.RequestStance(Stances.Wall, ActionPriority.User, "t");
        for (var i = 0; i < 4; i++) walled.Tick();
        Assert.Null(walled.Library.Actions.Resolve("role:click", new Random(1), Stances.Wall));
    }

    [Fact]
    public void Eating_on_the_wall_climbs_down_first()
    {
        _director.RequestStance(Stances.Wall, ActionPriority.User, "test");
        PlayOut();
        _director.Tick();
        var eat = _director.Library.Actions.Resolve("role:eat", new Random(1), Stances.Wall)!;
        Assert.Equal("munch", eat.Id);
        _director.Request(new ActionRequest(eat, ActionPriority.User, "test"));
        Assert.Equal("wall-exit", _director.CurrentAction.Id);
        PlayOut();
        _director.Tick();
        Assert.Equal("munch", _director.CurrentAction.Id);
        Assert.Equal(Stances.Ground, _director.Stance);
    }

    [Fact]
    public void Letting_go_falls_and_landing_returns_to_ground()
    {
        _director.RequestStance(Stances.Wall, ActionPriority.User, "test");
        PlayOut();
        _director.Tick();
        _director.RequestStance(Stances.Air, ActionPriority.User, "test");
        Assert.Equal("let-go", _director.CurrentAction.Id);
        PlayOut();
        _director.Tick();
        Assert.True(_director.Airborne);
        Assert.True(_director.CurrentAction.HasRole(Roles.Drag));   // 没有 fall 角色，回退到 drag
        // 空中来的 agent 请求排队，落地后播。
        Assert.Equal(PlayResult.Queued, _director.Request(new ActionRequest(A("wave"), ActionPriority.Agent, "agent")));
        _director.Land();
        Assert.Equal(Stances.Ground, _director.Stance);
        PlayOut();
        _director.Tick();
        Assert.Equal("wave", _director.CurrentAction.Id);
    }

    [Fact]
    public void Sleeping_is_detected_and_role_sleep_navigates()
    {
        var sleep = _director.Library.Actions.Resolve("role:sleep", new Random(1), Stances.Ground)!;
        Assert.Equal("nap-in", sleep.Id);
        _director.Request(new ActionRequest(sleep, ActionPriority.User, "test"));
        PlayOut();
        _director.Tick();
        Assert.Equal("nap", _director.Stance);
        Assert.True(_director.Sleeping);
        _director.RequestStance(Stances.Ground, ActionPriority.User, "wake");
        Assert.Equal("nap-out", _director.CurrentAction.Id);
    }

    [Fact]
    public void Ambient_weight_and_usable_hooks_filter_the_chain()
    {
        _director.Usable = a => a.Id != "wave";
        _director.AmbientWeight = a => a.HasRole(Roles.Move) ? 0 : 1;
        _director.Start();   // 丢掉装钩子之前预取的那一段
        for (var i = 0; i < 60; i++)
        {
            PlayOut();
            _director.Tick();
            Assert.NotEqual("wave", _director.CurrentAction.Id);
            Assert.NotEqual("run", _director.CurrentAction.Id);
        }
    }
}

public class LifeRulesTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 5, 12, 0, 0, TimeSpan.FromHours(8));
    private static readonly PetTraits Neutral = new();

    [Fact]
    public void Satiety_drops_six_per_hour_awake_and_two_resting()
    {
        var d = LifeData.New(Noon);
        d.Satiety = 80;
        LifeRules.Advance(d, TimeSpan.FromHours(1), awake: true, userPresent: false, Noon, Neutral, 1);
        Assert.Equal(74, d.Satiety, 3);
        LifeRules.Advance(d, TimeSpan.FromHours(1), awake: false, userPresent: false, Noon, Neutral, 1);
        Assert.Equal(72, d.Satiety, 3);
        // 贪吃的饿得快；速度 2× 翻倍。
        var greedy = LifeData.New(Noon);
        greedy.Satiety = 80;
        LifeRules.Advance(greedy, TimeSpan.FromHours(1), true, false, Noon, new PetTraits(Greedy: 100), 2);
        Assert.Equal(80 - 6 * 1.5 * 2, greedy.Satiety, 3);
    }

    [Fact]
    public void Energy_drops_faster_at_night_and_recovers_while_resting()
    {
        var night = new DateTimeOffset(2026, 11, 5, 23, 0, 0, TimeSpan.FromHours(8));
        var d = LifeData.New(night);
        d.Energy = 50;
        LifeRules.Advance(d, TimeSpan.FromHours(1), true, false, night, Neutral, 1);
        Assert.Equal(40, d.Energy, 3);
        LifeRules.Advance(d, TimeSpan.FromHours(1), false, false, night, Neutral, 1);
        Assert.Equal(65, d.Energy, 3);
    }

    [Fact]
    public void Neglect_only_counts_while_the_user_is_present()
    {
        var d = LifeData.New(Noon);
        var later = Noon.AddHours(3);
        LifeRules.Advance(d, TimeSpan.FromHours(1), true, userPresent: false, later, Neutral, 1);
        Assert.Equal(0, d.NeglectHours);
        LifeRules.Advance(d, TimeSpan.FromHours(1), true, userPresent: true, later, Neutral, 1);
        Assert.Equal(1, d.NeglectHours, 3);
        Assert.Contains(LifeRules.MoodBaseline(d, Neutral).Parts, p => p.Label == "被冷落");
        LifeRules.Interact(d, LifeInteraction.Pet, later, Neutral);
        Assert.Equal(0, d.NeglectHours);
    }

    [Fact]
    public void Daily_xp_is_capped_and_resets_after_midnight()
    {
        var d = LifeData.New(Noon);
        for (var i = 0; i < 100; i++) LifeRules.Interact(d, LifeInteraction.Pet, Noon, Neutral);
        Assert.Equal(LifeTuning.DailyXpCap, d.Xp);
        Assert.Equal(2, LifeRules.Level(d.Xp));
        LifeRules.Interact(d, LifeInteraction.Pet, Noon.AddDays(1), Neutral);
        Assert.Equal(LifeTuning.DailyXpCap + 3, d.Xp);
        Assert.Equal(3, d.TodayXp);
    }

    [Fact]
    public void Chat_xp_only_counts_twenty_turns_a_day()
    {
        var d = LifeData.New(Noon);
        for (var i = 0; i < 25; i++) LifeRules.Interact(d, LifeInteraction.Chat, Noon, Neutral);
        Assert.Equal(20, d.TodayChats);
        Assert.Equal(25, d.Stat(LifeStats.Chats));
        Assert.Equal(Math.Min(80, LifeTuning.DailyXpCap), d.Xp);
    }

    [Fact]
    public void Feeding_respects_cooldowns_snacks_and_fullness()
    {
        var profile = PetProfile.Default with { Diet = new PetDiet(["fish"], ["vegetable"]) };
        var d = LifeData.New(Noon);
        d.Satiety = 40;
        Assert.Equal(LifeRules.FeedOutcome.Ate, LifeRules.Feed(d, "rice-ball", Noon, profile, out var liked));
        Assert.Equal(0, liked);
        Assert.Equal(65, d.Satiety, 3);
        Assert.Equal(LifeRules.FeedOutcome.Cooldown, LifeRules.Feed(d, "rice-ball", Noon.AddMinutes(10), profile, out _));
        Assert.Equal(LifeRules.FeedOutcome.NoSnack, LifeRules.Feed(d, "fish", Noon, profile, out _));
        d.Snacks["fish"] = 1;
        var mood = d.Mood;
        Assert.Equal(LifeRules.FeedOutcome.Ate, LifeRules.Feed(d, "fish", Noon, profile, out liked));
        Assert.Equal(1, liked);
        Assert.True(d.Mood - mood >= 12);
        Assert.False(d.Snacks.ContainsKey("fish"));
        Assert.Equal(LifeRules.FeedOutcome.Ate, LifeRules.Feed(d, "vegetable", Noon, profile, out liked));
        Assert.Equal(-1, liked);
        d.Satiety = 96;
        Assert.Equal(LifeRules.FeedOutcome.Full, LifeRules.Feed(d, "milk", Noon, profile, out _));
    }

    [Fact]
    public void Snacks_cap_at_ten()
    {
        var d = LifeData.New(Noon);
        var random = new Random(1);
        for (var i = 0; i < 15; i++) LifeRules.AwardSnack(d, random);
        Assert.Equal(LifeTuning.MaxSnacks, d.SnackCount);
    }

    [Fact]
    public void Offline_catch_up_has_a_floor_and_ignores_clock_rollback()
    {
        var d = LifeData.New(Noon);
        d.Satiety = 80;
        d.Energy = 10;
        var away = LifeRules.CatchUp(d, Noon.AddDays(3), Neutral);
        Assert.Equal(TimeSpan.FromDays(3), away);
        Assert.Equal(LifeTuning.OfflineSatietyFloor, d.Satiety);
        Assert.Equal(100, d.Energy);

        var hungry = LifeData.New(Noon);
        hungry.Satiety = 20;   // 本来就更低：不动，也不涨
        LifeRules.CatchUp(hungry, Noon.AddDays(3), Neutral);
        Assert.Equal(20, hungry.Satiety);

        var back = LifeData.New(Noon);
        Assert.Equal(TimeSpan.Zero, LifeRules.CatchUp(back, Noon.AddHours(-5), Neutral));
        var forever = LifeData.New(Noon);
        Assert.Equal(TimeSpan.FromDays(365), LifeRules.CatchUp(forever, Noon.AddYears(3), Neutral));
    }

    [Fact]
    public void Thresholds_fire_once_with_hysteresis()
    {
        var d = LifeData.New(Noon);
        d.Satiety = 31;
        var before = d.Clone();
        d.Satiety = 29.9;
        Assert.Equal([PetEvents.Hungry], LifeRules.Crossings(before, d, Noon).Select(c => c.Event));
        before = d.Clone();
        d.Satiety = 30.1;   // 回到 30 以上但没到 40：不算吃饱
        Assert.Empty(LifeRules.Crossings(before, d, Noon));
        before = d.Clone();
        d.Satiety = 29.5;
        Assert.Empty(LifeRules.Crossings(before, d, Noon));
        before = d.Clone();
        d.Satiety = 45;
        Assert.Equal([PetEvents.Satisfied], LifeRules.Crossings(before, d, Noon).Select(c => c.Event));
    }

    [Fact]
    public void Level_up_is_reported()
    {
        var d = LifeData.New(Noon);
        d.Xp = 59;
        var before = d.Clone();
        d.Xp = 61;
        var events = LifeRules.Crossings(before, d, Noon);
        Assert.Contains(events, e => e.Event == PetEvents.LevelUp && e.Data.Contains(("level", "2")));
        Assert.Equal("认识了", LifeRules.LevelName(2));
        Assert.Equal(10, LifeRules.Level(99999));
    }

    [Fact]
    public void Store_round_trips_and_backs_up_broken_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "astra-life-" + Guid.NewGuid().ToString("N"));
        try
        {
            var d = LifeData.New(Noon);
            d.Satiety = 42;
            d.Snacks["cake"] = 2;
            d.TraitOverrides["bold"] = 80;
            LifeStore.Save(dir, d);
            var loaded = LifeStore.Load(dir)!;
            Assert.Equal(42, loaded.Satiety);
            Assert.Equal(2, loaded.Snacks["cake"]);
            Assert.Equal(80, loaded.TraitOverrides["bold"]);

            File.WriteAllText(Path.Combine(dir, LifeStore.FileName), "{ broken");
            Assert.Null(LifeStore.Load(dir));
            Assert.Contains(Directory.GetFiles(dir), f => Path.GetFileName(f).StartsWith("life.json.bad-"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}

public class ProfileTests
{
    private static JsonElement Manifest(string profile) => JsonDocument.Parse($$"""{"format": 2, "profile": {{profile}}}""").RootElement;

    [Fact]
    public void Missing_profile_uses_defaults_equal_to_the_old_constants()
    {
        var p = PetProfile.Parse(null);
        Assert.Equal(50, p.Traits.Clingy);
        Assert.Equal(330, p.Body.FeetY);
        Assert.Equal(ClickZones.Head, ClickZones.At(100, p.Body));
        Assert.Equal(ClickZones.Feet, ClickZones.At(300, p.Body));
    }

    [Fact]
    public void Values_are_clamped_and_unknown_foods_ignored()
    {
        var p = PetProfile.Parse(Manifest("""
            {"species": "blob", "traits": {"greedy": 140, "bold": -3},
             "physics": {"weight": 9},
             "diet": {"likes": ["fish", "dragonfruit"], "dislikes": ["fish", "vegetable"]},
             "body": {"bounds": [200, 40, 440, 340], "wall": {"contactX": 150}}}
            """));
        Assert.Equal("blob", p.Species);
        Assert.Equal(100, p.Traits.Greedy);
        Assert.Equal(0, p.Traits.Bold);
        Assert.Equal(3, p.Physics.Weight);
        Assert.Equal(0.6, p.Physics.Bounce);   // blob 的默认弹性
        Assert.Equal(["fish"], p.Diet.Likes);
        Assert.Equal(["vegetable"], p.Diet.Dislikes);
        Assert.Equal(340, p.Body.FeetY);
        Assert.Equal(150, p.Body.WallContactX);
    }

    [Fact]
    public void Unreasonable_bounds_are_ignored()
    {
        var p = PetProfile.Parse(Manifest("""{"body": {"bounds": [0, 0, 10, 10]}}"""));
        Assert.Equal(214, p.Body.X0);
    }

    [Fact]
    public void Traits_describe_only_strong_values()
    {
        Assert.Equal("", new PetTraits().Describe());
        Assert.Equal("很贪吃、胆子有点小", new PetTraits(Greedy: 80, Bold: 20).Describe());
        Assert.Equal(0.5, PetTraits.Factor(0));
        Assert.Equal(1.5, PetTraits.Factor(100));
    }
}

public class PetLifeBehaviorTests
{
    [Fact]
    public void Feeding_publishes_events_and_rules_play_eat()
    {
        using var rig = new HostRig();
        var events = new List<PetEvent>();
        rig.Events.Published += events.Add;
        rig.Host.Start();
        var life = rig.Host.Life;
        life.Adjust(satiety: 50);
        Assert.Equal(LifeRules.FeedOutcome.Ate, life.Feed("rice-ball"));
        Assert.Contains(events, e => e.Type == PetEvents.Fed && e["liked"] == "normal" && e["name"] == "饭团");
        // 测试素材没有 eat：回退到 happy → click。
        Assert.True(rig.Director.CurrentAction.HasRole(Roles.Click));
        Assert.Contains(rig.Voice.Said, s => s.Contains("饭团") || s.Contains("好吃") || s.Contains("吧唧"));
    }

    [Fact]
    public void Full_pet_refuses_food()
    {
        using var rig = new HostRig();
        rig.Host.Start();
        rig.Host.Life.Adjust(satiety: 99);
        Assert.Equal(LifeRules.FeedOutcome.Full, rig.Host.Life.Feed("milk"));
        Assert.Contains(rig.Voice.Said, s => s.Contains("吃不下") || s.Contains("圆滚滚"));
    }

    [Fact]
    public void Hunger_crossing_says_something_once()
    {
        using var rig = new HostRig();
        rig.Host.Start();
        rig.Host.Life.Adjust(satiety: 31);
        rig.Voice.Said.Clear();
        rig.Advance(TimeSpan.FromMinutes(30));   // 每小时 −6：半小时后穿过 30
        Assert.Single(rig.Voice.Said, s => s.Contains("饿") || s.Contains("咕咕") || s.Contains("想吃"));
        rig.Advance(TimeSpan.FromMinutes(30));
        Assert.Single(rig.Voice.Said, s => s.Contains("饿") || s.Contains("咕咕") || s.Contains("想吃"));
    }

    [Fact]
    public void Focus_rounds_award_snacks()
    {
        using var rig = new HostRig();
        rig.Host.Start();
        rig.Host.Pomodoro.Start();
        rig.Advance(TimeSpan.FromMinutes(26));
        Assert.Equal(1, rig.Host.Life.Data.SnackCount);
        Assert.Contains(rig.Voice.Said, s => s.Contains("专注奖励"));
        Assert.Equal(rig.Settings.PomodoroWork, rig.Host.Life.Data.Stat(LifeStats.FocusMinutes));
    }

    [Fact]
    public void Disabling_life_freezes_numbers_and_removes_hooks()
    {
        using var rig = new HostRig();
        rig.Host.Start();
        Assert.NotNull(rig.Director.AmbientWeight);
        rig.Settings.LifeEnabled = false;
        Assert.Null(rig.Director.AmbientWeight);
        Assert.Null(rig.Director.Usable);
        var satiety = rig.Host.Life.Data.Satiety;
        rig.Advance(TimeSpan.FromHours(2));
        Assert.Equal(satiety, rig.Host.Life.Data.Satiety);
        Assert.Equal(LifeRules.FeedOutcome.Unknown, rig.Host.Life.Feed("milk"));
    }

    [Fact]
    public void Need_and_bond_conditions_follow_life_state()
    {
        using var rig = new HostRig();
        rig.Host.Start();
        var when = new TriggerWhen { Need = new() { ["satiety"] = [0, 30] }, Bond = [1, 3] };
        var e = new PetEvent("x");
        rig.Host.Life.Adjust(satiety: 20);
        Assert.True(TriggerConditions.Hold(when, e, rig.Settings, rig.Time.GetLocalNow(), rig.Host.Life.TriggerState()));
        rig.Host.Life.Adjust(satiety: 50);
        Assert.False(TriggerConditions.Hold(when, e, rig.Settings, rig.Time.GetLocalNow(), rig.Host.Life.TriggerState()));
        rig.Settings.LifeEnabled = false;
        rig.Host.Life.Adjust(satiety: 20);
        Assert.False(TriggerConditions.Hold(when, e, rig.Settings, rig.Time.GetLocalNow(), rig.Host.Life.TriggerState()));
        Assert.True(TriggerConditions.Hold(new TriggerWhen { Stance = [Stances.Ground] }, e, rig.Settings,
            rig.Time.GetLocalNow(), rig.Host.Life.TriggerState()));
    }

    [Fact]
    public void Spam_clicks_make_it_dizzy_and_then_ignore_clicks()
    {
        using var rig = new HostRig();
        var events = new List<string>();
        rig.Events.Published += e => events.Add(e.Type);
        rig.Host.Start();
        for (var i = 0; i < 6; i++) rig.Input.RaiseClicked();
        Assert.Contains(PetEvents.Spammed, events);
        Assert.Equal(5, events.Count(t => t == PetEvents.Clicked));
        rig.Input.RaiseClicked();
        Assert.Equal(5, events.Count(t => t == PetEvents.Clicked));
        Assert.Contains(rig.Voice.Said, s => s.Contains("晕") || s.Contains("别戳"));
    }

    [Fact]
    public void Feed_menu_command_feeds_through_commands()
    {
        using var rig = new HostRig();
        rig.Host.Start();
        rig.Host.Life.Adjust(satiety: 50);
        rig.Input.RaiseMenuCommand(MenuEntry.FeedPrefix + "milk");
        Assert.Equal(1, rig.Host.Life.Data.Stat(LifeStats.Fed));
    }

    [Fact]
    public void Ambient_weight_slows_movement_when_tired()
    {
        using var rig = new HostRig();
        rig.Host.Start();
        var move = rig.Director.Library.Actions.WithRole(Roles.Move)[0];
        rig.Host.Life.Adjust(energy: 90);
        var rested = rig.Host.Life.Weight(move);
        rig.Host.Life.Adjust(energy: 10);
        Assert.Equal(rested * 0.3, rig.Host.Life.Weight(move), 6);
    }
}

public class PetBodyTests : IDisposable
{
    private readonly StanceLibrary _dir = new();
    private readonly PetSettings _settings = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero));
    private readonly PetDirector _director;
    private readonly PetBody _body;
    private readonly List<PetEvent> _events = new();
    private static readonly SurfaceMap Screen = new([new DesktopRect(0, 0, 1600, 900)]);

    public PetBodyTests()
    {
        _director = new PetDirector(_dir.Load(), _settings, new Random(3), _time);
        _director.Start();
        _body = new PetBody(_director, _settings, _events.Add, new Random(1), _time);
        _body.Configure(Screen, 0.5, 1);
    }

    public void Dispose() => _dir.Dispose();

    private double FeetY => _body.Y + 330 * 0.5;

    [Fact]
    public void Starts_standing_on_the_floor_in_the_middle()
    {
        Assert.Equal(900, FeetY, 3);
        Assert.Equal(800 - 160, _body.X, 3);
    }

    [Fact]
    public void Free_mode_starts_centered_and_never_falls()
    {
        var settings = new PetSettings { Placement = PetSettings.PlacementFree };
        var director = new PetDirector(_dir.Load(), settings, new Random(3), _time);
        director.Start();
        var body = new PetBody(director, settings, _ => { }, new Random(1), _time);
        body.Configure(Screen, 0.5, 1);
        Assert.Equal(450 - 90, body.Y, 3);
        body.BeginHold();
        body.HoldAt(100, 100);
        director.EndDrag();
        body.Release(0, 3000);
        Assert.False(body.Airborne);
        Assert.Equal(100, body.Y);
    }

    [Fact]
    public void Released_in_the_air_falls_and_lands_softly()
    {
        _body.BeginHold();
        _director.BeginDrag();
        _body.HoldAt(500, 200);
        _director.EndDrag();
        _body.Release(0, 0);
        Assert.True(_body.Airborne);
        for (var i = 0; i < 200 && _body.Airborne; i++) _body.PhysicsStep(TimeSpan.FromMilliseconds(16));
        Assert.False(_body.Airborne);
        Assert.Equal(900, FeetY, 3);
        Assert.Equal(Stances.Ground, _director.Stance);
        Assert.Contains(_events, e => e.Type == PetEvents.Landed && e["thrown"] == "false");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Landing_opens_the_land_clip_directly_or_falls_back_to_idle(bool ruleReacts)
    {
        // 回归：落地先开待机流（加预取两个 ffmpeg），马上又被落地动作打断，首帧被拖慢，看着落地慢半拍。
        using var dir = new StanceLibrary(land: true);
        var decoder = new FakeDecoder();
        var director = new PetDirector(dir.Load(decoder: decoder), _settings, new Random(3), _time);
        director.Start();
        var land = director.Library.Actions.Find("land")!;
        var body = new PetBody(director, _settings, e =>
        {
            // 模拟默认规则 landed：收到落地事件就请求落地动作。
            if (ruleReacts && e.Type == PetEvents.Landed)
                director.Request(new ActionRequest(land, ActionPriority.System, "rule:landed"));
        }, new Random(1), _time);
        body.Configure(Screen, 0.5, 1);
        body.Toss(0, 0);
        var before = decoder.Opened.Count;
        for (var i = 0; i < 200 && body.Airborne; i++) body.PhysicsStep(TimeSpan.FromMilliseconds(16));

        Assert.False(body.Airborne);
        Assert.Equal(Stances.Ground, director.Stance);
        var opened = decoder.Opened.Skip(before).Select(s => s.Clip.Name).ToList();
        if (ruleReacts)
        {
            Assert.Equal("land", director.CurrentAction.Id);
            Assert.Equal("land", opened[0]);   // 第一个打开的就是落地动作，没有白开的待机流
        }
        else
        {
            Assert.Equal("breathe", director.CurrentAction.Id);
            Assert.Equal("breathe", opened[0]);
        }
    }

    [Fact]
    public void Releasing_in_the_air_tells_the_window_so_physics_starts()
    {
        // 回归：窗口靠 Moved 启动物理定时器，松手进入下落时漏发过，宠物停在半空。
        var moved = 0;
        _body.Moved += () => moved++;
        _body.BeginHold();
        _director.BeginDrag();
        _body.HoldAt(500, 200);
        _director.EndDrag();
        moved = 0;
        _body.Release(3000, -500);
        Assert.True(_body.Airborne);
        Assert.True(moved > 0);
    }

    [Fact]
    public void Releasing_near_the_floor_snaps_without_falling()
    {
        _body.BeginHold();
        _body.HoldAt(500, 900 - 165 - 10);
        _director.EndDrag();
        _body.Release(0, 0);
        Assert.False(_body.Airborne);
        Assert.Equal(900, FeetY, 3);
    }

    [Fact]
    public void Fast_release_throws_bounces_off_walls_and_stays_on_screen()
    {
        _settings.AllowClimb = false;
        _body.BeginHold();
        _body.HoldAt(1200, 300);
        _director.EndDrag();
        _body.Release(5000, -800);
        Assert.Contains(_events, e => e.Type == PetEvents.Thrown);
        for (var i = 0; i < 600 && _body.Airborne; i++)
        {
            _body.PhysicsStep(TimeSpan.FromMilliseconds(16));
            Assert.InRange(_body.X + 214 * 0.5, -1, 1601);
        }
        Assert.False(_body.Airborne);
        Assert.Contains(_events, e => e.Type == PetEvents.Landed && e["thrown"] == "true");
    }

    [Fact]
    public void Walking_to_the_edge_and_climbing_aligns_the_contact_line_with_the_screen_edge()
    {
        Assert.True(_body.GoClimb());
        Assert.Equal("run", _director.CurrentAction.Id);
        // 走到左边或右边；按帧推进，直到上墙。
        for (var i = 0; i < 40 && _director.Stance != Stances.Wall; i++)
        {
            for (var f = 0; f < 3; f++)
            {
                if (_director.Tick() is null) break;
                _body.Step(_director.Frame, 3);
            }
            _director.Tick();
            _body.Step(_director.Frame, 3);
        }
        Assert.Equal(Stances.Wall, _director.Stance);
        // 贴墙：左墙时接触线（画布 x=113，默认值）对齐屏幕左边，右墙时镜像后对齐右边。
        var contact = _director.Direction < 0 ? _body.X + 113 * 0.5 : _body.X + (640 - 113) * 0.5;
        Assert.Equal(_director.Direction < 0 ? 0 : 1600, contact, 1);
    }

    [Fact]
    public void Placement_switch_to_ground_drops_a_floating_pet()
    {
        var settings = new PetSettings { Placement = PetSettings.PlacementFree };
        var director = new PetDirector(_dir.Load(), settings, new Random(3), _time);
        director.Start();
        var body = new PetBody(director, settings, _ => { }, new Random(1), _time);
        body.Configure(Screen, 0.5, 1);
        settings.Placement = PetSettings.PlacementGround;
        Assert.True(body.Airborne);
    }

    [Fact]
    public void Screens_changing_puts_a_lost_pet_back()
    {
        _body.BeginHold();
        _body.HoldAt(3000, 200);
        _body.Release(0, 0);
        _body.Configure(new SurfaceMap([new DesktopRect(0, 0, 1280, 800)]), 0.5, 1);
        Assert.InRange(_body.X, 0, 1280);
        Assert.Equal(800, _body.Y + 330 * 0.5, 3);
    }
}

public class SurfaceMapTests
{
    [Fact]
    public void Adjacent_edges_are_not_walls()
    {
        var a = new DesktopRect(0, 0, 1440, 900);
        var b = new DesktopRect(1440, 0, 1920, 1080);
        var map = new SurfaceMap([a, b]);
        Assert.True(map.IsWall(a, -1));
        Assert.False(map.IsWall(a, 1));
        Assert.False(map.IsWall(b, -1));
        Assert.True(map.IsWall(b, 1));
        Assert.Equal(900, map.FloorBelow(100, 500));
        Assert.Equal(1080, map.FloorBelow(1500, 500));
        Assert.Null(map.FloorBelow(5000, 500));
    }
}

public class LifeAgentTests
{
    [Fact]
    public void Play_action_lists_only_reachable_unlocked_actions()
    {
        using var dir = new StanceLibrary();
        var lib = dir.Load();
        // 墙上的、过渡的、系统角色的都不列；默认只列地面上的。
        var ids = PlayActionTool.Choosable(lib.Actions).Select(a => a.Id).ToList();
        Assert.Contains("wave", ids);
        Assert.DoesNotContain("climb", ids);
        Assert.DoesNotContain("wall-enter", ids);
        Assert.DoesNotContain("munch", ids);
        Assert.Empty(PlayActionTool.Choosable(lib.Actions, a => a.Id != "wave"));
    }

    [Fact]
    public void Settings_migration_keeps_old_users_in_free_placement()
    {
        var fresh = new PetSettings();
        fresh.Normalize(upgraded: false);
        Assert.Equal(PetSettings.PlacementGround, fresh.Placement);
        var old = new PetSettings();
        old.Normalize(upgraded: true);
        Assert.Equal(PetSettings.PlacementFree, old.Placement);
        // 迁移只做一次：之后用户改成站在地面不会被改回去。
        old.Placement = PetSettings.PlacementGround;
        old.Normalize(upgraded: true);
        Assert.Equal(PetSettings.PlacementGround, old.Placement);
    }
}

public class LedgeAndCeilingTests : IDisposable
{
    private readonly StanceLibrary _dir = new(ceiling: true);
    private readonly PetSettings _settings = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero));
    private readonly PetDirector _director;
    private readonly PetBody _body;
    private readonly List<PetEvent> _events = new();
    private static readonly SurfaceMap Screen = new([new DesktopRect(0, 0, 1600, 900)]);

    public LedgeAndCeilingTests()
    {
        _director = new PetDirector(_dir.Load(), _settings, new Random(3), _time);
        _director.Start();
        _body = new PetBody(_director, _settings, _events.Add, new Random(1), _time);
        _body.Configure(Screen, 0.5, 1);
    }

    public void Dispose() => _dir.Dispose();

    private double Feet => _body.Y + 330 * 0.5;

    private void FallUntilLanded()
    {
        for (var i = 0; i < 400 && _body.Airborne; i++) _body.PhysicsStep(TimeSpan.FromMilliseconds(16));
    }

    private void Drop(double x, double y)
    {
        _body.BeginHold();
        _director.BeginDrag();
        _body.HoldAt(x, y);
        _director.EndDrag();
        _body.Release(0, 0);
        FallUntilLanded();
    }

    [Fact]
    public void Ledges_skip_covered_parts_maximized_and_tiny_windows()
    {
        var windows = new List<AstraPet.Platform.DesktopWindow>
        {
            new(1, 300, 350, 200, 300),   // 最前：挡住后面那条边的 300..500
            new(2, 100, 400, 800, 400),
            new(3, 0, 0, 1600, 900),      // 最大化：顶边是天花板，不算
            new(4, 1200, 600, 60, 100),   // 太窄
        };
        var ledges = Screen.LedgesFrom(windows, 100);
        Assert.Contains(ledges, l => l.WindowId == 1 && l.X0 == 300 && l.X1 == 500 && l.Y == 350);
        // 窗口 1 的竖边跨过窗口 2 的顶边（350 ≤ 400 ≤ 650），所以窗口 2 的顶边被切成两段。
        Assert.Contains(ledges, l => l.WindowId == 2 && l.X0 == 100 && l.X1 == 300);
        Assert.Contains(ledges, l => l.WindowId == 2 && l.X0 == 500 && l.X1 == 900);
        Assert.DoesNotContain(ledges, l => l.WindowId is 3 or 4);
    }

    [Fact]
    public void Dropped_pet_lands_on_a_window_follows_it_and_falls_when_it_closes()
    {
        var window = new AstraPet.Platform.DesktopWindow(7, 400, 500, 600, 300);
        _body.UpdateWindows([window]);
        Drop(500, 100);
        Assert.Equal(500, Feet, 1);
        Assert.True(_body.OnLedge);

        _body.UpdateWindows([window with { X = 450, Y = 450 }]);   // 窗口挪了：跟着挪
        Assert.Equal(450, Feet, 1);
        Assert.Equal(550, _body.X, 1);

        _body.UpdateWindows([]);   // 窗口关了：掉下去，落到屏幕地面
        Assert.True(_body.Airborne);
        FallUntilLanded();
        Assert.Equal(900, Feet, 1);
    }

    [Fact]
    public void Walking_on_a_window_stays_on_its_top_edge()
    {
        _body.UpdateWindows([new AstraPet.Platform.DesktopWindow(7, 400, 500, 600, 300)]);
        Drop(600, 100);
        var run = _director.Library.Actions.Find("run")!;
        for (var i = 0; i < 20; i++)
        {
            _director.Request(new ActionRequest(run, ActionPriority.User, "test"));
            for (var f = 0; f < 3; f++) _body.Step(f, 3);
            Assert.InRange(_body.BodyPosition.CenterX, 400, 1000);
            Assert.Equal(500, Feet, 1);
        }
    }

    [Fact]
    public void Ceiling_is_entered_from_the_top_of_the_wall_and_dropped_from()
    {
        Assert.Contains(Stances.Ceiling, _director.Library.Stances.Available);
        // 墙中间：翻不上去。
        Assert.True(_body.GoClimb());
        for (var i = 0; i < 40 && _director.Stance != Stances.Wall; i++)
        {
            for (var f = 0; f < 3; f++) { if (_director.Tick() is null) break; _body.Step(_director.Frame, 3); }
            _director.Tick();
            _body.Step(_director.Frame, 3);
        }
        Assert.Equal(Stances.Wall, _director.Stance);
        Assert.Equal(PlayResult.Rejected, _director.RequestStance(Stances.Ceiling, ActionPriority.User, "t"));

        // 爬到顶：能翻上去，贴着屏幕顶，朝屋里。
        var climb = _director.Library.Actions.Find("climb")!;
        for (var i = 0; i < 30; i++)
        {
            _director.Request(new ActionRequest(climb, ActionPriority.User, "t"));
            for (var f = 0; f < 3; f++) _body.Step(f, 3);
        }
        var side = _director.Direction;
        Assert.Equal(PlayResult.Played, _director.RequestStance(Stances.Ceiling, ActionPriority.User, "t"));
        for (var f = 0; f < 3; f++) _body.Step(f, 3);
        for (var f = 0; f < 4; f++) _director.Tick();
        Assert.Equal(Stances.Ceiling, _director.Stance);
        Assert.Equal(-side, _director.Direction);
        Assert.Equal(0, _body.Y + 27 * 0.5, 1);   // 贴天花板的线（默认 27）对齐屏幕顶

        Assert.Equal(PlayResult.Played, _director.RequestStance(Stances.Air, ActionPriority.User, "t"));
        for (var f = 0; f < 4; f++) _director.Tick();
        Assert.True(_body.Airborne);
        FallUntilLanded();
        Assert.Equal(Stances.Ground, _director.Stance);
        Assert.Equal(900, Feet, 1);
    }

    [Fact]
    public void Summon_puts_the_pet_on_the_floor_under_the_cursor()
    {
        _body.Summon(300, 200);
        Assert.Equal(900, Feet, 1);
        Assert.Equal(300, _body.BodyPosition.CenterX, 1);
    }
}

internal sealed class FakeHotkey : AstraPet.Platform.IGlobalHotkey
{
    public Action? Pressed;
    public bool Started;
    public bool Start(Action pressed) { Pressed = pressed; Started = true; return true; }
    public void Stop() => Started = false;
    public string Display => "Ctrl+Alt+P";
    public void Dispose() { }
}

public class SummonTests
{
    [Fact]
    public void Hotkey_summons_and_greets_and_can_be_turned_off()
    {
        var hotkey = new FakeHotkey();
        using var rig = new HostRig(caps: new AstraPet.Platform.PlatformCapabilities { GlobalHotkey = true, IdleDetection = true }, hotkey: hotkey);
        var events = new List<string>();
        rig.Events.Published += e => events.Add(e.Type);
        rig.Host.Start();
        Assert.True(hotkey.Started);
        rig.Settings.Visible = false;
        hotkey.Pressed!();
        Assert.Contains(PetEvents.Summoned, events);
        Assert.True(rig.Settings.Visible);
        Assert.Contains(rig.Voice.Said, s => s.Contains("来啦") || s.Contains("叫我") || s.Contains("到！"));
        rig.Settings.SummonHotkey = false;
        Assert.False(hotkey.Started);
    }
}

public class NativeSurfaceTests
{
    [Fact]
    public void Mac_window_list_reads_bounds_without_permissions()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var platform = AstraPet.Platform.PlatformFactory.Create();
        Assert.True(platform.Capabilities.WindowSurfaces);
        var windows = platform.Windows.VisibleWindows();
        Assert.All(windows, w => Assert.True(w.Width > 0 && w.Height > 0));
        Assert.DoesNotContain(windows, w => w.Id == 0 && w.Width == 0);
    }
}
