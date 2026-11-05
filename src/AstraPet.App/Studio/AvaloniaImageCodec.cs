using System.Runtime.InteropServices;
using AstraPet.Core.Studio;
using AstraPet.Infrastructure.Studio;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace AstraPet.Studio;

/// <summary>
/// 工作台的图片编解码：PNG 走纯 C# 的 <see cref="PngCodec"/>（结果确定、不依赖渲染后端），
/// 生成模型回的 JPEG、WebP 和用户上传的其他格式交给 Avalonia（Skia）解码。编码一律出 PNG。
/// </summary>
public sealed class AvaloniaImageCodec : IImageCodec
{
    public static readonly AvaloniaImageCodec Instance = new();

    public RgbaImage Decode(byte[] data)
    {
        if (PngCodec.IsPng(data))
        {
            try { return PngCodec.Instance.Decode(data); }
            catch (InvalidDataException) { }   // 隔行扫描之类，交给 Skia
        }
        using var stream = new MemoryStream(data);
        Bitmap bitmap;
        try { bitmap = new Bitmap(stream); }
        catch (Exception e) { throw new InvalidDataException("图片读不出来（支持 PNG、JPEG、WebP）：" + e.Message, e); }
        using (bitmap)
        {
            var size = bitmap.PixelSize;
            if (size.Width <= 0 || size.Height <= 0 || (long)size.Width * size.Height > 100_000_000)
                throw new InvalidDataException("图片尺寸不合法");
            using var target = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
            using var fb = target.Lock();
            bitmap.CopyPixels(fb);
            var pixels = new byte[size.Width * size.Height * 4];
            for (var y = 0; y < size.Height; y++)
                Marshal.Copy(fb.Address + y * fb.RowBytes, pixels, y * size.Width * 4, size.Width * 4);
            return new RgbaImage(size.Width, size.Height, pixels);
        }
    }

    public byte[] EncodePng(RgbaImage image) => PngCodec.Instance.EncodePng(image);
}
