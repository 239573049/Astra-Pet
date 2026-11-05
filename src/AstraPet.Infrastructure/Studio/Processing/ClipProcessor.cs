using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Text.RegularExpressions;
using AstraPet.Core;

namespace AstraPet.Infrastructure.Studio;

/// <summary>源视频的基本信息。<see cref="HasAlpha"/>：自带透明通道（带 alpha 的 VP9 webm、ProRes 4444 mov 等），不用抠像。</summary>
public sealed record SourceInfo(int Width, int Height, double Fps, double Seconds, string Codec, string PixelFormat, bool HasAlpha)
{
    /// <summary>带 alpha 的 VP9 必须用 libvpx-vp9 解，ffmpeg 自带的 vp9 解码器会丢 alpha。</summary>
    public bool NeedsLibvpx => HasAlpha && Codec == "vp9";
}

/// <summary>处理选项。<see cref="Key"/> 为 null 时自动判断（自带透明通道就不抠，否则从首帧四角采样）。</summary>
public sealed record ClipOptions
{
    public KeyColor? Key { get; init; }
    /// <summary>move 动作：算步态曲线，不检查横向漂移。</summary>
    public bool IsMove { get; init; }
    /// <summary>drag 动作：被拎起时大小变化是正常的，不检查镜头变化。</summary>
    public bool IsDrag { get; init; }
    /// <summary>move 动作侧身跑的时间段（秒），null 表示整段。</summary>
    public (double Start, double End)? WalkWindow { get; init; }
    /// <summary>不在地面姿态的、过渡的动作：按画面锁定定位（<see cref="Placement.FrameLocked"/>），不按人物量。</summary>
    public bool FrameLocked { get; init; }
    /// <summary>过渡动作：首尾帧本来就不同，不查首尾重合、漂移和大小变化。</summary>
    public bool Transition { get; init; }
    /// <summary>贴墙姿态：查接触线稳不稳。</summary>
    public bool Wall { get; init; }
}

/// <summary>处理失败（不支持的视频、找不到人物、ffmpeg 出错），消息直接给用户看。</summary>
public sealed class ClipProcessingException(string message) : Exception(message);

/// <summary>
/// 后处理管线（design/pet-studio/06）：ffmpeg 解码 → 抠像 → 定位缩放 → ffmpeg 编码成 640×360 VP9 带 alpha 的 webm，同时算质检指标。
/// 算法和 tools/petgen/process.py 一致（金标准测试比对）。编码先写 .part，成功后改名；取消或失败时删掉。
/// </summary>
public sealed partial class ClipProcessor(string ffmpeg)
{
    public const int Crf = 32;
    /// <summary>最多处理多少秒（动作一般 3～10 秒，太长的视频只取开头）。</summary>
    public const double MaxSeconds = 30;
    /// <summary>超过 1080p 的源视频先让 ffmpeg 缩到 1080 高再抠，省时间（人物最后只有 270 像素高）。</summary>
    public const int MaxSourceHeight = 1080;
    /// <summary>同时处理几帧。每帧抠像要几十 MB 临时内存，不全占满。</summary>
    private static readonly int Parallelism = Math.Clamp(Environment.ProcessorCount - 2, 1, 6);

    [GeneratedRegex(@"Stream #0:\d+.*?: Video: (\w+)[^,]*, (\w+)(?:\([^)]*\))?, .*?\b(\d{2,5})x(\d{2,5})\b")]
    private static partial Regex VideoPattern();
    [GeneratedRegex(@"([0-9.]+) fps")]
    private static partial Regex FpsPattern();
    [GeneratedRegex(@"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)")]
    private static partial Regex DurationPattern();
    [GeneratedRegex(@"alpha_mode\s*:\s*1", RegexOptions.IgnoreCase)]
    private static partial Regex AlphaModePattern();
    [GeneratedRegex(@"^(yuva|rgba|bgra|argb|abgr|gbrap|ya\d|pal8)")]
    private static partial Regex AlphaFormatPattern();

