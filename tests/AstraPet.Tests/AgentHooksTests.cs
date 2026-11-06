using System.Text.Json.Nodes;
using AstraPet.Core;
using AstraPet.Core.External;
using AstraPet.External;
using Microsoft.Extensions.Time.Testing;

namespace AstraPet.Tests;

/// <summary>按会话聚合 hook（design/agent-hooks §4.2）。</summary>
public class AgentSessionsTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero));
    private readonly PetSettings _settings = new();
    private readonly PetEventBus _bus = new();
    private readonly List<PetEvent> _events = new();
    private readonly List<(string Text, bool Speak)> _said = new();
    private readonly AgentSessions _sessions;

    public AgentSessionsTests()
    {
        _bus.Published += _events.Add;
        _sessions = new AgentSessions(_bus, _settings, (t, s) => _said.Add((t, s)), _time);
    }

    private void Hook(string ev, string session = "s1", string agent = AgentKinds.Claude, string? project = "Astra-Pet") =>
        _sessions.Handle(new AgentHook(agent, ev, session, project));

    private List<string> Types => _events.Select(e => e.Type).ToList();

    [Fact]
    public void Long_turn_celebrates_after_idle()
    {
        Hook(AgentHookEvents.TurnStart);
        _time.Advance(TimeSpan.FromSeconds(95));
        Hook(AgentHookEvents.TurnEnd);

        Assert.Equal([PetEvents.AgentWorking, PetEvents.AgentIdle, PetEvents.AgentDone], Types);
        Assert.Equal("95", _events[^1]["seconds"]);
        Assert.Equal("Claude Code", _events[^1]["agent"]);
        Assert.Equal("Claude Code 在 Astra-Pet 里忙完了（1 分 35 秒）", _said.Single().Text);
    }

    [Fact]
    public void Short_turn_only_goes_idle()
    {
        Hook(AgentHookEvents.TurnStart);
        _time.Advance(TimeSpan.FromSeconds(5));
        Hook(AgentHookEvents.TurnEnd);

        Assert.Equal([PetEvents.AgentWorking, PetEvents.AgentIdle], Types);
        Assert.Empty(_said);
    }

    [Fact]
    public void Second_session_keeps_pet_working()
    {
        Hook(AgentHookEvents.TurnStart, "a");
        Hook(AgentHookEvents.TurnStart, "b", AgentKinds.Codex);
        _time.Advance(TimeSpan.FromMinutes(2));
        Hook(AgentHookEvents.TurnEnd, "a");

        // 只有第一个会话开始时发 working；a 完成时 b 还在干活，不发 idle。
        Assert.Equal([PetEvents.AgentWorking, PetEvents.AgentDone], Types);
        Assert.Equal("1", _events[^1]["active"]);

        Hook(AgentHookEvents.TurnEnd, "b", AgentKinds.Codex);
        Assert.Equal(PetEvents.AgentIdle, Types[2]);
    }

    [Fact]
    public void Waiting_alerts_and_speaks_and_still_counts_the_turn()
    {
        Hook(AgentHookEvents.TurnStart);
        Hook(AgentHookEvents.Waiting);
        Assert.Equal([PetEvents.AgentWorking, PetEvents.AgentIdle, PetEvents.AgentWaiting], Types);
        Assert.Equal(("Claude Code 在 Astra-Pet 里等你确认", true), _said.Single());

        _time.Advance(TimeSpan.FromMinutes(1));
        Hook(AgentHookEvents.TurnEnd);
        Assert.Equal(PetEvents.AgentDone, Types[^1]);
    }

    [Fact]
    public void Failure_is_sad_and_interrupt_is_quiet()
    {
        Hook(AgentHookEvents.TurnStart, "a");
        Hook(AgentHookEvents.TurnFailed, "a");
        Assert.Equal(PetEvents.AgentFailed, Types[^1]);

        Hook(AgentHookEvents.TurnStart, "b", AgentKinds.Codex);
        _time.Advance(TimeSpan.FromMinutes(5));
        Hook(AgentHookEvents.Interrupted, "b", AgentKinds.Codex);
        Assert.Equal(PetEvents.AgentIdle, Types[^1]);
        Assert.Single(_said);
    }

    [Fact]
    public void Project_name_can_be_hidden()
    {
        _settings.AgentShowProject = false;
        Hook(AgentHookEvents.TurnStart);
        Hook(AgentHookEvents.TurnFailed);
        Assert.Equal("Claude Code 出错了，去看看？", _said.Single().Text);
    }

    [Fact]
    public void Stale_sessions_are_swept()
    {
        Hook(AgentHookEvents.TurnStart);
        _time.Advance(AgentSessions.Stale);
        _sessions.Sweep();
        Assert.Equal(PetEvents.AgentIdle, Types[^1]);
        Assert.Equal(0, _sessions.Working);
    }

    [Fact]
    public void Session_end_clears_and_records_last_activity()
    {
        Hook(AgentHookEvents.TurnStart);
        Hook(AgentHookEvents.SessionEnd);
        Assert.Equal(PetEvents.AgentIdle, Types[^1]);
        Assert.Equal(AgentHookEvents.SessionEnd, _sessions.LastOf(AgentKinds.Claude)!.Event);
        Assert.Null(_sessions.LastOf(AgentKinds.Codex));
    }
}

