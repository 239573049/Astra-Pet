namespace AstraPet.Core.Studio;

/// <summary>非预乘的 RGBA8 图像。</summary>
public sealed record RgbaImage(int Width, int Height, byte[] Pixels)
{
    public static RgbaImage Blank(int width, int height) => new(width, height, new byte[width * height * 4]);
}

/// <summary>
/// 图片编解码。生成模型可能回 PNG、JPEG 或 WebP：App 里用 Avalonia（Skia）解任何格式；
/// Infrastructure 有一个纯 C# 的 PNG 实现，测试和编码用它。
/// </summary>
public interface IImageCodec
{
    RgbaImage Decode(byte[] data);
    byte[] EncodePng(RgbaImage image);
}
