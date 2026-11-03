using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Text.RegularExpressions;
using AstraPet.Core;

namespace AstraPet.Platform.Linux;

internal static class LinuxEnv
{
    public static bool IsWayland =>
        string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase)
        || Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 };

    public static string ConfigHome =>
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
            ? config
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

    /// <summary>在 PATH 里找一个命令，找不到返回 null。</summary>
    public static string? FindInPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin")
        .Split(':', StringSplitOptions.RemoveEmptyEntries)
        .Select(dir => Path.Combine(dir, name))
        .FirstOrDefault(File.Exists);
}

/// <summary>
/// 开机自启：按 XDG Autostart 规范写 $XDG_CONFIG_HOME/autostart/astra-pet.desktop，GNOME、KDE、Xfce 等都认。
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class XdgAutoStart : IAutoStart
{
    private static string EntryPath => Path.Combine(LinuxEnv.ConfigHome, "autostart", "astra-pet.desktop");

    public bool IsEnabled() => File.Exists(EntryPath);

    public bool Apply(bool enabled, IReadOnlyDictionary<string, string> environment)
    {
        try
        {
            if (enabled) Write(environment);
            else if (File.Exists(EntryPath)) File.Delete(EntryPath);
            PetLog.Write(enabled ? "已开启开机自启" : "已关闭开机自启");
            return true;
        }
        catch (Exception e)
        {
            PetLog.Write($"设置开机自启失败: {e.Message}");
            return false;
        }
    }

    private static void Write(IReadOnlyDictionary<string, string> environment)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("取不到可执行文件路径");
        // 和 macOS 的 plist 一样把素材目录、ffmpeg 带过去；用 env 启动，Exec 里不能直接写环境变量。
        var args = new List<string> { "env" };
        args.AddRange(environment.Select(kv => $"{kv.Key}={kv.Value}"));
        args.Add(exe);
        var exec = string.Join(" ", args.Select(Quote));
        var entry = $"""
            [Desktop Entry]
            Type=Application
            Name=Astra Pet
            Comment=桌面宠物
            Exec={exec}
            Icon=astra-pet
            Terminal=false
            X-GNOME-Autostart-enabled=true
            """;
        Directory.CreateDirectory(Path.GetDirectoryName(EntryPath)!);
        File.WriteAllText(EntryPath, entry + "\n");
    }

    /// <summary>
    /// Desktop Entry 规范的 Exec 引号规则：含空格或保留字符的参数用双引号括起来，
    /// 引号里的 " ` $ \ 前面加反斜杠；% 一律写成 %%（它是字段代码的前缀）。
    /// </summary>
    private static string Quote(string arg)
    {
        arg = arg.Replace("%", "%%");
        if (arg.Length > 0 && !arg.Any(c => char.IsWhiteSpace(c) || "\"'\\><~|&;$*?#()`".Contains(c)))
            return arg;
        var escaped = string.Concat(arg.Select(c => c is '"' or '`' or '$' or '\\' ? "\\" + c : c.ToString()));
        return $"\"{escaped}\"";
    }
}

/// <summary>
/// 朗读：优先 speech-dispatcher（spd-say，桌面发行版一般自带），没有就用 espeak-ng。
/// 两个都没有时 <see cref="Create"/> 返回 null，能力描述里 Speech = false。
/// </summary>
[SupportedOSPlatform("linux")]
internal abstract partial class LinuxSpeech : ProcessSpeech
{
    public static LinuxSpeech? Create() =>
        LinuxEnv.FindInPath("spd-say") is { } spd ? new SpdSpeech(spd)
        : LinuxEnv.FindInPath("espeak-ng") is { } espeak ? new EspeakSpeech(espeak)
        : null;

    public abstract string Engine { get; }

    /// <summary>speech-dispatcher：-r / -i 都是 -100..100，-w 让进程等念完再退出。</summary>
    private sealed partial class SpdSpeech(string exe) : LinuxSpeech
    {
        public override string Engine => "speech-dispatcher";

