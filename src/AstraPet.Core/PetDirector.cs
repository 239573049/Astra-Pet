using System.Diagnostics.CodeAnalysis;

namespace AstraPet.Core;

/// <summary>动作请求的优先级：高的能打断低的（同级后到的打断先到的）。拖拽另算，总是立即打断。</summary>
public enum ActionPriority
{
    /// <summary>随机链。</summary>
    Ambient,
    /// <summary>定时和系统状态：提醒、番茄钟、听歌。</summary>
    System,
    /// <summary>内置 Agent、外部 Agent。</summary>
    Agent,
    /// <summary>用户直接操作：单击、摸头、控制台 ▶。播完回待机。</summary>
    User,
}

public enum PlayResult { Played, Queued, Rejected }

/// <summary>
/// 一次「播这个动作」的请求。<paramref name="UntilEvents"/> 或 <paramref name="LoopFor"/> 不为空（或者动作本身是 loop）时循环播放，
/// 直到收到其中一个事件（<see cref="PetDirector.StopLoop"/>）或超时。
/// </summary>
public sealed record ActionRequest(
    ActionDef Action,
    ActionPriority Priority,
    string Source,
    TimeSpan? LoopFor = null,
    IReadOnlyList<string>? UntilEvents = null);

/// <summary>
/// 动画状态机，不碰窗口：决定下一段播什么、管理解码流、数帧号、记朝向。设计见 design/pet-studio/02-triggers.md §4、§5。
/// 外部通过 <see cref="Request"/> 提交动作请求，按优先级决定打断、排队还是丢弃。一段播完后按顺序选下一段：
/// 拖拽中 → 拖拽；用户触发的 → 待机；循环中 → 再来一遍；在放音乐 → 唱歌；待播队列；动作间隔内 → 待机；
/// 否则按各动作的随机链权重（<see cref="ActionDef.Ambient"/>）抽。转向动作播完才翻转朝向。
/// 窗口每个 tick 调 <see cref="Tick"/> 取一帧，在 <see cref="ClipStarted"/> 里处理走路之类和位置有关的事。
/// </summary>
public sealed class PetDirector : IDisposable
{
    public const int MaxPending = 3;
    public static readonly TimeSpan PendingTtl = TimeSpan.FromSeconds(10);
    /// <summary>没说什么时候停的循环最多播多久。</summary>
    public static readonly TimeSpan DefaultLoop = TimeSpan.FromSeconds(60);
    /// <summary>等事件结束的循环最多播多久，防止事件丢了一直循环。</summary>
    public static readonly TimeSpan MaxEventLoop = TimeSpan.FromMinutes(10);

    private sealed record Pending(ActionRequest Request, DateTimeOffset Expires);
    /// <summary>
    /// 下一段。<paramref name="Route"/> 是播完这段之后还要接着播的（姿态间寻路的剩余过渡 + 最终目标），
    /// <paramref name="LoopRequest"/> 是这段开始时要设成循环的请求（寻路的终点是循环请求时）。
    /// </summary>
    private sealed record Next(IFrameSource Stream, ActionDef Action, ActionPriority Priority, string Source, Pending? From,
        IReadOnlyList<ActionRequest>? Route = null, ActionRequest? LoopRequest = null);
    private sealed record Loop(ActionRequest Request, DateTimeOffset Until);

    private PetLibrary _library;
    private readonly PetSettings _settings;
    private readonly Random _random;
    private readonly TimeProvider _time;

    private Next? _queued;                          // 预先开好的下一段，切换时首帧已经在队列里
    private readonly List<Pending> _pending = new();  // 暂时打断不了当前片段的请求
    private List<ActionRequest> _route = new();     // 姿态间寻路：当前这段之后要依次播的
    private Loop? _loop;
    private readonly Dictionary<string, DateTimeOffset> _lastAuto = new();   // 动作冷却：非用户触发的最近一次
    private DateTimeOffset _restUntil;              // 动作间隔：这之前随机链只抽待机

    /// <param name="random">抽动作用的随机数；测试时传固定种子。</param>
    /// <param name="time">时钟；测试时传假时钟。</param>
    public PetDirector(PetLibrary library, PetSettings settings, Random? random = null, TimeProvider? time = null)
    {
        _library = library;
        _settings = settings;
        _random = random ?? new Random();
        _time = time ?? TimeProvider.System;
    }

