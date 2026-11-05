using AstraPet.Core;
using AstraPet.Core.Behaviors;
using Microsoft.Extensions.Time.Testing;

namespace AstraPet.Tests;

/// <summary>动作模型：格式 1 合成、格式 2 清单、回退链、覆盖层。</summary>
public class ActionModelTests : IDisposable
{
    private readonly TempLibrary _dir = new();
    private readonly FakeDecoder _decoder = new();
    private readonly string _overlay = Path.Combine(Path.GetTempPath(), "astra-overlay-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _dir.Dispose();
        try { Directory.Delete(_overlay, true); } catch (IOException) { }
    }

    [Fact]
    public void Format1_synthesizes_one_action_per_clip_with_stable_ids()
    {
        File.WriteAllText(Path.Combine(_dir.Dir, "move_strides.json"), """{"_comment": "x", "run": {"stride": 240, "curve": [0, 0.5, 1]}}""");
        File.WriteAllText(Path.Combine(_dir.Dir, "text_clips.json"), """{"no_mirror": ["shy"]}""");
        // 文件名按序数排序：shy 在 wave 前面。
        var lib = PetLibrary.Load(_dir.Dir, _decoder);

        Assert.Equal(1, lib.Format);
        Assert.Equal(["click-1", "click-2"], lib.Actions.WithRole(Roles.Click).Select(a => a.Id));
        var shy = lib.Actions.Find("click-1")!;
        Assert.Equal("shy", shy.Name);
        Assert.False(shy.Mirror);
        Assert.True(lib.Actions.Find("click-2")!.Mirror);
        Assert.Equal(240, lib.Actions.Find("move-1")!.Gait!.Stride);
        // 分类的总权重平分给其中的动作。
        Assert.Equal(20, shy.Ambient);
        Assert.Equal(30, lib.Actions.Find("idle-1")!.Ambient);
        Assert.Empty(lib.Actions.WithRole(Roles.Sing));
    }

    [Fact]
    public void Ambient_distribution_matches_the_old_chain()
    {
        var lib = PetLibrary.Load(_dir.Dir, _decoder);
        var random = new Random(7);
        var counts = new Dictionary<string, int>();
        const int n = 20000;
        for (var i = 0; i < n; i++)
        {
            var role = lib.Actions.PickAmbient(random, _ => true)!.Roles[0];
            counts[role] = counts.GetValueOrDefault(role) + 1;
        }
        Assert.InRange(counts[Roles.Idle] / (double)n, 0.28, 0.32);
        Assert.InRange(counts[Roles.Turn] / (double)n, 0.08, 0.12);
        Assert.InRange(counts[Roles.Click] / (double)n, 0.38, 0.42);
        Assert.InRange(counts[Roles.Move] / (double)n, 0.18, 0.22);
    }

    [Fact]
    public void Roles_fall_back_along_the_chain()
    {
        var lib = PetLibrary.Load(_dir.Dir, _decoder);
        var random = new Random(1);
        Assert.True(lib.Actions.PickByRole(Roles.Celebrate, random)!.HasRole(Roles.Click));
        Assert.True(lib.Actions.PickByRole(Roles.Sad, random)!.HasRole(Roles.Idle));
        Assert.Null(lib.Actions.PickByRole(Roles.Think, random));
        Assert.Equal("click-2", lib.Actions.Resolve("click-2", random)!.Id);
        Assert.True(lib.Actions.Resolve("role:happy", random)!.HasRole(Roles.Click));
    }

    [Fact]
    public void Format2_reads_actions_from_pet_json()
    {
        var root = Path.Combine(_dir.Dir, "pack");
        foreach (var id in new[] { "idle-a", "wave", "drag-a", "trot", "look" })
        {
            Directory.CreateDirectory(Path.Combine(root, "clips", id));
            File.WriteAllBytes(Path.Combine(root, "clips", id, "a.webm"), []);
        }
        File.WriteAllText(Path.Combine(root, "pet.json"), """
            {"format": 2, "id": "shiba", "name": "柴柴", "actions": [
              {"id": "idle-a", "roles": ["idle"], "clips": ["clips/idle-a/a.webm"]},
              {"id": "wave", "name": "挥手", "description": "挥手打招呼", "roles": ["click", "happy"], "clips": ["clips/wave/a.webm"], "lines": ["嗨"], "ambient": 5},
              {"id": "drag-a", "roles": ["drag"], "clips": ["clips/drag-a/a.webm"]},
              {"id": "trot", "roles": ["move"], "clips": ["clips/trot/a.webm"], "gait": {"stride": 220}},
              {"id": "look", "roles": ["turn"], "clips": ["clips/look/a.webm"], "mirror": false},
              {"id": "evil", "roles": ["click"], "clips": ["../../etc/passwd"]},
              {"id": "Bad Id", "roles": ["click"], "clips": ["clips/wave/a.webm"]}
            ]}
            """);
        var lib = PetLibrary.Load(root, _decoder);

        Assert.Equal(2, lib.Format);
        Assert.Equal(["idle-a", "wave", "drag-a", "trot", "look"], lib.Actions.All.Select(a => a.Id));
        var wave = lib.Actions.Find("wave")!;
        Assert.Equal(["嗨"], wave.Lines);
        Assert.Equal(5, wave.Ambient);
        Assert.Equal(30, lib.Actions.Find("idle-a")!.Ambient);
        Assert.Equal(220, lib.Actions.Find("trot")!.Gait!.Stride);
        Assert.False(lib.Actions.Find("look")!.Mirror);
    }

