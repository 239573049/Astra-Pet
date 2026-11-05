using System.ComponentModel;

namespace AstraPet.Core;

/// <summary>
/// 宠物的位置和物理（design/pet-life/04 §9）：窗口左上角在桌面上的坐标（桌面像素），
/// 走路 / 攀爬 / 过渡动作的位置插值，站在地面上的对齐，下落和抛掷的积分，贴墙的对齐。
/// 不认识 Avalonia：窗口把屏幕工作区、缩放、拖拽的位置和松手速度传进来，每帧调 <see cref="Step"/>，
/// 在 <see cref="Airborne"/> 时用自己的定时器调 <see cref="PhysicsStep"/>，然后把 <see cref="X"/> / <see cref="Y"/> 设到窗口上。
/// 「自由放置」模式下和 0.1.x 一样：没有重力，走路只改 X、在工作区边缘留 40 DIP 余量。
/// 会改 Director 状态的事（发事件、请求换姿态）都推迟到下一次 <see cref="Step"/> 里做，不在 Director 的回调里做，避免重入。
/// </summary>
public sealed class PetBody
{
    public const double EdgeMarginDip = 40;
    /// <summary>松手时离地面这么近（DIP）直接吸附落地，不播下落。</summary>
    public const double SnapDip = 20;
    /// <summary>松手速度超过这个（DIP/秒）算甩出去。</summary>
    public const double ThrowSpeedDip = 800;
    public const double GravityDip = 2600;
    public const double HardImpactDip = 900;
    public const double MaxSpeedDip = 6000;
    public const double AirDrag = 0.6;
    public static readonly TimeSpan MaxWallTime = TimeSpan.FromMinutes(3);

    private abstract record Motion;
    /// <summary>沿一个轴按步态走（地面走路、墙上攀爬）。</summary>
    private sealed record AxisMotion(Walk Walk, bool Vertical, bool CheckWall) : Motion;
    /// <summary>两个位置之间线性插值（过渡动作、在墙上往下滑）。</summary>
    private sealed record LerpMotion(double X0, double Y0, double X1, double Y1) : Motion;

    private readonly PetDirector _director;
    private readonly PetSettings _settings;
    private readonly Action<PetEvent> _publish;
    private readonly Random _random;
    private readonly TimeProvider _time;

    private Motion? _motion;
    private bool _motionDone;
    private double _vx, _vy;
    private bool _velocitySet;
    private bool _thrown;
    private int _wallSide = -1;
    private DateTimeOffset _wallSince;
    private bool _leaving;
    private bool _leaveRequested;
    private PetEvent? _deferredEvent;
    private bool _climbAfterWalk;
    private double _climbEdge;
    private bool _walkAgain;
    private bool _ceilingRequested;
    private bool _ceilingDrop;
    private (double Vx, double Vy)? _hop;
    private bool _configured;

    public PetBody(PetDirector director, PetSettings settings, Action<PetEvent> publish, Random? random = null, TimeProvider? time = null)
    {
        _director = director;
        _settings = settings;
        _publish = publish;
        _random = random ?? new Random();
        _time = time ?? TimeProvider.System;
        Profile = () => director.Library.Profile;
        director.ActionStarted += OnActionStarted;
        director.StanceChanged += OnStanceChanged;
        director.CanEnter = CanEnter;
        settings.PropertyChanged += OnSettingChanged;
    }

    /// <summary>窗口左上角（桌面像素）。</summary>
    public double X { get; private set; }
    public double Y { get; private set; }

    public SurfaceMap Surfaces { get; private set; } = new([]);
    /// <summary>画布像素 → 桌面像素（= 显示缩放 × 桌面缩放）。</summary>
    public double Scale { get; private set; } = 0.5;
    /// <summary>DIP → 桌面像素（桌面缩放）。</summary>
    public double Dip { get; private set; } = 1;

    public bool Held { get; private set; }
    public bool Airborne => _director.Airborne && !Held;

    /// <summary>位置变了（窗口照着设）。</summary>
    public event Action? Moved;

    /// <summary>当前档案（性格可能被用户覆盖过），由宿主接到 PetLife 上；默认用素材库里的。</summary>
    public Func<PetProfile> Profile { get; set; }

