using AstraPet.Core;
using AstraPet.Core.Behaviors;
using AstraPet.Platform;
using Microsoft.Extensions.Time.Testing;

namespace AstraPet.Tests;

internal sealed class FakeVoice : IPetVoice
{
    public List<string> Said { get; } = new();
    public void Say(string text, bool speak = true) => Said.Add(text);
}

internal sealed class FakeCommands : IPetCommands
{
    public List<string> Calls { get; } = new();
    public void OpenChat() => Calls.Add(nameof(OpenChat));
    public void OpenConsole(string? page = null) => Calls.Add(nameof(OpenConsole));
    public void ResetPosition() => Calls.Add(nameof(ResetPosition));
    /// <summary>为真时假装智能体接下了主动开口（记下起因）；默认为假，提醒和问候走固定台词。</summary>
    public bool AgentAvailable { get; set; }
    public List<string> Cues { get; } = new();
    public List<string> Reminders { get; } = new();
    public bool SpeakUp(string cue, string? fallback = null)
    {
        if (!AgentAvailable) return false;
        (fallback is null ? Cues : Reminders).Add(cue);
        return true;
    }
}

internal sealed class FakeMonitor : ISystemMonitor
{
    public TimeSpan? Idle { get; set; } = TimeSpan.Zero;
    public bool Audio { get; set; }
    public TimeSpan? IdleTime() => Idle;
    public Task<bool> IsAudioPlayingAsync() => Task.FromResult(Audio);
}

/// <summary>测试里没有 UI 线程，直接在当前线程上执行。</summary>
internal sealed class InlineUi : IUiDispatcher
{
    public void Post(Action action) => action();
}

/// <summary>搭一个完整的行为宿主：假时钟、假声音、假监测、内存里的动画。</summary>
internal sealed class HostRig : IDisposable
{
    private readonly TempLibrary _dir = new();

    public FakeTimeProvider Time { get; }
    public FakeVoice Voice { get; } = new();
    public FakeCommands Commands { get; } = new();
    public FakeMonitor Monitor { get; } = new();
    public PetInput Input { get; } = new();
    public PetSettings Settings { get; } = new() { AutoCheckUpdate = false, DailyGreeting = false };
    public PetDirector Director { get; }
    public PetHost Host { get; }
    public PetEventBus Events { get; } = new();

    public HostRig(DateTimeOffset? start = null, PlatformCapabilities? caps = null, IGlobalHotkey? hotkey = null)
    {
        Time = new FakeTimeProvider(start ?? new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));
        Director = new PetDirector(PetLibrary.Load(_dir.Dir, new FakeDecoder()), Settings, new Random(1), Time);
        Director.Start();
        Host = new PetHost(new PetContext
        {
            Settings = Settings,
            Director = Director,
            Input = Input,
            Voice = Voice,
            Commands = Commands,
            Monitor = Monitor,
            Capabilities = caps ?? new PlatformCapabilities { AudioDetection = true, IdleDetection = true },
            Updates = new UpdateChecker(Settings, new FakeFeed(null), "test-x64", ["zip"]),
            Time = Time,
            Ui = new InlineUi(),
            Random = new Random(1),
            Events = Events,
            Hotkey = hotkey ?? NoGlobalHotkey.Instance,
        });
    }

    /// <summary>按 5 秒一步往前拨（最短的定时器周期），让每个定时器都按真实节奏触发。</summary>
    public void Advance(TimeSpan span)
    {
        var step = TimeSpan.FromSeconds(5);
        for (var t = TimeSpan.Zero; t < span; t += step) Time.Advance(step);
    }

    public void Dispose()
    {
        Host.Dispose();
        _dir.Dispose();
    }
}

