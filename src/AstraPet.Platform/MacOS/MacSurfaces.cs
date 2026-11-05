using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AstraPet.Platform.MacOS;

/// <summary>
/// 其他应用窗口的位置（design/pet-life/04 §7）：CGWindowListCopyWindowInfo，只读 kCGWindowBounds / kCGWindowLayer /
/// kCGWindowOwnerPID / kCGWindowNumber / kCGWindowAlpha，不读窗口标题（标题要屏幕录制权限，这些不用）。
/// 坐标是全局显示坐标：主屏左上角为原点、单位 point，和 Avalonia 在 macOS 上的桌面坐标一致。结果从前到后。
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed partial class MacWindowSurfaces : IWindowSurfaces
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const uint OnScreenOnly = 1 << 0;
    private const uint ExcludeDesktopElements = 1 << 4;
    private const int CFNumberSInt64Type = 4;
    private const int CFNumberDoubleType = 13;

    private static readonly nint KeyLayer, KeyBounds, KeyPid, KeyNumber, KeyAlpha;
    private static readonly bool Ready;

    static MacWindowSurfaces()
    {
        try
        {
            var cg = NativeLibrary.Load(CoreGraphics);
            nint Key(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(cg, name));
            KeyLayer = Key("kCGWindowLayer");
            KeyBounds = Key("kCGWindowBounds");
            KeyPid = Key("kCGWindowOwnerPID");
            KeyNumber = Key("kCGWindowNumber");
            KeyAlpha = Key("kCGWindowAlpha");
            Ready = KeyLayer != 0 && KeyBounds != 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            Ready = false;
        }
    }

    public static bool Available => Ready;

    public IReadOnlyList<DesktopWindow> VisibleWindows()
    {
        if (!Ready) return [];
        var list = CGWindowListCopyWindowInfo(OnScreenOnly | ExcludeDesktopElements, 0);
        if (list == 0) return [];
        var result = new List<DesktopWindow>();
        try
        {
            var me = Environment.ProcessId;
            var count = CFArrayGetCount(list);
            for (nint i = 0; i < count; i++)
            {
                var dict = CFArrayGetValueAtIndex(list, i);
                if (Long(dict, KeyLayer) != 0) continue;                    // 只要普通窗口层（菜单栏、Dock、浮窗不要）
                if (Long(dict, KeyPid) == me) continue;
                if (Double(dict, KeyAlpha) is { } alpha && alpha <= 0.01) continue;
                var bounds = CFDictionaryGetValue(dict, KeyBounds);
                if (bounds == 0 || !CGRectMakeWithDictionaryRepresentation(bounds, out var rect)) continue;
                result.Add(new DesktopWindow(Long(dict, KeyNumber) ?? i, rect.X, rect.Y, rect.Width, rect.Height));
            }
        }
        finally
        {
            CFRelease(list);
        }
        return result;
    }

    private static long? Long(nint dict, nint key)
    {
        var n = CFDictionaryGetValue(dict, key);
        return n != 0 && CFNumberGetValue(n, CFNumberSInt64Type, out long v) ? v : null;
    }

    private static double? Double(nint dict, nint key)
    {
        var n = CFDictionaryGetValue(dict, key);
        return n != 0 && CFNumberGetValue(n, CFNumberDoubleType, out double v) ? v : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public double X, Y, Width, Height;
    }

    [LibraryImport(CoreGraphics)]
    private static partial nint CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);

    [LibraryImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool CGRectMakeWithDictionaryRepresentation(nint dict, out CGRect rect);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFArrayGetCount(nint array);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFArrayGetValueAtIndex(nint array, nint index);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFDictionaryGetValue(nint dict, nint key);

    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool CFNumberGetValue(nint number, int type, out long value);

    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool CFNumberGetValue(nint number, int type, out double value);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint obj);
}

/// <summary>
/// ⌃⌥P 全局快捷键：Carbon 的 RegisterEventHotKey。不需要辅助功能或输入监控权限（不监听键盘，只让系统在这个组合键按下时通知）。
/// 回调在主线程的 run loop 上来（Avalonia 在 macOS 上跑的就是 NSApplication 的主循环）。
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe partial class MacGlobalHotkey : IGlobalHotkey
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const uint KeyboardClass = 0x6B657962;   // 'keyb'
    private const uint HotKeyPressed = 5;
    private const uint KeyP = 0x23;                   // kVK_ANSI_P
    private const uint ControlKey = 1 << 12, OptionKey = 1 << 11;
    private const uint Signature = 0x41535450;        // 'ASTP'

    private static Action? _pressed;
    private nint _handler, _hotkey;

    public string Display => "⌃⌥P";

    public bool Start(Action pressed)
    {
        _pressed = pressed;
        if (_hotkey != 0) return true;
        try
        {
            var target = GetApplicationEventTarget();
            var spec = stackalloc uint[2] { KeyboardClass, HotKeyPressed };
            nint handler;
            if (InstallEventHandler(target, (nint)(delegate* unmanaged<nint, nint, nint, int>)&OnHotKey, 1, spec, 0, &handler) != 0)
                return false;
            _handler = handler;
            nint hotkey;
            if (RegisterEventHotKey(KeyP, ControlKey | OptionKey, new HotKeyId { Signature = Signature, Id = 1 }, target, 0, &hotkey) != 0)
            {
                RemoveEventHandler(_handler);
                _handler = 0;
                return false;
            }
            _hotkey = hotkey;
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    public void Stop()
    {
        if (_hotkey != 0) UnregisterEventHotKey(_hotkey);
        if (_handler != 0) RemoveEventHandler(_handler);
        _hotkey = _handler = 0;
    }

    public void Dispose() => Stop();

    [UnmanagedCallersOnly]
    private static int OnHotKey(nint callRef, nint eventRef, nint userData)
    {
        try { _pressed?.Invoke(); }
        catch { /* 回调里不能把异常抛回 Carbon */ }
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HotKeyId
    {
        public uint Signature;
        public uint Id;
    }

    [LibraryImport(Carbon)]
    private static partial nint GetApplicationEventTarget();

    [LibraryImport(Carbon)]
    private static partial int InstallEventHandler(nint target, nint handler, nuint count, uint* types, nint userData, nint* outRef);

    [LibraryImport(Carbon)]
    private static partial int RemoveEventHandler(nint handler);

    [LibraryImport(Carbon)]
    private static partial int RegisterEventHotKey(uint keyCode, uint modifiers, HotKeyId id, nint target, uint options, nint* outRef);

    [LibraryImport(Carbon)]
    private static partial int UnregisterEventHotKey(nint hotkey);
}