    public PetLibrary Library => _library;

    /// <summary>正在播的片段。调过 <see cref="Start"/> 之后才有。</summary>
    public IFrameSource Current { get; private set; } = null!;

    /// <summary>正在播的动作。</summary>
    public ActionDef CurrentAction { get; private set; } = null!;

    public ActionPriority CurrentPriority { get; private set; }

    /// <summary>当前动作是谁要求的：rule:&lt;规则 id&gt;、agent、console、ambient……</summary>
    public string CurrentSource { get; private set; } = "";

    /// <summary>最近一次取到的帧在当前片段里的帧号，换片段时为 -1。</summary>
    public int Frame { get; private set; }

    /// <summary>-1 朝左（素材原始朝向），+1 朝右。</summary>
    public int Direction { get; set; } = -1;

    /// <summary>当前片段由用户操作触发，播完回待机。</summary>
    public bool FromUser => CurrentPriority == ActionPriority.User;

    /// <summary>按住拖拽期间为 true，下一段一律接拖拽动画。用 <see cref="BeginDrag"/> / <see cref="EndDrag"/> 会顺带切动画。</summary>
    public bool Dragging { get; set; }

    /// <summary>当前姿态（design/pet-life/04 §3）。过渡动作播完才变；<see cref="Stances.Air"/> 表示在下落 / 被甩飞。</summary>
    public string Stance { get; private set; } = Stances.Ground;

    /// <summary>当前这段播完之后的姿态：正在播过渡动作时是它的终点。</summary>
    public string NextStance => CurrentAction?.Transition?.To ?? Stance;

    public bool Airborne => Stance == Stances.Air;

    /// <summary>当前姿态里的待机带 sleep 角色：在睡觉。</summary>
    public bool Sleeping => Stance != Stances.Ground && _library.Actions.InStance(Stance).Any(a => a.Transition is null && a.HasRole(Roles.Sleep));

    /// <summary>待播队列里的请求数（测试和控制台用）。</summary>
    public int PendingCount => _pending.Count;

    /// <summary>
    /// 随机链权重调制（design/pet-life/06 §3.3）：返回乘在 ambient 上的倍率。宠物体系开着时由 PetLife 设置；null 不调制。
    /// </summary>
    public Func<ActionDef, double>? AmbientWeight { get; set; }

    /// <summary>
    /// 这个动作现在能不能用（比如亲密度还没解锁）。不能用的不进随机链、不按角色挑；用户直接点名（控制台 ▶）不受限。null 都能用。
    /// </summary>
    public Func<ActionDef, bool>? Usable { get; set; }

    /// <summary>
    /// 能不能进入这个姿态：贴墙要站在屏幕边上、放置方式是「站在地面」并且允许爬墙，由 PetBody 设置。null 都能进。
    /// </summary>
    public Func<string, bool>? CanEnter { get; set; }

    /// <summary>新片段开始播（还没取首帧）。</summary>
    public event Action<IFrameSource>? ClipStarted;

    /// <summary>新片段开始播，带上动作和来源。循环的每一遍都会触发。</summary>
    public event Action<ActionDef, ActionPriority, string>? ActionStarted;

    /// <summary>姿态变了（参数是新姿态）。</summary>
    public event Action<string>? StanceChanged;

    /// <summary>换了角色（<see cref="UseLibrary"/>）。</summary>
    public event Action? LibraryChanged;

    public bool Mirrored => Direction > 0 && CurrentAction.Mirror;

    private DateTimeOffset Now => _time.GetUtcNow();

    public void Start() => Play(PetLibrary.Idle, fromUser: false);

    /// <summary>换角色：丢掉旧角色在播和预取的解码流、排队和循环，换上新素材库，从待机重新开始。</summary>
    public void UseLibrary(PetLibrary library)
    {
        _pending.Clear();
        _route.Clear();
        _loop = null;
        _lastAuto.Clear();
        _library = library;
        Dragging = false;
        SetStance(Stances.Ground);
        Start();
        LibraryChanged?.Invoke();
    }

    /// <summary>取下一帧。解码没跟上返回 null，调用方停在当前帧；当前片段播完就接下一段。</summary>
    public byte[]? Tick()
    {
        var frame = Current.TryTake();
        if (frame is null)
        {
            if (!Current.Completed) return null;
            Finish();
            frame = Current.TryTake();
            if (frame is null) return null;
        }
        Frame++;
        return frame;
    }