    [Fact]
    public void Overlay_changes_survive_reload_and_never_break_required_roles()
    {
        var lib = PetLibrary.Load(_dir.Dir, _decoder, overlayDirectory: _overlay);
        var overlay = lib.Overlay;
        overlay.OverrideFor("click-1").Ambient = 0;
        overlay.OverrideFor("click-2").Lines = ["哈喽！"];
        overlay.OverrideFor("idle-1").Enabled = false;     // 唯一的待机，不能禁用
        overlay.OverrideFor("gone").Enabled = false;       // 孤儿
        lib.ApplyOverlay(overlay);

        var reloaded = PetLibrary.Load(_dir.Dir, _decoder, overlayDirectory: _overlay);
        Assert.Equal(0, reloaded.Actions.Find("click-1")!.Ambient);
        Assert.Equal(["哈喽！"], reloaded.Actions.Find("click-2")!.Lines);
        Assert.True(reloaded.Actions.Find("idle-1")!.Enabled);
        // 权重 0 的动作不会被随机到，但还能被显式触发。
        var random = new Random(3);
        for (var i = 0; i < 200; i++)
            Assert.NotEqual("click-1", reloaded.Actions.PickAmbient(random, _ => true)!.Id);
        Assert.Equal("click-1", reloaded.Actions.Resolve("click-1", random)!.Id);
    }

    [Fact]
    public void Overlay_adds_user_actions_and_renames_duplicates()
    {
        Directory.CreateDirectory(Path.Combine(_overlay, "clips", "dance"));
        File.WriteAllBytes(Path.Combine(_overlay, "clips", "dance", "a.webm"), []);
        var overlay = new ActionOverlay();
        overlay.Added.Add(new AddedAction { Id = "dance", Name = "跳舞", Roles = ["happy", "dance"], Clips = ["clips/dance/a.webm"] });
        overlay.Added.Add(new AddedAction { Id = "click-1", Roles = ["click"], Clips = ["clips/dance/a.webm"] });
        overlay.Added.Add(new AddedAction { Id = "escape", Roles = ["click"], Clips = ["../x.webm"] });
        overlay.Save(_overlay);

        var lib = PetLibrary.Load(_dir.Dir, _decoder, overlayDirectory: _overlay);
        var dance = lib.Actions.Find("dance")!;
        Assert.Equal(ActionOrigin.User, dance.Origin);
        Assert.Equal(ActionOverlay.DefaultAddedAmbient, dance.Ambient);
        Assert.NotNull(lib.Actions.Find("click-1-user"));
        Assert.Null(lib.Actions.Find("escape"));
        // 有了 happy 动作，开心不再回退到 click。
        Assert.Equal("dance", lib.Actions.PickByRole(Roles.Happy, new Random(1))!.Id);
    }
}

