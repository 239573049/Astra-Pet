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
    public void OpenConsole() => Calls.Add(nameof(OpenConsole));
    public void ResetPosition() => Calls.Add(nameof(ResetPosition));
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

    public HostRig(DateTimeOffset? start = null, PlatformCapabilities? caps = null)
    {
        Time = new FakeTimeProvider(start ?? new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));
        Director = new PetDirector(PetLibrary.Load(_dir.Dir, new FakeDecoder()), Settings, new Random(1));
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
            Updates = new UpdateChecker(Settings, "test-x64", ["zip"]),
            Time = Time,
            Ui = new InlineUi(),
            Random = new Random(1),
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