    /// <summary>
    /// 提交动作请求：优先级不低于当前片段且当前动作允许打断 → 立即播；否则 system / agent 的请求排队
    /// （最多 <see cref="MaxPending"/> 个，<see cref="PendingTtl"/> 后过期），ambient 和 user 的丢弃。
    /// 非用户请求还受动作的冷却限制。动作不在当前姿态时先播过渡动作走过去，走不过去就拒绝。
    /// 拖拽中、空中时不打断（排队等落地）。
    /// </summary>
    public PlayResult Request(ActionRequest request)
    {
        var action = request.Action;
        if (!action.Enabled || action.Clips.Count == 0) return PlayResult.Rejected;
        if (request.Priority < ActionPriority.User && OnCooldown(action)) return PlayResult.Rejected;
        if (request.Priority < ActionPriority.User && Usable is not null && !Usable(action)) return PlayResult.Rejected;

        if (!Dragging && !Airborne && CanInterrupt(request.Priority))
        {
            if (RouteTo(Stance, request) is { } route)
            {
                BeginRoute(route);
                return PlayResult.Played;
            }
            // 地面上的动作、但从这里下不去（比如在墙上离地面太远）：先松手掉下去，落地后再播（04 §10）。
            if (action.Stance != Stances.Ground || Path(Stance, Stances.Air) is not { Count: > 0 } drop) return PlayResult.Rejected;
            BeginRoute(drop.Select(t => new ActionRequest(t, request.Priority, request.Source)).ToList());
            Purge();
            if (_pending.Count >= MaxPending) _pending.RemoveAt(0);
            _pending.Add(new Pending(request, Now + PendingTtl));
            return PlayResult.Queued;
        }
        if (request.Priority is not (ActionPriority.System or ActionPriority.Agent))
            return PlayResult.Rejected;

        Purge();
        if (_pending.Count >= MaxPending) _pending.RemoveAt(0);   // 挤掉最老的
        _pending.Add(new Pending(request, Now + PendingTtl));
        Replan();
        return PlayResult.Queued;
    }

    /// <summary>
    /// 走到某个姿态（规则的 <c>do: "stance:&lt;姿态&gt;"</c>）：依次播过渡动作，到了之后在那里待机。
    /// 已经在那里返回 Played；走不到返回 Rejected。
    /// </summary>
    public PlayResult RequestStance(string stance, ActionPriority priority, string source)
    {
        if (NextStance == stance && _route.Count == 0) return PlayResult.Played;
        if (Dragging || Airborne || !CanInterrupt(priority)) return PlayResult.Rejected;
        if (Path(Stance, stance) is not { Count: > 0 } path) return Stance == stance ? PlayResult.Played : PlayResult.Rejected;
        BeginRoute(path.Select(t => new ActionRequest(t, priority, source)).ToList());
        return PlayResult.Played;
    }

    /// <summary>这个动作现在能不能做到：在当前姿态，或者能沿姿态图走过去（Agent 的动作清单用）。</summary>
    public bool CanReach(ActionDef action) =>
        action.Stance == Stance || (!Airborne && Path(Stance, action.Stance) is not null);

    /// <summary>收到事件时调：等这个事件结束的循环停下来（当前这一遍播完为止）。</summary>
    public void StopLoop(string eventType)
    {
        if (_loop?.Request.UntilEvents is { } until && until.Contains(eventType))
        {
            _loop = null;
            Replan();
        }
    }

    /// <summary>当前有没有在循环（测试和控制台用）。</summary>
    public bool Looping => _loop is not null;

    /// <summary>开始拖拽：立即切到拖拽动画，之后一直循环到 <see cref="EndDrag"/>。</summary>
    public void BeginDrag()
    {
        Dragging = true;
        _route.Clear();
        Play(PetLibrary.Drag, fromUser: true);
    }

    /// <summary>松手：回到地面姿态待机。要不要往下掉由 PetBody 决定（<see cref="BeginFall"/>）。</summary>
    public void EndDrag()
    {
        Dragging = false;
        SetStance(Stances.Ground);
        Play(PetLibrary.Idle, fromUser: false);
    }

