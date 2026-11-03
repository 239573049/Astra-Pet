using System.Runtime.Versioning;

namespace AstraPet.Platform.MacOS;

[SupportedOSPlatform("macos")]
internal sealed class MacPlatform : PlatformBase
{
    public MacPlatform() : base("darwin")
    {
        var paths = new MacAppPaths();
        Paths = paths;
        AutoStart = new MacAutoStart();
        Speech = new MacSpeech();
        Monitor = new MacMonitor();
        Installer = new MacDmgInstaller();
        Capabilities = new PlatformCapabilities
        {
            ClickThrough = true,
            OverFullscreen = true,
            CaptureExclusion = true,
            AutoStart = true,
            Speech = true,
            IdleDetection = true,
            AudioDetection = true,
            InAppUpdate = MacDmgInstaller.CurrentAppBundle() is not null,
            Notes = new Dictionary<string, string>
            {
                [nameof(PlatformCapabilities.OverFullscreen)] = "全屏隐藏：宠物不进入其他应用的全屏空间。",
                [nameof(PlatformCapabilities.CaptureExclusion)] = "录屏：关闭后 OBS 的窗口捕获和屏幕捕获里都看不到宠物。",
                [nameof(PlatformCapabilities.InAppUpdate)] = "当前不是从 .app 运行的，没法原地更新。",
            },
        };
    }

    public override INativeWindow AttachWindow(nint handle) =>
        handle != 0 ? new MacNativeWindow(handle) : base.AttachWindow(handle);
}

[SupportedOSPlatform("macos")]
internal sealed class MacAppPaths : AppPaths
{
    /// <summary>.app 里非代码文件在 Contents/Resources（代码签名要求），开发运行时在程序旁边。</summary>
    public override string ResourceDirectory { get; } =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources")) is var res && Directory.Exists(res)
            ? res
            : AppContext.BaseDirectory;

    protected override string PlatformLogDirectory => Path.Combine(
        Folder(Environment.SpecialFolder.UserProfile), "Library", "Logs", "AstraPet");

    /// <summary>从 Finder 或 LaunchAgent 启动时 PATH 里没有 Homebrew。</summary>
    protected override IEnumerable<string> SystemToolDirectories => ["/opt/homebrew/bin", "/usr/local/bin"];
}
