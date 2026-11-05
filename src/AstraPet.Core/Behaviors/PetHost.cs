namespace AstraPet.Core.Behaviors;

/// <summary>
/// 宠物行为的宿主：创建并启动所有行为，关闭时一起停掉。行为之间不互相引用（菜单要控制番茄钟除外，由这里接好）。
/// 界面层把窗口事件转发到 <see cref="PetContext.Input"/>，把 <see cref="Menu"/> 的文字接到右键菜单上。
/// </summary>
public sealed class PetHost : IDisposable
{
    private readonly PetContext _context;
    private readonly IPetBehavior[] _behaviors;
    private bool _started;

    public PetHost(PetContext context)
    {
        _context = context;
        Pomodoro = new PomodoroBehavior();
        Play = new PlayBehavior();
        Summon = new SummonBehavior();
        Life = new PetLife();
        context.Life = Life;
        Menu = new MenuBehavior(Pomodoro, Play);
        UpdateNotifier = new UpdateNotifyBehavior();
        Reminders = new ReminderBehavior();
        Triggers = new TriggerEngine();
        Proactive = new ProactiveChatBehavior(Pomodoro);
        _behaviors =
        [
            Triggers,   // 最先启动：其他行为启动时就可能发事件（打招呼）
            Life,       // 其次：读存档、装上随机链调制，之后的行为发的事件它都要听
            new InteractionBehavior(),
            Menu,
            Pomodoro,
            Reminders,
            new PresenceBehavior(),
            new GreetingBehavior(),
            UpdateNotifier,
            Play,
            new CursorBehavior(),
            Summon,
            Proactive,
        ];
    }

    public TriggerEngine Triggers { get; }
    public PetLife Life { get; }
    public PlayBehavior Play { get; }
    public SummonBehavior Summon { get; }
    public PomodoroBehavior Pomodoro { get; }
    public ReminderBehavior Reminders { get; }
    public MenuBehavior Menu { get; }
    public UpdateNotifyBehavior UpdateNotifier { get; }
    public ProactiveChatBehavior Proactive { get; }

    /// <summary>宠物窗口打开后调一次。</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        foreach (var behavior in _behaviors) behavior.Start(_context);
    }

    public void Dispose()
    {
        foreach (var behavior in _behaviors) behavior.Dispose();
    }
}