    /// <summary>当前精力（0..100）；宠物体系关着时返回 null，不限制爬墙。</summary>
    public Func<double?> Energy { get; set; } = () => null;

    /// <summary>随机走路开始时调：返回想走到的桌面 X（身体中心），比如主动靠近光标（03 §4.4）；不想就返回 null。</summary>
    public Func<double?> ApproachTarget { get; set; } = () => null;

    private PetBodyMetrics Body => Profile().Body;
    private bool GroundMode => _settings.GroundMode;
    private double W => PetClip.Width * Scale;
    private double H => PetClip.Height * Scale;

    // 身体在窗口里的偏移（桌面像素），朝右时按镜像算。
    private double BodyLeft(int dir) => (dir < 0 ? Body.X0 : PetClip.Width - Body.X1) * Scale;
    private double BodyRight(int dir) => (dir < 0 ? Body.X1 : PetClip.Width - Body.X0) * Scale;
    private double CenterX => X + (BodyLeft(_director.Direction) + BodyRight(_director.Direction)) / 2;
    private double FeetOffset => (_director.Stance == Stances.Wall ? Body.WallFeetY : Body.FeetY) * Scale;
    private double Feet => Y + FeetOffset;

    // ───── 配置 ─────

    /// <summary>屏幕或缩放变了。第一次调用时把宠物放好；之后保持身体中心和脚底不动，掉出屏幕就放回来。</summary>
    public void Configure(SurfaceMap surfaces, double scale, double dip)
    {
        var first = !_configured;
        _configured = true;
        var centerX = X + W / 2;
        var feet = Feet;
        Surfaces = surfaces;
        Scale = scale;
        Dip = dip;
        if (first) { Reset(); return; }
        X = centerX - W / 2;
        Y = feet - FeetOffset;
        _motion = null;
        Settle();
    }

    /// <summary>放回主屏中央：站在地面模式下站在地面上，自由放置时在正中间。</summary>
    public void Reset()
    {
        var area = Surfaces.Primary;
        _motion = null;
        Held = false;
        if (_director.Stance != Stances.Ground && _director.Current is not null) _director.Land();
        X = area.X + (area.Width - W) / 2;
        Y = GroundMode ? area.Bottom - Body.FeetY * Scale : area.Y + (area.Height - H) / 2;
        Moved?.Invoke();
    }

    /// <summary>位置不在任何屏幕上（拔了副屏、改了分辨率）时放回来；站在地面模式下贴回地面。</summary>
    public void Settle()
    {
        var bounds = Surfaces.Bounds;
        if (CenterX < bounds.X || CenterX > bounds.Right || Y + Body.Y0 * Scale > bounds.Bottom || Feet < bounds.Y)
        {
            Reset();
            return;
        }
        if (GroundMode && _director.Stance == Stances.Ground && !Held && !Airborne)
            Y = FloorAt(CenterX, Feet) - FeetOffset;
        Moved?.Invoke();
    }

    // ───── 拖拽 ─────

    public void BeginHold()
    {
        Held = true;
        _motion = null;
        _vx = _vy = 0;
    }

    /// <summary>拖拽中，窗口被拖到了这里。</summary>
    public void HoldAt(double x, double y)
    {
        X = x;
        Y = y;
    }

    /// <summary>
    /// 松手（在 Director.EndDrag 之后调）。<paramref name="vx"/> / <paramref name="vy"/> 是最近 100 毫秒的速度（桌面像素 / 秒）。
    /// 自由放置：原地不动。站在地面：离地面很近直接吸附；否则下落，速度够快算甩出去（04 §4.2、§4.3）。
    /// </summary>
    public void Release(double vx, double vy)
    {
        Held = false;
        if (!GroundMode) return;
        var floor = FloorAt(CenterX, Feet);
        if (Feet >= floor - SnapDip * Dip)
        {
            Y = floor - FeetOffset;   // 很近，或者被拖到了任务栏里：直接站上去
            Moved?.Invoke();
            return;
        }
        var speed = Math.Sqrt(vx * vx + vy * vy);
        _thrown = _settings.AllowThrow && speed >= ThrowSpeedDip * Dip;
        if (_thrown)
        {
            var k = 1 / Math.Sqrt(Profile().Physics.Weight);
            var max = MaxSpeedDip * Dip;
            (_vx, _vy) = (Math.Clamp(vx * k, -max, max), Math.Clamp(vy * k, -max, max));
            _publish(PetEvent.Of(PetEvents.Thrown, ("speed", ((int)(speed / Dip)).ToString())));
        }
        else (_vx, _vy) = (0, 0);
        _velocitySet = true;
        _director.BeginFall();
        Moved?.Invoke();
    }