/// <summary>/v1/agent-hooks 接口和默认规则。</summary>
public class AgentHookApiTests : IDisposable
{
    private const string Token = "secret-token-1234";
    private readonly HostRig _rig = new();
    private readonly AgentPet _pet;
    private readonly ExternalApi _api;

    private sealed class AgentPet(HostRig rig) : IExternalPet
    {
        public PetDirector Director => rig.Director;
        public PetSettings Settings => rig.Settings;
        public PetEventBus Events => rig.Events;
        public AgentSessions Agents { get; } = new(rig.Events, rig.Settings, (_, _) => { }, rig.Time);
        AgentSessions? IExternalPet.Agents => Agents;
        public void Say(string text, bool speak) { }
    }

    public AgentHookApiTests()
    {
        _rig.Settings.ExternalToken = Token;
        _rig.Host.Start();
        _pet = new AgentPet(_rig);
        _api = new ExternalApi(_pet, new InlineUi(), _rig.Time);
    }

    public void Dispose() => _rig.Dispose();

    private Task<ExternalResponse> Post(string body, string path = "/v1/agent-hooks") =>
        _api.HandleAsync(new ExternalRequest("POST", path, "Bearer " + Token, null, body));

    [Fact]
    public async Task Hook_goes_through_sessions_and_rules()
    {
        var seen = new List<string>();
        _rig.Events.Published += e => seen.Add(e.Type);

        Assert.Equal(200, (await Post("{\"agent\":\"claude\",\"event\":\"turn-start\",\"session\":\"x\",\"project\":\"p\"}")).Status);
        Assert.Equal(1, _pet.Agents.Working);
        _rig.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(200, (await Post("{\"agent\":\"claude\",\"event\":\"turn-end\",\"session\":\"x\"}")).Status);

        Assert.Equal([PetEvents.AgentWorking, PetEvents.AgentIdle, PetEvents.AgentDone], seen);
        // 内置规则：忙完 → 庆祝（测试角色没有 celebrate，回退到 click），Agent 优先级。
        Assert.Equal("rule:agent-done", _rig.Director.CurrentSource);
        Assert.Equal(ActionPriority.Agent, _rig.Director.CurrentPriority);
    }

    [Theory]
    [InlineData("{\"agent\":\"cursor\",\"event\":\"turn-start\"}")]
    [InlineData("{\"agent\":\"claude\",\"event\":\"boom\"}")]
    [InlineData("{\"event\":\"turn-start\"}")]
    public async Task Rejects_unknown_agent_or_event(string body)
    {
        Assert.Equal(400, (await Post(body)).Status);
    }

    [Fact]
    public async Task Rejects_long_session_ids()
    {
        var body = new JsonObject { ["agent"] = "claude", ["event"] = "turn-start", ["session"] = new string('s', 200) };
        Assert.Equal(400, (await Post(body.ToJsonString())).Status);
    }

    [Fact]
    public async Task Hooks_have_their_own_rate_bucket()
    {
        for (var i = 0; i < ExternalApi.MaxRequestsPerSecond; i++)
            await _api.HandleAsync(new ExternalRequest("GET", "/v1/status", "Bearer " + Token, null, ""));
        // 普通接口的桶满了，hook 照样能进。
        for (var i = 0; i < ExternalApi.MaxHookRequestsPerSecond; i++)
            Assert.Equal(200, (await Post("{\"agent\":\"codex\",\"event\":\"waiting\"}")).Status);
        Assert.Equal(429, (await Post("{\"agent\":\"codex\",\"event\":\"waiting\"}")).Status);
    }

    [Fact]
    public async Task Pet_without_sessions_has_no_hook_endpoint()
    {
        var api = new ExternalApi(new RigPet(_rig), new InlineUi(), _rig.Time);
        var r = await api.HandleAsync(new ExternalRequest("POST", "/v1/agent-hooks", "Bearer " + Token, null,
            "{\"agent\":\"claude\",\"event\":\"turn-start\"}"));
        Assert.Equal(404, r.Status);
    }
}