    /// <summary>开始下落 / 被甩飞：进入 air，循环播 fall（回退 drag），直到 <see cref="Land"/> 或 <see cref="GrabWall"/>。</summary>
    public void BeginFall()
    {
        _route.Clear();
        SetStance(Stances.Air);
        Interrupt();
        Start(Open(PickInStance(Roles.Fall, Stances.Ground), ActionPriority.Ambient, "fall", null));
    }

    /// <summary>落地：回到地面待机（落地反应由规则 landed / landed-hard 请求）。</summary>
    public void Land()
    {
        SetStance(Stances.Ground);
        Interrupt();
        Start(Open(PickInStance(Roles.Idle, Stances.Ground), ActionPriority.Ambient, "ambient", null));
    }

    /// <summary>
    /// 物理落地的第一步：只回到地面姿态，下落片段先接着播，也不预取下一段。
    /// 紧接着发的 landed 事件会让规则直接请求落地动作，第一条打开的解码流就是它。
    /// 以前先 <see cref="Land"/> 开待机流（加预取共两个 ffmpeg），马上又被落地动作打断再开两个，首帧要等这几个进程都起来，看着像落地慢半拍。
    /// 之后一定要调 <see cref="FinishLanding"/>。
    /// </summary>
    public void Touchdown()
    {
        SetStance(Stances.Ground);
        _queued?.Stream.Dispose();
        _queued = null;
        _touchdown = true;
    }

    /// <summary>落地的第二步：落地反应没接上（规则关了、在冷却、被拒绝）就回待机。</summary>
    public void FinishLanding()
    {
        if (_touchdown && Stance == Stances.Ground) Land();
        _touchdown = false;
    }

    private bool _touchdown;

    /// <summary>在空中抓住了墙：没有可用的贴墙姿态时返回 false（继续掉）。</summary>
    public bool GrabWall()
    {
        if (!_library.Stances.Available.Contains(Stances.Wall)) return false;
        SetStance(Stances.Wall);
        Interrupt();
        Start(Open(PickInStance(Roles.Idle, Stances.Wall), ActionPriority.Ambient, "ambient", null));
        return true;
    }

    /// <summary>立即打断当前片段，改播这个角色（走回退链）的一个动作。控制台和测试用，不看优先级。</summary>
    public void Play(string role, bool fromUser)
    {
        var stance = Dragging ? Stances.Ground : Stance == Stances.Air ? Stances.Ground : Stance;
        var action = _library.Actions.PickByRole(role, _random, stance)
                     ?? _library.Actions.Resolve(role, _random, stance)
                     ?? PickInStance(PetLibrary.Click, stance);
        var request = new ActionRequest(action, fromUser ? ActionPriority.User : ActionPriority.Ambient, fromUser ? "user" : "ambient");
        if (!Dragging && RouteTo(Stance, request) is { } route) BeginRoute(route);
        else Begin(request);
    }

    /// <summary>立即打断当前片段，改播指定片段。</summary>
    public void Play(PetClip clip, bool fromUser)
    {
        var action = _library.ActionOf(clip) ?? new ActionDef { Id = "clip", Name = clip.Name, Roles = [clip.Category], Clips = [clip] };
        var priority = fromUser ? ActionPriority.User : ActionPriority.Ambient;
        var source = fromUser ? "user" : "ambient";
        if (action.Stance != Stance && !Dragging && !Airborne
            && RouteTo(Stance, new ActionRequest(action, priority, source)) is { Count: > 1 } route)
        {
            // 先走到这个动作的姿态，最后一段播指定的片段。
            _route = route.Skip(1).ToList();
            _loop = null;
            Interrupt();
            Start(Open(route[0].Action, priority, source, null) with { Route = _route.ToList() });
            return;
        }
        _route.Clear();
        Interrupt();
        Start(new Next(_library.Open(clip), action, priority, source, null));
    }

    private bool CanInterrupt(ActionPriority priority) =>
        Current is null
        || (priority >= CurrentPriority && CurrentAction.Interrupt switch
        {
            InterruptPolicy.Any => true,
            InterruptPolicy.User => priority == ActionPriority.User,
            _ => false,
        });

