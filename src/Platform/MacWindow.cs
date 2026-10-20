using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace AstraPet.Platform;

/// <summary>
/// macOS 上绕过 Avalonia 直接操作 NSWindow 的几件事。其他平台 <see cref="TryAttach"/> 返回 null。
/// 只用 objc_msgSend 调公开的 NSWindow 方法；按 COM 虚表调 Avalonia 原生层的路子试过，会抛 NSException 直接崩，别再走。
/// </summary>
public sealed class MacWindow
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    private static readonly IntPtr SetOpaque = sel_registerName("setOpaque:");
    private static readonly IntPtr SetBackgroundColor = sel_registerName("setBackgroundColor:");
    private static readonly IntPtr ClearColor = sel_registerName("clearColor");
    private static readonly IntPtr SetIgnoresMouseEvents = sel_registerName("setIgnoresMouseEvents:");
    private static readonly IntPtr MouseLocationOutsideOfEventStream = sel_registerName("mouseLocationOutsideOfEventStream");
    private static readonly IntPtr SetCollectionBehavior = sel_registerName("setCollectionBehavior:");
    private static readonly IntPtr SetSharingType = sel_registerName("setSharingType:");
    private static readonly IntPtr ContentViewSel = sel_registerName("contentView");
    private static readonly IntPtr BoundsSel = sel_registerName("bounds");
    private static readonly IntPtr AllocSel = sel_registerName("alloc");
    private static readonly IntPtr InitSel = sel_registerName("init");
    private static readonly IntPtr SetFrameSel = sel_registerName("setFrame:");
    private static readonly IntPtr SetAutoresizingMaskSel = sel_registerName("setAutoresizingMask:");
    private static readonly IntPtr AddSubviewPositioned = sel_registerName("addSubview:positioned:relativeTo:");
    private static readonly IntPtr SetStyleSel = sel_registerName("setStyle:");
    private static readonly IntPtr SetMaterialSel = sel_registerName("setMaterial:");
    private static readonly IntPtr SetBlendingModeSel = sel_registerName("setBlendingMode:");
    private static readonly IntPtr SetStateSel = sel_registerName("setState:");
    private static readonly IntPtr SubviewsSel = sel_registerName("subviews");
    private static readonly IntPtr CountSel = sel_registerName("count");
    private static readonly IntPtr ObjectAtIndexSel = sel_registerName("objectAtIndex:");
    private static readonly IntPtr IsKindOfClassSel = sel_registerName("isKindOfClass:");
    private static readonly IntPtr SetAlphaValueSel = sel_registerName("setAlphaValue:");

    // NSWindowCollectionBehavior
    private const nuint CanJoinAllSpaces = 1 << 0;
    private const nuint Stationary = 1 << 4;
    private const nuint FullScreenAuxiliary = 1 << 8;

    private readonly IntPtr _nsWindow;
    private bool _ignoresMouse;
    private IntPtr _backdrop;
    private bool _backdropIsGlass;

    private MacWindow(IntPtr nsWindow) => _nsWindow = nsWindow;

    /// <summary>窗口打开后才有 NSWindow，在 Opened 里调。</summary>
    public static MacWindow? TryAttach(Window window)
    {
        if (!OperatingSystem.IsMacOS()) return null;
        return window.TryGetPlatformHandle() is IMacOSTopLevelPlatformHandle { NSWindow: var ns } && ns != IntPtr.Zero
            ? new MacWindow(ns)
            : null;
    }

    /// <summary>
    /// Avalonia 12.1.x 的 macOS 后端在处理 TransparencyLevelHint 时少写了一个 else，
    /// 请求 Transparent 会被静默丢弃，窗口保持实心背景（见 Avalonia.Native/TopLevelImpl.cs
    /// 的 SetTransparencyLevelHint）。这里直接在 NSWindow 上补上原生后端本来该做的事：
    /// 背景色设为透明并关掉不透明标记。
    /// </summary>
    public void MakeTransparent()
    {
        objc_msgSend_bool(_nsWindow, SetOpaque, false);
        objc_msgSend(_nsWindow, SetBackgroundColor, objc_msgSend(objc_getClass("NSColor"), ClearColor));
    }

    /// <summary>
    /// 给窗口垫一层系统的毛玻璃，让界面本身只需要画半透明的内容：
    /// macOS 26 及以上用 NSGlassEffectView（Liquid Glass），更早的系统退回 NSVisualEffectView 的 HUD 材质。
    /// 垫在 Avalonia 的内容视图下面，所以窗口背景必须是透明的（先调 <see cref="MakeTransparent"/>）。
    /// 重复调用无副作用；返回是否用上了 Liquid Glass。
    /// </summary>
    public bool InstallGlassBackdrop()
    {
        if (_backdrop != IntPtr.Zero) return _backdropIsGlass;

        var container = objc_msgSend(_nsWindow, ContentViewSel);
        if (container == IntPtr.Zero) return false;

        var glass = objc_getClass("NSGlassEffectView");
        _backdropIsGlass = glass != IntPtr.Zero;
        var view = objc_msgSend(objc_msgSend(_backdropIsGlass ? glass : objc_getClass("NSVisualEffectView"), AllocSel), InitSel);
        if (_backdropIsGlass)
        {
            objc_msgSend_nuint(view, SetStyleSel, 0);                // NSGlassEffectViewStyleRegular
        }
        else
        {
            objc_msgSend_nuint(view, SetBlendingModeSel, 0);         // BehindWindow
            objc_msgSend_nuint(view, SetMaterialSel, 13);            // HUDWindow
            objc_msgSend_nuint(view, SetStateSel, 1);                // Active：窗口失焦时也保持毛玻璃
        }

        objc_msgSend_nuint(view, SetAutoresizingMaskSel, 2 | 16);   // 宽高跟着窗口走
        objc_msgSend_rect(view, SetFrameSel, ReadBounds(container));
        // NSWindowBelow(-1)，relativeTo 为 nil：放到最底层，Avalonia 的视图盖在它上面。
        objc_msgSend_addSubview(container, AddSubviewPositioned, view, -1, IntPtr.Zero);

        _backdrop = view;
        HideNativeTitleBarMaterial(container);
        return _backdropIsGlass;
    }

    /// <summary>
    /// Avalonia 在内容视图里还垫着标题栏材质（NSVisualEffectView）和一条分隔线（NSBox），
    /// 窗口背景透明后它们会在顶部透出一条带横线的色带，和「无边框」冲突。
    /// 用透明度压到 0 而不是 hidden：Avalonia 之后切换标题栏显示时会改 hidden，改不了 alpha。
    /// </summary>
    private void HideNativeTitleBarMaterial(IntPtr container)
    {
        var subviews = objc_msgSend(container, SubviewsSel);
        var count = (nint)objc_msgSend(subviews, CountSel);
        var effect = objc_getClass("NSVisualEffectView");
        var box = objc_getClass("NSBox");
        for (nint i = 0; i < count; i++)
        {
            var v = objc_msgSend_index(subviews, ObjectAtIndexSel, i);
            if (v == _backdrop) continue;
            if (IsKindOf(v, effect) || IsKindOf(v, box))
                objc_msgSend_double(v, SetAlphaValueSel, 0);
        }
    }

    private static bool IsKindOf(IntPtr obj, IntPtr cls) => objc_msgSend_isKind(obj, IsKindOfClassSel, cls);

    private static NSRect ReadBounds(IntPtr view)
    {
        // 32 字节的结构体：arm64 走寄存器，x86_64 要用 _stret 通过内存返回。
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            return objc_msgSend_rect_ret(view, BoundsSel);
        objc_msgSend_stret(out var rect, view, BoundsSel);
        return rect;
    }

    /// <summary>
    /// 窗口出现在哪些空间：始终跟到每个桌面空间；<paramref name="overFullscreen"/> 为真时
    /// 还能出现在其他应用的全屏空间里，为假时全屏应用前台时看不到宠物（「全屏隐藏」）。
    /// </summary>
    public void SetSpaces(bool overFullscreen) =>
        objc_msgSend_nuint(_nsWindow, SetCollectionBehavior,
            CanJoinAllSpaces | Stationary | (overFullscreen ? FullScreenAuxiliary : 0));

    /// <summary>
    /// NSWindow.sharingType：ReadOnly(1) 允许被截屏和录屏（OBS 的窗口捕获、屏幕捕获都能看到），
    /// None(0) 时录屏里这个窗口是空的。
    /// </summary>
    public void SetCapturable(bool capturable) =>
        objc_msgSend_nuint(_nsWindow, SetSharingType, capturable ? 1u : 0u);

    /// <summary>
    /// NSWindow.alphaValue：整个窗口（包括垫在下面的原生玻璃）的不透明度。
    /// Avalonia 的 Visual.Opacity 只管它自己画的内容，管不到原生视图，预热时要用这个把窗口整个藏起来。
    /// </summary>
    public void SetAlpha(double alpha) => objc_msgSend_double(_nsWindow, SetAlphaValueSel, alpha);

    /// <summary>
    /// NSWindow.ignoresMouseEvents。为真时点击、滚轮、悬停全部落到下面的窗口。
    /// 不设的话，透明窗口的透明像素照样会接住点击，所以要按光标下的像素来回切。
    /// </summary>
    public bool IgnoresMouse
    {
        get => _ignoresMouse;
        set
        {
            if (value == _ignoresMouse) return;
            _ignoresMouse = value;
            objc_msgSend_bool(_nsWindow, SetIgnoresMouseEvents, value);
        }
    }

    /// <summary>
    /// 当前光标相对窗口内容区左上角的位置（DIP，macOS 上就是 point）。
    /// 直接问窗口服务器，不依赖鼠标事件，所以窗口忽略鼠标时也读得到。
    /// </summary>
    public Point MouseLocation(double contentHeight)
    {
        // NSWindow 坐标原点在左下角。无边框窗口的 frame 就是内容区。
        var p = objc_msgSend_point(_nsWindow, MouseLocationOutsideOfEventStream);
        return new Point(p.X, contentHeight - p.Y);
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

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern NSRect objc_msgSend_rect_ret(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")]
    private static extern void objc_msgSend_stret(out NSRect result, IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_rect(IntPtr receiver, IntPtr selector, NSRect arg);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_index(IntPtr receiver, IntPtr selector, nint index);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_double(IntPtr receiver, IntPtr selector, double arg);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool objc_msgSend_isKind(IntPtr receiver, IntPtr selector, IntPtr cls);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_addSubview(IntPtr receiver, IntPtr selector, IntPtr view, nint position, IntPtr relativeTo);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_bool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool arg);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_nuint(IntPtr receiver, IntPtr selector, nuint arg);

    // arm64 和 x86_64 上两个 double 的结构体都走寄存器返回，用普通的 objc_msgSend，不是 _stret。
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern NSPoint objc_msgSend_point(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjC)]
    private static extern IntPtr sel_registerName(string name);
}