    // ───── 每帧 ─────

    /// <summary>窗口每取到一帧调一次：按帧号放窗口；处理推迟的事件和姿态请求。</summary>
    public void Step(int frame, int totalFrames)
    {
        if (_deferredEvent is { } e)
        {
            _deferredEvent = null;
            _publish(e);
        }
        if (_leaveRequested)
        {
            _leaveRequested = false;
            Leave();
        }
        if (_walkAgain)
        {
            _walkAgain = false;
            WalkToClimb();
        }
        if (_ceilingRequested)
        {
            _ceilingRequested = false;
            _director.RequestStance(Stances.Ceiling, ActionPriority.Ambient, "climb-top");
        }
        if (_hop is { } hop)
        {
            _hop = null;
            Toss(hop.Vx, hop.Vy, thrown: false);
        }
        if (Held || Airborne || _motion is null) return;

        var t = totalFrames <= 1 ? 1 : Math.Clamp((double)frame / (totalFrames - 1), 0, 1);
        switch (_motion)
        {
            case AxisMotion { Vertical: false } walk:
                X = walk.Walk.XAt(frame, totalFrames);
                break;
            case AxisMotion { Vertical: true } climb:
                Y = climb.Walk.XAt(frame, totalFrames);
                break;
            case LerpMotion lerp:
                X = lerp.X0 + (lerp.X1 - lerp.X0) * t;
                Y = lerp.Y0 + (lerp.Y1 - lerp.Y0) * t;
                break;
        }
        Moved?.Invoke();

        if (!_motionDone && frame >= totalFrames - 1)
        {
            _motionDone = true;
            if (_motion is AxisMotion { CheckWall: true }) CheckWallReached();
        }
    }

    /// <summary>空中时窗口每 16 毫秒左右调一次。</summary>
    public void PhysicsStep(TimeSpan elapsed)
    {
        if (!Airborne) return;
        var dt = Math.Clamp(elapsed.TotalSeconds, 0, 0.05);
        var physics = Profile().Physics;
        _vy += GravityDip * Dip * dt;
        var drag = Math.Max(0, 1 - AirDrag * dt);
        _vx *= drag;
        _vy *= drag;
        X += _vx * dt;
        Y += _vy * dt;

        var dir = _director.Direction;
        var bounds = Surfaces.Bounds;
        var screen = Surfaces.ScreenAt(Math.Clamp(CenterX, bounds.X, bounds.Right - 1), Math.Clamp(Feet - 1, bounds.Y, bounds.Bottom - 1));

        // 左右：墙（抓住或反弹）；屏幕之间的开口照常穿过去，只是别飞出所有屏幕。
        foreach (var side in new[] { -1, 1 })
        {
            var edge = side < 0 ? X + BodyLeft(dir) : X + BodyRight(dir);
            var limit = side < 0 ? screen.X : screen.Right;
            var outside = side < 0 ? edge < limit : edge > limit;
            if (!outside || !Surfaces.IsWall(screen, side)) continue;
            if (TryGrabWall(side, screen)) return;
            X += limit - edge;
            if (side < 0 ? _vx < 0 : _vx > 0) _vx = -_vx * physics.Bounce;
        }
        X = Math.Clamp(X, bounds.X - BodyLeft(dir), bounds.Right - BodyRight(dir));

        // 天花板
        var top = Y + Body.Y0 * Scale;
        if (top < screen.Y)
        {
            Y += screen.Y - top;
            if (_vy < 0) _vy = -_vy * physics.Bounce;
        }

        // 地面
        var floor = FloorAt(CenterX, Feet - Math.Abs(_vy) * dt - 1);
        if (Feet >= floor)
        {
            var hard = Math.Abs(_vy) >= HardImpactDip * Dip && (physics.Fragile || Profile().Traits.Bold < 50);
            Y = floor - FeetOffset;
            _vx = _vy = 0;
            var thrown = _thrown;
            _thrown = false;
            _director.Land();
            Moved?.Invoke();
            _publish(PetEvent.Of(PetEvents.Landed, ("impact", hard ? "hard" : "soft"), ("thrown", thrown ? "true" : "false")));
            return;
        }
        Moved?.Invoke();
    }

