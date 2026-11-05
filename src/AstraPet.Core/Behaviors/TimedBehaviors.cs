namespace AstraPet.Core.Behaviors;

/// <summary>每 15 秒检查一次：整点报时、喝水提醒、久坐提醒。</summary>
public sealed class ReminderBehavior : PetBehavior
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    /// <summary>离开电脑这么久算起身活动过，久坐计时重来。</summary>
    public static readonly TimeSpan AwayResetsSitting = TimeSpan.FromMinutes(5);

    public static readonly string[] DrinkLines = ["该喝水啦，补充一下水分吧 💧", "喝口水歇一歇～", "提醒你喝水！我也渴了"];
    public static readonly string[] SitLines = ["坐了好久了，起来活动一下吧", "伸个懒腰，转转脖子～", "站起来走两步，我等你"];

    private int _lastChimeHour;
    private DateTimeOffset _lastDrink;
    private DateTimeOffset _sittingSince;
    private DateTimeOffset _usingSince;

    /// <summary>
    /// 用户连续用电脑多久了（离开超过 <see cref="AwayResetsSitting"/> 重新算），给对话的情境层用（03 §3.5）。
    /// 和久坐提醒开没开无关；平台读不到空闲时长时为 null（没法知道用户离开过）。
    /// </summary>
    public TimeSpan? ContinuousUse => Context is { Capabilities.IdleDetection: true } ? Now - _usingSince : null;

    protected override void OnStart()
    {
        _lastChimeHour = Now.Hour;
        _lastDrink = _sittingSince = _usingSince = Now;
        Every(Interval, Tick);
    }

    private void Tick()
    {
        var now = Now;
        if (now.Hour != _lastChimeHour)
        {
            _lastChimeHour = now.Hour;
            Context.Events.Publish(PetEvents.Hour, ("hour", now.Hour.ToString()));
            if (Settings.HourlyChime) SayInCharacter($"整点报时：现在 {now:HH:mm} 了，告诉用户一声（可以顺口说点和这个时间有关的）", $"现在是 {now:HH:mm}，整点啦～");
        }

        if (!Settings.DrinkReminder) _lastDrink = now;
        else if (now - _lastDrink >= TimeSpan.FromMinutes(Settings.DrinkMinutes))
        {
            _lastDrink = now;
            SayInCharacter("喝水提醒：用户有一阵子没喝水了，提醒TA喝口水", Pick(DrinkLines));
            Context.Events.Publish(PetEvents.Drink);
        }

        if (Context.Monitor.IdleTime() > AwayResetsSitting) _usingSince = now;

        // 读不到空闲时长的平台当作一直在用，只按时间提醒。
        if (!Settings.SitReminder || Context.Monitor.IdleTime() > AwayResetsSitting) _sittingSince = now;
        else if (now - _sittingSince >= TimeSpan.FromMinutes(Settings.SitMinutes))
        {
            _sittingSince = now;
            SayInCharacter($"久坐提醒：用户已经连续坐了 {Settings.SitMinutes} 分钟左右，提醒TA起来活动一下", Pick(SitLines));
            Context.Events.Publish(PetEvents.Sit);
        }
    }
}

/// <summary>番茄钟：专注 / 休息交替，每 15 秒看一次是否到点。</summary>
public sealed class PomodoroBehavior : PetBehavior
{
    /// <summary>当前阶段结束的时间，null 表示没在计时。</summary>
    public DateTimeOffset? EndsAt { get; private set; }
    public bool Resting { get; private set; }
    public bool Running => EndsAt is not null;

    public string Status => EndsAt is { } end
        ? $"{(Resting ? "休息中" : "专注中")} · 剩余 {Math.Max(0, (end - Now).TotalMinutes):0} 分钟"
        : "未开始";

    protected override void OnStart() => Every(ReminderBehavior.Interval, Tick);

    public void Start()
    {
        Resting = false;
        EndsAt = Now.AddMinutes(Settings.PomodoroWork);
        Context.Voice.Say($"开始专注 {Settings.PomodoroWork} 分钟，我陪你！");
        Context.Events.Publish(PetEvents.PomodoroStarted, ("minutes", Settings.PomodoroWork.ToString()));
    }

