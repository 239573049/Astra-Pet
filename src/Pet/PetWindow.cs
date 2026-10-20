using System.ComponentModel;
using System.Runtime.InteropServices;
using AstraPet.Core;
using AstraPet.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using AtomContextMenu = AtomUI.Desktop.Controls.ContextMenu;
using AtomMenuItem = AtomUI.Desktop.Controls.MenuItem;

namespace AstraPet.Pet;

/// <summary>
/// 透明、无边框、置顶的宠物窗口：把 <see cref="PetDirector"/> 给的帧画出来，
/// 处理走路时的窗口位置、点击/拖拽、点击穿透。挑哪段动画由 director 决定，可调参数来自 <see cref="PetSettings"/>。
/// </summary>
public class PetWindow : Window
{
    private const double EdgeMargin = 40;      // 走到屏幕边缘前留出的余量（DIP）
    private const double DragThreshold = 4;    // 按下后移动超过这么多 DIP 才算拖拽，否则松手算点击
    private const byte HitAlpha = 32;          // alpha 低于它的像素算透明，鼠标穿透
    private const double PetDistance = 600;    // 在角色身上来回划动累计这么多 DIP 算一次摸头

    private readonly PetDirector _director;
    private readonly PetSettings _settings;
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly WriteableBitmap _bitmap;
    private readonly ScaleTransform _mirror = new(-1, 1);
    private readonly DispatcherTimer _frameTimer = new();
    private readonly DispatcherTimer _hitTimer = new() { Interval = TimeSpan.FromMilliseconds(15) };
    private readonly Random _random = new();

    private byte[]? _shown;                    // 屏幕上这一帧的像素，命中测试也读它
    private Walk? _walk;

    private bool _pressed;
    private bool _dragging;
    private PixelPoint _pressAt;               // 按下时指针的屏幕坐标（桌面像素）
    private PixelPoint _grab;                  // 指针相对窗口左上角的偏移（桌面像素）
    private Point? _lastHover;                 // 摸头：上一次悬停位置和累计划过的距离
    private double _hoverDistance;
    private DateTime _lastHoverAt;

    private MacWindow? _mac;

    /// <summary>一圈走路：起点、方向、这一圈走多少桌面像素。</summary>
    public sealed record Walk(int StartX, int Direction, double Distance, Gait Gait);

    /// <summary>单击角色（没有拖动）。</summary>
    public event Action? Clicked;

    /// <summary>双击角色。</summary>
    public event Action? DoubleClicked;

    /// <summary>鼠标在角色身上来回划了一阵。</summary>
    public event Action? Petted;

    /// <summary>右键菜单点了某项，参数是 <see cref="MenuEntry"/> 的 Id。</summary>
    public event Action<string>? MenuCommand;

    /// <summary>菜单项的显示文字，可以带状态（比如「继续动画」）。不设就用默认名字。</summary>
    public Func<string, string>? MenuLabel { get; set; }

    public PetWindow(PetDirector director, PetSettings settings)
    {
        _director = director;
        _settings = settings;

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

        // AtomUI 主题默认让菜单画在窗口内的 overlay 层上，宠物窗口是裸 Window 没有这一层，右键会抛
        // “no overlay layer is found”。窗口也太小装不下菜单，所以强制走原生弹窗。
        // 菜单项每次打开时按设置重建，控制台「菜单」页改了立即生效。
        var menu = new AtomContextMenu { ShouldUseOverlayPopup = false };
        menu.Opening += (_, _) => BuildMenu(menu);
        BuildMenu(menu);
        ContextMenu = menu;

        ResetPosition();

        _director.ClipStarted += OnClipStarted;
        _director.Start();
        _frameTimer.Tick += OnFrame;
        _hitTimer.Tick += (_, _) => UpdatePassthrough();
        _settings.PropertyChanged += OnSettingChanged;

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => EndPress(clicked: false);
        Opened += (_, _) =>
        {
            _mac = MacWindow.TryAttach(this);
            _mac?.MakeTransparent();
            ApplyWindowBehavior();
            PetLog.Write($"窗口位置 {Position.X},{Position.Y} 大小 {Width:0}x{Height:0} 缩放 {DesktopScaling}");
            UpdateTimers();
        };
        Closed += (_, _) =>
        {
            _settings.PropertyChanged -= OnSettingChanged;
            _frameTimer.Stop();
            _hitTimer.Stop();
            _director.Dispose();
            _bitmap.Dispose();
        };
    }

    /// <summary>当前这一圈走路，不在走时为 null。给控制台显示用。</summary>
    public Walk? CurrentWalk => _walk;

