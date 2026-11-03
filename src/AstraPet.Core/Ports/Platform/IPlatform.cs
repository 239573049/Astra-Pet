namespace AstraPet.Platform;

/// <summary>
/// 与操作系统有关的一切都从这里拿。启动时由平台层的 PlatformFactory.Create 按当前系统选好实现，
/// 之后各层只依赖这个接口：界面按 <see cref="Capabilities"/> 决定显示什么，不再自己判断是哪个系统。
/// 每个服务在不支持的平台上都有「什么都不做」的实现，调用方不用判空。
/// </summary>
public interface IPlatform
{
    PlatformIdentity Identity { get; }
    PlatformCapabilities Capabilities { get; }
    IAppPaths Paths { get; }
    IShell Shell { get; }
    IAutoStart AutoStart { get; }
    ISpeech Speech { get; }
    ISystemMonitor Monitor { get; }
    IUpdateInstaller Installer { get; }

    /// <summary>
    /// 窗口打开后调用，拿到操作这个窗口的原生接口。<paramref name="handle"/> 在 macOS 上是 NSWindow*，
    /// Windows 上是 HWND，X11 上是 Window。拿不到句柄或平台不支持时返回空实现，不返回 null。
    /// </summary>
    INativeWindow AttachWindow(nint handle);
}

/// <summary>
/// 系统和 CPU 架构，例如 darwin-arm64、win32-x64、linux-x64。
/// 和 LocalGate 上传时的 x-astra-platform 一致，更新检测拿它挑属于本机的文件。
/// </summary>
public sealed record PlatformIdentity(string Os, string Arch)
{
    public string Key => $"{Os}-{Arch}";

    public override string ToString() => Key;
}

/// <summary>
/// 当前平台实际能做到的事。有些要到运行时才知道（比如 Windows 版本够不够），由各平台在创建时探测一次。
/// 界面上不支持的开关置灰，并显示 <see cref="Notes"/> 里的说明。
/// </summary>
public sealed record PlatformCapabilities
{
    /// <summary>透明像素处鼠标穿透到下面的窗口。</summary>
    public bool ClickThrough { get; init; }
    /// <summary>能控制宠物在其他应用全屏时是否显示。</summary>
    public bool OverFullscreen { get; init; }
    /// <summary>能让录屏软件看不到宠物。</summary>
    public bool CaptureExclusion { get; init; }
    public bool AutoStart { get; init; }
    public bool Speech { get; init; }
    /// <summary>能读到用户多久没碰键鼠（省电模式要用）。</summary>
    public bool IdleDetection { get; init; }
    /// <summary>能判断是不是在放声音（唱歌动画要用）。</summary>
    public bool AudioDetection { get; init; }
    /// <summary>能在应用内安装更新；不能时退回浏览器下载。</summary>
    public bool InAppUpdate { get; init; }

    /// <summary>给用户看的说明，键是上面的属性名。支持的能力也可以有说明（比如「需要 Windows 10 2004 及以上」）。</summary>
    public IReadOnlyDictionary<string, string> Notes { get; init; } = new Dictionary<string, string>();

    public string? NoteFor(string capability) => Notes.GetValueOrDefault(capability);
}