    // ───── 控制台 / 行为调用 ─────

    /// <summary>去最近的墙边，走到了就爬上去（试玩按钮「去爬墙」）。没有贴墙姿态或不允许时返回 false。</summary>
    public bool GoClimb()
    {
        if (!GroundMode || !_settings.AllowClimb || !_director.Library.Stances.Available.Contains(Stances.Wall)) return false;
        if (_director.Stance == Stances.Wall) return true;
        if (NearWall() is not null)
            return _director.RequestStance(Stances.Wall, ActionPriority.User, "console") == PlayResult.Played;
        var screen = Surfaces.ScreenAt(CenterX, Feet - 1);
        var side = CenterX - screen.X < screen.Right - CenterX ? -1 : 1;
        _climbEdge = side < 0 ? screen.X : screen.Right;
        _climbAfterWalk = true;
        return WalkToClimb();
    }

    /// <summary>去爬墙：朝墙走一圈（一圈走不到就接着走）。</summary>
    private bool WalkToClimb()
    {
        if (!_climbAfterWalk || _director.Library.Actions.PickByRole(Roles.Move, _random) is not { } move) return false;
        Goal = _climbEdge;
        if (_director.Request(new ActionRequest(move, ActionPriority.User, "console")) == PlayResult.Played) return true;
        _climbAfterWalk = false;
        return false;
    }

    /// <summary>
    /// 甩一下 / 掉下来（试玩按钮，05 §1 ⑦）：给一个初速度（DIP/秒，向上为负）直接进入下落。自由放置模式下没有重力，什么都不做。
    /// </summary>
    public bool Toss(double vxDip, double vyDip) => Toss(vxDip, vyDip, thrown: vxDip != 0 || vyDip != 0);

    private bool Toss(double vxDip, double vyDip, bool thrown)
    {
        if (!GroundMode || Held || Airborne || _director.Dragging) return false;
        (_vx, _vy) = (vxDip * Dip, vyDip * Dip);
        _thrown = thrown;
        _velocitySet = true;
        if (_thrown) _publish(PetEvent.Of(PetEvents.Thrown, ("speed", ((int)Math.Sqrt(vxDip * vxDip + vyDip * vyDip)).ToString())));
        _director.BeginFall();
        Moved?.Invoke();
        return true;
    }

    /// <summary>
    /// 召唤（design/pet-life/03 §4.6）：出现在光标所在屏幕上光标的正下方（站在地面模式）或光标处（自由放置）。
    /// </summary>
    public void Summon(double x, double y)
    {
        if (Held) return;
        var screen = Surfaces.ScreenAt(x, y);
        _motion = null;
        _vx = _vy = 0;
        if (_director.Stance != Stances.Ground) _director.Land();
        var half = (BodyLeft(_director.Direction) + BodyRight(_director.Direction)) / 2;
        X = Math.Clamp(x - half, screen.X - BodyLeft(_director.Direction), screen.Right - BodyRight(_director.Direction));
        Y = GroundMode ? screen.Bottom - Body.FeetY * Scale : Math.Clamp(y - H / 2, screen.Y, screen.Bottom - H);
        Moved?.Invoke();
    }

    /// <summary>
    /// 其他应用的窗口变了（design/pet-life/04 §7）：重算能站的窗口顶边。站着的那个窗口移动了就跟着移；
    /// 窗口关了、最小化、被挡住了就掉下去。传空列表等于关掉「站在窗口上」。
    /// </summary>
    public void UpdateWindows(IReadOnlyList<AstraPet.Platform.DesktopWindow> windows)
    {
        var standing = GroundMode && !Held && !_director.Airborne && !_director.Dragging && _director.Stance == Stances.Ground
            ? Surfaces.LedgeAt(CenterX, Feet) : null;
        var ledges = windows.Count == 0 ? [] : Surfaces.LedgesFrom(windows, Body.Width * Scale * 1.2);
        Surfaces = Surfaces.WithLedges(ledges);
        if (standing is not { } old) return;
        foreach (var l in ledges.Where(l => l.WindowId == old.WindowId))
        {
            var dx = l.WindowX - old.WindowX;
            var dy = l.Y - old.Y;
            if (!l.Contains(CenterX + dx)) continue;
            if (dx == 0 && dy == 0) return;
            X += dx;
            Y += dy;
            _motion = null;
            Moved?.Invoke();
            return;
        }
        // 脚下的窗口没了：掉下去。
        _vx = _vy = 0;
        _velocitySet = true;
        _director.BeginFall();
        Moved?.Invoke();
    }

