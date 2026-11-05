using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AstraPet.Platform.Windows;

/// <summary>
/// 其他应用窗口的位置（design/pet-life/04 §7）：EnumWindows 按 Z 序（最前在前）列顶层窗口，
/// 跳过不可见、最小化、被 DWM 隐藏（cloaked，比如别的虚拟桌面）、工具窗口和本进程的窗口，
/// 位置用 DWMWA_EXTENDED_FRAME_BOUNDS（不含阴影的真实边框，物理像素，和 Avalonia 在 Windows 上的桌面坐标一致）。
/// 不读标题和类名。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class WinWindowSurfaces : IWindowSurfaces
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;

    [ThreadStatic] private static List<nint>? _found;

    public IReadOnlyList<DesktopWindow> VisibleWindows()
    {
        _found = new List<nint>();
        try
        {
            EnumWindows((nint)(delegate* unmanaged<nint, nint, int>)&Collect, 0);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }
        var me = (uint)Environment.ProcessId;
        var result = new List<DesktopWindow>();
        foreach (var hwnd in _found)
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) continue;
            if ((GetWindowLongPtrW(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) continue;
            uint pid;
            GetWindowThreadProcessId(hwnd, &pid);
            if (pid == me) continue;
            int cloaked = 0;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
            Rect r;
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, &r, sizeof(Rect)) != 0) continue;
            if (r.Right <= r.Left || r.Bottom <= r.Top) continue;
            result.Add(new DesktopWindow(hwnd, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
        }
        _found = null;
        return result;
    }

    [UnmanagedCallersOnly]
    private static int Collect(nint hwnd, nint param)
    {
        _found?.Add(hwnd);
        return 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(nint callback, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint hwnd, int index);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, uint* pid);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint hwnd, int attribute, void* value, int size);
}

/// <summary>
/// Ctrl+Alt+P 全局快捷键：每 120 毫秒用 GetAsyncKeyState 看这三个键是不是同时按下（只看这三个键，不记录别的输入）。
/// 用 RegisterHotKey 要有自己的消息循环或者挂 Avalonia 窗口的 WndProc，轮询更省事，也不会和别的程序抢组合键。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WinGlobalHotkey : IGlobalHotkey
{
    private const int VK_CONTROL = 0x11, VK_MENU = 0x12, VK_P = 0x50;

    private Timer? _timer;
    private Action? _pressed;
    private bool _down;

    public string Display => "Ctrl+Alt+P";

    public bool Start(Action pressed)
    {
        _pressed = pressed;
        _timer ??= new Timer(_ => Poll(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(120));
        return true;
    }

    private void Poll()
    {
        bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
        var down = Down(VK_CONTROL) && Down(VK_MENU) && Down(VK_P);
        if (down && !_down) _pressed?.Invoke();
        _down = down;
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose() => Stop();

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);
}