/// <summary>Director 的优先级、排队、循环、冷却。</summary>
public class DirectorRequestTests : IDisposable
{
    private readonly TempLibrary _dir = new();
    private readonly PetSettings _settings = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));
    private readonly PetDirector _director;

    public DirectorRequestTests()
    {
        _director = new PetDirector(PetLibrary.Load(_dir.Dir, new FakeDecoder()), _settings, new Random(1), _time);
        _director.Start();
    }

    public void Dispose() => _dir.Dispose();

    private ActionDef A(string id) => _director.Library.Actions.Find(id)!;

    private void PlayThrough()
    {
        var current = _director.Current;
        for (var i = 0; i < 100 && _director.Current == current; i++) _director.Tick();
        Assert.NotSame(current, _director.Current);
    }

    [Fact]
    public void Agent_interrupts_ambient()
    {
        Assert.Equal(PlayResult.Played, _director.Request(new(A("click-2"), ActionPriority.Agent, "agent")));
        Assert.Equal("click-2", _director.CurrentAction.Id);
        Assert.Equal("agent", _director.CurrentSource);
    }

    [Fact]
    public void Agent_waits_for_user_clip_then_idle()
    {
        _director.Play(PetLibrary.Click, fromUser: true);
        Assert.Equal(PlayResult.Queued, _director.Request(new(A("move-1"), ActionPriority.Agent, "agent")));
        Assert.Equal(1, _director.PendingCount);
        PlayThrough();
        Assert.True(_director.CurrentAction.HasRole(Roles.Idle));   // 用户触发的播完先回待机
        PlayThrough();
        Assert.Equal("move-1", _director.CurrentAction.Id);
        Assert.Equal(0, _director.PendingCount);
    }

    [Fact]
    public void Queued_requests_expire()
    {
        _director.Play(PetLibrary.Click, fromUser: true);
        _director.Request(new(A("move-1"), ActionPriority.Agent, "agent"));
        _time.Advance(PetDirector.PendingTtl + TimeSpan.FromSeconds(1));
        PlayThrough();   // → 待机，下一段是在待机开始时选的，那时请求已过期
        PlayThrough();
        Assert.NotEqual("agent", _director.CurrentSource);
    }

    [Fact]
    public void Ambient_and_user_requests_are_not_queued()
    {
        _director.Play(PetLibrary.Click, fromUser: true);
        Assert.Equal(PlayResult.Rejected, _director.Request(new(A("move-1"), ActionPriority.Ambient, "x")));
        Assert.Equal(PlayResult.Played, _director.Request(new(A("click-2"), ActionPriority.User, "user")));
    }

    [Fact]
    public void Loops_until_the_event_arrives()
    {
        _director.Request(new(A("turn-1"), ActionPriority.Agent, "rule:think", UntilEvents: ["chat.replied"]));
        for (var i = 0; i < 5; i++)
        {
            PlayThrough();
            Assert.Equal("rule:think", _director.CurrentSource);
        }
        _director.StopLoop("chat.failed");   // 不是等的事件
        Assert.True(_director.Looping);
        _director.StopLoop("chat.replied");
        Assert.False(_director.Looping);
        PlayThrough();
        Assert.NotEqual("rule:think", _director.CurrentSource);
    }

    [Fact]
    public void Interrupt_policy_user_blocks_agents()
    {
        var stubborn = A("click-1") with { Interrupt = InterruptPolicy.User };
        _director.Request(new(stubborn, ActionPriority.System, "x"));
        Assert.Equal(PlayResult.Queued, _director.Request(new(A("click-2"), ActionPriority.Agent, "agent")));
        Assert.Equal(PlayResult.Played, _director.Request(new(A("click-2"), ActionPriority.User, "user")));
    }

    [Fact]
    public void Cooldown_limits_automatic_requests_only()
    {
        var slow = A("click-1") with { Cooldown = TimeSpan.FromMinutes(1) };
        Assert.Equal(PlayResult.Played, _director.Request(new(slow, ActionPriority.Agent, "agent")));
        Assert.Equal(PlayResult.Rejected, _director.Request(new(slow, ActionPriority.Agent, "agent")));
        Assert.Equal(PlayResult.Played, _director.Request(new(slow, ActionPriority.User, "user")));
        _time.Advance(TimeSpan.FromMinutes(2));
        _director.Play(PetLibrary.Idle, fromUser: false);
        Assert.Equal(PlayResult.Played, _director.Request(new(slow, ActionPriority.Agent, "agent")));
    }

    [Fact]
    public void Dragging_holds_off_everything_but_drag()
    {
        _director.BeginDrag();
        Assert.Equal(PlayResult.Rejected, _director.Request(new(A("click-1"), ActionPriority.User, "user")));
        Assert.Equal(PlayResult.Queued, _director.Request(new(A("click-1"), ActionPriority.Agent, "agent")));
        PlayThrough();
        Assert.True(_director.CurrentAction.HasRole(Roles.Drag));
        _director.EndDrag();
        Assert.True(_director.CurrentAction.HasRole(Roles.Idle));
    }
}

/// <summary>事件 → 规则 → 动作。</summary>
public class TriggerTests
{
    [Fact]
    public void Click_plays_click_role_and_says_a_click_line()
    {
        using var rig = new HostRig();
        rig.Host.Start();
        rig.Input.RaiseClicked(ClickZones.Head);
        Assert.True(rig.Director.CurrentAction.HasRole(Roles.Click));
        Assert.Equal("rule:click-react", rig.Director.CurrentSource);
        Assert.Contains(Assert.Single(rig.Voice.Said), rig.Settings.ClickLineList());
    }