public class ReminderTests
{
    [Fact]
    public void Drink_reminder_fires_once_per_interval()
    {
        using var rig = new HostRig();
        rig.Settings.DrinkReminder = true;
        rig.Settings.DrinkMinutes = 60;
        rig.Host.Start();

        rig.Advance(TimeSpan.FromMinutes(59));
        Assert.DoesNotContain(rig.Voice.Said, ReminderBehavior.DrinkLines.Contains);
        rig.Advance(TimeSpan.FromMinutes(2));
        Assert.Single(rig.Voice.Said, ReminderBehavior.DrinkLines.Contains);
    }

    [Fact]
    public void Continuous_use_is_tracked_regardless_of_sit_reminder_and_resets_when_away()
    {
        using var rig = new HostRig();
        rig.Settings.SitReminder = false;
        rig.Host.Start();

        rig.Advance(TimeSpan.FromMinutes(100));
        Assert.Equal(TimeSpan.FromMinutes(100), rig.Host.Reminders.ContinuousUse);
        rig.Monitor.Idle = TimeSpan.FromMinutes(6);
        rig.Advance(TimeSpan.FromSeconds(30));
        rig.Monitor.Idle = TimeSpan.Zero;
        rig.Advance(TimeSpan.FromMinutes(10));
        Assert.InRange(rig.Host.Reminders.ContinuousUse!.Value.TotalMinutes, 10, 10.5);
    }

    [Fact]
    public void Continuous_use_is_unknown_without_idle_detection()
    {
        using var rig = new HostRig(caps: new PlatformCapabilities { IdleDetection = false });
        rig.Host.Start();
        rig.Advance(TimeSpan.FromMinutes(60));
        Assert.Null(rig.Host.Reminders.ContinuousUse);
    }

    [Fact]
    public void Being_away_resets_the_sitting_timer()
    {
        using var rig = new HostRig();
        rig.Settings.SitReminder = true;
        rig.Settings.SitMinutes = 45;
        rig.Host.Start();

        rig.Advance(TimeSpan.FromMinutes(30));
        rig.Monitor.Idle = TimeSpan.FromMinutes(6);   // 离开过电脑
        rig.Advance(TimeSpan.FromSeconds(30));
        rig.Monitor.Idle = TimeSpan.Zero;

        rig.Advance(TimeSpan.FromMinutes(20));        // 开机 50 分钟，但离开后才坐了 20 分钟
        Assert.DoesNotContain(rig.Voice.Said, ReminderBehavior.SitLines.Contains);
        rig.Advance(TimeSpan.FromMinutes(26));
        Assert.Single(rig.Voice.Said, ReminderBehavior.SitLines.Contains);
    }

    [Fact]
    public void Unknown_idle_time_does_not_reset_sitting()
    {
        // 读不到空闲时长的平台（null）当作一直在用，照常提醒。
        using var rig = new HostRig();
        rig.Monitor.Idle = null;
        rig.Settings.SitReminder = true;
        rig.Settings.SitMinutes = 10;
        rig.Host.Start();
        rig.Advance(TimeSpan.FromMinutes(11));
        Assert.Single(rig.Voice.Said, ReminderBehavior.SitLines.Contains);
    }

    [Fact]
    public void Hourly_chime_on_the_hour()
    {
        using var rig = new HostRig(new DateTimeOffset(2026, 11, 3, 9, 59, 30, TimeSpan.Zero));
        rig.Settings.HourlyChime = true;
        rig.Host.Start();
        rig.Advance(TimeSpan.FromMinutes(1));
        Assert.Contains(rig.Voice.Said, s => s.StartsWith("现在是 10:00"));
    }

    [Fact]
    public void Pomodoro_alternates_work_and_rest()
    {
        using var rig = new HostRig();
        rig.Settings.PomodoroWork = 25;
        rig.Settings.PomodoroRest = 5;
        rig.Host.Start();

        rig.Host.Pomodoro.Start();
        Assert.True(rig.Host.Pomodoro.Running);
        Assert.StartsWith("专注中", rig.Host.Pomodoro.Status);

        rig.Advance(TimeSpan.FromMinutes(25.5));
        Assert.True(rig.Host.Pomodoro.Resting);
        Assert.Contains(rig.Voice.Said, s => s.StartsWith("专注结束"));

        rig.Advance(TimeSpan.FromMinutes(5.5));
        Assert.False(rig.Host.Pomodoro.Resting);
        Assert.Contains(rig.Voice.Said, s => s.StartsWith("休息好了"));

        rig.Host.Pomodoro.Stop();
        Assert.Equal("未开始", rig.Host.Pomodoro.Status);
    }

