namespace AstraPet.Core.External;

/// <summary>编程智能体（Claude Code、Codex……）的 id 和显示名（design/agent-hooks §3）。</summary>
public static class AgentKinds
{
    public const string Claude = "claude";
    public const string Codex = "codex";
    public const string Dsh = "dsh";

    public static readonly IReadOnlyList<(string Id, string Name)> All =
    [
        (Claude, "Claude Code"),
        (Codex, "Codex"),
        (Dsh, "DeepSeek Harness"),
    ];

    public static bool IsKnown(string id) => All.Any(a => a.Id == id);
    public static string NameOf(string id) => All.FirstOrDefault(a => a.Id == id).Name ?? id;
}

/// <summary>转发命令发来的事件名（写进 hook 配置时就定好，不看智能体自己的事件名）。</summary>
public static class AgentHookEvents
{
    public const string TurnStart = "turn-start";
    public const string TurnEnd = "turn-end";
    public const string TurnFailed = "turn-failed";
    public const string Waiting = "waiting";
    public const string Interrupted = "interrupted";
    public const string SessionEnd = "session-end";

    public static readonly IReadOnlyList<string> All = [TurnStart, TurnEnd, TurnFailed, Waiting, Interrupted, SessionEnd];
}

/// <summary>一次 hook：哪个智能体、什么事、哪个会话、在哪个项目（目录名）。</summary>
/// <param name="AlwaysCelebrate">不看这一轮多久都庆祝（控制台「测试一下」用）。</param>
public sealed record AgentHook(string Agent, string Event, string Session, string? Project, bool AlwaysCelebrate = false);

/// <summary>
/// 按会话聚合各智能体的 hook（design/agent-hooks §4）：同时开几个会话时一个结束不停掉另一个的思考，
/// 一轮够久才庆祝，等确认时提醒。聚合后发 agent.* 事件交给触发规则，冒泡由这里直接说（走 SayLater，不打断聊天）。
/// 只在 UI 线程上用。
/// </summary>
public sealed class AgentSessions
{
    /// <summary>这么久没有任何事件就当会话没了（智能体被强杀、hook 丢了）。</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(30);

    private readonly PetEventBus _events;
    private readonly PetSettings _settings;
    private readonly Action<string, bool> _say;
    private readonly TimeProvider _time;
    private readonly Dictionary<(string Agent, string Session), Session> _sessions = new();
    private readonly Dictionary<string, AgentActivity> _last = new();

    public AgentSessions(PetEventBus events, PetSettings settings, Action<string, bool> say, TimeProvider? time = null)
    {
        _events = events;
        _settings = settings;
        _say = say;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>有会话的状态变化或收到事件（控制台刷新「最近一次」用）。</summary>
    public event Action? Changed;

    /// <summary>正在工作（不含空闲、等确认）的会话数。</summary>
    public int Working => _sessions.Values.Count(s => s.State == SessionState.Working);

    /// <summary>某个智能体最近一次收到的事件；没收到过为 null。</summary>
    public AgentActivity? LastOf(string agent) => _last.GetValueOrDefault(agent);

    public void Handle(AgentHook hook)
    {
        Sweep();
        var now = _time.GetUtcNow();
        var key = (hook.Agent, hook.Session);
        _last[hook.Agent] = new AgentActivity(hook.Event, hook.Project, now);
        var wasWorking = Working;
        _sessions.TryGetValue(key, out var session);
        var name = AgentKinds.NameOf(hook.Agent);
        var where = _settings.AgentShowProject && hook.Project is { Length: > 0 } p ? $" 在 {p} 里" : " ";

        switch (hook.Event)
        {
            case AgentHookEvents.TurnStart:
                session ??= new Session();
                session.State = SessionState.Working;
                session.StartedAt = now;
                session.LastAt = now;
                _sessions[key] = session;
                if (wasWorking == 0) Publish(PetEvents.AgentWorking, hook, null);
                break;

            case AgentHookEvents.TurnEnd:
            case AgentHookEvents.Interrupted:
            {
                // 中途等过确认也算这一轮（确认之后没有事件，状态停在 Waiting 直到 Stop）。
                var seconds = session is { State: not SessionState.Idle, StartedAt: { } started }
                    ? (now - started).TotalSeconds : 0;
                if (session is not null) { session.State = SessionState.Idle; session.LastAt = now; }
                IdleIfDone(hook);
                if (hook.Event == AgentHookEvents.TurnEnd && (hook.AlwaysCelebrate || seconds >= _settings.AgentCelebrateSeconds))
                {
                    Publish(PetEvents.AgentDone, hook, seconds);
                    _say($"{name}{where}忙完了（{Duration(seconds)}）", false);
                }
                break;
            }

            case AgentHookEvents.TurnFailed:
                if (session is not null) { session.State = SessionState.Idle; session.LastAt = now; }
                IdleIfDone(hook);
                Publish(PetEvents.AgentFailed, hook, null);
                _say($"{name}{where}出错了，去看看？", false);
                break;

            case AgentHookEvents.Waiting:
                session ??= new Session { StartedAt = now };
                session.State = SessionState.Waiting;
                session.LastAt = now;
                _sessions[key] = session;
                // 等确认的会话不算工作中：要是只剩它，思考先收起来，招手更显眼。
                IdleIfDone(hook);
                Publish(PetEvents.AgentWaiting, hook, null);
                _say($"{name}{where}等你确认", _settings.AgentSpeakWaiting);
                break;

            case AgentHookEvents.SessionEnd:
                _sessions.Remove(key);
                IdleIfDone(hook);
                break;
        }
        Changed?.Invoke();

        void IdleIfDone(AgentHook h)
        {
            if (wasWorking > 0 && Working == 0) Publish(PetEvents.AgentIdle, h, null);
        }
    }

    /// <summary>清掉太久没动静的会话；最后一个工作中的会话被清掉时发 agent.idle。定时调用（每分钟）。</summary>
    public void Sweep()
    {
        var now = _time.GetUtcNow();
        var wasWorking = Working;
        foreach (var key in _sessions.Where(s => now - s.Value.LastAt >= Stale).Select(s => s.Key).ToList())
            _sessions.Remove(key);
        if (wasWorking > 0 && Working == 0)
            _events.Publish(PetEvents.AgentIdle);
    }

    /// <summary>发 agent.* 事件，数据给触发规则用：agent（显示名）、project、active（还剩几个在工作）、seconds（这一轮多久）。</summary>
    private void Publish(string type, AgentHook hook, double? seconds)
    {
        var data = new List<(string, string)>
        {
            ("agent", AgentKinds.NameOf(hook.Agent)),
            ("project", hook.Project ?? ""),
            ("active", Working.ToString()),
        };
        if (seconds is { } s) data.Add(("seconds", ((int)s).ToString()));
        _events.Publish(type, [.. data]);
    }

    private static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} 分 {t.Seconds} 秒" : $"{t.Seconds} 秒";
    }

    private enum SessionState { Idle, Working, Waiting }

    private sealed class Session
    {
        public SessionState State;
        public DateTimeOffset? StartedAt;
        public DateTimeOffset LastAt;
    }
}

/// <summary>某个智能体最近一次的事件（控制台显示「最近一次：2 分钟前（Astra-Pet，完成）」）。</summary>
public sealed record AgentActivity(string Event, string? Project, DateTimeOffset At);
