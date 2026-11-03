using System.Runtime.Versioning;

namespace AstraPet.Platform.Linux;

/// <summary>
/// Linux（实验性）：目前只有应用内更新（deb）。点击穿透、自启、朗读、空闲和声音检测见 design/02-platform.md 第 2 步。
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxPlatform : PlatformBase
{
    /// <summary>deb 包装到这里（见 tools/package/make_deb.sh）。</summary>
    private const string DebInstallDir = "/opt/astra-pet/";

    public LinuxPlatform() : base("linux")
    {
        Installer = new DebInstaller();
        var fromDeb = AppContext.BaseDirectory.StartsWith(DebInstallDir, StringComparison.Ordinal);
        Capabilities = new PlatformCapabilities
        {
            InAppUpdate = fromDeb,
            Notes = new Dictionary<string, string>
            {
                [nameof(PlatformCapabilities.OverFullscreen)] = "全屏隐藏暂不支持 Linux。",
                [nameof(PlatformCapabilities.CaptureExclusion)] = "录屏开关暂不支持 Linux。",
                [nameof(PlatformCapabilities.ClickThrough)] = "透明处点击穿透暂不支持 Linux。",
                [nameof(PlatformCapabilities.AutoStart)] = "开机自启暂不支持 Linux。",
                [nameof(PlatformCapabilities.Speech)] = "朗读暂不支持 Linux。",
                [nameof(PlatformCapabilities.AudioDetection)] = "Linux 上暂不支持检测音乐播放。",
                [nameof(PlatformCapabilities.InAppUpdate)] = "不是通过 deb 包安装的，请到下载页手动更新。",
            },
        };
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
