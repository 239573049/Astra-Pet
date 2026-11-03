namespace AstraPet.Core;

/// <summary>
/// 一次播放：按顺序产出 RGBA 帧（<see cref="PetClip.Width"/> × <see cref="PetClip.Height"/>，非预乘 alpha）。
/// 实现可以在后台解码，UI 线程每个 tick 调一次 <see cref="TryTake"/>。用完的帧交给 <see cref="FramePool.Return"/>。
/// </summary>
public interface IFrameSource : IDisposable
{
    PetClip Clip { get; }

    /// <summary>取下一帧。返回 null 表示还没解出来，或者已经播完（用 <see cref="Completed"/> 区分）。</summary>
    byte[]? TryTake();

    /// <summary>整段已经解完，并且帧都被取走了。</summary>
    bool Completed { get; }
}

/// <summary>
/// 动画素材的解码器：启动时探测元数据，播放时打开帧源。
/// 现在的实现是 ffmpeg 子进程（Infrastructure 的 FfmpegClipDecoder）；测试里用内存里的假帧。
/// </summary>
public interface IClipDecoder
{
    /// <summary>读帧率、时长并检查素材能用（尺寸、透明通道）。不能用时抛异常，异常信息给人看。</summary>
    PetClip Probe(string path, string category);

    IFrameSource Open(PetClip clip);
}