    /// <summary>站在某个窗口的顶边上（不是屏幕地面）。</summary>
    public bool OnLedge => _director.Stance == Stances.Ground && Surfaces.LedgeAt(CenterX, Feet) is not null;

    /// <summary>下一次走路的目标（身体中心的桌面 X）。用一次就清掉。</summary>
    public double? Goal { get; set; }

    /// <summary>画布坐标（按屏幕上看到的，不管镜像）→ 桌面坐标。</summary>
    public (double X, double Y) CanvasToDesktop(double cx, double cy) => (X + cx * Scale, Y + cy * Scale);

    /// <summary>身体中心的桌面 X、头顶的桌面 Y。</summary>
    public (double CenterX, double Top) BodyPosition => (CenterX, Y + Body.Y0 * Scale);

    /// <summary>身体的桌面包围盒（左、上、右、脚底），按当前朝向算。</summary>
    public (double Left, double Top, double Right, double Bottom) BodyBounds =>
        (X + BodyLeft(_director.Direction), Y + Body.Y0 * Scale, X + BodyRight(_director.Direction), Feet);

    // ───── 内部 ─────

    private double FloorAt(double x, double y) =>
        Surfaces.FloorBelow(x, y) ?? Surfaces.ScreenAt(x, y).Bottom;

    /// <summary>离哪边的墙够近（身体边缘离屏幕边不到半个身宽，且那边是墙）：−1 左、+1 右、null 都不近。</summary>
    private int? NearWall()
    {
        var dir = _director.Direction;
        var screen = Surfaces.ScreenAt(CenterX, Feet - 1);
        if (_director.Stance == Stances.Ground && Surfaces.LedgeAt(CenterX, Feet) is not null) return null;   // 站在窗口上：不爬屏幕边
        var half = Body.Width * Scale / 2;
        if (X + BodyLeft(dir) - screen.X <= half && Surfaces.IsWall(screen, -1)) return -1;
        if (screen.Right - (X + BodyRight(dir)) <= half && Surfaces.IsWall(screen, 1)) return 1;
        return null;
    }

    /// <summary>贴着这边墙时窗口的 X：接触线对齐屏幕边。左墙不镜像，右墙镜像。</summary>
    private double WallX(int side, DesktopRect screen, ActionDef? action = null)
    {
        var contact = (action?.ContactX ?? Body.WallContactX) * Scale;
        return side < 0 ? screen.X - contact : screen.Right - (W - contact);
    }

    private bool CanEnter(string stance)
    {
        switch (stance)
        {
            case Stances.Wall:
                if (_director.Stance == Stances.Wall) return true;
                return GroundMode && _settings.AllowClimb && NearWall() is not null && (Energy() is not { } e || e >= 30);
            case Stances.Ground when _director.Stance == Stances.Wall:
                // 离地面不到一个身高才能直接下来，否则只能松手掉下去（04 §5.3）。
                return FloorAt(CenterX, Feet - 1) - Feet <= Body.Height * Scale;
            case Stances.Ceiling:
                if (_director.Stance == Stances.Ceiling) return true;
                // 只能从墙顶上翻过去：头离屏幕顶不到三分之一身高（04 §2 以后的倒挂）。
                return _director.Stance == Stances.Wall && GroundMode && _settings.AllowClimb
                       && Y + Body.Y0 * Scale - Surfaces.ScreenAt(CenterX, Y + Body.Y0 * Scale + 1).Y <= Body.Height * Scale * 0.35;
            default:
                return true;
        }
    }

