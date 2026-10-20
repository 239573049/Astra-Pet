using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace AstraPet.Platform;

/// <summary>Windows 上的窗口补丁：控制录屏软件能不能捕获到窗口。其他平台什么都不做。</summary>
public static class WinWindow
{
    private const uint WdaNone = 0;
    private const uint WdaExcludeFromCapture = 0x11;   // Windows 10 2004 起支持

    public static void SetCapturable(Window window, bool capturable)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (window.TryGetPlatformHandle() is { Handle: var hwnd } && hwnd != IntPtr.Zero)
            SetWindowDisplayAffinity(hwnd, capturable ? WdaNone : WdaExcludeFromCapture);
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
}