    public void Stop()
    {
        if (EndsAt is null) return;
        EndsAt = null;
        Context.Voice.Say("番茄钟停下啦");
        Context.Events.Publish(PetEvents.PomodoroStopped);
    }

    private void Tick()
    {
        if (EndsAt is not { } end || Now < end) return;
        Resting = !Resting;
        EndsAt = Now.AddMinutes(Resting ? Settings.PomodoroRest : Settings.PomodoroWork);
        SayInCharacter(Resting
                ? $"番茄钟：一段专注刚结束，告诉用户可以休息 {Settings.PomodoroRest} 分钟了"
                : $"番茄钟：休息时间到了，告诉用户接着专注 {Settings.PomodoroWork} 分钟",
            Resting ? $"专注结束！休息 {Settings.PomodoroRest} 分钟吧" : $"休息好了，再专注 {Settings.PomodoroWork} 分钟！");
        Context.Events.Publish(Resting ? PetEvents.FocusEnded : PetEvents.RestEnded,
            ("minutes", (Resting ? Settings.PomodoroRest : Settings.PomodoroWork).ToString()));
    }
}

/// <summary>每 5 秒：用户离开太久进省电模式；检测到在放声音时唱歌。</summary>
public sealed class PresenceBehavior : PetBehavior
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private bool _probingAudio;

    protected override void OnStart() => Every(Interval, Tick);

    private void Tick()
    {
        // 读不到空闲时长的平台不进省电模式。
        var idle = Context.Monitor.IdleTime() ?? TimeSpan.Zero;
        var saving = Settings.PowerSaving && idle > TimeSpan.FromMinutes(Settings.PowerSavingMinutes);
        if (saving != Settings.PowerSavingActive)
        {
            Settings.PowerSavingActive = saving;
            Context.Events.Publish(saving ? PetEvents.PowerSaving : PetEvents.PowerResumed);
        }

        if (!Settings.MusicDetect || !Context.Capabilities.AudioDetection)
        {
            Settings.MusicPlaying = false;
            return;
        }
        if (_probingAudio) return;
        _probingAudio = true;
        // 可能要起子进程，平台实现自己放到线程池上跑，这里等结果回来再切回 UI 线程。
        Context.Monitor.IsAudioPlayingAsync().ContinueWith(t => Context.Ui.Post(() =>
        {
            _probingAudio = false;
            var playing = t.IsCompletedSuccessfully && t.Result;
            if (playing == Settings.MusicPlaying) return;
            Settings.MusicPlaying = playing;
            PetLog.Write(playing ? "检测到在放音乐，开始唱歌" : "音乐停了");
            Context.Events.Publish(playing ? PetEvents.MusicStarted : PetEvents.MusicStopped);
        }), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}

/// <summary>
/// 自动检查更新：启动半分钟后查一次（等宠物打完招呼、网络起来），之后每 6 小时一次。
/// 发现新版本用气泡提醒；同一个版本只提醒一次，免得每次启动都唠叨。
/// </summary>
public sealed class UpdateNotifyBehavior : PetBehavior
{
    public static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override void OnStart() => Every(Interval, AutoCheck, FirstCheck);

    /// <summary>检查更新。<paramref name="automatic"/> 为真（定时检查）时，发现新版本用气泡提醒一次；手动检查的结果在控制台里看。</summary>
    public async Task<UpdateInfo?> CheckAsync(bool automatic)
    {
        var info = await Context.Updates.CheckAsync();
        if (info is not null && automatic && Settings.NotifiedUpdate != info.Version)
        {
            Settings.NotifiedUpdate = info.Version;
            Context.Events.Publish(PetEvents.UpdateFound, ("version", info.Version));
            Context.Voice.SayLater($"有新版本 {info.Version} 啦～去控制台「常规」里一键更新吧", speak: false);
        }
        return info;
    }

    private void AutoCheck()
    {
        // 没打开自动检查，或者根本没配服务器：安静地跳过，不往日志里刷。
        if (!Settings.AutoCheckUpdate || Context.Updates.Server.Length == 0) return;
        _ = CheckAsync(automatic: true);
    }
}

/// <summary>
/// 主动搭话：每 30 秒看一次。用户在电脑前、有一阵子没理宠物时，按设置的平均间隔（加随机）请 Agent 开口；
/// 用户离开电脑一阵再回来，也请它打个招呼。说什么、说不说都由模型按当前宠物的性格决定（见 <see cref="AstraPet.Ai.PetAgent.SpeakUpAsync"/>）。
/// 下面这些时候不开口：宠物藏起来 / 暂停、省电、专注番茄钟、睡觉、深夜、刚互动过。
/// </summary>
public sealed class ProactiveChatBehavior : PetBehavior
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    /// <summary>空闲超过这么久算离开；回来时打招呼。</summary>
    public static readonly TimeSpan AwayAfter = TimeSpan.FromMinutes(10);
    /// <summary>刚互动或聊过天，这么久之内不插话。</summary>
    public static readonly TimeSpan QuietAfterActivity = TimeSpan.FromMinutes(3);
    /// <summary>赶上不方便开口时，过一会儿再看。</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(2);

    /// <summary>没什么特别的事时，随机挑一个起因。</summary>
    public static readonly string[] IdleCues =
    [
        "有一阵子没和用户说话了，你有点无聊，想找用户搭句话",
        "你突然想到一件和自己的喜好、日常有关的小事，想跟用户分享",
        "你想关心一下用户：在忙什么、累不累",
        "你想根据现在的时间和【现在】里的情况随口说点什么",
        "你想起了关于用户的某件事（如果【关于用户，你记得】里有），想顺口提一下；没有的话就随便聊点别的",
        "你自己在桌面上待着，冒出了一个小念头或者小情绪，想说出来",
    ];

    private readonly PomodoroBehavior _pomodoro;
    private DateTimeOffset _next;
    private DateTimeOffset _lastActivity;
    private TimeSpan? _awayFor;

    public ProactiveChatBehavior(PomodoroBehavior pomodoro) => _pomodoro = pomodoro;

    /// <summary>下一次主动开口的时间（测试用）。</summary>
    public DateTimeOffset NextAt => _next;

    protected override void OnStart()
    {
        _lastActivity = Now;
        Schedule();
        Context.Events.Published += OnEvent;
        Every(Interval, Tick);
    }

    private void OnEvent(PetEvent e)
    {
        if (e.Type is PetEvents.Clicked or PetEvents.DoubleClicked or PetEvents.Petted or PetEvents.DragEnded
            or PetEvents.ChatThinking or PetEvents.ChatReplied or PetEvents.Fed or PetEvents.PlayEnded or PetEvents.Woken)
            _lastActivity = Now;
    }

    private void Schedule() =>
        _next = Now + TimeSpan.FromMinutes(Settings.ProactiveMinutes * (0.7 + Context.Random.NextDouble() * 0.8));

    private void Tick()
    {
        if (!Settings.ProactiveChat)
        {
            _awayFor = null;
            if (_next < Now) Schedule();
            return;
        }

        // 离开 → 回来：记下离开多久，回来后（刚有操作）打个招呼。读不到空闲时长的平台没有这一项。
        var idle = Context.Monitor.IdleTime();
        if (idle is { } away && away >= AwayAfter)
        {
            _awayFor = away;
            return;
        }
        if (_awayFor is { } gone && idle < TimeSpan.FromMinutes(1))
        {
            _awayFor = null;
            if (CanTalk(ignoreQuiet: true))
            {
                Talk($"用户离开电脑大约 {Math.Round(gone.TotalMinutes)} 分钟，刚刚回来");
                return;
            }
        }

        if (Now < _next) return;
        if (!CanTalk(ignoreQuiet: false))
        {
            _next = Now + RetryAfter;
            return;
        }
        Talk(Pick(IdleCues));
    }

    private bool CanTalk(bool ignoreQuiet)
    {
        if (!Settings.Visible || Settings.Paused || Settings.PowerSavingActive) return false;
        if (_pomodoro.Running && !_pomodoro.Resting) return false;
        if (Context.Director.Sleeping) return false;
        if (Now.Hour is >= 1 and < 7) return false;   // 深夜别吵
        return ignoreQuiet || Now - _lastActivity >= QuietAfterActivity;
    }

    private void Talk(string cue)
    {
        Schedule();
        if (Context.Commands.SpeakUp(cue)) PetLog.Write($"主动搭话：{cue}");
    }

    public override void Dispose()
    {
        if (Context is not null) Context.Events.Published -= OnEvent;
        base.Dispose();
    }
}