        protected override IEnumerable<VoiceOption> ReadVoices()
        {
            // 表头之后每行形如 "    Chinese_(Mandarin)   cmn   none"。espeak-ng 模块会把每个语音的
            // 几十种音色变体（"Afrikaans+Adam"）也列出来，加起来上万条，只留基础语音。
            foreach (var line in ProcessRunner.ReadOutput(exe, ["-L"]).Split('\n').Skip(1))
                if (VoiceLine().Match(line) is { Success: true } m && !m.Groups[1].Value.Contains('+'))
                    yield return new VoiceOption(m.Groups[1].Value, m.Groups[2].Value);
        }

        protected override ProcessStartInfo SpeakCommand(string text, SpeechOptions options)
        {
            var args = new List<string> { "-w",
                "-r", Math.Clamp((options.WordsPerMinute - 200) / 2, -100, 100).ToString(),
                "-i", Math.Clamp((int)(options.Volume * 200 - 100), -100, 100).ToString() };
            if (options.Voice.Length > 0) args.AddRange(["-y", options.Voice]);
            args.Add("--");
            args.Add(text);
            return ProcessRunner.Info(exe, args);
        }

        /// <summary>
        /// 杀掉客户端进程不会让守护进程停下来，要另外发一次取消。同步等它发完（几毫秒），
        /// 否则取消可能比下一句晚到，把新的那句也取消掉。
        /// </summary>
        protected override void OnStop()
        {
            try { ProcessRunner.ReadOutput(exe, ["-C"], 1000); }
            catch { /* 守护进程不在就算了 */ }
        }

        [GeneratedRegex(@"^\s*(\S.*?)\s{2,}(\S+)\s+\S+\s*$")]
        private static partial Regex VoiceLine();
    }

    /// <summary>espeak-ng：-s 是每分钟字数，-a 振幅 0..200。语音按语言代码选（-v cmn）。</summary>
    private sealed class EspeakSpeech(string exe) : LinuxSpeech
    {
        public override string Engine => "espeak-ng";

        protected override IEnumerable<VoiceOption> ReadVoices()
        {
            // 表头之后每行形如 " 5  cmn             --/M      Chinese_(Mandarin) sit/cmn ..."
            foreach (var line in ProcessRunner.ReadOutput(exe, ["--voices"]).Split('\n').Skip(1))
                if (line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [_, var language, _, _, ..])
                    yield return new VoiceOption(language, language);
        }

        protected override ProcessStartInfo SpeakCommand(string text, SpeechOptions options)
        {
            var args = new List<string> { "-s", options.WordsPerMinute.ToString(), "-a", ((int)(options.Volume * 200)).ToString() };
            if (options.Voice.Length > 0) args.AddRange(["-v", options.Voice]);
            args.Add("--");
            args.Add(text);
            return ProcessRunner.Info(exe, args);
        }
    }
}

