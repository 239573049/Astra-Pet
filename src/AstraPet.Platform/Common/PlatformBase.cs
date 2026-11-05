using System.Runtime.InteropServices;

namespace AstraPet.Platform;

/// <summary>
/// 各平台的公共部分：默认所有服务都是「不支持」的空实现，具体平台在构造函数里换上自己的。
/// 直接实例化它就是未知平台（FreeBSD 之类）：宠物能跑，没有附加能力。
/// </summary>
internal class PlatformBase(string os) : IPlatform
{
    public PlatformIdentity Identity { get; } = new(os, RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X64 => "x64",
        var other => other.ToString().ToLowerInvariant(),
    });

    public PlatformCapabilities Capabilities { get; protected init; } = new();
    public IAppPaths Paths { get; protected init; } = new AppPaths();
    public IShell Shell { get; protected init; } = new SystemShell();
    public IAutoStart AutoStart { get; protected init; } = new UnsupportedAutoStart();
    public ISpeech Speech { get; protected init; } = new UnsupportedSpeech();
    public ISystemMonitor Monitor { get; protected init; } = new UnsupportedMonitor();
    public IUpdateInstaller Installer { get; protected init; } = new UnsupportedInstaller();
    public IWindowSurfaces Windows { get; protected init; } = NoWindowSurfaces.Instance;
    public IGlobalHotkey Hotkey { get; protected init; } = NoGlobalHotkey.Instance;

    public virtual INativeWindow AttachWindow(nint handle) => NullNativeWindow.Instance;
}
