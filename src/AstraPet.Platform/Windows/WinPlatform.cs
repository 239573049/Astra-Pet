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
            ClickThrough = true,
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
        Folder(Environment.SpecialFolder.LocalApplicationData), "AstraPet", "logs");

    protected override string PlatformCacheDirectory => Path.Combine(
        Folder(Environment.SpecialFolder.LocalApplicationData), "AstraPet", "cache");
}

/// <summary>
/// Windows 上的窗口补丁：录屏捕获开关、逐像素点击穿透。
/// 点击穿透和 Electron 的 setIgnoreMouseEvents 是同一个做法：顶层窗口要同时带 WS_EX_LAYERED 和 WS_EX_TRANSPARENT，
/// 点击才会落到别的程序的窗口上（WM_NCHITTEST 返回 HTTRANSPARENT 只能传给同一线程的窗口，传不出去）。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WinNativeWindow(nint hwnd) : INativeWindow
{
    private const uint WdaNone = 0;
    private const uint WdaExcludeFromCapture = 0x11;   // Windows 10 2004 起支持

    private const int GwlExStyle = -20;
    private const nint WsExTransparent = 0x20;
    private const nint WsExLayered = 0x80000;
    private const uint LwaAlpha = 0x2;

    private bool _ignoresMouse;
    private bool _madeLayered;

    /// <summary>
    /// 第一次打开时如果窗口原本不是分层窗口，就加上 WS_EX_LAYERED，并设成完全不透明
    /// （分层窗口不设属性时根本不显示）。之后只来回切 WS_EX_TRANSPARENT，不再动 LAYERED，免得每次切换都重绘闪一下。
    /// </summary>
    public bool IgnoresMouse
    {
        get => _ignoresMouse;
        set
        {
            if (value == _ignoresMouse) return;
            _ignoresMouse = value;
            var style = GetWindowLongPtrW(hwnd, GwlExStyle);
            if (value && !_madeLayered && (style & WsExLayered) == 0)
            {
                style |= WsExLayered;
                SetWindowLongPtrW(hwnd, GwlExStyle, style);
                SetLayeredWindowAttributes(hwnd, 0, 255, LwaAlpha);
                _madeLayered = true;
            }
            SetWindowLongPtrW(hwnd, GwlExStyle, value ? style | WsExTransparent : style & ~WsExTransparent);
        }
    }

    /// <summary>
    /// 光标的屏幕坐标换成窗口客户区坐标（物理像素），再按窗口所在显示器的 DPI 换成 DIP。
    /// Avalonia 会把进程设成 Per-Monitor V2，所以这里拿到的都是物理像素。
    /// </summary>
    public (double X, double Y)? CursorInWindow()
    {
        if (!GetCursorPos(out var p) || !ScreenToClient(hwnd, ref p)) return null;
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;
        return (p.X / scale, p.Y / scale);
    }

    public void MakeTransparent() { }
    public void SetOverFullscreen(bool over) { }
    public void SetAlpha(double alpha) { }

    public void SetCapturable(bool capturable) =>
        SetWindowDisplayAffinity(hwnd, capturable ? WdaNone : WdaExcludeFromCapture);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    // 只有 64 位的 user32 导出 *LongPtr*；发布的是 win-x64（以后 win-arm64 也是 64 位）。
    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint hwnd, int index);

    [LibraryImport("user32.dll")]
    private static partial nint SetWindowLongPtrW(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetLayeredWindowAttributes(nint hwnd, uint colorKey, byte alpha, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ScreenToClient(nint hwnd, ref Point point);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);
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
