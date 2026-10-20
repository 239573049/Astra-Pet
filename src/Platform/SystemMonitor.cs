using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AstraPet.Platform;

/// <summary>读系统状态：用户多久没碰键鼠（省电模式用）、是不是在放音乐（唱歌动画用）。</summary>
public static class SystemMonitor
{
    /// <summary>距离上一次键盘或鼠标输入的秒数。不支持的平台返回 0（永远算在用）。</summary>
    public static double IdleSeconds()
    {
        if (OperatingSystem.IsMacOS())
            // kCGEventSourceStateCombinedSessionState = 0，kCGAnyInputEventType = ~0
            return CGEventSourceSecondsSinceLastEventType(0, uint.MaxValue);
        if (OperatingSystem.IsWindows())
        {
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (GetLastInputInfo(ref info))
                return (Environment.TickCount - (int)info.Time) / 1000.0;
        }
        return 0;
    }

    public static bool CanDetectAudio => OperatingSystem.IsMacOS();

    /// <summary>
    /// macOS 上有声音在输出时 coreaudiod 会挂一个阻止空闲睡眠的电源断言，
    /// 名字形如 com.apple.audio.context…preventuseridlesleep，用 pmset 读出来判断。
    /// 视频、通话也会触发，所以只能算「在放声音」。耗时约 20ms，别在 UI 线程上频繁调。
    /// </summary>
    public static bool IsAudioPlaying()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("/usr/bin/pmset", "-g assertions")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return false;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            return output.Split('\n').Any(line =>
                line.Contains("coreaudiod", StringComparison.Ordinal) &&
                line.Contains("PreventUserIdle", StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern double CGEventSourceSecondsSinceLastEventType(int stateId, uint eventType);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
}