    public async Task<SourceInfo> ProbeAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) throw new ClipProcessingException($"找不到文件：{path}");
        var (_, log) = await RunAsync(["-hide_banner", "-i", path], ct);
        var video = VideoPattern().Match(log);
        if (!video.Success) throw new ClipProcessingException("读不出视频流：可能不是视频文件，或者编码格式不支持（支持 H.264/H.265 的 mp4/mov、ProRes、VP9 webm）");
        var codec = video.Groups[1].Value;
        var pixFmt = video.Groups[2].Value;
        var line = log[video.Index..];
        line = line[..Math.Max(0, line.IndexOf('\n') is var nl and >= 0 ? nl : line.Length)];
        var fpsMatch = FpsPattern().Match(line);
        var fps = fpsMatch.Success && double.TryParse(fpsMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 1 ? f : 24;
        var d = DurationPattern().Match(log);
        var seconds = d.Success
            ? int.Parse(d.Groups[1].Value) * 3600 + int.Parse(d.Groups[2].Value) * 60 + double.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture)
            : 0;
        var hasAlpha = codec == "vp9" ? AlphaModePattern().IsMatch(log) : AlphaFormatPattern().IsMatch(pixFmt);
        return new SourceInfo(int.Parse(video.Groups[3].Value), int.Parse(video.Groups[4].Value), fps, seconds, codec, pixFmt, hasAlpha);
    }

    /// <summary>处理一段视频，写到 <paramref name="destination"/>（.webm）。进度是 0..1。</summary>
    public async Task<ClipReport> ProcessAsync(string source, string destination, ClipOptions options,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        try
        {
            return await ProcessCoreAsync(source, destination, options, progress, ct);
        }
        finally
        {
            // 一段处理下来要过几 GB 的大数组，GC 会把堆撑大且迟迟不还。处理完压缩一次大对象堆、把空出来的内存还给系统，
            // 不然常驻占用会停在几百 MB 甚至上 GB。这是个低频、用户在等进度条的时刻，一次阻塞 GC 不碍事。
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
    }

    private async Task<ClipReport> ProcessCoreAsync(string source, string destination, ClipOptions options,
        IProgress<double>? progress, CancellationToken ct)
    {
        var info = await ProbeAsync(source, ct);
        var (w, h) = (info.Width, info.Height);
        if (h > MaxSourceHeight)
        {
            w = (int)Math.Round(w * (double)MaxSourceHeight / h / 2) * 2;
            h = MaxSourceHeight;
        }
        var channels = info.HasAlpha ? 4 : 3;

        KeyColor? key = null;
        if (!info.HasAlpha)
        {
            key = options.Key;
            if (key is null)
            {
                var first = await ReadFramesAsync(source, info, w, h, 1, ct).FirstOrDefaultAsync(ct)
                            ?? throw new ClipProcessingException("视频里没有画面");
                key = ChromaKeyer.Detect(first, w, h)
                      ?? throw new ClipProcessingException("背景不是纯色（绿、蓝或品红），没法自动抠像。请用纯色背景的视频，或者已经带透明通道的 webm / mov");
            }
        }

        byte[] ToRgba(byte[] frame) => key is { } k ? ChromaKeyer.Key(frame, w, h, k) : frame;

        // 第一遍：前 5 帧量站姿。
        var boxes = new List<FigureBox?>();
        await foreach (var frame in ReadFramesAsync(source, info, w, h, FigureLocator.StandingFrames, ct))
            boxes.Add(FigureLocator.Measure(ToRgba(frame), w, h));
        var standing = FigureLocator.Standing(boxes)
                       ?? throw new ClipProcessingException("开头几帧里找不到人物：可能整个画面都被抠掉了，试试换个抠像色");
        if (standing.Height < 16) throw new ClipProcessingException("人物太小了，看不清");
        var placement = options.FrameLocked ? Placement.FrameLocked(w, h) : Placement.For(standing);

        // 第二遍：全量处理，边处理边编码。
        var part = destination + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var meter = new QaMeter();
        var expected = Math.Max(1, (int)Math.Round(Math.Min(info.Seconds, MaxSeconds) * info.Fps));
        byte[]? preview = null;
        try
        {
            using var encoder = StartEncoder(part, info.Fps);
            var stderr = encoder.StandardError.ReadToEndAsync(ct);
            var stdin = encoder.StandardInput.BaseStream;
            try
            {
                // 帧和帧之间互不相关：攒一小批并行抠像缩放，再按顺序编码、统计。
                var batch = new List<byte[]>(Parallelism);
                async Task FlushAsync()
                {
                    var placed = new byte[batch.Count][];
                    await Task.Run(() => Parallel.For(0, batch.Count, new ParallelOptions { CancellationToken = ct },
                        i => placed[i] = FramePlacer.Place(ToRgba(batch[i]), w, h, placement)), ct);
                    batch.Clear();
                    foreach (var frame in placed)
                    {
                        await stdin.WriteAsync(frame, ct);
                        meter.Add(frame);
                        if (meter.Frames == expected / 2 + 1) preview = frame;
                    }
                    progress?.Report(Math.Min(1, (double)meter.Frames / expected));
                }
                await foreach (var frame in ReadFramesAsync(source, info, w, h, null, ct))
                {
                    batch.Add(frame);
                    if (batch.Count == Parallelism) await FlushAsync();
                }
                if (batch.Count > 0) await FlushAsync();
                stdin.Close();
            }
            catch (IOException) when (encoder.HasExited || encoder.WaitForExit(2000))
            {
                // 编码器自己退出了（参数不支持之类），管道断开：报 ffmpeg 的错误而不是 Broken pipe。
                throw new ClipProcessingException($"编码失败（ffmpeg {encoder.ExitCode}）：{Tail(await stderr)}");
            }
            catch
            {
                Kill(encoder);
                throw;
            }
            await encoder.WaitForExitAsync(ct);
            var log = await stderr;
            if (encoder.ExitCode != 0) throw new ClipProcessingException($"编码失败（ffmpeg {encoder.ExitCode}）：{Tail(log)}");
            if (meter.Frames == 0) throw new ClipProcessingException("视频里没有画面");
            File.Move(part, destination, overwrite: true);
        }
        catch
        {
            TryDelete(part);
            throw;
        }

        var report = new ClipReport
        {
            Frames = meter.Frames,
            Fps = info.Fps,
            LoopIou = meter.LoopIou(),
            DriftX = meter.DriftX,
            DriftFeet = meter.DriftFeet,
            EdgeLeak = meter.EdgeLeak,
            Haze = meter.Haze,
            ScaleChange = meter.ScaleChange,
            MissingFrames = meter.MissingFrames(),
            Source = $"{info.Width}x{info.Height}",
            Scale = placement.Scale,
            Key = key,
            ContactX = meter.ContactX,
            ContactSpread = meter.ContactSpread,
            LastContactX = meter.LastLeft,
            FirstBounds = meter.FirstBounds,
            Curve = options.IsMove
                ? QaMeter.GaitCurve(meter.LegMotion, info.Fps, options.WalkWindow?.Start ?? 0, options.WalkWindow?.End ?? meter.Frames / info.Fps)
                : null,
            Preview = preview,
        };
        var warnings = options.Transition
            ? QaMeter.Warnings(report with { LoopIou = 1, DriftX = 0, ScaleChange = 0 }, true, true)
            : QaMeter.Warnings(report, options.IsMove, options.IsDrag);
        if (options.Wall && report.ContactSpread > QaMeter.MaxContactSpread)
            warnings.Add($"手脚离开了墙（接触线晃动 {report.ContactSpread:0} px），挂在屏幕边上会飘，建议重新生成");
        if (placement.NarrowedForWidth) warnings.Insert(0, "人物太宽，按宽度缩小了一些");
        if (info.Seconds > MaxSeconds) warnings.Insert(0, $"视频超过 {MaxSeconds:0} 秒，只用了开头 {MaxSeconds:0} 秒");
        return report with { Warnings = warnings };
    }

    /// <summary>流式解码：每帧 rgb24（自带透明通道时 rgba），<paramref name="limit"/> 为 null 时读到 <see cref="MaxSeconds"/>。</summary>
    private async IAsyncEnumerable<byte[]> ReadFramesAsync(string source, SourceInfo info, int w, int h, int? limit,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var args = new List<string> { "-v", "error", "-nostdin" };
        if (info.NeedsLibvpx) args.AddRange(["-c:v", "libvpx-vp9"]);
        args.AddRange(["-i", source, "-t", MaxSeconds.ToString(CultureInfo.InvariantCulture)]);
        if (limit is { } n) args.AddRange(["-frames:v", n.ToString(CultureInfo.InvariantCulture)]);
        if (w != info.Width || h != info.Height) args.AddRange(["-vf", $"scale={w}:{h}:flags=lanczos"]);
        args.AddRange(["-an", "-f", "rawvideo", "-pix_fmt", info.HasAlpha ? "rgba" : "rgb24", "-"]);

        using var proc = Start(args, redirectInput: false);
        var stderr = proc.StandardError.ReadToEndAsync(CancellationToken.None);
        var stdout = proc.StandardOutput.BaseStream;
        var size = w * h * (info.HasAlpha ? 4 : 3);
        var completed = false;
        try
        {
            while (true)
            {
                var buffer = new byte[size];
                var read = await stdout.ReadAtLeastAsync(buffer, size, throwOnEndOfStream: false, ct);
                if (read < size) break;
                yield return buffer;
            }
            completed = true;
        }
        finally
        {
            if (!completed || limit is not null) Kill(proc);
        }
        await proc.WaitForExitAsync(ct);
        if (limit is null && proc.ExitCode != 0)
            throw new ClipProcessingException($"解码失败（ffmpeg {proc.ExitCode}）：{Tail(await stderr)}");
    }

    private Process StartEncoder(string output, double fps) => Start(
    [
        "-y", "-v", "error", "-f", "rawvideo", "-pix_fmt", "rgba", "-s", $"{PetClip.Width}x{PetClip.Height}",
        "-r", fps.ToString("0.###", CultureInfo.InvariantCulture), "-i", "pipe:0",   // 不写 -：那要 fd 协议，精简构建只开了 pipe
        "-c:v", "libvpx-vp9", "-pix_fmt", "yuva420p", "-crf", Crf.ToString(CultureInfo.InvariantCulture), "-b:v", "0",
        "-row-mt", "1", "-an", "-f", "webm", output,
    ], redirectInput: true);

    private Process Start(IEnumerable<string> args, bool redirectInput)
    {
        var info = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = !redirectInput,
            RedirectStandardError = true,
            RedirectStandardInput = redirectInput,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        try
        {
            return Process.Start(info) ?? throw new ClipProcessingException("无法启动 ffmpeg");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new ClipProcessingException($"无法启动 ffmpeg（{ffmpeg}）：{e.Message}");
        }
    }

    private async Task<(int Exit, string Log)> RunAsync(IEnumerable<string> args, CancellationToken ct)
    {
        using var proc = Start(args, redirectInput: false);
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var log = await proc.StandardError.ReadToEndAsync(ct);
        await stdout;
        await proc.WaitForExitAsync(ct);
        return (proc.ExitCode, log);
    }

    private static void Kill(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(); } catch (InvalidOperationException) { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Tail(string log)
    {
        var lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" / ", lines.TakeLast(3));
    }
}
