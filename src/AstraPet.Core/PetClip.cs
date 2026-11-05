namespace AstraPet.Core;

/// <summary>
/// 一段角色动画素材的元数据。启动时由 <see cref="IClipDecoder.Probe"/> 探测（帧率、时长、确认 alpha 还在），
/// 不解码整段；播放时由 <see cref="IClipDecoder.Open"/> 打开的 <see cref="IFrameSource"/> 现场流式解码。
/// </summary>
public sealed class PetClip(string name, string category, string path, double fps, int estimatedFrames)
{
    public const int Width = 640;
    public const int Height = 360;
    public const int FrameBytes = Width * Height * 4;

    /// <summary>素材里人物的标准站姿：身高 270、脚底 y = 330、水平中心 x = 320（导入动作时按它缩放定位，见 design/pet-studio/06 §2.2）。</summary>
    public const int BodyHeight = 270, FeetY = 330, CenterX = 320;

    public string Name { get; } = name;
    public string Category { get; } = category;
    public string Path { get; } = path;
    public double Fps { get; } = fps;

    /// <summary>按容器时长估算的帧数。流式解码事先不知道确切帧数，算移动进度时用它。</summary>
    public int EstimatedFrames { get; } = estimatedFrames;
}
