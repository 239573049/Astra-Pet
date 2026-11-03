namespace AstraPet.Core.Behaviors;

/// <summary>单击说一句台词、双击打开对话、摸头时开心。点击动画本身由窗口播（它要知道是不是拖拽）。</summary>
public sealed class InteractionBehavior : PetBehavior
{
    public static readonly string[] PettingLines = ["嘿嘿，好舒服～", "再摸摸～", "呼噜呼噜…", "头发要被你揉乱啦"];

    protected override void OnStart()
    {
        Context.Input.Clicked += OnClicked;
        Context.Input.DoubleClicked += OnDoubleClicked;
        Context.Input.Petted += OnPetted;
    }

    private void OnClicked()
    {
        if (!Settings.ClickBubble) return;
        var lines = Settings.ClickLineList();
        if (lines.Length > 0) Context.Voice.Say(Pick(lines));
    }

    private void OnDoubleClicked()
    {
        if (Settings.DoubleClickChat) Context.Commands.OpenChat();
    }

    private void OnPetted()
    {
        Context.Director.Play(PetLibrary.Click, fromUser: true);
        Context.Voice.Say(Pick(PettingLines));
    }

    public override void Dispose()
    {
        if (Context is not null)
        {
            Context.Input.Clicked -= OnClicked;
            Context.Input.DoubleClicked -= OnDoubleClicked;
            Context.Input.Petted -= OnPetted;
        }
        base.Dispose();
    }
}

/// <summary>右键菜单：每项的文字（带状态）和点了之后做什么。「退出」由窗口自己处理。</summary>
public sealed class MenuBehavior(PomodoroBehavior pomodoro) : PetBehavior
{
    protected override void OnStart() => Context.Input.MenuCommand += OnCommand;

    public string LabelOf(string id) => id switch
    {
        MenuEntry.Pause => Settings.Paused ? "继续动画" : "暂停动画",
        MenuEntry.StayStill => Settings.StayStill ? "✓ 不移动" : "不移动",
        MenuEntry.Pomodoro => pomodoro.Running ? "停止番茄钟" : "开始番茄钟",
        MenuEntry.Mute => Settings.SpeakReplies ? "✓ 朗读回复" : "朗读回复",
        _ => MenuEntry.LabelOf(id),
    };

    private void OnCommand(string id)
    {
        switch (id)
        {
            case MenuEntry.Chat: Context.Commands.OpenChat(); break;
            case MenuEntry.Console: Context.Commands.OpenConsole(); break;
            case MenuEntry.Pause: Settings.Paused = !Settings.Paused; break;
            case MenuEntry.StayStill: Settings.StayStill = !Settings.StayStill; break;
            case MenuEntry.Pomodoro:
                if (pomodoro.Running) pomodoro.Stop(); else pomodoro.Start();
                break;
            case MenuEntry.Mute: Settings.SpeakReplies = !Settings.SpeakReplies; break;
            case MenuEntry.Center: Context.Commands.ResetPosition(); break;
            case MenuEntry.Hide: Settings.Visible = false; break;
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
        Context.Ui.Post(() => Context.Voice.Say(text));
    }
}
