using AstraPet.Platform;
using Avalonia.Controls;
using Avalonia.Platform;

namespace AstraPet.Adapters;

/// <summary>
/// 从 Avalonia 窗口取原生句柄交给平台层。平台层不依赖 Avalonia，只认句柄：
/// macOS 要的是 NSWindow*（Handle 是 NSView*），其他平台用 Handle（HWND、X11 Window）。
/// 窗口打开后才有句柄，在 Opened 里调。
/// </summary>
public static class NativeWindowAttach
{
    public static INativeWindow AttachNative(this Window window, IPlatform platform) =>
        platform.AttachWindow(window.TryGetPlatformHandle() switch
        {
            IMacOSTopLevelPlatformHandle mac => mac.NSWindow,
            { } handle => handle.Handle,
            null => 0,
        });
}