    [Fact]
    public void Disposed_host_stops_all_timers()
    {
        var rig = new HostRig();
        rig.Settings.DrinkReminder = true;
        rig.Settings.DrinkMinutes = 10;
        rig.Host.Start();
        rig.Dispose();
        rig.Advance(TimeSpan.FromMinutes(30));
        Assert.Empty(rig.Voice.Said);
    }
}

public class PresenceTests
{
    [Fact]
    public void Long_idle_enters_power_saving_and_returning_leaves_it()
    {
        using var rig = new HostRig();
        rig.Settings.PowerSaving = true;
        rig.Settings.PowerSavingMinutes = 5;
        rig.Host.Start();

        rig.Monitor.Idle = TimeSpan.FromMinutes(6);
        rig.Advance(TimeSpan.FromSeconds(5));
        Assert.True(rig.Settings.PowerSavingActive);

        rig.Monitor.Idle = TimeSpan.FromSeconds(1);
        rig.Advance(TimeSpan.FromSeconds(5));
        Assert.False(rig.Settings.PowerSavingActive);
    }

    [Fact]
    public void Unknown_idle_time_never_enters_power_saving()
    {
        using var rig = new HostRig();
        rig.Monitor.Idle = null;
        rig.Settings.PowerSaving = true;
        rig.Settings.PowerSavingMinutes = 1;
        rig.Host.Start();
        rig.Advance(TimeSpan.FromMinutes(10));
        Assert.False(rig.Settings.PowerSavingActive);
    }

    [Fact]
    public void Music_starts_singing_and_stops()
    {
        using var rig = new HostRig();
        rig.Settings.MusicDetect = true;
        rig.Host.Start();

        rig.Monitor.Audio = true;
        rig.Advance(TimeSpan.FromSeconds(5));
        Assert.True(rig.Settings.MusicPlaying);

        rig.Monitor.Audio = false;
        rig.Advance(TimeSpan.FromSeconds(5));
        Assert.False(rig.Settings.MusicPlaying);
    }

    [Fact]
    public void Music_detection_is_off_when_platform_cannot_detect_audio()
    {
        using var rig = new HostRig(caps: new PlatformCapabilities { AudioDetection = false });
        rig.Settings.MusicDetect = true;
        rig.Monitor.Audio = true;
        rig.Host.Start();
        rig.Advance(TimeSpan.FromSeconds(10));
        Assert.False(rig.Settings.MusicPlaying);
    }
}

public class InteractionTests
{
    [Fact]
    public void Click_says_a_configured_line()
    {
        using var rig = new HostRig();
        rig.Settings.ClickBubble = true;
        rig.Settings.ClickLines = "第一句\n第二句";
        rig.Host.Start();
        rig.Input.RaiseClicked();
        Assert.Contains(Assert.Single(rig.Voice.Said), new[] { "第一句", "第二句" });
    }

    [Fact]
    public void Double_click_opens_chat_only_when_enabled()
    {
        using var rig = new HostRig();
        rig.Host.Start();
        rig.Settings.DoubleClickChat = false;
        rig.Input.RaiseDoubleClicked();
        Assert.Empty(rig.Commands.Calls);
        rig.Settings.DoubleClickChat = true;
        rig.Input.RaiseDoubleClicked();
        Assert.Equal(["OpenChat"], rig.Commands.Calls);
    }

