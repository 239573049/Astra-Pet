using AstraPet.Ai;
using AstraPet.Core;
using AstraPet.Platform;
using Avalonia.Threading;

namespace AstraPet.Pet;

/// <summary>
/// 宠物的「行为层」：把宠物窗口的点击、摸头、右键菜单，和智能体、气泡、朗读、提醒、
/// 省电模式、听歌检测串起来。<see cref="PetWindow"/> 只管画和输入，这里决定怎么回应。
/// </summary>
public sealed class PetCompanion : IDisposable
{
    private static readonly string[] PettingLines = ["嘿嘿，好舒服～", "再摸摸～", "呼噜呼噜…", "头发要被你揉乱啦"];
    private static readonly string[] DrinkLines = ["该喝水啦，补充一下水分吧 💧", "喝口水歇一歇～", "提醒你喝水！我也渴了"];
    private static readonly string[] SitLines = ["坐了好久了，起来活动一下吧", "伸个懒腰，转转脖子～", "站起来走两步，我等你"];

    private readonly PetWindow _pet;
    private readonly PetDirector _director;
    private readonly PetSettings _settings;
    private readonly Random _random = new();
    private readonly DispatcherTimer _monitor = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };

    private bool _probingAudio;
    private int _lastChimeHour = DateTime.Now.Hour;
    private DateTime _lastDrink = DateTime.Now;
    private DateTime _sittingSince = DateTime.Now;

    public PetCompanion(PetWindow pet, PetDirector director, PetSettings settings)
    {
        _pet = pet;
        _director = director;
        _settings = settings;
        Agent = new PetAgent(settings);
        Bubble = new BubbleWindow(pet, settings);
        Updates = new UpdateChecker(settings);

        _pet.Clicked += OnClicked;
        _pet.DoubleClicked += () => { if (_settings.DoubleClickChat) ChatRequested?.Invoke(); };
        _pet.Petted += OnPetted;
        _pet.MenuCommand += OnMenuCommand;
        _pet.MenuLabel = MenuLabelOf;
        _pet.Closed += (_, _) => Dispose();
        _pet.Opened += (_, _) => Greet();

        Agent.Busy += busy => { if (busy) Bubble.Say("…", 0); };
        Agent.Remembered += added => Say("我记住啦：" + (added[0].StartsWith("用户") ? "你" + added[0][2..] : added[0]), speak: false);

        _monitor.Tick += (_, _) => Monitor();
        _monitor.Start();
        _clock.Tick += (_, _) => Clock();
        _clock.Start();

        // 更新检测：启动半分钟后查一次（等宠物打完招呼、网络起来），之后每 6 小时一次。
        _updateTimer.Tick += (_, _) => AutoCheckUpdate();
        _updateTimer.Start();
        _pet.Opened += (_, _) => DispatcherTimer.RunOnce(AutoCheckUpdate, TimeSpan.FromSeconds(30), DispatcherPriority.Background);
    }

    public UpdateChecker Updates { get; }

    /// <summary>
    /// 检查更新。<paramref name="automatic"/> 为真（定时检查）时，发现新版本用气泡提醒一次；
    /// 同一个版本只提醒一次，免得每次启动都唠叨。手动检查的结果在控制台里看。
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(bool automatic)
    {
        var info = await Updates.CheckAsync();
        if (info is not null && automatic && _settings.NotifiedUpdate != info.Version)
        {
            _settings.NotifiedUpdate = info.Version;
            Say($"有新版本 {info.Version} 啦～去控制台「常规」里下载吧", speak: false);
        }
        return info;
    }

    private void AutoCheckUpdate()
    {
        // 没打开自动检查，或者根本没配服务器：安静地跳过，不往日志里刷。
        if (!_settings.AutoCheckUpdate || Updates.Server.Length == 0) return;
        _ = CheckForUpdateAsync(automatic: true);
    }

    public PetAgent Agent { get; }
    public BubbleWindow Bubble { get; }

    /// <summary>需要打开对话框（双击、菜单）。</summary>
    public event Action? ChatRequested;

    public void RequestChat() => ChatRequested?.Invoke();

    /// <summary>需要打开控制台（菜单）。</summary>
    public event Action? ConsoleRequested;

    // ───── 番茄钟 ─────

    /// <summary>番茄钟当前阶段结束的时间，null 表示没在计时。</summary>
    public DateTime? PomodoroEndsAt { get; private set; }
    public bool PomodoroResting { get; private set; }
    public bool PomodoroRunning => PomodoroEndsAt is not null;

    public string PomodoroStatus => PomodoroEndsAt is { } end
        ? $"{(PomodoroResting ? "休息中" : "专注中")} · 剩余 {Math.Max(0, (end - DateTime.Now).TotalMinutes):0} 分钟"
        : "未开始";

    public void StartPomodoro()
    {
        PomodoroResting = false;
        PomodoroEndsAt = DateTime.Now.AddMinutes(_settings.PomodoroWork);
        Say($"开始专注 {_settings.PomodoroWork} 分钟，我陪你！");
    }

    public void StopPomodoro()
    {
        if (PomodoroEndsAt is null) return;
        PomodoroEndsAt = null;
        Say("番茄钟停下啦");
    }

    // ───── 说话 / 对话 ─────

    /// <summary>头顶冒一句话；<paramref name="speak"/> 为真且开了朗读提醒时同时念出来。</summary>
    public void Say(string text, bool speak = true)
    {
        Bubble.Say(text);
        if (speak && _settings.SpeakReminders) Speak(text);
    }

    public void Speak(string text) =>
        Speech.Speak(text, _settings.Voice, _settings.SpeechRate, _settings.SpeechVolume);

    /// <summary>发给智能体，回复显示在气泡里并按设置朗读。出错时气泡里说明原因，并把异常抛给调用方。</summary>
    public async Task<string> ChatAsync(string text)
    {
        try
        {
            var reply = await Agent.AskAsync(text);
            Bubble.Say(reply);
            if (_settings.SpeakReplies) Speak(reply);
            _director.Play(PetLibrary.Click, fromUser: true);
            return reply;
        }
        catch (Exception e)
        {
            PetLog.Write($"对话失败: {e.Message}");
            Bubble.Say("呜…我好像连不上大脑了，去控制台「智能」页看看吧");
            throw;
        }
    }

    // ───── 事件处理 ─────

    private void OnClicked()
    {
        if (!_settings.ClickBubble) return;
        var lines = _settings.ClickLineList();
        if (lines.Length > 0) Say(lines[_random.Next(lines.Length)]);
    }

    private void OnPetted()
    {
        _director.Play(PetLibrary.Click, fromUser: true);
        Say(PettingLines[_random.Next(PettingLines.Length)]);
    }

    private string MenuLabelOf(string id) => id switch
    {
        MenuEntry.Pause => _settings.Paused ? "继续动画" : "暂停动画",
        MenuEntry.StayStill => _settings.StayStill ? "✓ 不移动" : "不移动",
        MenuEntry.Pomodoro => PomodoroRunning ? "停止番茄钟" : "开始番茄钟",
        MenuEntry.Mute => _settings.SpeakReplies ? "✓ 朗读回复" : "朗读回复",
        _ => MenuEntry.LabelOf(id),
    };

    private void OnMenuCommand(string id)
    {
        switch (id)
        {
            case MenuEntry.Chat: ChatRequested?.Invoke(); break;
            case MenuEntry.Console: ConsoleRequested?.Invoke(); break;
            case MenuEntry.Pause: _settings.Paused = !_settings.Paused; break;
            case MenuEntry.StayStill: _settings.StayStill = !_settings.StayStill; break;
            case MenuEntry.Pomodoro:
                if (PomodoroRunning) StopPomodoro(); else StartPomodoro();
                break;
            case MenuEntry.Mute: _settings.SpeakReplies = !_settings.SpeakReplies; break;
            case MenuEntry.Center: _pet.ResetPosition(); break;
            case MenuEntry.Hide: _settings.Visible = false; break;
        }
    }

    private void Greet()
    {
        if (!_settings.DailyGreeting) return;
        var hour = DateTime.Now.Hour;
        var text = hour switch
        {
            < 5 => "这么晚还没睡呀，早点休息哦",
            < 11 => "早上好！今天也一起加油吧",
            < 14 => "中午好～记得吃饭哦",
            < 18 => "下午好，喝杯茶歇一会儿？",
            _ => "晚上好，今天辛苦啦",
        };
        Dispatcher.UIThread.Post(() => Say(text), DispatcherPriority.Background);
    }

    /// <summary>每 5 秒：省电模式、听歌检测。</summary>
    private void Monitor()
    {
        var idle = SystemMonitor.IdleSeconds();
        _settings.PowerSavingActive = _settings.PowerSaving && idle > _settings.PowerSavingMinutes * 60;

        // 离开电脑 5 分钟算起身活动过，久坐计时重来。
        if (idle > 300) _sittingSince = DateTime.Now;

        if (!_settings.MusicDetect || !SystemMonitor.CanDetectAudio)
        {
            _settings.MusicPlaying = false;
            return;
        }
        if (_probingAudio) return;
        _probingAudio = true;
        // pmset 要起子进程，放到线程池上跑。
        Task.Run(SystemMonitor.IsAudioPlaying).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            _probingAudio = false;
            var playing = t.IsCompletedSuccessfully && t.Result;
            if (playing == _settings.MusicPlaying) return;
            _settings.MusicPlaying = playing;
            PetLog.Write(playing ? "检测到在放音乐，开始唱歌" : "音乐停了");
            if (playing && !_settings.PowerSavingActive)
                _director.Play(PetLibrary.Sing, fromUser: false);
        }));
    }

    /// <summary>每 15 秒：整点报时、喝水、久坐、番茄钟。</summary>
    private void Clock()
    {
        var now = DateTime.Now;
        if (now.Hour != _lastChimeHour)
        {
            _lastChimeHour = now.Hour;
            if (_settings.HourlyChime) Say($"现在是 {now:HH:mm}，整点啦～");
        }

        if (!_settings.DrinkReminder) _lastDrink = now;
        else if (now - _lastDrink >= TimeSpan.FromMinutes(_settings.DrinkMinutes))
        {
            _lastDrink = now;
            Say(DrinkLines[_random.Next(DrinkLines.Length)]);
        }

        if (!_settings.SitReminder) _sittingSince = now;
        else if (now - _sittingSince >= TimeSpan.FromMinutes(_settings.SitMinutes))
        {
            _sittingSince = now;
            Say(SitLines[_random.Next(SitLines.Length)]);
        }

        if (PomodoroEndsAt is { } end && now >= end)
        {
            PomodoroResting = !PomodoroResting;
            PomodoroEndsAt = now.AddMinutes(PomodoroResting ? _settings.PomodoroRest : _settings.PomodoroWork);
            Say(PomodoroResting
                ? $"专注结束！休息 {_settings.PomodoroRest} 分钟吧"
                : $"休息好了，再专注 {_settings.PomodoroWork} 分钟！");
            _director.Play(PetLibrary.Click, fromUser: true);
        }
    }

    public void Dispose()
    {
        _monitor.Stop();
        _clock.Stop();
        _updateTimer.Stop();
        Speech.Stop();
        Bubble.Close();
    }
}