    [Fact]
    public void Click_settings_still_gate_the_reaction()
    {
        using var rig = new HostRig();
        rig.Settings.ClickReact = false;
        rig.Settings.ClickBubble = false;
        rig.Host.Start();
        rig.Input.RaiseClicked();
        Assert.True(rig.Director.CurrentAction.HasRole(Roles.Idle));
        Assert.Empty(rig.Voice.Said);
    }

    [Fact]
    public void Action_lines_override_click_lines()
    {
        using var rig = new HostRig();
        var lib = rig.Director.Library;
        foreach (var id in new[] { "click-1", "click-2" }) lib.Overlay.OverrideFor(id).Lines = ["专属台词"];
        lib.ApplyOverlay(lib.Overlay, save: false);
        rig.Host.Start();
        rig.Input.RaiseClicked();
        Assert.Equal("专属台词", rig.Voice.Said.Last());
    }

    [Fact]
    public void Pomodoro_end_celebrates_with_click_fallback()
    {
        using var rig = new HostRig();
        rig.Host.Start();
        rig.Host.Pomodoro.Start();
        rig.Advance(TimeSpan.FromMinutes(rig.Settings.PomodoroWork + 1));
        Assert.Equal("rule:pomodoro-end", rig.Director.CurrentSource);
        Assert.True(rig.Director.CurrentAction.HasRole(Roles.Click));
    }

    [Fact]
    public void Chat_events_drive_agent_actions()
    {
        using var rig = new HostRig();
        rig.Host.Start();
        var bus = rig.Events;
        bus.Publish(PetEvents.ChatAction, ("action", "click-2"));
        Assert.Equal("click-2", rig.Director.CurrentAction.Id);
        Assert.Equal("rule:agent-action", rig.Director.CurrentSource);

        rig.Director.Play(PetLibrary.Idle, fromUser: false);
        bus.Publish(PetEvents.ChatReplied, ("acted", "true"));
        Assert.True(rig.Director.CurrentAction.HasRole(Roles.Idle));
        bus.Publish(PetEvents.ChatReplied, ("acted", "false"));
        Assert.Equal("rule:reply", rig.Director.CurrentSource);
    }

    [Fact]
    public void User_rules_override_and_disable_builtins()
    {
        using var rig = new HostRig();
        rig.Settings.Triggers =
        [
            new TriggerRule { Id = "click-react", On = PetEvents.Clicked, Enabled = false },
            new TriggerRule { Id = "head-pat", On = PetEvents.Clicked, Do = "click-2", Priority = "user",
                              When = new TriggerWhen { Event = new() { ["zone"] = ClickZones.Head } } },
        ];
        rig.Host.Start();
        rig.Input.RaiseClicked(ClickZones.Body);
        Assert.True(rig.Director.CurrentAction.HasRole(Roles.Idle));
        rig.Input.RaiseClicked(ClickZones.Head);
        Assert.Equal("click-2", rig.Director.CurrentAction.Id);
    }

    [Theory]
    [InlineData(23, 22, 6, true)]
    [InlineData(3, 22, 6, true)]
    [InlineData(12, 22, 6, false)]
    [InlineData(8, 8, 23, true)]
    [InlineData(23, 8, 23, false)]
    [InlineData(5, 0, 0, true)]
    public void Hours_window_can_cross_midnight(int hour, int from, int to, bool expected) =>
        Assert.Equal(expected, TriggerConditions.InHours(hour, from, to));

    [Fact]
    public void Rule_cooldown_and_wildcard()
    {
        using var rig = new HostRig();
        rig.Settings.Triggers =
        [
            new TriggerRule { Id = "any-pomodoro", On = "pomodoro.*", Do = TriggerRule.None, Say = ["收到"], Cooldown = 60 },
        ];
        rig.Host.Start();
        rig.Events.Publish(PetEvents.PomodoroStarted);
        rig.Events.Publish(PetEvents.PomodoroStopped);
        Assert.Single(rig.Voice.Said, "收到");
        rig.Time.Advance(TimeSpan.FromSeconds(61));
        rig.Events.Publish(PetEvents.PomodoroStopped);
        Assert.Equal(2, rig.Voice.Said.Count(s => s == "收到"));
    }

    [Theory]
    [InlineData(10, ClickZones.Head)]
    [InlineData(200, ClickZones.Body)]
    [InlineData(320, ClickZones.Feet)]
    public void Click_zones_split_the_figure(double y, string zone) => Assert.Equal(zone, ClickZones.At(y));
}
