using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AstraPet.Platform.MacOS;

/// <summary>
/// 绕过 Avalonia 直接操作 NSWindow 的几件事。
/// 只用 objc_msgSend 调公开的 NSWindow 方法；按 COM 虚表调 Avalonia 原生层的路子试过，会抛 NSException 直接崩，别再走。
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed partial class MacNativeWindow(nint nsWindow) : INativeWindow
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    private static readonly nint SetOpaque = sel_registerName("setOpaque:");
    private static readonly nint SetBackgroundColor = sel_registerName("setBackgroundColor:");
    private static readonly nint ClearColor = sel_registerName("clearColor");
    private static readonly nint SetIgnoresMouseEvents = sel_registerName("setIgnoresMouseEvents:");
    private static readonly nint MouseLocationOutsideOfEventStream = sel_registerName("mouseLocationOutsideOfEventStream");
    private static readonly nint SetCollectionBehavior = sel_registerName("setCollectionBehavior:");
    private static readonly nint SetSharingType = sel_registerName("setSharingType:");
    private static readonly nint ContentViewSel = sel_registerName("contentView");
    private static readonly nint BoundsSel = sel_registerName("bounds");
    private static readonly nint SetAlphaValueSel = sel_registerName("setAlphaValue:");

    // NSWindowCollectionBehavior
    private const nuint CanJoinAllSpaces = 1 << 0;
    private const nuint Stationary = 1 << 4;
    private const nuint FullScreenAuxiliary = 1 << 8;

    private bool _ignoresMouse;

    /// <summary>
    /// Avalonia 12.1.x 的 macOS 后端在处理 TransparencyLevelHint 时少写了一个 else，
    /// 请求 Transparent 会被静默丢弃，窗口保持实心背景（见 Avalonia.Native/TopLevelImpl.cs
    /// 的 SetTransparencyLevelHint）。这里直接在 NSWindow 上补上原生后端本来该做的事：
    /// 背景色设为透明并关掉不透明标记。
    /// </summary>
    public void MakeTransparent()
    {
        objc_msgSend_bool(nsWindow, SetOpaque, false);
        objc_msgSend(nsWindow, SetBackgroundColor, objc_msgSend(objc_getClass("NSColor"), ClearColor));
    }

    /// <summary>
    /// 窗口出现在哪些空间：始终跟到每个桌面空间；<paramref name="over"/> 为真时
    /// 还能出现在其他应用的全屏空间里，为假时全屏应用前台时看不到宠物（「全屏隐藏」）。
    /// </summary>
    public void SetOverFullscreen(bool over) =>
        objc_msgSend_nuint(nsWindow, SetCollectionBehavior,
            CanJoinAllSpaces | Stationary | (over ? FullScreenAuxiliary : 0));

    /// <summary>
    /// NSWindow.sharingType：ReadOnly(1) 允许被截屏和录屏（OBS 的窗口捕获、屏幕捕获都能看到），
    /// None(0) 时录屏里这个窗口是空的。
    /// </summary>
    public void SetCapturable(bool capturable) =>
        objc_msgSend_nuint(nsWindow, SetSharingType, capturable ? 1u : 0u);

    /// <summary>
    /// NSWindow.alphaValue：整个窗口（包括原生视图）的不透明度。
    /// Avalonia 的 Visual.Opacity 只管它自己画的内容，管不到原生视图，预热时要用这个把窗口整个藏起来。
    /// </summary>
    public void SetAlpha(double alpha) => objc_msgSend_double(nsWindow, SetAlphaValueSel, alpha);

    /// <summary>
    /// NSWindow.ignoresMouseEvents。不设的话，透明窗口的透明像素照样会接住点击，所以要按光标下的像素来回切。
    /// </summary>
    public bool IgnoresMouse
    {
        get => _ignoresMouse;
        set
        {
            if (value == _ignoresMouse) return;
            _ignoresMouse = value;
            objc_msgSend_bool(nsWindow, SetIgnoresMouseEvents, value);
        }
    }

    /// <summary>
    /// 直接问窗口服务器，不依赖鼠标事件，所以窗口忽略鼠标时也读得到。macOS 上 DIP 就是 point。
    /// NSWindow 坐标原点在左下角，按内容视图的高度翻成左上角原点。无边框窗口的 frame 就是内容区。
    /// </summary>
    public (double X, double Y)? CursorInWindow()
    {
        var content = objc_msgSend(nsWindow, ContentViewSel);
        if (content == 0) return null;
        var height = ReadBounds(content).Height;
        var p = objc_msgSend_point(nsWindow, MouseLocationOutsideOfEventStream);
        return (p.X, height - p.Y);
    }

    private static NSRect ReadBounds(nint view)
    {
        // 32 字节的结构体：arm64 走寄存器，x86_64 要用 _stret 通过内存返回。
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            return objc_msgSend_rect_ret(view, BoundsSel);
        objc_msgSend_stret(out var rect, view, BoundsSel);
        return rect;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NSPoint
    {
        public readonly double X;
        public readonly double Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRect
    {
        public double X, Y, Width, Height;
    }

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint objc_msgSend(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint objc_msgSend(nint receiver, nint selector, nint arg);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial NSRect objc_msgSend_rect_ret(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend_stret")]
    private static partial void objc_msgSend_stret(out NSRect result, nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial void objc_msgSend_double(nint receiver, nint selector, double arg);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial void objc_msgSend_bool(nint receiver, nint selector, [MarshalAs(UnmanagedType.I1)] bool arg);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial void objc_msgSend_nuint(nint receiver, nint selector, nuint arg);

    // arm64 和 x86_64 上两个 double 的结构体都走寄存器返回，用普通的 objc_msgSend，不是 _stret。
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial NSPoint objc_msgSend_point(nint receiver, nint selector);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);
}
