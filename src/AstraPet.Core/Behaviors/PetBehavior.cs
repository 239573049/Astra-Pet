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
    /// <summary>打开控制台；<paramref name="page"/> 不为空时跳到那一页（比如 Life）。</summary>
    void OpenConsole(string? page = null);
    void ResetPosition();

    /// <summary>喂一个食物：界面先播食物飞到嘴边，再交给 <see cref="PetLife.Feed"/>。默认直接喂（测试、没有界面时）。</summary>
    void Feed(PetLife life, string foodId) => life.Feed(foodId);
}

/// <summary>宠物窗口上的输入事件，由界面层转发进来。行为只订阅这里，不认识窗口。</summary>
public sealed class PetInput
{
    /// <summary>单击，参数是点在哪：head / body / feet（见 <see cref="ClickZones"/>）。</summary>
    public event Action<string>? Clicked;
    public event Action? DoubleClicked;
    public event Action? Petted;
    public event Action? DragStarted;
    public event Action? DragEnded;
    public event Action<string>? MenuCommand;

    public void RaiseClicked(string zone = ClickZones.Body) => Clicked?.Invoke(zone);
    public void RaiseDoubleClicked() => DoubleClicked?.Invoke();
    public void RaisePetted() => Petted?.Invoke();
    public void RaiseDragStarted() => DragStarted?.Invoke();
    public void RaiseDragEnded() => DragEnded?.Invoke();
    public void RaiseMenuCommand(string id) => MenuCommand?.Invoke(id);
}

/// <summary>单击位置：按画布上角色身高的比例分三段。</summary>
public static class ClickZones
{
    public const string Head = "head";
    public const string Body = "body";
    public const string Feet = "feet";

    /// <summary>
    /// <paramref name="y"/> 是素材坐标（0..<see cref="PetClip.Height"/>）。角色站姿约定头顶 y≈60、脚底 y≈330（身高 270），
    /// 上 30% 算头，下 15% 算脚。
    /// </summary>
    public static string At(double y) => At(y, new PetBodyMetrics());

    /// <summary>按档案里的身体包围盒分区（design/pet-life/01 §3）；默认值和上面的常量一致。</summary>
    public static string At(double y, PetBodyMetrics body) =>
        y < body.Y0 + body.Height * body.HeadRatio ? Head : y > body.Y1 - body.Height * body.FeetRatio ? Feet : Body;
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
    /// <summary>事件总线：行为、窗口、Agent 往这里发事件，<see cref="TriggerEngine"/> 按规则决定做什么动作。</summary>
    public PetEventBus Events { get; init; } = new();
    /// <summary>宠物体系（数值），由 <see cref="PetHost"/> 建好后填进来；行为、规则、Agent 情境从这里读。</summary>
    public PetLife? Life { get; set; }
    /// <summary>位置与物理（design/pet-life/04）。界面层建好后填进来；测试里可以没有。</summary>
    public PetBody? Body { get; set; }
    /// <summary>光标在宠物画布上的位置（画布坐标 0..640 × 0..360，按屏幕上看到的样子，不考虑镜像），拿不到返回 null。</summary>
    public Func<(double X, double Y)?> Cursor { get; init; } = () => null;
    /// <summary>召唤用的全局快捷键；不支持的平台是空实现。</summary>
    public IGlobalHotkey Hotkey { get; init; } = NoGlobalHotkey.Instance;
    /// <summary>合并好的触发规则。</summary>
    public TriggerTable Triggers { get; init; } = new();
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