    /// <summary>放回当前屏幕工作区中央。</summary>
    public void ResetPosition()
    {
        // Position 和工作区是桌面坐标，窗口尺寸是 DIP，用 DesktopScaling 换算。
        // 不能用 Screen.Scaling：那是渲染缩放，macOS Retina 上是 2，而 macOS 的桌面坐标是 point。
        var area = Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1440, 900);
        var size = PixelSize.FromSize(new Size(Width, Height), DesktopScaling);
        _walk = null;
        Position = new PixelPoint(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2);
    }

    /// <summary>从外部（控制台）指定播哪段，按用户操作处理：播完回待机。</summary>
    public void Play(PetClip clip) => _director.Play(clip, fromUser: true);

    public void Play(string category) => _director.Play(category, fromUser: true);

    private void BuildMenu(AtomContextMenu menu)
    {
        menu.Items.Clear();
        foreach (var entry in _settings.MenuItems.Where(m => m.Visible))
        {
            var id = entry.Id;
            var item = new AtomMenuItem { Header = MenuLabel?.Invoke(id) ?? entry.Label };
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
        _mac?.SetSpaces(overFullscreen: !_settings.HideInFullscreen);
        _mac?.SetCapturable(_settings.AllowCapture);
        WinWindow.SetCapturable(this, _settings.AllowCapture);
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
                _walk = null;          // 尺寸变了，这一圈的距离作废，原地播完
                ApplySize();
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
            case nameof(PetSettings.ClickThrough):
                if (_mac is not null && !_settings.ClickThrough) _mac.IgnoresMouse = false;
                break;
        }
    }

    /// <summary>暂停或隐藏时停掉帧定时器：定格在当前帧，解码流被背压挡住，不占 CPU。</summary>
    private void UpdateTimers()
    {
        var running = _settings.Visible && !_settings.Paused;
        _frameTimer.IsEnabled = running;
        _hitTimer.IsEnabled = _mac is not null && _settings.Visible;
    }

    private void OnClipStarted(ClipStream stream)
    {
        _walk = stream.Clip.Category == PetLibrary.Move ? PlanWalk(stream.Clip) : null;
        UpdateFrameInterval();
        PetLog.Write($"播放 {stream.Clip.Category}/{stream.Clip.Name} x={Position.X} " +
                     $"朝{(_director.Direction < 0 ? "左" : "右")}" +
                     (_walk is null ? "" : $" 走 {_walk.Direction * _walk.Distance:0}px"));
    }

    /// <summary>帧间隔 = 1 / (帧率 × 播放速度)。走路位置按帧号算，所以走路速度跟着一起变。</summary>
    private void UpdateFrameInterval()
    {
        if (_director.Current is not { } current) return;
        _frameTimer.Interval = TimeSpan.FromSeconds(1.0 / (current.Clip.Fps * _settings.EffectiveSpeed));
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        // 解码没跟上就停在这一帧，位置也不动。
        if (_director.Tick() is not { } frame) return;
        Show(frame);
        if (_walk is not null)
            StepWalk(_walk);
    }

    /// <summary>
    /// 开始走一圈前定方向和距离，照原版 choose_move_direction：两边都够走一圈就随机挑，
    /// 只有一边够就往那边，都不够就往空间大的一边走、距离缩到剩余空间。朝向跟着走路方向。
    /// </summary>
    private Walk? PlanWalk(PetClip clip)
    {
        if (_director.Library.GaitOf(clip) is not { } gait) return null;

        var (minX, maxX) = WalkBounds();
        var stride = gait.Stride * _settings.Scale * DesktopScaling;
        double Room(int dir) => dir < 0 ? Position.X - minX : maxX - Position.X;

        var direction = (Room(-1) >= stride, Room(1) >= stride) switch
        {
            (true, true) => _random.Next(2) == 0 ? -1 : 1,
            (true, false) => -1,
            (false, true) => 1,
            _ => Room(-1) >= Room(1) ? -1 : 1,
        };
        _director.Direction = direction;
        return new Walk(Position.X, direction, Math.Clamp(Room(direction), 0, stride), gait);
    }

    /// <summary>位置只由帧号决定：起点 + 距离 × 曲线进度。不累加每帧位移，取整误差不会攒起来。</summary>
    private void StepWalk(Walk walk)
    {
        var progress = walk.Gait.ProgressAt(_director.Frame, _director.Current.Clip.EstimatedFrames);
        var x = walk.StartX + walk.Direction * (int)Math.Round(walk.Distance * progress);
        Position = new PixelPoint(x, Position.Y);
    }

    /// <summary>窗口左上角 X 的活动范围（桌面像素）。</summary>
    private (int Min, int Max) WalkBounds()
    {
        var area = (Screens.ScreenFromWindow(this) ?? Screens.Primary)?.WorkingArea
                   ?? new PixelRect(0, 0, 1440, 900);
        var margin = (int)(EdgeMargin * DesktopScaling);
        var width = (int)(Width * DesktopScaling);
        return (area.X + margin, area.Right - width - margin);
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
            ClipStream.Recycle(_shown);
        _shown = frame;

        // 朝右时整窗镜像，带文字的片段除外（text_clips.json）。
        _image.RenderTransform = _director.Mirrored ? _mirror : null;
        _image.InvalidateVisual();
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
        if (_mac is null) return;
        _mac.IgnoresMouse = _settings.ClickThrough && !_pressed
                            && !IsOpaqueAt(_mac.MouseLocation(ClientSize.Height));
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
        _pressAt = ScreenPoint(e);
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
            _director.Dragging = true;
            _director.Play(PetLibrary.Drag, fromUser: true);
        }
        Position = at - _grab;
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
        _director.Dragging = false;
        if (wasDragging)
            _director.Play(PetLibrary.Idle, fromUser: false);
        else if (clicked)
        {
            if (_settings.ClickReact) _director.Play(PetLibrary.Click, fromUser: true);
            Clicked?.Invoke();
        }
    }

    /// <summary>
    /// 摸头：鼠标没按下、在角色身上来回划，累计划过 <see cref="PetDistance"/> 触发一次。
    /// 停顿超过 1 秒或者移到透明处就重新计。只靠指针事件，所以光标不动不会误触发。
    /// </summary>
    private void TrackPetting(Point p)
    {
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
