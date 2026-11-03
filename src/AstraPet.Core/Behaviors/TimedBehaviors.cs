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

    protected override void OnStart()
    {
        _lastChimeHour = Now.Hour;
        _lastDrink = _sittingSince = Now;
        Every(Interval, Tick);
    }

    private void Tick()
    {
        var now = Now;
        if (now.Hour != _lastChimeHour)
        {
            _lastChimeHour = now.Hour;
            if (Settings.HourlyChime) Context.Voice.Say($"现在是 {now:HH:mm}，整点啦～");
        }

        if (!Settings.DrinkReminder) _lastDrink = now;
        else if (now - _lastDrink >= TimeSpan.FromMinutes(Settings.DrinkMinutes))
        {
            _lastDrink = now;
            Context.Voice.Say(Pick(DrinkLines));
        }

        // 读不到空闲时长的平台当作一直在用，只按时间提醒。
        if (!Settings.SitReminder || Context.Monitor.IdleTime() > AwayResetsSitting) _sittingSince = now;
        else if (now - _sittingSince >= TimeSpan.FromMinutes(Settings.SitMinutes))
        {
            _sittingSince = now;
            Context.Voice.Say(Pick(SitLines));
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
    }

    public void Stop()
    {
        if (EndsAt is null) return;
        EndsAt = null;
        Context.Voice.Say("番茄钟停下啦");
    }

    private void Tick()
    {
        if (EndsAt is not { } end || Now < end) return;
        Resting = !Resting;
        EndsAt = Now.AddMinutes(Resting ? Settings.PomodoroRest : Settings.PomodoroWork);
        Context.Voice.Say(Resting
            ? $"专注结束！休息 {Settings.PomodoroRest} 分钟吧"
            : $"休息好了，再专注 {Settings.PomodoroWork} 分钟！");
        Context.Director.Play(PetLibrary.Click, fromUser: true);
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
        Settings.PowerSavingActive = Settings.PowerSaving && idle > TimeSpan.FromMinutes(Settings.PowerSavingMinutes);

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
            if (playing && !Settings.PowerSavingActive)
                Context.Director.Play(PetLibrary.Sing, fromUser: false);
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
            Context.Voice.Say($"有新版本 {info.Version} 啦～去控制台「常规」里一键更新吧", speak: false);
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
