namespace AstraPet.Core.Behaviors;

/// <summary>把单击、摸头转成事件（动作和台词由触发规则决定，见 <see cref="DefaultTriggers"/>）；双击打开对话。</summary>
public sealed class InteractionBehavior : PetBehavior
{
    public static readonly string[] PettingLines = ["嘿嘿，好舒服～", "再摸摸～", "呼噜呼噜…", "头发要被你揉乱啦"];

    protected override void OnStart()
    {
        Context.Input.Clicked += OnClicked;
        Context.Input.DoubleClicked += OnDoubleClicked;
        Context.Input.Petted += OnPetted;
        Context.Input.DragStarted += OnDragStarted;
        Context.Input.DragEnded += OnDragEnded;
    }

    private void OnDragStarted() => Context.Events.Publish(PetEvents.DragStarted);
    private void OnDragEnded() => Context.Events.Publish(PetEvents.DragEnded);

    /// <summary>1 秒内单击这么多次算连点（design/pet-life/03 §4.1）。</summary>
    public const int SpamClicks = 6;
    public static readonly TimeSpan SpamWindow = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan SpamQuiet = TimeSpan.FromSeconds(5);

    private readonly Queue<DateTimeOffset> _clicks = new();
    private DateTimeOffset _quietUntil;

    private void OnClicked(string zone)
    {
        var now = Now;
        if (now < _quietUntil) return;   // 刚连点过：之后几秒的单击不再反应，免得打断晕乎乎
        while (_clicks.Count > 0 && now - _clicks.Peek() > SpamWindow) _clicks.Dequeue();
        _clicks.Enqueue(now);
        if (_clicks.Count >= SpamClicks)
        {
            _clicks.Clear();
            _quietUntil = now + SpamQuiet;
            Context.Events.Publish(PetEvents.Spammed);
            return;
        }
        // 睡着时单击是叫醒，不是普通的点击反应。
        if (Context.Director.Sleeping)
        {
            Context.Events.Publish(PetEvents.Woken);
            return;
        }
        Context.Events.Publish(PetEvents.Clicked, ("zone", zone));
    }

    private void OnDoubleClicked()
    {
        Context.Events.Publish(PetEvents.DoubleClicked);
        if (Settings.DoubleClickChat) Context.Commands.OpenChat();
    }

    private void OnPetted() => Context.Events.Publish(PetEvents.Petted);

    public override void Dispose()
    {
        if (Context is not null)
        {
            Context.Input.Clicked -= OnClicked;
            Context.Input.DoubleClicked -= OnDoubleClicked;
            Context.Input.Petted -= OnPetted;
            Context.Input.DragStarted -= OnDragStarted;
            Context.Input.DragEnded -= OnDragEnded;
        }
        base.Dispose();
    }
}

/// <summary>右键菜单：每项的文字（带状态）和点了之后做什么。「退出」由窗口自己处理。</summary>
public sealed class MenuBehavior(PomodoroBehavior pomodoro, PlayBehavior play) : PetBehavior
{
    protected override void OnStart() => Context.Input.MenuCommand += OnCommand;

    public string LabelOf(string id) => id switch
    {
        MenuEntry.Pause => Settings.Paused ? "继续动画" : "暂停动画",
        MenuEntry.Pomodoro => pomodoro.Running ? "停止番茄钟" : "开始番茄钟",
        MenuEntry.Play => play.Playing ? "不玩了" : "陪我玩",
        MenuEntry.Sleep => Context.Director.Sleeping ? "叫醒它" : "哄睡",
        _ => MenuEntry.LabelOf(id),
    };

    /// <summary>开关类菜单项当前是否打开（菜单上画勾）；不是开关的返回 null。</summary>
    public bool? IsChecked(string id) => id switch
    {
        MenuEntry.StayStill => Settings.StayStill,
        MenuEntry.Mute => Settings.SpeakReplies,
        _ => null,
    };

    private void OnCommand(string id)
    {
        switch (id)
        {
            case MenuEntry.Chat: Context.Commands.OpenChat(); break;
            case MenuEntry.Feed: break;   // 子菜单的标题，具体食物是 feed:<id>
            case MenuEntry.Console: Context.Commands.OpenConsole(null); break;
            case MenuEntry.Pause: Settings.Paused = !Settings.Paused; break;
            case MenuEntry.StayStill: Settings.StayStill = !Settings.StayStill; break;
            case MenuEntry.Pomodoro:
                if (pomodoro.Running) pomodoro.Stop(); else pomodoro.Start();
                break;
            case MenuEntry.Mute: Settings.SpeakReplies = !Settings.SpeakReplies; break;
            case MenuEntry.Center: Context.Commands.ResetPosition(); break;
            case MenuEntry.Hide: Settings.Visible = false; break;
            case MenuEntry.Play:
                if (play.Playing) play.Stop(); else play.Start();
                break;
            case MenuEntry.Sleep:
                Context.Events.Publish(Context.Director.Sleeping ? PetEvents.Woken : PetEvents.SleepRequested);
                break;
            case MenuEntry.Status: Context.Commands.OpenConsole("Life"); break;
            default:
                if (id.StartsWith(MenuEntry.FeedPrefix, StringComparison.Ordinal) && Context.Life is { } life)
                    Context.Commands.Feed(life, id[MenuEntry.FeedPrefix.Length..]);
                break;
        }
    }

    public override void Dispose()
    {
        if (Context is not null) Context.Input.MenuCommand -= OnCommand;
        base.Dispose();
    }
}

/// <summary>启动时按时段打个招呼。</summary>
public sealed class GreetingBehavior : PetBehavior
{
    protected override void OnStart()
    {
        if (!Settings.DailyGreeting) return;
        var text = Now.Hour switch
        {
            < 5 => "这么晚还没睡呀，早点休息哦",
            < 11 => "早上好！今天也一起加油吧",
            < 14 => "中午好～记得吃饭哦",
            < 18 => "下午好，喝杯茶歇一会儿？",
            _ => "晚上好，今天辛苦啦",
        };
        // 等窗口和气泡都摆好再说。
        Context.Ui.Post(() =>
        {
            Context.Voice.Say(text);
            Context.Events.Publish(PetEvents.AppStarted, ("hour", Now.Hour.ToString()));
        });
    }
}