    private void OnStanceChanged(string stance)
    {
        switch (stance)
        {
            case Stances.Air:
                _motion = null;
                if (!_velocitySet)
                {
                    // 从墙上松手：轻轻往外推一下，不然贴着墙直直掉；从天花板上松手直直掉。
                    _vx = _ceilingDrop ? 0 : (_wallSide < 0 ? 150 : -150) * Dip;
                    _vy = 0;
                }
                _velocitySet = false;
                _ceilingDrop = false;
                break;
            case Stances.Ceiling:
                _ceilingDrop = true;
                _wallSince = _time.GetUtcNow();
                _leaving = false;
                break;
            case Stances.Wall:
                _ceilingDrop = false;
                _wallSince = _time.GetUtcNow();
                _leaving = false;
                break;
        }
    }

    private void OnActionStarted(ActionDef action, ActionPriority priority, string source)
    {
        _motion = null;
        _motionDone = false;
        if (Held || _director.Airborne || _director.Dragging) return;
        var stance = _director.Stance;

        if (!GroundMode)
        {
            if (stance == Stances.Ground && action.HasRole(Roles.Move) && action.Gait is { Axis: GaitAxis.X } freeGait)
                PlanFreeWalk(freeGait);
            return;
        }

        if (action.Transition is { } transition)
        {
            PlanTransition(action, transition);
            return;
        }

        if (stance == Stances.Wall)
        {
            PlanOnWall(action);
            return;
        }
        if (stance == Stances.Ceiling)
        {
            PlanOnCeiling(action);
            return;
        }

        // 地面以及坐、睡这类地面上的姿态：脚底贴地。
        var floor = FloorAt(CenterX, Feet - 1);
        if (Math.Abs(floor - Feet) > 1)
        {
            Y = floor - FeetOffset;
            Moved?.Invoke();
        }
        if (stance == Stances.Ground && action.HasRole(Roles.Move) && action.Gait is { Axis: GaitAxis.X } gait)
            PlanGroundWalk(gait, source);
    }

    /// <summary>自由放置的走路：和 0.1.x 一样，在工作区左右边缘留 40 DIP。</summary>
    private void PlanFreeWalk(Gait gait)
    {
        var area = Surfaces.ScreenAt(X + W / 2, Y + H / 2);
        var margin = EdgeMarginDip * Dip;
        var minX = (int)(area.X + margin);
        var maxX = (int)(area.Right - W - margin);
        var stride = gait.Stride * Scale * Profile().Physics.WalkSpeed;
        var walk = WalkPlanner.Plan((int)X, minX, maxX, stride, gait, _random);
        _director.Direction = walk.Direction;
        _motion = new AxisMotion(walk, false, false);
    }

    /// <summary>站在地面上走：身体边缘可以走到屏幕边（为爬墙准备）；有目标就朝目标走。</summary>
    private void PlanGroundWalk(Gait gait, string source)
    {
        var screen = Surfaces.ScreenAt(CenterX, Feet - 1);
        var left = Math.Min(BodyLeft(-1), BodyLeft(1));
        var right = Math.Max(BodyRight(-1), BodyRight(1));
        var (from, to) = Surfaces.LedgeAt(CenterX, Feet) is { } ledge ? (ledge.X0, ledge.X1) : (screen.X, screen.Right);   // 在窗口上：只在这段顶边上走
        var minX = (int)Math.Ceiling(from - left);
        var maxX = (int)Math.Floor(to - right);
        var stride = gait.Stride * Scale * Profile().Physics.WalkSpeed;

        var goal = Goal;
        Goal = null;
        if (goal is null && source == "ambient") goal = ApproachTarget();
        int? target = goal is { } g ? (int)(g - (left + right) / 2) : null;
        var walk = WalkPlanner.Plan((int)X, minX, maxX, stride, gait, _random, target);
        _director.Direction = walk.Direction;
        _motion = new AxisMotion(walk, false, true);
    }

