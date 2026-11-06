using System.Collections.ObjectModel;
using AstraPet.Core;
using AstraPet.Core.External;
using AstraPet.External;
using AstraPet.Import;
using Avalonia.Threading;

namespace AstraPet.ControlPanel;

/// <summary>控制台「智能体」页（design/agent-hooks §6）：识别本机的编程智能体，一键写入 / 删除 hook。</summary>
public sealed class AgentsViewModel : Observable, IDisposable
{
    private readonly AgentSessions _sessions;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };

    public AgentsViewModel(AgentSessions sessions, PetSettings settings)
    {
        _sessions = sessions;
        Settings = settings;
        Cards = new ObservableCollection<AgentCard>(AgentIntegrations.All.Select(a => new AgentCard(a, sessions, settings)));
        _sessions.Changed += Refresh;
        Settings.PropertyChanged += OnSettingChanged;
        _clock.Tick += (_, _) => RefreshStatus();
    }

    public PetSettings Settings { get; }
    public ObservableCollection<AgentCard> Cards { get; }

    /// <summary>庆祝阈值滑块（秒）。</summary>
    public double CelebrateSeconds
    {
        get => Settings.AgentCelebrateSeconds;
        set => Settings.AgentCelebrateSeconds = (int)Math.Round(value);
    }

    private void OnSettingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PetSettings.AgentCelebrateSeconds)) Raise(nameof(CelebrateSeconds));
        if (e.PropertyName == nameof(PetSettings.ExternalApi)) RefreshStatus();
    }

    /// <summary>进入页面时重新识别、重读配置文件（用户可能刚装了智能体、或者手动改过配置）。</summary>
    public void Activate()
    {
        Refresh();
        _clock.Start();
    }

    public void Deactivate() => _clock.Stop();

    public void Refresh()
    {
        foreach (var card in Cards) card.Reload();
    }

    private void RefreshStatus()
    {
        foreach (var card in Cards) card.RaiseStatus();
    }

    /// <summary>假装收到一轮：开始干活，4 秒后忙完（必定庆祝），不经过任何智能体。</summary>
    public void Test()
    {
        // 每次点都是一个新会话：连点几下也各算各的时长。
        var session = "astrapet-test-" + Guid.NewGuid().ToString("N")[..8];
        _sessions.Handle(new AgentHook(AgentKinds.Claude, AgentHookEvents.TurnStart, session, "测试"));
        DispatcherTimer.RunOnce(
            () => _sessions.Handle(new AgentHook(AgentKinds.Claude, AgentHookEvents.TurnEnd, session, "测试", AlwaysCelebrate: true)),
            TimeSpan.FromSeconds(4));
    }

    public void UnbindAll()
    {
        AgentIntegrations.UnbindAll();
        Refresh();
    }

    public void Dispose()
    {
        _clock.Stop();
        _sessions.Changed -= Refresh;
        Settings.PropertyChanged -= OnSettingChanged;
    }
}

/// <summary>一个智能体的卡片：装没装、配置在哪、绑没绑、最近一次收到什么。</summary>
public sealed class AgentCard : Observable
{
    private readonly HookJsonAgent _agent;
    private readonly AgentSessions _sessions;
    private readonly PetSettings _settings;
    private AgentDetection _detection;
    private AgentBindState _state;
    private string? _error;

    public AgentCard(HookJsonAgent agent, AgentSessions sessions, PetSettings settings)
    {
        _agent = agent;
        _sessions = sessions;
        _settings = settings;
        _detection = agent.Detect();
        _state = SafeState();
    }

    private static string ExePath => Environment.ProcessPath ?? "AstraPet";

    public string Name => _agent.Name;
    public bool Installed => _detection.Installed;
    public bool CanToggle => Installed && _state != AgentBindState.Broken;

    /// <summary>开关：打开写入 hook（顺带打开本地接口），关掉删除。</summary>
    public bool IsBound
    {
        get => _state is AgentBindState.Bound or AgentBindState.Stale;
        set
        {
            if (value == IsBound) return;
            _error = null;
            try
            {
                if (value)
                {
                    _agent.Bind(ExePath);
                    if (!_settings.ExternalApi) _settings.ExternalApi = true;
                }
                else _agent.Unbind();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _error = e.Message;
                PetLog.Write($"{(value ? "绑定" : "解除")} {Name} 失败: {e.Message}");
            }
            Reload();
        }
    }

    public string Status
    {
        get
        {
            var where = ImportSources.DisplayPath(_detection.ConfigPath);
            if (_error is not null) return _error;
            if (!Installed) return $"没有找到 {Name}";
            switch (_state)
            {
                case AgentBindState.Broken:
                    return $"配置文件格式有误，请先修好：{where}";
                case AgentBindState.NotBound:
                    return $"已检测到 · {where}";
                case AgentBindState.Stale:
                    return "绑定的程序路径过期了，关掉再打开开关就能修正";
            }
            if (!_settings.ExternalApi) return "本地接口关着，宠物收不到事件：在「模型与记忆 → 外部控制」里打开";
            if (_sessions.LastOf(_agent.Id) is { } last)
                return $"已连接 · 最近一次：{Ago(last.At)}" +
                       (last.Project is { Length: > 0 } p ? $"（{p}，{EventLabel(last.Event)}）" : $"（{EventLabel(last.Event)}）");
            return "已绑定，等待第一个事件。" + _agent.AfterBindHint;
        }
    }

    internal void Reload()
    {
        _detection = _agent.Detect();
        _state = SafeState();
        foreach (var name in new[] { nameof(Installed), nameof(CanToggle), nameof(IsBound), nameof(Status) }) Raise(name);
    }

    internal void RaiseStatus() => Raise(nameof(Status));

    private AgentBindState SafeState()
    {
        try { return _agent.State(ExePath); }
        catch (Exception) { return AgentBindState.Broken; }
    }

    private static string Ago(DateTimeOffset at)
    {
        var d = DateTimeOffset.UtcNow - at;
        return d.TotalMinutes < 1 ? "刚刚" : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} 分钟前" : d.TotalDays < 1 ? $"{(int)d.TotalHours} 小时前" : at.ToLocalTime().ToString("M 月 d 日 HH:mm");
    }

    private static string EventLabel(string ev) => ev switch
    {
        AgentHookEvents.TurnStart => "开始干活",
        AgentHookEvents.TurnEnd => "完成",
        AgentHookEvents.TurnFailed => "出错",
        AgentHookEvents.Waiting => "等确认",
        AgentHookEvents.Interrupted => "中断",
        AgentHookEvents.SessionEnd => "会话结束",
        _ => ev,
    };
}