    [Fact]
    public void Petting_plays_click_and_purrs()
    {
        using var rig = new HostRig();
        rig.Host.Start();
        rig.Input.RaisePetted();
        Assert.Equal(PetLibrary.Click, rig.Director.Current.Clip.Category);
        Assert.Contains(Assert.Single(rig.Voice.Said), InteractionBehavior.PettingLines);
    }

    [Fact]
    public void Menu_toggles_state_and_labels_follow()
    {
        using var rig = new HostRig();
        rig.Host.Start();

        Assert.Equal("开始番茄钟", rig.Host.Menu.LabelOf(MenuEntry.Pomodoro));
        rig.Input.RaiseMenuCommand(MenuEntry.Pomodoro);
        Assert.Equal("停止番茄钟", rig.Host.Menu.LabelOf(MenuEntry.Pomodoro));

        rig.Input.RaiseMenuCommand(MenuEntry.Pause);
        Assert.True(rig.Settings.Paused);
        Assert.Equal("继续动画", rig.Host.Menu.LabelOf(MenuEntry.Pause));

        rig.Input.RaiseMenuCommand(MenuEntry.Center);
        rig.Input.RaiseMenuCommand(MenuEntry.Console);
        Assert.Equal(["ResetPosition", "OpenConsole"], rig.Commands.Calls);

        rig.Input.RaiseMenuCommand(MenuEntry.Hide);
        Assert.False(rig.Settings.Visible);
    }

    [Fact]
    public void Menu_rows_match_what_the_real_menu_shows()
    {
        using var rig = new HostRig();
        rig.Settings.Normalize();   // 菜单项由 Normalize 补齐（真实设置加载时都会走）
        rig.Settings.LifeEnabled = false;
        rig.Host.Start();

        var rows = rig.Host.Menu.Rows();
        // 打开了的项都在、按设置顺序；出现不了的带原因
        Assert.Equal(rig.Settings.MenuItems.Where(m => m.Visible).Select(m => m.Id), rows.Select(r => r.Id));
        Assert.Contains("宠物体系", rows.Single(r => r.Id == MenuEntry.Feed).HiddenReason);
        Assert.False(rows.Single(r => r.Id == MenuEntry.Status).Shown);
        Assert.Contains("睡觉动作", rows.Single(r => r.Id == MenuEntry.Sleep).HiddenReason);
        Assert.Equal("开始番茄钟", rows.Single(r => r.Id == MenuEntry.Pomodoro).Label);
        Assert.False(rows.Single(r => r.Id == MenuEntry.StayStill).IsOn);

        // 分隔线只在出现的项之间、分组变化处
        var shown = rows.Where(r => r.Shown).ToList();
        Assert.False(shown[0].SeparatorBefore);
        for (var i = 1; i < shown.Count; i++)
            Assert.Equal(shown[i].Group != shown[i - 1].Group, shown[i].SeparatorBefore);
        Assert.Equal(MenuEntry.Exit, shown[^1].Id);
    }

    [Fact]
    public void Action_submenu_plays_the_chosen_action()
    {
        using var rig = new HostRig();
        rig.Settings.Normalize();
        rig.Host.Start();

        var row = rig.Host.Menu.Rows().Single(r => r.Id == MenuEntry.Actions);
        Assert.True(row.Shown);
        Assert.NotEmpty(row.Children);
        Assert.All(row.Children, c => Assert.StartsWith(MenuEntry.ActionPrefix, c.Command));
        Assert.DoesNotContain(row.Children, c => c.Command.EndsWith(":idle"));   // 系统控制的动作不列

        var pick = row.Children.First(c => c.Enabled);
        rig.Input.RaiseMenuCommand(pick.Command);
        Assert.Equal(pick.Command[MenuEntry.ActionPrefix.Length..], rig.Director.CurrentAction.Id);
    }

