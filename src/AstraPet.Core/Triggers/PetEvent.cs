namespace AstraPet.Core;

/// <summary>
/// 发生了一件事：用户点了宠物、番茄钟到点、开始放音乐、Agent 回复了……类型是字符串（<see cref="PetEvents"/>），
/// 规则文件按名字引用。设计见 design/pet-studio/02-triggers.md §2。
/// </summary>
public sealed record PetEvent(string Type, IReadOnlyDictionary<string, string>? Data = null)
{
    public string? this[string key] => Data?.GetValueOrDefault(key);

    public static PetEvent Of(string type, params (string Key, string Value)[] data) =>
        new(type, data.Length == 0 ? null : data.ToDictionary(d => d.Key, d => d.Value));
}

/// <summary>同步的发布 / 订阅，只在 UI 线程上用。</summary>
public sealed class PetEventBus
{
    public event Action<PetEvent>? Published;

    public void Publish(PetEvent e) => Published?.Invoke(e);

    public void Publish(string type, params (string Key, string Value)[] data) => Publish(PetEvent.Of(type, data));
}

/// <summary>内置事件类型。</summary>
public static class PetEvents
{
    public const string Clicked = "pet.clicked";
    public const string DoubleClicked = "pet.doubleClicked";
    public const string Petted = "pet.petted";
    public const string DragStarted = "pet.dragStarted";
    public const string DragEnded = "pet.dragEnded";
    public const string AppStarted = "app.started";
    public const string Hour = "time.hour";
    public const string Drink = "reminder.drink";
    public const string Sit = "reminder.sit";
    public const string PomodoroStarted = "pomodoro.started";
    public const string FocusEnded = "pomodoro.focusEnded";
    public const string RestEnded = "pomodoro.restEnded";
    public const string PomodoroStopped = "pomodoro.stopped";
    public const string MusicStarted = "music.started";
    public const string MusicStopped = "music.stopped";
    public const string PowerSaving = "power.saving";
    public const string PowerResumed = "power.resumed";
    public const string ChatThinking = "chat.thinking";
    public const string ChatAction = "chat.action";
    public const string ChatReplied = "chat.replied";
    public const string ChatFailed = "chat.failed";
    public const string MemoryAdded = "memory.added";
    public const string UpdateFound = "update.found";
    /// <summary>外部 Agent 的约定事件（03 §2）：开始干活、做完了、出错了。其他 external.* 也能发，用户自己写规则。</summary>
    public const string ExternalStarted = "external.started";
    public const string ExternalDone = "external.done";
    public const string ExternalFailed = "external.failed";

    // 宠物体系（design/pet-life）
    public const string Fed = "pet.fed";
    public const string FeedRefused = "pet.feedRefused";
    public const string Spammed = "pet.spammed";
    public const string Thrown = "pet.thrown";
    public const string Landed = "pet.landed";
    public const string GrabbedWall = "pet.grabbedWall";
    public const string Woken = "pet.woken";
    public const string SleepRequested = "pet.sleep";
    public const string WallReached = "surface.wallReached";
    public const string CursorBehind = "cursor.behind";
    public const string PlayStarted = "play.started";
    public const string PlayCaught = "play.caught";
    public const string PlayEnded = "play.ended";
    public const string Hungry = "needs.hungry";
    public const string Starving = "needs.starving";
    public const string Satisfied = "needs.fed";
    public const string Sleepy = "needs.sleepy";
    public const string Rested = "needs.rested";
    public const string MoodChanged = "mood.changed";
    public const string LevelUp = "bond.levelUp";
    public const string SnackEarned = "life.snack";
    /// <summary>打开程序时离线超过一天（「好久不见」），数据 days。</summary>
    public const string Returned = "life.returned";
    /// <summary>按全局快捷键把宠物叫到光标下面。</summary>
    public const string Summoned = "hotkey.summon";

    /// <summary>控制台「绑定触发」里可选的事件和显示名。</summary>
    public static readonly IReadOnlyList<(string Type, string Label)> Bindable =
    [
        (Clicked, "单击"),
        (Petted, "摸头"),
        (AppStarted, "启动时"),
        (Hour, "整点"),
        (Drink, "喝水提醒"),
        (Sit, "久坐提醒"),
        (PomodoroStarted, "番茄钟开始"),
        (FocusEnded, "专注结束"),
        (RestEnded, "休息结束"),
        (MusicStarted, "开始放音乐"),
        (PowerSaving, "进入省电"),
        (PowerResumed, "离开省电"),
        (ChatReplied, "Agent 回复后"),
        (UpdateFound, "发现新版本"),
        (ExternalStarted, "外部 Agent 开始工作"),
        (ExternalDone, "外部 Agent 完成"),
        (ExternalFailed, "外部 Agent 出错"),
        (Fed, "喂食"),
        (Spammed, "连点"),
        (Thrown, "被甩出去"),
        (Landed, "落地"),
        (WallReached, "走到屏幕边"),
        (CursorBehind, "光标在背后"),
        (PlayCaught, "陪玩时扑到光标"),
        (Hungry, "饿了"),
        (Sleepy, "困了"),
        (Rested, "睡饱了"),
        (MoodChanged, "心情变化"),
        (LevelUp, "亲密度升级"),
        (Returned, "好久不见"),
        (Summoned, "被召唤"),
    ];

    public static string LabelOf(string type) =>
        Bindable.FirstOrDefault(b => b.Type == type).Label ?? type;
}