    /// <summary>从 <paramref name="from"/> 姿态播 <paramref name="request"/> 要依次播的请求（过渡 + 目标）；走不到返回 null。</summary>
    private List<ActionRequest>? RouteTo(string from, ActionRequest request)
    {
        var action = request.Action;
        if (action.Stance == from) return [request];
        if (Path(from, action.Stance) is not { } path) return null;
        var route = path.Select(t => new ActionRequest(t, request.Priority, request.Source)).ToList();
        route.Add(request);
        return route;
    }

    private IReadOnlyList<ActionDef>? Path(string from, string to) =>
        _library.Stances.Path(from, to, t => CanEnter?.Invoke(t.Transition!.To) ?? true);

    private void BeginRoute(List<ActionRequest> route)
    {
        Begin(route[0]);
        if (route.Count == 1) return;
        _route = route.Skip(1).ToList();
        Replan();
    }

    private void Begin(ActionRequest request)
    {
        _route.Clear();
        SetLoop(request);
        // 其他请求打断循环时循环不取消：插播完回到循环（和听歌时点一下、播完接着唱一样）。
        Interrupt();
        Start(Open(request.Action, request.Priority, request.Source, null));
    }

    private void SetLoop(ActionRequest request)
    {
        var action = request.Action;
        if (request.LoopFor is not null || request.UntilEvents is { Count: > 0 } || action.Kind == ActionKind.Loop)
        {
            var limit = request.LoopFor ?? (request.UntilEvents is { Count: > 0 } ? MaxEventLoop : DefaultLoop);
            _loop = new Loop(request, Now + limit);
        }
    }

    private void Interrupt()
    {
        _queued?.Stream.Dispose();
        _queued = null;
        Current?.Dispose();
    }

    private Next Open(ActionDef action, ActionPriority priority, string source, Pending? from) =>
        new(_library.Open(PetLibrary.PickClip(action, _random)), action, priority, source, from);

    private void SetStance(string stance)
    {
        if (Stance == stance) return;
        Stance = stance;
        StanceChanged?.Invoke(stance);
    }

    private void Finish()
    {
        if (CurrentAction.HasRole(PetLibrary.Turn) && Stance == Stances.Ground)
            Direction = -Direction;
        if (CurrentAction.Transition is { } transition)
            SetStance(transition.To);
        if (!CurrentAction.HasRole(PetLibrary.Idle) && !FromUser && CurrentAction.Transition is null)
            _restUntil = Now.AddSeconds(_settings.ActionInterval);

        var next = _queued ?? Plan();
        _queued = null;
        if (next.From is not null) _pending.Remove(next.From);
        Current.Dispose();
        Start(next);
    }

    [MemberNotNull(nameof(Current), nameof(CurrentAction))]
    private void Start(Next next)
    {
        _touchdown = false;
        Current = next.Stream;
        CurrentAction = next.Action;
        CurrentPriority = next.Priority;
        CurrentSource = next.Source;
        Frame = -1;
        if (next.Route is not null) _route = next.Route.ToList();
        if (next.LoopRequest is not null) SetLoop(next.LoopRequest);
        if (next.Priority < ActionPriority.User && next.Action.Cooldown > TimeSpan.Zero)
            _lastAuto[next.Action.Id] = Now;
        ClipStarted?.Invoke(next.Stream);
        ActionStarted?.Invoke(next.Action, next.Priority, next.Source);
        _queued = Plan();
    }

    /// <summary>排队或循环状态变了：丢掉预取的下一段重新选。</summary>
    private void Replan()
    {
        if (Current is null) return;
        _queued?.Stream.Dispose();
        _queued = Plan();
    }

    /// <summary>这个姿态下按角色挑一个（走回退链、过滤不能用的）；没有就用这个姿态的待机，再没有就用地面的点击。</summary>
    private ActionDef PickInStance(string role, string stance) =>
        _library.Actions.PickByRole(role, _random, stance, Usable)
        ?? _library.Actions.PickByRole(role, _random, stance)
        ?? _library.Actions.PickByRole(PetLibrary.Idle, _random, stance)
        ?? _library.Actions.PickByRole(PetLibrary.Click, _random)!;

