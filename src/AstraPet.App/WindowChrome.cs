using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace AstraPet;

/// <summary>
/// 无边框窗口的外框行为：拖动区按住移动、双击最大化，四边四角的透明拖拽条改大小，最大化时外框去掉圆角描边、拖拽条隐藏。
/// macOS 上 BeginResizeDrag 不可用，自己按指针位移算新位置和大小。和控制台窗口的做法一致。
/// </summary>
public sealed class WindowChrome
{
    private readonly Window _window;
    private WindowEdge _edge;
    private PixelPoint _start;
    private PixelPoint _origin;
    private Size _size;
    private bool _resizing;

    public WindowChrome(Window window, Border frame, Panel grips)
    {
        _window = window;
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property != Window.WindowStateProperty) return;
            var maximized = window.WindowState is WindowState.Maximized or WindowState.FullScreen;
            frame.Classes.Set("maximized", maximized);
            grips.IsVisible = !maximized;
        };
        foreach (var grip in grips.Children)
        {
            grip.PointerPressed += OnResizePressed;
            grip.PointerMoved += OnResizeMoved;
            grip.PointerReleased += OnResizeReleased;
        }
    }

    public static bool IsMac => OperatingSystem.IsMacOS();

    public void DragAreaPressed(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_window).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }
        _window.BeginMoveDrag(e);
    }

    public void Minimize() => _window.WindowState = WindowState.Minimized;

    public void ToggleMaximize() =>
        _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control grip || !e.GetCurrentPoint(_window).Properties.IsLeftButtonPressed) return;
        if (!Enum.TryParse<WindowEdge>(grip.Tag as string, out var edge)) return;
        e.Handled = true;
        if (!IsMac)
        {
            _window.BeginResizeDrag(edge, e);
            return;
        }
        _resizing = true;
        _edge = edge;
        _start = _window.PointToScreen(e.GetPosition(_window));
        _origin = _window.Position;
        _size = _window.ClientSize;
        e.Pointer.Capture(grip);
    }

    private void OnResizeMoved(object? sender, PointerEventArgs e)
    {
        if (!_resizing) return;
        var now = _window.PointToScreen(e.GetPosition(_window));
        var scale = _window.DesktopScaling;
        var dx = (now.X - _start.X) / scale;
        var dy = (now.Y - _start.Y) / scale;
        double w = _size.Width, h = _size.Height;
        int x = _origin.X, y = _origin.Y;

        if (_edge is WindowEdge.East or WindowEdge.NorthEast or WindowEdge.SouthEast)
            w = Math.Max(_window.MinWidth, _size.Width + dx);
        if (_edge is WindowEdge.West or WindowEdge.NorthWest or WindowEdge.SouthWest)
        {
            w = Math.Max(_window.MinWidth, _size.Width - dx);
            x = _origin.X + (int)Math.Round((_size.Width - w) * scale);
        }
        if (_edge is WindowEdge.South or WindowEdge.SouthEast or WindowEdge.SouthWest)
            h = Math.Max(_window.MinHeight, _size.Height + dy);
        if (_edge is WindowEdge.North or WindowEdge.NorthEast or WindowEdge.NorthWest)
        {
            h = Math.Max(_window.MinHeight, _size.Height - dy);
            y = _origin.Y + (int)Math.Round((_size.Height - h) * scale);
        }

        _window.Width = w;
        _window.Height = h;
        if (x != _window.Position.X || y != _window.Position.Y) _window.Position = new PixelPoint(x, y);
    }

    private void OnResizeReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_resizing) return;
        _resizing = false;
        e.Pointer.Capture(null);
    }
}
