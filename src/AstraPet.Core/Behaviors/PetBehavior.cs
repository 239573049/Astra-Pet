using AstraPet.Platform;

namespace AstraPet.Core.Behaviors;

/// <summary>回到 UI 线程。设置、Director、行为的状态都只在 UI 线程上读写。</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>宠物说话：头顶冒气泡，<paramref name="speak"/> 为真且开了朗读提醒时同时念出来。</summary>
public interface IPetVoice
{
    void Say(string text, bool speak = true);
}

/// <summary>行为需要界面替它做的事。</summary>
public interface IPetCommands
{
    void OpenChat();
    void OpenConsole();
    void ResetPosition();
}

/// <summary>宠物窗口上的输入事件，由界面层转发进来。行为只订阅这里，不认识窗口。</summary>
public sealed class PetInput
{
    public event Action? Clicked;
    public event Action? DoubleClicked;
    public event Action? Petted;
    public event Action<string>? MenuCommand;

    public void RaiseClicked() => Clicked?.Invoke();
    public void RaiseDoubleClicked() => DoubleClicked?.Invoke();
    public void RaisePetted() => Petted?.Invoke();
    public void RaiseMenuCommand(string id) => MenuCommand?.Invoke(id);
}

/// <summary>行为能用到的一切，由宿主注入。测试时换成假时钟、同步的 UI 调度和假的平台服务。</summary>
public sealed class PetContext
{
    public required PetSettings Settings { get; init; }
    public required PetDirector Director { get; init; }
    public required PetInput Input { get; init; }
    public required IPetVoice Voice { get; init; }
    public required IPetCommands Commands { get; init; }
    public required ISystemMonitor Monitor { get; init; }
    public required PlatformCapabilities Capabilities { get; init; }
    public required UpdateChecker Updates { get; init; }
    public required TimeProvider Time { get; init; }
    public required IUiDispatcher Ui { get; init; }
    public Random Random { get; init; } = new();
}

/// <summary>宠物的一种行为（提醒、互动、省电……）。宿主启动时调 <see cref="Start"/>，关闭时 Dispose。</summary>
public interface IPetBehavior : IDisposable
{
    void Start(PetContext context);
}

/// <summary>
/// 行为的公共部分：定时器走 <see cref="TimeProvider"/>（测试里可以拨快），回调先切回 UI 线程再执行；
/// Dispose 时停掉所有定时器。
/// </summary>
public abstract class PetBehavior : IPetBehavior
{
    private readonly List<ITimer> _timers = new();

    protected PetContext Context { get; private set; } = null!;
    protected PetSettings Settings => Context.Settings;
    protected DateTimeOffset Now => Context.Time.GetLocalNow();

    public void Start(PetContext context)
    {
        Context = context;
        OnStart();
    }

    protected abstract void OnStart();

    /// <summary>每隔 <paramref name="period"/> 在 UI 线程上执行一次；第一次在 <paramref name="first"/> 之后（默认等一个周期）。</summary>
    protected void Every(TimeSpan period, Action tick, TimeSpan? first = null) =>
        _timers.Add(Context.Time.CreateTimer(_ => Context.Ui.Post(tick), null, first ?? period, period));

    protected string Pick(IReadOnlyList<string> lines) => lines[Context.Random.Next(lines.Count)];

    public virtual void Dispose()
    {
        foreach (var timer in _timers) timer.Dispose();
        _timers.Clear();
    }
}
