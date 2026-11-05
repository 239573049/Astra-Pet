namespace AstraPet.Core.Behaviors;

/// <summary>
/// 光标相关的小互动（design/pet-life/03 §4.3、§4.4）：光标在背后停一会儿 → 发 cursor.behind（规则 look-at 让它转身）；
/// 给 PetBody 提供「主动靠近光标」的目标。只读光标位置，不读别的。
/// </summary>
public sealed class CursorBehavior : PetBehavior
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan BehindFor = TimeSpan.FromSeconds(1.5);

    private DateTimeOffset? _behindSince;

    protected override void OnStart()
    {
        Every(Interval, Tick);
        if (Context.Body is { } body)
        {
            body.ApproachTarget = ApproachTarget;
            body.Energy = () => Context.Life is { Enabled: true } life ? life.Data.Energy : null;
            if (Context.Life is { } l) body.Profile = () => l.Profile;
        }
    }

    private PetBodyMetrics Body => (Context.Life?.Profile ?? Context.Director.Library.Profile).Body;

    /// <summary>屏幕上看到的身体左右边（画布坐标），朝右时镜像。</summary>
    private (double Left, double Right) Span()
    {
        var b = Body;
        return Context.Director.Direction < 0 ? (b.X0, b.X1) : (PetClip.Width - b.X1, PetClip.Width - b.X0);
    }

    private void Tick()
    {
        var director = Context.Director;
        if (Settings.Paused || director.Stance != Stances.Ground || director.Dragging
            || !director.CurrentAction.HasRole(Roles.Idle) || Context.Cursor() is not var (cx, cy))
        {
            _behindSince = null;
            return;
        }
        var b = Body;
        var (left, right) = Span();
        var behind = director.Direction < 0 ? cx > right : cx < left;
        var dx = director.Direction < 0 ? cx - right : left - cx;
        var near = dx < b.Width * 3 && Math.Abs(cy - (b.Y0 + b.Y1) / 2) < b.Height * 1.5;
        if (!behind || !near)
        {
            _behindSince = null;
            return;
        }
        _behindSince ??= Now;
        if (Now - _behindSince < BehindFor) return;
        _behindSince = null;
        // 好奇的宠物更爱回头看。
        var traits = Context.Life?.Traits ?? director.Library.Profile.Traits;
        if (Context.Random.NextDouble() < 0.5 * PetTraits.Factor(traits.Curious))
            Context.Events.Publish(PetEvents.CursorBehind);
    }

    /// <summary>亲密度 ≥ 3、心情不低落时，随机走路有一定概率朝光标走（频率看黏人程度）。光标在别的屏幕上不去。</summary>
    private double? ApproachTarget()
    {
        if (Context.Life is not { Enabled: true } life || life.Level < 3 || life.Data.Mood < -30) return null;
        if (Context.Random.NextDouble() >= 0.3 * PetTraits.Factor(life.Traits.Clingy)) return null;
        if (Context.Body is not { } body || Context.Cursor() is not var (cx, cy)) return null;
        var (x, y) = body.CanvasToDesktop(cx, cy);
        var (center, top) = body.BodyPosition;
        return body.Surfaces.ScreenAt(center, top) == body.Surfaces.ScreenAt(x, y) ? x : null;
    }
}

/// <summary>
/// 陪它玩（design/pet-life/03 §4.5）：一分钟里宠物在地面上追着光标跑，光标在它头顶附近停半秒算「扑到了」。
/// 期间点击照常；结束时发 play.ended（PetLife 按一轮加心情和亲密度）。
/// </summary>
public sealed class PlayBehavior : PetBehavior
{
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(400);
    public static readonly TimeSpan CatchFor = TimeSpan.FromSeconds(0.5);

    private DateTimeOffset _until;
    private DateTimeOffset? _overHeadSince;

    public bool Playing { get; private set; }
    public int Caught { get; private set; }

    protected override void OnStart() => Every(Interval, Tick);

    public void Start()
    {
        if (Playing) return;
        Playing = true;
        Caught = 0;
        _until = Now + Duration;
        Context.Events.Publish(PetEvents.PlayStarted);
    }

    public void Stop()
    {
        if (!Playing) return;
        Playing = false;
        _overHeadSince = null;
        Context.Events.Publish(PetEvents.PlayEnded, ("caught", Caught.ToString()));
    }

    private void Tick()
    {
        if (!Playing) return;
        if (Now >= _until)
        {
            Stop();
            return;
        }
        var director = Context.Director;
        if (director.Dragging || director.Airborne || Context.Cursor() is not var (cx, cy)) return;

        var b = (Context.Life?.Profile ?? director.Library.Profile).Body;
        var (left, right) = director.Direction < 0 ? (b.X0, b.X1) : (PetClip.Width - b.X1, PetClip.Width - b.X0);
        var overHead = cx >= left && cx <= right && cy >= b.Y0 - b.Height * 0.4 && cy <= b.Y0 + b.Height * 0.35;
        if (overHead)
        {
            _overHeadSince ??= Now;
            if (Now - _overHeadSince >= CatchFor)
            {
                _overHeadSince = null;
                Caught++;
                Context.Events.Publish(PetEvents.PlayCaught);
            }
            return;
        }
        _overHeadSince = null;

        // 光标离得远、宠物闲着：朝光标跑一段。
        if (director.Stance != Stances.Ground || Context.Body is not { } body) return;
        if (!director.CurrentAction.HasRole(Roles.Idle)) return;
        var (x, _) = body.CanvasToDesktop(cx, cy);
        if (Math.Abs(x - body.BodyPosition.CenterX) < b.Width * body.Scale * 0.5) return;
        if (director.Library.Actions.PickByRole(Roles.Move, Context.Random) is not { } move) return;
        body.Goal = x;
        director.Request(new ActionRequest(move, ActionPriority.User, "play"));
    }
}

/// <summary>
/// 召唤（design/pet-life/03 §4.6）：按全局快捷键，宠物出现在光标下面，发 hotkey.summon（规则 summon 让它打招呼）。
/// 设置里关掉或平台不支持时不注册。
/// </summary>
public sealed class SummonBehavior : PetBehavior
{
    public bool Active { get; private set; }

    protected override void OnStart()
    {
        Settings.PropertyChanged += OnSettingChanged;
        Apply();
    }

    private void OnSettingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PetSettings.SummonHotkey)) Apply();
    }

    private void Apply()
    {
        if (Settings.SummonHotkey && Context.Capabilities.GlobalHotkey)
        {
            Active = Context.Hotkey.Start(() => Context.Ui.Post(Summon));
            if (!Active) PetLog.Write("召唤快捷键注册失败（可能被别的程序占用了）");
        }
        else
        {
            Context.Hotkey.Stop();
            Active = false;
        }
    }

    /// <summary>召唤：跑到光标下面。没有位置信息（测试里）时只发事件。</summary>
    public void Summon()
    {
        if (Context.Body is { } body && Context.Cursor() is var (cx, cy))
        {
            var (x, y) = body.CanvasToDesktop(cx, cy);
            body.Summon(x, y);
        }
        Settings.Visible = true;
        Context.Events.Publish(PetEvents.Summoned);
    }

    public override void Dispose()
    {
        if (Context is not null)
        {
            Settings.PropertyChanged -= OnSettingChanged;
            Context.Hotkey.Stop();
        }
        base.Dispose();
    }
}