    [Fact]
    public void New_menu_entries_are_inserted_before_exit()
    {
        var settings = new PetSettings();
        settings.MenuItems.Clear();
        foreach (var id in new[] { MenuEntry.Chat, MenuEntry.Exit }) settings.MenuItems.Add(new MenuEntry { Id = id });
        settings.Normalize();
        Assert.Equal(MenuEntry.Exit, settings.MenuItems[^1].Id);
        Assert.Contains(settings.MenuItems, m => m.Id == MenuEntry.Actions);
    }

    [Theory]
    [InlineData(8, "早上好")]
    [InlineData(12, "中午好")]
    [InlineData(23, "晚上好")]
    [InlineData(2, "这么晚还没睡")]
    public void Greeting_depends_on_time_of_day(int hour, string expected)
    {
        using var rig = new HostRig(new DateTimeOffset(2026, 11, 3, hour, 0, 0, TimeSpan.Zero));
        rig.Settings.DailyGreeting = true;
        rig.Host.Start();
        Assert.StartsWith(expected, Assert.Single(rig.Voice.Said));
    }
}

public class ProactiveChatTests
{
    [Fact]
    public void Speaks_up_after_the_interval_when_left_alone()
    {
        using var rig = new HostRig();
        rig.Commands.AgentAvailable = true;
        rig.Settings.ProactiveMinutes = 20;
        rig.Host.Start();

        rig.Advance(TimeSpan.FromMinutes(13));   // 最短 0.7 倍
        Assert.Empty(rig.Commands.Cues);
        rig.Advance(TimeSpan.FromMinutes(18));   // 最长 1.5 倍
        Assert.Single(rig.Commands.Cues);
        Assert.Contains(rig.Commands.Cues[0], ProactiveChatBehavior.IdleCues);
    }

    [Fact]
    public void Stays_quiet_when_disabled_focusing_or_just_interacted()
    {
        using var rig = new HostRig();
        rig.Commands.AgentAvailable = true;
        rig.Settings.ProactiveMinutes = 10;
        rig.Settings.ProactiveChat = false;
        rig.Host.Start();
        rig.Advance(TimeSpan.FromMinutes(40));
        Assert.Empty(rig.Commands.Cues);

        rig.Settings.ProactiveChat = true;
        rig.Host.Pomodoro.Start();               // 专注 25 分钟
        rig.Advance(TimeSpan.FromMinutes(24));
        Assert.Empty(rig.Commands.Cues);

        rig.Host.Pomodoro.Stop();
        rig.Events.Publish(PetEvents.Clicked);
        rig.Advance(TimeSpan.FromMinutes(2));    // 刚点过，不插话
        Assert.Empty(rig.Commands.Cues);
        rig.Advance(TimeSpan.FromMinutes(20));
        Assert.NotEmpty(rig.Commands.Cues);
    }

    [Fact]
    public void Greets_when_user_comes_back()
    {
        using var rig = new HostRig();
        rig.Commands.AgentAvailable = true;
        rig.Settings.ProactiveMinutes = 180;
        rig.Host.Start();

        rig.Monitor.Idle = TimeSpan.FromMinutes(15);
        rig.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(rig.Commands.Cues);
        rig.Monitor.Idle = TimeSpan.FromSeconds(3);
        rig.Advance(TimeSpan.FromMinutes(1));
        Assert.Single(rig.Commands.Cues);
        Assert.Contains("刚刚回来", rig.Commands.Cues[0]);
    }
}

public class InCharacterReminderTests
{
    [Fact]
    public void Reminders_go_through_the_agent_when_available()
    {
        using var rig = new HostRig();
        rig.Commands.AgentAvailable = true;
        rig.Settings.ProactiveChat = false;
        rig.Settings.DrinkReminder = true;
        rig.Settings.DrinkMinutes = 30;
        rig.Host.Start();

        rig.Advance(TimeSpan.FromMinutes(31));
        Assert.Contains(rig.Commands.Reminders, c => c.StartsWith("喝水提醒"));
        Assert.DoesNotContain(rig.Voice.Said, ReminderBehavior.DrinkLines.Contains);
    }
}