    private void CheckWallReached()
    {
        if (_director.Stance != Stances.Ground) return;
        if (Surfaces.LedgeAt(CenterX, Feet) is { } ledge)
        {
            // 走到窗口顶边的尽头：有时候跳下去（04 §7）。
            var half = Body.Width * Scale / 2;
            var end = CenterX - ledge.X0 < half ? -1 : ledge.X1 - CenterX < half ? 1 : 0;
            if (end != 0 && _random.NextDouble() < 0.3 * PetTraits.Factor(Profile().Traits.Bold))
                _hop = (end * 260, -380);
            return;
        }
        if (NearWall() is not { } side)
        {
            if (_climbAfterWalk) _walkAgain = true;   // 还没走到：下一帧接着走
            return;
        }
        if (_climbAfterWalk)
        {
            _climbAfterWalk = false;
            _director.RequestStance(Stances.Wall, ActionPriority.User, "console");
            return;
        }
        // 走到屏幕边：要不要爬上去由规则 climb-up 决定（概率、冷却、开关）。好奇、活泼的宠物更常发。
        var traits = Profile().Traits;
        if (_random.NextDouble() < PetTraits.Factor(traits.Curious) * PetTraits.Factor(traits.Energetic))
            _deferredEvent = PetEvent.Of(PetEvents.WallReached, ("side", side < 0 ? "left" : "right"));
    }

    private void PlanTransition(ActionDef action, StanceTransition transition)
    {
        var screen = Surfaces.ScreenAt(CenterX, Feet - 1);
        if (transition is { From: Stances.Ground, To: Stances.Wall })
        {
            var side = NearWall() ?? (CenterX - screen.X < screen.Right - CenterX ? -1 : 1);
            _wallSide = side;
            _director.Direction = side;
            var floor = FloorAt(CenterX, Feet - 1);
            _motion = new LerpMotion(X, Y, WallX(side, screen, action), floor - Body.WallFeetY * Scale);
        }
        else if (transition is { From: Stances.Wall, To: Stances.Ceiling })
        {
            // 从墙顶翻上天花板：贴着这边的墙角，之后朝屋里爬。
            var top = Surfaces.ScreenAt(CenterX, Y + Body.Y0 * Scale + 1);
            var dir = -_wallSide;
            _director.Direction = dir;
            var x = _wallSide < 0 ? top.X - BodyLeft(dir) : top.Right - BodyRight(dir);
            _motion = new LerpMotion(X, Y, x, top.Y - Body.CeilingContactY * Scale);
        }
        else if (transition is { From: Stances.Ceiling, To: Stances.Wall })
        {
            var top = Surfaces.ScreenAt(CenterX, Y + Body.CeilingContactY * Scale + 1);
            var side = CenterX - top.X < top.Right - CenterX ? -1 : 1;
            _wallSide = side;
            _director.Direction = side;
            _motion = new LerpMotion(X, Y, WallX(side, top, action), top.Y - Body.Y0 * Scale);
        }
        else if (transition is { From: Stances.Wall, To: Stances.Ground })
        {
            var dir = _director.Direction;
            var floor = FloorAt(CenterX, Feet - 1);
            var x = _wallSide < 0 ? screen.X - BodyLeft(dir) : screen.Right - BodyRight(dir);
            _motion = new LerpMotion(X, Y, x, floor - Body.FeetY * Scale);
        }
    }

    private void PlanOnWall(ActionDef action)
    {
        var screen = Surfaces.ScreenAt(CenterX, Feet - 1);
        X = WallX(_wallSide, screen, action);
        _director.Direction = _wallSide;
        var floor = FloorAt(CenterX, Feet - 1);
        var below = floor - Feet;
        var above = Y + Body.Y0 * Scale - screen.Y;

        if (!_leaving && (_time.GetUtcNow() - _wallSince > MaxWallTime || Energy() is < 20))
            _leaving = true;
        if (_leaving)
        {
            if (below <= Body.Height * Scale && CanEnter(Stances.Ground) || _director.Library.Stances.Path(Stances.Wall, Stances.Air) is not null)
            {
                _leaveRequested = true;   // 下一帧再请求，别在 Director 的回调里重入
                return;
            }
            // 没有「松手」动作又离地面远：一路滑下去再下墙。
            if (action.HasRole(Roles.Idle) && below > 1)
                _motion = new LerpMotion(X, Y, X, Y + below);
            return;
        }

        if (action.HasRole(Roles.Climb) && action.Gait is { } gait)
        {
            var room = Math.Max(0, above - Body.Height * Scale * 0.2);
            var stride = gait.Stride * Scale * Profile().Physics.ClimbSpeed;
            if (room > 4)
                _motion = new AxisMotion(new Walk((int)Y, -1, Math.Min(stride, room), gait), true, false);
            else if (CanEnter(Stances.Ceiling) && _director.Library.Stances.Available.Contains(Stances.Ceiling)
                     && _random.NextDouble() < 0.5 * PetTraits.Factor(Profile().Traits.Bold))
                _ceilingRequested = true;   // 爬到顶了：翻上天花板（下一帧再请求）
            return;
        }
        if (action.HasRole(Roles.Idle) && below > Body.Height * Scale * 0.3 && _random.NextDouble() < 0.35)
            _motion = new LerpMotion(X, Y, X, Y + Math.Min(below, Body.Height * Scale * 0.4));   // 慢慢往下滑一点
    }

