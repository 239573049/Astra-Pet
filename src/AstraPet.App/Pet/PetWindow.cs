using System.ComponentModel;
using System.Runtime.InteropServices;
using AstraPet.Adapters;
using AstraPet.Core;
using AstraPet.Core.Behaviors;
using AstraPet.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace AstraPet.Pet;

/// <summary>
/// 透明、无边框、置顶的宠物窗口：把 <see cref="PetDirector"/> 给的帧画出来，
/// 处理走路时的窗口位置、点击/拖拽、点击穿透。挑哪段动画由 director 决定，可调参数来自 <see cref="PetSettings"/>。
/// </summary>
public class PetWindow : Window
{
    private const double DragThreshold = 4;    // 按下后移动超过这么多 DIP 才算拖拽，否则松手算点击
    private const byte HitAlpha = 32;          // alpha 低于它的像素算透明，鼠标穿透
    private const double PetDistance = 600;    // 在角色身上来回划动累计这么多 DIP 算一次摸头

    private readonly PetDirector _director;
    private readonly PetSettings _settings;
    private readonly IPlatform _platform;
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly WriteableBitmap _bitmap;
    private readonly ScaleTransform _mirror = new(-1, 1);
    private readonly DispatcherTimer _frameTimer = new();
    private readonly FrameStats? _frameStats = FrameStats.FromEnvironment();
    private readonly DispatcherTimer _hitTimer = new() { Interval = TimeSpan.FromMilliseconds(15) };
    private readonly DispatcherTimer _physicsTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _windowsTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly System.Diagnostics.Stopwatch _physicsClock = new();
    private readonly PetBody _body;
    private readonly List<(long Ticks, PixelPoint At)> _dragSamples = new();   // 最近的拖拽位置，松手时算速度

    private byte[]? _shown;                    // 屏幕上这一帧的像素，命中测试也读它
    private Point? _hoverAt;                   // 状态浮层：悬停不动的位置和时间
    private DateTime _hoverSince;
    private bool _hoverShown;

    private bool _pressed;
    private bool _dragging;
    private PixelPoint _pressAt;               // 按下时指针的屏幕坐标（桌面像素）
    private PixelPoint _grab;                  // 指针相对窗口左上角的偏移（桌面像素）
    private Point _pressLocal;                 // 按下时指针在窗口里的位置（DIP），算点在哪个部位
    private Point? _lastHover;                 // 摸头：上一次悬停位置和累计划过的距离
    private double _hoverDistance;
    private DateTime _lastHoverAt;

    private INativeWindow _native = NullNativeWindow.Instance;

    /// <summary>单击角色（没有拖动）。</summary>
    public event Action<string>? Clicked;

    /// <summary>双击角色。</summary>
    public event Action? DoubleClicked;

    /// <summary>鼠标在角色身上来回划了一阵。</summary>
    public event Action? Petted;

    /// <summary>开始拖拽 / 松手。</summary>
    public event Action? DragStarted;
    public event Action? DragEnded;

    /// <summary>右键菜单点了某项，参数是 <see cref="MenuEntry"/> 的 Id。</summary>
    public event Action<string>? MenuCommand;

    /// <summary>菜单项的显示文字，可以带状态（比如「继续动画」）。不设就用默认名字。</summary>
    public Func<string, string>? MenuLabel { get; set; }

    /// <summary>开关类菜单项是否打开（打开的画勾）；不是开关的返回 null。</summary>
    public Func<string, bool?>? MenuChecked { get; set; }

    /// <summary>位置与物理（design/pet-life/04）。</summary>
    public PetBody Body => _body;

    /// <summary>PetBody 发出的事件（落地、甩出去、走到墙边……），由 PetCompanion 转到事件总线上。</summary>
    public event Action<PetEvent>? BodyEvent;

    /// <summary>鼠标在角色身上停了一秒（显示状态浮层）/ 离开了。</summary>
    public event Action? HoverStill;
    public event Action? HoverEnded;

