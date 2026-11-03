using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using AstraPet.Core;

namespace AstraPet.Infrastructure.Media;

/// <summary>
/// 用 ffmpeg 解宠物动画：启动时探测元数据（<see cref="Probe"/>），播放时每段起一个 ffmpeg 进程流式解码
/// （<see cref="FfmpegFrameSource"/>）。素材是带透明通道的 VP9 WebM，必须用 libvpx-vp9 解码器，
/// ffmpeg 自带的 vp9 解码器会把 alpha 丢掉。
/// </summary>
public sealed class FfmpegClipDecoder(string ffmpeg) : IClipDecoder
{
    private static readonly Regex DurationPattern = new(@"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)");
    private static readonly Regex VideoStreamPattern = new(@"Stream #0:\d+.*?: Video: (.*)");
    private static readonly Regex SizePattern = new(@"\b(\d+)x(\d+)\b");
    private static readonly Regex FpsPattern = new(@"([0-9.]+) fps");

    public IFrameSource Open(PetClip clip) => new FfmpegFrameSource(clip, ffmpeg);

    /// <summary>
    /// 用 libvpx-vp9 解一帧丢进 null 输出，从 stderr 的流信息里读帧率和时长。
    /// 顺带做两项检查：ffmpeg 有没有 libvpx-vp9，解出来的像素格式是不是带 alpha 的 yuva。
    /// 默认的 vp9 解码器会把 alpha 丢掉，窗口就成了实心矩形，所以这里宁可启动失败。
    /// </summary>
    public PetClip Probe(string path, string category)
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
        if (!size.Success || size.Groups[1].Value != $"{PetClip.Width}" || size.Groups[2].Value != $"{PetClip.Height}")
            throw new InvalidOperationException($"素材尺寸必须是 {PetClip.Width}x{PetClip.Height}: {path}\n{video}");

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
