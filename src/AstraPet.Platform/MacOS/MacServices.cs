using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Text.RegularExpressions;
using AstraPet.Core;

namespace AstraPet.Platform.MacOS;

/// <summary>开机自启：写 ~/Library/LaunchAgents 下的 plist（RunAtLoad）。</summary>
[SupportedOSPlatform("macos")]
internal sealed class MacAutoStart : IAutoStart
{
    private const string Label = "com.astrapet.pet";

    private static string PlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", Label + ".plist");

    public bool IsEnabled() => File.Exists(PlistPath);

    public bool Apply(bool enabled, IReadOnlyDictionary<string, string> environment)
    {
        try
        {
            if (enabled) Write(environment);
            else if (File.Exists(PlistPath)) File.Delete(PlistPath);
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
        static string X(string s) => SecurityElement.Escape(s);
        var env = string.Concat(environment.Select(kv => $"<key>{X(kv.Key)}</key><string>{X(kv.Value)}</string>"));
        var plist = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key><string>{Label}</string>
                <key>ProgramArguments</key><array><string>{X(exe)}</string></array>
                <key>EnvironmentVariables</key>
                <dict>{env}</dict>
                <key>RunAtLoad</key><true/>
                <key>ProcessType</key><string>Interactive</string>
            </dict>
            </plist>
            """;
        Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
        File.WriteAllText(PlistPath, plist);
    }
}

/// <summary>朗读：系统自带的 say。</summary>
[SupportedOSPlatform("macos")]
internal sealed partial class MacSpeech : ProcessSpeech
{
    private const string Say = "/usr/bin/say";

    protected override IEnumerable<VoiceOption> ReadVoices()
    {
        // 每行形如 "Tingting             zh_CN    # 你好！我叫婷婷。"，名字里可能有空格。
        foreach (var line in ProcessRunner.ReadOutput(Say, ["-v", "?"]).Split('\n'))
            if (VoiceLine().Match(line) is { Success: true } m)
                yield return new VoiceOption(m.Groups[1].Value.Trim(), m.Groups[2].Value);
    }

    protected override ProcessStartInfo SpeakCommand(string text, SpeechOptions options)
    {
        var args = new List<string>();
        if (options.Voice.Length > 0) args.AddRange(["-v", options.Voice]);
        // say 的 -r 就是每分钟字数；没有音量参数，用内嵌命令 [[volm x]]。
        args.AddRange(["-r", options.WordsPerMinute.ToString(), $"[[volm {options.Volume:0.00}]] {text}"]);
        return ProcessRunner.Info(Say, args);
    }

    [GeneratedRegex(@"^(.+?)\s+([a-z]{2,3}_[A-Z0-9]{2,3})\s+#")]
    private static partial Regex VoiceLine();
}

/// <summary>空闲时长读 CoreGraphics；是否在放声音问 CoreAudio。</summary>
[SupportedOSPlatform("macos")]
internal sealed partial class MacMonitor : ISystemMonitor
{
    private const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";

    // CoreAudio 的四字符常量
    private const uint SystemObject = 1;                       // kAudioObjectSystemObject
    private const uint DefaultOutputDevice = 0x644F7574;       // 'dOut'
    private const uint IsRunningSomewhere = 0x676F6E65;        // 'gone'
    private const uint ScopeGlobal = 0x676C6F62;               // 'glob'
    private const uint ElementMain = 0;

    public TimeSpan? IdleTime() =>
        // kCGEventSourceStateCombinedSessionState = 0，kCGAnyInputEventType = ~0
        TimeSpan.FromSeconds(CGEventSourceSecondsSinceLastEventType(0, uint.MaxValue));

    /// <summary>
    /// 默认输出设备上有没有哪个进程在跑音频 IO（kAudioDevicePropertyDeviceIsRunningSomewhere）。
    /// 直接查属性，微秒级，不用起子进程。视频、通话也算，所以只能算「在放声音」。
    /// CoreAudio 查询失败时退回读电源断言（pmset），两者实测结果一致。
    /// </summary>
    public Task<bool> IsAudioPlayingAsync() =>
        DefaultOutputRunning() is { } running ? Task.FromResult(running) : Task.Run(PowerAssertionSaysPlaying);

    private static unsafe bool? DefaultOutputRunning()
    {
        var address = new PropertyAddress(DefaultOutputDevice, ScopeGlobal, ElementMain);
        uint device = 0, size = sizeof(uint);
        if (AudioObjectGetPropertyData(SystemObject, &address, 0, null, &size, &device) != 0 || device == 0)
            return null;

        address = new PropertyAddress(IsRunningSomewhere, ScopeGlobal, ElementMain);
        uint running = 0;
        size = sizeof(uint);
        if (AudioObjectGetPropertyData(device, &address, 0, null, &size, &running) != 0)
            return null;
        return running != 0;
    }

    /// <summary>
    /// 有声音在输出时 coreaudiod 会挂一个阻止空闲睡眠的电源断言，
    /// 名字形如 com.apple.audio.context…preventuseridlesleep，用 pmset 读出来判断。耗时约 20ms。
    /// </summary>
    private static bool PowerAssertionSaysPlaying()
    {
        try
        {
            return ProcessRunner.ReadOutput("/usr/bin/pmset", ["-g", "assertions"], 2000)
                .Split('\n')
                .Any(line => line.Contains("coreaudiod", StringComparison.Ordinal) &&
                             line.Contains("PreventUserIdle", StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyAddress(uint selector, uint scope, uint element)
    {
        public readonly uint Selector = selector;
        public readonly uint Scope = scope;
        public readonly uint Element = element;
    }

    [LibraryImport(CoreAudio)]
    private static unsafe partial int AudioObjectGetPropertyData(uint objectId, PropertyAddress* address,
        uint qualifierSize, void* qualifier, uint* dataSize, void* data);

    [LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static partial double CGEventSourceSecondsSinceLastEventType(int stateId, uint eventType);
}
