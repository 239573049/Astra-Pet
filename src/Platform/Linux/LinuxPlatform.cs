using System.Runtime.Versioning;
using AstraPet.Core;

namespace AstraPet.Platform.Linux;

/// <summary>
/// Linux（实验性）：自启（XDG Autostart）、朗读（spd-say / espeak-ng）、空闲检测（XScreenSaver / D-Bus）、
/// 声音检测（pactl）、应用内更新（deb）。依赖的系统工具在启动时探测，没有的那项在能力描述里关掉并说明原因。
/// 点击穿透、全屏隐藏见 design/02-platform.md。
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxPlatform : PlatformBase
{
    /// <summary>deb 包装到这里（见 tools/package/make_deb.sh）。</summary>
    private const string DebInstallDir = "/opt/astra-pet/";

    public LinuxPlatform() : base("linux")
    {
        Installer = new DebInstaller();
        AutoStart = new XdgAutoStart();
        var speech = LinuxSpeech.Create();
        if (speech is not null) Speech = speech;
        var monitor = new LinuxMonitor();
        Monitor = monitor;

        var fromDeb = AppContext.BaseDirectory.StartsWith(DebInstallDir, StringComparison.Ordinal);
        var notes = new Dictionary<string, string>
        {
            [nameof(PlatformCapabilities.OverFullscreen)] = "全屏隐藏暂不支持 Linux。",
            [nameof(PlatformCapabilities.CaptureExclusion)] = "录屏开关暂不支持 Linux。",
            [nameof(PlatformCapabilities.ClickThrough)] = "透明处点击穿透暂不支持 Linux。",
        };
        if (speech is null) notes[nameof(PlatformCapabilities.Speech)] = "朗读需要安装 speech-dispatcher 或 espeak-ng。";
        if (!monitor.CanDetectAudio) notes[nameof(PlatformCapabilities.AudioDetection)] = "检测音乐播放需要 pactl（pulseaudio-utils）。";
        if (!monitor.CanReadIdle) notes[nameof(PlatformCapabilities.IdleDetection)] = LinuxEnv.IsWayland
            ? "当前桌面环境读不到空闲时长，省电模式不会自动开启。"
            : "读不到空闲时长（需要 libXss），省电模式不会自动开启。";
        if (!fromDeb) notes[nameof(PlatformCapabilities.InAppUpdate)] = "不是通过 deb 包安装的，请到下载页手动更新。";

        Capabilities = new PlatformCapabilities
        {
            AutoStart = true,
            Speech = speech is not null,
            IdleDetection = monitor.CanReadIdle,
            AudioDetection = monitor.CanDetectAudio,
            InAppUpdate = fromDeb,
            Notes = notes,
        };
        PetLog.Write($"Linux {(LinuxEnv.IsWayland ? "Wayland" : "X11")} 会话；朗读 {speech?.Engine ?? "无"}；" +
                     $"空闲检测 {(monitor.CanReadIdle ? "有" : "无")}；声音检测 {(monitor.CanDetectAudio ? "有" : "无")}");
    }
}

/// <summary>pkexec 弹系统的授权窗口，以管理员身份 dpkg -i；装完等本进程退出后重新启动。</summary>
[SupportedOSPlatform("linux")]
internal sealed class DebInstaller : UpdateInstallerBase
{
    public override IReadOnlyList<string> PreferredKinds { get; } = ["deb", "appimage", "zip"];
    protected override string InstallableKind => "deb";

    protected override async Task<string?> InstallCoreAsync(string deb)
    {
        // dpkg 换文件时，正在运行的旧程序不受影响。
        var (code, output) = await ProcessRunner.RunAsync("pkexec", "dpkg", "-i", deb);
        if (code is 126 or 127) return "授权被取消，或系统没有 pkexec；请手动安装：sudo dpkg -i " + deb;
        if (code != 0) return "dpkg 安装失败：" + output.Trim();

        var exe = Environment.ProcessPath ?? "/opt/astra-pet/AstraPet";
        ProcessRunner.StartDetached("/bin/sh", "-c", "while kill -0 \"$1\" 2>/dev/null; do sleep 0.3; done; exec \"$2\"",
            "astra-update", Environment.ProcessId.ToString(), exe);
        return null;
    }
}