    /// <summary>喂食子菜单的食物：id、显示文字、能不能点。</summary>
    public Func<IReadOnlyList<(string Id, string Label, bool Enabled)>>? FoodOptions { get; set; }

    /// <summary>菜单项现在要不要显示（比如没有睡觉动作时不显示「哄睡」）；null 都显示。</summary>
    public Func<string, bool>? MenuAvailable { get; set; }

    /// <summary>画完一帧。控制台概览页的实时预览用它刷新。</summary>
    public event Action? FrameShown;

    /// <summary>当前帧的位图（640x360，可能是镜像前的原图）。给控制台预览共用，不要改它。</summary>
    public Bitmap Frame => _bitmap;

    /// <summary>当前是否镜像显示。</summary>
    public bool Mirrored => _director.Mirrored;

    public PetWindow(PetDirector director, PetSettings settings, IPlatform platform)
    {
        _director = director;
        _settings = settings;
        _platform = platform;

        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Topmost = settings.Topmost;
        Opacity = settings.Opacity;
        ShowInTaskbar = false;
        CanResize = false;
        ShowActivated = false;
        ApplySize();

        _bitmap = new WriteableBitmap(
            new PixelSize(PetClip.Width, PetClip.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        _image.Source = _bitmap;
        _image.RenderTransformOrigin = RelativePoint.Center;
        Content = _image;

        // 宠物窗口是裸 Window 没有 overlay 层，窗口也太小装不下菜单，所以用 Avalonia 自带的 ContextMenu（走独立弹窗）。
        // 菜单项每次打开时按设置重建，控制台「菜单」页改了立即生效。
        var menu = new ContextMenu();
        menu.Opening += (_, _) => BuildMenu(menu);
        BuildMenu(menu);
        ContextMenu = menu;

        _body = new PetBody(director, settings, e => BodyEvent?.Invoke(e));
        _body.Moved += ApplyBodyPosition;
        ConfigureBody();

        _director.ClipStarted += OnClipStarted;
        // 进入空中（松手、松开墙、脚下的窗口没了）：不管是谁触发的，都要开物理定时器。
        _director.StanceChanged += stance => { if (stance == Stances.Air) StartPhysics(); };
        _director.Start();
        _frameTimer.Tick += OnFrame;
        _hitTimer.Tick += (_, _) => UpdatePassthrough();
        _physicsTimer.Tick += OnPhysics;
        _hoverTimer.Tick += (_, _) => CheckHover();
        _windowsTimer.Tick += (_, _) => RefreshWindowLedges();
        PointerExited += (_, _) => EndHover();
        _settings.PropertyChanged += OnSettingChanged;

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => EndPress(clicked: false);
        Opened += (_, _) =>
        {
            _native = this.AttachNative(_platform);
            _native.MakeTransparent();
            ApplyWindowBehavior();
            ConfigureBody();
            Screens.Changed += (_, _) => ConfigureBody();
            PetLog.Write($"窗口位置 {Position.X},{Position.Y} 大小 {Width:0}x{Height:0} 缩放 {DesktopScaling}");
            UpdateTimers();
        };
        Closed += (_, _) =>
        {
            _settings.PropertyChanged -= OnSettingChanged;
            _frameTimer.Stop();
            _hitTimer.Stop();
            _physicsTimer.Stop();
            _hoverTimer.Stop();
            _windowsTimer.Stop();
            _body.Detach();
            _director.Dispose();
            _bitmap.Dispose();
        };
    }

    /// <summary>放回主屏中央（站在地面模式下站在地面上）。</summary>
    public void ResetPosition() => _body.Reset();

    /// <summary>
    /// 屏幕、缩放变了：把工作区和缩放交给 PetBody。Position 和工作区是桌面坐标，窗口尺寸是 DIP，用 DesktopScaling 换算。
    /// 不能用 Screen.Scaling：那是渲染缩放，macOS Retina 上是 2，而 macOS 的桌面坐标是 point。
    /// </summary>
    private void ConfigureBody()
    {
        var all = Screens.All;
        var areas = all.Select(s => new DesktopRect(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height)).ToList();
        var primary = Math.Max(0, all.ToList().FindIndex(s => s.IsPrimary));
        _body.Configure(new SurfaceMap(areas, primary), _settings.Scale * DesktopScaling, DesktopScaling);
    }

    /// <summary>
    /// 站在其他窗口上（design/pet-life/04 §7）：开着时每半秒读一次其他窗口的位置交给 PetBody；关掉时清空（站在窗口上的会掉下来）。
    /// </summary>
    private void UpdateWindowsTimer()
    {
        var on = _settings.PerchOnWindows && _settings.GroundMode && _platform.Capabilities.WindowSurfaces && _settings.Visible;
        if (on == _windowsTimer.IsEnabled) return;
        _windowsTimer.IsEnabled = on;
        if (on) RefreshWindowLedges();
        else if (_body.Surfaces.Ledges.Count > 0) _body.UpdateWindows([]);
    }

    private void RefreshWindowLedges()
    {
        if (_body.Held) return;
        try
        {
            _body.UpdateWindows(_platform.Windows.VisibleWindows());
        }
        catch (Exception e)
        {
            PetLog.Write($"读其他窗口的位置失败，先不站在窗口上: {e.Message}");
            _windowsTimer.Stop();
        }
    }

    private void ApplyBodyPosition()
    {
        var at = new PixelPoint((int)Math.Round(_body.X), (int)Math.Round(_body.Y));
        if (at != Position) Position = at;
        if (_body.Airborne) StartPhysics();
    }

    private void StartPhysics()
    {
        if (_physicsTimer.IsEnabled) return;
        _physicsClock.Restart();
        _physicsTimer.Start();
    }

    private void OnPhysics(object? sender, EventArgs e)
    {
        var elapsed = _physicsClock.Elapsed;
        _physicsClock.Restart();
        _body.PhysicsStep(elapsed);
        if (!_body.Airborne) _physicsTimer.Stop();
    }

    /// <summary>光标在画布上的位置（画布坐标，按屏幕上看到的样子），读不到返回 null。窗口外也读得到。</summary>
    public (double X, double Y)? CursorOnCanvas()
    {
        var size = ClientSize;
        if (size.Width <= 0 || _native.CursorInWindow() is not var (x, y)) return null;
        return (x / size.Width * PetClip.Width, y / size.Height * PetClip.Height);
    }

    /// <summary>角色头顶中间的桌面坐标：气泡和状态浮层对齐到这里。</summary>
    public (double CenterX, double Top) HeadAnchor => _body.BodyPosition;

    /// <summary>从外部（控制台）指定播哪段，按用户操作处理：播完回待机。</summary>
    public void Play(PetClip clip) => _director.Play(clip, fromUser: true);

    public void Play(string category) => _director.Play(category, fromUser: true);

    // 菜单项的图标（24x24 线稿）和分组：相邻两项分组不同时中间画一条分隔线。
    private static readonly Dictionary<string, (string Group, Geometry Icon)> MenuStyle = new()
    {
        [MenuEntry.Chat] = ("open", Geometry.Parse("M 4,5 L 20,5 L 20,16 L 10,16 L 6,20 L 6,16 L 4,16 Z")),
        [MenuEntry.Console] = ("open", Geometry.Parse("M 4,6 L 20,6 M 4,12 L 20,12 M 4,18 L 20,18 M 9,4 L 9,8 M 15,10 L 15,14 M 8,16 L 8,20")),
        [MenuEntry.Pause] = ("state", Geometry.Parse("M 9,5 L 9,19 M 15,5 L 15,19")),
        [MenuEntry.StayStill] = ("state", Geometry.Parse("M 12,3 L 12,13 M 7,13 L 17,13 M 12,13 L 12,21 M 8,21 L 16,21")),
        [MenuEntry.Pomodoro] = ("state", Geometry.Parse("M 12,6 A 7.5,7.5 0 1 0 12,21 A 7.5,7.5 0 1 0 12,6 Z M 12,10 L 12,14 L 14.5,15.5 M 10,3 L 14,3")),
        [MenuEntry.Mute] = ("state", Geometry.Parse("M 4,10 L 8,10 L 13,5 L 13,19 L 8,14 L 4,14 Z M 16.5,9 A 4,4 0 0 1 16.5,15 M 19,6.5 A 7.5,7.5 0 0 1 19,17.5")),
        [MenuEntry.Center] = ("place", Geometry.Parse("M 12,5 A 7,7 0 1 0 12,19 A 7,7 0 1 0 12,5 Z M 12,2 L 12,6 M 12,18 L 12,22 M 2,12 L 6,12 M 18,12 L 22,12")),
        [MenuEntry.Hide] = ("place", Geometry.Parse("M 3,12 Q 12,4 21,12 Q 12,20 3,12 Z M 4,4 L 20,20")),
        [MenuEntry.Exit] = ("exit", Geometry.Parse("M 14,4 L 19,4 L 19,20 L 14,20 M 10,8 L 6,12 L 10,16 M 6,12 L 15,12")),
    };

    private void BuildMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        string? group = null;
        foreach (var entry in _settings.MenuItems.Where(m => m.Visible && (MenuAvailable?.Invoke(m.Id) ?? true)))
        {
            var id = entry.Id;
            var (itemGroup, icon) = MenuStyle.TryGetValue(id, out var style) ? style : ("", null);
            if (group is not null && itemGroup != group) menu.Items.Add(new Separator());
            group = itemGroup;

            var item = new MenuItem { Header = MenuLabel?.Invoke(id) ?? entry.Label };
            if (icon is not null)
                item.Icon = new Avalonia.Controls.Shapes.Path { Data = icon, Classes = { "menu-icon" } };
            if (MenuChecked?.Invoke(id) is { } on) item.IsChecked = on;
            if (id == MenuEntry.Exit) item.Classes.Add("danger");
            if (id == MenuEntry.Feed)
            {
                // 喂食是子菜单：每个食物一项，冷却中 / 没有零食的置灰（design/pet-life/03 §2.1）。
                foreach (var (foodId, label, enabled) in FoodOptions?.Invoke() ?? [])
                {
                    var food = new MenuItem { Header = label, IsEnabled = enabled };
                    var command = MenuEntry.FeedPrefix + foodId;
                    food.Click += (_, _) => MenuCommand?.Invoke(command);
                    item.Items.Add(food);
                }
                if (item.Items.Count == 0) continue;
                menu.Items.Add(item);
                continue;
            }
            item.Click += (_, _) =>
            {
                if (id == MenuEntry.Exit) Close();
                else MenuCommand?.Invoke(id);
            };
            menu.Items.Add(item);
        }
    }

    /// <summary>录屏捕获、全屏空间：都是窗口打开后才能设的平台属性。</summary>
    private void ApplyWindowBehavior()
    {
        _native.SetOverFullscreen(!_settings.HideInFullscreen);
        _native.SetCapturable(_settings.AllowCapture);
    }

    private void ApplySize()
    {
        Width = PetClip.Width * _settings.Scale;
        Height = PetClip.Height * _settings.Scale;
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PetSettings.Scale):
                ApplySize();
                ConfigureBody();       // 尺寸变了：这一圈的距离作废，脚底和身体中心不动
                break;
            case nameof(PetSettings.Topmost):
                Topmost = _settings.Topmost;
                break;
            case nameof(PetSettings.Visible):
                if (_settings.Visible) Show(); else Hide();
                UpdateTimers();
                break;
            case nameof(PetSettings.Paused):
                UpdateTimers();
                break;
            case nameof(PetSettings.Opacity):
                Opacity = _settings.Opacity;
                break;
            case nameof(PetSettings.Speed):
            case nameof(PetSettings.PowerSavingActive):
                UpdateFrameInterval();
                break;
            case nameof(PetSettings.HideInFullscreen):
            case nameof(PetSettings.AllowCapture):
                ApplyWindowBehavior();
                break;
            case nameof(PetSettings.PerchOnWindows):
            case nameof(PetSettings.Placement):
                UpdateWindowsTimer();
                break;
            case nameof(PetSettings.ClickThrough):
                if (!_settings.ClickThrough) _native.IgnoresMouse = false;
                break;
        }
    }

    /// <summary>暂停或隐藏时停掉帧定时器：定格在当前帧，解码流被背压挡住，不占 CPU。</summary>
    private void UpdateTimers()
    {
        var running = _settings.Visible && !_settings.Paused;
        _frameTimer.IsEnabled = running;
        _hitTimer.IsEnabled = _platform.Capabilities.ClickThrough && _settings.Visible;
        UpdateWindowsTimer();
    }

    private void OnClipStarted(IFrameSource stream)
    {
        var action = _director.CurrentAction;
        UpdateFrameInterval();
        PetLog.Write($"播放 {action.Id}（{stream.Clip.Name}，{_director.CurrentSource}）x={Position.X} y={Position.Y} " +
                     $"朝{(_director.Direction < 0 ? "左" : "右")} 姿态 {_director.Stance}");
    }

    /// <summary>帧率 × 播放速度。</summary>
    private double FrameRate => _director.Current is { } current ? current.Clip.Fps * _settings.EffectiveSpeed : 24;

    /// <summary>
    /// 帧间隔 = 1 / (帧率 × 播放速度)。走路位置按帧号算，所以走路速度跟着一起变。
    /// 试过「定时器跑两倍频率、按时钟决定换帧」，macOS 上实测反而更差（p95 从 42ms 变成 60ms：
    /// 时钟早到一点就要等下一个半拍），DispatcherTimer 本身在一帧一跳时已经很准。
    /// </summary>
    private void UpdateFrameInterval()
    {
        if (_director.Current is null) return;
        _frameTimer.Interval = TimeSpan.FromSeconds(1.0 / FrameRate);
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        // 解码没跟上就停在这一帧，位置也不动。
        if (_director.Tick() is not { } frame) return;
        _frameStats?.Shown(FrameRate);
        Show(frame);
        // 走路、攀爬、过渡动作的位置都只由帧号决定（PetBody），解码没跟上时位置也不动。
        _body.Step(_director.Frame, _director.Current.Clip.EstimatedFrames);
    }

    private void Show(byte[] frame)
    {
        using (var buffer = _bitmap.Lock())
        {
            const int rowBytes = PetClip.Width * 4;
            if (buffer.RowBytes == rowBytes)
                Marshal.Copy(frame, 0, buffer.Address, PetClip.FrameBytes);
            else
                for (var y = 0; y < PetClip.Height; y++)
                    Marshal.Copy(frame, y * rowBytes, buffer.Address + y * buffer.RowBytes, rowBytes);
        }

        if (_shown is not null)
            FramePool.Return(_shown);
        _shown = frame;

        // 朝右时整窗镜像，带文字的片段除外（text_clips.json）。
        _image.RenderTransform = _director.Mirrored ? _mirror : null;
        _image.InvalidateVisual();
        FrameShown?.Invoke();
    }

    /// <summary>窗口内 p 处（DIP）是不是角色身上的像素。按当前帧的 alpha 判断，考虑镜像。</summary>
    private bool IsOpaqueAt(Point p)
    {
        var size = ClientSize;
        if (_shown is null || p.X < 0 || p.Y < 0 || p.X >= size.Width || p.Y >= size.Height)
            return false;
        var x = (int)(p.X / size.Width * PetClip.Width);
        var y = (int)(p.Y / size.Height * PetClip.Height);
        if (_director.Mirrored) x = PetClip.Width - 1 - x;
        return _shown[(y * PetClip.Width + x) * 4 + 3] >= HitAlpha;
    }

    /// <summary>
    /// 光标在透明像素上时让窗口忽略鼠标，点击落到下面的窗口；在角色身上时恢复接收。
    /// 和原版 Windows 上逐像素切 WS_EX_TRANSPARENT 是同一个思路。按住期间一律接收，否则拖拽会断。
    /// </summary>
    private void UpdatePassthrough()
    {
        if (!_settings.ClickThrough || _pressed)
        {
            _native.IgnoresMouse = false;
            return;
        }
        // 读不到光标位置时宁可接住鼠标，也不要让宠物点不到。
        _native.IgnoresMouse = _native.CursorInWindow() is var (x, y) && !IsOpaqueAt(new Point(x, y));
    }

    private PixelPoint ScreenPoint(PointerEventArgs e) => this.PointToScreen(e.GetPosition(this));

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
        {
            DoubleClicked?.Invoke();
            e.Handled = true;
            return;
        }
        _pressed = true;
        EndHover();
        _pressAt = ScreenPoint(e);
        _pressLocal = e.GetPosition(this);
        _grab = _pressAt - Position;
        e.Pointer.Capture(this);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_pressed)
        {
            TrackPetting(e.GetPosition(this));
            return;
        }
        if (!_settings.DragEnabled) return;
        var at = ScreenPoint(e);
        if (!_dragging)
        {
            var threshold = DragThreshold * DesktopScaling;
            if (Math.Abs(at.X - _pressAt.X) < threshold && Math.Abs(at.Y - _pressAt.Y) < threshold) return;
            _dragging = true;
            _dragSamples.Clear();
            _body.BeginHold();
            _physicsTimer.Stop();
            _director.BeginDrag();
            DragStarted?.Invoke();
        }
        Position = at - _grab;
        _body.HoldAt(Position.X, Position.Y);
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        _dragSamples.Add((now, Position));
        _dragSamples.RemoveAll(p => System.Diagnostics.Stopwatch.GetElapsedTime(p.Ticks, now) > TimeSpan.FromMilliseconds(150));
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pressed) return;
        // 先结束按住状态再放开捕获，随后的 PointerCaptureLost 就成了空操作。
        EndPress(clicked: !_dragging);
        e.Pointer.Capture(null);
    }

    /// <summary>松手：拖过就回待机；没拖动就是一次点击，随机播一段点击回应。</summary>
    private void EndPress(bool clicked)
    {
        if (!_pressed) return;
        var wasDragging = _dragging;
        _pressed = false;
        _dragging = false;
        if (wasDragging)
        {
            _director.EndDrag();
            var (vx, vy) = DragVelocity();
            _body.Release(vx, vy);
            DragEnded?.Invoke();
        }
        else if (clicked)
        {
            // 点击的反应（动作、台词）由触发规则决定，见 DefaultTriggers。
            var height = ClientSize.Height;
            Clicked?.Invoke(ClickZones.At(height > 0 ? _pressLocal.Y / height * PetClip.Height : PetClip.Height / 2.0,
                _body.Profile().Body));
        }
    }

    /// <summary>
    /// 摸头：鼠标没按下、在角色身上来回划，累计划过 <see cref="PetDistance"/> 触发一次。
    /// 停顿超过 1 秒或者移到透明处就重新计。只靠指针事件，所以光标不动不会误触发。
    /// </summary>
    /// <summary>松手前最近 100 毫秒的平均速度（桌面像素 / 秒）。拖着停住再松手是 0，不会被甩出去。</summary>
    private (double X, double Y) DragVelocity()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var recent = _dragSamples.Where(p => System.Diagnostics.Stopwatch.GetElapsedTime(p.Ticks, now) <= TimeSpan.FromMilliseconds(100)).ToList();
        _dragSamples.Clear();
        if (recent.Count < 2) return (0, 0);
        var seconds = System.Diagnostics.Stopwatch.GetElapsedTime(recent[0].Ticks, recent[^1].Ticks).TotalSeconds;
        if (seconds <= 0.005) return (0, 0);
        return ((recent[^1].At.X - recent[0].At.X) / seconds, (recent[^1].At.Y - recent[0].At.Y) / seconds);
    }

    /// <summary>悬停：鼠标在角色身上、没按键、一秒没怎么动 → 显示状态浮层（design/pet-life/03 §3）；开始划动（摸头）就收起。</summary>
    private void TrackHover(Point p)
    {
        if (!_settings.HoverStatus || !IsOpaqueAt(p))
        {
            EndHover();
            return;
        }
        if (_hoverAt is { } last && Math.Abs(p.X - last.X) + Math.Abs(p.Y - last.Y) < 6) return;
        if (_hoverShown) EndHover();
        _hoverAt = p;
        _hoverSince = DateTime.Now;
        _hoverTimer.Start();
    }

    private void CheckHover()
    {
        if (_hoverAt is null || _pressed || _hoverShown) { _hoverTimer.Stop(); return; }
        if (DateTime.Now - _hoverSince < TimeSpan.FromSeconds(1)) return;
        _hoverTimer.Stop();
        _hoverShown = true;
        HoverStill?.Invoke();
    }

    private void EndHover()
    {
        _hoverTimer.Stop();
        _hoverAt = null;
        if (!_hoverShown) return;
        _hoverShown = false;
        HoverEnded?.Invoke();
    }

    private void TrackPetting(Point p)
    {
        TrackHover(p);
        if (!_settings.Petting || !IsOpaqueAt(p))
        {
            _lastHover = null;
            _hoverDistance = 0;
            return;
        }
        var now = DateTime.Now;
        if (_lastHover is { } last && now - _lastHoverAt < TimeSpan.FromSeconds(1))
            _hoverDistance += Math.Abs(p.X - last.X) + Math.Abs(p.Y - last.Y);
        else
            _hoverDistance = 0;
        _lastHover = p;
        _lastHoverAt = now;
        if (_hoverDistance < PetDistance) return;
        _hoverDistance = 0;
        Petted?.Invoke();
    }
}

