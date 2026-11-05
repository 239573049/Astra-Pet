using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using AstraPet.Core.Studio;

namespace AstraPet.Infrastructure.Studio;

/// <summary>
/// 纯 C# 的 PNG 编解码：解码支持 8 / 16 位的灰度、灰度 + alpha、RGB、RGBA 和 1～8 位调色板（含 tRNS），不支持隔行扫描；
/// 编码出 8 位 RGB 或 RGBA。生成模型回的 JPEG、WebP 由 App 里的 Avalonia 解码（<see cref="IImageCodec"/>）。
/// </summary>
public sealed class PngCodec : IImageCodec
{
    public static readonly PngCodec Instance = new();

    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static bool IsPng(byte[] data) => data.AsSpan().StartsWith(Signature);

    public RgbaImage Decode(byte[] data)
    {
        if (!IsPng(data)) throw new InvalidDataException("不是 PNG 图片");
        int width = 0, height = 0, depth = 0, colorType = 0;
        byte[]? palette = null, trns = null;
        using var idat = new MemoryStream();
        var pos = 8;
        while (pos + 8 <= data.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos));
            var type = Encoding.ASCII.GetString(data, pos + 4, 4);
            if (length < 0 || pos + 12 + length > data.Length) throw new InvalidDataException("PNG 数据不完整");
            var chunk = data.AsSpan(pos + 8, length);
            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(chunk);
                    height = BinaryPrimitives.ReadInt32BigEndian(chunk[4..]);
                    depth = chunk[8];
                    colorType = chunk[9];
                    if (chunk[12] != 0) throw new InvalidDataException("不支持隔行扫描的 PNG");
                    break;
                case "PLTE": palette = chunk.ToArray(); break;
                case "tRNS": trns = chunk.ToArray(); break;
                case "IDAT": idat.Write(chunk); break;
            }
            pos += 12 + length;
            if (type == "IEND") break;
        }
        if (width <= 0 || height <= 0 || (long)width * height > 100_000_000) throw new InvalidDataException("PNG 尺寸不合法");

        var channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException($"不支持的 PNG 颜色类型 {colorType}") };
        var bitsPerPixel = channels * depth;
        var stride = (width * bitsPerPixel + 7) / 8;
        var bpp = Math.Max(1, bitsPerPixel / 8);
        var raw = new byte[(stride + 1) * height];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
            z.ReadExactly(raw);

        var current = new byte[stride];
        var previous = new byte[stride];
        var output = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            Array.Copy(raw, y * (stride + 1) + 1, current, 0, stride);
            Unfilter(filter, current, previous, bpp);
            Expand(current, output.AsSpan(y * width * 4, width * 4), width, depth, colorType, palette, trns);
            (current, previous) = (previous, current);
        }
        return new RgbaImage(width, height, output);
    }

    private static void Unfilter(byte filter, byte[] row, byte[] prior, int bpp)
    {
        switch (filter)
        {
            case 0: break;
            case 1: for (var i = bpp; i < row.Length; i++) row[i] += row[i - bpp]; break;
            case 2: for (var i = 0; i < row.Length; i++) row[i] += prior[i]; break;
            case 3:
                for (var i = 0; i < row.Length; i++)
                    row[i] += (byte)(((i >= bpp ? row[i - bpp] : 0) + prior[i]) >> 1);
                break;
            case 4:
                for (var i = 0; i < row.Length; i++)
                    row[i] += Paeth(i >= bpp ? row[i - bpp] : (byte)0, prior[i], i >= bpp ? prior[i - bpp] : (byte)0);
                break;
            default: throw new InvalidDataException($"PNG 行过滤类型不合法：{filter}");
        }
    }

    private static byte Paeth(byte a, byte b, byte c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void Expand(byte[] row, Span<byte> dst, int width, int depth, int colorType, byte[]? palette, byte[]? trns)
    {
        int Sample(int index)   // 第 index 个样本，缩放到 8 位
        {
            switch (depth)
            {
                case 8: return row[index];
                case 16: return row[index * 2];
                default:
                    var perByte = 8 / depth;
                    var shift = 8 - depth * (index % perByte + 1);
                    var v = (row[index / perByte] >> shift) & ((1 << depth) - 1);
                    return colorType == 3 ? v : v * 255 / ((1 << depth) - 1);
            }
        }
        int RawSample(int index) => depth == 16 ? (row[index * 2] << 8) | row[index * 2 + 1]
            : depth == 8 ? row[index] : (row[index / (8 / depth)] >> (8 - depth * (index % (8 / depth) + 1))) & ((1 << depth) - 1);

        for (var x = 0; x < width; x++)
        {
            var o = x * 4;
            switch (colorType)
            {
                case 0:
                {
                    var g = (byte)Sample(x);
                    dst[o] = dst[o + 1] = dst[o + 2] = g;
                    dst[o + 3] = trns is { Length: >= 2 } && RawSample(x) == BinaryPrimitives.ReadUInt16BigEndian(trns) ? (byte)0 : (byte)255;
                    break;
                }
                case 2:
                    dst[o] = (byte)Sample(x * 3);
                    dst[o + 1] = (byte)Sample(x * 3 + 1);
                    dst[o + 2] = (byte)Sample(x * 3 + 2);
                    dst[o + 3] = trns is { Length: >= 6 }
                                 && RawSample(x * 3) == BinaryPrimitives.ReadUInt16BigEndian(trns)
                                 && RawSample(x * 3 + 1) == BinaryPrimitives.ReadUInt16BigEndian(trns.AsSpan(2))
                                 && RawSample(x * 3 + 2) == BinaryPrimitives.ReadUInt16BigEndian(trns.AsSpan(4)) ? (byte)0 : (byte)255;
                    break;
                case 3:
                {
                    var i = Sample(x);
                    if (palette is null || i * 3 + 2 >= palette.Length) throw new InvalidDataException("PNG 调色板不完整");
                    dst[o] = palette[i * 3];
                    dst[o + 1] = palette[i * 3 + 1];
                    dst[o + 2] = palette[i * 3 + 2];
                    dst[o + 3] = trns is not null && i < trns.Length ? trns[i] : (byte)255;
                    break;
                }
                case 4:
                    dst[o] = dst[o + 1] = dst[o + 2] = (byte)Sample(x * 2);
                    dst[o + 3] = (byte)Sample(x * 2 + 1);
                    break;
                case 6:
                    dst[o] = (byte)Sample(x * 4);
                    dst[o + 1] = (byte)Sample(x * 4 + 1);
                    dst[o + 2] = (byte)Sample(x * 4 + 2);
                    dst[o + 3] = (byte)Sample(x * 4 + 3);
                    break;
            }
        }
    }

    public byte[] EncodePng(RgbaImage image) => Encode(image, image.Pixels.Where((_, i) => i % 4 == 3).Any(a => a != 255));

    /// <summary>编码成 8 位 PNG；<paramref name="alpha"/> 为假时丢掉 alpha 存成 RGB（首尾帧要不带透明的图）。</summary>
    public static byte[] Encode(RgbaImage image, bool alpha)
    {
        var channels = alpha ? 4 : 3;
        var stride = image.Width * channels;
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[stride];
            var prior = new byte[stride];
            var filtered = new byte[stride + 1];
            var best = new byte[stride + 1];
            for (var y = 0; y < image.Height; y++)
            {
                for (var x = 0; x < image.Width; x++)
                    for (var c = 0; c < channels; c++)
                        row[x * channels + c] = image.Pixels[(y * image.Width + x) * 4 + c];
                // 每行挑绝对值和最小的过滤方式（libpng 的启发式）。
                long bestScore = long.MaxValue;
                for (byte f = 0; f <= 4; f++)
                {
                    filtered[0] = f;
                    long score = 0;
                    for (var i = 0; i < stride; i++)
                    {
                        var a = i >= channels ? row[i - channels] : (byte)0;
                        var b = prior[i];
                        var cc = i >= channels ? prior[i - channels] : (byte)0;
                        var v = f switch
                        {
                            0 => row[i],
                            1 => (byte)(row[i] - a),
                            2 => (byte)(row[i] - b),
                            3 => (byte)(row[i] - ((a + b) >> 1)),
                            _ => (byte)(row[i] - Paeth(a, b, cc)),
                        };
                        filtered[i + 1] = v;
                        score += v < 128 ? v : 256 - v;
                    }
                    if (score < bestScore)
                    {
                        bestScore = score;
                        Array.Copy(filtered, best, filtered.Length);
                    }
                }
                z.Write(best);
                (row, prior) = (prior, row);
            }
        }

        using var png = new MemoryStream();
        png.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), image.Height);
        header[8] = 8;
        header[9] = (byte)(alpha ? 6 : 2);
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buf, data.Length);
        s.Write(buf);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = Crc32(Crc32(0xFFFFFFFF, typeBytes), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(buf, crc);
        s.Write(buf);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(uint crc, byte[] data)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
