using System.Runtime.Versioning;
using System.Security;
using AstraPet.Core;

namespace AstraPet.Platform;

/// <summary>
/// 开机自启：macOS 写 ~/Library/LaunchAgents 下的 plist（RunAtLoad），Windows 写 HKCU\...\Run。
/// 启动命令是当前进程的可执行文件，素材目录和 ffmpeg 路径通过环境变量带过去，和这次启动保持一致。
/// </summary>
public static class AutoStart
{
    private const string Label = "com.astrapet.pet";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string PlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", Label + ".plist");

    public static bool IsSupported => OperatingSystem.IsMacOS() || OperatingSystem.IsWindows();

    public static bool IsEnabled()
    {
        if (OperatingSystem.IsMacOS()) return File.Exists(PlistPath);
        if (OperatingSystem.IsWindows()) return WindowsIsEnabled();
        return false;
    }

    /// <summary>写入或删除系统的自启项。失败只记日志，返回是否成功。</summary>
    public static bool Apply(bool enabled, string videosDir, string ffmpeg)
    {
        try
        {
            if (OperatingSystem.IsMacOS()) MacApply(enabled, videosDir, ffmpeg);
            else if (OperatingSystem.IsWindows()) WindowsApply(enabled);
            else return false;
            PetLog.Write(enabled ? "已开启开机自启" : "已关闭开机自启");
            return true;
        }
        catch (Exception e)
        {
            PetLog.Write($"设置开机自启失败: {e.Message}");
            return false;
        }
    }

    private static void MacApply(bool enabled, string videosDir, string ffmpeg)
    {
        if (!enabled)
        {
            if (File.Exists(PlistPath)) File.Delete(PlistPath);
            return;
        }
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("取不到可执行文件路径");
        string X(string s) => SecurityElement.Escape(s);
        var plist = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key><string>{Label}</string>
                <key>ProgramArguments</key><array><string>{X(exe)}</string></array>
                <key>EnvironmentVariables</key>
                <dict>
                    <key>ASTRA_PET_VIDEOS</key><string>{X(videosDir)}</string>
                    <key>FFMPEG</key><string>{X(ffmpeg)}</string>
                </dict>
                <key>RunAtLoad</key><true/>
                <key>ProcessType</key><string>Interactive</string>
            </dict>
            </plist>
            """;
        Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
        File.WriteAllText(PlistPath, plist);
    }

    [SupportedOSPlatform("windows")]
    private static bool WindowsIsEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("AstraPet") is not null;
    }

    [SupportedOSPlatform("windows")]
    private static void WindowsApply(bool enabled)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue("AstraPet", $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue("AstraPet", throwOnMissingValue: false);
    }
}
