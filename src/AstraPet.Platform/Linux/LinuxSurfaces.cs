using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AstraPet.Platform.Linux;

/// <summary>
/// 其他应用窗口的位置（design/pet-life/04 §7），X11：窗口管理器维护的 _NET_CLIENT_LIST_STACKING（从后到前，这里倒过来），
/// 每个窗口用 XTranslateCoordinates 换到根窗口坐标，再按 _NET_FRAME_EXTENTS 加上窗口管理器画的标题栏和边框。
/// 跳过 _NET_WM_STATE_HIDDEN（最小化）的和本进程（_NET_WM_PID）的。Wayland 原生窗口看不到，能力为 false。
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed unsafe partial class X11WindowSurfaces : IWindowSurfaces, IDisposable
{
    private const string X11 = "libX11.so.6";
    private const nint AnyPropertyType = 0;

    private readonly nint _display;
    private readonly nint _stacking, _state, _hidden, _pid, _frame;

    private X11WindowSurfaces(nint display)
    {
        _display = display;
        _stacking = XInternAtom(display, "_NET_CLIENT_LIST_STACKING", 0);
        _state = XInternAtom(display, "_NET_WM_STATE", 0);
        _hidden = XInternAtom(display, "_NET_WM_STATE_HIDDEN", 0);
        _pid = XInternAtom(display, "_NET_WM_PID", 0);
        _frame = XInternAtom(display, "_NET_FRAME_EXTENTS", 0);
    }

    /// <summary>X11 会话、连得上显示服务器时返回实例；否则 null。</summary>
    public static X11WindowSurfaces? TryCreate()
    {
        if (LinuxEnv.IsWayland || Environment.GetEnvironmentVariable("DISPLAY") is not { Length: > 0 }) return null;
        try
        {
            var display = XOpenDisplay(0);
            return display == 0 ? null : new X11WindowSurfaces(display);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    public IReadOnlyList<DesktopWindow> VisibleWindows()
    {
        var root = XDefaultRootWindow(_display);
        var windows = Property(root, _stacking);
        var result = new List<DesktopWindow>();
        var me = Environment.ProcessId;
        for (var i = windows.Length - 1; i >= 0; i--)   // 列表是从后到前
        {
            var w = (nint)windows[i];
            if (Property(w, _pid) is [var pid] && pid == me) continue;
            if (Property(w, _state).Contains((long)_hidden)) continue;
            int x, y;
            nint child, rootOut;
            uint width, height, border, depth;
            if (XGetGeometry(_display, w, &rootOut, &x, &y, &width, &height, &border, &depth) == 0) continue;
            if (XTranslateCoordinates(_display, w, root, 0, 0, &x, &y, &child) == 0) continue;
            var frame = Property(w, _frame);   // left, right, top, bottom
            var (l, r, t, b) = frame.Length == 4 ? (frame[0], frame[1], frame[2], frame[3]) : (0L, 0L, 0L, 0L);
            result.Add(new DesktopWindow(w, x - l, y - t, width + l + r, height + t + b));
        }
        XFlush(_display);
        return result;
    }

    /// <summary>读一个 32 位格式的窗口属性（X 里 32 位格式按 long 存）。</summary>
    private long[] Property(nint window, nint atom)
    {
        if (atom == 0) return [];
        nint type, data;
        int format;
        nuint count, after;
        if (XGetWindowProperty(_display, window, atom, 0, 4096, 0, AnyPropertyType, &type, &format, &count, &after, &data) != 0 || data == 0)
            return [];
        try
        {
            if (format != 32) return [];
            var result = new long[(int)count];
            for (var i = 0; i < result.Length; i++) result[i] = ((nint*)data)[i];
            return result;
        }
        finally
        {
            XFree(data);
        }
    }

    public void Dispose() => XCloseDisplay(_display);

    [LibraryImport(X11)] private static partial nint XOpenDisplay(nint name);
    [LibraryImport(X11)] private static partial int XCloseDisplay(nint display);
    [LibraryImport(X11)] private static partial nint XDefaultRootWindow(nint display);
    [LibraryImport(X11)] private static partial int XFlush(nint display);
    [LibraryImport(X11)] private static partial int XFree(nint data);

    [LibraryImport(X11, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint XInternAtom(nint display, string name, int onlyIfExists);

    [LibraryImport(X11)]
    private static partial int XGetWindowProperty(nint display, nint window, nint property, nint offset, nint length, int delete,
        nint reqType, nint* actualType, int* actualFormat, nuint* items, nuint* bytesAfter, nint* data);

    [LibraryImport(X11)]
    private static partial int XGetGeometry(nint display, nint drawable, nint* root, int* x, int* y, uint* width, uint* height,
        uint* border, uint* depth);

    [LibraryImport(X11)]
    private static partial int XTranslateCoordinates(nint display, nint src, nint dest, int srcX, int srcY, int* destX, int* destY, nint* child);
}

/// <summary>
/// Ctrl+Alt+P 全局快捷键（X11）：每 120 毫秒用 XQueryKeymap 看这三个键是不是同时按下，只看这三个键。
/// 用自己的显示连接（Xlib 的连接不能跨线程共用）。
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed unsafe partial class X11GlobalHotkey : IGlobalHotkey
{
    private const string X11 = "libX11.so.6";
    private const nint XK_Control_L = 0xffe3, XK_Control_R = 0xffe4, XK_Alt_L = 0xffe9, XK_Alt_R = 0xffea, XK_p = 0x70;

    private nint _display;
    private Timer? _timer;
    private Action? _pressed;
    private bool _down;
    private byte _ctrlL, _ctrlR, _altL, _altR, _p;
    private readonly object _gate = new();

    public string Display => "Ctrl+Alt+P";

    public static bool Supported => !LinuxEnv.IsWayland && Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 };

    public bool Start(Action pressed)
    {
        _pressed = pressed;
        lock (_gate)
        {
            if (_timer is not null) return true;
            try
            {
                _display = XOpenDisplay(0);
                if (_display == 0) return false;
                _ctrlL = XKeysymToKeycode(_display, XK_Control_L);
                _ctrlR = XKeysymToKeycode(_display, XK_Control_R);
                _altL = XKeysymToKeycode(_display, XK_Alt_L);
                _altR = XKeysymToKeycode(_display, XK_Alt_R);
                _p = XKeysymToKeycode(_display, XK_p);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
            _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(120));
            return true;
        }
    }

    private void Poll()
    {
        lock (_gate)
        {
            if (_display == 0) return;
            var keys = stackalloc byte[32];
            XQueryKeymap(_display, keys);
            bool Down(byte code) => code != 0 && (keys[code >> 3] & (1 << (code & 7))) != 0;
            var down = (Down(_ctrlL) || Down(_ctrlR)) && (Down(_altL) || Down(_altR)) && Down(_p);
            if (down && !_down) _pressed?.Invoke();
            _down = down;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            if (_display != 0) XCloseDisplay(_display);
            _display = 0;
        }
    }

    public void Dispose() => Stop();

    [LibraryImport(X11)] private static partial nint XOpenDisplay(nint name);
    [LibraryImport(X11)] private static partial int XCloseDisplay(nint display);
    [LibraryImport(X11)] private static partial byte XKeysymToKeycode(nint display, nint keysym);
    [LibraryImport(X11)] private static partial int XQueryKeymap(nint display, byte* keys);
}