/// <summary>
/// 空闲时长：X11 会话直接读 XScreenSaver 扩展；Wayland 会话下 XScreenSaver 只看得到 XWayland 窗口里的输入，
/// 读出来会一直涨，所以改问桌面环境（GNOME 的 Mutter IdleMonitor、KDE 的 org.freedesktop.ScreenSaver）。
/// 问 D-Bus 要起 gdbus 进程，在后台刷新，<see cref="IdleTime"/> 返回最近一次的结果。
/// 声音：pactl 列出输出设备，有状态是 RUNNING 的就算在放（PulseAudio 和 PipeWire 的 pipewire-pulse 都支持）。
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed partial class LinuxMonitor : ISystemMonitor, IDisposable
{
    private readonly Func<TimeSpan?>? _readIdle;
    private readonly bool _idleIsSlow;
    private readonly string? _pactl;
    private nint _display;
    private TimeSpan? _cachedIdle;
    private int _refreshing;

    public LinuxMonitor()
    {
        _pactl = LinuxEnv.FindInPath("pactl");
        if (!LinuxEnv.IsWayland && TryOpenXss())
            _readIdle = XssIdle;
        else if (LinuxEnv.FindInPath("gdbus") is { } gdbus)
        {
            // 两个接口各试一次，哪个能用就一直用哪个。
            Func<TimeSpan?> mutter = () => GdbusIdle(gdbus, "org.gnome.Mutter.IdleMonitor",
                "/org/gnome/Mutter/IdleMonitor/Core", "org.gnome.Mutter.IdleMonitor.GetIdletime");
            Func<TimeSpan?> kde = () => GdbusIdle(gdbus, "org.freedesktop.ScreenSaver",
                "/org/freedesktop/ScreenSaver", "org.freedesktop.ScreenSaver.GetSessionIdleTime");
            if ((_cachedIdle = mutter()) is not null) _readIdle = mutter;
            else if ((_cachedIdle = kde()) is not null) _readIdle = kde;
            _idleIsSlow = true;
        }
    }

    public bool CanReadIdle => _readIdle is not null;
    public bool CanDetectAudio => _pactl is not null;

    public TimeSpan? IdleTime()
    {
        if (_readIdle is null) return null;
        if (!_idleIsSlow) return _readIdle();
        if (Interlocked.Exchange(ref _refreshing, 1) == 0)
            Task.Run(() =>
            {
                try { _cachedIdle = _readIdle(); }
                finally { Volatile.Write(ref _refreshing, 0); }
            });
        return _cachedIdle;
    }

    public Task<bool> IsAudioPlayingAsync()
    {
        if (_pactl is null) return Task.FromResult(false);
        return Task.Run(() =>
        {
            try
            {
                // 每行：序号 名字 模块 采样格式 状态
                return ProcessRunner.ReadOutput(_pactl, ["list", "short", "sinks"], 2000)
                    .Split('\n')
                    .Any(line => line.Split('\t', StringSplitOptions.TrimEntries) is [.., "RUNNING"]);
            }
            catch
            {
                return false;
            }
        });
    }

    /// <summary>gdbus 的输出形如 "(uint64 12345,)" 或 "(uint32 12345,)"，单位都是毫秒。</summary>
    private static TimeSpan? GdbusIdle(string gdbus, string dest, string path, string method)
    {
        try
        {
            var output = ProcessRunner.ReadOutput(gdbus,
                ["call", "--session", "--dest", dest, "--object-path", path, "--method", method], 1000);
            return GdbusNumber().Match(output) is { Success: true } m && ulong.TryParse(m.Groups[1].Value, out var ms)
                ? TimeSpan.FromMilliseconds(ms)
                : null;
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(@"uint\d+\s+(\d+)")]
    private static partial Regex GdbusNumber();

    // ───── XScreenSaver ─────

    private bool TryOpenXss()
    {
        try
        {
            _display = XOpenDisplay(0);
            if (_display == 0) return false;
            if (XssIdle() is not null) return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // 没装 libXss，下面关掉 display 后退回 D-Bus
        }
        if (_display != 0) XCloseDisplay(_display);
        _display = 0;
        return false;
    }

    private unsafe TimeSpan? XssIdle()
    {
        var info = XScreenSaverAllocInfo();
        if (info == null) return null;
        try
        {
            return XScreenSaverQueryInfo(_display, XDefaultRootWindow(_display), info) != 0
                ? TimeSpan.FromMilliseconds(info->Idle)
                : null;
        }
        finally
        {
            XFree(info);
        }
    }

    public void Dispose()
    {
        if (_display != 0) XCloseDisplay(_display);
        _display = 0;
    }

    /// <summary>XScreenSaverInfo，字段都是 C 的 int / unsigned long（64 位系统上 8 字节）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct XScreenSaverInfo
    {
        public nuint Window;
        public int State;
        public int Kind;
        public nuint TilOrSince;
        public nuint Idle;
        public nuint EventMask;
    }

    [LibraryImport("libX11.so.6")]
    private static partial nint XOpenDisplay(nint name);

    [LibraryImport("libX11.so.6")]
    private static partial int XCloseDisplay(nint display);

    [LibraryImport("libX11.so.6")]
    private static partial nuint XDefaultRootWindow(nint display);

    [LibraryImport("libX11.so.6")]
    private static unsafe partial int XFree(void* data);

    [LibraryImport("libXss.so.1")]
    private static unsafe partial XScreenSaverInfo* XScreenSaverAllocInfo();

    [LibraryImport("libXss.so.1")]
    private static unsafe partial int XScreenSaverQueryInfo(nint display, nuint drawable, XScreenSaverInfo* info);
}
