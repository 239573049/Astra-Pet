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

/// <summary>右键菜单里的一行。<see cref="Shown"/> 为假时真实菜单里不出现，<see cref="HiddenReason"/> 说明为什么（控制台预览用）。</summary>
public sealed record MenuRow(
    string Id,
    string Label,
    string Group,
    bool? Checked,
    bool Shown,
    string? HiddenReason,
    IReadOnlyList<MenuSubItem> Children)
{
    /// <summary>和上一个出现的项分组不同：真实菜单在它前面画分隔线。</summary>
    public bool SeparatorBefore { get; init; }
    public bool HasChildren => Children.Count > 0;
    /// <summary>开关类、而且现在是打开的（画勾）。</summary>
    public bool IsOn => Checked == true;
}

/// <summary>子菜单的一项：点了发 <see cref="Command"/>。</summary>
public sealed record MenuSubItem(string Command, string Label, bool Enabled);

/// <summary>
/// 右键菜单：每项的文字（带状态）、能不能出现、子菜单，以及点了之后做什么。「退出」由窗口自己处理。
/// 真实菜单和控制台的预览都用 <see cref="Rows"/>，两边永远一致。
/// </summary>
public sealed class MenuBehavior(PomodoroBehavior pomodoro, PlayBehavior play) : PetBehavior
{
    protected override void OnStart() => Context.Input.MenuCommand += OnCommand;

    /// <summary>分组：相邻两项分组不同时中间画分隔线。</summary>
    public static string GroupOf(string id) => id switch
    {
        MenuEntry.Chat or MenuEntry.Console => "open",
        MenuEntry.Actions or MenuEntry.Feed or MenuEntry.Play or MenuEntry.Sleep or MenuEntry.Status => "pet",
        MenuEntry.Pause or MenuEntry.StayStill or MenuEntry.Pomodoro or MenuEntry.Mute => "state",
        MenuEntry.Center or MenuEntry.Hide => "place",
        MenuEntry.Exit => "exit",
        _ => "",
    };

    /// <summary>按设置里的顺序列出打开了的项（含现在出现不了的，带原因）。分隔线只算真正出现的项。</summary>
    public IReadOnlyList<MenuRow> Rows()
    {
        var rows = new List<MenuRow>();
        string? group = null;
        foreach (var entry in Settings.MenuItems.Where(m => m.Visible))
        {
            var id = entry.Id;
            var children = id switch
            {
                MenuEntry.Feed => FoodItems(),
                MenuEntry.Actions => ActionItems(),
                _ => (IReadOnlyList<MenuSubItem>)[],
            };
            var reason = HiddenReason(id, children);
            var row = new MenuRow(id, LabelOf(id), GroupOf(id), IsChecked(id), reason is null, reason, children);
            if (row.Shown)
            {
                row = row with { SeparatorBefore = group is not null && row.Group != group };
                group = row.Group;
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>这项现在为什么不出现在菜单里；能出现返回 null。</summary>
    private string? HiddenReason(string id, IReadOnlyList<MenuSubItem> children) => id switch
    {
        MenuEntry.Feed or MenuEntry.Status when Context.Life is not { Enabled: true } => "宠物体系（状态页）没打开",
        MenuEntry.Feed when children.Count == 0 => "没有可以喂的食物",
        MenuEntry.Sleep when !Context.Director.Library.Actions.All.Any(a => a.Enabled && a.HasRole(Roles.Sleep)) => "这只宠物没有睡觉动作",
        MenuEntry.Actions when children.Count == 0 => "这只宠物没有可以点播的动作",
        _ => null,
    };

    /// <summary>「喂食」子菜单：每个食物一项，标出喜欢的、冷却和零食数量（design/pet-life/03 §2.1）。</summary>
    private IReadOnlyList<MenuSubItem> FoodItems()
    {
        if (Context.Life is not { Enabled: true } life) return [];
        var diet = life.Profile.Diet;
        return Foods.All.Select(f =>
        {
            var (available, note) = life.FoodStatus(f);
            var star = diet.Likes.Contains(f.Id) ? " ★" : "";
            return new MenuSubItem(MenuEntry.FeedPrefix + f.Id, $"{f.Icon} {f.Name}{star}" + (note.Length > 0 ? $"（{note}）" : ""), available);
        }).ToList();
    }

    /// <summary>
    /// 「做个动作」子菜单：和 Agent 能选的同一批（不含待机、移动、拖拽这些系统控制的）。
    /// 还没解锁、现在的姿势够不着的置灰。
    /// </summary>
    private IReadOnlyList<MenuSubItem> ActionItems()
    {
        var director = Context.Director;
        return AstraPet.Ai.PlayActionTool.Choosable(director.Library.Actions, _ => true)
            .Select(a => new MenuSubItem(MenuEntry.ActionPrefix + a.Id, a.DisplayName,
                (director.Usable?.Invoke(a) ?? true) && director.CanReach(a)))
            .ToList();
    }

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
                else if (id.StartsWith(MenuEntry.ActionPrefix, StringComparison.Ordinal))
                    PlayAction(id[MenuEntry.ActionPrefix.Length..]);
                break;
        }
    }

    /// <summary>菜单点播一个动作：按用户操作的优先级播，播完回待机；动作带台词的随口说一句。</summary>
    private void PlayAction(string actionId)
    {
        var director = Context.Director;
        if (director.Library.Actions.All.FirstOrDefault(a => a.Id == actionId && a.Enabled) is not { } action) return;
        director.Request(new ActionRequest(action, ActionPriority.User, "menu"));
        if (action.Lines.Count > 0) Context.Voice.React(Pick(action.Lines));
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
    private string SystemPromptTime() => AstraPet.Ai.SystemPrompt.When(Now.DateTime);

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
            SayInCharacter($"你刚被用户打开（今天第一次见面或者刚重启），按现在的时间（{SystemPromptTime()}）跟用户打个招呼", text);
            Context.Events.Publish(PetEvents.AppStarted, ("hour", Now.Hour.ToString()));
        });
    }
}
