namespace AstraPet.Infrastructure.Studio;

/// <summary>
/// 和 Pillow 的 <c>Image.resize(..., LANCZOS)</c> 一致的缩放（libImaging/Resample.c）：先横向后纵向两遍，
/// 系数 22 位定点，中间结果截到 8 位。输入输出都是预乘 alpha 的 RGBA（Pillow 的 "RGBa"）。
/// </summary>
public static class Lanczos
{
    private const int PrecisionBits = 32 - 8 - 2;
    private const double Support = 3.0;

    private static double Sinc(double x)
    {
        if (x == 0.0) return 1.0;
        x *= Math.PI;
        return Math.Sin(x) / x;
    }

    private static double Filter(double x) => x >= -3.0 && x < 3.0 ? Sinc(x) * Sinc(x / 3) : 0.0;

    /// <summary>每个输出位置的起点、个数和定点系数。</summary>
    private static (int[] Bounds, int[] Kernel, int KSize) Coefficients(int inSize, int outSize)
    {
        double scale = (double)inSize / outSize, filterScale = Math.Max(scale, 1.0);
        var support = Support * filterScale;
        var ksize = (int)Math.Ceiling(support) * 2 + 1;
        var bounds = new int[outSize * 2];
        var kernel = new int[outSize * ksize];
        var k = new double[ksize];
        for (var xx = 0; xx < outSize; xx++)
        {
            var center = (xx + 0.5) * scale;
            var ss = 1.0 / filterScale;
            var xmin = Math.Max((int)(center - support + 0.5), 0);
            var xmax = Math.Min((int)(center + support + 0.5), inSize) - xmin;
            double ww = 0;
            for (var x = 0; x < xmax; x++)
            {
                k[x] = Filter((x + xmin - center + 0.5) * ss);
                ww += k[x];
            }
            for (var x = 0; x < xmax; x++)
            {
                var v = ww != 0.0 ? k[x] / ww : k[x];
                kernel[xx * ksize + x] = v < 0 ? (int)(-0.5 + v * (1 << PrecisionBits)) : (int)(0.5 + v * (1 << PrecisionBits));
            }
            bounds[xx * 2] = xmin;
            bounds[xx * 2 + 1] = xmax;
        }
        return (bounds, kernel, ksize);
    }

    private static byte Clip8(int v)
    {
        var s = v >> PrecisionBits;
        return s < 0 ? (byte)0 : s > 255 ? (byte)255 : (byte)s;
    }

    /// <summary>把 RGBA 图（预乘）从 w×h 缩放到 outW×outH。</summary>
    public static byte[] Resize(byte[] src, int w, int h, int outW, int outH)
    {
        var temp = src;
        int tempW = w;
        if (outW != w)
        {
            var (hb, hk, hks) = Coefficients(w, outW);
            temp = new byte[outW * h * 4];
            tempW = outW;
            for (var y = 0; y < h; y++)
            {
                var row = y * w * 4;
                for (var xx = 0; xx < outW; xx++)
                {
                    int xmin = hb[xx * 2], xmax = hb[xx * 2 + 1], kOff = xx * hks;
                    int s0 = 1 << (PrecisionBits - 1), s1 = s0, s2 = s0, s3 = s0;
                    for (var x = 0; x < xmax; x++)
                    {
                        var kv = hk[kOff + x];
                        var p = row + (x + xmin) * 4;
                        s0 += src[p] * kv; s1 += src[p + 1] * kv; s2 += src[p + 2] * kv; s3 += src[p + 3] * kv;
                    }
                    var o = (y * outW + xx) * 4;
                    temp[o] = Clip8(s0); temp[o + 1] = Clip8(s1); temp[o + 2] = Clip8(s2); temp[o + 3] = Clip8(s3);
                }
            }
        }
        if (outH == h) return temp == src ? (byte[])src.Clone() : temp;

        var (vb, vk, vks) = Coefficients(h, outH);
        var output = new byte[tempW * outH * 4];
        for (var yy = 0; yy < outH; yy++)
        {
            int ymin = vb[yy * 2], ymax = vb[yy * 2 + 1], kOff = yy * vks;
            for (var xx = 0; xx < tempW; xx++)
            {
                int s0 = 1 << (PrecisionBits - 1), s1 = s0, s2 = s0, s3 = s0;
                for (var y = 0; y < ymax; y++)
                {
                    var kv = vk[kOff + y];
                    var p = ((y + ymin) * tempW + xx) * 4;
                    s0 += temp[p] * kv; s1 += temp[p + 1] * kv; s2 += temp[p + 2] * kv; s3 += temp[p + 3] * kv;
                }
                var o = (yy * tempW + xx) * 4;
                output[o] = Clip8(s0); output[o + 1] = Clip8(s1); output[o + 2] = Clip8(s2); output[o + 3] = Clip8(s3);
            }
        }
        return output;
    }

    /// <summary>RGBA → 预乘（Pillow 的 RGBA→RGBa 转换）。</summary>
    public static void Premultiply(byte[] rgba)
    {
        for (var i = 0; i < rgba.Length; i += 4)
        {
            uint a = rgba[i + 3];
            for (var c = 0; c < 3; c++)
            {
                var t = rgba[i + c] * a + 128;
                rgba[i + c] = (byte)(((t >> 8) + t) >> 8);
            }
        }
    }

    /// <summary>预乘 → RGBA（Pillow 的 RGBa→RGBA 转换）。</summary>
    public static void Unpremultiply(byte[] rgba)
    {
        for (var i = 0; i < rgba.Length; i += 4)
        {
            int a = rgba[i + 3];
            if (a is 0 or 255) continue;
            for (var c = 0; c < 3; c++)
                rgba[i + c] = (byte)Math.Min(255, 255 * rgba[i + c] / a);
        }
    }
}
