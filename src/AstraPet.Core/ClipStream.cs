using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace AstraPet.Core;

/// <summary>
/// 一次播放：起一个 ffmpeg 进程边解边播。后台线程把 RGBA 帧读进有界队列，UI 线程每个 tick 取一帧。
/// 队列满了，ffmpeg 就被管道背压挡住，所以每条流只占几帧内存，和片段长短、片段个数都无关。
/// libvpx-vp9 解 640x360 出首帧只要二十毫秒左右，比一帧的间隔还短，现开现播不会卡。
/// </summary>
public sealed class ClipStream : IDisposable
{
    private const int QueueFrames = 6;
    private const int PoolLimit = 24;

    // 所有片段同一尺寸，帧缓冲跨流复用，免得每帧在大对象堆上新分配 900KB。
    private static readonly ConcurrentBag<byte[]> Pool = new();

    private readonly Process _process;
    private readonly BlockingCollection<byte[]> _ready = new(QueueFrames);
    private readonly CancellationTokenSource _cancel = new();
    private readonly StringBuilder _errors = new();

    private readonly Lock _processGate = new();
    private bool _processDisposed;

    public PetClip Clip { get; }

    /// <summary>整段已经解完，并且队列里的帧都被取走了。</summary>
    public bool Completed => _ready.IsCompleted;

    public ClipStream(PetClip clip, string ffmpeg)
    {
        Clip = clip;
        var info = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // libvpx-vp9 才保留 alpha；passthrough 防止按时间戳补帧或丢帧。
        foreach (var arg in new[] { "-v", "error", "-nostats", "-c:v", "libvpx-vp9", "-i", clip.Path,
                                    "-fps_mode", "passthrough", "-f", "rawvideo", "-pix_fmt", "rgba", "-" })
            info.ArgumentList.Add(arg);

        _process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 ffmpeg");
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                lock (_errors) _errors.AppendLine(e.Data);
        };
        _process.BeginErrorReadLine();
        new Thread(ReadFrames) { IsBackground = true, Name = $"decode {clip.Name}" }.Start();
    }

    /// <summary>取下一帧。返回 null 表示还没解出来，或者已经播完（用 <see cref="Completed"/> 区分）。</summary>
    public byte[]? TryTake() => _ready.TryTake(out var frame) ? frame : null;

    /// <summary>用完的帧还回缓冲池。</summary>
    public static void Recycle(byte[] frame)
    {
        if (Pool.Count < PoolLimit)
            Pool.Add(frame);
    }

    // 正常的停止路径（Dispose、播完）一律不抛异常：调试器下每个首次异常都会挂起整个运行时，
    // 点击、拖拽时频繁切流，曾在 vsdbg 下撞上运行时挂起期间的原生崩溃（SIGSEGV，pc=0）。
    private void ReadFrames()
    {
        var stdout = _process.StandardOutput.BaseStream;
        try
        {
            while (!_cancel.IsCancellationRequested)
            {
                var frame = Pool.TryTake(out var pooled) ? pooled : new byte[PetClip.FrameBytes];
                // Dispose 杀掉 ffmpeg 后管道关闭，这里读到不足一帧就退出，不会抛异常。
                if (stdout.ReadAtLeast(frame, frame.Length, throwOnEndOfStream: false) < frame.Length)
                {
                    Recycle(frame);
                    break;
                }
                // 队列满了就等，带超时轮询取消标记，代替会抛 OperationCanceledException 的 Add(token)。
                while (!_ready.TryAdd(frame, 50))
                {
                    if (_cancel.IsCancellationRequested)
                    {
                        Recycle(frame);
                        return;
                    }
                }
            }
        }
        catch (IOException) { }   // 兜底：个别平台管道断开时仍可能抛
        finally
        {
            _ready.CompleteAdding();
            _process.WaitForExit();
            if (!_cancel.IsCancellationRequested && _process.ExitCode != 0)
                lock (_errors)
                    PetLog.Write($"ffmpeg 解码 {Clip.Name} 失败 ({_process.ExitCode}): {_errors}");
            lock (_processGate)
            {
                _processDisposed = true;
                _process.Dispose();
            }
        }
    }

    public void Dispose()
    {
        _cancel.Cancel();
        // 读线程可能刚好在释放进程，加锁避免对已释放的 Process 调 Kill（会抛 InvalidOperationException）。
        // 进程已退出时 Kill 是空操作，不抛。
        lock (_processGate)
            if (!_processDisposed && !_process.HasExited)
                _process.Kill();
        while (_ready.TryTake(out var frame))
            Recycle(frame);
    }
}