/// <summary>往智能体的 hook 配置里写 / 删我们的条目（design/agent-hooks §5）。</summary>
public class HookJsonAgentTests : IDisposable
{
    private const string Exe = "/Applications/Astra Pet.app/Contents/MacOS/AstraPet";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "astrapet-hooks-" + Guid.NewGuid().ToString("N"));
    private readonly HookJsonAgent _agent;

    public HookJsonAgentTests()
    {
        Directory.CreateDirectory(_dir);
        _agent = new HookJsonAgent(AgentKinds.Claude, () => _dir, "settings.json", "no-such-command-astrapet",
            [("UserPromptSubmit", AgentHookEvents.TurnStart), ("Stop", AgentHookEvents.TurnEnd)]);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string FilePath => Path.Combine(_dir, "settings.json");
    private JsonObject Read() => (JsonObject)JsonNode.Parse(File.ReadAllText(FilePath))!;

    private const string Foreign = """
        {
          "model": "opus",
          "hooks": {
            "Stop": [ { "hooks": [ { "type": "command", "command": "orca-hook stop" } ] } ],
            "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "guard.sh" } ] } ]
          }
        }
        """;

    [Fact]
    public void Detects_by_config_dir()
    {
        Assert.True(_agent.Detect().Installed);
        var missing = new HookJsonAgent(AgentKinds.Codex, () => Path.Combine(_dir, "nope"), "hooks.json", "no-such-command-astrapet", []);
        Assert.False(missing.Detect().Installed);
    }

    [Fact]
    public void Binds_into_missing_file()
    {
        Assert.Equal(AgentBindState.NotBound, _agent.State(Exe));
        _agent.Bind(Exe);
        Assert.Equal(AgentBindState.Bound, _agent.State(Exe));
        var command = (string)Read()["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"]!;
        Assert.Equal($"\"{Exe}\" hook claude turn-end", command);
        Assert.False(File.Exists(FilePath + HookJsonAgent.BackupSuffix));
    }

    [Fact]
    public void Keeps_foreign_hooks_and_backs_up_once()
    {
        File.WriteAllText(FilePath, Foreign);
        _agent.Bind(Exe);
        _agent.Bind(Exe);   // 重复绑定不重复追加

        var root = Read();
        Assert.Equal("opus", (string)root["model"]!);
        var stop = root["hooks"]!["Stop"]!.AsArray();
        Assert.Equal(2, stop.Count);
        Assert.Equal("orca-hook stop", (string)stop[0]!["hooks"]![0]!["command"]!);
        Assert.Equal(Foreign, File.ReadAllText(FilePath + HookJsonAgent.BackupSuffix));

        _agent.Unbind();
        var after = Read();
        Assert.Single(after["hooks"]!["Stop"]!.AsArray());
        Assert.Null(after["hooks"]!["UserPromptSubmit"]);
        Assert.NotNull(after["hooks"]!["PreToolUse"]);
        Assert.Equal(AgentBindState.NotBound, _agent.State(Exe));
    }

    [Fact]
    public void Unbind_removes_empty_hooks_object()
    {
        File.WriteAllText(FilePath, "{\"model\":\"opus\"}");
        _agent.Bind(Exe);
        _agent.Unbind();
        Assert.Null(Read()["hooks"]);
    }

    [Fact]
    public void Moved_program_is_stale_and_rebind_fixes_it()
    {
        _agent.Bind("/old/place/AstraPet");
        Assert.Equal(AgentBindState.Stale, _agent.State(Exe));
        _agent.Bind(Exe);
        Assert.Equal(AgentBindState.Bound, _agent.State(Exe));
        Assert.Single(Read()["hooks"]!["Stop"]!.AsArray());
    }

    [Fact]
    public void Ignores_other_agents_entries()
    {
        var codex = new HookJsonAgent(AgentKinds.Codex, () => _dir, "settings.json", "x", [("Stop", AgentHookEvents.TurnEnd)]);
        codex.Bind(Exe);
        Assert.Equal(AgentBindState.NotBound, _agent.State(Exe));
        _agent.Bind(Exe);
        _agent.Unbind();
        Assert.Equal(AgentBindState.Bound, codex.State(Exe));
    }

    [Fact]
    public void Broken_config_is_never_written()
    {
        File.WriteAllText(FilePath, "{ not json");
        Assert.Equal(AgentBindState.Broken, _agent.State(Exe));
        Assert.Throws<InvalidDataException>(() => _agent.Bind(Exe));
        Assert.Equal("{ not json", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Body_takes_session_and_project_only()
    {
        var body = AgentHookCommand.BuildBody("claude", "turn-end",
            "{\"session_id\":\"abc\",\"cwd\":\"/Users/me/code/Astra-Pet/\",\"prompt\":\"secret\"}");
        Assert.Equal("abc", (string)body["session"]!);
        Assert.Equal("Astra-Pet", (string)body["project"]!);
        Assert.Null(body["prompt"]);
    }
}
