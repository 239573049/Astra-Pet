using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AstraPet.Core;
using Microsoft.Win32;

namespace AstraPet.Platform.Windows;

[SupportedOSPlatform("windows")]
internal sealed class WinPlatform : PlatformBase
{
    public WinPlatform() : base("win32")
    {
        Paths = new WinAppPaths();
        AutoStart = new WinAutoStart();
        Speech = new WinSpeech();
        Monitor = new WinMonitor();
        Installer = new WinInnoInstaller();

        // WDA_EXCLUDEFROMCAPTURE 从 Windows 10 2004（19041）起才有。
        var captureExclusion = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);
        Capabilities = new PlatformCapabilities
        {
            ClickThrough = false,
            OverFullscreen = false,
            CaptureExclusion = captureExclusion,
            AutoStart = true,
            Speech = true,
            IdleDetection = true,
            AudioDetection = false,
            InAppUpdate = true,
            Notes = new Dictionary<string, string>
            {
                [nameof(PlatformCapabilities.OverFullscreen)] = "全屏隐藏暂不支持 Windows。",
                [nameof(PlatformCapabilities.CaptureExclusion)] = captureExclusion
                    ? "录屏：关闭后录屏软件里看不到宠物。"
                    : "录屏开关需要 Windows 10 2004 及以上。",
                [nameof(PlatformCapabilities.ClickThrough)] = "透明处点击穿透暂不支持 Windows。",
                [nameof(PlatformCapabilities.AudioDetection)] = "Windows 上暂不支持检测音乐播放。",
            },
        };
    }

    public override INativeWindow AttachWindow(nint handle) =>
        handle != 0 ? new WinNativeWindow(handle) : base.AttachWindow(handle);
}

[SupportedOSPlatform("windows")]
internal sealed class WinAppPaths : AppPaths
{
    protected override string ExecutableSuffix => ".exe";

    protected override string PlatformLogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AstraPet", "logs");
}

/// <summary>Windows 上的窗口补丁：目前只有录屏捕获开关。</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WinNativeWindow(nint hwnd) : INativeWindow
{
    private const uint WdaNone = 0;
    private const uint WdaExcludeFromCapture = 0x11;   // Windows 10 2004 起支持

    public bool IgnoresMouse { get; set; }
    public (double X, double Y)? CursorInWindow() => null;
    public void MakeTransparent() { }
    public void SetOverFullscreen(bool over) { }
    public void SetAlpha(double alpha) { }

    public void SetCapturable(bool capturable) =>
        SetWindowDisplayAffinity(hwnd, capturable ? WdaNone : WdaExcludeFromCapture);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
}

/// <summary>开机自启：写 HKCU\Software\Microsoft\Windows\CurrentVersion\Run。</summary>
[SupportedOSPlatform("windows")]
internal sealed class WinAutoStart : IAutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AstraPet";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is not null;
    }

    /// <summary>注册表的 Run 项不能带环境变量，<paramref name="environment"/> 忽略。</summary>
    public bool Apply(bool enabled, IReadOnlyDictionary<string, string> environment)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            PetLog.Write(enabled ? "已开启开机自启" : "已关闭开机自启");
            return true;
        }
        catch (Exception e)
        {
            PetLog.Write($"设置开机自启失败: {e.Message}");
            return false;
        }
    }
}

/// <summary>朗读：PowerShell 里调 System.Speech。每次起 PowerShell 要一秒左右，以后可以换成直接调 SAPI。</summary>
[SupportedOSPlatform("windows")]
internal sealed class WinSpeech : ProcessSpeech
{
    protected override IEnumerable<VoiceOption> ReadVoices()
    {
        const string script = "Add-Type -AssemblyName System.Speech;" +
            "(New-Object System.Speech.Synthesis.SpeechSynthesizer).GetInstalledVoices()|" +
            "%{$_.VoiceInfo.Name+'|'+$_.VoiceInfo.Culture}";
        foreach (var line in ProcessRunner.ReadOutput("powershell", ["-NoProfile", "-Command", script]).Split('\n'))
            if (line.Split('|') is [var name, var culture])
                yield return new VoiceOption(name.Trim(), culture.Trim());
    }

    protected override ProcessStartInfo SpeakCommand(string text, SpeechOptions options)
    {
        // System.Speech 的 Rate 是 -10..10，按 200 字/分钟 为 0 粗略换算。
        var rate = Math.Clamp((options.WordsPerMinute - 200) / 20, -10, 10);
        static string Quote(string s) => s.Replace("'", "''");
        var select = options.Voice.Length > 0 ? $"$s.SelectVoice('{Quote(options.Voice)}');" : "";
        return ProcessRunner.Info("powershell", ["-NoProfile", "-Command",
            "Add-Type -AssemblyName System.Speech;" +
            "$s=New-Object System.Speech.Synthesis.SpeechSynthesizer;" +
            $"{select}$s.Rate={rate};$s.Volume={(int)(options.Volume * 100)};$s.Speak('{Quote(text)}')"]);
    }
}

[SupportedOSPlatform("windows")]
internal sealed partial class WinMonitor : ISystemMonitor
{
    public TimeSpan? IdleTime()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return null;
        // 两个都是 32 位毫秒计数，按无符号相减，系统开机超过 49.7 天回绕时也对。
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time));
    }

    public Task<bool> IsAudioPlayingAsync() => Task.FromResult(false);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInputInfo info);
}

/// <summary>
/// 静默运行 Inno Setup 安装包：它会自己关掉宠物、覆盖文件，/relaunch=1 让它装完重新启动宠物（见 AstraPet.iss）。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WinInnoInstaller : UpdateInstallerBase
{
    public override IReadOnlyList<string> PreferredKinds { get; } = ["exe", "zip"];
    protected override string InstallableKind => "exe";

    protected override Task<string?> InstallCoreAsync(string file)
    {
        using var _ = Process.Start(new ProcessStartInfo(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART /relaunch=1") { UseShellExecute = true });
        return Task.FromResult<string?>(null);
    }
}