/// <summary>
/// 排查流畅度用：ASTRA_PET_FRAME_STATS=1 时，每 30 秒把实际帧间隔的分布写进日志
/// （中位数、p95、最大值，和偏离理想间隔一半以上的帧占多少）。
/// </summary>
internal sealed class FrameStats
{
    private readonly List<double> _intervals = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double _last = -1, _ideal;
    private double _reportAt = 30;

    public static FrameStats? FromEnvironment() =>
        Environment.GetEnvironmentVariable("ASTRA_PET_FRAME_STATS") == "1" ? new FrameStats() : null;

    public void Shown(double fps)
    {
        var now = _clock.Elapsed.TotalSeconds;
        if (_last >= 0 && now - _last < 0.5) _intervals.Add((now - _last) * 1000);   // 暂停后的第一帧不算
        _last = now;
        _ideal = 1000 / fps;
        if (now < _reportAt || _intervals.Count == 0) return;
        _reportAt = now + 30;
        _intervals.Sort();
        double At(double q) => _intervals[(int)Math.Min(_intervals.Count - 1, q * _intervals.Count)];
        var off = _intervals.Count(i => Math.Abs(i - _ideal) > _ideal / 2) * 100.0 / _intervals.Count;
        PetLog.Write($"帧间隔 {_intervals.Count} 帧：理想 {_ideal:0.0}ms 中位 {At(0.5):0.0} p95 {At(0.95):0.0} 最大 {_intervals[^1]:0.0}，偏离一半以上 {off:0.0}%");
        _intervals.Clear();
    }
}
