using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AstraPet.Core;

/// <summary>
/// 一段角色动画素材。启动时只探测元数据（帧率、时长、确认 alpha 还在），不解码整段；
/// 播放时由 <see cref="ClipStream"/> 现场流式解码。
/// </summary>
public sealed class PetClip
{
    public const int Width = 640;
    public const int Height = 360;
    public const int FrameBytes = Width * Height * 4;

    private static readonly Regex DurationPattern = new(@"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)");
    private static readonly Regex VideoStreamPattern = new(@"Stream #0:\d+.*?: Video: (.*)");
    private static readonly Regex SizePattern = new(@"\b(\d+)x(\d+)\b");
    private static readonly Regex FpsPattern = new(@"([0-9.]+) fps");

    public string Name { get; }
    public string Category { get; }
    public string Path { get; }
    public double Fps { get; }

    /// <summary>按容器时长估算的帧数。流式解码事先不知道确切帧数，算移动进度时用它。</summary>
    public int EstimatedFrames { get; }

    private PetClip(string name, string category, string path, double fps, int estimatedFrames)
    {
        Name = name;
        Category = category;
        Path = path;
        Fps = fps;
        EstimatedFrames = estimatedFrames;
    }

    /// <summary>
    /// 用 libvpx-vp9 解一帧丢进 null 输出，从 stderr 的流信息里读帧率和时长。
    /// 顺带做两项检查：ffmpeg 有没有 libvpx-vp9，解出来的像素格式是不是带 alpha 的 yuva。
    /// 默认的 vp9 解码器会把 alpha 丢掉，窗口就成了实心矩形，所以这里宁可启动失败。
    /// </summary>
    public static PetClip Probe(string path, string category, string ffmpeg)
    {
        var info = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-hide_banner", "-nostats", "-c:v", "libvpx-vp9", "-i", path,
                                    "-frames:v", "1", "-f", "null", "-" })
            info.ArgumentList.Add(arg);

        using var proc = Process.Start(info) ?? throw new InvalidOperationException("无法启动 ffmpeg");
        var log = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"ffmpeg 探测失败 ({proc.ExitCode})，确认 `ffmpeg -decoders` 里有 libvpx-vp9: {path}\n{log}");

        var stream = VideoStreamPattern.Match(log);
        if (!stream.Success)
            throw new InvalidOperationException($"没有视频流: {path}");
        var video = stream.Groups[1].Value;

        if (!video.Contains("yuva"))
            throw new InvalidOperationException($"解出来没有 alpha 通道，需要 libvpx-vp9 解码带 alpha 的 VP9: {path}\n{video}");

        var size = SizePattern.Match(video);
        if (!size.Success || size.Groups[1].Value != $"{Width}" || size.Groups[2].Value != $"{Height}")
            throw new InvalidOperationException($"素材尺寸必须是 {Width}x{Height}: {path}\n{video}");

        var fpsMatch = FpsPattern.Match(video);
        var fps = fpsMatch.Success
                  && double.TryParse(fpsMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)
                  && f > 1
            ? f
            : 24;

        var duration = DurationPattern.Match(log);
        var seconds = duration.Success
            ? int.Parse(duration.Groups[1].Value) * 3600
              + int.Parse(duration.Groups[2].Value) * 60
              + double.Parse(duration.Groups[3].Value, CultureInfo.InvariantCulture)
            : 0;

        return new PetClip(System.IO.Path.GetFileNameWithoutExtension(path), category, path, fps,
            Math.Max(1, (int)Math.Round(seconds * fps)));
    }
}
