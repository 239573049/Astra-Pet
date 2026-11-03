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
        Menu = new MenuBehavior(Pomodoro);
        UpdateNotifier = new UpdateNotifyBehavior();
        _behaviors =
        [
            new InteractionBehavior(),
            Menu,
            Pomodoro,
            new ReminderBehavior(),
            new PresenceBehavior(),
            new GreetingBehavior(),
            UpdateNotifier,
        ];
    }

    public PomodoroBehavior Pomodoro { get; }
    public MenuBehavior Menu { get; }
    public UpdateNotifyBehavior UpdateNotifier { get; }

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