    /// <summary>倒挂在天花板上：贴着屏幕顶，爬的动作沿 X 走；待久了松手掉下去或回到墙上。</summary>
    private void PlanOnCeiling(ActionDef action)
    {
        var screen = Surfaces.ScreenAt(CenterX, Y + Body.CeilingContactY * Scale + 1);
        Y = screen.Y - Body.CeilingContactY * Scale;
        Moved?.Invoke();
        if (!_leaving && (_time.GetUtcNow() - _wallSince > MaxWallTime || Energy() is < 20))
            _leaving = true;
        if (_leaving)
        {
            _leaveRequested = true;
            return;
        }
        if (!action.HasRole(Roles.Climb) && !action.HasRole(Roles.Move) || action.Gait is not { Axis: GaitAxis.X } gait) return;
        var left = Math.Min(BodyLeft(-1), BodyLeft(1));
        var right = Math.Max(BodyRight(-1), BodyRight(1));
        var stride = gait.Stride * Scale * Profile().Physics.ClimbSpeed;
        var walk = WalkPlanner.Plan((int)X, (int)Math.Ceiling(screen.X - left), (int)Math.Floor(screen.Right - right), stride, gait, _random);
        _director.Direction = walk.Direction;
        _motion = new AxisMotion(walk, false, false);
    }

    private void Leave()
    {
        if (_director.Stance == Stances.Ceiling)
        {
            if (_director.RequestStance(Stances.Air, ActionPriority.System, "leave-ceiling") != PlayResult.Played)
                _director.RequestStance(Stances.Wall, ActionPriority.System, "leave-ceiling");
            return;
        }
        if (_director.Stance != Stances.Wall) return;
        if (CanEnter(Stances.Ground) && _director.RequestStance(Stances.Ground, ActionPriority.System, "leave-wall") == PlayResult.Played)
            return;
        _director.RequestStance(Stances.Air, ActionPriority.System, "leave-wall");
    }

    private bool TryGrabWall(int side, DesktopRect screen)
    {
        if (!GroundMode || !_settings.AllowClimb || !_director.Library.Stances.Available.Contains(Stances.Wall)) return false;
        if (_random.NextDouble() >= 0.35 * PetTraits.Factor(Profile().Traits.Bold)) return false;
        _wallSide = side;
        _director.Direction = side;
        if (!_director.GrabWall()) return false;
        X = WallX(side, screen);
        var floor = FloorAt(CenterX, Feet - 1);
        Y = Math.Min(Y, floor - Body.WallFeetY * Scale);
        _vx = _vy = 0;
        _thrown = false;
        Moved?.Invoke();
        _publish(PetEvent.Of(PetEvents.GrabbedWall, ("side", side < 0 ? "left" : "right")));
        return true;
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PetSettings.Placement) || _director.Current is null) return;
        if (GroundMode)
        {
            // 切到站在地面：在半空就掉下去。
            if (!Held && _director.Stance == Stances.Ground && FloorAt(CenterX, Feet - 1) - Feet > SnapDip * Dip)
            {
                _vx = _vy = 0;
                _velocitySet = true;
                _director.BeginFall();
                Moved?.Invoke();
            }
            else Settle();
        }
        else if (_director.Stance is not Stances.Ground)
            _director.Land();
    }

    public void Detach()
    {
        _director.ActionStarted -= OnActionStarted;
        _director.StanceChanged -= OnStanceChanged;
        _director.CanEnter = null;
        _settings.PropertyChanged -= OnSettingChanged;
    }
}
