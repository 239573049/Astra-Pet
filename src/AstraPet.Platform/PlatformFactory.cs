using AstraPet.Platform.Linux;
using AstraPet.Platform.MacOS;
using AstraPet.Platform.Windows;

namespace AstraPet.Platform;

/// <summary>
/// 按当前系统选平台实现。这是整个程序里唯一按操作系统分支的地方（各平台实现内部除外）。
/// NativeAOT 按 RID 发布时 OperatingSystem.IsXxx() 是常量，其他平台的实现会被整个裁掉。
/// </summary>
public static class PlatformFactory
{
    public static IPlatform Create()
    {
        if (OperatingSystem.IsMacOS()) return new MacPlatform();
        if (OperatingSystem.IsWindows()) return new WinPlatform();
        if (OperatingSystem.IsLinux()) return new LinuxPlatform();
        return new PlatformBase(Environment.OSVersion.Platform.ToString().ToLowerInvariant());
    }
}