    /// <summary>
    /// 选下一段并打开解码流。在当前片段开始时就为下一段预取调用，所以间隔判断要看当前片段播完的时刻，
    /// 姿态也按当前片段播完之后的算（<see cref="NextStance"/>）。
    /// </summary>
    private Next Plan()
    {
        var actions = _library.Actions;
        var stance = NextStance;
        Next ByRole(string role, ActionPriority priority, string source) =>
            Open(PickInStance(role, stance == Stances.Air ? Stances.Ground : stance), priority, source, null);

        if (Dragging) return Open(PickInStance(PetLibrary.Drag, Stances.Ground), ActionPriority.Ambient, "drag", null);
        if (stance == Stances.Air) return ByRole(Roles.Fall, ActionPriority.Ambient, "fall");

        // 寻路：上一段是过渡动作，接着播下一段（可能是目标本身）。
        if (_route.Count > 0 && _route[0].Action.Stance == stance)
        {
            var step = _route[0];
            var rest = _route.Skip(1).ToList();
            return Open(step.Action, step.Priority, step.Source, null) with
            {
                Route = rest,
                LoopRequest = rest.Count == 0 && IsLoopRequest(step) ? step : null,
            };
        }

        if (FromUser) return ByRole(PetLibrary.Idle, ActionPriority.Ambient, "ambient");

        if (_loop is { } loop)
        {
            if (Now < loop.Until && loop.Request.Action.Enabled && loop.Request.Action.Stance == stance)
                return Open(loop.Request.Action, loop.Request.Priority, loop.Request.Source, null);
            _loop = null;
        }

        if (stance == Stances.Ground && _settings is { MusicDetect: true, MusicPlaying: true, PowerSavingActive: false })
            return ByRole(PetLibrary.Sing, ActionPriority.System, "music");

        Purge();
        while (_pending.Count > 0)
        {
            var first = _pending[0];
            if (RouteTo(stance, first.Request) is { } route)
            {
                var rest = route.Skip(1).ToList();
                return Open(route[0].Action, route[0].Priority, route[0].Source, first) with
                {
                    Route = rest,
                    LoopRequest = rest.Count == 0 && IsLoopRequest(first.Request) ? first.Request : null,
                };
            }
            _pending.RemoveAt(0);   // 走不到那个姿态：丢掉
        }

        var clip = Current.Clip;
        if (_settings.ActionInterval > 0)
        {
            if (!CurrentAction.HasRole(PetLibrary.Idle)) return ByRole(PetLibrary.Idle, ActionPriority.Ambient, "ambient");
            var endsAt = Now.AddSeconds(clip.EstimatedFrames / clip.Fps / _settings.EffectiveSpeed);
            if (endsAt < _restUntil) return ByRole(PetLibrary.Idle, ActionPriority.Ambient, "ambient");
        }

        var picked = actions.PickAmbient(_random, a => AllowedAmbient(a, stance), AmbientWeight);
        return picked is null
            ? ByRole(PetLibrary.Idle, ActionPriority.Ambient, "ambient")
            : Open(picked, ActionPriority.Ambient, "ambient", null);
    }

    private static bool IsLoopRequest(ActionRequest r) =>
        r.LoopFor is not null || r.UntilEvents is { Count: > 0 } || r.Action.Kind == ActionKind.Loop;

    /// <summary>
    /// 随机链能抽的动作：这个姿态下的；省电时只有待机和离开睡姿以外的不抽；「不移动」时不抽移动和攀爬；
    /// 冷却中的、没解锁的、进不去的过渡不抽。
    /// </summary>
    private bool AllowedAmbient(ActionDef action, string stance)
    {
        if (action.Stance != stance) return false;
        if (_settings.PowerSavingActive) return action.HasRole(PetLibrary.Idle) && action.Transition is null;
        if (_settings.StayStill && (action.HasRole(PetLibrary.Move) || action.HasRole(Roles.Climb))) return false;
        if (action.Transition is { } t && (!_library.Stances.IsAvailable(t.To) || CanEnter?.Invoke(t.To) == false)) return false;
        if (Usable is not null && !Usable(action)) return false;
        return !OnCooldown(action);
    }

    private bool OnCooldown(ActionDef action) =>
        action.Cooldown > TimeSpan.Zero && _lastAuto.TryGetValue(action.Id, out var last) && Now - last < action.Cooldown;

    private void Purge()
    {
        var now = Now;
        _pending.RemoveAll(p => p.Expires <= now);
    }

    public void Dispose() => Interrupt();
}
