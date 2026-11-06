using System.Runtime.InteropServices;
using AstraPet.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace AstraPet.Studio;

/// <summary>
/// 循环播放一段处理好的片段（带透明的 640×360 webm），用和桌面宠物同一个解码器。
/// <see cref="IsPlaying"/> 为真并且在界面上时才起 ffmpeg，停了马上关掉；不播的时候什么都不画，下面的缩略图会露出来。
/// </summary>
public sealed class ClipPreview : Control
{
    public static readonly StyledProperty<string?> PathProperty =
        AvaloniaProperty.Register<ClipPreview, string?>(nameof(Path));

    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<ClipPreview, bool>(nameof(IsPlaying));

    /// <summary>工作台打开时设好（<see cref="StudioViewModel"/>）。没设就不播。</summary>
    public static IClipDecoder? Decoder { get; set; }

    // 探测要起一次 ffmpeg，按路径和修改时间缓存，悬停来回移不会反复探测
    private static readonly Dictionary<(string, DateTime), PetClip> Probed = [];

    private WriteableBitmap? _bitmap;
    private IFrameSource? _source;
    private DispatcherTimer? _timer;
    private CancellationTokenSource? _start;
    private bool _hasFrame;

    static ClipPreview()
    {
        AffectsRender<ClipPreview>(PathProperty);
    }

    public string? Path { get => GetValue(PathProperty); set => SetValue(PathProperty, value); }
    public bool IsPlaying { get => GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PathProperty || change.Property == IsPlayingProperty) Restart();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Restart();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Stop();
        // 离开界面（换页、关窗）就把位图还掉，一张 640x360 约 0.9MB；再显示时重新建。
        _bitmap?.Dispose();
        _bitmap = null;
    }

    private void Restart()
    {
        Stop();
        if (!IsPlaying || VisualRoot is null || Decoder is not { } decoder || Path is not { Length: > 0 } path || !File.Exists(path)) return;
        var cts = _start = new CancellationTokenSource();
        _ = StartAsync(decoder, path, cts.Token);
    }

    private async Task StartAsync(IClipDecoder decoder, string path, CancellationToken ct)
    {
        PetClip clip;
        try
        {
            var key = (path, File.GetLastWriteTimeUtc(path));
            if (!Probed.TryGetValue(key, out clip!))
            {
                clip = await Task.Run(() => decoder.Probe(path, "preview"), ct);
                Probed[key] = clip;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            if (e is not OperationCanceledException) PetLog.Write($"工作台：预览打不开 {path}: {e.Message}");
            return;
        }
        if (ct.IsCancellationRequested) return;
        _source = decoder.Open(clip);
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1 / clip.Fps), DispatcherPriority.Render, (_, _) => Tick(decoder));
        _timer.Start();
    }

    private void Tick(IClipDecoder decoder)
    {
        if (_source is null) return;
        if (_source.TryTake() is { } frame)
        {
            Draw(frame);
            FramePool.Return(frame);
            return;
        }
        if (!_source.Completed) return;
        // 播完从头再来：片段本来就是首尾相接的循环
        var clip = _source.Clip;
        _source.Dispose();
        _source = decoder.Open(clip);
    }

    private void Draw(byte[] rgba)
    {
        _bitmap ??= new WriteableBitmap(new PixelSize(PetClip.Width, PetClip.Height), new Vector(96, 96),
            PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using (var fb = _bitmap.Lock())
        {
            for (var y = 0; y < PetClip.Height; y++)
                Marshal.Copy(rgba, y * PetClip.Width * 4, fb.Address + y * fb.RowBytes, PetClip.Width * 4);
        }
        _hasFrame = true;
        InvalidateVisual();
    }

    private void Stop()
    {
        _start?.Cancel();
        _start = null;
        _timer?.Stop();
        _timer = null;
        _source?.Dispose();
        _source = null;
        if (_hasFrame) { _hasFrame = false; InvalidateVisual(); }
    }

    public override void Render(DrawingContext context)
    {
        if (!_hasFrame || _bitmap is null) return;
        var source = new Rect(0, 0, PetClip.Width, PetClip.Height);
        var scale = Math.Min(Bounds.Width / PetClip.Width, Bounds.Height / PetClip.Height);
        var size = new Size(PetClip.Width * scale, PetClip.Height * scale);
        var dest = new Rect(new Point((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2), size);
        context.DrawImage(_bitmap, source, dest);
    }
}
